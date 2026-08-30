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
    /// <summary>
    /// Falha de AUTENTICAÇÃO/credencial/configuração — produzida pela autoridade de autenticação (validação do JSON da
    /// service account / troca OAuth) ou pela validação de settings do conector. NÃO é um 400 da API de recurso
    /// (esse é <see cref="InvalidRequest"/>).
    /// </summary>
    AuthFailure,
    /// <summary>401 — credencial não autenticada pela API de recurso.</summary>
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
    /// <summary>
    /// 400 — a REQUISIÇÃO da aplicação foi rejeitada (contrato/argumento inválido). NÃO é falha de credencial (o token
    /// OAuth é tratado pela autoridade de autenticação): não instruir a troca de credenciais neste caso.
    /// </summary>
    InvalidRequest,
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
/// O conjunto ESPELHA a lista oficial atual de endpoints regionais e multirregionais do Google SecOps.
/// </summary>
internal static class ChronicleRegions
{
    private const string HostSuffix = "-chronicle.googleapis.com";

    /// <summary>
    /// Localidades OFICIAIS do Google SecOps (Chronicle API), sincronizadas com a lista de endpoints regionais/
    /// multirregionais. O host é sempre <c>{location}-chronicle.googleapis.com</c>. Espelhada no frontend
    /// (CHRONICLE_LOCATIONS); testes em cada camada comprovam que ambas usam exatamente este conjunto oficial.
    /// </summary>
    public static readonly IReadOnlyList<string> OfficialLocations = new[]
    {
        "us", "eu", "europe",
        "africa-south1",
        "asia-east1", "asia-northeast1", "asia-northeast3", "asia-south1", "asia-southeast1", "asia-southeast2",
        "australia-southeast1",
        "europe-central2", "europe-west2", "europe-west3", "europe-west6", "europe-west9", "europe-west12",
        "me-central1", "me-central2", "me-west1",
        "northamerica-northeast2", "southamerica-east1",
    };

    private static readonly IReadOnlySet<string> Supported = new HashSet<string>(OfficialLocations, StringComparer.Ordinal);

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
/// Resultado da busca de alertas (legacySearchEnterpriseWideAlerts): os itens de alerta ACHATADOS dos dois
/// agrupamentos oficiais (por ativo e por usuário) + se a fonte sinalizou <c>moreDataAvailable</c> e/ou se um limite
/// defensivo impediu a coleta integral. Qualquer um dos dois ⇒ PARCIAL. A deduplicação (por <c>alertNumber</c>) e a
/// agregação vivem no conector — o transporte só extrai e devolve os itens de alerta permitidos.
/// </summary>
public sealed record ChronicleAlertSearchResult(
    IReadOnlyList<JsonElement> AlertInfos, bool MoreDataAvailable, bool LimitHit)
{
    public bool IsPartial => MoreDataAvailable || LimitHit;
}

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-01] Transporte de baixo nível da Chronicle API unificada do Google SecOps — SOMENTE
/// LEITURA (só métodos HTTP GET), testável por HTTP simulado. Usa EXCLUSIVAMENTE os hosts regionais oficiais
/// (<c>*-chronicle.googleapis.com</c>) derivados da <see cref="ChronicleRegions"/> — nunca o host/cliente da antiga
/// Backstory API, nunca um baseUrl do tenant. HTTPS obrigatório, redirects recusados, bearer nunca encaminhado a host
/// diferente, segmentos de rota escapados, timeout/cancellation, limite de tamanho de resposta, teto de páginas/itens
/// e detecção de ciclo de pageToken. Falhas sobem SANITIZADAS (nunca corpo/segredo/URL).
/// </summary>
public interface IChronicleApiClient
{
    /// <summary>instances.get — prova conexão + permissão básica, SEM depender de casos ou alertas. GET <c>/v1alpha/.../instances/{id}</c>.</summary>
    Task<ChronicleInstance> GetInstanceAsync(
        string token, string projectId, string location, string instanceId, CancellationToken ct);

    /// <summary>
    /// cases.list (endpoint ESTÁVEL <c>/v1/.../cases</c>) — pagina e ENTREGA cada caso ao <paramref name="onCase"/>
    /// SEM materializar a coleção inteira. Devolve <c>true</c> quando a coleta foi PARCIAL (teto de páginas/itens,
    /// ciclo de pageToken, ou falha APÓS ao menos uma página válida) — preservando o piso já entregue. Lança
    /// <see cref="ChronicleApiException"/> SOMENTE quando a PRIMEIRA requisição falha (nenhuma página válida).
    /// </summary>
    Task<bool> CollectCasesAsync(
        string token, string projectId, string location, string instanceId,
        Action<JsonElement> onCase, CancellationToken ct);

