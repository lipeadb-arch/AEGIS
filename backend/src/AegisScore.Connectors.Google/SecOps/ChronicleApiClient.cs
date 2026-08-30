using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AegisScore.Connectors.Google.SecOps;

/// <summary>Natureza de uma falha ao falar com a Chronicle API unificada do Google SecOps — o conector a traduz em estado da fonte.</summary>
public enum ChronicleApiErrorKind
{
    /// <summary>400 na API de recurso — no nosso fluxo (requisição FIXA no servidor) sinaliza rejeição de autenticação/OAuth.</summary>
    AuthFailure,
    /// <summary>401 — credencial não autenticada.</summary>
    Unauthorized,
    /// <summary>403 — permissão insuficiente na instância/recurso.</summary>
    InsufficientPermission,
    /// <summary>404 — instância do SecOps não encontrada.</summary>
    InstanceNotFound,
    /// <summary>429 — throttling.</summary>
    Throttled,
    /// <summary>Tempo esgotado / cancelamento NÃO solicitado pelo chamador.</summary>
    Timeout,
    /// <summary>5xx, redirecionamento recusado ou transporte indisponível.</summary>
    Unavailable,
    /// <summary>200 com corpo estruturalmente inválido (não-JSON, raiz não-objeto, campo com tipo inesperado) ou acima do limite de tamanho.</summary>
    InvalidResponse,
    /// <summary>Paginação não pôde ser concluída com integridade (teto de páginas/itens, ciclo/repetição de page token).</summary>
    IncompleteCollection,
}

/// <summary>
/// Falha SANITIZADA de acesso à Chronicle API do Google SecOps — NUNCA carrega token, JSON da service account, URL,
/// PII, corpo de resposta ou payload bruto. Só metadados operacionais seguros (o status HTTP, quando houver).
/// </summary>
public sealed class ChronicleApiException : Exception
{
    public ChronicleApiErrorKind Kind { get; }
    public int? HttpStatusCode { get; }
    public ChronicleApiException(ChronicleApiErrorKind kind, string? detail = null, int? httpStatusCode = null)
        : base(detail)
    {
        Kind = kind;
        HttpStatusCode = httpStatusCode;
    }
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] ALLOWLIST interna de localidades OFICIALMENTE suportadas da Chronicle API e o host
/// regional correspondente. O tenant NUNCA fornece host/baseUrl: escolhe UMA localidade desta lista, e o host é
/// derivado (<c>{location}-chronicle.googleapis.com</c>) SOMENTE quando a localidade está na allowlist — uma
/// localidade desconhecida é REJEITADA (nunca vira host forjado, e o bearer nunca sai para um destino arbitrário).
/// </summary>
internal static class ChronicleRegions
{
    private const string HostSuffix = "-chronicle.googleapis.com";

    // Localidades regionais/multirregionais oficiais do Google SecOps (Chronicle API). O host é sempre
    // {location}-chronicle.googleapis.com. Curada e explícita — a completude é revisável em SECOPS-02.
    private static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "us", "europe",
        "europe-west2", "europe-west3", "europe-west6", "europe-west9", "europe-west12",
        "asia-southeast1", "asia-south1", "asia-northeast1", "australia-southeast1",
        "me-west1", "me-central1", "me-central2",
        "northamerica-northeast2", "southamerica-east1",
    };

    public static IReadOnlyCollection<string> SupportedLocations => Supported;

    public static bool IsSupported(string? location) =>
        !string.IsNullOrWhiteSpace(location) && Supported.Contains(location.Trim());

    /// <summary>Host regional OFICIAL da localidade — SOMENTE se estiver na allowlist. Falha FECHADO (localidade desconhecida rejeitada).</summary>
    public static string ResolveHost(string? location)
    {
        var loc = (location ?? "").Trim();
        if (!Supported.Contains(loc))
            throw new ChronicleApiException(ChronicleApiErrorKind.Unavailable,
                "localidade do Google SecOps não suportada");
        return loc + HostSuffix;
    }
}

/// <summary>Resultado MÍNIMO da prova de conexão (instances.get): só o <c>name</c> da instância, quando presente. Nunca conteúdo operacional.</summary>
public sealed record ChronicleInstance(string? Name);

