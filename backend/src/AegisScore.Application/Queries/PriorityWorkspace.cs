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
/// Uma FILA de vulnerabilidades ativo×CVE para a Central de Prioridades: o resumo tenant-scoped MULTICLOUD +
/// as principais exposições ABERTAS. Ambos vêm VERBATIM de <see cref="IVulnerabilityQuery"/> (página 1, no
/// máximo <see cref="PriorityWorkspaceDto.MaxQueueItems"/> itens, ciclo de vida efetivo aberto, ordenação
/// determinística por fatos da fonte + criticidade do ativo).
/// </summary>
public sealed record PriorityVulnerabilityQueueDto(
    VulnerabilitySummaryDto Summary,
    IReadOnlyList<VulnerabilityItemDto> Top);

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
public sealed record PriorityWorkspaceDto(
    string ReadModelVersion,
    DateTimeOffset GeneratedAt,
    WorkspaceOverallDto Posture,
    PriorityExposureQueueDto ConfigurationExposures,
    PriorityVulnerabilityQueueDto Vulnerabilities)
{
    /// <summary>Versão semântica do contrato composto (análoga a <c>aegis-score-v1</c> da fórmula de postura).</summary>
    public const string Version = "priority-workspace-v1";

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
