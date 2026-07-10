using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Abstractions;

// ---- AI engine (LLM-agnostic) -----------------------------------------------

/// <summary>
/// The Aegis Score AI engine. Implementations are swappable (Claude, Azure OpenAI, local).
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

/// <summary>
/// Transporte de LLM de baixo nível, agnóstico de provedor: entra um par system + user prompt, sai o
/// texto bruto da conclusão. É o seam que deixa cada serviço dono da própria engenharia de prompt e,
/// ao mesmo tempo, testável — basta mockar isto para exercitar um serviço (ex.: o avaliador do Aegis
/// Score) sem chamar um modelo real. Distinto de <see cref="IAiAssessmentService"/> (alto nível, por
/// caso de uso); este é só o cano de transporte.
/// </summary>
public interface ILLMClient
{
    Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
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

// ---- Connector secrets ------------------------------------------------------

/// <summary>
/// Cifra/decifra o blob de configuração sensível de um conector (tokens OAuth, API keys) ANTES de
/// persistir. A implementação usa a Data Protection API do ASP.NET Core (chaves gerenciadas fora do
/// código-fonte). Regra: nunca confiar num blob "já cifrado" enviado pelo cliente.
/// </summary>
public interface IConnectorSecretProtector
{
    /// <summary>Cifra texto em claro para armazenamento em repouso.</summary>
    string Protect(string plaintext);

    /// <summary>Decifra o valor cifrado. Lança se o payload foi adulterado ou a chave não confere.</summary>
    string Unprotect(string protectedValue);
}

// ---- Document Hub (Govern) --------------------------------------------------

/// <summary>Armazena/recupera o binário de um documento de governança (disco, blob, S3…).</summary>
public interface IDocumentStorage
{
    /// <summary>Persiste o conteúdo e devolve a URI de armazenamento.</summary>
    Task<string> SaveAsync(Guid tenantId, Guid documentId, string fileName, Stream content, CancellationToken ct);
    Task<Stream> OpenAsync(string storageUri, CancellationToken ct);
    Task DeleteAsync(string storageUri, CancellationToken ct);
}

/// <summary>Extrai o texto de um documento (PDF, DOCX, texto) para a leitura da IA.</summary>
public interface IDocumentTextExtractor
{
    /// <summary>True se este extrator sabe lidar com o contentType/arquivo informado.</summary>
    bool CanHandle(string? contentType, string? fileName);
    Task<string> ExtractAsync(Stream content, string? contentType, CancellationToken ct);
}

/// <summary>Fila de leitura: enfileira documentos para análise assíncrona pela IA.</summary>
public interface IDocumentAnalysisQueue
{
    ValueTask EnqueueAsync(Guid documentId, CancellationToken ct = default);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}
