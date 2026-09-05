using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity;

namespace AegisScore.Application.Queries;

// ---- [AEGIS-MVP-PRODUCT-01] Read model COMPOSTO da tela inicial (Visão geral) ----
//
// A tela inicial precisa responder cinco perguntas SEPARADAS — "o que já foi observado?", "o que merece
// atenção?", "quanto foi efetivamente avaliado?", "quais fontes estão incompletas/antigas?" e "por onde
// seguir?". O contrato existia partido em dois fluxos que se contradiziam: /dashboard/executive (maturidade
// CMMI + registro de riscos + ICR) e /scoring/workspace (postura determinística por controle). Um tenant com
// telemetria real e SEM maturidade legada aparecia como "nenhuma postura medida".
//
// Este read model NÃO cria um score novo e NÃO combina KNIGHT × NIST × CVSS × maturidade. Ele PARTICIONA a
// tela em dimensões independentes, cada uma com estado PRÓPRIO (<see cref="DashboardSignalState"/>): a
// ausência de maturidade não apaga ativos/vulnerabilidades/exposições/identidade, e a ausência de telemetria
// não finge maturidade. É PURA COMPOSIÇÃO das autoridades de leitura já existentes + contagens baratas.
//
// Honestidade que atravessa o contrato: valor NULO quando a dimensão nunca foi coletada — nunca zero. "0
// vulnerabilidades abertas" só existe depois de uma coleta real; falha de fonte é <c>Partial</c>, jamais zero.

/// <summary>
/// Estado de UMA dimensão da tela inicial — a distinção que decide se um painel pode aparecer com números.
/// Cada painel exige a própria evidência: nenhum deles herda o estado de outro.
/// </summary>
public enum DashboardSignalState
{
    /// <summary>Não há fonte capaz de produzir essa dimensão (nenhum conector/registro aplicável).</summary>
    NoSource = 0,

    /// <summary>Existe fonte, mas nada foi coletado/avaliado ainda. Valor NULO — nunca zero.</summary>
    NeverCollected = 1,

    /// <summary>Há dado, porém a fonte está degradada, parcial ou desatualizada — o número é uma leitura incompleta.</summary>
    Partial = 2,

    /// <summary>Há dado válido e a fonte respondeu por inteiro na última coleta.</summary>
    Available = 3,
}

/// <summary>
/// Uma métrica da tela inicial com a PROVENIÊNCIA junto: quem produziu o número e quando ele foi observado.
/// <see cref="Value"/> é NULO sempre que <see cref="State"/> for <see cref="DashboardSignalState.NoSource"/>
/// ou <see cref="DashboardSignalState.NeverCollected"/> — o front nunca precisa adivinhar se zero é leitura.
/// </summary>
/// <param name="State">Estado próprio desta métrica.</param>
/// <param name="Value">Contagem observada, ou <c>null</c> quando não houve leitura.</param>
/// <param name="SourceLabel">Origem legível do número (a tela identifica a fonte de cada métrica).</param>
/// <param name="ObservedAt">Instante da observação na fonte (freshness real), quando conhecido.</param>
/// <param name="Note">Explicação curta do estado quando ele não é <see cref="DashboardSignalState.Available"/>.</param>
public sealed record DashboardMetricDto(
    DashboardSignalState State,
    long? Value,
    string SourceLabel,
    DateTimeOffset? ObservedAt = null,
    string? Note = null);

/// <summary>
/// O que já foi OBSERVADO no ambiente — a primeira pergunta da tela. Cada métrica tem estado próprio: um
/// inventário de ativos coletado continua visível mesmo sem exposições, sem vulnerabilidades e sem maturidade.
/// </summary>
public sealed record DashboardEnvironmentDto(
    DashboardMetricDto Assets,
    DashboardMetricDto ConfigurationExposures,
    DashboardMetricDto Vulnerabilities,
    DashboardMetricDto AffectedAssets,
    DashboardMetricDto Identity);

