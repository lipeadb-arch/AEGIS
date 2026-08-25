using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Connectors.Microsoft.Defender;

/// <summary>Natureza de uma falha ao falar com a API do Microsoft Defender for Endpoint (traduzida pelo conector).</summary>
public enum DefenderApiErrorKind
{
    AuthFailure,
    InsufficientPermission,
    Throttled,
    NotFound,
    Unavailable,
}

/// <summary>Falha SANITIZADA de acesso à API do Defender (nunca carrega token/segredo/URL/PII/payload na mensagem).</summary>
public sealed class DefenderApiException : Exception
{
    public DefenderApiErrorKind Kind { get; }
    public DefenderApiException(DefenderApiErrorKind kind, string? detail = null) : base(detail) => Kind = kind;
}

/// <summary>Credenciais app-only (client credentials) para o Defender — mesma forma do Entra, recurso DIFERENTE.</summary>
public interface IDefenderCredentials
{
    string AzureTenantId { get; }
    string ClientId { get; }
    string ClientSecret { get; }
}

/// <summary>
/// [AEGIS-MVP-VULN-01] Transporte de baixo nível da API do Microsoft Defender for Endpoint (client credentials +
/// GET paginado). É o seam testável por HTTP simulado: mockando o <see cref="HttpClient"/> (via HttpMessageHandler)
/// o protocolo real é exercido — forma do token, header Bearer, paginação por <c>@odata.nextLink</c> e/ou
/// <c>$top</c>+<c>$skip</c>, e a classificação de 401/403/404/429/5xx.
/// </summary>
public interface IDefenderApiClient
{
    Task<string> AcquireTokenAsync(IDefenderCredentials creds, CancellationToken ct);

    /// <summary>
    /// Compatibilidade para consumidores que ainda precisam materializar a coleção inteira. O conector real de
    /// vulnerabilidades usa <see cref="StreamAllPagesAsync"/> para não reter o JSON bruto de todas as páginas.
    /// </summary>
    Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(
        string token, string relativeUrl, bool notFoundAsEmpty, CancellationToken ct);

    /// <summary>
    /// Percorre integralmente o endpoint, mas libera cada página depois que seus itens foram consumidos. A
    /// implementação default preserva compatibilidade com fakes antigos; <see cref="DefenderApiClient"/> fornece
    /// a implementação realmente streaming usada em produção.
    /// </summary>
    async IAsyncEnumerable<JsonElement> StreamAllPagesAsync(
        string token, string relativeUrl, bool notFoundAsEmpty,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var all = await GetAllPagesAsync(token, relativeUrl, notFoundAsEmpty, ct);
        foreach (var item in all)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>
    /// Leitura MÍNIMA (<c>$top=1</c>) usada pelo teste de conexão para PROVAR uma permissão específica sem coletar
    /// o dataset inteiro. Sucesso = 200 (ou 404 quando <paramref name="notFoundAsEmpty"/>). Qualquer outro status
    /// vira <see cref="DefenderApiException"/> classificada.
    /// </summary>
    Task ProbeAsync(string token, string relativeUrl, bool notFoundAsEmpty, CancellationToken ct);
}

/// <inheritdoc cref="IDefenderApiClient"/>
public sealed class DefenderApiClient : IDefenderApiClient
{
    // Origens OFICIAIS e CONSTANTES. O tenant NUNCA fornece base URL nem destino de requisição — impede
    // exfiltrar o bearer para uma origem arbitrária via @odata.nextLink forjado. ⚠️ O HOST das chamadas é
    // api.security.microsoft.com, mas o RECURSO do token é o LEGADO api.securitycenter.microsoft.com: uma
    // audiência divergente faz a API devolver 403 mesmo com o host certo (doc oficial). São constantes distintas.
    private const string LoginBaseUrl = "https://login.microsoftonline.com";
    private const string TokenResource = "https://api.securitycenter.microsoft.com/.default";
    private const string ApiBaseUrl = "https://api.security.microsoft.com";
    private const string ApiHost = "api.security.microsoft.com";

    // Página deliberadamente menor que o máximo da API. Em tenants grandes, 8k registros por página geravam um
    // pico desnecessário de DOM JSON + clones. O streaming torna o custo O(tamanho-da-página), não O(dataset bruto).
    private const int PageSize = 1000;
    private const int DefaultMaxPages = 2000;       // 1000 itens/página mantém capacidade equivalente ao teto antigo
    private const int DefaultMaxItems = 2_000_000;  // teto defensivo total por endpoint

    private readonly HttpClient _http;
    private readonly int _maxPages;
    private readonly int _maxItems;
    private readonly int _pageSize;

    public DefenderApiClient(HttpClient http) : this(http, DefaultMaxPages, DefaultMaxItems, PageSize) { }

    /// <summary>
    /// Construtor com tetos + tamanho de página injetáveis — SOMENTE para teste (exercita limites e a transição
    /// nextLink→$skip sem milhões de itens reais). É <c>internal</c> de propósito: nada disso vem do
    /// <c>ConnectorConfig</c>/tenant, e a DI só enxerga o construtor público de um argumento.
    /// </summary>
    internal DefenderApiClient(HttpClient http, int maxPages, int maxItems, int pageSize)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
        _maxItems = maxItems > 0 ? maxItems : DefaultMaxItems;
        _pageSize = pageSize > 0 ? pageSize : PageSize;
    }

