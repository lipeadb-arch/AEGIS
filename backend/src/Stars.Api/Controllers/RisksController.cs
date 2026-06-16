using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stars.Api.Contracts;
using Stars.Application.Scoring;
using Stars.Domain;
using Stars.Infrastructure.Persistence;

namespace Stars.Api.Controllers;

[ApiController]
[Route("api/v1/risks")]
public class RisksController : ControllerBase
{
    private readonly StarsDbContext _db;
    private readonly RiskScoringService _risk;

    public RisksController(StarsDbContext db, RiskScoringService risk)
    {
        _db = db;
        _risk = risk;
    }

    [HttpPost]
    public async Task<ActionResult<IdResponse>> Create(
        CreateRiskRequest req, [FromHeader(Name = "X-Tenant")] Guid tenantId, CancellationToken ct)
    {
        var r = new Risk
        {
            TenantId = tenantId,
            Code = req.Code,
            Title = req.Title,
            Description = req.Description,
            BusinessProcessId = req.BusinessProcessId,
            BusinessUnitId = req.BusinessUnitId,
            Threats = req.Threats,
            Vulnerabilities = req.Vulnerabilities
        };
        _db.Risks.Add(r);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(r.Id);
    }

    /// <summary>Score an inherent/residual evaluation: Probability + Impact + ProcessValue → level.</summary>
    [HttpPost("{riskId:guid}/evaluations")]
    public async Task<ActionResult<RiskEvaluationDto>> Evaluate(Guid riskId, RiskEvaluationRequest req, CancellationToken ct)
    {
        var risk = await _db.Risks.FirstOrDefaultAsync(r => r.Id == riskId, ct);
        if (risk is null) return NotFound();

        var bands = RiskBands.Default;
        var appetite = await _db.RiskAppetites.FirstOrDefaultAsync(ct);
        if (appetite is not null) bands = _risk.ParseBands(appetite.ThresholdsJson);

        var (score, level) = _risk.Evaluate(req.Probability, req.Impact, req.ProcessValue, bands);

        _db.RiskEvaluations.Add(new RiskEvaluation
        {
            RiskId = riskId,
            Phase = req.Phase,
            Probability = req.Probability,
            Impact = req.Impact,
            ProcessValue = req.ProcessValue,
            RiskScore = score,
            RiskLevel = level
        });
        await _db.SaveChangesAsync(ct);

        return new RiskEvaluationDto(score, level.ToString());
    }
}
