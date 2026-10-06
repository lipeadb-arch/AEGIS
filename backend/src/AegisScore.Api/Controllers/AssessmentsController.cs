using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <remarks>
/// [AEGIS-NIST-JOURNEY-01] Superfície LEGADA (não usada pelo portal; a jornada vive em <c>api/v1/nist/assessments</c>).
/// Endurecida: gravação só para Manager/TenantAdmin, o escopo precisa ser do tenant (antes, um escopo de outro tenant
/// recebia a avaliação) e os níveis ficam na escala 1–5.
/// </remarks>
[ApiController]
[Authorize]
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
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<IdResponse>> Create(CreateAssessmentRequest req, CancellationToken ct)
    {
        var fvId = req.FrameworkVersionId
            ?? (await _db.FrameworkVersions.AsNoTracking().FirstOrDefaultAsync(f => f.IsActive, ct))?.Id
            ?? throw new InvalidOperationException("No active framework version.");

        // Sem TenantId aqui — carimbado no SaveChangesAsync (fail-closed), como no RisksController.
        var a = new Assessment { FrameworkVersionId = fvId, Name = req.Name };
        _db.Assessments.Add(a);
        // [AEGIS-NIST-JOURNEY-02] Toda avaliação nasce com uma rodada: as avaliações de subcategoria pertencem a ela.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _db.NistCycles.Add(new NistAssessmentCycle
        {
            AssessmentId = a.Id, Name = "Rodada 1", PeriodKind = NistCyclePeriodKind.Other,
            PeriodStart = today, PeriodEnd = today, Status = NistCycleStatus.Open, Version = 1,
        });
        await _db.SaveChangesAsync(ct);
        return new IdResponse(a.Id);
    }

    [HttpPost("{assessmentId:guid}/scopes")]
    [Authorize(Roles = "Manager,TenantAdmin")]
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
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<MaturitySuggestionDto>> AiSuggest(Guid scopeId, AiSuggestRequest req, CancellationToken ct)
    {
        // Filtro de tenant: escopo de outro tenant (ou inexistente) é 404.
        if (!await _db.Scopes.AnyAsync(s => s.Id == scopeId, ct))
            return NotFound($"Escopo {scopeId} não encontrado.");
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
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<IdResponse>> UpsertEvaluation(Guid scopeId, string code, EvaluationUpsertRequest req, CancellationToken ct)
    {
        // Filtro de tenant: escopo de outro tenant (ou inexistente) é 404 — nunca recebe a avaliação.
        if (!await _db.Scopes.AnyAsync(s => s.Id == scopeId, ct))
            return NotFound($"Escopo {scopeId} não encontrado.");
        static bool OutOfScale(int? v) => v is int x && (x < AssessmentMethodology.MinLevel || x > AssessmentMethodology.MaxLevel);
        if (OutOfScale(req.CurrentLevel) || OutOfScale(req.CurrentScore) || OutOfScale(req.TargetLevel) || OutOfScale(req.TargetScore))
            return BadRequest("Níveis de maturidade vão de 1 a 5.");
        var sub = await _db.Subcategories.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        if (sub is null) return NotFound($"Subcategory {code} not found.");

        // [AEGIS-NIST-JOURNEY-02] Esta superfície não conhece rodadas: só grava quando a avaliação tem UMA rodada. Com mais de
        // uma, escolher uma seria arbitrário — a jornada do AEGIS NIST nomeia a rodada explicitamente.
        var assessmentId = await _db.Scopes.AsNoTracking().Where(s => s.Id == scopeId).Select(s => s.AssessmentId).FirstAsync(ct);
        var cycles = await _db.NistCycles.AsNoTracking().Where(c => c.AssessmentId == assessmentId).Select(c => c.Id).ToListAsync(ct);
        if (cycles.Count != 1)
            return Conflict("Esta avaliação tem mais de uma rodada (ou nenhuma): registre pela jornada do AEGIS NIST, que nomeia a rodada.");
        var cycleId = cycles[0];

        var eval = await _db.Evaluations.FirstOrDefaultAsync(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryId == sub.Id, ct);
        if (eval is null)
        {
            eval = new SubcategoryEvaluation { AssessmentScopeId = scopeId, CycleId = cycleId, SubcategoryId = sub.Id, EvaluatedBy = EvaluatedBy.Analyst };
            _db.Evaluations.Add(eval);
        }

        eval.CurrentLevel = req.CurrentLevel;
        eval.CurrentScore = req.CurrentScore ?? req.CurrentLevel;
        eval.CurrentComments = req.CurrentComments;
        eval.TargetLevel = req.TargetLevel;
        eval.TargetScore = req.TargetScore ?? req.TargetLevel;
        eval.TargetComments = req.TargetComments;
        eval.ReviewedAt = DateTimeOffset.UtcNow;
        eval.Version += 1;

        await _db.SaveChangesAsync(ct);
        return new IdResponse(eval.Id);
    }

    /// <summary>Maturity rollup for the assessment (overall / per function / per category + gaps).</summary>
    [HttpGet("{assessmentId:guid}/maturity")]
    public async Task<ActionResult<MaturityRollupDto>> Maturity(Guid assessmentId, CancellationToken ct)
    {
        // [AEGIS-NIST-JOURNEY-02] Só a rodada mais recente: somar rodadas contaria cada subcategoria mais de uma vez.
        var latest = await AegisScore.Infrastructure.Nist.NistCycleSelection.LatestCycleIdsAsync(_db, ct);
        var rows = await (from s in _db.Scopes
                          where s.AssessmentId == assessmentId
                          join e in _db.Evaluations on s.Id equals e.AssessmentScopeId
                          where latest.Contains(e.CycleId)
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
