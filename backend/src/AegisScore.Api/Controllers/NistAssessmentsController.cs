using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] AEGIS NIST — avaliação organizacional pelo NIST CSF 2.0, persistida: avaliação → escopo → seis
/// funções → subcategoria → evidência e justificativa → situação atual × alvo → lacunas e resumo.
///
/// Tenant IMPLÍCITO (claim <c>tenant_id</c> + filtro global fail-closed): avaliação, escopo, documento ou execução de outro
/// tenant respondem 404. Leitura para qualquer papel; gravação (avaliação, escopo, evidência e o pedido de sugestão à IA)
/// só para Manager/TenantAdmin — o mesmo critério do resultado manual do KNIGHT. O autor vem sempre do token.
/// A maturidade gravada aqui NÃO é o score de postura do tenant (aegis-score-v1): os dois conceitos ficam separados.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/nist/assessments")]
public sealed class NistAssessmentsController : ControllerBase
{
    private readonly INistAssessmentService _service;

    public NistAssessmentsController(INistAssessmentService service) => _service = service;

    /// <summary>Avaliações do tenant (mais recentes primeiro), com escopos e andamento.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<NistAssessmentView>>> List(CancellationToken ct) =>
        Run<IReadOnlyList<NistAssessmentView>>(async () => Ok(await _service.ListAsync(ct)));

    /// <summary>Histórico de maturidade por avaliação e escopo (um ponto por escopo, no mês da última revisão humana).</summary>
    [HttpGet("history")]
    public Task<ActionResult<IReadOnlyList<NistAssessmentHistoryItem>>> History(CancellationToken ct) =>
        Run<IReadOnlyList<NistAssessmentHistoryItem>>(async () => Ok(await _service.HistoryAsync(ct)));

    [HttpGet("{assessmentId:guid}")]
    public Task<ActionResult<NistAssessmentView>> Get(Guid assessmentId, CancellationToken ct) =>
        Run<NistAssessmentView>(async () => Ok(await _service.GetAsync(assessmentId, ct)));

    /// <response code="201">Avaliação criada (com o escopo inicial, quando informado).</response>
    /// <response code="400">Dados inválidos.</response>
    /// <response code="403">Papel insuficiente.</response>
    [HttpPost]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public Task<ActionResult<NistAssessmentView>> Create([FromBody] CreateNistAssessmentRequest request, CancellationToken ct) =>
        Run<NistAssessmentView>(async () =>
        {
            if (request is null) return BadRequest("Corpo da requisição ausente.");
            var created = await _service.CreateAsync(new CreateNistAssessmentCommand(
                request.Name ?? "", request.Description, request.StartDate, request.EndDate,
                request.InitialScopeName, request.InitialScopeDescription), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, created);
        });

