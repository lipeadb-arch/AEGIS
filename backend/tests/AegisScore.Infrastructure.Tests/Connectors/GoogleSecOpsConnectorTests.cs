using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Google;
using AegisScore.Connectors.Google.SecOps;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Transporte da Chronicle API do Google SecOps por HTTP SIMULADO: hosts regionais
/// oficiais derivados da allowlist por localidade, redirect recusado, bearer nunca encaminhado a outro host,
/// classificação de 400/401/403/404/429/timeout/5xx, JSON inválido, teto de tamanho, paginação de casos (ciclo/teto)
/// e parcialidade de alertas (moreDataAvailable/teto interno).
/// </summary>
public sealed class ChronicleApiClientTests
{
    private const string Token = "fake-token";

    // ---- Hosts regionais / allowlist ---------------------------------------------------------------

    [Fact]
    public async Task GetInstance_AllowedLocation_TargetsOfficialRegionalHost_WithBearer()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, """{"name":"projects/p/locations/us/instances/i"}""", null));
        var client = new ChronicleApiClient(new HttpClient(handler));

        var instance = await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);

        instance.Name.Should().Be("projects/p/locations/us/instances/i");
        handler.Hosts.Should().OnlyContain(h => h == "us-chronicle.googleapis.com", "host regional oficial derivado da localidade");
        handler.Paths.Should().ContainSingle().Which.Should().Be("/v1alpha/projects/proj/locations/us/instances/inst-1");
        handler.BearerSeen.Should().OnlyContain(b => b, "o bearer acompanha a requisição ao host oficial");
    }

    [Theory]
    [InlineData("us", "us-chronicle.googleapis.com")]
    [InlineData("europe", "europe-chronicle.googleapis.com")]
    [InlineData("europe-west3", "europe-west3-chronicle.googleapis.com")]
    [InlineData("asia-southeast1", "asia-southeast1-chronicle.googleapis.com")]
    public async Task AllowedLocations_ResolveToDerivedRegionalHost(string location, string expectedHost)
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}", null));
        var client = new ChronicleApiClient(new HttpClient(handler));
        await client.GetInstanceAsync(Token, "proj", location, "inst-1", CancellationToken.None);
        handler.Hosts.Should().OnlyContain(h => h == expectedHost);
    }

    [Theory]
    [InlineData("us-central1")]                    // zona GCP válida, mas NÃO é uma localidade SecOps suportada
    [InlineData("us.evil.example.com")]            // tentativa de forjar host arbitrário via "localidade"
    [InlineData("")]
    [InlineData("nonsense")]
    public async Task UnknownLocation_IsRejected_BeforeAnyHttp(string location)
    {
        var reached = false;
        var handler = new RecordingHandler(_ => { reached = true; return (HttpStatusCode.OK, "{}", null); });
        var client = new ChronicleApiClient(new HttpClient(handler));

        var act = async () => await client.GetInstanceAsync(Token, "proj", location, "inst-1", CancellationToken.None);

        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.Unavailable);
        reached.Should().BeFalse("uma localidade fora da allowlist nunca chega a montar/enviar a requisição — bearer nunca sai");
    }

    [Fact]
    public async Task Redirect_IsRefused_TokenNotForwarded()
    {
        // Um 3xx (com Location para outro host) é RECUSADO: o bearer nunca segue o redirecionamento.
        var handler = new RecordingHandler(_ => (HttpStatusCode.Redirect, "", "https://attacker.evil.example.com/steal"));
        var client = new ChronicleApiClient(new HttpClient(handler));

        var act = async () => await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);

        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.Unavailable);
        handler.Hosts.Should().OnlyContain(h => h == "us-chronicle.googleapis.com", "só houve UMA requisição, ao host oficial — o redirect não foi seguido");
    }

    // ---- Classificação de erro ---------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ChronicleApiErrorKind.AuthFailure)]
    [InlineData(HttpStatusCode.Unauthorized, ChronicleApiErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ChronicleApiErrorKind.InsufficientPermission)]
    [InlineData(HttpStatusCode.NotFound, ChronicleApiErrorKind.InstanceNotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ChronicleApiErrorKind.Throttled)]
    [InlineData(HttpStatusCode.RequestTimeout, ChronicleApiErrorKind.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, ChronicleApiErrorKind.Unavailable)]
    public async Task ErrorStatuses_AreClassified(HttpStatusCode status, ChronicleApiErrorKind kind)
    {
        var handler = new RecordingHandler(_ => (status, """{"error":{"code":1}}""", null));
        var client = new ChronicleApiClient(new HttpClient(handler));
        var act = async () => await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(kind);
    }

    [Fact]
    public async Task HttpTimeout_ClassifiedAsTimeout()
    {
        var client = new ChronicleApiClient(new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout"))));
        var act = async () => await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.Timeout);
    }

    [Fact]
    public async Task InvalidJson_FailsClosed()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "not-json", null));
        var client = new ChronicleApiClient(new HttpClient(handler));
        var act = async () => await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.InvalidResponse);
    }

    [Fact]
    public async Task ResponseTooLarge_FailsClosed()
    {
        var big = "{\"name\":\"" + new string('a', 512) + "\"}";
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, big, null));
        var client = new ChronicleApiClient(new HttpClient(handler), maxPages: 10, maxItems: 100, maxResponseBytes: 64, pageSize: 100);
        var act = async () => await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.InvalidResponse);
    }

    // ---- Casos: paginação -------------------------------------------------------------------------

    [Fact]
    public async Task ListCases_FollowsPageToken_Concatenates()
    {
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("pageToken=TOK2"))
                return (HttpStatusCode.OK, CasesPage(new[] { Case("c-b") }, next: null), null);
            return (HttpStatusCode.OK, CasesPage(new[] { Case("c-a") }, next: "TOK2"), null);
        });
        var client = new ChronicleApiClient(new HttpClient(handler));
        var items = await client.ListCasesAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListCases_EmptyArrayOmitted_IsEmptyLegit()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}", null));
        var client = new ChronicleApiClient(new HttpClient(handler));
        (await client.ListCasesAsync(Token, "proj", "us", "inst-1", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListCases_RepeatedPageToken_FailsClosed()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, CasesPage(new[] { Case("c") }, next: "SAME"), null));
        var client = new ChronicleApiClient(new HttpClient(handler), maxPages: 50, maxItems: 1000, maxResponseBytes: 1_000_000, pageSize: 500);
        var act = async () => await client.ListCasesAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.IncompleteCollection);
    }

    [Fact]
    public async Task ListCases_PageCap_FailsClosed()
    {
        var i = 0;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, CasesPage(new[] { Case($"c-{i++}") }, next: $"T{i}"), null));
        var client = new ChronicleApiClient(new HttpClient(handler), maxPages: 3, maxItems: 1000, maxResponseBytes: 1_000_000, pageSize: 500);
        var act = async () => await client.ListCasesAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.IncompleteCollection);
    }

    [Fact]
    public async Task ListCases_ItemCap_FailsClosed()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, CasesPage(new[] { Case("a"), Case("b"), Case("c") }, next: null), null));
        var client = new ChronicleApiClient(new HttpClient(handler), maxPages: 10, maxItems: 2, maxResponseBytes: 1_000_000, pageSize: 500);
        var act = async () => await client.ListCasesAsync(Token, "proj", "us", "inst-1", CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.IncompleteCollection);
    }

    // ---- Alertas: parcialidade ---------------------------------------------------------------------

    [Fact]
    public async Task SearchAlerts_MoreDataAvailable_IsPartial()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, AlertsBody(new[] { Alert("a1") }, moreData: true), null));
        var client = new ChronicleApiClient(new HttpClient(handler));
        var r = await client.SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        r.IsPartial.Should().BeTrue();
        r.Alerts.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAlerts_ItemCap_MarksPartial_PreservesFloor()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, AlertsBody(new[] { Alert("a1"), Alert("a2"), Alert("a3") }, moreData: false), null));
        var client = new ChronicleApiClient(new HttpClient(handler), maxPages: 10, maxItems: 2, maxResponseBytes: 1_000_000, pageSize: 100);
        var r = await client.SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        r.LimitHit.Should().BeTrue();
        r.IsPartial.Should().BeTrue();
        r.Alerts.Should().HaveCount(2, "os agregados obtidos são preservados como PISO");
    }

    [Fact]
    public async Task SearchAlerts_TimeRangeIsServerComputed_StartInclusiveEndExclusive()
    {
        string? seenUrl = null;
        var handler = new RecordingHandler(req => { seenUrl = req.RequestUri!.AbsoluteUri; return (HttpStatusCode.OK, AlertsBody(Array.Empty<string>(), false), null); });
        var client = new ChronicleApiClient(new HttpClient(handler));
        await client.SearchAlertsAsync(Token, "proj", "us", "inst-1",
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        seenUrl.Should().Contain("legacySearchEnterpriseWideAlerts");
        seenUrl.Should().Contain("startTime=").And.Contain("endTime=");
    }

    // ---- helpers -----------------------------------------------------------------------------------

    internal static string Case(string id, string priority = "PRIORITY_HIGH", string status = "OPEN", string updateTime = "2026-08-20T10:00:00Z") =>
        "{\"name\":\"" + id + "\",\"priority\":\"" + priority + "\",\"status\":\"" + status + "\",\"updateTime\":\"" + updateTime + "\"}";

    private static string CasesPage(IEnumerable<string> cases, string? next)
    {
        var body = "{\"cases\":[" + string.Join(",", cases) + "]";
        if (next is not null) body += ",\"nextPageToken\":\"" + next + "\"";
        return body + "}";
    }

    /// <summary>Corpo de UMA página de cases.list (sem nextPageToken) — reusado pelos testes do conector.</summary>
    internal static string CasesList(params string[] cases) => "{\"cases\":[" + string.Join(",", cases) + "]}";

    internal static string Alert(string id, string? severity = "HIGH", string createTime = "2026-08-21T09:00:00Z")
    {
        var body = "{\"id\":\"" + id + "\"";
        if (severity is not null) body += ",\"severity\":\"" + severity + "\"";
        body += ",\"createTime\":\"" + createTime + "\"}";
        return body;
    }

    internal static string AlertsBody(IEnumerable<string> alerts, bool moreData) =>
        "{\"alerts\":[" + string.Join(",", alerts) + "],\"moreDataAvailable\":" + (moreData ? "true" : "false") + "}";

    /// <summary>Handler que ROTEIA por URL e REGISTRA host/path/bearer de cada requisição (para provar allowlist e não-encaminhamento do bearer).</summary>
    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body, string? Location)> _route;
        public List<string> Hosts { get; } = new();
        public List<string> Paths { get; } = new();
        public List<bool> BearerSeen { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, (HttpStatusCode, string, string?)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            Paths.Add(request.RequestUri!.AbsolutePath);
            BearerSeen.Add(request.Headers.Authorization is { Scheme: "Bearer", Parameter: { Length: > 0 } });

            var (status, body, location) = _route(request);
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (location is not null) resp.Headers.Location = new Uri(location);
            return Task.FromResult(resp);
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
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Conector do Google SecOps por HTTP SIMULADO (autenticação FAKE): capability
/// Google/Siem, TestAsync via instances.get (sem depender de casos/alertas), coleta das DUAS dimensões independentes,
/// degradação honesta (uma dimensão falha, a outra preserva), pull falha quando nenhuma dimensão coleta, AUSÊNCIA de
/// EvidenceSignal, compatibilidade customerId→instanceId e higiene de segredo.
/// </summary>
public sealed class GoogleSecOpsConnectorTests
{
    private const string Sa = "{\\\"type\\\":\\\"service_account\\\"}";
    private static readonly string DefaultSettings =
        "{\"projectId\":\"demo-secops\",\"location\":\"us\",\"instanceId\":\"inst-123\",\"serviceAccountJson\":\"" + Sa + "\"}";

    private static ConnectorConfig Config(
        string? settings = null, ConnectorAuthType authType = ConnectorAuthType.ServiceAccount) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.Parse("aa000000-0000-0000-0000-000000000002"),
        Provider = ConnectorProvider.Google,
        Capability = ConnectorCapability.Siem,
        AuthType = authType,
        Enabled = true,
        EncryptedSettings = settings ?? DefaultSettings,
    };

    private static GoogleSecOpsConnector NewConnector(
        ChronicleApiClientTests.RecordingHandler handler, IGoogleSecOpsAuthenticator? auth = null) =>
        new(auth ?? new FakeAuth("fake-token"), new ChronicleApiClient(new HttpClient(handler)), new PassThroughProtector());

    [Fact]
    public void ProviderCapabilityAuth_AreGoogleSiemServiceAccount()
    {
        var c = NewConnector(new ChronicleApiClientTests.RecordingHandler(_ => (HttpStatusCode.OK, "{}", null)));
        c.Provider.Should().Be(ConnectorProvider.Google);
        c.Capability.Should().Be(ConnectorCapability.Siem);
    }

    [Fact]
    public async Task Test_Degraded_WhenNotConfigured()
    {
        var c = NewConnector(new ChronicleApiClientTests.RecordingHandler(_ => (HttpStatusCode.OK, "{}", null)));
        (await c.TestAsync(Config(settings: ""), CancellationToken.None)).Status.Should().Be(ConnectorStatus.Degraded);
    }

    [Fact]
    public async Task Test_UsesInstancesGet_NotCasesOrAlerts()
    {
        var handler = new ChronicleApiClientTests.RecordingHandler(_ => (HttpStatusCode.OK, """{"name":"projects/p/locations/us/instances/i"}""", null));
        var health = await NewConnector(handler).TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Healthy);
        handler.Paths.Should().ContainSingle("o teste faz UMA leitura — instances.get");
        handler.Paths[0].Should().EndWith("/instances/inst-123");
        handler.Paths[0].Should().NotContain("/cases").And.NotContain("legacySearchEnterpriseWideAlerts");
    }

    [Fact]
    public async Task Test_Failed_WhenForbidden_MentionsReadOnlyAccess()
    {
        var health = await NewConnector(new ChronicleApiClientTests.RecordingHandler(_ => (HttpStatusCode.Forbidden, "{}", null)))
            .TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Failed);
        health.Message.Should().Contain("somente leitura");
    }

    [Fact]
    public async Task Collect_BothDimensions_Available_Complete()
    {
        var handler = Router(
            cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(new[] { ChronicleApiClientTests.Alert("a1"), ChronicleApiClientTests.Alert("a2", severity: "MEDIUM") }, moreData: false), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Source.Should().Be("Google SecOps");
        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        snap.Cases.Period.Should().Be(SiemPeriodKind.CurrentInventory, "a listagem de casos é inventário, não janela temporal");
        snap.Cases.Observed.Should().Be(2);
        snap.Cases.Open.Should().Be(1, "um caso OPEN e um CLOSED");
        snap.Cases.Closed.Should().Be(1);
        snap.Cases.OpenByPriority.Should().NotBeNull();
        snap.Alerts.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.Period.Should().Be(SiemPeriodKind.RollingWindow);
        snap.Alerts.WindowDays.Should().Be(30);
        snap.Alerts.Observed.Should().Be(2);
        snap.Alerts.HighSeverity.Should().Be(1);
        snap.Alerts.MediumSeverity.Should().Be(1);
        snap.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Collect_AlertsMoreDataAvailable_MarksAlertsPartial_SnapshotIncomplete()
    {
        var handler = Router(
            cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(new[] { ChronicleApiClientTests.Alert("a1") }, moreData: true), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.State.Should().Be(SiemCollectionState.Partial);
        snap.Alerts.Observed.Should().Be(1, "o agregado obtido é preservado como piso");
        snap.IsComplete.Should().BeFalse("uma dimensão parcial degrada a fotografia");
    }

    [Fact]
    public async Task Collect_CasesAvailable_AlertsPermissionDenied_Degraded_NoThrow()
    {
        var handler = Router(
            cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.Forbidden, "{}", null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        snap.Cases.Observed.Should().Be(2);
        snap.Alerts.State.Should().Be(SiemCollectionState.PermissionDenied);
        snap.Alerts.Observed.Should().BeNull("permissão negada não vira zero — a contagem fica anulável");
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Collect_AlertsAvailable_CasesPermissionDenied_Degraded_NoThrow()
    {
        var handler = Router(
            cases: (HttpStatusCode.Forbidden, "{}", null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(new[] { ChronicleApiClientTests.Alert("a1") }, moreData: false), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.PermissionDenied);
        snap.Cases.Observed.Should().BeNull();
        snap.Alerts.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.Observed.Should().Be(1);
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Collect_BothDimensionsUnavailable_PullFails()
    {
        var handler = Router(cases: (HttpStatusCode.Forbidden, "{}", null), alerts: (HttpStatusCode.Forbidden, "{}", null));
        var act = async () => await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<ChronicleApiException>("nenhuma dimensão coletada → o pull deve falhar")).Which;
        ex.Kind.Should().Be(ChronicleApiErrorKind.InsufficientPermission, "a natureza do erro (permissão) é preservada, não virou zero");
    }

    [Fact]
    public async Task CollectAsync_YieldsNoSignals_ScoreUntouched()
    {
        var count = 0;
        await foreach (var _ in NewConnector(new ChronicleApiClientTests.RecordingHandler(_ => (HttpStatusCode.OK, "{}", null)))
            .CollectAsync(Config(), CancellationToken.None)) count++;
        count.Should().Be(0, "o Google SecOps não emite sinais de score — os controles seguem NotEvaluated");
    }

    [Fact]
    public async Task Collect_CustomerId_IsAcceptedAsInstanceIdCompat()
    {
        // Config LEGADA com customerId (sem instanceId): o conector o usa como instance ID canônico e coleta normalmente.
        var legacy = "{\"projectId\":\"demo-secops\",\"location\":\"us\",\"customerId\":\"cust-legacy\",\"serviceAccountJson\":\"" + Sa + "\"}";
        var handler = Router(
            cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(Array.Empty<string>(), moreData: false), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(legacy), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        handler.Paths.Should().OnlyContain(p => p.Contains("/instances/cust-legacy"), "customerId vira o instance ID canônico");
    }

    [Fact]
    public async Task Snapshot_CarriesNoServiceAccountSecret()
    {
        var handler = Router(
            cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(new[] { ChronicleApiClientTests.Alert("a1") }, moreData: false), null));
        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);
        var json = JsonSerializer.Serialize(snap);
        json.Should().NotContain("service_account").And.NotContain("fake-token");
    }

    // ---- Roteador de casos + alertas ----
    private static ChronicleApiClientTests.RecordingHandler Router(
        (HttpStatusCode, string, string?) cases, (HttpStatusCode, string, string?) alerts) =>
        new(req => req.RequestUri!.AbsoluteUri.Contains("legacySearchEnterpriseWideAlerts") ? alerts : cases);

    /// <summary>Inventário canned de 2 casos: um ABERTO (prioridade alta) e um FECHADO.</summary>
    private static string CasesTwo() => ChronicleApiClientTests.CasesList(
        ChronicleApiClientTests.Case("c-open", priority: "PRIORITY_HIGH", status: "OPEN"),
        ChronicleApiClientTests.Case("c-closed", priority: "PRIORITY_LOW", status: "CLOSED"));

    private sealed class FakeAuth : IGoogleSecOpsAuthenticator
    {
        private readonly string _token;
        public FakeAuth(string token) => _token = token;
        public Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct) => Task.FromResult(_token);
    }

    private sealed class PassThroughProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}

/// <summary>[AEGIS-MVP-GOOGLE-SECOPS-01] GARANTIAS estruturais do autenticador do SecOps: escopo somente leitura e ausência de domain-wide delegation.</summary>
public sealed class GoogleSecOpsAuthenticatorTests
{
    [Fact]
    public void Scope_IsChronicleReadonly_NotCloudPlatformNorWorkspaceAdmin()
    {
        GoogleSecOpsAuthenticator.ChronicleReadonlyScope.Should().Be("https://www.googleapis.com/auth/chronicle.readonly");
        GoogleSecOpsAuthenticator.ChronicleReadonlyScope.Should().NotContain("cloud-platform").And.NotContain("admin.directory");
    }

    [Fact]
    public void Authenticator_HasNoDelegatedUserParameter()
    {
        // A porta recebe SÓ o JSON da service account (+ CancellationToken): sem e-mail delegado → sem domain-wide delegation.
        var method = typeof(IGoogleSecOpsAuthenticator).GetMethod(nameof(IGoogleSecOpsAuthenticator.AcquireAccessTokenAsync))!;
        method.GetParameters().Select(p => p.ParameterType).Should().Equal(new[] { typeof(string), typeof(CancellationToken) });
        method.GetParameters().Should().NotContain(p => p.Name!.Contains("email", StringComparison.OrdinalIgnoreCase)
            || p.Name!.Contains("user", StringComparison.OrdinalIgnoreCase)
            || p.Name!.Contains("delegat", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>[AEGIS-MVP-GOOGLE-SECOPS-01] DI: o registry resolve Google/Siem para o adaptador REAL do SecOps, sem quebrar Google/VulnerabilityScanner.</summary>
public sealed class GoogleSecOpsDiTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGoogleConnectors();
        services.AddScoped<IConnectorRegistry, ConnectorRegistry>();
        services.AddSingleton<IConnectorSecretProtector, FakeProtector>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Registry_ResolvesGoogleSiem_ToSecOpsConnector()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IConnectorRegistry>()
            .Resolve(ConnectorProvider.Google, ConnectorCapability.Siem);
        resolved.Should().BeOfType<GoogleSecOpsConnector>();
    }

    [Fact]
    public void Registry_StillResolvesGoogleVulnerabilityScanner()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IConnectorRegistry>()
            .Resolve(ConnectorProvider.Google, ConnectorCapability.VulnerabilityScanner);
        resolved.Should().NotBeNull("a regressão do Google Cloud VM Manager continua registrada");
    }

    private sealed class FakeProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
