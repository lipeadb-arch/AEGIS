using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-PRODUCT-03] Planos de ação nascidos de um achado do AEGIS KNIGHT — a jornada que leva de
/// "existe uma exposição" a "isto foi feito, e aqui está a prova".
///
/// Autorização e isolamento reutilizam EXATAMENTE os padrões já existentes; nenhuma matriz nova de permissões
/// foi criada. LER é permitido a qualquer papel autenticado do tenant (inclusive Analyst, que já lê toda a
/// evidência do KNIGHT). ESCREVER exige <c>Manager</c> ou <c>TenantAdmin</c> — os mesmos papéis que já
/// respondem pela publicação de uma fotografia auditável, porque criar, executar e validar uma ação também
/// produz registro permanente sobre o cliente.
///
/// Tenant SEMPRE implícito: resolvido do claim <c>tenant_id</c> e aplicado pelo Global Query Filter
/// fail-closed — nunca por URL, querystring ou corpo. Uma ação de outro tenant é indistinguível de
/// inexistente (404), jamais uma pista de que existe.
///
/// Nada aqui altera score, veredito, cobertura, snapshot de avaliação ou o ledger de controles: concluir uma
/// ação não torna um achado conforme.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/remediation/action-plans")]
public class RemediationController : ControllerBase
{
    private readonly IRemediationService _service;
    private readonly ITenantContext _tenant;

    public RemediationController(IRemediationService service, ITenantContext tenant)
    {
        _service = service;
        _tenant = tenant;
    }

