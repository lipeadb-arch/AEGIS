using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using AegisScore.Infrastructure.Tests.Connectors;   // FakeRegistry, FakeProtector
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Invariante de arquitetura/regressão: o inventário de software, exercitado
/// pela CADEIA COMPLETA do <see cref="EvidenceIngestionExecutor"/> (não só o reconciler isolado), NUNCA cria
/// <see cref="EvidenceSignal"/> nem escreve em <see cref="TenantControlState"/> — presença/fraqueza/exploit/alerta
/// de software não concede nem remove pontos do AEGIS Score. Também comprova a saúde POR DIMENSÃO: uma coleta de
/// software incompleta rebaixa o conector para Degraded mesmo com vulnerabilidades Healthy (nunca "operacional"
/// silencioso quando só uma dimensão funcionou).
/// </summary>
public sealed class ScoreGuardSoftwareInventoryTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Source = "Microsoft Defender Vulnerability Management";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public ScoreGuardSoftwareInventoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        IngestionTestData.SeedFrameworkAndMappings(ctx);
        ctx.Tenants.Add(new Tenant { Id = Tenant, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private Guid SeedConnector()
    {
        using var db = NewContext(Tenant);
        var cfg = new ConnectorConfig
        {
            TenantId = Tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.VulnerabilityScanner,
            DisplayName = "Defender", AuthType = ConnectorAuthType.OAuthClientCredentials, Enabled = true,
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private EvidenceIngestionExecutor MakeExecutor(IConnectorRegistry registry) => new(
        _options, new NistSignalMapper(NewContext(null)), new FakeProtector(), registry,
        NullLogger<EvidenceIngestionExecutor>.Instance, NullLogger<ControlStateWriter>.Instance);

    private static VulnerabilityCollection Vulns(bool complete = true) => new(
        new[] { new VulnerabilityMachine("m1", "m1.demo.example.com", "Windows11", null) },
        Array.Empty<VulnerabilityCve>(), Array.Empty<MachineCveRelation>(), complete, 0, 0, 0, Source);

    private static SoftwareInventoryCollection Software(SoftwareInventoryCollectionState state, bool withProduct) => new(
        Source, state, DateTimeOffset.UtcNow,
        withProduct ? new[] { new SoftwareProductFact("microsoft-_-edge", "microsoft", "edge", 5, true, true, 3, 2.0) } : Array.Empty<SoftwareProductFact>(),
        withProduct ? new[] { new MachineSoftwareInstallation("m1", "microsoft", "edge", "120.0") } : Array.Empty<MachineSoftwareInstallation>(),
        0, 0);

    [Fact]
    public async Task Pull_Executor_ReconcilesSoftware_NeverCreatesEvidenceSignalOrControlState_EvenWithExploitAndAlert()
    {
        var connectorId = SeedConnector();
        // Produto com weaknesses=5, publicExploit=true, activeAlert=true — o CASO MAIS "convidativo" a virar score
        // por engano. A invariante deve segurar mesmo aqui.
        var connector = new FakeCombinedConnector(Vulns(), Software(SoftwareInventoryCollectionState.Available, withProduct: true));
        var exec = MakeExecutor(new FakeRegistry(connector));

        PullIngestionResult? result;
        await using (var read = NewContext(Tenant))
            result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        result!.SoftwareInventory.Should().NotBeNull();
        result.SoftwareInventory!.ProductsCreated.Should().Be(1);

        await using var assert = NewContext(Tenant);
        (await assert.Signals.CountAsync()).Should().Be(0, "produto com exploit público + alerta ativo NÃO gera EvidenceSignal");
        (await assert.TenantControlStates.CountAsync()).Should().Be(0, "inventário de software NUNCA toca o ledger/AEGIS Score");
        (await assert.SoftwareProducts.CountAsync()).Should().Be(1, "mas o fato operacional É persistido normalmente");
    }

    [Fact]
    public async Task Pull_Executor_SoftwareIncomplete_DegradesConnector_EvenWithVulnerabilitiesHealthy()
    {
        var connectorId = SeedConnector();
        var connector = new FakeCombinedConnector(Vulns(complete: true), Software(SoftwareInventoryCollectionState.InsufficientPermission, withProduct: false));
        var exec = MakeExecutor(new FakeRegistry(connector));

        await using (var read = NewContext(Tenant))
            await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        await using var assert = NewContext(Tenant);
        (await assert.Connectors.SingleAsync(c => c.Id == connectorId)).LastStatus.Should().Be(
            ConnectorStatus.Degraded,
            "vulnerabilidades completas isoladamente seriam Healthy, mas software indisponível rebaixa — nunca 'operacional' silencioso");
    }

    [Fact]
    public async Task Pull_Executor_SoftwareFailureAlone_DoesNotPreventVulnerabilityReconciliation()
    {
        var connectorId = SeedConnector();
        var connector = new FakeCombinedConnector(Vulns(complete: true), Software(SoftwareInventoryCollectionState.Unavailable, withProduct: false));
        var exec = MakeExecutor(new FakeRegistry(connector));

        PullIngestionResult? result;
        await using (var read = NewContext(Tenant))
            result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        result!.Vulnerabilities.Should().NotBeNull();
        result.Vulnerabilities!.WasComplete.Should().BeTrue("falha isolada de software não invalida a coleta de vulnerabilidades");
        await using var assert = NewContext(Tenant);
        (await assert.AssetSourceBindings.CountAsync()).Should().Be(1, "máquinas/bindings da dimensão de vulnerabilidades reconciliados normalmente");
    }

    /// <summary>Combina vulnerabilidades + software numa única aquisição — espelha MicrosoftDefenderVulnerabilityConnector.</summary>
    private sealed class FakeCombinedConnector : IEvidenceConnector, ICombinedVulnerabilityConnector
    {
        private readonly VulnerabilityCollection _vulns;
        private readonly SoftwareInventoryCollection _software;
        public FakeCombinedConnector(VulnerabilityCollection vulns, SoftwareInventoryCollection software)
        {
            _vulns = vulns;
            _software = software;
        }
        public ConnectorProvider Provider => ConnectorProvider.Microsoft;
        public ConnectorCapability Capability => ConnectorCapability.VulnerabilityScanner;
        public Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(new ConnectorHealth(ConnectorStatus.Healthy, null));
        public async IAsyncEnumerable<EvidenceSignal> CollectAsync(ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<VulnerabilityCollection> CollectVulnerabilitiesAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(_vulns);
        public Task<VulnerabilityAndSoftwareCollection> CollectVulnerabilitiesAndSoftwareAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(new VulnerabilityAndSoftwareCollection(_vulns, _software));
    }
}
