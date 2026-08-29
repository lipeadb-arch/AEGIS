using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

// ---- [AEGIS-MVP-VULN-01] Leitura tenant-scoped, MULTICLOUD, de vulnerabilidades associadas a ativos (ativo × CVE) ----
// Superfície DEDICADA — NÃO se mistura com as exposições de CONFIGURAÇÃO (postura/Secure Score), em /posture/exposures.
// A UNIDADE da lista é a EXPOSIÇÃO CONSOLIDADA ativo×CVE; as FONTES que a observam (Defender, Google, AWS…) aparecem
// como observações. Nenhum campo de provedor específico vaza para o contrato público — só "provider/displayName".

/// <summary>Filtro de estado do ciclo de vida EFETIVO da exposição (agregado das observações de fonte).</summary>
public enum VulnerabilityLifecycleFilter { All = 0, Open = 1, Resolved = 2 }

/// <summary>Filtro por disponibilidade de exploit (fato da fonte) — nunca "exploração ativa", que não afirmamos.</summary>
public enum VulnerabilityExploitFilter { All = 0, Exploitable = 1, Verified = 2 }

/// <summary>
/// Parâmetros da listagem. O tenant NÃO trafega — é IMPLÍCITO (JWT + Global Query Filter fail-closed). Filtros por
/// ciclo de vida efetivo, severidade, exploit, ativo, FONTE (provider e/ou conector) e busca. Página 1-based.
/// </summary>
public sealed record VulnerabilityFilter(
    VulnerabilityLifecycleFilter State = VulnerabilityLifecycleFilter.Open,
    string? Severity = null,
    VulnerabilityExploitFilter Exploit = VulnerabilityExploitFilter.All,
    Guid? AssetId = null,
    string? Provider = null,
    Guid? ConnectorId = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 25,
    // [AEGIS-MVP-LANGUAGE-02] Filtro EXATO por CVE (case-insensitive, sem Contains) — carrega as ocorrências
    // ativo×CVE de UM grupo quando o usuário expande. Distinto de Search (que casa por prefixo/substring).
    string? CveId = null);

/// <summary>Produto/versão AFETADO por um CVE num ativo (detalhe normalizado por fonte — nunca a resposta bruta).</summary>
public sealed record VulnerabilityProductDto(string? Product, string? Vendor, string? Version, string? FixingKb);

/// <summary>
/// Observação de UMA fonte sobre a exposição (a "quem viu isto"): conector/provider, ciclo de vida DAQUELA fonte,
/// instantes e produtos permitidos. Nunca machineId, IP, aadDeviceId, segredo ou payload bruto.
/// </summary>
public sealed record VulnerabilityObservationDto(
    Guid ConnectorConfigId,
    string Provider,
    string DisplayName,
    string LifecycleState,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<VulnerabilityProductDto> Products,
    int TotalProducts,
    bool ProductsTruncated);

/// <summary>
/// Uma vulnerabilidade (exposição CONSOLIDADA ativo×CVE) projetada para a tela. Fatos do CVE (severidade/CVSS/
/// exploit/EPSS) são da FONTE. O ciclo de vida EFETIVO é Open se QUALQUER fonte estiver Open. Só campos permitidos.
/// </summary>
public sealed record VulnerabilityItemDto(
    Guid Id,
    string CveId,
    string? CveTitle,
    string? Severity,
    double? CvssScore,
    string? CvssVector,
    bool? PublicExploit,
    bool? ExploitVerified,
    double? Epss,
    DateTimeOffset? PublishedOn,
    Guid AssetId,
    string AssetName,
    int AssetCriticality,
    string? AssetSubType,
    string EffectiveLifecycle,
    string Status,
    DateTimeOffset DetectedAt,
    IReadOnlyList<VulnerabilityObservationDto> Sources);

/// <summary>Contagem de vulnerabilidades ABERTAS por severidade textual (distribuição do resumo).</summary>
public sealed record VulnerabilitySeverityCountDto(string Severity, int Open);

/// <summary>Uma FONTE (conector de scanner) configurada no tenant, com a última sincronização e a saúde.</summary>
public sealed record VulnerabilitySourceDto(
    Guid ConnectorConfigId,
    string Provider,
    string DisplayName,
    DateTimeOffset? LastSyncAt,
    string Status);

