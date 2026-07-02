using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

/// <summary>An assessment campaign for a client against a framework version.</summary>
public class Assessment : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid FrameworkVersionId { get; set; }
    public string Name { get; set; } = "";
    public AssessmentStatus Status { get; set; } = AssessmentStatus.Draft;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public ICollection<AssessmentScope> Scopes { get; set; } = new List<AssessmentScope>();
}

/// <summary>
/// A scope = one Process assessed in one Business Unit
/// (the Plano Diretor "Macroatividade": [Assessment] – Processo – Área).
/// </summary>
public class AssessmentScope : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid BusinessProcessId { get; set; }
    public Guid BusinessUnitId { get; set; }
    public ScopeStatus Status { get; set; } = ScopeStatus.NotStarted;

    public ICollection<AssessmentTask> Tasks { get; set; } = new List<AssessmentTask>();
    public ICollection<Answer> Answers { get; set; } = new List<Answer>();
    public ICollection<Evidence> Evidence { get; set; } = new List<Evidence>();
    public ICollection<SubcategoryEvaluation> Evaluations { get; set; } = new List<SubcategoryEvaluation>();
}

/// <summary>A workflow step from the Plano Diretor (kickoff → questionnaire → ... → present).</summary>
public class AssessmentTask : Entity
{
    public Guid AssessmentScopeId { get; set; }
    public AssessmentTaskType Type { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Open;
    public string? AssigneeId { get; set; }
    public DateOnly? DueDate { get; set; }
}

/// <summary>A questionnaire question tied to a subcategory, with plain-language guidance.</summary>
public class Question : Entity
{
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }
    public string? ThemeGroup { get; set; }          // "INVENTÁRIO", "SOFTWARE E NUVEM"
    public string Text { get; set; } = "";           // "Existe inventário de ativos?"
    public string? Guidance { get; set; }            // "Orientação de resposta" (business language)
    public int Order { get; set; }
    public AnswerType AnswerType { get; set; } = AnswerType.YesNoNa;
}

/// <summary>An answer given for a question within a scope.</summary>
public class Answer : Entity
{
    public Guid AssessmentScopeId { get; set; }
    public Guid QuestionId { get; set; }
    public AnswerValue Value { get; set; } = AnswerValue.Unknown;
    public string? Comment { get; set; }
    public AnswerSource Source { get; set; } = AnswerSource.SelfDeclared;
    public string? RespondedById { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

/// <summary>Evidence backing an answer / evaluation: document, link, API signal, screenshot, interview.</summary>
public class Evidence : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid? AssessmentScopeId { get; set; }
    public string? SubcategoryCode { get; set; }
    public EvidenceType Type { get; set; }
    public EvidenceSource Source { get; set; } = EvidenceSource.Analyst;
    public string? Uri { get; set; }                 // SharePoint / external link
    public string? BlobRef { get; set; }             // stored object key
    public string? AiSummary { get; set; }           // AI extraction of the document
    public string? Hash { get; set; }                // integrity (audit trail)
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// THE CORE RECORD — one assessed subcategory in a scope, with Current vs Target maturity.
/// Mirrors a row of the workbook "Requirements" sheet, plus AI provenance.
/// </summary>
public class SubcategoryEvaluation : Entity
{
    public Guid AssessmentScopeId { get; set; }
    public Guid SubcategoryId { get; set; }
    public NistSubcategory? Subcategory { get; set; }

    public int? CurrentLevel { get; set; }           // 1..5
    public int? CurrentScore { get; set; }           // == CurrentLevel
    public string? CurrentComments { get; set; }

    public int? TargetLevel { get; set; }            // 1..5
    public int? TargetScore { get; set; }
    public string? TargetComments { get; set; }

    public EvaluatedBy EvaluatedBy { get; set; } = EvaluatedBy.Analyst;
    public double? Confidence { get; set; }          // AI confidence 0..1
    public string? Rationale { get; set; }           // AI/analyst justification
    public List<Guid> EvidenceRefs { get; set; } = new();

    public string? ReviewedById { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public int Gap => (TargetScore ?? 0) - (CurrentScore ?? 0);
}
