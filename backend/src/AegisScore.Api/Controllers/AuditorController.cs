using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Auditor Virtual — UMA identidade em toda a aplicação. A página aberta e as seleções da tela dão o FOCO da
/// conversa; não mudam a personalidade nem restringem o conhecimento. O contexto é montado no servidor a partir do tenant do token
/// (KNIGHT, NIST, documentos, inventário, publicações e postura do ambiente); a conversa pertence ao tenant E à conta autenticada.
///
/// Somente leitura: nenhuma resposta grava ou altera avaliação, nível, nota, achado, plano, evidência ou publicação — propostas seguem
/// pelos fluxos de revisão e confirmação das telas. O acesso ao provedor de IA continua pelo roteador tenant-scoped (gate, allowlist,
/// limites); sem provedor autorizado, responde o motor simulado, dito como demonstração.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/auditor")]
public class AuditorController : ControllerBase
{
    internal const int MaxMessage = 4000;

    private readonly IAiAssessmentService _ai;
    private readonly IAuditorContextBuilder _context;
    private readonly IAuditorConversationStore _conversations;

    public AuditorController(IAiAssessmentService ai, IAuditorContextBuilder context, IAuditorConversationStore conversations)
    {
        _ai = ai;
        _context = context;
        _conversations = conversations;
    }

    /// <summary>Um turno do Auditor no foco da tela, fundamentado nos registros do tenant e citando as fontes.</summary>
    /// <response code="200">Resposta do Auditor (fontes citadas, limitações, modo).</response>
    /// <response code="400">Mensagem ausente ou longa demais; identificadores da tela em formato inválido.</response>
    /// <response code="404">Conversa, seleção NIST ou avaliação KNIGHT inexistente neste tenant e conta.</response>
    /// <response code="409">Outra resposta foi concluída na mesma conversa, ou a conversa atingiu o limite.</response>
    /// <response code="429">Limite de perguntas por minuto atingido.</response>
    /// <response code="503">Motor de IA indisponível (transitório — repetir).</response>
    [HttpPost("chat")]
    [EnableRateLimiting("ai-auditor")]
    public async Task<ActionResult<AuditorChatResponseDto>> Chat(AuditorChatRequestDto req, CancellationToken ct)
    {
        var message = (req.Message ?? "").Trim();
        if (message.Length == 0 || message.Length > MaxMessage)
            return BadRequest(Msg($"Escreva uma mensagem de até {MaxMessage:N0} caracteres."));
        if (!Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var accountId) || accountId == Guid.Empty)
            return Unauthorized();

        AuditorFocus focus;
        try
        {
            focus = AuditorFocus.From(req.Page, req.Nist?.AssessmentId, req.Nist?.CycleId, req.Nist?.ScopeId, req.Nist?.Code,
                req.Knight?.RunId, req.Knight?.IndicatorId);
        }
        catch (AuditorFocusInvalidException ex)
        {
            return BadRequest(Msg(ex.Message));
        }

        try
        {
            // O histórico enviado pela tela NÃO é autoritativo: uma conversa de outro tenant ou de outra conta não é lida nem continuada.
            var conversation = await _conversations.ReadAsync(accountId, req.ConversationId, ct);
            var context = await _context.BuildAsync(focus, ct);
            var reply = await _ai.ChatAsync(new AuditorChatRequest(AuditorScope.Global, conversation.Messages, message, context, focus), ct);
            var checkedReply = AuditorReplyCheck.Check(reply, context.Assessments);
            var focusLabel = context.Assessments?.FocusLabel ?? focus.PageLabel;
            await _conversations.AppendAsync(accountId, conversation, message, checkedReply.Message, focusLabel, reply.Simulated, ct);

            return Ok(new AuditorChatResponseDto(
                checkedReply.Message, "GLOBAL", AuditorIntents.ToWire(AuditorIntent.Copilot), null,
                conversation.Id, reply.Simulated ? "Simulated" : "Real", focusLabel,
                checkedReply.Sources.Select(ToDto).ToList(),
                context.Assessments?.Limitations ?? Array.Empty<string>(),
                checkedReply.Notes));
        }
        catch (AuditorConversationNotFoundException)
        {
            return NotFound(Msg("Conversa não encontrada para este ambiente e usuário. Inicie uma nova conversa."));
        }
        catch (AuditorFocusNotFoundException ex)
        {
            return NotFound(Msg(ex.Message));
        }
        catch (AuditorConversationConflictException ex)
        {
            return Conflict(Msg(ex.Message));
        }
        catch (NistAssessmentNotFoundException ex)
        {
            return NotFound(Msg(ex.Message));
        }
    }

    private static AuditorSourceDto ToDto(AuditorContextSource s) => new(
        s.Key, s.Module, s.Nature, AuditorSourceNature.Label(s.Nature), s.Title, s.Detail, s.Date, s.IsDemo, s.Limitation,
        s.Link is null ? null : new AuditorSourceLinkDto(s.Link.Route, s.Link.Query, s.Link.Fragment));

    private static object Msg(string message) => new { title = message, message };
}
