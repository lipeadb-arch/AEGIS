using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>Configuration for the AI engine. The model/provider is fully swappable.</summary>
public class AiOptions
{
    public string Provider { get; set; } = "anthropic";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5";
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1/messages";
    public string AnthropicVersion { get; set; } = "2023-06-01";
    public int MaxTokens { get; set; } = 2000;
}

/// <summary>
/// Default <see cref="IAiAssessmentService"/> backed by the Anthropic Messages API.
/// This is the LLM-agnostic seam of Aegis Score: to use Azure OpenAI or a local model,
/// add another implementation and register it instead — no caller changes.
/// Every method returns a *suggestion*; the analyst remains in the loop.
/// </summary>
public class ClaudeAssessmentService : IAiAssessmentService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly AiOptions _opt;

    public ClaudeAssessmentService(HttpClient http, IOptions<AiOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
    }

    public async Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct)
    {
        const string system =
            "You are a NIST CSF 2.0 GRC analyst. Read the policy/procedure and extract verifiable " +
            "claims, mapping each to a NIST CSF 2.0 subcategory code (e.g. GV.OC-01). " +
            "Respond ONLY with JSON: {\"summary\":\"...\",\"claims\":[{\"subcategoryCode\":\"..\",\"claim\":\"..\",\"confidence\":0.0}]}.";
        var user = $"FILE: {request.FileName}\n\nCONTENT:\n{request.DocumentText}";

        var dto = await CompleteJsonAsync<DocAnalysisJson>(system, user, ct);
        var claims = (dto.claims ?? new())
            .Select(c => new DocumentClaim(c.subcategoryCode ?? "", c.claim ?? "", c.confidence))
            .ToList();
        return new DocumentAnalysis(dto.summary ?? "", claims);
    }

    public async Task<MaturitySuggestion> SuggestMaturityAsync(MaturitySuggestionRequest request, CancellationToken ct)
    {
        const string system =
            "You assess cybersecurity maturity on a 1–5 CMMI scale (1 Performed, 2 Documented, " +
            "3 Managed, 4 Quantitatively Managed, 5 Optimizing) for one NIST CSF 2.0 subcategory. " +
            "Weigh self-declared answers AGAINST documentary evidence and API facts; if they conflict, " +
            "lower confidence and explain. Respond ONLY with JSON: " +
            "{\"currentLevel\":1-5,\"confidence\":0.0-1.0,\"rationale\":\"...\"}.";

        var answers = string.Join("\n", request.Answers.Select(a => $"- Q: {a.Question}\n  A: {a.Answer}{(a.Comment is null ? "" : $" ({a.Comment})")}"));
        var evidence = string.Join("\n", request.EvidenceSummaries.Select(e => $"- {e}"));
        var signals = string.Join("\n", request.Signals.Select(s => $"- {s.SignalKey} = {s.Value} (sev {s.Severity})"));
        var user =
            $"SUBCATEGORY {request.SubcategoryCode}: {request.SubcategoryDescription}\n\n" +
            $"ANSWERS:\n{answers}\n\nEVIDENCE:\n{evidence}\n\nAPI FACTS:\n{signals}";

        var dto = await CompleteJsonAsync<MaturityJson>(system, user, ct);
        var level = Math.Clamp(dto.currentLevel, 1, 5);
        return new MaturitySuggestion(level, dto.confidence, dto.rationale ?? "", Array.Empty<Guid>());
    }

    public async Task<InterviewTurn> ConductInterviewTurnAsync(InterviewContext context, CancellationToken ct)
    {
        const string system =
            "You conduct a structured security maturity interview, one question at a time, to fill " +
            "evidence gaps for NIST CSF 2.0 subcategories. Ask the single most useful next question. " +
            "Respond ONLY with JSON: {\"question\":\"..\",\"targetSubcategoryCode\":\"..\",\"isComplete\":false}.";
        var user = $"PROCESS: {context.ProcessName}\n\nHISTORY:\n{string.Join("\n", context.History)}";

        var dto = await CompleteJsonAsync<InterviewJson>(system, user, ct);
        return new InterviewTurn(dto.question ?? "", dto.targetSubcategoryCode, dto.isComplete);
    }

    public async Task<IReadOnlyList<ActionPlanSuggestion>> GenerateActionPlanAsync(ActionPlanRequest request, CancellationToken ct)
    {
        const string system =
            "You produce a prioritized cybersecurity action plan. Given gaps (target−current) and ICR " +
            "per subcategory, propose concrete actions ordered by (gap × ICR). " +
            "Respond ONLY with JSON array: [{\"subcategoryCode\":\"..\",\"what\":\"..\",\"how\":\"..\",\"priority\":\"Alta|Média|Baixa\"}].";
        var gaps = string.Join("\n", request.Gaps.Select(g => $"- {g.SubcategoryCode}: gap {g.Gap}, ICR {g.Icr:0.0}"));

        var dto = await CompleteJsonAsync<List<ActionJson>>(system, gaps, ct);
        return (dto ?? new())
            .Select(a => new ActionPlanSuggestion(a.subcategoryCode ?? "", a.what ?? "", a.how ?? "", a.priority ?? "Média"))
            .ToList();
    }

    public async Task<string> GenerateExecutiveReportAsync(ExecutiveReportRequest request, CancellationToken ct)
    {
        const string system =
            "You are a CISO advisor. Write a concise executive 'Plano Diretor de Segurança' section in " +
            "Brazilian Portuguese: current maturity by process, top risks, control weaknesses and " +
            "improvement opportunities — in business language, not technical jargon. Markdown.";
        var user = $"Cliente: {request.ClientName}. Assessment: {request.AssessmentId}.";
        return await CompleteTextAsync(system, user, ct);
    }

    public async Task<IReadOnlyList<NormalizedSignal>> NormalizeSignalsAsync(RawSignalBatch batch, CancellationToken ct)
    {
        const string system =
            "You are a log/telemetry normalizer. You receive raw, possibly unknown tool output. " +
            "Extract essential fields (host, ip, severity, action, resource, score) and emit normalized " +
            "signals for a unified schema, mapping to NIST CSF 2.0 subcategory codes when evident. " +
            "Respond ONLY with JSON array: " +
            "[{\"signalKey\":\"..\",\"numericValue\":0,\"unit\":\"..\",\"severity\":0-4,\"mappedSubcategoryCodes\":[\"..\"]}].";
        var user = $"PROVIDER: {batch.Provider} / {batch.Capability}\nFORMAT: {batch.FormatHint ?? "auto"}\n\nRAW:\n{batch.RawPayload}";

        var dto = await CompleteJsonAsync<List<SignalJson>>(system, user, ct);
        return (dto ?? new())
            .Select(s => new NormalizedSignal(
                s.signalKey ?? "", s.numericValue, s.unit, s.severity,
                s.mappedSubcategoryCodes ?? new(), null))
            .ToList();
    }

    public async Task<AdvisoryDraft> GenerateAdvisoryAsync(AdvisoryGenerationRequest request, CancellationToken ct)
    {
        const string system =
            "You are a senior SOC/MSSP remediation advisor specialized in NIST CSF 2.0. Given ONE " +
            "subcategory code, write a remediation advisory the client's IT team can execute to raise " +
            "their Secure Score for that control. Reply in Brazilian Portuguese. Provide a short actionable " +
            "title, a 'documentedRisk' explaining WHY the gap matters (business/risk language), and a " +
            "numbered, technical 'technicalSteps' the IT team follows. " +
            "Respond ONLY with JSON: {\"title\":\"..\",\"documentedRisk\":\"..\",\"technicalSteps\":\"..\"}.";
        var user = $"SUBCATEGORY: {request.SubcategoryCode}";

        var dto = await CompleteJsonAsync<AdvisoryJson>(system, user, ct);
        return new AdvisoryDraft(dto.title ?? "", dto.documentedRisk ?? "", dto.technicalSteps ?? "");
    }

    public async Task<AuditorReply> ChatAsync(AuditorChatRequest request, CancellationToken ct)
    {
        // Roteamento de Intenção: o System Prompt manda a IA classificar (COPILOT vs START_INTERVIEW) e
        // devolver JSON estruturado. O escopo da tela ativa afina a persona e o foco de auditoria.
        var system = ChatSystemPrompt(request.Scope);
        var history = string.Join("\n", request.History.Select(m => $"{m.Role}: {m.Content}"));
        var user = $"HISTÓRICO:\n{history}\n\nMENSAGEM DO USUÁRIO: {request.UserMessage}";

        var raw = await CompleteTextAsync(system, user, ct);
        var routed = ParseRouter(raw);

        var intent = AuditorIntents.FromWire(routed.intent);
        object? metadata = intent == AuditorIntent.StartInterview
            ? new AuditorInterviewSeed(routed.targetSubcategoryCode)
            : null;
        return new AuditorReply(routed.message ?? "", request.Scope, intent, metadata);
    }

    /// <summary>
    /// System Prompt do Copiloto com ROTEAMENTO DE INTENÇÃO: persona GRC + foco do escopo ativo
    /// (<see cref="ScopeFocus"/>) + o CONTRATO de saída estruturada. A IA DEVE devolver só JSON classificando
    /// a mensagem em COPILOT (dúvida geral, respondida em <c>message</c>) ou START_INTERVIEW (pedido de
    /// auditoria — <c>message</c> já é a 1ª pergunta do fluxo NIST e <c>targetSubcategoryCode</c> a subcategoria).
    /// </summary>
    private static string ChatSystemPrompt(AuditorScope scope) =>
        "Você é o Copiloto GRC do Aegis Score, um auditor de cibersegurança sênior especialista em NIST CSF " +
        "2.0. Responda em Português do Brasil, objetivo e acionável; suas respostas são SUGESTÕES (o analista " +
        "decide) e nunca invente números — se faltar evidência, peça-a.\n\n" +
        "ROTEIE A INTENÇÃO da mensagem do usuário em uma de duas:\n" +
        "• \"COPILOT\": dúvida/consulta geral. Responda diretamente no campo \"message\".\n" +
        "• \"START_INTERVIEW\": o usuário quer AUDITAR, DIAGNOSTICAR ou FECHAR LACUNAS. Então \"message\" JÁ " +
        "DEVE SER a primeira pergunta investigativa do fluxo NIST, e \"targetSubcategoryCode\" o código da " +
        "subcategoria investigada (ex.: \"GV.SC-01\").\n\n" +
        ScopeFocus(scope) + "\n\n" +
        "Responda ESTRITAMENTE em JSON, sem nenhum texto fora dele: " +
        "{\"intent\":\"COPILOT|START_INTERVIEW\",\"message\":\"..\",\"targetSubcategoryCode\":\"..|null\"}.";

    /// <summary>Foco de auditoria por escopo (controles-alvo, métricas exigidas, tom) — injetado no prompt.</summary>
    private static string ScopeFocus(AuditorScope scope) => scope switch
    {
        AuditorScope.Global =>
            "ESCOPO: GLOBAL. Aja como gerador de relatórios executivos do Secure Score atual: sintetize a " +
            "postura por Função NIST, destaque as maiores lacunas por risco e recomende prioridades para o board. " +
            "Linguagem de negócio, não jargão técnico.",
        AuditorScope.Protect =>
            "ESCOPO: PROTECT (PR). Audite APENAS controles de proteção (PR.AA, PR.DS, PR.PS, PR.IR). Exija " +
            "métricas concretas: MFA privilegiado (meta 100%), Conditional Access, criptografia de endpoint (≥95%), " +
            "hardening CIS (≥80%) e zero patch crítico pendente. Privilégio sem MFA é falha crítica.",
        AuditorScope.Detect =>
            "ESCOPO: DETECT (DE). Foque em DE.AE e DE.CM: cobertura de logs críticos (≥95%), ativos críticos " +
            "monitorados, taxa de falso-positivo, cobertura MITRE ATT&CK e detecção de ataques simulados. Ponto cego " +
            "em ativo crítico é falha.",
        AuditorScope.Respond =>
            "ESCOPO: RESPOND (RS). Foque em RS.MA e RS.MI: MTTA (≤30 min), MTTR (≤120 min), isolamento automatizado " +
            "e cobertura de threat hunting. Resposta lenta amplia o dano.",
        AuditorScope.Recover =>
            "ESCOPO: RECOVER (RC). Foque em RC.RP: backups imutáveis, integridade validada (Valid) e RTO atendido — " +
            "resiliência a ransomware. Backup mutável ou não testado é falha crítica.",
        AuditorScope.Govern =>
            "ESCOPO: GOVERN (GV). Foque em GV.SC (cadeia de suprimentos — fornecedores com acesso à rede exigem " +
            "auditoria de terceiros), GV.RR (papéis/autoridades e revisão periódica de administradores) e GV.PO " +
            "(política aprovada e revisada).",
        AuditorScope.Identify =>
            "ESCOPO: IDENTIFY (ID). Foque em ID.AM (inventário — EDR ativo, SO suportado) e ID.RA (gestão de " +
            "vulnerabilidades). Ativo sem EDR ou em fim de vida é exposição.",
        _ => "ESCOPO: GLOBAL.",
    };

    /// <summary>
    /// Extrai a resposta roteada do texto do LLM. RESILIENTE (Tolerância Zero na UX): se a IA não devolver
    /// JSON válido, trata a conclusão inteira como uma resposta COPILOT — o chat nunca quebra por formatação.
    /// </summary>
    private static ChatRouterJson ParseRouter(string raw)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ChatRouterJson>(ExtractJson(raw), Json);
            if (dto is not null && !string.IsNullOrWhiteSpace(dto.message))
                return dto;
        }
        catch (JsonException) { /* cai no fallback resiliente abaixo */ }

        return new ChatRouterJson("COPILOT", raw.Trim(), null);
    }

    // ---- transport --------------------------------------------------------

    private async Task<string> CompleteTextAsync(string system, string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new AiUnavailableException(
                "Motor de IA não configurado: defina Ai:ApiKey via 'dotnet user-secrets' " +
                "(ou registre outra implementação de IAiAssessmentService).");

        var body = new
        {
            model = _opt.Model,
            max_tokens = _opt.MaxTokens,
            system,
            messages = new[] { new { role = "user", content = user } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.BaseUrl) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-api-key", _opt.ApiKey);
        req.Headers.Add("anthropic-version", _opt.AnthropicVersion);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var text = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString()));
        return text ?? "";
    }

    private async Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct)
    {
        var text = await CompleteTextAsync(system, user, ct);
        var json = ExtractJson(text);
        return JsonSerializer.Deserialize<T>(json, Json)
            ?? throw new InvalidOperationException("AI returned no parseable JSON.");
    }

    /// <summary>Strip markdown fences and isolate the first JSON object/array in the text.</summary>
    private static string ExtractJson(string text)
    {
        var t = text.Replace("```json", "").Replace("```", "").Trim();
        int start = t.IndexOfAny(new[] { '{', '[' });
        int end = t.LastIndexOfAny(new[] { '}', ']' });
        return (start >= 0 && end > start) ? t[start..(end + 1)] : t;
    }

    // ---- raw JSON shapes ----
    private record DocAnalysisJson(string? summary, List<ClaimJson>? claims);
    private record ClaimJson(string? subcategoryCode, string? claim, double confidence);
    private record MaturityJson(int currentLevel, double confidence, string? rationale);
    private record InterviewJson(string? question, string? targetSubcategoryCode, bool isComplete);
    private record ActionJson(string? subcategoryCode, string? what, string? how, string? priority);
    private record SignalJson(string? signalKey, double? numericValue, string? unit, int? severity, List<string>? mappedSubcategoryCodes);
    private record ChatRouterJson(string? intent, string? message, string? targetSubcategoryCode);
    private record AdvisoryJson(string? title, string? documentedRisk, string? technicalSteps);
}
