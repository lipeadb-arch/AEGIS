using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] O adaptador de coleta do Exchange Online como CONTRATO: o script embutido, a
/// leitura da saída e a verificação de runtime.
///
/// O transporte propriamente dito (processo descartável, entrada padrão, tempo limite, árvore de processos e
/// sanitização do diagnóstico) é COMPARTILHADO com o adaptador do Microsoft Teams e continua verificado pela
/// bateria dele — duplicá-lo aqui verificaria a mesma implementação duas vezes. O que é deste adaptador, e por
/// isso mora aqui, é o contrato do Exchange: comandos de leitura, conexão por token, teto de enumeração e a
/// distinção entre os comandos do MÓDULO e os comandos importados pela CONEXÃO.
/// </summary>
public sealed class ExchangeAdapterTransportTests
{
    private const string ResourceName = "AegisScore.Knight.Exchange.Collect.ps1";

    private static string Script()
    {
        using var stream = typeof(PowerShellExchangeAdminReader).Assembly.GetManifestResourceStream(ResourceName);
        stream.Should().NotBeNull("o adaptador é um recurso embutido — não um arquivo solto no disco do servidor");
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Só as linhas EXECUTADAS: comentários explicam decisões e não podem satisfazer uma asserção.</summary>
    private static string Executado(string script) =>
        string.Join("\n", script.Split('\n').Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

    [Fact]
    public void ScriptEmbutido_ExecutaSomenteComandosDeLeituraFixos()
    {
        var script = Script();

        foreach (var comando in new[]
        {
            "Get-OrganizationConfig", "Get-TransportConfig", "Get-SharingPolicy", "Get-OwaMailboxPolicy",
            "Get-TransportRule", "Get-RoleAssignmentPolicy", "Get-ExternalInOutlook",
            "Get-HostedOutboundSpamFilterPolicy", "Get-Mailbox", "Get-User", "Get-CASMailbox",
            "Get-MailboxAuditBypassAssociation",
        })
            script.Should().Contain(comando);

        // Nenhum verbo de escrita. O prefixo genérico não basta: "Set-" apareceria em Set-StrictMode, que é
        // configuração do próprio script — por isso a proibição é sobre os cmdlets do Exchange.
        foreach (var proibido in new[]
        {
            "Set-Mailbox", "Set-OrganizationConfig", "Set-TransportConfig", "New-TransportRule",
            "Remove-", "Disable-", "Enable-", "Add-RoleGroupMember", "New-ServicePrincipal",
            "Invoke-RestMethod", "Invoke-WebRequest",
        })
            Executado(script).Should().NotContain(proibido,
                $"o adaptador é somente leitura e não fala com endereço nenhum por conta própria ({proibido})");

        // A sessão é sempre encerrada, e a conexão é a de APLICATIVO por TOKEN — não por certificado.
        script.Should().Contain("Disconnect-ExchangeOnline");
        script.Should().Contain("Connect-ExchangeOnline");
        script.Should().Contain("-AccessToken $accessToken");
        script.Should().Contain("-Organization $organization",
            "a documentação exige o parâmetro de organização junto do token de aplicativo");
        foreach (var certificado in new[] { "-CertificateThumbprint", "-CertificateFilePath", "-Certificate ", "-ManagedIdentity" })
            Executado(script).Should().NotContain(certificado,
                "este conector autentica por token obtido com o segredo do cliente; nenhum certificado é exigido do locatário");

        // O token entra pela ENTRADA PADRÃO — nunca por argumento de linha de comando nem por ambiente.
        script.Should().Contain("[Console]::In.ReadToEnd()");
        Executado(script).Should().NotContain("$env:AEGIS", "segredo não trafega por variável de ambiente");
    }

    /// <summary>
    /// A distinção que este bloco inteiro depende: comandos do MÓDULO existem assim que ele é importado;
    /// comandos de SESSÃO só existem depois de uma conexão. O script declara os dois conjuntos SEPARADOS, e os
    /// conjuntos não se misturam — se misturassem, uma verificação offline "aprovaria" comandos que ela não tem
    /// como encontrar, e o gate ficaria verde para um ambiente em que nenhuma leitura funcionaria.
    /// </summary>
    [Fact]
    public void ScriptEmbutido_SeparaComandosDoModuloDosImportadosPelaConexao()
    {
        var script = Script();

        script.Should().Contain("$script:ModuleCommands");
        script.Should().Contain("$script:SessionCommands");

        var modulo = Trecho(script, "$script:ModuleCommands", "$script:SessionCommands");
        modulo.Should().Contain("Connect-ExchangeOnline").And.Contain("Disconnect-ExchangeOnline");
        foreach (var deSessao in new[] { "Get-OrganizationConfig", "Get-Mailbox", "Get-TransportRule" })
            modulo.Should().NotContain(deSessao,
                $"{deSessao} não existe antes da conexão: declará-lo como comando do módulo tornaria o gate offline mentiroso");

        // E a verificação de runtime devolve o contrato de sessão como DECLARAÇÃO, com capacidade própria.
        script.Should().Contain("SessionCommandContract");
    }

    [Fact]
    public void ScriptEmbutido_NaoReproduzAMensagemDaExcecao()
    {
        var script = Script();

        // A mensagem da exceção pode ser LIDA para classificar a falha localmente — o que ela não pode é ser
        // EMITIDA. A leitura fica confinada à classificação; fora dela, a mensagem não é tocada.
        var classificacao = Trecho(script, "function Get-ErrorCategory", "function Get-ErrorId");
        classificacao.Should().Contain(".Message", "é ali, e só ali, que a mensagem é usada — para classificar");

        script.Replace(classificacao, "").Should().NotContain(".Message",
            "a mensagem da exceção é texto livre da fonte e não atravessa a fronteira do processo");

        script.Should().Contain("FullyQualifiedErrorId").And.Contain("errorCategory").And.Contain("errorId");
    }

    /// <summary>
    /// A recusa por FALTA DE PAPEL de diretório é classificada como permissão insuficiente — e não como falha de
    /// autenticação. A diferença muda o que o operador vai corrigir: a permissão de API existe, o papel não.
    /// </summary>
    [Fact]
    public void ScriptEmbutido_ClassificaAFaltaDePapelComoAutorizacao()
    {
        var classificacao = Trecho(Script(), "function Get-ErrorCategory", "function Get-ErrorId");
        classificacao.Should().Contain("role assigned",
            "“the role assigned to application isn't supported in this scenario” é a recusa por falta de papel");

        // A asserção é sobre a linha EXECUTADA: o comentário acima dela explica a decisão e não a satisfaz.
        var linhaDePermissao = Executado(classificacao).Split('\n')
            .Single(l => l.Contains("role assigned", StringComparison.Ordinal));
        linhaDePermissao.Should().Contain("InsufficientPermission");
    }

    [Fact]
    public void ScriptEmbutido_NaoAceitaComandoVindoDoCliente()
    {
        var script = Script();
        foreach (var perigoso in new[] { "Invoke-Expression", "iex ", "[scriptblock]::Create", "& $request", "Start-Process" })
            script.Should().NotContain(perigoso, "nenhuma entrada do cliente pode virar comando executado");

        // O que vem da entrada é usado como VALOR (token, domínio, teto), nunca como comando.
        script.Should().Contain("Get-Prop $request 'accessToken'");
        script.Should().Contain("Get-Prop $request 'organization'");
    }

    /// <summary>
    /// O script SEMPRE escreve um documento de resultado — inclusive quando o próprio caminho de erro falha. Sem
    /// isso, um erro dentro do tratamento de erro faria o processo terminar sem saída, e o AEGIS veria apenas
    /// "terminou com código 1", sem categoria nem motivo.
    /// </summary>
    [Fact]
    public void ScriptEmbutido_SempreEscreveUmDocumentoDeResultado()
    {
        var script = Script();
        script.Should().Contain("DocumentoDeResultadoIndisponivel",
            "o documento mínimo de emergência é a última linha de defesa do contrato de saída");

        // A enumeração tem TETO, e o teto vigente sai no documento: o relatório precisa dizer o limite real da
        // coleta que produziu o veredito.
        script.Should().Contain("-ResultSize $script:EnumerationLimit");
        script.Should().Contain("enumerationLimit");
        script.Should().Contain("truncated");
    }

    // ---- Leitura da saída -------------------------------------------------------------------------------

    [Fact]
    public void LeituraDaSaida_PreservaTruncamentoTetoECategoriaPorComando()
    {
        var saida = PowerShellExchangeAdminReader.Parse("""
            {
              "runtime": { "powerShell": "7.4.7", "module": "3.9.2", "platform": "Unix" },
              "connected": true,
              "enumerationLimit": 5000,
              "reads": [
                { "capability": "ExchangeMailboxes", "command": "Get-Mailbox", "ok": true, "items": [], "truncated": true },
                { "capability": "ExchangeCasMailboxes", "command": "Get-CASMailbox", "ok": false, "items": [],
                  "truncated": false, "errorCategory": "InsufficientPermission",
                  "errorId": "AuthorizationFailed/UnauthorizedAccessException" }
              ]
            }
            """);

        saida.Connected.Should().BeTrue();
        saida.EnumerationLimit.Should().Be(5000);
        saida.Runtime.Module.Should().Be("3.9.2");

        var caixas = saida.Reads.Single(r => r.Capability == "ExchangeMailboxes");
        caixas.Ok.Should().BeTrue();
        caixas.Truncated.Should().BeTrue("uma lista que atingiu o teto não é a organização inteira");

        var cas = saida.Reads.Single(r => r.Capability == "ExchangeCasMailboxes");
        cas.Ok.Should().BeFalse();
        cas.ErrorCategory.Should().Be("InsufficientPermission");
        cas.ErrorId.Should().Be("AuthorizationFailed/UnauthorizedAccessException",
            "um identificador técnico legítimo é preservado — sanitizar não pode custar o diagnóstico");
    }

    [Fact]
    public void LeituraDaSaida_SemCampoItems_NaoQuebra_ESemTruncamentoEhFalso()
    {
        var saida = PowerShellExchangeAdminReader.Parse(
            """{ "runtime": {}, "connected": true, "reads": [ { "capability": "X", "command": "Get-X", "ok": true } ] }""");

        saida.Reads.Should().ContainSingle();
        saida.Reads[0].Items.ValueKind.Should().Be(JsonValueKind.Array);
        saida.Reads[0].Items.GetArrayLength().Should().Be(0);
        saida.Reads[0].Truncated.Should().BeFalse();
        saida.EnumerationLimit.Should().BeNull("o teto não informado é desconhecido, não zero");
    }

    [Fact]
    public void LeituraDaSaida_SegredoNoIdentificador_EhRedigido()
    {
        var jwt = "eyJhZWdpcyI6ImV4byJ9.QUVHSVMtRVhPLVNJTlRFVElDTw.YXNzaW5hdHVyYQ";
        var saida = PowerShellExchangeAdminReader.Parse(
            $$"""{ "runtime": {}, "connected": false, "connectionErrorId": "{{jwt}}", "reads": [] }""");

        saida.ConnectionErrorId.Should().NotContain("eyJ").And.Be("[REDIGIDO]");
    }

    [Fact]
    public void LeituraDaSaida_ResultadoIlegivel_ViraFalhaDeTransporteDeclarada()
    {
        FluentActions.Invoking(() => PowerShellExchangeAdminReader.Parse("isto não é json"))
            .Should().Throw<ExchangeAdminTransportException>()
            .WithMessage("*ilegível*");
    }

    // ---- Verificação de runtime -------------------------------------------------------------------------

    /// <summary>
    /// A verificação de runtime aprova o módulo e os comandos DELE, e diz — na saída — que os comandos de sessão
    /// NÃO foram verificados. Aprovar implicitamente os dois conjuntos seria declarar provado um ambiente em que
    /// nenhuma leitura do Exchange funcionaria.
    /// </summary>
    [Fact]
    public async Task VerificacaoDeRuntime_ProvaOModulo_EDeclaraOsComandosDeSessaoComoNaoVerificados()
    {
        var saida = new StringWriter();
        var codigo = await ExchangeRuntimeDiagnostics.RunAsync(new RuntimeFalso(moduloPresente: true), saida);

        codigo.Should().Be(0);
        var texto = saida.ToString();
        texto.Should().Contain("ExchangeOnlineManagement=3.9.2");
        texto.Should().Contain("comandos do módulo verificados=2 ausentes=0");
        texto.Should().Contain("comandos de sessão declarados=3");
        texto.Should().Contain("NÃO verificados aqui")
            .And.Contain("não comprova autorização",
                "um gate offline não pode ser lido como prova de que a coleta vai funcionar no locatário");
        texto.Should().Contain(ExchangeRuntimeDiagnostics.SuccessMarker);

        // E o ALCANCE sai junto do sucesso. Importar o módulo prova carregamento e comandos locais; não prova
        // compatibilidade integral com esta distribuição, autenticação, comandos de sessão nem coleta alguma.
        // Sem estas linhas, o marcador verde seria lido como "o Exchange está validado nesta imagem".
        texto.Should().Contain(ExchangeRuntimeDiagnostics.ScopeMarker);
        texto.Should().Contain("NÃO PROVADO:")
            .And.Contain("Debian")
            .And.Contain("autenticação")
            .And.Contain("coleta real");
    }

    [Fact]
    public async Task VerificacaoDeRuntime_SemModulo_Reprova_ENaoMascaraAFalha()
    {
        var saida = new StringWriter();
        var codigo = await ExchangeRuntimeDiagnostics.RunAsync(new RuntimeFalso(moduloPresente: false), saida);

        codigo.Should().Be(1);
        saida.ToString().Should().Contain("FALHA").And.NotContain(ExchangeRuntimeDiagnostics.SuccessMarker);
    }

    /// <summary>Adaptador falso que devolve o documento da verificação de runtime, sem processo externo algum.</summary>
    private sealed class RuntimeFalso : IExchangeAdminReader
    {
        private readonly bool _moduloPresente;
        internal RuntimeFalso(bool moduloPresente) => _moduloPresente = moduloPresente;

        public Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
            Task.FromResult(PowerShellExchangeAdminReader.Parse(JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = _moduloPresente ? "3.9.2" : null, platform = "Unix" },
                connected = false,
                enumerationLimit = 5000,
                reads = new object[]
                {
                    new { capability = "ModuleCheck", command = "Connect-ExchangeOnline", ok = true, items = Array.Empty<object>(), truncated = false },
                    new { capability = "ModuleCheck", command = "Disconnect-ExchangeOnline", ok = true, items = Array.Empty<object>(), truncated = false },
                    new
                    {
                        capability = "SessionCommandContract", command = "(importados pela conexão)", ok = true,
                        items = new object[]
                        {
                            new { command = "Get-OrganizationConfig" },
                            new { command = "Get-Mailbox" },
                            new { command = "Get-TransportRule" },
                        },
                        truncated = false,
                    },
                },
            })));

        /// <summary>O cenário sintético do caminho de ERRO: falha classificada, sem marcador na saída.</summary>
        public Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(PowerShellExchangeAdminReader.Parse(JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = "3.9.2", platform = "Unix" },
                connected = false,
                connectionErrorCategory = "AuthenticationFailure",
                connectionErrorId = "InvalidToken/SecurityTokenMalformedException",
                enumerationLimit = 5000,
                reads = Array.Empty<object>(),
            })));
    }

    private static string Trecho(string texto, string inicio, string fim)
    {
        var i = texto.IndexOf(inicio, StringComparison.Ordinal);
        var j = texto.IndexOf(fim, i + inicio.Length, StringComparison.Ordinal);
        i.Should().BeGreaterThanOrEqualTo(0, inicio);
        j.Should().BeGreaterThan(i, fim);
        return texto[i..j];
    }
}
