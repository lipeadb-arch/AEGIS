using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Connectors;   // IngestionTestData (framework mínimo)
using AegisScore.Infrastructure.Tests.Documents;     // PostgresProbe
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-SCORE-GUARD-SIEM-01] Aposentadoria fail-closed do mapping incorreto (SIEM alerta de alta severidade
/// concedia conformidade) e a invariante de prontidão — sobre o PACOTE REAL (106 subcategorias) em SQLite efêmero.
/// </summary>
public sealed class ScoreGuardSiemMappingTests : IDisposable
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static string CatalogPath => Path.Combine(DataDir, "nist_csf_2_0_catalog.json");
    private static string MethodologyPath => Path.Combine(DataDir, "aegis_methodology.json");
    private static string RulesPath => Path.Combine(DataDir, "aegis_assessment_rules.json");

    private const string RetiredKey = "siem.alert.highSeverity";

    private readonly SqliteConnection _connection;

    public ScoreGuardSiemMappingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SeedSignalMappings_NaoSemeiaAlertaAposentado_MasMantemSecureScoreEEdr()
    {
        await SeedAllAsync();

        await using var db = NewContext();
        (await db.SignalMappings.AnyAsync(m => m.Capability == ConnectorCapability.Siem && m.SignalKey == RetiredKey))
            .Should().BeFalse("o mapping do alerta grave foi aposentado — não é mais semeado");
        (await db.SignalMappings.AnyAsync(m => m.Capability == ConnectorCapability.SecureScore && m.SignalKey == "secureScore.overall"))
            .Should().BeTrue("Secure Score preservado");
        (await db.SignalMappings.AnyAsync(m => m.Capability == ConnectorCapability.Edr && m.SignalKey == "edr.threat.blocked"))
            .Should().BeTrue("EDR preservado");
    }

    [Fact]
    public async Task SeedSignalMappings_RemoveAlertaLegado_Idempotente_PreservaOutrosEAutoral()
    {
        await SeedAllAsync();

        // Injeta o mapping LEGADO (o aposentado) + um mapping AUTORAL desconhecido que NÃO pode ser apagado.
        Guid fvId;
        await using (var mut = NewContext())
        {
            fvId = await mut.FrameworkVersions.Where(f => f.IsActive).Select(f => f.Id).SingleAsync();
            mut.SignalMappings.Add(new SignalMapping
            {
                FrameworkVersionId = fvId, Capability = ConnectorCapability.Siem, SignalKey = RetiredKey,
                SubcategoryCodes = new() { "DE.AE-02", "DE.CM-01" }, ScoringHint = EvidenceSignalEvaluator.EventControlProven,
            });
            mut.SignalMappings.Add(new SignalMapping
            {
                FrameworkVersionId = fvId, Capability = ConnectorCapability.Cmdb, SignalKey = "custom.author.signal",
                SubcategoryCodes = new() { "DE.CM-01" }, ScoringHint = EvidenceSignalEvaluator.PercentHigherIsBetter,
            });
            await mut.SaveChangesAsync();
        }

        int before;
        await using (var db = NewContext()) before = await db.SignalMappings.CountAsync();

        // Reexecuta o seed → remove SOMENTE o par aposentado.
        await using (var ctx = NewContext()) await FrameworkSeeder.SeedSignalMappingsAsync(ctx);

        await using (var db = NewContext())
        {
            (await db.SignalMappings.AnyAsync(m => m.Capability == ConnectorCapability.Siem && m.SignalKey == RetiredKey))
                .Should().BeFalse("o par aposentado foi removido");
            (await db.SignalMappings.AnyAsync(m => m.SignalKey == "custom.author.signal"))
                .Should().BeTrue("mapping autoral/desconhecido é preservado (não é o aposentado)");
            (await db.SignalMappings.CountAsync()).Should().Be(before - 1, "exatamente UM mapping removido");
        }

        // Idempotência: a segunda execução não altera nada.
        await using (var ctx = NewContext()) await FrameworkSeeder.SeedSignalMappingsAsync(ctx);
        await using (var db2 = NewContext())
            (await db2.SignalMappings.CountAsync()).Should().Be(before - 1, "reexecução é no-op");
    }

    [Fact]
    public async Task Readiness_RecusaAlertaProibido_QuandoAtivo()
    {
        await SeedAllAsync();
        await using (var ok = NewContext())
            (await SchemaReadinessGuard.CheckActivePackageAsync(ok)).IsReady
                .Should().BeTrue("pacote válido sem mapping proibido");

        await using (var mut = NewContext())
        {
            var fvId = await mut.FrameworkVersions.Where(f => f.IsActive).Select(f => f.Id).SingleAsync();
            mut.SignalMappings.Add(new SignalMapping
            {
                FrameworkVersionId = fvId, Capability = ConnectorCapability.Siem, SignalKey = RetiredKey,
                SubcategoryCodes = new() { "DE.AE-02", "DE.CM-01" }, ScoringHint = EvidenceSignalEvaluator.EventControlProven,
            });
            await mut.SaveChangesAsync();
        }

        await using var db = NewContext();
        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);
        result.IsReady.Should().BeFalse("o mapping proibido torna a base NÃO pronta (fail-closed)");
        result.Describe().Should().Contain("PROIBIDO").And.Contain(RetiredKey);
    }

    private async Task SeedAllAsync()
    {
        await using var ctx = NewContext();
        await FrameworkSeeder.SeedAsync(ctx, CatalogPath, MethodologyPath);
        await FrameworkSeeder.SeedAssessmentRulesAsync(ctx, RulesPath, MethodologyPath);
        await FrameworkSeeder.SeedSignalMappingsAsync(ctx);
    }

    private AegisScoreDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(null));
}

