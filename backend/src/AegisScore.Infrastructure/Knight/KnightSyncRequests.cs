using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Registro e leitura dos pedidos de sincronização do tenant do CONTEXTO. O
/// isolamento vem do Global Query Filter (leitura) e do carimbo fail-closed do SaveChanges (escrita).
/// </summary>
public sealed class KnightSyncRequests : IKnightSyncRequests
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly ILogger<KnightSyncRequests>? _log;

    public KnightSyncRequests(AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock, ILogger<KnightSyncRequests>? log = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _log = log;
    }

    public async Task<KnightSyncEnqueueResult> EnqueueAsync(
        Guid connectorId, KnightSourceType source, Guid? requestedBy, CancellationToken ct = default)
    {
        _ = _tenant.TenantId ?? throw new TenantSecurityException("Pedido de sincronização sem tenant resolvido (fail-closed).");

        // Fast-path idempotente: um pedido ATIVO para o par (conector, fonte) é devolvido, nunca duplicado.
        // [AEGIS-KNIGHT-COVERAGE-02] A chave inclui a FONTE: o mesmo conector Microsoft alimenta o Entra ID e o
        // Teams, e uma coleta em andamento numa fonte não pode bloquear a outra.
        var active = await ActiveAsync(connectorId, source, ct);
        if (active is not null) return new KnightSyncEnqueueResult(await ToViewAsync(active, ct), true);

        var now = _clock.GetUtcNow();
        var request = new KnightSyncRequest
        {
            ConnectorConfigId = connectorId,
            SourceType = source,
            Status = KnightSyncStatus.Pending,
            RequestedAt = now,
            AvailableAt = now,
            RequestedBy = requestedBy,
        };
        _db.KnightSyncRequests.Add(request);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Só é idempotência se a causa for MESMO o índice único parcial (outro clique venceu a corrida).
            _db.Entry(request).State = EntityState.Detached;
            active = await ActiveAsync(connectorId, source, ct);
            if (active is null) throw;
            _log?.LogDebug(ex, "Pedido de sincronização ativo já existia para o conector {Connector} na fonte {Fonte}.",
                connectorId, source);
            return new KnightSyncEnqueueResult(await ToViewAsync(active, ct), true);
        }

        return new KnightSyncEnqueueResult(await ToViewAsync(request, ct), false);
    }

    public async Task<KnightSyncRequestView?> GetAsync(Guid connectorId, Guid requestId, CancellationToken ct = default)
    {
        var r = await _db.KnightSyncRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == requestId && x.ConnectorConfigId == connectorId, ct);
        return r is null ? null : await ToViewAsync(r, ct);
    }

    public async Task<KnightSyncRequestView?> GetLatestAsync(
        Guid connectorId, KnightSourceType? source = null, CancellationToken ct = default)
    {
        // Ordenação no cliente: o SQLite dos testes não ordena DateTimeOffset; o histórico por conector é pequeno.
        var query = _db.KnightSyncRequests.AsNoTracking().Where(x => x.ConnectorConfigId == connectorId);
        if (source is { } wanted) query = query.Where(x => x.SourceType == wanted);
        var rows = await query.ToListAsync(ct);
        var latest = rows.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id).FirstOrDefault();
        return latest is null ? null : await ToViewAsync(latest, ct);
    }

    public async Task<IReadOnlySet<Guid>> ActiveConnectorIdsAsync(CancellationToken ct = default) =>
        (await _db.KnightSyncRequests.AsNoTracking()
            .Where(x => x.Status == KnightSyncStatus.Pending || x.Status == KnightSyncStatus.Running)
            .Select(x => x.ConnectorConfigId)
            .ToListAsync(ct))
        .ToHashSet();

    private Task<KnightSyncRequest?> ActiveAsync(Guid connectorId, KnightSourceType source, CancellationToken ct) =>
        _db.KnightSyncRequests.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ConnectorConfigId == connectorId && x.SourceType == source
                && (x.Status == KnightSyncStatus.Pending || x.Status == KnightSyncStatus.Running), ct);

    private async Task<KnightSyncRequestView> ToViewAsync(KnightSyncRequest r, CancellationToken ct)
    {
        // A última avaliação CONCLUÍDA da fonte: o que continua valendo quando este pedido falha.
        var completed = await _db.KnightAssessmentRuns.AsNoTracking()
            .Where(x => x.SourceType == r.SourceType && x.Status == KnightRunStatus.Completed)
            .Select(x => new { x.Id, x.StartedAt, x.CompletedAt })
            .ToListAsync(ct);
        var last = completed.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).FirstOrDefault();

        return new KnightSyncRequestView(
            r.Id, r.ConnectorConfigId, r.SourceType, r.Status, r.RequestedAt, r.StartedAt, r.CompletedAt, r.Attempts,
            r.RunId, r.ResultSourceState, r.FailureCategory, r.Message,
            last?.Id, last?.CompletedAt ?? last?.StartedAt);
    }
}

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Fila DURÁVEL de sincronização do KNIGHT — mesmo desenho da fila de políticas:
/// aquisição atômica (<c>FOR UPDATE SKIP LOCKED</c> no PostgreSQL) sob contexto de sistema, e toda transição
/// guardada pelo lease, de modo que um worker que perdeu o lease não sobrescreve o desfecho de outro.
///
/// O VÍNCULO com a avaliação (<c>RunId</c>) não é gravado aqui: ele nasce na transação que grava a execução
/// (ver <c>AegisKnightAssessmentService.RunForSyncRequestAsync</c>). Aqui ele só é lido — para retomar sem coletar
/// de novo — e respeitado: concluir exige o mesmo vínculo; falhar exige que não haja vínculo.
/// </summary>
public sealed class DurableKnightSyncQueue : IKnightSyncQueue
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly KnightSyncOptions _options;

    public DurableKnightSyncQueue(IServiceScopeFactory scopes, TimeProvider clock, IOptions<KnightSyncOptions> options)
    {
        _scopes = scopes;
        _clock = clock;
        _options = options.Value;
        if (!_options.TryValidate(out var error)) throw new InvalidOperationException(error);
    }

    // Elegíveis: Pending disponíveis, ou Running com lease vencido/ausente (worker que morreu no meio).
    private const string ClaimSql = """
        UPDATE "KnightSyncRequests"
        SET "Status" = @running,
            "LeaseId" = @leaseId,
            "LeaseExpiresAt" = @leaseExpires,
            "Attempts" = "Attempts" + 1,
            "StartedAt" = @now,
            "UpdatedAt" = @now
        WHERE "Id" = (
            SELECT "Id" FROM "KnightSyncRequests"
            WHERE "AvailableAt" <= @now
              AND (
                  "Status" = @pending
                  OR ("Status" = @running
                      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= @now))
              )
            ORDER BY "AvailableAt", "CreatedAt"
            {LOCK}
            LIMIT 1
        )
        RETURNING "Id", "TenantId", "Attempts";
        """;

    public async Task<KnightSyncLease?> TryClaimNextAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        var leaseExpires = now.AddSeconds(_options.LeaseSeconds);

        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        var row = await DurableClaim.RunAsync(db, ClaimSql, cmd =>
        {
            DurableClaim.AddParam(cmd, "@running", (int)KnightSyncStatus.Running);
            DurableClaim.AddParam(cmd, "@pending", (int)KnightSyncStatus.Pending);
            DurableClaim.AddParam(cmd, "@leaseId", leaseId);
            DurableClaim.AddParam(cmd, "@leaseExpires", leaseExpires);
            DurableClaim.AddParam(cmd, "@now", now);
        }, ct);

        if (row is null) return null;

        var header = await db.KnightSyncRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Id == row.Value.Id)
            .Select(r => new { r.ConnectorConfigId, r.SourceType })
            .FirstAsync(ct);

        return new KnightSyncLease(row.Value.Id, row.Value.TenantId, header.ConnectorConfigId, header.SourceType, leaseId, row.Value.Attempts);
    }

    public Task<bool> RenewAsync(Guid requestId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var expires = now.AddSeconds(_options.LeaseSeconds);
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.LeaseExpiresAt, expires)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> CompleteAsync(
        Guid requestId, Guid leaseId, Guid runId, KnightSourceState sourceState, string message, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return RunGuardedAsync(requestId, leaseId, (q, c) => q
            .Where(r => r.RunId == null || r.RunId == runId)
            .ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, KnightSyncStatus.Completed)
            .SetProperty(r => r.CompletedAt, now)
            .SetProperty(r => r.RunId, runId)
            .SetProperty(r => r.ResultSourceState, sourceState)
            .SetProperty(r => r.FailureCategory, (string?)null)
            .SetProperty(r => r.Message, message)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> FailAsync(Guid requestId, Guid leaseId, string category, string message, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        // Um pedido com avaliação vinculada nunca vira "falhou — nenhuma avaliação nova foi registrada".
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.Where(r => r.RunId == null).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, KnightSyncStatus.Failed)
            .SetProperty(r => r.CompletedAt, now)
            .SetProperty(r => r.FailureCategory, category)
            .SetProperty(r => r.Message, message)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public Task<bool> ReleaseAsync(Guid requestId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return RunGuardedAsync(requestId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, KnightSyncStatus.Pending)
            .SetProperty(r => r.AvailableAt, now)
            .SetProperty(r => r.LeaseId, (Guid?)null)
            .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(r => r.Attempts, r => r.Attempts > 0 ? r.Attempts - 1 : 0)
            .SetProperty(r => r.UpdatedAt, now), c), ct);
    }

    public async Task<KnightSyncLinkedResult?> GetLinkedResultAsync(Guid requestId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        // O estado da fonte vem da PRÓPRIA execução vinculada — do mesmo tenant do pedido —, não de suposição.
        var linked = await (
                from r in db.KnightSyncRequests.IgnoreQueryFilters().AsNoTracking()
                where r.Id == requestId && r.RunId != null
                join run in db.KnightAssessmentRuns.IgnoreQueryFilters().AsNoTracking()
                    on new { Id = r.RunId!.Value, r.TenantId } equals new { run.Id, run.TenantId }
                select new { run.Id, run.SourceState })
            .FirstOrDefaultAsync(ct);
        return linked is null ? null : new KnightSyncLinkedResult(linked.Id, linked.SourceState);
    }

    private async Task<bool> RunGuardedAsync(
        Guid requestId, Guid leaseId,
        Func<IQueryable<KnightSyncRequest>, CancellationToken, Task<int>> update, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        var guarded = db.KnightSyncRequests.IgnoreQueryFilters()
            .Where(r => r.Id == requestId && r.LeaseId == leaseId && r.Status == KnightSyncStatus.Running);

        return await update(guarded, ct) > 0;
    }
}
