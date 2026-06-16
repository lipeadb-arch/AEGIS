using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stars.Domain;

namespace Stars.Application.Abstractions;

// ---- AI engine (LLM-agnostic) -----------------------------------------------

/// <summary>
/// The S.T.A.R.S AI engine. Implementations are swappable (Claude, Azure OpenAI, local).
/// Every output is a *suggestion* with confidence + rationale — the analyst validates.
/// </summary>
public interface IAiAssessmentService
{
    /// <summary>Read a policy/procedure, extract verifiable claims, map to subcategories.</summary>
    Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct);

    /// <summary>Suggest a maturity level (1–5) for a subcategory from answers, evidence and signals.</summary>
    Task<MaturitySuggestion> SuggestMaturityAsync(MaturitySuggestionRequest request, CancellationToken ct);

    /// <summary>Drive one turn of a conversational questionnaire / interview.</summary>
    Task<InterviewTurn> ConductInterviewTurnAsync(InterviewContext context, CancellationToken ct);

    /// <summary>Generate prioritized action plans from gaps × risk × ICR.</summary>
    Task<IReadOnlyList<ActionPlanSuggestion>> GenerateActionPlanAsync(ActionPlanRequest request, CancellationToken ct);

    /// <summary>Compose an executive report (Plano Diretor) in business language.</summary>
    Task<string> GenerateExecutiveReportAsync(ExecutiveReportRequest request, CancellationToken ct);

    /// <summary>
    /// Dynamic parser: turn raw, unstructured tool output (logs, arbitrary JSON, CSV exports)
    /// into normalized signals mapped to the unified schema. Lets us ingest unknown tools
    /// without a hand-written parser per product.
    /// </summary>
    Task<IReadOnlyList<NormalizedSignal>> NormalizeSignalsAsync(RawSignalBatch batch, CancellationToken ct);
}

public record DocumentAnalysisRequest(Guid TenantId, string DocumentText, string? FileName);
public record DocumentClaim(string SubcategoryCode, string Claim, double Confidence);
public record DocumentAnalysis(string Summary, IReadOnlyList<DocumentClaim> Claims);

public record MaturitySuggestionRequest(
    string SubcategoryCode,
    string SubcategoryDescription,
    IReadOnlyList<(string Question, string Answer, string? Comment)> Answers,
    IReadOnlyList<string> EvidenceSummaries,
    IReadOnlyList<(string SignalKey, double? Value, int? Severity)> Signals);

public record MaturitySuggestion(int CurrentLevel, double Confidence, string Rationale, IReadOnlyList<Guid> EvidenceRefs);

public record InterviewContext(Guid ScopeId, string ProcessName, IReadOnlyList<string> History);
public record InterviewTurn(string Question, string? TargetSubcategoryCode, bool IsComplete);

public record ActionPlanRequest(Guid TenantId, IReadOnlyList<(string SubcategoryCode, int Gap, double Icr)> Gaps);
public record ActionPlanSuggestion(string SubcategoryCode, string What, string How, string Priority);

public record ExecutiveReportRequest(Guid AssessmentId, string ClientName);

/// <summary>Raw output from some tool that the core does not understand natively.</summary>
public record RawSignalBatch(
    Guid TenantId,
    ConnectorProvider Provider,
    ConnectorCapability Capability,
    string RawPayload,          // log lines / JSON / CSV exactly as the tool emitted it
    string? FormatHint);        // "csv", "json", "syslog", null = let the LLM detect

/// <summary>A signal the LLM extracted and shaped into the unified schema.</summary>
public record NormalizedSignal(
    string SignalKey,
    double? NumericValue,
    string? Unit,
    int? Severity,
    IReadOnlyList<string> MappedSubcategoryCodes,
    string? RawJson);

// ---- Connector contract (stack-agnostic) ------------------------------------

public record ConnectorHealth(ConnectorStatus Status, string? Message);

/// <summary>
/// A plugin that collects evidence/facts from one client tool capability.
/// Add a new tool = add a new implementation; nothing else in the core changes.
/// </summary>
public interface IEvidenceConnector
{
    ConnectorProvider Provider { get; }
    ConnectorCapability Capability { get; }
    Task<ConnectorHealth> TestAsync(ConnectorConfig config, CancellationToken ct);
    IAsyncEnumerable<EvidenceSignal> CollectAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>Resolves the right connector for a given provider+capability at runtime.</summary>
public interface IConnectorRegistry
{
    IEvidenceConnector? Resolve(ConnectorProvider provider, ConnectorCapability capability);
    IReadOnlyList<IEvidenceConnector> All { get; }
}

// ---- Tenancy context --------------------------------------------------------

/// <summary>
/// Ambient accessor for the current request's tenant (resolved from the X-Tenant header
/// or a JWT claim). Used by the DbContext global query filter to enforce isolation.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
}
