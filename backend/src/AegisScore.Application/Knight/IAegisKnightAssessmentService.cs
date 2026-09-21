using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>Um indicador de uma execução KNIGHT, na visão de leitura da aplicação (nunca a entidade crua).</summary>
public sealed record KnightIndicatorView(
    string IndicatorId,
    string Title,
    KnightIndicatorCategory Category,
    SeverityLevel Severity,
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques,
    string Recommendation,
    DateTimeOffset CollectedAt,
    KnightSourceType SourceType,
    string? NotEvaluatedReason,
    /// <summary>
    /// [AEGIS-MVP-PRODUCT-02] TRUE quando ESTA execução preservou os objetos que sustentam o veredito. FALSE
    /// numa execução anterior à preservação — que permanece válida e NÃO é retropreenchida com dados atuais.
    /// </summary>
    bool HasAffectedDetail = false,
    /// <summary>TRUE quando a lista preservada cobre todo o conjunto que produziu a contagem.</summary>
    bool AffectedDetailComplete = false,
    /// <summary>O que a coleta não conseguiu enumerar no detalhe (sanitizado), quando aplicável.</summary>
    string? AffectedDetailLimitation = null,
    /// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Objetos de configuração que sustentaram o veredito (não contados como afetados).</summary>
    int EvidenceObjectCount = 0,
    /// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Perfil, eixos (domínio/serviço/provedor) e contribuição para a nota.</summary>
    KnightControlPresentation? Presentation = null,
    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Composição NOMEADA dos afetados preservados ("12 contas de usuário e 2 aplicações"),
    /// pela mesma definição das exportações. Nula quando nada foi afetado.
    /// </summary>
    string? AffectedComposition = null);

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Como um controle se apresenta no relatório: o problema, por que importa, a
/// configuração esperada, o que o resultado não comprova, os eixos (domínio × serviço × provedor), as
/// referências e a CONTRIBUIÇÃO para a nota KNIGHT pela fórmula oficial (peso × fator do veredito). O perfil é o
/// do catálogo corrente; <see cref="Criterion"/> só aparece quando a avaliação usou esse mesmo catálogo.
/// </summary>
public sealed record KnightControlPresentation(
    string Domain,
    string DomainLabel,
    string Service,
    string Provider,
    string? Description,
    string? Rationale,
    string? ExpectedConfiguration,
    string? DoesNotProve,
    string? Criterion,
    IReadOnlyList<KnightControlReference> References,
    IReadOnlyList<string> RequiredCapabilities,
    int Weight,
    double? Factor,
    double? AchievedPoints,
    double? PossiblePoints,
    string? Impact = null,
    string? Platform = null,
    string? ServiceKey = null);

/// <summary>Um objeto afetado em vários controles de uma avaliação — a base dos "principais objetos afetados".</summary>
public sealed record KnightAffectedSummaryItem(
    string ExternalId,
    KnightAffectedObjectKind Kind,
    string? DisplayName,
    string? UserPrincipalName,
    int ControlCount,
    IReadOnlyList<string> IndicatorIds);

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Objetos afetados de uma avaliação, contados em TRÊS unidades que não se
/// confundem: ocorrências (objeto × controle), objetos ÚNICOS e controles. <see cref="Complete"/> é falso
/// quando algum controle exposto não preservou o detalhe completo — o número de únicos vira um piso.
/// </summary>
public sealed record KnightAffectedSummary(
    Guid RunId,
    int ExposedControls,
    int Occurrences,
    int UniqueObjects,
    bool Complete,
    IReadOnlyList<string> IncompleteIndicatorIds,
    IReadOnlyList<KnightAffectedSummaryItem> Top);

