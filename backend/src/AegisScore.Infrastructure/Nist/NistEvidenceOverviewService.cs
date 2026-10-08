using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Remediation;
using Microsoft.EntityFrameworkCore;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Evidências da avaliação · rodada · escopo calculadas pelos registros do tenant (filtro global fail-closed):
/// <list type="bullet">
/// <item>KNIGHT → NIST: controles VINCULADOS pelo assessor (com a execução de origem e o aviso de execução mais recente), controles
/// DISPONÍVEIS para revisão (mapeamento explícito do catálogo, resultado avaliado, ainda não vinculados) e controles mapeados SEM
/// resultado avaliado — tudo da execução concluída mais recente de cada fonte, com demonstração e coleta parcial ditas;</item>
/// <item>lacunas de evidência: nível confirmado sem evidência vinculada e dados técnicos não avaliados;</item>
/// <item>tratamentos ligados (planos de achados NIST da rodada e de achados do KNIGHT relacionados), cada plano contado uma vez;</item>
/// <item>documentos e retratos do inventário já vinculados (o que as páginas de documentos e de ativos mostram na jornada).</item>
/// </list>
/// Nada aqui grava, aprova ou recalcula maturidade: vincular é a gravação da subcategoria, com a confirmação do assessor.
/// </summary>
public sealed class NistEvidenceOverviewService : INistEvidenceOverviewService
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IControlLanguageCatalog? _language;

    public NistEvidenceOverviewService(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock, IControlLanguageCatalog? language = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _language = language;
    }

    public Task<NistEvidenceOverviewView> GetAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        return AegisScore.Infrastructure.Queries.CrossSourceFactReader.InReadSnapshotAsync(_db, () => ReadAsync(assessmentId, cycleId, scopeId, ct), ct);
    }

    private async Task<NistEvidenceOverviewView> ReadAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var subs = ctx.AllSubcategories.ToList();
        var functionOf = ctx.Catalog.Functions.SelectMany(f => f.Categories.SelectMany(c => c.Subcategories.Select(s => (s.Code, f.Code))))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

        var evidence = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.RemovedAt == null && e.SubcategoryCode != null)
            .ToListAsync(ct);
        var evidenceByCode = evidence.GroupBy(e => e.SubcategoryCode!).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var (latestRuns, latestIndicators) = await NistKnightLatestRuns.LoadAsync(_db, ct);
        var latestByKey = latestRuns.ToDictionary(r => (r.SourceType, r.Mode));

        // Execuções de origem dos vínculos técnicos (podem ser anteriores às mais recentes).
        var linkedRefs = evidence.Where(e => e.OriginKind == EvidenceOriginKind.KnightIndicator)
            .Select(e => (Evidence: e, Ref: ParseRef(e.OriginRef))).ToList();
        var linkedRunIds = linkedRefs.Where(x => x.Ref is not null).Select(x => x.Ref!.Value.RunId).Distinct().ToList();
        var linkedRuns = linkedRunIds.Count == 0 ? new Dictionary<Guid, KnightAssessmentRun>()
            : await _db.KnightAssessmentRuns.AsNoTracking().Where(r => linkedRunIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var linkedIndicatorRows = linkedRunIds.Count == 0 ? new List<KnightIndicatorResult>()
            : await _db.KnightIndicatorResults.AsNoTracking().Where(i => linkedRunIds.Contains(i.RunId)).ToListAsync(ct);
        var linkedIndicators = linkedIndicatorRows.ToDictionary(i => (i.RunId, i.IndicatorId));
        var latestIndicatorsByKey = latestIndicators.ToDictionary(i => (i.RunId, i.IndicatorId));

        // Planos: NIST (achados da rodada) e KNIGHT (achados técnicos relacionados).
        var plansByFinding = await PlansByFindingAsync(_db, ctx.Findings.Select(f => f.Id).ToList(), ct);
        var knightPlans = (await _db.ActionPlans.AsNoTracking()
                .Include(p => p.Validations).Include(p => p.Events)
                .Where(p => p.KnightIndicatorId != null
                            && (p.OriginKind == ActionPlanOriginKind.KnightFinding || p.OriginKind == null))
                .AsSplitQuery()
                .ToListAsync(ct))
            .Select(RemediationService.ToView).ToList();

        var rows = new List<NistCorrelationRowView>();
        var mappedCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in subs)
        {
            var code = sub.Code;
            var eval = ctx.Evaluation(sub.Id);
            var codeEvidence = evidenceByCode.TryGetValue(code, out var list) ? list : new List<Evidence>();

            // ---- Vinculados (cada vínculo: um controle × esta subcategoria) ----
            var linked = new List<NistTechnicalEvidenceView>();
            var linkedKeys = new HashSet<(KnightSourceType, KnightAssessmentMode, string)>();
            foreach (var (e, r) in linkedRefs.Where(x => x.Evidence.SubcategoryCode == code)
                         .OrderBy(x => x.Evidence.CreatedAt.UtcTicks).ThenBy(x => x.Evidence.Id))
            {
                linkedRuns.TryGetValue(r?.RunId ?? Guid.Empty, out var run);
                KnightIndicatorResult? indicator = null;
                if (r is { } rr) linkedIndicators.TryGetValue((rr.RunId, rr.IndicatorId), out indicator);
                LatestKnightIndicator? newer = null;
                LatestKnightRun? newerRun = null;
                if (run is not null && r is { } rf && latestByKey.TryGetValue((run.SourceType, run.Mode), out var lr) && lr.Id != run.Id)
                {
                    newerRun = lr;
                    latestIndicatorsByKey.TryGetValue((lr.Id, rf.IndicatorId), out newer);
                }
                if (run is not null && r is { } rk) linkedKeys.Add((run.SourceType, run.Mode, rk.IndicatorId));
                var demo = run?.Mode == KnightAssessmentMode.Demo;
                var partial = run is not null && run.SourceState != KnightSourceState.Completed;
                linked.Add(new NistTechnicalEvidenceView(
                    e.Id, r?.RunId ?? Guid.Empty, r?.IndicatorId ?? "", e.Title ?? e.OriginRef ?? "Controle do KNIGHT",
                    indicator?.Status.ToString() ?? "Unavailable",
                    indicator is null ? "Resultado de origem não disponível" : NistAssessmentService.KnightStatusLabel(indicator.Status),
                    indicator is null ? "" : NistLabels.Severity(indicator.Severity.ToString()),
                    run is null ? "AEGIS KNIGHT" : $"AEGIS KNIGHT · {run.Source}",
                    e.CollectedAt, demo, partial,
                    newerRun is null ? null : newer is null ? "Controle ausente na execução mais recente" : NistAssessmentService.KnightStatusLabel(newer.Status),
                    newerRun?.Id,
                    Limitation(demo, partial, linkedEvidence: true)
                        + (newerRun is null ? "" : " Há execução mais recente da mesma fonte: o vínculo continua apontando a execução de origem.")));
            }

            // ---- Disponíveis para revisão e não avaliados (execução mais recente) ----
            var candidates = new List<NistTechnicalEvidenceView>();
            var notEvaluated = 0;
            foreach (var i in latestIndicators.Where(i => i.MapsTo(code)))
            {
                var run = latestRuns.First(r => r.Id == i.RunId);
                mappedCodes.Add(code);
                if (linkedKeys.Contains((run.SourceType, run.Mode, i.IndicatorId))) continue;   // o mesmo controle já vinculado
                if (!NistKnightLatestRuns.Evaluated.Contains(i.Status))
                {
                    if (i.Status != KnightIndicatorStatus.NotApplicable) notEvaluated++;
                    continue;
                }
                candidates.Add(new NistTechnicalEvidenceView(
                    null, run.Id, i.IndicatorId, $"{i.IndicatorId} — {i.Title}", i.Status.ToString(), NistAssessmentService.KnightStatusLabel(i.Status),
                    NistLabels.Severity(i.Severity.ToString()), $"AEGIS KNIGHT · {run.Source}", run.CompletedAt ?? i.CollectedAt,
                    run.IsDemo, run.Partial, null, null, Limitation(run.IsDemo, run.Partial, linkedEvidence: false)));
            }
            if (linked.Count > 0) mappedCodes.Add(code);

            // ---- Tratamentos ----
            var plans = new List<NistLinkedPlanView>();
            foreach (var f in ctx.Findings.Where(f => f.SubcategoryCode == code).OrderBy(f => f.CreatedAt.UtcTicks).ThenBy(f => f.Id))
                if (CurrentPlan(plansByFinding.TryGetValue(f.Id, out var pl) ? pl : null) is { } p)
                    plans.Add(PlanOf(p, "NistFinding", f.Id, null));
            var technicalKeys = linked.Where(l => l.KnightIndicatorId != "")
                .Select(l => (Source: linkedRuns.TryGetValue(l.KnightRunId, out var lrun) ? lrun.SourceType : (KnightSourceType?)null,
                              Mode: linkedRuns.TryGetValue(l.KnightRunId, out var lrun2) ? lrun2.Mode : (KnightAssessmentMode?)null, l.KnightIndicatorId))
                .Concat(candidates.Select(c => (Source: (KnightSourceType?)latestRuns.First(r => r.Id == c.KnightRunId).SourceType,
                                                 Mode: (KnightAssessmentMode?)latestRuns.First(r => r.Id == c.KnightRunId).Mode, c.KnightIndicatorId)))
                .ToList();
            foreach (var kp in knightPlans
                         .Where(p => technicalKeys.Any(k => k.KnightIndicatorId == p.KnightIndicatorId
                                                            && (p.OriginSourceType is null || p.OriginSourceType == k.Source)
                                                            && (p.OriginMode is null || p.OriginMode == k.Mode)))
                         .GroupBy(p => (p.KnightIndicatorId, p.OriginSourceType, p.OriginMode))
                         .Select(g => g.FirstOrDefault(p => p.IsActive) ?? g.OrderByDescending(p => p.CreatedAt.UtcTicks).First()))
                plans.Add(PlanOf(kp, "KnightFinding", null, kp.KnightIndicatorId));

            var evaluatedWithoutEvidence = eval is { HumanConfirmed: true, NotApplicable: false, CurrentLevel: not null } && codeEvidence.Count == 0;
            if (linked.Count == 0 && candidates.Count == 0 && notEvaluated == 0 && plans.Count == 0 && !evaluatedWithoutEvidence) continue;

            rows.Add(new NistCorrelationRowView(
                code, _language?.Get(code)?.Title ?? code, functionOf.TryGetValue(code, out var fn) ? fn : code[..2],
                StateOf(eval, ctx.EvidenceCount(code)),
                eval?.HumanConfirmed == true ? eval.CurrentLevel : null, eval?.HumanConfirmed == true ? eval.TargetLevel : null,
                eval?.HumanConfirmed == true,
                linked, candidates, notEvaluated, codeEvidence.Count, evaluatedWithoutEvidence,
                ctx.Findings.Count(f => f.SubcategoryCode == code && f.Status == NistFindingStatus.Open),
                plans));
        }

        // ---- Contagens sobre o universo completo do escopo, sem duplicar controle vinculado a várias subcategorias ----
        static string ControlKey(NistTechnicalEvidenceView v) => $"{v.KnightRunId:N}/{v.KnightIndicatorId}";
        var allPlans = rows.SelectMany(r => r.Plans).GroupBy(p => p.PlanId).Select(g => g.First()).ToList();
        var summary = new NistCorrelationSummaryView(
            subs.Count,
            mappedCodes.Count,
            rows.Sum(r => r.LinkedTechnical.Count),
            rows.SelectMany(r => r.LinkedTechnical).Select(ControlKey).Distinct(StringComparer.Ordinal).Count(),
            rows.Count(r => r.LinkedTechnical.Count > 0),
            rows.Sum(r => r.Candidates.Count),
            rows.SelectMany(r => r.Candidates).Select(ControlKey).Distinct(StringComparer.Ordinal).Count(),
            rows.Count(r => r.Candidates.Count > 0),
            rows.Count(r => r.TechnicalNotEvaluated > 0),
            rows.Count(r => r.EvaluatedWithoutEvidence),
            allPlans.Count,
            allPlans.Count(p => p.IsOverdue));

        // ---- Documentos e inventário vinculados ----
        var documentLinks = evidence
            .Where(e => e.OriginKind == EvidenceOriginKind.GovernanceDocument && Guid.TryParse(e.OriginRef, out _))
            .OrderBy(e => e.SubcategoryCode, StringComparer.Ordinal).ThenBy(e => e.CreatedAt.UtcTicks).ThenBy(e => e.Id)
            .Select(e => new NistDocumentLinkView(e.Id, e.SubcategoryCode!, Guid.Parse(e.OriginRef!), e.Title ?? "Documento", e.CreatedAt, e.RecordedByName))
            .ToList();
        var inventory = subs.Where(s => s.Code.StartsWith("ID.AM", StringComparison.Ordinal))
            .Select(s =>
            {
                var e = ctx.Evaluation(s.Id);
                var links = (evidenceByCode.TryGetValue(s.Code, out var l) ? l : new List<Evidence>())
                    .Where(x => x.OriginKind == EvidenceOriginKind.AssetInventory)
                    .OrderByDescending(x => x.CollectedAt.UtcTicks).ThenBy(x => x.Id)
                    .Select(x => new NistInventoryLinkView(x.Id, x.Title ?? "Inventário de ativos", x.CollectedAt, x.OriginScope, x.RecordedByName))
                    .ToList();
                return new NistInventoryTargetView(s.Code, _language?.Get(s.Code)?.Title ?? s.Code, StateOf(e, ctx.EvidenceCount(s.Code)),
                    e?.HumanConfirmed == true ? e.CurrentLevel : null, e?.HumanConfirmed == true, links);
            })
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
        var assets = await _db.Assets.AsNoTracking().Where(a => a.IsActive).Select(a => new { a.Category, a.DiscoverySource }).ToListAsync(ct);

        var limitations = new List<string>
        {
            "Correlação calculada pelos registros: mapeamento explícito do catálogo do KNIGHT para o NIST e vínculos gravados pelo assessor. Não é interpretação de IA e não aprova requisitos.",
            "Um resultado técnico cobre um aspecto do requisito; o atendimento da subcategoria depende também de evidências documentais e organizacionais e da avaliação do assessor.",
        };
        if (latestRuns.Count == 0)
            limitations.Add("Nenhuma avaliação do KNIGHT concluída: não há resultado técnico disponível para relacionar.");
        else
            limitations.Add("Considera a avaliação concluída mais recente de cada fonte do KNIGHT; o relatório consolidado não é usado como evidência.");
        var demoRuns = latestRuns.Where(r => r.IsDemo).Select(r => r.Source).ToList();
        if (demoRuns.Count > 0)
            limitations.Add($"Execução de demonstração (dados sintéticos) em: {string.Join(", ", demoRuns)}.");
        var partialRuns = latestRuns.Where(r => r.Partial).Select(r => $"{r.Source} ({r.SourceState})").ToList();
        if (partialRuns.Count > 0)
            limitations.Add($"Coleta parcial em: {string.Join(", ", partialRuns)} — controles sem leitura não são aprovações.");
        if (ctx.Cycle.Status == NistCycleStatus.Closed)
            limitations.Add("Rodada encerrada: os vínculos não podem ser alterados.");

        return new NistEvidenceOverviewView(
            ctx.Assessment.Id, ctx.Cycle.Id, ctx.Scope.Id, ctx.Assessment.Name, ctx.Cycle.Name, ctx.Cycle.Status.ToString(), ScopeName(ctx.Scope),
            _clock.GetUtcNow(),
            latestRuns.Select(r => new NistKnightRunView(r.Id, r.SourceType.ToString(), r.Source, r.Mode.ToString(), r.SourceState.ToString(),
                r.CatalogVersion, r.CompletedAt, r.IsDemo, r.Partial)).ToList(),
            summary,
            rows.OrderBy(r => FunctionIndex(r.FunctionCode)).ThenBy(r => r.Code, StringComparer.Ordinal).ToList(),
            documentLinks,
            inventory,
            assets.Count,
            NistAssessmentService.InventoryScopeOf(assets.Select(a => (a.Category, a.DiscoverySource)).ToList()),
            limitations);
    }

    private static string Limitation(bool demo, bool partial, bool linkedEvidence) =>
        (linkedEvidence
            ? "Vinculado pelo assessor como evidência técnica de apoio: não comprova sozinho o resultado organizacional."
            : "Disponível para revisão: o assessor decide se vincula. Não comprova sozinho o resultado organizacional.")
        + (demo ? " Demonstração (dados sintéticos)." : "")
        + (partial ? " Coleta parcial da fonte." : "");

    private static NistLinkedPlanView PlanOf(ActionPlanView p, string origin, Guid? findingId, string? indicator) =>
        new(p.Id, origin, p.Title, p.Status.ToString(), RemediationReading.StatusLabel(p.Status), p.IsOverdue, p.IsActive, findingId, indicator);

    private static (Guid RunId, string IndicatorId)? ParseRef(string? originRef)
    {
        var parts = (originRef ?? "").Split('/', 2);
        return parts.Length == 2 && Guid.TryParse(parts[0], out var run) && parts[1].Length > 0 ? (run, parts[1]) : null;
    }

    private static int FunctionIndex(string fn)
    {
        var i = Array.IndexOf(FunctionOrder, fn);
        return i < 0 ? int.MaxValue : i;
    }
}
