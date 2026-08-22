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

    /// <summary>
    /// Julga UM controle contra o trecho que o endereça, com a regra do 800-53 injetada (RAG dirigido).
    /// Segunda passada do pipeline documental — ver <see cref="DocumentControlEvaluationRequest"/>.
    /// </summary>
    Task<DocumentControlVerdict> EvaluateDocumentControlAsync(
        DocumentControlEvaluationRequest request, CancellationToken ct);

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
/// Um turno do Copiloto GRC: o escopo ativo, o histórico, a nova mensagem do usuário e o CONTEXTO
/// tenant-scoped somente leitura com que a IA fundamenta a resposta. O tenant NÃO trafega aqui — é
/// resolvido do claim do JWT na borda, nunca do corpo (Zero Trust); o <paramref name="Context"/> é montado
/// no servidor a partir do tenant autenticado. É opcional para preservar as construções existentes (stub/testes).
/// </summary>
public record AuditorChatRequest(
    AuditorScope Scope,
    IReadOnlyList<AuditorMessage> History,
    string UserMessage,
    AuditorTenantContext? Context = null);

/// <summary>
/// Contexto tenant-scoped, SOMENTE LEITURA e LIMITADO, com que o Auditor Virtual fundamenta as respostas.
/// É um DTO do AEGIS (nunca do SDK do provedor). Contém apenas AGREGADOS e trechos curtos JÁ VALIDADOS —
/// jamais documento completo, log bruto, credencial ou identificador pessoal. No Free Tier, só tenants da
/// allowlist (dados sintéticos) têm este contexto enviado ao provedor externo.
/// </summary>
public sealed record AuditorTenantContext(
    string ScoreState,                  // "Evaluated" | "NotEvaluated"
    double? ScorePercentage,            // null quando NotEvaluated (nunca 0% por ausência)
    double CoveragePercentage,
    int CompliantControls,
    int NonCompliantControls,
    int MitigatedControls,
    int NotEvaluatedControls,
    DateTimeOffset? LatestEvidenceAt,
    IReadOnlyList<AuditorFunctionPosture> Functions,
    IReadOnlyList<AuditorControlGap> TopGaps,
    IReadOnlyList<AuditorDocumentEvidence> RecentEvidence,
    AuditorConnectorContext Connectors,
    IReadOnlyList<string> PendingRecommendations,
    // [AEGIS-MVP-POSTURE-02] Principais exposições de configuração ABERTAS (no máx. 8), só campos permitidos —
    // nunca resposta bruta, actionUrl, segredo ou PII. Opcional/default null para preservar construções existentes.
    IReadOnlyList<AuditorPostureExposure>? TopExposures = null,
    // [AEGIS-MVP-VULN-01] Principais vulnerabilidades ativo×CVE ABERTAS (no máx. 8), priorizadas. SÓ campos
    // permitidos — nunca machineId/ExternalRef, IP, aadDeviceId, segredo, payload bruto, inventário completo ou PII.
    IReadOnlyList<AuditorVulnerability>? TopVulnerabilities = null);

/// <summary>
/// [AEGIS-MVP-VULN-01] Uma vulnerabilidade (exposição ativo×CVE) para o contexto do Auditor — FATO DA FONTE,
/// consultivo. Só os campos permitidos: CVE, severidade/CVSS, indicadores de exploit e EPSS, nome e criticidade
/// do ativo, produto afetado e ciclo de vida. NUNCA machineId/ExternalRef, IP, aadDeviceId ou payload bruto. A IA
/// explica/correlaciona/prioriza e apoia remediação, mas NÃO cria CVE, ativo, exploit, estado de resolução ou score.
/// </summary>
public sealed record AuditorVulnerability(
    string CveId,
    string? Severity,
    double? CvssScore,
    bool? PublicExploit,
    bool? ExploitVerified,
    double? Epss,
    string AssetName,
    int AssetCriticality,
    string? Product,
    string LifecycleState,
    // Fontes (providers) que OBSERVAM esta exposição — só o rótulo do provedor, nunca conector/binding/machineId.
    IReadOnlyList<string> Sources);

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Uma exposição de configuração (postura) para o contexto do Auditor — FATO DA FONTE,
/// consultivo. Só os campos permitidos: identificador da fonte, título, categoria/serviço, gap, rank/tier,
/// remediação CURTA e ameaças. A IA explica/correlaciona/prioriza, mas NÃO abre/fecha/altera rank/gap/estado.
/// </summary>
public sealed record AuditorPostureExposure(
    string ExternalId,
    string Title,
    string? Category,
    string? Service,
    double Gap,
    int? SourceRank,
    string? Tier,
    string? Remediation,
    IReadOnlyList<string> Threats);

/// <summary>Resumo de postura de UMA Função NIST para o contexto do Auditor.</summary>
public sealed record AuditorFunctionPosture(
    string Code, string Name, string EvaluationState, double? Percentage, double CoveragePercentage,
    int NonCompliantControls, int NotEvaluatedControls);

