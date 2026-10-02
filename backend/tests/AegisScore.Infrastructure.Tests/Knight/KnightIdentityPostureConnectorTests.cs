using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-ACCESS-01] "Testar conexão" (<see cref="KnightIdentityPostureConnector"/>) do conector
/// Microsoft/IdentityPosture — a credencial que alimenta as três fontes do KNIGHT (Entra ID, Teams, Exchange
/// Online). Reproduz e fecha a lacuna encontrada nesta entrega: ANTES desta classe, não havia nenhum
/// <c>IEvidenceConnector</c> registrado para essa combinação, e <c>ConnectorsController.Test</c> devolvia 501
/// sem informação nenhuma sobre as três fontes.
/// </summary>
public sealed class KnightIdentityPostureConnectorTests
{
    private static readonly Guid Tenant = Guid.Parse("ac0e5000-0000-4000-8000-000000000001");

    private static ConnectorConfig Config(string encryptedSettings = "cifrado-sintetico") => new()
    {
        TenantId = Tenant,
        Provider = ConnectorProvider.Microsoft,
        Capability = ConnectorCapability.IdentityPosture,
        EncryptedSettings = encryptedSettings,
    };

    private static KnightIdentityPostureConnector ConnectorFor(
        IConnectorSecretProtector? protector = null, FakeGraph? graph = null,
        ITeamsAdminReader? teamsReader = null, IExchangeAdminReader? exchangeReader = null) =>
        new(
            protector ?? new FakeProtector(),
            graph ?? new FakeGraph(),
            new FakeTeamsTokens(),
            teamsReader ?? new FakeTeamsReader(connected: true, readOk: true),
            new FakeExchangeTokens(),
            exchangeReader ?? new FakeExchangeReader(connected: true, readOk: true));

