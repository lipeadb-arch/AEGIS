using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// Testes FOCADOS da primeira vertical do AEGIS KNIGHT: a fórmula própria de score/cobertura, as cinco
/// regras determinísticas, o isolamento de tenant, a resiliência à indisponibilidade da IA e a identificação
/// inequívoca do modo demonstração. As partes puras (fórmula/regras) rodam sem banco; o serviço roda sobre
/// SQLite in-memory (banco relacional real: Global Query Filter e stamping fail-closed de verdade).
/// </summary>
public sealed class KnightAssessmentTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public KnightAssessmentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();   // KNIGHT não depende do catálogo NIST — nenhum seed necessário
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) Fórmula de score e cobertura (grounded no snapshot demo real) ----------------------------

    [Fact]
    public async Task Score_DemoMix_ComputesScoreAndCoverage()
    {
        var snapshot = await new DemoKnightPostureProvider().CollectAsync(TenantA);
        var evaluated = KnightIndicatorEvaluator.Evaluate(snapshot);

        var result = KnightScoreFormula.Compute(evaluated.Select(e => (e.Definition.Severity, e.Status)));

        // 0·10 + 0·7 + 1·4 + 0·4 + 0.5·7 = 7.5 ; pesos avaliados = 32 ; round(100·7.5/32) = 23.
        result.Score.Should().Be(23d);
        result.Coverage.Should().Be(100d, "todos os 5 indicadores são aplicáveis e foram avaliados");
        result.PassedCount.Should().Be(1);
        result.ExposedCount.Should().Be(3);
        result.MitigatedCount.Should().Be(1);
        result.FormulaVersion.Should().Be("knight-score-v1");
    }

    // ---- 2) Score null sem indicador avaliado (falta de dado NUNCA é aprovação) -----------------------

    [Theory]
    [InlineData(KnightIndicatorStatus.NotEvaluated)]
    [InlineData(KnightIndicatorStatus.Error)]
    [InlineData(KnightIndicatorStatus.NotApplicable)]
    public void Score_NoEvaluatedIndicator_IsNullNeverZero(KnightIndicatorStatus status)
    {
        var indicators = Enumerable.Repeat((SeverityLevel.Critical, status), 4);

        var result = KnightScoreFormula.Compute(indicators);

        result.Score.Should().BeNull("sem indicador avaliado o score é null, nunca 0");
    }

    // ---- 3) Cinco regras determinísticas sobre o snapshot demo ---------------------------------------

    [Theory]
    [InlineData("AK-ENTRA-001", KnightIndicatorStatus.Exposed)]   // 2 privilegiadas sem MFA
    [InlineData("AK-ENTRA-002", KnightIndicatorStatus.Exposed)]   // 12 privilegiadas (teto 10)
    [InlineData("AK-ENTRA-003", KnightIndicatorStatus.Passed)]    // 0 privilegiadas com mailbox
    [InlineData("AK-ENTRA-004", KnightIndicatorStatus.Exposed)]   // 3 convidados inativos
    [InlineData("AK-ENTRA-005", KnightIndicatorStatus.Mitigated)] // contas técnicas + controle comprovado
    public async Task Rules_DemoSnapshot_ProduceExpectedVerdict(string indicatorId, KnightIndicatorStatus expected)
    {
        var snapshot = await new DemoKnightPostureProvider().CollectAsync(TenantA);

        var result = KnightIndicatorEvaluator.Evaluate(snapshot).Single(r => r.Definition.Id == indicatorId);

        result.Status.Should().Be(expected);
    }

    // ---- 4) Mitigated EXIGE controle compensatório comprovado (um "toggle"/declaração não basta) -------

    [Theory]
    [InlineData(0, "none", KnightIndicatorStatus.Passed)]        // sem contas isentas → conforme
    [InlineData(2, "none", KnightIndicatorStatus.Exposed)]       // isentas, sem controle → exposto
    [InlineData(2, "declared", KnightIndicatorStatus.Exposed)]   // controle DECLARADO mas não comprovado → exposto
    [InlineData(2, "proven", KnightIndicatorStatus.Mitigated)]   // controle comprovado no snapshot → mitigado
    public void Rule005_MitigatedRequiresProvenCompensatingControl(
        int exemptCount, string controlMode, KnightIndicatorStatus expected)
    {
        var snapshot = SnapshotWithServiceAccounts(exemptCount, controlMode);

        var result = KnightIndicatorEvaluator.Evaluate(snapshot).Single(r => r.Definition.Id == "AK-ENTRA-005");

        result.Status.Should().Be(expected);
    }

    // ---- 5) Execução demo persistida, modo DEMONSTRAÇÃO identificado, IA consultiva (não decide nada) --

    [Fact]
    public async Task RunDemo_PersistsAssessment_MarksDemo_AndUsesAiWithoutInventingIndicators()
    {
        // A IA cita um indicador INEXISTENTE (AK-FAKE-999) — deve ser descartado (não pode adicionar indicador).
        const string aiJson =
            """{"executiveSummary":"Resumo de teste.","priorityRisks":[{"title":"R","rationale":"x","indicatorIds":["AK-ENTRA-001","AK-FAKE-999"]}],"recommendedActions":[{"order":1,"action":"Agir","indicatorIds":["AK-ENTRA-001"]}],"correlations":[],"collectionGaps":[]}""";

        KnightAssessment assessment;
        await using (var db = NewContext(TenantA))
            assessment = await ServiceFor(db, TenantA, new FakeLlmClient(aiJson)).RunDemoAssessmentAsync();

        assessment.Mode.Should().Be(KnightAssessmentMode.Demo, "esta entrega é somente demonstração");
        assessment.Status.Should().Be(KnightRunStatus.Completed);
        assessment.CatalogVersion.Should().Be("ak-knight-v1");
        assessment.ScoreFormulaVersion.Should().Be("knight-score-v1");
        assessment.Score.Should().Be(23d);
        assessment.Coverage.Should().Be(100d);
        assessment.Indicators.Should().HaveCount(5);
        assessment.AdvisoryFromAi.Should().BeTrue();
        assessment.Advisory!.PriorityRisks.Single().IndicatorIds
            .Should().ContainSingle().Which.Should().Be("AK-ENTRA-001", "a IA não pode citar indicador inexistente");

        // Persistiu de verdade (execução + 5 resultados) sob o tenant A.
        await using var assert = NewContext(TenantA);
        (await assert.KnightAssessmentRuns.CountAsync()).Should().Be(1);
        (await assert.KnightIndicatorResults.CountAsync()).Should().Be(5);
    }

    // ---- 6) Isolamento de tenant: tenant B não lê a execução do tenant A -----------------------------

    [Fact]
    public async Task GetById_OtherTenant_ReturnsNull()
    {
        Guid runId;
        await using (var dbA = NewContext(TenantA))
            runId = (await ServiceFor(dbA, TenantA, new FakeLlmClient(ValidAiJson)).RunDemoAssessmentAsync()).Id;

        // O MESMO Id, lido pelo tenant B, não é encontrado (Global Query Filter fail-closed).
        await using (var dbB = NewContext(TenantB))
            (await ServiceFor(dbB, TenantB, new FakeLlmClient(ValidAiJson)).GetByIdAsync(runId)).Should().BeNull();

        // Sanidade: o próprio tenant A lê normalmente.
        await using var dbA2 = NewContext(TenantA);
        (await ServiceFor(dbA2, TenantA, new FakeLlmClient(ValidAiJson)).GetByIdAsync(runId)).Should().NotBeNull();
    }

    // ---- 7) IA indisponível NÃO altera nem perde o assessment (fallback determinístico) ---------------

    [Fact]
    public async Task RunDemo_AiUnavailable_DoesNotLoseOrAlterAssessment()
    {
        KnightAssessment assessment;
        await using (var db = NewContext(TenantA))
            assessment = await ServiceFor(db, TenantA, new ThrowingLlmClient()).RunDemoAssessmentAsync();

        assessment.Status.Should().Be(KnightRunStatus.Completed, "a IA indisponível não reprova o assessment");
        assessment.Score.Should().Be(23d, "o score é determinístico — a IA não o altera");
        assessment.Indicators.Should().HaveCount(5);
        assessment.AdvisoryFromAi.Should().BeFalse("o resumo veio do fallback determinístico");
        assessment.Advisory.Should().NotBeNull("o fallback garante uma narrativa mesmo sem IA");

        await using var assert = NewContext(TenantA);
        (await assert.KnightAssessmentRuns.CountAsync()).Should().Be(1, "o assessment foi persistido apesar da falha da IA");
        (await assert.KnightIndicatorResults.CountAsync()).Should().Be(5);
    }

    // ---- 8) Saneamento das citações da IA: nenhuma conclusão sem evidência em indicador conhecido -----

    [Fact]
    public async Task Advisory_OnlyInventedIndicatorId_NotAcceptedAsAi_FallsBack()
    {
        const string json =
            """{"executiveSummary":"s","priorityRisks":[{"title":"R","rationale":"x","indicatorIds":["AK-FAKE-999"]}],"recommendedActions":[],"correlations":[],"collectionGaps":[]}""";

        var result = await new KnightAdvisoryGenerator(new FakeLlmClient(json))
            .GenerateAsync(AdvisoryInputWithKnown("AK-ENTRA-001"));

        result.FromAi.Should().BeFalse("uma conclusão citando só indicador inexistente não é aproveitável → fallback");
        // O fallback determinístico cita apenas indicadores conhecidos; o inventado nunca aparece.
        result.Advisory.PriorityRisks.SelectMany(r => r.IndicatorIds).Should().NotContain("AK-FAKE-999");
    }

    [Fact]
    public async Task Advisory_ValidPlusInventedId_KeepsOnlyValid_AndIsFromAi()
    {
        const string json =
            """{"executiveSummary":"s","priorityRisks":[{"title":"R","rationale":"x","indicatorIds":["AK-ENTRA-001","AK-FAKE-999"]}],"recommendedActions":[],"correlations":[],"collectionGaps":[]}""";

        var result = await new KnightAdvisoryGenerator(new FakeLlmClient(json))
            .GenerateAsync(AdvisoryInputWithKnown("AK-ENTRA-001"));

        result.FromAi.Should().BeTrue("uma conclusão com ID conhecido é preservada");
        result.Advisory.PriorityRisks.Single().IndicatorIds.Should().Equal("AK-ENTRA-001");
    }

    // ---- 9) Cancelamento INTERNO da IA (token do chamador NÃO cancelado) → fallback, sem perder nada -----

    [Fact]
    public async Task RunDemo_InternalAiCancellation_WithoutCallerCancel_FallsBackAndCompletes()
    {
        KnightAssessment assessment;
        await using (var db = NewContext(TenantA))
            assessment = await ServiceFor(db, TenantA, new CancelingLlmClient())
                .RunDemoAssessmentAsync(CancellationToken.None);

        assessment.Status.Should().Be(KnightRunStatus.Completed, "cancelamento interno da IA não reprova o assessment");
        assessment.AdvisoryFromAi.Should().BeFalse("cancelamento interno da IA vira indisponibilidade → fallback");
        assessment.Score.Should().Be(23d, "o score determinístico é preservado");
        assessment.Indicators.Should().HaveCount(5);

        await using var assert = NewContext(TenantA);
        (await assert.KnightAssessmentRuns.CountAsync()).Should().Be(1, "a execução foi persistida antes da IA");
        (await assert.KnightIndicatorResults.CountAsync()).Should().Be(5);
    }

    // ---- Infraestrutura do teste ---------------------------------------------------------------------

    private const string ValidAiJson =
        """{"executiveSummary":"ok","priorityRisks":[],"recommendedActions":[],"correlations":[],"collectionGaps":[]}""";

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid? tenantId, ILLMClient llm) =>
        new AegisKnightAssessmentService(
            db, new DemoKnightPostureProvider(), new KnightAdvisoryGenerator(llm), new SystemTenantContext(tenantId));

    /// <summary>Snapshot demo com o nº de contas técnicas isentas e o modo do controle compensatório sob teste.</summary>
    private static KnightPostureSnapshot SnapshotWithServiceAccounts(int exemptCount, string controlMode)
    {
        var exempt = Enumerable.Range(1, exemptCount).Select(i => $"svc-{i}@demo.example.com").ToArray();
        var controls = controlMode switch
        {
            "proven" => new[]
            {
                new KnightCompensatingControl("k", "prova técnica", KnightIndicatorCategory.ServiceAccounts, true),
            },
            "declared" => new[]
            {
                new KnightCompensatingControl("k", "apenas declarado", KnightIndicatorCategory.ServiceAccounts, false),
            },
            _ => Array.Empty<KnightCompensatingControl>(),
        };

        return new KnightPostureSnapshot(
            KnightAssessmentMode.Demo, "teste", "demo.example.com", DateTimeOffset.UtcNow,
            TotalPrivilegedAccounts: 5, PrivilegedAccountsWithoutMfa: 0, PrivilegedAccountsWithMailbox: 0,
            InactiveGuestAccountsOverWindow: 0, InactiveGuestWindowDays: 30,
            MfaExemptServiceAccounts: exempt, CompensatingControls: controls);
    }

    /// <summary>Entrada mínima de advisory com um conjunto de indicadores CONHECIDOS (para o saneamento de citações).</summary>
    private static KnightAdvisoryInput AdvisoryInputWithKnown(params string[] indicatorIds) =>
        new(KnightAssessmentMode.Demo, 50, 100,
            indicatorIds.Select(id => new KnightAdvisoryIndicator(
                id, "titulo", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
                KnightIndicatorStatus.Exposed, "evidencia", 1, new[] { "PR.AA-01" }, Array.Empty<string>())).ToList());

    /// <summary>ILLMClient determinístico: devolve um texto fixo (JSON do advisory), sem heurística nem rede.</summary>
    private sealed class FakeLlmClient : ILLMClient
    {
        private readonly string _response;
        public FakeLlmClient(string response) => _response = response;
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(_response);
    }

    /// <summary>ILLMClient que SEMPRE falha — prova que a indisponibilidade da IA não impede o assessment.</summary>
    private sealed class ThrowingLlmClient : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new InvalidOperationException("IA indisponível (teste).");
    }

    /// <summary>
    /// ILLMClient que lança TaskCanceledException (OperationCanceledException) do TRANSPORTE, sem o token do
    /// chamador estar cancelado — deve virar indisponibilidade (fallback), não cancelamento propagado.
    /// </summary>
    private sealed class CancelingLlmClient : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new TaskCanceledException("cancelamento interno do transporte (teste).");
    }
}
