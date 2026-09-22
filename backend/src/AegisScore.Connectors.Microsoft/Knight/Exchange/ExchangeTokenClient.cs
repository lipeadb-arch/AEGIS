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
///     <b>HIPÓTESE do AEGIS, a ser testada — não documentação e não conclusão.</b> De (2) o AEGIS SUPÕE que um
///     token de credenciais de cliente, emitido para a MESMA aplicação e o MESMO recurso, carregue as mesmas
///     reivindicações de papel e seja aceito. Duas coisas impedem de afirmar isso: a Microsoft não publica
///     declaração de que o segredo de cliente seja um meio suportado para este comando, e <b>ter as mesmas
///     permissões não implica que todo método de autenticação seja aceito</b> — o serviço pode exigir a prova
///     de posse que só o certificado dá, e essa exigência não apareceria em nenhuma leitura de documentação.
///     A hipótese é falseável e tem um teste definido: conectar contra um locatário real. Enquanto esse teste
///     não for feito, o método é uma APOSTA FUNDAMENTADA, e não um caminho demonstrado.
///   </description></item>
///   <item><description>
///     <b>NÃO VALIDADO.</b> Nenhuma conexão real foi estabelecida. Os testes deste pacote são SINTÉTICOS: eles
///     exercitam o contrato do documento de saída, a tradução para o ADM, a avaliação e as exportações. O gate
///     da imagem exercita a importação do módulo OFFLINE — o que ele demonstra é que o módulo fixado CARREGA
///     nesta imagem e que os comandos de conexão existem nela; ele NÃO demonstra compatibilidade integral com
///     Debian, não autentica, não carrega os comandos que só entram na sessão depois da conexão e não realiza
///     coleta alguma. Nada disso demonstra que o locatário aceita o token: essa é a primeira verificação da
///     homologação, e permanece como LIMITAÇÃO declarada.
///   </description></item>
/// </list>
///
/// <para><b>Se a conexão for recusada.</b> Uma recusa é ambígua por natureza e o produto não lhe atribui causa:
/// ele informa o que o serviço devolveu e lista as verificações a fazer — consentimento de
/// <c>Exchange.ManageAsApp</c>, atribuição de papel de diretório à aplicação, domínio da organização usado, e
/// suporte do método de autenticação em si. Afirmar, a partir de uma recusa, que faltam exatamente a permissão
/// de API e o papel de diretório seria inventar a causa a partir do sintoma.</para>
///
/// <para><b>Certificado: alternativa futura, não fallback.</b> Não existe caminho por certificado implementado
/// aqui — não há troca automática nem degradação graciosa. Se a homologação demonstrar que o segredo não
/// serve, adotar o certificado exige mudança de código e de configuração, e passa a ser trabalho planejado. O
/// que NÃO se faz é exigir do cliente a emissão, a distribuição e a rotação de um certificado antes de haver
/// necessidade demonstrada.</para>
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
