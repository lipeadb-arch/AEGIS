using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Auth;
using AegisScore.Api.Contracts;
using AegisScore.Application.Services;

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

    public PlatformIdentitiesController(IIdentityProvisioningService identities) => _identities = identities;

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

    private static PlatformIdentityDto ToDto(IdentitySummary i) =>
        new(i.Id, i.Email, i.HasLocalCredential, i.CreatedAt);
}