    [HttpPost("{assessmentId:guid}/scopes")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public Task<ActionResult<NistScopeView>> AddScope(Guid assessmentId, [FromBody] CreateNistScopeRequest request, CancellationToken ct) =>
        Run<NistScopeView>(async () =>
        {
            if (request is null) return BadRequest("Corpo da requisição ausente.");
            var scope = await _service.AddScopeAsync(assessmentId, new CreateNistScopeCommand(request.Name ?? "", request.Description), ct);
            return StatusCode(StatusCodes.Status201Created, scope);
        });

    /// <summary>Perfil atual × alvo (geral, funções, categorias) e lacunas determináveis do escopo.</summary>
    [HttpGet("{assessmentId:guid}/scopes/{scopeId:guid}/profile")]
    public Task<ActionResult<NistProfileView>> Profile(Guid assessmentId, Guid scopeId, CancellationToken ct) =>
        Run<NistProfileView>(async () => Ok(await _service.GetProfileAsync(assessmentId, scopeId, ct)));

    /// <summary>Uma função com TODAS as categorias e subcategorias do catálogo, avaliadas ou não.</summary>
    [HttpGet("{assessmentId:guid}/scopes/{scopeId:guid}/functions/{functionCode}")]
    public Task<ActionResult<NistFunctionView>> Function(Guid assessmentId, Guid scopeId, string functionCode, CancellationToken ct) =>
        Run<NistFunctionView>(async () => Ok(await _service.GetFunctionAsync(assessmentId, scopeId, functionCode, ct)));

    [HttpGet("{assessmentId:guid}/scopes/{scopeId:guid}/subcategories/{code}")]
    public Task<ActionResult<NistSubcategoryDetailView>> Subcategory(Guid assessmentId, Guid scopeId, string code, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () => Ok(await _service.GetSubcategoryAsync(assessmentId, scopeId, code, ct)));

    /// <response code="200">Avaliação gravada; devolve o detalhe atualizado (com a nova versão).</response>
    /// <response code="400">Nível fora da escala, "não se aplica" sem justificativa, texto longo demais…</response>
    /// <response code="409">Versão desatualizada: outra pessoa gravou antes.</response>
    [HttpPut("{assessmentId:guid}/scopes/{scopeId:guid}/subcategories/{code}")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public Task<ActionResult<NistSubcategoryDetailView>> Save(
        Guid assessmentId, Guid scopeId, string code, [FromBody] SaveNistEvaluationRequest request, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
        {
            if (request is null) return BadRequest("Corpo da requisição ausente.");
            return Ok(await _service.SaveEvaluationAsync(assessmentId, scopeId, code, new SaveNistEvaluationCommand(
                request.CurrentLevel, request.TargetLevel, request.NotApplicable, request.CurrentComments, request.TargetComments,
                request.Rationale, request.Gaps, request.RiskImpact, request.ImprovementGuidance, request.OwnerName,
                request.ExpectedVersion), CurrentActor(), ct));
        });

    [HttpPost("{assessmentId:guid}/scopes/{scopeId:guid}/subcategories/{code}/evidence")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public Task<ActionResult<NistSubcategoryDetailView>> LinkEvidence(
        Guid assessmentId, Guid scopeId, string code, [FromBody] LinkNistEvidenceRequest request, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
        {
            if (request is null) return BadRequest("Corpo da requisição ausente.");
            return Ok(await _service.LinkEvidenceAsync(assessmentId, scopeId, code, new LinkNistEvidenceCommand(
                request.Kind ?? "", request.DocumentId, request.KnightRunId, request.KnightIndicatorId, request.Title,
                request.Uri, request.Notes, request.CollectedOn, request.ManualType), CurrentActor(), ct));
        });

    [HttpDelete("{assessmentId:guid}/scopes/{scopeId:guid}/subcategories/{code}/evidence/{evidenceId:guid}")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public Task<ActionResult<NistSubcategoryDetailView>> RemoveEvidence(
        Guid assessmentId, Guid scopeId, string code, Guid evidenceId, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
            Ok(await _service.RemoveEvidenceAsync(assessmentId, scopeId, code, evidenceId, CurrentActor(), ct)));

    /// <summary>
    /// Sugestão de interpretação da IA para a situação atual. NÃO é gravada: o analista revisa e decide. Com a IA
    /// desativada ou sem resposta, 503 — e a avaliação continua possível sem ela.
    /// </summary>
    [HttpPost("{assessmentId:guid}/scopes/{scopeId:guid}/subcategories/{code}/ai-suggestion")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    [EnableRateLimiting("ai-auditor")]
    public Task<ActionResult<NistAiSuggestionView>> Suggest(Guid assessmentId, Guid scopeId, string code, CancellationToken ct) =>
        Run<NistAiSuggestionView>(async () => Ok(await _service.SuggestAsync(assessmentId, scopeId, code, ct)));

    // ---- Apoio ---------------------------------------------------------------------------------------

    private async Task<ActionResult<T>> Run<T>(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NistAssessmentNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (NistAssessmentValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (NistAssessmentConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (NistAiUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message });
        }
    }

    /// <summary>O autor vem do TOKEN, nunca do corpo. Nome ausente permanece vazio.</summary>
    private RemediationActor CurrentActor()
    {
        Guid? accountId = Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var id) && id != Guid.Empty ? id : null;
        return new RemediationActor(accountId, User.FindFirst("name")?.Value ?? "");
    }
}

public sealed record CreateNistAssessmentRequest(
    string? Name, string? Description, DateOnly? StartDate, DateOnly? EndDate, string? InitialScopeName, string? InitialScopeDescription);

public sealed record CreateNistScopeRequest(string? Name, string? Description);

public sealed record SaveNistEvaluationRequest(
    int? CurrentLevel,
    int? TargetLevel,
    bool NotApplicable,
    string? CurrentComments,
    string? TargetComments,
    string? Rationale,
    string? Gaps,
    string? RiskImpact,
    string? ImprovementGuidance,
    string? OwnerName,
    int ExpectedVersion);

public sealed record LinkNistEvidenceRequest(
    string? Kind,
    Guid? DocumentId,
    Guid? KnightRunId,
    string? KnightIndicatorId,
    string? Title,
    string? Uri,
    string? Notes,
    DateOnly? CollectedOn,
    string? ManualType);
