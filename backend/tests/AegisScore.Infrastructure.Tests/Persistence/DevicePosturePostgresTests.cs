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
using Microsoft.EntityFrameworkCore;
using Xunit;
using AppPosture = AegisScore.Application.Abstractions.DevicePostureSnapshot;
using DevicePostureSnapshot = AegisScore.Domain.DevicePostureSnapshot;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Migration, chave natural (upsert idempotente), substituição por DIMENSÃO,
/// preservação degradation-safe, isolamento por tenant e integridade referencial (FKs COMPOSTAS tenant-safe) da
/// postura de dispositivos em PostgreSQL 18 REAL (gate <c>AEGIS_TEST_PG</c>).
///
/// Prova que a migration desta entrega aplica de fato, que (Tenant, Conector) e as FKs compostas são invariantes
/// de BANCO (não apenas convenções do código), que uma dimensão que falha preserva a outra no PG real, e que
/// sincronizar postura de dispositivos não escreve NADA no ledger/score.
/// </summary>
public sealed class DevicePosturePostgresTests
{
    private const string Source = "Microsoft Intune";

    private static ConnectorConfig NewIntuneConnector(Guid tenant, Guid id) => new()
    {
        Id = id, TenantId = tenant, Provider = ConnectorProvider.Microsoft,
        Capability = ConnectorCapability.ConfigAnalyzer,
        DisplayName = "Microsoft Intune · Configuração e Conformidade",
        AuthType = ConnectorAuthType.OAuthClientCredentials,
        Enabled = true, EncryptedSettings = "{\"clientSecret\":\"s\"}",
    };

    private static DevicePolicyFact Policy(string id, DevicePolicyKind kind, DevicePolicyAssignmentState assignment) =>
        new(id, kind, $"Política {id}", "Windows", assignment,
            assignment == DevicePolicyAssignmentState.Assigned ? 1 : null, null);

