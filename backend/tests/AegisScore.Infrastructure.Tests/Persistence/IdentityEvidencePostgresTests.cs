using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Migration, unicidade (upsert idempotente), preservação degradation-safe,
/// isolamento por tenant e integridade referencial (FK COMPOSTA tenant-safe) da Evidence Fabric de identidade
/// em PostgreSQL 18 REAL (gate <c>AEGIS_TEST_PG</c>). Prova que a migration desta entrega aplica de fato, que a
/// chave natural (Tenant, Conector) e a FK ao conector são invariantes de banco, e que uma coleta que falha
/// preserva a última evidência válida no PG real.
/// </summary>
public sealed class IdentityEvidencePostgresTests
{
    private static ConnectorConfig NewEntraConnector(Guid tenant, Guid id) => new()
    {
        Id = id, TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
        DisplayName = "Microsoft Entra ID · AEGIS KNIGHT", AuthType = ConnectorAuthType.OAuthClientCredentials,
        Enabled = true, EncryptedSettings = "{\"clientSecret\":\"s\"}",
    };

    private static KnightCollectionResult Completed() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.Completed, "Microsoft Entra ID",
        new KnightFactSet(new[] { KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 7) }),
        new[] { new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected) },
        DateTimeOffset.UtcNow, "ok");

    private static KnightCollectionResult Failure() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.AuthenticationFailure, "Microsoft Entra ID",
        KnightFactSet.Empty, Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow, "auth failed");

    private static async Task CollectAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant, KnightCollectionResult result)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var registry = new KnightCollectorRegistry(new[] { new FixedCollector(result) });
        var config = new FixedConfig();
        await new IdentityEvidenceService(db, registry, config, new SystemTenantContext(tenant)).CollectAsync();
    }

    [Fact]
    public async Task Migration_Upsert_Degradation_And_Isolation_OnRealPostgres()
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
                .Should().Contain(m => m.EndsWith("_IdentityEvidenceFabric"),
                    "a migration da Evidence Fabric deve constar como aplicada no PostgreSQL real");
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = other, Name = "O", Slug = "o-" + other.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(NewEntraConnector(tenant, connector));
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
        {
            db.Connectors.Add(NewEntraConnector(other, otherConnector));
            await db.SaveChangesAsync();
        }

        // (a) Duas coletas completas IDÊNTICAS → um único snapshot (upsert idempotente por chave natural).
        await CollectAsync(opt, tenant, Completed());
        await CollectAsync(opt, tenant, Completed());
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            (await assert.IdentityEvidenceSnapshots.CountAsync()).Should().Be(1, "chave natural (Tenant, Conector) é única");

        // (b) Coleta que FALHA preserva a última evidência válida; registra a degradação.
        await CollectAsync(opt, tenant, Failure());
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var s = await assert.IdentityEvidenceSnapshots.SingleAsync();
            s.DataState.Should().Be(KnightSourceState.Completed);
            s.LastAttemptState.Should().Be(KnightSourceState.AuthenticationFailure);
            s.LastCollectionAt.Should().NotBeNull();
        }

        // (c) ISOLAMENTO por tenant: o outro tenant tem o SEU próprio snapshot.
        await CollectAsync(opt, other, Completed());
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(other)))
            (await assert.IdentityEvidenceSnapshots.SingleAsync()).ConnectorConfigId.Should().Be(otherConnector);
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            (await assert.IdentityEvidenceSnapshots.SingleAsync()).ConnectorConfigId.Should().Be(connector);

        // (d) UNICIDADE (Tenant, Conector) como invariante de banco: um 2º snapshot direto falha.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.IdentityEvidenceSnapshots.Add(new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = connector, Source = "Microsoft Entra ID", SchemaVersion = "v1", Fingerprint = "x",
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
            db.Connectors.Add(NewEntraConnector(tenantA, connectorA));   // conector existe SÓ no tenant A
            await db.SaveChangesAsync();
        }

        // (1) Conector INEXISTENTE → FK rejeita (23503).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            db.IdentityEvidenceSnapshots.Add(new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = Guid.NewGuid(), Source = "Microsoft Entra ID", SchemaVersion = "v1", Fingerprint = "x",
            });
            var act = async () => await db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>("FK exige conector existente"))
                .WithInnerException<Npgsql.PostgresException>().Which.SqlState.Should().Be("23503");
        }

        // (2) CROSS-TENANT: conector é do tenant A; snapshot do tenant B apontando p/ ele → FK composta rejeita.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
        {
            db.IdentityEvidenceSnapshots.Add(new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = connectorA, Source = "Microsoft Entra ID", SchemaVersion = "v1", Fingerprint = "x",
            });
            var act = async () => await db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>("(connectorA, tenantB) não existe em ConnectorConfig"))
                .WithInnerException<Npgsql.PostgresException>().Which.SqlState.Should().Be("23503");
        }

        // (3) CASCADE: snapshot válido para A; excluir o conector remove o snapshot — sem evidência órfã.
        await CollectAsync(opt, tenantA, Completed());
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
            (await assert.IdentityEvidenceSnapshots.CountAsync()).Should().Be(1);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var cfg = await db.Connectors.SingleAsync(c => c.Id == connectorA);
            db.Connectors.Remove(cfg);
            await db.SaveChangesAsync();
        }
        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
            (await assert.IdentityEvidenceSnapshots.CountAsync()).Should().Be(0, "excluir o conector cascateia no snapshot");
    }

    private sealed class FixedCollector : IKnightCollector
    {
        private readonly KnightCollectionResult _result;
        public FixedCollector(KnightCollectionResult result) => _result = result;
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private sealed class FixedConfig : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(new KnightEntraIdConfiguration("tenant", "client", "secret"));
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }
}
