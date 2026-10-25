using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight.PowerShell;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] O TRANSPORTE do adaptador PowerShell e a INTERPRETAÇÃO do processo pelo coletor, com
/// executáveis auxiliares CONTROLADOS no lugar do pwsh (o adaptador real os inicia, escreve o pedido na entrada padrão, lê os canos,
/// aplica tempo e limite e monta o documento; o coletor real do Teams decide os desfechos). Cada fato reproduz um defeito do
/// transporte anterior:
///   • saída misturada com outro fluxo invalidava o documento inteiro ("resultado ilegível" → "nenhuma leitura foi tentada");
///   • o código de saída era lido e ignorado — JSON com código ≠ 0 passava como coleta íntegra;
///   • um processo que morria no meio apagava as leituras já concluídas e afirmava que nenhuma tinha sido tentada;
///   • ao passar do limite de saída (ou de 64 KiB de erro-padrão) o leitor parava de drenar o cano, o processo travava na escrita e
///     só o tempo limite o encerrava — com o motivo errado.
/// A execução do pwsh e dos módulos REAIS é provada no gate da imagem (CI) e, com módulos de fixture, em
/// <see cref="PowerShellFixtureModuleTests"/>.
/// </summary>
public sealed class PowerShellAdapterProtocolTests
{
    private const string ReadClient =
        "##aegis:read {\"capability\":\"TeamsClientConfiguration\",\"command\":\"Get-CsTeamsClientConfiguration\",\"ok\":true,"
        + "\"items\":[{\"identity\":\"Global\",\"allowEmailIntoChannel\":false,\"allowDropBox\":false,\"allowBox\":false,\"allowGoogleDrive\":false,"
        + "\"allowShareFile\":false,\"allowEgnyte\":false,\"allowGuestUser\":true}],\"errorCategory\":null,\"errorId\":null,\"durationMs\":12}";

    private const string Result =
        "##aegis:result {\"runtime\":{\"powerShell\":\"7.4.7\",\"module\":\"7.9.0\",\"platform\":\"Unix\"},\"connected\":true,"
        + "\"connectionErrorId\":null,\"connectionErrorCategory\":null,\"reads\":[]}";

    private static readonly string[] Connected =
    {
        "##aegis:stage start", "##aegis:stage input", "##aegis:stage import", "##aegis:stage imported",
        "##aegis:stage connect", "##aegis:stage connected",
    };

    private static readonly KnightCollectionContext Ctx =
        new(Guid.Parse("aa000000-0000-0000-0000-0000000000d1"), new KnightTeamsConfiguration("dir-demo-0001", "client", "secret"));

