using System;
using System.Text.Json.Serialization;

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
/// Gravidade de um achado de controle — a régua ÚNICA de severidade do produto. Responde ao "e daí?" que
/// o <see cref="ControlStatus"/> não responde: dois controles <c>NonCompliant</c> não doem igual, e é a
/// severidade (ponderada pelo Raio de Explosão — ID.RA) que decide o que a TI corrige primeiro.
///
/// O valor numérico é o RANK de risco (0 = mais grave), então ordenar por ele já traz o crítico ao topo.
/// A escala é a MESMA que a tela de Postura de Identidade já usa (5 níveis de exposição de identidade) — uma
/// segunda régua de 4 níveis criaria dois vocabulários de risco divergentes no mesmo produto.
/// </summary>
public enum SeverityLevel
{
    Critical = 0,
    High = 1,
    Medium = 2,
    Low = 3,

    /// <summary>Sem risco material: achado informativo (ex.: controle conforme, registrado por completude).</summary>
    Informational = 4,
}

/// <summary>Helpers puros da régua de severidade (mesmo idioma de <c>AuditorScopes</c>).</summary>
public static class SeverityLevels
{
    /// <summary>
    /// Severidade PROXY derivada do status — o default enquanto o motor de IA não emite a severidade real
    /// (que virá ponderada pelo Raio de Explosão). Garante que todo controle tenha badge desde o primeiro
    /// veredito, sem inventar risco: é uma leitura direta do próprio status, não uma estimativa.
    /// </summary>
    public static SeverityLevel FromStatus(ControlStatus status) => status switch
    {
        ControlStatus.NonCompliant         => SeverityLevel.Critical,
        ControlStatus.MitigatedByThirdParty => SeverityLevel.Medium,
        _                                   => SeverityLevel.Low,   // Compliant — risco residual conhecido
    };
}

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
    ConfigAnalyzer = 4, Siem = 5, Edr = 6, Cmdb = 7, VulnerabilityScanner = 8,
    // Govern: fonte de DOCUMENTOS de política/governança (SharePoint, Google Workspace…), consumida
    // pelo Provider Pattern (IDocumentIntegrationProvider) — distinta das capacidades de telemetria acima.
    PolicyDocuments = 9
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

/// <summary>
/// [AEGIS-AUD-050] Estágio de uma <c>PolicySyncRequest</c> na fila operacional durável. Pending =
/// disponível para aquisição; Processing = adquirido sob lease; Completed = sincronizado com sucesso;
/// Failed = falha terminal (esgotou as tentativas). Pending/Processing são os estados ATIVOS que o índice
/// único parcial dedupe por tenant.
/// </summary>
public enum PolicySyncStatus { Pending = 0, Processing = 1, Completed = 2, Failed = 3 }

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

// ---- Identify (ID.AM) — metadados táticos da telemetria de ativo (alimentam a avaliação ativa) ----
// Serializados como STRING no JSON (payload legível: "Absent" em vez de 2) — anotação contida a estes
// enums, sem alterar a serialização global da API.

/// <summary>Estado do agente de EDR/antivírus no ativo: comunicando, silencioso/degradado ou ausente.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EdrCoverageStatus { Active = 0, Degraded = 1, Absent = 2 }

/// <summary>Ciclo de vida do sistema operacional: suportado, próximo do fim de vida ou já obsoleto (EOL).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OsLifecycleStatus { Supported = 0, ApproachingEndOfLife = 1, EndOfLife = 2 }

// ---- Identify (ID.RA) — Risk Assessment / Raio de Explosão -------------------

/// <summary>Natureza da aresta de dependência no grafo de topologia (Asset → Asset).</summary>
public enum DependencyType
{
    RunsOn = 0,           // aplicação roda em host/servidor
    Hosts = 1,            // host hospeda VM/container
    ConnectsTo = 2,       // conexão de rede
    AuthenticatesVia = 3, // depende de IdP/AD para autenticar
    StoresDataIn = 4,     // persiste dados em DB/storage
    ConsumesService = 5,  // consome API/serviço (interno ou de terceiro)
    DependsOn = 99        // dependência genérica
}

/// <summary>
/// Força do acoplamento de uma <see cref="DependencyType"/>: define QUANTO a falha do alvo PROPAGA para o
/// dependente. É o eixo que o motor de raio de explosão traduz em fator de decaimento a cada salto reverso.
/// </summary>
public enum DependencyStrength { Hard = 0, Soft = 1, Redundant = 2 }

/// <summary>Procedência de uma ameaça no catálogo (define o vocabulário do Code: CVE, MITRE ATT&amp;CK…).</summary>
public enum ThreatSource { Cve = 0, MitreAttck = 1, Kev = 2, ThreatIntel = 3, Internal = 99 }

/// <summary>Situação da exposição de um ativo a uma ameaça (a aresta ativo↔ameaça).</summary>
public enum ExposureStatus { Active = 0, Mitigated = 1, Accepted = 2, FalsePositive = 3 }

/// <summary>Gatilho que originou o cálculo de um raio de explosão.</summary>
public enum BlastRadiusTrigger { Manual = 0, Scheduled = 1, ThreatDriven = 2 }
