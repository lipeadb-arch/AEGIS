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

/// <summary>Estado de conformidade de um controle NIST para um tenant (núcleo do Aegis Score).</summary>
public enum ControlStatus { Compliant = 0, NonCompliant = 1, MitigatedByThirdParty = 2 }

/// <summary>
/// Procedência do veredito aplicado ao ledger de conformidade — define a PRECEDÊNCIA de escrita no
/// <see cref="TenantControlState"/>. Nome distinto de <see cref="EvidenceSource"/> (procedência de
/// evidência de assessment, já persistida) e de <see cref="CoverageEvidenceSource"/> (ledger de
/// cobertura documental): são três eixos diferentes e não devem colidir.
/// </summary>
public enum VerdictSource
{
    /// <summary>Análise documental (Govern): atesta processo/intenção. Só faz UPGRADE — jamais rebaixa.</summary>
    Documentary = 0,

    /// <summary>Telemetria (EDR/XDR/SIEM): prova implementação efetiva. AUTORITATIVA — eleva e rebaixa.</summary>
    Telemetry = 1,
}

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

// ---- Govern (GV) — Document Hub + Auditor Virtual (GRC) ----------------------

/// <summary>Natureza do documento de governança ingerido no hub.</summary>
public enum GovernanceDocumentType { Politica = 0, Norma = 1, Diretriz = 2, Procedimento = 3, Contrato = 4, Outro = 99 }

/// <summary>Origem do documento no hub (upload manual ou integração SharePoint/Confluence).</summary>
public enum DocumentSource { UploadManual = 0, Integracao = 1 }

/// <summary>Estágio do pipeline de leitura pela IA (o "Status de Leitura da IA" da UI).</summary>
public enum AiAnalysisStatus { Pending = 0, Queued = 1, Processing = 2, Analyzed = 3, Failed = 4 }

/// <summary>Validade documental (lifecycle), independente da leitura da IA.</summary>
public enum GovernanceStatus { Rascunho = 0, Vigente = 1, EmRevisao = 2, Expirado = 3, Descontinuado = 4 }

/// <summary>Estado de cobertura derivado por subcategoria, para o mapa do pilar Govern.</summary>
public enum CoverageStatus { NaoCoberto = 0, Parcial = 1, Coberto = 2 }

/// <summary>Fonte da evidência que sustenta a cobertura (governança híbrida: documentos + entrevistas).</summary>
public enum CoverageEvidenceSource { None = 0, Document = 1, Interview = 2, Both = 3 }

/// <summary>Situação de uma sessão de entrevista do Auditor Virtual.</summary>
public enum GrcInterviewStatus { Active = 0, Completed = 1, Abandoned = 2 }

/// <summary>Autor de uma mensagem na entrevista GRC.</summary>
public enum GrcMessageRole { System = 0, Assistant = 1, User = 2 }

// ---- Identify (ID) — inventário contínuo de ativos --------------------------

/// <summary>Vertical de ativo segundo o NIST CSF 2.0 (ID.AM — Asset Management).</summary>
public enum AssetCategory
{
    Hardware    = 0,   // Dispositivos físicos / hardware
    Software    = 1,   // Software, sistemas e aplicações
    Data        = 2,   // Dados / informações
    People      = 3,   // Pessoas
    Facilities  = 4,   // Instalações
    SupplyChain = 5    // Serviços / cadeia de suprimentos
}

/// <summary>Como o ativo entrou no inventário (base do inventário contínuo).</summary>
public enum AssetDiscoverySource { Manual = 0, Connector = 1, Import = 2 }
