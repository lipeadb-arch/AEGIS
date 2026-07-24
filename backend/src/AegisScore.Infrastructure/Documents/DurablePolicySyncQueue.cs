using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// [AEGIS-AUD-050] Fila operacional DURÁVEL de sincronização de políticas, apoiada no PostgreSQL. Substitui
/// o antigo gatilho em memória (sem durabilidade). O trabalho é uma <see cref="PolicySyncRequest"/>
/// persistida: o endpoint <c>/governance/documents/sync</c> a grava ANTES do 202, e o ciclo periódico apenas
/// a enfileira — o timer nunca é o transporte.
///
/// <see cref="EnqueueAsync"/> é idempotente por tenant (um único pedido ATIVO, invariante de índice único
/// parcial). A aquisição e as transições seguem o mesmo desenho do
/// <see cref="DurableDocumentAnalysisQueue"/>: claim atômico cross-tenant sob contexto de sistema, transições
/// (incl. batimento de lease) guardadas pelo lease.
/// </summary>
public sealed class DurablePolicySyncQueue : IPolicySyncQueue
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly PolicySyncQueueOptions _options;
    private readonly ILogger<DurablePolicySyncQueue> _log;

    public DurablePolicySyncQueue(
        IServiceScopeFactory scopes, TimeProvider clock,
        IOptions<PolicySyncQueueOptions> options, ILogger<DurablePolicySyncQueue> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
        _options = options.Value;
        if (!_options.TryValidate(out var error))
            throw new InvalidOperationException(error);
    }

    private const string ClaimSql = """
        UPDATE "PolicySyncRequests"
        SET "Status" = @processing,
            "LeaseId" = @leaseId,
            "LeaseExpiresAt" = @leaseExpires,
            "Attempts" = "Attempts" + 1,
            "UpdatedAt" = @now
        WHERE "Id" = (
            SELECT "Id" FROM "PolicySyncRequests"
            WHERE "AvailableAt" <= @now
              AND (
                  "Status" = @pending
                  OR ("Status" = @processing AND "LeaseExpiresAt" <= @now)
              )
            ORDER BY "AvailableAt", "CreatedAt"
            {LOCK}
            LIMIT 1
        )
        RETURNING "Id", "TenantId", "Attempts";
        """;

    public async Task EnqueueAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        // SystemTenantContext(tenantId): a leitura é query-filtered ao tenant e a escrita é stamped fail-closed.
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(tenantId));

        // Idempotência: no máximo um pedido ATIVO (Pending/Processing) por tenant. Fast-path que evita a
        // exceção no caso comum; a corrida é fechada pelo índice único parcial no SaveChanges.
        if (await HasActiveRequestAsync(db, ct)) return;

        var request = new PolicySyncRequest
        {
            // Sem TenantId — carimbado no SaveChanges (fail-closed).
            Status = PolicySyncStatus.Pending,
            RequestedAt = now,
            AvailableAt = now,
        };
        db.PolicySyncRequests.Add(request);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // NÃO engolir cegamente: só é idempotente se a causa REALMENTE foi a restrição de pedido-ativo-único.
            // Re-verifica — se já existe um pedido ativo, foi a corrida (ok); senão, a exceção é outra coisa
            // (constraint diferente, falha de banco) e DEVE propagar.
            db.Entry(request).State = EntityState.Detached;
            if (!await HasActiveRequestAsync(db, ct)) throw;
            _log.LogDebug(ex,
                "Pedido de sync ativo do tenant {Tenant} já existia (índice único) — enfileiramento idempotente.",
                tenantId);
        }
    }

    private static Task<bool> HasActiveRequestAsync(AegisScoreDbContext db, CancellationToken ct) =>
        db.PolicySyncRequests.AnyAsync(
            r => r.Status == PolicySyncStatus.Pending || r.Status == PolicySyncStatus.Processing, ct);

    public async Task<PolicySyncLease?> TryClaimNextAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        var leaseExpires = now.AddSeconds(_options.LeaseSeconds);

        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        var row = await DurableClaim.RunAsync(db, ClaimSql, cmd =>
        {
            DurableClaim.AddParam(cmd, "@processing", (int)PolicySyncStatus.Processing);
            DurableClaim.AddParam(cmd, "@pending", (int)PolicySyncStatus.Pending);
            DurableClaim.AddParam(cmd, "@leaseId", leaseId);
            DurableClaim.AddParam(cmd, "@leaseExpires", leaseExpires);
            DurableClaim.AddParam(cmd, "@now", now);
        }, ct);

        if (row is null) return null;
        return new PolicySyncLease(row.Value.Id, row.Value.TenantId, leaseId, row.Value.Attempts);
    }

    public Task<bool> RenewAsync(Guid requestId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var expires = now.AddSeconds(_options.LeaseSeconds);
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.LeaseExpiresAt, expires)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> CompleteAsync(Guid requestId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, PolicySyncStatus.Completed)
            .SetProperty(r => r.CompletedAt, now)
            .SetProperty(r => r.ErrorCategory, (string?)null)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> ScheduleRetryAsync(Guid requestId, Guid leaseId, string errorCategory, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var next = now.AddSeconds(_options.RetryBackoffSeconds);
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, PolicySyncStatus.Pending)
            .SetProperty(r => r.AvailableAt, next)
            .SetProperty(r => r.ErrorCategory, errorCategory)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> FailAsync(Guid requestId, Guid leaseId, string errorCategory, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, PolicySyncStatus.Failed)
            .SetProperty(r => r.CompletedAt, now)
            .SetProperty(r => r.ErrorCategory, errorCategory)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> ReleaseAsync(Guid requestId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, PolicySyncStatus.Pending)
            .SetProperty(r => r.AvailableAt, now)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.Attempts, r => r.Attempts > 0 ? r.Attempts - 1 : 0)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    private async Task<bool> RunGuardedAsync(
        Guid requestId, Guid leaseId,
        Func<IQueryable<PolicySyncRequest>, CancellationToken, Task<int>> update, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        var guarded = db.PolicySyncRequests.IgnoreQueryFilters()
            .Where(r => r.Id == requestId
                     && r.LeaseId == leaseId
                     && r.Status == PolicySyncStatus.Processing);

        return await update(guarded, ct) > 0;
    }
}
