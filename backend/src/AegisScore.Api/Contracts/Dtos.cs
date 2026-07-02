using AegisScore.Domain;

namespace AegisScore.Api.Contracts;

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
    string EncryptedSettings,
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
