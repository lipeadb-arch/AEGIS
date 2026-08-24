using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// <see cref="ILLMClient"/> de produção sobre a Anthropic Messages API (Claude, modelo pinado). É o ÚNICO
/// adaptador de provedor externo do MVP e vive isolado na Infrastructure — nenhum tipo Anthropic vaza para
/// controllers, workers, domínio ou frontend. É transporte PURO: mapeia o par system+user prompt para o
/// schema <c>/v1/messages</c> e devolve o texto BRUTO (concatenado dos blocos <c>content[].type == "text"</c>).
/// A remoção de cercas markdown e a desserialização do contrato estruturado permanecem nos serviços que
/// conhecem o contrato (<see cref="AegisAssessmentService"/>, <see cref="AegisAiEvaluatorService"/>) — este
/// cliente, como o stub, não conhece a forma de saída.
///
/// O acesso a este cliente é SEMPRE mediado pelo <see cref="TenantScopedLlmRouter"/> (gate do Free Tier):
/// nenhum consumidor o injeta diretamente para ignorar a fronteira de dados.
/// </summary>
public sealed class AnthropicLlmClient : ILLMClient
{
    /// <summary>
    /// Versão FIXA da Anthropic Messages API (cabeçalho obrigatório <c>anthropic-version</c>). É o contrato
    /// de compatibilidade da API — pinado no código, não configurável, para não depender de valor externo.
    /// </summary>
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly AiOptions _opt;

    public AnthropicLlmClient(HttpClient http, IOptions<AiOptions> opt)
    {
        _http = http;
        _opt = opt.Value;
    }

    public async Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new AiUnavailableException(
                "Motor de IA não configurado: defina Ai:ApiKey via 'dotnet user-secrets' ou variável de ambiente.");

        // Schema /v1/messages: model + max_tokens + system (string top-level) + messages[] com um bloco user.
        // O contrato de prompt do AEGIS NÃO muda para acomodar o Claude: o systemPrompt já pronto vai no campo
        // "system" e o userPrompt vira o único conteúdo da mensagem user. Os parâmetros de amostragem
        // (temperature/top_p/top_k) são OMITIDOS de propósito — preserva o comportamento padrão do modelo, como
        // no adaptador anterior, sem inflar consumo nem alterar a engenharia de prompt existente.
        var body = new
        {
            model = _opt.Model,
            max_tokens = _opt.MaxOutputTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } },
        };

        // Autenticação no cabeçalho x-api-key, NUNCA na query string: evita vazar a chave em logs de acesso,
        // proxies e telemetria de URL (hardening alinhado ao Secure by Design do AEGIS). anthropic-version é
        // obrigatório. O endpoint completo é o próprio BaseUrl (nada é anexado em runtime).
        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.BaseUrl) { Content = JsonContent.Create(body) };
        req.Headers.Add("x-api-key", _opt.ApiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);

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
            // Timeout POR TENTATIVA do Polly (única autoridade de timeout, 120s): a chamada excedeu o teto.
            // Traduz para indisponibilidade CONHECIDA (→ 503 no middleware; o worker persiste
            // AiUnavailableException, que o frontend já traduz). NÃO é cancelamento do chamador e NÃO cai para
            // stub. Mensagem interna sanitizada — nunca registra prompt, documento, resposta ou chave.
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

            // 429 é a cota/rate limit esgotada — caso distinto, com mensagem própria (o cliente vê "cota
            // esgotada", não um 503 genérico). Os retries transitórios já ocorreram no handler de resiliência.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AiQuotaExhaustedException(
                    "Cota da IA esgotada (HTTP 429). Aguarde a renovação da cota ou use o modo simulado.");

            // EnsureSuccessStatusCode lançaria HttpRequestException crua (→ 500). Sob a ótica do consumidor,
            // qualquer não-2xx é indisponibilidade do motor (401/403 chave, 404 modelo aposentado/inexistente,
            // 400 requisição, 5xx) — mapeamos para AiUnavailableException (503). O detalhe fica no log, nunca no
            // cliente. A resposta bruta NÃO é registrada aqui (pode conter dados do prompt); só o status e o modelo.
            if (!resp.IsSuccessStatusCode)
                throw new AiUnavailableException(
                    $"Motor de IA respondeu HTTP {(int)resp.StatusCode} para o modelo '{_opt.Model}'. " +
                    "Verifique Ai:Model/BaseUrl/ApiKey.");

            using var doc = JsonDocument.Parse(raw);
            return ExtractText(doc.RootElement);
        }
    }

    /// <summary>
    /// Extrai o texto da resposta da Messages API de forma defensiva. A resposta usa <c>content[]</c>, um ARRAY
    /// de blocos: NÃO se presume que <c>content[0]</c> seja o único bloco nem que seja de texto (a resposta pode
    /// intercalar blocos <c>text</c> com blocos não-textuais). Concatena o texto de TODOS os blocos com
    /// <c>type == "text"</c> na ordem. Se não houver conteúdo textual avaliável (ex.: resposta sem bloco de
    /// texto, ou <c>stop_reason == "max_tokens"</c> cortou antes de emitir texto) → falha explícita
    /// (<see cref="AiUnavailableException"/>) em vez de retornar vazio, para o consumidor degradar de forma
    /// controlada (fallback determinístico). A razão de parada entra na mensagem para diagnóstico; o corpo NÃO.
    /// </summary>
    private string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.ValueEquals("text")
                    && block.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
            }

            if (sb.Length > 0)
                return sb.ToString();
        }

        var reason = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString()
            : "sem blocos de texto na resposta";
        throw new AiUnavailableException($"Motor de IA não retornou conteúdo avaliável ({reason}).");
    }
}
