using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Infrastructure.Ai;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Estado tenant-scoped da IA para a interface: qual motor está efetivamente ativo PARA ESTE TENANT
/// (demonstrativo real, simulado, indisponível ou externo bloqueado) e o aviso do Free Tier. NUNCA expõe a
/// chave nem fragmento dela. Tenant IMPLÍCITO (claim do JWT via ITenantContext) — o slug decide o gate.
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

    /// <summary>Estado efetivo da IA para o tenant autenticado.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<AiStatusDto>> Status(CancellationToken ct)
    {
        var slug = await _resolver.GetCurrentSlugAsync(ct);
        var configured = _gate.ProviderConfigured;
        var externalAllowed = _gate.IsExternalAllowedForSlug(slug);
        var mode = _gate.Mode;

        // Estado EFETIVO para este tenant (o que a UI rotula):
        //  - DemoActive: motor demonstrativo real ativo (allowlist);
        //  - ExternalBlockedForTenant: provedor configurado, mas este tenant não está na allowlist → só stub;
        //  - Simulated: sem chave ou modo simulado → stub;
        //  - Unavailable: IA desligada por configuração.
        var state = mode switch
        {
            AiMode.Disabled => "Unavailable",
            AiMode.GeminiFreeDemo when configured && externalAllowed => "DemoActive",
            AiMode.GeminiFreeDemo when configured => "ExternalBlockedForTenant",
            _ => "Simulated",
        };

        var freeTier = mode == AiMode.GeminiFreeDemo;
        var notice = freeTier
            ? "Somente dados sintéticos ou demonstrativos. Não envie informações pessoais, confidenciais ou corporativas."
            : null;

        return Ok(new AiStatusDto(mode.ToString(), state, configured, externalAllowed, freeTier, notice));
    }
}

/// <summary>
/// Estado da IA para a UI. Nenhum campo carrega segredo. <c>EffectiveState</c> é o rótulo do tenant:
/// "DemoActive" | "ExternalBlockedForTenant" | "Simulated" | "Unavailable".
/// </summary>
public sealed record AiStatusDto(
    string Mode,
    string EffectiveState,
    bool ProviderConfigured,
    bool ExternalAllowedForTenant,
    bool FreeTier,
    string? LimitationNotice);
