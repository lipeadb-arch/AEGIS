using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Aegis Score — leitura da postura de segurança do tenant. A série temporal alimenta o gráfico de
/// tendência (modelo Microsoft Secure Score) no Angular.
/// </summary>
[ApiController]
[Authorize]   // autenticação corporativa (JWT). O FallbackPolicy já protegeria; explicitar declara a intenção.
[Route("api/v1/aegis-score")]
public class AegisScoreController : ControllerBase
{
    private readonly ITenantScoreTrendQuery _trend;
    private readonly IGetPendingControlsQuery _pending;
    private readonly ICurrentScoreQuery _current;

    public AegisScoreController(
        ITenantScoreTrendQuery trend, IGetPendingControlsQuery pending, ICurrentScoreQuery current)
    {
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
}
