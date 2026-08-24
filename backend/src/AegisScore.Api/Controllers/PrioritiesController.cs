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
}
