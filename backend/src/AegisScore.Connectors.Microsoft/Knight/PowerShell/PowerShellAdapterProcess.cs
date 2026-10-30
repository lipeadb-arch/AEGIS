using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight.PowerShell;

/// <summary>
/// Opções de UM adaptador de coleta em PowerShell. Nenhuma delas vem do cliente: são do AMBIENTE DE IMPLANTAÇÃO
/// (configuração do próprio AEGIS). O locatário não escolhe executável, caminho de módulo, comando nem tempo
/// limite.
/// </summary>
public abstract class PowerShellAdapterOptions
{
    /// <summary>Executável do PowerShell 7. Em Linux/contêiner, o caminho instalado na imagem.</summary>
    public string Executable { get; set; } = "pwsh";

    /// <summary>Diretório com o módulo oficial pré-instalado (imagem offline). Vazio = padrão da máquina.</summary>
    public string? ModulePath { get; set; }

    /// <summary>Tempo máximo de UMA coleta. Ao estourar, o processo e seus filhos são encerrados.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Teto, em BYTES, da saída padrão aceita do processo — defesa contra uma resposta absurda consumir memória. Aplicado durante
    /// o recebimento: ao alcançá-lo o processo é encerrado e o restante é drenado e descartado.
    /// </summary>
    public int MaxOutputBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Prazo para os canos fecharem depois que o processo terminou (um filho que herdou o cano pode mantê-lo aberto). Passado o
    /// prazo, a saída é declarada INCOMPLETA. Interno: só os testes o encurtam.
    /// </summary>
    internal TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal Func<ContainerMemorySample?> MemoryProbe { get; set; } = ContainerMemorySample.Read;
}

