using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;

namespace AegisScore.Connectors.Google.Cloud;

/// <summary>Natureza de uma falha ao falar com a API OS Config do Google Cloud — o conector a traduz em estado da fonte.</summary>
public enum GoogleCloudApiErrorKind
{
    AuthFailure,
    InsufficientPermission,
    Throttled,
    Unavailable,
    /// <summary>Resposta 200 com corpo estruturalmente inválido (não-JSON, raiz não-objeto, campos com tipo inesperado).</summary>
    InvalidPayload,
    /// <summary>Paginação não pôde ser concluída com integridade (teto de páginas, ciclo/repetição de page token).</summary>
    IncompleteCollection,
}

/// <summary>
/// Falha SANITIZADA de acesso à API OS Config do Google Cloud (nunca carrega token, JSON da service account,
/// URL, PII ou payload bruto na mensagem).
/// </summary>
public sealed class GoogleCloudApiException : Exception
{
    public GoogleCloudApiErrorKind Kind { get; }
    public GoogleCloudApiException(GoogleCloudApiErrorKind kind, string? detail = null) : base(detail) => Kind = kind;
}

/// <summary>
/// [AEGIS-MVP-MULTICLOUD-01] Autenticação da service account do GOOGLE CLOUD (não Workspace) — porta TESTÁVEL. A
/// implementação de produção VALIDA o JSON pela autoridade única <see cref="GoogleCloudServiceAccountValidator"/>
/// (só service account oficial) e então constrói um <c>ServiceAccountCredential</c> DIRETAMENTE dos campos
/// validados — NÃO usa <c>GoogleCredential.FromJson</c> —, trocando o JWT por um access token contra o endpoint de
/// token OFICIAL (constante), com o ESCOPO Cloud Platform.
///
/// ⚠️ SEM domain-wide delegation: NÃO define <c>User</c>/<c>Subject</c> nem recebe e-mail de administrador delegado
/// — diferente do <see cref="GoogleWorkspaceAuthenticator"/>. Nenhum endpoint/credential source do documento do
/// tenant decide o destino da troca OAuth. A restrição efetiva de leitura vem dos papéis IAM somente leitura
/// concedidos à service account (ex.: <c>roles/osconfig.vulnerabilityReportViewer</c>), não do escopo. A assinatura
/// deste método (só o JSON da service account, sem e-mail) é a garantia estrutural de que não há delegação.
/// </summary>
public interface IGoogleCloudOsConfigAuthenticator
{
    Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct);
}

/// <summary>
/// Cliente HTTP de baixo nível da API OS Config (VM Manager Vulnerability Reports) — host OFICIAL fixo, GET
/// paginado por <c>pageToken</c>/<c>nextPageToken</c>. É o seam testável por HTTP simulado (mockando o
/// <see cref="HttpClient"/> via <see cref="HttpMessageHandler"/>). O tenant nunca fornece URL de destino.
/// </summary>
public interface IGoogleCloudOsConfigApiClient
{
    /// <summary>
    /// Coleta INTEGRAL e paginada dos vulnerabilityReports de TODAS as instâncias com inventário numa
    /// <paramref name="location"/> (zona) de um <paramref name="projectId"/> — instância wildcard <c>-</c>. Segue
    /// <c>nextPageToken</c> até o fim, com detecção de ciclo/repetição de token e teto de páginas (fail-closed:
    /// exceder o teto ou repetir um token vira <see cref="GoogleCloudApiErrorKind.IncompleteCollection"/>).
    /// </summary>
    Task<IReadOnlyList<JsonElement>> GetAllVulnerabilityReportsAsync(
        string token, string projectId, string location, CancellationToken ct);

    /// <summary>
    /// Leitura MÍNIMA (<c>pageSize=1</c>) de uma única zona, usada pelo teste de conexão para PROVAR autenticação
    /// + permissão de leitura no MENOR endpoint possível, sem coletar o dataset inteiro nem persistir nada.
    /// </summary>
    Task ProbeAsync(string token, string projectId, string location, CancellationToken ct);
}

/// <summary>
/// [AEGIS-MVP-MULTICLOUD-01] Campos JÁ VALIDADOS de uma service account do Google Cloud (imutável). Nunca carrega o
/// JSON bruto. <c>ToString</c> oculta a chave privada — e a credencial nunca é interpolada em exception/log.
/// </summary>
internal sealed record GoogleCloudServiceAccountCredential(string ClientEmail, string PrivateKey)
{
    public override string ToString() =>
        $"GoogleCloudServiceAccountCredential {{ ClientEmail = {ClientEmail}, PrivateKey = *** }}";
}

