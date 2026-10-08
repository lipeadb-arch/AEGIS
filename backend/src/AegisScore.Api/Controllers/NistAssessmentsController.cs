using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Application.Remediation;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] AEGIS NIST — avaliação organizacional pelo NIST CSF 2.0, persistida: avaliação → escopo → seis
/// funções → subcategoria → evidência e justificativa → situação atual × alvo → lacunas e resumo.
///
/// [AEGIS-NIST-JOURNEY-02] Jornada completa: RODADAS (toda rota de trabalho nomeia avaliação · rodada · escopo),
/// procedimentos de avaliação, achados com plano de tratamento (origem NIST), responsáveis, revisão, trilha, publicação da
/// fotografia de maturidade e importação/exportação CSV de trabalho.
///
/// [AEGIS-NIST-AI-ASSIST-01] Assistência contextual de IA (subcategoria, achado e tratamento, resumo executivo): gera SUGESTÕES
/// identificadas sobre o contexto montado no servidor; incorporar passa pelas rotas normais de gravação, com a referência da
/// sugestão. Com a IA desativada, não configurada ou indisponível, a jornada manual segue (503 com o motivo).
///
/// Tenant IMPLÍCITO (claim <c>tenant_id</c> + filtro global fail-closed): qualquer objeto de outro tenant responde 404.
/// Leitura para qualquer papel; gravação só para Manager/TenantAdmin — designar alguém como responsável, avaliador ou
/// revisor NÃO concede privilégio. O autor vem sempre do token. A maturidade gravada aqui NÃO é o score de postura.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/nist/assessments")]
public sealed class NistAssessmentsController : ControllerBase
{
    private const string Writers = "Manager,TenantAdmin";
    private const string CyclePath = "{assessmentId:guid}/cycles/{cycleId:guid}/scopes/{scopeId:guid}";

    private readonly INistAssessmentService _service;
    private readonly INistWorkService _work;
    private readonly INistPublicationService _publication;
    private readonly INistImportService _import;
    private readonly INistAssistService _assist;

    public NistAssessmentsController(
        INistAssessmentService service, INistWorkService work, INistPublicationService publication, INistImportService import, INistAssistService assist)
    {
        _service = service;
        _work = work;
        _publication = publication;
        _import = import;
        _assist = assist;
    }

    // ---- Avaliações, escopos, rodadas, pessoas e trilha ------------------------------------------------

    /// <summary>Avaliações do tenant (mais recentes primeiro), com escopos, rodadas e andamento da rodada mais recente.</summary>
    [HttpGet]
    public Task<ActionResult<IReadOnlyList<NistAssessmentView>>> List(CancellationToken ct) =>
        Run<IReadOnlyList<NistAssessmentView>>(async () => Ok(await _service.ListAsync(ct)));

    /// <summary>Histórico de maturidade por avaliação, rodada e escopo (só avaliações confirmadas por revisão humana).</summary>
    [HttpGet("history")]
    public Task<ActionResult<IReadOnlyList<NistAssessmentHistoryItem>>> History(CancellationToken ct) =>
        Run<IReadOnlyList<NistAssessmentHistoryItem>>(async () => Ok(await _service.HistoryAsync(ct)));

    /// <summary>Usuários ATIVOS do tenant que podem receber trabalho (nome e papel; sem e-mail).</summary>
    [HttpGet("assignees")]
    public Task<ActionResult<IReadOnlyList<NistAssigneeView>>> Assignees(CancellationToken ct) =>
        Run<IReadOnlyList<NistAssigneeView>>(async () => Ok(await _service.AssigneesAsync(ct)));

    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Estado da IA para a assistência NIST (desativada, simulada, não configurada, real).</summary>
    [HttpGet("assist/availability")]
    public Task<ActionResult<NistAssistAvailabilityView>> AssistAvailability(CancellationToken ct) =>
        Run<NistAssistAvailabilityView>(async () => Ok(await _assist.AvailabilityAsync(ct)));

    [HttpGet("{assessmentId:guid}")]
    public Task<ActionResult<NistAssessmentView>> Get(Guid assessmentId, CancellationToken ct) =>
        Run<NistAssessmentView>(async () => Ok(await _service.GetAsync(assessmentId, ct)));

