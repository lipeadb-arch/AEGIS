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
/// [AEGIS-NIST-JOURNEY-02] Ela não nomeia a rodada nem recebe a versão-base do cliente, então não consegue garantir o que a
/// jornada exige: recusa de rodada encerrada, conflito de edição, autoria e trilha. Por isso as ESCRITAS (criar avaliação,
/// criar escopo e gravar subcategoria) respondem 410 com a rota explícita da jornada — antes, a gravação mudava níveis e
/// carimbo de revisão até em rodada encerrada, sem trilha. As leituras (consolidado de maturidade e sugestão da IA, que
/// nunca é gravada) continuam. Papéis são conferidos antes da recusa.
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
    public IActionResult Create() => Retired("Avaliações são criadas em POST /api/v1/nist/assessments (com a primeira rodada e o escopo inicial).");

    [HttpPost("{assessmentId:guid}/scopes")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public IActionResult AddScope(Guid assessmentId) =>
        Retired("Escopos são criados em POST /api/v1/nist/assessments/{avaliação}/scopes.");

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

    /// <summary>Gravação de subcategoria: só pela rota explícita da jornada (rodada nomeada e versão-base do cliente).</summary>
    [HttpPut("scopes/{scopeId:guid}/evaluations/{code}")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public IActionResult UpsertEvaluation(Guid scopeId, string code) =>
        Retired("Grave a subcategoria em PUT /api/v1/nist/assessments/{avaliação}/cycles/{rodada}/scopes/{escopo}/subcategories/{código}, " +
                "informando a versão lida (expectedVersion): a rota explícita recusa rodada encerrada, confere a edição concorrente e registra autoria e trilha.");

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

    // Nada é lido nem gravado antes da recusa: a resposta não revela se o escopo ou a avaliação existem.
    private ObjectResult Retired(string detail) => Problem(
        statusCode: StatusCodes.Status410Gone,
        title: "Esta rota antiga não grava mais: use a jornada do AEGIS NIST.",
        detail: detail);

    internal static MaturityRollupDto ToDto(MaturityResult r)
    {
        static AggregateDto M(AggregateScore a) =>
            new(a.Level.ToString(), a.RefCode, a.CurrentScore, a.TargetScore, a.Gap, a.Count);

        return new MaturityRollupDto(M(r.Overall), r.Functions.Select(M).ToList(), r.Categories.Select(M).ToList());
    }
}
