using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Autoridade ÚNICA de leitura do inventário de software (produto consolidado
/// vendor+nome) do tenant ambiente. Somente leitura, isolada pelo Global Query Filter (fail-closed). A UNIDADE da
/// lista é o PRODUTO; os ativos relacionados carregam sob demanda (expansão paginada). Filtros/agregações/
/// ordenação/paginação acontecem NO BANCO. Não usa IA para ranking/filtro/contagem/status — tudo determinístico.
/// </summary>
public sealed class SoftwareInventoryQuery : ISoftwareInventoryQuery
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int AssetPreviewCap = 5;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;

    public SoftwareInventoryQuery(AegisScoreDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<SoftwareInventoryListDto> GetAsync(SoftwareInventoryFilter filter, CancellationToken ct = default)
    {
        var page = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize <= 0 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);

        if (_tenant.TenantId is null)
            return Empty(page, pageSize);

        var summary = await BuildSummaryAsync(ct);

        var q = _db.SoftwareProducts.AsNoTracking().Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Vendor))
        {
            var vendorKey = filter.Vendor!.Trim().ToLowerInvariant();
            q = q.Where(p => p.VendorKey == vendorKey);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var needle = filter.Search!.Trim().ToLower();
            q = q.Where(p => p.Name.ToLower().Contains(needle) || p.Vendor.ToLower().Contains(needle));
        }
        if (filter.PublicExploitOnly) q = q.Where(p => p.HasPublicExploit);
        if (filter.ActiveAlertOnly) q = q.Where(p => p.HasActiveAlert);
        if (filter.Weakness == SoftwareWeaknessFilter.WithWeaknesses) q = q.Where(p => p.WeaknessesCount > 0);
        if (filter.MinImpactScore is { } min) q = q.Where(p => p.ImpactScore != null && p.ImpactScore >= min);
        if (filter.MaxImpactScore is { } max) q = q.Where(p => p.ImpactScore != null && p.ImpactScore <= max);
        if (filter.AssetId is { } assetId)
            q = q.Where(p => _db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id && i.AssetId == assetId));

        // Estado EFETIVO do produto: Open se QUALQUER instalação estiver Open OU se não houver instalação
        // conhecida (mesmo idioma da exposição ativo×CVE); Resolved só quando há instalações e nenhuma está aberta.
        q = filter.State switch
        {
            SoftwareObservationStateFilter.Open => q.Where(p =>
                _db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id && i.LifecycleState == ObservationLifecycle.Open)
                || !_db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id)),
            SoftwareObservationStateFilter.Resolved => q.Where(p =>
                _db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id)
                && !_db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id && i.LifecycleState == ObservationLifecycle.Open)),
            _ => q,
        };

        var total = await q.CountAsync(ct);

        // Ordenação DETERMINÍSTICA no banco: (1) abertos primeiro; (2) exploit público; (3) alerta ativo;
        // (4) fraquezas; (5) dispositivos com instalação aberta; (6) nome/vendor; (7) Id (desempate estável).
        var pageIds = await q
            .OrderByDescending(p =>
                _db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id && i.LifecycleState == ObservationLifecycle.Open)
                || !_db.SoftwareInstallations.Any(i => i.SoftwareProductId == p.Id))
            .ThenByDescending(p => p.HasPublicExploit)
            .ThenByDescending(p => p.HasActiveAlert)
            .ThenByDescending(p => p.WeaknessesCount)
            .ThenByDescending(p => _db.SoftwareInstallations.Count(i => i.SoftwareProductId == p.Id && i.LifecycleState == ObservationLifecycle.Open))
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Vendor)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var items = await LoadPageAsync(pageIds, ct);
        return new SoftwareInventoryListDto(summary, items, total, page, pageSize);
    }

    public async Task<SoftwareProductAssetsDto> GetAssetsAsync(Guid productId, int page, int pageSize, CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        if (_tenant.TenantId is null) return new SoftwareProductAssetsDto(Array.Empty<SoftwareInstalledAssetPreviewDto>(), 0, page, pageSize);

        var q = _db.SoftwareInstallations.AsNoTracking().Where(i => i.SoftwareProductId == productId);
        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(i => i.LifecycleState == ObservationLifecycle.Open)
            .ThenByDescending(i => i.Asset!.Criticality)
            .ThenBy(i => i.Asset!.Name)
            .ThenBy(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.AssetId, Name = i.Asset!.Name, i.Asset!.Criticality, i.Asset!.SubType, i.Version, i.LifecycleState,
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new SoftwareInstalledAssetPreviewDto(
                r.AssetId, r.Name, r.Criticality, r.SubType,
                string.IsNullOrEmpty(r.Version) ? null : SourceTextSanitizer.ToPlainText(r.Version, 100),
                r.LifecycleState == ObservationLifecycle.Open ? "Open" : "Resolved"))
            .ToList();

        return new SoftwareProductAssetsDto(items, total, page, pageSize);
    }

    // ---- Página: carrega fontes + prévia de ativos SÓ dos IDs da página --------------------------------------

    private async Task<IReadOnlyList<SoftwareProductListItemDto>> LoadPageAsync(List<Guid> pageIds, CancellationToken ct)
    {
        if (pageIds.Count == 0) return Array.Empty<SoftwareProductListItemDto>();

        var products = await _db.SoftwareProducts.AsNoTracking()
            .Where(p => pageIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id, p.Vendor, p.Name, p.WeaknessesCount, p.HasPublicExploit, p.HasActiveAlert, p.ImpactScore,
                p.FirstSeenAt, p.LastSeenAt,
            })
            .ToListAsync(ct);
        var byId = products.ToDictionary(p => p.Id);

        var sourceRows = await _db.SoftwareProductSourceBindings.AsNoTracking()
            .Where(b => pageIds.Contains(b.SoftwareProductId) && b.IsActive)
            .Select(b => new { b.SoftwareProductId, Provider = b.ConnectorConfig!.Provider })
            .ToListAsync(ct);
        var sourcesByProduct = sourceRows.GroupBy(s => s.SoftwareProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(s => s.Provider.ToString()).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList());

        var installRows = await _db.SoftwareInstallations.AsNoTracking()
            .Where(i => pageIds.Contains(i.SoftwareProductId))
            .Select(i => new
            {
                i.SoftwareProductId, i.AssetId, Name = i.Asset!.Name, i.Asset!.Criticality, i.Asset!.SubType,
                i.Version, i.LifecycleState,
            })
            .ToListAsync(ct);
        var installsByProduct = installRows.GroupBy(i => i.SoftwareProductId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<SoftwareProductListItemDto>(pageIds.Count);
        foreach (var id in pageIds)   // preserva a ordem determinística da página
        {
            if (!byId.TryGetValue(id, out var p)) continue;
            installsByProduct.TryGetValue(id, out var installs);
            installs ??= new();
            sourcesByProduct.TryGetValue(id, out var sources);
            sources ??= Array.Empty<string>();

            var distinctDevices = installs.Select(i => i.AssetId).Distinct().Count();
            var openInstalls = installs.Where(i => i.LifecycleState == ObservationLifecycle.Open).ToList();
            var anyOpen = openInstalls.Count > 0;
            var effective = anyOpen || installs.Count == 0 ? "Open" : "Resolved";

            var preview = openInstalls
                .OrderByDescending(i => i.Criticality).ThenBy(i => i.Name, StringComparer.Ordinal)
                .Take(AssetPreviewCap)
                .Select(i => new SoftwareInstalledAssetPreviewDto(
                    i.AssetId, i.Name, i.Criticality, i.SubType,
                    string.IsNullOrEmpty(i.Version) ? null : SourceTextSanitizer.ToPlainText(i.Version, 100), "Open"))
                .ToList();

            result.Add(new SoftwareProductListItemDto(
                p.Id,
                SourceTextSanitizer.ToPlainText(p.Vendor, 200) ?? p.Vendor,
                SourceTextSanitizer.ToPlainText(p.Name, 300) ?? p.Name,
                distinctDevices, openInstalls.Select(i => i.AssetId).Distinct().Count(),
                p.WeaknessesCount, p.HasPublicExploit, p.HasActiveAlert, p.ImpactScore,
                FirstAction(p.HasPublicExploit, p.HasActiveAlert, p.WeaknessesCount),
                sources, preview, openInstalls.Count > preview.Count,
                p.FirstSeenAt, p.LastSeenAt, effective));
        }
        return result;
    }

    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-01] Primeira ação DETERMINÍSTICA e neutra de provedor — NUNCA gerada por IA.
    /// Só combina os fatos já coletados (exploit público/alerta ativo/fraquezas); uma explicação consultiva futura
    /// pode elaborar sobre estes fatos determinísticos, mas não é autoridade sobre eles.
    /// </summary>
    private static string FirstAction(bool publicExploit, bool activeAlert, int weaknesses)
    {
        if (publicExploit && activeAlert)
            return "Priorizar a atualização imediatamente — há exploit público conhecido e um alerta ativo associado a este produto.";
        if (publicExploit)
            return "Atualizar assim que possível — existe exploit público conhecido para este produto.";
        if (activeAlert)
            return "Investigar o alerta ativo associado e avaliar a atualização deste produto.";
        if (weaknesses > 0)
            return "Avaliar a atualização — há fraquezas conhecidas associadas a este produto.";
        return "Sem ação prioritária indicada pela fonte no momento — manter monitoramento.";
    }

    // ---- Resumo tenant-wide (agregados NO BANCO) ---------------------------------------------------

    private async Task<SoftwareInventorySummaryDto> BuildSummaryAsync(CancellationToken ct)
    {
        var activeQ = _db.SoftwareProducts.AsNoTracking().Where(p => p.IsActive);

        var totalProducts = await activeQ.CountAsync(ct);
        var withWeaknesses = await activeQ.CountAsync(p => p.WeaknessesCount > 0, ct);
        var withExploit = await activeQ.CountAsync(p => p.HasPublicExploit, ct);
        var withAlert = await activeQ.CountAsync(p => p.HasActiveAlert, ct);
        var exposedInstallations = await _db.SoftwareInstallations.AsNoTracking()
            .Where(i => i.LifecycleState == ObservationLifecycle.Open && i.SoftwareProduct!.IsActive)
            .Select(i => i.AssetId)
            .Distinct()
            .CountAsync(ct);

        // Fontes: os MESMOS conectores VulnerabilityScanner do tenant (Software Inventory é dimensão adicional do
        // mesmo conector) — o snapshot, quando existe, carrega o estado/freshness ESPECÍFICO desta dimensão.
        var connectors = await _db.Connectors.AsNoTracking()
            .Where(c => c.Capability == ConnectorCapability.VulnerabilityScanner)
            .Select(c => new { c.Id, c.Provider, c.DisplayName })
            .ToListAsync(ct);
        var snapshots = (await _db.SoftwareInventorySnapshots.AsNoTracking().ToListAsync(ct))
            .ToDictionary(s => s.ConnectorConfigId);

        var sources = connectors.Select(c =>
        {
            snapshots.TryGetValue(c.Id, out var snap);
            return new SoftwareInventorySourceDto(
                c.Id, c.Provider.ToString(), c.DisplayName,
                (snap?.CollectionState ?? SoftwareInventoryCollectionState.NeverCollected).ToString(),
                (snap?.LastAttemptState ?? SoftwareInventoryCollectionState.NeverCollected).ToString(),
                snap?.LastAttemptAt ?? default,
                snap?.LastCollectionAt,
                snap?.LastAttemptDetail);
        }).ToList();

        var lastCollectedAt = sources
            .Where(s => s.LastCollectionAt.HasValue)
            .Select(s => s.LastCollectionAt)
            .DefaultIfEmpty(null)
            .Max();
        var neverCollected = lastCollectedAt is null;

        return new SoftwareInventorySummaryDto(
            totalProducts, withWeaknesses, withExploit, withAlert, exposedInstallations,
            sources, lastCollectedAt, neverCollected);
    }

    private static SoftwareInventoryListDto Empty(int page, int pageSize) => new(
        new SoftwareInventorySummaryDto(0, 0, 0, 0, 0, Array.Empty<SoftwareInventorySourceDto>(), null, true),
        Array.Empty<SoftwareProductListItemDto>(), 0, page, pageSize);
}
