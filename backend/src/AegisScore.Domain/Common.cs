using System;

namespace AegisScore.Domain;

/// <summary>Base for every persisted entity. Operational entities also carry a TenantId.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Marker for entities isolated per client (multi-tenant).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

// ---- Shared enums ------------------------------------------------------------

public enum TenantStatus { Onboarding = 0, Active = 1, Suspended = 2 }

public enum ProcessClassification { Publico = 0, Interno = 1, Confidencial = 2, Restrito = 3 }

public enum AssessmentStatus { Draft = 0, InProgress = 1, InReview = 2, Published = 3 }

public enum ScopeStatus { NotStarted = 0, Questionnaire = 1, Validation = 2, Evaluation = 3, Done = 4 }

public enum AssessmentTaskType
{
    Kickoff = 0, SendQuestionnaire = 1, FollowUp = 2, ValidateAnswers = 3,
    EvaluateMaturity = 4, Interview = 5, PresentResults = 6
}

public enum TaskStatus { Open = 0, InProgress = 1, Done = 2, Blocked = 3 }

public enum AnswerType { YesNoNa = 0, Scale = 1, Text = 2 }

public enum AnswerValue { Yes = 0, No = 1, NotApplicable = 2, Partial = 3, Unknown = 4 }

public enum EvidenceType { Document = 0, Link = 1, ApiSignal = 2, Screenshot = 3, Interview = 4 }

public enum EvidenceSource { SelfDeclared = 0, AiInferred = 1, ApiValidated = 2, Analyst = 3 }

public enum EvaluatedBy { Analyst = 0, Ai = 1 }

/// <summary>Who/what produced an answer.</summary>
public enum AnswerSource { SelfDeclared = 0, AiInferred = 1, ApiValidated = 2 }

public enum ConnectorProvider
{
    Microsoft = 0, Google = 1, Aws = 2, MicrosoftSentinel = 3,
    CrowdStrike = 4, Splunk = 5, Generic = 99
}

public enum ConnectorCapability
{
    SecureScore = 0, DefenderExposure = 1, PurviewCompliance = 2, AzureAdvisor = 3,
    ConfigAnalyzer = 4, Siem = 5, Edr = 6, Cmdb = 7, VulnerabilityScanner = 8
}

public enum ConnectorAuthType { OAuthClientCredentials = 0, ApiKey = 1, ServiceAccount = 2 }

public enum ConnectorStatus { Unknown = 0, Healthy = 1, Degraded = 2, Failed = 3 }

public enum RiskPhase { Inherent = 0, Residual = 1 }

public enum RiskLevel { Baixo = 0, Medio = 1, Alto = 2, Critico = 3 }

public enum RiskTreatmentType { Aceitar = 0, Mitigar = 1, Transferir = 2, Evitar = 3 }

public enum ActionPlanStatus { Aberto = 0, EmAndamento = 1, Concluido = 2, Vencido = 3 }

public enum SnapshotLevel { Overall = 0, Function = 1, Category = 2, Subcategory = 3, Scope = 4 }

public enum IcrSubjectType { Vulnerability = 0, Asset = 1, Risk = 2, Process = 3 }

public enum IcrBand { Controlado = 0, Moderado = 1, Alto = 2, Critico = 3 }
