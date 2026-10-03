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
    private readonly IKnightManualResultService? _manual;

    public KnightAssessmentsController(IAegisKnightAssessmentService service, ITenantContext tenant, IKnightManualResultService? manual = null)
    {
        _service = service;
        _tenant = tenant;
        _manual = manual;
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
    public async Task<ActionResult<KnightReferenceCoverageDto>> GetReferenceCoverage(CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var c = KnightReferenceCatalog.Coverage();
        // [AEGIS-KNIGHT-CLOSURE-01] Resultado manual vigente do tenant, por referência — à parte da disposição automatizada.
        var manual = (_manual is null ? Array.Empty<KnightManualResultView>() : await _manual.CurrentAsync(ct))
            .ToDictionary(m => m.ReferenceKey, StringComparer.Ordinal);
        static KnightReferenceCoverageGroupDto G(KnightReferenceCoverageGroup g) => new(
            g.Key, g.Label, g.Total, g.Implemented, g.Partial, g.Pending, g.ManualOnly, g.RequiresAccess, g.ApiLimitation,
            g.FullPercent, g.PartialPercent, g.AnyAutomatedPercent, g.PreviewOnly, g.PreviewBacked);
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
                    s.Disposition.ToString(), KnightReferenceCatalog.DispositionLabel(s.Disposition), s.IndicatorIds, s.Note,
                    s.PreviewApis ?? Array.Empty<string>(), KnightManualResults.Eligible(s.Disposition),
                    manual.TryGetValue(s.Control.Key, out var m) ? ManualDto(m) : null);
            }).ToList()));
    }

    /// <summary>[AEGIS-KNIGHT-CLOSURE-01] Histórico dos resultados manuais de UM controle de referência.</summary>
    [HttpGet("manual-results/{referenceKey}")]
    public async Task<ActionResult<IReadOnlyList<KnightManualResultDto>>> GetManualHistory(string referenceKey, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (_manual is null) return Ok(Array.Empty<KnightManualResultDto>());
        return Ok((await _manual.HistoryAsync(referenceKey, ct)).Select(ManualDto).ToList());
    }

    /// <summary>
    /// [AEGIS-KNIGHT-CLOSURE-01] Registra o resultado de uma verificação MANUAL (atestação) para um controle de referência sem
    /// avaliação automatizada. O autor vem do token. Não altera nota, cobertura automatizada nem aprovação.
    /// </summary>
    /// <response code="201">Resultado registrado.</response>
    /// <response code="400">Pedido inválido (controle com avaliação automatizada, sem evidência, sem justificativa…).</response>
    /// <response code="403">Papel insuficiente (Analyst não registra resultado manual).</response>
    [HttpPost("manual-results")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<KnightManualResultDto>> RecordManual([FromBody] RecordKnightManualResultRequest request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (request is null) return BadRequest("Corpo da requisição ausente.");
        if (_manual is null) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        Guid? accountId = Guid.TryParse(User.FindFirst(AegisScore.Infrastructure.Auth.JwtTokenService.AccountClaim)?.Value, out var id) && id != Guid.Empty
            ? id : null;
        try
        {
            var saved = await _manual.RecordAsync(new RecordKnightManualResultCommand(request.ReferenceKey, request.Result, request.Justification,
                request.ResponsibleName, request.EvidenceReference, request.EvidenceDocumentId, request.ValidUntil),
                new AegisScore.Application.Remediation.RemediationActor(accountId, User.FindFirst("name")?.Value ?? ""), ct);
            return StatusCode(StatusCodes.Status201Created, ManualDto(saved));
        }
        catch (KnightManualResultValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>[AEGIS-KNIGHT-CLOSURE-01] Capacidade com o requisito e a versão preview, da mesma autoridade das exportações.</summary>
    private static KnightCapabilityDto CapabilityDto(KnightCapabilityStatus c) => new(
        c.Capability.ToString(), c.Outcome.ToString(), c.Detail,
        AegisScore.Application.Posture.Export.KnightCapabilityLabels.RequiredPermission(c.Capability, KnightSourceType.Consolidated),
        KnightCollectorCapabilities.PreviewApi(c.Capability));

    private static KnightManualResultDto ManualDto(KnightManualResultView m) => new(
        m.Id, m.ReferenceKey, m.Result.ToString(), m.ResultLabel, m.Justification, m.ResponsibleName, m.EvidenceReference,
        m.EvidenceDocumentId, m.EvidenceDocumentTitle, m.EvidenceDocumentSha256, m.ValidUntil, m.Expired, m.ReferenceDisposition,
        m.CatalogVersion, m.RecordedByName, m.RecordedAt);

    /// <summary>
    /// [AEGIS-KNIGHT-PRESENTATION-01] Glossário ÚNICO dos termos técnicos — a mesma lista que entra nas exportações.
    /// Leitura pura de código: não consulta nem coleta nada do cliente.
    /// </summary>
    [HttpGet("glossary")]
    public ActionResult<KnightGlossaryDto> GetGlossary()
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        return Ok(new KnightGlossaryDto(
            KnightGlossary.Terms.Select(t => new KnightGlossaryTermDto(t.Term, t.Meaning, t.Explanation)).ToList(),
            KnightGlossary.IdentifierExplanation));
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

    /// <summary>
    /// [AEGIS-KNIGHT-CONSOLIDATED-01] Leitura AO VIVO do relatório consolidado (Entra ID + Teams + Exchange
    /// Online): combina a última avaliação CONCLUÍDA de cada fonte pedida em <paramref name="sources"/> pela
    /// MESMA fórmula knight-score-v1 sobre a união dos indicadores — nunca a média das notas por fonte. Sem o
    /// parâmetro <paramref name="explicitSelection"/>, ausência de <paramref name="sources"/> é o padrão CLARO
    /// (todas as candidatas com avaliação concluída) — string de consulta não distingue "ausente" de "vazio",
    /// então <paramref name="explicitSelection"/><c>=true</c> é como o chamador afirma "esta é a seleção exata,
    /// mesmo vazia" (ex.: a pessoa desmarcou todas as fontes de propósito) sem reverter ao padrão em silêncio.
    /// NUNCA persiste nem dispara coleta: publicar um relatório congelado exportável é
    /// <c>POST /api/v1/posture/snapshots/consolidated</c>.
    /// </summary>
    /// <response code="200">Composição consolidada (mesmo sem nenhuma fonte disponível — a ausência é o conteúdo).</response>
    /// <response code="400">Alguma fonte pedida não é reconhecida.</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    [HttpGet("consolidated")]
    public async Task<ActionResult<KnightAssessmentDto>> GetConsolidated(
        [FromQuery] string[]? sources, [FromQuery(Name = "explicit")] bool explicitSelection, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        var parsed = new List<KnightSourceType>();
        foreach (var name in sources ?? Array.Empty<string>())
        {
            if (!KnightSourceNames.TryParse(name, out var s))
                return BadRequest($"Fonte desconhecida: '{name}'.");
            parsed.Add(s);
        }

        IReadOnlyCollection<KnightSourceType>? requested = explicitSelection ? parsed : (parsed.Count > 0 ? parsed : null);

        var latest = await _service.GetLatestBySourceAsync(ct);
        var combined = KnightConsolidatedBuilder.Build(latest, requested);
        return Ok(ToConsolidatedDto(combined));
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
        return Ok(SummaryDto(s));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Resumo dos objetos afetados de uma COMPOSIÇÃO (relatório consolidado): as execuções
    /// reais das fontes incluídas (<c>?runs=</c>, repetido), com um objeto presente em duas fontes contado uma vez.
    /// Somente leitura; execução de outro tenant não existe aqui.
    /// </summary>
    [HttpGet("affected-summary")]
    public async Task<ActionResult<KnightAffectedSummaryDto>> GetCompositionAffectedSummary([FromQuery] Guid[] runs, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (runs is null || runs.Length == 0) return BadRequest("Informe ao menos uma execução (runs).");
        if (runs.Length > 20) return BadRequest("No máximo 20 execuções por composição.");
        var s = await _service.GetAffectedSummaryAsync(runs, ct);
        if (s is null) return NotFound();
        return Ok(SummaryDto(s));
    }

    private static KnightAffectedSummaryDto SummaryDto(KnightAffectedSummary s) => new(
        s.RunId, s.ExposedControls, s.Occurrences, s.UniqueObjects, s.Complete, s.IncompleteIndicatorIds,
        s.Top.Select(t => new KnightAffectedSummaryItemDto(
            t.ExternalId, t.Kind.ToString(), t.DisplayName, t.UserPrincipalName, t.ControlCount, t.IndicatorIds,
            KnightObjectNouns.Label(t.Kind, t.ExternalId))).ToList());

    // ---- Mapeamento ----------------------------------------------------------------------------------

    private static bool TryParseSource(string source, out KnightSourceType sourceType) =>
        KnightSourceNames.TryParse(source, out sourceType);

    /// <summary>
    /// [AEGIS-KNIGHT-CONSOLIDATED-01] Reaproveita o mesmo CONTRATO <see cref="KnightAssessmentDto"/> das
    /// avaliações de fonte única — o cliente já sabe renderizá-lo — só que sintético (Id vazio, nunca uma
    /// execução persistida) e com <see cref="KnightAssessmentDto.Sources"/> preenchido. É o que permite as MESMAS
    /// telas (Visão geral, Controles e findings) funcionarem para o relatório consolidado sem duplicar UI.
    /// </summary>
    private static KnightAssessmentDto ToConsolidatedDto(KnightConsolidatedAssessment a)
    {
        var sources = a.Sources.Select(ToDto).ToList();
        var includedLabels = sources.Where(s => s.Included).Select(s => s.Label).ToList();
        var sourceLabel = KnightConsolidatedLabel.For(includedLabels);
        var counts = new KnightCountsDto(a.PassedCount, a.ExposedCount, a.MitigatedCount, a.NotEvaluatedCount, a.ErrorCount, a.NotApplicableCount);
        var at = a.DataRecency ?? DateTimeOffset.UtcNow;

        return new KnightAssessmentDto(
            Guid.Empty, KnightAssessmentMode.Live.ToString(), false, KnightSourceType.Consolidated.ToString(),
            // A execução (composição) terminou; a COLETA só é completa se todas as fontes incluídas forem íntegras.
            KnightConsolidatedCollection.StateOf(a.Sources).ToString(), sourceLabel, KnightRunStatus.Completed.ToString(),
            "ak-knight-consolidated", a.FormulaVersion, at, a.DataRecency,
            a.Score, a.Coverage, counts, a.Indicators.Select(ToDto).ToList(),
            a.Capabilities.Select(c => CapabilityDto(c)).ToList(),
            null, false, sources);
    }

    private static KnightConsolidatedSourceDto ToDto(KnightConsolidatedSourceEntry e) => new(
        e.Source.ToString(), KnightConnectorSources.Slug(e.Source), e.Label, e.Included, e.AvailabilityState,
        e.SourceRunId, e.SourceState, e.CatalogVersion, e.CapturedAt, e.Score, e.Coverage,
        e.PassedCount is null ? null : new KnightCountsDto(
            e.PassedCount.Value, e.ExposedCount!.Value, e.MitigatedCount!.Value,
            e.NotEvaluatedCount!.Value, e.ErrorCount!.Value, e.NotApplicableCount!.Value),
        e.CollectionLimitations);

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
        a.Capabilities.Select(c => CapabilityDto(c)).ToList(),
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
            i.Presentation.Impact, i.Presentation.Platform, i.Presentation.ServiceKey, i.Presentation.PreviewApis),
        i.AffectedComposition);

    private static KnightAdvisoryDto ToDto(KnightAdvisory ad) => new(
        ad.ExecutiveSummary,
        ad.PriorityRisks.Select(r => new KnightPriorityRiskDto(r.Title, r.Rationale, r.IndicatorIds)).ToList(),
        ad.RecommendedActions.Select(r => new KnightRecommendedActionDto(r.Order, r.Action, r.IndicatorIds)).ToList(),
        ad.Correlations.Select(c => new KnightCorrelationDto(c.Description, c.IndicatorIds)).ToList(),
        ad.CollectionGaps);
}
