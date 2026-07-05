using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// IDENTIFY (ID.AM) — CRUD tático do inventário contínuo de ativos.
/// Tenant 100% implícito: o global query filter escopa toda leitura ao tenant ambiente e o
/// SaveChangesAsync carimba o TenantId na escrita (fail-closed). Sem [FromHeader] de tenant.
/// </summary>
[ApiController]
[Route("api/v1/assets")]
public class AssetsController : ControllerBase
{
    private const int MaxPageSize = 200;

    private readonly AegisScoreDbContext _db;
    public AssetsController(AegisScoreDbContext db) => _db = db;

    /// <summary>Lista paginada com filtros NIST combinados (categoria, risco, criticidade, busca).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<AssetDto>>> List([FromQuery] AssetQuery query, CancellationToken ct)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        // Base já isolada por tenant pelo global query filter. Filtros combinados via AND.
        var q = _db.Assets.AsNoTracking().AsQueryable();

        if (query.Category is { Count: > 0 })
            q = q.Where(a => query.Category.Contains(a.Category));       // OR entre categorias NIST
        if (query.RiskLevel is { } level)
            q = q.Where(a => a.RiskLevel == level);
        if (query.Criticality is { } crit)
            q = q.Where(a => a.Criticality == crit);
        if (query.IsActive is { } active)
            q = q.Where(a => a.IsActive == active);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                EF.Functions.ILike(a.Name, $"%{term}%") ||
                (a.SubType != null && EF.Functions.ILike(a.SubType, $"%{term}%")) ||
                (a.ExternalRef != null && EF.Functions.ILike(a.ExternalRef, $"%{term}%")));
        }

        var total = await q.LongCountAsync(ct);

        var rows = await q
            .OrderByDescending(a => a.RiskScore ?? -1)   // maior risco primeiro; não avaliados ao fim
            .ThenByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new PagedResult<AssetDto>(rows.Select(ToDto).ToList(), page, pageSize, total, totalPages);
    }

    /// <summary>Detalhe de um ativo. Um id de outro tenant retorna 404 (o filtro não o resolve — não vaza).</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AssetDto>> Get(Guid id, CancellationToken ct)
    {
        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return asset is null ? NotFound() : ToDto(asset);
    }

    [HttpPost]
    public async Task<ActionResult<IdResponse>> Create(CreateAssetRequest req, CancellationToken ct)
    {
        if (req.Criticality is < 1 or > 4)
            return BadRequest("Criticality deve estar entre 1 e 4.");

        // Unicidade de ExternalRef dentro do tenant (o índice único filtrado reforça no banco).
        if (!string.IsNullOrWhiteSpace(req.ExternalRef) &&
            await _db.Assets.AnyAsync(a => a.ExternalRef == req.ExternalRef, ct))
            return Conflict($"Já existe um ativo com a referência externa '{req.ExternalRef}' neste cliente.");

        var asset = new Asset
        {
            // Sem TenantId — carimbado no SaveChangesAsync (fail-closed).
            Name = req.Name,
            Category = req.Category,
            SubType = req.SubType,
            Description = req.Description,
            Criticality = req.Criticality,
            OwnerName = req.OwnerName,
            ExternalRef = req.ExternalRef,
            BusinessProcessId = req.BusinessProcessId,
            DiscoverySource = AssetDiscoverySource.Manual
            // Campos de risco ficam nulos: preenchidos depois pelo motor de IA.
        };
        _db.Assets.Add(asset);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = asset.Id }, new IdResponse(asset.Id));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateAssetRequest req, CancellationToken ct)
    {
        if (req.Criticality is < 1 or > 4)
            return BadRequest("Criticality deve estar entre 1 e 4.");

        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.ExternalRef) &&
            await _db.Assets.AnyAsync(a => a.Id != id && a.ExternalRef == req.ExternalRef, ct))
            return Conflict($"Já existe outro ativo com a referência externa '{req.ExternalRef}' neste cliente.");

        // Contexto/negócio é editável. Score/nível de risco NÃO — pertencem ao motor de IA.
        asset.Name = req.Name;
        asset.Category = req.Category;
        asset.SubType = req.SubType;
        asset.Description = req.Description;
        asset.Criticality = req.Criticality;
        asset.OwnerName = req.OwnerName;
        asset.ExternalRef = req.ExternalRef;
        asset.BusinessProcessId = req.BusinessProcessId;
        asset.IsActive = req.IsActive;

        await _db.SaveChangesAsync(ct);   // UpdatedAt carimbado automaticamente
        return NoContent();
    }

    /// <summary>Remoção definitiva. Para "desativar" preservando histórico, use PUT com IsActive=false.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var asset = await _db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();

        _db.Assets.Remove(asset);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static AssetDto ToDto(Asset a) => new(
        a.Id, a.Name, a.Category.ToString(), a.SubType, a.Description,
        a.Criticality, a.OwnerName, a.ExternalRef, a.BusinessProcessId,
        a.DiscoverySource.ToString(), a.LastSeenAt, a.IsActive,
        a.RiskScore, a.RiskLevel?.ToString(), a.RiskScoredAt, a.CreatedAt);
}