    [HttpPost]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistAssessmentView>> Create([FromBody] CreateNistAssessmentRequest request, CancellationToken ct) =>
        Run<NistAssessmentView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            var created = await _service.CreateAsync(new CreateNistAssessmentCommand(
                request.Name ?? "", request.Description, request.StartDate, request.EndDate,
                request.InitialScopeName, request.InitialScopeDescription,
                request.InitialCycleName, request.InitialCyclePeriodKind, request.InitialCyclePeriodStart, request.InitialCyclePeriodEnd), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, created);
        });

    [HttpPost("{assessmentId:guid}/scopes")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistScopeView>> AddScope(Guid assessmentId, [FromBody] CreateNistScopeRequest request, CancellationToken ct) =>
        Run<NistScopeView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            var scope = await _service.AddScopeAsync(assessmentId, new CreateNistScopeCommand(request.Name ?? "", request.Description), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, scope);
        });

    /// <summary>Nova rodada (mensal, trimestral ou período informado), opcionalmente aproveitando outra como referência ou rascunho.</summary>
    [HttpPost("{assessmentId:guid}/cycles")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistCycleView>> CreateCycle(Guid assessmentId, [FromBody] CreateNistCycleRequest request, CancellationToken ct) =>
        Run<NistCycleView>(async () =>
        {
            if (request is null || request.PeriodStart is null || request.PeriodEnd is null)
                return BadRequest(Message("Informe nome e período da rodada."));
            var cycle = await _service.CreateCycleAsync(assessmentId, new CreateNistCycleCommand(request.Name ?? "", request.PeriodKind ?? "",
                request.PeriodStart.Value, request.PeriodEnd.Value, request.SeedFromCycleId, request.SeedMode ?? "None"), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, cycle);
        });

    /// <summary>Encerrar (somente leitura) ou reabrir uma rodada.</summary>
    [HttpPut("{assessmentId:guid}/cycles/{cycleId:guid}/status")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistCycleView>> SetCycleStatus(Guid assessmentId, Guid cycleId, [FromBody] SetNistCycleStatusRequest request, CancellationToken ct) =>
        Run<NistCycleView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _service.SetCycleStatusAsync(assessmentId, cycleId, new SetNistCycleStatusCommand(request.Status ?? "", request.ExpectedVersion), CurrentActor(), ct));
        });

    /// <summary>Trilha da avaliação: alterações com autor, instante e valores anteriores/novos (inclui os planos dos achados).</summary>
    [HttpGet("{assessmentId:guid}/audit")]
    public Task<ActionResult<IReadOnlyList<NistAuditEntryView>>> Audit(
        Guid assessmentId, [FromQuery] Guid? cycleId, [FromQuery] Guid? scopeId, [FromQuery] string? code, CancellationToken ct) =>
        Run<IReadOnlyList<NistAuditEntryView>>(async () => Ok(await _service.AuditAsync(assessmentId, cycleId, scopeId, code, ct)));

    /// <summary>Achados da avaliação (filtros opcionais de rodada, escopo, subcategoria e situação), com o plano de cada um.</summary>
    [HttpGet("{assessmentId:guid}/findings")]
    public Task<ActionResult<IReadOnlyList<NistFindingView>>> Findings(
        Guid assessmentId, [FromQuery] Guid? cycleId, [FromQuery] Guid? scopeId, [FromQuery] string? code, [FromQuery] string? status, CancellationToken ct) =>
        Run<IReadOnlyList<NistFindingView>>(async () => Ok(await _work.ListFindingsAsync(assessmentId, new NistFindingFilter(cycleId, scopeId, code, status), ct)));

    /// <summary>Publicações (fotografias de maturidade) da avaliação.</summary>
    [HttpGet("{assessmentId:guid}/publications")]
    public Task<ActionResult<IReadOnlyList<NistPublicationView>>> Publications(
        Guid assessmentId, [FromQuery] Guid? cycleId, [FromQuery] Guid? scopeId, CancellationToken ct) =>
        Run<IReadOnlyList<NistPublicationView>>(async () => Ok(await _publication.ListAsync(assessmentId, cycleId, scopeId, ct)));

    /// <summary>Compara duas rodadas da mesma avaliação no mesmo escopo (mudança de cobertura e de universo explícitas).</summary>
    [HttpGet("{assessmentId:guid}/scopes/{scopeId:guid}/compare")]
    public Task<ActionResult<NistCycleComparison>> Compare(
        Guid assessmentId, Guid scopeId, [FromQuery] Guid baseCycleId, [FromQuery] Guid targetCycleId, CancellationToken ct) =>
        Run<NistCycleComparison>(async () => Ok(await _publication.CompareCyclesAsync(assessmentId, scopeId, baseCycleId, targetCycleId, ct)));

    // ---- Rodada · escopo -----------------------------------------------------------------------------------

    /// <summary>Perfil atual × alvo, lacunas, andamento, procedimentos e tratamento da rodada e escopo.</summary>
    [HttpGet(CyclePath + "/profile")]
    public Task<ActionResult<NistProfileView>> Profile(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct) =>
        Run<NistProfileView>(async () => Ok(await _service.GetProfileAsync(assessmentId, cycleId, scopeId, ct)));

    /// <summary>Uma função com TODAS as categorias e subcategorias do catálogo, avaliadas ou não.</summary>
    [HttpGet(CyclePath + "/functions/{functionCode}")]
    public Task<ActionResult<NistFunctionView>> Function(Guid assessmentId, Guid cycleId, Guid scopeId, string functionCode, CancellationToken ct) =>
        Run<NistFunctionView>(async () => Ok(await _service.GetFunctionAsync(assessmentId, cycleId, scopeId, functionCode, ct)));

    /// <summary>Prévia da publicação: o relatório que seria congelado e a impressão digital do conteúdo revisado.</summary>
    /// <remarks>[AEGIS-ASSESSMENT-VISUALS-01] <c>historyUntil</c> ("aaaa-mm") e <c>historyMonths</c> (1–36) escolhem o período do histórico.</remarks>
    [HttpGet(CyclePath + "/publication-preview")]
    public Task<ActionResult<NistPublicationPreview>> PublicationPreview(
        Guid assessmentId, Guid cycleId, Guid scopeId, [FromQuery] string? historyUntil, [FromQuery] int? historyMonths, CancellationToken ct) =>
        Run<NistPublicationPreview>(async () =>
        {
            if (!HistoryWindow.TryCreate(historyUntil, historyMonths, out var window, out var bad)) return BadRequest(Message(bad));
            return Ok(await _publication.PreviewAsync(assessmentId, cycleId, scopeId, ct, window));
        });

    /// <response code="201">Fotografia publicada (exporte por /api/v1/posture/snapshots/{id}/export?format=html|pdf|csv).</response>
    /// <response code="409">O conteúdo mudou desde a prévia revisada — nada foi publicado.</response>
    [HttpPost(CyclePath + "/publications")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistPublicationView>> Publish(Guid assessmentId, Guid cycleId, Guid scopeId, [FromBody] PublishNistRequest request, CancellationToken ct) =>
        Run<NistPublicationView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            if (!HistoryWindow.TryCreate(request.HistoryUntil, request.HistoryMonths, out var window, out var bad)) return BadRequest(Message(bad));
            var view = await _publication.PublishAsync(assessmentId, cycleId, scopeId,
                new PublishNistCommand(request.ExpectedFingerprint ?? "", request.ExpectedHistoryFingerprint, window), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, view);
        });

    /// <summary>CSV de trabalho da rodada e escopo (todas as subcategorias, valores vigentes e versão de cada registro).</summary>
    [HttpGet(CyclePath + "/working-csv")]
    public async Task<IActionResult> WorkingCsv(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        var result = await Run<NistCsvFile>(async () => Ok(await _import.TemplateAsync(assessmentId, cycleId, scopeId, ct)));
        return result.Result is OkObjectResult { Value: NistCsvFile file }
            ? File(file.Content, "text/csv; charset=utf-8", file.FileName)
            : result.Result!;
    }

    /// <summary>Prévia da importação: nada é gravado; erros e conflitos por linha; token que amarra arquivo e versões.</summary>
    [HttpPost(CyclePath + "/import/preview")]
    [Authorize(Roles = Writers)]
    [RequestSizeLimit(3_000_000)]
    public Task<ActionResult<NistImportPreview>> ImportPreview(Guid assessmentId, Guid cycleId, Guid scopeId, [FromBody] NistImportRequest request, CancellationToken ct) =>
        Run<NistImportPreview>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _import.PreviewAsync(assessmentId, cycleId, scopeId, request.Csv ?? "", request.FileName, ct));
        });

    /// <response code="200">Importação aplicada (conteúdo como rascunho a confirmar).</response>
    /// <response code="400">Arquivo com erros — nada foi gravado.</response>
    /// <response code="409">Prévia desatualizada ou versão em conflito — nada foi gravado.</response>
    [HttpPost(CyclePath + "/import/apply")]
    [Authorize(Roles = Writers)]
    [RequestSizeLimit(3_000_000)]
    public Task<ActionResult<NistImportResult>> ImportApply(Guid assessmentId, Guid cycleId, Guid scopeId, [FromBody] NistImportRequest request, CancellationToken ct) =>
        Run<NistImportResult>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _import.ApplyAsync(assessmentId, cycleId, scopeId, request.Csv ?? "", request.FileName, request.Token ?? "", CurrentActor(), ct));
        });

    // ---- Subcategoria ---------------------------------------------------------------------------------------

    [HttpGet(CyclePath + "/subcategories/{code}")]
    public Task<ActionResult<NistSubcategoryDetailView>> Subcategory(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () => Ok(await _service.GetSubcategoryAsync(assessmentId, cycleId, scopeId, code, ct)));

    /// <response code="200">Avaliação gravada (confirma conteúdo herdado/importado); devolve o detalhe com a nova versão.</response>
    /// <response code="409">Versão desatualizada: outra pessoa gravou antes.</response>
    [HttpPut(CyclePath + "/subcategories/{code}")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistSubcategoryDetailView>> Save(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] SaveNistEvaluationRequest request, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _service.SaveEvaluationAsync(assessmentId, cycleId, scopeId, code, new SaveNistEvaluationCommand(
                request.CurrentLevel, request.TargetLevel, request.NotApplicable, request.CurrentComments, request.TargetComments,
                request.Rationale, request.Gaps, request.RiskImpact, request.ImprovementGuidance, request.OwnerName,
                request.ExpectedVersion, request.OwnerUserId, request.OwnerIsExternal, request.OwnerContact,
                Assistance(request.Assistance)), CurrentActor(), ct));
        });

    /// <summary>Designa avaliador e revisor (usuários ativos do tenant). Designar não concede privilégio.</summary>
    [HttpPut(CyclePath + "/subcategories/{code}/assignment")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistSubcategoryDetailView>> Assign(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] AssignNistRolesRequest request, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _service.AssignAsync(assessmentId, cycleId, scopeId, code,
                new AssignNistRolesCommand(request.AssessorUserId, request.ReviewerUserId, request.ExpectedVersion), CurrentActor(), ct));
        });

    /// <summary>Decisão do revisor (aprovar ou devolver) — por outra pessoa que não a autora da versão vigente.</summary>
    [HttpPost(CyclePath + "/subcategories/{code}/review")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistSubcategoryDetailView>> Review(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] ReviewNistRequest request, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _service.ReviewAsync(assessmentId, cycleId, scopeId, code,
                new ReviewNistEvaluationCommand(request.Decision ?? "", request.Note, request.ExpectedVersion), CurrentActor(), ct));
        });

    [HttpPost(CyclePath + "/subcategories/{code}/evidence")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistSubcategoryDetailView>> LinkEvidence(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] LinkNistEvidenceRequest request, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _service.LinkEvidenceAsync(assessmentId, cycleId, scopeId, code, new LinkNistEvidenceCommand(
                request.Kind ?? "", request.DocumentId, request.KnightRunId, request.KnightIndicatorId, request.Title,
                request.Uri, request.Notes, request.CollectedOn, request.ManualType), CurrentActor(), ct));
        });

    [HttpDelete(CyclePath + "/subcategories/{code}/evidence/{evidenceId:guid}")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistSubcategoryDetailView>> RemoveEvidence(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid evidenceId, CancellationToken ct) =>
        Run<NistSubcategoryDetailView>(async () =>
            Ok(await _service.RemoveEvidenceAsync(assessmentId, cycleId, scopeId, code, evidenceId, CurrentActor(), ct)));

    // ---- [AEGIS-NIST-AI-ASSIST-01] Assistência de IA ----------------------------------------------------------

    /// <summary>O contexto que a assistência usaria agora (fontes, impressão digital) e a última sugestão — sem chamar a IA.</summary>
    [HttpGet(CyclePath + "/subcategories/{code}/assist/context")]
    public Task<ActionResult<NistAssistContextView>> SubcategoryAssistContext(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CancellationToken ct) =>
        Run<NistAssistContextView>(async () => Ok(await _assist.SubcategoryContextAsync(assessmentId, cycleId, scopeId, code, ct)));

    /// <summary>
    /// Analisar as evidências: sugestão estruturada (resultado esperado, evidências favoráveis e contraditórias, lacunas,
    /// perguntas, procedimentos, melhorias, riscos, critérios e nível só com base suficiente). Nada é gravado na avaliação.
    /// </summary>
    /// <response code="503">IA desativada, indisponível, sem resposta no tempo ou resposta inválida (motivo em <c>reason</c>).</response>
    [HttpPost(CyclePath + "/subcategories/{code}/assist")]
    [Authorize(Roles = Writers)]
    [EnableRateLimiting("ai-auditor")]
    public Task<ActionResult<NistAssistView>> AssistSubcategory(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] NistAssistHttpRequest? request, CancellationToken ct) =>
        Run<NistAssistView>(async () => Ok(await _assist.AssistSubcategoryAsync(assessmentId, cycleId, scopeId, code,
            new NistAssistRequest(request?.Reuse ?? true), CurrentActor(), ct)));

    /// <summary>Planeja, de uma vez, os procedimentos sugeridos que a pessoa escolheu (planejar não é realizar).</summary>
    [HttpPost(CyclePath + "/subcategories/{code}/procedures/from-assistance")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<IReadOnlyList<NistProcedureView>>> PlanProceduresFromAssistance(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] PlanProceduresFromAssistanceRequest request, CancellationToken ct) =>
        Run<IReadOnlyList<NistProcedureView>>(async () =>
        {
            if (request is null || request.AssistanceId is not { } id) return BadRequest(Message("Informe a sugestão de origem."));
            var created = await _work.PlanProceduresFromAssistanceAsync(assessmentId, cycleId, scopeId, code, new PlanNistProceduresFromAssistanceCommand(
                id, (request.Procedures ?? Array.Empty<NistAssistedProcedureRequest>()).Select(p => new NistAssistedProcedureInput(p.Method ?? "", p.Procedure ?? "")).ToList(),
                request.AcknowledgeStale), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, created);
        });

    [HttpGet(CyclePath + "/findings/{findingId:guid}/assist/context")]
    public Task<ActionResult<NistAssistContextView>> FindingAssistContext(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, [FromQuery] string? focus, CancellationToken ct) =>
        Run<NistAssistContextView>(async () => Ok(await _assist.FindingContextAsync(assessmentId, cycleId, scopeId, findingId, focus, ct)));

    /// <summary>Explicar o achado (focus=Explain) ou sugerir tratamento (focus=Treatment). Não cria nem altera plano.</summary>
    [HttpPost(CyclePath + "/findings/{findingId:guid}/assist")]
    [Authorize(Roles = Writers)]
    [EnableRateLimiting("ai-auditor")]
    public Task<ActionResult<NistAssistView>> AssistFinding(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, [FromBody] NistAssistHttpRequest? request, CancellationToken ct) =>
        Run<NistAssistView>(async () => Ok(await _assist.AssistFindingAsync(assessmentId, cycleId, scopeId, findingId,
            new NistAssistRequest(request?.Reuse ?? true, request?.Focus), CurrentActor(), ct)));

    [HttpGet(CyclePath + "/executive-summary/assist/context")]
    public Task<ActionResult<NistAssistContextView>> ExecutiveAssistContext(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct) =>
        Run<NistAssistContextView>(async () => Ok(await _assist.ExecutiveContextAsync(assessmentId, cycleId, scopeId, ct)));

    /// <summary>Preparar resumo executivo: interpretação dos indicadores determinísticos (a IA não os calcula).</summary>
    [HttpPost(CyclePath + "/executive-summary/assist")]
    [Authorize(Roles = Writers)]
    [EnableRateLimiting("ai-auditor")]
    public Task<ActionResult<NistAssistView>> AssistExecutive(
        Guid assessmentId, Guid cycleId, Guid scopeId, [FromBody] NistAssistHttpRequest? request, CancellationToken ct) =>
        Run<NistAssistView>(async () => Ok(await _assist.AssistExecutiveAsync(assessmentId, cycleId, scopeId,
            new NistAssistRequest(request?.Reuse ?? true), CurrentActor(), ct)));

    /// <summary>Resumo executivo aceito da rodada e escopo (204 quando não há).</summary>
    [HttpGet(CyclePath + "/executive-summary")]
    public Task<ActionResult<NistExecutiveSummaryView>> ExecutiveSummary(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct) =>
        Run<NistExecutiveSummaryView>(async () =>
            await _assist.GetExecutiveSummaryAsync(assessmentId, cycleId, scopeId, ct) is { } view ? Ok(view) : NoContent());

    /// <summary>Aceitar (com ou sem edição) ou redigir o resumo executivo. Entra na próxima publicação enquanto a base for a mesma.</summary>
    /// <response code="409">Versão desatualizada, ou sugestão gerada sobre um estado anterior da rodada.</response>
    [HttpPut(CyclePath + "/executive-summary")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistExecutiveSummaryView>> SaveExecutiveSummary(
        Guid assessmentId, Guid cycleId, Guid scopeId, [FromBody] SaveNistExecutiveSummaryRequest request, CancellationToken ct) =>
        Run<NistExecutiveSummaryView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _assist.SaveExecutiveSummaryAsync(assessmentId, cycleId, scopeId, new SaveNistExecutiveSummaryCommand(
                (request.Sections ?? Array.Empty<NistExecutiveSectionRequest>()).Select(s => new NistExecutiveSectionInput(s.Key ?? "", s.Text ?? "")).ToList(),
                request.AssistanceId, request.ExpectedVersion, request.AcknowledgeStale), CurrentActor(), ct));
        });

    /// <summary>Revisão humana posterior do resumo, por outra pessoa que não quem o aceitou.</summary>
    [HttpPost(CyclePath + "/executive-summary/review")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistExecutiveSummaryView>> ReviewExecutiveSummary(
        Guid assessmentId, Guid cycleId, Guid scopeId, [FromBody] ReviewNistExecutiveSummaryRequest request, CancellationToken ct) =>
        Run<NistExecutiveSummaryView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _assist.ReviewExecutiveSummaryAsync(assessmentId, cycleId, scopeId,
                new ReviewNistExecutiveSummaryCommand(request.ExpectedVersion, request.Note), CurrentActor(), ct));
        });

    [HttpDelete(CyclePath + "/executive-summary")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<object>> WithdrawExecutiveSummary(
        Guid assessmentId, Guid cycleId, Guid scopeId, [FromQuery] int expectedVersion, CancellationToken ct) =>
        Run<object>(async () =>
        {
            await _assist.WithdrawExecutiveSummaryAsync(assessmentId, cycleId, scopeId, expectedVersion, CurrentActor(), ct);
            return NoContent();
        });

    // ---- Procedimentos de avaliação --------------------------------------------------------------------------

    [HttpPost(CyclePath + "/subcategories/{code}/procedures")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistProcedureView>> AddProcedure(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] AddNistProcedureRequest request, CancellationToken ct) =>
        Run<NistProcedureView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            var view = await _work.AddProcedureAsync(assessmentId, cycleId, scopeId, code, new AddNistProcedureCommand(request.Method ?? "", request.Procedure ?? ""), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, view);
        });

    /// <summary>Atualiza o procedimento planejado e/ou registra o RESULTADO (andamento, data, observação, conclusão, evidências).</summary>
    [HttpPut(CyclePath + "/subcategories/{code}/procedures/{procedureId:guid}")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistProcedureView>> UpdateProcedure(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid procedureId, [FromBody] UpdateNistProcedureRequest request, CancellationToken ct) =>
        Run<NistProcedureView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _work.UpdateProcedureAsync(assessmentId, cycleId, scopeId, code, procedureId, new UpdateNistProcedureCommand(
                request.Procedure, request.Status ?? "", request.Outcome, request.Observation, request.PerformedOn, request.EvidenceIds, request.ExpectedVersion), CurrentActor(), ct));
        });

    [HttpDelete(CyclePath + "/subcategories/{code}/procedures/{procedureId:guid}")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<object>> RemoveProcedure(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid procedureId, [FromQuery] int expectedVersion, CancellationToken ct) =>
        Run<object>(async () =>
        {
            await _work.RemoveProcedureAsync(assessmentId, cycleId, scopeId, code, procedureId, expectedVersion, CurrentActor(), ct);
            return NoContent();
        });

    // ---- Achados e planos ------------------------------------------------------------------------------------

    /// <summary>Registra um achado a partir de lacuna DOCUMENTADA (decisão do analista), opcionalmente já com o plano.</summary>
    [HttpPost(CyclePath + "/subcategories/{code}/findings")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> CreateFinding(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, [FromBody] CreateNistFindingRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            var view = await _work.CreateFindingAsync(assessmentId, cycleId, scopeId, code, new CreateNistFindingCommand(
                request.Title ?? "", request.Condition ?? "", request.Risk ?? "", request.Impact ?? "", request.Severity ?? "", request.SeverityRationale ?? "",
                request.Priority ?? "", request.PriorityRationale ?? "", request.Recommendation ?? "", request.EvidenceIds,
                request.Plan is { } p ? PlanInput(p) : null), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, view);
        });

    [HttpGet(CyclePath + "/findings/{findingId:guid}")]
    public Task<ActionResult<NistFindingView>> GetFinding(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, CancellationToken ct) =>
        Run<NistFindingView>(async () => Ok(await _work.GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct)));

    [HttpPut(CyclePath + "/findings/{findingId:guid}")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> UpdateFinding(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, [FromBody] UpdateNistFindingRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _work.UpdateFindingAsync(assessmentId, cycleId, scopeId, findingId, new UpdateNistFindingCommand(
                request.Title, request.Condition, request.Risk, request.Impact, request.Severity, request.SeverityRationale, request.Priority,
                request.PriorityRationale, request.Recommendation, request.EvidenceIds, request.ExpectedVersion, Assistance(request.Assistance)), CurrentActor(), ct));
        });

    /// <summary>Situação do achado: aberto, risco aceito ou encerrado (com justificativa). A maturidade não muda por isso.</summary>
    [HttpPut(CyclePath + "/findings/{findingId:guid}/status")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> SetFindingStatus(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, [FromBody] SetNistFindingStatusRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _work.SetFindingStatusAsync(assessmentId, cycleId, scopeId, findingId,
                new SetNistFindingStatusCommand(request.Status ?? "", request.Note, request.ExpectedVersion), CurrentActor(), ct));
        });

    [HttpPost(CyclePath + "/findings/{findingId:guid}/plans")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> CreatePlan(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, [FromBody] NistPlanRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            var view = await _work.CreatePlanAsync(assessmentId, cycleId, scopeId, findingId, PlanInput(request), CurrentActor(), ct);
            return StatusCode(StatusCodes.Status201Created, view);
        });

    /// <summary>Edita o plano e/ou muda a etapa (inclusive reabrir um plano concluído: novo ciclo, validação anterior no histórico).</summary>
    [HttpPut(CyclePath + "/findings/{findingId:guid}/plans/{planId:guid}")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> UpdatePlan(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, [FromBody] UpdateNistPlanRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _work.UpdatePlanAsync(assessmentId, cycleId, scopeId, findingId, planId, new UpdateNistPlanCommand(
                request.ExpectedVersion, request.Title, request.ProposedAction, Responsible(request.Responsible), request.ResponsibleArea,
                request.DueDate, request.Status), CurrentActor(), ct));
        });

    [HttpPost(CyclePath + "/findings/{findingId:guid}/plans/{planId:guid}/execution")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> RecordPlanExecution(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, [FromBody] NistPlanExecutionRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _work.RecordPlanExecutionAsync(assessmentId, cycleId, scopeId, findingId, planId,
                new RecordExecutionCommand(request.ExpectedVersion, request.Notes ?? "", request.EvidenceReference), CurrentActor(), ct));
        });

    /// <summary>Validação HUMANA com evidência referenciada (não altera maturidade; a reavaliação é outro ato).</summary>
    [HttpPost(CyclePath + "/findings/{findingId:guid}/plans/{planId:guid}/validations")]
    [Authorize(Roles = Writers)]
    public Task<ActionResult<NistFindingView>> ValidatePlan(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, [FromBody] NistPlanValidationRequest request, CancellationToken ct) =>
        Run<NistFindingView>(async () =>
        {
            if (request is null) return BadRequest(Message("Corpo da requisição ausente."));
            return Ok(await _work.ValidatePlanAsync(assessmentId, cycleId, scopeId, findingId, planId,
                new ValidateActionPlanCommand(request.ExpectedVersion, null, request.EvidenceReference, request.Note), CurrentActor(), ct));
        });

    // ---- Apoio ---------------------------------------------------------------------------------------

    private static NistPlanInput PlanInput(NistPlanRequest p) =>
        new(p.Title ?? "", p.ProposedAction, Responsible(p.Responsible), p.ResponsibleArea, p.DueDate, Assistance(p.Assistance));

    private static NistAssistanceReference? Assistance(NistAssistanceRequest? a) =>
        a?.AssistanceId is { } id ? new NistAssistanceReference(id, a.Fields, a.AcknowledgeStale) : null;

    private static NistResponsibleInput? Responsible(NistResponsibleRequest? r) =>
        r is null ? null : new NistResponsibleInput(r.UserId, r.Name, r.IsExternal, r.Contact);

    private static object Message(string message) => new { message };

    private async Task<ActionResult<T>> Run<T>(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NistAssessmentNotFoundException ex)
        {
            return NotFound(Message(ex.Message));
        }
        catch (NistAssessmentValidationException ex)
        {
            return BadRequest(Message(ex.Message));
        }
        catch (NistAssistStaleException ex)
        {
            return Conflict(new { message = ex.Message, reason = "AssistanceStale" });
        }
        catch (NistAssessmentConflictException ex)
        {
            return Conflict(Message(ex.Message));
        }
        catch (NistAiUnavailableException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message, reason = ex.Reason });
        }
    }

    /// <summary>O autor vem do TOKEN, nunca do corpo. Nome ausente permanece vazio.</summary>
    private RemediationActor CurrentActor()
    {
        Guid? accountId = Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var id) && id != Guid.Empty ? id : null;
        return new RemediationActor(accountId, User.FindFirst("name")?.Value ?? "");
    }
}

