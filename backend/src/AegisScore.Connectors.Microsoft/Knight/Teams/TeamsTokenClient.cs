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
        // Ordem e recursos da documentação: primeiro o Microsoft Graph, depois a API de administração do Teams.
        var graph = await AcquireAsync(credentials, GraphScope, GraphResource, ct);
        var teams = await AcquireAsync(credentials, TeamsScope, TeamsResource, ct);
        return new TeamsAdminCredentials(credentials.AzureTenantId, graph, teams);
    }

    /// <summary>[AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Qual dos dois tokens foi recusado — a etapa exata da falha.</summary>
    internal const string GraphResource = "Microsoft Graph (1º token do Teams)";
    internal const string TeamsResource = "Skype and Teams Tenant Admin API (2º token do Teams)";

    private async Task<string> AcquireAsync(IMicrosoftGraphCredentials config, string scope, string resource, CancellationToken ct)
    {
        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(config.AzureTenantId)}/oauth2/v2.0/token";
        // [AEGIS-KNIGHT-COVERAGE-04] Segredo OU certificado: o corpo vem do formulário compartilhado.
        using var form = new FormUrlEncodedContent(EntraGraphClient.CredentialFields(config, scope));

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        // [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Antes só o status chegava ao diagnóstico; agora também error, AADSTS, trace e
        // correlação — e qual dos dois tokens foi recusado. A descrição e o corpo continuam fora.
        if (!response.IsSuccessStatusCode)
            throw EntraGraphException.TokenRefused(Classify(response.StatusCode), response, body, resource);
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
            "resposta do token endpoint sem access_token", endpointPath: "/oauth2/v2.0/token") { TokenResource = resource };
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