/// <summary>Uma lacuna de controle (não conforme / não avaliado) para o Auditor priorizar.</summary>
public sealed record AuditorControlGap(string SubcategoryCode, string Status, string? Reason);

/// <summary>Evidência documental RELEVANTE e curta (trecho literal já validado — nunca o documento inteiro).</summary>
public sealed record AuditorDocumentEvidence(
    string DocumentTitle, string SubcategoryCode, double Confidence, string EvidenceQuote);

/// <summary>Saúde/recência agregada dos conectores do tenant (sem segredo, sem endpoint).</summary>
public sealed record AuditorConnectorContext(
    int Configured, int Enabled, int Healthy, int Degraded, int Failed, int NeverSynced,
    DateTimeOffset? LastSyncAt);

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

/// <summary>
/// SEGUNDA passada do RAG documental: julgar UM controle contra o trecho que o endereça, com a régua do
/// 800-53 na mão. A primeira passada (<see cref="DocumentAnalysisRequest"/>) é TRIAGEM — descobre quais
/// controles o documento toca, porque o documento não declara o alvo. Só depois de saber o alvo é
/// possível carregar a regra e montar este payload enxuto.
///
/// O que viaja é estritamente: o trecho relevante + o controle + os critérios de evidência. Nunca o
/// documento inteiro: além do custo, texto irrelevante dilui a atenção do modelo e o faz ancorar em
/// parágrafos que não provam o controle sob julgamento.
/// </summary>
/// <param name="SubcategoryCode">Controle NIST sob julgamento ("PR.AA-01").</param>
/// <param name="ControlOutcome">O outcome do catálogo — o que precisa estar demonstrado.</param>
/// <param name="EvidenceRequirements">Os <c>evidence_requirements</c> da regra do 800-53.</param>
/// <param name="CalculationLogic">A rubrica de cálculo da regra; vazia quando a regra não a define.</param>
/// <param name="DocumentExcerpt">Trecho selecionado pelo <c>DocumentChunker</c>, não o documento cru.</param>
public record DocumentControlEvaluationRequest(
    string SubcategoryCode,
    string ControlOutcome,
    IReadOnlyList<string> EvidenceRequirements,
    string CalculationLogic,
    string DocumentExcerpt,
    string? FileName);

/// <summary>
/// Veredito documental de UM controle, com os quatro eixos SEPARADOS que a integridade probatória exige:
/// se o trecho SUSTENTA o controle (<paramref name="Supported"/>), o TRECHO LITERAL que o prova
/// (<paramref name="EvidenceQuote"/>), a CONFIANÇA e o RACIONAL. A separação é deliberada: o racional é
/// paráfrase/análise e nunca vale como prova; só o <paramref name="EvidenceQuote"/> — validado EM CÓDIGO
/// como literalmente presente no texto extraído — pode gerar mapping, cobertura ou crédito no score.
///
/// ⚠️ A resposta do modelo é apenas uma PROPOSTA. A autoridade final é a validação em código: um
/// <paramref name="Supported"/>=true com trecho inventado (ausente do texto) é DESCARTADO fail-closed.
/// </summary>
/// <param name="Supported">O trecho PROVA explicitamente o controle? Título, palavra genérica, menção
/// temática ou intenção futura NÃO sustentam — nesses casos é false.</param>
/// <param name="EvidenceQuote">Trecho LITERAL (verbatim) do documento que prova o controle. Vazio quando
/// <paramref name="Supported"/>=false. Jamais paráfrase — é validado por presença literal no texto.</param>
/// <param name="Confidence">0..1 — quão bem o trecho PROVA o controle (não quão bonito é o texto).</param>
/// <param name="Rationale">Justificativa técnica citando o que o documento diz (ou deixa de dizer). É
/// análise, NÃO prova.</param>
public record DocumentControlVerdict(bool Supported, string EvidenceQuote, double Confidence, string Rationale);

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

// ---- [AEGIS-AUD-050] Filas operacionais DURÁVEIS (PostgreSQL) ----------------
// Substituem os canais em memória anteriores (sem durabilidade), que perdiam o trabalho em qualquer reinício,
// não sobreviviam ao encerramento no meio do processamento e não coordenavam múltiplas réplicas. O
// mecanismo durável é o PostgreSQL já em uso — sem broker externo, portátil entre ambientes. A aquisição é
// ATÔMICA (FOR UPDATE SKIP LOCKED): duas réplicas nunca pegam o mesmo item. O lease expira sozinho
// (recupera worker caído), a falha transitória agenda retry, o limite de tentativas fecha em Failed e a
// entrega é at-least-once, apoiada na idempotência dos invariantes do banco. A VARREDURA cross-tenant vive
// SOMENTE neste componente de aquisição, sob contexto de sistema explicitamente controlado; após o claim,
// o processamento segue sob o tenant dono do item.

/// <summary>
/// Lease adquirido sobre um documento a analisar: o alvo, o tenant dono, a identidade do lease (para
/// confirmar/soltar o trabalho depois) e o nº de aquisições já feitas (para decidir retry × falha terminal).
/// </summary>
public sealed record DocumentAnalysisLease(Guid DocumentId, Guid TenantId, Guid LeaseId, int Attempts);

