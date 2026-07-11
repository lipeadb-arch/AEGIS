using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Scoring — superfície ÚNICA de leitura da postura de segurança do tenant (modelo Microsoft Secure
/// Score). Consolida o que antes se dividia em dois controllers sobre o mesmo domínio:
/// <list type="bullet">
/// <item><c>current</c> / <c>trend</c> / <c>pending</c> — os KPIs do HUD: Score Atual em tempo real,
/// a série temporal do gráfico de tendência e a contagem de pendências;</item>
/// <item><c>dashboard</c> — a matriz de conformidade controle a controle (estado de cada subcategoria NIST).</item>
/// </list>
/// Tenant sempre IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT pelo ITenantContext e aplicado
/// pelo Global Query Filter (fail-closed) — nunca via URL/QueryString, de modo que um tenant jamais
/// leia a postura de outro.
/// </summary>
[ApiController]
[Authorize]   // o FallbackPolicy já protegeria; explicitar declara a intenção — e é o que faz o
              // ITenantContext resolver o tenant pelo claim 'tenant_id' do JWT, não pelo header spoofável.
[Route("api/v1/scoring")]
public class ScoringController : ControllerBase
{
    private readonly IControlStateDashboardQuery _dashboard;
    private readonly ITenantScoreTrendQuery _trend;
    private readonly IGetPendingControlsQuery _pending;
    private readonly ICurrentScoreQuery _current;

    public ScoringController(
        IControlStateDashboardQuery dashboard,
        ITenantScoreTrendQuery trend,
        IGetPendingControlsQuery pending,
        ICurrentScoreQuery current)
    {
        _dashboard = dashboard;
        _trend = trend;
        _pending = pending;
        _current = current;
    }

    /// <summary>
    /// KPI hero "Score Atual": a postura consolidada do tenant AGORA — SUM(pontos obtidos) /
    /// SUM(pontos possíveis) das subcategorias avaliadas — calculada em tempo real sobre o
    /// TenantControlState, sem esperar a foto diária do worker. É o que faz o HUD refletir avaliações
    /// recém-processadas (ex.: Govern). Tenant IMPLÍCITO via ITenantContext + Global Query Filter.
    /// </summary>
    [HttpGet("current")]
    public async Task<ActionResult<CurrentScoreDto>> GetCurrent(CancellationToken ct = default)
        => Ok(await _current.GetCurrentAsync(ct));

    /// <summary>
    /// Série diária de postura dos últimos <paramref name="days"/> dias (default 30, ex.: 7/30/90),
    /// em ordem cronológica. O tenant é IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT pelo
    /// ITenantContext e aplicado pelo Global Query Filter (fail-closed) — nunca via URL/QueryString,
    /// de modo que um tenant jamais leia a série de outro.
    /// </summary>
    [HttpGet("trend")]
    public async Task<ActionResult<IReadOnlyList<TenantTrendDto>>> GetTrend(
        [FromQuery] int days = 30, CancellationToken ct = default)
    {
        var series = await _trend.GetTrendAsync(days, ct);
        return Ok(series);
    }

    /// <summary>
    /// KPI "Controles Pendentes": nº de controles NIST não-conformes (Status == NonCompliant) do
    /// tenant. Tenant IMPLÍCITO via ITenantContext + Global Query Filter (fail-closed) — nunca por
    /// URL/QueryString.
    /// </summary>
    [HttpGet("pending")]
    public async Task<ActionResult<int>> GetPendingControls(CancellationToken ct = default)
        => Ok(await _pending.CountAsync(ct));

    /// <summary>
    /// Estado atual de todos os controles avaliados do tenant, ordenado pelo código NIST. O tenant é
    /// IMPLÍCITO: resolvido do claim <c>tenant_id</c> pelo ITenantContext e aplicado pelo Global Query
    /// Filter (fail-closed) — nunca via URL/QueryString, de modo que um tenant jamais leia o de outro.
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<ActionResult<IReadOnlyList<TenantControlStateDto>>> GetDashboard(CancellationToken ct = default)
        => Ok(await _dashboard.GetDashboardAsync(ct));
}
