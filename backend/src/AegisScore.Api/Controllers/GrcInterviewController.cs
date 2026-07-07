using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// GOVERN → Auditor Virtual (chatbot GRC). Identifica os gaps do NIST não cobertos pelos documentos
/// e conduz uma entrevista para fechá-los: as respostas atualizam o ledger de cobertura e, quando
/// confirmam uma lacuna na prática, geram um IdentifiedRisk. Sem [FromHeader] de tenant.
/// </summary>
[ApiController]
[Route("api/v1/governance/interviews")]
public class GrcInterviewController : ControllerBase
{
    private const string GovernFunctionCode = "GV";

    private readonly AegisScoreDbContext _db;
    private readonly IAiAssessmentService _ai;

    public GrcInterviewController(AegisScoreDbContext db, IAiAssessmentService ai)
    {
        _db = db;
        _ai = ai;
    }

    /// <summary>Gaps atuais do pilar GOVERN (subcategorias NaoCoberto/Parcial) — semeia o diagnóstico.</summary>
    [HttpGet("gaps")]
    public async Task<ActionResult<IEnumerable<GapDto>>> Gaps(CancellationToken ct)
    {
        var gaps = await GvGapsAsync(ct);
        return gaps.Select(g => new GapDto(g.Code, g.Description, g.Status.ToString())).ToList();
    }

    /// <summary>Abre uma sessão: resolve os gaps-alvo e gera a primeira pergunta investigativa da IA.</summary>
    [HttpPost]
    public async Task<ActionResult<InterviewTurnDto>> Start(StartInterviewRequest req, CancellationToken ct)
    {
        var targets = req.SubcategoryCodes?.ToList()
            ?? (await GvGapsAsync(ct)).Select(g => g.Code).ToList();

        if (targets.Count == 0)
            return BadRequest("Sem gaps a investigar — todas as subcategorias GV já estão cobertas.");

        // A sessão nasce em memória — Entity.Id já é gerado no cliente (Guid.NewGuid()),
        // então podemos referenciá-lo na chamada à IA antes de qualquer gravação.
        var session = new GrcInterviewSession
        {
            Title = string.IsNullOrWhiteSpace(req.Title) ? $"Diagnóstico de Gaps — {DateTime.UtcNow:yyyy-MM}" : req.Title!,
            AssessmentId = req.AssessmentId,
            Status = GrcInterviewStatus.Active,
            TargetSubcategoryCodes = targets,
        };

        // IA PRIMEIRO: se o motor de IA falhar (ex.: AiUnavailableException → 503), nada é
        // gravado no PostgreSQL — evita as sessões órfãs (Active, sem mensagens).
        var turn = await _ai.ConductInterviewTurnAsync(
            new InterviewContext(session.Id, session.Title, GapFraming(targets)), ct);

        // Só então persistimos sessão + primeira pergunta juntas, num único SaveChanges
        // (mesma transação): ou ambas entram, ou nenhuma. O StampTenant carimba as duas.
        var question = new GrcInterviewMessage
        {
            SessionId = session.Id,
            Role = GrcMessageRole.Assistant,
            Content = turn.Question ?? "",
            Sequence = 0,
            TargetSubcategoryCode = turn.TargetSubcategoryCode,
        };
        session.Messages.Add(question);
        _db.GrcInterviewSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        return new InterviewTurnDto(session.Id, ToDto(question), turn.IsComplete, null, null);
    }

    /// <summary>Sessão + histórico de mensagens (replay do drawer de chat).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InterviewSessionDto>> Get(Guid id, CancellationToken ct)
    {
        var s = await _db.GrcInterviewSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();

        var msgs = await _db.GrcInterviewMessages.AsNoTracking()
            .Where(m => m.SessionId == id).OrderBy(m => m.Sequence).ToListAsync(ct);

        return new InterviewSessionDto(
            s.Id, s.Title, s.Status.ToString(), s.TargetSubcategoryCodes, s.StartedAt,
            msgs.Select(ToDto).ToList());
    }

