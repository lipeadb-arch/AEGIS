using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Infrastructure.Ai;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Estado tenant-scoped da IA para a interface. É um retrato de configuração, não um health check em tempo real,
/// e nunca expõe a chave nem fragmentos dela.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/ai")]
public sealed class AiStatusController : ControllerBase
{
    private readonly IAiFreeTierGate _gate;
    private readonly IAiTenantResolver _resolver;

    public AiStatusController(IAiFreeTierGate gate, IAiTenantResolver resolver)
    {
        _gate = gate;
        _resolver = resolver;
    }

    [HttpGet("status")]
    public async Task<ActionResult<AiStatusDto>> Status(CancellationToken ct)
    {
        var slug = await _resolver.GetCurrentSlugAsync(ct);
        var configured = _gate.ProviderConfigured;
        var externalAllowed = _gate.IsExternalAllowedForSlug(slug);
        var mode = _gate.Mode;

        var state = mode switch
        {
            AiMode.Disabled => "Unavailable",
            AiMode.ExternalEnterprise when configured && externalAllowed => "EnterpriseConfigured",
            AiMode.ExternalEnterprise when configured => "ExternalBlockedForTenant",
            AiMode.ExternalDemo when configured && externalAllowed => "DemoConfigured",
            AiMode.ExternalDemo when configured => "ExternalBlockedForTenant",
            _ => "Simulated",
        };

        var freeTier = mode == AiMode.ExternalDemo;
        var notice = mode switch
        {
            AiMode.ExternalDemo =>
                "Somente dados sintéticos ou demonstrativos. Não envie informações pessoais, confidenciais ou corporativas.",
            AiMode.ExternalEnterprise =>
                "Uso corporativo habilitado para este tenant. Respeite as políticas internas de classificação, necessidade e minimização de dados.",
            _ => null,
        };

        return Ok(new AiStatusDto(mode.ToString(), state, configured, externalAllowed, freeTier, notice));
    }
}

/// <summary>
/// Estado da IA para a UI — retrato de configuração, não health check em tempo real.
/// <c>EffectiveState</c>: EnterpriseConfigured | DemoConfigured | ExternalBlockedForTenant | Simulated | Unavailable.
/// </summary>
public sealed record AiStatusDto(
    string Mode,
    string EffectiveState,
    bool ProviderConfigured,
    bool ExternalAllowedForTenant,
    bool FreeTier,
    string? LimitationNotice);
