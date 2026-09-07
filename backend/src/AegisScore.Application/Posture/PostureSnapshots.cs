using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Posture;

// [AEGIS-AUD-035/036/037] Contratos de leitura/escrita da fotografia AUDITÁVEL de postura. A publicação NÃO
// recebe score/cobertura/contagens/vereditos do cliente — o servidor constrói a fotografia exclusivamente
// pelas autoridades atuais do domínio (aegis-score-v1 sobre o ledger; knight-score-v1 sobre o último
// assessment). A comparação só é válida entre fotografias compatíveis; senão, um estado de incompatibilidade.

/// <summary>Versão do schema da fotografia — carimbada em cada publicação para rastrear a evolução do formato.</summary>
public static class PostureSnapshotSchema
{
    public const string Version = "posture-snapshot-v1";
}

/// <summary>Referência de evidência sanitizada de um controle congelado (só metadados; nunca payload/segredo).</summary>
public sealed record PostureEvidenceRefDto(string Kind, string Source, string Reference, DateTimeOffset? CollectedAt);

/// <summary>
/// Um controle NIST congelado numa fotografia AEGIS Score/NIST — do catálogo ATIVO completo. <paramref name="Evaluated"/>
/// distingue "sem score" (não avaliado, <paramref name="Status"/> nulo) de <c>NonCompliant</c>. Pontos obtidos são 0
/// quando não avaliado; o peso elegível (<paramref name="MaxPoints"/>) é sempre preservado.
/// </summary>
public sealed record PostureSnapshotControlDto(
    string SubcategoryCode,
    string FunctionCode,
    bool Evaluated,
    string? Status,
    int AchievedPoints,
    int MaxPoints,
    string? VerdictSource,
    DateTimeOffset? EvaluatedAt,
    IReadOnlyList<PostureEvidenceRefDto> EvidenceRefs);

/// <summary>Um indicador KNIGHT congelado numa fotografia KNIGHT.</summary>
public sealed record PostureSnapshotIndicatorDto(
    string IndicatorId,
    string Title,
    string Category,
    string Severity,
    string Status,
    string Evidence,
    int AffectedObjectCount,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques,
    string SourceType,
    DateTimeOffset CollectedAt);

/// <summary>
/// Resumo de uma fotografia publicada — o suficiente para a lista cronológica e a seleção de comparação, sem
/// carregar os itens. <see cref="EvaluationState"/> separa "sem score" (NotEvaluated) de "0%".
/// </summary>
public sealed record PostureSnapshotSummaryDto(
    Guid Id,
    string Type,
    string SchemaVersion,
    string FormulaVersion,
    string CatalogVersion,
    string SemanticFamily,
    string? SourceType,
    string? SourceLabel,
    DateTimeOffset CapturedAt,
    string EvaluationState,
    double? Score,
    double Coverage,
    int EvaluatedItems,
    int EligibleItems,
    int CompliantCount,
    int NonCompliantCount,
    int MitigatedCount,
    int NotEvaluatedCount,
    int ErrorCount,
    int NotApplicableCount,
    DateTimeOffset? DataRecency,
    string ContentHash,
    /// <summary>
    /// [AEGIS-MVP-PRODUCT-03] Cliente CONGELADO na publicação — o relatório não vai buscar o nome de hoje.
    /// Nulo nas fotografias publicadas antes desta entrega.
    /// </summary>
    string? ClientName = null,
    /// <summary>
    /// [AEGIS-MVP-PRODUCT-03] Avaliação KNIGHT EXATA congelada. Nula em fotografias AEGIS Score/NIST e nas
    /// KNIGHT anteriores a esta entrega — nesse caso a fotografia não sabe dizer qual execução a originou.
    /// </summary>
    Guid? SourceRunId = null);

/// <summary>
/// [AEGIS-MVP-PRODUCT-03] Uma ação CONGELADA na fotografia. Estado do plano, resultado observado no achado e
/// método de validação viajam SEPARADOS — colapsá-los num "resolvido" é exatamente o que o relatório não pode
/// fazer. Enums viajam como NOME.
/// </summary>
public sealed record PostureSnapshotActionItemDto(
    Guid ActionPlanId,
    string IndicatorId,
    string Title,
    string? ProposedAction,
    string? ResponsiblePerson,
    string? ResponsibleArea,
    DateOnly? DueDate,
    string Status,
    bool WasOverdue,
    string NextStep,
    string? ValidationMethod,
    string? ValidationOutcome,
    DateTimeOffset? ValidatedAt,
    int? ObservedBefore,
    int? ObservedAfter,
    bool ComparedBySets,
    string? ValidationRationale);

/// <summary>Detalhe completo de uma fotografia — resumo + agregados crus + itens congelados (controles OU indicadores).</summary>
public sealed record PostureSnapshotDetailDto(
    PostureSnapshotSummaryDto Summary,
    int AchievedPoints,
    int PossiblePoints,
    int EligiblePoints,
    IReadOnlyList<PostureSnapshotControlDto> Controls,
    IReadOnlyList<PostureSnapshotIndicatorDto> Indicators,
    /// <summary>[AEGIS-MVP-PRODUCT-03] Limitações de COLETA congeladas — o que a avaliação não conseguiu ver.</summary>
    IReadOnlyList<string>? CollectionLimitations = null,
    /// <summary>[AEGIS-MVP-PRODUCT-03] Ações congeladas no instante da publicação.</summary>
    IReadOnlyList<PostureSnapshotActionItemDto>? ActionItems = null);

