using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] A resposta chegou, mas não segue o contrato documentado (corpo que não é
    /// JSON, página sem a lista <c>value</c>, próximo link inválido ou fora do host). Não é indisponibilidade do serviço.
    /// </summary>
    Contract,

    /// <summary>[AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] A leitura parou no teto de paginação do AEGIS com páginas restantes.</summary>
    Incomplete,
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

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Identificador da requisição devolvido pelo serviço (<c>request-id</c> do
    /// Graph, <c>x-ms-request-id</c> do Resource Manager): só um GUID, útil para o suporte do fornecedor, nunca um segredo.
    /// </summary>
    public string? ServiceRequestId { get; }

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] O QUE fugiu do contrato documentado ("objeto settings ausente", "página sem a lista
    /// value"), em texto do PRÓPRIO AEGIS — nunca texto da fonte. Entra no motivo da falha de contrato, para que objeto
    /// ausente, propriedade ausente e tipo incompatível não cheguem à tela com a mesma frase genérica.
    /// </summary>
    public string? ContractDetail { get; init; }

    /// <summary>
    /// [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Códigos numéricos do Microsoft Entra ID na recusa de um token
    /// (<c>error_codes</c>, apresentados como <c>AADSTS…</c>). Só dígitos — nunca a descrição, que cita a aplicação.
    /// </summary>
    public IReadOnlyList<string> OAuthErrorCodes { get; init; } = Array.Empty<string>();

    /// <summary>[AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] <c>correlation_id</c> devolvido pelo endpoint de token (só GUID).</summary>
    public string? ServiceCorrelationId { get; init; }

    /// <summary>[AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Recurso do token recusado (constante do AEGIS, nunca texto da fonte).</summary>
    public string? TokenResource { get; init; }

    /// <summary>
    /// [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Recusa do endpoint de token com os metadados OAuth SEGUROS: <c>error</c>,
    /// <c>error_codes</c>, <c>trace_id</c> e <c>correlation_id</c>. <c>error_description</c>, o corpo e qualquer outro campo
    /// nunca atravessam esta fronteira.
    /// </summary>
    internal static EntraGraphException TokenRefused(
        EntraGraphErrorKind kind, HttpResponseMessage response, string? body, string? resource)
    {
        var oauth = OAuthTokenError.Read(body);
        return new EntraGraphException(kind, $"token endpoint retornou {(int)response.StatusCode}", (int)response.StatusCode,
            oauth.Error, "/oauth2/v2.0/token", oauth.TraceId ?? RequestIdOf(response))
        {
            OAuthErrorCodes = oauth.Codes,
            ServiceCorrelationId = oauth.CorrelationId,
            TokenResource = resource,
        };
    }

    public EntraGraphException(
        EntraGraphErrorKind kind,
        string? detail = null,
        int? httpStatusCode = null,
        string? graphErrorCode = null,
        string? endpointPath = null,
        string? serviceRequestId = null) : base(detail)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
        GraphErrorCode = SanitizeCode(graphErrorCode);
        EndpointPath = SanitizePath(endpointPath);
        ServiceRequestId = SanitizeRequestId(serviceRequestId);
    }

    /// <summary>Só um GUID passa; qualquer outro valor de cabeçalho é descartado.</summary>
    internal static string? SanitizeRequestId(string? value) =>
        Guid.TryParse(value?.Trim(), out var id) ? id.ToString("D") : null;

    /// <summary>O identificador da requisição nos cabeçalhos da resposta (Graph, Resource Manager, Fabric).</summary>
    internal static string? RequestIdOf(HttpResponseMessage response)
    {
        foreach (var name in new[] { "request-id", "x-ms-request-id", "RequestId" })
            if (response.Headers.TryGetValues(name, out var values))
                foreach (var v in values)
                    if (SanitizeRequestId(v) is { } id) return id;
        return null;
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
/// [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Campos estruturados e seguros da resposta de erro do endpoint de token do
/// Microsoft Entra ID. Cada um é validado por forma: <c>error</c> em minúsculas e sublinhado, códigos só com dígitos,
/// identificadores só GUID. O que não passa é descartado — e a descrição nunca é lida.
/// </summary>
internal sealed record OAuthTokenError(string? Error, IReadOnlyList<string> Codes, string? TraceId, string? CorrelationId)
{
    private static readonly OAuthTokenError None = new(null, Array.Empty<string>(), null, null);

    public static OAuthTokenError Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 64_000) return None;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return None;
            string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                && e.GetString() is { Length: > 0 and <= 64 } s && s.All(c => c is (>= 'a' and <= 'z') or '_')
                ? s
                : null;
            var codes = new List<string>();
            if (root.TryGetProperty("error_codes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var n) && n is > 0 and < 100_000_000 && codes.Count < 5)
                        codes.Add($"AADSTS{n}");
            return new OAuthTokenError(error, codes.Distinct().ToList(), Guid(root, "trace_id"), Guid(root, "correlation_id"));
        }
        catch (JsonException)
        {
            return None;
        }
    }

    private static string? Guid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? EntraGraphException.SanitizeRequestId(v.GetString())
            : null;
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

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Campos do pedido de token; um certificado ilegível vira falha de AUTENTICAÇÃO declarada,
    /// nunca uma exceção sem categoria.
    /// </summary>
    internal static Dictionary<string, string> CredentialFields(IMicrosoftGraphCredentials config, string scope)
    {
        try
        {
            return MicrosoftClientCredentialForm.Fields(config, scope);
        }
        catch (MicrosoftCertificateException ex)
        {
            throw new EntraGraphException(EntraGraphErrorKind.AuthFailure, ex.Message, endpointPath: "/oauth2/v2.0/token");
        }
    }

    public async Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct)
    {
        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(config.AzureTenantId)}/oauth2/v2.0/token";
        // [AEGIS-KNIGHT-COVERAGE-04] Segredo OU certificado: o corpo vem do formulário compartilhado.
        var form = new FormUrlEncodedContent(CredentialFields(config, $"{GraphBaseUrl}/.default"));

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] O endpoint de token devolve 400 para credencial recusada (invalid_client,
            // unauthorized_client): é falha de AUTENTICAÇÃO, não indisponibilidade — os outros clientes de token já tratavam
            // assim. O código OAuth curto acompanha o diagnóstico; a descrição (que cita a aplicação) nunca.
            // [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Também os códigos AADSTS e os identificadores trace/correlação.
            throw EntraGraphException.TokenRefused(ClassifyToken(resp.StatusCode), resp, body, "Microsoft Graph");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new EntraGraphException(EntraGraphErrorKind.Contract, "resposta do token não é JSON válido", (int)resp.StatusCode,
                endpointPath: "/oauth2/v2.0/token");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("access_token", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() is { Length: > 0 } token)
                return token;
        }
        throw new EntraGraphException(EntraGraphErrorKind.Contract, "resposta do token sem access_token válido", (int)resp.StatusCode,
            endpointPath: "/oauth2/v2.0/token");
    }

    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string token, IMicrosoftGraphCredentials config, string relativeUrl, [EnumeratorCancellation] CancellationToken ct)
    {
        var next = BuildGraphUrl(relativeUrl);
        var pages = 0;
        while (!string.IsNullOrEmpty(next))
        {
            if (pages >= _maxPages)
                throw new EntraGraphException(EntraGraphErrorKind.Incomplete, "limite de paginação atingido com páginas restantes",
                    endpointPath: PathOf(next));

            ValidateGraphUrl(next!);
            var body = await SendGetAsync(token, next!, ct);
            pages++;

            var pageItems = new List<JsonElement>();
            string? nextLink;
            using (var doc = ParseOrThrow(body, next!))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("value", out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                    throw new EntraGraphException(EntraGraphErrorKind.Contract,
                        "pagina do Graph sem o array value esperado", 200, endpointPath: PathOf(next));

                foreach (var item in arr.EnumerateArray())
                    pageItems.Add(item.Clone());

                if (root.TryGetProperty("@odata.nextLink", out var nl))
                {
                    nextLink = nl.ValueKind switch
                    {
                        JsonValueKind.String => nl.GetString(),
                        JsonValueKind.Null => null,
                        _ => throw new EntraGraphException(EntraGraphErrorKind.Contract,
                            "@odata.nextLink com tipo invalido", 200, endpointPath: PathOf(next)),
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
        using var doc = ParseOrThrow(body, url);
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
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Um próximo link fora do host oficial é resposta fora do contrato.
            throw new EntraGraphException(EntraGraphErrorKind.Contract, "destino de requisicao reprovado pela allowlist do Microsoft Graph");
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
                endpointPath,
                EntraGraphException.RequestIdOf(resp));
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

    private static JsonDocument ParseOrThrow(string body, string url)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new EntraGraphException(EntraGraphErrorKind.Contract, "resposta do Graph nao e JSON valido", 200, endpointPath: PathOf(url));
        }
    }

    private static string? PathOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : null;

    /// <summary>Endpoint de token: 400 e 401 são credencial recusada; 429 é limite; o resto, indisponibilidade.</summary>
    internal static EntraGraphErrorKind ClassifyToken(HttpStatusCode code) => code switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        _ => Classify(code),
    };

    private static EntraGraphErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => EntraGraphErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => EntraGraphErrorKind.Throttled,
        _ => EntraGraphErrorKind.Unavailable,
    };
}