    /// <summary>
    /// Registra a resposta do usuário, avalia a subcategoria sob investigação (converte o gap para
    /// Coberto/Parcial e gera um IdentifiedRisk quando a lacuna persiste) e devolve a próxima pergunta.
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<InterviewTurnDto>> Answer(Guid id, PostAnswerRequest req, CancellationToken ct)
    {
        var session = await _db.GrcInterviewSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return NotFound();
        if (session.Status != GrcInterviewStatus.Active) return Conflict("Sessão não está ativa.");

        var history = await _db.GrcInterviewMessages
            .Where(m => m.SessionId == id).OrderBy(m => m.Sequence).ToListAsync(ct);

        // Subcategoria sob investigação = alvo da última pergunta do assistente.
        var lastQuestion = history.LastOrDefault(m => m.Role == GrcMessageRole.Assistant);
        var targetCode = lastQuestion?.TargetSubcategoryCode;

        await AppendMessageAsync(id, GrcMessageRole.User, req.Content, targetCode, ct);

        // Avalia a resposta e atualiza o ledger + eventual risco.
        CoverageChangeDto? change = null;
        Guid? identifiedRiskId = null;
        if (!string.IsNullOrWhiteSpace(targetCode) && lastQuestion is not null)
            (change, identifiedRiskId) = await EvaluateAnswerAsync(session, targetCode!, lastQuestion.Content, req.Content, ct);

        // Próxima pergunta (ou fim).
        var replayed = await _db.GrcInterviewMessages
            .Where(m => m.SessionId == id).OrderBy(m => m.Sequence).ToListAsync(ct);
        var turn = await _ai.ConductInterviewTurnAsync(
            new InterviewContext(id, session.Title, BuildHistory(session.TargetSubcategoryCodes, replayed)), ct);

        InterviewMessageDto? nextDto = null;
        if (!turn.IsComplete && !string.IsNullOrWhiteSpace(turn.Question))
        {
            var next = await AppendMessageAsync(id, GrcMessageRole.Assistant, turn.Question, turn.TargetSubcategoryCode, ct);
            nextDto = ToDto(next);
        }
        else
        {
            session.Status = GrcInterviewStatus.Completed;
            session.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return new InterviewTurnDto(id, nextDto, turn.IsComplete, change, identifiedRiskId);
    }

    /// <summary>Finaliza a sessão manualmente.</summary>
    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        var s = await _db.GrcInterviewSessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        s.Status = GrcInterviewStatus.Completed;
        s.CompletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Trilha de auditoria: riscos identificados pela sessão.</summary>
    [HttpGet("{id:guid}/outcomes")]
    public async Task<ActionResult<IEnumerable<IdentifiedRiskDto>>> Outcomes(Guid id, CancellationToken ct)
    {
        var risks = await _db.IdentifiedRisks.AsNoTracking()
            .Where(r => r.OriginInterviewSessionId == id)
            .OrderBy(r => r.SubcategoryCode)
            .ToListAsync(ct);

        return risks.Select(r => new IdentifiedRiskDto(
            r.Id, r.Title, r.Description, r.Cause, r.Consequence, r.SubcategoryCode,
            r.AssessmentId, r.PromotedToRisk, r.IdentifiedAt)).ToList();
    }

    // ---- helpers ----

    /// <summary>Pede à IA um nível de maturidade para a resposta e converte em cobertura + risco.</summary>
    private async Task<(CoverageChangeDto? change, Guid? riskId)> EvaluateAnswerAsync(
        GrcInterviewSession session, string code, string question, string answer, CancellationToken ct)
    {
        var description = await _db.Subcategories.AsNoTracking()
            .Where(s => s.Code == code).Select(s => s.Description).FirstOrDefaultAsync(ct) ?? code;

        var suggestion = await _ai.SuggestMaturityAsync(new MaturitySuggestionRequest(
            code, description,
            new[] { (question, answer, (string?)null) },
            Array.Empty<string>(),
            Array.Empty<(string, double?, int?)>()), ct);

        var status = suggestion.CurrentLevel >= 4 ? CoverageStatus.Coberto
                   : suggestion.CurrentLevel >= 2 ? CoverageStatus.Parcial
                   : CoverageStatus.NaoCoberto;

        // Atualiza o ledger (fonte Interview / Both).
        var cov = await _db.SubcategoryCoverages.FirstOrDefaultAsync(c => c.SubcategoryCode == code, ct);
        if (cov is null)
        {
            cov = new SubcategoryCoverage { SubcategoryCode = code };
            _db.SubcategoryCoverages.Add(cov);
        }
        cov.Status = status;
        cov.EvidenceSource = cov.EvidenceSource is CoverageEvidenceSource.Document or CoverageEvidenceSource.Both
            ? CoverageEvidenceSource.Both : CoverageEvidenceSource.Interview;
        cov.OriginInterviewSessionId = session.Id;
        cov.Confidence = suggestion.Confidence;
        cov.LastEvaluatedAt = DateTimeOffset.UtcNow;

        // Lacuna confirmada na prática → gera IdentifiedRisk.
        Guid? riskId = null;
        if (status != CoverageStatus.Coberto)
        {
            var risk = new IdentifiedRisk
            {
                Title = $"Lacuna de controle em {code}",
                Description = suggestion.Rationale,
                SubcategoryCode = code,
                AssessmentId = session.AssessmentId,
                OriginInterviewSessionId = session.Id,
            };
            _db.IdentifiedRisks.Add(risk);
            riskId = risk.Id;
        }

        await _db.SaveChangesAsync(ct);
        return (new CoverageChangeDto(code, status.ToString(), cov.EvidenceSource.ToString()), riskId);
    }

    /// <summary>Subcategorias GV não cobertas (gaps), com a descrição do catálogo.</summary>
    private async Task<List<(string Code, string Description, CoverageStatus Status)>> GvGapsAsync(CancellationToken ct)
    {
        var subs = await _db.Subcategories.AsNoTracking()
            .Where(s => s.Code.StartsWith(GovernFunctionCode))
            .Select(s => new { s.Code, s.Description })
            .ToListAsync(ct);

        var ledger = await _db.SubcategoryCoverages.AsNoTracking().ToListAsync(ct);
        var status = ledger.ToDictionary(x => x.SubcategoryCode, x => x.Status);

        return subs
            .Select(s => (s.Code, s.Description, status.TryGetValue(s.Code, out var st) ? st : CoverageStatus.NaoCoberto))
            .Where(x => x.Item3 != CoverageStatus.Coberto)
            .OrderBy(x => x.Code)
            .ToList();
    }

    private async Task<GrcInterviewMessage> AppendMessageAsync(
        Guid sessionId, GrcMessageRole role, string content, string? targetCode, CancellationToken ct)
    {
        var next = await _db.GrcInterviewMessages.Where(m => m.SessionId == sessionId).CountAsync(ct);
        var msg = new GrcInterviewMessage
        {
            SessionId = sessionId,
            Role = role,
            Content = content,
            Sequence = next,
            TargetSubcategoryCode = targetCode,
        };
        _db.GrcInterviewMessages.Add(msg);
        await _db.SaveChangesAsync(ct);
        return msg;
    }

    private static List<string> GapFraming(IReadOnlyList<string> targets) => new()
    {
        $"Contexto de auditoria GRC. Investigue, uma de cada vez, as subcategorias NIST CSF 2.0 " +
        $"ainda não cobertas por documentos: {string.Join(", ", targets)}.",
    };

    private static List<string> BuildHistory(IReadOnlyList<string> targets, IEnumerable<GrcInterviewMessage> messages)
    {
        var history = GapFraming(targets);
        history.AddRange(messages.Select(m => $"{m.Role}: {m.Content}"));
        return history;
    }

    private static InterviewMessageDto ToDto(GrcInterviewMessage m) =>
        new(m.Id, m.Role.ToString(), m.Content, m.Sequence, m.TargetSubcategoryCode, m.SentAt);
}
