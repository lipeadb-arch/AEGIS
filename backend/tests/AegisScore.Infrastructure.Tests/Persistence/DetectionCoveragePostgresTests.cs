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
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Migration, unicidade, substituição atômica, isolamento e preservação da cobertura de
/// detecção em PostgreSQL 18 REAL (gate <c>AEGIS_TEST_PG</c>). Prova que a migration desta entrega aplica de fato,
/// que o índice único (Tenant, Conector) é invariante de banco e que a reconciliação atômica funciona no PG real.
/// </summary>
public sealed class DetectionCoveragePostgresTests
{
    private static AppTechnique Tech(string id, int rules, int live, int alerting) =>
        new(id, id, id.Contains('.'), null, Array.Empty<string>(), rules, live, alerting);

    private static AppCoverage Snap(DetectionCoverageCollectionState state, int active, params AppTechnique[] techniques) =>
        new("Google SecOps", "17.1", state, DateTimeOffset.UtcNow, active, active, 0, active, active, techniques);

    [Fact]
    public async Task Migration_Uniqueness_AtomicReplacement_And_Isolation_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var connector = Guid.NewGuid();

        // (a) A MIGRATION aplica de fato no PostgreSQL e aparece entre as aplicadas.
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

        async Task Reconcile(Guid t, AppCoverage snap)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(t));
            await new DetectionCoverageReconciler(db).ReconcileAsync(connector, snap, CancellationToken.None);
        }

        // (b) Primeira coleta completa + substituição ATÔMICA (troca o conjunto de técnicas, sem órfãos).
        await Reconcile(tenant, Snap(DetectionCoverageCollectionState.Available, 2, Tech("T1059", 1, 1, 1), Tech("T1110", 1, 1, 1)));
        await Reconcile(tenant, Snap(DetectionCoverageCollectionState.Available, 1, Tech("T1566", 1, 1, 1)));

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await assert.DetectionCoverageSnapshots.CountAsync()).Should().Be(1);
            var s = await assert.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
            s.Techniques.Should().ContainSingle().Which.TechniqueId.Should().Be("T1566");
            (await assert.DetectionCoverageTechniques.CountAsync()).Should().Be(1, "substituição atômica não deixa filhos órfãos");
        }

        // (c) Falha total PRESERVA o inventário; a tentativa falha fica registrada.
        await Reconcile(tenant, Snap(DetectionCoverageCollectionState.Unavailable, 0));
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var s = await assert.DetectionCoverageSnapshots.Include(x => x.Techniques).SingleAsync();
            s.CollectionState.Should().Be(DetectionCoverageCollectionState.Available);
            s.LastAttemptState.Should().Be(DetectionCoverageCollectionState.Unavailable);
            s.Techniques.Should().ContainSingle();
        }

        // (d) ISOLAMENTO por tenant: o MESMO connectorId em outro tenant tem seu próprio snapshot.
        await Reconcile(other, Snap(DetectionCoverageCollectionState.Available, 3, Tech("T1059", 3, 3, 3)));
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
            (await assert.DetectionCoverageSnapshots.SingleAsync()).TotalActiveRules.Should().Be(3);

        // (e) UNICIDADE (Tenant, Conector) como invariante de banco: um 2º snapshot direto falha.
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
}
