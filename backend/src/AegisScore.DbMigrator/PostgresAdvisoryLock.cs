using Microsoft.Extensions.Logging;
using Npgsql;

namespace AegisScore.DbMigrator;

/// <summary>
/// [AEGIS-AUD-052] Advisory lock exclusivo do PostgreSQL, cobrindo a sequência INTEIRA do migrator:
/// migration principal, migration do key ring, seed do catálogo, seed das regras e verificação final.
///
/// Por que não basta o lock do EF Core: desde a versão 9, <c>MigrateAsync()</c> adquire um lock
/// próprio — mas ele é liberado quando a migration termina. O seed do AEGIS é código nosso, chamado
/// DEPOIS, e ficaria descoberto justamente na etapa que podia duplicar o catálogo. O lock do EF também
/// só cobriria "seeding code" se usássemos o mecanismo <c>UseSeeding</c> do próprio EF, o que não é o
/// caso. Daí um lock explícito, mantido por fora, do início ao fim.
///
/// O lock é por SESSÃO, então a conexão precisa ser dedicada e permanecer aberta durante toda a
/// operação — usar uma conexão do pool do EF faria o lock ser liberado no primeiro retorno ao pool.
/// Vantagem sobre uma tabela de lock: se o processo morrer, o PostgreSQL encerra a sessão e libera
/// sozinho; não existe lock órfão travando a próxima execução.
/// </summary>
public sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    /// <summary>
    /// Chave determinística e estável. <c>1095059273</c> é "AEGI" em ASCII big-endian; <c>52</c> é o
    /// número do achado. Valores fixos no código de propósito: derivar de hash de string arriscaria
    /// mudar a chave num refactor e permitir duas execuções simultâneas sem que ninguém percebesse.
    /// </summary>
    public const int ClassId = 1095059273;

    public const int ObjectId = 52;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly NpgsqlConnection _connection;
    private readonly ILogger _logger;
    private bool _held;

    private PostgresAdvisoryLock(NpgsqlConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        _held = true;
    }

    /// <summary>
    /// Tenta adquirir o lock até esgotar <paramref name="timeout"/>. Devolve <c>null</c> quando não
    /// consegue — sinal de que outra execução administrativa está em andamento.
    /// </summary>
    /// <param name="connection">Conexão DEDICADA e já aberta; não use uma do pool do EF.</param>
    public static async Task<PostgresAdvisoryLock?> TryAcquireAsync(
        NpgsqlConnection connection,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var avisou = false;

        while (true)
        {
            if (await TryLockOnceAsync(connection, ct))
            {
                logger.LogInformation(
                    "Advisory lock ({ClassId}, {ObjectId}) adquirido.", ClassId, ObjectId);
                return new PostgresAdvisoryLock(connection, logger);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogError(
                    "Advisory lock ({ClassId}, {ObjectId}) não adquirido em {Timeout}s. Outra execução do " +
                    "migrator provavelmente está em andamento — aguarde o término em vez de forçar.",
                    ClassId, ObjectId, (int)timeout.TotalSeconds);
                return null;
            }

            if (!avisou)
            {
                logger.LogWarning(
                    "Advisory lock ocupado por outra execução; aguardando até {Timeout}s.",
                    (int)timeout.TotalSeconds);
                avisou = true;
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static async Task<bool> TryLockOnceAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@c, @o)", connection);
        cmd.Parameters.AddWithValue("c", ClassId);
        cmd.Parameters.AddWithValue("o", ObjectId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Libera o lock. Idempotente — chamar duas vezes não é erro.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_held) return;
        _held = false;

        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@c, @o)", _connection);
            cmd.Parameters.AddWithValue("c", ClassId);
            cmd.Parameters.AddWithValue("o", ObjectId);
            await cmd.ExecuteScalarAsync();
            _logger.LogInformation("Advisory lock ({ClassId}, {ObjectId}) liberado.", ClassId, ObjectId);
        }
        catch (Exception ex)
        {
            // Não relançamos: a sessão será encerrada logo a seguir e o PostgreSQL libera o lock de
            // qualquer forma. Mascarar o desfecho real do migrator por causa disso seria pior.
            _logger.LogWarning(ex,
                "Falha ao liberar o advisory lock explicitamente; será liberado no fechamento da sessão.");
        }
    }
}
