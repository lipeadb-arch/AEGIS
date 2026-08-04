using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Api.Controllers;

/// <summary>
/// AEGIS KNIGHT — assessment MULTICOLETOR de postura de identidade e exposição. Superfície DEDICADA, distinta
/// da telemetria de identidade legada e do AEGIS Score geral. Fontes: Demo (sintético), Microsoft Entra ID
/// (coleta real somente-leitura) e Google Workspace (capacidade arquitetural — coletor real na próxima
/// entrega). Tenant IMPLÍCITO: resolvido do claim <c>tenant_id</c> do JWT e aplicado pelo Global Query Filter
/// (fail-closed) — um tenant jamais lê o assessment de outro. A execução real só ocorre com configuração
/// aplicável; uma falha real NUNCA cai para Demo.
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

    /// <summary>Executa um assessment de DEMONSTRAÇÃO (fonte Demo, sintética) e devolve o resultado completo.</summary>
    [HttpPost("demo")]
    public async Task<ActionResult<KnightAssessmentDto>> RunDemo(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        return Ok(ToDto(await _service.RunDemoAssessmentAsync(ct)));
    }

    /// <summary>
    /// Executa um assessment da FONTE indicada (ex.: <c>entra</c>). Para fontes reais exige configuração
    /// aplicável: sem ela retorna 409 (e NUNCA cai para Demo). Fonte desconhecida → 400.
    /// </summary>
    /// <response code="200">Assessment executado (pode ter coleta parcial — ver sourceState).</response>
    /// <response code="400">Fonte desconhecida.</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="409">Fonte real sem configuração aplicável neste tenant.</response>
    [HttpPost("run/{source}")]
    public async Task<ActionResult<KnightAssessmentDto>> Run(string source, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (!TryParseSource(source, out var sourceType))
            return BadRequest($"Fonte desconhecida: '{source}'.");

        try
        {
            return Ok(ToDto(await _service.RunAssessmentAsync(sourceType, ct)));
        }
        catch (KnightSourceNotConfiguredException)
        {
            return Conflict($"A fonte {sourceType} não está configurada para este tenant.");
        }
    }

    /// <summary>Disponibilidade das fontes para o tenant (Demo sempre; reais conforme configuração).</summary>
    [HttpGet("sources")]
    public async Task<ActionResult<KnightSourcesDto>> GetSources(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var status = await _service.GetSourcesStatusAsync(ct);
        return Ok(new KnightSourcesDto(
            status.DemoAvailable,
            status.RealSources.Select(s => new KnightSourceDto(s.Source.ToString(), s.Label, s.Configured, s.Enabled)).ToList()));
    }

    /// <summary>Último assessment do tenant (200 com corpo, 204 sem nenhum, 401 sem tenant).</summary>
    [HttpGet("latest")]
    public async Task<ActionResult<KnightAssessmentDto>> GetLatest(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var assessment = await _service.GetLatestAsync(ct);
        return assessment is null ? NoContent() : Ok(ToDto(assessment));
    }

    /// <summary>Assessment por Id (401 sem tenant; 404 inexistente/de outro tenant com tenant válido).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KnightAssessmentDto>> GetById(Guid id, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var assessment = await _service.GetByIdAsync(id, ct);
        return assessment is null ? NotFound() : Ok(ToDto(assessment));
    }

    // ---- Mapeamento ----------------------------------------------------------------------------------

    private static bool TryParseSource(string source, out KnightSourceType sourceType)
    {
        switch ((source ?? "").Trim().ToLowerInvariant())
        {
            case "demo": sourceType = KnightSourceType.Demo; return true;
            case "entra":
            case "entraid":
            case "microsoftentraid": sourceType = KnightSourceType.MicrosoftEntraId; return true;
            case "google":
            case "googleworkspace": sourceType = KnightSourceType.GoogleWorkspace; return true;
            default: sourceType = default; return false;
        }
    }

    private static KnightAssessmentDto ToDto(KnightAssessment a) => new(
        a.Id,
        a.Mode.ToString(),
        a.SourceType == KnightSourceType.Demo,
        a.SourceType.ToString(),
        a.SourceState.ToString(),
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
        a.Capabilities.Select(c => new KnightCapabilityDto(c.Capability.ToString(), c.Outcome.ToString(), c.Detail)).ToList(),
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
        i.CollectedAt,
        i.SourceType.ToString(),
        i.NotEvaluatedReason);

    private static KnightAdvisoryDto ToDto(KnightAdvisory ad) => new(
        ad.ExecutiveSummary,
        ad.PriorityRisks.Select(r => new KnightPriorityRiskDto(r.Title, r.Rationale, r.IndicatorIds)).ToList(),
        ad.RecommendedActions.Select(r => new KnightRecommendedActionDto(r.Order, r.Action, r.IndicatorIds)).ToList(),
        ad.Correlations.Select(c => new KnightCorrelationDto(c.Description, c.IndicatorIds)).ToList(),
        ad.CollectionGaps);
}
