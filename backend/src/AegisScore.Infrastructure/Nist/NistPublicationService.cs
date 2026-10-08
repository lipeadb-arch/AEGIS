using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Application.Remediation;
using AegisScore.Application.Scoring;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] Relatório de maturidade de uma avaliação · rodada · escopo e a sua publicação IMUTÁVEL sobre a
/// infraestrutura de fotografias existente (PostureSnapshot tipo NistMaturity, hash determinístico, gatilho append-only,
/// exportação HTML/PDF/CSV). Três garantias:
/// <list type="bullet">
/// <item>a publicação confere a impressão digital do conteúdo revisado na tela — conteúdo diferente recusa (409), nunca
/// troca a seleção em silêncio;</item>
/// <item>o relatório inteiro é congelado sob o hash: reexportar nunca consulta o estado atual;</item>
/// <item>a maturidade (1–5) fica em colunas próprias — o score 0–100 da fotografia permanece nulo.</item>
/// </list>
/// A montagem faz várias consultas (contexto, contagens, evidências, procedimentos, achados, planos); todas leem UM instante
/// do banco numa transação somente leitura REPEATABLE READ no PostgreSQL — uma alteração confirmada no meio nunca entra pela
/// metade. A transação termina ao fim da montagem: gravar a fotografia, exportar e esperar o usuário acontecem fora dela.
/// </summary>
public sealed class NistPublicationService : INistPublicationService
{
    internal const string SchemaVersion = "posture-snapshot-nist-maturity-v1";

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IControlLanguageCatalog? _language;
    private readonly MaturityScoringService _maturity = new();

    public NistPublicationService(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock, IControlLanguageCatalog? language = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _language = language;
    }

