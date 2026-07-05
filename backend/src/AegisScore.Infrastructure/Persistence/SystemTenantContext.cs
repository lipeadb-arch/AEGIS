using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// Contexto de tenant privilegiado, não-HTTP. O stamping de escrita é fail-closed, então rotinas
/// de background/seed precisam de um tenant resolvido que NÃO venha de um header de request.
/// </summary>
public sealed class SystemTenantContext : ITenantContext
{
    public SystemTenantContext(Guid? tenantId) => TenantId = tenantId;
    public Guid? TenantId { get; }
}