    /// <summary>Ações de achado do tenant (mais recentes primeiro), com filtro por achado e por atividade.</summary>
    /// <response code="200">Lista (possivelmente vazia).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <param name="sourceType">
    /// Fonte de ORIGEM (Demo/MicrosoftEntraId/GoogleWorkspace). A tela passa a fonte da avaliação que está
    /// exibindo: sem esse recorte, uma ação nascida do cenário de DEMONSTRAÇÃO apareceria ao lado de achados
    /// de uma coleta real com a mesma aparência de trabalho real em curso.
    /// </param>
    /// <param name="mode">Modo de origem (Demo/Live) — o mesmo eixo, explícito.</param>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActionPlanDto>>> List(
        [FromQuery] string? indicatorId, [FromQuery] bool activeOnly = false,
        [FromQuery] string? sourceType = null, [FromQuery] string? mode = null,
        CancellationToken ct = default)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        KnightSourceType? source = null;
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            if (!Enum.TryParse<KnightSourceType>(sourceType, ignoreCase: true, out var parsedSource))
                return BadRequest($"Fonte desconhecida: '{sourceType}'.");
            source = parsedSource;
        }

        KnightAssessmentMode? parsedMode = null;
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!Enum.TryParse<KnightAssessmentMode>(mode, ignoreCase: true, out var m))
                return BadRequest($"Modo desconhecido: '{mode}'.");
            parsedMode = m;
        }

        var plans = await _service.ListAsync(
            new ActionPlanFilter(indicatorId, activeOnly, source, parsedMode), ct);
        return Ok(plans.Select(ToDto).ToList());
    }

    /// <summary>Uma ação com trilha e validações (404 inexistente/de outro tenant).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ActionPlanDto>> GetById(Guid id, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var plan = await _service.GetAsync(id, ct);
        return plan is null ? NotFound() : Ok(ToDto(plan));
    }

    /// <summary>
    /// Cria uma ação para um achado de uma avaliação do tenant. Um segundo clique na mesma origem NÃO
    /// duplica: retorna 409 com o identificador da ação ativa existente, para a tela abri-la.
    /// </summary>
    /// <response code="201">Ação criada.</response>
    /// <response code="400">Pedido inválido (título ausente, por exemplo).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="403">Papel do tenant insuficiente (Analyst não cria ações).</response>
    /// <response code="404">Avaliação ou achado inexistentes neste tenant.</response>
    /// <response code="409">Já existe ação ativa para este achado.</response>
    [HttpPost]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<ActionPlanDto>> Create(
        [FromBody] CreateActionPlanRequest request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (request is null)
            return BadRequest("Corpo da requisição ausente.");

        try
        {
            var created = await _service.CreateForFindingAsync(
                new CreateFindingActionPlanCommand(
                    request.RunId, request.IndicatorId, request.Title, request.ProposedAction,
                    request.ResponsiblePerson, request.ResponsibleArea, request.DueDate),
                CurrentActor(), ct);

            return created is null
                ? NotFound()
                : CreatedAtAction(nameof(GetById), new { id = created.Id }, ToDto(created));
        }
        catch (ActionPlanValidationException ex) { return BadRequest(ex.Message); }
        catch (ActionPlanConflictException ex) { return Conflict(ConflictBody(ex)); }
    }

    /// <summary>Edita campos e/ou avança a etapa. Exige a versão lida (concorrência otimista).</summary>
    /// <response code="409">Transição não permitida ou ação alterada por outra pessoa.</response>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<ActionPlanDto>> Update(
        Guid id, [FromBody] UpdateActionPlanRequest request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (request is null)
            return BadRequest("Corpo da requisição ausente.");

        ActionPlanStatus? status = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!TryParseStatus(request.Status!, out var parsed))
                return BadRequest($"Etapa desconhecida: '{request.Status}'.");
            status = parsed;
        }

        try
        {
            var updated = await _service.UpdateAsync(
                id,
                new UpdateActionPlanCommand(
                    request.ExpectedVersion, request.Title, request.ProposedAction,
                    request.ResponsiblePerson, request.ResponsibleArea, request.DueDate, status),
                CurrentActor(), ct);
            return updated is null ? NotFound() : Ok(ToDto(updated));
        }
        catch (ActionPlanValidationException ex) { return BadRequest(ex.Message); }
        catch (ActionPlanConflictException ex) { return Conflict(ConflictBody(ex)); }
    }

    /// <summary>Registra a EXECUÇÃO relatada e leva a ação para "Aguardando validação". Não comprova correção.</summary>
    [HttpPost("{id:guid}/execution")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<ActionPlanDto>> RecordExecution(
        Guid id, [FromBody] RecordActionPlanExecutionRequest request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (request is null)
            return BadRequest("Corpo da requisição ausente.");

        try
        {
            var updated = await _service.RecordExecutionAsync(
                id, new RecordExecutionCommand(request.ExpectedVersion, request.Notes, request.EvidenceReference),
                CurrentActor(), ct);
            return updated is null ? NotFound() : Ok(ToDto(updated));
        }
        catch (ActionPlanValidationException ex) { return BadRequest(ex.Message); }
        catch (ActionPlanConflictException ex) { return Conflict(ConflictBody(ex)); }
    }

    /// <summary>
    /// Registra uma validação. Com <c>validationRunId</c>, o DESFECHO é decidido pelo servidor a partir da
    /// comparação (fonte, regras, ordem temporal e suficiência da evidência para AQUELE indicador) — o cliente
    /// não escolhe "resolvido". Sem ela, exige-se referência de evidência e registra-se uma atestação humana.
    /// </summary>
    [HttpPost("{id:guid}/validations")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<ActionPlanDto>> Validate(
        Guid id, [FromBody] ValidateActionPlanRequest request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (request is null)
            return BadRequest("Corpo da requisição ausente.");

        try
        {
            var updated = await _service.ValidateAsync(
                id,
                new ValidateActionPlanCommand(
                    request.ExpectedVersion, request.ValidationRunId, request.EvidenceReference, request.Note),
                CurrentActor(), ct);
            return updated is null ? NotFound() : Ok(ToDto(updated));
        }
        catch (ActionPlanValidationException ex) { return BadRequest(ex.Message); }
        catch (ActionPlanConflictException ex) { return Conflict(ConflictBody(ex)); }
    }

    // ---- Apoio --------------------------------------------------------------------------------------

    /// <summary>
    /// O autor da mudança vem do TOKEN, nunca do corpo: um cliente não escolhe em nome de quem registra. Nome
    /// ausente permanece vazio — nunca é substituído por um rótulo inventado.
    /// </summary>
    private RemediationActor CurrentActor()
    {
        Guid? accountId = Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var id) && id != Guid.Empty
            ? id
            : null;
        return new RemediationActor(accountId, User.FindFirst("name")?.Value ?? "");
    }

    /// <summary>Corpo do 409: a mensagem e, quando o conflito é de duplicidade, a ação existente a abrir.</summary>
    private static object ConflictBody(ActionPlanConflictException ex) =>
        new { message = ex.Message, existingActionPlanId = ex.ExistingActionPlanId };

    private static bool TryParseStatus(string value, out ActionPlanStatus status)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "aberto": status = ActionPlanStatus.Aberto; return true;
            case "emandamento": status = ActionPlanStatus.EmAndamento; return true;
            case "aguardandovalidacao": status = ActionPlanStatus.AguardandoValidacao; return true;
            case "concluido": status = ActionPlanStatus.Concluido; return true;
            default: status = default; return false;
        }
    }

    private static ActionPlanDto ToDto(ActionPlanView p) => new(
        p.Id,
        p.KnightIndicatorId,
        p.OriginRunId,
        p.OriginAffectedCount,
        p.OriginSourceType?.ToString(),
        p.OriginMode?.ToString(),
        p.Title,
        p.ProposedAction,
        p.ResponsiblePerson,
        p.ResponsibleArea,
        p.DueDate,
        p.Status.ToString(),
        p.IsOverdue,
        p.IsActive,
        p.NextStep,
        p.ExecutionNotes,
        p.ExecutionEvidenceRef,
        p.ExecutedAt,
        p.CompletedAt,
        p.CreatedAt,
        p.CycleStartedAt,
        p.Version,
        p.LatestValidation is null ? null : ToDto(p.LatestValidation),
        p.ApplicableValidation is null ? null : ToDto(p.ApplicableValidation),
        p.AllowedTransitions.Select(t => t.ToString()).ToList(),
        p.ClosureBlockedReason,
        p.Validations.Select(ToDto).ToList(),
        p.Events.Select(e => new ActionPlanEventDto(
            e.Kind.ToString(), e.At, e.ActorName, e.FromStatus?.ToString(), e.ToStatus?.ToString(), e.Note)).ToList());

    private static ActionPlanValidationDto ToDto(ActionPlanValidationView v) => new(
        v.Method.ToString(),
        v.Outcome.ToString(),
        v.ValidationRunId,
        v.EvidenceReference,
        v.EvidenceCollectedAt,
        v.PrecedesReportedExecution,
        v.AppliesToCurrentCycle,
        v.ObservedBefore,
        v.ObservedAfter,
        v.ObjectsNoLongerPresent,
        v.ComparedBySets,
        v.Rationale,
        v.DecidedAt,
        v.DecidedByName);
}
