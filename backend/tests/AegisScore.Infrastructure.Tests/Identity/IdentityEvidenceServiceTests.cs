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
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Testes FOCADOS da Evidence Fabric de identidade: UMA aquisição por operação
/// (o mesmo snapshot alimenta os consumidores), preservação dos estados por capacidade na coleta parcial,
/// permissão insuficiente que NÃO vira zero/conformidade/ausência genérica, falha nova que preserva a última
/// evidência válida e sinaliza degradação, isolamento por tenant, ausência de PII/segredos no snapshot e a
/// invariante de score (coletar NÃO cria EvidenceSignal/TenantControlState).
/// </summary>
public sealed class IdentityEvidenceServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Secret = "super-secret-client-value-DO-NOT-PERSIST";

    private readonly SqliteConnection _connection;

    public IdentityEvidenceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    // ---- 1) UMA aquisição por operação; o MESMO snapshot alimenta os consumidores -------------------

    [Fact]
    public async Task Collect_SingleAcquisition_SameSnapshotFeedsKnightAndPosture()
    {
        await SeedConnectorAsync(TenantA, enabled: true);
        var collector = new CountingCollector(CompletedResult());

        await using (var db = NewContext(TenantA))
        {
            var acquisition = await ServiceFor(db, TenantA, collector).CollectAsync();
            acquisition.ConnectorState.Should().Be(IdentityEvidenceConnectorState.Configured);
            acquisition.CollectionResult.Should().NotBeNull();
        }

        // Exatamente UMA aquisição real do Graph nesta operação.
        collector.Calls.Should().Be(1, "uma operação lógica faz uma única aquisição");

        // O consumidor de POSTURA lê o MESMO snapshot persistido — SEM nova aquisição do Graph.
        await using (var db = NewContext(TenantA))
        {
            var projection = await ServiceFor(db, TenantA, collector).GetLatestProjectionAsync();
            projection.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete);
            projection.Controls.Should().NotBeEmpty();
        }
        collector.Calls.Should().Be(1, "a leitura da postura NÃO dispara uma segunda aquisição");

        // O consumidor KNIGHT avalia os MESMOS fatos normalizados de forma determinística.
        await using (var db = NewContext(TenantA))
        {
            var acquisition = await ServiceFor(db, TenantA, collector).CollectAsync();
            var evaluated = KnightIndicatorEvaluator.Evaluate(acquisition.CollectionResult!.Facts, KnightSourceType.MicrosoftEntraId);
            evaluated.Single(e => e.Definition.Id == "AK-ENTRA-001").Status
                .Should().Be(KnightIndicatorStatus.Exposed, "3 privilegiados sem MFA → exposto (determinístico)");
        }
    }

    // ---- 2) Coleta PARCIAL preserva os estados por capacidade ---------------------------------------

    [Fact]
    public async Task Collect_Partial_PreservesPerCapabilityStates()
    {
        await SeedConnectorAsync(TenantA, enabled: true);
        var collector = new CountingCollector(PartialResult());

        IdentityEvidenceProjection projection;
        await using (var db = NewContext(TenantA))
            projection = IdentityEvidenceProjection.Build(
                IdentityEvidenceConnectorState.Configured, (await ServiceFor(db, TenantA, collector).CollectAsync()).Snapshot);

        projection.CollectionState.Should().Be(IdentityEvidenceCollectionState.Partial);
        projection.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.ConditionalAccessPolicies && c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
        projection.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.PrivilegedRoleInventory && c.Outcome == KnightCapabilityOutcome.Collected);
    }

    // ---- 3) InsufficientPermission NÃO vira zero, conformidade nem ausência genérica ----------------

    [Fact]
    public async Task Collect_InsufficientPermission_IsNotZeroNorCompliantNorGenericAbsence()
    {
        await SeedConnectorAsync(TenantA, enabled: true);
        var collector = new CountingCollector(PartialResult());

        await using var db = NewContext(TenantA);
        var acquisition = await ServiceFor(db, TenantA, collector).CollectAsync();

        // O fato ausente por permissão mantém o MOTIVO (não vira contagem zero silenciosa).
        var missing = acquisition.CollectionResult!.Facts.Get(KnightSignalKey.AdminMfaPolicyEnforced);
        missing.Outcome.Should().Be(KnightObservationOutcome.Missing);
        missing.MissingReason.Should().Contain("Permissão");
        missing.Count.Should().BeNull("permissão insuficiente não é uma contagem zero");

        // A capacidade permanece InsufficientPermission — NÃO colapsa em Unavailable genérico.
        var projection = IdentityEvidenceProjection.Build(acquisition.ConnectorState, acquisition.Snapshot);
        projection.Capabilities.Single(c => c.Capability == KnightCapability.ConditionalAccessPolicies)
            .Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);

        // Nenhum controle de identidade é aprovado/avaliado — é "coletado, porém insuficiente".
        projection.Controls.Should().OnlyContain(c => c.State == IdentityControlEvidenceState.CollectedButInsufficient);
        projection.Controls.Should().NotContain(c => c.State == IdentityControlEvidenceState.Evaluated);
    }

    // ---- 4) Falha NOVA preserva o último snapshot válido e sinaliza degradação -----------------------

    [Fact]
    public async Task Collect_NewFailure_PreservesLastValidSnapshot_AndSignalsDegradation()
    {
        await SeedConnectorAsync(TenantA, enabled: true);

        // 1ª coleta COMPLETA → evidência válida persistida.
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(CompletedResult())).CollectAsync();

        // 2ª coleta FALHA (auth) sem dado → NÃO apaga a evidência; registra a degradação.
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(FailureResult())).CollectAsync();

        await using (var assert = NewContext(TenantA))
        {
            var snap = await assert.IdentityEvidenceSnapshots.SingleAsync();
            snap.DataState.Should().Be(KnightSourceState.Completed, "a última evidência válida é preservada");
            snap.LastAttemptState.Should().Be(KnightSourceState.AuthenticationFailure, "a degradação é registrada à parte");
            snap.LastCollectionAt.Should().NotBeNull();
            snap.FactsJson.Should().Contain("PrivilegedAccountsWithoutMfa", "os fatos completos seguem preservados");

            var connector = await assert.Connectors.SingleAsync();
            connector.LastStatus.Should().Be(ConnectorStatus.Degraded, "falha com evidência anterior = degradado, não Failed");
        }

        await using (var db = NewContext(TenantA))
        {
            var projection = await ServiceFor(db, TenantA, new CountingCollector(FailureResult())).GetLatestProjectionAsync();
            projection.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete, "os dados preservados prevalecem");
            projection.IsDegraded.Should().BeTrue();
            projection.LastAttemptState.Should().Be(KnightSourceState.AuthenticationFailure);
        }
    }

    // ---- 5) Isolamento por tenant: A nunca lê/projeta a evidência de B -------------------------------

    [Fact]
    public async Task Collect_TenantIsolation_ANeverReadsBEvidence()
    {
        await SeedConnectorAsync(TenantA, enabled: true);
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(CompletedResult())).CollectAsync();

        // Tenant B não tem conector → sem fonte; e não enxerga o snapshot de A (query filter).
        await using (var db = NewContext(TenantB))
        {
            var projection = await ServiceFor(db, TenantB, new CountingCollector(CompletedResult())).GetLatestProjectionAsync();
            projection.ConnectorState.Should().Be(IdentityEvidenceConnectorState.NotConfigured);
            (await db.IdentityEvidenceSnapshots.CountAsync()).Should().Be(0, "B não lê a evidência de A");
        }

        // A prova de que a linha existe, mas carimbada com o tenant de A (visível ignorando o filtro).
        await using (var raw = NewContext(null))
        {
            var all = await raw.IdentityEvidenceSnapshots.IgnoreQueryFilters().ToListAsync();
            all.Should().ContainSingle().Which.TenantId.Should().Be(TenantA);
        }
    }

    // ---- 6) O snapshot NÃO persiste PII nem segredos ------------------------------------------------

    [Fact]
    public async Task Collect_Snapshot_PersistsNoPiiNorSecrets()
    {
        await SeedConnectorAsync(TenantA, enabled: true);
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(CompletedResult())).CollectAsync();

        await using var assert = NewContext(TenantA);
        var snap = await assert.IdentityEvidenceSnapshots.SingleAsync();
        var body = snap.FactsJson + snap.CapabilitiesJson + (snap.LastAttemptDetail ?? "");
        body.Should().NotContain(Secret, "o segredo do conector jamais é persistido no snapshot");
        body.Should().NotContain("@", "nenhum e-mail/UPN de usuário ou aplicação é persistido");
    }

    // ---- 7) Coletar NÃO altera o score (sem EvidenceSignal/TenantControlState) -----------------------

    [Fact]
    public async Task Collect_WritesNoEvidenceSignalNorControlState()
    {
        await SeedConnectorAsync(TenantA, enabled: true);
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA, new CountingCollector(CompletedResult())).CollectAsync();

        (await db.Signals.CountAsync()).Should().Be(0, "a Evidence Fabric é consultiva — não cria sinal de score");
        (await db.TenantControlStates.CountAsync()).Should().Be(0, "coletar não grava veredito no ledger");
    }

    // ---- 8) Conector desabilitado/ausente: recusa a coleta ------------------------------------------

    [Fact]
    public async Task Collect_DisabledConnector_RefusesAndDoesNotCollect()
    {
        await SeedConnectorAsync(TenantA, enabled: false);
        var collector = new CountingCollector(CompletedResult());
        await using var db = NewContext(TenantA);

        var acquisition = await ServiceFor(db, TenantA, collector).CollectAsync();

        acquisition.ConnectorState.Should().Be(IdentityEvidenceConnectorState.Disabled);
        acquisition.CollectionResult.Should().BeNull();
        collector.Calls.Should().Be(0, "conector desabilitado não coleta");
    }

    [Fact]
    public async Task Collect_NoConnector_ReturnsNotConfigured()
    {
        await using var db = NewContext(TenantA);
        var acquisition = await ServiceFor(db, TenantA, new CountingCollector(CompletedResult())).CollectAsync();
        acquisition.ConnectorState.Should().Be(IdentityEvidenceConnectorState.NotConfigured);
        acquisition.CollectionResult.Should().BeNull();
    }

    // ---- infraestrutura do teste -------------------------------------------------------------------

    private IIdentityEvidenceService ServiceFor(AegisScoreDbContext db, Guid tenantId, IKnightCollector collector)
    {
        var registry = new KnightCollectorRegistry(new[] { collector });
        var config = new FakeConfigProvider(new KnightEntraIdConfiguration("tenant", "client", Secret));
        return new IdentityEvidenceService(db, registry, config, new SystemTenantContext(tenantId));
    }

    private async Task SeedConnectorAsync(Guid tenantId, bool enabled)
    {
        await using var db = NewContext(null);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
        await db.SaveChangesAsync();
        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft Entra ID · AEGIS KNIGHT", Enabled = enabled,
            EncryptedSettings = "{\"clientSecret\":\"" + Secret + "\"}",
        });
        await dbt.SaveChangesAsync();
    }

    private static KnightCollectionResult CompletedResult() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.Completed, "Microsoft Entra ID",
        new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 12),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 3),
            KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 4),
            KnightObservation.OfRatio(KnightSignalKey.MfaRegistrationCoveragePercent, 72.5),
            KnightObservation.OfFlag(KnightSignalKey.SecurityDefaultsEnabled, false),
        }),
        new[]
        {
            new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected),
        },
        DateTimeOffset.UtcNow, "Coleta do Microsoft Entra ID concluída.");

    private static KnightCollectionResult PartialResult() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.PartialCollection, "Microsoft Entra ID",
        new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 8),
            KnightObservation.MissingData(KnightSignalKey.AdminMfaPolicyEnforced, "Permissão insuficiente para esta coleta."),
        }),
        new[]
        {
            new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.ConditionalAccessPolicies, KnightCapabilityOutcome.InsufficientPermission, "Permissão insuficiente para esta coleta."),
        },
        DateTimeOffset.UtcNow, "Coleta parcial do Microsoft Entra ID.");

    private static KnightCollectionResult FailureResult() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.AuthenticationFailure, "Microsoft Entra ID",
        KnightFactSet.Empty, Array.Empty<KnightCapabilityStatus>(),
        DateTimeOffset.UtcNow, "Falha ao autenticar a aplicação junto ao Microsoft Graph.");

    private sealed class CountingCollector : IKnightCollector
    {
        private readonly KnightCollectionResult _result;
        public int Calls { get; private set; }
        public CountingCollector(KnightCollectionResult result) => _result = result;
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeConfigProvider : IKnightSourceConfigurationProvider
    {
        private readonly KnightSourceConfiguration _config;
        public FakeConfigProvider(KnightSourceConfiguration config) => _config = config;
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult(source == KnightSourceType.MicrosoftEntraId ? _config : new KnightSourceNotConfigured(source));
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }
}
