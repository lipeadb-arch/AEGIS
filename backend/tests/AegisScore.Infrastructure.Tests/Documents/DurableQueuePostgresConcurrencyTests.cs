using System.Collections.Concurrent;
using AegisScore.Domain;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// [AEGIS-AUD-050] Concorrência REAL da aquisição, validada contra um PostgreSQL DESCARTÁVEL — o
/// comportamento de <c>FOR UPDATE SKIP LOCKED</c> (e a semântica de <c>timestamptz</c>) não existe no SQLite.
///
/// Gated por <c>AEGIS_TEST_PG</c> (connection string para um banco de MANUTENÇÃO, ex.: <c>postgres</c>). Sem a
/// variável, os testes registram a ausência e retornam — o mesmo padrão da validação de banco descartável já
/// usada no projeto (sem dependência de pacote de "skip"). Cada teste cria e destrói um database próprio,
/// nunca tocando o <c>aegis_dev</c>.
/// </summary>
public sealed class DurableQueuePostgresConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public DurableQueuePostgresConcurrencyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task TwoInstances_UnderRealConcurrency_NeverClaimSameItem()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }

        var dbOptions = pg.DbOptions();
        await EnsureSchemaAsync(dbOptions);

        const int docCount = 40;
        var tenant = Guid.NewGuid();
        await SeedQueuedAsync(dbOptions, tenant, docCount);

        var queue = NewQueue(dbOptions);

        // 8 "réplicas" drenando a fila em paralelo. Se o SKIP LOCKED falhasse, duas pegariam o mesmo item.
        var claimed = new ConcurrentBag<Guid>();
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var lease = await queue.TryClaimNextAsync();
                if (lease is null) break;
                claimed.Add(lease.DocumentId);
            }
        }));
        await Task.WhenAll(workers);

        claimed.Should().HaveCount(docCount);
        claimed.Distinct().Should().HaveCount(docCount,
            "FOR UPDATE SKIP LOCKED garante que duas réplicas nunca adquiram o mesmo documento");
    }

    [Fact]
    public async Task LiveLeaseNotStolen_ExpiredReclaimed_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }

        var dbOptions = pg.DbOptions();
        await EnsureSchemaAsync(dbOptions);

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var tenant = Guid.NewGuid();
        await SeedQueuedAsync(dbOptions, tenant, 1);

        var queue = NewQueue(dbOptions, clock, leaseSeconds: 60);
        var first = await queue.TryClaimNextAsync();
        first.Should().NotBeNull();

        clock.Advance(TimeSpan.FromSeconds(30));
        (await queue.TryClaimNextAsync()).Should().BeNull("lease vigente não é roubado (timestamptz real)");

        clock.Advance(TimeSpan.FromSeconds(31));   // 61s > 60s
        var reclaim = await queue.TryClaimNextAsync();
        reclaim.Should().NotBeNull();
        reclaim!.DocumentId.Should().Be(first!.DocumentId);
        reclaim.Attempts.Should().Be(2);
    }

    // ---- helpers ----

    private static DurableDocumentAnalysisQueue NewQueue(
        DbContextOptions<AegisScoreDbContext> dbOptions, TimeProvider? clock = null, int leaseSeconds = 300)
    {
        var services = new ServiceCollection();
        services.AddSingleton(dbOptions);
        var provider = services.BuildServiceProvider();
        var opt = new DocumentAnalysisQueueOptions { LeaseSeconds = leaseSeconds, HeartbeatSeconds = leaseSeconds / 3 };
        return new DurableDocumentAnalysisQueue(
            provider.GetRequiredService<IServiceScopeFactory>(), clock ?? TimeProvider.System,
            Options.Create(opt), NullLogger<DurableDocumentAnalysisQueue>.Instance);
    }

    private static async Task EnsureSchemaAsync(DbContextOptions<AegisScoreDbContext> dbOptions)
    {
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null));
        await db.Database.EnsureCreatedAsync();
    }

    private static async Task SeedQueuedAsync(DbContextOptions<AegisScoreDbContext> dbOptions, Guid tenant, int count)
    {
        await using var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(tenant));
        for (var i = 0; i < count; i++)
            db.GovernanceDocuments.Add(new GovernanceDocument
            {
                Title = $"doc {i}", Type = GovernanceDocumentType.Politica, Source = DocumentSource.Integracao,
                FileName = $"p{i}.pdf", ContentType = "application/pdf", StorageUri = $"file://p{i}.pdf",
                AnalysisStatus = AiAnalysisStatus.Queued, AnalysisQueuedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
    }
}

/// <summary>Banco PostgreSQL descartável: cria um database único a partir de <c>AEGIS_TEST_PG</c> e o remove no fim.</summary>
internal sealed class PostgresProbe : IAsyncDisposable
{
    private readonly string _adminConn;
    private readonly string _dbName;
    private readonly string _dbConn;

    private PostgresProbe(string adminConn, string dbName, string dbConn)
    {
        _adminConn = adminConn;
        _dbName = dbName;
        _dbConn = dbConn;
    }

    public DbContextOptions<AegisScoreDbContext> DbOptions() =>
        new DbContextOptionsBuilder<AegisScoreDbContext>().UseNpgsql(_dbConn).Options;

    public static async Task<PostgresProbe?> TryCreateAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("AEGIS_TEST_PG");
        if (string.IsNullOrWhiteSpace(baseConn)) return null;

        var dbName = "aegis_aud050_test_" + Guid.NewGuid().ToString("N")[..12];
        var builder = new NpgsqlConnectionStringBuilder(baseConn) { Pooling = false };
        var adminConn = builder.ConnectionString;

        await using (var conn = new NpgsqlConnection(adminConn))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.Database = dbName;
        return new PostgresProbe(adminConn, dbName, builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }
}
