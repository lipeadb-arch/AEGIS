using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-02] Superfície tenant-scoped SOMENTE LEITURA da POSTURA DE CONFIGURAÇÃO E
/// CONFORMIDADE DE DISPOSITIVOS (políticas configuradas + estado efetivo dos dispositivos gerenciados). É
/// CONSULTIVA: mostra o que está configurado e observado no gerenciador de dispositivos, não comprova controle
/// implementado e NÃO altera o AEGIS Score, a conformidade NIST nem os estados determinísticos dos controles.
///
/// Tenant sempre IMPLÍCITO (claim <c>tenant_id</c> do JWT + Global Query Filter fail-closed) — nunca via
/// URL/QueryString. A coleta é do pipeline de ingestão (pull); aqui só se LÊ o snapshot atual (agregados seguros
/// — nunca identificador/nome de dispositivo, usuário, payload de política ou credencial).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/device-posture")]
public class DevicePostureController : ControllerBase
{
    private readonly IDevicePostureQuery _posture;

    public DevicePostureController(IDevicePostureQuery posture) => _posture = posture;

    /// <summary>
    /// Postura de dispositivos atual do tenant: fonte, estado de CADA dimensão (políticas, atribuição e
    /// dispositivos), totais, políticas e grupos agregados. Uma dimensão sem inventário devolve números nulos —
    /// nunca zero.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<DevicePostureViewDto>> Get(CancellationToken ct = default) =>
        Ok(await _posture.GetAsync(ct));
}
