using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

[ApiController]
[Route("api/v1/assessments")]
public class AssessmentsController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly IAiAssessmentService _ai;
    private readonly MaturityScoringService _maturity;

    public AssessmentsController(AegisScoreDbContext db, IAiAssessmentService ai, MaturityScoringService maturity)
    {
        _db = db;
        _ai = ai;
        _maturity = maturity;
    }

    [HttpPost]
    public async Task<ActionResult<IdResponse>> Create(CreateAssessmentRequest req, CancellationToken ct)
    {
        var fvId = req.FrameworkVersionId
            ?? (await _db.FrameworkVersions.AsNoTracking().FirstOrDefaultAsync(f => f.IsActive, ct))?.Id
            ?? throw new InvalidOperationException("No active framework version.");

        // Sem TenantId aqui — carimbado no SaveChangesAsync (fail-closed), como no RisksController.
        var a = new Assessment { FrameworkVersionId = fvId, Name = req.Name };
        _db.Assessments.Add(a);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(a.Id);
    }

    [HttpPost("{assessmentId:guid}/scopes")]
    public async Task<ActionResult<IdResponse>> AddScope(
        Guid assessmentId, CreateScopeRequest req, CancellationToken ct)
    {
        // Scoped by the tenant query filter: a foreign / non-existent assessment yields 404, not a 500 FK violation.
        if (!await _db.Assessments.AnyAsync(a => a.Id == assessmentId, ct))
            return NotFound($"Assessment {assessmentId} não encontrado.");

        var scope = new AssessmentScope
        {
            // Sem TenantId aqui — carimbado no SaveChangesAsync (fail-closed).
            AssessmentId = assessmentId,
            BusinessProcessId = req.BusinessProcessId,
            BusinessUnitId = req.BusinessUnitId
        };
        _db.Scopes.Add(scope);
        await _db.SaveChangesAsync(ct);
        return new IdResponse(scope.Id);
    }

    /// <summary>Ask the AI engine to suggest a maturity level from answers, evidence and collected signals.</summary>
    [HttpPost("scopes/{scopeId:guid}/ai-suggest")]
    public async Task<ActionResult<MaturitySuggestionDto>> AiSuggest(Guid scopeId, AiSuggestRequest req, CancellationToken ct)
    {
        var sub = await _db.Subcategories.AsNoTracking().FirstOrDefaultAsync(s => s.Code == req.SubcategoryCode, ct);
        if (sub is null) return NotFound($"Subcategory {req.SubcategoryCode} not found.");

        // Pull connector signals mapped to this subcategory (filtered in memory: jsonb list).
        var allSignals = await _db.Signals.AsNoTracking().ToListAsync(ct);
        var signals = allSignals
            .Where(s => s.MappedSubcategoryCodes.Contains(req.SubcategoryCode))
            .Select(s => (s.SignalKey, s.NumericValue, s.Severity))
            .ToList();

        var aiReq = new MaturitySuggestionRequest(
            sub.Code,
            sub.Description,
            req.Answers.Select(a => (a.Question, a.Answer, a.Comment)).ToList(),
            req.EvidenceSummaries,
            signals);

        var s = await _ai.SuggestMaturityAsync(aiReq, ct);
        return new MaturitySuggestionDto(s.CurrentLevel, s.Confidence, s.Rationale);
    }

    /// <summary>Create/update the analyst-validated evaluation for one subcategory in a scope.</summary>
    [HttpPut("scopes/{scopeId:guid}/evaluations/{code}")]
    public async Task<ActionResult<IdResponse>> UpsertEvaluation(Guid scopeId, string code, EvaluationUpsertRequest req, CancellationToken ct)
    {
        var sub = await _db.Subcategories.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        if (sub is null) return NotFound($"Subcategory {code} not found.");

        var eval = await _db.Evaluations.FirstOrDefaultAsync(e => e.AssessmentScopeId == scopeId && e.SubcategoryId == sub.Id, ct);
        if (eval is null)
        {
            eval = new SubcategoryEvaluation { AssessmentScopeId = scopeId, SubcategoryId = sub.Id, EvaluatedBy = EvaluatedBy.Analyst };
            _db.Evaluations.Add(eval);
        }

        eval.CurrentLevel = req.CurrentLevel;
        eval.CurrentScore = req.CurrentScore ?? req.CurrentLevel;
        eval.CurrentComments = req.CurrentComments;
        eval.TargetLevel = req.TargetLevel;
        eval.TargetScore = req.TargetScore ?? req.TargetLevel;
        eval.TargetComments = req.TargetComments;
        eval.ReviewedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return new IdResponse(eval.Id);
    }

    /// <summary>Maturity rollup for the assessment (overall / per function / per category + gaps).</summary>
    [HttpGet("{assessmentId:guid}/maturity")]
    public async Task<ActionResult<MaturityRollupDto>> Maturity(Guid assessmentId, CancellationToken ct)
    {
        var rows = await (from s in _db.Scopes
                          where s.AssessmentId == assessmentId
                          join e in _db.Evaluations on s.Id equals e.AssessmentScopeId
                          join sub in _db.Subcategories on e.SubcategoryId equals sub.Id
                          select new { sub.Code, e.CurrentScore, e.TargetScore })
                         .ToListAsync(ct);

        var result = _maturity.Aggregate(rows.Select(x => new SubcategoryScore(x.Code, x.CurrentScore, x.TargetScore)));
        return ToDto(result);
    }

    internal static MaturityRollupDto ToDto(MaturityResult r)
    {
        static AggregateDto M(AggregateScore a) =>
            new(a.Level.ToString(), a.RefCode, a.CurrentScore, a.TargetScore, a.Gap, a.Count);

        return new MaturityRollupDto(M(r.Overall), r.Functions.Select(M).ToList(), r.Categories.Select(M).ToList());
    }
}
