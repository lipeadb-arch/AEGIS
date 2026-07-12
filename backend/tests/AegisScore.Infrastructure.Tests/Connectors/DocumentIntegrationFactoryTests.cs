using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// Prova o roteador do Provider Pattern de ingestão documental (<see cref="DocumentIntegrationFactory"/>):
/// dado o fornecedor configurado pelo tenant, resolve a estratégia certa — a defesa contra vendor lock-in.
/// Puro, sem banco: exercita apenas a seleção por <c>ConnectorProvider</c>.
/// </summary>
public sealed class DocumentIntegrationFactoryTests
{
    [Fact]
    public void GetProvider_ResolveEstrategiaRegistradaPeloFornecedor()
    {
        var sharepoint = new FakeProvider(ConnectorProvider.Microsoft);
        var factory = new DocumentIntegrationFactory(new IDocumentIntegrationProvider[] { sharepoint });

        factory.GetProvider(ConnectorProvider.Microsoft)
            .Should().BeSameAs(sharepoint, "a fábrica devolve a estratégia do stack configurado pelo tenant");
    }

    [Fact]
    public void GetProvider_FornecedorSemEstrategia_DevolveNull()
    {
        var factory = new DocumentIntegrationFactory(
            new IDocumentIntegrationProvider[] { new FakeProvider(ConnectorProvider.Microsoft) });

        // Tenant configurou Google, mas o conector do Google ainda não foi implantado: null → o worker ignora.
        factory.GetProvider(ConnectorProvider.Google)
            .Should().BeNull("sem estratégia registrada, a fábrica não inventa um provedor");
    }

    private sealed class FakeProvider : IDocumentIntegrationProvider
    {
        public FakeProvider(ConnectorProvider provider) => Provider = provider;
        public ConnectorProvider Provider { get; }
        public Task<IEnumerable<DocumentDto>> FetchPoliciesAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(Enumerable.Empty<DocumentDto>());
    }
}
