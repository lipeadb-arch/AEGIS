using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// [AEGIS-AUD-050] Fila operacional DURÁVEL de análise de documentos, apoiada no PostgreSQL. Substitui o
/// antigo canal em memória (sem durabilidade): o próprio <see cref="GovernanceDocument"/> é o
/// item de trabalho, e o STATUS persistido é a fila — Queued/Pending disponível, Processing adquirido,
/// Analyzed sucesso, Failed terminal.
///
/// A aquisição é atômica (<see cref="DurableClaim"/> / FOR UPDATE SKIP LOCKED) e faz a VARREDURA
/// cross-tenant sob <see cref="SystemTenantContext"/>(null) — o único ponto onde isso é permitido. As
/// transições subsequentes (renovar, confirmar, retry, falhar, soltar) são um único <c>UPDATE</c> guardado
/// pelo lease (<c>WHERE "Id" = … AND "AnalysisLeaseId" = … AND status = Processing</c>): um worker que perdeu
/// o lease (por expiração + reaquisição por outra réplica) vira no-op, sem sobrescrever o trabalho alheio.
/// Singleton — constrói o <c>DbContext</c> à mão por operação, como os demais workers.
/// </summary>
public sealed class DurableDocumentAnalysisQueue : IDocumentAnalysisQueue
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly DocumentAnalysisQueueOptions _options;
    private readonly ILogger<DurableDocumentAnalysisQueue> _log;

    public DurableDocumentAnalysisQueue(
        IServiceScopeFactory scopes, TimeProvider clock,
        IOptions<DocumentAnalysisQueueOptions> options, ILogger<DurableDocumentAnalysisQueue> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
        _options = options.Value;
        // Falha CLARA na composição (startup) se as opções forem inválidas — nunca silenciosamente clampadas.
        if (!_options.TryValidate(out var error))
            throw new InvalidOperationException(error);
    }

    // Aquisição atômica. Só entram documentos COM binário (StorageUri IS NOT NULL): um registro de /connect
    // sem binário nunca é adquirido. Elegíveis: Queued/Pending disponíveis (respeitando o backoff de retry),
    // ou Processing cujo lease EXPIROU (worker caído). Um Processing com lease vigente é excluído — não se
    // rouba trabalho em andamento.
    private const string ClaimSql = """
        UPDATE "GovernanceDocuments"
        SET "AnalysisStatus" = @processing,
            "AnalysisLeaseId" = @leaseId,
            "AnalysisLeaseExpiresAt" = @leaseExpires,
            "AnalysisAttempts" = "AnalysisAttempts" + 1,
            "UpdatedAt" = @now
        WHERE "Id" = (
            SELECT "Id" FROM "GovernanceDocuments"
            WHERE "StorageUri" IS NOT NULL
              AND ("AnalysisNextAttemptAt" IS NULL OR "AnalysisNextAttemptAt" <= @now)
              AND (
                  "AnalysisStatus" = @queued
                  OR "AnalysisStatus" = @pending
                  OR ("AnalysisStatus" = @processing AND "AnalysisLeaseExpiresAt" <= @now)
              )
            ORDER BY "AnalysisQueuedAt" NULLS FIRST, "CreatedAt"
            {LOCK}
            LIMIT 1
        )
        RETURNING "Id", "TenantId", "AnalysisAttempts";
        """;

    public async Task<DocumentAnalysisLease?> TryClaimNextAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        var leaseExpires = now.AddSeconds(_options.LeaseSeconds);

        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        var row = await DurableClaim.RunAsync(db, ClaimSql, cmd =>
        {
            DurableClaim.AddParam(cmd, "@processing", (int)AiAnalysisStatus.Processing);
            DurableClaim.AddParam(cmd, "@queued", (int)AiAnalysisStatus.Queued);
            DurableClaim.AddParam(cmd, "@pending", (int)AiAnalysisStatus.Pending);
            DurableClaim.AddParam(cmd, "@leaseId", leaseId);
            DurableClaim.AddParam(cmd, "@leaseExpires", leaseExpires);
            DurableClaim.AddParam(cmd, "@now", now);
        }, ct);

        if (row is null) return null;
        return new DocumentAnalysisLease(row.Value.Id, row.Value.TenantId, leaseId, row.Value.Attempts);
    }

    public Task<bool> RenewAsync(Guid documentId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var expires = now.AddSeconds(_options.LeaseSeconds);
        // Batimento: estende a expiração enquanto o trabalho ainda dura. Guardado pelo lease — se já foi
        // perdido, retorna false e o worker cancela o processamento.
        return RunGuardedAsync(documentId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.AnalysisLeaseExpiresAt, expires)
            .SetProperty(d => d.UpdatedAt, now), c), ct);
    }

    public Task<bool> CompleteAsync(Guid documentId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        return RunGuardedAsync(documentId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.AnalysisStatus, AiAnalysisStatus.Analyzed)
            .SetProperty(d => d.AnalyzedAt, now)
            .SetProperty(d => d.AnalysisError, (string?)null)
            .SetProperty(d => d.AnalysisLeaseId, (Guid?)null)
            .SetProperty(d => d.AnalysisLeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(d => d.AnalysisNextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(d => d.UpdatedAt, now), c), ct);
    }

    public Task<bool> ScheduleRetryAsync(Guid documentId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var next = now.AddSeconds(_options.RetryBackoffSeconds);
        // Volta a Pending (disponível), soltando o lease; o backoff atrasa a próxima aquisição. A tentativa
        // já foi contada na aquisição — não se mexe em AnalysisAttempts aqui.
        return RunGuardedAsync(documentId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.AnalysisStatus, AiAnalysisStatus.Pending)
            .SetProperty(d => d.AnalysisLeaseId, (Guid?)null)
            .SetProperty(d => d.AnalysisLeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(d => d.AnalysisNextAttemptAt, (DateTimeOffset?)next)
            .SetProperty(d => d.UpdatedAt, now), c), ct);
    }

    public Task<bool> FailAsync(Guid documentId, Guid leaseId, string errorCategory, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        // Terminal. AnalysisError recebe a CATEGORIA sanitizada (nome do tipo de exceção), nunca a mensagem
        // bruta — não amplia o AEGIS-AUD-054, apenas evita introduzir novo vazamento.
        return RunGuardedAsync(documentId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.AnalysisStatus, AiAnalysisStatus.Failed)
            .SetProperty(d => d.AnalysisError, errorCategory)
            .SetProperty(d => d.AnalysisLeaseId, (Guid?)null)
            .SetProperty(d => d.AnalysisLeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(d => d.AnalysisNextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(d => d.UpdatedAt, now), c), ct);
    }

    public Task<bool> ReleaseAsync(Guid documentId, Guid leaseId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        // Desligamento gracioso: devolve à fila IMEDIATAMENTE (sem backoff) e ESTORNA a tentativa gasta nesta
        // aquisição — um deploy não pode consumir o orçamento de retry de um trabalho que nem chegou a falhar.
        // O estorno NUNCA leva abaixo de zero (CASE WHEN > 0).
        return RunGuardedAsync(documentId, leaseId, (q, c) => q.ExecuteUpdateAsync(s => s
            .SetProperty(d => d.AnalysisStatus, AiAnalysisStatus.Pending)
            .SetProperty(d => d.AnalysisLeaseId, (Guid?)null)
            .SetProperty(d => d.AnalysisLeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(d => d.AnalysisNextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(d => d.AnalysisAttempts, d => d.AnalysisAttempts > 0 ? d.AnalysisAttempts - 1 : 0)
            .SetProperty(d => d.UpdatedAt, now), c), ct);
    }

    /// <summary>
    /// Transição de estado guardada pelo lease: filtra por <c>Id + AnalysisLeaseId + status Processing</c> e
    /// aplica o <paramref name="update"/> (um único <c>UPDATE</c>). <c>IgnoreQueryFilters</c> porque a operação
    /// é por Id global sob contexto de sistema; devolve <c>true</c> só se a linha ainda era nossa. O setter é
    /// passado como callback para o compilador inferir o tipo de <c>ExecuteUpdate</c> (evita nomeá-lo).
    /// </summary>
    private async Task<bool> RunGuardedAsync(
        Guid documentId, Guid leaseId,
        Func<IQueryable<GovernanceDocument>, CancellationToken, Task<int>> update, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<AegisScoreDbContext>>();
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));

        var guarded = db.GovernanceDocuments.IgnoreQueryFilters()
            .Where(d => d.Id == documentId
                     && d.AnalysisLeaseId == leaseId
                     && d.AnalysisStatus == AiAnalysisStatus.Processing);

        return await update(guarded, ct) > 0;
    }
}
