using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// [AEGIS-AUD-050] Plumbing ADO.NET compartilhado pela aquisição atômica das filas operacionais duráveis.
///
/// A aquisição é UM único statement <c>UPDATE … WHERE "Id" = (SELECT … FOR UPDATE SKIP LOCKED LIMIT 1)
/// RETURNING …</c>: o <c>SELECT</c> tranca a linha candidata e PULA as já trancadas por outra réplica, e o
/// <c>UPDATE</c> a marca adquirida no mesmo passo. É atômico por si só (autocommit) — duas réplicas nunca
/// pegam o mesmo item, sem transação explícita nem lock em memória.
///
/// A cláusula de lock é aplicada só no PostgreSQL (o alvo de produção). Sob SQLite (usado só nos testes de
/// máquina de estados), as escritas já são serializadas, então o placeholder <c>{LOCK}</c> vira vazio e o
/// mesmo SQL roda como fila relacional simples — a garantia de concorrência real é validada contra
/// PostgreSQL descartável.
/// </summary>
internal static class DurableClaim
{
    /// <summary>Linha devolvida pela aquisição: o item, o tenant dono e o nº de tentativas já pós-incremento.</summary>
    internal readonly record struct ClaimRow(Guid Id, Guid TenantId, int Attempts);

    /// <summary>
    /// Executa a aquisição sobre a conexão do <paramref name="db"/>, substituindo <c>{LOCK}</c> pela cláusula
    /// do provedor. Devolve a linha adquirida ou <c>null</c> quando não há trabalho. Gerencia a conexão de
    /// forma segura para os dois provedores: só abre se estiver fechada e só fecha o que abriu (a conexão
    /// compartilhada in-memory dos testes permanece intacta).
    /// </summary>
    public static async Task<ClaimRow?> RunAsync(
        AegisScoreDbContext db, string sqlWithLockPlaceholder, Action<DbCommand> addParams, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var lockClause = IsNpgsql(conn) ? "FOR UPDATE SKIP LOCKED" : string.Empty;

        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sqlWithLockPlaceholder.Replace("{LOCK}", lockClause);
            addParams(cmd);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new ClaimRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2));
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }

    /// <summary>Adiciona um parâmetro nomeado ao comando, traduzindo <c>null</c> para <see cref="DBNull"/>.</summary>
    public static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    /// <summary>Detecta o provedor pela conexão sem referenciar o pacote do SQLite na Infrastructure.</summary>
    private static bool IsNpgsql(DbConnection conn) =>
        conn.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
}
