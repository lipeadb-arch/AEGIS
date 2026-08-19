using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Api.Auth;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// [AEGIS-AUD-010] Superfície de PLATAFORMA para o provisionamento GLOBAL de identidade — a criação da
/// <c>IdentityAccount</c> (a PESSOA, dona da credencial). [AEGIS-AUD-011] Protegida pela POLICY global
/// <see cref="PlatformAuthorization.PolicyName"/> (claim <c>platform_role = PlatformAdmin</c>), não por papel
/// de tenant: criar uma entidade global é autoridade de PLATAFORMA, que nenhum <c>TenantAdmin</c> possui. É a contrapartida do <see cref="UsersController"/>, que concede acesso
/// a tenant a uma identidade JÁ provisionada.
///
/// Aqui NÃO se concede acesso a nenhum tenant: não há TenantId, membership, papel nem lista de tenants. Só
/// o e-mail global e, conforme o modo de autenticação, uma senha local opcional. A resposta jamais ecoa o
/// hash de senha.
/// </summary>
[ApiController]
[Route("api/v1/platform/identities")]
[Authorize(Policy = PlatformAuthorization.PolicyName)]   // [AEGIS-AUD-011] autoridade GLOBAL, não papel de tenant
public sealed class PlatformIdentitiesController : ControllerBase
{
    private readonly IIdentityProvisioningService _identities;
    private readonly IAuthService _auth;

    public PlatformIdentitiesController(IIdentityProvisioningService identities, IAuthService auth)
    {
        _identities = identities;
        _auth = auth;
    }

    /// <summary>
    /// Provisiona uma identidade global. 201 na criação; 409 se o e-mail já existir GLOBALMENTE; 400 para
    /// e-mail inválido ou violação da política de senha por modo (senha exigida em Local, recusada em
    /// Federated, opcional em Hybrid).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PlatformIdentityDto>> Provision(
        ProvisionIdentityRequest req, CancellationToken ct)
    {
        var result = await _identities.ProvisionAsync(
            new ProvisionIdentityCommand(req.Email, req.Password), ct);

        return result.Status switch
        {
            IdentityProvisioningStatus.Created =>
                // Sem Location: a identidade ainda não tem GET canônico, e apontar de volta para este POST
                // seria uma URL mentirosa (mesma decisão do onboarding e do UsersController).
                StatusCode(StatusCodes.Status201Created, ToDto(result.Identity!)),

            IdentityProvisioningStatus.EmailAlreadyInUse =>
                Conflict("Já existe uma identidade global com este e-mail."),

            _ => BadRequest(result.Detail ?? "Requisição inválida."),
        };
    }

    /// <summary>
    /// Redefinição ADMINISTRATIVA da senha local de uma identidade existente — recuperação legítima quando a
    /// pessoa não consegue mais autenticar (sem e-mail/SMTP nesta entrega). Autoridade EXCLUSIVA de plataforma
    /// (a policy de classe <see cref="PlatformAuthorization.PolicyName"/> já barra qualquer <c>TenantAdmin</c>);
    /// permitir que um papel de tenant redefinisse a credencial GLOBAL seria takeover cross-tenant. O alvo é a
    /// <see cref="IdentityAccount"/> (nunca um membership) — o princípio da identidade global é preservado.
    ///
    /// Desfechos: 204 (redefinida — hash substituído e sessões revogadas em todos os tenants) · 404 (identidade
    /// inexistente, genérico) · 409 (conta federated-only, sem credencial local a redefinir; OU auto-redefinição —
    /// o próprio administrador deve usar a troca normal, que exige a senha atual) · 400 (senha fora da política).
    /// O ator vem da claim <c>account_id</c> do JWT, NUNCA do corpo. A senha/hash nunca é registrada nem devolvida.
    /// </summary>
    [HttpPost("{accountId:guid}/password")]
    [EnableRateLimiting("platform-password-reset")]   // mutação de credencial: nunca ilimitada
    public async Task<IActionResult> ResetPassword(
        Guid accountId, AdminResetPasswordRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var actorAccountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _auth.AdminResetPasswordAsync(actorAccountId, accountId, req.NewPassword, ct);

        return result.Status switch
        {
            AdminPasswordResetStatus.Reset => NoContent(),

            // Identidade global inexistente: resposta GENÉRICA, sem revelar se o e-mail existe.
            AdminPasswordResetStatus.NotFound =>
                NotFound(new { title = "Identidade não encontrada.", status = 404 }),

            // Federated-only e auto-redefinição são ambos 409 (conflito de estado), com a mensagem do serviço.
            AdminPasswordResetStatus.NoLocalCredential or AdminPasswordResetStatus.SelfResetForbidden =>
                Conflict(new { title = result.Detail ?? "Operação não permitida.", status = 409 }),

            // Senha fora da política (autoridade PasswordPolicy): 400, nada foi alterado.
            _ => BadRequest(new { title = result.Detail ?? "Não foi possível redefinir a senha.", status = 400 }),
        };
    }

    private static PlatformIdentityDto ToDto(IdentitySummary i) =>
        new(i.Id, i.Email, i.HasLocalCredential, i.CreatedAt);
}
