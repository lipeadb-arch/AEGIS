using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Remediation;

namespace AegisScore.Application.Nist;

// [AEGIS-NIST-JOURNEY-01] Contratos da jornada do AEGIS NIST: avaliação → escopo → funções → subcategoria → evidência
// e justificativa → atual × alvo → lacunas e resumo. Dois conceitos que NÃO se misturam aqui:
//   • maturidade ATUAL × ALVO por avaliação (escala 1–5 autoral, aegis-methodology-v1) — o que estes contratos gravam;
//   • score de POSTURA do tenant (aegis-score-v1, TenantControlState) — apenas lido, como contexto, nunca escrito.

/// <summary>Estado de uma subcategoria numa avaliação. Ausência de nível é ausência de avaliação, nunca zero.</summary>
public static class NistSubcategoryStates
{
    /// <summary>Sem registro, ou registro sem nível nem "não se aplica" e sem anotação.</summary>
    public const string NotEvaluated = "NotEvaluated";

    /// <summary>Há registro (anotação, alvo ou evidência), mas a situação atual ainda não foi definida.</summary>
    public const string InProgress = "InProgress";

    /// <summary>Situação atual definida por revisão humana (o alvo pode faltar: lacuna indeterminada).</summary>
    public const string Evaluated = "Evaluated";

    /// <summary>Resultado declarado não aplicável ao escopo, com justificativa.</summary>
    public const string NotApplicable = "NotApplicable";
}

public sealed record CreateNistAssessmentCommand(
    string Name, string? Description, DateOnly? StartDate, DateOnly? EndDate, string? InitialScopeName, string? InitialScopeDescription);

public sealed record CreateNistScopeCommand(string Name, string? Description);

/// <summary>Gravação da avaliação humana de uma subcategoria. <paramref name="ExpectedVersion"/> 0 = criação.</summary>
public sealed record SaveNistEvaluationCommand(
    int? CurrentLevel,
    int? TargetLevel,
    bool NotApplicable,
    string? CurrentComments,
    string? TargetComments,
    string? Rationale,
    string? Gaps,
    string? RiskImpact,
    string? ImprovementGuidance,
    string? OwnerName,
    int ExpectedVersion);

/// <summary>
/// Vínculo de evidência. <paramref name="Kind"/>: <c>Manual</c>, <c>GovernanceDocument</c>, <c>KnightIndicator</c> ou
/// <c>AssetInventory</c>. Cada tipo usa só os campos que lhe dizem respeito; o servidor confere a origem.
/// </summary>
public sealed record LinkNistEvidenceCommand(
    string Kind,
    Guid? DocumentId,
    Guid? KnightRunId,
    string? KnightIndicatorId,
    string? Title,
    string? Uri,
    string? Notes,
    DateOnly? CollectedOn,
    string? ManualType);

public sealed record NistScopeView(
    Guid Id,
    string Name,
    string? Description,
    int Subcategories,
    int Evaluated,
    int NotApplicable,
    int InProgress,
    int WithEvidence,
    DateTimeOffset? LastReviewedAt);

public sealed record NistAssessmentView(
    Guid Id,
    string Name,
    string? Description,
    string Status,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string MethodologyVersion,
    string FrameworkName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastReviewedAt,
    IReadOnlyList<NistScopeView> Scopes);

/// <summary>Agregado do perfil (atual, alvo, lacuna anuláveis) e sobre quantas subcategorias foi calculado.</summary>
public sealed record NistProfileScoreView(
    string Code,
    double? Current,
    double? Target,
    double? Gap,
    int Subcategories,
    int WithCurrent,
    int WithTarget,
    int WithGap,
    int NotApplicable,
    int Evaluated);

public sealed record NistSubcategoryRowView(
    string Code,
    string Title,
    string State,
    int? CurrentLevel,
    int? TargetLevel,
    int? Gap,
    string? OwnerName,
    int EvidenceCount,
    DateTimeOffset? ReviewedAt);

public sealed record NistCategoryView(
    string Code,
    string Name,
    string Definition,
    NistProfileScoreView Profile,
    IReadOnlyList<NistSubcategoryRowView> Subcategories);

public sealed record NistFunctionView(
    Guid AssessmentId,
    Guid ScopeId,
    string Code,
    string Name,
    string Definition,
    NistProfileScoreView Profile,
    IReadOnlyList<NistCategoryView> Categories);

/// <summary>Lacuna determinável de uma subcategoria (atual e alvo registrados).</summary>
public sealed record NistGapView(string Code, string Title, int CurrentLevel, int TargetLevel, int Gap, string? OwnerName, string? ImprovementGuidance);

public sealed record NistProfileView(
    Guid AssessmentId,
    Guid ScopeId,
    string MethodologyVersion,
    NistProfileScoreView Overall,
    IReadOnlyList<NistProfileScoreView> Functions,
    IReadOnlyList<NistProfileScoreView> Categories,
    IReadOnlyList<NistGapView> Gaps,
    int IndeterminateGaps);

