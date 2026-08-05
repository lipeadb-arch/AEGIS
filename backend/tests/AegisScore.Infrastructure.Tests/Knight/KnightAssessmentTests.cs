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
/// Testes FOCADOS do AEGIS KNIGHT MULTICOLETOR: preservação do Demo (score 23), avaliação por fatos
/// normalizados, ausência de dado → NotEvaluated (nunca Passed), seleção de coletor por fonte, um coletor
/// Google Workspace FALSO percorrendo o pipeline sem tocar o núcleo, falha real que NÃO cai para Demo,
/// isolamento de tenant, metadados de fonte persistidos e a IA sem autoridade sobre veredito/score.
/// </summary>
public sealed class KnightAssessmentTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string ValidAiJson =
        """{"executiveSummary":"ok","priorityRisks":[{"title":"R","rationale":"x","indicatorIds":["AK-ENTRA-001"]}],"recommendedActions":[],"correlations":[],"collectionGaps":[]}""";

    private readonly SqliteConnection _connection;

    public KnightAssessmentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) Preservação do Demo: score 23 / cobertura 100% / mistura 1-3-1 -------------------------

    [Fact]
    public async Task Demo_ScoreCoverageAndMix_Preserved()
    {
        var result = await new DemoKnightCollector().CollectAsync(new KnightCollectionContext(TenantA, new KnightDemoConfiguration()));
        var evaluated = KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.Demo);
        var score = KnightScoreFormula.Compute(evaluated.Select(e => (e.Definition.Severity, e.Status)));

        evaluated.Should().HaveCount(5, "o perfil Demo aplica os 5 indicadores compartilhados");
        score.Score.Should().Be(23d);
        score.Coverage.Should().Be(100d);
        score.PassedCount.Should().Be(1);
        score.ExposedCount.Should().Be(3);
        score.MitigatedCount.Should().Be(1);
    }

    // ---- 2) Score null sem indicador avaliado ------------------------------------------------------

    [Theory]
    [InlineData(KnightIndicatorStatus.NotEvaluated)]
    [InlineData(KnightIndicatorStatus.Error)]
    [InlineData(KnightIndicatorStatus.NotApplicable)]
    public void Score_NoEvaluatedIndicator_IsNull(KnightIndicatorStatus status)
    {
        var result = KnightScoreFormula.Compute(Enumerable.Repeat((SeverityLevel.Critical, status), 4));
        result.Score.Should().BeNull();
    }

    // ---- 3) Cinco regras determinísticas sobre os FATOS do Demo ------------------------------------

    [Theory]
    [InlineData("AK-ENTRA-001", KnightIndicatorStatus.Exposed)]
    [InlineData("AK-ENTRA-002", KnightIndicatorStatus.Exposed)]
    [InlineData("AK-ENTRA-003", KnightIndicatorStatus.Passed)]
    [InlineData("AK-ENTRA-004", KnightIndicatorStatus.Exposed)]
    [InlineData("AK-ENTRA-005", KnightIndicatorStatus.Mitigated)]
    public async Task Rules_DemoFacts_ProduceExpectedVerdict(string indicatorId, KnightIndicatorStatus expected)
    {
        var result = await new DemoKnightCollector().CollectAsync(new KnightCollectionContext(TenantA, new KnightDemoConfiguration()));
        var r = KnightIndicatorEvaluator.Evaluate(result.Facts, KnightSourceType.Demo).Single(x => x.Definition.Id == indicatorId);
        r.Status.Should().Be(expected);
    }

    // ---- 4) Mitigated (AK-ENTRA-005) exige controle compensatório COMPROVADO -----------------------

    [Theory]
    [InlineData(0, "none", KnightIndicatorStatus.Passed)]
    [InlineData(2, "none", KnightIndicatorStatus.Exposed)]
    [InlineData(2, "unproven", KnightIndicatorStatus.Exposed)]
    [InlineData(2, "proven", KnightIndicatorStatus.Mitigated)]
    public void Rule005_MitigatedRequiresProvenControl(int exempt, string mode, KnightIndicatorStatus expected)
    {
        var obs = new List<KnightObservation> { KnightObservation.OfCount(KnightSignalKey.ServiceAccountsMfaExempt, exempt) };
        if (mode == "proven") obs.Add(KnightObservation.OfFlag(KnightSignalKey.ServiceAccountMfaExemptionProven, true));
        if (mode == "unproven") obs.Add(KnightObservation.OfFlag(KnightSignalKey.ServiceAccountMfaExemptionProven, false));

        var r = KnightIndicatorEvaluator.Evaluate(new KnightFactSet(obs), KnightSourceType.Demo).Single(x => x.Definition.Id == "AK-ENTRA-005");

        r.Status.Should().Be(expected);
    }

    // ---- 5) Dado ausente → NotEvaluated com motivo — NUNCA Passed ----------------------------------

    [Fact]
    public void MissingFacts_YieldNotEvaluatedWithReason_NeverPassed()
    {
        var results = KnightIndicatorEvaluator.Evaluate(KnightFactSet.Empty, KnightSourceType.MicrosoftEntraId);

        results.Should().NotContain(r => r.Status == KnightIndicatorStatus.Passed, "ausência de dado nunca vira conformidade");
        results.Where(r => r.Status == KnightIndicatorStatus.NotEvaluated)
            .Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.NotEvaluatedReason));
        results.Should().NotBeEmpty();
    }

    // ---- 6) Execução Demo persiste metadados de FONTE; score preservado ---------------------------

    [Fact]
    public async Task RunDemo_PersistsSourceMetadata_AndScore()
    {
        KnightAssessment a;
        await using (var db = NewContext(TenantA))
            a = await ServiceFor(db, TenantA, new[] { (IKnightCollector)new DemoKnightCollector() },
                DemoConfig(), new FakeLlmClient(ValidAiJson)).RunDemoAssessmentAsync();

        a.SourceType.Should().Be(KnightSourceType.Demo);
        a.SourceState.Should().Be(KnightSourceState.Completed);
        a.Mode.Should().Be(KnightAssessmentMode.Demo);
        a.Score.Should().Be(23d);
        a.Coverage.Should().Be(100d);
        a.Indicators.Should().HaveCount(5);
        a.Indicators.Should().OnlyContain(i => i.SourceType == KnightSourceType.Demo);
        a.Capabilities.Should().NotBeEmpty();

        await using var assert = NewContext(TenantA);
        var run = await assert.KnightAssessmentRuns.Include(r => r.Indicators).SingleAsync();
        run.SourceType.Should().Be(KnightSourceType.Demo);
        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.CapabilitiesJson.Should().NotBeNullOrWhiteSpace();
        run.Indicators.Should().OnlyContain(i => i.SourceType == KnightSourceType.Demo);
    }

    // ---- 7) Isolamento de tenant -------------------------------------------------------------------

    [Fact]
    public async Task GetById_OtherTenant_ReturnsNull()
    {
        Guid runId;
        await using (var dbA = NewContext(TenantA))
            runId = (await ServiceFor(dbA, TenantA, new[] { (IKnightCollector)new DemoKnightCollector() },
                DemoConfig(), new FakeLlmClient(ValidAiJson)).RunDemoAssessmentAsync()).Id;

        await using (var dbB = NewContext(TenantB))
            (await ServiceFor(dbB, TenantB, new[] { (IKnightCollector)new DemoKnightCollector() },
                DemoConfig(), new FakeLlmClient(ValidAiJson)).GetByIdAsync(runId)).Should().BeNull();
    }

    // ---- 8) Fonte real SEM configuração: lança e NÃO cria run (não cai para Demo) ------------------

    [Fact]
    public async Task RunAssessment_RealSourceNotConfigured_Throws_AndPersistsNothing()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA, new[] { (IKnightCollector)new DemoKnightCollector() },
            new FakeConfigProvider(s => new KnightSourceNotConfigured(s)), new FakeLlmClient(ValidAiJson));

        var act = () => svc.RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);

        await act.Should().ThrowAsync<KnightSourceNotConfiguredException>();
        (await db.KnightAssessmentRuns.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    // ---- 9) Falha REAL da coleta é persistida com o estado real — NUNCA vira Demo ------------------

    [Fact]
    public async Task RunAssessment_RealCollectionFails_PersistsRealState_NotDemo()
    {
        var failResult = new KnightCollectionResult(
            KnightSourceType.MicrosoftEntraId, KnightSourceState.AuthenticationFailure, "Microsoft Entra ID",
            KnightFactSet.Empty, Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow, "auth failed");

        KnightAssessment a;
        await using (var db = NewContext(TenantA))
            a = await ServiceFor(db, TenantA,
                new[] { (IKnightCollector)new DemoKnightCollector(), new FakeCollector(KnightSourceType.MicrosoftEntraId, failResult) },
                new FakeConfigProvider(s => new TestConfiguredSource(s)), new FakeLlmClient(ValidAiJson))
                .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);

        a.SourceType.Should().Be(KnightSourceType.MicrosoftEntraId);
        a.SourceState.Should().Be(KnightSourceState.AuthenticationFailure);
        a.Mode.Should().Be(KnightAssessmentMode.Live);
        a.Source.Should().NotContain("Demonstra", "uma falha real jamais é apresentada como Demo");
        a.Score.Should().BeNull("sem fatos, nenhum indicador é avaliado (não é 0, não é Demo)");
        a.Indicators.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);
    }

    // ---- 10) Coletor Google Workspace FALSO percorre o pipeline sem tocar o núcleo -----------------

    [Fact]
    public async Task FakeGoogleWorkspaceCollector_FlowsThroughPipeline_Unchanged()
    {
        var googleFacts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 4),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 0),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithMailbox, 0),
            KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 0),
            KnightObservation.OfCount(KnightSignalKey.ServiceAccountsMfaExempt, 0),
        });
        var googleResult = new KnightCollectionResult(
            KnightSourceType.GoogleWorkspace, KnightSourceState.Completed, "Google Workspace (fake)",
            googleFacts, new[] { new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected) },
            DateTimeOffset.UtcNow);

        KnightAssessment a;
        await using (var db = NewContext(TenantA))
            a = await ServiceFor(db, TenantA,
                new[] { (IKnightCollector)new DemoKnightCollector(), new FakeCollector(KnightSourceType.GoogleWorkspace, googleResult) },
                new FakeConfigProvider(s => new TestConfiguredSource(s)), new FakeLlmClient(ValidAiJson))
                .RunAssessmentAsync(KnightSourceType.GoogleWorkspace);

        a.SourceType.Should().Be(KnightSourceType.GoogleWorkspace);
        a.Status.Should().Be(KnightRunStatus.Completed);
        // 5 indicadores compartilhados (avaliados por este fake) + 6 GoogleOnly (AK-GWS-001..006, sem fatos aqui
        // → NotEvaluated). O núcleo não muda: os aplicáveis à fonte são avaliados sem tratamento especial.
        a.Indicators.Should().HaveCount(11, "5 compartilhados + 6 GoogleOnly aplicáveis ao Google Workspace");
        a.Indicators.Should().OnlyContain(i => i.SourceType == KnightSourceType.GoogleWorkspace);

        await using var assert = NewContext(TenantA);
        (await assert.KnightAssessmentRuns.CountAsync()).Should().Be(1);
    }

    // ---- 11) IA indisponível não altera veredito/score (fallback) ---------------------------------

    [Fact]
    public async Task Ai_Unavailable_DoesNotAlterScoreOrVerdicts()
    {
        KnightAssessment a;
        await using (var db = NewContext(TenantA))
            a = await ServiceFor(db, TenantA, new[] { (IKnightCollector)new DemoKnightCollector() },
                DemoConfig(), new ThrowingLlmClient()).RunDemoAssessmentAsync();

        a.Score.Should().Be(23d, "a IA não decide/altera o score");
        a.ExposedCount.Should().Be(3);
        a.AdvisoryFromAi.Should().BeFalse();
        a.Advisory.Should().NotBeNull("o fallback garante narrativa mesmo sem IA");
    }

    // ---- infraestrutura do teste -------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IKnightSourceConfigurationProvider DemoConfig() =>
        new FakeConfigProvider(_ => new KnightDemoConfiguration());

    private static IAegisKnightAssessmentService ServiceFor(
        AegisScoreDbContext db, Guid? tenantId, IEnumerable<IKnightCollector> collectors,
        IKnightSourceConfigurationProvider config, ILLMClient llm) =>
        new AegisKnightAssessmentService(
            db, new KnightCollectorRegistry(collectors), config, new KnightAdvisoryGenerator(llm), new SystemTenantContext(tenantId));

    /// <summary>Config de fonte "configurada" genérica só para teste (IsConfigured=true, sem credencial).</summary>
    private sealed record TestConfiguredSource(KnightSourceType Src) : KnightSourceConfiguration
    {
        public override KnightSourceType Source => Src;
    }

    private sealed class FakeConfigProvider : IKnightSourceConfigurationProvider
    {
        private readonly Func<KnightSourceType, KnightSourceConfiguration> _resolve;
        public FakeConfigProvider(Func<KnightSourceType, KnightSourceConfiguration> resolve) => _resolve = resolve;
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult(_resolve(source));
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed class FakeCollector : IKnightCollector
    {
        private readonly KnightCollectionResult _result;
        public FakeCollector(KnightSourceType source, KnightCollectionResult result) { Source = source; _result = result; }
        public KnightSourceType Source { get; }
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private sealed class FakeLlmClient : ILLMClient
    {
        private readonly string _response;
        public FakeLlmClient(string response) => _response = response;
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(_response);
    }

    private sealed class ThrowingLlmClient : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new InvalidOperationException("IA indisponível (teste).");
    }
}
