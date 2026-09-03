using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

// ---- [AEGIS-MVP-MICROSOFT-COVERAGE-01] Leitura tenant-scoped do inventário/exposição de software ----------------
// Superfície DEDICADA e SOMENTE LEITURA — nunca cria/altera produto, instalação ou exposição (isso é papel do
// SoftwareInventoryReconciler). A UNIDADE da lista é o PRODUTO consolidado (vendor+nome); os ativos relacionados
// carregam sob demanda ao expandir. IDs técnicos/vendor ficam nos detalhes — a lista usa linguagem em pt-BR.

/// <summary>Filtro por presença de fraquezas conhecidas (fato da fonte — nunca inferido).</summary>
public enum SoftwareWeaknessFilter { All = 0, WithWeaknesses = 1 }

/// <summary>Filtro pelo estado EFETIVO da observação do produto (agregado das instalações ativas).</summary>
public enum SoftwareObservationStateFilter { All = 0, Open = 1, Resolved = 2 }

/// <summary>
/// Parâmetros da listagem de software. O tenant NÃO trafega — é IMPLÍCITO (JWT + Global Query Filter fail-closed).
/// </summary>
public sealed record SoftwareInventoryFilter(
    string? Search = null,
    string? Vendor = null,
    bool PublicExploitOnly = false,
    bool ActiveAlertOnly = false,
    SoftwareWeaknessFilter Weakness = SoftwareWeaknessFilter.All,
    double? MinImpactScore = null,
    double? MaxImpactScore = null,
    SoftwareObservationStateFilter State = SoftwareObservationStateFilter.All,
    /// <summary>Correlação produto→ativo é a leitura padrão (expandir); este filtro cobre a direção ativo→software.</summary>
    Guid? AssetId = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>Uma FONTE (conector Defender-like) configurada no tenant, com estado/freshness da dimensão de software.</summary>
public sealed record SoftwareInventorySourceDto(
    Guid ConnectorConfigId,
    string Provider,
    string DisplayName,
    string CollectionState,
    string LastAttemptState,
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? LastCollectionAt,
    string? LastAttemptDetail);

/// <summary>
/// KPIs tenant-scoped do inventário de software. <see cref="NeverCollected"/> distingue "nenhuma fonte com
/// Software.Read.All sincronizou ainda" de "coletado sem achados" — nunca mostrar zero sintético quando indisponível.
/// </summary>
public sealed record SoftwareInventorySummaryDto(
    int TotalProducts,
    int ProductsWithWeaknesses,
    int ProductsWithPublicExploit,
    int ProductsWithActiveAlert,
    int ExposedInstallations,
    IReadOnlyList<SoftwareInventorySourceDto> Sources,
    DateTimeOffset? LastCollectedAt,
    bool NeverCollected);

/// <summary>Prévia curta de um ativo com este produto instalado (para o card do produto — teto explícito).</summary>
public sealed record SoftwareInstalledAssetPreviewDto(
    Guid AssetId, string AssetName, int Criticality, string? SubType, string? Version, string EffectiveState);

/// <summary>
/// Um produto de software projetado para a lista PRIORIZADA. Fatos de exposição (weaknesses/exploit/alert/impacto)
/// são da FONTE — nunca inferidos pela IA. <see cref="FirstAction"/> é texto determinístico e neutro de provedor.
/// </summary>
public sealed record SoftwareProductListItemDto(
    Guid Id,
    string Vendor,
    string Name,
    int InstalledDeviceCount,
    int OpenInstallationCount,
    int WeaknessesCount,
    bool PublicExploit,
    bool ActiveAlert,
    double? ImpactScore,
    string FirstAction,
    IReadOnlyList<string> Sources,
    IReadOnlyList<SoftwareInstalledAssetPreviewDto> AssetPreview,
    bool AssetPreviewTruncated,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string EffectiveState);

/// <summary>Página de produtos + resumo. <see cref="Total"/> é a contagem FILTRADA (para paginação).</summary>
public sealed record SoftwareInventoryListDto(
    SoftwareInventorySummaryDto Summary,
    IReadOnlyList<SoftwareProductListItemDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Ativos relacionados a UM produto (expansão sob demanda) — paginado, nunca N+1 na abertura padrão.</summary>
public sealed record SoftwareProductAssetsDto(
    IReadOnlyList<SoftwareInstalledAssetPreviewDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Autoridade ÚNICA de leitura do inventário de software do tenant (Global Query Filter fail-closed). Somente
/// leitura; agregação/filtros/ordenação/paginação NO BANCO. Sem tenant, devolve vazio.
/// </summary>
public interface ISoftwareInventoryQuery
{
    /// <summary>Lista PRIORIZADA de produtos + resumo/KPIs.</summary>
    Task<SoftwareInventoryListDto> GetAsync(SoftwareInventoryFilter filter, CancellationToken ct = default);

    /// <summary>Ativos relacionados a UM produto (expansão paginada sob demanda).</summary>
    Task<SoftwareProductAssetsDto> GetAssetsAsync(Guid productId, int page, int pageSize, CancellationToken ct = default);
}
