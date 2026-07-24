using AegisScore.Domain;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// [AEGIS-AUD-050] Máquina de estados da fila operacional durável de sincronização de políticas (SQLite
/// in-memory). Cobre o enfileiramento DURÁVEL (que sustenta o "202 só após persistir"), a idempotência por
/// tenant (um único pedido ativo) e o ciclo claim → lease → confirmar/retry. A concorrência real fica no
/// teste PostgreSQL.
/// </summary>
public sealed class DurablePolicySyncQueueTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _dbOptions;
    private readonly ServiceProvider _provider;
    private readonly FakeTimeProvider _clock;
    private readonly PolicySyncQueueOptions _options;
    private readonly DurablePolicySyncQueue _queue;

    public DurablePolicySyncQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using (var ctx = NewContext(TenantA)) ctx.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_dbOptions);
        _provider = services.BuildServiceProvider();

        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        _options = new PolicySyncQueueOptions
        {
            LeaseSeconds = 60, PollSeconds = 1, MaxAttempts = 3, RetryBackoffSeconds = 60,
            HeartbeatSeconds = 20, PeriodicIntervalMinutes = 60,
        };
        _queue = NewQueue(_options);
    }

    public void Dispose() { _provider.Dispose(); _connection.Dispose(); }

    [Fact]   // o pedido é PERSISTIDO no enfileiramento (base do "202 só depois de salvo") e carrega o tenant
    public async Task Enqueue_PersistsPendingRequest_WithTenant()
    {
        await _queue.EnqueueAsync(TenantA);

        await using var db = NewContext(TenantA);
        var req = await db.PolicySyncRequests.SingleAsync();
        req.Status.Should().Be(PolicySyncStatus.Pending);
        req.TenantId.Should().Be(TenantA);
    }

    [Fact]   // idempotência: no máximo um pedido ATIVO por tenant
    public async Task Enqueue_Twice_KeepsSingleActiveRequest()
    {
        await _queue.EnqueueAsync(TenantA);
        await _queue.EnqueueAsync(TenantA);

        await using var db = NewContext(null);
        (await db.PolicySyncRequests.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]   // tenants distintos têm pedidos independentes
    public async Task Enqueue_DifferentTenants_AreIndependent()
    {
        await _queue.EnqueueAsync(TenantA);
        await _queue.EnqueueAsync(TenantB);

        await using var db = NewContext(null);
        (await db.PolicySyncRequests.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]   // claim traz o tenant; após Complete o pedido sai do estado ATIVO e um novo enqueue é permitido
    public async Task Claim_Complete_ThenReenqueueAllowed()
    {
        await _queue.EnqueueAsync(TenantA);

        var lease = await _queue.TryClaimNextAsync();
        lease.Should().NotBeNull();
        lease!.TenantId.Should().Be(TenantA);
        lease.Attempts.Should().Be(1);

        (await _queue.CompleteAsync(lease.RequestId, lease.LeaseId)).Should().BeTrue();

        await using (var db = NewContext(TenantA))
            (await db.PolicySyncRequests.SingleAsync()).Status.Should().Be(PolicySyncStatus.Completed);

        // O anterior não é mais ativo → novo enqueue cria outro pedido.
        await _queue.EnqueueAsync(TenantA);
        await using (var db = NewContext(null))
            (await db.PolicySyncRequests.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]   // lease vigente não é roubado; expirado é recuperável
    public async Task Claim_LiveLeaseNotStolen_ExpiredReclaimable()
    {
        await _queue.EnqueueAsync(TenantA);
        var first = await _queue.TryClaimNextAsync();

        _clock.Advance(TimeSpan.FromSeconds(30));
        (await _queue.TryClaimNextAsync()).Should().BeNull("lease vigente");

        _clock.Advance(TimeSpan.FromSeconds(31));   // total 61s > 60s de lease
        var second = await _queue.TryClaimNextAsync();
        second.Should().NotBeNull();
        second!.RequestId.Should().Be(first!.RequestId);
        second.Attempts.Should().Be(2);
    }

    [Fact]   // renovação estende o lease
    public async Task Renew_ExtendsLease()
    {
        await _queue.EnqueueAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        _clock.Advance(TimeSpan.FromSeconds(50));
        (await _queue.RenewAsync(lease!.RequestId, lease.LeaseId)).Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(20));
        (await _queue.TryClaimNextAsync()).Should().BeNull("renovado");
    }

    [Fact]   // retry agenda AvailableAt no futuro e grava a categoria sanitizada
    public async Task ScheduleRetry_SetsBackoff_AndCategory()
    {
        await _queue.EnqueueAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        (await _queue.ScheduleRetryAsync(lease!.RequestId, lease.LeaseId, "TimeoutException")).Should().BeTrue();
        (await _queue.TryClaimNextAsync()).Should().BeNull("dentro do backoff de 60s");

        await using (var db = NewContext(TenantA))
            (await db.PolicySyncRequests.SingleAsync()).ErrorCategory.Should().Be("TimeoutException");

        _clock.Advance(TimeSpan.FromSeconds(61));
        (await _queue.TryClaimNextAsync()).Should().NotBeNull();
    }

    [Fact]   // release estorna a tentativa e disponibiliza de imediato
    public async Task Release_RefundsAttempt()
    {
        await _queue.EnqueueAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        (await _queue.ReleaseAsync(lease!.RequestId, lease.LeaseId)).Should().BeTrue();
        var reclaim = await _queue.TryClaimNextAsync();
        reclaim.Should().NotBeNull();
        reclaim!.Attempts.Should().Be(1, "a tentativa foi estornada no release");
    }

    [Fact]   // transições guardadas pelo lease
    public async Task Transitions_WithWrongLease_AreNoOps()
    {
        await _queue.EnqueueAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();
        var wrong = Guid.NewGuid();

        (await _queue.CompleteAsync(lease!.RequestId, wrong)).Should().BeFalse();
        (await _queue.FailAsync(lease.RequestId, wrong, "X")).Should().BeFalse();
    }

    [Fact]   // opções inválidas fazem a fila FALHAR claramente
    public void Constructor_WithInvalidOptions_Throws()
    {
        var act = () => NewQueue(new PolicySyncQueueOptions { PeriodicIntervalMinutes = 0 });
        act.Should().Throw<InvalidOperationException>().WithMessage("*PeriodicIntervalMinutes*");
    }

    // ---- infraestrutura ----

    private DurablePolicySyncQueue NewQueue(PolicySyncQueueOptions opt) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _clock, Options.Create(opt),
            NullLogger<DurablePolicySyncQueue>.Instance);

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_dbOptions, new SystemTenantContext(tenant));
}
