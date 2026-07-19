using AegisScore.Application.Abstractions;
using AegisScore.Application.Assessment;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Queries;

/// <summary>
/// Blindagem contra evidência VELHA e documento NÃO ACEITO — as duas formas de um painel de postura
/// mentir por omissão. Um conector que morreu deixa a linha no banco e o controle continuaria "coberto";
/// um upload sem processamento pareceria política vigente. Aqui o relógio é injetado
/// (<see cref="FakeTimeProvider"/>), então o TTL é testado sem esperar 72 horas.
/// </summary>
public sealed class SignalFreshnessTests : IDisposable
{
    private const string TelemetryControl = "PR.AA-01";     // regra com fonte de ferramenta
    private const string DocumentaryControl = "GV.PO-01";   // regra MANUAL_AUDIT_REQUIRED
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private Guid _telemetrySubId;
    private Guid _documentarySubId;

    public SignalFreshnessTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalogAndRules(ctx);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SinalDENTRO_daJanela_NaoGeraLacuna()
    {
        await GivenTelemetryVerdictAt(Now.AddHours(-71));   // 1h antes do limite de 72h

        var row = await SingleRowAsync(TelemetryControl, freshnessHours: 72);

        row.MissingRequirements.Should().BeEmpty("um sinal de 71h ainda prova o controle");
    }

    [Fact]
    public async Task SinalFORA_daJanela_ViraLacunaDeTelemetria()
    {
        await GivenTelemetryVerdictAt(Now.AddHours(-73));   // 1h além do limite

        var row = await SingleRowAsync(TelemetryControl, freshnessHours: 72);

        var gap = row.MissingRequirements.Should().ContainSingle().Subject;
        gap.Type.Should().Be(nameof(ComplianceRequirementType.Telemetry));
        gap.SourceIdentifier.Should().Be("Entra ID");
        gap.Description.Should().Contain("OBSOLETO",
            "o operador precisa distinguir conector que nunca foi ligado de conector que PAROU");
    }

    [Fact]
    public async Task LacunaDeObsolescencia_DizHaQuantoTempo_ENaoSoQueFalta()
    {
        await GivenTelemetryVerdictAt(Now.AddDays(-5));

        var row = await SingleRowAsync(TelemetryControl, freshnessHours: 72);

        row.MissingRequirements.Should().ContainSingle()
            .Which.Description.Should().Contain("5 dia(s)");
    }

    [Fact]
    public async Task JanelaDesligada_NuncaMarcaObsoleto()
    {
        // Fail-safe: uma configuração zerada não pode transformar o painel inteiro em ponto cego.
        await GivenTelemetryVerdictAt(Now.AddYears(-2));

        var row = await SingleRowAsync(TelemetryControl, freshnessHours: 0);

        row.MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task VerdictoDOCUMENTAL_NaoContaComoSinalDeTelemetria()
    {
        // Um PDF recém-processado não prova o controle TÉCNICO: a fonte é Documentary, então para o eixo
        // de telemetria o controle segue sem sinal — ainda que a data seja de agora mesmo.
        await GivenVerdictAt(TelemetryControl, Now, VerdictSource.Documentary);

        var row = await SingleRowAsync(TelemetryControl, freshnessHours: 72);

        row.MissingRequirements.Should().ContainSingle()
            .Which.Type.Should().Be(nameof(ComplianceRequirementType.Telemetry));
    }

    // ---- Validação de documento: processado E ACEITO, não apenas enviado -------------

    [Fact]
    public async Task CoberturaPARCIAL_NaoContaComoDocumentoVerificado()
    {
        // Parcial = o RAG leu e NÃO se convenceu (confiança abaixo do limiar). Aceitá-la como prova
        // deixaria um rascunho de política pontuar como norma vigente.
        await GivenVerdictAt(DocumentaryControl, Now, VerdictSource.Documentary);
        await GivenCoverage(DocumentaryControl, CoverageStatus.Parcial, CoverageEvidenceSource.Document);

        var row = await SingleRowAsync(DocumentaryControl, freshnessHours: 72);

        row.MissingRequirements.Should().ContainSingle()
            .Which.Type.Should().Be(nameof(ComplianceRequirementType.Documentation));
    }

    [Fact]
    public async Task CoberturaPorENTREVISTA_NaoContaComoDocumentoVerificado()
    {
        // Entrevista é auto-declaração do auditado. Tratá-la como prova documental deixaria o auditado
        // atestar a si mesmo — o oposto de uma auditoria.
        await GivenVerdictAt(DocumentaryControl, Now, VerdictSource.Documentary);
        await GivenCoverage(DocumentaryControl, CoverageStatus.Coberto, CoverageEvidenceSource.Interview);

        var row = await SingleRowAsync(DocumentaryControl, freshnessHours: 72);

        row.MissingRequirements.Should().ContainSingle()
            .Which.Type.Should().Be(nameof(ComplianceRequirementType.Documentation));
    }

    [Fact]
    public async Task CoberturaCOBERTA_porDOCUMENTO_SatisfazOEixoDocumental()
    {
        await GivenVerdictAt(DocumentaryControl, Now, VerdictSource.Documentary);
        await GivenCoverage(DocumentaryControl, CoverageStatus.Coberto, CoverageEvidenceSource.Document);

        var row = await SingleRowAsync(DocumentaryControl, freshnessHours: 72);

        row.MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task LacunaDerivada_NaoDUPLICA_aQueOMotorJaPersistiu()
    {
        // O motor viu o payload cru e gravou a lacuna; esta camada só vê datas. Emitir a segunda faria a
        // UI listar o mesmo problema duas vezes.
        await GivenTelemetryVerdictAt(Now.AddDays(-9), new MissingRequirement(
            ComplianceRequirementType.Telemetry, "Entra ID", "Conector não configurado."));

        var row = await SingleRowAsync(TelemetryControl, freshnessHours: 72);

        row.MissingRequirements.Should().ContainSingle()
            .Which.Description.Should().Be("Conector não configurado.", "a do motor tem precedência");
    }

    // ---- harness -------------------------------------------------------------------

    private Task GivenTelemetryVerdictAt(DateTimeOffset at, params MissingRequirement[] gaps) =>
        GivenVerdictAt(TelemetryControl, at, VerdictSource.Telemetry, gaps);

    private async Task GivenVerdictAt(
        string code, DateTimeOffset at, VerdictSource source, params MissingRequirement[] gaps)
    {
        await using var db = NewContext(TenantA);
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = code == TelemetryControl ? _telemetrySubId : _documentarySubId,
            Status = ControlStatus.NonCompliant,
            CurrentScore = 0,
            LastVerdictSource = source,
            LastEvaluatedAt = at,
            MissingRequirements = gaps.ToList(),
        });
        await db.SaveChangesAsync();
    }

