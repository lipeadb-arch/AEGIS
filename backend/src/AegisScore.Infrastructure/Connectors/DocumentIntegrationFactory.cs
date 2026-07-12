using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// Roteador do Provider Pattern de ingestão documental: mapeia o fornecedor (vindo da configuração do
/// tenant) para a estratégia <see cref="IDocumentIntegrationProvider"/> registrada na DI. Adicionar uma
/// fonte nova (Google Workspace, Confluence…) é registrar mais um provider — a fábrica e o resto do núcleo
/// ficam intactos. Mesmo idioma do <see cref="ConnectorRegistry"/> (que resolve <c>IEvidenceConnector</c>):
/// resolve estratégias já injetadas, sem <c>new</c> manual.
/// </summary>
public sealed class DocumentIntegrationFactory : IDocumentIntegrationFactory
{
    private readonly IReadOnlyDictionary<ConnectorProvider, IDocumentIntegrationProvider> _providers;

    public DocumentIntegrationFactory(IEnumerable<IDocumentIntegrationProvider> providers)
        => _providers = providers.ToDictionary(p => p.Provider);

    public IDocumentIntegrationProvider? GetProvider(ConnectorProvider provider) =>
        _providers.TryGetValue(provider, out var p) ? p : null;
}
