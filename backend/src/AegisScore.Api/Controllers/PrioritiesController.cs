using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-PRIORITIES-01] Central operacional de prioridades — superfície tenant-scoped SOMENTE LEITURA
/// que COMPÕE (sem combinar num único score) a postura NIST atual, a fila de exposições de configuração e a
/// fila de vulnerabilidades ativo×CVE. Tenant sempre IMPLÍCITO (claim <c>tenant_id</c> do JWT + Global Query
/// Filter fail-closed, herdado das queries que compõe) — nunca via URL/QueryString/body.
///
/// Provider-neutral: o contrato não pressupõe Microsoft; cada fila mostra a própria fonte/provider real. O
/// endpoint não altera estado, não aciona coleta e não aciona IA — a análise consultiva é acionada pelo
/// usuário na tela, reutilizando o Auditor Virtual.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/priorities")]
public class PrioritiesController : ControllerBase
{
    private readonly IPriorityWorkspaceQuery _priorities;

    public PrioritiesController(IPriorityWorkspaceQuery priorities) => _priorities = priorities;

    /// <summary>
    /// Read model composto da Central de Prioridades do tenant ambiente: postura atual + fila de exposições de
    /// configuração (resumo + top abertos) + fila de vulnerabilidades (resumo + top abertos). Somente leitura.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PriorityWorkspaceDto>> Get(CancellationToken ct = default)
        => Ok(await _priorities.GetAsync(ct));

    /// <summary>
    /// [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes — SEPARADAS das filas e de qualquer ranking de risco:
    /// resumo por regra × estado (unidade: ativos) e lista agrupada por ativo × regra (unidade: situações), paginada, em
    /// ordem por nome do ativo (não por risco). <paramref name="state"/> padrão: situações identificadas.
    /// </summary>
    [HttpGet("correlations")]
    public async Task<ActionResult<CrossSourceSituationListDto>> Correlations(
        [FromServices] ICrossSourceCorrelationQuery correlations,
        [FromQuery] string? state = null, [FromQuery] string? rule = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        if (state is not null && !CrossSourceStates.IsKnown(state))
            return BadRequest($"Estado desconhecido. Use: {string.Join(", ", CrossSourceStates.All)}.");
        if (rule is not null && CrossSourceRules.Find(rule) is null)
            return BadRequest($"Regra desconhecida. Use: {string.Join(", ", CrossSourceRules.All.Select(r => r.Code))}.");
        return Ok(await correlations.ListAsync(new CrossSourceSituationFilter(state, rule, page, pageSize), ct));
    }

    /// <summary>
    /// [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento de vulnerabilidades em DISPOSITIVOS — fila SEPARADA das
    /// demais (não é ordem universal entre identidades, documentação e dispositivos): um item por dispositivo, apontando
    /// o caso (ativo × CVE) que determinou a posição, com motivo, fatores, ressalvas e próxima ação. Política versionada
    /// e determinística; candidatos selecionados antes da paginação. <paramref name="band"/>: p1…p4, insufficient ou
    /// nulo (todas as faixas).
    /// </summary>
    [HttpGet("devices")]
    public async Task<ActionResult<DevicePriorityListDto>> Devices(
        [FromServices] IDevicePriorityQuery priorities,
        [FromQuery] string? band = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        if (!DevicePriorityBands.IsKnownFilter(band))
            return BadRequest($"Faixa desconhecida. Use: {string.Join(", ", DevicePriorityBands.Prioritized)} ou {DevicePriorityBands.Insufficient}.");
        return Ok(await priorities.ListAsync(new DevicePriorityFilter(band, page, pageSize), ct));
    }
}
