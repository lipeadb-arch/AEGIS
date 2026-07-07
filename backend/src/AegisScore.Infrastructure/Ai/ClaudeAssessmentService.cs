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
}
