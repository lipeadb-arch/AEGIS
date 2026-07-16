using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Advisories;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Scoring — motor CONSULTIVO: Recomendações de Remediação (advisories). Transforma o diagnóstico
/// (TenantControlState) em ação tática: o SOC gera aqui a recomendação técnica mastigada — risco
/// documentado + passo a passo exportável — que a TI do cliente executa para elevar o Secure Score.
///
/// Tenant sempre IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT pelo ITenantContext e carimbado
/// no SaveChanges (fail-closed) — nunca via URL/corpo, de modo que um tenant jamais crie ou leia o
/// advisory de outro. O <c>TenantConsistencyMiddleware</c> ainda barra (403) token sem tenant válido ou
/// divergente do header X-Tenant.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/scoring/advisories")]
public class AdvisoriesController : ControllerBase
{
    private readonly IGenerateAdvisoryHandler _generate;

    public AdvisoriesController(IGenerateAdvisoryHandler generate) => _generate = generate;

    /// <summary>
    /// Gera e persiste um advisory para a subcategoria NIST informada. O texto é redigido pelo motor de
    /// IA (Stub canned em DEV, LLM real com Ai:ApiKey) — o corpo só carrega o código do controle.
    /// </summary>
    /// <response code="201">Advisory criado, com título, risco documentado e passo a passo técnico.</response>
    /// <response code="400">Código de subcategoria ausente/vazio.</response>
    [HttpPost]
    [ProducesResponseType(typeof(RemediationAdvisoryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RemediationAdvisoryDto>> Create(
        [FromBody] CreateAdvisoryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.SubcategoryCode))
            return BadRequest("O código da subcategoria NIST (subcategoryCode) é obrigatório.");

        var dto = await _generate.HandleAsync(new GenerateAdvisoryCommand(request.SubcategoryCode), ct);

        // 201: novo recurso persistido. O Location aponta para o advisory pelo seu id (GET futuro).
        return Created($"api/v1/scoring/advisories/{dto.Id}", dto);
    }
}