    public Task<NistMaturityReport> BuildReportAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default) =>
        InReadSnapshotAsync(() => BuildReportCoreAsync(assessmentId, cycleId, scopeId, ct), ct);

    public Task<NistMaturityReport> BuildBasisReportAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default) =>
        InReadSnapshotAsync(() => BuildBasisCoreAsync(assessmentId, cycleId, scopeId, ct), ct);

    internal const string StaleSummaryLimitation =
        "Há resumo executivo aceito para esta rodada e escopo, mas preparado sobre um estado anterior: ele não entra nesta leitura. " +
        "Revise-o e aceite de novo para incluí-lo.";

    /// <summary>
    /// [AEGIS-NIST-AI-ASSIST-01] Relatório = BASE determinística + o resumo executivo aceito, SE ele foi preparado sobre esta mesma
    /// base. Resumo de base anterior não entra (e a limitação diz isso); ausência de resumo deixa o relatório como sempre foi.
    /// </summary>
    private async Task<NistMaturityReport> BuildReportCoreAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        var core = await BuildBasisCoreAsync(assessmentId, cycleId, scopeId, ct);
        var summary = await _db.NistExecutiveSummaries.AsNoTracking().FirstOrDefaultAsync(x => x.CycleId == cycleId && x.AssessmentScopeId == scopeId, ct);
        if (summary is null) return core;
        if (!string.Equals(summary.BasisFingerprint, NistReportCanonical.Fingerprint(core), StringComparison.Ordinal))
            return core with { Limitations = core.Limitations.Append(StaleSummaryLimitation).ToList() };

        var generation = summary.AssistanceId is { } id ? await _db.NistAiAssistances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        var sections = JsonSerializer.Deserialize<List<NistReportInterpretationSection>>(summary.SectionsJson, SectionsJson)
                       ?? new List<NistReportInterpretationSection>();
        // A revisão vale só para a aceitação vigente, de outra pessoa (base já conferida acima).
        var reviewed = NistAssistService.ReviewIsCurrent(summary);
        var simulated = generation?.Mode == NistAssistEngineMode.Simulated;
        var notice = (generation is null
                         ? "Resumo executivo redigido pela pessoa."
                         : simulated
                             ? "DEMONSTRAÇÃO: texto preparado pelo motor SIMULADO da assistência (sem análise real) e aceito por pessoa."
                             : "Interpretação preparada com assistência de IA e aceita por pessoa.")
                     + " Explica os indicadores determinísticos do AEGIS desta fotografia; não calcula nem altera notas, contagens, cobertura ou classificações."
                     + (reviewed ? "" : " Sem revisão humana posterior de outra pessoa.");
        return core with
        {
            Interpretation = new NistReportInterpretation(
                sections, generation is null ? "Manual" : "Assisted", generation?.Mode.ToString(), generation?.Id,
                NistReportCanonical.Micro(generation?.GeneratedAt), generation?.RequestedByName, summary.AcceptedByName,
                NistReportCanonical.Micro(summary.AcceptedAt), summary.Edited, summary.StaleAcknowledged,
                reviewed ? summary.ReviewedByName : null, reviewed ? NistReportCanonical.Micro(summary.ReviewedAt) : null,
                summary.BasisFingerprint, notice),
        };
    }

    private static readonly JsonSerializerOptions SectionsJson = new(JsonSerializerDefaults.Web);

    private Task<T> InReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken ct) =>
        AegisScore.Infrastructure.Queries.CrossSourceFactReader.InReadSnapshotAsync(_db, read, ct);

    private async Task<NistMaturityReport> BuildBasisCoreAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        // [AEGIS-NIST-AI-ASSIST-01] Procedência do conteúdo assistido ACEITO (só campos cujo texto vigente é o incorporado).
        var assisted = await NistAssistProvenance.LoadAsync(_db, cycleId, scopeId, ct);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var clientName = await _db.Tenants.AsNoTracking().Where(t => t.Id == _tenant.TenantId).Select(t => t.Name).FirstOrDefaultAsync(ct);
        var seedName = ctx.Cycle.SeedFromCycleId is { } seedId
            ? await _db.NistCycles.AsNoTracking().Where(c => c.Id == seedId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        var evidence = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.RemovedAt == null && e.SubcategoryCode != null)
            .ToListAsync(ct);
        var plans = await PlansByFindingAsync(_db, ctx.Findings.Select(f => f.Id).ToList(), ct);

        var categories = ctx.Catalog.Functions.SelectMany(f => f.Categories.Select(c => (Fn: f, Cat: c)))
            .OrderBy(x => Array.IndexOf(FunctionOrder, x.Fn.Code)).ThenBy(x => CategoryRank(x.Cat.Code)).ThenBy(x => x.Cat.Code, StringComparer.Ordinal)
            .ToList();
        var subs = categories.SelectMany(x => x.Cat.Subcategories.OrderBy(s => s.Code, StringComparer.Ordinal)
            .Select(s => (x.Fn, x.Cat, Sub: s))).ToList();

        var states = subs.ToDictionary(x => x.Sub.Code, x => StateOf(ctx.Evaluation(x.Sub.Id), ctx.EvidenceCount(x.Sub.Code)), StringComparer.Ordinal);
        var profile = _maturity.AggregateProfile(subs.Select(x => ProfileScoreOf(x.Sub.Code, ctx.Evaluation(x.Sub.Id))));

        string TitleOf(NistSubcategory s) => _language?.Get(s.Code)?.Title ?? s.Code;

        NistReportProfile ProfileOf(ProfileScore p, string name, string? fnCode, IEnumerable<string> codes)
        {
            var set = codes.ToList();
            return new NistReportProfile(p.RefCode, name, fnCode, p.Current, p.Target, p.Gap, p.Subcategories, p.WithCurrent, p.WithTarget,
                p.WithGap, p.NotApplicable,
                set.Count(c => states[c] == NistSubcategoryStates.Evaluated),
                set.Count(c => states[c] == NistSubcategoryStates.InProgress),
                set.Count(c => states[c] == NistSubcategoryStates.PendingConfirmation),
                set.Count(c => states[c] == NistSubcategoryStates.NotEvaluated));
        }

        var functions = ctx.Catalog.Functions.Select(f =>
        {
            var p = profile.Functions.FirstOrDefault(x => x.RefCode == f.Code)
                ?? new ProfileScore(SnapshotLevel.Function, f.Code, null, null, null, 0, 0, 0, 0, 0);
            return ProfileOf(p, NistLabels.FunctionName(f.Code), null, f.Categories.SelectMany(c => c.Subcategories).Select(s => s.Code));
        }).ToList();
        var categoryProfiles = categories.Select(x =>
        {
            var p = profile.Categories.FirstOrDefault(c => c.RefCode == x.Cat.Code)
                ?? new ProfileScore(SnapshotLevel.Category, x.Cat.Code, null, null, null, 0, 0, 0, 0, 0);
            return ProfileOf(p, x.Cat.Name, x.Fn.Code, x.Cat.Subcategories.Select(s => s.Code));
        }).ToList();

        // ---- Achados e planos ----
        var findings = ctx.Findings
            .OrderBy(f => f.Status == NistFindingStatus.Open ? 0 : 1).ThenBy(f => NistLabels.SeverityRank(f.Severity.ToString()))
            .ThenBy(f => f.SubcategoryCode, StringComparer.Ordinal).ThenBy(f => f.CreatedAt.UtcTicks).ThenBy(f => f.Id)
            .Select(f =>
            {
                var current = CurrentPlan(plans.TryGetValue(f.Id, out var l) ? l : null);
                var view = NistWorkService.FindingView(f, ctx.Cycle.Name, ScopeName(ctx.Scope), _language?.Get(f.SubcategoryCode)?.Title ?? f.SubcategoryCode,
                    current);
                var fieldsOf = assisted.For(NistAssistTarget.Finding, f.Id,
                        new Dictionary<string, string?>(StringComparer.Ordinal) { ["recommendation"] = f.Recommendation })
                    .Concat(current is null ? Array.Empty<AegisScore.Application.Nist.NistAssistedFieldView>() : assisted.For(NistAssistTarget.Plan, current.Id,
                        new Dictionary<string, string?>(StringComparer.Ordinal) { ["proposedAction"] = current.ProposedAction }))
                    .ToList();
                return new NistReportFinding(
                    f.Id, f.SubcategoryCode, view.SubcategoryTitle, f.Title, f.Condition, f.Risk, f.Impact,
                    f.Severity.ToString(), NistLabels.Severity(f.Severity.ToString()), f.SeverityRationale,
                    f.Priority.ToString(), NistLabels.Priority(f.Priority.ToString()), f.PriorityRationale,
                    f.Recommendation, f.Status.ToString(), NistLabels.FindingStatus(f.Status.ToString()), f.StatusNote,
                    f.EvidenceIds.ToList(), f.CreatedByName, NistReportCanonical.Micro(f.CreatedAt),
                    view.Origin?.CurrentLevel, view.Origin?.TargetLevel, view.Origin?.Gap,
                    current is { } plan ? PlanOf(plan) : null, view.TreatmentLabel,
                    NistAssistProvenance.Report(fieldsOf));
            }).ToList();

        // ---- Subcategorias (ordem oficial) ----
        var evidenceByCode = evidence.GroupBy(e => e.SubcategoryCode!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.CreatedAt.UtcTicks).ThenBy(e => e.Id).ToList(), StringComparer.Ordinal);
        var subRows = subs.Select(x =>
        {
            var e = ctx.Evaluation(x.Sub.Id);
            var confirmed = e is { HumanConfirmed: true };
            var procs = ctx.Procedures.Where(p => p.SubcategoryCode == x.Sub.Code)
                .OrderBy(p => p.Method).ThenBy(p => p.CreatedAt.UtcTicks).ThenBy(p => p.Id)
                .Select(p => new NistReportProcedure(p.Id, p.Method.ToString(), NistLabels.Method(p.Method.ToString()), p.Procedure,
                    p.Status.ToString(), NistLabels.ProcedureStatus(p.Status.ToString()), p.Outcome?.ToString(),
                    p.Outcome is null ? null : NistLabels.Outcome(p.Outcome.ToString()), p.Observation, p.PerformedOn,
                    p.ResultRecordedByName, NistReportCanonical.Micro(p.ResultRecordedAt), p.EvidenceIds.ToList(), p.ContentOrigin.ToString()))
                .ToList();
            var ev = (evidenceByCode.TryGetValue(x.Sub.Code, out var list) ? list : new List<Evidence>())
                .Select(v => new NistReportEvidence(v.Id, v.OriginKind.ToString(), NistLabels.EvidenceOrigin(v.OriginKind.ToString()), v.Type.ToString(),
                    v.Title ?? "(sem título)", v.Notes, v.Uri, v.OriginRef, v.OriginLabel, v.OriginScope,
                    NistReportCanonical.Micro(v.CollectedAt), NistReportCanonical.Micro(v.CreatedAt), v.RecordedByName))
                .ToList();
            var review = e is null ? NistReviewStates.None : ReviewStateOf(e);
            var assistedFields = (e is null ? Array.Empty<AegisScore.Application.Nist.NistAssistedFieldView>()
                    : assisted.For(NistAssistTarget.Evaluation, e.Id, NistAssistProvenance.EvaluationTexts(e)))
                .Concat(ctx.Procedures.Where(p => p.SubcategoryCode == x.Sub.Code)
                    .OrderBy(p => p.Method).ThenBy(p => p.CreatedAt.UtcTicks).ThenBy(p => p.Id)
                    .SelectMany(p => assisted.For(NistAssistTarget.Procedure, p.Id,
                            new Dictionary<string, string?>(StringComparer.Ordinal) { ["procedure"] = p.Procedure })
                        .Select(a => a with { Label = $"procedimento planejado ({NistLabels.Method(p.Method.ToString()).ToLowerInvariant()})" })))
                .ToList();
            return new NistReportSubcategory(
                x.Sub.Code, TitleOf(x.Sub), x.Fn.Code, x.Cat.Code, x.Sub.Description,
                states[x.Sub.Code], NistLabels.State(states[x.Sub.Code]),
                e?.CurrentLevel, e?.TargetLevel, confirmed ? e!.Gap : null, e?.NotApplicable ?? false,
                e?.Rationale, e?.CurrentComments, e?.TargetComments, e?.Gaps, e?.RiskImpact, e?.ImprovementGuidance,
                new NistReportPerson(e?.OwnerName,
                    e?.OwnerUserId is not null ? "User" : e?.OwnerIsExternal == true ? "External" : string.IsNullOrWhiteSpace(e?.OwnerName) ? "None" : "Text",
                    e?.OwnerContact),
                e?.AssessorName, e?.ReviewerName,
                confirmed ? e!.ReviewedByName : null, confirmed ? NistReportCanonical.Micro(e!.ReviewedAt) : null,
                e?.ContentOrigin.ToString() ?? "Analyst", e?.OriginNote,
                review, NistLabels.Review(review), e?.ReviewDecisionByName, NistReportCanonical.Micro(e?.ReviewDecisionAt), e?.ReviewDecisionNote,
                e?.Version ?? 0, procs, ev,
                findings.Where(f => f.SubcategoryCode == x.Sub.Code).Select(f => f.Id).ToList(),
                NistAssistProvenance.Report(assistedFields));
        }).ToList();

        // ---- Lacunas prioritárias: diferença confirmada, mais achados abertos e severidade ----
        var priorityGaps = subRows.Where(s => s.Gap is > 0 && s.State == NistSubcategoryStates.Evaluated)
            .Select(s =>
            {
                var open = findings.Where(f => f.SubcategoryCode == s.Code && f.Status == "Open").ToList();
                var worst = open.OrderBy(f => NistLabels.SeverityRank(f.Severity)).FirstOrDefault();
                return new NistReportGap(s.Code, s.Title, s.FunctionCode, s.CurrentLevel!.Value, s.TargetLevel!.Value, s.Gap!.Value, s.RiskImpact,
                    open.Count, worst?.SeverityLabel,
                    open.Count == 0 ? "Sem achado registrado (meta de melhoria)" : worst!.TreatmentLabel);
            })
            .OrderByDescending(g => g.Gap).ThenBy(g => g.HighestSeverityLabel is null ? 1 : 0).ThenBy(g => g.Code, StringComparer.Ordinal)
            .Take(10).ToList();

        // ---- Resumo ----
        var total = subRows.Count;
        var evaluated = subRows.Count(s => s.State == NistSubcategoryStates.Evaluated);
        var na = subRows.Count(s => s.State == NistSubcategoryStates.NotApplicable);
        var currentPlans = findings.Where(f => f.Plan is not null).Select(f => f.Plan!).ToList();
        var allProcedures = subRows.SelectMany(s => s.Procedures).ToList();
        var lastReviewed = ctx.Evaluations.Values.Where(e => e.HumanConfirmed).Select(e => (DateTimeOffset?)e.ReviewedAt!.Value).Max();
        var summary = new NistReportSummary(
            profile.Overall.Current, profile.Overall.Target, profile.Overall.Gap,
            total, evaluated, na,
            subRows.Count(s => s.State == NistSubcategoryStates.InProgress),
            subRows.Count(s => s.State == NistSubcategoryStates.PendingConfirmation),
            subRows.Count(s => s.State == NistSubcategoryStates.NotEvaluated),
            total == 0 ? 0 : Math.Round((evaluated + na) * 100.0 / total, 1),
            profile.Overall.WithCurrent, profile.Overall.WithTarget, profile.Overall.WithGap,
            subRows.Count(s => s.State != NistSubcategoryStates.NotApplicable && s.Gap is null),
            subRows.Count(s => s.ReviewState == NistReviewStates.Approved),
            subRows.Count(s => s.ReviewState == NistReviewStates.ChangesRequested),
            subRows.Count(s => s.ReviewState == NistReviewStates.Outdated),
            subRows.Sum(s => s.Evidence.Count),
            allProcedures.Count(p => p.Status is "Planned" or "InProgress"),
            allProcedures.Count(p => p.Status == "Performed"),
            allProcedures.Count(p => p.Status == "NotPerformed"),
            findings.Count(f => f.Status == "Open"),
            findings.Count(f => f.Status == "RiskAccepted"),
            findings.Count(f => f.Status == "Closed"),
            currentPlans.Count(p => p.Status is "Aberto" or "EmAndamento" or "AguardandoValidacao"),
            currentPlans.Count(p => p.Status == "Concluido"),
            currentPlans.Count(p => p.IsOverdue),
            NistReportCanonical.Micro(lastReviewed));

        // ---- Limitações: o que o leitor precisa saber sobre a base ----
        var limitations = new List<string>();
        if (summary.PendingConfirmation > 0)
            limitations.Add($"{summary.PendingConfirmation} subcategoria(s) com conteúdo herdado de outra rodada ou importado aguardam confirmação humana e não entram nas médias.");
        if (summary.NotEvaluated + summary.InProgress > 0)
            limitations.Add($"{summary.NotEvaluated + summary.InProgress} subcategoria(s) sem situação atual confirmada: ausência de avaliação não é zero e não entra nas médias.");
        if (summary.IndeterminateGaps > 0)
            limitations.Add($"Lacuna indeterminada em {summary.IndeterminateGaps} subcategoria(s) (falta atual ou alvo confirmado).");
        if (summary.ReviewOutdated > 0)
            limitations.Add($"{summary.ReviewOutdated} decisão(ões) do revisor não valem para a versão vigente: o conteúdo mudou depois da revisão.");
        if (summary.ProceduresPlanned > 0)
            limitations.Add($"{summary.ProceduresPlanned} procedimento(s) planejado(s) sem resultado registrado — método planejado não comprova a realização do teste.");
        if (evidence.Any(e => e.OriginKind == EvidenceOriginKind.KnightIndicator))
            limitations.Add("Evidências do AEGIS KNIGHT são apoio técnico mapeado explicitamente; não comprovam sozinhas o resultado organizacional.");
        if (evidence.Any(e => (e.OriginScope ?? "").Contains("demonstração", StringComparison.OrdinalIgnoreCase)))
            limitations.Add("Há evidência técnica de cenário de DEMONSTRAÇÃO (dados sintéticos) vinculada a esta rodada.");
        if (ctx.Cycle.Status == NistCycleStatus.Open)
            limitations.Add("Rodada aberta no instante desta leitura: alterações posteriores não entram numa fotografia já publicada.");

        var scale = ctx.Catalog.MaturityLevels.OrderBy(l => l.Level).Select(l => new NistReportLevel(l.Level, l.Name, l.Description)).ToList();
        return new NistMaturityReport(
            NistMaturityReport.SchemaV1,
            new NistReportClient(clientName),
            new NistReportAssessment(ctx.Assessment.Id, ctx.Assessment.Name, ctx.Assessment.Description, ctx.Assessment.Status.ToString(),
                ctx.Assessment.StartDate, ctx.Assessment.EndDate),
            new NistReportCycle(ctx.Cycle.Id, ctx.Cycle.Name, ctx.Cycle.PeriodKind.ToString(), NistLabels.PeriodKind(ctx.Cycle.PeriodKind.ToString()),
                ctx.Cycle.PeriodStart, ctx.Cycle.PeriodEnd, ctx.Cycle.Status.ToString(), seedName, ctx.Cycle.SeedMode.ToString(),
                NistLabels.SeedMode(ctx.Cycle.SeedMode.ToString())),
            new NistReportScope(ctx.Scope.Id, ScopeName(ctx.Scope), ctx.Scope.Description),
            new NistReportCatalog(ctx.Catalog.Name, ctx.Catalog.Functions.Count, categories.Count, total),
            new NistReportMethodology(ctx.Assessment.MethodologyVersion, scale, NistMethodologyText.Statement,
                NistMethodologyText.AggregationRule, NistMethodologyText.CoverageRule, NistMethodologyText.InstrumentsNote),
            summary, functions, categoryProfiles, subRows, findings, priorityGaps, limitations, null);

        NistReportPlan PlanOf(ActionPlanView p)
        {
            var v = p.ApplicableValidation ?? p.LatestValidation;
            return new NistReportPlan(p.Id, p.Title, p.ProposedAction,
                new NistReportPerson(p.ResponsiblePerson,
                    p.ResponsibleUserId is not null ? "User" : p.ResponsibleIsExternal ? "External" : string.IsNullOrWhiteSpace(p.ResponsiblePerson) ? "None" : "Text",
                    p.ResponsibleContact),
                p.ResponsibleArea, p.DueDate, p.Status.ToString(), RemediationReading.StatusLabel(p.Status),
                ActionPlan.IsOverdueOn(p.Status, p.DueDate, today), p.NextStep, p.ExecutionNotes, NistReportCanonical.Micro(p.ExecutedAt),
                v is null ? null : RemediationReading.MethodLabel(v.Method), v is null ? null : RemediationReading.OutcomeLabel(v.Outcome),
                v?.EvidenceReference, v?.DecidedByName, NistReportCanonical.Micro(v?.DecidedAt), v?.AppliesToCurrentCycle,
                p.WasReopened, NistReportCanonical.Micro(p.CycleStartedAt), NistReportCanonical.Micro(p.CreatedAt));
        }
    }

    public async Task<NistPublicationPreview> PreviewAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default,
        HistoryWindow? historyWindow = null)
    {
        var report = await BuildReportAsync(assessmentId, cycleId, scopeId, ct);
        var warnings = new List<string>();
        // [AEGIS-NIST-AI-ASSIST-01] O que a publicação levará (ou não) do resumo executivo aceito.
        if (report.Interpretation is { } it)
        {
            if (it.Mode == nameof(NistAssistEngineMode.Simulated))
                warnings.Add("O resumo executivo incluído veio do motor SIMULADO (demonstração): sairá marcado como tal no relatório.");
            if (it.ReviewedByName is null)
                warnings.Add("O resumo executivo incluído não teve revisão humana posterior de outra pessoa (sairá dito assim).");
        }
        else if (report.Limitations.Contains(StaleSummaryLimitation))
        {
            warnings.Add("O resumo executivo aceito foi preparado sobre um estado anterior da rodada e NÃO entra nesta publicação. Revise-o e aceite de novo, ou publique sem ele.");
        }
        if (report.Summary.Evaluated == 0 && report.Summary.NotApplicable == 0)
            warnings.Add("Nenhuma subcategoria tem avaliação confirmada: a fotografia registrará ausência de avaliação (sem médias).");
        if (report.Summary.PendingConfirmation > 0)
            warnings.Add($"{report.Summary.PendingConfirmation} subcategoria(s) aguardam confirmação humana e entram na fotografia como pendentes, fora das médias.");
        // [AEGIS-ASSESSMENT-VISUALS-01] A série que a publicação congelaria: o ponto desta rodada sai do próprio conteúdo revisado.
        var provisional = NewSnapshot(report, assessmentId, cycleId, scopeId, _tenant.TenantId!.Value,
            NistReportCanonical.Micro(_clock.GetUtcNow()), report);
        provisional.Id = Guid.Empty;
        var history = await BuildHistoryAsync(provisional, historyWindow ?? HistoryWindow.Default, ct);
        return new NistPublicationPreview(assessmentId, cycleId, scopeId, NistReportCanonical.Fingerprint(report), report.Summary,
            report.Functions, report.Findings.Count, report.Limitations, warnings, history,
            report.Interpretation is not null);
    }

    /// <summary>
    /// [AEGIS-ASSESSMENT-VISUALS-01] Série mensal da mesma avaliação e escopo, sobre as fotografias de maturidade JÁ publicadas
    /// do tenant (query filter fail-closed) — nunca as publicadas depois desta.
    /// </summary>
    private async Task<FrozenPostureHistory> BuildHistoryAsync(PostureSnapshot snapshot, HistoryWindow window, CancellationToken ct)
    {
        var rows = await _db.PostureSnapshots.AsNoTracking()
            .Where(x => x.Type == PostureSnapshotType.NistMaturity)
            .ToListAsync(ct);
        return FrozenPostureHistoryBuilder.Build(PostureSnapshotSummaries.Of(snapshot), rows.Select(PostureSnapshotSummaries.Of).ToList(), window);
    }

    public async Task<NistPublicationView> PublishAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, PublishNistCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var expected = (command.ExpectedFingerprint ?? "").Trim().ToLowerInvariant();
        if (expected.Length != 64)
            throw new NistAssessmentValidationException("Revise a prévia antes de publicar: a impressão digital do conteúdo revisado é obrigatória.");

        // Leitura consistente já encerrada aqui; a fotografia e o evento de publicação vão juntos no mesmo SaveChanges.
        var report = await BuildReportAsync(assessmentId, cycleId, scopeId, ct);
        var fingerprint = NistReportCanonical.Fingerprint(report);
        if (!string.Equals(fingerprint, expected, StringComparison.Ordinal))
            throw new NistAssessmentConflictException(
                "O conteúdo da rodada mudou desde a revisão na tela. Nada foi publicado: revise a prévia novamente antes de publicar.");

        var tenantId = _tenant.TenantId!.Value;
        var now = NistReportCanonical.Micro(_clock.GetUtcNow());
        var actorName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        var frozen = report with { Publication = new NistReportPublication(now, actorName, fingerprint) };
        var s = report.Summary;

        var snapshot = NewSnapshot(report, assessmentId, cycleId, scopeId, tenantId, now, frozen);
        // [AEGIS-ASSESSMENT-VISUALS-01] Histórico congelado com o ponto desta publicação; diferente do apresentado na prévia → 409.
        var history = await BuildHistoryAsync(snapshot, command.HistoryWindow ?? HistoryWindow.Default, ct);
        try
        {
            FrozenPostureHistoryBuilder.EnsureMatches(history, command.ExpectedHistoryFingerprint);
        }
        catch (HistoryChangedException ex)
        {
            throw new NistAssessmentConflictException(ex.Message);
        }
        snapshot.HistoryJson = FrozenPostureHistoryBuilder.Serialize(history);
        snapshot.ContentHash = PostureSnapshotHasher.Compute(snapshot);
        _db.PostureSnapshots.Add(snapshot);

        var changes = new List<NistFieldChange>();
        Diff(changes, "snapshot", "fotografia", null, snapshot.Id.ToString("D"));
        Diff(changes, "contentHash", "hash do conteúdo", null, snapshot.ContentHash);
        Diff(changes, "fingerprint", "impressão digital revisada", null, fingerprint);
        Diff(changes, "history", "histórico congelado", null, $"{history.From:yyyy-MM} a {history.Until:yyyy-MM} · {history.Points.Count} ponto(s) · {history.BasisFingerprint}");
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, null, "Publication", snapshot.Id, "Published",
            $"Fotografia de maturidade publicada: rodada \"{report.Cycle.Name}\", escopo \"{report.Scope.Name}\" — atual {NistReportCanonical.Level(s.Current)}, " +
            $"alvo {NistReportCanonical.Level(s.Target)}, cobertura {s.Coverage:0.#}%.", changes);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return ViewOf(snapshot, frozen);
    }

    /// <summary>A fotografia de maturidade da rodada (sem hash). Na prévia, <paramref name="frozen"/> é o relatório sem publicação.</summary>
    private static PostureSnapshot NewSnapshot(
        NistMaturityReport report, Guid assessmentId, Guid cycleId, Guid scopeId, Guid tenantId, DateTimeOffset now, NistMaturityReport frozen)
    {
        var s = report.Summary;
        return new PostureSnapshot
        {
            TenantId = tenantId,
            Type = PostureSnapshotType.NistMaturity,
            SchemaVersion = SchemaVersion,
            FormulaVersion = Truncate(report.Methodology.Version, 50),
            CatalogVersion = Truncate(report.Catalog.FrameworkName, 200),
            SemanticFamily = Truncate($"nist-maturity:{report.Methodology.Version}:{report.Catalog.FrameworkName}:{assessmentId:N}:{scopeId:N}", 300),
            SourceType = null,
            SourceLabel = Truncate($"{report.Assessment.Name} · {report.Scope.Name}", 200),
            CapturedAt = now,
            Score = null,   // 0–100 NÃO se aplica à maturidade: a escala 1–5 vive em MaturityCurrent/Target/Gap
            AchievedPoints = 0,
            PossiblePoints = 0,
            EligiblePoints = 0,
            Coverage = s.Coverage,
            EvaluatedItems = s.Evaluated,
            EligibleItems = s.Subcategories,
            NotApplicableCount = s.NotApplicable,
            NotEvaluatedCount = s.Subcategories - s.Evaluated - s.NotApplicable,
            DataRecency = s.LastReviewedAt,
            ClientName = report.Client.Name is null ? null : Truncate(report.Client.Name, 200),
            NistAssessmentId = assessmentId,
            NistCycleId = cycleId,
            NistScopeId = scopeId,
            NistCycleName = Truncate(report.Cycle.Name, 120),
            MaturityCurrent = s.Current,
            MaturityTarget = s.Target,
            MaturityGap = s.Gap,
            NistReportJson = NistReportCanonical.Serialize(frozen),
            CreatedAt = now,
        };
    }

    public async Task<IReadOnlyList<NistPublicationView>> ListAsync(Guid assessmentId, Guid? cycleId, Guid? scopeId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        // Avaliação de outro tenant (ou inexistente) é 404 — como em todas as rotas da avaliação, não uma lista vazia.
        if (!await _db.Assessments.AsNoTracking().AnyAsync(a => a.Id == assessmentId, ct))
            throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        var q = _db.PostureSnapshots.AsNoTracking()
            .Where(x => x.Type == PostureSnapshotType.NistMaturity && x.NistAssessmentId == assessmentId);
        if (cycleId is { } c) q = q.Where(x => x.NistCycleId == c);
        if (scopeId is { } s) q = q.Where(x => x.NistScopeId == s);
        var rows = await q.ToListAsync(ct);
        return rows.OrderByDescending(x => x.CapturedAt.UtcTicks).ThenByDescending(x => x.Id)
            .Select(x => ViewOf(x, NistReportCanonical.Deserialize(x.NistReportJson!))).ToList();
    }

    public async Task<NistCycleComparison> CompareCyclesAsync(Guid assessmentId, Guid scopeId, Guid baseCycleId, Guid targetCycleId, CancellationToken ct = default)
    {
        if (baseCycleId == targetCycleId)
            throw new NistAssessmentValidationException("Escolha duas rodadas diferentes para comparar.");
        // As duas rodadas saem do MESMO instante do banco.
        var (a, b) = await InReadSnapshotAsync(async () => (
            await BuildReportCoreAsync(assessmentId, baseCycleId, scopeId, ct),
            await BuildReportCoreAsync(assessmentId, targetCycleId, scopeId, ct)), ct);
        // A rodada mais antiga (pelo início do período) é a base: o comparativo segue o tempo, não a ordem dos parâmetros.
        var (first, second) = a.Cycle.PeriodStart <= b.Cycle.PeriodStart ? (a, b) : (b, a);
        return NistCycleComparer.Compare(first, second);
    }

    private static NistPublicationView ViewOf(PostureSnapshot x, NistMaturityReport report) => new(
        x.Id, x.NistAssessmentId ?? Guid.Empty, x.NistCycleId ?? Guid.Empty, report.Cycle.Name, x.NistScopeId ?? Guid.Empty, report.Scope.Name,
        x.CapturedAt, x.ContentHash, report.Publication?.ContentFingerprint ?? "", x.MaturityCurrent, x.MaturityTarget, x.MaturityGap,
        x.Coverage, x.EvaluatedItems, x.NotApplicableCount, x.EligibleItems, report.Publication?.PublishedByName, x.FormulaVersion);
}
