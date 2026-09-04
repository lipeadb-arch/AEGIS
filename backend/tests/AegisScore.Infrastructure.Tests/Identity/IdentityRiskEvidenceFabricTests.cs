using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
/// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Evidence Fabric de identidade no schema v2 — os agregados de risco e de
/// métodos de autenticação passam a viajar no MESMO snapshot compartilhado, sem migration.
///
/// O que estes testes travam:
///  • o envelope v2 persiste e devolve os agregados, e um snapshot v1 (array nu) continua legível SEM inventar
///    zeros para os blocos novos;
///  • upsert idempotente e fingerprint estável — coleta idêntica não reescreve o corpo;
///  • uma coleta que FALHA depois NÃO destrói a última fotografia válida de risco;
///  • KNIGHT e postura leem a MESMA fotografia, sem uma segunda consulta ao Graph;
///  • isolamento por tenant;
///  • ZERO PII persistida;
///  • GUARDAS DE AUTORIDADE: nada disso altera AEGIS Score, KNIGHT Score, EvidenceSignal, TenantControlState
///    ou o veredito NIST de qualquer controle.
/// </summary>
public sealed class IdentityRiskEvidenceFabricTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid TenantB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string Secret = "super-secret-client-value-DO-NOT-PERSIST";
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public IdentityRiskEvidenceFabricTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ================================================================================================
    //  1) Schema v2 e leitura compatível do v1
    // ================================================================================================

    [Fact]
    public async Task SchemaV2_PersistsAndReturnsRiskAggregates()
    {
        await SeedConnectorAsync(TenantA);
        var collector = new CountingCollector(WithRisk());

        await using var db = NewContext(TenantA);
        var acquisition = await ServiceFor(db, TenantA, collector).CollectAsync();

        var snapshot = acquisition.Snapshot!;
        snapshot.SchemaVersion.Should().Be(IdentityEvidenceService.SchemaVersion).And.Be("aegis-identity-evidence-v2");

        snapshot.IdentityRisk.Should().NotBeNull();
        snapshot.IdentityRisk!.RiskyUsers!.Active.Should().Be(3);
        snapshot.IdentityRisk.RiskyUsers.HighRiskActive.Should().Be(2);
        snapshot.IdentityRisk.RiskDetections!.TotalInWindow.Should().Be(7);
        snapshot.IdentityRisk.RiskDetections.TopTypes.Should().ContainSingle(t => t.Category == "leakedcredentials");
        snapshot.AuthenticationPosture!.PasswordlessCapable.Should().Be(4);

        // As observações do v1 continuam no MESMO envelope, intactas.
        snapshot.Facts.Should().Contain(f => f.Key == KnightSignalKey.PrivilegedAccountsTotal && f.Count == 12);

        var stored = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        using var doc = JsonDocument.Parse(stored.FactsJson);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object, "o v2 é um envelope, não um array nu");
        doc.RootElement.GetProperty("observations").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task SchemaV1_RemainsReadable_AndDoesNotInventZeroAggregates()
    {
        await SeedConnectorAsync(TenantA);

        // Grava um snapshot EXATAMENTE no formato antigo: FactsJson é um ARRAY nu de observações.
        var legacyFacts = JsonSerializer.Serialize(
            new[] { new { key = "PrivilegedAccountsTotal", outcome = "Collected", count = 9 } });

        Guid connectorId;
        await using (var seed = NewContext(TenantA))
        {
            connectorId = (await seed.Connectors.SingleAsync()).Id;
            seed.IdentityEvidenceSnapshots.Add(new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = connectorId,
                Source = "Microsoft Entra ID",
                SourceType = KnightSourceType.MicrosoftEntraId,
                SchemaVersion = IdentityEvidenceService.LegacySchemaVersion,
                DataState = KnightSourceState.Completed,
                LastAttemptState = KnightSourceState.Completed,
                LastAttemptAt = Now,
                LastCollectionAt = Now,
                FactsJson = legacyFacts,
                CapabilitiesJson = """[{"capability":"PrivilegedRoleInventory","outcome":"Collected","detail":null}]""",
                Fingerprint = "legacy",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(TenantA);
        var projection = await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).GetLatestProjectionAsync();

        projection.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete);
        projection.SchemaVersion.Should().Be("aegis-identity-evidence-v1", "o snapshot antigo não é reescrito");
        projection.Capabilities.Should().ContainSingle(c => c.Capability == KnightCapability.PrivilegedRoleInventory);
        projection.IdentityRisk.Should().BeNull("um snapshot v1 não tem risco — e ausência NUNCA vira zero");
        projection.AuthenticationPosture.Should().BeNull();
    }

    [Fact]
    public async Task UnreadableFactsJson_DegradesToNoFacts_NeverToFakeNumbers()
    {
        await SeedConnectorAsync(TenantA);

        await using (var seed = NewContext(TenantA))
        {
            var connectorId = (await seed.Connectors.SingleAsync()).Id;
            seed.IdentityEvidenceSnapshots.Add(new IdentityEvidenceSnapshot
            {
                ConnectorConfigId = connectorId,
                Source = "Microsoft Entra ID",
                SchemaVersion = IdentityEvidenceService.SchemaVersion,
                DataState = KnightSourceState.Completed,
                LastAttemptState = KnightSourceState.Completed,
                LastAttemptAt = Now,
                LastCollectionAt = Now,
                FactsJson = "{ isto não é json ]",
                CapabilitiesJson = "[]",
                Fingerprint = "broken",
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(TenantA);
        var projection = await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).GetLatestProjectionAsync();

        projection.IdentityRisk.Should().BeNull();
        projection.Capabilities.Should().BeEmpty();
    }

    // ================================================================================================
    //  2) Idempotência, fingerprint e isolamento
    // ================================================================================================

    [Fact]
    public async Task IdenticalCollection_IsIdempotent_AndKeepsTheSameFingerprint()
    {
        await SeedConnectorAsync(TenantA);
        var collector = new CountingCollector(WithRisk());

        string firstFingerprint;
        await using (var db = NewContext(TenantA))
        {
            await ServiceFor(db, TenantA, collector).CollectAsync();
            firstFingerprint = (await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync()).Fingerprint;
        }

        await using (var db = NewContext(TenantA))
        {
            await ServiceFor(db, TenantA, collector).CollectAsync();
            var snapshots = await db.IdentityEvidenceSnapshots.AsNoTracking().ToListAsync();
            snapshots.Should().ContainSingle("a chave natural (tenant, conector) mantém UM snapshot");
            snapshots[0].Fingerprint.Should().Be(firstFingerprint, "fatos idênticos ⇒ fingerprint idêntico");
        }
    }

    [Fact]
    public async Task ChangedRiskAggregates_ChangeTheFingerprint()
    {
        await SeedConnectorAsync(TenantA);

        string before;
        await using (var db = NewContext(TenantA))
        {
            await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).CollectAsync();
            before = (await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync()).Fingerprint;
        }

        await using (var db = NewContext(TenantA))
        {
            await ServiceFor(db, TenantA, new CountingCollector(WithRisk(activeUsers: 9))).CollectAsync();
            var after = (await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync()).Fingerprint;
            after.Should().NotBe(before, "o risco faz parte dos dados versionados pelo fingerprint");
        }
    }

    [Fact]
    public async Task Snapshots_AreIsolatedPerTenant()
    {
        await SeedConnectorAsync(TenantA);
        await SeedConnectorAsync(TenantB);

        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(WithRisk(activeUsers: 3))).CollectAsync();
        await using (var db = NewContext(TenantB))
            await ServiceFor(db, TenantB, new CountingCollector(WithRisk(activeUsers: 11))).CollectAsync();

        await using (var db = NewContext(TenantA))
        {
            var p = await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).GetLatestProjectionAsync();
            p.IdentityRisk!.RiskyUsers!.Active.Should().Be(3, "o tenant A jamais enxerga o risco do tenant B");
            (await db.IdentityEvidenceSnapshots.CountAsync()).Should().Be(1);
        }

        await using (var db = NewContext(TenantB))
        {
            var p = await ServiceFor(db, TenantB, new CountingCollector(WithRisk())).GetLatestProjectionAsync();
            p.IdentityRisk!.RiskyUsers!.Active.Should().Be(11);
        }
    }

    // ================================================================================================
    //  3) Degradação segura
    // ================================================================================================

    [Fact]
    public async Task LaterFailure_PreservesTheLastValidRiskSnapshot()
    {
        await SeedConnectorAsync(TenantA);

        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(WithRisk(activeUsers: 5))).CollectAsync();

        // Uma coleta posterior FALHA por completo (autenticação): nada de risco vem com ela.
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, new CountingCollector(FailedResult())).CollectAsync();

        await using (var db = NewContext(TenantA))
        {
            var p = await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).GetLatestProjectionAsync();

            p.IdentityRisk.Should().NotBeNull("a última fotografia válida sobrevive à falha");
            p.IdentityRisk!.RiskyUsers!.Active.Should().Be(5, "os agregados preservados são os da coleta boa");
            p.CollectionState.Should().Be(IdentityEvidenceCollectionState.Complete, "os DADOS continuam completos");
            p.LastAttemptState.Should().Be(KnightSourceState.AuthenticationFailure, "a degradação aparece à parte");
            p.IsDegraded.Should().BeTrue();
            p.CollectedAt.Should().NotBe(p.LastAttemptAt, "freshness dos dados ≠ instante da última tentativa");
        }
    }

    [Fact]
    public async Task PartialCollection_StoresTheDimensionThatWorked_AndTheTypedStateOfTheOther()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ServiceFor(db, TenantA, new CountingCollector(PartialRiskResult())).CollectAsync();
        var p = await ServiceFor(db, TenantA, new CountingCollector(PartialRiskResult())).GetLatestProjectionAsync();

        p.CollectionState.Should().Be(IdentityEvidenceCollectionState.Partial);
        p.IdentityRisk!.RiskyUsers.Should().NotBeNull("a dimensão que funcionou tem dados");
        p.IdentityRisk.RiskDetections.Should().BeNull("a dimensão sem licença NÃO vira zero");
        p.IdentityRisk.RiskDetectionsOutcome.Should().Be(KnightCapabilityOutcome.LimitedByLicense);
        p.Capabilities.Should().Contain(c =>
            c.Capability == KnightCapability.IdentityRiskDetections && c.Outcome == KnightCapabilityOutcome.LimitedByLicense);
    }

    // ================================================================================================
    //  4) UMA fotografia para todos os consumidores
    // ================================================================================================

    [Fact]
    public async Task KnightAndPosture_ReadTheSameSnapshot_WithoutASecondGraphCall()
    {
        await SeedConnectorAsync(TenantA);
        var collector = new CountingCollector(WithRisk(activeUsers: 4));

        IdentityRiskyUserFacts fromCollect;
        await using (var db = NewContext(TenantA))
        {
            var acquisition = await ServiceFor(db, TenantA, collector).CollectAsync();
            fromCollect = acquisition.Snapshot!.IdentityRisk!.RiskyUsers!;
        }
        collector.Calls.Should().Be(1);

        // A postura/tela lê o snapshot PERSISTIDO — sem nova aquisição.
        await using (var db = NewContext(TenantA))
        {
            var projection = await ServiceFor(db, TenantA, collector).GetLatestProjectionAsync();
            projection.IdentityRisk!.RiskyUsers.Should().BeEquivalentTo(fromCollect, "é literalmente a mesma fotografia");
        }
        collector.Calls.Should().Be(1, "a leitura NÃO dispara uma segunda consulta ao Graph");

        // E lendo de novo, ainda uma só.
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA, collector).GetLatestProjectionAsync();
        collector.Calls.Should().Be(1);
    }

    // ================================================================================================
    //  5) Privacidade na persistência
    // ================================================================================================

    [Fact]
    public async Task PersistedSnapshot_ContainsNoPiiNorSecret()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).CollectAsync();

        var stored = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        var body = stored.FactsJson + stored.CapabilitiesJson + (stored.LastAttemptDetail ?? "") + stored.Source;

        foreach (var sentinel in new[]
                 {
                     Secret, "userPrincipalName", "userDisplayName", "userId", "ipAddress",
                     "requestId", "correlationId", "additionalInfo", "userAgent",
                 })
            body.Should().NotContain(sentinel, $"'{sentinel}' jamais é persistido");

        body.Should().NotContain("@", "nenhum e-mail/UPN atravessa a normalização");
    }

    // ================================================================================================
    //  6) GUARDAS DE AUTORIDADE
    // ================================================================================================

    [Fact]
    public async Task RiskCollection_WritesNoEvidenceSignal_NoControlState_NoScoreSnapshot()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ServiceFor(db, TenantA, new CountingCollector(WithRisk(activeUsers: 42))).CollectAsync();

        (await db.Signals.CountAsync()).Should().Be(0, "risco de identidade NÃO cria sinal de evidência");
        (await db.TenantControlStates.CountAsync()).Should().Be(0, "risco de identidade NÃO grava veredito no ledger");
        (await db.TenantScoreSnapshots.CountAsync()).Should().Be(0, "risco de identidade NÃO produz snapshot de score");
        (await db.Evaluations.CountAsync()).Should().Be(0, "nenhum controle NIST é avaliado por risco");
    }

    [Fact]
    public void KnightScore_IsByteForByteIdentical_WithAndWithoutRiskAggregates()
    {
        // MESMO conjunto de fatos; a única diferença é a presença dos agregados de risco no resultado.
        var facts = ScorableFacts();
        var withoutRisk = new KnightCollectionResult(
            KnightSourceType.MicrosoftEntraId, KnightSourceState.Completed, "Microsoft Entra ID",
            facts, Array.Empty<KnightCapabilityStatus>(), Now);
        var withRisk = withoutRisk with
        {
            IdentityRisk = RiskPosture(activeUsers: 99),
            AuthenticationPosture = AuthPosture(),
        };

        var before = ScoreOf(withoutRisk);
        var after = ScoreOf(withRisk);

        after.Should().BeEquivalentTo(before, "o KNIGHT Score não conhece os agregados de risco nesta entrega");
        after.FormulaVersion.Should().Be("knight-score-v1", "a versão da fórmula não mudou");
    }

    [Fact]
    public void NistVerdicts_AreIdentical_WithAndWithoutRiskAggregates()
    {
        var facts = ScorableFacts();

        var baseline = KnightIndicatorEvaluator.Evaluate(facts, KnightSourceType.MicrosoftEntraId)
            .Select(e => (e.Definition.Id, e.Status, Nist: string.Join(",", e.Definition.NistCodes)))
            .ToList();

        // Reavaliar os MESMOS fatos, ainda que a coleta traga risco junto, produz exatamente os mesmos vereditos.
        var withRisk = KnightIndicatorEvaluator.Evaluate(facts, KnightSourceType.MicrosoftEntraId)
            .Select(e => (e.Definition.Id, e.Status, Nist: string.Join(",", e.Definition.NistCodes)))
            .ToList();

        withRisk.Should().BeEquivalentTo(baseline, "nenhum controle NIST é promovido nem rebaixado por risco");
        baseline.Should().NotBeEmpty("a guarda só vale se houver indicadores avaliados");
    }

    [Fact]
    public async Task NewCapabilities_DoNotChangeTheEvaluationOfExistingSignals()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var acquisition = await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).CollectAsync();
        var evaluated = KnightIndicatorEvaluator.Evaluate(acquisition.CollectionResult!.Facts, KnightSourceType.MicrosoftEntraId);

        // O veredito determinístico continua vindo APENAS dos sinais — nenhuma detecção "aprova" um indicador.
        evaluated.Single(e => e.Definition.Id == "AK-ENTRA-001").Status.Should().Be(KnightIndicatorStatus.Exposed);
        evaluated.Should().NotContain(e => e.Status == KnightIndicatorStatus.Passed && e.Definition.NistCodes.Count == 0);
    }

    [Fact]
    public async Task ZeroDetections_DoNotPromoteAnyControlToCompliant()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        // Coleta ÍNTEGRA com ZERO detecções — o cenário em que a tentação de "aprovar" seria maior.
        await ServiceFor(db, TenantA, new CountingCollector(WithRisk(activeUsers: 0, detections: 0))).CollectAsync();
        var p = await ServiceFor(db, TenantA, new CountingCollector(WithRisk())).GetLatestProjectionAsync();

        p.IdentityRisk!.RiskDetections!.TotalInWindow.Should().Be(0);
        p.Controls.Should().NotBeEmpty();
        p.Controls.Should().OnlyContain(c => c.State == IdentityControlEvidenceState.CollectedButInsufficient,
            "ausência de detecções NÃO comprova eficácia de controle");
        p.Controls.Should().NotContain(c => c.State == IdentityControlEvidenceState.Evaluated);
        (await db.TenantControlStates.CountAsync()).Should().Be(0);
        (await db.Signals.CountAsync()).Should().Be(0);
    }

    // ================================================================================================
    //  Infraestrutura do teste
    // ================================================================================================

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private IIdentityEvidenceService ServiceFor(AegisScoreDbContext db, Guid tenantId, IKnightCollector collector) =>
        new IdentityEvidenceService(
            db,
            new KnightCollectorRegistry(new[] { collector }),
            new FakeConfigProvider(new KnightEntraIdConfiguration("tenant", "client", Secret)),
            new SystemTenantContext(tenantId));

    private async Task SeedConnectorAsync(Guid tenantId)
    {
        await using var db = NewContext(null);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
        await db.SaveChangesAsync();

        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId,
            Provider = ConnectorProvider.Microsoft,
            Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft Entra ID · AEGIS KNIGHT",
            Enabled = true,
            EncryptedSettings = "{\"clientSecret\":\"" + Secret + "\"}",
        });
        await dbt.SaveChangesAsync();
    }

    private static KnightFactSet ScorableFacts() => new(new[]
    {
        KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 12),
        KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 3),
        KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 4),
        KnightObservation.OfRatio(KnightSignalKey.MfaRegistrationCoveragePercent, 72.5),
        KnightObservation.OfFlag(KnightSignalKey.SecurityDefaultsEnabled, false),
    });

    private static KnightScoreResult ScoreOf(KnightCollectionResult result) =>
        KnightScoreFormula.Compute(
            KnightIndicatorEvaluator.Evaluate(result.Facts, result.Source)
                .Select(e => (e.Definition.Severity, e.Status)));

    private static IdentityRiskPosture RiskPosture(long activeUsers = 3, long detections = 7) => new(
        KnightCapabilityOutcome.Collected, null,
        new IdentityRiskyUserFacts(
            Total: activeUsers + 1,
            Deleted: 1,
            Processing: 0,
            Levels: new IdentityRiskLevelDistribution(High: 2, Low: Math.Max(0, activeUsers - 2)),
            States: new IdentityRiskStateDistribution(AtRisk: activeUsers),
            HighRiskActive: activeUsers >= 2 ? 2 : activeUsers,
            MostRecentRiskUpdateAt: Now.AddDays(-1),
            IsComplete: true),
        KnightCapabilityOutcome.Collected, null,
        new IdentityRiskDetectionFacts(
            WindowDays: IdentityRiskWindows.DetectionWindowDays,
            WindowStart: Now.AddDays(-30),
            WindowEnd: Now,
            TotalInWindow: detections,
            OutsideWindow: 2,
            Undated: 0,
            InRecentWindow: detections > 0 ? 1 : 0,
            Levels: new IdentityRiskLevelDistribution(High: detections),
            States: new IdentityRiskStateDistribution(AtRisk: detections),
            Realtime: detections,
            NearRealtime: 0,
            Offline: 0,
            TimingNotDefined: 0,
            TimingUnknown: 0,
            PremiumDetailWithheld: 0,
            HighRiskActive: detections,
            TopTypes: detections > 0
                ? new[] { new IdentityRiskCategoryCount("leakedcredentials", detections) }
                : Array.Empty<IdentityRiskCategoryCount>(),
            MostRecentDetectionAt: detections > 0 ? Now.AddDays(-1) : null,
            IsComplete: true),
        Now);

    private static IdentityAuthenticationPosture AuthPosture() => new(
        TotalUsers: 10, MfaCapable: 8, MfaRegistered: 8, PasswordlessCapable: 4, CapabilityUnknown: 0,
        MethodsRegistered: new[] { new IdentityRiskCategoryCount("microsoftauthenticatorpush", 8) },
        IsComplete: true);

    private static KnightCollectionResult WithRisk(long activeUsers = 3, long detections = 7) => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.Completed, "Microsoft Entra ID",
        ScorableFacts(),
        new[]
        {
            new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.IdentityRiskyUsers, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.IdentityRiskDetections, KnightCapabilityOutcome.Collected),
        },
        Now, "Coleta do Microsoft Entra ID concluída.",
        RiskPosture(activeUsers, detections), AuthPosture());

    private static KnightCollectionResult PartialRiskResult() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.PartialCollection, "Microsoft Entra ID",
        ScorableFacts(),
        new[]
        {
            new KnightCapabilityStatus(KnightCapability.IdentityRiskyUsers, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.IdentityRiskDetections, KnightCapabilityOutcome.LimitedByLicense,
                "O tenant não tem licença Microsoft Entra ID P1/P2 suficiente para ler detecções de risco."),
        },
        Now, "Coleta parcial do Microsoft Entra ID.",
        RiskPosture() with
        {
            RiskDetectionsOutcome = KnightCapabilityOutcome.LimitedByLicense,
            RiskDetectionsDetail = "O tenant não tem licença Microsoft Entra ID P1/P2 suficiente para ler detecções de risco.",
            RiskDetections = null,
        },
        AuthPosture());

    private static KnightCollectionResult FailedResult() => new(
        KnightSourceType.MicrosoftEntraId, KnightSourceState.AuthenticationFailure, "Microsoft Entra ID",
        KnightFactSet.Empty, Array.Empty<KnightCapabilityStatus>(), Now.AddHours(1),
        "Falha ao autenticar a aplicação junto ao Microsoft Graph.");

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
