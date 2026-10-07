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
//
// [AEGIS-NIST-JOURNEY-02] A jornada ganha RODADAS (toda leitura e gravação nomeia avaliação · rodada · escopo), conteúdo
// herdado/importado distinto de avaliação confirmada, responsáveis vinculados a usuários do tenant, avaliador e revisor,
// decisão do revisor, trilha de alterações com valores anteriores e novos. Procedimentos, achados, planos, publicação e
// importação CSV têm contratos próprios (NistWork.cs, NistPublication.cs, NistImport.cs).

/// <summary>Estado de uma subcategoria numa rodada. Ausência de nível é ausência de avaliação, nunca zero.</summary>
public static class NistSubcategoryStates
{
    /// <summary>Sem registro, ou registro sem nível nem "não se aplica" e sem anotação.</summary>
    public const string NotEvaluated = "NotEvaluated";

    /// <summary>Há registro humano (anotação, alvo ou evidência), mas a situação atual ainda não foi definida.</summary>
    public const string InProgress = "InProgress";

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-02] Há conteúdo HERDADO de outra rodada, IMPORTADO por CSV ou semeado sem revisão humana:
    /// aguarda confirmação na tela e não entra nas médias.
    /// </summary>
    public const string PendingConfirmation = "PendingConfirmation";

    /// <summary>Situação atual definida por revisão humana (o alvo pode faltar: lacuna indeterminada).</summary>
    public const string Evaluated = "Evaluated";

    /// <summary>Resultado declarado não aplicável ao escopo, com justificativa, por revisão humana.</summary>
    public const string NotApplicable = "NotApplicable";
}

/// <summary>[AEGIS-NIST-JOURNEY-02] Situação da decisão do revisor sobre a versão VIGENTE da avaliação.</summary>
public static class NistReviewStates
{
    /// <summary>Sem decisão do revisor.</summary>
    public const string None = "None";

    /// <summary>Aprovada — o conteúdo vigente é o mesmo que o revisor aprovou.</summary>
    public const string Approved = "Approved";

    /// <summary>Devolvida para ajuste — o conteúdo vigente é o mesmo que o revisor devolveu.</summary>
    public const string ChangesRequested = "ChangesRequested";

    /// <summary>Houve decisão, mas o conteúdo mudou depois dela: a decisão não vale para a versão vigente.</summary>
    public const string Outdated = "Outdated";
}

// ---- Comandos ----------------------------------------------------------------------------------------------------

public sealed record CreateNistAssessmentCommand(
    string Name,
    string? Description,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? InitialScopeName,
    string? InitialScopeDescription,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Primeira rodada (opcional: sem nome, "Rodada 1" no período da avaliação).</summary>
    string? InitialCycleName = null,
    string? InitialCyclePeriodKind = null,
    DateOnly? InitialCyclePeriodStart = null,
    DateOnly? InitialCyclePeriodEnd = null);

public sealed record CreateNistScopeCommand(string Name, string? Description);

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] Nova rodada. <paramref name="PeriodKind"/>: Monthly, Quarterly ou Other. <paramref name="SeedMode"/>:
/// None, Reference (a rodada de origem aparece como referência) ou Draft (níveis, textos e procedimentos planejados viram
/// rascunho identificado — nunca revisão humana).
/// </summary>
public sealed record CreateNistCycleCommand(
    string Name, string PeriodKind, DateOnly PeriodStart, DateOnly PeriodEnd, Guid? SeedFromCycleId, string SeedMode);

/// <summary>[AEGIS-NIST-JOURNEY-02] Encerrar (Closed) ou reabrir (Open) uma rodada.</summary>
public sealed record SetNistCycleStatusCommand(string Status, int ExpectedVersion);

