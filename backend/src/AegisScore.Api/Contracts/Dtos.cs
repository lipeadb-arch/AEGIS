using AegisScore.Domain;

namespace AegisScore.Api.Contracts;

// ---- Auth ----
public record LoginRequest(string Email, string Password);
/// <summary>O refresh token NÃO trafega aqui — vai apenas no cookie HttpOnly. Só o access token é exposto.</summary>
public record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt);

// ---- Tenant Switcher (SSO simulado) ----
/// <summary>
/// Um ambiente disponível no seletor do HUD. <paramref name="Role"/> é o papel NAQUELE cliente — a
/// mesma pessoa pode ser TenantAdmin num e Analyst noutro.
/// </summary>
public record TenantOptionDto(Guid Id, string Name, string Slug, string Role);

/// <summary>
/// Corpo da troca de ambiente. Só o ALVO trafega: a pessoa vem da claim <c>account_id</c> do JWT, que
/// o cliente não consegue forjar. Aceitar e-mail/conta aqui reabriria o vetor que a conta global fecha.
/// </summary>
public record SwitchTenantRequest(Guid TargetTenantId);

// ---- Users (identidades) ----
/// <summary>
/// Criação de identidade no tenant ambiente. O <c>TenantId</c> NÃO trafega: vem do claim
/// <c>tenant_id</c> do JWT (Zero Trust). A senha trafega em claro dentro do TLS e é imediatamente
/// derivada em PBKDF2 no servidor — nunca persistida nem registrada em log.
/// </summary>
public record CreateUserRequest(string Email, string DisplayName, string Password, UserRole Role);

/// <summary>
/// Concessão IDEMPOTENTE de acesso ao tenant ambiente. <paramref name="InitialPassword"/> só é exigida
/// quando a identidade ainda não existe aqui — identidades de tenants distintos são independentes e não
/// compartilham credencial.
/// </summary>
public record AssignUserAccessRequest(string Email, UserRole Role, string? InitialPassword = null);

