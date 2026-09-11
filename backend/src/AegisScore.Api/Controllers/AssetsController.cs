using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// IDENTIFY (ID.AM) — leitura do inventário contínuo de ativos. Superfície PASSIVA por design: apenas
/// lista o inventário (a grid tática do frontend). A gestão de ativos entra por descoberta contínua
/// (seed/conector), e a AVALIAÇÃO de conformidade do ativo é ativa e vem por telemetria
/// (<c>POST api/v1/telemetry/asset</c> → motor → ledger ID.AM), não por CRUD manual.
///
/// [AEGIS-ENTITY-RESOLUTION-01] Cada linha é UM ativo canônico: um dispositivo observado por duas fontes aparece uma
/// vez, com o resumo das fontes e do vínculo entre elas; o detalhe (<c>{id}/sources</c>) mostra cada fonte e a
/// explicação do vínculo. Somente leitura — nunca cria, move ou funde registro de fonte.
///
/// Tenant 100% implícito: o global query filter escopa toda leitura ao tenant ambiente. Sem [FromHeader].
/// </summary>
[ApiController]
[Route("api/v1/assets")]
[Authorize]
public class AssetsController : ControllerBase
{
    private const int MaxPageSize = 200;

    private readonly AegisScoreDbContext _db;
    private readonly IAssetSourceQuery _sources;

    public AssetsController(AegisScoreDbContext db, IAssetSourceQuery sources)
    {
        _db = db;
        _sources = sources;
    }

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

        // Resumo das fontes SÓ para a página (uma consulta limitada, nunca N+1).
        var summaries = await _sources.GetSummariesAsync(rows.Select(r => r.Id).ToList(), ct);

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        return new PagedResult<AssetDto>(
            rows.Select(a => ToDto(a, summaries.TryGetValue(a.Id, out var s) ? s : null)).ToList(),
            page, pageSize, total, totalPages);
    }

    /// <summary>
    /// [AEGIS-ENTITY-RESOLUTION-01] Fontes que observaram o ativo, a última observação de cada uma e o estado do
    /// vínculo entre elas. Identificadores técnicos (id na fonte, identificador de diretório) só no DIAGNÓSTICO,
    /// para o administrador do tenant — nunca na listagem nem para os demais papéis.
    /// </summary>
    [HttpGet("{id:guid}/sources")]
    public async Task<ActionResult<AssetSourcesDto>> Sources(Guid id, CancellationToken ct)
    {
        var dto = await _sources.GetSourcesAsync(id, includeDiagnostics: User.IsInRole("TenantAdmin"), ct);
        return dto is null ? NotFound() : dto;
    }

    private static AssetDto ToDto(Asset a, AssetSourceSummaryDto? sources) => new(
        a.Id, a.Name, a.Category.ToString(), a.SubType, a.Description,
        a.Criticality, a.OwnerName, a.ExternalRef, a.BusinessProcessId,
        a.DiscoverySource.ToString(), a.LastSeenAt, a.IsActive,
        a.RiskScore, a.RiskLevel?.ToString(), a.RiskScoredAt, a.CreatedAt,
        NameIsPlaceholder: a.NameOrigin == AssetNameOrigin.Placeholder,
        Sources: sources);
}