    private sealed class FakeTokens : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials("dir-demo-0001", "graph-token-sintetico", "teams-token-sintetico"));
    }

    /// <summary>Um "pwsh" auxiliar: escreve as linhas dadas na saída padrão, opcionalmente inunda o erro-padrão, dorme e sai.</summary>
    private static string Helper(IEnumerable<string> stdout, int exitCode, int stderrLines = 0, int noiseLines = 0, int sleepSeconds = 0,
        int holdPipeSeconds = 0)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aegis-ps-proto-" + Guid.NewGuid().ToString("n"))).FullName;
        var lines = stdout.ToList();
        if (OperatingSystem.IsWindows())
        {
            var sb = new StringBuilder("@echo off\r\nmore > nul\r\n");
            foreach (var l in lines) sb.Append("echo ").Append(l).Append("\r\n");
            if (noiseLines > 0) sb.Append($"for /L %%i in (1,1,{noiseLines}) do echo ruido-de-host-linha-%%i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\r\n");
            if (stderrLines > 0) sb.Append($"for /L %%i in (1,1,{stderrLines}) do echo diagnostico-do-modulo-linha-%%i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx 1>&2\r\n");
            if (sleepSeconds > 0) sb.Append($"ping -n {sleepSeconds + 1} 127.0.0.1 > nul\r\n");
            if (holdPipeSeconds > 0) sb.Append($"start \"\" /b ping -n {holdPipeSeconds + 1} 127.0.0.1\r\n");
            sb.Append($"exit /b {exitCode}\r\n");
            var cmd = Path.Combine(dir, "pwsh-auxiliar.cmd");
            File.WriteAllText(cmd, sb.ToString(), new UTF8Encoding(false));
            return cmd;
        }

        var sh = new StringBuilder("#!/bin/sh\ncat > /dev/null\n");
        foreach (var l in lines) sh.Append("printf '%s\\n' '").Append(l.Replace("'", "'\\''")).Append("'\n");
        if (noiseLines > 0) sh.Append($"i=0; while [ $i -lt {noiseLines} ]; do echo \"ruido-de-host-linha-$i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"; i=$((i+1)); done\n");
        if (stderrLines > 0) sh.Append($"i=0; while [ $i -lt {stderrLines} ]; do echo \"diagnostico-do-modulo-linha-$i-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\" >&2; i=$((i+1)); done\n");
        if (sleepSeconds > 0) sh.Append($"sleep {sleepSeconds}\n");
        if (holdPipeSeconds > 0) sh.Append($"sleep {holdPipeSeconds} &\n");
        sh.Append($"exit {exitCode}\n");
        var path = Path.Combine(dir, "pwsh-auxiliar.sh");
        File.WriteAllText(path, sh.ToString(), new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static PowerShellTeamsAdminReader Reader(string executable, TimeSpan? timeout = null, int? maxOutput = null, TimeSpan? drain = null) =>
        new(new TeamsPowerShellOptions
        {
            Executable = executable,
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
            MaxOutputBytes = maxOutput ?? 16 * 1024 * 1024,
            DrainTimeout = drain ?? TimeSpan.FromSeconds(30),
        });

    private static Task<KnightCollectionResult> Collect(PowerShellTeamsAdminReader reader) =>
        new TeamsKnightCollector(new FakeTokens(), reader).CollectAsync(Ctx);

    private static KnightCapabilityStatus Cap(KnightCollectionResult r, KnightCapability c) => r.Capabilities.Single(x => x.Capability == c);

    [Fact]
    public async Task SaidaMisturadaComOutroFluxo_NaoInvalidaODocumento()
    {
        var helper = Helper(new[] { "WARNING: aviso escrito pelo módulo na saída padrão" }
            .Concat(Connected).Append("##aegis:stage read TeamsClientConfiguration").Append(ReadClient)
            .Append("texto solto escrito pelo host do módulo").Append("##aegis:stage disconnect").Append(Result), 0);

        var output = await Reader(helper).ReadAsync(new TeamsAdminCredentials("dir", "g", "t"));

        output.Process!.Clean.Should().BeTrue();
        output.Process.NoiseLines.Should().Be(2, "as linhas fora do protocolo são contadas, nunca interpretadas");
        output.Connected.Should().BeTrue();
        output.Reads.Should().ContainSingle(r => r.Capability == "TeamsClientConfiguration" && r.Ok && r.DurationMs == 12);
    }

    /// <summary>Processo encerrado no MEIO (código 137): leituras concluídas preservadas; a iniciada falha; as seguintes, não tentadas.</summary>
    [Fact]
    public async Task ProcessoEncerradoNoMeio_PreservaAsLeiturasConcluidas_EDizOEstagio()
    {
        var helper = Helper(Connected
            .Append("##aegis:stage read TeamsClientConfiguration").Append(ReadClient)
            .Append("##aegis:stage read TeamsFederationConfiguration"), 137);

        var result = await Collect(Reader(helper));

        Cap(result, KnightCapability.TeamsClientConfiguration).Outcome.Should().Be(KnightCapabilityOutcome.Collected,
            "a leitura terminou e emitiu o próprio registro antes de o processo morrer");
        var federation = Cap(result, KnightCapability.TeamsFederationConfiguration);
        federation.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        federation.Detail.Should().Contain("iniciada").And.Contain("código 137");
        federation.Diagnostic!.Kind.Should().Be(KnightFailureKind.ProcessExit);
        federation.Diagnostic.Code.Should().Be("exit:137");
        var meeting = Cap(result, KnightCapability.TeamsMeetingPolicies);
        meeting.Outcome.Should().Be(KnightCapabilityOutcome.NotAttempted);
        meeting.Detail.Should().Contain("não tentada").And.Contain("leitura TeamsFederationConfiguration");
        result.State.Should().Be(KnightSourceState.PartialCollection);
        result.Capabilities.Should().NotContain(c => c.Detail != null && c.Detail.Contains("Nenhuma leitura foi tentada"),
            "leituras começaram — afirmar o contrário seria falso");
        result.TenantConfiguration!.Read<TeamsClientConfiguration>().Items.Should().ContainSingle();
    }

    /// <summary>Documento completo, mas código ≠ 0: as leituras valem, a coleta NÃO é declarada íntegra.</summary>
    [Fact]
    public async Task ResultadoComCodigoDiferenteDeZero_NuncaEhColetaIntegra()
    {
        var reads = new[]
        {
            "TeamsClientConfiguration", "TeamsFederationConfiguration", "TeamsMeetingPolicies", "TeamsMessagingPolicies",
            "TeamsAppPermissionPolicies", "TeamsPolicyAssignments",
        }.SelectMany(c => new[]
        {
            "##aegis:stage read " + c,
            "##aegis:read {\"capability\":\"" + c + "\",\"command\":\"Get-" + c + "\",\"ok\":true,\"items\":[],\"errorCategory\":null,\"errorId\":null}",
        });
        var helper = Helper(Connected.Concat(reads).Append(Result), 3);

        var result = await Collect(Reader(helper));

        result.State.Should().Be(KnightSourceState.PartialCollection, "código diferente de zero nunca é coleta concluída");
        result.Detail.Should().Contain("código 3").And.Contain("não é declarada íntegra");
    }

    /// <summary>Sem nenhum quadro e sem resultado: aí, sim, está demonstrado que nenhuma leitura foi tentada.</summary>
    [Fact]
    public async Task SemQuadroAlgum_NenhumaLeituraFoiTentada_ComOEstagio()
    {
        var helper = Helper(new[] { "texto que não é do protocolo" }, 0);

        var result = await Collect(Reader(helper));

        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Error
            && c.Detail!.Contains("Nenhuma leitura foi tentada") && c.Detail.Contains("sem emitir o documento de resultado"));
        result.Capabilities.First().Diagnostic!.Kind.Should().Be(KnightFailureKind.ProcessNoResult);
    }

    /// <summary>Saída acima do limite: o processo é encerrado NA HORA, e não se afirma o que não se sabe.</summary>
    [Fact]
    public async Task SaidaAcimaDoLimite_EncerraNaHora_ENaoAfirmaQueNadaFoiTentado()
    {
        var helper = Helper(Connected, 0, noiseLines: 400, sleepSeconds: 40);
        var clock = Stopwatch.StartNew();

        var output = await Reader(helper, TimeSpan.FromSeconds(120), maxOutput: 4096).ReadAsync(new TeamsAdminCredentials("dir", "g", "t"));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25), "o limite encerra o processo — não o tempo limite de 120 s");
        output.Process!.Termination.Should().Be(PowerShellTermination.OutputLimit);
        output.Process.OutputComplete.Should().BeFalse();
        var interpreted = PowerShellRunInterpretation.BeforeReads("Microsoft Teams", output.Process, "c1");
        interpreted.Detail.Should().Contain("não é possível determinar").And.NotContain("Nenhuma leitura foi tentada");
    }

    /// <summary>Erro-padrão volumoso não trava o processo: o excedente é drenado e descartado.</summary>
    [Fact]
    public async Task ErroPadraoVolumoso_NaoTravaOProcesso()
    {
        var helper = Helper(Connected.Append(Result), 0, stderrLines: 4000);
        var clock = Stopwatch.StartNew();

        var output = await Reader(helper, TimeSpan.FromSeconds(120)).ReadAsync(new TeamsAdminCredentials("dir", "g", "t"));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
        output.Process!.Clean.Should().BeTrue();
        output.Process.SafeDiagnostics.Length.Should().BeLessThanOrEqualTo(64 * 1024 + 512);
    }

    /// <summary>Tempo esgotado: indisponibilidade com o estágio em que o processo estava.</summary>
    [Fact]
    public async Task TempoEsgotado_DizOEstagio_EQueNenhumaLeituraComecou()
    {
        var helper = Helper(new[] { "##aegis:stage start", "##aegis:stage import", "##aegis:stage imported", "##aegis:stage connect" }, 0, sleepSeconds: 40);

        var result = await Collect(Reader(helper, TimeSpan.FromSeconds(3)));

        var first = result.Capabilities.First();
        first.Outcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        first.Diagnostic!.Kind.Should().Be(KnightFailureKind.Timeout);
        first.Diagnostic.Stage.Should().Be("connect");
        first.Detail.Should().Contain("conexão com o serviço").And.Contain("tempo limite");
    }

    /// <summary>Cancelamento do usuário: propaga e encerra o processo (não espera o tempo limite).</summary>
    [Fact]
    public async Task CancelamentoDoUsuario_Propaga_EEncerraOProcesso()
    {
        var helper = Helper(Connected, 0, sleepSeconds: 40);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var clock = Stopwatch.StartNew();

        await FluentActions.Awaiting(() => Reader(helper, TimeSpan.FromSeconds(120)).ReadAsync(new TeamsAdminCredentials("dir", "g", "t"), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25));
    }

    /// <summary>O script falhou antes de conectar (módulo que não carregou): o motivo não é "conexão recusada".</summary>
    [Fact]
    public async Task FalhaAntesDeConectar_NaoViraConexaoRecusada()
    {
        var helper = Helper(new[]
        {
            "##aegis:stage start", "##aegis:stage input", "##aegis:stage import",
            "##aegis:result {\"runtime\":{\"powerShell\":\"7.4.7\",\"module\":null,\"platform\":\"Unix\"},\"connected\":false,"
            + "\"connectionErrorId\":\"Modules_ModuleNotFound/FileNotFoundException\",\"connectionErrorCategory\":\"Error\",\"reads\":[]}",
        }, 0);

        var result = await Collect(Reader(helper));

        var cap = result.Capabilities.First();
        cap.Detail.Should().Contain("antes de conectar").And.Contain("importação do módulo oficial").And.Contain("Nenhuma leitura foi tentada");
        cap.Detail.Should().NotContain("recusada");
        cap.Diagnostic!.Kind.Should().Be(KnightFailureKind.RuntimeUnavailable);
        cap.Diagnostic.Code.Should().Be("Modules_ModuleNotFound/FileNotFoundException", "o identificador sanitizado do script é preservado");
    }

    // ── Leitor de quadros sobre BYTES: teto aplicado durante o recebimento ──────────────────────────────────────────────

    /// <summary>Fluxo controlado: entrega os bytes em pedaços do tamanho pedido, de forma síncrona (alocação medida na thread).</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Fluxo SEM quebra de linha que não para sozinho: só termina depois de encerrado, após mais um volume de drenagem.</summary>
    private sealed class EndlessLineStream(Func<bool> killed, long drainAfterKill) : Stream
    {
        private long _afterKill;
        public long Produced { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (killed())
            {
                if (_afterKill >= drainAfterKill) return 0;
                _afterKill += buffer.Length;
            }
            buffer.Fill((byte)'A');
            Produced += buffer.Length;
            return buffer.Length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Produced; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Uma linha sem fim (nenhuma quebra): antes, ReadLineAsync acumulava a linha inteira antes de comparar o teto. Agora o teto
    /// em bytes é aplicado ao receber — o processo é encerrado ao alcançá-lo e a drenagem posterior não acumula nada.
    /// </summary>
    [Fact]
    public async Task SaidaSemQuebraDeLinha_AcimaDoLimite_EncerraSemAcumular()
    {
        const int limit = 1024 * 1024;
        var kills = 0;
        var frames = new PowerShellAdapterProcess.FrameCollector(limit, () => kills++);
        var stream = new EndlessLineStream(() => kills > 0, drainAfterKill: 64L * 1024 * 1024);

        var before = GC.GetAllocatedBytesForCurrentThread();
        await frames.ReadAsync(stream);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        kills.Should().Be(1, "o processo é encerrado ao alcançar o teto, uma única vez");
        frames.LimitExceeded.Should().BeTrue();
        frames.ReachedEnd.Should().BeFalse("a saída foi cortada — não está completa");
        stream.Produced.Should().BeGreaterThan(64L * 1024 * 1024, "o restante foi drenado até o fim do cano");
        allocated.Should().BeLessThan(8L * 1024 * 1024, "a linha não é acumulada além do teto e a drenagem descarta em bloco fixo");
        frames.NoiseLines.Should().Be(0, "a linha cortada é descartada, não contada");
        frames.Document().Should().BeNull();
    }

    /// <summary>
    /// Registros válidos recebidos em fragmentos de 1 byte — com caracteres UTF-8 de 2 e 4 bytes divididos entre leituras —
    /// chegam íntegros; as leituras concluídas são reconhecidas e o ruído é contado.
    /// </summary>
    [Fact]
    public async Task RegistroRecebidoEmFragmentos_ComUtf8Dividido_ChegaIntegro()
    {
        const string nome = "Configuração ç 🔒";
        var read = "##aegis:read {\"capability\":\"TeamsClientConfiguration\",\"ok\":true,\"items\":[{\"identity\":\"" + nome + "\"}]}";
        var text = string.Join("\r\n", Connected) + "\r\nWARNING: aviso ção\r\n" + read + "\n" + Result + "\n";
        var frames = new PowerShellAdapterProcess.FrameCollector(1024 * 1024, () => throw new InvalidOperationException("não deveria encerrar"));

        await frames.ReadAsync(new ChunkedStream(Encoding.UTF8.GetBytes(text), chunk: 1));

        frames.ReachedEnd.Should().BeTrue();
        frames.LimitExceeded.Should().BeFalse();
        frames.Malformed.Should().Be(0);
        frames.NoiseLines.Should().Be(1);
        frames.Stages.Should().Equal("start", "input", "import", "imported", "connect", "connected");
        frames.FinishedReads.Should().Equal("TeamsClientConfiguration");
        frames.Result.Should().NotBeNull();
        var doc = JsonNode.Parse(frames.Document()!)!;
        doc["reads"]![0]!["items"]![0]!["identity"]!.GetValue<string>().Should().Be(nome);
    }

    /// <summary>
    /// O teto é em BYTES (antes contava caracteres): um texto UTF-8 com menos caracteres que o teto, mas mais bytes, passa do
    /// limite; com o teto exatamente no tamanho em bytes, tudo é aceito — inclusive o último caractere multibyte.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    public async Task TextoUtf8PertoDoLimite_ContaBytes(int delta, bool exceeds)
    {
        var read = "##aegis:read {\"capability\":\"TeamsClientConfiguration\",\"ok\":true,\"items\":[{\"identity\":\""
            + new string('ç', 300) + "\"}]}\n";
        var text = Result + "\n" + read;
        var bytes = Encoding.UTF8.GetBytes(text);
        text.Length.Should().BeLessThan(bytes.Length + delta, "o teto fica ACIMA da contagem de caracteres nos dois casos");
        var kills = 0;
        var frames = new PowerShellAdapterProcess.FrameCollector(bytes.Length + delta, () => kills++);

        await frames.ReadAsync(new ChunkedStream(bytes, chunk: 7));

        frames.LimitExceeded.Should().Be(exceeds);
        kills.Should().Be(exceeds ? 1 : 0);
        frames.Result.Should().NotBeNull("o registro completo recebido ANTES do teto é preservado");
        if (exceeds)
        {
            frames.FinishedReads.Should().BeEmpty("a linha cortada pelo teto não vale como leitura");
            frames.ReachedEnd.Should().BeFalse();
        }
        else
        {
            frames.FinishedReads.Should().Equal("TeamsClientConfiguration");
            frames.ReachedEnd.Should().BeTrue();
            JsonNode.Parse(frames.Document()!)!["reads"]![0]!["items"]![0]!["identity"]!.GetValue<string>()
                .Should().Be(new string('ç', 300));
        }
    }

    // ── Saída incompleta nunca é execução íntegra ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reprodução: código 0, resultado emitido e terminação "Completed", mas a saída padrão não chegou ao fim. Antes, Clean
    /// ignorava OutputComplete e devolvia true — coletores e gates tratavam a execução como íntegra.
    /// </summary>
    [Fact]
    public void RelatorioComSaidaIncompleta_NuncaEhIntegro()
    {
        var report = new PowerShellRunReport(PowerShellTermination.Completed, 0, new[] { "start", "result" }, Array.Empty<string>(),
            Array.Empty<string>(), ResultEmitted: true, OutputComplete: false, 10, 0, 0, "");

        report.Clean.Should().BeFalse("a saída não foi recebida por inteiro");
        PowerShellRunInterpretation.KindOf(report).Should().Be(KnightFailureKind.Incomplete);
    }

    /// <summary>A terminação só é "Completed" com código 0, resultado E saída completa; o caso positivo continua íntegro.</summary>
    [Fact]
    public void Terminacao_SaidaIncompletaVemAntesDeSemResultado_ECasoPositivoPreservado()
    {
        PowerShellRunReport.TerminationOf(false, false, 0, resultEmitted: true, outputComplete: false)
            .Should().Be(PowerShellTermination.OutputIncomplete);
        PowerShellRunReport.TerminationOf(false, false, 0, resultEmitted: false, outputComplete: false)
            .Should().Be(PowerShellTermination.OutputIncomplete, "sem o fim da saída, a ausência do resultado não é conhecida");
        PowerShellRunReport.TerminationOf(false, false, 3, resultEmitted: true, outputComplete: false)
            .Should().Be(PowerShellTermination.NonZeroExit);
        PowerShellRunReport.TerminationOf(false, false, 0, resultEmitted: false, outputComplete: true)
            .Should().Be(PowerShellTermination.NoResult);
        PowerShellRunReport.TerminationOf(false, false, 0, resultEmitted: true, outputComplete: true)
            .Should().Be(PowerShellTermination.Completed);
        PowerShellRunReport.CleanRun().Clean.Should().BeTrue();
    }

    /// <summary>
    /// Processo REAL que termina com código 0 e resultado, mas um filho herda a saída padrão e a mantém aberta: o cano não fecha
    /// no prazo de drenagem. A execução é declarada INCOMPLETA (não "terminou normalmente"); a leitura concluída antes vale, as
    /// demais não são afirmadas e a coleta fica parcial. O gate da imagem reprova a mesma execução.
    /// </summary>
    [Fact]
    public async Task SaidaNaoRecebidaAteOFim_ColetaParcial_LeiturasConcluidasPreservadas_EGateReprova()
    {
        var helper = Helper(Connected.Append("##aegis:stage read TeamsClientConfiguration").Append(ReadClient)
            .Append("##aegis:stage disconnect").Append(Result), 0, holdPipeSeconds: 15);
        var clock = Stopwatch.StartNew();

        var output = await Reader(helper, drain: TimeSpan.FromSeconds(1)).ReadAsync(new TeamsAdminCredentials("dir", "g", "t"));

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "o prazo de drenagem do teste é de 1 s");
        var report = output.Process!;
        report.ExitCode.Should().Be(0);
        report.ResultEmitted.Should().BeTrue();
        report.OutputComplete.Should().BeFalse();
        report.Termination.Should().Be(PowerShellTermination.OutputIncomplete);
        report.Clean.Should().BeFalse();
        output.Reads.Should().ContainSingle(r => r.Capability == "TeamsClientConfiguration" && r.Ok);

        var gate = new StringWriter();
        PowerShellRunInterpretation.CheckProcess(gate, report, "cenário sintético").Should().BeFalse();
        gate.ToString().Should().Contain("terminação=OutputIncomplete").And.Contain("FALHA").And.Contain("não foi recebida até o fim");

        var result = await Collect(Reader(Helper(Connected.Append("##aegis:stage read TeamsClientConfiguration").Append(ReadClient)
            .Append("##aegis:stage disconnect").Append(Result), 0, holdPipeSeconds: 15), drain: TimeSpan.FromSeconds(1)));

        Cap(result, KnightCapability.TeamsClientConfiguration).Outcome.Should().Be(KnightCapabilityOutcome.Collected,
            "a leitura terminou e emitiu o próprio registro antes do corte");
        var federation = Cap(result, KnightCapability.TeamsFederationConfiguration);
        federation.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        federation.Detail.Should().Contain("não é possível determinar").And.Contain("não foi recebida até o fim");
        federation.Diagnostic!.Kind.Should().Be(KnightFailureKind.Incomplete);
        result.State.Should().Be(KnightSourceState.PartialCollection, "saída incompleta nunca é coleta concluída");
        result.Detail.Should().Contain("não foi recebida até o fim").And.Contain("não é declarada íntegra");
        result.Detail.Should().NotContain("terminou normalmente");
    }
}
