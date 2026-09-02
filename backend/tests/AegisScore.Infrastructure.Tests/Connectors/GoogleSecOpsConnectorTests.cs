using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Transporte da Chronicle API do Google SecOps por HTTP SIMULADO, aderente ao contrato
/// REST OFICIAL: hosts regionais por localidade (allowlist oficial), redirect recusado, bearer nunca encaminhado,
/// classificação 400(InvalidRequest)/401/403/404/429/timeout/5xx, JSON inválido, teto de tamanho, cases.list (v1,
/// agregação incremental com parcialidade preservada) e legacySearchEnterpriseWideAlerts (params oficiais + envelope
/// agrupado por ativo/usuário).
/// </summary>
public sealed class ChronicleApiClientTests
{
    private const string Token = "fake-token";

    // ---- Hosts regionais / allowlist oficial -------------------------------------------------------

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
    [InlineData("eu", "eu-chronicle.googleapis.com")]
    [InlineData("europe", "europe-chronicle.googleapis.com")]
    [InlineData("europe-west3", "europe-west3-chronicle.googleapis.com")]
    [InlineData("asia-southeast1", "asia-southeast1-chronicle.googleapis.com")]
    [InlineData("africa-south1", "africa-south1-chronicle.googleapis.com")]
    public async Task AllowedLocations_ResolveToDerivedRegionalHost(string location, string expectedHost)
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}", null));
        var client = new ChronicleApiClient(new HttpClient(handler));
        await client.GetInstanceAsync(Token, "proj", location, "inst-1", CancellationToken.None);
        handler.Hosts.Should().OnlyContain(h => h == expectedHost);
    }

    [Fact]
    public void AllowlistMatchesOfficialSet()
    {
        // A allowlist do BACKEND é EXATAMENTE o conjunto oficial atual (mesmo conjunto espelhado no frontend).
        ChronicleRegions.SupportedLocations.Should().BeEquivalentTo(GoogleSecOpsOfficial.Locations);
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
        var handler = new RecordingHandler(_ => (HttpStatusCode.Redirect, "", "https://attacker.evil.example.com/steal"));
        var client = new ChronicleApiClient(new HttpClient(handler));

        var act = async () => await client.GetInstanceAsync(Token, "proj", "us", "inst-1", CancellationToken.None);

        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.Unavailable);
        handler.Hosts.Should().OnlyContain(h => h == "us-chronicle.googleapis.com", "só houve UMA requisição, ao host oficial — o redirect não foi seguido");
    }

    // ---- Classificação de erro ---------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ChronicleApiErrorKind.InvalidRequest)]   // 400 = requisição inválida, NÃO credencial
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

    // ---- cases.list (v1, incremental, parcialidade preservada) -------------------------------------

    [Fact]
    public async Task CollectCases_UsesStableV1Endpoint()
    {
        string? path = null;
        var handler = new RecordingHandler(req => { path = req.RequestUri!.AbsolutePath; return (HttpStatusCode.OK, CasesList(Case()), null); });
        var client = new ChronicleApiClient(new HttpClient(handler));
        await client.CollectCasesAsync(Token, "proj", "us", "inst-1", _ => { }, CancellationToken.None);
        path.Should().StartWith("/v1/").And.EndWith("/cases");
        path.Should().NotContain("/v1beta/");
    }

    [Fact]
    public async Task CollectCases_FollowsPageToken_Concatenates()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("pageToken=TOK2")
                ? (HttpStatusCode.OK, CasesPage(new[] { Case() }, next: null), null)
                : (HttpStatusCode.OK, CasesPage(new[] { Case() }, next: "TOK2"), null));
        var (count, partial) = await CollectCases(new ChronicleApiClient(new HttpClient(handler)));
        count.Should().Be(2);
        partial.Should().BeFalse();
    }

    [Fact]
    public async Task CollectCases_EmptyArrayOmitted_IsEmptyLegit_NotPartial()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, "{}", null));
        var (count, partial) = await CollectCases(new ChronicleApiClient(new HttpClient(handler)));
        count.Should().Be(0);
        partial.Should().BeFalse();
    }

    [Fact]
    public async Task CollectCases_RepeatedPageToken_PreservesFloor_Partial()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, CasesPage(new[] { Case() }, next: "SAME"), null));
        var (count, partial) = await CollectCases(new ChronicleApiClient(new HttpClient(handler), 50, 1000, 1_000_000, 500));
        partial.Should().BeTrue("ciclo de pageToken → PARCIAL, sem lançar");
        count.Should().BeGreaterThan(0, "o piso já coletado é preservado");
    }

    [Fact]
    public async Task CollectCases_PageCap_PreservesFloor_Partial()
    {
        var i = 0;
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, CasesPage(new[] { Case() }, next: $"T{i++}"), null));
        var (count, partial) = await CollectCases(new ChronicleApiClient(new HttpClient(handler), 3, 1000, 1_000_000, 500));
        partial.Should().BeTrue("teto de páginas → PARCIAL");
        count.Should().Be(3, "as 3 páginas válidas são preservadas como piso");
    }

    [Fact]
    public async Task CollectCases_ItemCap_PreservesFloor_Partial()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.OK, CasesPage(new[] { Case(), Case(), Case() }, next: null), null));
        var (count, partial) = await CollectCases(new ChronicleApiClient(new HttpClient(handler), 10, 2, 1_000_000, 500));
        partial.Should().BeTrue("teto de itens → PARCIAL");
        count.Should().Be(2, "o piso (2 casos) é preservado");
    }

    [Fact]
    public async Task CollectCases_FirstPageFailure_Throws()
    {
        var handler = new RecordingHandler(_ => (HttpStatusCode.Forbidden, "{}", null));
        var act = async () => await new ChronicleApiClient(new HttpClient(handler))
            .CollectCasesAsync(Token, "proj", "us", "inst-1", _ => { }, CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.InsufficientPermission);
    }

    [Fact]
    public async Task CollectCases_FailureAfterValidPage_PreservesFloor_Partial()
    {
        var handler = new RecordingHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("pageToken=TOK2")
                ? (HttpStatusCode.InternalServerError, "{}", null)
                : (HttpStatusCode.OK, CasesPage(new[] { Case() }, next: "TOK2"), null));
        var (count, partial) = await CollectCases(new ChronicleApiClient(new HttpClient(handler)));
        partial.Should().BeTrue("falha APÓS uma página válida → PARCIAL, sem descartar o piso");
        count.Should().Be(1);
    }

    // ---- legacySearchEnterpriseWideAlerts (params + envelope oficiais) -----------------------------

    [Fact]
    public async Task SearchAlerts_UsesOfficialParams_TimestampRangeAndMaxNum()
    {
        string? url = null;
        var handler = new RecordingHandler(req => { url = req.RequestUri!.AbsoluteUri; return (HttpStatusCode.OK, AlertsBody(assetInfos: Array.Empty<string>(), moreData: false), null); });
        var client = new ChronicleApiClient(new HttpClient(handler));
        await client.SearchAlertsAsync(Token, "proj", "us", "inst-1",
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        url.Should().Contain("legacySearchEnterpriseWideAlerts");
        url.Should().Contain("timestampRange.startTime=").And.Contain("timestampRange.endTime=").And.Contain("maxNumAlertsReturn=");
        url.Should().NotContain("pageSize=", "essa operação não é paginada por pageSize");
        url.Should().NotContain("?startTime=").And.NotContain("&endTime=", "os parâmetros SOLTOS antigos foram removidos");
    }

    [Fact]
    public async Task SearchAlerts_AssetGrouping_Flattened()
    {
        var body = AlertsBody(assetInfos: new[] { AlertInfo("1"), AlertInfo("2") }, moreData: false);
        var r = await new ChronicleApiClient(new HttpClient(new RecordingHandler(_ => (HttpStatusCode.OK, body, null))))
            .SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        r.AlertInfos.Should().HaveCount(2);
        r.IsPartial.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAlerts_UserGrouping_Flattened()
    {
        var body = AlertsBody(userInfos: new[] { AlertInfo("1"), AlertInfo("2") }, moreData: false);
        var r = await new ChronicleApiClient(new HttpClient(new RecordingHandler(_ => (HttpStatusCode.OK, body, null))))
            .SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        r.AlertInfos.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAlerts_MoreDataAvailable_IsPartial()
    {
        var body = AlertsBody(assetInfos: new[] { AlertInfo("1") }, moreData: true);
        var r = await new ChronicleApiClient(new HttpClient(new RecordingHandler(_ => (HttpStatusCode.OK, body, null))))
            .SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        r.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task SearchAlerts_ItemCap_MarksPartial_PreservesFloor()
    {
        var body = AlertsBody(assetInfos: new[] { AlertInfo("1"), AlertInfo("2"), AlertInfo("3") }, moreData: false);
        var client = new ChronicleApiClient(new HttpClient(new RecordingHandler(_ => (HttpStatusCode.OK, body, null))), 10, 100, 1_000_000, 100, maxAlerts: 2);
        var r = await client.SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        r.LimitHit.Should().BeTrue();
        r.IsPartial.Should().BeTrue();
        r.AlertInfos.Should().HaveCount(2, "o piso é preservado");
    }

    [Theory]
    [InlineData("""{"alertSummaries":123}""")]                         // summaries não-array
    [InlineData("""{"alertSummaries":[{"alertInfo":123}]}""")]         // infos não-array
    [InlineData("""{"moreDataAvailable":"yes"}""")]                    // bool com tipo inválido
    public async Task SearchAlerts_MalformedEnvelope_FailsClosed(string body)
    {
        var act = async () => await new ChronicleApiClient(new HttpClient(new RecordingHandler(_ => (HttpStatusCode.OK, body, null))))
            .SearchAlertsAsync(Token, "proj", "us", "inst-1", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);
        (await act.Should().ThrowAsync<ChronicleApiException>()).Which.Kind.Should().Be(ChronicleApiErrorKind.InvalidResponse);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static async Task<(int Count, bool Partial)> CollectCases(ChronicleApiClient client)
    {
        var count = 0;
        var partial = await client.CollectCasesAsync(Token, "proj", "us", "inst-1", _ => count++, CancellationToken.None);
        return (count, partial);
    }

    /// <summary>Caso OFICIAL: <c>status</c> + <c>priority</c> + <c>updateTime</c> (epoch-millis como string int64).</summary>
    internal static string Case(string status = "OPENED", string? priority = "PRIORITY_HIGH", string? updateMillis = "1755684000000")
    {
        var parts = new List<string> { "\"status\":\"" + status + "\"" };
        if (priority is not null) parts.Add("\"priority\":\"" + priority + "\"");
        if (updateMillis is not null) parts.Add("\"updateTime\":\"" + updateMillis + "\"");
        return "{" + string.Join(",", parts) + "}";
    }

    private static string CasesPage(IEnumerable<string> cases, string? next)
    {
        var body = "{\"cases\":[" + string.Join(",", cases) + "]";
        if (next is not null) body += ",\"nextPageToken\":\"" + next + "\"";
        return body + "}";
    }

    internal static string CasesList(params string[] cases) => "{\"cases\":[" + string.Join(",", cases) + "]}";

    /// <summary>Item de alerta OFICIAL (AssetAlertInfo/UdmEventInfo): só campos permitidos (alertNumber/uid/eventLogToken/severity/alertTime).</summary>
    internal static string AlertInfo(
        string? alertNumber = "1001", string? severity = "HIGH", string? alertTime = "2026-08-21T09:00:00Z",
        string? uid = null, string? eventLogToken = null)
    {
        var parts = new List<string>();
        if (alertNumber is not null) parts.Add("\"alertNumber\":\"" + alertNumber + "\"");
        if (uid is not null) parts.Add("\"uid\":\"" + uid + "\"");
        if (eventLogToken is not null) parts.Add("\"eventLogToken\":\"" + eventLogToken + "\"");
        if (severity is not null) parts.Add("\"severity\":\"" + severity + "\"");
        if (alertTime is not null) parts.Add("\"alertTime\":\"" + alertTime + "\"");
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>Envelope OFICIAL: agrupamento por ativo (alertSummaries[].alertInfo[]) e/ou por usuário (userAlertSummaries[].alertInfos[]) + moreDataAvailable.</summary>
    internal static string AlertsBody(string[]? assetInfos = null, string[]? userInfos = null, bool moreData = false)
    {
        var parts = new List<string>();
        if (assetInfos is not null)
            parts.Add("\"alertSummaries\":[{\"alertInfo\":[" + string.Join(",", assetInfos) + "]}]");
        if (userInfos is not null)
            parts.Add("\"userAlertSummaries\":[{\"alertInfos\":[" + string.Join(",", userInfos) + "]}]");
        parts.Add("\"moreDataAvailable\":" + (moreData ? "true" : "false"));
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>Handler que ROTEIA por URL e REGISTRA host/path/bearer de cada requisição.</summary>
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

/// <summary>Conjunto OFICIAL de localidades do Google SecOps — a fonte da verdade dos testes de equivalência (backend e frontend usam exatamente isto).</summary>
internal static class GoogleSecOpsOfficial
{
    public static readonly string[] Locations =
    {
        "us", "eu", "europe",
        "africa-south1",
        "asia-east1", "asia-northeast1", "asia-northeast3", "asia-south1", "asia-southeast1", "asia-southeast2",
        "australia-southeast1",
        "europe-central2", "europe-west2", "europe-west3", "europe-west6", "europe-west9", "europe-west12",
        "me-central1", "me-central2", "me-west1",
        "northamerica-northeast2", "southamerica-east1",
    };
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Conector do Google SecOps por HTTP SIMULADO (autenticação FAKE): capability Google/Siem,
/// TestAsync via instances.get, coleta das DUAS dimensões independentes com fixtures OFICIAIS (status/priority/
/// updateTime epoch-millis; alertas agrupados por ativo/usuário com dedup por alertNumber), degradação honesta,
/// parcialidade preservada, ausência de EvidenceSignal, compatibilidade customerId→instanceId e higiene de segredo.
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
        new(auth ?? new FakeAuth("fake-token"), new ChronicleApiClient(new HttpClient(handler)),
            new PassThroughProtector(), new FakeMitreCatalog());

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
        handler.Paths[0].Should().StartWith("/v1alpha/").And.EndWith("/instances/inst-123");
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
    public async Task Test_Failed_WhenBadRequest_DoesNotBlameCredentials()
    {
        // 400 = requisição rejeitada (contrato), NÃO credencial: a mensagem não pode instruir troca de credencial.
        var health = await NewConnector(new ChronicleApiClientTests.RecordingHandler(_ => (HttpStatusCode.BadRequest, "{}", null)))
            .TestAsync(Config(), CancellationToken.None);
        health.Status.Should().Be(ConnectorStatus.Failed);
        health.Message.Should().Contain("Não é falha de credencial");
        health.Message.Should().NotContain("service account");
    }

    [Fact]
    public async Task Collect_BothDimensions_Available_Complete()
    {
        var handler = Router(cases: (HttpStatusCode.OK, CasesTwo(), null), alerts: (HttpStatusCode.OK,
            ChronicleApiClientTests.AlertsBody(assetInfos: new[] { ChronicleApiClientTests.AlertInfo("1", severity: "HIGH"), ChronicleApiClientTests.AlertInfo("2", severity: "MEDIUM") }), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Source.Should().Be("Google SecOps");
        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        snap.Cases.Period.Should().Be(SiemPeriodKind.CurrentInventory, "a listagem de casos é inventário, não janela temporal");
        snap.Cases.Observed.Should().Be(2);
        snap.Cases.Open.Should().Be(1, "um caso OPENED e um CLOSED");
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
    public async Task Collect_CaseStatuses_CountedByOfficialStatus()
    {
        // OPENED e CLOSED contam; MERGED/CREATION_PENDING/desconhecido/ausente contam só no total observado.
        var cases = ChronicleApiClientTests.CasesList(
            ChronicleApiClientTests.Case(status: "OPENED"),
            ChronicleApiClientTests.Case(status: "CLOSED"),
            ChronicleApiClientTests.Case(status: "MERGED"),
            ChronicleApiClientTests.Case(status: "CREATION_PENDING"),
            ChronicleApiClientTests.Case(status: "WHATEVER"),
            ChronicleApiClientTests.Case(status: "CASE_DATA_STATE_UNSPECIFIED", priority: null, updateMillis: null));
        var snap = await NewConnector(Router(cases: (HttpStatusCode.OK, cases, null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: Array.Empty<string>()), null)))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.Observed.Should().Be(6, "todos contam no total observado");
        snap.Cases.Open.Should().Be(1, "só OPENED conta como aberto — desconhecido/ausente NÃO é presumido aberto");
        snap.Cases.Closed.Should().Be(1, "só CLOSED conta como fechado");
    }

    [Fact]
    public async Task Collect_Priority_OnlyForOpenCases()
    {
        var cases = ChronicleApiClientTests.CasesList(
            ChronicleApiClientTests.Case(status: "OPENED", priority: "PRIORITY_CRITICAL"),
            ChronicleApiClientTests.Case(status: "CLOSED", priority: "PRIORITY_LOW"));
        var snap = await NewConnector(Router(cases: (HttpStatusCode.OK, cases, null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: Array.Empty<string>()), null)))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.OpenByPriority.Should().ContainSingle();
        snap.Cases.OpenByPriority![0].Priority.Should().Be("PRIORITY_CRITICAL");
        snap.Cases.OpenByPriority[0].Count.Should().Be(1);
    }

    [Fact]
    public async Task Collect_UpdateTime_EpochMillis_ParsedAndInvalidIgnored()
    {
        var cases = ChronicleApiClientTests.CasesList(
            ChronicleApiClientTests.Case(status: "OPENED", updateMillis: "1755684000000"),
            ChronicleApiClientTests.Case(status: "OPENED", updateMillis: "not-a-number"));
        var snap = await NewConnector(Router(cases: (HttpStatusCode.OK, cases, null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: Array.Empty<string>()), null)))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.Observed.Should().Be(2, "o timestamp inválido não invalida as demais contagens");
        snap.Cases.LastEvidenceAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1755684000000L));
    }

    [Fact]
    public async Task Collect_CasesCycle_MarksCasesPartial_FloorPreserved()
    {
        // Um ciclo de pageToken degrada a dimensão de casos SEM descartar o piso já coletado.
        var handler = Router(
            cases: (HttpStatusCode.OK, CasesPageWithNext(ChronicleApiClientTests.Case(status: "OPENED"), "SAME"), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: Array.Empty<string>()), null));
        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.Partial);
        snap.Cases.IsComplete.Should().BeFalse();
        snap.Cases.Observed.Should().BeGreaterThan(0, "o piso é preservado");
    }

    [Fact]
    public async Task Collect_Alerts_DedupsSameAlertNumberAcrossAssetAndUser()
    {
        // O MESMO alertNumber aparece no agrupamento por ativo E por usuário → conta UMA vez.
        var body = ChronicleApiClientTests.AlertsBody(
            assetInfos: new[] { ChronicleApiClientTests.AlertInfo("777", severity: "HIGH") },
            userInfos: new[] { ChronicleApiClientTests.AlertInfo("777", severity: "HIGH") });
        var snap = await NewConnector(Router(cases: (HttpStatusCode.OK, ChronicleApiClientTests.CasesList(), null), alerts: (HttpStatusCode.OK, body, null)))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Alerts.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.Observed.Should().Be(1, "alertNumber unifica ativo+usuário");
        snap.Alerts.HighSeverity.Should().Be(1);
    }

    [Fact]
    public async Task Collect_Alerts_FallbackByUidOrEventLogToken()
    {
        var body = ChronicleApiClientTests.AlertsBody(assetInfos: new[]
        {
            ChronicleApiClientTests.AlertInfo(alertNumber: null, uid: "uid-a", severity: "HIGH"),
            ChronicleApiClientTests.AlertInfo(alertNumber: null, eventLogToken: "tok-b", severity: "MEDIUM"),
        });
        var snap = await NewConnector(Router(cases: (HttpStatusCode.OK, ChronicleApiClientTests.CasesList(), null), alerts: (HttpStatusCode.OK, body, null)))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Alerts.State.Should().Be(SiemCollectionState.Available, "uid/eventLogToken são identidades estáveis de fallback");
        snap.Alerts.Observed.Should().Be(2);
    }

    [Fact]
    public async Task Collect_Alerts_UnidentifiedItem_MarksPartial_FloorPreserved()
    {
        var body = ChronicleApiClientTests.AlertsBody(assetInfos: new[]
        {
            ChronicleApiClientTests.AlertInfo("5", severity: "HIGH"),
            ChronicleApiClientTests.AlertInfo(alertNumber: null, severity: "LOW"),   // sem identidade confiável
        });
        var snap = await NewConnector(Router(cases: (HttpStatusCode.OK, ChronicleApiClientTests.CasesList(), null), alerts: (HttpStatusCode.OK, body, null)))
            .CollectPostureAsync(Config(), CancellationToken.None);

        snap.Alerts.State.Should().Be(SiemCollectionState.Partial, "item sem identidade confiável torna a coleta parcial");
        snap.Alerts.Observed.Should().Be(1, "só os identificáveis contam (piso)");
    }

    [Fact]
    public async Task Collect_AlertsMoreDataAvailable_MarksAlertsPartial_SnapshotIncomplete()
    {
        var handler = Router(cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: new[] { ChronicleApiClientTests.AlertInfo("1") }, moreData: true), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        snap.Alerts.State.Should().Be(SiemCollectionState.Partial);
        snap.Alerts.Observed.Should().Be(1, "o agregado obtido é preservado como piso");
        snap.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Collect_CasesAvailable_AlertsPermissionDenied_Degraded_NoThrow()
    {
        var handler = Router(cases: (HttpStatusCode.OK, CasesTwo(), null), alerts: (HttpStatusCode.Forbidden, "{}", null));
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
        var handler = Router(cases: (HttpStatusCode.Forbidden, "{}", null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: new[] { ChronicleApiClientTests.AlertInfo("1") }), null));
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
        var legacy = "{\"projectId\":\"demo-secops\",\"location\":\"us\",\"customerId\":\"cust-legacy\",\"serviceAccountJson\":\"" + Sa + "\"}";
        var handler = Router(cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: Array.Empty<string>()), null));

        var snap = await NewConnector(handler).CollectPostureAsync(Config(legacy), CancellationToken.None);

        snap.Cases.State.Should().Be(SiemCollectionState.Available);
        handler.Paths.Should().OnlyContain(p => p.Contains("/instances/cust-legacy"), "customerId vira o instance ID canônico");
    }

    [Fact]
    public async Task Snapshot_CarriesNoServiceAccountSecret()
    {
        var handler = Router(cases: (HttpStatusCode.OK, CasesTwo(), null),
            alerts: (HttpStatusCode.OK, ChronicleApiClientTests.AlertsBody(assetInfos: new[] { ChronicleApiClientTests.AlertInfo("1") }), null));
        var snap = await NewConnector(handler).CollectPostureAsync(Config(), CancellationToken.None);
        var json = JsonSerializer.Serialize(snap);
        json.Should().NotContain("service_account").And.NotContain("fake-token");
    }

    // ---- Roteador de casos + alertas ----
    private static ChronicleApiClientTests.RecordingHandler Router(
        (HttpStatusCode, string, string?) cases, (HttpStatusCode, string, string?) alerts) =>
        new(req => req.RequestUri!.AbsoluteUri.Contains("legacySearchEnterpriseWideAlerts") ? alerts : cases);

    private static string CasesTwo() => ChronicleApiClientTests.CasesList(
        ChronicleApiClientTests.Case(status: "OPENED", priority: "PRIORITY_HIGH"),
        ChronicleApiClientTests.Case(status: "CLOSED", priority: "PRIORITY_LOW"));

    private static string CasesPageWithNext(string oneCase, string next) =>
        "{\"cases\":[" + oneCase + "],\"nextPageToken\":\"" + next + "\"}";

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

/// <summary>[AEGIS-MVP-GOOGLE-SECOPS-01] GARANTIAS do autenticador do SecOps: escopo `chronicle` (não readonly, não cloud-platform) e ausência de domain-wide delegation.</summary>
public sealed class GoogleSecOpsAuthenticatorTests
{
    [Fact]
    public void Scope_IsChronicle_NotReadonly_NotCloudPlatform_NotWorkspaceAdmin()
    {
        GoogleSecOpsAuthenticator.ChronicleScope.Should().Be("https://www.googleapis.com/auth/chronicle");
        GoogleSecOpsAuthenticator.ChronicleScope.Should().NotContain("readonly", "cases.list não aceita chronicle.readonly");
        GoogleSecOpsAuthenticator.ChronicleScope.Should().NotContain("cloud-platform").And.NotContain("admin.directory");
    }

    [Fact]
    public void Authenticator_HasNoDelegatedUserParameter()
    {
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
        // [AEGIS-MVP-GOOGLE-SECOPS-02] O conector do SecOps agora depende do catálogo MITRE (validação de técnicas).
        services.AddSingleton<AegisScore.Application.Services.IMitreAttackCatalog, FakeMitreCatalog>();
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
