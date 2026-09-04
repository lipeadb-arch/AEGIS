using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft;
using AegisScore.Connectors.Microsoft.Intune;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using AegisScore.Infrastructure.Tests.Connectors;   // FakeRegistry, FakeProtector, IngestionTestData
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppPosture = AegisScore.Application.Abstractions.DevicePostureSnapshot;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Invariante de arquitetura/regressão da FRONTEIRA DE AUTORIDADE: a postura de
/// dispositivos, exercitada pela CADEIA COMPLETA do <see cref="EvidenceIngestionExecutor"/> (não só o reconciler
/// isolado), NUNCA cria <see cref="EvidenceSignal"/> nem escreve em <see cref="TenantControlState"/> — política
/// existente, política atribuída e dispositivo conforme não concedem nem removem pontos do AEGIS Score.
///
/// Cobre também a saúde POR DIMENSÃO (sem a permissão de dispositivos o conector fica Degraded, nunca Healthy nem
/// Failed) e o registro do adaptador no par Microsoft/ConfigAnalyzer.
/// </summary>
public sealed class ScoreGuardDevicePostureTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private const string Source = "Microsoft Intune";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public ScoreGuardDevicePostureTests()
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
            TenantId = Tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.ConfigAnalyzer,
            DisplayName = "Microsoft Intune · Configuração e Conformidade",
            AuthType = ConnectorAuthType.OAuthClientCredentials, Enabled = true,
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private EvidenceIngestionExecutor MakeExecutor(IConnectorRegistry registry) => new(
        _options, new NistSignalMapper(NewContext(null)), new FakeProtector(), registry,
        NullLogger<EvidenceIngestionExecutor>.Instance, NullLogger<ControlStateWriter>.Instance);

    private static AppPosture Posture(
        DevicePostureDimensionState configurationState, DevicePostureDimensionState deviceState) => new(
        Source,
        configurationState is DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial
            ? new DevicePostureConfigurationDimension(
                configurationState, DateTimeOffset.UtcNow,
                new[]
                {
                    new DevicePolicyFact("pol-1", DevicePolicyKind.CompliancePolicy, "Baseline", "Windows",
                        DevicePolicyAssignmentState.Assigned, 1, null),
                },
                DevicePostureDimensionState.Available, 0, null)
            : DevicePostureConfigurationDimension.Failed(configurationState, DateTimeOffset.UtcNow, "sem permissão"),
        deviceState is DevicePostureDimensionState.Available or DevicePostureDimensionState.Partial
            ? new DevicePostureDeviceDimension(
                deviceState, DateTimeOffset.UtcNow,
                new[]
                {
                    new DeviceGroupFact("Windows", DeviceComplianceBucket.Compliant,
                        DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 25),
                },
                25, 30, 25, 0, null)
            : DevicePostureDeviceDimension.Failed(deviceState, DateTimeOffset.UtcNow, "sem permissão", 30));

    // ---- 1) FRONTEIRA DE AUTORIDADE ---------------------------------------------------------------

    [Fact]
    public async Task Pull_Executor_ReconcilesDevicePosture_NeverCreatesEvidenceSignalOrControlState()
    {
        var connectorId = SeedConnector();
        // O caso MAIS "convidativo" a virar score por engano: política atribuída + 25 dispositivos 100% conformes,
        // criptografados e sincronizados. A invariante deve segurar mesmo aqui.
        var connector = new FakeDevicePostureConnector(
            Posture(DevicePostureDimensionState.Available, DevicePostureDimensionState.Available));
        var exec = MakeExecutor(new FakeRegistry(connector));

        PullIngestionResult? result;
        await using (var read = NewContext(Tenant))
            result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        result!.DevicePosture.Should().NotBeNull();
        result.DevicePosture!.PoliciesStored.Should().Be(1);
        result.DevicePosture.TotalDevices.Should().Be(25);
        result.Status.Should().Be(ConnectorStatus.Healthy, "as DUAS dimensões vieram completas");

        await using var assert = NewContext(Tenant);
        (await assert.Signals.CountAsync()).Should().Be(0,
            "25 dispositivos conformes NÃO geram EvidenceSignal");
        (await assert.TenantControlStates.CountAsync()).Should().Be(0,
            "postura de dispositivos NUNCA toca o ledger/AEGIS Score");
        (await assert.DevicePostureSnapshots.CountAsync()).Should().Be(1,
            "mas o fato operacional É persistido normalmente");
        (await assert.DevicePostureDeviceGroups.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Pull_Executor_RepeatedSync_StaysAtZeroSignalsAndZeroControlStates()
    {
        var connectorId = SeedConnector();
        var connector = new FakeDevicePostureConnector(
            Posture(DevicePostureDimensionState.Available, DevicePostureDimensionState.Available));
        var exec = MakeExecutor(new FakeRegistry(connector));

        for (var i = 0; i < 3; i++)
            await using (var read = NewContext(Tenant))
                await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        await using var assert = NewContext(Tenant);
        (await assert.Signals.CountAsync()).Should().Be(0);
        (await assert.TenantControlStates.CountAsync()).Should().Be(0);
        (await assert.DevicePostureSnapshots.CountAsync()).Should().Be(1, "idempotente pela chave natural");
        (await assert.DevicePosturePolicies.CountAsync()).Should().Be(1, "sem acúmulo de filhos");
    }

    // ---- 2) saúde POR DIMENSÃO ---------------------------------------------------------------------

    [Theory]
    [InlineData(DevicePostureDimensionState.NotAuthorized)]
    [InlineData(DevicePostureDimensionState.NotLicensed)]
    [InlineData(DevicePostureDimensionState.Unavailable)]
    [InlineData(DevicePostureDimensionState.Partial)]
    public async Task Pull_Executor_DeviceDimensionDegraded_DegradesConnector_ButNeverFails(
        DevicePostureDimensionState deviceState)
    {
        var connectorId = SeedConnector();
        var connector = new FakeDevicePostureConnector(
            Posture(DevicePostureDimensionState.Available, deviceState));
        var exec = MakeExecutor(new FakeRegistry(connector));

        PullIngestionResult? result;
        await using (var read = NewContext(Tenant))
            result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        result!.Status.Should().Be(ConnectorStatus.Degraded,
            "uma dimensão bloqueada nunca deixa o conector parecer plenamente operacional");

        await using var assert = NewContext(Tenant);
        var saved = await assert.Connectors.SingleAsync(c => c.Id == connectorId);
        saved.LastStatus.Should().Be(ConnectorStatus.Degraded, "e nunca Failed — a outra dimensão segue válida");
        (await assert.DevicePosturePolicies.CountAsync()).Should().Be(1,
            "a dimensão de políticas continua sendo persistida normalmente");
    }

    [Fact]
    public async Task Pull_Executor_WithoutManagedDevicesPermission_ReportsNullTotal_NotZero()
    {
        var connectorId = SeedConnector();
        var connector = new FakeDevicePostureConnector(
            Posture(DevicePostureDimensionState.Available, DevicePostureDimensionState.NotAuthorized));
        var exec = MakeExecutor(new FakeRegistry(connector));

        PullIngestionResult? result;
        await using (var read = NewContext(Tenant))
            result = await exec.CollectPullAsync(await read.Connectors.SingleAsync(c => c.Id == connectorId), default);

        result!.DevicePosture!.DeviceState.Should().Be(DevicePostureDimensionState.NotAuthorized);
        result.DevicePosture.DevicesPreserved.Should().BeTrue("nada é sobrescrito por uma dimensão bloqueada");
        result.DevicePosture.ConfigurationState.Should().Be(DevicePostureDimensionState.Available);

        await using var assert = NewContext(Tenant);
        var snapshot = await assert.DevicePostureSnapshots.SingleAsync();
        snapshot.DeviceState.Should().Be(DevicePostureDimensionState.NeverCollected,
            "o estado ARMAZENADO segue 'nunca coletado' — a leitura devolve null, não zero");
        (await assert.DevicePostureDeviceGroups.CountAsync()).Should().Be(0);
    }

    // ---- 3) registro no registry e lifetime --------------------------------------------------------

    [Fact]
    public void Registry_ResolvesMicrosoftConfigAnalyzer_AsTheIntuneConnector_Scoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftConnectors();
        services.AddScoped<IConnectorRegistry, ConnectorRegistry>();
        services.AddSingleton<IConnectorSecretProtector, PassthroughProtector>();
        services.AddSingleton(TimeProvider.System);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        IEvidenceConnector first;
        using (var scope = provider.CreateScope())
        {
            var resolved = scope.ServiceProvider.GetRequiredService<IConnectorRegistry>()
                .Resolve(ConnectorProvider.Microsoft, ConnectorCapability.ConfigAnalyzer);
            resolved.Should().BeOfType<MicrosoftIntuneDevicePostureConnector>();
            resolved.Should().BeAssignableTo<IDevicePostureCollector>();
            first = resolved!;
        }
        using (var scope = provider.CreateScope())
        {
            var resolved = scope.ServiceProvider.GetRequiredService<IConnectorRegistry>()
                .Resolve(ConnectorProvider.Microsoft, ConnectorCapability.ConfigAnalyzer);
            resolved.Should().NotBeSameAs(first, "scoped — o typed HttpClient nunca é capturado no root provider");
        }
    }

    [Fact]
    public void Registry_OtherMicrosoftConnectors_RemainRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicrosoftConnectors();
        services.AddScoped<IConnectorRegistry, ConnectorRegistry>();
        services.AddSingleton<IConnectorSecretProtector, PassthroughProtector>();
        services.AddSingleton(TimeProvider.System);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IConnectorRegistry>();

        registry.Resolve(ConnectorProvider.Microsoft, ConnectorCapability.SecureScore).Should().NotBeNull();
        registry.Resolve(ConnectorProvider.Microsoft, ConnectorCapability.VulnerabilityScanner).Should().NotBeNull();
        registry.Resolve(ConnectorProvider.MicrosoftSentinel, ConnectorCapability.Siem).Should().NotBeNull();
        // A capacidade ConfigAnalyzer NÃO é sequestrada de outros providers: o par provider+capability é a chave.
        registry.Resolve(ConnectorProvider.Aws, ConnectorCapability.ConfigAnalyzer).Should().BeNull();
    }

    // ---- duplo de teste ----------------------------------------------------------------------------

    private sealed class PassthroughProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    /// <summary>Conector que só produz postura de dispositivos — nenhum sinal, como o adaptador real do Intune.</summary>
    private sealed class FakeDevicePostureConnector : IEvidenceConnector, IDevicePostureCollector
    {
        private readonly AppPosture _posture;
        public FakeDevicePostureConnector(AppPosture posture) => _posture = posture;

        public ConnectorProvider Provider => ConnectorProvider.Microsoft;
        public ConnectorCapability Capability => ConnectorCapability.ConfigAnalyzer;

        public Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(new ConnectorHealth(ConnectorStatus.Degraded, "teste"));

#pragma warning disable CS1998
        public async IAsyncEnumerable<EvidenceSignal> CollectAsync(
            ConnectorConfig config, [EnumeratorCancellation] CancellationToken ct)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<AppPosture> CollectDevicePostureAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(_posture);
    }
}
