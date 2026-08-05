using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Google;

/// <summary>Natureza de uma falha ao falar com o Google Workspace — o coletor a traduz em estado da fonte.</summary>
public enum GoogleWorkspaceErrorKind
{
    AuthFailure,
    InsufficientPermission,
    Throttled,
    Unavailable,
}

/// <summary>Falha SANITIZADA de acesso ao Google Workspace (nunca carrega token/segredo/chave/URL/payload).</summary>
public sealed class GoogleWorkspaceException : Exception
{
    public GoogleWorkspaceErrorKind Kind { get; }
    public GoogleWorkspaceException(GoogleWorkspaceErrorKind kind, string? detail = null) : base(detail) => Kind = kind;
}

/// <summary>
/// Autenticação da service account (domain-wide delegation) — porta TESTÁVEL. A implementação de produção usa a
/// biblioteca OFICIAL do Google (assina o JWT e troca por access token contra os endpoints oficiais); os testes
/// injetam um fake e não tocam rede/criptografia. A chave privada/JSON nunca entra em log ou resultado.
/// </summary>
public interface IGoogleWorkspaceAuthenticator
{
    Task<string> AcquireAccessTokenAsync(KnightGoogleWorkspaceConfiguration config, CancellationToken ct);
}

/// <summary>
/// Cliente HTTP de baixo nível do Admin SDK (Directory) e Reports do Google — host OFICIAL fixo, GET paginado
/// por <c>nextPageToken</c>. É o seam testável por HTTP simulado (mockando o <see cref="HttpClient"/>).
/// </summary>
public interface IGoogleWorkspaceApiClient
{
    IAsyncEnumerable<JsonElement> GetPagedAsync(string token, string relativePath, string itemsProperty, CancellationToken ct);
    Task<JsonElement> GetJsonAsync(string token, string relativePath, CancellationToken ct);
}

/// <inheritdoc cref="IGoogleWorkspaceAuthenticator"/>
public sealed class GoogleWorkspaceAuthenticator : IGoogleWorkspaceAuthenticator
{
    // Escopos SOMENTE LEITURA (oficiais) exigidos pelas coletas: usuários/2SV, grupos e membros, domínios e
    // auditoria. Fixos no código — o tenant não escolhe escopos.
    private static readonly string[] Scopes =
    {
        "https://www.googleapis.com/auth/admin.directory.user.readonly",
        "https://www.googleapis.com/auth/admin.directory.group.readonly",
        "https://www.googleapis.com/auth/admin.directory.group.member.readonly",
        "https://www.googleapis.com/auth/admin.directory.domain.readonly",
        "https://www.googleapis.com/auth/admin.reports.audit.readonly",
    };

    public async Task<string> AcquireAccessTokenAsync(KnightGoogleWorkspaceConfiguration config, CancellationToken ct)
    {
        try
        {
            // A biblioteca oficial assina o JWT da service account e troca por access token, IMPERSONANDO o
            // administrador delegado (domain-wide delegation). Endpoints oficiais do Google — nada configurável.
            var credential = GoogleCredential.FromJson(config.ServiceAccountJson)
                .CreateScoped(Scopes)
                .CreateWithUser(config.DelegatedAdminEmail);

            var token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
            if (string.IsNullOrEmpty(token))
                throw new GoogleWorkspaceException(GoogleWorkspaceErrorKind.AuthFailure, "access token vazio da service account");
            return token;
        }
        catch (GoogleWorkspaceException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SANITIZADO: nunca inclui a chave privada, o JSON da service account nem o detalhe do erro.
            throw new GoogleWorkspaceException(GoogleWorkspaceErrorKind.AuthFailure, "falha ao obter access token da service account do Google");
        }
    }
}

/// <inheritdoc cref="IGoogleWorkspaceApiClient"/>
public sealed class GoogleWorkspaceApiClient : IGoogleWorkspaceApiClient
{
    // Host OFICIAL do Admin SDK (Directory) e Reports — CONSTANTE. O tenant não fornece URL de destino.
    private const string ApiBaseUrl = "https://admin.googleapis.com";
    private const string ApiHost = "admin.googleapis.com";
    private const int DefaultMaxPages = 200;   // teto defensivo de paginação

    private readonly HttpClient _http;
    private readonly int _maxPages;

    public GoogleWorkspaceApiClient(HttpClient http) : this(http, DefaultMaxPages) { }

    /// <summary>Teto de paginação injetável — SOMENTE para teste; nunca vem do tenant. A DI usa o ctor público.</summary>
    internal GoogleWorkspaceApiClient(HttpClient http, int maxPages)
    {
        _http = http;
        _maxPages = maxPages > 0 ? maxPages : DefaultMaxPages;
    }

    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string token, string relativePath, string itemsProperty, [EnumeratorCancellation] CancellationToken ct)
    {
        string? pageToken = null;
        var pages = 0;
        do
        {
            // Teto atingido com páginas AINDA pendentes: NÃO truncamos em silêncio (isso viraria contagem/
            // conformidade sobre um conjunto incompleto). A capacidade correspondente fica indisponível.
            if (pages >= _maxPages)
                throw new GoogleWorkspaceException(GoogleWorkspaceErrorKind.Unavailable, "limite de paginação atingido com páginas restantes");

            var url = BuildUrl(relativePath, pageToken);
            ValidateOfficialUrl(url);
            var body = await SendGetAsync(token, url, ct);
            pages++;

            var pageItems = new List<JsonElement>();
            string? next;
            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(itemsProperty, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var item in arr.EnumerateArray())
                        pageItems.Add(item.Clone());   // clones sobrevivem ao dispose do documento
                next = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("nextPageToken", out var nt) && nt.ValueKind == JsonValueKind.String
                    ? nt.GetString() : null;
            }

            foreach (var it in pageItems)
                yield return it;

            pageToken = next;
        } while (!string.IsNullOrEmpty(pageToken));
    }

    public async Task<JsonElement> GetJsonAsync(string token, string relativePath, CancellationToken ct)
    {
        var url = BuildUrl(relativePath, null);
        ValidateOfficialUrl(url);
        var body = await SendGetAsync(token, url, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static string BuildUrl(string relativePath, string? pageToken)
    {
        var baseUrl = relativePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"{ApiBaseUrl}/{relativePath.TrimStart('/')}";
        if (string.IsNullOrEmpty(pageToken)) return baseUrl;
        var sep = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{sep}pageToken={Uri.EscapeDataString(pageToken)}";
    }

    /// <summary>Defesa em profundidade: só HTTPS no host OFICIAL do Google (o token de página nunca vira URL livre).</summary>
    private static void ValidateOfficialUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new GoogleWorkspaceException(GoogleWorkspaceErrorKind.Unavailable, "destino de requisicao reprovado pela allowlist do Google Workspace");
        }
    }

    private async Task<string> SendGetAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleWorkspaceException(Classify(resp.StatusCode), $"google api retornou {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static GoogleWorkspaceErrorKind Classify(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => GoogleWorkspaceErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => GoogleWorkspaceErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => GoogleWorkspaceErrorKind.Throttled,
        _ => GoogleWorkspaceErrorKind.Unavailable,
    };
}