    private async Task GivenCoverage(string code, CoverageStatus status, CoverageEvidenceSource source)
    {
        await using var db = NewContext(TenantA);
        db.SubcategoryCoverages.Add(new SubcategoryCoverage
        {
            SubcategoryCode = code,
            Status = status,
            EvidenceSource = source,
            LastEvaluatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Application.Queries.TenantControlStateDto> SingleRowAsync(string code, int freshnessHours)
    {
        await using var db = NewContext(TenantA);
        var query = new ControlStateDashboardQuery(
            db,
            Options.Create(new ScoringOptions { DefaultSignalFreshnessHours = freshnessHours }),
            new FakeTimeProvider(Now));

        var rows = await query.GetDashboardAsync();
        return rows.Single(r => r.SubcategoryCode == code);
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private void SeedCatalogAndRules(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { Code = "PR", Name = "PROTECT" };
        var prAa = new NistCategory { Code = "PR.AA", Name = "Identity" };
        var telemetrySub = new NistSubcategory
        {
            Code = TelemetryControl, Description = "Identities managed", MaxScorePoints = 20,
        };
        prAa.Subcategories.Add(telemetrySub);
        pr.Categories.Add(prAa);

        var gv = new NistFunction { Code = "GV", Name = "GOVERN" };
        var gvPo = new NistCategory { Code = "GV.PO", Name = "Policy" };
        var documentarySub = new NistSubcategory
        {
            Code = DocumentaryControl, Description = "Policy established", MaxScorePoints = 5,
        };
        gvPo.Subcategories.Add(documentarySub);
        gv.Categories.Add(gvPo);

        fv.Functions.Add(pr);
        fv.Functions.Add(gv);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();

        _telemetrySubId = telemetrySub.Id;
        _documentarySubId = documentarySub.Id;

        ctx.AssessmentRules.AddRange(
            new AegisAssessmentRule
            {
                SubcategoryCode = TelemetryControl,
                SubcategoryId = _telemetrySubId,
                EvaluationMetrics = new() { "Cobertura de MFA privilegiado (IA-2(1))" },
                CalculationLogic = "score = com_mfa / privilegiadas",
                EvidenceRequirements = new() { "Entra ID: authenticationMethods e sign-in logs" },
            },
            new AegisAssessmentRule
            {
                SubcategoryCode = DocumentaryControl,
                SubcategoryId = _documentarySubId,
                EvaluationMetrics = new() { "Política aprovada e vigente (PM-1)" },
                CalculationLogic = "score = aprovada AND revisada_12m",
                EvidenceRequirements = new() { RuleEvaluator.ManualAuditToken },
            });
        ctx.SaveChanges();
    }
}