/// <summary>
/// [AEGIS-MVP-MULTICLOUD-01] Autoridade ÚNICA, PURA e testável de validação do JSON da service account do Google
/// Cloud — o boundary de segredo/autenticação. O JSON vem do tenant (só cifrado em repouso): <c>GoogleCredential
/// .FromJson</c> aceitaria formatos além de <c>service_account</c> (ex.: <c>authorized_user</c>,
/// <c>external_account</c>) e poderia interpretar endpoints/credential sources presentes no próprio documento,
/// direcionando a troca OAuth para destinos fora da allowlist. Esta autoridade FECHA esse boundary: aceita SOMENTE
/// service account com <c>token_uri</c> igual ao endpoint OFICIAL, sem domain-wide delegation. NUNCA devolve/loga a
/// chave privada nem inclui valores recebidos (chave, e-mail, URL, JSON) em exceptions — a falha é uma constante.
/// </summary>
internal static class GoogleCloudServiceAccountValidator
{
    // Endpoint de token OFICIAL suportado pelo conector — CONSTANTE. É a ÚNICA origem de troca OAuth aceita, e é o
    // valor efetivamente passado ao ServiceAccountCredential (o token_uri do tenant é validado, mas nunca é o destino).
    public const string OfficialTokenUri = "https://oauth2.googleapis.com/token";
    private const string OfficialTokenHost = "oauth2.googleapis.com";
    private const string OfficialTokenPath = "/token";

    /// <summary>
    /// Valida o JSON da service account. Sucesso → <see cref="GoogleCloudServiceAccountCredential"/>. Qualquer
    /// desvio (tipo diferente de service_account, campo ausente/vazio, token_uri não oficial) →
    /// <see cref="GoogleCloudApiException"/> com <see cref="GoogleCloudApiErrorKind.AuthFailure"/> e mensagem
    /// CONSTANTE (sem chave, e-mail, URL recebida ou JSON).
    /// </summary>
    public static GoogleCloudServiceAccountCredential Validate(string serviceAccountJson)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson)) throw Fail();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(serviceAccountJson); }
        catch (JsonException) { throw Fail(); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Fail();

            // type DEVE ser EXATAMENTE "service_account": rejeita authorized_user, external_account,
            // impersonated_service_account e qualquer outro formato de credencial.
            if (!TryStr(root, "type", out var type) || !string.Equals(type, "service_account", StringComparison.Ordinal))
                throw Fail();

            if (!TryStr(root, "client_email", out var email) || string.IsNullOrWhiteSpace(email)) throw Fail();
            if (!TryStr(root, "private_key", out var key) || string.IsNullOrWhiteSpace(key)) throw Fail();

            // token_uri OBRIGATÓRIO e EXATAMENTE o endpoint oficial (HTTPS, host oficial, porta padrão, sem
            // userinfo/query/fragmento). Fecha o vetor de credential source / token URL arbitrário do tenant.
            if (!TryStr(root, "token_uri", out var tokenUri) || !IsOfficialTokenUri(tokenUri)) throw Fail();

            // NÃO lê project_id: o projeto DONO da service account pode diferir do projeto consultado (a SA pode
            // receber acesso somente leitura a outro projeto). NÃO lê subject/delegated user: sem domain-wide delegation.
            return new GoogleCloudServiceAccountCredential(email!.Trim(), key!);
        }
    }

    /// <summary>Falha SANITIZADA de configuração/credencial — mensagem CONSTANTE, jamais valores recebidos.</summary>
    private static GoogleCloudApiException Fail() => new(
        GoogleCloudApiErrorKind.AuthFailure,
        "credencial do Google Cloud inválida ou não suportada (exige service account oficial com token_uri oficial; sem domain-wide delegation).");

    private static bool TryStr(JsonElement root, string prop, out string? value)
    {
        if (root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString();
            return true;
        }
        value = null;
        return false;
    }

    private static bool IsOfficialTokenUri(string? tokenUri)
    {
        if (string.IsNullOrWhiteSpace(tokenUri) || !Uri.TryCreate(tokenUri.Trim(), UriKind.Absolute, out var uri))
            return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.Equals(uri.Host, OfficialTokenHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.AbsolutePath, OfficialTokenPath, StringComparison.Ordinal);
    }
}

