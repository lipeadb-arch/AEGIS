using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Posture;
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
    private readonly ITenantContext _tenant;

    public PostureSnapshotsController(IPostureSnapshotService service, ITenantContext tenant)
    {
        _service = service;
        _tenant = tenant;
    }

    /// <summary>
    /// Publica uma fotografia da postura ATUAL. O corpo só indica o instrumento (e a fonte KNIGHT, opcional);
    /// o servidor constrói a fotografia pelas autoridades do domínio — nunca por números vindos do cliente.
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
        if (request is null || !TryParseType(request.Type, out var type))
            return BadRequest($"Tipo de fotografia desconhecido: '{request?.Type}'.");

        KnightSourceType? source = null;
        if (type == PostureSnapshotType.Knight && !string.IsNullOrWhiteSpace(request.Source))
        {
            if (!TryParseSource(request.Source!, out var parsed))
                return BadRequest($"Fonte KNIGHT desconhecida: '{request.Source}'.");
            source = parsed;
        }

        try
        {
            var detail = await _service.PublishAsync(type, source, ct);
            return CreatedAtAction(nameof(GetById), new { id = detail.Summary.Id }, detail);
        }
        catch (PostureSnapshotNotAvailableException ex)
        {
            return Conflict(ex.Message);
        }
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
            if (!TryParseType(type!, out var parsed))
                return BadRequest($"Tipo de fotografia desconhecido: '{type}'.");
            filter = parsed;
        }

        return Ok(await _service.ListAsync(filter, ct));
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
}