    [Fact]
    public async Task TresFontesConfiguradasEAceitas_DevolveHealthy()
    {
        var health = await ConnectorFor().TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Healthy);
        health.Message.Should().Contain("Microsoft Entra ID").And.Contain("Microsoft Teams").And.Contain("Exchange Online");
        health.Message.Should().Contain(
            "PRIMEIRA verificação real de que o método por segredo de cliente funciona",
            "esta é a razão de ser deste pacote para o Exchange — não apenas mais um teste de token");
    }

    [Fact]
    public async Task CredencialIlegivel_DevolveFailed_SemTentarNenhumaFonte()
    {
        var health = await ConnectorFor(protector: new FakeProtector(legivel: false))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Failed);
        health.Message.Should().Contain("sem credenciais legíveis");
    }

    [Fact]
    public async Task ConectorSemSegredoConfigurado_DevolveFailed_SemTentarNenhumaFonte()
    {
        var health = await ConnectorFor().TestAsync(Config(encryptedSettings: ""), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Failed);
        health.Message.Should().Contain("sem credenciais legíveis");
    }

    [Fact]
    public async Task ExchangeRecusaConexao_NaoAtribuiCausaEspecifica_MasEnumeraAsVerificacoes()
    {
        var health = await ConnectorFor(
            exchangeReader: new FakeExchangeReader(connected: false, readOk: false, category: "InsufficientPermission"))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded, "Entra e Teams continuam respondendo");
        health.Message.Should().Contain("Exchange Online:").And.Contain("não identifica a causa");
        health.Message.Should().Contain("Exchange.ManageAsApp");
        health.Message.Should().Contain(
            "sem confirmação documental de que o serviço o aceite",
            "a recusa não pode virar uma afirmação de que o método por segredo de cliente não funciona");
    }

    [Fact]
    public async Task TeamsConectaMasLeituraRecusada_NomeiaOComandoEOrientaOPapel()
    {
        var health = await ConnectorFor(teamsReader: new FakeTeamsReader(connected: true, readOk: false))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Get-CsTeamsClientConfiguration");
        health.Message.Should().Contain("Leitor do Teams");
    }

    [Fact]
    public async Task EntraTokenRecusado_MensagemDizFalhaDeAutenticacao()
    {
        var health = await ConnectorFor(graph: new FakeGraph(fail: EntraGraphErrorKind.AuthFailure))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded, "Teams e Exchange continuam respondendo");
        health.Message.Should().Contain("Microsoft Entra ID:").And.Contain("falha de autenticação");
    }

    [Fact]
    public async Task AdaptadorDeTransporteQuebrado_NaoDerrubaOTeste_DizQueNadaPodeSerConcluido()
    {
        var health = await ConnectorFor(teamsReader: new QuebraTeams())
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Microsoft Teams:")
            .And.Contain("não pôde ser executado")
            .And.Contain("Nenhuma conclusão sobre a credencial é possível");
    }

    [Fact]
    public async Task TeamsLeituraRecusadaPorLicenca_NaoOrientaConcessaoDePapel()
    {
        var health = await ConnectorFor(
            teamsReader: new FakeTeamsReader(connected: true, readOk: false, readErrorCategory: "LimitedByLicense"))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Microsoft Teams:").And.Contain("licença");
        health.Message.Should().NotContain("Confira o papel",
            "uma recusa por licença não é resolvida concedendo mais papel — orientar isso seria uma causa inventada");
    }

    [Fact]
    public async Task ExchangeConexaoRecusadaPorLimiteDeTaxa_NaoOrientaConcessaoDePapel()
    {
        var health = await ConnectorFor(
            exchangeReader: new FakeExchangeReader(connected: false, readOk: false, category: "Throttled"))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Exchange Online:").And.Contain("limite de taxa");
        health.Message.Should().NotContain("Confira",
            "uma recusa por limite de taxa não é resolvida conferindo permissão, papel ou domínio — é só questão de tentar de novo");
    }

    [Fact]
    public async Task EntraFalhaDeTransporte_NaoDerrubaOTeste_DemaisFontesContinuam()
    {
        var health = await ConnectorFor(graph: new FakeGraph(throwRaw: new System.Net.Http.HttpRequestException("conexão recusada")))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded, "Teams e Exchange continuam respondendo mesmo com falha de rede no Entra");
        health.Message.Should().Contain("Microsoft Entra ID:")
            .And.Contain("rede")
            .And.Contain("causa não determinada");
        health.Message.Should().Contain("Microsoft Teams").And.Contain("Exchange Online",
            "uma falha isolada numa fonte não pode impedir o diagnóstico das outras duas");
    }

    [Fact]
    public async Task EntraFalhaInternaInesperada_MensagemNeutra_NaoViraFalhaDeRede()
    {
        var health = await ConnectorFor(graph: new FakeGraph(throwRaw: new InvalidOperationException("estado interno sintético")))
            .TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded, "Teams e Exchange continuam respondendo");
        var entra = health.Message!.Split('\n').Single(l => l.StartsWith("Microsoft Entra ID:"));
        entra.Should().Contain("falha inesperada");
        entra.Should().NotContain("falha de rede",
            "um erro interno do AEGIS não pode ser apresentado como falha de rede ou tempo limite");
        health.Message.Should().Contain("Microsoft Teams: credencial aceita")
            .And.Contain("Exchange Online: credencial aceita");
        health.Message.Should().NotContain("estado interno sintético",
            "a mensagem de uma exceção inesperada não atravessa para o diagnóstico");
    }

    [Fact]
    public async Task ResultadoPositivo_DeixaClaroQueSoALeituraDeVerificacaoFoiConfirmada()
    {
        var health = await ConnectorFor().TestAsync(Config(), CancellationToken.None);

        health.Message.Should().Contain("apenas a conexão e a leitura de verificação")
            .And.Contain("sincronização");
    }

    [Fact]
    public async Task CancelamentoRealSolicitadoPeloUsuario_PropagaEmVezDeVirarFailed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await ConnectorFor(graph: new FakeGraph(throwOnCancellation: true))
            .TestAsync(Config(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelamento pedido pelo usuário nunca pode virar um resultado Failed silencioso");
    }

    [Fact]
    public void ConectorEstaRegistradoParaMicrosoftIdentityPosture()
    {
        var connector = ConnectorFor();
        connector.Provider.Should().Be(ConnectorProvider.Microsoft);
        connector.Capability.Should().Be(ConnectorCapability.IdentityPosture);
    }

    [Fact]
    public async Task CollectAsync_NuncaEmiteSinal()
    {
        var signals = new List<EvidenceSignal>();
        await foreach (var s in ConnectorFor().CollectAsync(Config(), CancellationToken.None)) signals.Add(s);

        signals.Should().BeEmpty(
            "o AEGIS KNIGHT tem caminho de coleta próprio (IKnightCollector); este conector só serve ao teste de conexão");
    }

    // ---- Duplas de teste ------------------------------------------------------------------------------

    /// <summary>Espelha o formato real (Unprotect devolve o JSON em claro); "ilegível" simula segredo adulterado.</summary>
    private sealed class FakeProtector : IConnectorSecretProtector
    {
        private readonly bool _legivel;
        public FakeProtector(bool legivel = true) => _legivel = legivel;

        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedValue) => _legivel
            ? """{"tenantId":"dir-demo-0001","clientId":"client-sintetico","clientSecret":"secret-sintetico"}"""
            : throw new FormatException("segredo sintético adulterado para este teste");
    }

    private sealed class FakeGraph : IEntraGraphClient
    {
        private readonly EntraGraphErrorKind? _fail;
        private readonly Exception? _throwRaw;
        private readonly bool _throwOnCancellation;
        public FakeGraph(EntraGraphErrorKind? fail = null, Exception? throwRaw = null, bool throwOnCancellation = false)
        {
            _fail = fail;
            _throwRaw = throwRaw;
            _throwOnCancellation = throwOnCancellation;
        }

        public Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct)
        {
            if (_throwOnCancellation) ct.ThrowIfCancellationRequested();
            if (_throwRaw is not null) throw _throwRaw;
            if (_fail is { } kind)
                throw new EntraGraphException(kind, "token endpoint recusou", 401, endpointPath: "/oauth2/v2.0/token");
            return Task.FromResult("graph-token-sintetico");
        }

        public async IAsyncEnumerable<JsonElement> GetPagedAsync(
            string token, IMicrosoftGraphCredentials config, string relativeUrl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<JsonElement> GetJsonAsync(string token, IMicrosoftGraphCredentials config, string relativeUrl, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse(
                """{"value":[{"verifiedDomains":[{"isInitial":true,"name":"demo.onmicrosoft.example.com"}]}]}""").RootElement);
    }

    internal sealed class FakeTeamsTokens : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials(credentials.AzureTenantId, "graph-sintetico", "teams-sintetico"));
    }

    internal sealed class FakeTeamsReader : ITeamsAdminReader
    {
        private readonly bool _connected;
        private readonly bool _readOk;
        private readonly string _readErrorCategory;
        public FakeTeamsReader(bool connected, bool readOk, string readErrorCategory = "InsufficientPermission")
        {
            _connected = connected;
            _readOk = readOk;
            _readErrorCategory = readErrorCategory;
        }

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            TestConnectionAsync(credentials, ct);

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);

        public Task<TeamsAdminOutput> TestConnectionAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
        {
            object[] reads = _connected
                ? new object[]
                {
                    new
                    {
                        capability = "TeamsClientConfiguration",
                        command = "Get-CsTeamsClientConfiguration",
                        ok = _readOk,
                        items = Array.Empty<object>(),
                        errorCategory = _readOk ? null : _readErrorCategory,
                        errorId = (string?)null,
                    },
                }
                : Array.Empty<object>();

            var json = JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = "6.9.0", platform = "Unix" },
                connected = _connected,
                connectionErrorId = _connected ? null : "AADSTS700016",
                connectionErrorCategory = _connected ? null : "InsufficientPermission",
                reads,
            });
            return Task.FromResult(PowerShellTeamsAdminReader.Parse(json));
        }
    }

    private sealed class QuebraTeams : ITeamsAdminReader
    {
        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            throw new TeamsAdminTransportException("O runtime do PowerShell não pôde ser iniciado neste ambiente.");

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);

        public Task<TeamsAdminOutput> TestConnectionAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            ReadAsync(credentials, ct);
    }

    internal sealed class FakeExchangeTokens : IExchangeTokenClient
    {
        public Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new ExchangeAdminCredentials("demo.onmicrosoft.example.com", "exo-token-sintetico"));
    }

    internal sealed class FakeExchangeReader : IExchangeAdminReader
    {
        private readonly bool _connected;
        private readonly bool _readOk;
        private readonly string _category;

        public FakeExchangeReader(bool connected, bool readOk, string category = "InsufficientPermission")
        {
            _connected = connected;
            _readOk = readOk;
            _category = category;
        }

        public Task<ExchangeAdminOutput> ReadAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default) =>
            TestConnectionAsync(credentials, ct);

        public Task<ExchangeAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);

        public Task<ExchangeAdminOutput> TestConnectionAsync(ExchangeAdminCredentials credentials, CancellationToken ct = default)
        {
            object[] reads = _connected
                ? new object[]
                {
                    new
                    {
                        capability = "ExchangeOrganizationConfig",
                        command = "Get-OrganizationConfig",
                        ok = _readOk,
                        items = Array.Empty<object>(),
                        truncated = false,
                        errorCategory = _readOk ? null : "InsufficientPermission",
                        errorId = (string?)null,
                    },
                }
                : Array.Empty<object>();

            var json = JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = "3.9.2", platform = "Unix" },
                connected = _connected,
                connectionErrorId = _connected ? null : "InvalidToken/SecurityTokenMalformedException",
                connectionErrorCategory = _connected ? null : _category,
                enumerationLimit = 5000,
                reads,
            });
            return Task.FromResult(PowerShellExchangeAdminReader.Parse(json));
        }
    }
}
