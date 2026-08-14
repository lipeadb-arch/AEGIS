using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Auth;
using AegisScore.Api.Contracts;
using AegisScore.Application.Services;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Onboarding de usuário no tenant ambiente — o administrador inicial adiciona o gestor e os demais. Esta é
/// a ÚNICA superfície que pode CRIAR uma identidade global E conceder acesso na mesma operação, então exige
/// SIMULTANEAMENTE as duas autoridades:
/// <list type="bullet">
/// <item>a POLICY global <see cref="PlatformAuthorization.PolicyName"/> (claim <c>platform_role=PlatformAdmin</c>) —
/// criar identidade global é autoridade de PLATAFORMA; e</item>
/// <item>o papel tenant-scoped <c>TenantAdmin</c> no ambiente atual — conceder acesso é ato do administrador do tenant.</item>
/// </list>
/// Os dois <c>[Authorize]</c> se combinam com E lógico: um <c>TenantAdmin</c> SEM autoridade global é recusado
/// (403) — ele administra apenas os usuários já vinculados (<see cref="UsersController"/>), sem criar identidades.
/// O tenant é SEMPRE o ambiente (claim do JWT), nunca do corpo/rota.
/// </summary>
[ApiController]
[Route("api/v1/platform/tenant-users")]
[Authorize(Policy = PlatformAuthorization.PolicyName)]
[Authorize(Roles = "TenantAdmin")]
public sealed class PlatformTenantUsersController : ControllerBase
{
    private readonly IPlatformTenantUserService _onboarding;

    public PlatformTenantUsersController(IPlatformTenantUserService onboarding) => _onboarding = onboarding;

    /// <summary>
    /// Cadastra (ou concede acesso a) um usuário no tenant atual. 201 quando cria a identidade ou concede um
    /// acesso novo; 200 quando só atualiza um acesso já existente. 400 (e-mail/nome/senha), 403 (papel não
    /// atribuível). ⚠️ Se a pessoa já existe, a senha informada é IGNORADA — <c>IdentityExisted</c> deixa isso
    /// explícito para a UI comunicar que a credencial existente foi preservada.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<OnboardTenantUserResponse>> Onboard(
        OnboardTenantUserRequest req, CancellationToken ct)
    {
        var result = await _onboarding.OnboardAsync(
            new OnboardTenantUserCommand(req.Email, req.DisplayName, req.Role, req.InitialPassword), ct);

        return result.Status switch
        {
            TenantUserOnboardingStatus.IdentityCreatedAndGranted =>
                StatusCode(StatusCodes.Status201Created, ToResponse(result, "identity_created")),

            TenantUserOnboardingStatus.ExistingIdentityGranted =>
                StatusCode(StatusCodes.Status201Created, ToResponse(result, "access_granted")),

            TenantUserOnboardingStatus.ExistingIdentityAccessUpdated =>
                Ok(ToResponse(result, "access_updated")),

            // Recusa de AUTORIZAÇÃO (papel não atribuível), não de formato — 403.
            TenantUserOnboardingStatus.RoleNotAssignable => StatusCode(
                StatusCodes.Status403Forbidden,
                new { title = result.Detail ?? "Papel não atribuível.", status = 403 }),

            // Erros de contrato/política de senha: 400 com a mensagem do serviço (nunca ecoa a senha).
            _ => BadRequest(new { title = result.Detail ?? "Requisição inválida.", status = 400 }),
        };
    }

    private static OnboardTenantUserResponse ToResponse(TenantUserOnboardingResult result, string outcome)
    {
        var u = result.User!;
        return new OnboardTenantUserResponse(
            new TenantUserDto(
                u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.IsActive,
                u.HasLocalCredential, u.CreatedAt, u.LastLoginAt),
            outcome,
            result.IdentityExisted);
    }
}
