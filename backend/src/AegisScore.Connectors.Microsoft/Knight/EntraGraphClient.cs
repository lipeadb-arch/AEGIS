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

/// <summary>Falha SANITIZADA de acesso ao Graph (nunca carrega token/segredo/PII na mensagem).</summary>
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
    Task<string> AcquireTokenAsync(KnightEntraIdConfiguration config, CancellationToken ct);
    IAsyncEnumerable<JsonElement> GetPagedAsync(string token, KnightEntraIdConfiguration config, string relativeUrl, CancellationToken ct);
    Task<JsonElement> GetJsonAsync(string token, KnightEntraIdConfiguration config, string relativeUrl, CancellationToken ct);
}

/// <inheritdoc cref="IEntraGraphClient"/>
public sealed class EntraGraphClient : IEntraGraphClient
{
    private const string DefaultLogin = "https://login.microsoftonline.com";
    private const string DefaultGraph = "https://graph.microsoft.com";
    private const int MaxPages = 200;   // teto defensivo de paginação

    private readonly HttpClient _http;

    public EntraGraphClient(HttpClient http) => _http = http;

    public async Task<string> AcquireTokenAsync(KnightEntraIdConfiguration config, CancellationToken ct)
    {
        var login = (config.LoginBaseUrl ?? DefaultLogin).TrimEnd('/');
        var graph = (config.GraphBaseUrl ?? DefaultGraph).TrimEnd('/');
        var url = $"{login}/{Uri.EscapeDataString(config.AzureTenantId)}/oauth2/v2.0/token";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = $"{graph}/.default",
            ["grant_type"] = "client_credentials",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new EntraGraphException(Classify(resp.StatusCode), $"token endpoint retornou {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("access_token", out var t) && t.GetString() is { Length: > 0 } token)
            return token;
        throw new EntraGraphException(EntraGraphErrorKind.AuthFailure, "resposta do token sem access_token");
    }

    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string token, KnightEntraIdConfiguration config, string relativeUrl, [EnumeratorCancellation] CancellationToken ct)
    {
        var graph = (config.GraphBaseUrl ?? DefaultGraph).TrimEnd('/');
        var next = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl
            : $"{graph}/v1.0/{relativeUrl.TrimStart('/')}";

        var pages = 0;
        while (!string.IsNullOrEmpty(next) && pages++ < MaxPages)
        {
            var body = await SendGetAsync(token, next, ct);

            // Extrai clones ANTES de dispor o documento — clones sobrevivem ao dispose (evita element inválido).
            var pageItems = new List<JsonElement>();
            string? nextLink;
            using (var doc = JsonDocument.Parse(body))
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

    public async Task<JsonElement> GetJsonAsync(string token, KnightEntraIdConfiguration config, string relativeUrl, CancellationToken ct)
    {
        var graph = (config.GraphBaseUrl ?? DefaultGraph).TrimEnd('/');
        var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativeUrl
            : $"{graph}/v1.0/{relativeUrl.TrimStart('/')}";
        var body = await SendGetAsync(token, url, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task<string> SendGetAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new EntraGraphException(Classify(resp.StatusCode), $"graph retornou {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static EntraGraphErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => EntraGraphErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => EntraGraphErrorKind.Throttled,
        _ => EntraGraphErrorKind.Unavailable,
    };
}
