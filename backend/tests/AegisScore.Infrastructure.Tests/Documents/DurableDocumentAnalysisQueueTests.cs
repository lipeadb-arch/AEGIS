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
/// [AEGIS-AUD-050] Máquina de estados da fila operacional durável de análise de documentos. Roda sobre SQLite
/// in-memory (banco relacional real: exercita o claim por status, o lease e as transições guardadas de
/// verdade). O provedor omite <c>FOR UPDATE SKIP LOCKED</c> no SQLite — a concorrência REAL é validada contra
/// PostgreSQL descartável em <see cref="DurableQueuePostgresConcurrencyTests"/>. O tempo é controlado por
/// <see cref="FakeTimeProvider"/>, sem sleeps reais.
/// </summary>
public sealed class DurableDocumentAnalysisQueueTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _dbOptions;
    private readonly ServiceProvider _provider;
    private readonly FakeTimeProvider _clock;
    private readonly DocumentAnalysisQueueOptions _options;
    private readonly DurableDocumentAnalysisQueue _queue;

    public DurableDocumentAnalysisQueueTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using (var ctx = NewContext(TenantA)) ctx.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_dbOptions);
        _provider = services.BuildServiceProvider();

        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        _options = new DocumentAnalysisQueueOptions
        {
            LeaseSeconds = 60, PollSeconds = 1, MaxAttempts = 3, RetryBackoffSeconds = 30, HeartbeatSeconds = 20,
        };
        _queue = NewQueue(_options);
    }

    public void Dispose() { _provider.Dispose(); _connection.Dispose(); }

    // ---- Aquisição, persistência e tenant --------------------------------------------

    [Fact]   // trabalho persiste após reconstrução do processo + Queued sobrevive a "restart" + TenantId preservado
    public async Task ClaimNext_AfterRebuild_ReturnsPersistedWork_WithTenant()
    {
        var id = await SeedQueuedDocAsync(TenantA);

        // "Restart": uma instância NOVA da fila (sem estado em memória) ainda encontra o trabalho no banco.
        var lease = await NewQueue(_options).TryClaimNextAsync();

        lease.Should().NotBeNull();
        lease!.DocumentId.Should().Be(id);
        lease.TenantId.Should().Be(TenantA);
        lease.Attempts.Should().Be(1);

        var doc = await LoadAsync(id);
        doc.AnalysisStatus.Should().Be(AiAnalysisStatus.Processing);
        doc.AnalysisLeaseId.Should().Be(lease.LeaseId);
    }

    [Fact]   // documento sem binário (registro /connect) NUNCA é adquirido
    public async Task ClaimNext_SkipsDocumentsWithoutBinary()
    {
        await using (var db = NewContext(TenantA))
        {
            db.GovernanceDocuments.Add(new GovernanceDocument
            {
                Title = "sem binário", Source = DocumentSource.Integracao,
                AnalysisStatus = AiAnalysisStatus.Pending, StorageUri = null,
            });
            await db.SaveChangesAsync();
        }
        (await _queue.TryClaimNextAsync()).Should().BeNull();
    }

    [Fact]   // dois "workers" (sequenciais) não pegam o mesmo item — a versão concorrente é o teste PostgreSQL
    public async Task TwoClaims_DoNotReturnSameLiveItem()
    {
        await SeedQueuedDocAsync(TenantA);
        (await _queue.TryClaimNextAsync()).Should().NotBeNull();
        (await _queue.TryClaimNextAsync()).Should().BeNull("o único item está Processing sob lease vigente");
    }

    // ---- Lease: roubo, expiração, renovação ------------------------------------------

    [Fact]   // lease VIGENTE não pode ser roubado
    public async Task ClaimNext_LiveLease_NotStolen()
    {
        await SeedQueuedDocAsync(TenantA);
        (await _queue.TryClaimNextAsync()).Should().NotBeNull();
        _clock.Advance(TimeSpan.FromSeconds(30));   // ainda dentro do lease de 60s
        (await _queue.TryClaimNextAsync()).Should().BeNull();
    }

    [Fact]   // lease EXPIRADO volta a ser adquirível (worker caído) e conta outra tentativa
    public async Task ClaimNext_ExpiredLease_Reclaimable()
    {
        await SeedQueuedDocAsync(TenantA);
        var first = await _queue.TryClaimNextAsync();

        _clock.Advance(TimeSpan.FromSeconds(61));   // lease de 60s venceu
        var second = await _queue.TryClaimNextAsync();

        second.Should().NotBeNull();
        second!.DocumentId.Should().Be(first!.DocumentId);
        second.LeaseId.Should().NotBe(first.LeaseId);
        second.Attempts.Should().Be(2);
    }

    [Fact]   // RENOVAÇÃO do lease (heartbeat) estende a expiração e impede a reaquisição
    public async Task Renew_ExtendsLease_PreventingReclaim()
    {
        await SeedQueuedDocAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        _clock.Advance(TimeSpan.FromSeconds(50));
        (await _queue.RenewAsync(lease!.DocumentId, lease.LeaseId)).Should().BeTrue();

        // Sem a renovação, aos 70s o lease de 60 teria vencido; com ela foi estendido para 50+60 = 110s.
        _clock.Advance(TimeSpan.FromSeconds(20));
        (await _queue.TryClaimNextAsync()).Should().BeNull("a renovação estendeu o lease");
    }

    [Fact]   // renovação é guardada pelo lease: um lease alheio/expirado não renova
    public async Task Renew_WithWrongLease_Fails()
    {
        await SeedQueuedDocAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();
        (await _queue.RenewAsync(lease!.DocumentId, Guid.NewGuid())).Should().BeFalse();
    }

    // ---- Confirmação, retry, falha terminal, release ---------------------------------

    [Fact]   // sucesso conclui EXATAMENTE uma vez (a 2ª confirmação é no-op)
    public async Task Complete_TransitionsToAnalyzed_ThenNoOp()
    {
        var id = await SeedQueuedDocAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        (await _queue.CompleteAsync(id, lease!.LeaseId)).Should().BeTrue();
        (await LoadAsync(id)).AnalysisStatus.Should().Be(AiAnalysisStatus.Analyzed);

        (await _queue.CompleteAsync(id, lease.LeaseId)).Should().BeFalse("o lease já não é o vigente");
    }

    [Fact]   // falha TRANSITÓRIA agenda retry com backoff; antes do backoff nada é adquirível
    public async Task ScheduleRetry_ReturnsToPending_WithBackoff()
    {
        var id = await SeedQueuedDocAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        (await _queue.ScheduleRetryAsync(id, lease!.LeaseId)).Should().BeTrue();
        (await _queue.TryClaimNextAsync()).Should().BeNull("dentro do backoff de 30s");

        _clock.Advance(TimeSpan.FromSeconds(31));
        var reclaimed = await _queue.TryClaimNextAsync();
        reclaimed.Should().NotBeNull();
        reclaimed!.Attempts.Should().Be(2);
    }

    [Fact]   // limite de tentativas termina em Failed, com categoria SANITIZADA (nunca mensagem bruta)
    public async Task Fail_TransitionsToFailed_WithSanitizedCategory()
    {
        var id = await SeedQueuedDocAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();

        (await _queue.FailAsync(id, lease!.LeaseId, "HttpRequestException")).Should().BeTrue();

        var doc = await LoadAsync(id);
        doc.AnalysisStatus.Should().Be(AiAnalysisStatus.Failed);
        doc.AnalysisError.Should().Be("HttpRequestException");
        doc.AnalysisLeaseId.Should().BeNull();
    }

    [Fact]   // shutdown (release) devolve o trabalho SEM consumir tentativa
    public async Task Release_ReturnsToPending_AndRefundsAttempt()
    {
        var id = await SeedQueuedDocAsync(TenantA);
        var lease = await _queue.TryClaimNextAsync();
        lease!.Attempts.Should().Be(1);

        (await _queue.ReleaseAsync(id, lease.LeaseId)).Should().BeTrue();
        (await LoadAsync(id)).AnalysisAttempts.Should().Be(0, "o release estornou a tentativa");

        // Disponível de imediato (sem backoff) e reaquirível.
        var reclaim = await _queue.TryClaimNextAsync();
        reclaim.Should().NotBeNull();
        reclaim!.Attempts.Should().Be(1);
    }

    [Fact]   // todas as transições são guardadas por Id + LeaseId + status Processing
    public async Task Transitions_WithWrongLease_AreNoOps()
    {
        var id = await SeedQueuedDocAsync(TenantA);
        await _queue.TryClaimNextAsync();
        var wrong = Guid.NewGuid();

        (await _queue.CompleteAsync(id, wrong)).Should().BeFalse();
        (await _queue.ScheduleRetryAsync(id, wrong)).Should().BeFalse();
        (await _queue.FailAsync(id, wrong, "X")).Should().BeFalse();
        (await _queue.ReleaseAsync(id, wrong)).Should().BeFalse();

        (await LoadAsync(id)).AnalysisStatus.Should().Be(AiAnalysisStatus.Processing, "nenhuma transição alheia pegou");
    }

    [Fact]   // opções inválidas fazem a fila FALHAR claramente na composição
    public void Constructor_WithInvalidOptions_Throws()
    {
        var bad = new DocumentAnalysisQueueOptions { LeaseSeconds = -1 };
        var act = () => NewQueue(bad);
        act.Should().Throw<InvalidOperationException>().WithMessage("*LeaseSeconds*");
    }

    // ---- infraestrutura do teste -----------------------------------------------------

    private DurableDocumentAnalysisQueue NewQueue(DocumentAnalysisQueueOptions opt) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _clock, Options.Create(opt),
            NullLogger<DurableDocumentAnalysisQueue>.Instance);

    private AegisScoreDbContext NewContext(Guid? tenant) => new(_dbOptions, new SystemTenantContext(tenant));

    private async Task<GovernanceDocument> LoadAsync(Guid id)
    {
        await using var db = NewContext(null);
        return await db.GovernanceDocuments.IgnoreQueryFilters().FirstAsync(d => d.Id == id);
    }

    private async Task<Guid> SeedQueuedDocAsync(Guid tenant)
    {
        await using var db = NewContext(tenant);
        var doc = new GovernanceDocument
        {
            Title = "Política", Type = GovernanceDocumentType.Politica, Source = DocumentSource.Integracao,
            FileName = "p.pdf", ContentType = "application/pdf", StorageUri = "file://p.pdf",
            AnalysisStatus = AiAnalysisStatus.Queued, AnalysisQueuedAt = _clock.GetUtcNow(),
        };
        db.GovernanceDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }
}