/// <summary>Uma mudança de um item (controle/indicador) entre duas fotografias.</summary>
public sealed record PostureItemChangeDto(string Code, string Title, string PreviousStatus, string CurrentStatus);

/// <summary>Variação das contagens por veredito (atual − anterior).</summary>
public sealed record PostureCountDeltaDto(
    int Compliant, int NonCompliant, int Mitigated, int NotEvaluated, int Error, int NotApplicable);

/// <summary>Estado da variação de score — distingue número de transição de/para "não avaliado".</summary>
public enum ScoreDeltaState
{
    /// <summary>Ambos os lados têm score: <see cref="PostureComparisonDeltaDto.ScoreDelta"/> é a diferença numérica.</summary>
    Numeric = 0,
    /// <summary>O anterior não tinha score e o atual tem — passou a ser avaliado.</summary>
    BecameEvaluated = 1,
    /// <summary>O anterior tinha score e o atual não — deixou de ser avaliado.</summary>
    BecameUnevaluated = 2,
    /// <summary>Nenhum dos lados tem score — sem variação numérica possível.</summary>
    BothUnevaluated = 3,
}

/// <summary>Delta de uma comparação COMPATÍVEL entre a fotografia anterior e a atual (ordenadas por instante).</summary>
public sealed record PostureComparisonDeltaDto(
    double? ScoreDelta,
    string ScoreDeltaState,
    double CoverageDelta,
    PostureCountDeltaDto Counts,
    IReadOnlyList<PostureItemChangeDto> Improved,
    IReadOnlyList<PostureItemChangeDto> Worsened,
    IReadOnlyList<PostureItemChangeDto> NowEvaluated,
    IReadOnlyList<PostureItemChangeDto> NoLongerEvaluated);

/// <summary>
/// Resultado de uma comparação. Quando <see cref="Compatible"/> é falso, <see cref="Delta"/> é nulo e
/// <see cref="IncompatibilityReasons"/> explica por quê — NUNCA se calcula um delta enganoso. Quando compatível,
/// <see cref="Previous"/>/<see cref="Current"/> são as fotografias ordenadas por instante e <see cref="Delta"/> o comparativo.
/// </summary>
public sealed record PostureComparisonResultDto(
    bool Compatible,
    IReadOnlyList<string> IncompatibilityReasons,
    PostureSnapshotSummaryDto? Previous,
    PostureSnapshotSummaryDto? Current,
    PostureComparisonDeltaDto? Delta);

/// <summary>Erro de publicação: não há postura disponível para fotografar (ex.: nenhum assessment KNIGHT ainda).</summary>
public sealed class PostureSnapshotNotAvailableException : Exception
{
    public PostureSnapshotNotAvailableException(string message) : base(message) { }
}

/// <summary>
/// Serviço de aplicação da fotografia AUDITÁVEL de postura — publicação controlada (o servidor constrói pela
/// autoridade do domínio), leitura por tenant e comparação compatível. Todas as operações respeitam o
/// isolamento de tenant (Global Query Filter fail-closed). A fotografia é APPEND-ONLY: sem update/delete.
/// </summary>
public interface IPostureSnapshotService
{
    /// <summary>
    /// Publica uma fotografia da postura ATUAL do tipo indicado.
    ///
    /// Para KNIGHT: com <paramref name="runId"/>, congela EXATAMENTE aquela execução — é o que impede que
    /// publicar a avaliação aberta por link vire, em silêncio, a publicação da mais recente (os dois
    /// resultados vêm de coletas diferentes e não se substituem). Sem <paramref name="runId"/>, o
    /// comportamento existente é preservado: congela o ÚLTIMO assessment do tenant, opcionalmente da
    /// <paramref name="source"/> indicada.
    ///
    /// Lança <see cref="PostureSnapshotNotAvailableException"/> quando não há postura a fotografar — inclusive
    /// quando a execução pedida não existe neste tenant, caso em que NÃO se cai para a mais recente. O cliente
    /// NUNCA fornece score/cobertura/contagens.
    /// </summary>
    Task<PostureSnapshotDetailDto> PublishAsync(
        PostureSnapshotType type, KnightSourceType? source, Guid? runId = null, CancellationToken ct = default);

    /// <summary>Lista as fotografias do tenant (mais recentes primeiro), opcionalmente filtradas por tipo.</summary>
    Task<IReadOnlyList<PostureSnapshotSummaryDto>> ListAsync(PostureSnapshotType? type, CancellationToken ct = default);

    /// <summary>Detalhe de uma fotografia do tenant (<c>null</c> quando inexistente ou de outro tenant).</summary>
    Task<PostureSnapshotDetailDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Compara duas fotografias do tenant. Se alguma não existir, devolve <c>null</c> (o controller responde 404).
    /// Se existirem mas forem incompatíveis, devolve um resultado com <see cref="PostureComparisonResultDto.Compatible"/> falso.
    /// </summary>
    Task<PostureComparisonResultDto?> CompareAsync(Guid baseId, Guid targetId, CancellationToken ct = default);
}
