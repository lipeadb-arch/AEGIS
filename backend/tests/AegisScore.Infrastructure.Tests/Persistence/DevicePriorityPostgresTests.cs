using System;
using System.Collections.Generic;
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
/// [AEGIS-RISK-PRIORITIZATION-01] O que SÓ o PostgreSQL real prova sobre a prioridade de tratamento (gate
/// <c>AEGIS_TEST_PG</c>): a migration aditiva da proveniência da criticidade sobre ativos LEGADOS sem inventar declaração;
/// a tradução real no Npgsql da classificação da CVE, da seleção de candidatos (limite superior por GROUP BY + MIN), do
/// agrupamento por (ativo, conector, marca, classificação), da paginação dos casos e do isolamento por tenant; e a
/// LEITURA COERENTE durante uma ingestão — pausada no meio da publicação (a CVE não publicada nunca entra) e concluída no
/// meio da leitura (REPEATABLE READ: uma fotografia só). A ingestão passa pelos conectores reais sobre HTTP sintético.
/// </summary>
public sealed class DevicePriorityPostgresTests
{
    private const string PreviousMigration = "20260912001829_CrossSource01_DeviceSnapshotOutcome";
    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DevX = SyntheticDeviceSources.DevX;

    [Fact]
    public async Task Migration_NoInventedProvenance_ThenRankCandidatesAndCasesTranslateOnNpgsql_TenantIsolated()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var opt = pg.DbOptions();
        var now = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var legacyAsset = Guid.NewGuid();

