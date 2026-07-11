using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// <see cref="ILLMClient"/> de produção sobre a REST API do Google Gemini (modelo Flash, baixa latência
/// de inferência). Substitui o <see cref="StubLlmClient"/>. É transporte PURO: mapeia o par system+user
/// prompt para o schema <c>generateContent</c> e devolve o texto BRUTO do candidato. A remoção de cercas
/// markdown e a desserialização do veredito estruturado (ComplianceVerdict) permanecem no
/// <see cref="AegisAiEvaluatorService"/> — este cliente, como o stub, não conhece o contrato de saída.
/// </summary>
public sealed class GeminiLlmClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly AegisAiOptions _opt;

    public GeminiLlmClient(HttpClient http, IOptions<AegisAiOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
    }

    public async Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new AiUnavailableException(
                "Motor Gemini não configurado: defina AegisAi:ApiKey via 'dotnet user-secrets'.");

        // Schema generateContent do Gemini: system_instruction (snake_case) + contents[].parts[].text.
        // Nomes propositalmente em lower/snake_case — são o contrato literal da API, não estilo C#.
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } }
        };

        // Chave no cabeçalho x-goog-api-key, NÃO na query string: evita vazá-la em logs de acesso,
        // proxies e telemetria de URL (hardening alinhado ao Secure by Design do AEGIS).
        var url = $"{_opt.BaseUrl}/{_opt.Model}:generateContent";

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-goog-api-key", _opt.ApiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            // Falha de transporte (DNS, conexão, TLS): o motor externo está inacessível — condição
            // OPERACIONAL, não um bug do servidor. Vira AiUnavailableException → 503 no middleware.
            throw new AiUnavailableException("Falha de transporte ao contatar o motor Gemini.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout do HttpClient (não o cancelamento do chamador) — igualmente transitório.
            throw new AiUnavailableException("Timeout ao contatar o motor Gemini.", ex);
        }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);

            // EnsureSuccessStatusCode lançaria HttpRequestException crua (→ 500). Sob a ótica do avaliador,
            // qualquer não-2xx é indisponibilidade do motor (404 modelo aposentado/inexistente, 401/403 chave,
            // 429 quota) — mapeamos para AiUnavailableException (503). O detalhe fica no log, nunca no cliente.
            if (!resp.IsSuccessStatusCode)
                throw new AiUnavailableException(
                    $"Motor Gemini respondeu HTTP {(int)resp.StatusCode} para o modelo '{_opt.Model}'. " +
                    $"Verifique AegisAi:Model/BaseUrl/ApiKey. Resposta: {Truncate(raw, 300)}");

            using var doc = JsonDocument.Parse(raw);
            return ExtractText(doc.RootElement);
        }
    }

    /// <summary>Corta o corpo de erro para o log não inchar (o cliente nunca vê este texto — 503 genérico).</summary>
    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Isola candidates[0].content.parts[0].text de forma defensiva: o Gemini pode devolver 200 OK SEM
    /// candidato quando o safety filter bloqueia (promptFeedback.blockReason). Sem texto → falha explícita
    /// (AiUnavailableException) em vez de NullReference, para o avaliador degradar de forma controlada.
    /// </summary>
    private static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array
            && parts.GetArrayLength() > 0
            && parts[0].TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "";
        }

        var reason = root.TryGetProperty("promptFeedback", out var fb) && fb.TryGetProperty("blockReason", out var br)
            ? br.GetString()
            : "sem candidatos na resposta";
        throw new AiUnavailableException($"Gemini não retornou conteúdo avaliável ({reason}).");
    }
}
