using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Microsoft.Knight;

/// <summary>Natureza de uma falha ao falar com o Microsoft Graph — o coletor a traduz em estado da fonte.</summary>
public enum EntraGraphErrorKind
{
    AuthFailure,
    InsufficientPermission,
    Throttled,
    Unavailable,
}

/// <summary>Falha SANITIZADA de acesso ao Graph (nunca carrega token/segredo/URL/PII/payload na mensagem).</summary>
public sealed class EntraGraphException : Exception
{
    public EntraGraphErrorKind Kind { get; }
    public EntraGraphException(EntraGraphErrorKind kind, string? detail = null) : base(detail) => Kind = kind;
}

/// <summary>
/// Transporte de baixo nível do Microsoft Graph (client credentials + GET paginado). É o seam testável por
/// HTTP simulado: mockando o <see cref="HttpClient"/> (via HttpMessageHandler) o protocolo real é exercido —
/// forma do token, header Bearer, paginação por <c>@odata.nextLink</c>, e a classificação de 401/403/429/5xx.
/// </summary>
public interface IEntraGraphClient
{
    Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct);
    IAsyncEnumerable<JsonElement> GetPagedAsync(string token, IMicrosoftGraphCredentials config, string relativeUrl, CancellationToken ct);
    Task<JsonElement> GetJsonAsync(string token, IMicrosoftGraphCredentials config, string relativeUrl, CancellationToken ct);
}

/// <inheritdoc cref="IEntraGraphClient"/>
public sealed class EntraGraphClient : IEntraGraphClient
{
    // Origens OFICIAIS (constantes). O tenant NUNCA fornece base URL nem destino de requisição — impede
    // exfiltrar o bearer token para uma origem arbitrária via @odata.nextLink forjado. Nuvens soberanas
    // ficam FORA desta entrega; um suporte futuro exigiria uma ALLOWLIST explícita de ambientes, nunca URL livre.
    private const string LoginBaseUrl = "https://login.microsoftonline.com";
    private const string GraphBaseUrl = "https://graph.microsoft.com";
    private const string GraphHost = "graph.microsoft.com";
    private const int DefaultMaxPages = 200;   // teto defensivo de paginação

    private readonly HttpClient _http;
    private readonly int _maxPages;

    public EntraGraphClient(HttpClient http) : this(http, DefaultMaxPages) { }

    /// <summary>
    /// Construtor com teto de paginação injetável — SOMENTE para teste (exercita o limite sem 200 páginas reais).
    /// É <c>internal</c> de propósito: o teto NUNCA vem do <c>ConnectorConfig</c>/tenant, e a DI só enxerga o
    /// construtor público de um argumento.
    /// </summary>
    internal EntraGraphClient(HttpClient http, int maxPages)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
    }

    public async Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct)
    {
        // URL do endpoint de token montada só a partir de CONSTANTE oficial + tenantId escapado — sem entrada livre.
        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(config.AzureTenantId)}/oauth2/v2.0/token";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = $"{GraphBaseUrl}/.default",
            ["grant_type"] = "client_credentials",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new EntraGraphException(Classify(resp.StatusCode), $"token endpoint retornou {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);

        // Parsing DEFENSIVO: JSON inválido ou access_token com tipo/valor inesperado vira AuthFailure SANITIZADA
        // — nenhuma JsonException/InvalidOperationException escapa para a API, e a mensagem não carrega corpo,
        // token, segredo ou URL. (Um servidor de token comprometido/errado não derruba a coleta com stack cru.)
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new EntraGraphException(EntraGraphErrorKind.AuthFailure, "resposta do token não é JSON válido");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("access_token", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() is { Length: > 0 } token)
                return token;
        }
        throw new EntraGraphException(EntraGraphErrorKind.AuthFailure, "resposta do token sem access_token válido");
    }

    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string token, IMicrosoftGraphCredentials config, string relativeUrl, [EnumeratorCancellation] CancellationToken ct)
    {
        var next = BuildGraphUrl(relativeUrl);

        var pages = 0;
        while (!string.IsNullOrEmpty(next))
        {
            // Teto de paginação atingido com páginas AINDA pendentes: NÃO truncamos em silêncio (isso viraria
            // conformidade/contagem com dados incompletos). A capacidade correspondente fica indisponível.
            if (pages >= _maxPages)
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "limite de paginação atingido com páginas restantes");

            // Valida o destino ANTES de criar/enviar a requisição — inclusive o @odata.nextLink devolvido pela
            // página anterior. Um nextLink de origem diferente é REPROVADO aqui: o bearer nunca chega a ele.
            ValidateGraphUrl(next!);
            var body = await SendGetAsync(token, next!, ct);
            pages++;

            // Extrai clones ANTES de dispor o documento — clones sobrevivem ao dispose (evita element inválido).
            var pageItems = new List<JsonElement>();
            string? nextLink;
            using (var doc = ParseOrThrow(body))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var item in arr.EnumerateArray())
                        pageItems.Add(item.Clone());
                nextLink = root.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
            }

            foreach (var it in pageItems)
                yield return it;

            next = nextLink;
        }
    }

    public async Task<JsonElement> GetJsonAsync(string token, IMicrosoftGraphCredentials config, string relativeUrl, CancellationToken ct)
    {
        var url = BuildGraphUrl(relativeUrl);
        ValidateGraphUrl(url);
        var body = await SendGetAsync(token, url, ct);
        using var doc = ParseOrThrow(body);
        return doc.RootElement.Clone();
    }

    // ---- Construção e validação de destino ---------------------------------------------------------

    /// <summary>Constrói a URL absoluta a partir de um caminho relativo (o coletor nunca passa URL absoluta).</summary>
    private static string BuildGraphUrl(string relativeUrl) =>
        relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl                                        // caso raro: validado por ValidateGraphUrl adiante
            : $"{GraphBaseUrl}/v1.0/{relativeUrl.TrimStart('/')}";

    /// <summary>
    /// Aceita SOMENTE HTTPS na MESMA origem oficial do Microsoft Graph. Rejeita mudança de esquema, host,
    /// porta, userinfo ou origem — antes de qualquer envio, para o bearer nunca sair para um destino reprovado.
    /// A exceção é SANITIZADA (não inclui a URL, o token nem o payload).
    /// </summary>
    private static void ValidateGraphUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, GraphHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "destino de requisicao reprovado pela allowlist do Microsoft Graph");
        }
    }

    private async Task<string> SendGetAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        // Fail-CLOSED de completude: SÓ 200 OK é resposta completa esperada. Qualquer outro status — inclusive
        // 206 Partial Content, que é 2xx mas NÃO é a resposta completa — vira falha sanitizada. Aceitar 206 como
        // sucesso permitiria reconciliar/resolver por omissão sobre uma fotografia incompleta.
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new EntraGraphException(Classify(resp.StatusCode), $"graph retornou {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Parse DEFENSIVO: JSON inválido do Graph vira falha SANITIZADA (sem corpo/token/URL/segredo).</summary>
    private static JsonDocument ParseOrThrow(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "resposta do Graph nao e JSON valido");
        }
    }

    private static EntraGraphErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => EntraGraphErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => EntraGraphErrorKind.Throttled,
        _ => EntraGraphErrorKind.Unavailable,
    };
}