/// <summary>
/// [AEGIS-MVP-SCORE-GUARD-SIEM-01] Reparo CONSERVADOR de estados legados (SQLite efêmero, framework mínimo): o
/// alerta SIEM aposentado é reprojetado a partir da evidência remanescente OU retraído para "não avaliado" (nunca
/// NonCompliant); tenant sem sinal legado é intocado; reexecução é idempotente; crédito documental elegível reaparece.
/// </summary>
public sealed class ScoreGuardSiemRepairTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string RetiredKey = "siem.alert.highSeverity";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public ScoreGuardSiemRepairTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        IngestionTestData.SeedFrameworkAndMappings(ctx);   // framework mínimo, mappings JÁ pós-aposentadoria
        ctx.Tenants.AddRange(
            new Tenant { Id = Tenant, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = OtherTenant, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Repair_SemEvidenciaRemanescente_RetraiParaNaoAvaliado_NuncaNonCompliant_PreservaOutros()
    {
        var siem = SeedConnector(Tenant, ConnectorCapability.Siem);
        SeedSignal(Tenant, siem, RetiredKey, mapped: new[] { "DE.AE-02", "DE.CM-01" });
        // Estado INFLADO por telemetria (o alerta o tornou Compliant) + um controle NÃO afetado que deve sobreviver.
        SeedTelemetryState(Tenant, "DE.AE-02", ControlStatus.Compliant, 10);
        SeedTelemetryState(Tenant, "RS.MI-01", ControlStatus.Compliant, 10);   // não afetado

        var changed = await LegacySiemAlertScoreRepair.RepairAsync(_options, NullLoggerFactory.Instance);

        changed.Should().Be(1, "só DE.AE-02 foi retraído");
        await using var db = NewContext(Tenant);
        (await db.TenantControlStates.Include(x => x.Subcategory).AnyAsync(x => x.Subcategory!.Code == "DE.AE-02"))
            .Should().BeFalse("sem evidência válida remanescente → estado retraído (volta a NÃO avaliado), nunca NonCompliant");
        (await db.TenantControlStates.Include(x => x.Subcategory).SingleAsync(x => x.Subcategory!.Code == "RS.MI-01"))
            .Status.Should().Be(ControlStatus.Compliant, "controle NÃO afetado é preservado");
    }

    [Fact]
    public async Task Repair_DeCm01SustentadoPorOutraEvidencia_NaoPerdeVeredicto()
    {
        var siem = SeedConnector(Tenant, ConnectorCapability.Siem);
        var edr = SeedConnector(Tenant, ConnectorCapability.Edr);
        SeedSignal(Tenant, siem, RetiredKey, mapped: new[] { "DE.AE-02", "DE.CM-01" });
        // EDR comprova DE.CM-01 de forma determinística e VÁLIDA (edr.threat.blocked → event.controlProven).
        SeedSignal(Tenant, edr, "edr.threat.blocked", numericValue: 1, mapped: new[] { "DE.CM-01", "RS.MI-01" });
        SeedTelemetryState(Tenant, "DE.CM-01", ControlStatus.Compliant, 10);

        await LegacySiemAlertScoreRepair.RepairAsync(_options, NullLoggerFactory.Instance);

        await using var db = NewContext(Tenant);
        var deCm = await db.TenantControlStates.Include(x => x.Subcategory).SingleAsync(x => x.Subcategory!.Code == "DE.CM-01");
        deCm.Status.Should().Be(ControlStatus.Compliant, "a evidência do EDR (válida) ainda sustenta o veredito — não é retraído");
        deCm.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task Repair_TenantSemSinalLegado_NaoEhAlterado()
    {
        // OtherTenant tem um estado telemétrico afetado, mas NÃO possui o sinal legado → fora de escopo, intocado.
        SeedTelemetryState(OtherTenant, "DE.AE-02", ControlStatus.Compliant, 10);
        // Tenant no escopo (com sinal legado), para o reparo de fato rodar.
        var siem = SeedConnector(Tenant, ConnectorCapability.Siem);
        SeedSignal(Tenant, siem, RetiredKey, mapped: new[] { "DE.AE-02" });
        SeedTelemetryState(Tenant, "DE.AE-02", ControlStatus.Compliant, 10);

        await LegacySiemAlertScoreRepair.RepairAsync(_options, NullLoggerFactory.Instance);

        await using var other = NewContext(OtherTenant);
        (await other.TenantControlStates.Include(x => x.Subcategory).SingleAsync(x => x.Subcategory!.Code == "DE.AE-02"))
            .Status.Should().Be(ControlStatus.Compliant, "tenant SEM sinal legado nunca é alcançado pelo reparo");
    }

    [Fact]
    public async Task Repair_Idempotente_ReexecucaoNaoAltera()
    {
        var siem = SeedConnector(Tenant, ConnectorCapability.Siem);
        SeedSignal(Tenant, siem, RetiredKey, mapped: new[] { "DE.AE-02" });
        SeedTelemetryState(Tenant, "DE.AE-02", ControlStatus.Compliant, 10);

        (await LegacySiemAlertScoreRepair.RepairAsync(_options, NullLoggerFactory.Instance)).Should().Be(1);
        (await LegacySiemAlertScoreRepair.RepairAsync(_options, NullLoggerFactory.Instance))
            .Should().Be(0, "a 2ª execução não encontra estado telemétrico inflado — no-op");

        await using var db = NewContext(Tenant);
        (await db.TenantControlStates.CountAsync()).Should().Be(0, "DE.AE-02 permanece retraído após reexecução");
    }

    [Fact]
    public async Task Repair_AposRetracao_CreditoDocumentalElegivelReaparece()
    {
        var siem = SeedConnector(Tenant, ConnectorCapability.Siem);
        SeedSignal(Tenant, siem, RetiredKey, mapped: new[] { "DE.AE-02" });
        SeedTelemetryState(Tenant, "DE.AE-02", ControlStatus.Compliant, 10);
        SeedEligibleDocument(Tenant, "DE.AE-02", confidence: 0.85);   // trecho literal + confiança >= limiar

        await LegacySiemAlertScoreRepair.RepairAsync(_options, NullLoggerFactory.Instance);

        await using var db = NewContext(Tenant);
        var deAe = await db.TenantControlStates.Include(x => x.Subcategory).SingleAsync(x => x.Subcategory!.Code == "DE.AE-02");
        deAe.LastVerdictSource.Should().Be(VerdictSource.Documentary, "após retrair a telemetria, o crédito documental elegível reaparece");
        deAe.Status.Should().Be(ControlStatus.MitigatedByThirdParty, "crédito documental parcial (reusa o reconciliador existente)");
    }

    // ---- Harness --------------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private Guid SeedConnector(Guid tenant, ConnectorCapability capability)
    {
        using var db = NewContext(tenant);
        var cfg = new ConnectorConfig
        {
            TenantId = tenant, Provider = ConnectorProvider.Generic, Capability = capability,
            DisplayName = $"Generic {capability}", AuthType = ConnectorAuthType.ApiKey, Enabled = true,
        };
        db.Connectors.Add(cfg);
        db.SaveChanges();
        return cfg.Id;
    }

    private void SeedSignal(
        Guid tenant, Guid connectorId, string signalKey, string[] mapped,
        double? numericValue = 3, string? unit = "count")
    {
        using var db = NewContext(tenant);
        db.Signals.Add(new EvidenceSignal
        {
            ConnectorConfigId = connectorId, SignalKey = signalKey, NumericValue = numericValue, Unit = unit,
            MappedSubcategoryCodes = mapped.ToList(), CollectedAt = DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
        });
        db.SaveChanges();
    }

    private void SeedTelemetryState(Guid tenant, string code, ControlStatus status, int score)
    {
        using var db = NewContext(tenant);
        var subId = db.Subcategories.Single(s => s.Code == code).Id;
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = status, CurrentScore = score,
            LastVerdictSource = VerdictSource.Telemetry, AiEvidence = "legado",
        });
        db.SaveChanges();
    }

    private void SeedEligibleDocument(Guid tenant, string code, double confidence)
    {
        using var db = NewContext(tenant);
        var doc = new GovernanceDocument
        {
            Title = "Política de Monitoramento", FileName = "monitoramento.pdf",
            AnalysisStatus = AiAnalysisStatus.Analyzed, AnalyzedAt = DateTimeOffset.UtcNow,
        };
        db.GovernanceDocuments.Add(doc);
        db.SaveChanges();
        db.DocumentControlMappings.Add(new DocumentControlMapping
        {
            GovernanceDocumentId = doc.Id, SubcategoryCode = code, Confidence = confidence,
            EvidenceQuote = "trecho literal que sustenta o controle", Evidence = "racional da análise",
        });
        db.SaveChanges();
    }
}

