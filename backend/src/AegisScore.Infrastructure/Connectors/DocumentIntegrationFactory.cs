using Microsoft.Extensions.Options;
using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// Roteador do Provider Pattern de ingestão documental: mapeia o fornecedor (vindo da configuração do
/// tenant) para a estratégia <see cref="IDocumentIntegrationProvider"/> registrada na DI. Adicionar uma
/// fonte nova (Google Workspace, Confluence…) é registrar mais um provider — a fábrica e o resto do núcleo
/// ficam intactos. Mesmo idioma do <see cref="ConnectorRegistry"/> (que resolve <c>IEvidenceConnector</c>):
/// resolve estratégias já injetadas, sem <c>new</c> manual.
///
/// [AEGIS-MVP-PRODUCT-01] A fábrica é também o GATE de honestidade: um provedor que declara
/// <see cref="IDocumentIntegrationProvider.IsSimulated"/> só é resolvível com o modo demonstrativo
/// EXPLICITAMENTE ligado (<see cref="DocumentIntegrationOptions.AllowSimulatedProviders"/>). Fora dele o
/// provedor simulado não existe para o caminho operacional — o worker de ingestão simplesmente não o
/// encontra, e a superfície de sync responde que a integração não está disponível. Documentos JÁ
/// persistidos não são tocados: o gate age na aquisição, nunca no acervo.
/// </summary>
public sealed class DocumentIntegrationFactory : IDocumentIntegrationFactory
{
    private readonly IReadOnlyDictionary<ConnectorProvider, IDocumentIntegrationProvider> _providers;
    private readonly bool _allowSimulated;

    public DocumentIntegrationFactory(
        IEnumerable<IDocumentIntegrationProvider> providers,
        IOptions<DocumentIntegrationOptions> options)
    {
        _providers = providers.ToDictionary(p => p.Provider);
        _allowSimulated = options.Value.AllowSimulatedProviders;
    }

    public IDocumentIntegrationProvider? GetProvider(ConnectorProvider provider)
    {
        if (!_providers.TryGetValue(provider, out var p)) return null;
        // Simulado fora do modo demonstrativo = inexistente. Devolver null (e não lançar) preserva o
        // contrato do chamador: o worker registra "sem provedor" e segue com os demais tenants/fontes.
        return p.IsSimulated && !_allowSimulated ? null : p;
    }

    public DocumentIntegrationAvailability GetAvailability()
    {
        var usable = _providers.Values
            .Where(p => !p.IsSimulated || _allowSimulated)
            .Select(p => p.Provider.ToString())
            .OrderBy(name => name)
            .ToList();

        return new DocumentIntegrationAvailability(
            // "Operacional" exige fonte REAL: em modo demonstrativo o provedor simulado é resolvível, mas
            // não passa a valer como capacidade de produção — a interface precisa distinguir os dois casos.
            HasOperationalProvider: _providers.Values.Any(p => !p.IsSimulated),
            SimulatedModeEnabled: _allowSimulated,
            AvailableProviders: usable);
    }
}