/// <summary>
/// Fila operacional DURÁVEL de análise de documentos (AEGIS-AUD-050). O próprio <c>GovernanceDocument</c> é
/// o item de trabalho — o status persistido (Queued/Pending = disponível, Processing = adquirido, Analyzed =
/// sucesso, Failed = terminal) substitui o canal em memória. Enfileirar é apenas persistir o documento em
/// Queued (feito pelo controller/worker de ingestão); daí em diante esta porta cuida da entrega segura.
/// </summary>
public interface IDocumentAnalysisQueue
{
    /// <summary>Adquire atomicamente o próximo documento disponível (Queued/Pending, ou Processing com lease
    /// VENCIDO) e o marca Processing sob um lease novo, incrementando as tentativas. Null = sem trabalho.</summary>
    Task<DocumentAnalysisLease?> TryClaimNextAsync(CancellationToken ct = default);

    /// <summary>BATIMENTO de lease: estende a expiração enquanto o trabalho ainda dura, sob a guarda do lease.
    /// False se o lease já não é o vigente (perdido) — o chamador deve ABORTAR o processamento. É o que impede
    /// que uma análise mais longa que o lease seja adquirida por outra réplica.</summary>
    Task<bool> RenewAsync(Guid documentId, Guid leaseId, CancellationToken ct = default);

    /// <summary>Confirma o sucesso (Processing → Analyzed) sob a guarda do lease. False se o lease já não é o
    /// vigente — outra réplica assumiu e o chamador NÃO deve sobrescrever (a perda é DETECTADA aqui).</summary>
    Task<bool> CompleteAsync(Guid documentId, Guid leaseId, CancellationToken ct = default);

    /// <summary>Falha TRANSITÓRIA: devolve o documento para nova tentativa (Processing → Pending) com backoff,
    /// sob a guarda do lease. O nº de tentativas já foi incrementado na aquisição.</summary>
    Task<bool> ScheduleRetryAsync(Guid documentId, Guid leaseId, CancellationToken ct = default);

    /// <summary>Falha TERMINAL (tentativas esgotadas ou defeito irrecuperável): Processing → Failed, com uma
    /// categoria de erro SANITIZADA — nunca a mensagem bruta (AEGIS-AUD-054) — sob a guarda do lease.</summary>
    Task<bool> FailAsync(Guid documentId, Guid leaseId, string errorCategory, CancellationToken ct = default);

    /// <summary>Desligamento gracioso: solta o lease e devolve o documento à fila SEM custar tentativa
    /// (Processing → Pending, tentativa estornada), para que outra réplica/o próximo boot o retome já.</summary>
    Task<bool> ReleaseAsync(Guid documentId, Guid leaseId, CancellationToken ct = default);
}

/// <summary>Lease adquirido sobre uma solicitação de sincronização de políticas (mesmo contrato do documento).</summary>
public sealed record PolicySyncLease(Guid RequestId, Guid TenantId, Guid LeaseId, int Attempts);

/// <summary>
/// Fila operacional DURÁVEL de sincronização de políticas (AEGIS-AUD-050). Substitui o gatilho em memória: o
/// endpoint <c>/governance/documents/sync</c> PERSISTE uma <c>PolicySyncRequest</c> antes de responder 202, e o
/// ciclo periódico apenas ENFILEIRA o trabalho — o <c>PeriodicTimer</c> é agendador, nunca transporte nem a
/// única memória do pedido. O <c>PolicyIngestionWorker</c> adquire com lease atômico e processa.
/// </summary>
public interface IPolicySyncQueue
{
    /// <summary>Enfileira (persiste) um pedido de sync do tenant. Idempotente: um único pedido ATIVO
    /// (Pending/Processing) por tenant é invariante de banco — se já existe, é no-op. Usado pelo endpoint sob
    /// demanda e pelo ciclo periódico, sempre com stamping fail-closed do tenant.</summary>
    Task EnqueueAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Adquire atomicamente o próximo pedido disponível (Pending, ou Processing com lease VENCIDO) e o
    /// marca Processing sob um lease novo. Null = sem trabalho.</summary>
    Task<PolicySyncLease?> TryClaimNextAsync(CancellationToken ct = default);

    /// <summary>BATIMENTO de lease: estende a expiração enquanto o fetch/ingestão ainda dura, sob a guarda do
    /// lease. False se o lease foi perdido — o chamador deve abortar.</summary>
    Task<bool> RenewAsync(Guid requestId, Guid leaseId, CancellationToken ct = default);

    Task<bool> CompleteAsync(Guid requestId, Guid leaseId, CancellationToken ct = default);
    Task<bool> ScheduleRetryAsync(Guid requestId, Guid leaseId, string errorCategory, CancellationToken ct = default);
    Task<bool> FailAsync(Guid requestId, Guid leaseId, string errorCategory, CancellationToken ct = default);
    Task<bool> ReleaseAsync(Guid requestId, Guid leaseId, CancellationToken ct = default);
}