/// <summary>
/// Responsável identificado: usuário ATIVO do tenant (<paramref name="UserId"/>, verificado no servidor), contato externo
/// (<paramref name="IsExternal"/> com nome e contato) ou texto livre (legado). Nunca há conversão automática de texto em vínculo.
/// </summary>
public sealed record NistResponsibleInput(Guid? UserId, string? Name, bool IsExternal, string? Contact);

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
    int ExpectedVersion,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Responsável pela prática como usuário do tenant (vence o texto).</summary>
    Guid? OwnerUserId = null,
    bool OwnerIsExternal = false,
    string? OwnerContact = null,
    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Campos aplicados de uma sugestão da IA (justificativa, lacunas, risco, melhoria, nível).</summary>
    NistAssistanceReference? Assistance = null);

/// <summary>[AEGIS-NIST-JOURNEY-02] Designação de avaliador e revisor (usuários ativos do tenant; nulo = sem designação).</summary>
public sealed record AssignNistRolesCommand(Guid? AssessorUserId, Guid? ReviewerUserId, int ExpectedVersion);

/// <summary>[AEGIS-NIST-JOURNEY-02] Decisão do revisor: Approved ou ChangesRequested (com nota).</summary>
public sealed record ReviewNistEvaluationCommand(string Decision, string? Note, int ExpectedVersion);

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

// ---- Leituras ----------------------------------------------------------------------------------------------------

/// <summary>[AEGIS-NIST-JOURNEY-02] Uma rodada da avaliação.</summary>
public sealed record NistCycleView(
    Guid Id,
    string Name,
    string PeriodKind,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    Guid? SeedFromCycleId,
    string? SeedFromCycleName,
    string SeedMode,
    DateTimeOffset CreatedAt,
    string? CreatedByName,
    DateTimeOffset? ClosedAt,
    string? ClosedByName,
    int Version,
    int Publications);

/// <summary>
/// Andamento de um escopo NA RODADA <paramref name="CycleId"/> (a rodada é dita explicitamente: os números de outra rodada
/// nunca são apresentados como se fossem desta).
/// </summary>
public sealed record NistScopeView(
    Guid Id,
    string Name,
    string? Description,
    int Subcategories,
    int Evaluated,
    int NotApplicable,
    int InProgress,
    int WithEvidence,
    DateTimeOffset? LastReviewedAt,
    Guid? CycleId = null,
    int PendingConfirmation = 0);

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
    IReadOnlyList<NistScopeView> Scopes,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Rodadas (mais recente primeiro).</summary>
    IReadOnlyList<NistCycleView>? Cycles = null,
    /// <summary>A rodada a que se referem os números de andamento dos escopos acima (a mais recente).</summary>
    Guid? ProgressCycleId = null);

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
    DateTimeOffset? ReviewedAt,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Decisão do revisor sobre a versão vigente.</summary>
    string ReviewState = NistReviewStates.None,
    int ProceduresPlanned = 0,
    int ProceduresPerformed = 0,
    int OpenFindings = 0,
    string? AssessorName = null,
    string? ReviewerName = null);

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
    IReadOnlyList<NistCategoryView> Categories,
    Guid? CycleId = null);

/// <summary>Lacuna determinável de uma subcategoria (atual e alvo confirmados).</summary>
public sealed record NistGapView(
    string Code,
    string Title,
    int CurrentLevel,
    int TargetLevel,
    int Gap,
    string? OwnerName,
    string? ImprovementGuidance,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Achados abertos registrados nesta subcategoria da rodada.</summary>
    int OpenFindings = 0,
    string? RiskImpact = null);

/// <summary>[AEGIS-NIST-JOURNEY-02] Contagem por estado (para gráficos que levam às subcategorias).</summary>
public sealed record NistStateCountsView(
    string Code,
    int Subcategories,
    int NotEvaluated,
    int InProgress,
    int PendingConfirmation,
    int Evaluated,
    int NotApplicable,
    int ReviewApproved,
    int ReviewOutdated);

