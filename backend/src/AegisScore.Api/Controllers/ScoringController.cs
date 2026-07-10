using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Scoring — leitura da matriz de conformidade do tenant (estado de cada controle NIST). Alimenta o HUD
/// de scoring no Angular, que hoje exibe o baseline documental (50%) e passará a refletir a telemetria
/// assim que o motor de ingestão for ligado.
/// </summary>
[ApiController]
[Authorize]   // o FallbackPolicy já protegeria; explicitar declara a intenção — e é o que faz o
              // ITenantContext resolver o tenant pelo claim 'tenant_id' do JWT, não pelo header spoofável.
[Route("api/v1/scoring")]
public class ScoringController : ControllerBase
{
    private readonly IControlStateDashboardQuery _dashboard;

    public ScoringController(IControlStateDashboardQuery dashboard) => _dashboard = dashboard;

    /// <summary>
    /// Estado atual de todos os controles avaliados do tenant, ordenado pelo código NIST. O tenant é
    /// IMPLÍCITO: resolvido do claim <c>tenant_id</c> pelo ITenantContext e aplicado pelo Global Query
    /// Filter (fail-closed) — nunca via URL/QueryString, de modo que um tenant jamais leia o de outro.
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<IReadOnlyList<TenantControlStateDto>>> GetDashboard(CancellationToken ct = default)
        => Ok(await _dashboard.GetDashboardAsync(ct));
}
