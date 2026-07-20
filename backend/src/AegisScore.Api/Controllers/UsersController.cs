using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Provisionamento de identidades do tenant.
///
/// ⚠️ Controller SEPARADO do <see cref="AuthController"/> de propósito. Aquele é a única superfície
/// ANÔNIMA da API (login/refresh/logout se autenticam por credencial própria, não por Bearer);
/// pendurar criação de usuário lá colocaria uma rota privilegiada dentro de um controller marcado
/// <c>[AllowAnonymous]</c> — um deslize de atributo viraria criação de conta sem autenticação.
///
/// O tenant NUNCA vem do corpo nem da rota: é o do claim <c>tenant_id</c> do JWT, e o
/// <c>StampTenant</c> do DbContext revalida na gravação (fail-closed). Escritas de identidade exigem
/// <c>TenantAdmin</c> — o mesmo papel que já governa as demais escritas de configuração do tenant.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly IUserManagementService _users;
    private readonly IAuthService _auth;
    private readonly ITenantContext _tenant;

    public UsersController(IUserManagementService users, IAuthService auth, ITenantContext tenant)
    {
        _users = users;
        _auth = auth;
        _tenant = tenant;
    }

    /// <summary>
    /// Ambientes que a PESSOA autenticada pode assumir — alimenta o seletor do HUD. Exige apenas sessão
    /// válida (não TenantAdmin): todo analista precisa enxergar os próprios acessos.
    ///
    /// A lista é derivada da claim <c>account_id</c>, então ninguém consulta os acessos de outra pessoa,
    /// e só entram memberships ATIVOS de tenants não suspensos.
    /// </summary>
    [HttpGet("me/tenants")]
    public async Task<ActionResult<IReadOnlyList<TenantOptionDto>>> MyTenants(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var accountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var tenants = await _auth.GetAccessibleTenantsAsync(accountId, ct);
        return Ok(tenants
            .Select(t => new TenantOptionDto(t.Id, t.Name, t.Slug, t.Role.ToString()))
            .ToList());
    }

    /// <summary>
    /// Tenant ambiente resolvido pelo JWT. Garantido não-nulo numa rota autenticada — o
    /// <c>TenantConsistencyMiddleware</c> barra (403) qualquer token sem claim <c>tenant_id</c> válida.
    /// </summary>
    private Guid CurrentTenantId => _tenant.TenantId
        ?? throw new InvalidOperationException("Rota autenticada sem tenant no contexto.");

    /// <summary>Cria uma identidade neste tenant. 409 se o e-mail já for usado AQUI.</summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest req, CancellationToken ct)
    {
        var result = await _users.CreateUserAsync(
            new CreateUserCommand(req.Email, req.DisplayName, req.Password, req.Role), ct);

        return Respond(result);
    }

    /// <summary>
    /// Concede/atualiza o acesso de um e-mail a ESTE tenant (idempotente): cria a identidade se ausente
    /// (exige senha inicial), ou aplica o papel e reativa se já existir.
    ///
    /// O tenant de destino é sempre o ambiente — o modelo é Um-para-Muitos, então não existe "mover"
    /// alguém para outro tenant. Passamos <see cref="CurrentTenantId"/> ao comando como ASSERÇÃO: o
    /// serviço recusa se divergir do contexto.
    /// </summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpPost("access")]
    public async Task<ActionResult<UserDto>> AssignAccess(
        AssignUserAccessRequest req, CancellationToken ct)
    {
        var result = await _users.AssignUserToTenantAsync(
            new AssignUserToTenantCommand(CurrentTenantId, req.Email, req.Role, req.InitialPassword), ct);

        return Respond(result);
    }

    /// <summary>Traduz o desfecho do serviço em HTTP. A cópia de validação vem do serviço (dono da política).</summary>
    private ActionResult<UserDto> Respond(UserProvisioningResult result) => result.Status switch
    {
        UserProvisioningStatus.Created =>
            // Sem Location: identidade ainda não tem GET canônico, e apontar de volta para este POST
            // seria uma URL mentirosa (mesma decisão da §20.5).
            StatusCode(StatusCodes.Status201Created, ToDto(result.User!)),

        UserProvisioningStatus.AccessUpdated => Ok(ToDto(result.User!)),

        UserProvisioningStatus.EmailAlreadyInUse =>
            Conflict("Já existe uma identidade com este e-mail neste cliente."),

        // Recusa de AUTORIZAÇÃO, não de formato: o pedido é sintaticamente válido e foi negado por
        // política de privilégio. 403 conta essa história; 400 a esconderia como erro de digitação.
        UserProvisioningStatus.RoleNotAssignable => Forbid(),

        _ => BadRequest(result.Detail ?? "Requisição inválida."),
    };

    private static UserDto ToDto(UserSummary u) => new(
        u.Id, u.TenantId, u.Email, u.DisplayName, u.Role.ToString(),
        u.IsActive, u.CreatedAt, u.LastLoginAt);
}
