using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
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
using Npgsql;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-ENTITY-RESOLUTION-01] O que SÓ o PostgreSQL real prova sobre a resolução de dispositivos (gate
/// <c>AEGIS_TEST_PG</c>): a migration aditiva sobre um banco LEGADO sem inventar identificador; as invariantes no
/// próprio banco (chave única, um dispositivo por ativo, FK composta tenant-safe, CHECK); a chegada CONCORRENTE das
/// duas fontes pelo executor real, com os conectores reais sobre HTTP sintético, sem duplicação silenciosa; e o
/// isolamento da leitura consolidada (listagem real, com ordenação/ILike do Npgsql).
/// </summary>
public sealed class DeviceResolutionPostgresTests
{
    private const string PreviousMigration = "20260910221714_Adm02_SnapshotRetentionRemovalProof";
    private const string DirA = SyntheticDeviceSources.DirA;
    private const string DevX = SyntheticDeviceSources.DevX;

    [Fact]
    public async Task Migration_OverLegacyBindings_InventsNothing_ThenLinks_AndTheDatabaseEnforcesTheInvariants()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var defender = Guid.NewGuid();
        var intune = Guid.NewGuid();
        var legacyAsset = Guid.NewGuid();

        // ---- 1) Banco "de antes": schema na migration anterior, dados legados por SQL cru ------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(PreviousMigration);
            foreach (var (id, slug) in new[] { (tenant, "legado-a"), (other, "legado-b") })
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Tenants" ("Id","Name","Slug","Status","CreatedAt")
                    VALUES ({0}, 'Cliente Legado Sintético', {1}, 1, now());
                    """, id, slug + "-" + id.ToString("N"));
            foreach (var (id, capability) in new[]
                     { (defender, ConnectorCapability.VulnerabilityScanner), (intune, ConnectorCapability.ConfigAnalyzer) })
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Connectors" ("Id","TenantId","Provider","Capability","DisplayName","AuthType",
                      "EncryptedSettings","Enabled","SyncIntervalMinutes","LastStatus","CreatedAt")
                    VALUES ({0}, {1}, {2}, {3}, 'Conector sintético', {4}, '{{}}', true, 360, 0, now());
                    """, id, tenant, (int)ConnectorProvider.Microsoft, (int)capability,
                    (int)ConnectorAuthType.OAuthClientCredentials);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Assets" ("Id","TenantId","Name","Category","Criticality","OwnerName","DiscoverySource",
                  "IsActive","LastSeenAt","CreatedAt")
                VALUES ({0}, {1}, 'Servidor de arquivos (curado)', {2}, 3, 'Equipe de Infraestrutura', {3}, true,
                  now() - interval '1 day', now() - interval '60 days');
                """, legacyAsset, tenant, (int)AssetCategory.Hardware, (int)AssetDiscoverySource.Connector);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AssetSourceBindings" ("Id","TenantId","AssetId","ConnectorConfigId","ExternalId","DisplayName",
                  "SubType","FirstObservedAt","LastObservedAt","IsActive","CreatedAt")
                VALUES ({0}, {1}, {2}, {3}, 'm-legado', 'fs01.demo.example.com', 'Windows11',
                  now() - interval '60 days', now() - interval '1 day', true, now() - interval '60 days');
                """, Guid.NewGuid(), tenant, legacyAsset, defender);
        }

        // ---- 2) A migration nova aplica sobre o legado e NÃO inventa identificador ------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().Contain(m => m.EndsWith("_DeviceResolution01_StrongIdentifiers"));
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var b = await db.AssetSourceBindings.SingleAsync();
            b.ResolutionState.Should().Be(AssetBindingResolutionState.NotEvaluated);
            b.DirectoryIdStatus.Should().Be(DirectoryIdentifierStatus.NotEvaluated);
            b.DirectoryDeviceId.Should().BeNull("nenhum identificador é preenchido por suposição");
            b.DirectoryNamespace.Should().BeNull();
            b.SourceLabel.Should().BeNull();
            (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(0);
            (await db.Assets.SingleAsync()).NameOrigin.Should().Be(AssetNameOrigin.Unspecified);
        }

        // ---- 3) A coleta real estabelece a chave NO ativo legado; a outra fonte entra nele ----------------------
        await ReconcileDefenderAsync(opt, tenant, defender, ("m-legado", "fs01.demo.example.com", DevX));
        await ReconcileIntuneAsync(opt, tenant, intune, complete: true, ("dev-1", DevX));
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var asset = await db.Assets.SingleAsync();
            asset.Id.Should().Be(legacyAsset);
            asset.Name.Should().Be("Servidor de arquivos (curado)");
            asset.OwnerName.Should().Be("Equipe de Infraestrutura");
            asset.Criticality.Should().Be(3);
            (await db.AssetSourceBindings.ToListAsync()).Should().HaveCount(2)
                .And.OnlyContain(x => x.AssetId == legacyAsset && x.ResolutionState == AssetBindingResolutionState.Linked);
            (await db.AssetStrongIdentifiers.SingleAsync()).AssetId.Should().Be(legacyAsset);
        }

        // ---- 4) Invariantes NO BANCO, por SQL cru (sem passar pela aplicação) -----------------------------------
        var secondAsset = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Assets.Add(new Asset { Id = secondAsset, Name = "Outro ativo sintético", DiscoverySource = AssetDiscoverySource.Connector });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            (await InsertKey(db, tenant, secondAsset, DevX)).Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "UX_AssetStrongIdentifier_Natural",
                "uma chave aponta para UM ativo");
            (await InsertKey(db, tenant, legacyAsset, SyntheticDeviceSources.DevY)).Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "UX_AssetStrongIdentifier_AssetScope",
                "um ativo não carrega dois dispositivos do mesmo diretório");
            (await InsertKey(db, other, legacyAsset, SyntheticDeviceSources.DevY)).Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.ForeignKeyViolation
                && e.ConstraintName == "FK_AssetStrongIdentifiers_Assets_Asset_Tenant",
                "a FK composta recusa chave de um tenant apontando para ativo de outro");
            (await InsertKey(db, tenant, secondAsset, "00000000-0000-0000-0000-000000000000")).Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.CheckViolation && e.ConstraintName == "CK_AssetStrongIdentifiers_Valid",
                "o GUID vazio é recusado pelo próprio banco");
        }
    }

    [Fact]
    public async Task ConcurrentArrivalOfDefenderAndIntune_ThroughTheRealExecutor_NeverDuplicates_InBothOrders()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        var (defender, intune) = await SeedTenantAsync(opt, tenant);

        const int rounds = 6;
        const int perRound = 20;
        var devices = new List<(string MachineId, string IntuneId, string DeviceId)>();
        for (var round = 0; round < rounds; round++)
        {
            for (var i = 0; i < perRound; i++)
                devices.Add(($"m-{round}-{i}", $"dev-{round}-{i}", Guid.NewGuid().ToString("D")));

            // Cada fotografia é COMPLETA e cumulativa (nada sai), com dispositivos NOVOS a cada rodada — é neles que
            // as duas fontes disputam a criação da chave forte.
            var src = new SyntheticDeviceSources
            {
                DefenderMachines = SyntheticDeviceSources.Page(devices
                    .Select(d => SyntheticDeviceSources.Machine(d.MachineId, d.MachineId + ".demo.example.com",
                        SyntheticDeviceSources.Q(d.DeviceId))).ToArray()),
                IntuneDevices = SyntheticDeviceSources.Page(devices
                    .Select(d => SyntheticDeviceSources.Device(d.IntuneId, SyntheticDeviceSources.Q(d.DeviceId))).ToArray()),
            };

            var syncs = new List<Func<Task>>
            {
                () => src.SyncAsync(opt, tenant, defender),
                () => src.SyncAsync(opt, tenant, intune),
            };
            if (round % 2 == 1) syncs.Reverse();                           // as duas ordens de chegada
            if (round == 3) syncs.Add(() => src.SyncAsync(opt, tenant, defender));   // mesma fonte em paralelo
            await Task.WhenAll(syncs.Select(s => Task.Run(s)));
        }

        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var keys = await db.AssetStrongIdentifiers.AsNoTracking().ToListAsync();
        var assets = await db.Assets.AsNoTracking().ToListAsync();
        var bindings = await db.AssetSourceBindings.AsNoTracking().ToListAsync();

        keys.Should().HaveCount(devices.Count, "uma chave forte por dispositivo, nunca duas");
        assets.Should().HaveCount(devices.Count, "nenhum ativo duplicado nem órfão da corrida");
        bindings.Should().HaveCount(devices.Count * 2);
        bindings.Should().OnlyContain(b => b.IsActive && b.ResolutionState == AssetBindingResolutionState.Linked);
        foreach (var group in bindings.GroupBy(b => b.AssetId))
            group.Select(b => b.ConnectorConfigId).Should().BeEquivalentTo(new[] { defender, intune },
                "cada ativo carrega exatamente um registro de cada fonte");
        foreach (var d in devices)
        {
            var key = keys.Single(k => k.IdentifierValue == d.DeviceId);
            bindings.Single(b => b.ExternalId == d.MachineId).AssetId.Should().Be(key.AssetId);
            bindings.Single(b => b.ExternalId == d.IntuneId).AssetId.Should().Be(key.AssetId);
        }

        // A visão consolidada REAL (ordenação e filtros do Npgsql): cada dispositivo aparece UMA vez.
        var controller = new AssetsController(db, new AssetSourceQuery(db))
        {
            ControllerContext = SyntheticDeviceSources.ControllerContextFor("Analyst"),
        };
        var page = (await controller.List(new AssetQuery { Page = 1, PageSize = 200 }, CancellationToken.None)).Value!;
        page.TotalCount.Should().Be(devices.Count, "os inventários das duas fontes não são somados");
        page.Items.Should().OnlyContain(a => a.Sources!.ActiveSourceCount == 2
            && a.Sources.CrossSourceState == AssetCrossSourceStates.Linked);
    }

    [Fact]
    public async Task SameDirectoryAndIdsInTwoTenants_StaySeparate_AndTheConsolidatedReadIsIsolated()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (defenderA, intuneA) = await SeedTenantAsync(opt, tenantA);
        var (defenderB, intuneB) = await SeedTenantAsync(opt, tenantB, migrate: false);

        var src = new SyntheticDeviceSources
        {
            DefenderMachines = SyntheticDeviceSources.Page(
                SyntheticDeviceSources.Machine("m-1", "pc-01.demo.example.com", SyntheticDeviceSources.Q(DevX))),
            IntuneDevices = SyntheticDeviceSources.Page(
                SyntheticDeviceSources.Device("dev-1", SyntheticDeviceSources.Q(DevX))),
        };
        await Task.WhenAll(
            src.SyncAsync(opt, tenantA, defenderA), src.SyncAsync(opt, tenantB, intuneB));
        await Task.WhenAll(
            src.SyncAsync(opt, tenantA, intuneA), src.SyncAsync(opt, tenantB, defenderB));

        Guid assetA, assetB;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            assetA = (await db.Assets.SingleAsync()).Id;
            (await db.AssetSourceBindings.CountAsync()).Should().Be(2);
            (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(1);
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
            assetB = (await db.Assets.SingleAsync()).Id;
        assetA.Should().NotBe(assetB);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var controller = new AssetsController(db, new AssetSourceQuery(db))
            {
                ControllerContext = SyntheticDeviceSources.ControllerContextFor("TenantAdmin"),
            };
            var list = (await controller.List(new AssetQuery { Page = 1, PageSize = 50 }, CancellationToken.None)).Value!;
            list.Items.Should().ContainSingle().Which.Id.Should().Be(assetA);
            var foreign = await controller.Sources(assetB, CancellationToken.None);
            foreign.Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundResult>(
                "o ativo do outro tenant não existe nesta leitura, nem para o administrador");
            var own = (await controller.Sources(assetA, CancellationToken.None)).Value!;
            own.CrossSourceState.Should().Be(AssetCrossSourceStates.Linked);
            own.Sources.Should().OnlyContain(s => s.Diagnostics != null && s.Diagnostics.DirectoryNamespace == DirA);
        }
    }

    // ---- Ciclo de vida sob concorrência: intercalações FORÇADAS por barreiras ------------------------------------
    // Cada caso para uma passada num ponto exato (checkpoint da autoridade de resolução), executa a outra por inteiro
    // — ou prova, pelo pg_stat_activity, que ela está BLOQUEADA numa trava — e só então libera a primeira. Nada
    // depende de sorte de agendamento.

    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-11T10:00:00Z");

    [Fact]
    public async Task OlderPassPausedAfterPresence_NeverDeactivatesWhatANewerPassObserved_AndALatePassPublishesNothing()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        var (_, intune) = await SeedTenantAsync(opt, tenant);
        var (d1, d2, d3) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        await IntunePassAsync(opt, tenant, intune, T0, null, ("dev-1", d1), ("dev-2", d2), ("dev-3", d3));

        // A (fotografia t1: só dev-1) publica a presença e para ANTES da ausência.
        using var barrier = new Barrier(DeviceIdentityResolver.CheckpointPresenceCommitted);
        var a = Task.Run(() => IntunePassAsync(opt, tenant, intune, T0.AddMinutes(1), barrier.Hook, ("dev-1", d1)));
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        // B (fotografia t2, MAIS RECENTE: dev-1 e dev-2) roda inteira no meio. A marca tem fração ABAIXO do
        // microssegundo de propósito: o timestamptz a trunca, e a precedência/ausência dependem da igualdade exata
        // entre a marca em memória e a lida do banco (DeviceSnapshotMarker.Normalize).
        var t2 = T0.AddMinutes(2).AddTicks(7);
        var published = DeviceSnapshotMarker.Normalize(t2);
        var b = await IntunePassAsync(opt, tenant, intune, t2, null, ("dev-1", d1), ("dev-2", d2));
        b.BindingsDeactivated.Should().Be(1, "dev-3 saiu da fotografia completa mais recente");

        barrier.Release();
        var late = await a.WaitAsync(TimeSpan.FromSeconds(30));
        late.Superseded.Should().BeTrue("a passada anterior foi superada antes de publicar a ausência");
        late.DeactivationApplied.Should().BeFalse();
        late.BindingsDeactivated.Should().Be(0);

        await AssertIntuneStateAsync(opt, tenant, intune, published,
            active: new[] { "dev-1", "dev-2" }, inactive: new[] { "dev-3" });

        // Uma passada ainda mais atrasada (t1,5 < t2) chega depois de tudo: não publica nada, nem reativa dev-3.
        var older = await IntunePassAsync(opt, tenant, intune, T0.AddSeconds(90), null,
            ("dev-1", d1), ("dev-2", d2), ("dev-3", d3));
        older.Superseded.Should().BeTrue();
        older.BindingsCreated.Should().Be(0);
        await AssertIntuneStateAsync(opt, tenant, intune, published,
            active: new[] { "dev-1", "dev-2" }, inactive: new[] { "dev-3" });
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        (await db.Connectors.SingleAsync(c => c.Id == intune)).DeviceSnapshotWatermark.Should().Be(published);
        (await db.AssetStrongIdentifiers.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task PresenceArrivingWhileAbsenceHoldsTheSourceLock_WaitsForIt_AndIsNeverUndone()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        var (_, intune) = await SeedTenantAsync(opt, tenant);
        var (d1, d2) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        await IntunePassAsync(opt, tenant, intune, T0, null, ("dev-1", d1), ("dev-2", d2));

        // A (t1: só dev-1) entra na ausência — trava da fonte adquirida, precedência conferida — e para ANTES do UPDATE.
        using var barrier = new Barrier(DeviceIdentityResolver.CheckpointAbsenceLocked);
        var a = Task.Run(() => IntunePassAsync(opt, tenant, intune, T0.AddMinutes(1), barrier.Hook, ("dev-1", d1)));
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        // B (t2: dev-1 e dev-2) chega exatamente nessa janela e fica BLOQUEADA na trava da fonte.
        var t2 = T0.AddMinutes(2);
        var b = Task.Run(() => IntunePassAsync(opt, tenant, intune, t2, null, ("dev-1", d1), ("dev-2", d2)));
        await WaitUntilBlockedOnLockAsync(opt, "pg_advisory_xact_lock");
        b.IsCompleted.Should().BeFalse("a presença nova não intercala entre a seleção e o UPDATE da ausência");

        barrier.Release();
        var absent = await a.WaitAsync(TimeSpan.FromSeconds(30));
        var present = await b.WaitAsync(TimeSpan.FromSeconds(30));
        absent.DeactivationApplied.Should().BeTrue();
        absent.BindingsDeactivated.Should().Be(1, "na fotografia t1, dev-2 estava ausente");
        present.BindingsDeactivated.Should().Be(0);

        await AssertIntuneStateAsync(opt, tenant, intune, t2, active: new[] { "dev-1", "dev-2" }, inactive: Array.Empty<string>());
    }

    [Fact]
    public async Task CrossSourceRecomputeOfTheSameAsset_IsSerialized_AndReflectsTheLastBindingChange()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        var (defender, intune) = await SeedTenantAsync(opt, tenant);
        var device = Guid.NewGuid().ToString("D");
        await DefenderPassAsync(opt, tenant, defender, T0, null, new[] { ("m-1", device) }, Array.Empty<string>());
        await IntunePassAsync(opt, tenant, intune, T0, null, ("dev-1", device));
        Guid assetId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            assetId = (await db.Assets.SingleAsync()).Id;
            (await db.AssetSourceBindings.CountAsync(x => x.AssetId == assetId && x.IsActive)).Should().Be(2);
        }

        // Intune: fotografia completa SEM o dispositivo. Para no recálculo com a linha do ativo travada, depois de ler
        // os bindings — nesse instante o do Defender ainda está ativo, então ela vai gravar "ativo".
        using var barrier = new Barrier(DeviceIdentityResolver.CheckpointRecomputeLocked);
        var i = Task.Run(() => IntunePassAsync(opt, tenant, intune, T0.AddMinutes(1), barrier.Hook));
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        // Defender: fotografia completa também sem ele — desativa o próprio binding e fica BLOQUEADO no recálculo.
        var d = Task.Run(() => DefenderPassAsync(opt, tenant, defender, T0.AddMinutes(1), null,
            Array.Empty<(string, string)>(), Array.Empty<string>()));
        await WaitUntilBlockedOnLockAsync(opt, "FOR NO KEY UPDATE");
        d.IsCompleted.Should().BeFalse("o recálculo do mesmo ativo por outra fonte espera a trava da linha");

        barrier.Release();
        await i.WaitAsync(TimeSpan.FromSeconds(30));
        var defenderResult = await d.WaitAsync(TimeSpan.FromSeconds(30));
        defenderResult.BindingsDeactivated.Should().Be(1);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.AssetSourceBindings.ToListAsync()).Should().HaveCount(2).And.OnlyContain(x => !x.IsActive);
            (await db.Assets.SingleAsync()).IsActive.Should().BeFalse(
                "o último recálculo releu os bindings depois da trava e viu as duas fontes ausentes");
            (await db.AssetStrongIdentifiers.SingleAsync()).AssetId.Should().Be(assetId, "ausência não é exclusão da chave");
        }
    }

    [Fact]
    public async Task SameDefenderConnector_OlderPassPausedAfterResolution_StopsPublishing_AndNeverUndoesTheNewerSnapshot()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        var (defender, _) = await SeedTenantAsync(opt, tenant);
        var (x, y) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var both = new[] { ("m-1", x), ("m-2", y) };
        await DefenderPassAsync(opt, tenant, defender, T0, null, both, new[] { "m-1", "m-2" });

        // A (t1: as duas máquinas, com vulnerabilidade) resolve os dispositivos e para ANTES dos lotes de exposição.
        using var barrier = new Barrier(DeviceIdentityResolver.CheckpointPresenceCommitted);
        var a = Task.Run(() => DefenderPassAsync(opt, tenant, defender, T0.AddMinutes(1), barrier.Hook, both, new[] { "m-1", "m-2" }));
        await barrier.Reached.WaitAsync(TimeSpan.FromSeconds(30));

        // B (t2, MAIS RECENTE: só m-1) roda inteira: m-2 sai, a vulnerabilidade de m-2 é resolvida.
        var t2 = T0.AddMinutes(2);
        var b = await DefenderPassAsync(opt, tenant, defender, t2, null, new[] { ("m-1", x) }, new[] { "m-1" });
        b.BindingsDeactivated.Should().Be(1);
        b.ObservationsResolved.Should().Be(1);

        barrier.Release();
        var late = await a.WaitAsync(TimeSpan.FromSeconds(30));
        late.Resolution!.Superseded.Should().BeTrue();
        late.ObservationsOpened.Should().Be(0);
        late.ObservationsReopened.Should().Be(0, "a passada atrasada não reabre o que a mais nova resolveu");
        late.ObservationsResolved.Should().Be(0, "nem resolve o que a mais nova observou");
        late.BindingsDeactivated.Should().Be(0);

        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var bindings = (await db.AssetSourceBindings.ToListAsync()).ToDictionary(z => z.ExternalId);
        bindings["m-1"].IsActive.Should().BeTrue();
        bindings["m-1"].LastObservedAt.Should().Be(t2);
        bindings["m-2"].IsActive.Should().BeFalse();
        (await db.Assets.SingleAsync(z => z.Id == bindings["m-1"].AssetId)).IsActive.Should().BeTrue();
        (await db.Assets.SingleAsync(z => z.Id == bindings["m-2"].AssetId)).IsActive.Should().BeFalse();
        var observations = await db.AssetThreatObservations.Include(o => o.AssetThreatExposure).ToListAsync();
        var m1 = observations.Single(o => o.AssetThreatExposure!.AssetId == bindings["m-1"].AssetId);
        var m2 = observations.Single(o => o.AssetThreatExposure!.AssetId == bindings["m-2"].AssetId);
        m1.LifecycleState.Should().Be(ObservationLifecycle.Open);
        m1.LastSeenAt.Should().Be(t2, "LastSeenAt nunca regride para a fotografia anterior");
        m2.LifecycleState.Should().Be(ObservationLifecycle.Resolved);
    }

    // ---- apoio --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Barreira de UM checkpoint: avisa quando a passada chega nele e a segura até ser liberada. Descartável: se uma
    /// asserção falhar antes do <see cref="Release"/>, o <c>using</c> a libera antes de o banco descartável ser
    /// removido — a passada pausada nunca fica segurando trava/transação até o timeout.
    /// </summary>
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

    /// <summary>
    /// Espera — pela condição observada no próprio PostgreSQL, não por um intervalo fixo — até uma sessão deste banco
    /// estar BLOQUEADA numa trava executando a consulta indicada (a da trava da fonte ou a da linha do ativo). É o que
    /// prova que a intercalação pretendida aconteceu; sem a trava no código, a espera falha por timeout.
    /// </summary>
    private static async Task WaitUntilBlockedOnLockAsync(DbContextOptions<AegisScoreDbContext> opt, string queryFragment)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity " +
                "WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE {0}",
                "%" + queryFragment + "%").SingleAsync();
            if (waiting > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(
            $"Nenhuma sessão ficou bloqueada em '{queryFragment}' — a intercalação não foi forçada.");
    }

    private static async Task<DeviceResolutionSyncResult> IntunePassAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector, DateTimeOffset snapshotAt,
        Func<string, CancellationToken, Task>? checkpoint, params (string Id, string DeviceId)[] devices)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var observations = devices.Select(d => new DeviceSourceObservation(
            d.Id, DeviceDirectoryIdentifiers.ParseDeviceId(d.DeviceId), null, "Windows", null,
            DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted)).ToList();
        return await new DeviceIdentityResolver(db) { Checkpoint = checkpoint }.ReconcileSnapshotAsync(
            connector, "Microsoft Intune", DirA, observations, completeSnapshot: true, snapshotAt, CancellationToken.None);
    }

    private static async Task<VulnerabilitySyncResult> DefenderPassAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector, DateTimeOffset collectedAt,
        Func<string, CancellationToken, Task>? checkpoint, (string Id, string DeviceId)[] machines, string[] vulnerable)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var collection = new VulnerabilityCollection(
            machines.Select(m => new VulnerabilityMachine(m.Id, m.Id + ".demo.example.com", "Windows11", null,
                DeviceDirectoryIdentifiers.ParseDeviceId(m.DeviceId))).ToList(),
            Array.Empty<VulnerabilityCve>(),
            vulnerable.Select(m => new MachineCveRelation(m, "CVE-2024-7256", "chrome", "google", "1.0", null, "High")).ToList(),
            IsComplete: true, 0, 0, 0, "Microsoft Defender Vulnerability Management", DirA, collectedAt);
        return await new VulnerabilityReconciler(db) { Checkpoint = checkpoint }
            .ReconcileAsync(connector, collection, CancellationToken.None);
    }

    private static async Task AssertIntuneStateAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector, DateTimeOffset latest,
        string[] active, string[] inactive)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var bindings = await db.AssetSourceBindings.Where(x => x.ConnectorConfigId == connector).ToListAsync();
        var assets = await db.Assets.ToDictionaryAsync(a => a.Id);
        foreach (var id in active)
        {
            var b = bindings.Single(x => x.ExternalId == id);
            b.IsActive.Should().BeTrue(id + " foi observado pela fotografia mais recente");
            b.LastObservedAt.Should().Be(latest, id + ": a presença mais recente nunca é substituída pela anterior");
            assets[b.AssetId].IsActive.Should().BeTrue(id);
        }
        foreach (var id in inactive)
        {
            var b = bindings.Single(x => x.ExternalId == id);
            b.IsActive.Should().BeFalse(id);
            b.ResolvedAt.Should().Be(latest, id + ": desativado pela fotografia mais recente");
            assets[b.AssetId].IsActive.Should().BeFalse(id);
        }
    }

    private static async Task<(Guid Defender, Guid Intune)> SeedTenantAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, bool migrate = true)
    {
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            if (migrate) await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente Sintético", Slug = "sint-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var tdb = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var defender = SyntheticDeviceSources.Connector(tenant, ConnectorCapability.VulnerabilityScanner, DirA);
        var intune = SyntheticDeviceSources.Connector(tenant, ConnectorCapability.ConfigAnalyzer, DirA);
        tdb.Connectors.AddRange(defender, intune);
        await tdb.SaveChangesAsync();
        return (defender.Id, intune.Id);
    }

    private static async Task ReconcileDefenderAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector,
        params (string Id, string Dns, string DeviceId)[] machines)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var collection = new VulnerabilityCollection(
            machines.Select(m => new VulnerabilityMachine(m.Id, m.Dns, "Windows11", null,
                DeviceDirectoryIdentifiers.ParseDeviceId(m.DeviceId))).ToList(),
            Array.Empty<VulnerabilityCve>(), Array.Empty<MachineCveRelation>(),
            IsComplete: true, 0, 0, 0, "Microsoft Defender Vulnerability Management", DirA);
        await new VulnerabilityReconciler(db).ReconcileAsync(connector, collection, CancellationToken.None);
    }

    private static async Task ReconcileIntuneAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector, bool complete,
        params (string Id, string DeviceId)[] devices)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var observations = devices.Select(d => new DeviceSourceObservation(
            d.Id, DeviceDirectoryIdentifiers.ParseDeviceId(d.DeviceId), null, "Windows", null,
            DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted)).ToList();
        await new DeviceIdentityResolver(db).ReconcileSnapshotAsync(
            connector, "Microsoft Intune", DirA, observations, complete, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static async Task<PostgresException?> InsertKey(AegisScoreDbContext db, Guid tenant, Guid asset, string value)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AssetStrongIdentifiers" ("Id","TenantId","AssetId","DirectoryNamespace","IdentifierType",
                  "IdentifierValue","EstablishedAt","EstablishedByConnectorConfigId","EstablishedBySource","CreatedAt")
                VALUES ({0}, {1}, {2}, {3}, 1, {4}, now(), {5}, 'teste sintético', now());
                """, Guid.NewGuid(), tenant, asset, DirA, value, Guid.NewGuid());
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }
}