    private static AppPosture FullyAvailable() => new(
        Source,
        new DevicePostureConfigurationDimension(
            DevicePostureDimensionState.Available, DateTimeOffset.UtcNow,
            new[]
            {
                Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned),
                Policy("pol-2", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Unassigned),
                Policy("cfg-1", DevicePolicyKind.DeviceConfiguration, DevicePolicyAssignmentState.Assigned),
            },
            DevicePostureDimensionState.Available, 0, null),
        new DevicePostureDeviceDimension(
            DevicePostureDimensionState.Available, DateTimeOffset.UtcNow,
            new[]
            {
                new DeviceGroupFact("Windows", DeviceComplianceBucket.Compliant,
                    DeviceEncryptionBucket.Encrypted, DeviceActivityBucket.Active, 7),
                new DeviceGroupFact("Windows", DeviceComplianceBucket.Noncompliant,
                    DeviceEncryptionBucket.NotEncrypted, DeviceActivityBucket.Stale, 3),
            },
            10, 30, 8, 0, null));

    private static AppPosture DevicesBlocked() => new(
        Source,
        new DevicePostureConfigurationDimension(
            DevicePostureDimensionState.Available, DateTimeOffset.UtcNow,
            new[] { Policy("pol-1", DevicePolicyKind.CompliancePolicy, DevicePolicyAssignmentState.Assigned) },
            DevicePostureDimensionState.Available, 0, null),
        DevicePostureDeviceDimension.Failed(
            DevicePostureDimensionState.NotAuthorized, DateTimeOffset.UtcNow,
            "Sem permissão para ler dispositivos gerenciados.", 30));

    private static async Task ReconcileAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connector, AppPosture posture)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        await new DevicePostureReconciler(db).ReconcileAsync(connector, posture, CancellationToken.None);
    }

    [Fact]
    public async Task Migration_NaturalKey_DimensionReplacement_And_Isolation_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var connector = Guid.NewGuid();
        var otherConnector = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().Contain(m => m.EndsWith("_DevicePosture_MicrosoftIntune"),
                    "a migration da postura de dispositivos deve constar como aplicada no PostgreSQL real");
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = other, Name = "O", Slug = "o-" + other.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(NewIntuneConnector(tenant, connector));
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            db.Connectors.Add(NewIntuneConnector(other, otherConnector));
            await db.SaveChangesAsync();
        }

        // 1) Primeira coleta completa: as duas dimensões gravadas, totais derivados dos grupos.
        await ReconcileAsync(opt, tenant, connector, FullyAvailable());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshot = await db.DevicePostureSnapshots
                .Include(s => s.Policies).Include(s => s.DeviceGroups).SingleAsync();
            snapshot.Policies.Should().HaveCount(3);
            snapshot.DeviceGroups.Should().HaveCount(2);
            snapshot.TotalDevices.Should().Be(10);
            snapshot.CompliantDevices.Should().Be(7);
            snapshot.NoncompliantDevices.Should().Be(3);
            snapshot.DevicesWithDirectoryId.Should().Be(8);
        }

        // 2) Upsert idempotente pela chave natural (Tenant, Conector) — nunca uma segunda linha.
        await ReconcileAsync(opt, tenant, connector, FullyAvailable());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.DevicePostureSnapshots.CountAsync()).Should().Be(1);
            (await db.DevicePosturePolicies.CountAsync()).Should().Be(3, "sem acúmulo de filhos");
            (await db.DevicePostureDeviceGroups.CountAsync()).Should().Be(2);
        }

        // 3) A dimensão de dispositivos falha por permissão: os dados anteriores dela SOBREVIVEM no PG real,
        //    e a de políticas é atualizada normalmente. Uma falha JAMAIS vira zero.
        await ReconcileAsync(opt, tenant, connector, DevicesBlocked());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshot = await db.DevicePostureSnapshots
                .Include(s => s.Policies).Include(s => s.DeviceGroups).SingleAsync();
            snapshot.DeviceAttemptState.Should().Be(DevicePostureDimensionState.NotAuthorized);
            snapshot.DeviceState.Should().Be(DevicePostureDimensionState.Available, "os dados anteriores ficam");
            snapshot.TotalDevices.Should().Be(10);
            snapshot.DeviceGroups.Should().HaveCount(2);
            snapshot.Policies.Should().HaveCount(1, "a dimensão de políticas foi substituída sozinha");
            snapshot.ConfigurationAttemptState.Should().Be(DevicePostureDimensionState.Available);
        }

        // 4) Isolamento: o outro tenant tem a própria fotografia e nenhuma linha vaza em qualquer direção.
        await ReconcileAsync(opt, other, otherConnector, FullyAvailable());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.DevicePostureSnapshots.CountAsync()).Should().Be(1);
            (await db.DevicePosturePolicies.CountAsync()).Should().Be(1);
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            (await db.DevicePostureSnapshots.CountAsync()).Should().Be(1);
            (await db.DevicePosturePolicies.CountAsync()).Should().Be(3);
            (await db.DevicePostureDeviceGroups.CountAsync()).Should().Be(2);
        }

        // 5) FRONTEIRA DE AUTORIDADE no PG real: nenhuma linha de score/evidência foi criada.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.Signals.CountAsync()).Should().Be(0);
            (await db.TenantControlStates.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task CompositeTenantSafeForeignKeys_RejectOrphansAndCrossTenant_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var connector = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = other, Name = "O", Slug = "o-" + other.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(NewIntuneConnector(tenant, connector));
            await db.SaveChangesAsync();
        }

        // (a) Snapshot apontando para um conector INEXISTENTE: o BANCO recusa (FK composta), não só o código.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.DevicePostureSnapshots.Add(new DevicePostureSnapshot
            {
                ConnectorConfigId = Guid.NewGuid(), Source = Source,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("FK composta (ConnectorConfigId, TenantId) fail-closed");
        }

        // (b) Snapshot de um tenant apontando para o conector de OUTRO tenant: recusado pela MESMA FK composta.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            db.DevicePostureSnapshots.Add(new DevicePostureSnapshot
            {
                ConnectorConfigId = connector, Source = Source,   // conector pertence a `tenant`
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("o banco recusa referência cross-tenant");
        }

        // (c) Cascade: excluir o conector remove a postura E os filhos, sem linha órfã.
        await ReconcileAsync(opt, tenant, connector, FullyAvailable());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Remove(await db.Connectors.SingleAsync(c => c.Id == connector));
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.DevicePostureSnapshots.CountAsync()).Should().Be(0);
            (await db.DevicePosturePolicies.CountAsync()).Should().Be(0);
            (await db.DevicePostureDeviceGroups.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task NaturalUniqueIndexes_AreDatabaseInvariants_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connector = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(NewIntuneConnector(tenant, connector));
            await db.SaveChangesAsync();
        }

        await ReconcileAsync(opt, tenant, connector, FullyAvailable());

        // (Tenant, Conector) é ÚNICO: uma segunda fotografia do mesmo conector é recusada pelo banco.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.DevicePostureSnapshots.Add(new DevicePostureSnapshot
            {
                ConnectorConfigId = connector, Source = Source,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("UX_DevicePostureSnapshot_Natural");
        }

        // A mesma política (família + id) não se repete no snapshot.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshotId = (await db.DevicePostureSnapshots.SingleAsync()).Id;
            db.DevicePosturePolicies.Add(new DevicePosturePolicy
            {
                DevicePostureSnapshotId = snapshotId, ExternalId = "pol-1",
                Kind = DevicePolicyKind.CompliancePolicy, DisplayName = "duplicata",
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("UX_DevicePosturePolicy_Natural");
        }

        // O mesmo recorte (SO × conformidade × criptografia × atividade) não se repete no snapshot.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshotId = (await db.DevicePostureSnapshots.SingleAsync()).Id;
            db.DevicePostureDeviceGroups.Add(new DevicePostureDeviceGroup
            {
                DevicePostureSnapshotId = snapshotId, OperatingSystem = "Windows",
                Compliance = DeviceComplianceBucket.Compliant,
                Encryption = DeviceEncryptionBucket.Encrypted,
                Activity = DeviceActivityBucket.Active,
                DeviceCount = 1,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("UX_DevicePostureDeviceGroup_Natural");
        }
    }
}
