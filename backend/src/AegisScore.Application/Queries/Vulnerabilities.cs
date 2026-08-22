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
    int PageSize = 25);

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

/// <summary>
/// Autoridade ÚNICA de leitura das vulnerabilidades do tenant ambiente (Global Query Filter fail-closed). Somente
/// leitura; nunca cria/altera/resolve exposição/observação. Filtros/agregações/ordenação/paginação NO BANCO —
/// carrega observações/produtos só para os IDs da página. Sem tenant, devolve vazio.
/// </summary>
public interface IVulnerabilityQuery
{
    Task<VulnerabilityListDto> GetAsync(VulnerabilityFilter filter, CancellationToken ct = default);
}
