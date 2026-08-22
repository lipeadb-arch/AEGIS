using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

// ---- [AEGIS-MVP-POSTURE-02] Leitura tenant-scoped das exposições de configuração (postura) ----

/// <summary>Filtro de estado do ciclo de vida AEGIS para a listagem de exposições.</summary>
public enum PostureExposureStateFilter { All = 0, Open = 1, Resolved = 2 }

/// <summary>
/// Parâmetros de consulta da listagem de exposições. O tenant NÃO trafega aqui — é IMPLÍCITO (claim do JWT +
/// Global Query Filter fail-closed). Página 1-based; <see cref="PageSize"/> é normalizado pela query (piso/teto).
/// </summary>
public sealed record PostureExposureFilter(
    PostureExposureStateFilter State = PostureExposureStateFilter.Open,
    string? Category = null,
    string? Service = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>Uma exposição de configuração projetada para a tela (sem segredo, sem actionUrl, sem PII).</summary>
public sealed record PostureExposureItemDto(
    Guid Id,
    string ExternalId,
    string Title,
    string? Category,
    string? Service,
    string? ActionType,
    double CurrentScore,
    double MaxScore,
    double Gap,
    int? SourceRank,
    string? Tier,
    string? ImplementationCost,
    string? UserImpact,
    string? Remediation,
    string? RemediationImpact,
    IReadOnlyList<string> Threats,
    string? SourceState,
    string LifecycleState,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Contagem de exposições ABERTAS por categoria (distribuição do resumo).</summary>
public sealed record PostureExposureCategoryCountDto(string Category, int Open);

/// <summary>
/// Resumo da postura de exposição do tenant. <see cref="LastCollectedAt"/> é a última coleta observada
/// (null = "Ainda não coletado", NUNCA 0). <see cref="LatestSecureScorePercent"/> é o Secure Score geral
/// mais recente coletado (do sinal <c>secureScore.overall</c>) — null quando ainda não há coleta.
/// </summary>
public sealed record PostureExposureSummaryDto(
    string SourceLabel,
    int TotalOpen,
    int TotalResolved,
    IReadOnlyList<PostureExposureCategoryCountDto> OpenByCategory,
    DateTimeOffset? LastCollectedAt,
    double? LatestSecureScorePercent,
    DateTimeOffset? LatestSecureScoreAt);

/// <summary>Página de exposições + resumo. <see cref="Total"/> é a contagem FILTRADA (para paginação).</summary>
public sealed record PostureExposureListDto(
    PostureExposureSummaryDto Summary,
    IReadOnlyList<PostureExposureItemDto> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Autoridade ÚNICA de leitura das exposições de postura do tenant ambiente (Global Query Filter fail-closed).
/// Somente leitura; nunca cria/altera/resolve finding (isso é do pipeline de coleta). Sem tenant, devolve vazio.
/// </summary>
public interface IPostureExposureQuery
{
    Task<PostureExposureListDto> GetAsync(PostureExposureFilter filter, CancellationToken ct = default);
}