/// <summary>
/// [AEGIS-MVP-SCORE-GUARD-SIEM-01] Reparo de estados legados em PostgreSQL REAL (gate <c>AEGIS_TEST_PG</c>): schema
/// aplicado, framework/sinal legado/estado inflado semeados, reparo executado, resultado final confirmado e a
/// reexecução idempotente. Entra no job PostgreSQL do CI (nenhum teste PostgreSQL é pulado com o gate exigido).
/// </summary>
public sealed class ScoreGuardSiemRepairPostgresTests
{
    private const string RetiredKey = "siem.alert.highSeverity";

    [Fact]
    public async Task Repair_RetraiEstadoInflado_Idempotente_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado (no CI o gate exige e falha se ausente)
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        Guid connectorId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.EnsureCreatedAsync();
            IngestionTestData.SeedFrameworkAndMappings(db);   // mappings JÁ pós-aposentadoria (sem o alerta)
            db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var cfg = new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Generic, Capability = ConnectorCapability.Siem,
                DisplayName = "Generic SIEM", AuthType = ConnectorAuthType.ApiKey, Enabled = true,
            };
            db.Connectors.Add(cfg);
            await db.SaveChangesAsync();
            connectorId = cfg.Id;

            db.Signals.Add(new EvidenceSignal
            {
                ConnectorConfigId = connectorId, SignalKey = RetiredKey, NumericValue = 3, Unit = "count",
                MappedSubcategoryCodes = new() { "DE.AE-02", "DE.CM-01" }, CollectedAt = DateTimeOffset.UtcNow,
            });
            var deAeId = await db.Subcategories.Where(s => s.Code == "DE.AE-02").Select(s => s.Id).SingleAsync();
            db.TenantControlStates.Add(new TenantControlState
            {
                SubcategoryId = deAeId, Status = ControlStatus.Compliant, CurrentScore = 10,
                LastVerdictSource = VerdictSource.Telemetry, AiEvidence = "legado",
            });
            await db.SaveChangesAsync();
        }

        var changed = await LegacySiemAlertScoreRepair.RepairAsync(opt, NullLoggerFactory.Instance);
        changed.Should().Be(1, "o único estado telemétrico inflado (DE.AE-02) foi retraído");

        await using (var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await assert.TenantControlStates.CountAsync())
                .Should().Be(0, "DE.AE-02 retraído para NÃO avaliado; nenhum NonCompliant inventado");
            (await assert.Signals.CountAsync())
                .Should().Be(1, "o EvidenceSignal legado é preservado como registro auditável");
        }

        // Idempotência real: 2ª execução não altera nada.
        (await LegacySiemAlertScoreRepair.RepairAsync(opt, NullLoggerFactory.Instance))
            .Should().Be(0, "reexecução no PostgreSQL real é no-op");
    }
}
