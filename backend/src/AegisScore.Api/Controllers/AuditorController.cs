using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Copiloto GRC ONIPRESENTE — o Auditor Virtual com consciência de contexto, disponível em TODA a
/// plataforma (não mais preso à aba de Governança). Recebe o escopo da tela ativa (<c>ContextScope</c>) e
/// delega ao motor de IA (<see cref="IAiAssessmentService.ChatAsync"/>), que ajusta o System Prompt
/// DINAMICAMENTE — auditar só Protect (exigir MFA/criptografia) em PR, gerar relatório executivo do Secure
/// Score em GLOBAL, etc.
///
/// Distinto do <c>GrcInterviewController</c> (<c>/governance/interviews</c>), que conduz a entrevista
/// estruturada de fechamento de gaps do pilar GOVERN: ESTE é o chat livre, escopado por Função NIST.
///
/// Tenant IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT — nunca do corpo (Zero Trust). O escopo
/// NÃO é fronteira de segurança (o chat é read-only); um escopo desconhecido cai em GLOBAL (fail-safe).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/auditor")]
public class AuditorController : ControllerBase
{
    private readonly IAiAssessmentService _ai;

    public AuditorController(IAiAssessmentService ai) => _ai = ai;

    /// <summary>
    /// Um turno do Copiloto no escopo da tela ativa.
    /// </summary>
    /// <response code="200">Resposta do Copiloto.</response>
    /// <response code="400">Mensagem do usuário ausente.</response>
    /// <response code="503">Motor de IA indisponível (transitório — repetir).</response>
    [HttpPost("chat")]
    public async Task<ActionResult<AuditorChatResponseDto>> Chat(AuditorChatRequestDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return BadRequest("A mensagem do usuário é obrigatória.");

        var scope = AuditorScopes.FromCode(req.ContextScope);
        var history = (req.History ?? Array.Empty<AuditorChatMessageDto>())
            .Select(m => new AuditorMessage(m.Role, m.Content))
            .ToList();

        // A IA roteia a intenção (COPILOT vs START_INTERVIEW) e o campo Message já traz a resposta/pergunta.
        // AiUnavailableException (motor real caído) vira 503 no GlobalExceptionHandlingMiddleware.
        var reply = await _ai.ChatAsync(new AuditorChatRequest(scope, history, req.Message), ct);
        return Ok(new AuditorChatResponseDto(
            reply.Message, reply.Scope.ToString(), AuditorIntents.ToWire(reply.Intent), reply.Metadata));
    }
}