public sealed record CreateNistAssessmentRequest(
    string? Name, string? Description, DateOnly? StartDate, DateOnly? EndDate, string? InitialScopeName, string? InitialScopeDescription,
    string? InitialCycleName = null, string? InitialCyclePeriodKind = null, DateOnly? InitialCyclePeriodStart = null, DateOnly? InitialCyclePeriodEnd = null);

public sealed record CreateNistScopeRequest(string? Name, string? Description);

public sealed record CreateNistCycleRequest(string? Name, string? PeriodKind, DateOnly? PeriodStart, DateOnly? PeriodEnd, Guid? SeedFromCycleId, string? SeedMode);

public sealed record SetNistCycleStatusRequest(string? Status, int ExpectedVersion);

public sealed record SaveNistEvaluationRequest(
    int? CurrentLevel,
    int? TargetLevel,
    bool NotApplicable,
    string? CurrentComments,
    string? TargetComments,
    string? Rationale,
    string? Gaps,
    string? RiskImpact,
    string? ImprovementGuidance,
    string? OwnerName,
    int ExpectedVersion,
    Guid? OwnerUserId = null,
    bool OwnerIsExternal = false,
    string? OwnerContact = null,
    NistAssistanceRequest? Assistance = null);

/// <summary>[AEGIS-NIST-AI-ASSIST-01] Sugestão de origem do conteúdo aplicado e os campos que vieram dela.</summary>
public sealed record NistAssistanceRequest(Guid? AssistanceId, IReadOnlyList<string>? Fields, bool AcknowledgeStale = false);

