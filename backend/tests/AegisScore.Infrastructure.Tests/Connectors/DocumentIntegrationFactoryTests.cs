using Microsoft.Extensions.Options;
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
///
/// [AEGIS-MVP-PRODUCT-01] Cobre também o GATE de honestidade: um provedor que se declara SIMULADO fica fora
/// do caminho operacional a menos que o modo demonstrativo esteja explicitamente ligado.
/// </summary>
public sealed class DocumentIntegrationFactoryTests
{
    [Fact]
    public void GetProvider_ResolveEstrategiaRegistradaPeloFornecedor()
    {
        var sharepoint = new FakeProvider(ConnectorProvider.Microsoft);
        var factory = FactoryWith(allowSimulated: false, sharepoint);

        factory.GetProvider(ConnectorProvider.Microsoft)
            .Should().BeSameAs(sharepoint, "a fábrica devolve a estratégia do stack configurado pelo tenant");
    }

    [Fact]
    public void GetProvider_FornecedorSemEstrategia_DevolveNull()
    {
        var factory = FactoryWith(allowSimulated: false, new FakeProvider(ConnectorProvider.Microsoft));

        // Tenant configurou Google, mas o conector do Google ainda não foi implantado: null → o worker ignora.
        factory.GetProvider(ConnectorProvider.Google)
            .Should().BeNull("sem estratégia registrada, a fábrica não inventa um provedor");
    }

    [Fact]
    public void GetProvider_ProvedorSimulado_NaoEhResolvidoNoCaminhoOperacional()
    {
        var factory = FactoryWith(allowSimulated: false, new FakeProvider(ConnectorProvider.Microsoft, simulated: true));

        factory.GetProvider(ConnectorProvider.Microsoft)
            .Should().BeNull("um provedor que sintetiza documentos não pode ingerir política fictícia como se fosse do cliente");
    }

    [Fact]
    public void GetProvider_ProvedorSimulado_ResolveComModoDemonstrativoExplicito()
    {
        var demo = new FakeProvider(ConnectorProvider.Microsoft, simulated: true);
        var factory = FactoryWith(allowSimulated: true, demo);

        factory.GetProvider(ConnectorProvider.Microsoft)
            .Should().BeSameAs(demo, "a demonstração continua possível — mas só sob uma chave explícita da instância");
    }

    [Fact]
    public void GetAvailability_SoSimulado_SemModoDemo_NaoAnunciaCapacidadeAlguma()
    {
        var availability = FactoryWith(allowSimulated: false,
            new FakeProvider(ConnectorProvider.Microsoft, simulated: true)).GetAvailability();

        availability.HasOperationalProvider.Should().BeFalse("não há fonte documental REAL implantada");
        availability.SimulatedModeEnabled.Should().BeFalse();
        availability.AvailableProviders.Should().BeEmpty("nada é resolvível — a interface precisa dizer isso");
    }

    [Fact]
    public void GetAvailability_SoSimulado_ComModoDemo_DistingueDemonstracaoDeCapacidadeReal()
    {
        var availability = FactoryWith(allowSimulated: true,
            new FakeProvider(ConnectorProvider.Microsoft, simulated: true)).GetAvailability();

        availability.AvailableProviders.Should().ContainSingle("em demonstração o provedor é resolvível");
        availability.SimulatedModeEnabled.Should().BeTrue();
        availability.HasOperationalProvider.Should().BeFalse(
            "modo demonstrativo NÃO promove um stub a capacidade de produção");
    }

    [Fact]
    public void GetAvailability_ProvedorReal_AnunciaCapacidadeOperacional()
    {
        var availability = FactoryWith(allowSimulated: false,
            new FakeProvider(ConnectorProvider.Google)).GetAvailability();

        availability.HasOperationalProvider.Should().BeTrue();
        availability.AvailableProviders.Should().ContainSingle().Which.Should().Be(nameof(ConnectorProvider.Google));
    }

    private static DocumentIntegrationFactory FactoryWith(bool allowSimulated, params IDocumentIntegrationProvider[] providers) =>
        new(providers, Options.Create(new DocumentIntegrationOptions { AllowSimulatedProviders = allowSimulated }));

    private sealed class FakeProvider : IDocumentIntegrationProvider
    {
        public FakeProvider(ConnectorProvider provider, bool simulated = false)
        {
            Provider = provider;
            IsSimulated = simulated;
        }

        public ConnectorProvider Provider { get; }
        public bool IsSimulated { get; }
        public Task<IEnumerable<DocumentDto>> FetchPoliciesAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(Enumerable.Empty<DocumentDto>());
    }
}
