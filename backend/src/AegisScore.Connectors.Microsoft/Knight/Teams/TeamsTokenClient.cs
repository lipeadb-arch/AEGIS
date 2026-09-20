using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Microsoft.Knight.Teams;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Obtém os DOIS tokens de aplicativo que o módulo Teams PowerShell exige para a
/// autenticação de aplicativo por tokens de acesso.
///
/// São dois porque são DOIS RECURSOS: o Microsoft Graph e a API de administração do Teams. Um token emitido
/// para o Graph não vale no recurso do Teams — e reaproveitá-lo seria, além de inútil, entregar um token a um
/// destino para o qual ele não foi emitido. Cada um é pedido com o seu próprio escopo, pelo fluxo de
/// credenciais de cliente, com as MESMAS credenciais do conector Microsoft já configurado: nenhuma credencial
/// nova é pedida ao cliente e nenhuma permissão é concedida pelo AEGIS.
///
/// As autoridades e os identificadores de recurso são CONSTANTES oficiais aqui — o locatário nunca fornece um
/// endereço de destino (do contrário, um tenant malicioso poderia dirigir o token para um endpoint próprio).
/// </summary>
public interface ITeamsTokenClient
{
    Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default);
}

/// <inheritdoc cref="ITeamsTokenClient"/>
public sealed class TeamsTokenClient : ITeamsTokenClient
{
    private const string LoginBaseUrl = "https://login.microsoftonline.com";

    /// <summary>Escopo do Microsoft Graph (um dos dois tokens exigidos pela conexão).</summary>
    internal const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>
    /// Escopo da API de administração de locatário do Skype e do Teams — o identificador de recurso publicado
    /// pela Microsoft na documentação da autenticação de aplicativo do módulo Teams PowerShell.
    /// </summary>
    internal const string TeamsScope = "48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default";

    private readonly HttpClient _http;

    public TeamsTokenClient(HttpClient http) => _http = http;

    public async Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var graph = await AcquireAsync(credentials, GraphScope, ct);
        var teams = await AcquireAsync(credentials, TeamsScope, ct);
        return new TeamsAdminCredentials(credentials.AzureTenantId, graph, teams);
    }

    private async Task<string> AcquireAsync(IMicrosoftGraphCredentials config, string scope, CancellationToken ct)
    {
        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(config.AzureTenantId)}/oauth2/v2.0/token";
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = scope,
            ["grant_type"] = "client_credentials",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new EntraGraphException(
                Classify(response.StatusCode),
                $"token endpoint retornou {(int)response.StatusCode}",
                (int)response.StatusCode,
                endpointPath: "/oauth2/v2.0/token");

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == JsonValueKind.String
                && token.GetString() is { Length: > 0 } value)
                return value;
        }
        catch (JsonException)
        {
            // Cai no lançamento abaixo: a resposta não é utilizável, e o corpo nunca é propagado.
        }

        throw new EntraGraphException(EntraGraphErrorKind.AuthFailure,
            "resposta do token endpoint sem access_token", endpointPath: "/oauth2/v2.0/token");
    }

    private static EntraGraphErrorKind Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => EntraGraphErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => EntraGraphErrorKind.Throttled,
        HttpStatusCode.BadRequest => EntraGraphErrorKind.AuthFailure,
        _ => EntraGraphErrorKind.Unavailable,
    };
}
