using AegisScore.Application.Abstractions;

namespace AegisScore.Api;

/// <summary>Resolves the current tenant from the <c>X-Tenant</c> request header (or JWT claim).</summary>
public class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpTenantContext(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid? TenantId
    {
        get
        {
            var header = _accessor.HttpContext?.Request.Headers["X-Tenant"].FirstOrDefault();
            return Guid.TryParse(header, out var id) ? id : null;
        }
    }
}