        // ---- 1) Banco "de antes": um ativo legado com criticidade 4 cadastrada, sem proveniência ------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);
            foreach (var (id, slug) in new[] { (tenant, "dp-a"), (other, "dp-b") })
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Tenants" ("Id","Name","Slug","Status","CreatedAt")
                    VALUES ({0}, 'Cliente Sintético', {1}, 1, now());
                    """, id, slug + "-" + id.ToString("N"));
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Assets" ("Id","TenantId","Name","Category","Criticality","DiscoverySource","IsActive","NameOrigin","CreatedAt")
                VALUES ({0}, {1}, 'Servidor legado sintético', 0, 4, 0, true, 0, now());
                """, legacyAsset, tenant);
        }

        // ---- 2) A migration aplica e NÃO inventa declaração para o valor legado ----------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().Contain(m => m.EndsWith("_RiskPrioritization01_AssetCriticalityDeclaration"));
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var legacy = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == legacyAsset);
            legacy.Criticality.Should().Be(4, "o valor legado não é reescrito");
            legacy.CriticalityDeclaredAt.Should().BeNull();
            legacy.CriticalityDeclaredValue.Should().BeNull();
            var detail = (await Query(db, now).GetForAssetAsync(legacyAsset, 1, 10))!;
            detail.Criticality.State.Should().Be(DevicePriorityCriticalityStates.NotConfirmed, "valor legado sem proveniência");
            detail.Status.Should().Be(DevicePriorityStatuses.NoSourceRecord);
        }

        // ---- 3) Ingestão real nos dois tenants, com os MESMOS identificadores ---------------------------------------------
        var (defender, intune) = await ConnectorsAsync(opt, tenant);
        var (defenderB, intuneB) = await ConnectorsAsync(opt, other);
        var src = Acquisition(now,
            new[] { ("mde-0001", "pc-01.demo.example.com", DevX), ("mde-0002", "aa-baixa.demo.example.com", null),
                    ("mde-0003", "zz-critica.demo.example.com", null) },
            new[] { ("mde-0001", "CVE-2024-1001"), ("mde-0001", "CVE-2024-1002"), ("mde-0001", "CVE-2024-1003"),
                    ("mde-0002", "CVE-2024-1004"), ("mde-0003", "CVE-2024-1005") },
            Cve("CVE-2024-1001", "High", 8.1), Cve("CVE-2024-1002", "Medium", 5.5), Cve("CVE-2024-1003", "Low", 2.0),
            Cve("CVE-2024-1004", "Low", 3.1), Cve("CVE-2024-1005", "Critical", 9.8, verified: true));
        await src.SyncAsync(opt, tenant, defender);
        await src.SyncAsync(opt, tenant, intune);
        var srcB = Acquisition(now, new[] { ("mde-0001", "pc-01.demo.example.com", (string?)DevX) }, new[] { ("mde-0001", "CVE-2024-1005") },
            Cve("CVE-2024-1005", "Critical", 9.8, verified: true));
        await srcB.SyncAsync(opt, other, defenderB);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var list = await Query(db, now).ListAsync(new DevicePriorityFilter());
            list.Summary.CandidateAssets.Should().Be(3, "o tenant vizinho, com os mesmos identificadores, não entra");
            list.Items.Select(i => (i.AssetName, i.Band)).Should().Equal(
                ("zz-critica.demo.example.com", DevicePriorityBands.P1),
                ("pc-01.demo.example.com", DevicePriorityBands.P2),   // Alta sem exploit (P3) + situações entre fontes
                ("aa-baixa.demo.example.com", DevicePriorityBands.P4));
            list.Items[1].Aggravators.Should().ContainSingle();

            // Seleção de candidatos no banco: com teto 1, o avaliado é o de melhor limite superior — não o primeiro nome.
            var cut = await Query(db, now, max: 1).ListAsync(new DevicePriorityFilter());
            cut.Summary.EvaluationTruncated.Should().BeTrue();
            cut.Items.Should().ContainSingle().Which.AssetName.Should().Be("zz-critica.demo.example.com");

            // Casos paginados no banco, na ordem da política.
            var pc = list.Items[1].AssetId;
            var page2 = (await Query(db, now).GetForAssetAsync(pc, casePage: 2, casePageSize: 1))!;
            page2.Cases!.Total.Should().Be(3);
            page2.Cases.Items.Single().CveId.Should().Be("CVE-2024-1002");
            page2.DeterminingCase!.CveId.Should().Be("CVE-2024-1001", "o determinante independe da página pedida");

            // Tradução da classificação no Npgsql = a mesma classificação em memória.
            var ranks = await db.Threats.AsNoTracking().Where(t => t.Code.StartsWith("CVE-2024-100"))
                .Select(DevicePriorityPolicy.Rank).ToListAsync();
            var threats = await db.Threats.AsNoTracking().Where(t => t.Code.StartsWith("CVE-2024-100")).ToListAsync();
            foreach (var t in threats)
            {
                var memory = DevicePriorityPolicy.RankOf(t.CvssScore, t.Severity, t.PublicExploit, t.ExploitVerified);
                var r = ranks.Single(x => x.ThreatId == t.Id);
                (r.SeverityOrder, r.ExploitOrder).Should().Be((memory.SeverityOrder, memory.ExploitOrder), t.Code);
            }
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            var b = await Query(db, now).ListAsync(new DevicePriorityFilter());
            b.Items.Should().ContainSingle().Which.Band.Should().Be(DevicePriorityBands.P1);
            (await Query(db, now).GetForAssetAsync(legacyAsset, 1, 10)).Should().BeNull();
        }
    }

    [Fact]
    public async Task IngestionPausedMidPublication_TheUnpublishedCveNeverEntersThePriority()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var now = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var tenant = await TenantAsync(opt);
        var (defender, _) = await ConnectorsAsync(opt, tenant);
        var machine = new[] { ("mde-0001", "pc-01.demo.example.com", (string?)DevX) };
        await Acquisition(now, machine, new[] { ("mde-0001", "CVE-2024-2001") }, Cve("CVE-2024-2001", "High", 8.1))
            .SyncAsync(opt, tenant, defender);
        var assetId = await AssetOfAsync(opt, tenant, defender);

        using var barrier = new Barrier(DeviceIdentityResolver.CheckpointPresenceCommitted);
        var newer = Acquisition(now, machine, new[] { ("mde-0001", "CVE-2024-2001"), ("mde-0001", "CVE-2024-2002") },
            Cve("CVE-2024-2001", "High", 8.1), Cve("CVE-2024-2002", "Critical", 9.8, publicExploit: true));
        var sync = Task.Run(() => newer.SyncAsync(opt, tenant, defender, barrier.Hook));
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        var during = await DetailAsync(opt, tenant, assetId, now);
        during.Band.Should().Be(DevicePriorityBands.P3, "a CVE crítica ainda não publicada não entra");
        during.Cases!.Total.Should().Be(1);
        during.Caveats.Select(c => c.Code).Should().Contain(new[] { "publicationNotConcluded", "previousAcquisition" });

        barrier.Release();
        await sync.WaitAsync(TimeSpan.FromSeconds(60));
        var after = await DetailAsync(opt, tenant, assetId, now);
        after.Band.Should().Be(DevicePriorityBands.P1);
        after.DeterminingCase!.CveId.Should().Be("CVE-2024-2002");
        after.Caveats.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestionCommittedMidRead_TheQueueSeesOneSnapshot_NotAMixOfBothAcquisitions()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var now = DeviceSnapshotMarker.Normalize(DateTimeOffset.UtcNow);
        var tenant = await TenantAsync(opt);
        var (defender, _) = await ConnectorsAsync(opt, tenant);
        var machine = new[] { ("mde-0001", "pc-01.demo.example.com", (string?)DevX) };
        await Acquisition(now, machine, new[] { ("mde-0001", "CVE-2024-3001") }, Cve("CVE-2024-3001", "High", 8.1))
            .SyncAsync(opt, tenant, defender);

        // A leitura lê as fontes e PARA antes dos fatos; nesse intervalo uma aquisição nova completa é publicada. Em
        // REPEATABLE READ a leitura inteira é a de antes — nunca marca antiga com fatos novos.
        using var barrier = new Barrier(DevicePriorityQuery.CheckpointFactsPending);
        var read = Task.Run(async () =>
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            var query = new DevicePriorityQuery(db, new FakeTimeProvider(now.AddHours(2)),
                Options.Create(new CrossSourceCorrelationOptions())) { Checkpoint = barrier.Hook };
            return await query.ListAsync(new DevicePriorityFilter());
        });
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));
        await Acquisition(now, machine, new[] { ("mde-0001", "CVE-2024-3001"), ("mde-0001", "CVE-2024-3002") },
                Cve("CVE-2024-3001", "High", 8.1), Cve("CVE-2024-3002", "Critical", 9.8, publicExploit: true))
            .SyncAsync(opt, tenant, defender);

        barrier.Release();
        var snapshot = await read.WaitAsync(TimeSpan.FromSeconds(30));
        var item = snapshot.Items.Should().ContainSingle().Subject;
        item.Band.Should().Be(DevicePriorityBands.P3, "a leitura é a fotografia de antes, inteira");
        item.DeterminingCase!.CveId.Should().Be("CVE-2024-3001");
        item.Caveats.Should().BeEmpty("marca, desfecho e fatos são do MESMO instante");

        await using var fresh = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        (await Query(fresh, now).ListAsync(new DevicePriorityFilter())).Items.Single().Band
            .Should().Be(DevicePriorityBands.P1, "a leitura seguinte vê a aquisição nova");
    }

    // ---- apoio --------------------------------------------------------------------------------------------------

    private static string Q(string s) => SyntheticDeviceSources.Q(s);

    private static string Cve(string id, string severity, double cvss, bool? publicExploit = null, bool? verified = null)
    {
        var s = "{\"id\":" + Q(id) + ",\"name\":" + Q(id) + ",\"severity\":" + Q(severity) + ",\"cvssV3\":" +
            cvss.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (publicExploit is { } pe) s += ",\"publicExploit\":" + (pe ? "true" : "false");
        if (verified is { } ev) s += ",\"exploitVerified\":" + (ev ? "true" : "false");
        return s + "}";
    }

    /// <summary>Aquisição sintética: máquinas do Defender (com ou sem identificador de diretório) e o Intune não conforme do X.</summary>
    private static SyntheticDeviceSources Acquisition(
        DateTimeOffset now, (string Id, string Dns, string? DeviceId)[] machines, (string Machine, string Cve)[] relations,
        params string[] cves)
    {
        var device = "{\"id\":\"int-0001\",\"complianceState\":\"noncompliant\",\"operatingSystem\":\"Windows\"" +
            ",\"lastSyncDateTime\":" + Q(now.AddHours(-1).ToString("O")) + ",\"isEncrypted\":false,\"azureADDeviceId\":" + Q(DevX) + "}";
        return new SyntheticDeviceSources
        {
            IntuneNow = now,
            DefenderMachines = SyntheticDeviceSources.Page(machines.Select(m =>
                "{\"id\":" + Q(m.Id) + ",\"osPlatform\":\"Windows11\",\"lastSeen\":" + Q(now.AddHours(-1).ToString("O")) +
                ",\"computerDnsName\":" + Q(m.Dns) + (m.DeviceId is null ? "" : ",\"aadDeviceId\":" + Q(m.DeviceId)) + "}").ToArray()),
            DefenderRelations = SyntheticDeviceSources.Page(relations.Select(r => SyntheticDeviceSources.Relation(r.Machine, r.Cve)).ToArray()),
            DefenderCves = SyntheticDeviceSources.Page(cves),
            IntuneDevices = SyntheticDeviceSources.Page(device),
        };
    }

    private static async Task<Guid> TenantAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        var tenant = Guid.NewGuid();
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente Sintético", Slug = "dp-" + tenant.ToString("N"), Status = TenantStatus.Active });
        await db.SaveChangesAsync();
        return tenant;
    }

    private static async Task<(Guid Defender, Guid Intune)> ConnectorsAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var d = SyntheticDeviceSources.Connector(tenant, ConnectorCapability.VulnerabilityScanner, DirA);
        var i = SyntheticDeviceSources.Connector(tenant, ConnectorCapability.ConfigAnalyzer, DirA);
        db.Connectors.AddRange(d, i);
        await db.SaveChangesAsync();
        return (d.Id, i.Id);
    }

    private static DevicePriorityQuery Query(AegisScoreDbContext db, DateTimeOffset now, int? max = null) =>
        new(db, new FakeTimeProvider(now.AddHours(2)), Options.Create(new CrossSourceCorrelationOptions()))
        {
            MaxEvaluatedAssets = max ?? DevicePriorityQuery.DefaultMaxEvaluatedAssets,
        };

    private static async Task<AssetDevicePriorityDto> DetailAsync(
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
