using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Services;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Telemetry — superfície de ingestão PASSIVA de sinais de segurança (webhook para EDR/SIEM: Microsoft
/// Defender, Sentinel, CrowdStrike…). É o chamador que faltava para o motor de avaliação por IA
/// (<see cref="ITelemetryIngestionService"/> → <c>EvaluateAsync</c> → <c>ControlStateWriter</c>): cada
/// alerta ingerido vira um veredito NIST com fonte <c>Telemetry</c> — a evidência AUTORITATIVA, a única
/// que pode levar um controle a 100% (a análise documental tem teto de 50%).
///
/// Tenant IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT pelo ITenantContext e aplicado pelo
/// Global Query Filter (fail-closed) — o sinal é sempre gravado no tenant do chamador, nunca via corpo/URL.
/// </summary>
[ApiController]
[Authorize]   // superfície de escrita no ledger: exige usuário autenticado (o FallbackPolicy já cobre; explícito declara a intenção).
[Route("api/v1/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly ITelemetryIngestionService _ingestion;

    public TelemetryController(ITelemetryIngestionService ingestion) => _ingestion = ingestion;

    /// <summary>
    /// Ingere um alerta de segurança e avalia o controle NIST indicado. O motor decide o status
    /// (Compliant / MitigatedByThirdParty / NonCompliant) e o <c>ControlStateWriter</c> faz o upsert do
    /// ledger com fonte <c>Telemetry</c> — sobrescrevendo qualquer veredito documental vigente.
    /// </summary>
    /// <response code="200">Veredito aplicado ao ledger.</response>
    /// <response code="400">Payload incompleto ou <c>SubcategoryCode</c> fora do catálogo NIST.</response>
    /// <response code="503">Motor de IA indisponível (transitório — repetir).</response>
    [HttpPost("ingest")]
    public async Task<ActionResult<TelemetryVerdictDto>> Ingest(TelemetryIngestionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SubcategoryCode))
            return BadRequest("SubcategoryCode é obrigatório: indica qual controle NIST a evidência endereça.");
        if (string.IsNullOrWhiteSpace(req.RawData))
            return BadRequest("RawData é obrigatório: é a evidência técnica que o motor avalia.");

        var signal = new TelemetrySignal(
            req.Source ?? "", req.EventName ?? "", req.Severity ?? "", req.SubcategoryCode, req.RawData);

        ComplianceVerdict verdict;
        try
        {
            verdict = await _ingestion.IngestAsync(signal, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Único caminho de InvalidOperationException no motor: código NIST inexistente no catálogo.
            // É erro do cliente (400), não falha do servidor — a mensagem cita o código e não vaza interno.
            return BadRequest(ex.Message);
        }

        var pct = verdict.MaxScorePoints == 0
            ? 0
            : (int)Math.Round(100.0 * verdict.AwardedScore / verdict.MaxScorePoints);

        return Ok(new TelemetryVerdictDto(
            req.SubcategoryCode, verdict.Status.ToString(),
            verdict.AwardedScore, verdict.MaxScorePoints, pct, verdict.AiEvidence));
    }
}
