using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Connectors.Google.SecOps;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Scoring;
using AegisScore.Infrastructure.Tests.Connectors;   // FakeMitreCatalog, RuleFixtures
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppCoverage = AegisScore.Application.Abstractions.DetectionCoverageSnapshot;
using AppTechnique = AegisScore.Application.Abstractions.DetectionTechniqueCoverage;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Reconciliação + leitura + integração do pull da cobertura de detecção (SQLite
/// relacional). Prova: substituição atômica, idempotência por fingerprint, preservação do snapshot em falha,
/// isolamento por tenant, ordenação/rótulos da query e — o mais importante — que sincronizar regras produz ZERO
/// EvidenceSignal e ZERO TenantControlState (não toca o AEGIS Score).
/// </summary>
public sealed class DetectionCoveragePersistenceTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherTenant = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public DetectionCoveragePersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = Tenant, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.Tenants.Add(new Tenant { Id = OtherTenant, Name = "Beta", Slug = "beta", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private static AppTechnique Tech(string id, int rules, int live, int alerting) =>
        new(id, id, id.Contains('.'), null, Array.Empty<string>(), rules, live, alerting);

    private static AppCoverage Snap(
        DetectionCoverageCollectionState state, int active, int withMitre, int withoutMitre, int live, int alerting,
        params AppTechnique[] techniques) =>
        new("Google SecOps", "17.1", state, DateTimeOffset.UtcNow, active, withMitre, withoutMitre, live, alerting, techniques);

    private async Task Reconcile(Guid tenant, Guid connector, AppCoverage snap)
    {
        await using var db = NewContext(tenant);
        await new DetectionCoverageReconciler(db).ReconcileAsync(connector, snap, CancellationToken.None);
    }

    // ---- Reconciler --------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_FirstComplete_StoresSnapshotAndTechniques()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 3, 2, 1, 2, 1,
            Tech("T1059", 2, 2, 1), Tech("T1110", 1, 0, 0)));

        await using var db = NewContext(Tenant);
        var s = await db.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
        s.CollectionState.Should().Be(DetectionCoverageCollectionState.Available);
        s.TotalActiveRules.Should().Be(3);
        s.Techniques.Should().HaveCount(2);
        s.Fingerprint.Should().NotBeNullOrEmpty();
        s.LastCollectionAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_IdenticalFingerprint_DoesNotRewriteChildren()
    {
        var connector = Guid.NewGuid();
        var snap = Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1));
        await Reconcile(Tenant, connector, snap);

        Guid[] idsBefore;
        await using (var db = NewContext(Tenant))
            idsBefore = await db.DetectionCoverageTechniques.Select(t => t.Id).OrderBy(x => x).ToArrayAsync();

        await Reconcile(Tenant, connector, snap);   // idêntico → não reescreve filhos

        await using var assert = NewContext(Tenant);
        var idsAfter = await assert.DetectionCoverageTechniques.Select(t => t.Id).OrderBy(x => x).ToArrayAsync();
        idsAfter.Should().Equal(idsBefore, "fingerprint idêntico não recria os filhos");
        (await assert.DetectionCoverageSnapshots.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_AtomicReplacement_SwapsTechniqueSet()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1)));
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1110", 1, 1, 1)));

        await using var db = NewContext(Tenant);
        var s = await db.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
        s.Techniques.Should().ContainSingle().Which.TechniqueId.Should().Be("T1110", "o conjunto anterior foi substituído");
        (await db.DetectionCoverageTechniques.CountAsync()).Should().Be(1, "sem filhos órfãos");
    }

    [Fact]
    public async Task Reconcile_PartialAfterComplete_PreservesCompleteData()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 2, 2, 0, 2, 2,
            Tech("T1059", 1, 1, 1), Tech("T1110", 1, 1, 1)));
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Partial, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1)));

        await using var db = NewContext(Tenant);
        var s = await db.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
        s.CollectionState.Should().Be(DetectionCoverageCollectionState.Available, "parcial não rebaixa um snapshot completo");
        s.LastAttemptState.Should().Be(DetectionCoverageCollectionState.Partial, "mas a tentativa parcial fica registrada");
        s.Techniques.Should().HaveCount(2, "os dados completos são preservados");
    }

    [Fact]
    public async Task Reconcile_UnavailableAfterComplete_PreservesData_RecordsAttempt()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1)));
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Unavailable, 0, 0, 0, 0, 0));

        await using var db = NewContext(Tenant);
        var s = await db.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
        s.CollectionState.Should().Be(DetectionCoverageCollectionState.Available);
        s.LastAttemptState.Should().Be(DetectionCoverageCollectionState.Unavailable);
        s.Techniques.Should().ContainSingle("a falha total NÃO apaga o último inventário");
    }

    [Fact]
    public async Task Reconcile_FirstUnavailable_NoPrior_CreatesHonestPlaceholder()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Unavailable, 0, 0, 0, 0, 0));

        await using var db = NewContext(Tenant);
        var s = await db.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
        s.CollectionState.Should().Be(DetectionCoverageCollectionState.NeverCollected, "sem inventário: placeholder honesto, não finge coleta");
        s.LastAttemptState.Should().Be(DetectionCoverageCollectionState.Unavailable);
        s.Techniques.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_TenantIsolation_And_UniquePerTenantConnector()
    {
        var connector = Guid.NewGuid();   // MESMO connectorId em dois tenants → um snapshot em cada
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1)));
        await Reconcile(OtherTenant, connector, Snap(DetectionCoverageCollectionState.Available, 5, 5, 0, 5, 5, Tech("T1110", 5, 5, 5)));

        await using (var a = NewContext(Tenant))
            (await a.DetectionCoverageSnapshots.SingleAsync()).TotalActiveRules.Should().Be(1);
        await using (var b = NewContext(OtherTenant))
            (await b.DetectionCoverageSnapshots.SingleAsync()).TotalActiveRules.Should().Be(5);
    }

    // ---- Query -------------------------------------------------------------------------------------

    private DetectionCoverageQuery Query(Guid? tenant) =>
        new(NewContext(tenant), new SystemTenantContext(tenant), new FakeMitreCatalog());

    [Fact]
    public async Task Query_NotConfigured_WhenNoSiemConnector()
    {
        var view = await Query(Tenant).GetAsync();
        view.State.Should().Be(DetectionCoverageViewState.NotConfigured);
        view.AffectsScore.Should().BeFalse();
    }

    [Fact]
    public async Task Query_NeverSynced_WhenConnectorExistsButNoSnapshot()
    {
        await SeedSiemConnector(Tenant);
        var view = await Query(Tenant).GetAsync();
        view.State.Should().Be(DetectionCoverageViewState.NeverSynced);
    }

    [Fact]
    public async Task Query_Available_ResolvesNames_OrdersAttentionFirst()
    {
        var connector = await SeedSiemConnector(Tenant);
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 3, 3, 0, 2, 1,
            Tech("T1566", 1, 1, 1),   // ok (rank 2)
            Tech("T1059", 1, 1, 0),   // live sem alerting (rank 1)
            Tech("T1110", 1, 0, 0))); // sem live (rank 0)

        var view = await Query(Tenant).GetAsync();
        view.State.Should().Be(DetectionCoverageViewState.Available);
        view.Summary.ActiveRules.Should().Be(3);
        view.Summary.TechniquesNeedingAttention.Should().Be(2);
        view.Techniques[0].TechniqueId.Should().Be("T1110", "sem regra em execução vem primeiro");
        view.Techniques[0].Name.Should().Be("Brute Force", "nome resolvido pelo catálogo");
        view.Techniques[1].TechniqueId.Should().Be("T1059", "live mas sem alerting vem em seguida");
        view.Techniques[2].TechniqueId.Should().Be("T1566", "ok vem por último");
        view.Techniques[2].NeedsAttention.Should().BeFalse();
    }

    [Fact]
    public async Task Query_UnavailableWithPreservedData_ShowsInventoryAndFailedState()
    {
        var connector = await SeedSiemConnector(Tenant);
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1)));
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Unavailable, 0, 0, 0, 0, 0));

        var view = await Query(Tenant).GetAsync();
        view.State.Should().Be(DetectionCoverageViewState.Unavailable);
        view.StoredCollectionState.Should().Be("Available");
        view.Techniques.Should().ContainSingle("mostra o último inventário preservado");
    }

    [Fact]
    public async Task Query_NeverPersistsOrExposesRuleNames()
    {
        var connector = await SeedSiemConnector(Tenant);
        await Reconcile(Tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, 1, 0, 1, 1, Tech("T1059", 1, 1, 1)));

        // Nenhuma coluna guarda nome/texto de regra — só ID de técnica + contagens.
        await using (var db = NewContext(Tenant))
            (await db.DetectionCoverageTechniques.SingleAsync()).TechniqueId.Should().Be("T1059");

        var view = await Query(Tenant).GetAsync();
        JsonSerializer.Serialize(view).Should().NotContain("ru/", "o contrato nunca expõe identificador/nome de regra");
    }

    private async Task<Guid> SeedSiemConnector(Guid tenant)
    {
        await using var db = NewContext(tenant);
        var cfg = new ConnectorConfig
        {
            TenantId = tenant, Provider = ConnectorProvider.Google, Capability = ConnectorCapability.Siem,
            DisplayName = "Google SecOps", AuthType = ConnectorAuthType.ServiceAccount, Enabled = true,
            EncryptedSettings = DetectionPullFixtures.Settings,
        };
        db.Connectors.Add(cfg);
        await db.SaveChangesAsync();
        return cfg.Id;
    }

    // ---- Integração do pull + GARANTIAS DE SCORE (seção 11) ----------------------------------------

    private EvidenceIngestionExecutor MakeExecutor(GoogleSecOpsConnector connector) => new(
        _options, new NistSignalMapper(NewContext(null)), new IdentityPayload(),
        new FakeRegistry(connector), NullLogger<EvidenceIngestionExecutor>.Instance, NullLogger<ControlStateWriter>.Instance);

    [Fact]
    public async Task Pull_Coverage_PersistsSnapshot_WithZeroSignalsAndZeroControlState()
    {
        var connectorId = await SeedSiemConnector(Tenant);
        var connector = DetectionPullFixtures.Connector(DetectionPullFixtures.Router(
            rules: RuleFixtures.List(
                RuleFixtures.Rule("ru/a", techniqueMeta: "T1059"),
                RuleFixtures.Rule("ru/b", live: false, techniqueMeta: "T1110"))));
        var exec = MakeExecutor(connector);

        await using (var read = NewContext(Tenant))
        {
            var result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);
            result!.DetectionCoverage.Should().NotBeNull();
            result.DetectionCoverage!.State.Should().Be(DetectionCoverageCollectionState.Available);
        }

        await using var assert = NewContext(Tenant);
        (await assert.Signals.CountAsync()).Should().Be(0, "sincronizar regras NÃO cria EvidenceSignal");
        (await assert.TenantControlStates.CountAsync()).Should().Be(0, "NÃO cria nem altera TenantControlState");
        var snap = await assert.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
        snap.Techniques.Should().HaveCount(2);
        (await assert.Connectors.SingleAsync(c => c.Id == connectorId)).LastStatus.Should().Be(ConnectorStatus.Healthy);
    }

    [Fact]
    public async Task Pull_Coverage_Idempotent_SecondSyncKeepsSingleSnapshot()
    {
        var connectorId = await SeedSiemConnector(Tenant);
        var body = RuleFixtures.List(RuleFixtures.Rule("ru/a", techniqueMeta: "T1059"));

        for (var i = 0; i < 2; i++)
        {
            var exec = MakeExecutor(DetectionPullFixtures.Connector(DetectionPullFixtures.Router(rules: body)));
            await using var read = NewContext(Tenant);
            await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);
        }

        await using var assert = NewContext(Tenant);
        (await assert.DetectionCoverageSnapshots.CountAsync()).Should().Be(1, "upsert idempotente por (tenant, conector)");
    }

    [Fact]
    public async Task Pull_RulesFail_ButCasesAlertsSurvive_And_CoverageUnavailable()
    {
        var connectorId = await SeedSiemConnector(Tenant);
        // Casos/alertas OK (vazios, completos); rules.list 403 → cobertura indisponível, mas o pull NÃO falha.
        var connector = DetectionPullFixtures.Connector(DetectionPullFixtures.Router(
            rulesStatus: HttpStatusCode.Forbidden));
        var exec = MakeExecutor(connector);

        await using (var read = NewContext(Tenant))
        {
            var result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);
            result!.Siem.Should().NotBeNull("casos/alertas continuam disponíveis quando a Rules API falha");
            result.Siem!.Cases.State.Should().Be(SiemCollectionState.Available);
            result.DetectionCoverage!.State.Should().Be(DetectionCoverageCollectionState.Unavailable);
        }

        await using var assert = NewContext(Tenant);
        (await assert.Signals.CountAsync()).Should().Be(0);
        var snap = await assert.DetectionCoverageSnapshots.SingleAsync();
        snap.LastAttemptState.Should().Be(DetectionCoverageCollectionState.Unavailable);
        (await assert.Connectors.SingleAsync(c => c.Id == connectorId)).LastStatus
            .Should().Be(ConnectorStatus.Degraded, "cobertura indisponível degrada — não falha");
    }

    private sealed class FakeRegistry : IConnectorRegistry
    {
        private readonly IEvidenceConnector _connector;
        public FakeRegistry(IEvidenceConnector connector) => _connector = connector;
        public IReadOnlyList<IEvidenceConnector> All => new[] { _connector };
        public IEvidenceConnector? Resolve(ConnectorProvider provider, ConnectorCapability capability) => _connector;
    }

    private sealed class IdentityPayload : IEvidenceRawPayloadProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}