/// <summary>
/// Risco de NEGÓCIO — maturidade CMMI, registro de riscos e ICR. Dimensão DELIBERADAMENTE separada da postura
/// por controle: ela vem de avaliação assistida (assessment/entrevista), não de telemetria. Enquanto não
/// existir avaliação, o estado é <see cref="DashboardSignalState.NeverCollected"/> e os valores são NULOS —
/// e isso NÃO pode esconder as dimensões de ambiente acima.
/// </summary>
/// <param name="MaturityState">Estado da avaliação de maturidade (há ao menos UMA subcategoria avaliada?).</param>
/// <param name="OverallMaturity">Maturidade geral apurada pelo rollup do servidor; <c>null</c> sem avaliação.</param>
/// <param name="TargetMaturity">Alvo apurado pelo mesmo rollup; <c>null</c> sem avaliação.</param>
/// <param name="EvaluatedSubcategories">Quantas subcategorias têm avaliação de maturidade registrada.</param>
/// <param name="IcrState">Estado do Índice de Criticidade de Risco (medições persistidas?).</param>
/// <param name="IcrScore">Média dos ICRs realmente persistidos; <c>null</c> sem medição (nunca fabricado).</param>
/// <param name="IcrBand">Banda do ICR quando ele existe.</param>
/// <param name="RiskRegisterState">Estado do registro de riscos de negócio.</param>
/// <param name="RisksEvaluated">Riscos com ao menos uma avaliação registrada.</param>
/// <param name="CriticalProcessesExposed">Processos DISTINTOS com risco Alto/Crítico; <c>null</c> sem registro.</param>
/// <param name="OverdueActionPlans">Planos de ação vencidos; <c>null</c> sem registro.</param>
public sealed record DashboardBusinessRiskDto(
    DashboardSignalState MaturityState,
    double? OverallMaturity,
    double? TargetMaturity,
    int EvaluatedSubcategories,
    DashboardSignalState IcrState,
    double? IcrScore,
    string? IcrBand,
    DashboardSignalState RiskRegisterState,
    int RisksEvaluated,
    long? CriticalProcessesExposed,
    long? OverdueActionPlans);

/// <summary>
/// Postura de IDENTIDADE projetada para a tela inicial, a partir do MESMO snapshot consultivo da Evidence
/// Fabric (sem nova aquisição, sem Graph). As capacidades indisponíveis são nomeadas para que uma coleta
/// PARCIAL (permissão ausente) apareça como "o que continua disponível", nunca como integração sem dados.
/// </summary>
/// <param name="State">Estado desta dimensão, derivado do estado de coleta do snapshot.</param>
/// <param name="CollectionState">Estado de coleta bruto da Evidence Fabric (preservado, sem reinterpretação).</param>
/// <param name="SourceLabel">Fonte real do snapshot.</param>
/// <param name="CollectedAt">Instante da coleta armazenada.</param>
/// <param name="IsDegraded">A última tentativa degradou (a evidência anterior é preservada).</param>
/// <param name="CapabilitiesCollected">Capacidades que a fonte entregou nesta coleta.</param>
/// <param name="CapabilitiesMissing">Capacidades que faltaram — com o motivo real (permissão, licença, falha).</param>
/// <param name="ControlsAwaitingEvidence">Controles NIST de identidade com telemetria coletada porém insuficiente.</param>
public sealed record DashboardIdentityDto(
    DashboardSignalState State,
    IdentityEvidenceCollectionState CollectionState,
    string SourceLabel,
    DateTimeOffset? CollectedAt,
    bool IsDegraded,
    IReadOnlyList<string> CapabilitiesCollected,
    IReadOnlyList<DashboardIdentityGapDto> CapabilitiesMissing,
    int ControlsAwaitingEvidence);

/// <summary>Uma capacidade de identidade que a fonte NÃO entregou, com o motivo real (nunca "sem dados").</summary>
public sealed record DashboardIdentityGapDto(string Capability, string Outcome, string? Detail);

/// <summary>
/// Uma FONTE do ambiente na leitura de saúde da tela inicial. Reprojeta a saúde de conectores que a projeção
/// do workspace já apura, acrescentando apenas <see cref="StaleDays"/> (dias desde a última sincronização) —
/// derivado do relógio injetável, não recalculado a partir de outra autoridade.
/// </summary>
public sealed record DashboardSourceDto(
    string Id,
    string DisplayName,
    string Provider,
    string Capability,
    string Status,
    bool Enabled,
    bool EverSynced,
    DateTimeOffset? LastSyncAt,
    int? StaleDays);

