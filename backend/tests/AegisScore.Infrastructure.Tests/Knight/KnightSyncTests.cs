using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api;
using AegisScore.Api.Workers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Sincronização do KNIGHT iniciada em Integrações: pedido DURÁVEL e identificado,
/// um pedido ativo por conector (duplo clique não coleta duas vezes), isolamento por tenant na leitura,
/// transições guardadas pelo lease, interrupção declarada depois das tentativas e o worker executando a
/// autoridade de avaliação SOB O TENANT DONO do pedido, com o desfecho de cada caminho registrado.
/// </summary>
public sealed class KnightSyncTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-9999-9999-9999-999999999999");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-9999-9999-9999-999999999999");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public KnightSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AegisScoreDbContext(_options, new SystemTenantContext(null));
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Pedido_EhIdempotentePorConector_EIsoladoPorTenant()
    {
        var conectorA = await SeedAsync(TenantA);
        await SeedAsync(TenantB);

        await using var dbA = Ctx(TenantA);
        var reqA = Requests(dbA, TenantA);
        var primeiro = await reqA.EnqueueAsync(conectorA, KnightSourceType.MicrosoftEntraId, null);
        var segundo = await reqA.EnqueueAsync(conectorA, KnightSourceType.MicrosoftEntraId, null);

        primeiro.AlreadyActive.Should().BeFalse();
        segundo.AlreadyActive.Should().BeTrue("um segundo clique devolve o MESMO pedido, sem nova coleta");
        segundo.Request.Id.Should().Be(primeiro.Request.Id);
        (await reqA.ActiveConnectorIdsAsync()).Should().Contain(conectorA);

        await using var dbB = Ctx(TenantB);
        var reqB = Requests(dbB, TenantB);
        (await reqB.GetAsync(conectorA, primeiro.Request.Id)).Should().BeNull("o pedido de outro tenant é indistinguível de inexistente");
        (await reqB.GetLatestAsync(conectorA)).Should().BeNull();
        (await reqB.ActiveConnectorIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Lease_GuardaAsTransicoes_EInterrupcaoRepetidaTerminaFalha()
    {
        var conector = await SeedAsync(TenantA);
        await using var db = Ctx(TenantA);
        var pedido = (await Requests(db, TenantA).EnqueueAsync(conector, KnightSourceType.MicrosoftEntraId, null)).Request;

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var queue = Queue(clock, leaseSeconds: 30);

        var lease = await queue.TryClaimNextAsync();
        lease.Should().NotBeNull();
        lease!.TenantId.Should().Be(TenantA);
        lease.ConnectorId.Should().Be(conector);
        lease.Source.Should().Be(KnightSourceType.MicrosoftEntraId);
        (await queue.TryClaimNextAsync()).Should().BeNull("com lease vigente ninguém mais adquire o pedido");

        (await queue.CompleteAsync(pedido.Id, Guid.NewGuid(), Guid.NewGuid(), KnightSourceState.Completed, "x"))
            .Should().BeFalse("quem não detém o lease não grava o desfecho");

        // O worker morreu no meio: o lease vence e o pedido volta a ser adquirível, com a tentativa contada.
        clock.Advance(TimeSpan.FromSeconds(31));
        var segunda = await queue.TryClaimNextAsync();
        segunda!.Attempts.Should().Be(2);

        // Acima do limite, o worker registra INTERRUPÇÃO — nunca espera infinita.
        clock.Advance(TimeSpan.FromSeconds(31));
        var terceira = await queue.TryClaimNextAsync();
        terceira!.Attempts.Should().Be(3);
        await Worker(queue, new FakeAssessments(_ => throw new InvalidOperationException("não deveria executar")))
            .ProcessAsync(terceira, CancellationToken.None);

        var fim = await Requests(Ctx(TenantA), TenantA).GetAsync(conector, pedido.Id);
        fim!.Status.Should().Be(KnightSyncStatus.Failed);
        fim.FailureCategory.Should().Be("Interrupted");
        fim.RunId.Should().BeNull();
    }

    [Theory]
    [InlineData("ok", KnightSyncStatus.Completed, null)]
    [InlineData("naoConfigurado", KnightSyncStatus.Failed, "ConnectorNotConfigured")]
    [InlineData("erro", KnightSyncStatus.Failed, "CollectionError")]
    public async Task Worker_ExecutaSobOTenantDoPedido_ERegistraODesfecho(string caso, KnightSyncStatus esperado, string? categoria)
    {
        var conector = await SeedAsync(TenantA);
        await SeedAsync(TenantB);
        await using var db = Ctx(TenantA);
        var pedido = (await Requests(db, TenantA).EnqueueAsync(conector, KnightSourceType.MicrosoftEntraId, null)).Request;

        var queue = Queue(new ManualClock(DateTimeOffset.UtcNow), leaseSeconds: 60);
        var runId = Guid.NewGuid();
        Guid? tenantVisto = null;
        var fake = new FakeAssessments(tenant =>
        {
            tenantVisto = tenant;
            return caso switch
            {
                "ok" => Assessment(runId, KnightSourceState.PartialCollection),
                "naoConfigurado" => throw new KnightSourceNotConfiguredException(KnightSourceType.MicrosoftEntraId),
                _ => throw new InvalidOperationException("Graph caiu no meio"),
            };
        });

        var lease = await queue.TryClaimNextAsync();
        await Worker(queue, fake).ProcessAsync(lease!, CancellationToken.None);

        tenantVisto.Should().Be(TenantA, "o serviço resolvido no escopo do worker enxerga o tenant DONO do pedido");
        var fim = await Requests(Ctx(TenantA), TenantA).GetAsync(conector, pedido.Id);
        fim!.Status.Should().Be(esperado);
        fim.FailureCategory.Should().Be(categoria);
        if (caso == "ok")
        {
            fim.RunId.Should().Be(runId);
            fim.ResultSourceState.Should().Be(KnightSourceState.PartialCollection);
            fim.Message.Should().Contain("parcial");
        }
        else
        {
            fim.RunId.Should().BeNull("uma tentativa que falhou não produz avaliação nova");
            fim.Message.Should().NotBeNullOrWhiteSpace();
        }

        (await Requests(Ctx(TenantA), TenantA).ActiveConnectorIdsAsync()).Should().BeEmpty("o pedido saiu do estado ativo");
    }

    // ---- Infraestrutura -----------------------------------------------------------------------------

    private AegisScoreDbContext Ctx(Guid? tenant) => new(_options, new SystemTenantContext(tenant));

    private static KnightSyncRequests Requests(AegisScoreDbContext db, Guid tenant) =>
        new(db, new SystemTenantContext(tenant), TimeProvider.System);

    private async Task<Guid> SeedAsync(Guid tenantId)
    {
        await using (var db = Ctx(null))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = Ctx(tenantId);
        var c = new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Entra", Enabled = true, EncryptedSettings = "cifrado",
        };
        dbt.Connectors.Add(c);
        await dbt.SaveChangesAsync();
        return c.Id;
    }

    private ServiceProvider Provider(IKnightSyncQueue queue, IAegisKnightAssessmentService assessments)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<TenantScopeOverride>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddScoped(sp => new CapturingService(assessments, sp.GetRequiredService<ITenantContext>()));
        services.AddScoped<IAegisKnightAssessmentService>(sp => sp.GetRequiredService<CapturingService>());
        services.AddSingleton(queue);
        return services.BuildServiceProvider();
    }

    private DurableKnightSyncQueue Queue(TimeProvider clock, int leaseSeconds)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_options);
        var sp = services.BuildServiceProvider();
        return new DurableKnightSyncQueue(sp.GetRequiredService<IServiceScopeFactory>(), clock,
            Options.Create(new KnightSyncOptions { LeaseSeconds = leaseSeconds, HeartbeatSeconds = 5, PollSeconds = 1, MaxAttempts = 2 }));
    }

    private KnightSyncWorker Worker(IKnightSyncQueue queue, FakeAssessments fake) =>
        new(Provider(queue, fake).GetRequiredService<IServiceScopeFactory>(), queue,
            Options.Create(new KnightSyncOptions { LeaseSeconds = 60, HeartbeatSeconds = 5, PollSeconds = 1, MaxAttempts = 2 }),
            NullLogger<KnightSyncWorker>.Instance);

    private static KnightAssessment Assessment(Guid id, KnightSourceState state) => new(
        id, KnightAssessmentMode.Live, KnightSourceType.MicrosoftEntraId, state, "Microsoft Entra ID", KnightRunStatus.Completed,
        KnightCatalog.Version, KnightScoreFormula.Version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 70, 80,
        1, 1, 0, 0, 0, 0, Array.Empty<KnightIndicatorView>(), Array.Empty<KnightCapabilityStatus>(), null, false);

    /// <summary>Delegado de avaliação que recebe o tenant que o escopo do worker resolveu.</summary>
    private sealed class FakeAssessments : IAegisKnightAssessmentService
    {
        private readonly Func<Guid?, KnightAssessment> _run;
        public FakeAssessments(Func<Guid?, KnightAssessment> run) => _run = run;
        public Guid? Tenant { get; set; }
        public Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default) => Task.FromResult(_run(Tenant));
        public Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAffectedObjectsPage?> GetAffectedObjectsAsync(Guid runId, string indicatorId, int page, int pageSize, string? search,
            CancellationToken ct = default, KnightObjectRelation relation = KnightObjectRelation.Affected) => throw new NotSupportedException();
        public Task<KnightAffectedSummary?> GetAffectedSummaryAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Resolvido no escopo do worker: repassa ao fake o tenant que o ITenantContext daquele escopo devolve.</summary>
    private sealed class CapturingService : IAegisKnightAssessmentService
    {
        private readonly FakeAssessments _inner;
        private readonly ITenantContext _tenant;
        public CapturingService(IAegisKnightAssessmentService inner, ITenantContext tenant) { _inner = (FakeAssessments)inner; _tenant = tenant; }
        public Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default) => _inner.RunDemoAssessmentAsync(ct);
        public Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default)
        {
            _inner.Tenant = _tenant.TenantId;
            return _inner.RunAssessmentAsync(source, ct);
        }
        public Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default) => _inner.GetLatestAsync(ct);
        public Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default) => _inner.GetSourcesStatusAsync(ct);
        public Task<KnightAffectedObjectsPage?> GetAffectedObjectsAsync(Guid runId, string indicatorId, int page, int pageSize, string? search,
            CancellationToken ct = default, KnightObjectRelation relation = KnightObjectRelation.Affected) => _inner.GetAffectedObjectsAsync(runId, indicatorId, page, pageSize, search, ct, relation);
        public Task<KnightAffectedSummary?> GetAffectedSummaryAsync(Guid runId, CancellationToken ct = default) => _inner.GetAffectedSummaryAsync(runId, ct);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
