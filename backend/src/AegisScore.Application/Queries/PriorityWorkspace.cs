using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Application.Queries;

// ---- [AEGIS-MVP-PRIORITIES-01] Central operacional de prioridades: read model COMPOSTO tenant-scoped ----
//
// Reúne numa única leitura o que o usuário precisa para responder "como está minha postura?", "quais
// exposições de configuração merecem atenção?", "quais vulnerabilidades afetam ativos relevantes?" e "o que
// devo analisar/remediar primeiro?". É PURA COMPOSIÇÃO: delega às autoridades de leitura já existentes
// (IWorkspacePostureQuery, IPostureExposureQuery, IVulnerabilityQuery) e NUNCA recalcula score, gap, rank,
// lifecycle, CVSS, EPSS, criticidade ou postura — os valores são preservados exatamente como as queries
// autoritativas os produzem, com a MESMA ordenação determinística.
//
// INVARIANTE METODOLÓGICA: NÃO existe um "score geral de risco" combinando NIST × Secure Score × CVSS ×
// EPSS × criticidade. Essas dimensões têm significados distintos (postura = cobertura/maturidade; exposições
// de configuração = lacunas de fonte operacional; vulnerabilidades = fraquezas observadas em ativos). Por
// isso o contrato mantém DUAS FILAS SEPARADAS (exposições de configuração × vulnerabilidades) e nenhuma
// ordenação matemática única entre itens de filas diferentes.
//
// MULTICLOUD/provider-neutral: o agregado não é nomeado por fornecedor; cada conjunto carrega e mostra a
// própria fonte/provider real (SourceLabel das exposições, Sources das vulnerabilidades). Campos específicos
// de uma fonte permanecem OPCIONAIS (ex.: SourceRank) e não há enum/condicional fechado de Microsoft — o
// contrato NÃO bloqueia futuras fontes (Google Cloud, AWS, on-premise), que NÃO são implementadas aqui.

/// <summary>
/// Uma FILA de exposições de configuração para a Central de Prioridades: o resumo tenant-scoped (autoridade
/// da postura de exposição) + os principais itens ABERTOS. Ambos vêm VERBATIM de <see cref="IPostureExposureQuery"/>
/// (página 1, no máximo <see cref="PriorityWorkspaceDto.MaxQueueItems"/> itens, estado aberto, ordenação da fonte).
/// </summary>
public sealed record PriorityExposureQueueDto(
    PostureExposureSummaryDto Summary,
    IReadOnlyList<PostureExposureItemDto> Top);

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] Uma FILA de vulnerabilidades para a Central de Prioridades — agora por GRUPO/CVE
/// (não mais a mesma ocorrência ativo×CVE repetida em N ativos). Resumo tenant-scoped MULTICLOUD + os principais
/// GRUPOS ABERTOS, VERBATIM de <see cref="IVulnerabilityQuery.GetOverviewAsync"/> (página 1, no máximo
/// <see cref="PriorityWorkspaceDto.MaxQueueItems"/>, ciclo de vida efetivo aberto, ordenação determinística).
/// O ALCANCE de cada item é a quantidade de ativos afetados (<c>AffectedAssetCount</c>); CVE/métricas ficam
/// secundários.
/// </summary>
public sealed record PriorityVulnerabilityQueueDto(
    VulnerabilitySummaryDto Summary,
    IReadOnlyList<VulnerabilityGroupDto> Top);

/// <summary>
/// [AEGIS-MVP-PRODUCT-02] UM achado de identidade na Central — VERBATIM da autoridade KNIGHT. Severidade,
/// resultado, evidência, quantidade afetada e data vêm da avaliação persistida; a Central não recalcula,
/// não reordena por critério próprio e não converte "não avaliado" em nada.
/// </summary>
/// <param name="HasAffectedDetail">
/// TRUE quando a avaliação preservou os objetos que sustentam o achado — é o que autoriza a Central a
/// oferecer o link "ver afetados" em vez de prometer um detalhe que não existe naquela execução.
/// </param>
public sealed record PriorityKnightFindingDto(
    string IndicatorId,
    string Title,
    string Category,
    string Severity,
    string Status,
    string Evidence,
    int AffectedObjectCount,
    bool HasAffectedDetail,
    DateTimeOffset CollectedAt);