/// <summary>
/// Resultado da busca de alertas (legacySearchEnterpriseWideAlerts): os alertas coletados + se a fonte sinalizou
/// <c>moreDataAvailable</c> e/ou se um limite defensivo impediu a coleta integral. Qualquer um dos dois ⇒ PARCIAL.
/// </summary>
public sealed record ChronicleAlertSearchResult(
    IReadOnlyList<JsonElement> Alerts, bool MoreDataAvailable, bool LimitHit)
{
    public bool IsPartial => MoreDataAvailable || LimitHit;
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Transporte de baixo nível da Chronicle API unificada do Google SecOps — SOMENTE
/// LEITURA, testável por HTTP simulado. Usa EXCLUSIVAMENTE os hosts regionais oficiais (<c>*-chronicle.googleapis.com</c>)
/// derivados da <see cref="ChronicleRegions"/> — nunca o host/cliente da antiga Backstory API, nunca um baseUrl do
/// tenant. HTTPS obrigatório, redirects recusados, bearer nunca encaminhado a host diferente, segmentos de rota
/// escapados, timeout/cancellation, limite de tamanho de resposta, teto de páginas/itens e detecção de ciclo de
/// pageToken. Falhas sobem SANITIZADAS (nunca corpo/segredo/URL).
/// </summary>
public interface IChronicleApiClient
{
    /// <summary>instances.get — prova conexão + permissão básica, SEM depender de casos ou alertas. GET <c>/v1alpha/.../instances/{id}</c>.</summary>
    Task<ChronicleInstance> GetInstanceAsync(
        string token, string projectId, string location, string instanceId, CancellationToken ct);

    /// <summary>cases.list — coleta INTEGRAL e paginada dos casos (inventário atual). GET <c>/v1beta/.../cases</c>.</summary>
    Task<IReadOnlyList<JsonElement>> ListCasesAsync(
        string token, string projectId, string location, string instanceId, CancellationToken ct);

    /// <summary>legacySearchEnterpriseWideAlerts — busca de alertas numa janela [start, end) fixa. GET <c>/v1alpha/.../legacy:legacySearchEnterpriseWideAlerts</c>.</summary>
    Task<ChronicleAlertSearchResult> SearchAlertsAsync(
        string token, string projectId, string location, string instanceId,
        DateTimeOffset startInclusive, DateTimeOffset endExclusive, CancellationToken ct);
}

/// <inheritdoc cref="IChronicleApiClient"/>
public sealed class ChronicleApiClient : IChronicleApiClient
{
    private const int DefaultPageSize = 1000;
    private const int DefaultMaxPages = 100;              // teto defensivo de páginas (cases.list)
    private const int DefaultMaxItems = 200_000;          // teto defensivo de itens materializados
    private const int DefaultMaxResponseBytes = 8 * 1024 * 1024;   // teto defensivo do corpo de UMA resposta

    private readonly HttpClient _http;
    private readonly int _maxPages;
    private readonly int _maxItems;
    private readonly int _maxResponseBytes;
    private readonly int _pageSize;

    public ChronicleApiClient(HttpClient http)
        : this(http, DefaultMaxPages, DefaultMaxItems, DefaultMaxResponseBytes, DefaultPageSize) { }

    /// <summary>Ctor com tetos injetáveis — SOMENTE para teste (exercita limites/ciclo sem dados reais em excesso). <c>internal</c>: nada disso vem do tenant.</summary>
    internal ChronicleApiClient(HttpClient http, int maxPages, int maxItems, int maxResponseBytes, int pageSize)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
        _maxItems = maxItems > 0 ? maxItems : DefaultMaxItems;
        _maxResponseBytes = maxResponseBytes > 0 ? maxResponseBytes : DefaultMaxResponseBytes;
        _pageSize = pageSize > 0 ? pageSize : DefaultPageSize;
    }

    // ---- instances.get (prova de conexão) ---------------------------------------------------------

    public async Task<ChronicleInstance> GetInstanceAsync(
        string token, string projectId, string location, string instanceId, CancellationToken ct)
    {
        var host = ChronicleRegions.ResolveHost(location);
        var url = BuildInstanceUrl(host, projectId, location, instanceId);
        ValidateOfficialUrl(url, host);

        var body = await SendGetAsync(token, url, host, ct);
        var root = ParseObject(body);   // 200 estruturalmente válido confirma que a API respondeu (não só um proxy)
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new ChronicleInstance(name);
    }

    // ---- cases.list (inventário atual, paginação completa e limitada) -----------------------------

    public async Task<IReadOnlyList<JsonElement>> ListCasesAsync(
        string token, string projectId, string location, string instanceId, CancellationToken ct)
    {
        var host = ChronicleRegions.ResolveHost(location);
        var results = new List<JsonElement>();
        var visitedTokens = new HashSet<string>(StringComparer.Ordinal);   // detecção de repetição/ciclo de page token
        string? pageToken = null;
        var pages = 0;

        do
        {
            if (pages >= _maxPages)
                throw new ChronicleApiException(ChronicleApiErrorKind.IncompleteCollection,
                    "limite de paginação atingido com páginas restantes");
            if (!string.IsNullOrEmpty(pageToken) && !visitedTokens.Add(pageToken))
                throw new ChronicleApiException(ChronicleApiErrorKind.IncompleteCollection,
                    "repetição de page token detectada na paginação");

            var url = BuildCasesUrl(host, projectId, location, instanceId, pageToken);
            ValidateOfficialUrl(url, host);

            var body = await SendGetAsync(token, url, host, ct);
            pages++;

            var (items, next) = ParseListPage(body, "cases");
            foreach (var it in items)
            {
                results.Add(it);
                if (results.Count > _maxItems)
                    throw new ChronicleApiException(ChronicleApiErrorKind.IncompleteCollection,
                        "limite de itens materializados excedido");
            }

            pageToken = next;
        } while (!string.IsNullOrEmpty(pageToken));

        return results;
    }

    // ---- legacySearchEnterpriseWideAlerts (janela fixa, agregação em memória) ----------------------

    public async Task<ChronicleAlertSearchResult> SearchAlertsAsync(
        string token, string projectId, string location, string instanceId,
        DateTimeOffset startInclusive, DateTimeOffset endExclusive, CancellationToken ct)
    {
        var host = ChronicleRegions.ResolveHost(location);
        var url = BuildAlertsUrl(host, projectId, location, instanceId, startInclusive, endExclusive);
        ValidateOfficialUrl(url, host);

        var body = await SendGetAsync(token, url, host, ct);
        return ParseAlertSearch(body);
    }

    // ---- Construção e validação de destino --------------------------------------------------------

    private static string BuildInstanceUrl(string host, string projectId, string location, string instanceId) =>
        $"https://{host}/v1alpha/{InstancePath(projectId, location, instanceId)}";

    private string BuildCasesUrl(string host, string projectId, string location, string instanceId, string? pageToken)
    {
        var url = $"https://{host}/v1beta/{InstancePath(projectId, location, instanceId)}/cases?pageSize={_pageSize}";
        if (!string.IsNullOrEmpty(pageToken))
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        return url;
    }

    private string BuildAlertsUrl(
        string host, string projectId, string location, string instanceId,
        DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        // Janela [start, end): início inclusivo e fim exclusivo, calculados no servidor (o conector os fornece). Os
        // nomes de parâmetro seguem o contrato alpha documentado de legacySearchEnterpriseWideAlerts (start/end time +
        // pageSize) — a confirmação contra uma instância viva fica registrada como item de SECOPS-02.
        var start = Uri.EscapeDataString(startInclusive.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        var end = Uri.EscapeDataString(endExclusive.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        return $"https://{host}/v1alpha/{InstancePath(projectId, location, instanceId)}/legacy:legacySearchEnterpriseWideAlerts"
             + $"?startTime={start}&endTime={end}&pageSize={_maxItems}";
    }

    /// <summary>Path da instância com TODOS os segmentos variáveis ESCAPADOS — um projeto/localidade/instância malformado não quebra a origem.</summary>
    private static string InstancePath(string projectId, string location, string instanceId) =>
        $"projects/{Uri.EscapeDataString(projectId)}/locations/{Uri.EscapeDataString(location)}/instances/{Uri.EscapeDataString(instanceId)}";

    /// <summary>
    /// Aceita SOMENTE HTTPS no host regional OFICIAL resolvido pela allowlist. Rejeita mudança de esquema, host,
    /// porta, userinfo — ANTES de qualquer envio, para o bearer nunca sair para um destino reprovado. SANITIZADA.
    /// </summary>
    private static void ValidateOfficialUrl(string url, string expectedHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ChronicleApiException(ChronicleApiErrorKind.Unavailable,
                "destino de requisicao reprovado pela allowlist da Chronicle API do Google SecOps");
        }
    }

    private async Task<string> SendGetAsync(string token, string url, string expectedHost, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChronicleApiException(ChronicleApiErrorKind.Timeout, "tempo esgotado na consulta ao Google SecOps");
        }
        catch (HttpRequestException)
        {
            throw new ChronicleApiException(ChronicleApiErrorKind.Unavailable, "Google SecOps inacessível");
        }

        using (resp)
        {
            // Redirect DESABILITADO na origem (handler) E recusado aqui (defesa em profundidade): um 3xx nunca faz o
            // bearer seguir para outro Location — a resposta de redirecionamento é tratada como indisponibilidade.
            var status = (int)resp.StatusCode;
            if (status is >= 300 and <= 399)
                throw new ChronicleApiException(ChronicleApiErrorKind.Unavailable,
                    "redirecionamento recusado pela Chronicle API do Google SecOps", status);

            if (resp.StatusCode != HttpStatusCode.OK)
                throw new ChronicleApiException(Classify(resp.StatusCode), $"google secops retornou {status}", status);

            // Defesa extra: mesmo em 200, confirma que o corpo veio do host oficial (o handler não seguiu redirect).
            if (resp.RequestMessage?.RequestUri is { } finalUri
                && !string.Equals(finalUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
                throw new ChronicleApiException(ChronicleApiErrorKind.Unavailable,
                    "resposta do Google SecOps veio de host inesperado");

            return await ReadCappedAsync(resp, ct);
        }
    }

    /// <summary>Lê o corpo com TETO de tamanho (fail-closed): um servidor que omita Content-Length e envie um corpo enorme não estoura a memória.</summary>
    private async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength is { } declared && declared > _maxResponseBytes)
            throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse,
                "resposta do Google SecOps excede o tamanho máximo");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (ms.Length + read > _maxResponseBytes)
                throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse,
                    "resposta do Google SecOps excede o tamanho máximo");
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // ---- Parsing fail-closed ----------------------------------------------------------------------

    /// <summary>Raiz OBJETO ou falha SANITIZADA (nunca JsonException escapando, nunca corpo ecoado).</summary>
    private static JsonElement ParseObject(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse,
                "resposta da Chronicle API nao e JSON valido");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse,
                    "resposta da Chronicle API com raiz inesperada");
            return doc.RootElement.Clone();
        }
    }

    /// <summary>
    /// Parse estrutural FAIL-CLOSED de uma página de listagem. Raiz OBJETO. O array de itens é OPCIONAL (o REST do
    /// Google OMITE arrays vazios: página sem ele = coleção vazia LEGÍTIMA). Presente com tipo errado, ou
    /// <c>nextPageToken</c> presente com tipo errado ⇒ inválido (nunca vazio em silêncio). Devolve clones.
    /// </summary>
    private static (List<JsonElement> Items, string? NextPageToken) ParseListPage(string body, string itemsProperty)
    {
        var root = ParseObject(body);
        var items = new List<JsonElement>();
        if (root.TryGetProperty(itemsProperty, out var arr))
        {
            if (arr.ValueKind != JsonValueKind.Array)
                throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse,
                    $"campo {itemsProperty} com tipo invalido");
            foreach (var item in arr.EnumerateArray())
                items.Add(item.Clone());
        }

        string? nextPageToken = null;
        if (root.TryGetProperty("nextPageToken", out var nt))
        {
            nextPageToken = nt.ValueKind switch
            {
                JsonValueKind.String => nt.GetString(),
                JsonValueKind.Null => null,
                _ => throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse,
                    "nextPageToken com tipo invalido"),
            };
        }

        return (items, nextPageToken);
    }

    /// <summary>
    /// Parse fail-closed da resposta de busca de alertas. Raiz OBJETO. <c>alerts</c> OPCIONAL (ausente = zero alertas);
    /// presente com tipo errado ⇒ inválido. <c>moreDataAvailable</c> OPCIONAL bool (presente com tipo errado ⇒
    /// inválido). Um teto interno de itens marca <c>LimitHit</c> (parcial) sem materializar um volume inesperado.
    /// </summary>
    private ChronicleAlertSearchResult ParseAlertSearch(string body)
    {
        var root = ParseObject(body);

        var alerts = new List<JsonElement>();
        var limitHit = false;
        if (root.TryGetProperty("alerts", out var arr))
        {
            if (arr.ValueKind != JsonValueKind.Array)
                throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse, "campo alerts com tipo invalido");
            foreach (var item in arr.EnumerateArray())
            {
                if (alerts.Count >= _maxItems)
                {
                    limitHit = true;   // teto defensivo: honestamente PARCIAL (não silencia o excesso)
                    break;
                }
                alerts.Add(item.Clone());
            }
        }

        var moreDataAvailable = false;
        if (root.TryGetProperty("moreDataAvailable", out var more))
        {
            moreDataAvailable = more.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => false,
                _ => throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse, "moreDataAvailable com tipo invalido"),
            };
        }

        return new ChronicleAlertSearchResult(alerts, moreDataAvailable, limitHit);
    }

    private static ChronicleApiErrorKind Classify(HttpStatusCode code) => code switch
    {
        // 400 na API de recurso, com requisição FIXA no servidor, sinaliza rejeição de autenticação/OAuth (não um
        // request malformado do usuário — o usuário não monta a requisição). Classificado como AuthFailure.
        HttpStatusCode.BadRequest => ChronicleApiErrorKind.AuthFailure,
        HttpStatusCode.Unauthorized => ChronicleApiErrorKind.Unauthorized,
        HttpStatusCode.Forbidden => ChronicleApiErrorKind.InsufficientPermission,
        HttpStatusCode.NotFound => ChronicleApiErrorKind.InstanceNotFound,
        HttpStatusCode.TooManyRequests => ChronicleApiErrorKind.Throttled,
        HttpStatusCode.RequestTimeout => ChronicleApiErrorKind.Timeout,
        HttpStatusCode.GatewayTimeout => ChronicleApiErrorKind.Timeout,
        _ => ChronicleApiErrorKind.Unavailable,
    };
}
