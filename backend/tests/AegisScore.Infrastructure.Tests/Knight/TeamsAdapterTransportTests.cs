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
    /// <summary>Como o script se comporta no CENÁRIO SINTÉTICO de coleta (o caminho de ERRO do adaptador).</summary>
    public enum Coleta
    {
        /// <summary>Falhou e classificou: é o único desfecho aceitável.</summary>
        FalhaClassificada,

        /// <summary>Falhou sem categoria: o operador fica sem saber o que houve.</summary>
        SemCategoria,

        /// <summary>Caiu no documento mínimo de emergência: a montagem do resultado quebrou.</summary>
        DocumentoMinimo,

        /// <summary>Devolveu o token recebido dentro do diagnóstico.</summary>
        VazaMarcador,

        /// <summary>Estabeleceu conexão — impossível com marcadores, e inaceitável nesta verificação.</summary>
        Conecta,
    }

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
            "Get-CsTeamsMessagingPolicy", "Get-CsTeamsAppPermissionPolicy", "Get-CsGroupPolicyAssignment",
        })
            script.Should().Contain(comando);

        foreach (var proibido in new[] { "Set-Cs", "New-Cs", "Remove-Cs", "Grant-Cs", "Update-Cs", "Invoke-RestMethod", "Invoke-WebRequest" })
            script.Should().NotContain(proibido, $"o adaptador é somente leitura e não fala com endereço nenhum por conta própria ({proibido})");

        // [Revisão dirigida] Nenhum comando da lista oficial de NÃO SUPORTADOS com autenticação de aplicativo — que
        // é a única forma usada por este conector. Chamá-los produziria falha por motivo errado e diagnóstico
        // enganoso ("sem permissão" no lugar de "não suportado"). A ausência é verificada no EXECUTADO: nas linhas
        // de comando, não nos comentários que explicam por que eles não estão aqui.
        // https://learn.microsoft.com/en-us/microsoftteams/teams-powershell-application-authentication
        var executado = string.Join("\n", script.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));
        foreach (var incompativel in new[]
        {
            "Get-AllM365TeamsApps", "Get-M365TeamsApp", "Update-M365TeamsApp", "Get-M365UnifiedTenantSettings",
            "Get-M365UnifiedCustomPendingApps", "New-Team", "Get-MultiGeoRegion", "Set-CsOnlineApplicationInstance",
        })
            executado.Should().NotContain(incompativel,
                $"{incompativel} não é suportado com autenticação de aplicativo e não pode estar no fluxo operacional");

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

    // ======================================================================================================
    //  [Revisão dirigida] O DIAGNÓSTICO da falha de transporte, com um processo auxiliar CONTROLADO
    //
    //  O auxiliar é um programa de verdade, escrito pelo teste: ele recebe (ou não) a entrada padrão, escreve um
    //  diagnóstico SINTÉTICO conhecido em erro-padrão e termina sem resultado. Assim o teste sabe exatamente o
    //  que entrou, e pode exigir o que sai: a causa útil preservada e os marcadores de segredo ausentes.
    //  Nenhuma credencial real é usada — os “segredos” são marcadores inventados aqui.
    // ======================================================================================================

    /// <summary>Marcadores SINTÉTICOS. Nenhum deles é credencial de nada; existem só para serem procurados.</summary>
    private const string TokenSintetico =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJhZWdpcy1tYXJjYWRvci1zaW50ZXRpY28ifQ.YWVnaXNNYXJjYWRvclNpbnRldGljb0Fzc2luYXR1cmE";
    private const string SegredoOpaco = "AEGISMARCADORSINTETICOxQ7bT2mK9wL4vR8nZ1cY6dH3sJ0pA5uF";
    private const string CausaUtil = "Get-CsTeamsMeetingPolicy nao respondeu";

    /// <summary>
    /// Escreve um programa auxiliar e devolve o caminho dele. O adaptador chama o executável com argumentos de
    /// PowerShell, que este programa simplesmente ignora — e é o que se quer: o comportamento sob teste é o do
    /// ADAPTADOR diante de um processo que termina sem resultado, não o de nenhum interpretador.
    /// </summary>
    private static string Auxiliar(string nome, bool consomeEntrada, string erroPadrao, int codigoDeSaida)
    {
        var pasta = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "aegis-teams-aux-" + Guid.NewGuid().ToString("n"))).FullName;
        var linhas = erroPadrao.Split('\n').Where(l => l.Trim().Length > 0).Select(l => l.Trim()).ToList();

        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(pasta, nome + ".cmd");
            File.WriteAllText(cmd,
                "@echo off\r\n"
                + (consomeEntrada ? "more > nul\r\n" : "")
                + string.Concat(linhas.Select(l => "1>&2 echo " + l + "\r\n"))
                + "exit /b " + codigoDeSaida + "\r\n",
                new UTF8Encoding(false));
            return cmd;
        }

        var sh = Path.Combine(pasta, nome + ".sh");
        File.WriteAllText(sh,
            "#!/bin/sh\n"
            + (consomeEntrada ? "cat > /dev/null\n" : "")
            + string.Concat(linhas.Select(l => "echo \"" + l + "\" >&2\n"))
            + "exit " + codigoDeSaida + "\n",
            new UTF8Encoding(false));
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>
    /// O caminho normal da falha: o auxiliar CONSOME a entrada, escreve um diagnóstico com segredos sintéticos
    /// misturados à causa útil e sai com código próprio. O adaptador deve entregar a causa e nomear o código —
    /// e nenhum dos marcadores pode sobreviver, incluindo o token que a própria chamada entregou ao processo.
    /// </summary>
    [Fact]
    public async Task ProcessoSemResultado_TrazACausaUtilSanitizada_ESemNenhumSegredo()
    {
        var diagnostico =
            "Authorization: Bearer " + TokenSintetico + "\n"
            + CausaUtil + " (client_secret=" + SegredoOpaco + ")\n"
            + "token de entrada ecoado: " + TokenSintetico;

        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = Auxiliar("consome", consomeEntrada: true, diagnostico, codigoDeSaida: 7),
            Timeout = TimeSpan.FromSeconds(30),
        });

        var erro = await FluentActions
            .Awaiting(() => reader.ReadAsync(new TeamsAdminCredentials("locatario-sintetico", TokenSintetico, SegredoOpaco)))
            .Should().ThrowAsync<TeamsAdminTransportException>();
        var mensagem = erro.Which.Message;

        // 1) A CAUSA ÚTIL chega a quem opera — é ela que diz o que investigar.
        mensagem.Should().Contain("código 7", "sem o código de saída, o diagnóstico não identifica o desfecho do processo");
        mensagem.Should().Contain("sem devolver resultado");
        mensagem.Should().Contain(CausaUtil, "a causa é justamente o que não pode ser perdido na sanitização");

        // 2) NENHUM marcador sintético sobrevive — nem por igualdade (tokens da chamada), nem por forma.
        foreach (var marcador in new[] { TokenSintetico, SegredoOpaco, "AEGISMARCADORSINTETICO" })
            mensagem.Should().NotContain(marcador, "segredo não atravessa a fronteira do diagnóstico");
        mensagem.Should().NotContain("Bearer ey", "o cabeçalho de autorização não sai em texto");
        mensagem.Should().Contain("[REDIGIDO]", "o que foi removido é declarado, não some em silêncio");
    }

    /// <summary>
    /// [Revisão dirigida] O processo que termina ANTES de consumir a entrada. Foi assim que o CI reprovou: o
    /// adaptador escreve os tokens, o cano rompe, e o IOException era tratado ANTES da espera pelo processo —
    /// trocando a causa real (“terminou com código N” + diagnóstico) por “a comunicação falhou”.
    ///
    /// O rompimento aqui é DETERMINÍSTICO nos dois sistemas: o auxiliar nunca lê a entrada e a credencial
    /// sintética é maior que o buffer do cano, então a escrita não tem como se completar. A garantia exigida é a
    /// mesma do caso anterior — causa útil preservada, segredo nenhum.
    /// </summary>
    [Fact]
    public async Task ProcessoQueTerminaAntesDeConsumirAEntrada_PreservaACausa_ENaoAComunicacaoFalhou()
    {
        var tokenEnorme = TokenSintetico + new string('A', 256 * 1024);

        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = Auxiliar("ignora", consomeEntrada: false,
                CausaUtil + " (client_secret=" + SegredoOpaco + ")", codigoDeSaida: 3),
            Timeout = TimeSpan.FromSeconds(30),
        });

        var erro = await FluentActions
            .Awaiting(() => reader.ReadAsync(new TeamsAdminCredentials("locatario-sintetico", tokenEnorme, SegredoOpaco)))
            .Should().ThrowAsync<TeamsAdminTransportException>();
        var mensagem = erro.Which.Message;

        mensagem.Should().NotContain("A comunicação com o processo de coleta do Microsoft Teams falhou",
            "o cano rompido diz que o processo já terminou — não é a causa, é a consequência dela");
        mensagem.Should().Contain("código 3").And.Contain("sem devolver resultado");
        mensagem.Should().Contain(CausaUtil);
        foreach (var marcador in new[] { TokenSintetico, SegredoOpaco, "AEGISMARCADORSINTETICO" })
            mensagem.Should().NotContain(marcador);
    }

    /// <summary>Processo que termina sem resultado E sem diagnóstico: o adaptador diz isso, em vez de calar.</summary>
    [Fact]
    public async Task ProcessoSemResultadoESemDiagnostico_DizQueNaoHouveDiagnostico()
    {
        var reader = new PowerShellTeamsAdminReader(new TeamsPowerShellOptions
        {
            Executable = Auxiliar("mudo", consomeEntrada: true, "", codigoDeSaida: 4),
            Timeout = TimeSpan.FromSeconds(30),
        });

        var erro = await FluentActions.Awaiting(() => reader.CheckRuntimeAsync())
            .Should().ThrowAsync<TeamsAdminTransportException>();
        erro.Which.Message.Should().Contain("código 4").And.Contain("não escreveu diagnóstico algum");
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
        saida.ToString().Should().Contain("module-check=OK").And.Contain("coleta-sintetica=OK",
            "a marca de sucesso só é impressa depois dos DOIS cenários — o de sucesso e o de erro");
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

    /// <summary>
    /// [Revisão dirigida] DEFEITO REPRODUZIDO: o identificador técnico do erro era redigido só por ser longo, e
    /// nomes de exceção legítimos passam de 24 caracteres com folga. O diagnóstico saía “[REDIGIDO]” e o operador
    /// perdia a única pista da causa — sanitizar não pode custar a informação que justifica sanitizar.
    ///
    /// O que continua sendo redigido: cadeias com cara de segredo codificado. O que passa: nome composto.
    /// </summary>
    [Theory]
    [InlineData("UnauthorizedAccessException", true)]                       // 27 caracteres, nome de tipo
    [InlineData("DocumentoDeResultadoIndisponivel", true)]                  // 32 caracteres, sentinela do script
    [InlineData("TooManyRequests/HttpRequestException", true)]              // dois nomes com separador
    [InlineData("CommandNotFound", true)]
    [InlineData("AQABAAEAAADnfolhJpSnRYB1SVj4xhFRm1x9k2LpQ7vT4wXyZ0aB", false)]  // dígitos: base64
    [InlineData("0123456789012345678901234567890123", false)]              // só dígitos
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEF", false)]                // caixa uniforme
    [InlineData("ZXlKaGJHY2lPaUpJVXpJMU5pSXNJblI9cCI6", false)]            // base64 com “=”
    public void IdentificadorTecnico_PreservaNomeDeErro_ERedigeSegredoCodificado(string id, bool preservado)
    {
        var saida = PowerShellTeamsAdminReader.Parse(
            $$"""{"connected":false,"connectionErrorCategory":"Error","connectionErrorId":"{{id}}","reads":[]}""");

        if (preservado)
            saida.ConnectionErrorId.Should().Be(id, "é um nome técnico, não um segredo — e é ele que diz a causa");
        else
            saida.ConnectionErrorId.Should().Be("[REDIGIDO]", "tem forma de segredo codificado");
    }

    /// <summary>
    /// [Revisão dirigida] A verificação não para no module-check: ela executa o script no CAMINHO DE ERRO, com
    /// marcadores no lugar dos tokens, e exige um documento de resultado íntegro. É esse cenário que demonstra
    /// comportamento — e cada forma de quebrá-lo reprova, sem exceção.
    /// </summary>
    [Theory]
    [InlineData(Coleta.SemCategoria, "não classificou a falha")]
    [InlineData(Coleta.DocumentoMinimo, "documento mínimo de emergência")]
    [InlineData(Coleta.VazaMarcador, "marcador sintético de segredo apareceu")]
    [InlineData(Coleta.Conecta, "não pode resultar em conexão estabelecida")]
    public async Task CenarioSintetico_CadaQuebraDoContratoDeSaida_Reprova(Coleta coleta, string motivo)
    {
        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunAsync(new RuntimeFake("7.9.0", 0, coleta), saida);

        codigo.Should().Be(1);
        saida.ToString().Should().Contain(motivo).And.NotContain(TeamsRuntimeDiagnostics.SuccessMarker);

        // O module-check passou: a reprovação é do cenário sintético, e a saída deixa isso claro.
        saida.ToString().Should().Contain("module-check=OK").And.NotContain("coleta-sintetica=OK");
    }

    /// <summary>Sem documento algum no cenário sintético, a verificação reprova dizendo que o contrato quebrou.</summary>
    [Fact]
    public async Task CenarioSintetico_SemDocumentoDeResultado_Reprova()
    {
        var saida = new StringWriter();
        var codigo = await TeamsRuntimeDiagnostics.RunAsync(new SemColeta(), saida);

        codigo.Should().Be(1);
        saida.ToString().Should().Contain("não devolveu documento de resultado")
            .And.NotContain(TeamsRuntimeDiagnostics.SuccessMarker);
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

    internal sealed class RuntimeFake : ITeamsAdminReader
    {
        private readonly string? _modulo;
        private readonly int _ausentes;
        private readonly Coleta _coleta;

        public RuntimeFake(string? modulo, int ausentes, Coleta coleta = Coleta.FalhaClassificada)
        {
            _modulo = modulo;
            _ausentes = ausentes;
            _coleta = coleta;
        }

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(credentials);
            var json = JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = _modulo, platform = "Unix" },
                connected = _coleta == Coleta.Conecta,
                connectionErrorCategory = _coleta switch
                {
                    Coleta.SemCategoria or Coleta.Conecta => null,
                    Coleta.DocumentoMinimo => "Error",
                    _ => "Authentication",
                },
                connectionErrorId = _coleta switch
                {
                    Coleta.SemCategoria or Coleta.Conecta => null,
                    Coleta.DocumentoMinimo => "DocumentoDeResultadoIndisponivel",
                    Coleta.VazaMarcador => credentials.GraphToken,
                    _ => "InvalidAccessToken/AuthenticationException",
                },
                reads = Array.Empty<object>(),
            });
            return Task.FromResult(PowerShellTeamsAdminReader.Parse(json));
        }

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default)
        {
            var comandos = new[]
            {
                "Connect-MicrosoftTeams", "Disconnect-MicrosoftTeams", "Get-CsTeamsClientConfiguration",
                "Get-CsTenantFederationConfiguration", "Get-CsTeamsMeetingPolicy", "Get-CsTeamsMessagingPolicy",
                "Get-CsTeamsAppPermissionPolicy", "Get-CsGroupPolicyAssignment",
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

    /// <summary>Module-check passa, mas a coleta sintética não devolve documento — o contrato quebrou no erro.</summary>
    private sealed class SemColeta : ITeamsAdminReader
    {
        private readonly RuntimeFake _ok = new("7.9.0", 0);

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => _ok.CheckRuntimeAsync(ct);

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            throw new TeamsAdminTransportException("O adaptador do Microsoft Teams terminou com código 1 sem devolver resultado.");
    }

    private sealed class SemRuntime : ITeamsAdminReader
    {
        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            CheckRuntimeAsync(ct);

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
            throw new TeamsAdminTransportException("O runtime do PowerShell não pôde ser iniciado neste ambiente.");
    }
}