/// <summary>
/// [AEGIS-MVP-PRODUCT-02] FILA de achados de identidade do AEGIS KNIGHT — a TERCEIRA fila, e não uma quarta
/// dimensão somada às outras. O score KNIGHT segue com fórmula própria e NÃO é combinado com postura NIST nem
/// com CVSS/EPSS num índice novo: ele aparece aqui apenas identificado como o que é.
///
/// A fila aponta para a MESMA avaliação que a tela do KNIGHT mostra (<see cref="RunId"/>), de modo que os
/// dois lugares não possam divergir; e a origem é sempre explícita (<see cref="IsDemo"/>), para que evidência
/// demonstrativa e corporativa nunca se misturem sem aviso.
/// </summary>
/// <param name="RunId"><c>null</c> quando o tenant ainda não tem nenhuma avaliação — a Central diz isso.</param>
/// <remarks>
/// Categoria, severidade, veredito, fonte e estado viajam como NOME (string), não como ordinal — o mesmo
/// idioma dos demais DTOs de leitura desta Central. A API não tem conversor global de enums; deixar um enum
/// cru aqui faria a tela receber um número e comparar com um nome.
/// </remarks>
public sealed record PriorityKnightQueueDto(
    Guid? RunId,
    bool IsDemo,
    string? SourceLabel,
    string? SourceType,
    string? SourceState,
    DateTimeOffset? CollectedAt,
    double? Score,
    double Coverage,
    int ExposedCount,
    int NotEvaluatedCount,
    IReadOnlyList<PriorityKnightFindingDto> Top);

/// <summary>
/// Read model COMPOSTO da Central de Prioridades. Reúne, SEM combinar num único índice, as três dimensões
/// semanticamente distintas: <see cref="Posture"/> (postura NIST atual, já calculada pelo workspace),
/// <see cref="ConfigurationExposures"/> (fila de exposições de configuração) e
/// <see cref="Vulnerabilities"/> (fila de vulnerabilidades em ativos). A quantidade de ativos afetados e as
/// datas de frescor/coleta NÃO são recalculadas: derivam dos resumos já existentes de cada fila.
/// </summary>
/// <param name="ReadModelVersion">Versão semântica DESTE read model composto (contrato, não score).</param>
/// <param name="GeneratedAt">Instante de geração da leitura (relógio injetável — <c>TimeProvider</c>).</param>
/// <param name="Posture">Postura consolidada atual do tenant (mesma autoridade do Dashboard/Funções).</param>
/// <param name="ConfigurationExposures">Fila de exposições de configuração (resumo + top abertos).</param>
/// <param name="Vulnerabilities">Fila de vulnerabilidades em ativos (resumo + top abertos).</param>
/// <param name="IdentityFindings">Fila de achados de identidade do AEGIS KNIGHT (leitura da avaliação persistida).</param>
public sealed record PriorityWorkspaceDto(
    string ReadModelVersion,
    DateTimeOffset GeneratedAt,
    WorkspaceOverallDto Posture,
    PriorityExposureQueueDto ConfigurationExposures,
    PriorityVulnerabilityQueueDto Vulnerabilities,
    PriorityKnightQueueDto IdentityFindings)
{
    /// <summary>
    /// Versão semântica do contrato composto. [AEGIS-MVP-LANGUAGE-02] o <c>v2</c> trocou ocorrências ativo×CVE
    /// por GRUPOS de CVE/problema. [AEGIS-MVP-PRODUCT-02] o <c>v3</c> acrescenta a fila de achados de
    /// identidade do KNIGHT — uma fila A MAIS, jamais um índice combinado com as outras.
    /// </summary>
    public const string Version = "priority-workspace-v3";

    /// <summary>Teto de itens por fila nesta primeira versão (página 1, somente abertos).</summary>
    public const int MaxQueueItems = 5;
}

/// <summary>
/// Composição de leitura da Central de Prioridades do tenant ambiente — NÃO uma nova autoridade de decisão, e
/// sim a agregação das três autoridades de leitura já existentes. Somente leitura e PURA COMPOSIÇÃO: nunca
/// aciona coleta, nunca aciona IA, nunca altera estado e nunca recalcula valores. O tenant é IMPLÍCITO —
/// herdado por construção das queries tenant-scoped que compõe (ITenantContext + Global Query Filter
/// fail-closed); jamais recebido por parâmetro. Sem tenant, o resultado reflete os vazios fail-closed das
/// queries subjacentes.
/// </summary>
public interface IPriorityWorkspaceQuery
{
    Task<PriorityWorkspaceDto> GetAsync(CancellationToken ct = default);
}
