using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using AegisScore.Api.Contracts;
using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Api.Controllers;

/// <summary>
/// IDENTIFY (ID.RA) — Raio de Explosão. Dado um ativo-epicentro, dispara o cálculo do impacto de
/// comprometê-lo (traversal reverso do grafo de dependências) e PERSISTE o snapshot. Tenant IMPLÍCITO:
/// o ativo e o grafo são resolvidos no escopo do tenant ambiente (Global Query Filter fail-closed) —
/// nunca vem do corpo. O motor de cálculo é puro; este controller só expõe o orquestrador.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/risk-assessment")]
public class BlastRadiusController : ControllerBase
{
    private readonly IBlastRadiusAssessmentService _service;

    public BlastRadiusController(IBlastRadiusAssessmentService service) => _service = service;

    /// <summary>
    /// Calcula e persiste o raio de explosão de <paramref name="assetId"/>, opcionalmente sob um cenário de
    /// ameaça (<c>ScenarioThreatId</c> no corpo — ausente = raio topológico puro).
    /// </summary>
    /// <response code="201">Snapshot do raio criado, com score, nível de risco e nós impactados.</response>
    /// <response code="404">Ativo-epicentro não encontrado no tenant.</response>
    [HttpPost("{assetId:guid}/blast-radius")]
    public async Task<ActionResult<BlastRadiusResponseDto>> Assess(
        Guid assetId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] BlastRadiusRequestDto? request,
        CancellationToken ct)
    {
        BlastRadiusAssessment assessment;
        try
        {
            assessment = await _service.AssessAsync(assetId, request?.ScenarioThreatId, ct);
        }
        catch (InvalidOperationException)
        {
            // O orquestrador lança quando o ativo-epicentro não existe no tenant — vira 404, não um 500 opaco.
            return NotFound($"Ativo '{assetId}' não encontrado no tenant.");
        }

        // 201: um novo snapshot foi persistido. O Location aponta para o recurso pelo seu id (GET futuro).
        return Created($"api/v1/risk-assessment/blast-radius/{assessment.Id}", ToDto(assessment));
    }

    private static BlastRadiusResponseDto ToDto(BlastRadiusAssessment a) => new(
        a.Id,
        a.RootAssetId,
        a.BlastRadiusScore,
        a.RiskLevel.ToString(),
        a.ImpactedAssetCount,
        a.ImpactedProcessCount,
        a.MaxDepth,
        a.ComputedAt,
        a.ImpactedNodes
            .OrderByDescending(n => n.PropagatedImpact)
            .Select(n => new BlastRadiusNodeDto(
                n.ImpactedAssetId, n.Distance, n.PropagatedImpact, n.PathStrength.ToString()))
            .ToList());
}
