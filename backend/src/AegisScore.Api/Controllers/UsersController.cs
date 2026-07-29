using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Concessão de acesso a tenant (memberships) e leitura dos ambientes da pessoa autenticada.
///
/// ⚠️ Controller SEPARADO do <see cref="AuthController"/> de propósito. Aquele é a única superfície
/// ANÔNIMA da API (login/refresh/logout se autenticam por credencial própria, não por Bearer);
/// pendurar concessão de acesso lá colocaria uma rota privilegiada dentro de um controller marcado
/// <c>[AllowAnonymous]</c> — um deslize de atributo viraria escrita sem autenticação.
///
/// [AEGIS-AUD-010] Esta superfície NÃO cria identidade global nem toca credencial: a criação da
/// <c>IdentityAccount</c> é autoridade de PLATAFORMA (ver <see cref="PlatformIdentitiesController"/>). Aqui
/// só se concede acesso ao tenant AMBIENTE a uma identidade que JÁ existe, exigindo <c>TenantAdmin</c>. O
/// tenant NUNCA vem do corpo nem da rota: é o do claim <c>tenant_id</c> do JWT, e o <c>StampTenant</c> do
/// DbContext revalida na gravação (fail-closed).
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly IUserManagementService _users;
    private readonly IAuthService _auth;

    public UsersController(IUserManagementService users, IAuthService auth)
    {
        _users = users;
        _auth = auth;
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
    /// [AEGIS-AUD-010] Concede/atualiza o acesso de uma identidade global a ESTE tenant (idempotente): cria
    /// o membership se ausente, ou aplica papel/nome e reativa se já existir. A identidade deve preexistir —
    /// um <c>IdentityAccountId</c> inexistente devolve 404 genérico, sem criar nada.
    ///
    /// O tenant de destino é sempre o ambiente (Um-para-Muitos, sem "mover" alguém entre tenants). Nem
    /// e-mail, nem senha, nem TenantId trafegam: a descoberta é por <c>IdentityAccountId</c> e o tenant vem
    /// do JWT. Escrita exige <c>TenantAdmin</c>.
    /// </summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpPost("access")]
    public async Task<ActionResult<UserDto>> GrantAccess(
        AssignUserAccessRequest req, CancellationToken ct)
    {
        var result = await _users.GrantAccessAsync(
            new GrantTenantAccessCommand(req.IdentityAccountId, req.DisplayName, req.Role), ct);

        return Respond(result);
    }

    /// <summary>Traduz o desfecho do serviço em HTTP. A cópia de validação vem do serviço (dono da política).</summary>
    private ActionResult<UserDto> Respond(AccessGrantResult result) => result.Status switch
    {
        AccessGrantStatus.Granted =>
            // Sem Location: membership ainda não tem GET canônico, e apontar de volta para este POST
            // seria uma URL mentirosa (mesma decisão da §20.5).
            StatusCode(StatusCodes.Status201Created, ToDto(result.User!)),

        AccessGrantStatus.AccessUpdated => Ok(ToDto(result.User!)),

        // Identidade global inexistente: resposta GENÉRICA (404), sem revelar mais nem criar nada.
        AccessGrantStatus.IdentityNotFound =>
            NotFound(new { title = "Identidade não encontrada.", status = 404 }),

        // Recusa de AUTORIZAÇÃO, não de formato: o pedido é sintaticamente válido e foi negado por
        // política de privilégio. 403 conta essa história; 400 a esconderia como erro de digitação.
        AccessGrantStatus.RoleNotAssignable => Forbid(),

        _ => BadRequest(result.Detail ?? "Requisição inválida."),
    };

    private static UserDto ToDto(UserSummary u) => new(
        u.Id, u.TenantId, u.Email, u.DisplayName, u.Role.ToString(),
        u.IsActive, u.CreatedAt, u.LastLoginAt);
}
