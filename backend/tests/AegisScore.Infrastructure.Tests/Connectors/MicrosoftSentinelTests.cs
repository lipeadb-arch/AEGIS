using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft.Sentinel;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Transporte do Log Analytics (<see cref="LogAnalyticsClient"/>) por HTTP simulado:
/// forma do token, corpo <c>{query,timespan}</c>, classificação de 401/403/429(+Retry-After)/timeout/5xx e parsing
/// fail-closed de <c>tables/columns/rows</c> (JSON inválido, sem tables, resposta vazia, resultado parcial).
/// </summary>
public sealed class LogAnalyticsClientTests
{
    private const string Workspace = "abcdefab-1234-5678-9abc-abcdefabcdef";
    private const string Token = "fake-token";
    private const string TokenJson = """{"access_token":"fake-token","expires_in":3600,"token_type":"Bearer"}""";

    private static readonly Creds Cfg = new("11111111-2222-3333-4444-555555555555", "app", "SUPER-SECRET");

    // ---- Token -------------------------------------------------------------------------------------

    [Fact]
    public async Task AcquireToken_Valid_ReturnsAccessToken()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.OK, TokenJson))));
        (await client.AcquireTokenAsync(Cfg, CancellationToken.None)).Should().Be(Token);
    }

    [Fact]
    public async Task AcquireToken_UsesOfficialLogAnalyticsScope()
    {
        string? sentBody = null;
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, body) => { sentBody = body; return Json(HttpStatusCode.OK, TokenJson); })));
        await client.AcquireTokenAsync(Cfg, CancellationToken.None);
        // O audience/scope OFICIAL exigido pela API do Log Analytics (client credentials).
        sentBody.Should().Contain("api.loganalytics.azure.com%2F.default");
        sentBody.Should().Contain("grant_type=client_credentials");
    }

    // ---- Query -------------------------------------------------------------------------------------

    [Fact]
    public async Task Query_Valid_ParsesTablesColumnsRows_AndSendsFixedBody()
    {
        string? sentBody = null;
        var body = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"A","type":"long"},{"name":"B","type":"long"}],"rows":[[7,9]]}]}""";
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, b) => { sentBody = b; return Json(HttpStatusCode.OK, body); })));

        var result = await client.QueryAsync(Token, Workspace, "print AegisProbe=1", "P30D", CancellationToken.None);

        // Corpo FIXO no servidor: a KQL e o timespan explícito vão no payload (nunca vêm do usuário).
        sentBody.Should().Contain("\"timespan\":\"P30D\"").And.Contain("AegisProbe");

        result.IsPartial.Should().BeFalse();
        var t = result.Primary!;
        t.Name.Should().Be("PrimaryResult");
        t.Columns.Should().Equal("A", "B");
        t.Rows.Should().HaveCount(1);
        t.Rows[0][t.IndexOf("A")].GetInt64().Should().Be(7);
        t.Rows[0][t.IndexOf("B")].GetInt64().Should().Be(9);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LogAnalyticsErrorKind.AuthFailure)]
    [InlineData(HttpStatusCode.Forbidden, LogAnalyticsErrorKind.InsufficientPermission)]
    [InlineData(HttpStatusCode.TooManyRequests, LogAnalyticsErrorKind.Throttled)]
    [InlineData(HttpStatusCode.RequestTimeout, LogAnalyticsErrorKind.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, LogAnalyticsErrorKind.Unavailable)]
    public async Task Query_ErrorStatuses_Classified(HttpStatusCode status, LogAnalyticsErrorKind kind)
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(status, """{"error":{"code":"X"}}"""))));
        var act = async () => await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        (await act.Should().ThrowAsync<LogAnalyticsException>()).Which.Kind.Should().Be(kind);
    }

    [Fact]
    public async Task Query_429_ReadsRetryAfterSeconds()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) =>
        {
            var resp = Json((HttpStatusCode)429, """{"error":{"code":"Throttled"}}""");
            resp.Headers.Add("Retry-After", "30");
            return resp;
        })));

        var ex = (await ((Func<Task>)(() => client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None)))
            .Should().ThrowAsync<LogAnalyticsException>()).Which;
        ex.Kind.Should().Be(LogAnalyticsErrorKind.Throttled);
        ex.RetryAfterSeconds.Should().Be(30);
    }

    [Fact]
    public async Task Query_HttpTimeout_ClassifiedAsTimeout()
    {
        // HttpClient sinaliza timeout com TaskCanceledException SEM cancelamento do chamador.
        var client = new LogAnalyticsClient(new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout"))));
        var act = async () => await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        (await act.Should().ThrowAsync<LogAnalyticsException>()).Which.Kind.Should().Be(LogAnalyticsErrorKind.Timeout);
    }

    [Fact]
    public async Task Query_InvalidJson_FailsClosed()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.OK, "not-json"))));
        var act = async () => await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        (await act.Should().ThrowAsync<LogAnalyticsException>()).Which.Kind.Should().Be(LogAnalyticsErrorKind.Unavailable);
    }

    [Fact]
    public async Task Query_RootWithoutTables_FailsClosed()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.OK, """{"notTables":1}"""))));
        var act = async () => await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        (await act.Should().ThrowAsync<LogAnalyticsException>()).Which.Kind.Should().Be(LogAnalyticsErrorKind.Unavailable);
    }

    [Fact]
    public async Task Query_EmptyResult_ParsesWithoutFailing()
    {
        // Tabela presente com ZERO linhas — resposta vazia legítima (workspace sem dados), não falha.
        var body = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"A","type":"long"}],"rows":[]}]}""";
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.OK, body))));
        var result = await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        result.Primary!.Rows.Should().BeEmpty();
        result.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task Query_PartialResultSignaled_MarksPartial_ButParsesTables()
    {
        // 200 OK + error.code "PartialError" ⇒ resultado truncado/parcial SINALIZADO pela API: degrada, não falha.
        var body = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"A","type":"long"}],"rows":[[3]]}],"error":{"code":"PartialError","message":"x"}}""";
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.OK, body))));
        var result = await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        result.IsPartial.Should().BeTrue();
        result.Primary!.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task Query_NonPartialErrorObject_FailsClosed()
    {
        var body = """{"error":{"code":"SemanticError","message":"boom"}}""";
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.OK, body))));
        var act = async () => await client.QueryAsync(Token, Workspace, "q", "P30D", CancellationToken.None);
        (await act.Should().ThrowAsync<LogAnalyticsException>()).Which.Kind.Should().Be(LogAnalyticsErrorKind.Unavailable);
    }

    [Fact]
    public async Task Query_WorkspaceIdNotGuid_FailsClosedBeforeHttp()
    {
        var reached = false;
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => { reached = true; return Json(HttpStatusCode.OK, "{}"); })));
        var act = async () => await client.QueryAsync(Token, "not-a-guid", "q", "P30D", CancellationToken.None);
        (await act.Should().ThrowAsync<LogAnalyticsException>()).Which.Kind.Should().Be(LogAnalyticsErrorKind.Unavailable);
        reached.Should().BeFalse("um workspaceId fora do formato GUID nunca chega a montar a requisição");
    }

    // ---- Harness -----------------------------------------------------------------------------------

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Creds(string AzureTenantId, string ClientId, string ClientSecret) : ILogAnalyticsCredentials;

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _route;
        public RouteHandler(Func<HttpRequestMessage, string, HttpResponseMessage> route) => _route = route;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            return _route(req, body);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => throw _ex;
    }
}

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Conector do Sentinel: teste de conexão (probe <c>print AegisProbe=1</c>), coleta da
/// postura operacional (dedup <c>arg_max</c>, agregação determinística, alertas "quando disponível"), ausência de
/// sinais de score e higiene de segredo/token (nunca em erro/DTO). O secret nunca vaza; o score não é tocado.
/// </summary>
public sealed class MicrosoftSentinelConnectorTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "top-secret-value";
    private const string Workspace = "abcdefab-1234-5678-9abc-abcdefabcdef";
    private const string TokenJson = """{"access_token":"fake-token","expires_in":3600,"token_type":"Bearer"}""";

    private const string IncidentBody = """
{"tables":[{"name":"PrimaryResult","columns":[
{"name":"IncidentsObserved","type":"long"},{"name":"OpenIncidents","type":"long"},
{"name":"OpenHigh","type":"long"},{"name":"OpenMedium","type":"long"},{"name":"OpenLow","type":"long"},
{"name":"OpenInformational","type":"long"},{"name":"NewIncidents","type":"long"},{"name":"ClosedIncidents","type":"long"},
{"name":"MeanTimeToCloseMinutes","type":"real"},{"name":"LastEvidenceAt","type":"datetime"}],
"rows":[[10,4,2,1,1,0,6,3,120.0,"2026-08-20T10:00:00Z"]]}]}
""";

    private const string AlertBody = """
{"tables":[{"name":"PrimaryResult","columns":[
{"name":"AlertsObserved","type":"long"},{"name":"AlertsHigh","type":"long"},
{"name":"AlertsMedium","type":"long"},{"name":"LastAlertAt","type":"datetime"}],
"rows":[[25,5,7,"2026-08-21T09:00:00Z"]]}]}
""";

    private const string ProbeBody = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"AegisProbe","type":"long"}],"rows":[[1]]}]}""";

    // ---- TestAsync ---------------------------------------------------------------------------------

    [Fact]
    public async Task TestAsync_ProbeSucceeds_Healthy()
    {
        var conn = NewConnector(new SentinelRouter(ProbeBody));
        var health = await conn.TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Healthy);
    }

    [Fact]
    public async Task TestAsync_MissingWorkspaceId_Degraded_NoHttp()
    {
        var reached = false;
        var conn = NewConnector(new SentinelRouter(ProbeBody, onQuery: () => reached = true));
        var health = await conn.TestAsync(Config(workspaceId: null), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Workspace ID");
        reached.Should().BeFalse("sem workspaceId não há chamada — falha SÓ do Sentinel");
    }

    [Fact]
    public async Task TestAsync_MissingCredentials_Degraded()
    {
        var conn = NewConnector(new SentinelRouter(ProbeBody));
        var health = await conn.TestAsync(new ConnectorConfig { EncryptedSettings = "" }, CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Degraded);
    }

    [Fact]
    public async Task TestAsync_Forbidden_Failed_WithRbacGuidance()
    {
        var conn = NewConnector(new SentinelRouter(queryStatus: HttpStatusCode.Forbidden, queryBody: """{"error":{"code":"Forbidden"}}"""));
        var health = await conn.TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Failed);
        health.Message.Should().Contain("Log Analytics Reader");
        health.Message.Should().NotContain(Secret).And.NotContain("fake-token");
    }

    [Fact]
    public async Task TestAsync_Unauthorized_Failed()
    {
        var conn = NewConnector(new SentinelRouter(queryStatus: HttpStatusCode.Unauthorized, queryBody: """{"error":{"code":"Unauthorized"}}"""));
        var health = await conn.TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Failed);
    }

    // ---- CollectPostureAsync -----------------------------------------------------------------------

    [Fact]
    public async Task CollectPosture_UsesArgMaxDedup_AndAggregatesDeterministically()
    {
        var router = new SentinelRouter(incidentBody: IncidentBody, alertBody: AlertBody);
        var conn = NewConnector(router);

        var snap = await conn.CollectPostureAsync(Config(), CancellationToken.None);

        // Deduplicação do histórico: a consulta de incidentes usa arg_max(TimeGenerated, *) by IncidentNumber.
        router.IncidentQuerySent.Should().Contain("arg_max(TimeGenerated").And.Contain("by IncidentNumber");
        router.IncidentQuerySent.Should().Contain("SecurityIncident");

        // Agregação determinística a partir da fotografia canned.
        snap.WindowDays.Should().Be(30);
        snap.IncidentsObserved.Should().Be(10);
        snap.OpenIncidents.Should().Be(4);
        snap.OpenHighSeverity.Should().Be(2);
        snap.OpenMediumSeverity.Should().Be(1);
        snap.OpenLowSeverity.Should().Be(1);
        snap.OpenInformationalSeverity.Should().Be(0);
        snap.NewIncidents.Should().Be(6);
        snap.ClosedIncidents.Should().Be(3);
        snap.MeanTimeToCloseHours.Should().Be(2.0, "120 minutos ÷ 60");
        snap.AlertsObserved.Should().Be(25);
        snap.AlertsHighSeverity.Should().Be(5);
        snap.AlertsMediumSeverity.Should().Be(7);
        snap.LastEvidenceAt.Should().Be(DateTimeOffset.Parse("2026-08-21T09:00:00Z"), "o mais recente entre incidente e alerta");
        snap.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task CollectPosture_SecurityAlertUnavailable_IncidentsStillValid()
    {
        // SecurityAlert ausente ⇒ a consulta de alertas retorna 400 (SemanticError). É ABSORVIDO: alertas = 0 e a
        // coleta primária de incidentes permanece válida.
        var router = new SentinelRouter(incidentBody: IncidentBody, alertStatus: HttpStatusCode.BadRequest,
            alertBody: """{"error":{"code":"SemanticError"}}""");
        var conn = NewConnector(router);

        var snap = await conn.CollectPostureAsync(Config(), CancellationToken.None);

        snap.IncidentsObserved.Should().Be(10);
        snap.AlertsObserved.Should().Be(0, "alertas são 'quando disponível' — a ausência não derruba a coleta");
        snap.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task CollectPosture_PartialIncidentResult_IsNotComplete()
    {
        var partial = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"IncidentsObserved","type":"long"}],"rows":[[3]]}],"error":{"code":"PartialError"}}""";
        var conn = NewConnector(new SentinelRouter(incidentBody: partial, alertBody: AlertBody));
        var snap = await conn.CollectPostureAsync(Config(), CancellationToken.None);
        snap.IsComplete.Should().BeFalse("resultado parcial sinalizado degrada a saúde do conector");
    }

    [Fact]
    public async Task CollectPosture_MissingWorkspaceId_ThrowsSanitized_NoSecretLeak()
    {
        var conn = NewConnector(new SentinelRouter(IncidentBody));
        var ex = (await ((Func<Task>)(() => conn.CollectPostureAsync(Config(workspaceId: null), CancellationToken.None)))
            .Should().ThrowAsync<LogAnalyticsException>()).Which;
        ex.Message.Should().NotContain(Secret).And.NotContain("fake-token");
    }

    [Fact]
    public async Task CollectPosture_SnapshotCarriesNoSecretOrToken()
    {
        var conn = NewConnector(new SentinelRouter(incidentBody: IncidentBody, alertBody: AlertBody));
        var snap = await conn.CollectPostureAsync(Config(), CancellationToken.None);
        var json = JsonSerializer.Serialize(snap);
        json.Should().NotContain(Secret).And.NotContain("fake-token").And.NotContain(TenantId);
    }

    // ---- IEvidenceConnector: sem sinais de score ---------------------------------------------------

    [Fact]
    public async Task CollectAsync_YieldsNoSignals_ScoreUntouched()
    {
        var conn = NewConnector(new SentinelRouter(IncidentBody));
        var count = 0;
        await foreach (var _ in conn.CollectAsync(Config(), CancellationToken.None)) count++;
        count.Should().Be(0, "o Sentinel não emite sinais de score — os controles seguem NotEvaluated");
    }

    [Fact]
    public void ProviderAndCapability_AreMicrosoftSentinelSiem()
    {
        var conn = NewConnector(new SentinelRouter(ProbeBody));
        conn.Provider.Should().Be(ConnectorProvider.MicrosoftSentinel);
        conn.Capability.Should().Be(ConnectorCapability.Siem);
    }

    // ---- Harness -----------------------------------------------------------------------------------

    private static MicrosoftSentinelConnector NewConnector(HttpMessageHandler handler) =>
        new(new LogAnalyticsClient(new HttpClient(handler)), new IdentityProtector());

    private static ConnectorConfig Config(string? workspaceId = Workspace)
    {
        var ws = workspaceId is null ? "" : $$""","workspaceId":"{{workspaceId}}" """;
        return new ConnectorConfig
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.Parse("aa000000-0000-0000-0000-000000000001"),
            Provider = ConnectorProvider.MicrosoftSentinel,
            Capability = ConnectorCapability.Siem,
            Enabled = true,
            EncryptedSettings = $$"""{"tenantId":"{{TenantId}}","clientId":"app","clientSecret":"{{Secret}}"{{ws}}}""",
        };
    }

    /// <summary>Roteia token (login) e consultas (loganalytics) por host; distingue incidentes/alertas/probe pelo corpo KQL.</summary>
    private sealed class SentinelRouter : HttpMessageHandler
    {
        private readonly string? _incidentBody;
        private readonly string? _alertBody;
        private readonly HttpStatusCode _queryStatus;
        private readonly HttpStatusCode _alertStatus;
        private readonly string? _queryBody;
        private readonly Action? _onQuery;

        public string? IncidentQuerySent { get; private set; }

        public SentinelRouter(
            string? incidentBody = null, string? alertBody = null,
            HttpStatusCode queryStatus = HttpStatusCode.OK, HttpStatusCode alertStatus = HttpStatusCode.OK,
            string? queryBody = null, Action? onQuery = null)
        {
            _incidentBody = incidentBody;
            _alertBody = alertBody;
            _queryStatus = queryStatus;
            _alertStatus = alertStatus;
            _queryBody = queryBody;
            _onQuery = onQuery;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var uri = req.RequestUri!;
            if (uri.AbsoluteUri.Contains("/oauth2/v2.0/token"))
                return Json(HttpStatusCode.OK, TokenJson);

            _onQuery?.Invoke();
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);

            // Erro forçado (401/403/etc.) de consulta genérica.
            if (_queryStatus != HttpStatusCode.OK)
                return Json(_queryStatus, _queryBody ?? """{"error":{"code":"X"}}""");

            if (body.Contains("SecurityIncident"))
            {
                IncidentQuerySent = body;
                return Json(HttpStatusCode.OK, _incidentBody ?? """{"tables":[]}""");
            }
            if (body.Contains("SecurityAlert"))
                return Json(_alertStatus, _alertBody ?? """{"error":{"code":"SemanticError"}}""");

            // Probe (print AegisProbe=1).
            return Json(HttpStatusCode.OK, _incidentBody ?? ProbeBody);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>Protetor identidade (settings em claro nos testes) — settings NÃO são segredo aqui, só cifra em prod.</summary>
    private sealed class IdentityProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
