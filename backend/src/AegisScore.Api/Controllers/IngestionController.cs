using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-AUD-020] Entrada GENÉRICA e neutra de fornecedor para eventos de SIEM/EDR (push). Superfície
/// EXTERNA: autentica-se pela CHAVE do conector (header dedicado), NUNCA por JWT de usuário — por isso é
/// <c>[AllowAnonymous]</c> (fora da FallbackPolicy). O <c>connectorId</c> identifica o conector mas NÃO
/// autoriza sozinho; o tenant é resolvido EXCLUSIVAMENTE pelo ConnectorConfig autenticado, nunca do corpo,
/// query string ou header do chamador. Conector inexistente/incompatível/desabilitado e chave inválida
/// produzem a MESMA recusa genérica (401), sem revelar existência. A chave e o payload NUNCA são logados.
/// </summary>
[ApiController]
[Route("api/v1/ingestion")]
[AllowAnonymous]
public sealed class IngestionController : ControllerBase
{
    /// <summary>Header dedicado da chave de ingestão — NUNCA em query string/URL (evita vazamento em log/proxy).</summary>
    private const string KeyHeader = "X-Ingestion-Key";

    private readonly IConnectorIngestionAuthenticator _auth;
    private readonly IEvidenceIngestionExecutor _executor;

    public IngestionController(IConnectorIngestionAuthenticator auth, IEvidenceIngestionExecutor executor)
    {
        _auth = auth;
        _executor = executor;
    }

    /// <summary>
    /// Recebe um lote de eventos. 401 genérico para autenticação inválida; 400 para contrato inválido; 422
    /// para signalKey sem mapping; 200 informa <c>accepted</c>/<c>deduplicated</c>/<c>receivedAt</c>.
    /// </summary>
    [HttpPost("connectors/{connectorId:guid}/events")]
    [EnableRateLimiting("ingestion")]
    [RequestSizeLimit(2 * 1024 * 1024)]   // teto do corpo do request (2 MB); o executor ainda limita eventos/payload
    public async Task<IActionResult> Ingest(
        Guid connectorId, [FromBody] IngestionBatchDto batch, CancellationToken ct)
    {
        // A chave viaja SÓ no header dedicado. Autenticação e tenant vêm do conector — nunca do chamador.
        var key = Request.Headers[KeyHeader].FirstOrDefault();
        var connector = await _auth.AuthenticateAsync(connectorId, key ?? "", ct);
        if (connector is null)
            // Recusa genérica e INDISTINGUÍVEL: não confirma se o conector existe. Nada da chave/payload em log.
            return Unauthorized(new { title = "Credenciais de ingestão inválidas.", status = 401 });

        var result = await _executor.IngestPushAsync(connector, ToBatch(batch), ct);

        return result.Outcome switch
        {
            PushOutcome.Accepted => Ok(new IngestionResultDto(
                result.Accepted, result.Deduplicated, 0, result.ReceivedAt)),
            PushOutcome.ContractError => BadRequest(new IngestionResultDto(
                0, 0, result.Errors.Count, result.ReceivedAt, result.Errors)),
            PushOutcome.Unmapped => UnprocessableEntity(new IngestionResultDto(
                0, 0, 0, result.ReceivedAt, result.Errors)),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>Traduz o DTO da borda no contrato da Application. O payload bruto é serializado do JSON recebido.</summary>
    private static EvidenceBatch ToBatch(IngestionBatchDto dto) => new(
        dto.SchemaVersion,
        (dto.Events ?? Array.Empty<IngestionEventDto>())
            .Select(e => new EvidenceEvent(
                e.EventId, e.SignalKey, e.EventType, e.Source, e.Severity, e.NumericValue, e.Unit,
                e.CollectedAt,
                // Payload bruto (objeto JSON) → texto; será PROTEGIDO em repouso pelo executor, nunca legível.
                e.Data is { } d ? d.GetRawText() : null))
            .ToList());
}