/// <inheritdoc cref="IGoogleCloudOsConfigAuthenticator"/>
public sealed class GoogleCloudOsConfigAuthenticator : IGoogleCloudOsConfigAuthenticator
{
    /// <summary>
    /// Escopo OAuth OFICIAL declarado pelo método <c>vulnerabilityReports.list</c> no discovery da API OS Config —
    /// o ÚNICO escopo aceito. A leitura somente-leitura efetiva é imposta pelos PAPÉIS IAM, não pelo escopo.
    /// </summary>
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    public async Task<string> AcquireAccessTokenAsync(string serviceAccountJson, CancellationToken ct)
    {
        // Boundary FECHADO: valida o JSON pela autoridade única (rejeita tudo que não seja service account oficial)
        // ANTES de qualquer construção de credencial ou rede. Falha de validação = AuthFailure sanitizada.
        var cred = GoogleCloudServiceAccountValidator.Validate(serviceAccountJson);

        try
        {
            // Constrói o ServiceAccountCredential DIRETAMENTE dos campos validados — NÃO usa GoogleCredential.FromJson
            // (que interpretaria endpoints/credential source do documento do tenant). O endpoint de token é a CONSTANTE
            // oficial (nunca o valor do tenant). SEM User/Subject/CreateWithUser → SEM domain-wide delegation: a service
            // account atua como ela mesma, limitada aos papéis IAM que possui.
            var initializer = new ServiceAccountCredential.Initializer(
                    cred.ClientEmail, GoogleCloudServiceAccountValidator.OfficialTokenUri)
                {
                    Scopes = new[] { CloudPlatformScope },
                }
                .FromPrivateKey(cred.PrivateKey);
            var credential = new ServiceAccountCredential(initializer);

            var token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
            if (string.IsNullOrEmpty(token))
                throw new GoogleCloudApiException(GoogleCloudApiErrorKind.AuthFailure, "access token vazio da service account do Google Cloud");
            return token;
        }
        catch (GoogleCloudApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SANITIZADO: nunca inclui a chave privada, o e-mail, a URL, o JSON da service account nem o detalhe do erro.
            throw new GoogleCloudApiException(GoogleCloudApiErrorKind.AuthFailure,
                "falha ao obter access token da service account do Google Cloud");
        }
    }
}

/// <inheritdoc cref="IGoogleCloudOsConfigApiClient"/>
public sealed class GoogleCloudOsConfigApiClient : IGoogleCloudOsConfigApiClient
{
    // Origem OFICIAL e CONSTANTE da API OS Config. O tenant NUNCA fornece base URL nem destino — impede exfiltrar
    // o bearer para uma origem arbitrária. A paginação é por pageToken (query param no MESMO host), não por URL de
    // continuação; ainda assim toda URL montada é revalidada contra a allowlist antes do envio.
    private const string ApiBaseUrl = "https://osconfig.googleapis.com";
    private const string ApiHost = "osconfig.googleapis.com";
    private const string ItemsProperty = "vulnerabilityReports";

    private const int DefaultPageSize = 500;
    private const int DefaultMaxPages = 500;        // teto defensivo de páginas
    private const int DefaultMaxItems = 2_000_000;  // teto defensivo de itens materializados

    private readonly HttpClient _http;
    private readonly int _maxPages;
    private readonly int _maxItems;
    private readonly int _pageSize;

    public GoogleCloudOsConfigApiClient(HttpClient http) : this(http, DefaultMaxPages, DefaultMaxItems, DefaultPageSize) { }

