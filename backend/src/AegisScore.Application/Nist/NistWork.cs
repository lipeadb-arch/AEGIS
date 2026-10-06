using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Remediation;

namespace AegisScore.Application.Nist;

// [AEGIS-NIST-JOURNEY-02] Trabalho dentro de uma subcategoria: procedimentos de avaliação (examinar, entrevistar, testar),
// achados registrados pelo analista e o plano de tratamento de cada achado (o MESMO mecanismo de planos do produto, com
// origem NIST explícita). Invariantes:
//   • procedimento PLANEJADO não é resultado — o resultado tem andamento, data, observação e conclusão próprios;
//   • achado nunca nasce sozinho de atual × alvo: o analista decide, com risco, impacto, severidade e prioridade justificados;
//   • concluir o plano não muda maturidade, score nem conformidade — a reavaliação é outro ato.

/// <summary>Novo procedimento planejado. <paramref name="Method"/>: Examine, Interview ou Test.</summary>
public sealed record AddNistProcedureCommand(string Method, string Procedure);

/// <summary>
/// Atualização de um procedimento: texto planejado e/ou RESULTADO. <paramref name="Status"/>: Planned, InProgress, Performed
/// ou NotPerformed. Realizado exige data, observação e conclusão (<paramref name="Outcome"/>: Satisfactory,
/// PartiallySatisfactory, Unsatisfactory, Inconclusive); não realizado exige o motivo na observação.
/// </summary>
public sealed record UpdateNistProcedureCommand(
    string? Procedure,
    string Status,
    string? Outcome,
    string? Observation,
    DateOnly? PerformedOn,
    IReadOnlyList<Guid>? EvidenceIds,
    int ExpectedVersion);

public sealed record NistProcedureView(
    Guid Id,
    string Method,
    string Procedure,
    string Status,
    string? Outcome,
    string? Observation,
    DateOnly? PerformedOn,
    string? ResultRecordedByName,
    DateTimeOffset? ResultRecordedAt,
    IReadOnlyList<Guid> EvidenceIds,
    string ContentOrigin,
    string? OriginNote,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    int Version);

/// <summary>Plano criado junto com o achado (opcional).</summary>
public sealed record NistPlanInput(
    string Title,
    string? ProposedAction,
    NistResponsibleInput? Responsible,
    string? ResponsibleArea,
    DateOnly? DueDate);

public sealed record CreateNistFindingCommand(
    string Title,
    string Condition,
    string Risk,
    string Impact,
    string Severity,
    string SeverityRationale,
    string Priority,
    string PriorityRationale,
    string Recommendation,
    IReadOnlyList<Guid>? EvidenceIds,
    NistPlanInput? Plan);

/// <summary>Edição do achado (campos nulos permanecem).</summary>
public sealed record UpdateNistFindingCommand(
    string? Title,
    string? Condition,
    string? Risk,
    string? Impact,
    string? Severity,
    string? SeverityRationale,
    string? Priority,
    string? PriorityRationale,
    string? Recommendation,
    IReadOnlyList<Guid>? EvidenceIds,
    int ExpectedVersion);

/// <summary>Situação do achado: Open, RiskAccepted ou Closed (as duas últimas com justificativa).</summary>
public sealed record SetNistFindingStatusCommand(string Status, string? Note, int ExpectedVersion);

/// <summary>Edição do plano de um achado NIST (campos nulos permanecem; <paramref name="Status"/> nulo = sem transição).</summary>
public sealed record UpdateNistPlanCommand(
    int ExpectedVersion,
    string? Title,
    string? ProposedAction,
    NistResponsibleInput? Responsible,
    string? ResponsibleArea,
    DateOnly? DueDate,
    string? Status);

/// <summary>Contexto de ORIGEM congelado no achado (a razão de ele existir, como estava no registro).</summary>
public sealed record NistFindingOriginContext(
    string Schema,
    Guid AssessmentId,
    Guid CycleId,
    string CycleName,
    Guid ScopeId,
    string ScopeName,
    string SubcategoryCode,
    string SubcategoryTitle,
    int EvaluationVersion,
    int? CurrentLevel,
    int? TargetLevel,
    int? Gap,
    string? GapsText,
    IReadOnlyList<string> ProcedureFindings,
    DateTimeOffset RecordedAt)
{
    public const string SchemaV1 = "nist-finding-origin-v1";
}

public sealed record NistFindingView(
    Guid Id,
    Guid AssessmentId,
    Guid CycleId,
    string CycleName,
    Guid ScopeId,
    string ScopeName,
    string SubcategoryCode,
    string SubcategoryTitle,
    string Title,
    string Condition,
    string Risk,
    string Impact,
    string Severity,
    string SeverityRationale,
    string Priority,
    string PriorityRationale,
    string Recommendation,
    IReadOnlyList<Guid> EvidenceIds,
    string Status,
    string? StatusNote,
    DateTimeOffset? StatusChangedAt,
    string? StatusChangedByName,
    NistFindingOriginContext? Origin,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    int Version,
    /// <summary>Plano de tratamento vigente (o ativo, ou o mais recente), quando existe.</summary>
    NistPlanView? Plan,
    /// <summary>Situação do TRATAMENTO, dita em uma frase (a do plano, ou "sem plano").</summary>
    string TreatmentLabel);