public sealed record NistAssistHttpRequest(bool? Reuse, string? Focus);

public sealed record NistAssistedProcedureRequest(string? Method, string? Procedure);

public sealed record PlanProceduresFromAssistanceRequest(Guid? AssistanceId, IReadOnlyList<NistAssistedProcedureRequest>? Procedures, bool AcknowledgeStale = false);

public sealed record NistExecutiveSectionRequest(string? Key, string? Text);

public sealed record SaveNistExecutiveSummaryRequest(IReadOnlyList<NistExecutiveSectionRequest>? Sections, Guid? AssistanceId, int ExpectedVersion, bool AcknowledgeStale = false);

public sealed record ReviewNistExecutiveSummaryRequest(int ExpectedVersion, string? Note);

public sealed record AssignNistRolesRequest(Guid? AssessorUserId, Guid? ReviewerUserId, int ExpectedVersion);

public sealed record ReviewNistRequest(string? Decision, string? Note, int ExpectedVersion);

public sealed record LinkNistEvidenceRequest(
    string? Kind,
    Guid? DocumentId,
    Guid? KnightRunId,
    string? KnightIndicatorId,
    string? Title,
    string? Uri,
    string? Notes,
    DateOnly? CollectedOn,
    string? ManualType);

public sealed record AddNistProcedureRequest(string? Method, string? Procedure);

