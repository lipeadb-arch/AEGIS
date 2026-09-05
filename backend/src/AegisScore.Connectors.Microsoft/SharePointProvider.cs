using System.Text;
using Microsoft.Extensions.Options;
using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft;

/// <summary>
/// Provedor de ingestão de documentos do Microsoft 365 (SharePoint / Microsoft Graph). Implementa o
/// Provider Pattern <see cref="IDocumentIntegrationProvider"/> — o núcleo o enxerga apenas como "a fonte
/// de políticas do stack Microsoft", nunca como Graph API.
///
/// STUB (esta fase): NÃO chama Graph/HTTP ainda. Devolve um lote de políticas MOCKADAS — com conteúdo
/// textual real, para o pipeline de extração/IA rodar de ponta a ponta — o suficiente para PROVAR a
/// injeção de dependência e o fluxo agnóstico fábrica → provedor → worker → hub. A implementação real
/// autentica via OAuth client credentials (segredos em <see cref="ConnectorConfig.EncryptedSettings"/>) e
/// lista/baixa os itens da biblioteca de documentos (GET /sites/{id}/drive/root/children). Adicionar o
/// Google Workspace é outra <see cref="IDocumentIntegrationProvider"/>, num pacote Connectors.Google —
/// nada no núcleo muda.
///
/// [AEGIS-MVP-PRODUCT-01] Enquanto for STUB, este provedor se declara SIMULADO
/// (<see cref="IsSimulated"/>) e fica FORA do caminho operacional: a
/// <c>DocumentIntegrationFactory</c> não o resolve sem o modo demonstrativo explícito. O guard abaixo é a
/// segunda barreira — se alguma composição futura o injetar direto, contornando a fábrica, a chamada FALHA
/// em vez de entregar políticas fictícias como se fossem do cliente. Documentos já persistidos por
/// execuções anteriores permanecem intocados: a proteção age na AQUISIÇÃO, não no acervo.
/// </summary>
public sealed class SharePointProvider : IDocumentIntegrationProvider
{
    private readonly DocumentIntegrationOptions _options;

    public SharePointProvider(IOptions<DocumentIntegrationOptions> options) => _options = options.Value;

    public ConnectorProvider Provider => ConnectorProvider.Microsoft;

    /// <summary>Sintetiza documentos — não lê o SharePoint real. Ver o comentário da classe.</summary>
    public bool IsSimulated => true;

    public Task<IEnumerable<DocumentDto>> FetchPoliciesAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Defesa em profundidade: a fábrica já esconde este provedor fora do modo demonstrativo. Chegar
        // aqui com a simulação desligada significa que alguém o injetou por fora — e ingerir conteúdo
        // sintético como política corporativa do cliente é pior que falhar.
        if (!_options.AllowSimulatedProviders)
            throw new InvalidOperationException(
                "O provedor de documentos do SharePoint é SIMULADO e está desativado. " +
                "Ligue DocumentIntegration:AllowSimulatedProviders apenas em demonstração explícita.");

        // MOCK: representa a biblioteca "Políticas de Segurança" de um site do SharePoint. Cada item traz
        // conteúdo textual (text/plain) que o PlainTextExtractor lê e a IA mapeia a controles do Govern.
        IEnumerable<DocumentDto> policies = new[]
        {
            new DocumentDto(
                Title: "Política de Segurança da Informação",
                SourceReference: "https://contoso.sharepoint.com/sites/governanca/Politicas/PSI.aspx",
                FileName: "politica-seguranca-informacao.txt",
                ContentType: "text/plain",
                Type: GovernanceDocumentType.Politica,
                Content: Utf8(
                    "POLÍTICA DE SEGURANÇA DA INFORMAÇÃO\n" +
                    "A organização estabelece, comunica e mantém uma política de segurança da informação\n" +
                    "aprovada pela alta direção, com papéis e responsabilidades definidos (GV.PO, GV.RR).\n" +
                    "A política é revisada periodicamente e sempre que houver mudança relevante.")),

            new DocumentDto(
                Title: "Norma de Gestão de Riscos de Fornecedores (Supply Chain)",
                SourceReference: "https://contoso.sharepoint.com/sites/governanca/Politicas/SCRM.aspx",
                FileName: "gestao-riscos-fornecedores.txt",
                ContentType: "text/plain",
                Type: GovernanceDocumentType.Norma,
                Content: Utf8(
                    "GESTÃO DE RISCOS DA CADEIA DE SUPRIMENTOS\n" +
                    "Fornecedores de tecnologia com acesso à rede corporativa passam por due diligence e\n" +
                    "auditoria periódica de segurança (GV.SC). Os contratos incluem cláusulas de segurança\n" +
                    "e direito de auditoria.")),
        };

        return Task.FromResult(policies);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
