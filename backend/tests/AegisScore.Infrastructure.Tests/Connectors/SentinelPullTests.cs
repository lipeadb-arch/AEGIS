using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft.Sentinel;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-SENTINEL] Cadeia PULL ponta a ponta do Sentinel pelo executor real (HTTP simulado): a
/// postura operacional retorna no resultado SEM virar EvidenceSignal, SEM tocar o ledger/score; e uma falha do
/// Sentinel carimba SÓ o conector do Sentinel (Failed), sem contaminar o estado de outra capacidade.
/// </summary>
public sealed class SentinelPullTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
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

    private const string AlertBody = """{"tables":[{"name":"PrimaryResult","columns":[{"name":"AlertsObserved","type":"long"},{"name":"AlertsHigh","type":"long"},{"name":"AlertsMedium","type":"long"},{"name":"LastAlertAt","type":"datetime"}],"rows":[[25,5,7,"2026-08-21T09:00:00Z"]]}]}""";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public SentinelPullTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = Tenant, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Pull_Sentinel_ReturnsSnapshot_WithoutSignalsOrScore()
    {
        var connectorId = SeedSentinelConnector();
        var exec = MakeExecutor(new SentinelHandler(incidentBody: IncidentBody, alertBody: AlertBody));

        await using var read = NewContext(Tenant);
        var config = await read.Connectors.SingleAsync(c => c.Id == connectorId);

        var result = await exec.CollectPullAsync(config, default);

        result.Should().NotBeNull();
        result!.Persisted.Should().Be(0, "o Sentinel não emite sinais de score");
        result.Skipped.Should().Be(0);
        result.Status.Should().Be(ConnectorStatus.Healthy);
        result.Sentinel.Should().NotBeNull();
        result.Sentinel!.IncidentsObserved.Should().Be(10);
        result.Sentinel.OpenHighSeverity.Should().Be(2);
        result.Sentinel.AlertsObserved.Should().Be(25);
        result.Sentinel.IsComplete.Should().BeTrue();

        await using var assert = NewContext(Tenant);
        (await assert.Signals.CountAsync()).Should().Be(0, "nenhuma evidência/sinal foi persistido");
        (await assert.TenantControlStates.CountAsync()).Should().Be(0, "o ledger determinístico não foi tocado");
        (await assert.Connectors.SingleAsync(c => c.Id == connectorId)).LastStatus.Should().Be(ConnectorStatus.Healthy);
    }

    [Fact]
    public async Task Pull_SentinelFailure_StampsOnlySentinelFailed_OtherCapabilityUntouched()
    {
        var sentinelId = SeedSentinelConnector();

        // Uma OUTRA capacidade, previamente saudável — a falha do Sentinel não pode alterá-la.
        var otherId = Guid.NewGuid();
        await using (var seed = NewContext(Tenant))
        {
            seed.Connectors.Add(new ConnectorConfig
            {
                Id = otherId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.SecureScore,
                DisplayName = "Secure Score", EncryptedSettings = "x", LastStatus = ConnectorStatus.Healthy,
            });
            await seed.SaveChangesAsync();
        }

        // Token OK, mas a consulta responde 403 → o conector do Sentinel lança e o executor carimba Failed.
        var exec = MakeExecutor(new SentinelHandler(queryStatus: HttpStatusCode.Forbidden));

        await using var read = NewContext(Tenant);
        var config = await read.Connectors.SingleAsync(c => c.Id == sentinelId);

        var act = async () => await exec.CollectPullAsync(config, default);
        await act.Should().ThrowAsync<LogAnalyticsException>("a falha da fonte propaga após carimbar Failed");

        await using var assert = NewContext(Tenant);
        (await assert.Connectors.SingleAsync(c => c.Id == sentinelId)).LastStatus
            .Should().Be(ConnectorStatus.Failed);
        (await assert.Connectors.SingleAsync(c => c.Id == otherId)).LastStatus
            .Should().Be(ConnectorStatus.Healthy, "a falha do Sentinel não contamina outra capacidade");
    }

    // ---- Harness -----------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private EvidenceIngestionExecutor MakeExecutor(HttpMessageHandler handler)
    {
        var connector = new MicrosoftSentinelConnector(
            new LogAnalyticsClient(new HttpClient(handler)), new IdentitySecret());
        return new EvidenceIngestionExecutor(
            _options, new NistSignalMapper(NewContext(null)), new IdentityPayload(),
            new FakeRegistry(connector), NullLogger<EvidenceIngestionExecutor>.Instance,
            NullLogger<ControlStateWriter>.Instance);
    }

    private Guid SeedSentinelConnector()
    {
        var id = Guid.NewGuid();
        using var ctx = NewContext(Tenant);
        ctx.Connectors.Add(new ConnectorConfig
        {
            Id = id, Provider = ConnectorProvider.MicrosoftSentinel, Capability = ConnectorCapability.Siem,
            DisplayName = "Microsoft Sentinel", Enabled = true,
            EncryptedSettings = $$"""{"tenantId":"11111111-2222-3333-4444-555555555555","clientId":"app","clientSecret":"s","workspaceId":"{{Workspace}}"}""",
        });
        ctx.SaveChanges();
        return id;
    }

    private sealed class FakeRegistry : IConnectorRegistry
    {
        private readonly IEvidenceConnector _connector;
        public FakeRegistry(IEvidenceConnector connector) => _connector = connector;
        public IReadOnlyList<IEvidenceConnector> All => new[] { _connector };
        public IEvidenceConnector? Resolve(ConnectorProvider provider, ConnectorCapability capability) =>
            _connector.Provider == provider && _connector.Capability == capability ? _connector : null;
    }

    private sealed class IdentitySecret : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class IdentityPayload : IEvidenceRawPayloadProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class SentinelHandler : HttpMessageHandler
    {
        private readonly string? _incidentBody;
        private readonly string? _alertBody;
        private readonly HttpStatusCode _queryStatus;

        public SentinelHandler(
            string? incidentBody = null, string? alertBody = null, HttpStatusCode queryStatus = HttpStatusCode.OK)
        {
            _incidentBody = incidentBody;
            _alertBody = alertBody;
            _queryStatus = queryStatus;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsoluteUri.Contains("/oauth2/v2.0/token"))
                return Json(HttpStatusCode.OK, TokenJson);

            if (_queryStatus != HttpStatusCode.OK)
                return Json(_queryStatus, """{"error":{"code":"Forbidden"}}""");

            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            if (body.Contains("SecurityIncident"))
                return Json(HttpStatusCode.OK, _incidentBody ?? """{"tables":[]}""");
            if (body.Contains("SecurityAlert"))
                return Json(HttpStatusCode.OK, _alertBody ?? """{"tables":[{"name":"PrimaryResult","columns":[],"rows":[]}]}""");
            return Json(HttpStatusCode.OK, """{"tables":[{"name":"PrimaryResult","columns":[{"name":"AegisProbe","type":"long"}],"rows":[[1]]}]}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
