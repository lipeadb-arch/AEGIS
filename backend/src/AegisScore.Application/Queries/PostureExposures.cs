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

/// <summary>Estado de cobertura da linguagem clara de uma exposição (o catálogo do fornecedor é dinâmico).</summary>
public enum ExposureLanguageCoverage
{
    /// <summary>Há redação autoral (catálogo) para esta exposição.</summary>
    Localized = 0,

    /// <summary>Sem redação autoral — usa o texto de fonte SANITIZADO (fallback honesto), nunca tradução inventada.</summary>
    SourceOnly = 1,
}

/// <summary>
/// Uma exposição de configuração projetada para a tela (sem segredo, sem actionUrl, sem PII). [AEGIS-MVP-LANGUAGE-02]
/// Os campos de TEXTO DA FONTE (<see cref="Title"/>/<see cref="Remediation"/>/<see cref="RemediationImpact"/> e os
/// <c>Source*</c>) já vêm SANITIZADOS (sem HTML/script/href) — conteúdo bruto de conector nunca cruza a fronteira.
/// As telas novas consomem a camada CLARA (<see cref="DisplayTitle"/>/<see cref="PlainSummary"/>/
/// <see cref="WhyItMatters"/>/<see cref="FirstAction"/>); os campos antigos ficam por compatibilidade.
/// </summary>
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
    DateTimeOffset? ResolvedAt)
{
    // ---- [AEGIS-MVP-LANGUAGE-02] Camada CLARA (autoral) + texto de FONTE sanitizado (secundário) ----
    // Aditivos: um cliente antigo continua lendo os campos originais; as telas novas usam estes.

    /// <summary>Título claro (autoral) OU, sem catálogo, o título de fonte sanitizado (SourceOnly). Nunca vazio.</summary>
    public string DisplayTitle { get; init; } = "";

    /// <summary>O que a exposição significa (autoral). Nulo em SourceOnly.</summary>
    public string? PlainSummary { get; init; }

    /// <summary>Por que importa (autoral). Nulo em SourceOnly.</summary>
    public string? WhyItMatters { get; init; }

    /// <summary>Primeira ação (autoral) OU a remediação de fonte sanitizada (fallback). Nulo se nenhuma existir.</summary>
    public string? FirstAction { get; init; }

    /// <summary>Título ORIGINAL da fonte, sanitizado — referência técnica secundária.</summary>
    public string? SourceTitle { get; init; }

    /// <summary>Remediação ORIGINAL da fonte, sanitizada — referência técnica secundária (não bloco bruto).</summary>
    public string? SourceRemediation { get; init; }

    /// <summary>Impacto da remediação ORIGINAL da fonte, sanitizado — referência técnica secundária.</summary>
    public string? SourceRemediationImpact { get; init; }

    /// <summary>Cobertura da linguagem: "Localized" (há redação autoral) ou "SourceOnly" (fallback de fonte).</summary>
    public string LanguageCoverage { get; init; } = nameof(ExposureLanguageCoverage.SourceOnly);
}

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
