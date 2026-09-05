using Microsoft.Extensions.Options;
using AegisScore.Application.Services;
using AegisScore.Connectors.Microsoft;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-PRODUCT-01] O provedor documental do SharePoint ainda é um STUB: ele SINTETIZA políticas com
/// conteúdo plausível que atravessa o mesmo pipeline (extração → IA → teto documental) e apareceria para o
/// cliente como a política corporativa DELE. Estes testes provam a segunda barreira — a fábrica já o esconde
/// do caminho operacional, e uma injeção direta que contorne a fábrica FALHA em vez de ingerir ficção.
///
/// A demonstração continua possível: com a chave explícita da instância ligada, o stub volta a produzir o
/// mesmo lote de sempre. Nada aqui remove documentos já persistidos — o guard age na AQUISIÇÃO.
/// </summary>
public sealed class SimulatedDocumentProviderGuardTests
{
    [Fact]
    public void SharePointProvider_SeDeclaraSimulado()
    {
        Provider(allowSimulated: false).IsSimulated
            .Should().BeTrue("enquanto for stub, o provedor precisa se declarar — é o que a fábrica lê para escondê-lo");
    }

    [Fact]
    public async Task FetchPolicies_ForaDoModoDemonstrativo_Falha()
    {
        var act = async () => await Provider(allowSimulated: false).FetchPoliciesAsync(Guid.NewGuid());

        (await act.Should().ThrowAsync<InvalidOperationException>(
            "ingerir política sintética sob o nome do cliente é pior que falhar"))
            .WithMessage("*SIMULADO*");
    }

    [Fact]
    public async Task FetchPolicies_EmModoDemonstrativoExplicito_ContinuaFuncionando()
    {
        var policies = await Provider(allowSimulated: true).FetchPoliciesAsync(Guid.NewGuid());

        policies.Should().NotBeEmpty("a demonstração de ponta a ponta segue disponível sob a chave explícita");
    }

    [Fact]
    public void ProvedorReal_NaoPrecisaDeclararNada()
    {
        // Tipado pela PORTA de propósito: o default só existe na interface (implementação padrão).
        ((IDocumentIntegrationProvider)new RealProvider()).IsSimulated
            .Should().BeFalse("o padrão da porta é 'real' — só o stub carrega o ônus da declaração");
    }

    private static SharePointProvider Provider(bool allowSimulated) =>
        new(Options.Create(new DocumentIntegrationOptions { AllowSimulatedProviders = allowSimulated }));

    /// <summary>Implementação mínima da porta que NÃO sobrescreve <c>IsSimulated</c> — prova o default.</summary>
    private sealed class RealProvider : IDocumentIntegrationProvider
    {
        public ConnectorProvider Provider => ConnectorProvider.Google;
        public Task<IEnumerable<DocumentDto>> FetchPoliciesAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult(Enumerable.Empty<DocumentDto>());
    }
}