/// <summary>Uma validação de plano na visão da jornada NIST (enums como texto, rótulos em pt-BR).</summary>
public sealed record NistPlanValidationView(
    string Method, string MethodLabel, string Outcome, string OutcomeLabel, string? EvidenceReference, string Rationale,
    DateTimeOffset DecidedAt, string DecidedByName, bool AppliesToCurrentCycle);

public sealed record NistPlanEventView(string Kind, DateTimeOffset At, string ActorName, string? FromStatus, string? ToStatus, string? Note);

/// <summary>
/// O plano de tratamento de um achado NIST na visão da jornada: o MESMO plano do produto (etapas, ciclo, execução, validação,
/// trilha), com a origem NIST explícita e os enums como texto. Nenhum identificador do KNIGHT é exposto como se existisse.
/// </summary>
public sealed record NistPlanView(
    Guid Id,
    string OriginKind,
    NistPlanOrigin? NistOrigin,
    string Title,
    string? ProposedAction,
    string? ResponsiblePerson,
    Guid? ResponsibleUserId,
    bool ResponsibleIsExternal,
    string? ResponsibleContact,
    string? ResponsibleArea,
    DateOnly? DueDate,
    string Status,
    string StatusLabel,
    bool IsOverdue,
    bool IsActive,
    string NextStep,
    string? ExecutionNotes,
    string? ExecutionEvidenceRef,
    DateTimeOffset? ExecutedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset CycleStartedAt,
    bool WasReopened,
    int Version,
    IReadOnlyList<string> AllowedTransitions,
    string? ClosureBlockedReason,
    NistPlanValidationView? LatestValidation,
    NistPlanValidationView? ApplicableValidation,
    IReadOnlyList<NistPlanEventView> Events)
{
    public static NistPlanView From(ActionPlanView p)
    {
        static NistPlanValidationView V(ActionPlanValidationView v) => new(
            v.Method.ToString(), RemediationReading.MethodLabel(v.Method), v.Outcome.ToString(), RemediationReading.OutcomeLabel(v.Outcome),
            v.EvidenceReference, v.Rationale, v.DecidedAt, v.DecidedByName, v.AppliesToCurrentCycle);
        return new NistPlanView(
            p.Id, p.OriginKind.ToString(), p.NistOrigin, p.Title, p.ProposedAction, p.ResponsiblePerson, p.ResponsibleUserId,
            p.ResponsibleIsExternal, p.ResponsibleContact, p.ResponsibleArea, p.DueDate, p.Status.ToString(), RemediationReading.StatusLabel(p.Status),
            p.IsOverdue, p.IsActive, p.NextStep, p.ExecutionNotes, p.ExecutionEvidenceRef, p.ExecutedAt, p.CompletedAt, p.CreatedAt,
            p.CycleStartedAt, p.WasReopened, p.Version, p.AllowedTransitions.Select(t => t.ToString()).ToList(), p.ClosureBlockedReason,
            p.LatestValidation is null ? null : V(p.LatestValidation),
            p.ApplicableValidation is null ? null : V(p.ApplicableValidation),
            p.Events.Select(e => new NistPlanEventView(e.Kind.ToString(), e.At, e.ActorName, e.FromStatus?.ToString(), e.ToStatus?.ToString(), e.Note)).ToList());
    }
}

/// <summary>Filtro da lista de achados de uma avaliação (rodada e escopo opcionais).</summary>
public sealed record NistFindingFilter(Guid? CycleId, Guid? ScopeId, string? SubcategoryCode, string? Status);

/// <summary>Trabalho de uma subcategoria e tratamento da avaliação. Mesmas regras de tenant, papel e versão da jornada.</summary>
public interface INistWorkService
{
    Task<NistProcedureView> AddProcedureAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, AddNistProcedureCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistProcedureView> UpdateProcedureAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid procedureId, UpdateNistProcedureCommand command, RemediationActor actor, CancellationToken ct = default);
    Task RemoveProcedureAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid procedureId, int expectedVersion, RemediationActor actor, CancellationToken ct = default);

    Task<NistFindingView> CreateFindingAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CreateNistFindingCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistFindingView> UpdateFindingAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, UpdateNistFindingCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistFindingView> SetFindingStatusAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, SetNistFindingStatusCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistFindingView> GetFindingAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, CancellationToken ct = default);
    Task<IReadOnlyList<NistFindingView>> ListFindingsAsync(Guid assessmentId, NistFindingFilter filter, CancellationToken ct = default);

    Task<NistFindingView> CreatePlanAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, NistPlanInput input, RemediationActor actor, CancellationToken ct = default);
    Task<NistFindingView> UpdatePlanAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, UpdateNistPlanCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistFindingView> RecordPlanExecutionAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, RecordExecutionCommand command, RemediationActor actor, CancellationToken ct = default);
    Task<NistFindingView> ValidatePlanAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, ValidateActionPlanCommand command, RemediationActor actor, CancellationToken ct = default);
}