    /// <summary>
    /// Construtor com tetos + tamanho de página injetáveis — SOMENTE para teste (exercita limites/ciclo sem
    /// milhões de itens). É <c>internal</c> de propósito: nada disso vem do tenant, e a DI usa o ctor público.
    /// </summary>
    internal GoogleCloudOsConfigApiClient(HttpClient http, int maxPages, int maxItems, int pageSize)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
        _maxItems = maxItems > 0 ? maxItems : DefaultMaxItems;
        _pageSize = pageSize > 0 ? pageSize : DefaultPageSize;
    }

    public async Task<IReadOnlyList<JsonElement>> GetAllVulnerabilityReportsAsync(
        string token, string projectId, string location, CancellationToken ct)
    {
        var results = new List<JsonElement>();
        var visitedTokens = new HashSet<string>(StringComparer.Ordinal);   // detecção de repetição/ciclo de page token
        string? pageToken = null;
        var pages = 0;

        do
        {
            if (pages >= _maxPages)
                throw new GoogleCloudApiException(GoogleCloudApiErrorKind.IncompleteCollection,
                    "limite de paginação atingido com páginas restantes");
            // Um pageToken repetido significaria loop infinito — falha fechado, jamais coleta incompleta silenciosa.
            if (!string.IsNullOrEmpty(pageToken) && !visitedTokens.Add(pageToken))
                throw new GoogleCloudApiException(GoogleCloudApiErrorKind.IncompleteCollection,
                    "repetição de page token detectada na paginação");

            var url = BuildUrl(projectId, location, pageToken);
            ValidateOfficialUrl(url);   // revalida o destino ANTES de enviar (defesa em profundidade)

            var body = await SendGetAsync(token, url, ct);
            pages++;

            var (items, next) = ParsePage(body);
            foreach (var it in items)
            {
                results.Add(it);
                if (results.Count > _maxItems)
                    throw new GoogleCloudApiException(GoogleCloudApiErrorKind.IncompleteCollection,
                        "limite de itens materializados excedido");
            }

            pageToken = next;
        } while (!string.IsNullOrEmpty(pageToken));

        return results;
    }

    public async Task ProbeAsync(string token, string projectId, string location, CancellationToken ct)
    {
        var url = BuildUrl(projectId, location, pageToken: null, pageSizeOverride: 1);
        ValidateOfficialUrl(url);
        var body = await SendGetAsync(token, url, ct);
        // Estruturalmente válido (raiz objeto) confirma que a API respondeu de fato — não só um 200 de proxy.
        ParsePage(body);
    }

    // ---- Construção e validação de destino ---------------------------------------------------------

    private string BuildUrl(string projectId, string location, string? pageToken, int? pageSizeOverride = null)
    {
        // Segmentos de path escapados: mesmo um projeto/zona malformado não pode quebrar a origem (e a allowlist
        // barra o resultado). A instância é o wildcard oficial "-" (todos os relatórios da zona).
        var baseUrl =
            $"{ApiBaseUrl}/v1/projects/{Uri.EscapeDataString(projectId)}/locations/{Uri.EscapeDataString(location)}/instances/-/vulnerabilityReports";
        var url = $"{baseUrl}?pageSize={pageSizeOverride ?? _pageSize}";
        if (!string.IsNullOrEmpty(pageToken))
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        return url;
    }

    /// <summary>
    /// Aceita SOMENTE HTTPS na origem oficial da API OS Config. Rejeita mudança de esquema, host, porta, userinfo
    /// ou origem — antes de qualquer envio, para o bearer nunca sair para um destino reprovado. SANITIZADA.
    /// </summary>
    private static void ValidateOfficialUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new GoogleCloudApiException(GoogleCloudApiErrorKind.Unavailable,
                "destino de requisicao reprovado pela allowlist da API OS Config do Google Cloud");
        }
    }

    private async Task<string> SendGetAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        // Fail-CLOSED de completude: SÓ 200 OK é resposta completa esperada. Qualquer outro status vira falha
        // sanitizada e classificada (o corpo NUNCA é ecoado).
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new GoogleCloudApiException(Classify(resp.StatusCode), $"osconfig retornou {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Parse estrutural FAIL-CLOSED de uma página. A raiz precisa ser OBJETO. O array <c>vulnerabilityReports</c>
    /// é OPCIONAL — o REST do Google OMITE arrays vazios, então uma página sem ele é uma coleção vazia LEGÍTIMA (≠
    /// Defender). Mas se ele estiver PRESENTE com tipo errado, ou <c>nextPageToken</c> presente com tipo errado, o
    /// 200 NÃO é uma página válida — nunca vira vazio em silêncio. Devolve clones (sobrevivem ao dispose).
    /// </summary>
    private static (List<JsonElement> Items, string? NextPageToken) ParsePage(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new GoogleCloudApiException(GoogleCloudApiErrorKind.InvalidPayload,
                "resposta da API OS Config nao e JSON valido");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new GoogleCloudApiException(GoogleCloudApiErrorKind.InvalidPayload,
                    "pagina da API OS Config com raiz inesperada");

            var items = new List<JsonElement>();
            if (root.TryGetProperty(ItemsProperty, out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array)
                    throw new GoogleCloudApiException(GoogleCloudApiErrorKind.InvalidPayload,
                        "campo vulnerabilityReports com tipo invalido");
                foreach (var item in arr.EnumerateArray())
                    items.Add(item.Clone());
            }
            // Array ausente = página vazia legítima (Google omite arrays vazios).

            string? nextPageToken;
            if (root.TryGetProperty("nextPageToken", out var nt))
            {
                nextPageToken = nt.ValueKind switch
                {
                    JsonValueKind.String => nt.GetString(),
                    JsonValueKind.Null => null,
                    _ => throw new GoogleCloudApiException(GoogleCloudApiErrorKind.InvalidPayload,
                        "nextPageToken com tipo invalido"),
                };
            }
            else
            {
                nextPageToken = null;
            }

            return (items, nextPageToken);
        }
    }

    private static GoogleCloudApiErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => GoogleCloudApiErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => GoogleCloudApiErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => GoogleCloudApiErrorKind.Throttled,
        _ => GoogleCloudApiErrorKind.Unavailable,
    };
}