/// <summary>Fixtures compartilhadas do pull da cobertura de detecção (conector REAL do SecOps por HTTP simulado).</summary>
internal static class DetectionPullFixtures
{
    private const string Sa = "{\\\"type\\\":\\\"service_account\\\"}";
    public static readonly string Settings =
        "{\"projectId\":\"demo-secops\",\"location\":\"us\",\"instanceId\":\"inst-123\",\"serviceAccountJson\":\"" + Sa + "\"}";

    public static GoogleSecOpsConnector Connector(Func<HttpRequestMessage, (HttpStatusCode, string, string?)> route) =>
        new(new FakeAuth(), new ChronicleApiClient(new HttpClient(new ChronicleApiClientTests.RecordingHandler(route))),
            new PassThrough(), new FakeMitreCatalog());

    /// <summary>Roteia por URL: rules / alerts / cases / instances. Casos e alertas vazios-OK (postura Available).</summary>
    public static Func<HttpRequestMessage, (HttpStatusCode, string, string?)> Router(
        string? rules = null, HttpStatusCode rulesStatus = HttpStatusCode.OK)
    {
        return req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/rules"))
                return (rulesStatus, rules ?? "{}", null);
            if (url.Contains("legacySearchEnterpriseWideAlerts"))
                return (HttpStatusCode.OK, "{\"moreDataAvailable\":false}", null);
            if (url.Contains("/cases"))
                return (HttpStatusCode.OK, "{}", null);
            return (HttpStatusCode.OK, "{\"name\":\"projects/p/locations/us/instances/i\"}", null);
        };
    }

    private sealed class FakeAuth : IGoogleSecOpsAuthenticator
    {
        public Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct) => Task.FromResult("fake-token");
    }

    private sealed class PassThrough : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
