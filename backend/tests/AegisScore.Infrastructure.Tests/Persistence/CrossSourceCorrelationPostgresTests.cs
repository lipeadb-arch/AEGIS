using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Tests.Connectors;   // SyntheticDeviceSources
using AegisScore.Infrastructure.Tests.Documents;    // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-CROSS-SOURCE-01] O que SÓ o PostgreSQL real prova sobre as situações entre fontes (gate <c>AEGIS_TEST_PG</c>):
/// a migration aditiva sobre conectores LEGADOS sem inventar desfecho; a tradução real das consultas relacionais
/// (agregação por marca, IN de marcas, UNION de CVEs, paginação e isolamento por tenant) no Npgsql; e a LEITURA
/// COERENTE durante uma ingestão — pausada no meio da publicação (os fatos são qualificados, nunca misturados) e
/// concluída no meio da leitura (a leitura vê UMA fotografia do banco, em REPEATABLE READ). A ingestão passa pelos
/// conectores reais sobre HTTP sintético e pelo executor real; as barreiras liberam em <c>Dispose</c>.
/// </summary>
public sealed class CrossSourceCorrelationPostgresTests
{
    private const string PreviousMigration = "20260911141553_DeviceResolution02_LifecycleAndConflictProvenance";
    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DevX = SyntheticDeviceSources.DevX;
    private const string RuleA = CrossSourceRuleCodes.VulnerableAndNoncompliant;

    [Fact]
    public async Task Migration_LeavesLegacyOutcomeUnrecorded_ThenRealIngestionAndReadTranslateOnNpgsql_TenantIsolated()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var opt = pg.DbOptions();
        var now = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var defender = Guid.NewGuid();
        var intune = Guid.NewGuid();
        var legacyWatermark = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

