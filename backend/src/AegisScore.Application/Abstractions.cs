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

    /// <summary>
    /// Copiloto GRC ONIPRESENTE com CONSCIÊNCIA DE CONTEXTO e ROTEAMENTO DE INTENÇÃO (Agentic Routing):
    /// responde no escopo da tela ativa (<see cref="AuditorScope"/>) com o System Prompt ajustado
    /// DINAMICAMENTE, e classifica a mensagem numa <see cref="AuditorIntent"/> — dúvida geral (Copilot) ou
    /// pedido de auditoria (StartInterview, cuja resposta JÁ É a 1ª pergunta do fluxo NIST). A implementação
    /// obriga a IA a devolver saída ESTRUTURADA. Toda saída é uma SUGESTÃO; o analista permanece no laço.
    /// </summary>
    Task<AuditorReply> ChatAsync(AuditorChatRequest request, CancellationToken ct);

    /// <summary>
    /// Redige uma Recomendação de Remediação (advisory) para uma subcategoria NIST: título, risco
    /// documentado (o "porquê", em linguagem de risco) e o passo a passo técnico exportável (o "como
    /// fazer" que a TI do cliente executa). É a saída CONSULTIVA do Aegis Score — uma SUGESTÃO que o
    /// analista do SOC revisa antes de entregar. O Stub devolve texto canned ancorado no código do
    /// controle; o motor real compõe o texto via LLM.
    /// </summary>
    Task<AdvisoryDraft> GenerateAdvisoryAsync(AdvisoryGenerationRequest request, CancellationToken ct);
}

// ---- Copiloto GRC (Auditor onipresente, com escopo de contexto) --------------

/// <summary>
/// Escopo de contexto do Copiloto GRC: a tela/Função NIST onde o usuário está. Ajusta a persona e o foco
/// de auditoria da IA. <c>Global</c> = visão executiva do Secure Score (fora de uma Função dedicada).
/// </summary>
public enum AuditorScope { Global = 0, Govern, Identify, Protect, Detect, Respond, Recover }

/// <summary>Uma fala do histórico do chat (papel + conteúdo). Conteúdo é dado NÃO confiável (anti-injeção).</summary>
public record AuditorMessage(string Role, string Content);

/// <summary>
/// Um turno do Copiloto GRC: o escopo ativo, o histórico e a nova mensagem do usuário. O tenant NÃO
/// trafega aqui — é resolvido do claim do JWT na borda, nunca do corpo (Zero Trust).
/// </summary>
public record AuditorChatRequest(AuditorScope Scope, IReadOnlyList<AuditorMessage> History, string UserMessage);

/// <summary>
/// Intenção roteada pela IA (Agentic Routing): <c>Copilot</c> = dúvida/consulta geral respondida na hora;
/// <c>StartInterview</c> = o usuário pediu para auditar/fechar lacunas, então a resposta JÁ É a primeira
/// pergunta do fluxo NIST e a UI deve entrar no modo entrevista.
/// </summary>
public enum AuditorIntent { Copilot = 0, StartInterview }

/// <summary>
/// Carga estruturada opcional da resposta (o <c>Metadata</c>) — o que a UI precisa para reagir à intenção.
/// Em <see cref="AuditorIntent.StartInterview"/>, semeia a entrevista com a subcategoria NIST investigada.
/// </summary>
public record AuditorInterviewSeed(string? TargetSubcategoryCode);

/// <summary>
/// Resposta do Copiloto com ROTEAMENTO DE INTENÇÃO: a fala (<paramref name="Message"/> — em StartInterview,
/// já a 1ª pergunta), o escopo, a <paramref name="Intent"/> classificada e um <paramref name="Metadata"/>
/// estruturado opcional (ex.: <see cref="AuditorInterviewSeed"/>) para a UI reagir.
/// </summary>
public record AuditorReply(string Message, AuditorScope Scope, AuditorIntent Intent, object? Metadata = null);

/// <summary>Traduz a <see cref="AuditorIntent"/> de/para o código de fio ("COPILOT"/"START_INTERVIEW") —
/// enum-string na fronteira, a UI não depende do valor numérico do enum. Default seguro: COPILOT.</summary>
public static class AuditorIntents
{
    public static string ToWire(AuditorIntent intent) => intent switch
    {
        AuditorIntent.StartInterview => "START_INTERVIEW",
        _ => "COPILOT",
    };

    public static AuditorIntent FromWire(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "START_INTERVIEW" => AuditorIntent.StartInterview,
        _ => AuditorIntent.Copilot,
    };
}

/// <summary>Mapeia o código de escopo vindo da UI no enum. O escopo NÃO é fronteira de segurança (o chat é
/// read-only); um valor desconhecido cai em <see cref="AuditorScope.Global"/> (fail-safe, não fail-closed).</summary>
public static class AuditorScopes
{
    public static AuditorScope FromCode(string? code) => (code ?? "").Trim().ToUpperInvariant() switch
    {
        "GV" => AuditorScope.Govern,
        "ID" => AuditorScope.Identify,
        "PR" => AuditorScope.Protect,
        "DE" => AuditorScope.Detect,
        "RS" => AuditorScope.Respond,
        "RC" => AuditorScope.Recover,
        _ => AuditorScope.Global,
    };
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

// ---- Recomendações de Remediação (Advisories) -------------------------------

/// <summary>Pedido de redação de um advisory: o código NIST-alvo é o que ancora o texto (canned ou LLM).</summary>
public record AdvisoryGenerationRequest(string SubcategoryCode);

/// <summary>
/// Rascunho de advisory produzido pelo motor de IA: título + risco documentado + passo a passo técnico.
/// É uma SUGESTÃO (o analista do SOC valida antes de entregar ao cliente).
/// </summary>
public record AdvisoryDraft(string Title, string DocumentedRisk, string TechnicalSteps);

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

/// <summary>
/// Gatilho de sincronização de políticas SOB DEMANDA (Govern). Desacopla o request HTTP do trabalho de
/// ingestão: o controller publica o tenant e devolve 202 na hora; o <c>PolicyIngestionWorker</c> — além do
/// ciclo periódico do timer — consome o canal e executa o fetch/registro em background. Canal em memória
/// (mesmo idioma do <see cref="IDocumentAnalysisQueue"/>); em produção troca-se por um broker mantendo a porta.
/// </summary>
public interface IPolicySyncTrigger
{
    /// <summary>Publica um pedido de sincronização das políticas do tenant. Não bloqueia: só enfileira.</summary>
    ValueTask RequestSyncAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Fluxo assíncrono dos tenants a sincronizar, consumido pelo worker.</summary>
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}
