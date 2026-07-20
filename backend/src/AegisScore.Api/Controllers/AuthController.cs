using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Autenticação da Etapa 2: login por credenciais e rotação de refresh token (RTR).
/// O access token (JWT) volta no corpo JSON; o refresh token volta apenas num cookie
/// <c>HttpOnly</c> + <c>Secure</c> + <c>SameSite=Strict</c>, escopado ao path de auth, de modo que
/// o front nunca o manipula em JavaScript (mitiga XSS) e ele só acompanha as chamadas de auth.
/// Opera dentro do tenant ambiente (header <c>X-Tenant</c>), como o restante da API.
///
/// Única superfície anônima da API: login/refresh/logout formam a família de gestão de sessão e
/// se autenticam por credencial própria (senha ou cookie de refresh), não pelo Bearer — por isso
/// ficam fora da FallbackPolicy que exige autenticação em todo o resto.
/// </summary>
/// <remarks>
/// ⚠️ O <c>[AllowAnonymous]</c> saiu da CLASSE e passou a marcar ação a ação. Motivo: no ASP.NET Core
/// um <c>[AllowAnonymous]</c> de classe curto-circuita qualquer <c>[Authorize]</c> de método — com ele
/// no topo, a rota de troca de ambiente ficaria aberta mesmo anotada. Anonimato agora é declarado onde
/// de fato existe (login/refresh/logout, que se autenticam por credencial própria).
/// </remarks>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    /// <summary>Cookie do refresh token, escopado ao path de auth para reduzir sua superfície de envio.</summary>
    private const string RefreshCookie = "aegis_rt";

    /// <summary>Path do cookie: ele só acompanha as chamadas sob /api/v1/auth (refresh, logout).</summary>
    private const string CookiePath = "/api/v1/auth";

    private readonly IAuthService _auth;
    public AuthController(IAuthService auth) => _auth = auth;

    /// <summary>Valida credenciais e emite o par de tokens. 401 sem revelar se o e-mail existe.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]   // [Alto 4] freia brute force / credential stuffing
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var pair = await _auth.LoginAsync(req.Email, req.Password, ct);
        if (pair is null)
            return Unauthorized(new { title = "Credenciais inválidas.", status = 401 });

        SetRefreshCookie(pair.RefreshToken, pair.RefreshTokenExpiresAt);
        return new AuthResponse(pair.AccessToken, pair.AccessTokenExpiresAt);
    }

    /// <summary>Rotaciona o refresh token do cookie e devolve um novo access token.</summary>
    [AllowAnonymous]
    [HttpPost("refresh")]
    [EnableRateLimiting("auth-refresh")]   // [Alto 4] limita o replay em massa (DoS da cascata)
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
    {
        var current = Request.Cookies[RefreshCookie];
        var pair = await _auth.RefreshAsync(current ?? "", ct);
        if (pair is null)
        {
            // Token inválido, expirado ou breach detectado: limpa o cookie e força novo login.
            ClearRefreshCookie();
            return Unauthorized(new { title = "Sessão inválida ou expirada.", status = 401 });
        }

        SetRefreshCookie(pair.RefreshToken, pair.RefreshTokenExpiresAt);
        return new AuthResponse(pair.AccessToken, pair.AccessTokenExpiresAt);
    }

    /// <summary>Revoga o refresh token atual (se houver) e limpa o cookie. Idempotente.</summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var current = Request.Cookies[RefreshCookie];
        if (!string.IsNullOrEmpty(current))
            await _auth.LogoutAsync(current, ct);

        ClearRefreshCookie();
        return NoContent();
    }

    /// <summary>
    /// Troca o ambiente ativo (Tenant Switcher do HUD). Exige sessão válida: a pessoa é lida da claim
    /// <c>account_id</c> do JWT — NUNCA de um e-mail ou id vindo do corpo, que o cliente poderia forjar.
    /// O corpo só carrega o ALVO, e é o serviço que confirma o membership ativo lá.
    ///
    /// O refresh token do ambiente anterior é revogado no serviço e SUBSTITUÍDO no cookie: o cliente
    /// não fica com duas sessões vivas de tenants diferentes.
    /// </summary>
    [Authorize]
    [HttpPost("switch-tenant")]
    public async Task<ActionResult<AuthResponse>> SwitchTenant(
        SwitchTenantRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst(JwtTokenService.AccountClaim)?.Value, out var accountId))
            return Unauthorized(new { title = "Token sem conta de identidade.", status = 401 });

        var pair = await _auth.SwitchTenantAsync(
            accountId, req.TargetTenantId, Request.Cookies[RefreshCookie], ct);

        // 403 e não 404: o alvo pode existir e simplesmente não ser seu. Não distinguimos os dois casos
        // para não transformar a rota num oráculo de existência de tenants.
        if (pair is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { title = "Sem acesso ativo ao ambiente solicitado.", status = 403 });

        SetRefreshCookie(pair.RefreshToken, pair.RefreshTokenExpiresAt);
        return new AuthResponse(pair.AccessToken, pair.AccessTokenExpiresAt);
    }

    private void SetRefreshCookie(string token, DateTimeOffset expires) =>
        Response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true,                      // invisível ao JavaScript (mitiga XSS)
            Secure = true,                        // só trafega por HTTPS (localhost é exceção do browser)
            SameSite = SameSiteMode.Strict,       // não acompanha requisições cross-site (mitiga CSRF)
            Expires = expires,                    // expira junto com o refresh token
            Path = CookiePath,
            IsEssential = true,                   // cookie funcional: independe de consentimento de cookies
        });

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
        });
}
