using System.Linq;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Gate do Free Tier — a FRONTEIRA DE DADOS por configuração. Prova a matriz mode × chave × allowlist sem
/// rede nem banco: só o modo demonstrativo, com chave, e para um slug da allowlist libera o motor externo.
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

    [Fact]
    public void SemChave_ProviderNaoConfigurado_NuncaLiberaExterno()
    {
        var g = Gate(AiMode.GeminiFreeDemo, "", "sandbox");

        g.ProviderConfigured.Should().BeFalse();
        g.IsExternalAllowedForSlug("sandbox").Should().BeFalse("sem chave, nem o tenant da allowlist chama externo");
    }

    [Fact]
    public void ModoSimulado_NuncaLiberaExterno_MesmoComChaveEAllowlist()
    {
        var g = Gate(AiMode.Simulated, "chave", "sandbox");

        g.ProviderConfigured.Should().BeFalse();
        g.IsExternalAllowedForSlug("sandbox").Should().BeFalse();
    }

    [Fact]
    public void GeminiFreeDemo_ComChave_LiberaSomenteAllowlist_CaseInsensitive()
    {
        var g = Gate(AiMode.GeminiFreeDemo, "chave", "sandbox-lab");

        g.ProviderConfigured.Should().BeTrue();
        g.IsExternalAllowedForSlug("sandbox-lab").Should().BeTrue();
        g.IsExternalAllowedForSlug("SANDBOX-LAB").Should().BeTrue("a comparação de slug é case-insensitive");
        g.IsExternalAllowedForSlug("tenant-corporativo").Should().BeFalse("fora da allowlist NUNCA libera");
        g.IsExternalAllowedForSlug(null).Should().BeFalse();
        g.IsExternalAllowedForSlug("").Should().BeFalse();
    }

    [Fact]
    public void AllowlistVazia_NuncaLibera()
    {
        var g = Gate(AiMode.GeminiFreeDemo, "chave");

        g.ProviderConfigured.Should().BeTrue();
        g.IsExternalAllowedForSlug("qualquer").Should().BeFalse("allowlist vazia = nenhum tenant externo");
    }
}