public sealed record NistEvaluationView(
    Guid Id,
    string State,
    int? CurrentLevel,
    int? TargetLevel,
    int? Gap,
    bool NotApplicable,
    string? CurrentComments,
    string? TargetComments,
    string? Rationale,
    string? Gaps,
    string? RiskImpact,
    string? ImprovementGuidance,
    string? OwnerName,
    string EvaluatedBy,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    int Version);

public sealed record NistEvidenceView(
    Guid Id,
    string OriginKind,
    string Type,
    string Title,
    string? Notes,
    string? Uri,
    string? OriginRef,
    string? OriginLabel,
    string? OriginScope,
    DateTimeOffset CollectedAt,
    DateTimeOffset LinkedAt,
    string? RecordedByName);

/// <summary>
/// Evidência DISPONÍVEL para vincular — sugerida pela origem, nunca vinculada sozinha. <paramref name="Criterion"/> diz
/// por que ela é oferecida (mapeamento explícito do catálogo KNIGHT, trecho literal do documento, inventário…).
/// </summary>
public sealed record NistAvailableEvidenceView(
    string OriginKind,
    string Title,
    string? OriginLabel,
    string? OriginScope,
    DateTimeOffset? CollectedAt,
    string? Status,
    string Criterion,
    string? Limitation,
    Guid? DocumentId,
    Guid? KnightRunId,
    string? KnightIndicatorId,
    bool IsDemo,
    bool AlreadyLinked);

/// <summary>Leitura do score de POSTURA do tenant (aegis-score-v1) para a subcategoria — contexto, não maturidade.</summary>
public sealed record NistPostureReadingView(string Status, string VerdictSource, int AchievedPoints, int MaxPoints, DateTimeOffset LastEvaluatedAt);

public sealed record NistMaturityLevelView(int Level, string Name, string Description);

public sealed record NistSubcategoryDetailView(
    Guid AssessmentId,
    Guid ScopeId,
    string Code,
    string FunctionCode,
    string FunctionName,
    string CategoryCode,
    string CategoryName,
    string Title,
    string Summary,
    string Impact,
    string InitialAction,
    string OfficialOutcome,
    string? ImplementationExamples,
    NistEvaluationView? Evaluation,
    IReadOnlyList<NistEvidenceView> Evidence,
    IReadOnlyList<NistAvailableEvidenceView> AvailableEvidence,
    NistPostureReadingView? Posture,
    IReadOnlyList<NistMaturityLevelView> MaturityScale,
    string MethodologyVersion);

/// <summary>Sugestão da IA — NUNCA gravada como avaliação nem como revisão humana.</summary>
public sealed record NistAiSuggestionView(int SuggestedCurrentLevel, double Confidence, string Rationale, bool Simulated, DateTimeOffset GeneratedAt);

public sealed record NistAssessmentHistoryItem(
    Guid AssessmentId,
    string AssessmentName,
    Guid ScopeId,
    string ScopeName,
    string MethodologyVersion,
    DateOnly ReferenceMonth,
    string ReferenceCriterion,
    DateTimeOffset? LastReviewedAt,
    int Subcategories,
    int Evaluated,
    int NotApplicable,
    double? Current,
    double? Target);

public class NistAssessmentValidationException : Exception
{
    public NistAssessmentValidationException(string message) : base(message) { }
}

public class NistAssessmentNotFoundException : Exception
{
    public NistAssessmentNotFoundException(string message) : base(message) { }
}

public class NistAssessmentConflictException : Exception
{
    public NistAssessmentConflictException(string message) : base(message) { }
}

public class NistAiUnavailableException : Exception
{
    public NistAiUnavailableException(string message) : base(message) { }
}

/// <summary>Porta da jornada NIST. Tenant implícito (filtro global fail-closed); autor sempre do token.</summary>
public interface INistAssessmentService
{
    Task<IReadOnlyList<NistAssessmentView>> ListAsync(CancellationToken ct = default);
    Task<NistAssessmentView> GetAsync(Guid assessmentId, CancellationToken ct = default);
    Task<NistAssessmentView> CreateAsync(CreateNistAssessmentCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistScopeView> AddScopeAsync(Guid assessmentId, CreateNistScopeCommand command, CancellationToken ct = default);
    Task<NistFunctionView> GetFunctionAsync(Guid assessmentId, Guid scopeId, string functionCode, CancellationToken ct = default);
    Task<NistProfileView> GetProfileAsync(Guid assessmentId, Guid scopeId, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> GetSubcategoryAsync(Guid assessmentId, Guid scopeId, string code, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> SaveEvaluationAsync(Guid assessmentId, Guid scopeId, string code, SaveNistEvaluationCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> LinkEvidenceAsync(Guid assessmentId, Guid scopeId, string code, LinkNistEvidenceCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> RemoveEvidenceAsync(Guid assessmentId, Guid scopeId, string code, Guid evidenceId, RemediationActor actor, CancellationToken ct = default);
    Task<NistAiSuggestionView> SuggestAsync(Guid assessmentId, Guid scopeId, string code, CancellationToken ct = default);
    Task<IReadOnlyList<NistAssessmentHistoryItem>> HistoryAsync(CancellationToken ct = default);
}
