using System.Linq;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Fronteira de dados por configuração: modo externo + chave + allowlist. Prova tanto o uso demonstrativo
/// quanto o corporativo sem rede nem banco.
/// </summary>
public sealed class AiFreeTierGateTests
{
    private static AiFreeTierGate Gate(AiMode mode, string apiKey, params string[] allow) =>
        new(Options.Create(new AiOptions
        {
            Mode = mode,
            ApiKey = apiKey,
            FreeTier = new AiFreeTierOptions { AllowedTenantSlugs = allow.ToList() },
        }));

    [Theory]
    [InlineData(AiMode.ExternalDemo)]
    [InlineData(AiMode.ExternalEnterprise)]
    public void ModoExterno_SemChave_NuncaLibera(AiMode mode)
    {
        var g = Gate(mode, "", "tenant");

        g.ProviderConfigured.Should().BeFalse();
        g.IsExternalAllowedForSlug("tenant").Should().BeFalse();
    }

    [Fact]
    public void ModoSimulado_NuncaLiberaExterno_MesmoComChaveEAllowlist()
    {
        var g = Gate(AiMode.Simulated, "chave", "tenant");

        g.ProviderConfigured.Should().BeFalse();
        g.IsExternalAllowedForSlug("tenant").Should().BeFalse();
    }

    [Theory]
    [InlineData(AiMode.ExternalDemo)]
    [InlineData(AiMode.ExternalEnterprise)]
    public void ModoExterno_ComChave_LiberaSomenteAllowlist_CaseInsensitive(AiMode mode)
    {
        var g = Gate(mode, "chave", "tenant-autorizado");

        g.ProviderConfigured.Should().BeTrue();
        g.IsExternalAllowedForSlug("tenant-autorizado").Should().BeTrue();
        g.IsExternalAllowedForSlug("TENANT-AUTORIZADO").Should().BeTrue();
        g.IsExternalAllowedForSlug("outro-tenant").Should().BeFalse("fora da allowlist nunca libera");
        g.IsExternalAllowedForSlug(null).Should().BeFalse();
        g.IsExternalAllowedForSlug("").Should().BeFalse();
    }

    [Theory]
    [InlineData(AiMode.ExternalDemo)]
    [InlineData(AiMode.ExternalEnterprise)]
    public void AllowlistVazia_NuncaLibera(AiMode mode)
    {
        var g = Gate(mode, "chave");

        g.ProviderConfigured.Should().BeTrue();
        g.IsExternalAllowedForSlug("qualquer").Should().BeFalse();
    }
}