/// <summary>
/// Resumo tenant-scoped MULTICLOUD. <see cref="NeverCollected"/> distingue "ainda não coletado" (nenhum conector de
/// scanner do tenant já sincronizou) de "coletado sem achados". Contagens DISTINTAS de exposições, CVEs e ativos
/// afetados (abertos) — nunca duplicadas por múltiplas fontes observarem a mesma exposição.
/// </summary>
public sealed record VulnerabilitySummaryDto(
    int TotalOpen,
    int TotalResolved,
    int DistinctCvesOpen,
    int AffectedAssetsOpen,
    IReadOnlyList<VulnerabilitySeverityCountDto> OpenBySeverity,
    IReadOnlyList<VulnerabilitySourceDto> Sources,
    DateTimeOffset? LastCollectedAt,
    bool NeverCollected);

/// <summary>Página de vulnerabilidades + resumo. <see cref="Total"/> é a contagem FILTRADA (para paginação).</summary>
public sealed record VulnerabilityListDto(
    VulnerabilitySummaryDto Summary,
    IReadOnlyList<VulnerabilityItemDto> Items,
    int Total,
    int Page,
    int PageSize);

// ---- [AEGIS-MVP-LANGUAGE-02] Visão AGRUPADA por CVE/problema — a leitura PADRÃO da tela e da fila de prioridades ----
// A unidade passa a ser o PROBLEMA (CVE), não a ocorrência ativo×CVE: ~11k CVEs em vez de 334k linhas. Agregação,
// filtros, ordenação e paginação acontecem NO BANCO; detalhes por ativo carregam sob demanda (filtro EXATO por CVE).

/// <summary>Prévia curta de um ativo afetado por um CVE (para o card do grupo — teto explícito).</summary>
public sealed record VulnerabilityAssetPreviewDto(string AssetName, int Criticality, string? SubType);

/// <summary>
/// Um GRUPO de vulnerabilidade (um CVE observado em N ativos), projetado para a tela. FATOS DA FONTE
/// (severidade/CVSS/EPSS/exploit) são do CVE; contagem de ativos, criticidade máxima e ciclo de vida efetivo são
/// agregados do grupo. <see cref="SourceTitle"/> é o título ORIGINAL sanitizado (nulo quando vazio ou igual ao CVE).
/// <see cref="ProductLabel"/> só é preenchido quando TODOS os ativos afetados compartilham um mesmo subtipo (produto
/// confiável); do contrário nulo (o front cai em "ativos do ambiente"). A linguagem clara do título é DERIVADA
/// deterministicamente no cliente a partir de severidade + produto — não afirmamos exploração ativa em lugar algum.
/// </summary>
public sealed record VulnerabilityGroupDto(
    string CveId,
    string? Severity,
    double? CvssScore,
    string? CvssVector,
    double? Epss,
    bool PublicExploit,
    bool ExploitVerified,
    DateTimeOffset? PublishedOn,
    string? SourceTitle,
    string? ProductLabel,
    int AffectedAssetCount,
    int MaxAssetCriticality,
    IReadOnlyList<VulnerabilityAssetPreviewDto> AssetPreview,
    bool AssetPreviewTruncated,
    IReadOnlyList<string> Providers,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string EffectiveLifecycle);

/// <summary>
/// Página da visão AGRUPADA + resumo tenant-scoped. <see cref="Total"/> é a contagem de GRUPOS/CVEs distintos
/// (filtrada) — a paginação da tela principal é por PROBLEMA, nunca por ocorrência ativo×CVE.
/// </summary>
public sealed record VulnerabilityOverviewDto(
    VulnerabilitySummaryDto Summary,
    IReadOnlyList<VulnerabilityGroupDto> Groups,
    int Total,
    int Page,
    int PageSize);

/// <summary>
/// Autoridade ÚNICA de leitura das vulnerabilidades do tenant ambiente (Global Query Filter fail-closed). Somente
/// leitura; nunca cria/altera/resolve exposição/observação. Filtros/agregações/ordenação/paginação NO BANCO —
/// carrega observações/produtos só para os IDs da página. Sem tenant, devolve vazio.
/// </summary>
public interface IVulnerabilityQuery
{
    /// <summary>Ocorrências ativo×CVE (detalhe/compatibilidade). Aceita filtro EXATO por CVE para expandir um grupo.</summary>
    Task<VulnerabilityListDto> GetAsync(VulnerabilityFilter filter, CancellationToken ct = default);

    /// <summary>[AEGIS-MVP-LANGUAGE-02] Visão AGRUPADA por CVE/problema — leitura PADRÃO da tela e da fila de prioridades.</summary>
    Task<VulnerabilityOverviewDto> GetOverviewAsync(VulnerabilityFilter filter, CancellationToken ct = default);
}
