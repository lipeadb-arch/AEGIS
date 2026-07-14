using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Application.Telemetry.Providers;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Telemetria de IDENTIDADE (Microsoft Entra ID) — superfície ATIVA de coleta: diferente do
/// <see cref="TelemetryController"/> (webhook passivo que recebe o payload pronto), aqui o Aegis PUXA a
/// postura de identidade do tenant via <see cref="IEntraIdTelemetryProvider"/> e a avalia.
///
/// Tenant IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT (<c>HttpTenantContext</c>), nunca do corpo
/// (Zero Trust). O corpo carrega apenas o CONTEXTO que o Entra desconhece — domínio a consultar e controles
/// compensatórios de rede (isolamento de OT/legado). Reusa a esteira do <see cref="ITelemetryIngestionService"/>
/// (nenhum serviço novo): o retrato de identidade é MULTI-CONTROLE, então é ingerido uma vez por controle
/// que alimenta (PR.AA-01 = dimensão MFA; GV.RR-01 = dimensão governança/excesso de admins).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/telemetry/identity")]
public class IdentityTelemetryController : ControllerBase
{
    /// <summary>Controles NIST que o retrato de identidade do Entra alimenta (código + pilar do cabeçalho).</summary>
    private static readonly (string Code, string Pillar)[] IdentityControls =
    {
        ("PR.AA-01", "Protect"),
        ("GV.RR-01", "Govern"),
    };

    private readonly IEntraIdTelemetryProvider _entra;
    private readonly ITelemetryIngestionService _ingestion;
    private readonly ITenantContext _tenant;

    public IdentityTelemetryController(
        IEntraIdTelemetryProvider entra, ITelemetryIngestionService ingestion, ITenantContext tenant)
    {
        _entra = entra;
        _ingestion = ingestion;
        _tenant = tenant;
    }

    /// <summary>
    /// Coleta a postura de identidade do Entra ID do tenant e a avalia contra PR.AA-01 e GV.RR-01. O corpo
    /// é OPCIONAL: informe <c>hasNetworkIsolation</c>/<c>compensatingControls</c> para que o motor pondere
    /// contas de serviço/OT sem MFA isoladas na rede (controle compensatório) e não gere falso positivo.
    /// </summary>
    /// <response code="200">Vereditos aplicados ao ledger (um por controle avaliado), com fonte Telemetry.</response>
    /// <response code="400">Um dos controles-alvo não existe no catálogo NIST.</response>
    /// <response code="401">Tenant não resolvido no contexto (claim tenant_id ausente).</response>
    /// <response code="503">Motor de IA indisponível (transitório — repetir).</response>
    [HttpPost("entra-id")]
    public async Task<ActionResult<IReadOnlyList<TelemetryVerdictDto>>> IngestEntraId(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] EntraIdIdentityIngestionRequest? request,
        CancellationToken ct)
    {
        // Fail-closed: sem tenant resolvido do JWT, nada é coletado nem avaliado.
        if (_tenant.TenantId is not Guid tenantId)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var domain = string.IsNullOrWhiteSpace(request?.TenantDomain) ? "" : request!.TenantDomain!;

        // 1) Coleta a postura crua do Entra ID (stub agora; Microsoft Graph + OAuth depois).
        var posture = await _entra.FetchIdentityPostureAsync(tenantId, domain, ct);

        // 2) Normaliza ENXERTANDO o contexto de rede que o Entra não conhece (isolamento/compensatórios).
        var signal = posture.ToTelemetrySignal(
            request?.HasNetworkIsolation ?? false, request?.CompensatingControls);

        // 3) Reusa a esteira: o MESMO retrato (ToMetricLines) alimenta cada controle. Um payload por controle,
        //    cabeçalho de código distinto — o StubLlmClient/motor discrimina pela âncora de controle.
        var metrics = signal.ToMetricLines();
        try
        {
            var verdicts = new List<TelemetryVerdictDto>(IdentityControls.Length);
            foreach (var (code, pillar) in IdentityControls)
            {
                var verdict = await _ingestion.IngestCategoryAsync(
                    new CategoryTelemetrySignal(code, pillar, "Identity", metrics), ct);
                verdicts.Add(ToDto(code, verdict));
            }
            return Ok(verdicts);
        }
        catch (InvalidOperationException ex)
        {
            // Código NIST fora do catálogo é erro do cliente/configuração, não 500 opaco.
            return BadRequest(ex.Message);
        }
    }

    private static TelemetryVerdictDto ToDto(string code, ComplianceVerdict v) => new(
        code,
        v.Status.ToString(),
        v.AwardedScore,
        v.MaxScorePoints,
        v.MaxScorePoints == 0 ? 0 : (int)Math.Round(100.0 * v.AwardedScore / v.MaxScorePoints),
        v.AiEvidence);
}
