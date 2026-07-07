using System.Net.Mime;
using AegisScore.Infrastructure.Auth;

namespace AegisScore.Api;

/// <summary>
/// Defesa em profundidade da isolação por tenant no pipeline autenticado. Roda depois que o JWT foi
/// validado e garante que:
///  (a) todo token autenticado carrega uma claim <c>tenant_id</c> válida; e
///  (b) se o cliente também enviou o header <c>X-Tenant</c>, ele NÃO diverge do tenant do token.
/// Qualquer divergência é uma tentativa de acesso cross-tenant: rejeitada com HTTP 403 e registrada
/// como evento de segurança (para monitoramento ativo do SOC). Requisições anônimas (login/refresh)
/// atravessam sem verificação — o tenant delas vem do header e é isolado pelo query filter.
/// </summary>
public sealed class TenantConsistencyMiddleware
{
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
        if (user.Identity?.IsAuthenticated == true)
        {
            var tokenTenantRaw = user.FindFirst(JwtTokenService.TenantClaim)?.Value;

            // (a) Token autenticado sem tenant válido nunca deveria existir — barra fail-closed.
            if (!Guid.TryParse(tokenTenantRaw, out var tokenTenant) || tokenTenant == Guid.Empty)
            {
                _logger.LogWarning(
                    "SECURITY: token autenticado sem claim tenant_id válida. " +
                    "TraceId={TraceId} Method={Method} Path={Path} User={User}",
                    context.TraceIdentifier, context.Request.Method, context.Request.Path.Value, Subject(user));
                await WriteForbiddenAsync(context, "Token sem tenant válido.");
                return;
            }

            // (b) Header X-Tenant (se presente) não pode pedir um tenant diferente do token.
            var headerRaw = context.Request.Headers["X-Tenant"].FirstOrDefault();
            if (!string.IsNullOrEmpty(headerRaw)
                && Guid.TryParse(headerRaw, out var headerTenant)
                && headerTenant != tokenTenant)
            {
                _logger.LogWarning(
                    "SECURITY: acesso cross-tenant rejeitado. TokenTenant={Token} HeaderTenant={Header} " +
                    "TraceId={TraceId} Method={Method} Path={Path} User={User}",
                    tokenTenant, headerTenant, context.TraceIdentifier,
                    context.Request.Method, context.Request.Path.Value, Subject(user));
                await WriteForbiddenAsync(context, "Tenant do token diverge do tenant requisitado.");
                return;
            }
        }

        await _next(context);
    }

    private static string Subject(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? "(?)";

    private static async Task WriteForbiddenAsync(HttpContext context, string title)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsJsonAsync(new { title, status = 403, traceId = context.TraceIdentifier });
    }
}