/// <summary>[AEGIS-NIST-JOURNEY-02] Andamento dos procedimentos por método.</summary>
public sealed record NistProcedureProgressView(
    string Method,
    int Planned,
    int InProgress,
    int Performed,
    int NotPerformed,
    int Satisfactory,
    int PartiallySatisfactory,
    int Unsatisfactory,
    int Inconclusive);

/// <summary>[AEGIS-NIST-JOURNEY-02] Tratamento: achados e planos da rodada e escopo.</summary>
public sealed record NistTreatmentView(
    int FindingsOpen,
    int FindingsRiskAccepted,
    int FindingsClosed,
    IReadOnlyDictionary<string, int> OpenBySeverity,
    int FindingsWithoutPlan,
    int PlansOpen,
    int PlansInProgress,
    int PlansAwaitingValidation,
    int PlansCompleted,
    int PlansOverdue);

public sealed record NistProfileView(
    Guid AssessmentId,
    Guid ScopeId,
    string MethodologyVersion,
    NistProfileScoreView Overall,
    IReadOnlyList<NistProfileScoreView> Functions,
    IReadOnlyList<NistProfileScoreView> Categories,
    IReadOnlyList<NistGapView> Gaps,
    int IndeterminateGaps,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Rodada do perfil, andamento por função, procedimentos e tratamento.</summary>
    Guid? CycleId = null,
    NistStateCountsView? States = null,
    IReadOnlyList<NistStateCountsView>? FunctionStates = null,
    IReadOnlyList<NistProcedureProgressView>? Procedures = null,
    NistTreatmentView? Treatment = null);

/// <summary>[AEGIS-NIST-JOURNEY-02] Responsável na visão de leitura: vínculo, externo ou texto (legado).</summary>
public sealed record NistResponsibleView(string? Name, Guid? UserId, string Kind, string? Contact, bool UserActive)
{
    public const string KindUser = "User";
    public const string KindExternal = "External";
    public const string KindText = "Text";
    public const string KindNone = "None";
}

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
    int Version,
    // ---- [AEGIS-NIST-JOURNEY-02] ----
    string ContentOrigin = "Analyst",
    string? OriginNote = null,
    bool HumanConfirmed = true,
    NistResponsibleView? Owner = null,
    Guid? AssessorUserId = null,
    string? AssessorName = null,
    Guid? ReviewerUserId = null,
    string? ReviewerName = null,
    string ReviewState = NistReviewStates.None,
    string? ReviewDecisionByName = null,
    DateTimeOffset? ReviewDecisionAt = null,
    string? ReviewDecisionNote = null,
    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Campos cujo texto VIGENTE veio de uma sugestão da IA (editado ou não pela pessoa).</summary>
    IReadOnlyList<NistAssistedFieldView>? AssistedFields = null);

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

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] A mesma subcategoria na rodada de ORIGEM (referência de leitura): o que foi registrado lá,
/// por quem e quando. Nunca é a avaliação desta rodada.
/// </summary>
public sealed record NistCycleReferenceView(
    Guid CycleId,
    string CycleName,
    string State,
    int? CurrentLevel,
    int? TargetLevel,
    bool NotApplicable,
    string? Rationale,
    string? Gaps,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    int EvidenceCount,
    int ProceduresPerformed,
    int Findings);

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
    string MethodologyVersion,
    // ---- [AEGIS-NIST-JOURNEY-02] ----
    Guid? CycleId = null,
    string? CycleName = null,
    string? CycleStatus = null,
    IReadOnlyList<NistProcedureView>? Procedures = null,
    IReadOnlyList<NistFindingView>? Findings = null,
    NistCycleReferenceView? Reference = null,
    /// <summary>Por que um achado ainda não pode ser registrado aqui (nulo quando pode).</summary>
    string? FindingBlockedReason = null);

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
    double? Target,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Rodada do ponto.</summary>
    Guid? CycleId = null,
    string? CycleName = null,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null);

