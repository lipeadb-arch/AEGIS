using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] O TRANSPORTE do adaptador de coleta do Teams: o script EMBUTIDO (é ele, e só ele,
/// que o processo do PowerShell executa) e o comportamento do processo quando o runtime não está disponível.
///
/// O que esta bateria NÃO faz — e por isso não se confunde com homologação: ela não executa o PowerShell nem
/// conecta em locatário nenhum. A execução REAL do runtime e a importação do módulo oficial são provadas
/// DENTRO da imagem Linux de implantação, no passo "Teams collection runtime inside the image" do CI, onde o
/// módulo está instalado. Aqui ficam as garantias que independem do runtime.
/// </summary>
public sealed class TeamsAdapterTransportTests
{
    private const string ResourceName = "AegisScore.Knight.Teams.Collect.ps1";

    private static string Script()
    {
        using var stream = typeof(PowerShellTeamsAdminReader).Assembly.GetManifestResourceStream(ResourceName);
        stream.Should().NotBeNull("o adaptador é um recurso embutido — não um arquivo solto no disco do servidor");
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ScriptEmbutido_ExecutaSomenteComandosDeLeituraFixos()
    {
        var script = Script();

        // Todos os comandos que o adaptador executa — e todos são de LEITURA (verbo Get), exceto conectar e
        // desconectar a sessão. Nenhum outro verbo pode aparecer.
        foreach (var comando in new[]
        {
            "Get-CsTeamsClientConfiguration", "Get-CsTenantFederationConfiguration", "Get-CsTeamsMeetingPolicy",
            "Get-CsTeamsMessagingPolicy", "Get-CsTeamsAppPermissionPolicy", "Get-AllM365TeamsApps",
            "Get-CsGroupPolicyAssignment",
        })
            script.Should().Contain(comando);

        foreach (var proibido in new[] { "Set-Cs", "New-Cs", "Remove-Cs", "Grant-Cs", "Update-Cs", "Invoke-RestMethod", "Invoke-WebRequest" })
            script.Should().NotContain(proibido, $"o adaptador é somente leitura e não fala com endereço nenhum por conta própria ({proibido})");

        // A sessão é sempre encerrada, e a conexão é a de APLICATIVO por tokens (não por certificado).
        script.Should().Contain("Disconnect-MicrosoftTeams");
        script.Should().Contain("Connect-MicrosoftTeams -AccessTokens");
        script.Should().NotContain("-CertificateThumbprint");

        // Os tokens entram pela ENTRADA PADRÃO — nunca por argumento de linha de comando nem por ambiente.
        script.Should().Contain("[Console]::In.ReadToEnd()");
    }

    /// <summary>
    /// [Revisão dirigida] O script NÃO reproduz a mensagem da exceção. Truncar texto de terceiro não é
    /// sanitizar: uma mensagem de erro pode repetir o cabeçalho de autorização ou o token que a originou, e um
    /// corte por tamanho preserva justamente o começo. O que sai é a CATEGORIA e um identificador técnico
    /// restrito a um conjunto fixo de caracteres.
    /// </summary>
    [Fact]
    public void ScriptEmbutido_NaoReproduzAMensagemDaExcecao()
    {
        var script = Script();

        // A mensagem da exceção pode ser LIDA para classificar a falha localmente — o que ela não pode é ser
        // EMITIDA. A leitura fica confinada à classificação; fora dela, a mensagem não é tocada.
        var classificacao = script[script.IndexOf("function Get-ErrorCategory", StringComparison.Ordinal)
            ..script.IndexOf("function Get-ErrorId", StringComparison.Ordinal)];
        classificacao.Should().Contain(".Message", "é ali, e só ali, que a mensagem é usada — para classificar");

        var forsDaClassificacao = script.Replace(classificacao, "");
        forsDaClassificacao.Should().NotContain(".Message",
            "a mensagem da exceção é texto livre da fonte e não atravessa a fronteira do processo");

        // O que atravessa a fronteira é a categoria e o identificador técnico — nunca o texto da fonte.
        script.Should().Contain("FullyQualifiedErrorId");
        script.Should().Contain("errorCategory");
        script.Should().Contain("errorId");
        script.Should().NotContain("Get-ErrorSummary", "o resumo do texto bruto foi aposentado");
    }

    [Fact]
    public void ScriptEmbutido_NaoAceitaComandoVindoDoCliente()
    {
        var script = Script();

        // Nada que transforme entrada em código: o locatário fornece tokens, não comandos.
        foreach (var perigoso in new[] { "Invoke-Expression", "iex ", "[scriptblock]::Create", "& $request", "Start-Process" })
            script.Should().NotContain(perigoso, "nenhuma entrada do cliente pode virar comando executado");
    }

    [Fact]
    public async Task RuntimeInexistente_ViraFalhaDeTransporteDeclarada_NuncaResultadoVazio()
    {
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = "aegis-pwsh-que-nao-existe",
            Timeout = TimeSpan.FromSeconds(5),
        });

        var erro = await FluentActions
            .Awaiting(() => reader.ReadAsync(new TeamsAdminCredentials("dir", "graph", "teams")))
            .Should().ThrowAsync<TeamsAdminTransportException>();
        erro.Which.Message.Should().Contain("PowerShell");
    }

    /// <summary>
    /// [Revisão dirigida] O script SEMPRE escreve um documento de resultado — inclusive quando o próprio caminho
    /// de erro falha. Sem essa garantia, um erro dentro do bloco de tratamento faria o processo terminar sem
    /// saída alguma, e o AEGIS veria apenas “terminou com código 1”, sem categoria nem motivo. Foi exatamente
    /// esse o sintoma observado no CI ao executar o adaptador pela primeira vez dentro da imagem.
    /// </summary>
    [Fact]
    public void ScriptEmbutido_SempreEscreveUmDocumentoDeResultado()
    {
        var script = Script();

        // A classificação e o identificador de erro rodam no caminho de ERRO: nenhum dos dois pode lançar.
        foreach (var funcao in new[] { "function Get-ErrorCategory", "function Get-ErrorId" })
        {
            var corpo = script[script.IndexOf(funcao, StringComparison.Ordinal)..];
            corpo = corpo[..corpo.IndexOf("\n}", StringComparison.Ordinal)];
            corpo.Should().Contain("catch", $"{funcao} roda no caminho de erro e não pode falhar");
        }

        // E o bloco final tem um documento MÍNIMO escrito à mão, para o caso de a montagem falhar.
        script.Should().Contain("DocumentoDeResultadoIndisponivel");
    }

    /// <summary>
    /// Quando o processo termina sem resultado, o diagnóstico SANITIZADO do erro-padrão entra na falha de
    /// transporte: sem ele o operador fica com o código de saída e mais nada. O texto já passou pela
    /// sanitização, e não é ele que chega ao ADM nem ao relatório — o coletor monta a mensagem do cliente.
    /// </summary>
    [Fact]
    public async Task ProcessoSemResultado_TrazODiagnosticoSanitizadoNaFalhaDeTransporte()
    {
        // Um executável que EXISTE, recusa os argumentos do adaptador e termina na hora — sem jamais ler a
        // entrada padrão. É o que acontece de verdade quando o runtime da imagem não aceita o pedido.
        //
        // Há uma CORRIDA entre a escrita dos tokens e a morte do processo, e ela cai de um lado em cada
        // sistema: no shell POSIX do CI a escrita perde e o cano rompe (foi assim que o defeito apareceu — o
        // adaptador trocava a causa real por “a comunicação falhou” e perdia código de saída e diagnóstico);
        // no Windows a escrita costuma ganhar e o caminho normal é exercitado. O teste cobre os DOIS finais
        // porque exige a mesma garantia em ambos: a falha nomeia o código de saída e traz o diagnóstico.
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
                : "/bin/sh",
            Timeout = TimeSpan.FromSeconds(10),
        });

        var erro = await FluentActions.Awaiting(() => reader.CheckRuntimeAsync())
            .Should().ThrowAsync<TeamsAdminTransportException>();
        erro.Which.Message.Should().Contain("sem devolver resultado");
    }

    [Fact]
    public async Task ScriptEhMaterializadoEmArquivoProprio_ComLeituraRestritaNoUnix()
    {
        // O materializador roda ANTES de tentar o processo; o executável inexistente falha depois disso.
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = "aegis-pwsh-que-nao-existe",
            Timeout = TimeSpan.FromSeconds(5),
        });
        await FluentActions.Awaiting(() => reader.CheckRuntimeAsync()).Should().ThrowAsync<TeamsAdminTransportException>();

        var temporarios = Directory.GetDirectories(Path.GetTempPath(), "aegis-teams-*")
            .SelectMany(d => Directory.GetFiles(d, "teams-collect.ps1"))
            .ToList();
        temporarios.Should().NotBeEmpty("o script embutido é escrito num diretório temporário próprio do processo");

        var arquivo = temporarios[0];
        File.ReadAllText(arquivo).Should().Contain("Get-CsTeamsMeetingPolicy");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(arquivo).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                "o script materializado não é legível por outros usuários da máquina");
    }

    [Fact]
    public void LeituraDaSaida_PreservaCategoriaDeFalhaPorComando()
    {
        var saida = PowerShellTeamsAdminReader.Parse("""
            {"runtime":{"powerShell":"7.4.6","module":"7.9.0","platform":"Unix"},
             "connected":true,"connectionErrorId":null,"connectionErrorCategory":null,
             "reads":[
               {"capability":"TeamsMeetingPolicies","command":"Get-CsTeamsMeetingPolicy","ok":false,
                "items":[],"errorCategory":"Throttled","errorId":"TooManyRequests/HttpRequestException"},
               {"capability":"TeamsMessagingPolicies","command":"Get-CsTeamsMessagingPolicy","ok":true,
                "items":[{"identity":"Global","allowSecurityEndUserReporting":true}],"errorCategory":null,"errorId":null}]}
            """);

        saida.Runtime.Module.Should().Be("7.9.0");
        saida.Connected.Should().BeTrue();

        var falhou = saida.Reads.Single(r => r.Capability == "TeamsMeetingPolicies");
        falhou.Ok.Should().BeFalse();
        falhou.ErrorCategory.Should().Be("Throttled");
        falhou.ErrorId.Should().Be("TooManyRequests/HttpRequestException");
        falhou.Items.GetArrayLength().Should().Be(0);

        var leu = saida.Reads.Single(r => r.Capability == "TeamsMessagingPolicies");
        leu.Ok.Should().BeTrue();
        leu.Items.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void LeituraDaSaida_SemCampoItems_NaoQuebra_EVaziaNaoViraNulo()
    {
        var saida = PowerShellTeamsAdminReader.Parse(
            """{"connected":true,"reads":[{"capability":"TeamsClientConfiguration","command":"Get-CsTeamsClientConfiguration","ok":true}]}""");

        saida.Reads.Single().Items.GetArrayLength().Should().Be(0);
        saida.Runtime.PowerShell.Should().BeNull("o que a fonte não informou permanece nulo");
    }

    // ======================================================================================================
    //  [Revisão dirigida] Verificação de RUNTIME pelo caminho REAL do adaptador
    // ======================================================================================================

    /// <summary>
    /// A verificação executada no CI é a do PRODUTO: monta o leitor com as opções do ambiente e roda o script
    /// EMBUTIDO em modo de conferência de módulo. Aqui a forma do desfecho é travada com uma saída sintética —
    /// a execução real, com PowerShell e módulo de verdade, acontece dentro da imagem Linux no CI.
    /// </summary>
    [Fact]
    public async Task VerificacaoDeRuntime_SoAprovaComModuloImportadoETodosOsComandosPresentes()
    {
        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunAsync(new RuntimeFake(modulo: "7.9.0", ausentes: 0), saida);

        codigo.Should().Be(0);
        saida.ToString().Should().Contain(TeamsRuntimeDiagnostics.SuccessMarker).And.Contain("MicrosoftTeams=7.9.0");
    }

    [Fact]
    public async Task VerificacaoDeRuntime_ModuloNaoImportado_Reprova_SemMascararAFalha()
    {
        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunAsync(new RuntimeFake(modulo: null, ausentes: 0), saida);

        codigo.Should().Be(1);
        saida.ToString().Should().Contain("não foi importado").And.NotContain(TeamsRuntimeDiagnostics.SuccessMarker);
    }

    [Fact]
    public async Task VerificacaoDeRuntime_ComandoAusente_Reprova_EDizQual()
    {
        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunAsync(new RuntimeFake(modulo: "7.9.0", ausentes: 1), saida);

        codigo.Should().Be(1);
        saida.ToString().Should().Contain("comandos ausentes").And.NotContain(TeamsRuntimeDiagnostics.SuccessMarker);
    }

    [Fact]
    public async Task VerificacaoDeRuntime_FalhaDeTransporte_Reprova_SemExcecaoVazando()
    {
        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunAsync(new SemRuntime(), saida);

        codigo.Should().Be(1);
        saida.ToString().Should().Contain("FALHA no transporte");
    }

    /// <summary>A verificação lê as opções REAIS do ambiente — é isso que a torna equivalente ao caminho de coleta.</summary>
    [Fact]
    public async Task VerificacaoDeRuntime_UsaOExecutavelEOCaminhoDeModuloDaConfiguracao()
    {
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Executable"] = "aegis-pwsh-que-nao-existe",
                ["ModulePath"] = "/opt/aegis/psmodules",
            })
            .Build();

        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunFromConfigurationAsync(configuracao, saida);

        codigo.Should().Be(1, "o executável configurado não existe nesta máquina");
        saida.ToString().Should().Contain("aegis-pwsh-que-nao-existe").And.Contain("/opt/aegis/psmodules");
    }

    // ======================================================================================================
    //  [Revisão dirigida] Truncar não é sanitizar
    // ======================================================================================================

    /// <summary>
    /// Marcadores SINTÉTICOS com forma de segredo — nenhuma credencial real. O diagnóstico registrado não pode
    /// conter nenhum deles, e o texto útil em volta continua legível.
    /// </summary>
    [Theory]
    [InlineData("eyJhZWdpcyI6InQifQ.QUVHSVMtU0lOVEVUSUNP.YXNzaW5hdHVyYQ")]
    [InlineData("Authorization: Bearer AEGIS-MARCADOR-SINTETICO-0123456789")]
    [InlineData("client_secret=AEGIS-SEGREDO-SINTETICO-abcdefghijklmnop")]
    [InlineData("AEGISMARCADORSINTETICOxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public void DiagnosticoDoProcesso_EhSanitizado_NaoApenasTruncado(string marcador)
    {
        // O marcador fica ANTES do corte por tamanho: truncar sozinho o preservaria inteiro.
        var bruto = $"Falha ao conectar. {marcador} " + new string('x', 2000);

        var limpo = ScrubViaReader(bruto);
        limpo.Should().NotContain(marcador);
        limpo.Should().Contain("Falha ao conectar", "a informação útil sobre a causa é preservada");
        limpo.Should().Contain("[REDIGIDO]");
    }

    /// <summary>O token que o AEGIS entregou ao processo é removido por IGUALDADE, qualquer que seja sua forma.</summary>
    [Fact]
    public void SegredoConhecidoDaColeta_EhRemovidoPorIgualdade()
    {
        const string token = "token-sintetico-desta-coleta-0001";
        var limpo = ScrubViaReader($"o comando falhou usando {token} no cabecalho", token);

        limpo.Should().NotContain(token).And.Contain("[REDIGIDO]").And.Contain("o comando falhou");
    }

    /// <summary>
    /// Invoca a sanitização pelo mesmo tipo que o leitor usa. O tipo é INTERNO de propósito: sanitizar é
    /// responsabilidade do transporte, não superfície pública.
    /// </summary>
    private static string ScrubViaReader(string texto, params string?[] segredos)
    {
        var tipo = typeof(PowerShellTeamsAdminReader).Assembly
            .GetType("AegisScore.Connectors.Microsoft.Knight.Teams.TeamsDiagnosticScrubber")!;
        var metodo = tipo.GetMethod("Scrub", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)metodo.Invoke(null, [texto, segredos.Length == 0 ? null : segredos])!;
    }

    // ---- Duplas de teste ------------------------------------------------------------------------------

    private sealed class RuntimeFake : ITeamsAdminReader
    {
        private readonly string? _modulo;
        private readonly int _ausentes;
        public RuntimeFake(string? modulo, int ausentes) { _modulo = modulo; _ausentes = ausentes; }

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            CheckRuntimeAsync(ct);

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default)
        {
            var comandos = new[]
            {
                "Connect-MicrosoftTeams", "Disconnect-MicrosoftTeams", "Get-CsTeamsClientConfiguration",
                "Get-CsTenantFederationConfiguration", "Get-CsTeamsMeetingPolicy", "Get-CsTeamsMessagingPolicy",
                "Get-CsTeamsAppPermissionPolicy", "Get-AllM365TeamsApps", "Get-CsGroupPolicyAssignment",
            };
            var reads = comandos.Select((c, i) => new
            {
                capability = "ModuleCheck",
                command = c,
                ok = i >= _ausentes,
                items = Array.Empty<object>(),
                errorCategory = i >= _ausentes ? null : "Unavailable",
                errorId = i >= _ausentes ? null : "CommandNotFound",
            });

            var json = JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = _modulo, platform = "Unix" },
                connected = false,
                connectionErrorId = (string?)null,
                connectionErrorCategory = (string?)null,
                reads,
            });
            return Task.FromResult(PowerShellTeamsAdminReader.Parse(json));
        }
    }

    private sealed class SemRuntime : ITeamsAdminReader
    {
        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            CheckRuntimeAsync(ct);

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
            throw new TeamsAdminTransportException("O runtime do PowerShell não pôde ser iniciado neste ambiente.");
    }
}