/// <summary>
/// Um assessment KNIGHT completo, na visão de leitura da aplicação: a execução, a FONTE e seu estado, os
/// indicadores, o estado por capacidade e o resumo consultivo (com procedência — IA ou fallback). O score é
/// ANULÁVEL e é o score KNIGHT, distinto do AEGIS Score.
/// </summary>
public sealed record KnightAssessment(
    Guid Id,
    KnightAssessmentMode Mode,
    KnightSourceType SourceType,
    KnightSourceState SourceState,
    string Source,
    KnightRunStatus Status,
    string CatalogVersion,
    string ScoreFormulaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double? Score,
    double Coverage,
    int PassedCount,
    int ExposedCount,
    int MitigatedCount,
    int NotEvaluatedCount,
    int ErrorCount,
    int NotApplicableCount,
    IReadOnlyList<KnightIndicatorView> Indicators,
    IReadOnlyList<KnightCapabilityStatus> Capabilities,
    KnightAdvisory? Advisory,
    bool AdvisoryFromAi);

/// <summary>
/// [AEGIS-KNIGHT-DURABLE-01] Uma execução que NÃO concluiu — o cabeçalho dela, nunca um resultado. Não
/// carrega score, cobertura, indicadores nem narrativa porque não tem veredito fechado a oferecer: é a
/// EXISTÊNCIA da tentativa que é a informação.
/// </summary>
public sealed record KnightUnfinishedRun(
    Guid Id,
    KnightRunStatus Status,
    KnightSourceType SourceType,
    KnightAssessmentMode Mode,
    DateTimeOffset StartedAt);

/// <summary>
/// [AEGIS-KNIGHT-DURABLE-01] A leitura da "última avaliação": o último resultado CONCLUÍDO e, à parte, a
/// tentativa mais recente que não concluiu (quando começou DEPOIS dele). São coisas diferentes e aparecem
/// separadas — uma execução não concluída nunca é apresentada como resultado, e a existência dela nunca é
/// omitida por uma avaliação antiga exibida como se fosse a atual.
/// </summary>
/// <param name="Assessment">O último assessment CONCLUÍDO, ou <c>null</c> quando ainda não há nenhum.</param>
/// <param name="UnfinishedAttempt">A tentativa não concluída que o sucede, ou <c>null</c>.</param>
public sealed record KnightLatestAssessment(
    KnightAssessment? Assessment,
    KnightUnfinishedRun? UnfinishedAttempt);

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] A última avaliação de UMA fonte, com a fonte identificada. É o bloco da leitura
/// COMPOSTA: o KNIGHT deixou de ter uma "última avaliação" única quando passou a avaliar mais de uma fonte.
/// Apresentar só a mais recente faria a avaliação do Microsoft Entra ID desaparecer da tela assim que uma
/// sincronização do Microsoft Teams terminasse — e o contrário também.
/// </summary>
public sealed record KnightSourceLatest(
    KnightSourceType Source,
    string Label,
    KnightAssessment? Assessment,
    KnightUnfinishedRun? UnfinishedAttempt);

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] A fotografia CORRENTE do assessment: a última avaliação concluída de CADA fonte
/// que já produziu alguma. Não há soma de notas entre fontes — cada fonte tem a própria nota, a própria
/// cobertura e a própria data —, e a tela é obrigada a dizer de qual aquisição veio cada número.
/// </summary>
public sealed record KnightLatestBySource(IReadOnlyList<KnightSourceLatest> Sources);

/// <summary>Disponibilidade das fontes para o tenant — o que a UI usa para oferecer Demo × execução real.</summary>
public sealed record KnightSourceInfo(KnightSourceType Source, string Label, bool Configured, bool Enabled);

/// <summary>Estado das fontes KNIGHT do tenant: Demo sempre disponível; fontes reais conforme configuração.</summary>
public sealed record KnightSourcesStatus(bool DemoAvailable, IReadOnlyList<KnightSourceInfo> RealSources);

/// <summary>Tentativa de executar uma fonte REAL sem configuração aplicável — o serviço NÃO cai para Demo.</summary>
public sealed class KnightSourceNotConfiguredException : Exception
{
    public KnightSourceType SourceType { get; }
    public KnightSourceNotConfiguredException(KnightSourceType source)
        : base($"Fonte {source} não está configurada para este tenant.") => SourceType = source;
}

