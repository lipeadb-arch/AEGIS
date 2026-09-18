using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api;

/// <summary>
/// Resolve o tenant da requisição para o query filter/stamping do DbContext.
/// Ordem de precedência:
///  1. Claim <c>tenant_id</c> do JWT quando o usuário está autenticado — fonte assinada e à prova de
///     adulteração; o cliente não consegue forjá-la sem a chave.
///  2. Header <c>X-Tenant</c> como fallback para rotas públicas (login/refresh, onde o access token
///     está ausente ou expirado) e chamadas não autenticadas.
/// A divergência entre as duas fontes é barrada à parte pelo <see cref="TenantConsistencyMiddleware"/>.
/// </summary>
public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;
    private readonly TenantScopeOverride? _override;

    public HttpTenantContext(IHttpContextAccessor accessor, TenantScopeOverride? scopeOverride = null)
    {
        _accessor = accessor;
        _override = scopeOverride;
    }

    public Guid? TenantId
    {
        get
        {
            // [AEGIS-KNIGHT-MULTICLOUD-01] Escopo de worker com tenant fixado pelo servidor (nunca por dado de
            // requisição). Numa requisição HTTP o override não é definido e nada abaixo muda.
            if (_override?.TenantId is Guid fixado && _accessor.HttpContext is null) return fixado;

            var http = _accessor.HttpContext;
            if (http is null) return null;   // fora de uma requisição (startup/seed): sem tenant ambiente

            // 1) Preferência: tenant carimbado no token (confiável quando autenticado).
            if (http.User.Identity?.IsAuthenticated == true
                && Guid.TryParse(http.User.FindFirst(JwtTokenService.TenantClaim)?.Value, out var fromClaim))
                return fromClaim;

            // 2) Fallback: header X-Tenant (login/refresh e demais chamadas anônimas).
            var header = http.Request.Headers["X-Tenant"].FirstOrDefault();
            return Guid.TryParse(header, out var fromHeader) ? fromHeader : null;
        }
    }
}
