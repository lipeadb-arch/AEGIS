using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// <see cref="ILLMClient"/> de produção sobre a REST API do Google Gemini (modelo Flash pinado, baixa
/// latência de inferência). É o ÚNICO adaptador de provedor do MVP e vive isolado na Infrastructure —
/// nenhum tipo Gemini vaza para controllers, workers, domínio ou frontend. É transporte PURO: mapeia o par
/// system+user prompt para o schema <c>generateContent</c> e devolve o texto BRUTO do candidato. A remoção
/// de cercas markdown e a desserialização do contrato estruturado permanecem nos serviços que conhecem o
/// contrato (<see cref="AegisAssessmentService"/>, <see cref="AegisAiEvaluatorService"/>) — este cliente,
/// como o stub, não conhece a forma de saída.
///
/// O acesso a este cliente é SEMPRE mediado pelo <see cref="TenantScopedLlmRouter"/> (gate do Free Tier):
/// nenhum consumidor o injeta diretamente para ignorar a fronteira de dados.
/// </summary>
public sealed class GeminiLlmClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _opt;

    public GeminiLlmClient(HttpClient http, IOptions<AiOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
    }

    public async Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new AiUnavailableException(
                "Motor de IA não configurado: defina Ai:ApiKey via 'dotnet user-secrets' ou variável de ambiente.");

        // Schema generateContent do Gemini: system_instruction (snake_case) + contents[].parts[].text +
        // generationConfig (só o teto de tokens de saída). Nomes propositalmente em lower/snake_case — são
        // o contrato literal da API, não estilo C#. Os parâmetros de amostragem (temperature/topP/topK) são
        // OMITIDOS de propósito: a doc oficial do Gemini 3.x recomenda os valores padrão do modelo, e não
        // se envia thinking_budget (preserva o nível de raciocínio padrão, sem aumentar consumo de cota).
        var body = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            generationConfig = new
            {
                maxOutputTokens = _opt.MaxOutputTokens,
            },
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
            throw new AiUnavailableException("Falha de transporte ao contatar o motor de IA.", ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            // Timeout POR TENTATIVA do Polly (única autoridade de timeout, 120s): a chamada excedeu o teto —
            // era ele que vazava cru como categoria `TimeoutRejectedException` no worker. Traduz para
            // indisponibilidade CONHECIDA (→ 503 no middleware; o worker persiste AiUnavailableException, que
            // o frontend já traduz). NÃO é cancelamento do chamador e NÃO cai para stub. Mensagem interna
            // sanitizada — nunca registra prompt, documento, resposta ou chave.
            throw new AiUnavailableException("Timeout ao aguardar resposta do motor de IA.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout do HttpClient (não o cancelamento do chamador) — igualmente transitório.
            throw new AiUnavailableException("Timeout ao contatar o motor de IA.", ex);
        }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);

            // 429 é a cota gratuita esgotada — caso distinto, com mensagem própria (o cliente vê "cota
            // esgotada", não um 503 genérico). Os retries transitórios já ocorreram no handler de resiliência.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AiQuotaExhaustedException(
                    "Cota gratuita da IA esgotada (HTTP 429). Aguarde a renovação da cota ou use o modo simulado.");

            // EnsureSuccessStatusCode lançaria HttpRequestException crua (→ 500). Sob a ótica do consumidor,
            // qualquer não-2xx é indisponibilidade do motor (404 modelo aposentado/inexistente, 401/403 chave,
            // 5xx) — mapeamos para AiUnavailableException (503). O detalhe fica no log, nunca no cliente. A
            // resposta bruta NÃO é registrada aqui (pode conter dados do prompt); só o status e o modelo.
            if (!resp.IsSuccessStatusCode)
                throw new AiUnavailableException(
                    $"Motor de IA respondeu HTTP {(int)resp.StatusCode} para o modelo '{_opt.Model}'. " +
                    "Verifique Ai:Model/BaseUrl/ApiKey.");

            using var doc = JsonDocument.Parse(raw);
            return ExtractText(doc.RootElement);
        }
    }

    /// <summary>
    /// Isola candidates[0].content.parts[0].text de forma defensiva: o Gemini pode devolver 200 OK SEM
    /// candidato quando o safety filter bloqueia (promptFeedback.blockReason) ou quando o teto de tokens
    /// corta a saída antes de qualquer parte. Sem texto → falha explícita (AiUnavailableException) em vez de
    /// NullReference, para o consumidor degradar de forma controlada (fallback determinístico).
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
        throw new AiUnavailableException($"Motor de IA não retornou conteúdo avaliável ({reason}).");
    }
}