    /// <summary>legacySearchEnterpriseWideAlerts — busca de alertas numa janela [start, end) fixa. GET <c>/v1alpha/.../legacy:legacySearchEnterpriseWideAlerts</c>.</summary>
    Task<ChronicleAlertSearchResult> SearchAlertsAsync(
        string token, string projectId, string location, string instanceId,
        DateTimeOffset startInclusive, DateTimeOffset endExclusive, CancellationToken ct);
}

/// <inheritdoc cref="IChronicleApiClient"/>
public sealed class ChronicleApiClient : IChronicleApiClient
{
    private const int DefaultPageSize = 1000;               // cases.list — máximo oficial
    private const int DefaultMaxPages = 100;                // teto defensivo de páginas (cases.list)
    private const int DefaultMaxItems = 200_000;            // teto defensivo de CASOS observados
    private const int DefaultMaxAlertsReturn = 10_000;      // maxNumAlertsReturn — proporcional ao MVP/memória (≠ teto de casos)
    private const int DefaultMaxResponseBytes = 8 * 1024 * 1024;   // teto defensivo do corpo de UMA resposta

    private readonly HttpClient _http;
    private readonly int _maxPages;
    private readonly int _maxItems;
    private readonly int _maxAlerts;
    private readonly int _maxResponseBytes;
    private readonly int _pageSize;

    public ChronicleApiClient(HttpClient http)
        : this(http, DefaultMaxPages, DefaultMaxItems, DefaultMaxResponseBytes, DefaultPageSize, DefaultMaxAlertsReturn) { }

    /// <summary>Ctor com tetos injetáveis — SOMENTE para teste (exercita limites/ciclo sem dados reais em excesso). <c>internal</c>: nada disso vem do tenant.</summary>
    internal ChronicleApiClient(
        HttpClient http, int maxPages, int maxItems, int maxResponseBytes, int pageSize,
        int maxAlerts = DefaultMaxAlertsReturn)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
        _maxItems = maxItems > 0 ? maxItems : DefaultMaxItems;
        _maxAlerts = maxAlerts > 0 ? maxAlerts : DefaultMaxAlertsReturn;
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

    // ---- cases.list (v1 estável, agregação INCREMENTAL — nunca materializa a coleção) --------------

    public async Task<bool> CollectCasesAsync(
        string token, string projectId, string location, string instanceId,
        Action<JsonElement> onCase, CancellationToken ct)
    {
        var host = ChronicleRegions.ResolveHost(location);
        var visitedTokens = new HashSet<string>(StringComparer.Ordinal);   // detecção de repetição/ciclo de page token
        string? pageToken = null;
        var pages = 0;
        var observed = 0;

        while (true)
        {
            // Teto de páginas / ciclo de token → PARCIAL (piso já entregue preservado), NUNCA descarta as páginas boas.
            if (pages >= _maxPages) return true;
            if (!string.IsNullOrEmpty(pageToken) && !visitedTokens.Add(pageToken)) return true;

            var url = BuildCasesUrl(host, projectId, location, instanceId, pageToken);
            ValidateOfficialUrl(url, host);

            string body;
            try
            {
                body = await SendGetAsync(token, url, host, ct);
            }
            catch (ChronicleApiException)
            {
                if (pages == 0) throw;   // a PRIMEIRA requisição falhou → falha classificada (sem piso)
                return true;             // falha APÓS ≥1 página válida → preserva o piso e marca PARCIAL
            }

            List<JsonElement> items;
            string? next;
            try
            {
                (items, next) = ParseListPage(body, "cases");
            }
            catch (ChronicleApiException)
            {
                if (pages == 0) throw;
                return true;
            }
            pages++;

            foreach (var it in items)
            {
                if (observed >= _maxItems) return true;   // teto de itens → PARCIAL, preservando o piso já entregue
                onCase(it);                                // agrega incrementalmente; o objeto não é retido pelo transporte
                observed++;
            }

            pageToken = next;
            if (string.IsNullOrEmpty(pageToken)) return false;   // COMPLETO
        }
    }