/// <summary>
/// Serviço de aplicação DEDICADO do AEGIS KNIGHT — MULTICOLETOR. Orquestra a execução persistida: resolve a
/// configuração da fonte, coleta pelo coletor da fonte, avalia deterministicamente os fatos normalizados,
/// calcula score/cobertura, persiste (com fonte/estado), gera o resumo consultivo (com fallback) e lê por
/// tenant. Todas as operações respeitam o isolamento de tenant (Global Query Filter fail-closed).
/// </summary>
public interface IAegisKnightAssessmentService
{
    /// <summary>Executa um assessment de DEMONSTRAÇÃO (fonte Demo, sintética) ponta a ponta.</summary>
    Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default);

    /// <summary>
    /// Executa um assessment da FONTE indicada. Para fontes reais, exige configuração aplicável: se ausente,
    /// lança <see cref="KnightSourceNotConfiguredException"/> (NUNCA cai para Demo). Uma falha real da coleta
    /// (permissão/indisponibilidade) é persistida com o estado real, sem substituição por dados sintéticos.
    /// </summary>
    Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default);

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] A mesma avaliação, pedida por uma sincronização de Integrações. Idempotente
    /// POR PEDIDO: se o pedido já tem avaliação vinculada, devolve-a sem coletar; senão coleta, avalia e grava a
    /// execução e o vínculo com o pedido NUMA transação curta, guardada pelo lease — quem perdeu o lease não
    /// grava resultado. A narrativa da IA continua sendo enriquecimento posterior, fora da transação.
    /// </summary>
    Task<KnightSyncRunResult> RunForSyncRequestAsync(KnightSourceType source, KnightSyncBinding binding, CancellationToken ct = default);

    /// <summary>
    /// [AEGIS-KNIGHT-DURABLE-01] Último assessment CONCLUÍDO do tenant do contexto e, separadamente, a
    /// tentativa não concluída que o sucede. Nunca devolve uma execução em andamento/abandonada no lugar
    /// do resultado, nem esconde que ela existe.
    /// </summary>
    Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default);

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] A última avaliação concluída de CADA fonte que já produziu alguma, com a
    /// tentativa não finalizada que a sucede, quando houver. Somente leitura: não dispara coleta.
    /// </summary>
    Task<KnightLatestBySource> GetLatestBySourceAsync(CancellationToken ct = default);

    /// <summary>Assessment por Id, restrito ao tenant do contexto (<c>null</c> quando inexistente ou de outro tenant).</summary>
    Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Disponibilidade das fontes para o tenant (Demo sempre; reais conforme configuração).</summary>
    Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-02] Objetos que sustentam UM achado de UMA execução, paginados e pesquisados NO
    /// SERVIDOR. Restrito ao tenant do contexto: uma execução de outro tenant é indistinguível de inexistente
    /// (<c>null</c>). A lista é sempre a da execução pedida — nunca a coleta atual reapresentada como prova de
    /// um veredito antigo. Somente leitura: NÃO dispara coleta na fonte.
    /// </summary>
    /// <returns><c>null</c> quando a execução ou o achado não existem neste tenant.</returns>
    Task<KnightAffectedObjectsPage?> GetAffectedObjectsAsync(
        Guid runId, string indicatorId, int page, int pageSize, string? search, CancellationToken ct = default,
        KnightObjectRelation relation = KnightObjectRelation.Affected);

    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] Resumo dos objetos afetados da avaliação: ocorrências, únicos e os que mais
    /// se repetem entre controles expostos. Somente leitura; <c>null</c> quando a avaliação não existe no tenant.
    /// </summary>
    Task<KnightAffectedSummary?> GetAffectedSummaryAsync(Guid runId, CancellationToken ct = default);
}