/// <summary>
/// Falha do TRANSPORTE que impede a execução: o runtime não pôde ser iniciado ou o recurso do script falta no assembly.
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Depois que o processo inicia, o desfecho (código de saída, tempo esgotado, saída
/// cortada, resultado ausente) é DEVOLVIDO no <see cref="PowerShellRunReport"/> — com o estágio alcançado e as leituras
/// concluídas antes do fim — em vez de virar uma exceção que apagava tudo e afirmava "nenhuma leitura foi tentada".
/// </summary>
public class PowerShellAdapterException : Exception
{
    public PowerShellAdapterException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Como o processo do adaptador terminou.</summary>
public enum PowerShellTermination
{
    /// <summary>Código 0 e documento de resultado emitido: a única terminação íntegra.</summary>
    Completed = 0,

    /// <summary>Código diferente de zero (inclui encerramento por sinal: 128 + número do sinal).</summary>
    NonZeroExit = 1,

    /// <summary>Código 0, mas sem o documento de resultado.</summary>
    NoResult = 2,

    /// <summary>Não terminou no tempo previsto e foi encerrado pelo AEGIS.</summary>
    TimedOut = 3,

    /// <summary>A saída ultrapassou o limite aceito e o processo foi encerrado pelo AEGIS.</summary>
    OutputLimit = 4,

    /// <summary>
    /// Código 0, mas a saída padrão NÃO foi recebida até o fim (a leitura falhou ou o cano não fechou no prazo). O que chegou
    /// vale; o que não chegou não pode ser confirmado — inclusive o próprio resultado.
    /// </summary>
    OutputIncomplete = 5,

    /// <summary>O AEGIS encerrou somente o adaptador para preservar a API sob pressão de memória do contêiner.</summary>
    MemoryLimit = 6,
}

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] O que se sabe do processo, além do documento: como terminou, até onde chegou e quais
/// leituras começaram e terminaram. Só vocabulário fechado e números — o diagnóstico de erro-padrão já sanitizado fica para o
/// log do operador.
/// </summary>
public sealed record PowerShellRunReport(
    PowerShellTermination Termination,
    int? ExitCode,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> StartedReads,
    IReadOnlyList<string> FinishedReads,
    bool ResultEmitted,
    bool OutputComplete,
    long DurationMs,
    int NoiseLines,
    int MalformedFrames,
    string SafeDiagnostics,
    PowerShellMemoryUsage? Memory = null)
{
    /// <summary>Terminou íntegro: código 0, resultado emitido, saída completa e nenhum quadro malformado.</summary>
    public bool Clean => Termination == PowerShellTermination.Completed && OutputComplete && ResultEmitted && MalformedFrames == 0;

    /// <summary>
    /// Terminação a partir do que se observou. Saída incompleta vem antes de "sem resultado": sem o fim da saída, a ausência do
    /// resultado não é conhecida. Só código 0, resultado e saída completa produzem <see cref="PowerShellTermination.Completed"/>.
    /// </summary>
    public static PowerShellTermination TerminationOf(bool limitExceeded, bool timedOut, int? exitCode, bool resultEmitted, bool outputComplete) =>
        limitExceeded ? PowerShellTermination.OutputLimit
        : timedOut ? PowerShellTermination.TimedOut
        : exitCode is not 0 ? PowerShellTermination.NonZeroExit
        : !outputComplete ? PowerShellTermination.OutputIncomplete
        : !resultEmitted ? PowerShellTermination.NoResult
        : PowerShellTermination.Completed;

    /// <summary>Último estágio declarado pelo script (nulo quando o script não chegou a declarar nenhum).</summary>
    public string? LastStage => Stages.Count == 0 ? null : Stages[^1];

    public bool Reached(string stage) => Stages.Any(s => string.Equals(s, stage, StringComparison.Ordinal));

    /// <summary>Relatório de uma execução íntegra (para leitores montados a partir de um documento já pronto).</summary>
    public static PowerShellRunReport CleanRun(IEnumerable<string>? reads = null)
    {
        var list = (reads ?? Array.Empty<string>()).ToList();
        return new PowerShellRunReport(PowerShellTermination.Completed, 0, new[] { "result" }, list, list, true, true, 0, 0, 0, "");
    }
}

/// <summary>O que UMA execução produziu: o documento montado a partir dos quadros (ou nulo) e o relatório do processo.</summary>
public sealed record PowerShellAdapterRun(string? Document, PowerShellRunReport Report)
{
    /// <summary>Compatibilidade: o documento como a saída padrão era lida antes do protocolo de quadros.</summary>
    public string StandardOutput => Document ?? "";

    public string SafeDiagnostics => Report.SafeDiagnostics;

    public int ExitCode => Report.ExitCode ?? -1;
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Execução de um script EMBUTIDO num processo de PowerShell separado e descartável.
///
/// Esta classe é a extração — não a reescrita — do transporte que o adaptador do Microsoft Teams já usava e que
/// passou por duas revisões dirigidas. Cada decisão abaixo custou um defeito real para ser aprendida, e por isso
/// existe UMA implementação, não duas:
///
///   • UM PROCESSO POR COLETA. O módulo mantém estado de conexão no processo. Um processo novo por coleta torna
///     impossível que a sessão de um locatário alcance a coleta de outro, mesmo sob erro. Encerrar um processo
///     (com a árvore de filhos) é determinístico; abortar uma sessão hospedada não é.
///   • SCRIPT EMBUTIDO NO ASSEMBLY. É a única coisa executada. Não há comando, parâmetro nem endereço vindos do
///     cliente.
///   • SEGREDOS PELA ENTRADA PADRÃO. Nunca por argumento nem por variável de ambiente: em Linux a linha de
///     comando de um processo é legível por outros processos da máquina e o ambiente aparece em despejos.
///   • CANO ROMPIDO NÃO APAGA A CAUSA. Quando o processo termina antes de ler a entrada, a escrita quebra o cano
///     e lança. Tratar isso como "a comunicação falhou" substituiria o diagnóstico real (o código de saída e o
///     erro-padrão) por uma mensagem genérica — foi um defeito real do bloco anterior.
///   • DIAGNÓSTICO SANITIZADO, NÃO TRUNCADO. O erro-padrão é texto de TERCEIRO e pode repetir cabeçalho de
///     autorização ou token; um corte por tamanho preservaria justamente o começo, que é onde o segredo costuma
///     estar.
///
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] PROTOCOLO DE QUADROS na saída padrão. O script escreve linhas prefixadas por
/// <c>##aegis:</c> — <c>stage</c> (estágio alcançado, gravado ANTES de cada passo), <c>read</c> (UMA leitura concluída, emitida
/// assim que termina) e <c>result</c> (o resumo final). Qualquer outra linha é RUÍDO de outro fluxo (avisos, saída de host de
/// módulo) e é contada, nunca interpretada como resultado. Daí as garantias:
///   • saída misturada com outros fluxos não invalida o documento;
///   • um processo que morre no meio preserva as leituras já concluídas e diz em que estágio parou;
///   • "nenhuma leitura foi tentada" só é afirmado quando a saída completa mostra que nenhuma leitura começou;
///   • código diferente de zero NUNCA é coleta íntegra, mesmo que algum JSON tenha sido emitido;
///   • saída acima do limite encerra o processo NA HORA (antes, o leitor parava de drenar o cano, o processo travava na
///     escrita e só o tempo limite o encerrava — com o motivo errado).
/// </summary>
public sealed class PowerShellAdapterProcess
{
    /// <summary>Prefixo dos quadros do protocolo na saída padrão.</summary>
    public const string FramePrefix = "##aegis:";

    private const int MaxStderrChars = 64 * 1024;
    private const int MaxNoiseSampleChars = 4 * 1024;

    private readonly PowerShellAdapterOptions _options;
    private readonly string _resourceName;
    private readonly string _scriptFileName;
    private readonly string _tempPrefix;
    private readonly string _adapterLabel;
    private readonly ILogger? _log;

    private readonly SemaphoreSlim _scriptGate = new(1, 1);
    private string? _scriptPath;

    /// <param name="resourceName">Nome lógico do recurso embutido com o script.</param>
    /// <param name="scriptFileName">Nome do arquivo materializado (só para legibilidade do diagnóstico).</param>
    /// <param name="tempPrefix">Prefixo do diretório temporário próprio deste adaptador.</param>
    /// <param name="adapterLabel">Como o adaptador é chamado nas mensagens ao operador.</param>
    public PowerShellAdapterProcess(
        PowerShellAdapterOptions options,
        string resourceName,
        string scriptFileName,
        string tempPrefix,
        string adapterLabel,
        ILogger? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resourceName = resourceName;
        _scriptFileName = scriptFileName;
        _tempPrefix = tempPrefix;
        _adapterLabel = adapterLabel;
        _log = log;
    }

    public string AdapterLabel => _adapterLabel;

    /// <summary>
    /// Executa o script com o pedido entregue pela ENTRADA PADRÃO. <paramref name="knownSecrets"/> são os
    /// valores que o AEGIS acabou de entregar ao processo: removidos por IGUALDADE do diagnóstico, sem depender
    /// de nenhuma heurística acertar a forma do segredo. <paramref name="buildPayload"/> recebe o diretório de
    /// trabalho PRIVADO desta execução (temporários do módulo), que é apagado ao final.
    /// </summary>
    public async Task<PowerShellAdapterRun> RunAsync(
        Func<string, string> buildPayload, IReadOnlyList<string?>? knownSecrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buildPayload);
        var script = await EnsureScriptAsync(ct);

        // Diretório de trabalho PRIVADO desta execução (0700 no Unix): temporários do módulo (o EXO gera um módulo temporário
        // por conexão) ficam fora do /tmp compartilhado e somem com a execução.
        var work = Directory.CreateTempSubdirectory(_tempPrefix + "run-");
        try
        {
            return await RunCoreAsync(script, buildPayload(work.FullName), work.FullName, knownSecrets, ct);
        }
        finally
        {
            try { work.Delete(recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogDebug("Diretório de trabalho do adaptador do {Adaptador} não removido: {Tipo}.", _adapterLabel, ex.GetType().Name);
            }
        }
    }

    /// <summary>Compatibilidade: pedido que não usa o diretório de trabalho.</summary>
    public Task<PowerShellAdapterRun> RunAsync(string stdinPayload, IReadOnlyList<string?>? knownSecrets, CancellationToken ct) =>
        RunAsync(_ => stdinPayload, knownSecrets, ct);

    private async Task<PowerShellAdapterRun> RunCoreAsync(
        string script, string stdinPayload, string workDir, IReadOnlyList<string?>? knownSecrets, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        if (!string.IsNullOrWhiteSpace(_options.ModulePath))
        {
            // Imagem OFFLINE: o módulo já está na imagem; nada é baixado em tempo de execução.
            var existing = Environment.GetEnvironmentVariable("PSModulePath");
            psi.Environment["PSModulePath"] = string.IsNullOrWhiteSpace(existing)
                ? _options.ModulePath!
                : _options.ModulePath + Path.PathSeparator + existing;
        }

        // Temporários do processo no diretório PRIVADO desta execução (TMPDIR no Unix; TEMP/TMP no Windows).
        psi.Environment["TMPDIR"] = workDir;
        psi.Environment["TEMP"] = workDir;
        psi.Environment["TMP"] = workDir;

        if (_options.MemoryProbe() is not null) ContainerMemorySample.ConfigureChildGc(psi.Environment);

        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new PowerShellAdapterException(
                $"O runtime do PowerShell não pôde ser iniciado neste ambiente. A coleta do {_adapterLabel} não foi tentada.", ex);
        }

        var frames = new FrameCollector(_options.MaxOutputBytes, () => KillTree(process), stage =>
        {
            // Nunca registrar texto arbitrário do módulo: apenas estágios conhecidos ou capacidades do catálogo.
            var known = stage is "start" or "input" or "import" or "imported" or "connect" or "connected"
                or "connect-failed" or "compliance-connect" or "disconnect" or "result";
            if (stage.StartsWith("read ", StringComparison.Ordinal))
                known = Enum.TryParse<KnightCapability>(stage[5..], out var capability)
                    && Enum.IsDefined(capability) && capability.ToString() == stage[5..];
            if (known) _log?.LogInformation("Adaptador do {Adaptador}: Estagio={Estagio}, DuracaoMs={Duracao}.",
                _adapterLabel, stage, clock.ElapsedMilliseconds);
        });
        // Os leitores NÃO observam o tempo limite: depois de encerrar o processo, eles drenam até o fim do cano, para que nada
        // do que foi escrito antes do encerramento se perca.
        var stdout = frames.ReadAsync(process.StandardOutput.BaseStream);
        var stderr = ReadStderrAsync(process.StandardError);

        using var memoryMonitorStop = new CancellationTokenSource();
        var memoryLimited = 0;
        var memoryTrack = MemoryTrack.Begin(_options.MemoryProbe());
        var memoryMonitor = MonitorMemoryAsync(process, memoryTrack, () => Interlocked.Exchange(ref memoryLimited, 1), memoryMonitorStop.Token);

        var timedOut = false;
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(stdinPayload.AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
            }
            catch (IOException)
            {
                // O processo fechou a entrada antes de receber o pedido — quase sempre porque JÁ TERMINOU
                // (runtime ausente, argumento recusado, falha na importação do módulo). Isso não é o fim do
                // diagnóstico: o motivo está no erro-padrão e no código de saída.
            }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { /* cano já rompido: nada a fechar */ }
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested)
            {
                memoryMonitorStop.Cancel();
                await memoryMonitor;
                await DrainQuietlyAsync(stdout, stderr);
                throw;
            }
            timedOut = true;
        }

        memoryMonitorStop.Cancel();
        await memoryMonitor;

        try
        {
            // Depois de encerrado (pelo fim normal, pelo tempo ou pelo limite de saída), o processo fecha os canos e os
            // leitores terminam. A espera é limitada para nunca prender a coleta num filho que herdou o cano.
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(_options.DrainTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            KillTree(process);
        }

        var errors = await stderr.WaitAsync(_options.DrainTimeout, CancellationToken.None).ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result : "", TaskScheduler.Default);
        // Leitura da saída que falhou ou não terminou no prazo: congela o que chegou (nada mais é aceito depois daqui) e a saída
        // fica INCOMPLETA — nunca vira execução íntegra.
        await stdout.WaitAsync(_options.DrainTimeout, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        frames.Freeze();

        int? exitCode = process.HasExited ? process.ExitCode : null;
        var safe = PowerShellDiagnosticScrubber.Scrub(errors.Trim(), knownSecrets);
        var noise = PowerShellDiagnosticScrubber.Scrub(frames.NoiseSample.Trim(), knownSecrets);

        var outputComplete = !frames.LimitExceeded && frames.ReachedEnd;
        var termination = memoryLimited != 0 ? PowerShellTermination.MemoryLimit
            : PowerShellRunReport.TerminationOf(frames.LimitExceeded, timedOut, exitCode, frames.Result is not null, outputComplete);

        var report = new PowerShellRunReport(
            termination, exitCode, frames.Stages, frames.StartedReads, frames.FinishedReads, frames.Result is not null,
            OutputComplete: outputComplete,
            (long)clock.Elapsed.TotalMilliseconds, frames.NoiseLines, frames.Malformed, safe,
            memoryTrack?.Usage(frames.ChildHeapBytes, frames.ChildCommittedBytes));

        if (report.Memory is { } used)
            _log?.LogInformation(
                "knight-memoria adaptador={Adaptador} terminacao={Terminacao} estagio={Estagio} duracaoMs={Duracao} limite={Limite} "
                + "inicioContainer={InicioContainer} inicioRecuperavel={InicioRecuperavel} apiRss={ApiRss} apiAnon={ApiAnon} apiHeap={ApiHeap} "
                + "apiComprometido={ApiComprometido} picoContainer={PicoContainer} picoAnon={PicoAnon} picoShmem={PicoShmem} picoFile={PicoFile} "
                + "picoActiveFile={PicoActive} picoInactiveFile={PicoInactive} picoPressao={PicoPressao} filhoPicoRss={FilhoRss} "
                + "filhoPicoAnon={FilhoAnon} filhoHeap={FilhoHeap} filhoComprometido={FilhoComprometido}",
                _adapterLabel, termination, report.LastStage ?? "(nenhum)", report.DurationMs, used.LimitBytes,
                used.StartContainerBytes, used.StartReclaimableBytes, used.ApiRssBytes, used.ApiAnonBytes, used.ApiManagedHeapBytes,
                used.ApiCommittedBytes, used.PeakContainerBytes, used.PeakAnonBytes, used.PeakShmemBytes, used.PeakFileBytes,
                used.PeakActiveFileBytes, used.PeakInactiveFileBytes, used.PeakPressureBytes, used.ChildPeakRssBytes,
                used.ChildPeakAnonBytes, used.ChildManagedHeapBytes, used.ChildCommittedBytes);

        if (safe.Length > 0)
            _log?.LogWarning(
                "Adaptador do {Adaptador} escreveu {Bytes} caractere(s) em erro-padrão. Diagnóstico sanitizado: {Detalhe}",
                _adapterLabel, errors.Length, safe);
        if (frames.NoiseLines > 0)
            _log?.LogInformation(
                "Adaptador do {Adaptador} escreveu {Linhas} linha(s) fora do protocolo na saída padrão (ignoradas). Amostra sanitizada: {Amostra}",
                _adapterLabel, frames.NoiseLines, noise);
        if (!report.Clean)
            _log?.LogWarning(
                "Adaptador do {Adaptador} terminou sem integridade: Terminacao={Terminacao}, Codigo={Codigo}, Estagio={Estagio}, "
                + "LeiturasIniciadas={Iniciadas}, LeiturasConcluidas={Concluidas}, Resultado={Resultado}, SaidaCompleta={Completa}, "
                + "QuadrosMalformados={Malformados}, DuracaoMs={Duracao}",
                _adapterLabel, termination, exitCode, report.LastStage ?? "(nenhum)", report.StartedReads.Count,
                report.FinishedReads.Count, report.ResultEmitted, report.OutputComplete, report.MalformedFrames, report.DurationMs);

        return new PowerShellAdapterRun(frames.Document(), report);
    }

    private async Task MonitorMemoryAsync(Process process, MemoryTrack? track, Action limitReached, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                var memory = _options.MemoryProbe();
                if (memory is not null) track?.Observe(memory, process);
                if (memory is { UnderPressure: true })
                {
                    _log?.LogWarning("Adaptador do {Adaptador}: encerrado para preservar a API. MemoriaAtualBytes={Atual}, LimiteBytes={Limite}.",
                        _adapterLabel, memory.CurrentBytes, memory.LimitBytes);
                    limitReached();
                    KillTree(process);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Picos de UMA execução, separando contêiner, API, filho e heap gerenciado. Só leitura de contadores.</summary>
    private sealed class MemoryTrack
    {
        private readonly ContainerMemorySample _start;
        private readonly ProcessMemorySample? _api;
        private readonly long _apiHeap;
        private readonly long _apiCommitted;
        private long _peakContainer, _peakAnon, _peakShmem, _peakFile, _peakActive, _peakInactive, _peakPressure, _childRss, _childAnon;

        private MemoryTrack(ContainerMemorySample start)
        {
            _start = start;
            _api = ProcessMemorySample.Read(Environment.ProcessId);
            _apiHeap = GC.GetTotalMemory(false);
            _apiCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
            Observe(start, null);
        }

        public static MemoryTrack? Begin(ContainerMemorySample? start) => start is null ? null : new MemoryTrack(start);

        public void Observe(ContainerMemorySample sample, Process? child)
        {
            _peakContainer = Math.Max(_peakContainer, sample.CurrentBytes);
            _peakPressure = Math.Max(_peakPressure, sample.CurrentBytes);
            if (sample.Stat is { } stat)
            {
                _peakAnon = Math.Max(_peakAnon, stat.Anon);
                _peakShmem = Math.Max(_peakShmem, stat.Shmem);
                _peakFile = Math.Max(_peakFile, stat.File);
                _peakActive = Math.Max(_peakActive, stat.ActiveFile);
                _peakInactive = Math.Max(_peakInactive, stat.InactiveFile);
            }
            if (child is not null && ProcessMemorySample.Read(child.Id) is { } memory)
            {
                _childRss = Math.Max(_childRss, memory.RssBytes);
                _childAnon = Math.Max(_childAnon, memory.AnonBytes);
            }
        }

        public PowerShellMemoryUsage Usage(long? childHeap, long? childCommitted) => new(
            _start.LimitBytes, _start.CurrentBytes, 0, _api?.RssBytes ?? 0, _api?.AnonBytes ?? 0, _apiHeap, _apiCommitted,
            _peakContainer, _peakAnon, _peakShmem, _peakFile, _peakActive, _peakInactive, _peakPressure, _childRss, _childAnon,
            childHeap, childCommitted);
    }

    private static async Task DrainQuietlyAsync(Task stdout, Task stderr)
    {
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* cancelamento do chamador: o que interessa é propagar */ }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Encerrar é o melhor esforço: o processo pode ter terminado entre a verificação e a chamada.
        }
    }

    /// <summary>
    /// Erro-padrão: os primeiros 64 KiB são guardados para o diagnóstico e o RESTO é drenado e descartado — parar de ler
    /// travaria o processo na escrita (cano cheio) até o tempo limite.
    /// </summary>
    private static async Task<string> ReadStderrAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer.AsMemory()); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
            if (read == 0) break;
            var room = MaxStderrChars - builder.Length;
            if (room > 0) builder.Append(buffer, 0, Math.Min(room, read));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Leitor da saída padrão pelo protocolo de quadros. Mantém só o que é do protocolo; conta (e amostra) o resto.
    /// Lê BYTES em blocos de tamanho fixo e aplica o teto DURANTE o recebimento: uma linha enorme ou um fluxo sem quebra de
    /// linha não acumula além de <see cref="PowerShellAdapterOptions.MaxOutputBytes"/>. A decodificação UTF-8 acontece por
    /// linha completa — um caractere dividido entre dois blocos chega inteiro. Linha fora do protocolo guarda só a amostra.
    /// </summary>
    internal sealed class FrameCollector
    {
        private const int ChunkBytes = 16 * 1024;
        private const int NoiseKeepBytes = 2 * 1024;
        private static readonly byte[] PrefixBytes = Encoding.UTF8.GetBytes(FramePrefix);
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        private readonly long _maxBytes;
        private readonly Action _kill;
        private readonly Action<string>? _onStage;
        private readonly List<string> _stages = new();
        private readonly List<string> _started = new();
        private readonly List<string> _finished = new();
        // [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Cada leitura fica como o UTF-8 que chegou (já validado como objeto JSON), não como árvore
        // de nós: a árvore de uma enumeração de milhares de caixas custava várias vezes o texto, ficava no heap da API enquanto o
        // filho ainda coletava as leituras seguintes, e era clonada inteira para montar o documento.
        private readonly List<byte[]> _reads = new();
        private readonly StringBuilder _noise = new();
        private readonly MemoryStream _line = new();
        private readonly object _gate = new();
        private bool _lineIsNoise;
        private bool _streamStart = true;
        private bool _frozen;
        private long _total;

        public FrameCollector(int maxBytes, Action kill, Action<string>? onStage = null)
        {
            _maxBytes = maxBytes > 0 ? maxBytes : long.MaxValue;
            _kill = kill;
            _onStage = onStage;
        }

        public IReadOnlyList<string> Stages => _stages;
        public IReadOnlyList<string> StartedReads => _started;
        public IReadOnlyList<string> FinishedReads => _finished;
        public JsonObject? Result { get; private set; }
        public int NoiseLines { get; private set; }
        public int Malformed { get; private set; }
        public bool LimitExceeded { get; private set; }
        public bool ReachedEnd { get; private set; }
        public string NoiseSample => _noise.ToString();
        public long? ChildHeapBytes { get; private set; }
        public long? ChildCommittedBytes { get; private set; }

        /// <summary>
        /// Encerra a aceitação: o que chegou até aqui é o que vale, e um leitor que ainda esteja ativo (cano mantido aberto por
        /// um filho) não altera mais o estado enquanto o relatório e o documento são montados. Sem fim alcançado, a saída fica
        /// incompleta.
        /// </summary>
        public void Freeze()
        {
            lock (_gate) _frozen = true;
        }

        public async Task ReadAsync(Stream stream)
        {
            var buffer = new byte[ChunkBytes];
            while (true)
            {
                int read;
                try { read = await stream.ReadAsync(buffer.AsMemory()); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return; }
                lock (_gate)
                {
                    if (_frozen) { if (read == 0) return; continue; } // relatório já montado: drena e descarta
                    if (read == 0)
                    {
                        if (!LimitExceeded)
                        {
                            if (_line.Length > 0 || _lineIsNoise) FinishLine(); // última linha sem quebra
                            ReachedEnd = true;
                        }
                        return;
                    }
                    if (LimitExceeded) continue; // processo já encerrado: drena no mesmo bloco fixo e descarta
                    Receive(buffer.AsSpan(0, read));
                }
            }
        }

        private void Receive(ReadOnlySpan<byte> chunk)
        {
            if (_streamStart)
            {
                _streamStart = false;
                if (chunk.StartsWith(Bom)) chunk = chunk[Bom.Length..];
            }

            var room = _maxBytes - _total;
            _total += chunk.Length;
            if (chunk.Length > room)
            {
                // Aceita só o que cabe: linhas completas dentro do teto valem; a linha cortada no meio é descartada.
                Consume(chunk[..(int)room]);
                LimitExceeded = true;
                _line.SetLength(0);
                _kill();
                return;
            }
            Consume(chunk);
        }

        private void Consume(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                var newline = data.IndexOf((byte)'\n');
                Append(newline < 0 ? data : data[..newline]);
                if (newline < 0) return;
                FinishLine();
                data = data[(newline + 1)..];
            }
        }

        private void Append(ReadOnlySpan<byte> part)
        {
            if (_lineIsNoise)
            {
                var keep = NoiseKeepBytes - (int)_line.Length;
                if (keep > 0) _line.Write(part[..Math.Min(keep, part.Length)]);
                return;
            }
            _line.Write(part);
            if (_line.Length >= PrefixBytes.Length
                && !_line.GetBuffer().AsSpan(0, PrefixBytes.Length).SequenceEqual(PrefixBytes))
            {
                // Fora do protocolo: dali em diante só a amostra é guardada.
                _lineIsNoise = true;
                if (_line.Length > NoiseKeepBytes) _line.SetLength(NoiseKeepBytes);
            }
        }

        private void FinishLine()
        {
            var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
            _line.SetLength(0);
            if (_line.Capacity > 1024 * 1024) _line.Capacity = ChunkBytes; // não retém o buffer de um quadro grande
            _lineIsNoise = false;
            Accept(text);
        }

        private void Accept(string line)
        {
            var text = line.TrimEnd('\r');
            if (!text.StartsWith(FramePrefix, StringComparison.Ordinal))
            {
                if (text.Trim().Length == 0) return;
                NoiseLines++;
                if (_noise.Length < MaxNoiseSampleChars)
                    _noise.Append(text.Length > 400 ? text[..400] : text).Append('\n');
                return;
            }

            var body = text[FramePrefix.Length..];
            var space = body.IndexOf(' ');
            var kind = space < 0 ? body : body[..space];
            var payload = space < 0 ? "" : body[(space + 1)..];
            switch (kind)
            {
                case "stage":
                    var stage = Bounded(payload.Trim());
                    if (stage.Length == 0) { Malformed++; return; }
                    _stages.Add(stage);
                    _onStage?.Invoke(stage);
                    if (stage.StartsWith("read ", StringComparison.Ordinal)) _started.Add(stage[5..].Trim());
                    break;
                case "read":
                    if (ParseRead(payload) is { } read)
                    {
                        _reads.Add(read.Utf8);
                        if (read.Capability is { } capability) _finished.Add(capability);
                    }
                    else Malformed++;
                    break;
                case "result":
                    if (ParseObject(payload) is { } result) Result = result;
                    else Malformed++;
                    break;
                case "memory":
                    // Heap do PRÓPRIO filho, declarado pelo script: "<bytes em uso> <bytes comprometidos>". Só números.
                    var numbers = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (numbers.Length == 2 && long.TryParse(numbers[0], out var heap) && long.TryParse(numbers[1], out var committed))
                    {
                        ChildHeapBytes = Math.Max(ChildHeapBytes ?? 0, heap);
                        ChildCommittedBytes = Math.Max(ChildCommittedBytes ?? 0, committed);
                    }
                    else Malformed++;
                    break;
                default:
                    Malformed++;
                    break;
            }
        }

        private static string Bounded(string s)
        {
            var chars = s.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or ':').Take(120).ToArray();
            return new string(chars);
        }

        private static JsonObject? ParseObject(string payload)
        {
            try { return JsonNode.Parse(payload) as JsonObject; }
            catch (JsonException) { return null; }
        }

        /// <summary>Quadro de leitura: precisa ser um objeto JSON; a capacidade, quando é texto, marca a leitura como concluída.</summary>
        private static (byte[] Utf8, string? Capability)? ParseRead(string payload)
        {
            var utf8 = Encoding.UTF8.GetBytes(payload);
            try
            {
                using var doc = JsonDocument.Parse(utf8);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                return (utf8, root.TryGetProperty("capability", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null);
            }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// O documento no formato que os leitores já interpretam: o resumo, com as leituras vindas dos quadros. Sem resumo, um
        /// documento mínimo — a conexão vale como estabelecida só se o script declarou o estágio dela.
        /// </summary>
        public string? Document()
        {
            if (Result is null && _reads.Count == 0 && _stages.Count == 0) return null;
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                if (Result is null)
                {
                    w.WriteStartObject("runtime");
                    w.WriteEndObject();
                    w.WriteBoolean("connected", _stages.Contains("connected"));
                }
                else
                {
                    foreach (var (name, value) in Result)
                    {
                        if (name == "reads") continue;
                        w.WritePropertyName(name);
                        if (value is null) w.WriteNullValue();
                        else value.WriteTo(w);
                    }
                }
                w.WriteStartArray("reads");
                foreach (var r in _reads) w.WriteRawValue(r, skipInputValidation: true); // validado ao receber o quadro
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    /// <summary>
    /// Materializa o script embutido UMA vez por instância, num diretório temporário próprio. O conteúdo vem do
    /// assembly — não de disco, nem de configuração, nem do cliente.
    /// </summary>
    private async Task<string> EnsureScriptAsync(CancellationToken ct)
    {
        if (_scriptPath is { } cached && File.Exists(cached)) return cached;

        await _scriptGate.WaitAsync(ct);
        try
        {
            if (_scriptPath is { } again && File.Exists(again)) return again;

            await using var stream = typeof(PowerShellAdapterProcess).Assembly.GetManifestResourceStream(_resourceName)
                ?? throw new PowerShellAdapterException($"Recurso {_resourceName} ausente do assembly.");

            var dir = Directory.CreateTempSubdirectory(_tempPrefix);
            var path = Path.Combine(dir.FullName, _scriptFileName);
            await using (var file = File.Create(path))
            {
                await stream.CopyToAsync(file, ct);
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            _scriptPath = path;
            return path;
        }
        finally
        {
            _scriptGate.Release();
        }
    }
}
