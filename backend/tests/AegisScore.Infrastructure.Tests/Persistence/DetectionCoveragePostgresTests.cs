using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AppCoverage = AegisScore.Application.Abstractions.DetectionCoverageSnapshot;
using AppTechnique = AegisScore.Application.Abstractions.DetectionTechniqueCoverage;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Migration, unicidade, substituição atômica, isolamento, integridade referencial
/// (FK tenant-safe) e preservação da cobertura de detecção em PostgreSQL 18 REAL (gate <c>AEGIS_TEST_PG</c>). Prova
/// que a migration desta entrega aplica de fato, que o índice único (Tenant, Conector) e a FK ao conector são
/// invariantes de banco, e que a reconciliação atômica funciona no PG real.
/// </summary>
public sealed class DetectionCoveragePostgresTests
{
    private static AppTechnique Tech(string id, int rules, int live, int alerting,
        int normal = 0, int limited = 0, int paused = 0, int unknown = 0) =>
        new(id, id, id.Contains('.'), null, Array.Empty<string>(),
            rules, live, normal, limited, paused, unknown, alerting);

    private static AppCoverage Snap(DetectionCoverageCollectionState state, int active, params AppTechnique[] techniques) =>
        new("Google SecOps", "17.1", state, DateTimeOffset.UtcNow, active, active, 0, active,
            active, 0, 0, 0, active, techniques);

    private const string Settings =
        "{\"projectId\":\"demo-secops\",\"location\":\"us\",\"instanceId\":\"inst-123\"," +
        "\"serviceAccountJson\":\"{}\"}";

    private static ConnectorConfig NewSiemConnector(Guid tenant, Guid id) => new()
    {
        Id = id, TenantId = tenant, Provider = ConnectorProvider.Google, Capability = ConnectorCapability.Siem,
        DisplayName = "Google SecOps", AuthType = ConnectorAuthType.ServiceAccount, Enabled = true,
        EncryptedSettings = Settings,
    };

    [Fact]
    public async Task Migration_Uniqueness_AtomicReplacement_And_Isolation_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var connector = Guid.NewGuid();        // conector do tenant principal
        var otherConnector = Guid.NewGuid();   // conector distinto do outro tenant (Id de ConnectorConfig é global)

