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
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Queries;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Leitura do inventário de software (SQLite): isolamento por tenant, filtros,
/// ordenação determinística, paginação no banco, never-collected × coletado-sem-achados, ausência de PII/payload
/// bruto na projeção, e expansão paginada de ativos relacionados a um produto.
/// </summary>
public sealed class SoftwareInventoryQueryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Source = "Microsoft Defender Vulnerability Management";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public SoftwareInventoryQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));
    private SoftwareInventoryQuery Query(Guid tenant) => new(NewContext(tenant), new SystemTenantContext(tenant));

    private Guid SeedConnector(Guid tenant, string name = "Defender")
    {
        using var db = NewContext(tenant);
        var cfg = new ConnectorConfig
        {
            TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.VulnerabilityScanner,
            DisplayName = name, AuthType = ConnectorAuthType.OAuthClientCredentials, Enabled = true,
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private Guid SeedAssetWithBinding(Guid tenant, Guid connectorId, string machineId, int criticality = 1)
    {
        using var db = NewContext(tenant);
        var asset = new Asset { TenantId = tenant, Name = machineId, Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Connector, IsActive = true, Criticality = criticality };
        db.Assets.Add(asset);
        db.AssetSourceBindings.Add(new AssetSourceBinding { TenantId = tenant, AssetId = asset.Id, ConnectorConfigId = connectorId, ExternalId = machineId, IsActive = true });
        db.SaveChanges();
        return asset.Id;
    }

    private async Task ReconcileAsync(Guid tenant, Guid connectorId, SoftwareInventoryCollection c)
    {
        await using var db = NewContext(tenant);
        await new SoftwareInventoryReconciler(db).ReconcileAsync(connectorId, c, CancellationToken.None);
    }

    private static SoftwareInventoryCollection Coll(params (string machine, string productId, string vendor, string name, int weaknesses, bool pubExploit, bool alert)[] rows)
    {
        var products = rows.Select(r => r.productId).Distinct()
            .Select(pid => { var r = rows.First(x => x.productId == pid); return new SoftwareProductFact(pid, r.vendor, r.name, r.weaknesses, r.pubExploit, r.alert, 1, 1.0); })
            .ToArray();
        var installs = rows.Select(r => new MachineSoftwareInstallation(r.machine, r.vendor, r.name, "1.0")).ToArray();
        return new SoftwareInventoryCollection(Source, SoftwareInventoryCollectionState.Available, DateTimeOffset.UtcNow, products, installs, 0, 0);
    }

    [Fact]
    public async Task Query_IsolatesTenants()
    {
        var connA = SeedConnector(TenantA);
        SeedAssetWithBinding(TenantA, connA, "m1");
        await ReconcileAsync(TenantA, connA, Coll(("m1", "p-a", "v", "produto-a", 0, false, false)));

        var connB = SeedConnector(TenantB);
        SeedAssetWithBinding(TenantB, connB, "m1");
        await ReconcileAsync(TenantB, connB, Coll(("m1", "p-b", "v", "produto-b", 0, false, false)));

        var ra = await Query(TenantA).GetAsync(new SoftwareInventoryFilter());
        ra.Items.Should().ContainSingle().Which.Name.Should().Be("produto-a");
        var rb = await Query(TenantB).GetAsync(new SoftwareInventoryFilter());
        rb.Items.Should().ContainSingle().Which.Name.Should().Be("produto-b");
    }

    [Fact]
    public async Task Query_NeverCollected_DistinctFromCollectedWithoutFindings()
    {
        SeedConnector(TenantA);   // conector existe, mas NUNCA sincronizou a dimensão de software
        var neverCollected = await Query(TenantA).GetAsync(new SoftwareInventoryFilter());
        neverCollected.Summary.NeverCollected.Should().BeTrue();
        neverCollected.Summary.LastCollectedAt.Should().BeNull();

        var connB = SeedConnector(TenantB);
        await ReconcileAsync(TenantB, connB, new SoftwareInventoryCollection(
            Source, SoftwareInventoryCollectionState.Available, DateTimeOffset.UtcNow,
            Array.Empty<SoftwareProductFact>(), Array.Empty<MachineSoftwareInstallation>(), 0, 0));
        var collectedEmpty = await Query(TenantB).GetAsync(new SoftwareInventoryFilter());
        collectedEmpty.Summary.NeverCollected.Should().BeFalse("coletado com zero achados é DISTINTO de nunca coletado");
        collectedEmpty.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_FiltersByPublicExploitAndWeaknesses()
    {
        var conn = SeedConnector(TenantA);
        SeedAssetWithBinding(TenantA, conn, "m1");
        await ReconcileAsync(TenantA, conn, Coll(
            ("m1", "p-clean", "v", "limpo", 0, false, false),
            ("m1", "p-exploit", "v", "exploitado", 0, true, false),
            ("m1", "p-weak", "v", "fraco", 3, false, false)));

        var exploitOnly = await Query(TenantA).GetAsync(new SoftwareInventoryFilter(PublicExploitOnly: true));
        exploitOnly.Items.Should().ContainSingle().Which.Name.Should().Be("exploitado");

        var weakOnly = await Query(TenantA).GetAsync(new SoftwareInventoryFilter(Weakness: SoftwareWeaknessFilter.WithWeaknesses));
        weakOnly.Items.Should().ContainSingle().Which.Name.Should().Be("fraco");

        var all = await Query(TenantA).GetAsync(new SoftwareInventoryFilter());
        all.Items.Should().HaveCount(3);
        all.Summary.TotalProducts.Should().Be(3);
        all.Summary.ProductsWithPublicExploit.Should().Be(1);
        all.Summary.ProductsWithWeaknesses.Should().Be(1);
    }

    [Fact]
    public async Task Query_DeterministicOrdering_ExploitAndAlertFirst_ThenWeaknesses_ThenName()
    {
        var conn = SeedConnector(TenantA);
        SeedAssetWithBinding(TenantA, conn, "m1");
        await ReconcileAsync(TenantA, conn, Coll(
            ("m1", "p-z", "v", "zebra-limpo", 0, false, false),
            ("m1", "p-alert", "v", "alerta-ativo", 0, false, true),
            ("m1", "p-exploit", "v", "exploit-publico", 0, true, false)));

        var result = await Query(TenantA).GetAsync(new SoftwareInventoryFilter(State: SoftwareObservationStateFilter.All));
        var names = result.Items.Select(i => i.Name).ToList();
        names[0].Should().Be("exploit-publico", "exploit público vem primeiro");
        names[1].Should().Be("alerta-ativo", "alerta ativo vem em seguida");
        names[2].Should().Be("zebra-limpo", "sem exploit/alerta/fraqueza fica por último");
    }

    [Fact]
    public async Task Query_Pagination_IsStableAndBounded()
    {
        var conn = SeedConnector(TenantA);
        SeedAssetWithBinding(TenantA, conn, "m1");
        var rows = Enumerable.Range(0, 5)
            .Select(i => ($"m1", $"p{i}", "v", $"produto{i:D2}", 0, false, false))
            .ToArray();
        await ReconcileAsync(TenantA, conn, Coll(rows));

        var page1 = await Query(TenantA).GetAsync(new SoftwareInventoryFilter(Page: 1, PageSize: 2));
        var page2 = await Query(TenantA).GetAsync(new SoftwareInventoryFilter(Page: 2, PageSize: 2));
        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
        page1.Total.Should().Be(5);
        page1.Items.Select(i => i.Id).Should().NotIntersectWith(page2.Items.Select(i => i.Id), "sem repetição entre páginas");
    }

    [Fact]
    public async Task Query_ProjectedItem_NeverLeaksMachineIdOrRawPayload()
    {
        // O ASSET tem um nome legível (o que a UI mostra); o machineId TÉCNICO da fonte (ExternalId do binding) é
        // um identificador DISTINTO, usado só para correlação — nunca deve aparecer na projeção da API.
        const string technicalMachineId = "9eaf3a8b-5962-4e0e-b1af-9ec756664a9b";
        var conn = SeedConnector(TenantA);
        using (var db = NewContext(TenantA))
        {
            var asset = new Asset { TenantId = TenantA, Name = "host1.demo.example.com", Category = AssetCategory.Hardware, DiscoverySource = AssetDiscoverySource.Connector, IsActive = true, Criticality = 1 };
            db.Assets.Add(asset);
            db.AssetSourceBindings.Add(new AssetSourceBinding { TenantId = TenantA, AssetId = asset.Id, ConnectorConfigId = conn, ExternalId = technicalMachineId, IsActive = true });
            db.SaveChanges();
        }
        await ReconcileAsync(TenantA, conn, Coll((technicalMachineId, "p1", "v", "produto", 0, false, false)));

        var result = await Query(TenantA).GetAsync(new SoftwareInventoryFilter());
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        json.Should().Contain("host1.demo.example.com", "o nome legível do ativo aparece normalmente");
        json.Should().NotContain(technicalMachineId, "o machineId TÉCNICO da fonte nunca vaza na projeção — só o Asset consolidado");
    }

    [Fact]
    public async Task GetAssets_ExpandsRelatedAssets_Paginated()
    {
        var conn = SeedConnector(TenantA);
        SeedAssetWithBinding(TenantA, conn, "m1", criticality: 4);
        SeedAssetWithBinding(TenantA, conn, "m2", criticality: 1);
        await ReconcileAsync(TenantA, conn, Coll(
            ("m1", "p1", "v", "produto", 0, false, false),
            ("m2", "p1", "v", "produto", 0, false, false)));

        var list = await Query(TenantA).GetAsync(new SoftwareInventoryFilter());
        var productId = list.Items.Single().Id;

        var assets = await Query(TenantA).GetAssetsAsync(productId, 1, 25);
        assets.Total.Should().Be(2);
        assets.Items.Should().HaveCount(2);
        assets.Items[0].Criticality.Should().Be(4, "maior criticidade primeiro (ordenação determinística)");
    }
}