public sealed record UpdateNistProcedureRequest(
    string? Procedure, string? Status, string? Outcome, string? Observation, DateOnly? PerformedOn, IReadOnlyList<Guid>? EvidenceIds, int ExpectedVersion);

public sealed record NistResponsibleRequest(Guid? UserId, string? Name, bool IsExternal, string? Contact);

public sealed record NistPlanRequest(
    string? Title, string? ProposedAction, NistResponsibleRequest? Responsible, string? ResponsibleArea, DateOnly? DueDate,
    NistAssistanceRequest? Assistance = null);

public sealed record CreateNistFindingRequest(
    string? Title, string? Condition, string? Risk, string? Impact, string? Severity, string? SeverityRationale, string? Priority,
    string? PriorityRationale, string? Recommendation, IReadOnlyList<Guid>? EvidenceIds, NistPlanRequest? Plan);

public sealed record UpdateNistFindingRequest(
    string? Title, string? Condition, string? Risk, string? Impact, string? Severity, string? SeverityRationale, string? Priority,
    string? PriorityRationale, string? Recommendation, IReadOnlyList<Guid>? EvidenceIds, int ExpectedVersion,
    NistAssistanceRequest? Assistance = null);

public sealed record SetNistFindingStatusRequest(string? Status, string? Note, int ExpectedVersion);

public sealed record UpdateNistPlanRequest(
    int ExpectedVersion, string? Title, string? ProposedAction, NistResponsibleRequest? Responsible, string? ResponsibleArea, DateOnly? DueDate, string? Status);

public sealed record NistPlanExecutionRequest(int ExpectedVersion, string? Notes, string? EvidenceReference);

public sealed record NistPlanValidationRequest(int ExpectedVersion, string? EvidenceReference, string? Note);

public sealed record PublishNistRequest(
    string? ExpectedFingerprint,
    // [AEGIS-ASSESSMENT-VISUALS-01] Período do histórico congelado e a impressão digital do histórico da prévia.
    string? ExpectedHistoryFingerprint = null,
    string? HistoryUntil = null,
    int? HistoryMonths = null);

public sealed record NistImportRequest(string? Csv, string? FileName, string? Token);
