using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>Endpoint de status tenant-scoped, sem exposição de segredo.</summary>
public sealed class AiStatusControllerTests
{
    [Fact]
    public async Task Status_TenantAllowlisted_DemoConfigured_ComAvisoSintetico()
    {
        var ctrl = Controller(AiMode.ExternalDemo, "chave-super-secreta", "sandbox", "sandbox");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("DemoConfigured");
        dto.ProviderConfigured.Should().BeTrue();
        dto.ExternalAllowedForTenant.Should().BeTrue();
        dto.FreeTier.Should().BeTrue();
        dto.LimitationNotice.Should().Contain("sintéticos");
        JsonSerializer.Serialize(dto).Should().NotContain("chave-super-secreta");
    }

    [Fact]
    public async Task Status_TenantAllowlisted_EnterpriseConfigured_SemRotuloDemo()
    {
        var ctrl = Controller(AiMode.ExternalEnterprise, "chave-super-secreta", "corp", "corp");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("EnterpriseConfigured");
        dto.ProviderConfigured.Should().BeTrue();
        dto.ExternalAllowedForTenant.Should().BeTrue();
        dto.FreeTier.Should().BeFalse();
        dto.LimitationNotice.Should().Contain("Uso corporativo habilitado");
        dto.LimitationNotice.Should().Contain("minimização");
        dto.LimitationNotice.Should().NotContain("Somente dados sintéticos");
        JsonSerializer.Serialize(dto).Should().NotContain("chave-super-secreta");
    }

    [Theory]
    [InlineData(AiMode.ExternalDemo)]
    [InlineData(AiMode.ExternalEnterprise)]
    public async Task Status_TenantForaDaAllowlist_ExternalBlocked(AiMode mode)
    {
        var ctrl = Controller(mode, "k", "tenant-nao-autorizado", "tenant-autorizado");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("ExternalBlockedForTenant");
        dto.ExternalAllowedForTenant.Should().BeFalse();
    }

    [Fact]
    public async Task Status_ModoSimulado_Simulated_SemAviso()
    {
        var ctrl = Controller(AiMode.Simulated, "", "qualquer", "sandbox");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("Simulated");
        dto.FreeTier.Should().BeFalse();
        dto.LimitationNotice.Should().BeNull();
    }

    private static AiStatusController Controller(AiMode mode, string apiKey, string slug, string allow)
    {
        var gate = new AiFreeTierGate(Options.Create(new AiOptions
        {
            Mode = mode,
            ApiKey = apiKey,
            FreeTier = new AiFreeTierOptions { AllowedTenantSlugs = { allow } },
        }));
        return new AiStatusController(gate, new FakeResolver(slug));
    }

    private static async Task<AiStatusDto> Read(AiStatusController ctrl)
    {
        var result = await ctrl.Status(CancellationToken.None);
        return (AiStatusDto)((OkObjectResult)result.Result!).Value!;
    }

    private sealed class FakeResolver : IAiTenantResolver
    {
        private readonly string? _slug;
        public FakeResolver(string? slug) => _slug = slug;
        public void OverrideTenant(System.Guid tenantId) { }
        public Task<string?> GetCurrentSlugAsync(CancellationToken ct = default) => Task.FromResult(_slug);
    }
}