/// <summary>[AEGIS-NIST-JOURNEY-02] Usuário ATIVO do tenant que pode receber trabalho (sem e-mail: só o necessário).</summary>
public sealed record NistAssigneeView(Guid UserId, string DisplayName, string Role);

/// <summary>[AEGIS-NIST-JOURNEY-02] Um valor alterado: campo, rótulo, anterior e novo (truncados).</summary>
public sealed record NistFieldChange(string Field, string Label, string? From, string? To);

/// <summary>[AEGIS-NIST-JOURNEY-02] Uma entrada da trilha (NIST ou plano de ação vinculado), já pronta para leitura.</summary>
public sealed record NistAuditEntryView(
    Guid Id,
    DateTimeOffset At,
    string ActorName,
    string Subject,
    Guid? SubjectId,
    string Action,
    string Summary,
    Guid? CycleId,
    Guid? ScopeId,
    string? SubcategoryCode,
    IReadOnlyList<NistFieldChange> Changes);

// ---- Erros -------------------------------------------------------------------------------------------------------

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

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] A sugestão foi gerada sobre um contexto que mudou: incorporar exige gerar outra ou declarar a
/// revisão diante do estado atual. É um conflito (409) com motivo próprio, distinto do conflito de versão.
/// </summary>
public sealed class NistAssistStaleException : NistAssessmentConflictException
{
    public NistAssistStaleException(string message) : base(message) { }
}

public class NistAiUnavailableException : Exception
{
    public NistAiUnavailableException(string message) : base(message) { }

    /// <summary>
    /// [AEGIS-NIST-AI-ASSIST-01] Por que não houve assistência: Disabled (IA desativada), Unavailable (provedor fora do ar
    /// ou cota), Timeout, InvalidResponse (resposta fora do contrato, descartada). A jornada manual segue em todos.
    /// </summary>
    public NistAiUnavailableException(string message, string reason) : base(message) => Reason = reason;

    public string Reason { get; } = "Unavailable";
}

/// <summary>
/// Porta da jornada NIST. Tenant implícito (filtro global fail-closed); autor sempre do token. [AEGIS-NIST-JOURNEY-02]
/// Toda leitura e gravação de trabalho nomeia avaliação, RODADA e escopo — nenhuma rodada é escolhida implicitamente.
/// </summary>
public interface INistAssessmentService
{
    Task<IReadOnlyList<NistAssessmentView>> ListAsync(CancellationToken ct = default);
    Task<NistAssessmentView> GetAsync(Guid assessmentId, CancellationToken ct = default);
    Task<NistAssessmentView> CreateAsync(CreateNistAssessmentCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistScopeView> AddScopeAsync(Guid assessmentId, CreateNistScopeCommand command, RemediationActor actor, CancellationToken ct = default);

    Task<NistCycleView> CreateCycleAsync(Guid assessmentId, CreateNistCycleCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistCycleView> SetCycleStatusAsync(Guid assessmentId, Guid cycleId, SetNistCycleStatusCommand command, RemediationActor actor, CancellationToken ct = default);

    Task<NistFunctionView> GetFunctionAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string functionCode, CancellationToken ct = default);
    Task<NistProfileView> GetProfileAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> GetSubcategoryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> SaveEvaluationAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, SaveNistEvaluationCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> AssignAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, AssignNistRolesCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> ReviewAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, ReviewNistEvaluationCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> LinkEvidenceAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, LinkNistEvidenceCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistSubcategoryDetailView> RemoveEvidenceAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid evidenceId, RemediationActor actor, CancellationToken ct = default);

    Task<IReadOnlyList<NistAssessmentHistoryItem>> HistoryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NistAssigneeView>> AssigneesAsync(CancellationToken ct = default);

    /// <summary>Trilha da avaliação (opcionalmente de uma rodada, escopo e subcategoria), mais recente primeiro.</summary>
    Task<IReadOnlyList<NistAuditEntryView>> AuditAsync(Guid assessmentId, Guid? cycleId, Guid? scopeId, string? code, CancellationToken ct = default);
}
