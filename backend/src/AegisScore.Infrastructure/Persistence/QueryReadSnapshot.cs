using System.Data;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>Leitura coerente do NIST e Auditor: uma fotografia transacional, sem depender de scanners.</summary>
internal static class QueryReadSnapshot
{
    internal static async Task<T> InReadSnapshotAsync<T>(AegisScoreDbContext db, Func<Task<T>> read, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await read();
        var npgsql = db.Database.IsNpgsql();
        await using var tx = npgsql
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : await db.Database.BeginTransactionAsync(ct);
        if (npgsql) await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var result = await read();
        await tx.CommitAsync(ct);
        return result;
    }
}
