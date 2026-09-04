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

    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] O schema v2 do <c>FactsJson</c> em PostgreSQL REAL: os agregados de
    /// risco sobrevivem ao round-trip, um snapshot gravado no formato v1 (array nu) continua legível SEM
    /// inventar zeros, a coleta que falha depois preserva o risco anterior — e NADA disso escreve score.
    ///
    /// ⚠️ Esta classe está nominalmente no filtro do job `migrations-pg`; estes casos executam de fato lá.
    /// </summary>
    [Fact]
    public async Task IdentityRisk_SchemaV2_RoundTrip_V1Compatibility_And_NoScoreWrite_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var legacyTenant = Guid.NewGuid();
        var connector = Guid.NewGuid();
        var legacyConnector = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "R", Slug = "r-" + tenant.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = legacyTenant, Name = "L", Slug = "l-" + legacyTenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(NewEntraConnector(tenant, connector));
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(legacyTenant)))
        {
            db.Connectors.Add(NewEntraConnector(legacyTenant, legacyConnector));
            await db.SaveChangesAsync();
        }

        // (a) ROUND-TRIP v2: os agregados de risco atravessam o PostgreSQL real sem perda.
        await CollectAsync(opt, tenant, CompletedWithRisk());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var stored = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
            stored.SchemaVersion.Should().Be(IdentityEvidenceService.SchemaVersion);

            var projection = await ProjectionAsync(opt, tenant);
            projection.IdentityRisk.Should().NotBeNull();
            projection.IdentityRisk!.RiskyUsers!.Active.Should().Be(4);
            projection.IdentityRisk.RiskyUsers.Levels.Hidden.Should().Be(1, "nível oculto pela licença sobrevive ao round-trip");
            projection.IdentityRisk.RiskDetections!.TotalInWindow.Should().Be(6);
            projection.IdentityRisk.RiskDetections.TopTypes.Should().ContainSingle(t => t.Category == "leakedcredentials");
            projection.AuthenticationPosture!.PasswordlessCapable.Should().Be(2);

            // PRIVACIDADE no banco real: nenhum campo pessoal foi persistido.
            var body = stored.FactsJson + stored.CapabilitiesJson;
            foreach (var sentinel in new[] { "userPrincipalName", "userDisplayName", "userId", "ipAddress", "requestId", "correlationId", "additionalInfo", "@" })
                body.Should().NotContain(sentinel, $"'{sentinel}' jamais é persistido");
        }

        // (b) COMPATIBILIDADE v1: um snapshot no formato antigo (array nu) segue legível, sem agregados falsos.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(legacyTenant)))
        {
            db.IdentityEvidenceSnapshots.Add(new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = legacyConnector,
                Source = "Microsoft Entra ID",
                SourceType = KnightSourceType.MicrosoftEntraId,
                SchemaVersion = IdentityEvidenceService.LegacySchemaVersion,
                DataState = KnightSourceState.Completed,
                LastAttemptState = KnightSourceState.Completed,
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastCollectionAt = DateTimeOffset.UtcNow,
                FactsJson = """[{"key":"PrivilegedAccountsTotal","outcome":"Collected","count":5}]""",
                CapabilitiesJson = """[{"capability":"PrivilegedRoleInventory","outcome":"Collected","detail":null}]""",
                Fingerprint = "legacy",
            });
            await db.SaveChangesAsync();
        }
        var legacyProjection = await ProjectionAsync(opt, legacyTenant);
        legacyProjection.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete);
        legacyProjection.SchemaVersion.Should().Be(IdentityEvidenceService.LegacySchemaVersion, "o v1 NÃO é reescrito");
        legacyProjection.Capabilities.Should().ContainSingle(c => c.Capability == KnightCapability.PrivilegedRoleInventory);
        legacyProjection.IdentityRisk.Should().BeNull("snapshot v1 não tem risco — e ausência nunca vira zero");

        // (c) DEGRADAÇÃO: a coleta seguinte falha e o risco ANTERIOR continua servido.
        await CollectAsync(opt, tenant, Failure());
        var preserved = await ProjectionAsync(opt, tenant);
        preserved.IdentityRisk.Should().NotBeNull("a última fotografia válida de risco sobrevive à falha");
        preserved.IdentityRisk!.RiskyUsers!.Active.Should().Be(4);
        preserved.LastAttemptState.Should().Be(KnightSourceState.AuthenticationFailure);
        preserved.IsDegraded.Should().BeTrue();

        // (d) AUTORIDADE: nenhuma escrita de score em PostgreSQL real.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.Signals.CountAsync()).Should().Be(0, "risco de identidade não cria EvidenceSignal");
            (await db.TenantControlStates.CountAsync()).Should().Be(0, "risco de identidade não grava veredito");
            (await db.TenantScoreSnapshots.CountAsync()).Should().Be(0, "risco de identidade não produz score");
        }
    }

    private static async Task<IdentityEvidenceProjection> ProjectionAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var registry = new KnightCollectorRegistry(new[] { new FixedCollector(Completed()) });
        return await new IdentityEvidenceService(db, registry, new FixedConfig(), new SystemTenantContext(tenant))
            .GetLatestProjectionAsync();
    }

    /// <summary>Coleta completa carregando os agregados de risco do schema v2 (sem PII, só contagens).</summary>
    private static KnightCollectionResult CompletedWithRisk()
    {
        var now = DateTimeOffset.UtcNow;
        var risk = new IdentityRiskPosture(
            KnightCapabilityOutcome.Collected, null,
            new IdentityRiskyUserFacts(
                Total: 6, Deleted: 1, Processing: 1,
                Levels: new IdentityRiskLevelDistribution(High: 2, Medium: 1, Low: 1, Hidden: 1),
                States: new IdentityRiskStateDistribution(AtRisk: 3, ConfirmedCompromised: 1, Remediated: 1),
                HighRiskActive: 2,
                MostRecentRiskUpdateAt: now.AddDays(-1),
                IsComplete: true),
            KnightCapabilityOutcome.Collected, null,
            new IdentityRiskDetectionFacts(
                WindowDays: IdentityRiskWindows.DetectionWindowDays,
                WindowStart: now.AddDays(-30), WindowEnd: now,
                TotalInWindow: 6, OutsideWindow: 3, Undated: 1, InRecentWindow: 2,
                Levels: new IdentityRiskLevelDistribution(High: 4, Low: 2),
                States: new IdentityRiskStateDistribution(AtRisk: 4, Remediated: 2),
                Realtime: 3, NearRealtime: 1, Offline: 2, TimingNotDefined: 0, TimingUnknown: 0,
                PremiumDetailWithheld: 1, HighRiskActive: 4,
                TopTypes: new[] { new IdentityRiskCategoryCount("leakedcredentials", 6) },
                MostRecentDetectionAt: now.AddDays(-1),
                IsComplete: true),
            now);

        var auth = new IdentityAuthenticationPosture(
            TotalUsers: 8, MfaCapable: 6, MfaRegistered: 6, PasswordlessCapable: 2, CapabilityUnknown: 0,
            MethodsRegistered: new[] { new IdentityRiskCategoryCount("microsoftauthenticatorpush", 6) },
            IsComplete: true);

        return new KnightCollectionResult(
            KnightSourceType.MicrosoftEntraId, KnightSourceState.Completed, "Microsoft Entra ID",
            new KnightFactSet(new[] { KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 7) }),
            new[]
            {
                new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.IdentityRiskyUsers, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.IdentityRiskDetections, KnightCapabilityOutcome.Collected),
            },
            now, "ok", risk, auth);
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
