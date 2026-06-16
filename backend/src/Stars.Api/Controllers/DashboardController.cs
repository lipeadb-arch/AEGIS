using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stars.Api.Contracts;
using Stars.Application.Scoring;
using Stars.Domain;
using Stars.Infrastructure.Persistence;

namespace Stars.Api.Controllers;

/// <summary>
/// The "modelo forte" view: turns SOC telemetry into business risk exposure — maturity by NIST
/// function, gaps, risk heat-map and the ICR — modeled on the reference dashboards.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly StarsDbContext _db;
    private readonly MaturityScoringService _maturity;
    private readonly IcrScoringService _icr;

    public DashboardController(StarsDbContext db, MaturityScoringService maturity, IcrScoringService icr)
    {
        _db = db;
        _maturity = maturity;
        _icr = icr;
    }

    [HttpGet("executive")]
    public async Task<ActionResult<ExecutiveDashboardDto>> Executive(
        [FromHeader(Name = "X-Tenant")] Guid tenantId, CancellationToken ct)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
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
        var riskEvals = await (from r in _db.Risks
                               join ev in _db.RiskEvaluations on r.Id equals ev.RiskId
                               select ev).ToListAsync(ct);

        var latest = riskEvals
            .GroupBy(e => e.RiskId)
            .Select(g => g.OrderByDescending(x => x.EvaluatedAt).First())
            .ToList();

        var heatmap = latest
            .GroupBy(e => new { e.Probability, e.Impact })
            .Select(g => new HeatCellDto(g.Key.Probability, g.Key.Impact, g.Count()))
            .ToList();

        var byLevel = latest
            .GroupBy(e => e.RiskLevel)
            .Select(g => new RiskLevelCountDto(g.Key.ToString(), g.Count()))
            .ToList();

        // ---- Exposure cards ----
        var actionPlans = await (from r in _db.Risks
                                 join ap in _db.ActionPlans on r.Id equals ap.RiskId
                                 select ap).ToListAsync(ct);

        var overdueCount = actionPlans.Count(ap => ap.IsOverdue);
        var ineffectiveControls = scoreRows.Count(x => (x.CurrentScore ?? 0) <= 2);
        var criticalExposed = latest.Count(e => e.RiskLevel is RiskLevel.Alto or RiskLevel.Critico);

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
}
