using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
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

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Cobertura de IMPLEMENTAÇÃO do catálogo de referência na versão corrente do catálogo
    /// KNIGHT: controle a controle, com disposição e motivo. Leitura pura do catálogo — não consulta nem coleta nada
    /// do cliente, e é a mesma para todos os tenants.
    /// </summary>
    [HttpGet("reference-coverage")]
    public ActionResult<KnightReferenceCoverageDto> GetReferenceCoverage()
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var c = KnightReferenceCatalog.Coverage();
        static KnightReferenceCoverageGroupDto G(KnightReferenceCoverageGroup g) => new(
            g.Key, g.Label, g.Total, g.Implemented, g.Partial, g.Pending, g.ManualOnly, g.RequiresAccess, g.ApiLimitation,
            g.FullPercent, g.PartialPercent, g.AnyAutomatedPercent);
        return Ok(new KnightReferenceCoverageDto(
            c.CatalogVersion, c.ReferenceCommit,
            c.Frameworks.Select(f => $"{f.Name} {f.Version}").ToList(),
            G(c.Total), c.ByPlatform.Select(G).ToList(), c.ByService.Select(G).ToList(),
            c.Controls.Select(s =>
            {
                var d = KnightServices.Describe(s.Control.Service);
                return new KnightReferenceControlStatusDto(
                    s.Control.Key, s.Control.Framework, s.Control.Version, s.Control.Section, s.Control.Variant,
                    s.Control.Service.ToString(), d?.Label ?? s.Control.Service.ToString(),
                    d is null ? "" : KnightServices.PlatformLabel(d.Platform), s.Control.Severity.ToString(), s.Control.Title,
                    s.Disposition.ToString(), KnightReferenceCatalog.DispositionLabel(s.Disposition), s.IndicatorIds, s.Note);
            }).ToList()));
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

    /// <summary>
    /// Último assessment CONCLUÍDO do tenant — formato público preservado: o corpo é o próprio
    /// <see cref="KnightAssessmentDto"/> (200), ou 204 quando não há resultado concluído; 401 sem tenant.
    /// [AEGIS-KNIGHT-DURABLE-01] Uma execução não finalizada nunca é devolvida aqui como "a última avaliação";
    /// quem precisa saber que ela existe usa <c>GET latest-state</c>.
    /// </summary>
    [HttpGet("latest")]
    public async Task<ActionResult<KnightAssessmentDto>> GetLatest(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var latest = await _service.GetLatestAsync(ct);
        return latest.Assessment is null ? NoContent() : Ok(ToDto(latest.Assessment));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-DURABLE-01] Leitura COMPOSTA: o último resultado CONCLUÍDO e, à parte, a tentativa não
    /// finalizada mais recente que o sucede. Sai da MESMA autoridade de <c>GET latest</c>. Sempre 200, com
    /// os dois campos explícitos (qualquer um pode ser nulo) — a ausência também é resposta.
    /// </summary>
    /// <response code="200">Resultado concluído e/ou tentativa não finalizada (ambos podem ser nulos).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    [HttpGet("latest-state")]
    public async Task<ActionResult<KnightLatestDto>> GetLatestState(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var latest = await _service.GetLatestAsync(ct);
        return Ok(new KnightLatestDto(
            latest.Assessment is null ? null : ToDto(latest.Assessment),
            latest.UnfinishedAttempt is null ? null : ToDto(latest.UnfinishedAttempt)));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] A última avaliação concluída de CADA fonte, com a tentativa não finalizada que
    /// a sucede. É o que a tela do KNIGHT lê: com mais de uma fonte avaliada (Microsoft Entra ID e Microsoft
    /// Teams), apresentar apenas a sincronização mais recente esconderia a avaliação da outra fonte.
    ///
    /// Cada bloco traz a própria nota, a própria cobertura e a própria data — não há nota somada entre fontes.
    /// Somente leitura: abrir a tela NÃO dispara coleta.
    /// </summary>
    /// <response code="200">Lista por fonte (vazia quando o tenant nunca avaliou nada).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    [HttpGet("latest-by-source")]
    public async Task<ActionResult<KnightLatestBySourceDto>> GetLatestBySource(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var latest = await _service.GetLatestBySourceAsync(ct);
        return Ok(new KnightLatestBySourceDto(latest.Sources
            .Select(s => new KnightSourceLatestDto(
                s.Source.ToString(),
                KnightConnectorSources.Slug(s.Source),
                s.Label,
                s.Assessment is null ? null : ToDto(s.Assessment),
                s.UnfinishedAttempt is null ? null : ToDto(s.UnfinishedAttempt)))
            .ToList()));
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

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-02] Objetos que sustentam UM achado de UMA avaliação — paginados e pesquisados NO
    /// SERVIDOR (o navegador nunca recebe a lista inteira para filtrar depois). Somente leitura: abrir o
    /// detalhe NÃO dispara coleta na fonte.
    ///
    /// Autorização e isolamento vêm do mesmo lugar de toda leitura de evidência do KNIGHT: autenticação
    /// obrigatória no controller e tenant IMPLÍCITO (claim + Global Query Filter fail-closed). Uma avaliação
    /// de outro tenant é indistinguível de inexistente — 404, nunca uma pista de que existe.
    /// </summary>
    /// <response code="200">Página de afetados (pode ser vazia com detalhe ausente — ver state).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="404">Avaliação ou achado inexistentes neste tenant.</response>
    [HttpGet("{runId:guid}/indicators/{indicatorId}/affected")]
    public async Task<ActionResult<KnightAffectedObjectsDto>> GetAffected(
        Guid runId, string indicatorId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = KnightAffectedObjectsPage.DefaultPageSize,
        [FromQuery] string? search = null,
        [FromQuery] string? relation = null,
        CancellationToken ct = default)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        // [AEGIS-KNIGHT-MULTICLOUD-01] Padrão = afetados (contrato anterior preservado); "evidence" lista a
        // configuração que sustentou o veredito.
        var rel = (relation ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "affected" => (KnightObjectRelation?)KnightObjectRelation.Affected,
            "evidence" => KnightObjectRelation.Evidence,
            _ => null,
        };
        if (rel is null) return BadRequest($"Relação desconhecida: '{relation}'. Use 'affected' ou 'evidence'.");

        var result = await _service.GetAffectedObjectsAsync(runId, indicatorId, page, pageSize, search, ct, rel.Value);
        if (result is null) return NotFound();

        return Ok(new KnightAffectedObjectsDto(
            result.RunId,
            result.IndicatorId,
            result.State.ToString(),
            result.AffectedObjectCount,
            result.TotalPreserved,
            result.MatchCount,
            result.Page,
            result.PageSize,
            result.Items.Select(o => new KnightAffectedObjectDto(
                o.ExternalId, o.Kind.ToString(), o.DisplayName, o.UserPrincipalName, o.Roles, o.Detail,
                o.Relation.ToString(), o.ObservedConfiguration)).ToList(),
            result.Limitation,
            result.CollectedAt));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] Resumo dos objetos afetados de UMA avaliação: ocorrências (objeto × controle),
    /// objetos únicos e os que mais se repetem entre controles expostos. Somente leitura; 404 fora do tenant.
    /// </summary>
    [HttpGet("{runId:guid}/affected-summary")]
    public async Task<ActionResult<KnightAffectedSummaryDto>> GetAffectedSummary(Guid runId, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var s = await _service.GetAffectedSummaryAsync(runId, ct);
        if (s is null) return NotFound();
        return Ok(new KnightAffectedSummaryDto(
            s.RunId, s.ExposedControls, s.Occurrences, s.UniqueObjects, s.Complete, s.IncompleteIndicatorIds,
            s.Top.Select(t => new KnightAffectedSummaryItemDto(
                t.ExternalId, t.Kind.ToString(), t.DisplayName, t.UserPrincipalName, t.ControlCount, t.IndicatorIds)).ToList()));
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
            // [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams: fonte própria, credencial do mesmo conector Microsoft.
            case "teams":
            case "microsoftteams": sourceType = KnightSourceType.MicrosoftTeams; return true;
            default: sourceType = default; return false;
        }
    }

    private static KnightUnfinishedRunDto ToDto(KnightUnfinishedRun r) => new(
        r.Id, r.Status.ToString(), r.SourceType.ToString(), r.Mode.ToString(), r.StartedAt);

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
        i.NotEvaluatedReason,
        i.HasAffectedDetail,
        i.AffectedDetailComplete,
        i.AffectedDetailLimitation,
        i.EvidenceObjectCount,
        i.Presentation is null ? null : new KnightControlPresentationDto(
            i.Presentation.Domain, i.Presentation.DomainLabel, i.Presentation.Service, i.Presentation.Provider,
            i.Presentation.Description, i.Presentation.Rationale, i.Presentation.ExpectedConfiguration,
            i.Presentation.DoesNotProve, i.Presentation.Criterion,
            i.Presentation.References.Select(r => new KnightControlReferenceDto(r.Framework, r.Version, r.Code, r.Url)).ToList(),
            i.Presentation.RequiredCapabilities, i.Presentation.Weight, i.Presentation.Factor,
            i.Presentation.AchievedPoints, i.Presentation.PossiblePoints,
            i.Presentation.Impact, i.Presentation.Platform, i.Presentation.ServiceKey),
        i.AffectedComposition);

    private static KnightAdvisoryDto ToDto(KnightAdvisory ad) => new(
        ad.ExecutiveSummary,
        ad.PriorityRisks.Select(r => new KnightPriorityRiskDto(r.Title, r.Rationale, r.IndicatorIds)).ToList(),
        ad.RecommendedActions.Select(r => new KnightRecommendedActionDto(r.Order, r.Action, r.IndicatorIds)).ToList(),
        ad.Correlations.Select(c => new KnightCorrelationDto(c.Description, c.IndicatorIds)).ToList(),
        ad.CollectionGaps);
}
