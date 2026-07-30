using System.Net.Mime;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api;

/// <summary>
/// [AEGIS-AUD-018] Autoridade CENTRAL fail-closed do <c>X-Tenant</c> para as rotas tenant-scoped. Roda
/// depois que o JWT foi validado e a autorização decidida. Para toda requisição autenticada FORA da família
/// de autenticação, o tenant é EXIGIDO explicitamente e validado:
///  (a) a claim <c>tenant_id</c> precisa existir e ser válida (nunca <c>Guid.Empty</c>) — o tenant vem
///      SEMPRE da claim assinada, jamais só de um header spoofável; e
///  (b) o header <c>X-Tenant</c> é OBRIGATÓRIO, GUID bem-formado e igual ao tenant do token.
///
/// Desfechos: claim ausente/inválida → 403; <c>X-Tenant</c> ausente/vazio → 400; malformado → 400;
/// divergente da claim → 403 (evento de segurança para o SOC); claim e header válidos e iguais → prossegue.
/// Assim o header nunca substitui, sobrepõe ou "adivinha" o tenant — e jamais se cai silenciosamente em
/// <c>Guid.Empty</c>, no primeiro tenant ou no tenant anterior. O SPA já envia o <c>X-Tenant</c> em toda
/// chamada autenticada (derivado do próprio token), então nenhuma metadata de rota é necessária.
///
/// A família <c>/api/v1/auth</c> (login/refresh/logout/troca federada/seleção/switch) atravessa SEM esta
/// verificação: ela se autentica por credencial própria (senha, cookie, token Entra ou ticket de seleção),
/// gerencia o próprio tenant (o switch/seleção justamente O TROCA) e não lê dados tenant-scoped pelo
/// <c>X-Tenant</c> — impor a claim aqui quebraria a troca federada, cujo principal Entra não tem
/// <c>tenant_id</c>. As demais rotas anônimas caem na FallbackPolicy (401) antes de qualquer dado.
/// </summary>
public sealed class TenantConsistencyMiddleware
{
    /// <summary>Família de gestão de sessão: autentica por credencial própria e gerencia o próprio tenant.</summary>
    private const string AuthPathPrefix = "/api/v1/auth";

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantConsistencyMiddleware> _logger;

    public TenantConsistencyMiddleware(RequestDelegate next, ILogger<TenantConsistencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;

        // A família de auth (inclui a troca federada, cujo principal não tem tenant_id, e o switch/seleção,
        // que TROCAM o tenant) não passa por aqui — ela não resolve dados pelo X-Tenant.
        var isAuthFamily = context.Request.Path.StartsWithSegments(
            AuthPathPrefix, StringComparison.OrdinalIgnoreCase);

        if (!isAuthFamily && user.Identity?.IsAuthenticated == true)
        {
            var tokenTenantRaw = user.FindFirst(JwtTokenService.TenantClaim)?.Value;

            // (a) Token autenticado (JWT local) sem tenant válido nunca deveria existir — barra fail-closed.
            if (!Guid.TryParse(tokenTenantRaw, out var tokenTenant) || tokenTenant == Guid.Empty)
            {
                _logger.LogWarning(
                    "SECURITY: token autenticado sem claim tenant_id válida. " +
                    "TraceId={TraceId} Method={Method} Path={Path} User={User}",
                    context.TraceIdentifier, context.Request.Method, context.Request.Path.Value, Subject(user));
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden, "Token sem tenant válido.");
                return;
            }

            // (b) Header X-Tenant é OBRIGATÓRIO na rota tenant-scoped. O SPA sempre o envia (derivado do
            //     próprio token); sua AUSÊNCIA denuncia um cliente que não declarou o tenant — fail-closed 400.
            var headerRaw = context.Request.Headers["X-Tenant"].FirstOrDefault();
            if (string.IsNullOrEmpty(headerRaw))
            {
                _logger.LogWarning(
                    "SECURITY: header X-Tenant ausente em rota tenant-scoped autenticada. " +
                    "TraceId={TraceId} Method={Method} Path={Path} User={User}",
                    context.TraceIdentifier, context.Request.Method, context.Request.Path.Value, Subject(user));
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Header X-Tenant obrigatório.");
                return;
            }

            // Malformado: fail-closed com 400 (um header quebrado denuncia cliente defeituoso/hostil).
            if (!Guid.TryParse(headerRaw, out var headerTenant))
            {
                _logger.LogWarning(
                    "SECURITY: header X-Tenant malformado rejeitado. " +
                    "TraceId={TraceId} Method={Method} Path={Path} User={User}",
                    context.TraceIdentifier, context.Request.Method, context.Request.Path.Value, Subject(user));
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Header X-Tenant inválido.");
                return;
            }

            // Divergente (inclui Guid.Empty): tentativa de acesso cross-tenant — 403 e evento de segurança.
            if (headerTenant != tokenTenant)
            {
                _logger.LogWarning(
                    "SECURITY: acesso cross-tenant rejeitado. TokenTenant={Token} HeaderTenant={Header} " +
                    "TraceId={TraceId} Method={Method} Path={Path} User={User}",
                    tokenTenant, headerTenant, context.TraceIdentifier,
                    context.Request.Method, context.Request.Path.Value, Subject(user));
                await WriteProblemAsync(
                    context, StatusCodes.Status403Forbidden, "Tenant do token diverge do tenant requisitado.");
                return;
            }
        }

        await _next(context);
    }

    private static string Subject(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? "(?)";

    /// <summary>Resposta consistente e SEM revelar existência de tenants — só o título genérico e o status.</summary>
    private static async Task WriteProblemAsync(HttpContext context, int status, string title)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsJsonAsync(new { title, status, traceId = context.TraceIdentifier });
    }
}
