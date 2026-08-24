using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;

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
    private readonly IAuditorContextBuilder _context;

    public AuditorController(IAiAssessmentService ai, IAuditorContextBuilder context)
    {
        _ai = ai;
        _context = context;
    }

    /// <summary>
    /// Um turno do Copiloto no escopo da tela ativa, FUNDAMENTADO no contexto tenant-scoped somente leitura.
    /// </summary>
    /// <response code="200">Resposta do Copiloto.</response>
    /// <response code="400">Mensagem do usuário ausente.</response>
    /// <response code="429">Limite de perguntas por minuto atingido (Free Tier).</response>
    /// <response code="503">Motor de IA indisponível (transitório — repetir).</response>
    [HttpPost("chat")]
    [EnableRateLimiting("ai-auditor")]
    public async Task<ActionResult<AuditorChatResponseDto>> Chat(AuditorChatRequestDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Message))
            return BadRequest("A mensagem do usuário é obrigatória.");

        var scope = AuditorScopes.FromCode(req.ContextScope);
        var history = (req.History ?? Array.Empty<AuditorChatMessageDto>())
            .Select(m => new AuditorMessage(m.Role, m.Content))
            .ToList();

        // Contexto tenant-scoped (score/cobertura, lacunas, evidência documental curta, conectores,
        // recomendações) montado SERVER-SIDE a partir do tenant autenticado — nunca do corpo (Zero Trust).
        // No modo simulado ou fora da allowlist, este contexto NÃO trafega para nenhum motor externo (o gate
        // roteia para o stub); no modo demonstrativo, só tenants sintéticos da allowlist o enviam ao Anthropic.
        var context = await _context.BuildAsync(ct);

        // A IA roteia a intenção (COPILOT vs START_INTERVIEW) e o campo Message já traz a resposta/pergunta.
        // AiUnavailableException/AiQuotaExhaustedException (motor real caído/cota) viram 503 no middleware.
        var reply = await _ai.ChatAsync(new AuditorChatRequest(scope, history, req.Message, context), ct);
        return Ok(new AuditorChatResponseDto(
            reply.Message, reply.Scope.ToString(), AuditorIntents.ToWire(reply.Intent), reply.Metadata));
    }
}
