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
    public async Task AcquireToken_SendsClientCredentialsWithOfficialLogAnalyticsIoScope()
    {
        string? sentBody = null;
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, body) => { sentBody = body; return Json(HttpStatusCode.OK, TokenJson); })));
        await client.AcquireTokenAsync(Cfg, CancellationToken.None);

        // Recurso OFICIAL do token (client credentials) = api.loganalytics.io/.default — DISTINTO do host da consulta.
        sentBody.Should().Contain("scope=https%3A%2F%2Fapi.loganalytics.io%2F.default");
        sentBody.Should().Contain("grant_type=client_credentials");
        sentBody.Should().Contain("client_id=app");
        sentBody.Should().Contain("client_secret=SUPER-SECRET", "o segredo é ENVIADO no formulário (necessário para autenticar)");
        // Não aceitar o domínio da CONSULTA como recurso do token — nem como fallback silencioso.
        sentBody.Should().NotContain("api.loganalytics.azure.com%2F.default");
    }

    [Fact]
    public async Task AcquireToken_400InvalidClient_ClassifiedAsAuthFailure_WithoutLeakingSecretOrDescription()
    {
        // AAD devolve credencial inválida como HTTP 400 com um campo string `error`. NUNCA vira Unavailable, e a
        // exceção nunca carrega error_description, corpo bruto ou o segredo.
        const string body = """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.","error_codes":[7000215]}""";
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.BadRequest, body))));

        var ex = (await ((Func<Task>)(() => client.AcquireTokenAsync(Cfg, CancellationToken.None)))
            .Should().ThrowAsync<LogAnalyticsException>()).Which;
        ex.Kind.Should().Be(LogAnalyticsErrorKind.AuthFailure);
        ex.ApiErrorCode.Should().Be("invalid_client");
        var surface = $"{ex.Message}|{ex.ApiErrorCode}";
        surface.Should().NotContain("SUPER-SECRET").And.NotContain("error_description")
            .And.NotContain("AADSTS7000215").And.NotContain("Invalid client secret");
    }

    [Fact]
    public async Task AcquireToken_400InvalidJson_ClassifiedAsAuthFailure()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.BadRequest, "not-json"))));
        var ex = (await ((Func<Task>)(() => client.AcquireTokenAsync(Cfg, CancellationToken.None)))
            .Should().ThrowAsync<LogAnalyticsException>()).Which;
        ex.Kind.Should().Be(LogAnalyticsErrorKind.AuthFailure, "400 no endpoint OAuth é rejeição de credencial, não indisponibilidade");
        ex.ApiErrorCode.Should().BeNull("corpo não-JSON não produz código, mas não vira Unavailable");
    }

    [Fact]
    public async Task AcquireToken_401_ClassifiedAsAuthFailure()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""))));
        var ex = (await ((Func<Task>)(() => client.AcquireTokenAsync(Cfg, CancellationToken.None)))
            .Should().ThrowAsync<LogAnalyticsException>()).Which;
        ex.Kind.Should().Be(LogAnalyticsErrorKind.AuthFailure);
    }

    [Fact]
    public async Task AcquireToken_429WithRetryAfter_ClassifiedAsThrottled()
    {
        var client = new LogAnalyticsClient(new HttpClient(new RouteHandler((_, _) =>
        {
            var resp = Json((HttpStatusCode)429, """{"error":"temporarily_unavailable"}""");
            resp.Headers.Add("Retry-After", "42");
            return resp;
        })));
        var ex = (await ((Func<Task>)(() => client.AcquireTokenAsync(Cfg, CancellationToken.None)))
            .Should().ThrowAsync<LogAnalyticsException>()).Which;
        ex.Kind.Should().Be(LogAnalyticsErrorKind.Throttled, "throttling é preservado no endpoint de token");
        ex.RetryAfterSeconds.Should().Be(42);
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

        // Fonte PROVIDER-NEUTRAL rotulada; incidentes = dimensão de casos numa JANELA deslizante de 30 dias.
        snap.Source.Should().Be("Microsoft Sentinel");
        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        snap.Cases.Period.Should().Be(SiemPeriodKind.RollingWindow);
        snap.Cases.WindowDays.Should().Be(30);
        snap.Cases.Observed.Should().Be(10);
        snap.Cases.Open.Should().Be(4);
        snap.Cases.OpenHighSeverity.Should().Be(2);
        snap.Cases.OpenMediumSeverity.Should().Be(1);
        snap.Cases.OpenLowSeverity.Should().Be(1);
        snap.Cases.OpenInformationalSeverity.Should().Be(0);
        snap.Cases.New.Should().Be(6);
        snap.Cases.Closed.Should().Be(3);
        snap.Cases.MeanTimeToCloseHours.Should().Be(2.0, "120 minutos ÷ 60");
        snap.Cases.LastEvidenceAt.Should().Be(DateTimeOffset.Parse("2026-08-20T10:00:00Z"));

        // Alertas = dimensão INDEPENDENTE, também janela de 30 dias, com estado explícito.
        snap.Alerts.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.Period.Should().Be(SiemPeriodKind.RollingWindow);
        snap.Alerts.WindowDays.Should().Be(30);
        snap.Alerts.Observed.Should().Be(25);
        snap.Alerts.HighSeverity.Should().Be(5);
        snap.Alerts.MediumSeverity.Should().Be(7);
        snap.Alerts.LastEvidenceAt.Should().Be(DateTimeOffset.Parse("2026-08-21T09:00:00Z"));

        snap.IsComplete.Should().BeTrue("ambas as dimensões completas");
    }

    [Fact]
    public async Task CollectPosture_AlertsAvailableEmpty_StateAvailable_ZeroObserved_Complete()
    {
        // Consulta bem-sucedida com ZERO alertas (summarize devolve uma linha de zeros) → Available, e a coleta
        // permanece COMPLETA (incidentes completos). Distinto de "indisponível".
        const string emptyAlerts = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"AlertsObserved","type":"long"},{"name":"AlertsHigh","type":"long"},{"name":"AlertsMedium","type":"long"},{"name":"LastAlertAt","type":"datetime"}],"rows":[[0,0,0,null]]}]}""";
        var snap = await NewConnector(new SentinelRouter(incidentBody: IncidentBody, alertBody: emptyAlerts))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Alerts.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.Observed.Should().Be(0);
        snap.IsComplete.Should().BeTrue();
    }

    [Theory]
    // Tabela ausente: código específico direto, OU envolto em BadArgumentError.details[] (forma REAL do Log Analytics).
    // A tabela ausente é a dimensão NÃO oferecida pela fonte → estado neutro Unsupported.
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"SemanticError"}}""", SiemCollectionState.Unsupported)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"BadArgumentError","details":[{"code":"SemanticError","message":"failed to resolve table 'SecurityAlert'"}]}}""", SiemCollectionState.Unsupported)]
    // ⚠️ 400 GENÉRICO (sem código reconhecido de tabela ausente) → Unavailable, NÃO Unsupported.
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"BadArgumentError"}}""", SiemCollectionState.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden"}}""", SiemCollectionState.PermissionDenied)]
    [InlineData((HttpStatusCode)429, """{"error":{"code":"Throttled"}}""", SiemCollectionState.Throttled)]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"code":"Boom"}}""", SiemCollectionState.Unavailable)]
    public async Task CollectPosture_AlertsFailure_TypedState_NulledAndIncomplete_IncidentsPreserved(
        HttpStatusCode alertStatus, string alertBody, SiemCollectionState expected)
    {
        var snap = await NewConnector(new SentinelRouter(incidentBody: IncidentBody, alertStatus: alertStatus, alertBody: alertBody))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.Observed.Should().Be(10, "os agregados de incidentes são preservados");
        snap.Cases.IsComplete.Should().BeTrue("a dimensão de casos não é contaminada pela falha de alertas");
        snap.Alerts.State.Should().Be(expected);
        snap.Alerts.Observed.Should().BeNull("estado ≠ Available não finge zero — a contagem fica ANULÁVEL");
        snap.IsComplete.Should().BeFalse("alertas não comprovados → coleta incompleta (conector Degraded)");
    }

    [Fact]
    public async Task CollectPosture_Alerts200InvalidShape_StateUnavailable_Incomplete()
    {
        // 200 OK mas SEM a linha de agregação esperada (summarize sempre devolveria uma) → resposta inválida.
        const string invalid = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"AlertsObserved","type":"long"}],"rows":[]}]}""";
        var snap = await NewConnector(new SentinelRouter(incidentBody: IncidentBody, alertBody: invalid))
            .CollectPostureAsync(Config(), CancellationToken.None);
        snap.Alerts.State.Should().Be(SiemCollectionState.Unavailable);
        snap.Alerts.Observed.Should().BeNull();
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task CollectPosture_AlertsTimeout_StateTimeout_Incomplete()
    {
        var router = new SentinelRouter(incidentBody: IncidentBody, alertThrow: new TaskCanceledException("timeout"));
        var snap = await NewConnector(router).CollectPostureAsync(Config(), CancellationToken.None);
        snap.Alerts.State.Should().Be(SiemCollectionState.Timeout);
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task CollectPosture_AlertsPartial_StatePartial_Incomplete()
    {
        const string partialAlerts = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"AlertsObserved","type":"long"}],"rows":[[3]]}],"error":{"code":"PartialError"}}""";
        var snap = await NewConnector(new SentinelRouter(incidentBody: IncidentBody, alertBody: partialAlerts))
            .CollectPostureAsync(Config(), CancellationToken.None);
        snap.Alerts.State.Should().Be(SiemCollectionState.Partial);
        snap.Alerts.Observed.Should().BeNull("resultado parcial não é contagem confiável");
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task CollectPosture_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var router = new SentinelRouter(incidentBody: IncidentBody, alertBody: AlertBody);
        var act = async () => await NewConnector(router).CollectPostureAsync(Config(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("cancelamento solicitado continua propagando");
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
        private readonly Exception? _alertThrow;

        public string? IncidentQuerySent { get; private set; }

        public SentinelRouter(
            string? incidentBody = null, string? alertBody = null,
            HttpStatusCode queryStatus = HttpStatusCode.OK, HttpStatusCode alertStatus = HttpStatusCode.OK,
            string? queryBody = null, Action? onQuery = null, Exception? alertThrow = null)
        {
            _incidentBody = incidentBody;
            _alertBody = alertBody;
            _queryStatus = queryStatus;
            _alertStatus = alertStatus;
            _queryBody = queryBody;
            _onQuery = onQuery;
            _alertThrow = alertThrow;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();   // cancelamento solicitado propaga
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
            {
                if (_alertThrow is not null) throw _alertThrow;   // ex.: TaskCanceledException = timeout HTTP
                return Json(_alertStatus, _alertBody ?? """{"error":{"code":"SemanticError"}}""");
            }

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
