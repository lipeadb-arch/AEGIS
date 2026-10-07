using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-AUD-035/036/037] Fotografia AUDITÁVEL de postura — histórico imutável COMPARTILHADO entre o AEGIS
/// Score/NIST e o AEGIS KNIGHT. Superfície ÚNICA de publicação controlada, leitura e comparação; a fotografia é
/// APPEND-ONLY (sem update/delete). Tenant SEMPRE implícito: resolvido do claim <c>tenant_id</c> do JWT pelo
/// ITenantContext e aplicado pelo Global Query Filter (fail-closed) — nunca via URL/QueryString/corpo, de modo
/// que um tenant jamais publique, leia ou compare a fotografia de outro. NÃO há score combinado entre os
/// instrumentos: o tipo os separa, e a comparação só é válida entre fotografias compatíveis.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/posture/snapshots")]
public class PostureSnapshotsController : ControllerBase
{
    private readonly IPostureSnapshotService _service;
    private readonly IPostureSnapshotExporter _exporter;
    private readonly ITenantContext _tenant;

    public PostureSnapshotsController(
        IPostureSnapshotService service, IPostureSnapshotExporter exporter, ITenantContext tenant)
    {
        _service = service;
        _exporter = exporter;
        _tenant = tenant;
    }

    /// <summary>
    /// Publica uma fotografia da postura ATUAL. O corpo só indica o instrumento (e, para KNIGHT, a fonte ou a
    /// avaliação exata); o servidor constrói a fotografia pelas autoridades do domínio — nunca por números
    /// vindos do cliente. [AEGIS-MVP-PRODUCT-03] Informando <c>runId</c>, publica-se AQUELA avaliação: publicar
    /// a avaliação aberta por link e receber a fotografia de outra coleta seria a substituição silenciosa que a
    /// tela do KNIGHT já se recusa a fazer.
    /// Publicar cria um registro PERMANENTE e imutável: exige papel tenant-scoped <c>Manager</c> ou
    /// <c>TenantAdmin</c> (um Analyst pode LER o histórico, mas não publicar). Não amplia autoridade de plataforma.
    /// </summary>
    /// <response code="201">Fotografia publicada.</response>
    /// <response code="400">Tipo/fonte desconhecidos.</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="403">Papel do tenant insuficiente (Analyst não publica).</response>
    /// <response code="409">Não há postura a fotografar (ex.: nenhum assessment KNIGHT concluído).</response>
    [HttpPost]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<PostureSnapshotDetailDto>> Publish(
        [FromBody] PublishPostureSnapshotRequest request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (request is not null && IsNistMaturity(request.Type))
            return BadRequest("A fotografia de maturidade NIST é publicada na jornada do AEGIS NIST (avaliação · rodada · escopo).");
        if (request is null || !TryParseType(request.Type, out var type))
            return BadRequest($"Tipo de fotografia desconhecido: '{request?.Type}'.");

        KnightSourceType? source = null;
        if (type == PostureSnapshotType.Knight && !string.IsNullOrWhiteSpace(request.Source))
        {
            if (!TryParseSource(request.Source!, out var parsed))
                return BadRequest($"Fonte KNIGHT desconhecida: '{request.Source}'.");
            source = parsed;
        }

        if (!HistoryWindow.TryCreate(request.HistoryUntil, request.HistoryMonths, out var window, out var invalid))
            return BadRequest(invalid);

        try
        {
            // [AEGIS-MVP-PRODUCT-03] Com runId, publica EXATAMENTE aquela avaliação. Sem ele, o comportamento
            // existente (a mais recente, opcionalmente da fonte) é preservado. Uma avaliação pedida e
            // indisponível vira 409 — jamais a substituição silenciosa pela mais recente.
            var detail = await _service.PublishAsync(type, source, request.RunId, ct, window, request.ExpectedHistoryFingerprint);
            return CreatedAtAction(nameof(GetById), new { id = detail.Summary.Id }, detail);
        }
        catch (PostureSnapshotNotAvailableException ex)
        {
            return Conflict(ex.Message);
        }
        catch (HistoryChangedException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// [AEGIS-ASSESSMENT-VISUALS-01] Prévia do histórico mensal que a publicação KNIGHT congelaria para a execução indicada
    /// (<paramref name="runId"/>, ou a mais recente da <paramref name="source"/>), no período escolhido. Devolve a série e a
    /// impressão digital que a publicação confere (409 se a série mudou). Somente leitura; qualquer papel que lê o histórico.
    /// </summary>
    [HttpGet("history-preview")]
    public async Task<ActionResult<FrozenPostureHistory>> HistoryPreview(
        [FromQuery] Guid? runId, [FromQuery] string? source, [FromQuery] string? until, [FromQuery] int? months, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        KnightSourceType? parsed = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (!TryParseSource(source!, out var s)) return BadRequest($"Fonte KNIGHT desconhecida: '{source}'.");
            parsed = s;
        }
        if (!HistoryWindow.TryCreate(until, months, out var window, out var invalid))
            return BadRequest(invalid);
        try
        {
            return Ok(await _service.PreviewKnightHistoryAsync(parsed, runId, window, ct));
        }
        catch (PostureSnapshotNotAvailableException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Idem, para a composição consolidada PINADA (mesmo corpo da publicação).</summary>
    [HttpPost("consolidated/history-preview")]
    public async Task<ActionResult<FrozenPostureHistory>> ConsolidatedHistoryPreview(
        [FromBody] PublishConsolidatedKnightSnapshotRequest? request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (!TrySelection(request, out var selection, out var bad))
            return BadRequest(bad);
        if (!HistoryWindow.TryCreate(request?.HistoryUntil, request?.HistoryMonths, out var window, out var invalid))
            return BadRequest(invalid);
        try
        {
            return Ok(await _service.PreviewConsolidatedHistoryAsync(selection, window, ct));
        }
        catch (PostureSnapshotNotAvailableException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-CONSOLIDATED-01] Publica uma fotografia KNIGHT que COMPÕE, sem somar, as execuções
    /// PINADAS em <paramref name="request"/>.<c>Selection</c> — a composição que a tela mostrava como incluída
    /// no instante da publicação, fonte a fonte. [AEGIS-KNIGHT-CONSOLIDATED-02] Cada item fixa a execução EXATA
    /// (nunca "a mais recente"): tenant (Global Query Filter), fonte e conclusão são revalidados no servidor, e
    /// uma execução que deixou de satisfazer alguma delas recusa a publicação em vez de ser silenciosamente
    /// substituída. <c>Selection</c> nula preserva o comportamento legado (última concluída de cada candidata).
    /// Todas as fontes elegíveis sempre aparecem na composição congelada — incluídas ou não. Mesmo controle de acesso
    /// da publicação por fonte.
    /// </summary>
    /// <response code="201">Fotografia consolidada publicada.</response>
    /// <response code="400">Alguma fonte pedida não é reconhecida, não é candidata do relatório consolidado, ou aparece duplicada.</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="403">Papel do tenant insuficiente (Analyst não publica).</response>
    /// <response code="409">Nenhuma fonte foi selecionada, ou alguma execução pinada não está mais disponível/concluída/deste tenant.</response>
    [HttpPost("consolidated")]
    [Authorize(Roles = "Manager,TenantAdmin")]
    public async Task<ActionResult<PostureSnapshotDetailDto>> PublishConsolidated(
        [FromBody] PublishConsolidatedKnightSnapshotRequest? request, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        if (!TrySelection(request, out var selection, out var bad))
            return BadRequest(bad);
        if (!HistoryWindow.TryCreate(request?.HistoryUntil, request?.HistoryMonths, out var window, out var invalid))
            return BadRequest(invalid);

        try
        {
            var detail = await _service.PublishConsolidatedKnightAsync(selection, ct, window, request?.ExpectedHistoryFingerprint);
            return CreatedAtAction(nameof(GetById), new { id = detail.Summary.Id }, detail);
        }
        catch (PostureSnapshotNotAvailableException ex)
        {
            return Conflict(ex.Message);
        }
        catch (HistoryChangedException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>Valida a seleção pinada do consolidado (fonte conhecida, elegível, com execução e sem repetição).</summary>
    private static bool TrySelection(
        PublishConsolidatedKnightSnapshotRequest? request, out List<KnightConsolidatedSourceSelection>? selection, out string error)
    {
        selection = null;
        error = "";
        if (request?.Selection is null) return true;
        selection = new List<KnightConsolidatedSourceSelection>();
        foreach (var item in request.Selection)
        {
            if (!KnightSourceNames.TryParse(item.Source, out var source))
            {
                error = $"Fonte KNIGHT desconhecida: '{item.Source}'.";
                return false;
            }
            if (!KnightConsolidatedCandidates.Sources.Contains(source))
            {
                // [AEGIS-ASSESSMENT-VISUALS-01] A lista vem do catálogo único de fontes (nove fontes Microsoft elegíveis).
                error = $"Fonte '{item.Source}' não é elegível para o relatório consolidado ({string.Join(", ", KnightConsolidatedCandidates.Sources.Select(KnightConsolidatedCandidates.StaticLabel))}).";
                return false;
            }
            if (item.RunId == Guid.Empty)
            {
                error = $"Execução ausente para a fonte '{item.Source}'.";
                return false;
            }
            if (selection.Any(s => s.Source == source))
            {
                error = $"Mais de uma execução foi indicada para a fonte '{item.Source}'.";
                return false;
            }
            selection.Add(new KnightConsolidatedSourceSelection(source, item.RunId));
        }
        return true;
    }

    /// <summary>Lista as fotografias do tenant (mais recentes primeiro), opcionalmente filtradas por tipo.</summary>
    /// <response code="200">Lista (possivelmente vazia).</response>
    /// <response code="400">Tipo de filtro desconhecido.</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PostureSnapshotSummaryDto>>> List(
        [FromQuery] string? type, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");

        PostureSnapshotType? filter = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (IsNistMaturity(type))
                filter = PostureSnapshotType.NistMaturity;
            else if (!TryParseType(type!, out var parsed))
                return BadRequest($"Tipo de fotografia desconhecido: '{type}'.");
            else
                filter = parsed;
        }

        return Ok(await _service.ListAsync(filter, ct));
    }

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-01] Evolução MENSAL por instrumento e fonte (KNIGHT e NIST em séries separadas), a partir das
    /// fotografias publicadas: cada mês é a última publicação do mês; mês sem publicação fica sem ponto; fotografias de
    /// fórmula, catálogo ou esquema diferentes não são ligadas (mesma regra da comparação).
    /// </summary>
    /// <remarks>
    /// [AEGIS-ASSESSMENT-VISUALS-01] <c>until</c> ("aaaa-mm") escolhe o último mês do período; <c>months</c> (1–36) o tamanho.
    /// O mês corrente só aparece quando pertence ao período pedido.
    /// </remarks>
    [HttpGet("monthly")]
    public async Task<ActionResult<PostureMonthlyHistoryDto>> Monthly(
        [FromQuery] int months, [FromQuery] string? until, [FromServices] TimeProvider clock, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (!HistoryWindow.TryParseMonth(until, out var last))
            return BadRequest($"Mês final inválido: '{until}'. Use o formato aaaa-mm.");
        var all = await _service.ListAsync(null, ct);
        return Ok(PostureMonthlyHistory.Build(all, clock.GetUtcNow(), months <= 0 ? PostureMonthlyHistory.DefaultMonths : months, last));
    }

    /// <summary>Detalhe de uma fotografia do tenant (401 sem tenant; 404 inexistente/de outro tenant).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PostureSnapshotDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        var detail = await _service.GetAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>
    /// [AEGIS-AUD-034] Baixa o relatório executivo da fotografia derivado EXCLUSIVAMENTE dela: <c>?format=pdf</c>
    /// (PDF executivo pt-BR) ou <c>?format=csv</c> (dados completos, UTF-8 com BOM, protegido contra CSV Injection).
    /// Autoriza os MESMOS usuários que já leem a fotografia (inclusive Analyst) — só o class-level <c>[Authorize]</c>.
    /// Tenant SEMPRE implícito no contexto (nunca por URL/query/corpo); a fotografia é lida pelo query filter
    /// fail-closed, então a de outro tenant simplesmente não é encontrada. Antes de gerar, o ContentHash é
    /// reverificado. Não grava nada e não altera a fotografia.
    /// </summary>
    /// <response code="200">Download do arquivo (Content-Type e nome seguros).</response>
    /// <response code="400">Formato desconhecido (só pdf/csv).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="404">Fotografia inexistente ou de outro tenant.</response>
    /// <response code="409">Integridade da fotografia não confere (hash divergente) — exportação bloqueada.</response>
    [HttpGet("{id:guid}/export")]
    public async Task<IActionResult> Export(Guid id, [FromQuery] string? format, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (!PostureExportFormats.TryParse(format, out var fmt))
            return BadRequest($"Formato de exportação desconhecido: '{format}'. Use 'pdf', 'csv' ou 'html'.");

        try
        {
            var result = await _exporter.ExportAsync(id, fmt, ct);
            return result is null ? NotFound() : File(result.Content, result.ContentType, result.FileName);
        }
        catch (PostureSnapshotIntegrityException ex)
        {
            return Conflict(ex.Message);
        }
        catch (PostureExportNotSupportedException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Compara duas fotografias do tenant. Devolve o comparativo quando COMPATÍVEIS ou um estado explícito de
    /// incompatibilidade — NUNCA um delta enganoso. 404 quando alguma fotografia não existe.
    /// </summary>
    /// <response code="200">Resultado (compatível → delta; incompatível → estado com motivos).</response>
    /// <response code="401">Tenant não resolvido no contexto.</response>
    /// <response code="404">Uma das fotografias não existe (ou é de outro tenant).</response>
    [HttpGet("compare")]
    public async Task<ActionResult<PostureComparisonResultDto>> Compare(
        [FromQuery] Guid baseId, [FromQuery] Guid targetId, CancellationToken ct)
    {
        if (_tenant.TenantId is not Guid)
            return Unauthorized("Tenant não resolvido no contexto (claim tenant_id ausente).");
        if (baseId == Guid.Empty || targetId == Guid.Empty)
            return BadRequest("Informe as duas fotografias a comparar (baseId e targetId).");

        var result = await _service.CompareAsync(baseId, targetId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    // ---- Parsing ------------------------------------------------------------------------------------

    private static bool TryParseType(string value, out PostureSnapshotType type)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "aegis":
            case "aegisscore":
            case "aegisscorenist":
            case "nist": type = PostureSnapshotType.AegisScoreNist; return true;
            case "knight":
            case "aegisknight": type = PostureSnapshotType.Knight; return true;
            default: type = default; return false;
        }
    }

    /// <summary>[AEGIS-NIST-JOURNEY-02] Maturidade NIST (1–5) — instrumento próprio, listado mas publicado só pela jornada NIST.</summary>
    private static bool IsNistMaturity(string? value) =>
        (value ?? "").Trim().Replace("-", "").Replace("_", "").ToLowerInvariant() is "nistmaturity" or "nistmaturidade";

    private static bool TryParseSource(string source, out KnightSourceType sourceType) =>
        KnightSourceNames.TryParse(source, out sourceType);
}
