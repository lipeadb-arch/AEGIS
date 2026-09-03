using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Reconciliação de inventário de software (SQLite): upsert idempotente de
/// produto/binding/instalação, resolução SOMENTE em coleta completa, preservação honesta em falha/parcial,
/// recompute agregado do produto, isolamento tenant e a invariante de que NADA toca o AEGIS Score.
/// </summary>
public sealed class SoftwareInventoryReconcilerTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Source = "Microsoft Defender Vulnerability Management";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _connA;
    private Guid _assetM1;

    public SoftwareInventoryReconcilerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = Tenant, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.SaveChanges();
        _connA = SeedConnector("Defender A");
        _assetM1 = SeedAssetWithBinding(_connA, "m1");
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private Guid SeedConnector(string name)
    {
        using var db = NewContext(Tenant);
        var cfg = new ConnectorConfig
        {
            TenantId = Tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.VulnerabilityScanner,
            DisplayName = name, AuthType = ConnectorAuthType.OAuthClientCredentials, Enabled = true,
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    /// <summary>Simula o que a dimensão de vulnerabilidades JÁ normalizou nesta sincronização (mesmo conector).</summary>
    private Guid SeedAssetWithBinding(Guid connectorId, string machineId)
    {
        using var db = NewContext(Tenant);
        var asset = new Asset
        {
            TenantId = Tenant, Name = machineId, Category = AssetCategory.Hardware,
            DiscoverySource = AssetDiscoverySource.Connector, IsActive = true, Criticality = 2,
        };
        db.Assets.Add(asset);
        db.AssetSourceBindings.Add(new AssetSourceBinding
        {
            TenantId = Tenant, AssetId = asset.Id, ConnectorConfigId = connectorId, ExternalId = machineId, IsActive = true,
        });
        db.SaveChanges();
        return asset.Id;
    }

    private async Task<SoftwareInventorySyncResult> Reconcile(Guid connectorId, SoftwareInventoryCollection c)
    {
        await using var db = NewContext(Tenant);
        return await new SoftwareInventoryReconciler(db).ReconcileAsync(connectorId, c, CancellationToken.None);
    }

    private static SoftwareProductFact Product(
        string id, string vendor = "microsoft", string name = "edge", int weaknesses = 0,
        bool publicExploit = false, bool activeAlert = false, int exposedMachines = 0, double? impact = 1.0) =>
        new(id, vendor, name, weaknesses, publicExploit, activeAlert, exposedMachines, impact);

    private static MachineSoftwareInstallation Install(string machineId, string vendor = "microsoft", string name = "edge", string? version = "1.0") =>
        new(machineId, vendor, name, version);

    private static SoftwareInventoryCollection Coll(
        SoftwareInventoryCollectionState state, SoftwareProductFact[] products, MachineSoftwareInstallation[] installs,
        int invalidProducts = 0, int invalidInstalls = 0) =>
        new(Source, state, DateTimeOffset.UtcNow, products, installs, invalidProducts, invalidInstalls);

    [Fact]
    public async Task Reconcile_FirstCollection_CreatesProductBindingInstallation()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("microsoft-_-edge") }, new[] { Install("m1") }));

        await using var db = NewContext(Tenant);
        (await db.SoftwareProducts.CountAsync()).Should().Be(1);
        (await db.SoftwareProductSourceBindings.CountAsync()).Should().Be(1);
        (await db.SoftwareInstallations.CountAsync()).Should().Be(1);
        var install = await db.SoftwareInstallations.SingleAsync();
        install.LifecycleState.Should().Be(ObservationLifecycle.Open);
        install.AssetId.Should().Be(_assetM1);
        install.Version.Should().Be("1.0");
    }

    [Fact]
    public async Task Reconcile_Idempotent_NoDuplicates()
    {
        var c = Coll(SoftwareInventoryCollectionState.Available, new[] { Product("microsoft-_-edge") }, new[] { Install("m1") });
        await Reconcile(_connA, c);
        await Reconcile(_connA, c);
        await Reconcile(_connA, c);

        await using var db = NewContext(Tenant);
        (await db.SoftwareProducts.CountAsync()).Should().Be(1);
        (await db.SoftwareProductSourceBindings.CountAsync()).Should().Be(1);
        (await db.SoftwareInstallations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_MissingVersion_UsesEmptySentinel_NeverNull()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available,
            new[] { Product("microsoft-_-edge") }, new[] { Install("m1", version: null) }));

        await using var db = NewContext(Tenant);
        var install = await db.SoftwareInstallations.SingleAsync();
        install.Version.Should().Be("", "versão ausente é sentinela vazia — nunca NULL (preserva o índice único no Postgres)");
    }

    [Fact]
    public async Task Reconcile_NeverCreatesEvidenceSignalOrControlState()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("microsoft-_-edge") }, new[] { Install("m1") }));
        await using var db = NewContext(Tenant);
        (await db.Signals.CountAsync()).Should().Be(0, "inventário de software não vira EvidenceSignal");
        (await db.TenantControlStates.CountAsync()).Should().Be(0, "inventário de software não toca o ledger/score");
    }

    [Fact]
    public async Task Reconcile_CompleteCollection_ResolvesMissingInstallation()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("microsoft-_-edge") }, new[] { Install("m1") }));
        // Segunda coleta COMPLETA sem o produto: resolve por omissão.
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>()));

        await using var db = NewContext(Tenant);
        var install = await db.SoftwareInstallations.SingleAsync();
        install.LifecycleState.Should().Be(ObservationLifecycle.Resolved, "coleta COMPLETA sem o produto resolve por omissão");
        var binding = await db.SoftwareProductSourceBindings.SingleAsync();
        binding.IsActive.Should().BeFalse("binding da fonte desativado quando ausente numa coleta completa");
    }

    [Fact]
    public async Task Reconcile_PartialCollection_NeverResolvesByOmission()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("microsoft-_-edge") }, new[] { Install("m1") }));
        // Segunda coleta PARCIAL sem o produto: NÃO resolve (fail-closed).
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Partial, Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>()));

        await using var db = NewContext(Tenant);
        var install = await db.SoftwareInstallations.SingleAsync();
        install.LifecycleState.Should().Be(ObservationLifecycle.Open, "coleta PARCIAL nunca resolve por omissão");
    }

    [Fact]
    public async Task Reconcile_ReopensResolvedInstallation_WhenSeenAgain()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p1") }, new[] { Install("m1") }));
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>()));
        var r3 = await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p1") }, new[] { Install("m1") }));

        r3.InstallationsReopened.Should().Be(1);
        await using var db = NewContext(Tenant);
        (await db.SoftwareInstallations.SingleAsync()).LifecycleState.Should().Be(ObservationLifecycle.Open);
    }

    [Theory]
    [InlineData(SoftwareInventoryCollectionState.InsufficientPermission)]
    [InlineData(SoftwareInventoryCollectionState.Unsupported)]
    [InlineData(SoftwareInventoryCollectionState.Unavailable)]
    public async Task Reconcile_FailureState_PreservesPreviousValidData_OnlyRecordsAttempt(SoftwareInventoryCollectionState failure)
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p1", weaknesses: 5) }, new[] { Install("m1") }));

        var result = await Reconcile(_connA, Coll(failure, Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>()));

        result.WasComplete.Should().BeFalse();
        await using var db = NewContext(Tenant);
        (await db.SoftwareProducts.CountAsync()).Should().Be(1, "dados válidos anteriores são PRESERVADOS numa falha");
        (await db.SoftwareInstallations.SingleAsync()).LifecycleState.Should().Be(ObservationLifecycle.Open, "falha não resolve nada");
        var snapshot = await db.SoftwareInventorySnapshots.SingleAsync();
        snapshot.CollectionState.Should().Be(SoftwareInventoryCollectionState.Available, "o estado dos DADOS armazenados continua Available");
        snapshot.LastAttemptState.Should().Be(failure, "a tentativa MAIS RECENTE é registrada honestamente");
    }

    [Fact]
    public async Task Reconcile_NeverFabricatesEmptyValidCollection_OnUnavailable()
    {
        var result = await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Unavailable, Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>()));

        result.State.Should().Be(SoftwareInventoryCollectionState.Unavailable);
        await using var db = NewContext(Tenant);
        var snapshot = await db.SoftwareInventorySnapshots.SingleAsync();
        snapshot.CollectionState.Should().Be(SoftwareInventoryCollectionState.NeverCollected,
            "sem coleta válida anterior, o placeholder é honesto — nunca finge um inventário Available vazio");
    }

    [Fact]
    public async Task Reconcile_OrphanInstallation_NoBindingForMachine_IsSkippedNotCrashed()
    {
        var result = await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available,
            new[] { Product("p1") }, new[] { Install("machine-sem-binding") }));

        await using var db = NewContext(Tenant);
        (await db.SoftwareInstallations.CountAsync()).Should().Be(0, "sem AssetSourceBinding para a máquina — instalação órfã ignorada, não fabricada");
        result.State.Should().Be(SoftwareInventoryCollectionState.Available, "o reconciliador não recusa a coleta inteira por isso");
    }

    [Fact]
    public async Task Reconcile_ConsolidatesSameProductAcrossTwoSources_NoDuplicateProduct()
    {
        var connB = SeedConnector2();
        SeedAssetBindingForConnector(connB, "m1", _assetM1);

        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("microsoft-_-edge", vendor: "Microsoft", name: "Edge") }, new[] { Install("m1", vendor: "Microsoft", name: "Edge") }));
        await Reconcile(connB, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("some-other-id", vendor: "microsoft", name: "edge") }, new[] { Install("m1", vendor: "microsoft", name: "edge") }));

        await using var db = NewContext(Tenant);
        (await db.SoftwareProducts.CountAsync()).Should().Be(1, "mesma identidade natural (vendor+nome normalizados) consolida entre fontes");
        (await db.SoftwareProductSourceBindings.CountAsync()).Should().Be(2, "cada fonte mantém seu PRÓPRIO binding");
    }

    [Fact]
    public async Task Reconcile_OneSourceResolved_DoesNotCloseOtherSourceInstallation()
    {
        var connB = SeedConnector2();
        SeedAssetBindingForConnector(connB, "m1", _assetM1);

        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p-a", name: "prod") }, new[] { Install("m1", name: "prod") }));
        await Reconcile(connB, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p-b", name: "prod") }, new[] { Install("m1", name: "prod") }));

        // A não vê mais o produto — resolve SÓ a instalação de A.
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>()));

        await using var db = NewContext(Tenant);
        var installs = await db.SoftwareInstallations.ToListAsync();
        installs.Should().HaveCount(2);
        installs.Single(i => i.ConnectorConfigId == _connA).LifecycleState.Should().Be(ObservationLifecycle.Resolved);
        installs.Single(i => i.ConnectorConfigId == connB).LifecycleState.Should().Be(ObservationLifecycle.Open,
            "uma fonte nunca encerra a observação de outra fonte");
    }

    [Fact]
    public async Task Reconcile_RecomputesProductAggregates_FromActiveBindings()
    {
        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available,
            new[] { Product("p1", weaknesses: 3, publicExploit: true, exposedMachines: 5, impact: 2.5) }, new[] { Install("m1") }));

        await using var db = NewContext(Tenant);
        var product = await db.SoftwareProducts.SingleAsync();
        product.WeaknessesCount.Should().Be(3);
        product.HasPublicExploit.Should().BeTrue();
        product.ExposedMachinesCount.Should().Be(5);
        product.ImpactScore.Should().Be(2.5);
        product.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Reconcile_TenantIsolation_SeparateTenantsNeverCrossData()
    {
        var tenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        using (var db = NewContext(null))
        {
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
            db.SaveChanges();
        }
        Guid connB, assetB;
        using (var db = NewContext(tenantB))
        {
            var cfg = new ConnectorConfig { TenantId = tenantB, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.VulnerabilityScanner, DisplayName = "B", AuthType = ConnectorAuthType.OAuthClientCredentials, Enabled = true };
            db.Connectors.Add(cfg);
            db.SaveChanges();
            connB = cfg.Id;
            var asset = new Asset { TenantId = tenantB, Name = "m1", Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Connector, IsActive = true, Criticality = 1 };
            db.Assets.Add(asset);
            db.AssetSourceBindings.Add(new AssetSourceBinding { TenantId = tenantB, AssetId = asset.Id, ConnectorConfigId = connB, ExternalId = "m1", IsActive = true });
            db.SaveChanges();
            assetB = asset.Id;
        }

        await Reconcile(_connA, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p1") }, new[] { Install("m1") }));
        await using (var dbB = new AegisScoreDbContext(_options, new SystemTenantContext(tenantB)))
            await new SoftwareInventoryReconciler(dbB).ReconcileAsync(connB, Coll(SoftwareInventoryCollectionState.Available, new[] { Product("p1") }, new[] { Install("m1") }), CancellationToken.None);

        await using (var a = NewContext(Tenant))
            (await a.SoftwareProducts.CountAsync()).Should().Be(1, "Tenant A só vê o SEU produto");
        await using (var b = new AegisScoreDbContext(_options, new SystemTenantContext(tenantB)))
            (await b.SoftwareProducts.CountAsync()).Should().Be(1, "Tenant B só vê o SEU produto — sem vazamento cruzado");
    }

    // ---- Harness auxiliar (segunda fonte) -----------------------------------------------------------

    private Guid SeedConnector2()
    {
        using var db = NewContext(Tenant);
        var cfg = new ConnectorConfig
        {
            TenantId = Tenant, Provider = ConnectorProvider.Generic, Capability = ConnectorCapability.VulnerabilityScanner,
            DisplayName = "Scanner B", AuthType = ConnectorAuthType.ApiKey, Enabled = true,
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private void SeedAssetBindingForConnector(Guid connectorId, string machineId, Guid assetId)
    {
        using var db = NewContext(Tenant);
        db.AssetSourceBindings.Add(new AssetSourceBinding
        {
            TenantId = Tenant, AssetId = assetId, ConnectorConfigId = connectorId, ExternalId = machineId, IsActive = true,
        });
        db.SaveChanges();
    }
}

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Reconciliação em PostgreSQL REAL (gate <c>AEGIS_TEST_PG</c>): migration
/// aplicada, FKs compostas tenant-safe, upsert idempotente e resolução por coleta completa sobre o banco real.
/// Entra no job PostgreSQL do CI (nenhum teste PostgreSQL é pulado com o gate exigido).
/// </summary>
public sealed class SoftwareInventoryPostgresTests
{
    private const string Source = "Microsoft Defender Vulnerability Management";
    private readonly ITestOutputHelper _output;

    public SoftwareInventoryPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Migration_Applied_TenantSafeFKs_IdempotentLifecycle_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null)
        {
            _output.WriteLine("PULADO (local): AEGIS_TEST_PG ausente — sem evidência PostgreSQL real.");
            return;
        }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        Guid connectorId, assetId;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            var applied = await db.Database.GetAppliedMigrationsAsync();
            applied.Should().Contain("20260903152245_SoftwareInventory_MicrosoftCoverage01",
                "a migration desta entrega deve constar como aplicada no PostgreSQL real");
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var cfg = new ConnectorConfig { TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.VulnerabilityScanner, DisplayName = "A", AuthType = ConnectorAuthType.OAuthClientCredentials };
            db.Connectors.Add(cfg);
            await db.SaveChangesAsync();
            connectorId = cfg.Id;

            var asset = new Asset { TenantId = tenant, Name = "m1", Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Connector, IsActive = true, Criticality = 1 };
            db.Assets.Add(asset);
            db.AssetSourceBindings.Add(new AssetSourceBinding { TenantId = tenant, AssetId = asset.Id, ConnectorConfigId = connectorId, ExternalId = "m1", IsActive = true });
            await db.SaveChangesAsync();
            assetId = asset.Id;
        }

        SoftwareInventoryCollection Coll(SoftwareInventoryCollectionState state, bool withProduct) => new(
            Source, state, DateTimeOffset.UtcNow,
            withProduct ? new[] { new SoftwareProductFact("microsoft-_-edge", "microsoft", "edge", 2, true, false, 1, 1.5) } : Array.Empty<SoftwareProductFact>(),
            withProduct ? new[] { new MachineSoftwareInstallation("m1", "microsoft", "edge", "120.0") } : Array.Empty<MachineSoftwareInstallation>(),
            0, 0);

        async Task<SoftwareInventorySyncResult> Recon(SoftwareInventoryCollection c)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            return await new SoftwareInventoryReconciler(db).ReconcileAsync(connectorId, c, default);
        }

        await Recon(Coll(SoftwareInventoryCollectionState.Available, true));
        await Recon(Coll(SoftwareInventoryCollectionState.Available, true));   // idempotente

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.SoftwareProducts.CountAsync()).Should().Be(1);
            (await db.SoftwareProductSourceBindings.CountAsync()).Should().Be(1);
            var install = await db.SoftwareInstallations.SingleAsync();
            install.AssetId.Should().Be(assetId);
            install.LifecycleState.Should().Be(ObservationLifecycle.Open);
        }

        // FK composta tenant-safe: um SoftwareInstallation apontando para Asset de OUTRO tenant é recusado.
        var otherTenant = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = otherTenant, Name = "Other", Slug = "other-" + otherTenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        Guid otherAsset;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(otherTenant)))
        {
            var asset = new Asset { TenantId = otherTenant, Name = "outro", Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Connector, IsActive = true, Criticality = 1 };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            otherAsset = asset.Id;
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var productId = await db.SoftwareProducts.Select(p => p.Id).SingleAsync();
            db.SoftwareInstallations.Add(new SoftwareInstallation
            {
                TenantId = tenant, SoftwareProductId = productId, AssetId = otherAsset, ConnectorConfigId = connectorId, Version = "x",
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("FK composta (AssetId, TenantId) recusa referência a ativo de outro tenant");
        }

        // Coleta COMPLETA sem o produto → resolve por omissão (lifecycle real em Postgres).
        await Recon(Coll(SoftwareInventoryCollectionState.Available, false));
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.SoftwareInstallations.SingleAsync()).LifecycleState.Should().Be(ObservationLifecycle.Resolved);
            (await db.SoftwareProductSourceBindings.SingleAsync()).IsActive.Should().BeFalse();
        }

        // Falha (sem permissão) preserva os dados já resolvidos — não reescreve nem lança.
        var failResult = await Recon(Coll(SoftwareInventoryCollectionState.InsufficientPermission, false));
        failResult.WasComplete.Should().BeFalse();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshot = await db.SoftwareInventorySnapshots.SingleAsync();
            snapshot.LastAttemptState.Should().Be(SoftwareInventoryCollectionState.InsufficientPermission);
            snapshot.CollectionState.Should().Be(SoftwareInventoryCollectionState.Available, "dados armazenados preservados apesar da falha");
        }

        // Zero EvidenceSignal/TenantControlState em todo o fluxo.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.Signals.CountAsync()).Should().Be(0);
            (await db.TenantControlStates.CountAsync()).Should().Be(0);
        }
    }
}
