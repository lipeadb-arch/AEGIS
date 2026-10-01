using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Microsoft.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Credencial de APLICATIVO: segredo OU certificado
// ============================================================================
// Todo cliente de token do KNIGHT (Graph, Teams, Exchange, Security & Compliance, administração do SharePoint,
// Fabric e Azure Resource Manager) pede o token pelo MESMO fluxo de credenciais de cliente. O que muda é como a
// aplicação prova quem é:
//   • SEGREDO — `client_secret` no corpo, como até o bloco anterior;
//   • CERTIFICADO — `client_assertion`: um JWT assinado com a chave privada do certificado registrado na aplicação.
//     É a forma que a Microsoft documenta para a autenticação de aplicativo do Exchange Online, do Security &
//     Compliance PowerShell e da API de administração do SharePoint (que RECUSA token obtido por segredo).
// Havendo certificado, ele é usado; o segredo fica como alternativa para os serviços que o aceitam. Os endereços
// são constantes oficiais — o locatário nunca fornece destino — e nem o segredo nem a chave saem deste processo.

/// <summary>Monta o corpo do pedido de token de aplicativo, com segredo ou com asserção assinada por certificado.</summary>
public static class MicrosoftClientCredentialForm
{
    /// <summary>Autoridade oficial de login (constante — o locatário não escolhe destino).</summary>
    public const string LoginBaseUrl = "https://login.microsoftonline.com";

    /// <summary>Tipo documentado da asserção de cliente por JWT.</summary>
    internal const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Endereço do endpoint de token do locatário.</summary>
    public static string TokenEndpoint(IMicrosoftGraphCredentials credentials) =>
        $"{LoginBaseUrl}/{Uri.EscapeDataString(credentials.AzureTenantId)}/oauth2/v2.0/token";

    /// <summary>Campos do pedido de token para o <paramref name="scope"/> pedido.</summary>
    public static Dictionary<string, string> Fields(IMicrosoftGraphCredentials credentials, string scope, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["scope"] = scope,
            ["grant_type"] = "client_credentials",
        };

        if (credentials.ClientCertificate is { } certificate)
        {
            fields["client_assertion_type"] = AssertionType;
            fields["client_assertion"] = SignedAssertion(credentials, certificate, clock ?? TimeProvider.System);
        }
        else
        {
            fields["client_secret"] = credentials.ClientSecret;
        }

        return fields;
    }

    /// <summary>
    /// JWT de asserção de cliente (RFC 7523), assinado em RS256 com a chave privada do certificado. O cabeçalho leva a
    /// impressão digital do certificado (<c>x5t</c> e <c>x5t#S256</c>), que é como o Microsoft Entra ID localiza a
    /// chave pública registrada na aplicação. Validade curta (10 minutos) e identificador único por pedido.
    /// </summary>
    internal static string SignedAssertion(IMicrosoftGraphCredentials credentials, MicrosoftClientCertificate certificate, TimeProvider clock)
    {
        using var cert = MicrosoftCertificates.Load(certificate);
        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new MicrosoftCertificateException("o certificado da aplicação não tem chave privada RSA utilizável.");

        var now = clock.GetUtcNow();
        var header = new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = Base64Url(cert.GetCertHash(HashAlgorithmName.SHA1)),
            ["x5t#S256"] = Base64Url(cert.GetCertHash(HashAlgorithmName.SHA256)),
        };
        var payload = new Dictionary<string, object>
        {
            ["aud"] = TokenEndpoint(credentials),
            ["iss"] = credentials.ClientId,
            ["sub"] = credentials.ClientId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
        };

        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    internal static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Token de aplicativo para um recurso qualquer da Microsoft, pelo fluxo de credenciais de cliente.</summary>
public interface IMicrosoftAppTokenClient
{
    Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default);
}

/// <inheritdoc cref="IMicrosoftAppTokenClient"/>
public sealed class MicrosoftAppTokenClient : IMicrosoftAppTokenClient
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public MicrosoftAppTokenClient(HttpClient http, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        Dictionary<string, string> fields;
        try
        {
            fields = MicrosoftClientCredentialForm.Fields(credentials, scope, _clock);
        }
        catch (MicrosoftCertificateException ex)
        {
            throw new EntraGraphException(EntraGraphErrorKind.AuthFailure, ex.Message, endpointPath: "/oauth2/v2.0/token");
        }

        using var form = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, MicrosoftClientCredentialForm.TokenEndpoint(credentials)) { Content = form };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new EntraGraphException(Classify(response.StatusCode),
                $"token endpoint retornou {(int)response.StatusCode}", (int)response.StatusCode, endpointPath: "/oauth2/v2.0/token");

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

    internal static EntraGraphErrorKind Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => EntraGraphErrorKind.AuthFailure,
        HttpStatusCode.Forbidden => EntraGraphErrorKind.InsufficientPermission,
        HttpStatusCode.TooManyRequests => EntraGraphErrorKind.Throttled,
        HttpStatusCode.BadRequest => EntraGraphErrorKind.AuthFailure,
        _ => EntraGraphErrorKind.Unavailable,
    };
}
