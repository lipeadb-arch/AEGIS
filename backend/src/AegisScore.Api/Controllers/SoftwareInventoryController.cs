using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Application.Queries;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] IDENTIFY (ID.AM) — leitura do INVENTÁRIO/EXPOSIÇÃO DE SOFTWARE (produto
/// consolidado vendor+nome, correlacionado a ativos via instalação). Superfície DEDICADA e somente leitura — a
/// tela consome esta rota como a aba "Software exposto" da área de Vulnerabilidades, sem novo item de menu.
///
/// Tenant 100% implícito: o Global Query Filter escopa toda leitura ao tenant do JWT. A coleta/reconciliação
/// (escrita) é do pipeline de conectores; aqui nunca se cria/altera produto/instalação. Software Inventory é
/// evidência OPERACIONAL/DE EXPOSIÇÃO — não concede nem remove pontos do AEGIS Score.
/// </summary>
[ApiController]
[Route("api/v1/software-inventory")]
[Authorize]
public class SoftwareInventoryController : ControllerBase
{
    private readonly ISoftwareInventoryQuery _query;

    public SoftwareInventoryController(ISoftwareInventoryQuery query) => _query = query;

    /// <summary>Lista PRIORIZADA de produtos de software + resumo/KPIs, com filtros e ordenação determinística.</summary>
    [HttpGet]
    public async Task<ActionResult<SoftwareInventoryListDto>> List(
        [FromQuery] string? search,
        [FromQuery] string? vendor,
        [FromQuery] bool publicExploit = false,
        [FromQuery] bool activeAlert = false,
        [FromQuery] bool withWeaknesses = false,
        [FromQuery] double? minImpact = null,
        [FromQuery] double? maxImpact = null,
        [FromQuery] string? state = null,
        [FromQuery] Guid? assetId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var filter = new SoftwareInventoryFilter(
            Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Vendor: string.IsNullOrWhiteSpace(vendor) ? null : vendor.Trim().ToLowerInvariant(),
            PublicExploitOnly: publicExploit,
            ActiveAlertOnly: activeAlert,
            Weakness: withWeaknesses ? SoftwareWeaknessFilter.WithWeaknesses : SoftwareWeaknessFilter.All,
            MinImpactScore: minImpact,
            MaxImpactScore: maxImpact,
            State: ParseState(state),
            AssetId: assetId,
            Page: page,
            PageSize: pageSize);

        return await _query.GetAsync(filter, ct);
    }

    /// <summary>Ativos relacionados a UM produto (expansão paginada sob demanda — nunca N+1 na abertura padrão).</summary>
    [HttpGet("{productId:guid}/assets")]
    public async Task<ActionResult<SoftwareProductAssetsDto>> Assets(
        Guid productId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        return await _query.GetAssetsAsync(productId, page, pageSize, ct);
    }

    private static SoftwareObservationStateFilter ParseState(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "open" => SoftwareObservationStateFilter.Open,
        "resolved" => SoftwareObservationStateFilter.Resolved,
        _ => SoftwareObservationStateFilter.All,
    };
}