    public async Task<string> AcquireTokenAsync(IDefenderCredentials creds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(creds.AzureTenantId)
            || string.IsNullOrWhiteSpace(creds.ClientId)
            || string.IsNullOrWhiteSpace(creds.ClientSecret))
            throw new DefenderApiException(DefenderApiErrorKind.AuthFailure, "credenciais app-only incompletas");

        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(creds.AzureTenantId)}/oauth2/v2.0/token";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["scope"] = TokenResource,
            ["grant_type"] = "client_credentials",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DefenderApiException(Classify(resp.StatusCode), $"token endpoint retornou {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DefenderApiException(DefenderApiErrorKind.AuthFailure, "resposta do token não é JSON válido");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("access_token", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() is { Length: > 0 } token)
                return token;
        }
        throw new DefenderApiException(DefenderApiErrorKind.AuthFailure, "resposta do token sem access_token válido");
    }

    public async Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(
        string token, string relativeUrl, bool notFoundAsEmpty, CancellationToken ct)
    {
        // Mantido para compatibilidade/testes. Produção usa StreamAllPagesAsync e normaliza item a item.
        var results = new List<JsonElement>();
        await foreach (var item in StreamAllPagesAsync(token, relativeUrl, notFoundAsEmpty, ct))
            results.Add(item);
        return results;
    }

    public async IAsyncEnumerable<JsonElement> StreamAllPagesAsync(
        string token, string relativeUrl, bool notFoundAsEmpty,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = BuildUrl(relativeUrl, top: _pageSize, skip: null);
        var pages = 0;
        var consumed = 0;

        while (!string.IsNullOrEmpty(next))
        {
            ct.ThrowIfCancellationRequested();

            if (pages >= _maxPages)
                throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "limite de paginação atingido com páginas restantes");
            if (!visited.Add(next!))
                throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "ciclo/repetição de página detectado na paginação");

            ValidateDefenderUrl(next!);

            string body;
            try
            {
                body = await SendGetAsync(token, next!, ct);
            }
            catch (DefenderApiException ex) when (ex.Kind == DefenderApiErrorKind.NotFound && notFoundAsEmpty && pages == 0)
            {
                yield break;
            }
            pages++;

            // ParsePage materializa SOMENTE a página atual. Depois do último yield desta página, os clones e o
            // corpo bruto ficam elegíveis para GC antes da próxima requisição.
            var (items, nextLink) = ParsePage(body);
            foreach (var item in items)
            {
                consumed++;
                if (consumed > _maxItems)
                    throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "limite de itens processados excedido");
                yield return item;
            }

            if (!string.IsNullOrEmpty(nextLink))
            {
                next = nextLink;
            }
            else if (items.Count >= _pageSize)
            {
                next = BuildUrl(relativeUrl, top: _pageSize, skip: consumed);
            }
            else
            {
                next = null;
            }
        }
    }

    public async Task ProbeAsync(string token, string relativeUrl, bool notFoundAsEmpty, CancellationToken ct)
    {
        var url = BuildUrl(relativeUrl, top: 1, skip: null);
        ValidateDefenderUrl(url);
        try
        {
            var body = await SendGetAsync(token, url, ct);
            ParsePage(body);
        }
        catch (DefenderApiException ex) when (ex.Kind == DefenderApiErrorKind.NotFound && notFoundAsEmpty)
        {
            // 404 aqui (ex.: tenant sem máquinas) confirma a PERMISSÃO — a leitura foi autorizada, só não há dado.
        }
    }

    // ---- Construção e validação de destino ---------------------------------------------------------

    private static string BuildUrl(string relativeUrl, int top, int? skip)
    {
        if (relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return relativeUrl;
        var baseUrl = $"{ApiBaseUrl}/{relativeUrl.TrimStart('/')}";
        var sep = relativeUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{sep}$top={top}";
        if (skip is { } s && s > 0) url += $"&$skip={s}";
        return url;
    }

    private static void ValidateDefenderUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "destino de requisicao reprovado pela allowlist do Defender");
        }
    }

    private async Task<string> SendGetAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DefenderApiException(Classify(resp.StatusCode), $"defender retornou {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Parse estrutural FAIL-CLOSED de UMA página. Os clones sobrevivem ao dispose do documento, mas deixam de ser
    /// retidos assim que a página é consumida pelo streaming.
    /// </summary>
    private static (List<JsonElement> Items, string? NextLink) ParsePage(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "resposta do Defender nao e JSON valido");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("value", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "pagina do Defender sem o array value esperado");

            var items = new List<JsonElement>();
            foreach (var item in arr.EnumerateArray())
                items.Add(item.Clone());

            string? nextLink;
            if (root.TryGetProperty("@odata.nextLink", out var nl))
            {
                nextLink = nl.ValueKind switch
                {
                    JsonValueKind.String => nl.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "@odata.nextLink com tipo invalido"),
                };
            }
            else
            {
                nextLink = null;
            }

            return (items, nextLink);
        }
    }

    private static DefenderApiErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => DefenderApiErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => DefenderApiErrorKind.InsufficientPermission,
        HttpStatusCode.NotFound => DefenderApiErrorKind.NotFound,
        HttpStatusCode.TooManyRequests => DefenderApiErrorKind.Throttled,
        _ => DefenderApiErrorKind.Unavailable,
    };
}
