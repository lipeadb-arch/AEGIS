using AegisScore.Application.Abstractions;
using AegisScore.Application.Assessment;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Os dois cenários que separam "falta o log" de "falta a política", exercitados pelo fluxo REAL
/// (StubLlmClient → AegisAiEvaluatorService → ControlStateWriter → ledger), com a regra do 800-53
/// semeada no banco para que o <c>AssessmentRuleContextBuilder</c> a injete no prompt de verdade.
///
/// Meta: provar que a UI receberá o objeto TIPADO — o ícone de rede e o ícone de pasta saem de
/// <c>MissingRequirement.Type</c>, não de heurística sobre texto livre.
/// </summary>
public sealed class MissingRequirementScenarioTests : IDisposable
{
    private const string TelemetryControl = "PR.AA-01";     // regra com fonte de ferramenta
    private const string DocumentaryControl = "GV.PO-01";   // regra MANUAL_AUDIT_REQUIRED
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public MissingRequirementScenarioTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalogAndRules(ctx);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task CenarioA_BlindSpot_FonteDeTelemetriaAusente_ProduzLacunaDoTipoTelemetry()
    {
        // O payload declara que o conector de identidade não está integrado: o controle não é reprovado
        // por MÉRITO (não sabemos se o MFA existe) — é reprovado por AUSÊNCIA DE PROVA.
        const string payload =
            "Identity telemetry:\nTelemetry Source: absent\nEntraID_Login_Logs: not ingested";

        var verdict = await EvaluateAsync(TelemetryControl, payload);

        verdict.Status.Should().Be(ControlStatus.NonCompliant);
        var gap = verdict.MissingRequirements.Should().ContainSingle().Subject;
        gap.Type.Should().Be(ComplianceRequirementType.Telemetry, "a UI acende o ícone de REDE aqui");
        gap.SourceIdentifier.Should().Be("Entra ID", "o operador precisa saber QUAL conector ligar");

        // E chegou tipado ao ledger — que é o que a UI vai ler.
        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates
            .SingleAsync(s => s.Subcategory!.Code == TelemetryControl);
        state.MissingRequirements.Should().ContainSingle()
            .Which.Type.Should().Be(ComplianceRequirementType.Telemetry);
    }

    [Fact]
    public async Task CenarioB_GovernanceGap_PoliticaNaoProcessada_ProduzLacunaDoTipoDocumentation()
    {
        // Controle cuja regra é MANUAL_AUDIT_REQUIRED: nenhuma ferramenta do stack o prova sozinha.
        // Sem documento processado no Document Hub, a lacuna é de GOVERNANÇA, não de sensor.
        const string payload = "Governance review:\nPolicy_MFA_Enforcement: no processed document found";

        var verdict = await EvaluateAsync(DocumentaryControl, payload);

        verdict.Status.Should().Be(ControlStatus.NonCompliant);
        var gap = verdict.MissingRequirements.Should().ContainSingle().Subject;
        gap.Type.Should().Be(ComplianceRequirementType.Documentation, "a UI acende o ícone de PASTA aqui");
        gap.SourceIdentifier.Should().Be(RuleEvaluator.ManualAuditToken);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates
            .SingleAsync(s => s.Subcategory!.Code == DocumentaryControl);
        state.MissingRequirements.Should().ContainSingle()
            .Which.Type.Should().Be(ComplianceRequirementType.Documentation);
    }

    [Fact]
    public async Task FalhaDeMERITO_ComTelemetriaPresente_NaoProduzLacunaDeProva()
    {
        // A distinção que dá sentido ao recurso: aqui o sinal CHEGOU e mostrou o controle falhando.
        // Reportar "falta telemetria" seria mentir — e mandaria o operador configurar um conector que
        // já está funcionando.
        const string payload = "Identity telemetry:\nPrivileged MFA Coverage: 40\nConditional Access Enforced: false";

        var verdict = await EvaluateAsync(TelemetryControl, payload);

        verdict.Status.Should().Be(ControlStatus.NonCompliant);
        verdict.MissingRequirements.Should().BeEmpty(
            "lacuna de PRÁTICA não é lacuna de PROVA — o sinal existe e reprovou o controle");
    }

    [Fact]
    public async Task ControleConforme_NuncaCarregaLacuna()
    {
        const string payload = "Identity telemetry:\nPrivileged MFA Coverage: 100\nConditional Access Enforced: true";

        var verdict = await EvaluateAsync(TelemetryControl, payload);

        verdict.Status.Should().Be(ControlStatus.Compliant);
        verdict.MissingRequirements.Should().BeEmpty();
    }

    // ---- harness -------------------------------------------------------------------

    private async Task<ComplianceVerdict> EvaluateAsync(string subcategoryCode, string payload)
    {
        await using var db = NewContext(TenantA);
        var ctx = new SystemTenantContext(TenantA);
        var writer = new ControlStateWriter(db, ctx, NullLogger<ControlStateWriter>.Instance);
        var evaluator = new AegisAiEvaluatorService(
            db, new StubLlmClient(), ctx, writer, new AssessmentRuleContextBuilder(db),
            StaticAuditorPersonaProvider.Neutral);

        return await evaluator.EvaluateAsync(TenantA, subcategoryCode, payload);
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>
    /// Catálogo mínimo + as regras do 800-53 correspondentes, com o vocabulário REAL de
    /// <c>evidence_requirements</c> — fonte de ferramenta num controle, MANUAL_AUDIT_REQUIRED no outro.
    /// </summary>
    private static void SeedCatalogAndRules(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { Code = "PR", Name = "PROTECT" };
        var prAa = new NistCategory { Code = "PR.AA", Name = "Identity" };
        prAa.Subcategories.Add(new NistSubcategory
        {
            Code = TelemetryControl,
            Description = "Identities and credentials are managed.",
            MaxScorePoints = 20,
        });
        pr.Categories.Add(prAa);

        var gv = new NistFunction { Code = "GV", Name = "GOVERN" };
        var gvPo = new NistCategory { Code = "GV.PO", Name = "Policy" };
        gvPo.Subcategories.Add(new NistSubcategory
        {
            Code = DocumentaryControl,
            Description = "Organizational cybersecurity policy is established.",
            MaxScorePoints = 5,
        });
        gv.Categories.Add(gvPo);

        fv.Functions.Add(pr);
        fv.Functions.Add(gv);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();

        var subs = ctx.Subcategories.ToDictionary(s => s.Code, s => s.Id);

        ctx.AssessmentRules.AddRange(
            new AegisAssessmentRule
            {
                SubcategoryCode = TelemetryControl,
                SubcategoryId = subs[TelemetryControl],
                EvaluationMetrics = new() { "Cobertura de MFA em contas privilegiadas (IA-2(1))" },
                CalculationLogic = "score = contas_privilegiadas_com_mfa / contas_privilegiadas",
                EvidenceRequirements = new()
                {
                    "Entra ID: authenticationMethods e sign-in logs para cobertura de MFA privilegiado",
                    "Microsoft Sentinel: regras analíticas de autenticação sem MFA",
                },
            },
            new AegisAssessmentRule
            {
                SubcategoryCode = DocumentaryControl,
                SubcategoryId = subs[DocumentaryControl],
                EvaluationMetrics = new() { "Existência e vigência da política aprovada (PM-1)" },
                CalculationLogic = "score = politica_aprovada AND revisada_nos_ultimos_12_meses",
                EvidenceRequirements = new() { RuleEvaluator.ManualAuditToken },
            });
        ctx.SaveChanges();
    }
}
