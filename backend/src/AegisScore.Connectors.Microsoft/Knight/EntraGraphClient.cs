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

/// <summary>
/// Falha SANITIZADA de acesso ao Graph. Expõe somente metadados operacionais seguros para diagnóstico:
/// status HTTP, código de erro do Graph e caminho do endpoint (sem query string). Nunca carrega token,
/// segredo, URL completa, mensagem bruta, PII ou payload.
/// </summary>
public sealed class EntraGraphException : Exception
{
    public EntraGraphErrorKind Kind { get; }
    public int? HttpStatusCode { get; }
    public string? GraphErrorCode { get; }
    public string? EndpointPath { get; }

    public EntraGraphException(
        EntraGraphErrorKind kind,
        string? detail = null,
        int? httpStatusCode = null,
        string? graphErrorCode = null,
        string? endpointPath = null) : base(detail)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
        GraphErrorCode = SanitizeCode(graphErrorCode);
        EndpointPath = SanitizePath(endpointPath);
    }

    private static string? SanitizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 96) trimmed = trimmed[..96];
        foreach (var ch in trimmed)
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')) return null;
        return trimmed;
    }

    private static string? SanitizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        return path.StartsWith('/') && path.Length <= 180 ? path : null;
    }
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
    private const string LoginBaseUrl = "https://login.microsoftonline.com";
    private const string GraphBaseUrl = "https://graph.microsoft.com";
    private const string GraphHost = "graph.microsoft.com";
    private const int DefaultMaxPages = 200;

    private readonly HttpClient _http;
    private readonly int _maxPages;

    public EntraGraphClient(HttpClient http) : this(http, DefaultMaxPages) { }

    internal EntraGraphClient(HttpClient http, int maxPages)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
    }

    public async Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct)
    {
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
            throw new EntraGraphException(
                Classify(resp.StatusCode),
                $"token endpoint retornou {(int)resp.StatusCode}",
                (int)resp.StatusCode,
                endpointPath: "/oauth2/v2.0/token");

        var body = await resp.Content.ReadAsStringAsync(ct);
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
            if (pages >= _maxPages)
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "limite de paginação atingido com páginas restantes");

            ValidateGraphUrl(next!);
            var body = await SendGetAsync(token, next!, ct);
            pages++;

            var pageItems = new List<JsonElement>();
            string? nextLink;
            using (var doc = ParseOrThrow(body))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("value", out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                    throw new EntraGraphException(EntraGraphErrorKind.Unavailable,
                        "pagina do Graph sem o array value esperado");

                foreach (var item in arr.EnumerateArray())
                    pageItems.Add(item.Clone());

                if (root.TryGetProperty("@odata.nextLink", out var nl))
                {
                    nextLink = nl.ValueKind switch
                    {
                        JsonValueKind.String => nl.GetString(),
                        JsonValueKind.Null => null,
                        _ => throw new EntraGraphException(EntraGraphErrorKind.Unavailable,
                            "@odata.nextLink com tipo invalido"),
                    };
                }
                else
                {
                    nextLink = null;
                }
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

    private static string BuildGraphUrl(string relativeUrl) =>
        relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl
            : $"{GraphBaseUrl}/v1.0/{relativeUrl.TrimStart('/')}";

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
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var endpointPath = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : null;
            throw new EntraGraphException(
                Classify(resp.StatusCode),
                $"graph retornou {(int)resp.StatusCode}",
                (int)resp.StatusCode,
                TryReadGraphErrorCode(body),
                endpointPath);
        }
        return body;
    }

    private static string? TryReadGraphErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64_000) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String)
                return null;
            return code.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
