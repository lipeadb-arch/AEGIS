using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Postura de EVIDÊNCIA de identidade (Microsoft Entra ID). Superfície de
/// LEITURA/COLETA da Evidence Fabric compartilhada: NÃO existe mais uma segunda integração Entra com números
/// simulados nem um mapeamento hardcoded para PR.AA-01/GV.RR-01. Tanto esta rota quanto o botão de coleta do
/// AEGIS KNIGHT convergem para o MESMO caso de uso (<see cref="IIdentityEvidenceService"/>) — uma aquisição
/// real por operação lógica, sem um segundo cliente Graph, credencial ou consulta duplicada.
///
/// A resposta é CONSULTIVA: separa explicitamente o estado do conector, o da coleta e a evidência POR
/// controle NIST — e reconhece que a telemetria coletada, embora presente, é INSUFICIENTE para avaliar o
/// requisito dos controles de identidade (PR.AA-01, PR.AA-03, GV.RR-01). NUNCA grava veredito no ledger,
/// NUNCA concede pontos ao AEGIS Score. Tenant IMPLÍCITO do claim <c>tenant_id</c> do JWT (Zero Trust).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/telemetry/identity")]
public class IdentityTelemetryController : ControllerBase
{
    private readonly IIdentityEvidenceService _evidence;
    private readonly ITenantContext _tenant;

    public IdentityTelemetryController(IIdentityEvidenceService evidence, ITenantContext tenant)
    {
        _evidence = evidence;
        _tenant = tenant;
    }

    /// <summary>
    /// Lê a postura de evidência de identidade do ÚLTIMO snapshot compartilhado (sem nova aquisição): estado do
    /// conector, estado da coleta (completa/parcial/nunca), degradação, fonte/freshness reais e a evidência por
    /// controle NIST. Não consulta o Graph e não altera score.
    /// </summary>
    /// <response code="200">Projeção consultiva da evidência de identidade (sem score).</response>
    /// <response code="401">Tenant não resolvido no contexto (claim tenant_id ausente).</response>
    [HttpGet("entra-id")]
    public async Task<ActionResult<IdentityEvidenceProjectionDto>> GetEntraId(CancellationToken ct)
    {
        if (_tenant.TenantId is null)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var projection = await _evidence.GetLatestProjectionAsync(ct);
        return Ok(ToDto(projection));
    }

    /// <summary>
    /// Dispara UMA aquisição real da postura de identidade do Entra ID pela Evidence Fabric compartilhada,
    /// persiste o snapshot normalizado (tenant-safe, com proveniência/completude) e devolve a projeção
    /// consultiva resultante. Uma coleta que falhe NÃO apaga a última evidência válida — sinaliza a degradação.
    /// Recusa conector desabilitado/sem credencial devolvendo o estado do conector. NÃO grava no ledger.
    /// </summary>
    /// <response code="200">Projeção consultiva após a aquisição (sem score).</response>
    /// <response code="401">Tenant não resolvido no contexto (claim tenant_id ausente).</response>
    [HttpPost("entra-id")]
    public async Task<ActionResult<IdentityEvidenceProjectionDto>> CollectEntraId(CancellationToken ct)
    {
        if (_tenant.TenantId is null)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var acquisition = await _evidence.CollectAsync(ct);
        var projection = IdentityEvidenceProjection.Build(acquisition.ConnectorState, acquisition.Snapshot);
        return Ok(ToDto(projection));
    }

    private static IdentityEvidenceProjectionDto ToDto(IdentityEvidenceProjection p) => new(
        p.ConnectorState.ToString(),
        p.CollectionState.ToString(),
        p.LastAttemptState.ToString(),
        p.IsDegraded,
        p.Source,
        p.SchemaVersion,
        p.CollectedAt,
        p.LastAttemptAt,
        p.LastAttemptDetail,
        p.Capabilities.Select(c => new IdentityCapabilityDto(c.Capability.ToString(), c.Outcome.ToString(), c.Detail)).ToList(),
        p.Controls.Select(c => new IdentityControlEvidenceDto(c.Code, c.Title, c.State.ToString(), c.Explanation)).ToList());
}