/// <summary>
/// Saúde consolidada das fontes: os contadores VERBATIM da projeção do workspace + a lista reprojetada, já
/// ordenada por gravidade (o que exige atenção primeiro). <see cref="Attention"/> conta as fontes habilitadas
/// que não estão saudáveis OU nunca sincronizaram — o número que a tela mostra como "fontes a verificar".
/// </summary>
public sealed record DashboardSourcesDto(
    int Configured,
    int Enabled,
    int Disabled,
    int Healthy,
    int Degraded,
    int Failed,
    int NeverSynced,
    int Attention,
    DateTimeOffset? LastSyncAt,
    IReadOnlyList<DashboardSourceDto> Items);

/// <summary>
/// Read model COMPOSTO da tela inicial. Cada bloco carrega o próprio estado e a própria origem; nada aqui é
/// recalculado e nenhuma dimensão é combinada com outra num índice único.
/// </summary>
/// <param name="ReadModelVersion">Versão semântica DESTE contrato (não é versão de fórmula de score).</param>
/// <param name="GeneratedAt">Instante da leitura (relógio injetável).</param>
/// <param name="ClientName">Nome do ambiente/cliente ativo.</param>
/// <param name="Posture">Postura determinística consolidada — MESMA autoridade do /scoring/workspace.</param>
/// <param name="EvidenceCoverage">Cobertura por natureza da prova esperada (telemetria × governança × híbrida × orientada).</param>
/// <param name="Environment">O que já foi observado no ambiente, métrica a métrica.</param>
/// <param name="BusinessRisk">Maturidade, registro de riscos e ICR — dimensão separada, com estado próprio.</param>
/// <param name="ConfigurationExposures">Fila curta de exposições de configuração abertas (autoridade das exposições).</param>
/// <param name="Vulnerabilities">Fila curta de vulnerabilidades abertas por CVE (autoridade das vulnerabilidades).</param>
/// <param name="Identity">Postura consultiva de identidade do último snapshot da Evidence Fabric.</param>
/// <param name="Sources">Saúde e recência das fontes conectadas.</param>
public sealed record DashboardOverviewDto(
    string ReadModelVersion,
    DateTimeOffset GeneratedAt,
    string ClientName,
    WorkspaceOverallDto Posture,
    EvidenceCoverageSummaryDto EvidenceCoverage,
    DashboardEnvironmentDto Environment,
    DashboardBusinessRiskDto BusinessRisk,
    PriorityExposureQueueDto ConfigurationExposures,
    PriorityVulnerabilityQueueDto Vulnerabilities,
    DashboardIdentityDto Identity,
    DashboardSourcesDto Sources)
{
    /// <summary>Versão semântica do contrato composto da tela inicial.</summary>
    public const string Version = "dashboard-overview-v1";

    /// <summary>Teto de itens por fila na tela inicial — a Central de Prioridades é a lista completa.</summary>
    public const int MaxQueueItems = 4;

    /// <summary>
    /// A partir de quantos dias sem sincronizar uma fonte habilitada é tratada como DESATUALIZADA. É um limiar
    /// de APRESENTAÇÃO (a tela avisa "leitura antiga"), não um veredito de conformidade — nada de score muda.
    /// </summary>
    public const int StaleAfterDays = 7;
}

/// <summary>
/// Leitura COMPOSTA da tela inicial do tenant ambiente. NÃO é uma nova autoridade: delega às queries de
/// leitura já existentes e a contagens agregadas baratas (COUNT no banco — nunca materializa inventário).
/// Somente leitura: jamais aciona coleta externa, IA ou escrita. Tenant IMPLÍCITO por construção
/// (ITenantContext + Global Query Filter fail-closed).
/// </summary>
public interface IDashboardOverviewQuery
{
    Task<DashboardOverviewDto> GetAsync(CancellationToken ct = default);
}
