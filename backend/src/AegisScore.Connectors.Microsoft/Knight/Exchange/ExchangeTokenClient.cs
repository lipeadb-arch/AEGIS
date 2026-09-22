using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Microsoft.Knight.Exchange;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Obtém o que a conexão de aplicativo com o Exchange Online exige: um token do
/// RECURSO do Exchange e o DOMÍNIO da organização.
///
/// <para><b>Por que o token é outro.</b> O Exchange Online PowerShell valida um token emitido para
/// <c>https://outlook.office365.com</c>. O token do Microsoft Graph e o da administração do Teams são de outros
/// recursos: não valem aqui, e reaproveitá-los seria entregar um token a um destino para o qual ele não foi
/// emitido. Cada um é pedido com o seu próprio escopo, pelo fluxo de credenciais de cliente, com as MESMAS
/// credenciais do conector Microsoft já configurado — nenhuma credencial nova é pedida ao cliente.</para>
///
/// <para><b>Evidência que sustenta este método, e o que ela NÃO cobre.</b> A decisão foi tomada a partir da
/// documentação oficial, e o registro abaixo separa o que está DOCUMENTADO do que é INFERÊNCIA — porque a
/// existência do parâmetro, sozinha, não comprova o fluxo:</para>
///
/// <list type="number">
///   <item><description>
///     <b>DOCUMENTADO.</b> A referência de <c>Connect-ExchangeOnline</c>
///     (learn.microsoft.com/powershell/module/exchangepowershell/connect-exchangeonline) descreve o parâmetro
///     <c>-AccessToken</c>, disponível a partir da versão 3.1.0-Preview1 do módulo, e determina que, para um
///     token de APLICATIVO, ele seja usado junto de <c>-Organization</c>. A versão fixada na imagem (3.9.2) é
///     posterior a essa, e o gate de runtime confere que o comando existe nela.
///   </description></item>
///   <item><description>
///     <b>DOCUMENTADO.</b> O artigo de autenticação apenas de aplicativo
///     (learn.microsoft.com/powershell/exchange/app-only-auth-powershell-v2), na seção que explica o
///     funcionamento, afirma que o RBAC da sessão é configurado a partir da informação de PAPEL DE DIRETÓRIO
///     disponível NO TOKEN. A autorização, portanto, viaja dentro do token — não no meio usado para obtê-lo.
///     O mesmo artigo descreve o certificado como a forma de obter esse token.
///   </description></item>
///   <item><description>
///     <b>INFERÊNCIA, não documentação.</b> De (2) segue que um token de credenciais de cliente para o mesmo
///     recurso, emitido para a mesma aplicação, carrega as mesmas reivindicações de papel e é aceito pela
///     mesma validação. A Microsoft NÃO publica uma afirmação explícita de que o segredo de cliente é um meio
///     suportado para este comando. Logo: o certificado é tratado aqui como um caminho de obtenção, e não
///     como requisito do serviço — mas isso é conclusão do AEGIS, e não citação.
///   </description></item>
///   <item><description>
///     <b>NÃO VALIDADO.</b> Nenhuma conexão real foi estabelecida. Os testes deste pacote são SINTÉTICOS: eles
///     exercitam o contrato do documento de saída, a tradução para o ADM, a avaliação e as exportações, e o
///     gate da imagem exercita a importação do módulo OFFLINE. Nada disso demonstra que o locatário aceita o
///     token: essa é a primeira verificação da homologação, e permanece como LIMITAÇÃO declarada. Se a recusa
///     vier, ela chega classificada como autorização (não como falha de autenticação) e o produto informa as
///     duas concessões que faltam — permissão de API e papel de diretório.
///   </description></item>
/// </list>
///
/// <para>O caminho por certificado não foi descartado: ele é a alternativa imediata caso a homologação
/// demonstre que o segredo não serve. O que NÃO se faz é exigir do cliente a emissão, a distribuição e a
/// rotação de um certificado antes de haver necessidade demonstrada.</para>
///
/// <para><b>Por que o domínio é resolvido aqui, e agora.</b> O parâmetro de organização da conexão pede o
/// domínio <c>.onmicrosoft.com</c> principal, não o identificador do locatário — e o conector guarda o
/// identificador. O domínio é lido na PRÓPRIA aquisição, pelo Microsoft Graph, com a permissão
/// <c>Organization.Read.All</c> que este conector já exige. Herdá-lo de uma coleta anterior seria usar
/// evidência de outra aquisição como se fosse desta.</para>
///
/// As autoridades e os identificadores de recurso são CONSTANTES oficiais aqui — o locatário nunca fornece um
/// endereço de destino (do contrário, um locatário malicioso poderia dirigir o token para um endpoint próprio).
/// </summary>
public interface IExchangeTokenClient
{
    Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default);
}

/// <inheritdoc cref="IExchangeTokenClient"/>
public sealed class ExchangeTokenClient : IExchangeTokenClient
{
    private const string LoginBaseUrl = "https://login.microsoftonline.com";

    /// <summary>
    /// Escopo do recurso do Exchange Online. É o mesmo recurso que a documentação usa na URL de consentimento
    /// administrativo da aplicação para Exchange Online.
    /// </summary>
    internal const string ExchangeScope = "https://outlook.office365.com/.default";

    /// <summary>Leitura do domínio inicial do locatário. <c>Organization.Read.All</c> cobre esta consulta.</summary>
    internal const string OrganizationUrl = "organization?$select=verifiedDomains";

    private readonly HttpClient _http;
    private readonly IEntraGraphClient _graph;

    public ExchangeTokenClient(HttpClient http, IEntraGraphClient graph)
    {
        _http = http;
        _graph = graph;
    }

    public async Task<ExchangeAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var organization = await ResolveOrganizationAsync(credentials, ct);
        var token = await AcquireExchangeTokenAsync(credentials, ct);
        return new ExchangeAdminCredentials(organization, token);
    }

    /// <summary>
    /// Domínio INICIAL do locatário (o <c>.onmicrosoft.com</c> criado com ele e que nunca muda). A resposta traz
    /// os domínios verificados com um sinalizador de "inicial"; só ele serve, porque um domínio personalizado
    /// pode ser removido e o parâmetro de organização deixaria de resolver.
    /// </summary>
    private async Task<string> ResolveOrganizationAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct)
    {
        var graphToken = await _graph.AcquireTokenAsync(credentials, ct);
        var body = await _graph.GetJsonAsync(graphToken, credentials, OrganizationUrl, ct);

        var domains = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.Object)
                .SelectMany(o => o.TryGetProperty("verifiedDomains", out var vd) && vd.ValueKind == JsonValueKind.Array
                    ? vd.EnumerateArray()
                    : Enumerable.Empty<JsonElement>())
                .ToList()
            : new List<JsonElement>();

        var initial = domains.FirstOrDefault(d =>
            d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("isInitial", out var f) && f.ValueKind == JsonValueKind.True);

        if (initial.ValueKind == JsonValueKind.Object
            && initial.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            && n.GetString() is { Length: > 0 } name)
            return name;

        // Sem o domínio não há conexão possível, e inventar um valor plausível a partir do identificador do
        // locatário seria adivinhar. A falha é declarada com a causa, e a coleta inteira fica não avaliada.
        throw new EntraGraphException(EntraGraphErrorKind.Unavailable,
            "a resposta da organização não trouxe o dominio inicial do locatario",
            endpointPath: "/organization");
    }

    private async Task<string> AcquireExchangeTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct)
    {
        var url = $"{LoginBaseUrl}/{Uri.EscapeDataString(config.AzureTenantId)}/oauth2/v2.0/token";
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = ExchangeScope,
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
