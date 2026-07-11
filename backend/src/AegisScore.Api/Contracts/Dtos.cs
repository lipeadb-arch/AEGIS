using AegisScore.Domain;

namespace AegisScore.Api.Contracts;

// ---- Auth ----
public record LoginRequest(string Email, string Password);
/// <summary>O refresh token NÃO trafega aqui — vai apenas no cookie HttpOnly. Só o access token é exposto.</summary>
public record AuthResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt);

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

// ---- Identify: inventário de ativos (ID.AM) ----
public record CreateAssetRequest(
    string Name, AssetCategory Category, string? SubType, string? Description,
    int Criticality, string? OwnerName, string? ExternalRef, Guid? BusinessProcessId);

public record UpdateAssetRequest(
    string Name, AssetCategory Category, string? SubType, string? Description,
    int Criticality, string? OwnerName, string? ExternalRef, Guid? BusinessProcessId, bool IsActive);

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

/// <summary>Veredito devolvido pela ingestão: o status técnico e os pontos já gravados no ledger.</summary>
public record TelemetryVerdictDto(
    string SubcategoryCode, string Status, int AwardedScore, int MaxScorePoints, int Percentage, string AiEvidence);