        // ---- 1) Banco "de antes": conectores com marca de fotografia publicada, sem desfecho registrado ----------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);
            foreach (var (id, slug) in new[] { (tenant, "xs-a"), (other, "xs-b") })
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Tenants" ("Id","Name","Slug","Status","CreatedAt")
                    VALUES ({0}, 'Cliente Sintético', {1}, 1, now());
                    """, id, slug + "-" + id.ToString("N"));
            var settings = "{\"tenantId\":\"" + DirA + "\",\"clientId\":\"app-sintetico\",\"clientSecret\":\"segredo-sintetico\"}";
            foreach (var (id, capability) in new[]
                     { (defender, ConnectorCapability.VulnerabilityScanner), (intune, ConnectorCapability.ConfigAnalyzer) })
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Connectors" ("Id","TenantId","Provider","Capability","DisplayName","AuthType",
                      "EncryptedSettings","Enabled","SyncIntervalMinutes","LastStatus","CreatedAt","DeviceSnapshotWatermark")
                    VALUES ({0}, {1}, {2}, {3}, 'Conector sintético legado', {4}, {5}, true, 360, 1, now(), {6});
                    """, id, tenant, (int)ConnectorProvider.Microsoft, (int)capability,
                    (int)ConnectorAuthType.OAuthClientCredentials, settings, legacyWatermark);
        }

        // ---- 2) A migration aplica e NÃO inventa desfecho para a fotografia já publicada ---------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().Contain(m => m.EndsWith("_CrossSource01_DeviceSnapshotOutcome"));
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var legacy = await db.Connectors.AsNoTracking().SingleAsync(c => c.Id == defender);
            legacy.DeviceSnapshotOutcome.Should().Be(DeviceSnapshotOutcome.NotRecorded);
            legacy.DeviceSnapshotWatermark.Should().Be(legacyWatermark, "a marca existente não é reescrita");
        }

        // ---- 3) Ingestão real nos dois tenants, com os MESMOS identificadores ---------------------------------------------
        Guid defenderB, intuneB;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            var d = SyntheticDeviceSources.Connector(other, ConnectorCapability.VulnerabilityScanner, DirA);
            var i = SyntheticDeviceSources.Connector(other, ConnectorCapability.ConfigAnalyzer, DirA);
            db.Connectors.AddRange(d, i);
            await db.SaveChangesAsync();
            (defenderB, intuneB) = (d.Id, i.Id);
        }
        var src = Acquisition(now, "CVE-2024-1001", "CVE-2024-1002", "CVE-2024-1003");
        await src.SyncAsync(opt, tenant, defender);
        await src.SyncAsync(opt, tenant, intune);
        var srcB = Acquisition(now, "CVE-2024-1001");
        await srcB.SyncAsync(opt, other, defenderB);
        await srcB.SyncAsync(opt, other, intuneB);

        Guid assetA;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.Connectors.AsNoTracking().SingleAsync(c => c.Id == defender)).DeviceSnapshotOutcome
                .Should().Be(DeviceSnapshotOutcome.Complete, "a primeira passada real registra o desfecho");

            var query = Query(db, now);
            var list = await query.ListAsync(new CrossSourceSituationFilter(PageSize: 1));
            list.Summary.AssetsWithBothSources.Should().Be(1, "o tenant vizinho, com os mesmos identificadores, não entra");
            list.Summary.SituationsIdentified.Should().Be(2);
            list.Total.Should().Be(2);
            list.Items.Should().ContainSingle().Which.RuleCode.Should().Be(RuleA);
            list.Items[0].OpenCveCount.Should().Be(3);
            list.Items[0].CvePreview.Should().Equal("CVE-2024-1001", "CVE-2024-1002", "CVE-2024-1003");
            assetA = list.Items[0].AssetId;

            var detail = (await query.GetForAssetAsync(assetA, cvePage: 2, cvePageSize: 2))!;
            detail.Cves!.Total.Should().Be(3);
            detail.Cves.Items.Select(c => c.CveId).Should().Equal("CVE-2024-1003");
            detail.Rules.Should().OnlyContain(r => r.State == CrossSourceStates.Identified && !r.HasCaveats);
        }

        // ---- 4) Isolamento: o outro tenant não enxerga o ativo; e desfecho não registrado vira ressalva, não afirmação ----
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
            (await Query(db, now).GetForAssetAsync(assetA, 1, 10)).Should().BeNull();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            await db.Connectors.Where(c => c.Id == defender)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeviceSnapshotOutcome, DeviceSnapshotOutcome.NotRecorded));
            var a = (await Query(db, now).GetForAssetAsync(assetA, 1, 10))!.Rules.Single(r => r.RuleCode == RuleA);
            a.State.Should().Be(CrossSourceStates.Identified);
            a.Caveats.Should().Contain(c => c.Code == "outcomeNotRecorded");
        }
    }

    [Fact]
    public async Task IngestionPausedMidPublication_TheReadQualifiesEachFact_AndNeverShowsTheUnpublishedPart()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var now = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var (defender, intune) = await SeedTenantAsync(opt, tenant);
        await Acquisition(now, "CVE-2024-1001").SyncAsync(opt, tenant, defender);
        await Acquisition(now, "CVE-2024-1001").SyncAsync(opt, tenant, intune);
        var assetId = await AssetOfAsync(opt, tenant, defender);
        var first = await WatermarkAsync(opt, tenant, defender);

        // Nova aquisição do Defender (uma CVE a mais) PAUSADA logo depois de publicar a presença dos dispositivos: a
        // marca avançou e o desfecho é "publicando"; nenhum lote de vulnerabilidades foi publicado ainda.
        using var barrier = new Barrier(DeviceIdentityResolver.CheckpointPresenceCommitted);
        var newer = Acquisition(now, "CVE-2024-1001", "CVE-2024-1002");
        var sync = Task.Run(() => newer.SyncAsync(opt, tenant, defender, barrier.Hook));
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        var during = await DetailAsync(opt, tenant, assetId, now);
        var a = during.Rules.Single(r => r.RuleCode == RuleA);
        a.State.Should().Be(CrossSourceStates.Identified);
        a.OpenCveCount.Should().Be(1, "a CVE ainda não publicada não aparece");
        a.Caveats.Select(c => c.Code).Should().Contain(new[] { "publicationNotConcluded", "previousAcquisition" });
        during.Evidence.Single(e => e.Role == "vulnerabilities").AcquisitionState
            .Should().Be(CrossSourceAcquisitionStates.CurrentUnconcluded);
        var cve = during.Cves!.Items.Should().ContainSingle().Subject;
        cve.Sources.Single().AcquiredAt.Should().Be(first, "cada CVE mantém a data da aquisição que a observou");
        cve.Sources.Single().AcquisitionState.Should().Be(CrossSourceAcquisitionStates.Previous);

        barrier.Release();
        await sync.WaitAsync(TimeSpan.FromSeconds(60));
        var after = await DetailAsync(opt, tenant, assetId, now);
        var done = after.Rules.Single(r => r.RuleCode == RuleA);
        done.HasCaveats.Should().BeFalse();
        done.OpenCveCount.Should().Be(2);
        after.Cves!.Items.Should().OnlyContain(c => c.Sources.Single().AcquisitionState == CrossSourceAcquisitionStates.Current);
        (await WatermarkAsync(opt, tenant, defender)).Should().BeAfter(first);
    }

    [Fact]
    public async Task IngestionCommittedMidRead_TheReadSeesOneSnapshot_NotAMixOfBothAcquisitions()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var now = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var (defender, intune) = await SeedTenantAsync(opt, tenant);
        await Acquisition(now, "CVE-2024-1001").SyncAsync(opt, tenant, defender);
        await Acquisition(now, "CVE-2024-1001").SyncAsync(opt, tenant, intune);
        var assetId = await AssetOfAsync(opt, tenant, defender);
        var first = await WatermarkAsync(opt, tenant, defender);

        // A leitura lê as fontes (marca e desfecho) e PARA antes de ler os fatos. Nesse intervalo uma aquisição nova
        // completa é publicada inteira (marca, presença, lotes, ausência, desfecho). Numa leitura sem fotografia única,
        // os fatos novos seriam comparados à marca antiga — uma mistura. Em REPEATABLE READ, a leitura inteira é a de antes.
        using var barrier = new Barrier(CrossSourceCorrelationQuery.CheckpointFactsPending);
        var read = Task.Run(async () =>
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            var query = new CrossSourceCorrelationQuery(db, new FakeTimeProvider(now.AddHours(2)),
                Options.Create(new CrossSourceCorrelationOptions())) { Checkpoint = barrier.Hook };
            return (await query.GetForAssetAsync(assetId, 1, 10))!;
        });
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        await Acquisition(now, "CVE-2024-1001", "CVE-2024-1002").SyncAsync(opt, tenant, defender);
        var published = await WatermarkAsync(opt, tenant, defender);
        published.Should().BeAfter(first, "a aquisição nova foi publicada enquanto a leitura estava aberta");

        barrier.Release();
        var snapshot = await read.WaitAsync(TimeSpan.FromSeconds(30));
        var a = snapshot.Rules.Single(r => r.RuleCode == RuleA);
        a.OpenCveCount.Should().Be(1, "a leitura é a fotografia de antes, inteira");
        a.HasCaveats.Should().BeFalse("marca, desfecho e fatos são do MESMO instante — nada fica 'sem marca registrada'");
        snapshot.Evidence.Single(e => e.Role == "vulnerabilities").AcquiredAt.Should().Be(first);
        snapshot.Cves!.Items.Single().Sources.Single().AcquisitionState.Should().Be(CrossSourceAcquisitionStates.Current);

        var fresh = await DetailAsync(opt, tenant, assetId, now);
        fresh.Rules.Single(r => r.RuleCode == RuleA).OpenCveCount.Should().Be(2, "a leitura seguinte vê a aquisição nova");
        fresh.Evidence.Single(e => e.Role == "vulnerabilities").AcquiredAt.Should().Be(published);
    }

    // ---- apoio --------------------------------------------------------------------------------------------------

    /// <summary>Uma aquisição sintética das duas fontes (mesmo dispositivo X), com datas relativas a <paramref name="now"/>.</summary>
    private static SyntheticDeviceSources Acquisition(DateTimeOffset now, params string[] cves)
    {
        string Q(string s) => SyntheticDeviceSources.Q(s);
        var machine = "{\"id\":\"mde-0001\",\"osPlatform\":\"Windows11\",\"lastSeen\":" + Q(now.AddHours(-1).ToString("O")) +
            ",\"computerDnsName\":\"pc-01.demo.example.com\",\"aadDeviceId\":" + Q(DevX) + "}";
        var device = "{\"id\":\"int-0001\",\"complianceState\":\"noncompliant\",\"operatingSystem\":\"Windows\"" +
            ",\"lastSyncDateTime\":" + Q(now.AddHours(-1).ToString("O")) + ",\"isEncrypted\":false,\"azureADDeviceId\":" + Q(DevX) + "}";
        return new SyntheticDeviceSources
        {
            IntuneNow = now,
            DefenderMachines = SyntheticDeviceSources.Page(machine),
            DefenderRelations = SyntheticDeviceSources.Page(cves.Select(c => SyntheticDeviceSources.Relation("mde-0001", c)).ToArray()),
            DefenderCves = SyntheticDeviceSources.Page(cves
                .Select(c => "{\"id\":" + Q(c) + ",\"name\":" + Q(c) + ",\"severity\":\"High\",\"cvssV3\":8.1}").ToArray()),
            IntuneDevices = SyntheticDeviceSources.Page(device),
        };
    }

    private static async Task<(Guid Defender, Guid Intune)> SeedTenantAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant)
    {
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente Sintético", Slug = "xs-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var tdb = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var d = SyntheticDeviceSources.Connector(tenant, ConnectorCapability.VulnerabilityScanner, DirA);
        var i = SyntheticDeviceSources.Connector(tenant, ConnectorCapability.ConfigAnalyzer, DirA);
        tdb.Connectors.AddRange(d, i);
        await tdb.SaveChangesAsync();
        return (d.Id, i.Id);
    }

    private static ICrossSourceCorrelationQuery Query(AegisScoreDbContext db, DateTimeOffset now) =>
        new CrossSourceCorrelationQuery(db, new FakeTimeProvider(now.AddHours(2)), Options.Create(new CrossSourceCorrelationOptions()));

    private static async Task<AssetCrossSourceDto> DetailAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid assetId, DateTimeOffset now)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        return (await Query(db, now).GetForAssetAsync(assetId, 1, 10))!;
    }

    private static async Task<Guid> AssetOfAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid defender)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        return (await db.AssetSourceBindings.AsNoTracking().SingleAsync(b => b.ConnectorConfigId == defender)).AssetId;
    }

    private static async Task<DateTimeOffset> WatermarkAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        return (await db.Connectors.AsNoTracking().SingleAsync(c => c.Id == connector)).DeviceSnapshotWatermark!.Value;
    }

    /// <summary>Barreira de UM checkpoint; libera no <c>Dispose</c> para nunca segurar transação até o timeout.</summary>
    private sealed class Barrier : IDisposable
    {
        private readonly string _at;
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Barrier(string at) => _at = at;
        public Task Reached => _reached.Task;
        public void Release() => _release.TrySetResult();
        public void Dispose() => Release();

        public async Task Hook(string checkpoint, CancellationToken ct)
        {
            if (checkpoint != _at) return;
            _reached.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
    }
}