/// <summary>Identidade na visão da API. Deliberadamente SEM <c>PasswordHash</c> — nem o hash sai daqui.</summary>
public record UserDto(
    Guid Id, Guid TenantId, string Email, string DisplayName, string Role,
    bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

// ---- Framework ----
public record FrameworkDto(Guid Id, string Name, string? Source, IReadOnlyList<FunctionDto> Functions);
public record FunctionDto(string Code, string Name, string Definition, IReadOnlyList<CategoryDto> Categories);
public record CategoryDto(string Code, string Name, string Definition, IReadOnlyList<SubcategoryDto> Subcategories);
public record SubcategoryDto(string Code, string Description);
public record MaturityLevelDto(int Level, string Name, string Description, int Score);

// ---- Onboarding ----
public record CreateTenantRequest(string Name, string Slug);
public record CreateBusinessUnitRequest(string Name, string? Code, string? ManagerName, string? ManagerEmail);
public record CreateProcessRequest(string Name, string? ProcessCategory, ProcessClassification Classification, int ProcessValue);
public record CreateConnectorRequest(
    ConnectorProvider Provider,
    ConnectorCapability Capability,
    string DisplayName,
    ConnectorAuthType AuthType,
    string Settings,             // texto em claro; cifrado no servidor (Data Protection) antes de persistir
    int SyncIntervalMinutes = 360);

public record IdResponse(Guid Id);

// ---- Connectors ----
/// <summary>
/// Conector configurado, na visão da API. Deliberadamente SEM o blob de credenciais: o segredo é
/// escrita-apenas (entra em claro no <see cref="CreateConnectorRequest.Settings"/>, é cifrado no
/// servidor e só o coletor o decifra). Nunca ecoa numa resposta, nem cifrado.
/// </summary>
/// <param name="HasCredentials">
/// Há segredo guardado? Distingue "configurado" de "cadastrado sem credencial" na UI, sem revelar
/// nada do segredo em si.
/// </param>
public record ConnectorConfigDto(
    Guid Id, string Provider, string Capability, string DisplayName, string AuthType,
    bool Enabled, int SyncIntervalMinutes, DateTimeOffset? LastSyncAt, string LastStatus,
    bool HasCredentials);

public record ConnectorHealthDto(string Status, string? Message);
public record SignalDto(string SignalKey, double? NumericValue, string? Unit, int? Severity, IReadOnlyList<string> MappedSubcategoryCodes, DateTimeOffset CollectedAt);
public record SyncResultDto(int SignalsCollected, IReadOnlyList<SignalDto> Signals);

// ---- Assessments ----
public record CreateAssessmentRequest(string Name, Guid? FrameworkVersionId);
public record CreateScopeRequest(Guid BusinessProcessId, Guid BusinessUnitId);
public record AiSuggestRequest(
    string SubcategoryCode,
    IReadOnlyList<AnswerInput> Answers,
    IReadOnlyList<string> EvidenceSummaries);
public record AnswerInput(string Question, string Answer, string? Comment);
public record MaturitySuggestionDto(int CurrentLevel, double Confidence, string Rationale);
public record EvaluationUpsertRequest(
    int? CurrentLevel, int? CurrentScore, string? CurrentComments,
    int? TargetLevel, int? TargetScore, string? TargetComments);

public record AggregateDto(string Level, string RefCode, double CurrentScore, double TargetScore, double Gap, int Count);
public record MaturityRollupDto(AggregateDto Overall, IReadOnlyList<AggregateDto> Functions, IReadOnlyList<AggregateDto> Categories);

// ---- Risk ----
public record CreateRiskRequest(string Code, string Title, string? Description, Guid? BusinessProcessId, Guid? BusinessUnitId, string? Threats, string? Vulnerabilities);
public record RiskEvaluationRequest(RiskPhase Phase, int Probability, int Impact, int ProcessValue);
public record RiskEvaluationDto(int Score, string Level);

// ---- Executive dashboard ----
public record ExecutiveDashboardDto(
    string ClientName,
    DateTimeOffset GeneratedAt,
    ExposureCardsDto Exposure,
    IReadOnlyList<RadarPointDto> MaturityByFunction,
    IReadOnlyList<GapPointDto> TopGaps,
    IReadOnlyList<HeatCellDto> RiskHeatmap,
    IReadOnlyList<RiskLevelCountDto> RiskByLevel,
    IcrDto Icr);

public record ExposureCardsDto(
    int CriticalProcessesExposed,
    int IneffectiveControls,
    int OverdueActionPlans,
    double OverallMaturity,
    double TargetMaturity);

/// <summary>
/// Resumo do PIOR raio de explosão conhecido do tenant — o "custo do fracasso" em linguagem de negócio.
///
/// ⚠️ Vive FORA do <see cref="ExecutiveDashboardDto"/> de propósito. O dashboard executivo já faz 6
/// consultas e é o que decide o First Contentful Paint; pendurar mais um JOIN nele atrasaria a tela
/// inteira por um painel secundário. Endpoint próprio ⇒ o painel carrega sozinho, depois, sem bloquear.
/// </summary>
/// <param name="RootAssetName">Epicentro — o ativo cujo comprometimento produz o maior alcance.</param>
/// <param name="Score">Magnitude 0–100 do raio (mesma régua do ICR).</param>
/// <param name="RiskLevel">Banda de risco do raio ("Critico", "Alto"…).</param>
/// <param name="ImpactedAssetCount">Ativos alcançados transitivamente a partir do epicentro.</param>
/// <param name="ImpactedProcessCount">Processos de negócio atingidos — a tradução para a diretoria.</param>
/// <param name="MaxDepth">Profundidade máxima da propagação, em saltos.</param>
/// <param name="AssessedAt">Quando este raio foi calculado.</param>
public record BlastRadiusSummaryDto(
    string RootAssetName,
    double Score,
    string RiskLevel,
    int ImpactedAssetCount,
    int ImpactedProcessCount,
    int MaxDepth,
    DateTimeOffset AssessedAt);

public record RadarPointDto(string Function, string FunctionName, double Current, double Target);
public record GapPointDto(string Code, string Name, double Current, double Target, double Gap);
public record HeatCellDto(int Probability, int Impact, int Count);
public record RiskLevelCountDto(string Level, int Count);
public record IcrDto(double Score, string Band);

// ---- Govern: Document Hub ----
public record ConnectDocumentRequest(string Title, GovernanceDocumentType Type, string SourceReference);
public record DocumentMappingDto(string SubcategoryCode, double Confidence, string? Evidence, bool AnalystConfirmed);
public record GovernanceDocumentDto(
    Guid Id, string Title, string Type, string Source, string? SourceReference,
    string? FileName, string? ContentType, long? FileSizeBytes, string? Sha256,
    DateOnly? DocumentDate, string Status, string AnalysisStatus, string? AnalysisSummary,
    string? AnalysisError, DateTimeOffset? AnalyzedAt, IReadOnlyList<DocumentMappingDto> Mappings);
public record DocumentAcceptedDto(Guid Id, string AnalysisStatus);
public record ConfirmMappingRequest(bool Confirmed, double? Confidence);
/// <summary>Resposta 202 do gatilho manual de sincronização de políticas (Govern): trabalho aceito e enfileirado.</summary>
public record PolicySyncAcceptedDto(Guid TenantId, string Status, string Message);

// ---- Govern: cobertura híbrida (documentos + entrevistas) ----
public record CoverageCellDto(string Code, string Description, string Status, string EvidenceSource);
public record GovernCategoryCoverageDto(string Code, string Name, IReadOnlyList<CoverageCellDto> Subcategories);
public record GovernCoverageDto(double CoveredPct, double PartialPct, IReadOnlyList<GovernCategoryCoverageDto> Categories);
public record GapDto(string Code, string Description, string Status);

// ---- Govern: Auditor Virtual (GRC) ----
public record StartInterviewRequest(string? Title, Guid? AssessmentId, IReadOnlyList<string>? SubcategoryCodes);
public record InterviewMessageDto(
    Guid Id, string Role, string Content, int Sequence, string? TargetSubcategoryCode, DateTimeOffset SentAt);
public record InterviewSessionDto(
    Guid Id, string Title, string Status, IReadOnlyList<string> TargetSubcategoryCodes,
    DateTimeOffset StartedAt, IReadOnlyList<InterviewMessageDto> Messages);
public record PostAnswerRequest(string Content);
public record CoverageChangeDto(string SubcategoryCode, string Status, string EvidenceSource);
public record InterviewTurnDto(
    Guid SessionId, InterviewMessageDto? Question, bool IsComplete,
    CoverageChangeDto? CoverageChange, Guid? IdentifiedRiskId);
public record IdentifiedRiskDto(
    Guid Id, string Title, string Description, string? Cause, string? Consequence,
    string SubcategoryCode, Guid? AssessmentId, bool PromotedToRisk, DateTimeOffset IdentifiedAt);

// ---- Identify: inventário de ativos (ID.AM) — somente leitura (a avaliação é ativa, via telemetria) ----
public record AssetDto(
    Guid Id, string Name, string Category, string? SubType, string? Description,
    int Criticality, string? OwnerName, string? ExternalRef, Guid? BusinessProcessId,
    string DiscoverySource, DateTimeOffset? LastSeenAt, bool IsActive,
    double? RiskScore, string? RiskLevel, DateTimeOffset? RiskScoredAt, DateTimeOffset CreatedAt);

/// <summary>Filtros combinados da grid tática (NIST). Ligados por AND; categorias por OR entre si.</summary>
public class AssetQuery
{
    public List<AssetCategory>? Category { get; set; }   // ?category=Hardware&category=Software
    public RiskLevel? RiskLevel { get; set; }
    public int? Criticality { get; set; }
    public bool? IsActive { get; set; }
    public string? Search { get; set; }                  // Name / SubType / ExternalRef
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

/// <summary>Envelope de paginação genérico (reutilizável por outras grids).</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount, int TotalPages);

// ---- Telemetry: ingestão passiva de sinais de segurança (EDR/SIEM → motor de IA) ----
/// <summary>
/// Payload do webhook de ingestão de telemetria. Envelope genérico de um alerta de ferramenta de
/// segurança (Defender, Sentinel, CrowdStrike…). O <paramref name="SubcategoryCode"/> é o que direciona
/// o motor: diz QUAL controle NIST esta evidência endereça — o mapeamento evento→controle é
/// responsabilidade do emissor/conector (que conhece a semântica da ferramenta), não do motor, que só
/// julga se a evidência PROVA o controle. <paramref name="RawData"/> é a evidência técnica crua, tratada
/// como dado NÃO confiável (fronteira anti-injeção no User Prompt do avaliador).
/// </summary>
public record TelemetryIngestionRequest(
    string Source, string EventName, string Severity, string SubcategoryCode, string RawData);

/// <summary>
/// Telemetria de UM ativo (Identify / ID.AM), com os metadados táticos que decidem a postura: cobertura
/// de EDR, ciclo de vida do SO, nº de CVEs críticas ativas e se o ativo é vital. O motor os avalia contra
/// o controle de gestão de ativos (default <c>ID.AM-01</c>) e grava com fonte <c>Telemetry</c>.
/// </summary>
public record AssetTelemetryRequest(
    string AssetName,
    EdrCoverageStatus EdrCoverage,
    OsLifecycleStatus OsLifecycle,
    int CriticalVulnerabilitiesCount,
    bool IsCriticalAsset,
    string? SubcategoryCode = null);   // default ID.AM-01 (resolvido no controller)

// ---- Protect (PR): telemetria especializada por categoria (SOC multicloud) ----
// Contratos específicos por categoria do Protect. Todos carregam o SubcategoryCode — o motor avalia a
// evidência CONTRA um controle NIST concreto (PR.AA-01, PR.DS-01, PR.PS-01, PR.IR-01).

/// <summary>PR.AA — Identity & Access Management. Privilégio sem MFA integral é falha crítica.</summary>
public record IdentityProtectTelemetryDto(
    double PrivilegedMfaCoverage, double StandardMfaCoverage, int StaleAccountsActive,
    bool ConditionalAccessEnforced, string SubcategoryCode);

/// <summary>PR.DS — Data Security. Criptografia de endpoint insuficiente ou tráfego em claro reprova.</summary>
public record DataProtectTelemetryDto(
    double EndpointEncryptionCoverage, int DlpActivePoliciesCount, bool UnencryptedTrafficDetected,
    string SubcategoryCode);

/// <summary>PR.PS — Platform Security. Hardening CIS abaixo do mínimo ou patch crítico pendente reprova.</summary>
public record PlatformProtectTelemetryDto(
    double CisBenchmarkComplianceRate, bool AppLockerEnforced, int MissingCriticalPatchesCount,
    string SubcategoryCode);

/// <summary>PR.IR — Technology Infrastructure Resilience. Firewall sem política default-deny reprova.</summary>
public record NetworkProtectTelemetryDto(
    bool MicrosegmentationActive, bool DefaultDenyFirewallEnforced, string SubcategoryCode);

// ---- Detect (DE): telemetria especializada por categoria (SOC avançado) ----
// Contratos por categoria do Detect. NB (NIST CSF 2.0): a função DE tem apenas DE.AE (Adverse Event
// Analysis) e DE.CM (Continuous Monitoring); o antigo DE.DP (Detection Processes) do CSF 1.1 foi absorvido
// em DE.AE (ex.: DE.AE-06 herda o DE.DP-4). Códigos reais sugeridos: DE.AE-02, DE.CM-01, DE.AE-06.

/// <summary>DE.AE — Adverse Event Analysis. Anomalia grave não investigada ou fadiga de alerta reprova.</summary>
public record AnomaliesDetectTelemetryDto(
    int UninvestigatedHighAnomaliesCount, double FalsePositiveRate, int CorrelationRulesFiredCount,
    string SubcategoryCode);

/// <summary>DE.CM — Continuous Monitoring. Cobertura de logs críticos baixa ou ativo crítico sem monitoração reprova.</summary>
public record MonitoringDetectTelemetryDto(
    double CriticalLogSourceCoverage, int UnmonitoredCriticalAssetsCount, double NetworkVisibilityCoverage,
    string SubcategoryCode);

/// <summary>Detection Engineering (o antigo DE.DP, hoje sob DE.AE). Baixa cobertura MITRE ou ataques simulados não detectados reprova.</summary>
public record ProcessDetectTelemetryDto(
    double MitreAttckCoverageRate, int ActiveDetectionRulesCount, double SimulatedAttacksDetectedRate,
    string SubcategoryCode);

// ---- Respond (RS) & Recover (RC): resposta a incidentes e resiliência (SOC de alta performance) ----
// Códigos reais no catálogo CSF 2.0: RS.MA-01, RS.MI-01, RC.RP-01.

/// <summary>RS.MA — Incident Analysis. Reconhecimento lento (MTTA) ou baixa cobertura de threat hunting reprova.</summary>
public record AnalysisRespondTelemetryDto(
    int MeanTimeToAcknowledgeMins, double ThreatHuntingCoverageRate, string SubcategoryCode);

/// <summary>RS.MI — Incident Mitigation. Sem isolamento automatizado ou resposta lenta (MTTR) reprova.</summary>
public record MitigationRespondTelemetryDto(
    bool AutomatedIsolationEnabled, int MeanTimeToRespondMins, string SubcategoryCode);

/// <summary>RC.RP — Recovery Plan Execution. Backup mutável, integridade não-Valid ou RTO não atendido reprova.</summary>
public record ExecutionRecoverTelemetryDto(
    bool ImmutableBackupsEnabled, string BackupIntegrityStatus, bool RecoveryTimeObjectiveMet,
    string SubcategoryCode);

// ---- Govern (GV): telemetria estruturada de governança (além da análise documental) ----
// Governança não se resume a ler PDFs: métricas estruturadas de cadeia de suprimentos (GV.SC) e de
// papéis/autoridades (GV.RR) chegam como telemetria — fonte AUTORITATIVA, não o teto documental de 50%.
// Códigos reais no catálogo CSF 2.0: GV.SC-01, GV.RR-01.

/// <summary>GV.SC — Cybersecurity Supply Chain Risk Mgmt. Fornecedor de TI com acesso à rede sem auditoria de terceiros ativa reprova.</summary>
public record SupplyChainTelemetryDto(
    int SuppliersWithNetworkAccess, int CriticalSuppliersCount, bool ThirdPartyAudited, string SubcategoryCode);

/// <summary>GV.RR — Roles, Responsibilities & Authorities. Conta de administrador sem revisão periódica de acesso reprova.</summary>
public record RolesTelemetryDto(
    int TotalAdminAccounts, int AdminAccountsWithoutReview, bool PrivilegedAccessReviewConfigured, string SubcategoryCode);

/// <summary>Veredito devolvido pela ingestão: o status técnico e os pontos já gravados no ledger.</summary>
public record TelemetryVerdictDto(
    string SubcategoryCode, string Status, int AwardedScore, int MaxScorePoints, int Percentage, string AiEvidence);

/// <summary>
/// Corpo OPCIONAL do POST de ingestão do Entra ID. As MÉTRICAS de identidade vêm do provider (Graph/stub),
/// NÃO do cliente — aqui só trafega o CONTEXTO que o Entra desconhece: o domínio do tenant a consultar e os
/// controles compensatórios de REDE (isolamento de OT/legado), que o motor pondera para não gerar falso
/// positivo em ambiente industrial. O TenantId NÃO trafega: é resolvido do claim <c>tenant_id</c> do JWT.
/// </summary>
public record EntraIdIdentityIngestionRequest(
    string? TenantDomain = null,
    bool HasNetworkIsolation = false,
    IReadOnlyList<string>? CompensatingControls = null);

// ---- Auditor Virtual (Copiloto GRC onipresente, com escopo de contexto) ----
/// <summary>Uma fala do histórico do chat (Role: "user"|"assistant"; Content: texto). Dado NÃO confiável.</summary>
public record AuditorChatMessageDto(string Role, string Content);

/// <summary>
/// Turno do Copiloto GRC. <paramref name="ContextScope"/> é o código da tela ativa ("GLOBAL","GV","ID",
/// "PR","DE","RS","RC"), que ajusta dinamicamente o System Prompt da IA. O TenantId NÃO trafega aqui — é
/// resolvido do claim <c>tenant_id</c> do JWT (Zero Trust).
/// </summary>
public record AuditorChatRequestDto(
    string ContextScope, string Message, IReadOnlyList<AuditorChatMessageDto>? History);

/// <summary>
/// Resposta do Copiloto com ROTEAMENTO DE INTENÇÃO. <paramref name="Intent"/> ("COPILOT"|"START_INTERVIEW")
/// diz à UI como reagir; <paramref name="Metadata"/> é a carga estruturada opcional da intenção (em
/// START_INTERVIEW, semeia a entrevista com a subcategoria investigada).
/// </summary>
public record AuditorChatResponseDto(string Reply, string Scope, string Intent, object? Metadata);

// ---- Risk Assessment (ID.RA) — Raio de Explosão ----

/// <summary>Corpo OPCIONAL do POST de raio de explosão: um cenário de ameaça para simulação. Ausente = raio topológico puro.</summary>
public record BlastRadiusRequestDto(Guid? ScenarioThreatId);

/// <summary>Um ativo colateral no raio de explosão (espelha <see cref="AegisScore.Domain.BlastRadiusImpactNode"/>).</summary>
public record BlastRadiusNodeDto(Guid ImpactedAssetId, int Distance, double PropagatedImpact, string PathStrength);

/// <summary>Resposta do cálculo: score/nível agregado + métricas + os nós impactados (ordenados por impacto).</summary>
public record BlastRadiusResponseDto(
    Guid AssessmentId,
    Guid RootAssetId,
    double BlastRadiusScore,
    string RiskLevel,
    int ImpactedAssetCount,
    int ImpactedProcessCount,
    int MaxDepth,
    DateTimeOffset ComputedAt,
    IReadOnlyList<BlastRadiusNodeDto> ImpactedNodes);

// ---- Scoring: Recomendações de Remediação (Advisories) ----
/// <summary>
/// Corpo do POST de criação de advisory: só o código NIST-alvo trafega. O texto (título, risco, passo a
/// passo) é REDIGIDO pelo motor de IA no servidor — o cliente não injeta prosa. O TenantId NÃO trafega:
/// é resolvido do claim <c>tenant_id</c> do JWT (Zero Trust).
/// </summary>
public record CreateAdvisoryRequest(string SubcategoryCode);
