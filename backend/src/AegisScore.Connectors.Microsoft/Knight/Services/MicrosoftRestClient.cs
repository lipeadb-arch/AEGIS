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

namespace AegisScore.Connectors.Microsoft.Knight.Services;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Cliente REST somente leitura para as APIs Microsoft fora do Graph
// ============================================================================
// Azure Resource Manager, API de administração do Fabric, API de administração do SharePoint e plano de dados do
// Key Vault. Três garantias, as mesmas do cliente do Graph:
//   • DESTINO FIXO — só HTTPS e só os hosts oficiais abaixo; um "próximo link" que aponte para outro host é recusado
//     (o token não segue para uma origem arbitrária);
//   • SÓ GET — nenhum verbo de escrita existe aqui;
//   • FALHA SANITIZADA — status, código de erro curto e caminho do endpoint (sem query string). Nunca o corpo.
// O limite de taxa é respeitado uma vez (Retry-After até 30 s); persistindo, a leitura falha como "limite de taxa".

public interface IMicrosoftRestClient
{
    Task<JsonElement> GetAsync(string token, string url, CancellationToken ct);

    /// <summary>Itera as páginas de uma coleção (<c>value</c> + <c>nextLink</c>, <c>@odata.nextLink</c> ou <c>continuationUri</c>).</summary>
    IAsyncEnumerable<JsonElement> GetPagedAsync(string token, string url, CancellationToken ct, int maxPages = 200);
}

public sealed class MicrosoftRestClient : IMicrosoftRestClient
{
    /// <summary>Hosts exatos permitidos.</summary>
    private static readonly HashSet<string> ExactHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "management.azure.com",
        "api.fabric.microsoft.com",
        "graph.microsoft.com",
    };

    /// <summary>Sufixos permitidos (host do locatário no SharePoint; cofres no Key Vault).</summary>
    // [AEGIS-KNIGHT-CLOSURE-01] Workspaces do Azure Databricks (API REST do próprio workspace; host oficial *.azuredatabricks.net).
    private static readonly string[] HostSuffixes = { "-admin.sharepoint.com", ".vault.azure.net", ".azuredatabricks.net" };

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MicrosoftRestClient(HttpClient http) : this(http, (d, ct) => Task.Delay(d, ct)) { }

    internal MicrosoftRestClient(HttpClient http, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        _delay = delay;
    }

    public static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (ExactHosts.Contains(uri.Host) || HostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)));

    public async Task<JsonElement> GetAsync(string token, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowed(uri))
            throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "destino fora dos hosts oficiais permitidos");

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                if (wait > TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
                await _delay(wait, ct);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new EntraGraphException(Classify(response.StatusCode, body), $"retornou {(int)response.StatusCode}",
                    (int)response.StatusCode, ErrorCode(body), uri.AbsolutePath);

            if (string.IsNullOrWhiteSpace(body)) return default;
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "resposta não é JSON válido",
                    (int)response.StatusCode, endpointPath: uri.AbsolutePath);
            }
        }
    }

    public async IAsyncEnumerable<JsonElement> GetPagedAsync(
        string token, string url, [EnumeratorCancellation] CancellationToken ct, int maxPages = 200)
    {
        string? next = url;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var first) ? first.Host : null;
        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var root = await GetAsync(token, next, ct);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    yield return item;

            next = NextLink(root, url);
            // O próximo link tem de ficar no MESMO host: o token nunca segue para outro destino.
            if (next is not null && (!Uri.TryCreate(next, UriKind.Absolute, out var nu) || !string.Equals(nu.Host, host, StringComparison.OrdinalIgnoreCase)))
                throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "próximo link fora do host de origem");
        }
    }

    private static string? NextLink(JsonElement root, string original)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "nextLink", "@odata.nextLink", "continuationUri" })
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } link)
                return link;
        // Fabric: só o token de continuação — a mesma URL com o parâmetro documentado.
        if (root.TryGetProperty("continuationToken", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } token)
        {
            var baseUrl = original.Split('?')[0];
            return $"{baseUrl}?continuationToken={Uri.EscapeDataString(token)}";
        }
        return null;
    }

    private static EntraGraphErrorKind Classify(HttpStatusCode status, string body) => status switch
    {
        HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => EntraGraphErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => EntraGraphErrorKind.Throttled,
        // O Resource Manager devolve 400/404 com código de autorização em alguns provedores.
        _ when (ErrorCode(body) ?? "").Contains("Authorization", StringComparison.OrdinalIgnoreCase) => EntraGraphErrorKind.InsufficientPermission,
        _ => EntraGraphErrorKind.Unavailable,
    };

    /// <summary>Código de erro curto da resposta (<c>error.code</c> ou <c>errorCode</c>) — nunca a mensagem.</summary>
    internal static string? ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString();
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
            }
            if (root.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.String) return ec.GetString();
            // [AEGIS-KNIGHT-CLOSURE-01] Formato de erro da API REST do Databricks.
            if (root.TryGetProperty("error_code", out var dec) && dec.ValueKind == JsonValueKind.String) return dec.GetString();
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String) return code.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
