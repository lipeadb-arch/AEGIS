using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Resolve o SLUG do tenant vigente para o gate do Free Tier. Em requisições HTTP usa o
/// <see cref="ITenantContext"/> (claim do JWT). Em rotinas de background (o worker documental) que constroem
/// o próprio contexto de tenant, chame <see cref="OverrideTenant"/> com o tenant do lease ANTES de usar a IA:
/// o slug é lido da tabela <c>Tenants</c> (que NÃO tem query filter). O resultado é cacheado por escopo — é
/// uma leitura por chave primária, barata e idempotente.
/// </summary>
public interface IAiTenantResolver
{
    /// <summary>Fixa o tenant a resolver (rotinas de background sem contexto HTTP). Reseta o cache.</summary>
    void OverrideTenant(Guid tenantId);

    /// <summary>Slug do tenant vigente (override, senão o do <see cref="ITenantContext"/>); null sem tenant.</summary>
    Task<string?> GetCurrentSlugAsync(CancellationToken ct = default);
}

/// <summary>Implementação scoped: um cache de slug por requisição/escopo de worker.</summary>
public sealed class AiTenantResolver : IAiTenantResolver
{
    private readonly ITenantContext _tenant;
    private readonly AegisScoreDbContext _db;
    private Guid? _override;
    private string? _slug;
    private bool _resolved;

    public AiTenantResolver(ITenantContext tenant, AegisScoreDbContext db)
    {
        _tenant = tenant;
        _db = db;
    }

    public void OverrideTenant(Guid tenantId)
    {
        _override = tenantId;
        _resolved = false;
        _slug = null;
    }

    public async Task<string?> GetCurrentSlugAsync(CancellationToken ct = default)
    {
        if (_resolved) return _slug;

        var id = _override ?? _tenant.TenantId;
        _slug = id is Guid tid
            ? await _db.Tenants.AsNoTracking().Where(t => t.Id == tid).Select(t => t.Slug).FirstOrDefaultAsync(ct)
            : null;
        _resolved = true;
        return _slug;
    }
}
