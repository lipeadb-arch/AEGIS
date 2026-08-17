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

/// <summary>
/// Endpoint de status da IA: prova o rótulo EFETIVO por tenant (demonstrativo/simulado/bloqueado) e a
/// invariante de segurança — NENHUM campo carrega a chave ou fragmento dela.
/// </summary>
public sealed class AiStatusControllerTests
{
    [Fact]
    public async Task Status_TenantAllowlisted_DemoActive_SemVazarChave()
    {
        var ctrl = Controller(mode: AiMode.GeminiFreeDemo, apiKey: "chave-super-secreta", slug: "sandbox", allow: "sandbox");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("DemoActive");
        dto.ProviderConfigured.Should().BeTrue();
        dto.ExternalAllowedForTenant.Should().BeTrue();
        dto.FreeTier.Should().BeTrue();
        dto.LimitationNotice.Should().Contain("sintéticos");
        JsonSerializer.Serialize(dto).Should().NotContain("chave-super-secreta", "o status JAMAIS expõe a chave");
    }

    [Fact]
    public async Task Status_TenantForaDaAllowlist_ExternalBlocked()
    {
        var ctrl = Controller(mode: AiMode.GeminiFreeDemo, apiKey: "k", slug: "tenant-corporativo", allow: "sandbox");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("ExternalBlockedForTenant");
        dto.ExternalAllowedForTenant.Should().BeFalse();
        dto.FreeTier.Should().BeTrue("o aviso do Free Tier continua valendo para o operador");
    }

    [Fact]
    public async Task Status_ModoSimulado_Simulated_SemAviso()
    {
        var ctrl = Controller(mode: AiMode.Simulated, apiKey: "", slug: "qualquer", allow: "sandbox");

        var dto = await Read(ctrl);

        dto.EffectiveState.Should().Be("Simulated");
        dto.FreeTier.Should().BeFalse();
        dto.LimitationNotice.Should().BeNull();
    }

    // ---- helpers ------------------------------------------------------------------

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