        // (a) A MIGRATION aplica de fato no PostgreSQL e aparece entre as aplicadas; semeia tenants (contexto sistêmico)
        // e conectores (cada um sob o contexto do SEU tenant — a gravação multi-tenant é fail-closed sem tenant resolvido).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().Contain(m => m.EndsWith("_DetectionCoverage_GoogleSecOps"),
                    "a migration da entrega GOOGLE-SECOPS-02 deve constar como aplicada no PostgreSQL real");
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = other, Name = "O", Slug = "o-" + other.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(NewSiemConnector(tenant, connector));
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            db.Connectors.Add(NewSiemConnector(other, otherConnector));
            await db.SaveChangesAsync();
        }

        async Task Reconcile(Guid t, Guid conn, AppCoverage snap)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(t));
            await new DetectionCoverageReconciler(db).ReconcileAsync(conn, snap, CancellationToken.None);
        }

        // (b) Primeira coleta completa + substituição ATÔMICA (troca o conjunto de técnicas, sem órfãos).
        await Reconcile(tenant, connector, Snap(DetectionCoverageCollectionState.Available, 2, Tech("T1059", 1, 1, 1), Tech("T1110", 1, 1, 1)));
        await Reconcile(tenant, connector, Snap(DetectionCoverageCollectionState.Available, 1, Tech("T1566", 1, 1, 1)));

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await assert.DetectionCoverageSnapshots.CountAsync()).Should().Be(1);
            var s = await assert.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
            s.Techniques.Should().ContainSingle().Which.TechniqueId.Should().Be("T1566");
            (await assert.DetectionCoverageTechniques.CountAsync()).Should().Be(1, "substituição atômica não deixa filhos órfãos");
        }

        // (c) Falha total PRESERVA o inventário; a tentativa falha fica registrada.
        await Reconcile(tenant, connector, Snap(DetectionCoverageCollectionState.Unavailable, 0));
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var s = await assert.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
            s.CollectionState.Should().Be(DetectionCoverageCollectionState.Available);
            s.LastAttemptState.Should().Be(DetectionCoverageCollectionState.Unavailable);
            s.Techniques.Should().ContainSingle();
        }

        // (d) ISOLAMENTO por tenant: o outro tenant tem seu próprio conector e seu próprio snapshot.
        await Reconcile(other, otherConnector, Snap(DetectionCoverageCollectionState.Available, 3, Tech("T1059", 3, 3, 3)));
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
            (await assert.DetectionCoverageSnapshots.SingleAsync()).TotalActiveRules.Should().Be(3);

        // (e) UNICIDADE (Tenant, Conector) como invariante de banco: um 2º snapshot direto (conector existente) falha.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.DetectionCoverageSnapshots.Add(new DetectionCoverageSnapshot
            {
                ConnectorConfigId = connector, Source = "Google SecOps", AttackVersion = "17.1", Fingerprint = "x",
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("índice único (Tenant, Conector)");
        }
    }

    [Fact]
    public async Task ForeignKey_TenantSafe_RejectsOrphanAndCrossTenant_CascadesOnConnectorDelete()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var connectorA = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenantA, Name = "A", Slug = "a-" + tenantA.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "B", Slug = "b-" + tenantB.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            db.Connectors.Add(NewSiemConnector(tenantA, connectorA));   // conector existe SÓ no tenant A
            await db.SaveChangesAsync();
        }

        // (1) Conector INEXISTENTE: snapshot referenciando um ConnectorConfigId que não existe → FK rejeita.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            db.DetectionCoverageSnapshots.Add(new DetectionCoverageSnapshot
            {
                ConnectorConfigId = Guid.NewGuid(), Source = "Google SecOps", AttackVersion = "17.1", Fingerprint = "x",
            });
            var act = async () => await db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>("FK exige conector existente"))
                .WithInnerException<Npgsql.PostgresException>()
                .Which.SqlState.Should().Be("23503", "violação de foreign_key_violation");
        }

        // (2) CROSS-TENANT: o conector é do tenant A; um snapshot do tenant B apontando p/ ele → FK composta rejeita.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
        {
            db.DetectionCoverageSnapshots.Add(new DetectionCoverageSnapshot
            {
                ConnectorConfigId = connectorA, Source = "Google SecOps", AttackVersion = "17.1", Fingerprint = "x",
            });
            var act = async () => await db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>("(connectorA, tenantB) não existe em ConnectorConfig"))
                .WithInnerException<Npgsql.PostgresException>()
                .Which.SqlState.Should().Be("23503");
        }

        // (3) CASCADE: reconcilia um snapshot válido para (A, connectorA); excluir o conector remove snapshot + técnicas.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
            await new DetectionCoverageReconciler(db).ReconcileAsync(connectorA,
                Snap(DetectionCoverageCollectionState.Available, 2, Tech("T1059", 1, 1, 1), Tech("T1110", 1, 1, 1)),
                CancellationToken.None);

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            (await assert.DetectionCoverageSnapshots.CountAsync()).Should().Be(1);
            (await assert.DetectionCoverageTechniques.CountAsync()).Should().Be(2, "a FK aplicou e o snapshot tem filhos");
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var cfg = await db.Connectors.SingleAsync(c => c.Id == connectorA);
            db.Connectors.Remove(cfg);
            await db.SaveChangesAsync();
        }

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            (await assert.DetectionCoverageSnapshots.CountAsync()).Should().Be(0, "excluir o conector cascateia no snapshot");
            (await assert.DetectionCoverageTechniques.CountAsync()).Should().Be(0, "e nas técnicas filhas — sem cobertura órfã");
        }
    }
}
