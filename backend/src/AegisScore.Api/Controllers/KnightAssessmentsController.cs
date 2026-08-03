using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Api.Controllers;

/// <summary>
/// AEGIS KNIGHT — assessment de postura de identidade e exposição. Superfície DEDICADA (rota
/// <c>/api/v1/knight/assessments</c>), distinta da telemetria de identidade legada e do AEGIS Score geral.
///
/// Nesta primeira vertical, a execução é SOMENTE DEMONSTRAÇÃO (dados 100% sintéticos, example.com): o Aegis
/// NÃO está conectado ao Entra ID / AD / Okta. Os vereditos são determinísticos; a IA é apenas consultiva e
/// sua indisponibilidade não reprova nem invalida o assessment. Tenant IMPLÍCITO: resolvido do claim
/// <c>tenant_id</c> do JWT pelo <see cref="ITenantContext"/> e aplicado pelo Global Query Filter (fail-closed)
/// — nunca via URL/corpo, de modo que um tenant jamais leia o assessment de outro.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/knight/assessments")]
public class KnightAssessmentsController : ControllerBase
{
    private readonly IAegisKnightAssessmentService _service;
    private readonly ITenantContext _tenant;

    public KnightAssessmentsController(IAegisKnightAssessmentService service, ITenantContext tenant)
    {
        _service = service;
        _tenant = tenant;
    }

    /// <summary>
    /// Executa um assessment de DEMONSTRAÇÃO ponta a ponta e devolve o resultado completo. Coleta o snapshot
    /// sintético, avalia deterministicamente, calcula o score/cobertura KNIGHT, persiste, tenta gerar a
    /// narrativa consultiva e conclui MESMO SE a IA estiver indisponível.
    /// </summary>
    /// <response code="200">Assessment demo executado e persistido.</response>
    /// <response code="401">Tenant não resolvido no contexto (claim tenant_id ausente).</response>
    [HttpPost("demo")]
    public async Task<ActionResult<KnightAssessmentDto>> RunDemo(CancellationToken ct)
    {
        // Fail-closed: sem tenant resolvido do JWT, nada é executado nem persistido.
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var assessment = await _service.RunDemoAssessmentAsync(ct);
        return Ok(ToDto(assessment));
    }

    /// <summary>
    /// Último assessment do tenant. Contrato explícito: tenant ausente → 401; tenant válido sem nenhum
    /// assessment → 204; caso contrário 200 com o corpo.
    /// </summary>
    [HttpGet("latest")]
    public async Task<ActionResult<KnightAssessmentDto>> GetLatest(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var assessment = await _service.GetLatestAsync(ct);
        return assessment is null ? NoContent() : Ok(ToDto(assessment));
    }

    /// <summary>
    /// Assessment por Id. Contrato explícito: tenant ausente → 401; inexistente ou de outro tenant (com
    /// tenant válido) → 404; caso contrário 200.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnightAssessmentDto>> GetById(Guid id, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var assessment = await _service.GetByIdAsync(id, ct);
        return assessment is null ? NotFound() : Ok(ToDto(assessment));
    }

    // ---- Mapeamento read model → DTO (enums como nome) --------------------------------------------

    private static KnightAssessmentDto ToDto(KnightAssessment a) => new(
        a.Id,
        a.Mode.ToString(),
        a.Mode == KnightAssessmentMode.Demo,
        a.Source,
        a.Status.ToString(),
        a.CatalogVersion,
        a.ScoreFormulaVersion,
        a.StartedAt,
        a.CompletedAt,
        a.Score,
        a.Coverage,
        new KnightCountsDto(
            a.PassedCount, a.ExposedCount, a.MitigatedCount,
            a.NotEvaluatedCount, a.ErrorCount, a.NotApplicableCount),
        a.Indicators.Select(ToDto).ToList(),
        a.Advisory is null ? null : ToDto(a.Advisory),
        a.AdvisoryFromAi);

    private static KnightIndicatorDto ToDto(KnightIndicatorView i) => new(
        i.IndicatorId,
        i.Title,
        i.Category.ToString(),
        i.Severity.ToString(),
        i.Status.ToString(),
        i.Evidence,
        i.AffectedObjectCount,
        i.NistCodes,
        i.MitreTechniques,
        i.Recommendation,
        i.CollectedAt);

    private static KnightAdvisoryDto ToDto(KnightAdvisory ad) => new(
        ad.ExecutiveSummary,
        ad.PriorityRisks.Select(r => new KnightPriorityRiskDto(r.Title, r.Rationale, r.IndicatorIds)).ToList(),
        ad.RecommendedActions.Select(r => new KnightRecommendedActionDto(r.Order, r.Action, r.IndicatorIds)).ToList(),
        ad.Correlations.Select(c => new KnightCorrelationDto(c.Description, c.IndicatorIds)).ToList(),
        ad.CollectionGaps);
}
