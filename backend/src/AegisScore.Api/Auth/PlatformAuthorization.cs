using Microsoft.AspNetCore.Authorization;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Auth;

/// <summary>
/// [AEGIS-AUD-011] Autorização de PLATAFORMA (autoridade global), inequivocamente separada da autorização
/// tenant-scoped (papéis via claim <c>role</c> + <c>[Authorize(Roles=...)]</c>). A policy exige a claim
/// GLOBAL <c>platform_role = PlatformAdmin</c> — emitida a partir de <see cref="IdentityAccount.PlatformRole"/>,
/// NUNCA de <see cref="User.Role"/>. Consequências desejadas:
/// <list type="bullet">
/// <item>um <c>TenantAdmin</c> (mesmo admin do próprio tenant) NÃO alcança rotas de plataforma;</item>
/// <item>um <c>PlatformAdmin</c> global as alcança mesmo sendo <c>Analyst</c> no tenant ativo;</item>
/// <item>trocar de tenant reemite <c>role</c>, mas não afeta esta autoridade global.</item>
/// </list>
/// </summary>
public static class PlatformAuthorization
{
    /// <summary>Nome da policy de administração de plataforma.</summary>
    public const string PolicyName = "PlatformAdmin";

    /// <summary>
    /// Registra a policy no contêiner de autorização. Centralizado de propósito: Program.cs e os testes de
    /// policy/claims exercitam EXATAMENTE esta definição, sem risco de divergência.
    /// </summary>
    public static void AddPlatformPolicy(AuthorizationOptions options) =>
        options.AddPolicy(PolicyName, p => p
            .RequireAuthenticatedUser()
            .RequireClaim(JwtTokenService.PlatformRoleClaim, nameof(PlatformRole.PlatformAdmin)));
}
