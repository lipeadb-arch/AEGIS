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
        // O ator vem da claim account_id (nunca do corpo): conceder acesso a uma identidade EXISTENTE edita/
        // reativa um membership e precisa das mesmas guardas administrativas (não pode virar um bypass).
        if (!TryGetAccountId(out var accountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _users.GrantAccessAsync(
            new GrantTenantAccessCommand(req.IdentityAccountId, req.DisplayName, req.Role, accountId), ct);

        return Respond(result);
    }

    /// <summary>
    /// Lista os acessos do tenant ambiente (implícito no JWT). Exige <c>TenantAdmin</c>: administrar acessos
    /// é papel do administrador do ambiente. A consulta respeita o Global Query Filter — nunca vaza
    /// membership de outro cliente. Dados SEGUROS (sem hash); o e-mail e o papel já vêm da autoridade.
    /// </summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantUserDto>>> List(CancellationToken ct)
    {
        var users = await _users.ListUsersAsync(ct);
        return Ok(users.Select(ToListDto).ToList());
    }

    /// <summary>
    /// Edita o nome e/ou o papel de um acesso do tenant ambiente. Alvo por rota (query filter → outro tenant
    /// é o MESMO 404 de inexistente). As guardas de auto-rebaixamento e de último administrador vivem no
    /// serviço; aqui só traduzimos o desfecho em HTTP.
    /// </summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpPut("{membershipId:guid}")]
    public async Task<ActionResult<UserDto>> Update(
        Guid membershipId, UpdateMembershipRequest req, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _users.UpdateMembershipAsync(
            new UpdateMembershipCommand(membershipId, accountId, req.DisplayName, req.Role), ct);
        return RespondAdmin(result);
    }

    /// <summary>
    /// Desativa (reversível — nunca exclusão física) um acesso do tenant ambiente. Barra auto-desativação e a
    /// remoção do último administrador ativo; revoga os refresh tokens do membership.
    /// </summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpPost("{membershipId:guid}/deactivate")]
    public async Task<ActionResult<UserDto>> Deactivate(Guid membershipId, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _users.SetMembershipStatusAsync(
            new SetMembershipStatusCommand(membershipId, accountId, Active: false), ct);
        return RespondAdmin(result);
    }

    /// <summary>Reativa um acesso do tenant ambiente. Idempotente; NÃO restaura sessões antigas (elas seguem revogadas).</summary>
    [Authorize(Roles = "TenantAdmin")]
    [HttpPost("{membershipId:guid}/reactivate")]
    public async Task<ActionResult<UserDto>> Reactivate(Guid membershipId, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var result = await _users.SetMembershipStatusAsync(
            new SetMembershipStatusCommand(membershipId, accountId, Active: true), ct);
        return RespondAdmin(result);
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

        // Conflitos de INVARIANTE quando a concessão editaria um membership existente (auto-rebaixamento /
        // último administrador): 409 com mensagem orientada à operação — as MESMAS guardas das rotas de edição.
        AccessGrantStatus.SelfDemotionForbidden or AccessGrantStatus.LastAdminProtected =>
            Conflict(new { title = result.Detail ?? "Operação não permitida.", status = 409 }),

        _ => BadRequest(result.Detail ?? "Requisição inválida."),
    };

    /// <summary>
    /// Traduz o desfecho da administração de membership em HTTP com códigos coerentes:
    /// 200 (editado) · 404 (inexistente/cross-tenant) · 400 (nome) · 403 (papel) · 409 (auto-lockout / último admin).
    /// Mensagens já sanitizadas vêm do serviço (dono da política).
    /// </summary>
    private ActionResult<UserDto> RespondAdmin(MembershipAdminResult result) => result.Status switch
    {
        MembershipAdminStatus.Updated => Ok(ToDto(result.User!)),

        MembershipAdminStatus.NotFound =>
            NotFound(new { title = "Usuário não encontrado neste ambiente.", status = 404 }),

        MembershipAdminStatus.InvalidDisplayName =>
            BadRequest(new { title = result.Detail ?? "Nome de exibição inválido.", status = 400 }),

        // Recusa de AUTORIZAÇÃO (papel não atribuível), não de formato — 403 conta essa história.
        MembershipAdminStatus.RoleNotAssignable => StatusCode(
            StatusCodes.Status403Forbidden, new { title = result.Detail ?? "Papel não atribuível.", status = 403 }),

        // Conflitos de INVARIANTE (auto-lockout / último administrador): 409 com mensagem orientada à operação.
        MembershipAdminStatus.SelfDeactivationForbidden or
        MembershipAdminStatus.SelfDemotionForbidden or
        MembershipAdminStatus.LastAdminProtected => Conflict(
            new { title = result.Detail ?? "Operação não permitida.", status = 409 }),

        _ => BadRequest(new { title = "Requisição inválida.", status = 400 }),
    };

    /// <summary>Lê a PESSOA autenticada da claim <c>account_id</c> — nunca de um id do corpo (que se forjaria).</summary>
    private bool TryGetAccountId(out Guid accountId) =>
        Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out accountId) && accountId != Guid.Empty;

    private static UserDto ToDto(UserSummary u) => new(
        u.Id, u.TenantId, u.Email, u.DisplayName, u.Role.ToString(),
        u.IsActive, u.CreatedAt, u.LastLoginAt);

    private static TenantUserDto ToListDto(TenantUserListItem u) => new(
        u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.IsActive,
        u.HasLocalCredential, u.CreatedAt, u.LastLoginAt);
}
