using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// The "modelo forte" view: turns SOC telemetry into business risk exposure — maturity by NIST
/// function, gaps, risk heat-map and the ICR — modeled on the reference dashboards.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly MaturityScoringService _maturity;
    private readonly IcrScoringService _icr;

    public DashboardController(AegisScoreDbContext db, ITenantContext tenant, MaturityScoringService maturity, IcrScoringService icr)
    {
        _db = db;
        _tenant = tenant;
        _maturity = maturity;
        _icr = icr;
    }

    [HttpGet("executive")]
    public async Task<ActionResult<ExecutiveDashboardDto>> Executive(CancellationToken ct)
    {
        // Tenant implícito: o nome do cliente vem da entidade raiz Tenant (não filtrada por design),
        // resolvida pelo ITenantContext ambiente — a mesma fonte que alimenta os Global Query Filters abaixo.
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == _tenant.TenantId, ct);
        var clientName = tenant?.Name ?? "—";

        // ---- Maturity (all evaluations across the tenant's assessments) ----
        var scoreRows = await (from s in _db.Scopes
                               join e in _db.Evaluations on s.Id equals e.AssessmentScopeId
                               join sub in _db.Subcategories on e.SubcategoryId equals sub.Id
                               select new { sub.Code, e.CurrentScore, e.TargetScore })
                              .ToListAsync(ct);

        var rollup = _maturity.Aggregate(scoreRows.Select(x => new SubcategoryScore(x.Code, x.CurrentScore, x.TargetScore)));

        var functions = await _db.Functions.AsNoTracking().OrderBy(f => f.Order)
            .Select(f => new { f.Code, f.Name }).ToListAsync(ct);

        var radar = functions.Select(f =>
        {
            var agg = rollup.Functions.FirstOrDefault(x => x.RefCode == f.Code);
            return new RadarPointDto(f.Code, f.Name, agg?.CurrentScore ?? 0, agg?.TargetScore ?? 0);
        }).ToList();

        var topGaps = rollup.Categories
            .OrderByDescending(c => c.Gap)
            .Take(8)
            .Select(c => new GapPointDto(c.RefCode, c.RefCode, c.CurrentScore, c.TargetScore, c.Gap))
            .ToList();

        // ---- Risk (latest evaluation per risk) ----
        // Carrega a avaliação + o processo do risco para deduplicar exposição por processo.
        var riskEvals = await (from r in _db.Risks
                               join ev in _db.RiskEvaluations on r.Id equals ev.RiskId
                               select new { Ev = ev, r.BusinessProcessId })
                              .AsNoTracking().ToListAsync(ct);

        var latest = riskEvals
            .GroupBy(x => x.Ev.RiskId)
            .Select(g => g.OrderByDescending(x => x.Ev.EvaluatedAt).First())
            .ToList();

        var heatmap = latest
            .GroupBy(x => new { x.Ev.Probability, x.Ev.Impact })
            .Select(g => new HeatCellDto(g.Key.Probability, g.Key.Impact, g.Count()))
            .ToList();

        var byLevel = latest
            .GroupBy(x => x.Ev.RiskLevel)
            .Select(g => new RiskLevelCountDto(g.Key.ToString(), g.Count()))
            .ToList();

        // ---- Exposure cards ----
        var actionPlans = await (from r in _db.Risks
                                 join ap in _db.ActionPlans on r.Id equals ap.RiskId
                                 select ap).AsNoTracking().ToListAsync(ct);

        var overdueCount = actionPlans.Count(ap => ap.IsOverdue);
        // Apenas subcategorias efetivamente pontuadas (CurrentScore != null) e fracas (<= 2);
        // null é "ainda não avaliado", não "controle inefetivo".
        var ineffectiveControls = scoreRows.Count(x => x.CurrentScore.HasValue && x.CurrentScore.Value <= 2);
        // Processos DISTINTOS com ao menos um risco Alto/Crítico (não a contagem de riscos).
        var criticalExposed = latest
            .Where(x => x.Ev.RiskLevel is RiskLevel.Alto or RiskLevel.Critico)
            .Select(x => x.BusinessProcessId)
            .Where(id => id != null)
            .Distinct()
            .Count();

        var exposure = new ExposureCardsDto(
            criticalExposed,
            ineffectiveControls,
            overdueCount,
            rollup.Overall.CurrentScore,
            rollup.Overall.TargetScore);

        // ---- ICR (average of stored scores, else an org-level proxy from current posture) ----
        double icrScore;
        var stored = await _db.IcrScores.AsNoTracking().ToListAsync(ct);
        if (stored.Count > 0)
        {
            icrScore = Math.Round(stored.Average(s => s.Score), 1);
        }
        else
        {
            var weights = await _db.IcrWeightProfiles.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == null, ct)
                          ?? new IcrWeightProfile();

            var processValues = await _db.Processes.AsNoTracking().Select(p => p.ProcessValue).ToListAsync(ct);
            var avgProcessValue = processValues.Count == 0 ? 1.0 : processValues.Average();

            var input = new IcrInput(
                TechnicalSeverity: 0.5,
                AssetCriticality: 0.5,
                BusinessImpact: avgProcessValue / 4.0,
                RecentExploitation: 0.3,
                RegulatoryExposure: 0.4,
                ControlEffectiveness: rollup.Overall.CurrentScore / 5.0,
                OverdueActionPlan: actionPlans.Count == 0 ? 0 : (double)overdueCount / actionPlans.Count);

            icrScore = _icr.Compute(input, weights).Score;
        }

        var icr = new IcrDto(icrScore, _icr.BandOf(icrScore).ToString());

        return new ExecutiveDashboardDto(clientName, DateTimeOffset.UtcNow, exposure, radar, topGaps, heatmap, byLevel, icr);
    }

    /// <summary>
    /// O PIOR raio de explosão já calculado para o tenant — "se cair o ativo X, quantos outros caem
    /// junto?". Traduz risco técnico em impacto de negócio para a vitrine executiva.
    ///
    /// Endpoint SEPARADO do /executive de propósito (ver <see cref="BlastRadiusSummaryDto"/>): o painel
    /// é secundário e não pode entrar no caminho crítico do First Contentful Paint.
    ///
    /// Barato por construção: o <c>BlastRadiusAssessment</c> já MATERIALIZA as contagens no momento do
    /// traversal (<c>ImpactedAssetCount</c>, <c>ImpactedProcessCount</c>, <c>MaxDepth</c>), então aqui
    /// não há grafo a percorrer — é um ORDER BY + LIMIT 1 com um JOIN para o nome do ativo.
    ///
    /// Escolhe o de MAIOR score, não o mais recente: a diretoria pergunta "qual é o nosso pior cenário?",
    /// não "qual foi o último que rodamos". Tenant implícito (Global Query Filter fail-closed).
    /// </summary>
    [HttpGet("blast-radius-summary")]
    [ProducesResponseType(typeof(BlastRadiusSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<BlastRadiusSummaryDto>> BlastRadiusSummary(CancellationToken ct)
    {
        var worst = await _db.BlastRadiusAssessments.AsNoTracking()
            .OrderByDescending(a => a.BlastRadiusScore)
            .ThenByDescending(a => a.CreatedAt)
            .Select(a => new BlastRadiusSummaryDto(
                a.RootAsset!.Name,
                a.BlastRadiusScore,
                a.RiskLevel.ToString(),
                a.ImpactedAssetCount,
                a.ImpactedProcessCount,
                a.MaxDepth,
                a.CreatedAt))
            .FirstOrDefaultAsync(ct);

        // 204 e não um DTO zerado: "nunca calculamos um raio" é diferente de "o raio é zero", e o
        // frontend precisa dessa distinção para escolher entre estado vazio e número.
        return worst is null ? NoContent() : worst;
    }
}
