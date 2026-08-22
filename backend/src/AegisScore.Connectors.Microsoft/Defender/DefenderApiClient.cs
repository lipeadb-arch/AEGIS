using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Coleta INTEGRAL e paginada de <paramref name="relativeUrl"/> (ex.: <c>api/machines</c>), materializada em
    /// memória. Segue <c>@odata.nextLink</c> quando presente e cai para <c>$top</c>+<c>$skip</c> quando a página
    /// veio cheia sem nextLink. Só 200 é página completa; um <c>404</c> na PRIMEIRA página, quando
    /// <paramref name="notFoundAsEmpty"/>, significa coleção COMPLETA vazia (ex.: tenant sem máquinas). Falha
    /// fechado ao exceder o teto de páginas/itens ou ao detectar repetição de página.
    /// </summary>
    Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(
        string token, string relativeUrl, bool notFoundAsEmpty, CancellationToken ct);

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

    /// <summary>Tamanho de página pedido no <c>$top</c> — dentro do teto dos três endpoints (machines/vulns 10k, catálogo 8k).</summary>
    private const int PageSize = 8000;
    private const int DefaultMaxPages = 500;      // teto defensivo de páginas
    private const int DefaultMaxItems = 2_000_000; // teto defensivo de itens materializados

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
        // Contrato configurado: tenantId/clientId/clientSecret precisam existir ANTES de qualquer requisição
        // (fail-closed — não se dispara um POST de token com credencial vazia). O conector já valida; o transporte
        // reforça de forma defensiva. Mensagem sanitizada (nunca ecoa o valor).
        if (string.IsNullOrWhiteSpace(creds.AzureTenantId)
            || string.IsNullOrWhiteSpace(creds.ClientId)
            || string.IsNullOrWhiteSpace(creds.ClientSecret))
            throw new DefenderApiException(DefenderApiErrorKind.AuthFailure, "credenciais app-only incompletas");

        // URL do endpoint de token montada só a partir de CONSTANTE oficial + tenantId escapado — sem entrada livre.
        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(creds.AzureTenantId)}/oauth2/v2.0/token";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["scope"] = TokenResource,   // recurso LEGADO securitycenter — a audiência que a API valida
            ["grant_type"] = "client_credentials",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var resp = await _http.SendAsync(req, ct);
        // EXATAMENTE 200: um 2xx que não seja 200 (ex.: 204/206) NÃO é uma resposta de token válida.
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DefenderApiException(Classify(resp.StatusCode), $"token endpoint retornou {(int)resp.StatusCode}");

        var body = await resp.Content.ReadAsStringAsync(ct);

        // Parsing DEFENSIVO: JSON inválido ou access_token com tipo/valor inesperado vira AuthFailure SANITIZADA —
        // nenhuma exceção de parsing escapa e a mensagem nunca carrega corpo, token, segredo ou URL.
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
        var results = new List<JsonElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);   // detecção de ciclo/repetição de página

        string? next = BuildUrl(relativeUrl, top: _pageSize, skip: null);
        var pages = 0;

        while (!string.IsNullOrEmpty(next))
        {
            if (pages >= _maxPages)
                throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "limite de paginação atingido com páginas restantes");
            if (!visited.Add(next!))
                throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "ciclo/repetição de página detectado na paginação");

            // Valida o destino ANTES de enviar — inclusive o @odata.nextLink devolvido pela página anterior.
            ValidateDefenderUrl(next!);

            string body;
            try
            {
                body = await SendGetAsync(token, next!, ct);
            }
            catch (DefenderApiException ex) when (ex.Kind == DefenderApiErrorKind.NotFound && notFoundAsEmpty && pages == 0)
            {
                // 404 na PRIMEIRA página de um endpoint que admite vazio (ex.: /api/machines): coleção COMPLETA vazia.
                return results;
            }
            pages++;

            var (items, nextLink) = ParsePage(body);
            foreach (var it in items)
            {
                results.Add(it);
                if (results.Count > _maxItems)
                    throw new DefenderApiException(DefenderApiErrorKind.Unavailable, "limite de itens materializados excedido");
            }

            if (!string.IsNullOrEmpty(nextLink))
            {
                next = nextLink;   // paginação por @odata.nextLink
            }
            else if (items.Count >= _pageSize)
            {
                // Página CHEIA sem nextLink → continua por $top+$skip. O $skip usa a quantidade EFETIVAMENTE
                // CONSUMIDA (results.Count) — inclusive após páginas por nextLink — para nunca reiniciar em 0 e
                // repetir dados na transição entre os dois modos de paginação.
                next = BuildUrl(relativeUrl, top: _pageSize, skip: results.Count);
            }
            else
            {
                next = null;   // última página
            }
        }

        return results;
    }

    public async Task ProbeAsync(string token, string relativeUrl, bool notFoundAsEmpty, CancellationToken ct)
    {
        var url = BuildUrl(relativeUrl, top: 1, skip: null);
        ValidateDefenderUrl(url);
        try
        {
            var body = await SendGetAsync(token, url, ct);
            // Estruturalmente válido (raiz objeto + value array) confirma que a API respondeu de fato — não só 200 de proxy.
            ParsePage(body);
        }
        catch (DefenderApiException ex) when (ex.Kind == DefenderApiErrorKind.NotFound && notFoundAsEmpty)
        {
            // 404 aqui (ex.: tenant sem máquinas) confirma a PERMISSÃO — a leitura foi autorizada, só não há dado.
        }
    }

    // ---- Construção e validação de destino ---------------------------------------------------------

    /// <summary>Monta a URL absoluta a partir do caminho relativo + paginação (o coletor nunca passa URL absoluta).</summary>
    private static string BuildUrl(string relativeUrl, int top, int? skip)
    {
        if (relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return relativeUrl;   // caso raro: validado por ValidateDefenderUrl adiante
        var baseUrl = $"{ApiBaseUrl}/{relativeUrl.TrimStart('/')}";
        var sep = relativeUrl.Contains('?') ? "&" : "?";
        var url = $"{baseUrl}{sep}$top={top}";
        if (skip is { } s && s > 0) url += $"&$skip={s}";
        return url;
    }

    /// <summary>
    /// Aceita SOMENTE HTTPS na MESMA origem oficial da API do Defender. Rejeita mudança de esquema, host, porta,
    /// userinfo ou origem — antes de qualquer envio, para o bearer nunca sair para um destino reprovado. A exceção
    /// é SANITIZADA (não inclui a URL, o token nem o payload).
    /// </summary>
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
        // Fail-CLOSED de completude: SÓ 200 OK é resposta completa esperada. 206 (2xx, mas NÃO completa) e qualquer
        // outro status viram falha sanitizada. O 404 é classificado (o chamador decide se é "vazio legítimo").
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DefenderApiException(Classify(resp.StatusCode), $"defender retornou {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Parse estrutural FAIL-CLOSED de uma página: raiz OBJETO + <c>value</c> ARRAY. Um 200 OK com corpo
    /// error/{}/raiz array/<c>value</c> de outro tipo NÃO é página completa — nunca vira coleção vazia em silêncio.
    /// Devolve clones (sobrevivem ao dispose do documento) + o <c>@odata.nextLink</c> (só ausente/null/string).
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