    // ---- legacySearchEnterpriseWideAlerts (janela fixa, envelope oficial) --------------------------

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
        // Endpoint ESTÁVEL v1 de cases.list (não v1beta). pageSize máximo oficial de 1000.
        var url = $"https://{host}/v1/{InstancePath(projectId, location, instanceId)}/cases?pageSize={_pageSize}";
        if (!string.IsNullOrEmpty(pageToken))
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        return url;
    }

    private string BuildAlertsUrl(
        string host, string projectId, string location, string instanceId,
        DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        // Contrato OFICIAL: `timestampRange` (objeto Interval) + `maxNumAlertsReturn` (inteiro obrigatório). Na
        // transcodificação REST viram os campos aninhados timestampRange.startTime / timestampRange.endTime. Esta
        // operação NÃO é paginada por pageSize. Janela [start, end): início INCLUSIVO, fim EXCLUSIVO, RFC 3339, 30d
        // calculados no servidor. maxNumAlertsReturn é um teto defensivo FIXO do servidor (proporcional ao MVP).
        var start = Uri.EscapeDataString(Rfc3339(startInclusive));
        var end = Uri.EscapeDataString(Rfc3339(endExclusive));
        return $"https://{host}/v1alpha/{InstancePath(projectId, location, instanceId)}/legacy:legacySearchEnterpriseWideAlerts"
             + $"?timestampRange.startTime={start}&timestampRange.endTime={end}&maxNumAlertsReturn={_maxAlerts}";
    }

    private static string Rfc3339(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

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
    /// Parse fail-closed do envelope OFICIAL de <c>legacySearchEnterpriseWideAlerts</c>. Extrai os itens de alerta dos
    /// DOIS agrupamentos oficiais — por ativo (<c>alertSummaries[].alertInfo[]</c>) e por usuário
    /// (<c>userAlertSummaries[].alertInfos[]</c>) — num único fluxo achatado. Arrays AUSENTES = zero (o Google omite
    /// arrays vazios); presentes com tipo errado ⇒ inválido. <c>moreDataAvailable</c> opcional bool. Um teto interno de
    /// itens marca <c>LimitHit</c> (parcial). NÃO projeta ativo/usuário/evento/título/payload — só clona o item de
    /// alerta, cujos campos permitidos o conector lê (alertNumber/uid/severity/alertTime/eventLogToken).
    /// </summary>
    private ChronicleAlertSearchResult ParseAlertSearch(string body)
    {
        var root = ParseObject(body);

        var alertInfos = new List<JsonElement>();
        var limitHit = false;
        // Agrupamento por ATIVO usa a propriedade `alertInfo`; por USUÁRIO usa `alertInfos` (quirk oficial).
        limitHit |= CollectAlertInfos(root, "alertSummaries", "alertInfo", alertInfos);
        limitHit |= CollectAlertInfos(root, "userAlertSummaries", "alertInfos", alertInfos);

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

        return new ChronicleAlertSearchResult(alertInfos, moreDataAvailable, limitHit);
    }

    /// <summary>
    /// Achata os itens de alerta de um agrupamento (<paramref name="summariesProp"/> → <paramref name="infosProp"/>)
    /// em <paramref name="into"/>. Devolve <c>true</c> se o teto interno de itens foi atingido (parcial). Summaries/
    /// infos ausentes = vazio legítimo; presentes com tipo errado ⇒ inválido.
    /// </summary>
    private bool CollectAlertInfos(JsonElement root, string summariesProp, string infosProp, List<JsonElement> into)
    {
        if (!root.TryGetProperty(summariesProp, out var summaries)) return false;
        if (summaries.ValueKind != JsonValueKind.Array)
            throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse, $"campo {summariesProp} com tipo invalido");

        foreach (var summary in summaries.EnumerateArray())
        {
            if (summary.ValueKind != JsonValueKind.Object)
                throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse, $"item de {summariesProp} com tipo invalido");
            if (!summary.TryGetProperty(infosProp, out var infos)) continue;   // grupo sem infos = vazio legítimo
            if (infos.ValueKind != JsonValueKind.Array)
                throw new ChronicleApiException(ChronicleApiErrorKind.InvalidResponse, $"campo {infosProp} com tipo invalido");

            foreach (var info in infos.EnumerateArray())
            {
                if (into.Count >= _maxAlerts) return true;   // teto defensivo → parcial (não silencia o excesso)
                into.Add(info.Clone());
            }
        }
        return false;
    }

    private static ChronicleApiErrorKind Classify(HttpStatusCode code) => code switch
    {
        // 400 é a REQUISIÇÃO da aplicação rejeitada (contrato/argumento) — NÃO uma falha de credencial (o token OAuth
        // já é tratado pela autoridade de autenticação). Nunca instruir troca de credenciais neste caso.
        HttpStatusCode.BadRequest => ChronicleApiErrorKind.InvalidRequest,
        HttpStatusCode.Unauthorized => ChronicleApiErrorKind.Unauthorized,
        HttpStatusCode.Forbidden => ChronicleApiErrorKind.InsufficientPermission,
        HttpStatusCode.NotFound => ChronicleApiErrorKind.InstanceNotFound,
        HttpStatusCode.TooManyRequests => ChronicleApiErrorKind.Throttled,
        HttpStatusCode.RequestTimeout => ChronicleApiErrorKind.Timeout,
        HttpStatusCode.GatewayTimeout => ChronicleApiErrorKind.Timeout,
        _ => ChronicleApiErrorKind.Unavailable,
    };
}
