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
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AppPosture = AegisScore.Application.Abstractions.DevicePostureSnapshot;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Reconciliação + leitura da postura de dispositivos (SQLite relacional).
/// Prova: substituição por DIMENSÃO, preservação independente quando uma dimensão falha, idempotência por
/// fingerprint, isolamento por tenant, rótulos/nulidade honestos da query e — o mais importante — que
/// sincronizar postura de dispositivos produz ZERO EvidenceSignal e ZERO TenantControlState (não toca o score).
/// </summary>
public sealed class DevicePosturePersistenceTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherTenant = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public DevicePosturePersistenceTests()
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

    // ---- fixtures --------------------------------------------------------------------------------

    private static DevicePolicyFact Policy(string id, DevicePolicyKind kind, DevicePolicyAssignmentState assignment) =>
        new(id, kind, $"Política {id}", "Windows", assignment,
            assignment == DevicePolicyAssignmentState.Assigned ? 1 : assignment == DevicePolicyAssignmentState.Unassigned ? 0 : null,
            null);

    private static DevicePostureConfigurationDimension Configuration(
        DevicePostureDimensionState state,
        DevicePostureDimensionState assignmentState = DevicePostureDimensionState.Available,
        params DevicePolicyFact[] policies) =>
        new(state, DateTimeOffset.UtcNow, policies, assignmentState, 0, null);

    private static DeviceGroupFact Group(
        string os, DeviceComplianceBucket compliance, DeviceEncryptionBucket encryption,
        DeviceActivityBucket activity, int count) =>
        new(os, compliance, encryption, activity, count);

    private static DevicePostureDeviceDimension Devices(
        DevicePostureDimensionState state, int withDirectoryId = 0, params DeviceGroupFact[] groups) =>
        new(state, DateTimeOffset.UtcNow, groups, groups.Sum(g => g.DeviceCount), 30, withDirectoryId, 0, null);

    private static AppPosture Posture(
        DevicePostureConfigurationDimension configuration, DevicePostureDeviceDimension devices) =>
        new("Microsoft Intune", configuration, devices);

    private static AppPosture FullyAvailable() => Posture(
        Configuration(
            DevicePostureDimensionState.Available, DevicePostureDimensionState.Available,
            Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned),
            Policy("pol-2", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Unassigned),
            Policy("cfg-1", DevicePolicyKind.DeviceConfiguration, DevicePolicyAssignmentState.Assigned)),
        Devices(
            DevicePostureDimensionState.Available, withDirectoryId: 8,
            Group("Windows", DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 7),
            Group("Windows", DeviceComplianceBucket.Noncompliant, DeviceEncryptionBucket.NotEncrypted, DeviceActivityBucket.Stale, 3)));

    private async Task<DevicePostureSyncResult> Reconcile(Guid tenant, Guid connector, AppPosture posture)
    {
        await EnsureConnector(tenant, connector);
        await using var db = NewContext(tenant);
        return await new DevicePostureReconciler(db).ReconcileAsync(connector, posture, CancellationToken.None);
    }

    private async Task EnsureConnector(Guid tenant, Guid id)
    {
        await using var db = NewContext(tenant);
        if (await db.Connectors.AnyAsync(c => c.Id == id)) return;
        db.Connectors.Add(new ConnectorConfig
        {
            Id = id,
            TenantId = tenant,
            Provider = ConnectorProvider.Microsoft,
            Capability = ConnectorCapability.ConfigAnalyzer,
            DisplayName = "Microsoft Intune · Configuração e Conformidade",
            AuthType = ConnectorAuthType.OAuthClientCredentials,
            Enabled = true,
            EncryptedSettings = "{\"clientSecret\":\"s\"}",
        });
        await db.SaveChangesAsync();
    }

    private async Task<DevicePostureViewDto> Read(Guid tenant)
    {
        await using var db = NewContext(tenant);
        return await new DevicePostureQuery(db, new SystemTenantContext(tenant)).GetAsync();
    }

    // ---- 1) substituição atômica e idempotência ---------------------------------------------------

    [Fact]
    public async Task Reconcile_StoresBothDimensions_AndDerivesTotalsFromGroups()
    {
        var connector = Guid.NewGuid();
        var result = await Reconcile(Tenant, connector, FullyAvailable());

        result.ConfigurationState.Should().Be(DevicePostureDimensionState.Available);
        result.DeviceState.Should().Be(DevicePostureDimensionState.Available);
        result.PoliciesStored.Should().Be(3);
        result.DeviceGroupsStored.Should().Be(2);
        result.TotalDevices.Should().Be(10);

        await using var db = NewContext(Tenant);
        var snapshot = await db.DevicePostureSnapshots
            .Include(s => s.Policies).Include(s => s.DeviceGroups).SingleAsync();

        snapshot.CompliancePolicyCount.Should().Be(2);
        snapshot.DeviceConfigurationCount.Should().Be(1);
        snapshot.PoliciesAssigned.Should().Be(2);
        snapshot.PoliciesUnassigned.Should().Be(1);
        snapshot.CompliantDevices.Should().Be(7);
        snapshot.NoncompliantDevices.Should().Be(3);
        snapshot.EncryptedDevices.Should().Be(7);
        snapshot.StaleDevices.Should().Be(3);
        snapshot.DevicesWithDirectoryId.Should().Be(8);
        snapshot.DeviceGroups.Sum(g => g.DeviceCount).Should().Be(snapshot.TotalDevices);
    }

    [Fact]
    public async Task Reconcile_IsIdempotent_SameFactsDoNotDuplicateChildren()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());
        await Reconcile(Tenant, connector, FullyAvailable());

        await using var db = NewContext(Tenant);
        (await db.DevicePostureSnapshots.CountAsync()).Should().Be(1, "chave natural (tenant, conector)");
        (await db.DevicePosturePolicies.CountAsync()).Should().Be(3);
        (await db.DevicePostureDeviceGroups.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Reconcile_ReplacesChildren_WhenFactsChange()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        await Reconcile(Tenant, connector, Posture(
            Configuration(
                DevicePostureDimensionState.Available, DevicePostureDimensionState.Available,
                Policy("pol-9", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned)),
            Devices(
                DevicePostureDimensionState.Available,
                groups: Group("macOS", DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 4))));

        await using var db = NewContext(Tenant);
        var policies = await db.DevicePosturePolicies.Select(p => p.ExternalId).ToListAsync();
        policies.Should().BeEquivalentTo(new[] { "pol-9" }, "os filhos anteriores foram substituídos, não somados");
        (await db.DevicePostureDeviceGroups.CountAsync()).Should().Be(1);
        (await db.DevicePostureSnapshots.SingleAsync()).TotalDevices.Should().Be(4);
    }

    // ---- 2) preservação INDEPENDENTE por dimensão --------------------------------------------------

    [Theory]
    [InlineData(DevicePostureDimensionState.NotAuthorized)]
    [InlineData(DevicePostureDimensionState.NotLicensed)]
    [InlineData(DevicePostureDimensionState.Unavailable)]
    public async Task Reconcile_DeviceFailure_PreservesDevices_AndKeepsPoliciesFresh(DevicePostureDimensionState failure)
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        var result = await Reconcile(Tenant, connector, Posture(
            Configuration(
                DevicePostureDimensionState.Available, DevicePostureDimensionState.Available,
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned)),
            DevicePostureDeviceDimension.Failed(failure, DateTimeOffset.UtcNow, "sem permissão", 30)));

        result.DevicesPreserved.Should().BeTrue();
        result.ConfigurationPreserved.Should().BeFalse();

        await using var db = NewContext(Tenant);
        var snapshot = await db.DevicePostureSnapshots
            .Include(s => s.Policies).Include(s => s.DeviceGroups).SingleAsync();

        snapshot.DeviceAttemptState.Should().Be(failure, "a tentativa falha é registrada honestamente");
        snapshot.DeviceState.Should().Be(DevicePostureDimensionState.Available, "os dados anteriores sobrevivem");
        snapshot.TotalDevices.Should().Be(10, "uma falha NUNCA substitui fatos válidos por zero");
        snapshot.DeviceGroups.Should().HaveCount(2);

        snapshot.Policies.Should().HaveCount(1, "a dimensão de políticas foi atualizada normalmente");
        snapshot.ConfigurationState.Should().Be(DevicePostureDimensionState.Available);
    }

    [Fact]
    public async Task Reconcile_ConfigurationFailure_PreservesPolicies_AndKeepsDevicesFresh()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        var result = await Reconcile(Tenant, connector, Posture(
            DevicePostureConfigurationDimension.Failed(
                DevicePostureDimensionState.NotAuthorized, DateTimeOffset.UtcNow, "sem permissão"),
            Devices(
                DevicePostureDimensionState.Available,
                groups: Group("iOS", DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Unknown, DeviceActivityBucket.Active, 2))));

        result.ConfigurationPreserved.Should().BeTrue();
        result.DevicesPreserved.Should().BeFalse();

        await using var db = NewContext(Tenant);
        var snapshot = await db.DevicePostureSnapshots
            .Include(s => s.Policies).Include(s => s.DeviceGroups).SingleAsync();

        snapshot.Policies.Should().HaveCount(3, "as políticas anteriores continuam íntegras");
        snapshot.CompliancePolicyCount.Should().Be(2);
        snapshot.ConfigurationAttemptState.Should().Be(DevicePostureDimensionState.NotAuthorized);
        snapshot.TotalDevices.Should().Be(2, "a dimensão de dispositivos foi atualizada normalmente");
    }

    [Fact]
    public async Task Reconcile_FirstCollectionFailingBothDimensions_StoresHonestPlaceholder()
    {
        var connector = Guid.NewGuid();
        var result = await Reconcile(Tenant, connector, Posture(
            DevicePostureConfigurationDimension.Failed(
                DevicePostureDimensionState.NotAuthorized, DateTimeOffset.UtcNow, "sem permissão"),
            DevicePostureDeviceDimension.Failed(
                DevicePostureDimensionState.NotAuthorized, DateTimeOffset.UtcNow, "sem permissão", 30)));

        result.PoliciesStored.Should().Be(0);
        result.DeviceGroupsStored.Should().Be(0);

        await using var db = NewContext(Tenant);
        var snapshot = await db.DevicePostureSnapshots.SingleAsync();
        snapshot.ConfigurationState.Should().Be(DevicePostureDimensionState.NeverCollected,
            "nunca se finge inventário — o estado ARMAZENADO segue 'nunca coletado'");
        snapshot.DeviceState.Should().Be(DevicePostureDimensionState.NeverCollected);
        snapshot.ConfigurationAttemptState.Should().Be(DevicePostureDimensionState.NotAuthorized);
    }

    [Fact]
    public async Task Reconcile_PartialWouldDowngradeComplete_PreservesTheCompleteInventory()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        await Reconcile(Tenant, connector, Posture(
            Configuration(
                DevicePostureDimensionState.Partial, DevicePostureDimensionState.Partial,
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned)),
            Devices(
                DevicePostureDimensionState.Partial,
                groups: Group("Windows", DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 1))));

        await using var db = NewContext(Tenant);
        var snapshot = await db.DevicePostureSnapshots
            .Include(s => s.Policies).Include(s => s.DeviceGroups).SingleAsync();

        snapshot.ConfigurationState.Should().Be(DevicePostureDimensionState.Available);
        snapshot.Policies.Should().HaveCount(3, "um piso não rebaixa um inventário completo");
        snapshot.DeviceState.Should().Be(DevicePostureDimensionState.Available);
        snapshot.TotalDevices.Should().Be(10);
        snapshot.ConfigurationAttemptState.Should().Be(DevicePostureDimensionState.Partial,
            "a tentativa parcial é registrada, sem alterar os dados");
    }

    [Fact]
    public async Task Reconcile_FirstPartialCollection_StoresTheFloor_MarkedAsPartial()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Posture(
            Configuration(
                DevicePostureDimensionState.Partial, DevicePostureDimensionState.Partial,
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Unknown)),
            Devices(
                DevicePostureDimensionState.Partial,
                groups: Group("Windows", DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 5))));

        await using var db = NewContext(Tenant);
        var snapshot = await db.DevicePostureSnapshots.SingleAsync();
        snapshot.ConfigurationState.Should().Be(DevicePostureDimensionState.Partial, "nunca marcado como completo");
        snapshot.DeviceState.Should().Be(DevicePostureDimensionState.Partial);
        snapshot.TotalDevices.Should().Be(5);
    }

    // ---- 3) isolamento entre tenants ---------------------------------------------------------------

    [Fact]
    public async Task Reconcile_IsolatesTenants_NoLeakInEitherDirection()
    {
        var connectorA = Guid.NewGuid();
        var connectorB = Guid.NewGuid();
        await Reconcile(Tenant, connectorA, FullyAvailable());
        await Reconcile(OtherTenant, connectorB, Posture(
            Configuration(DevicePostureDimensionState.Available, DevicePostureDimensionState.Available,
                Policy("beta-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned)),
            Devices(DevicePostureDimensionState.Available,
                groups: Group("Linux", DeviceComplianceBucket.Compliant, DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 99))));

        var alfa = await Read(Tenant);
        var beta = await Read(OtherTenant);

        alfa.DeviceSummary.TotalDevices.Should().Be(10);
        alfa.Policies.Select(p => p.ExternalId).Should().NotContain("beta-1");
        beta.DeviceSummary.TotalDevices.Should().Be(99);
        beta.Policies.Select(p => p.ExternalId).Should().BeEquivalentTo(new[] { "beta-1" });

        await using var db = NewContext(Tenant);
        (await db.DevicePostureSnapshots.CountAsync()).Should().Be(1, "o query filter isola por tenant");
        (await db.DevicePostureDeviceGroups.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Cascade_OnConnectorDeletion_RemovesOnlyThatTenantsPosture()
    {
        var connectorA = Guid.NewGuid();
        var connectorB = Guid.NewGuid();
        await Reconcile(Tenant, connectorA, FullyAvailable());
        await Reconcile(OtherTenant, connectorB, FullyAvailable());

        await using (var db = NewContext(Tenant))
        {
            db.Connectors.Remove(await db.Connectors.SingleAsync(c => c.Id == connectorA));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(Tenant))
        {
            (await db.DevicePostureSnapshots.CountAsync()).Should().Be(0, "cascade removeu a postura do conector");
            (await db.DevicePosturePolicies.CountAsync()).Should().Be(0, "e, em cadeia, seus filhos");
        }
        await using (var db = NewContext(OtherTenant))
        {
            (await db.DevicePostureSnapshots.CountAsync()).Should().Be(1, "o outro tenant permanece intacto");
            (await db.DevicePosturePolicies.CountAsync()).Should().Be(3);
        }
    }

    // ---- 4) leitura HONESTA -------------------------------------------------------------------------

    [Fact]
    public async Task Query_WithoutConnector_ReportsNotConfigured_WithEveryNumberNull()
    {
        var view = await Read(Tenant);

        view.State.Should().Be(DevicePostureViewState.NotConfigured);
        view.DeviceSummary.TotalDevices.Should().BeNull();
        view.DeviceSummary.Noncompliant.Should().BeNull("indisponível NUNCA vira zero");
        view.ConfigurationSummary.TotalPolicies.Should().BeNull();
        view.AffectsScore.Should().BeFalse();
    }

    [Fact]
    public async Task Query_WithConnectorButNoSync_ReportsNeverSynced()
    {
        await EnsureConnector(Tenant, Guid.NewGuid());
        var view = await Read(Tenant);

        view.State.Should().Be(DevicePostureViewState.NeverSynced);
        view.DeviceSummary.TotalDevices.Should().BeNull();
    }

    [Fact]
    public async Task Query_WhenDevicesNotAuthorized_ReturnsNullCounts_NeverZero()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Posture(
            Configuration(
                DevicePostureDimensionState.Available, DevicePostureDimensionState.Available,
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned)),
            DevicePostureDeviceDimension.Failed(
                DevicePostureDimensionState.NotAuthorized, DateTimeOffset.UtcNow, "sem permissão", 30)));

        var view = await Read(Tenant);

        view.State.Should().Be(DevicePostureViewState.Data);
        view.Configuration.HasData.Should().BeTrue();
        view.ConfigurationSummary.TotalPolicies.Should().Be(1);

        view.Devices.HasData.Should().BeFalse();
        view.Devices.State.Should().Be(nameof(DevicePostureDimensionState.NotAuthorized));
        view.Devices.Label.Should().Be("Bloqueada por permissão");
        view.Devices.RequiredPermission.Should().Be("DeviceManagementManagedDevices.Read.All");
        view.Devices.ActionHint.Should().NotBeNullOrWhiteSpace("a tela mostra a ação objetiva para destravar");
        view.DeviceSummary.TotalDevices.Should().BeNull();
        view.DeviceSummary.Compliant.Should().BeNull();
        view.DeviceSummary.Noncompliant.Should().BeNull(
            "jamais dizer '0 dispositivos não conformes' sem ter coletado dispositivo algum");
        view.DeviceGroups.Should().BeEmpty();
    }

    [Fact]
    public async Task Query_WhenInventoryPreservedAfterFailure_MarksStale_ButKeepsNumbers()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());
        await Reconcile(Tenant, connector, Posture(
            Configuration(DevicePostureDimensionState.Available, DevicePostureDimensionState.Available,
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned)),
            DevicePostureDeviceDimension.Failed(
                DevicePostureDimensionState.Unavailable, DateTimeOffset.UtcNow, "graph indisponível", 30)));

        var view = await Read(Tenant);

        view.Devices.HasData.Should().BeTrue("o último inventário válido continua legível");
        view.Devices.IsStale.Should().BeTrue("a tela avisa que os números podem estar defasados");
        view.Devices.StoredState.Should().Be(nameof(DevicePostureDimensionState.Available));
        view.Devices.State.Should().Be(nameof(DevicePostureDimensionState.Unavailable));
        view.DeviceSummary.TotalDevices.Should().Be(10);
    }

    [Fact]
    public async Task Query_ReportsAssignmentSeparately_AndNeverCountsUnknownAsUnassigned()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, Posture(
            Configuration(
                DevicePostureDimensionState.Available, DevicePostureDimensionState.Unavailable,
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Unknown),
                Policy("pol-2", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Unknown)),
            Devices(DevicePostureDimensionState.Available)));

        var view = await Read(Tenant);

        view.Configuration.HasData.Should().BeTrue();
        view.Assignment.HasData.Should().BeFalse("a sub-dimensão de atribuição não foi comprovada");
        view.ConfigurationSummary.PoliciesUnassigned.Should().BeNull(
            "sem prova de atribuição, nenhuma política é declarada 'sem atribuição'");
        view.ConfigurationSummary.PoliciesAssignmentUnknown.Should().Be(2);
        view.Policies.Should().OnlyContain(p => p.AssignmentState == "Unknown");
        view.Policies.Should().OnlyContain(p => p.AssignmentLabel == "Atribuição desconhecida");
    }

    [Fact]
    public async Task Query_ReportsCorrelationGap_WithoutJoiningAssetsByName()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        var view = await Read(Tenant);

        view.Correlation.DeterministicCorrelationAvailable.Should().BeFalse();
        view.Correlation.DevicesWithDirectoryId.Should().Be(8, "a lacuna é registrada com o fato que a sustenta");
        view.Correlation.Explanation.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Query_ExposesGroupsThatSupportEveryRequiredFilter()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        var view = await Read(Tenant);

        view.DeviceGroups.Should().HaveCount(2);
        view.DeviceGroups.Should().OnlyContain(g =>
            g.OperatingSystem.Length > 0 && g.Compliance.Length > 0 && g.Encryption.Length > 0 && g.Activity.Length > 0);
        view.DeviceGroups.Sum(g => g.DeviceCount).Should().Be(view.DeviceSummary.TotalDevices);
        view.DeviceSummary.StaleThresholdDays.Should().Be(30);
    }

    [Fact]
    public async Task Query_NeverPromisesNistEvaluationOrScoreChange()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());

        var view = await Read(Tenant);

        view.AffectsScore.Should().BeFalse();
        view.ScoreDisclaimer.Should().Contain("não alteram o AEGIS Score");
        var text = string.Join(" ", new[]
        {
            view.ScoreDisclaimer, view.Correlation.Explanation,
            view.Configuration.ActionHint ?? "", view.Devices.ActionHint ?? "",
        });
        text.Should().NotContain("conforme com o NIST");
        text.Should().NotContain("Compliant");
    }

    // ---- 5) FRONTEIRA DE AUTORIDADE: zero score, zero evidência ------------------------------------

    [Fact]
    public async Task Reconcile_ProducesZeroEvidenceSignals_AndZeroControlStates()
    {
        var connector = Guid.NewGuid();
        await Reconcile(Tenant, connector, FullyAvailable());
        await Reconcile(Tenant, connector, FullyAvailable());

        await using var db = NewContext(Tenant);
        (await db.Signals.CountAsync()).Should().Be(0,
            "postura de dispositivos NUNCA vira EvidenceSignal");
        (await db.TenantControlStates.CountAsync()).Should().Be(0,
            "nenhum controle NIST é promovido pela existência de política ou dispositivo conforme");
        (await db.Evidence.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_NeverPromotesAnExistingControlState()
    {
        var connector = Guid.NewGuid();
        SeedMinimalFramework();
        SeedControlState("PR.AA-01", ControlStatus.NonCompliant, score: 0);

        await Reconcile(Tenant, connector, FullyAvailable());
        await Reconcile(Tenant, connector, FullyAvailable());

        await using var db = NewContext(Tenant);
        var state = await db.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant,
            "nem 3 políticas nem 7 dispositivos conformes promovem um controle a Compliant");
        state.CurrentScore.Should().Be(0, "postura de dispositivos nunca soma pontos");
        state.LastVerdictSource.Should().Be(VerdictSource.Documentary, "nenhum veredito telemétrico foi gravado");
    }

    /// <summary>Catálogo MÍNIMO (PR.AA-01) só para provar que um estado EXISTENTE não é tocado pela reconciliação.</summary>
    private void SeedMinimalFramework()
    {
        using var db = NewContext(null);
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var pr = new NistFunction { FrameworkVersionId = fv.Id, Code = "PR", Name = "Protect" };
        var cat = new NistCategory { FunctionId = pr.Id, Code = "PR.AA", Name = "PR.AA" };
        cat.Subcategories.Add(new NistSubcategory
        {
            CategoryId = cat.Id, Code = "PR.AA-01", Description = "PR.AA-01", MaxScorePoints = 10,
        });
        pr.Categories.Add(cat);
        fv.Functions.Add(pr);
        db.FrameworkVersions.Add(fv);
        db.SaveChanges();
    }

    private void SeedControlState(string code, ControlStatus status, int score)
    {
        using var db = NewContext(Tenant);
        var subId = db.Subcategories.IgnoreQueryFilters().Single(s => s.Code == code).Id;
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = status, CurrentScore = score,
        });
        db.SaveChanges();
    }
}
