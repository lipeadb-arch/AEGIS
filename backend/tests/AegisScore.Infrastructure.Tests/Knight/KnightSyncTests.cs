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
///
/// Revisão corretiva — vínculo DURÁVEL entre pedido e resultado. Antes, a avaliação era gravada e só DEPOIS o
/// pedido recebia o RunId; nessa janela: (1) queda do processo → a retomada coletava de novo e gravava uma
/// SEGUNDA avaliação; (2) falha ao finalizar → o pedido virava "falhou — nenhuma avaliação nova foi registrada"
/// com a avaliação gravada; (3) worker que perdeu o lease → gravava um resultado órfão, que passava a ser a
/// "última avaliação". Agora a execução e o vínculo são gravados juntos, guardados pelo lease, e as retomadas
/// usam o vínculo — com o serviço REAL de avaliação sobre o Graph simulado.
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

    // ---- Vínculo durável entre pedido e resultado (serviço REAL) ------------------------------------

    [Fact]
    public async Task AvaliacaoGravadaSemFinalizarOPedido_RetomadaFinalizaComElaSemColetarDeNovo()
    {
        var (queue, clock, pedido) = await PendingRequestAsync();
        var graph = new KnightGraphScenario.CountingHandler();

        // Tentativa 1: a avaliação é gravada e vinculada; o processo cai antes de finalizar o pedido.
        var lease1 = await queue.TryClaimNextAsync();
        KnightSyncRunResult primeira;
        await using (var db = Ctx(TenantA))
            primeira = await RealService(db, graph).RunForSyncRequestAsync(
                KnightSourceType.MicrosoftEntraId, new KnightSyncBinding(pedido, lease1!.LeaseId));
        primeira.Outcome.Should().Be(KnightSyncRunOutcome.Registered);
        var run = primeira.Assessment!.Id;

        var meio = await Request(pedido);
        meio.Status.Should().Be(KnightSyncStatus.Running, "o pedido ainda não foi finalizado");
        meio.RunId.Should().Be(run, "o vínculo nasceu na MESMA transação que gravou a avaliação");
        var chamadas = graph.Calls;

        // A tentativa 2 também cai sem fazer nada; a 3 já passou do limite — e mesmo assim o desfecho é o resultado.
        clock.Advance(TimeSpan.FromSeconds(31));
        (await queue.TryClaimNextAsync())!.Attempts.Should().Be(2);
        clock.Advance(TimeSpan.FromSeconds(31));
        var lease3 = await queue.TryClaimNextAsync();
        lease3!.Attempts.Should().Be(3);
        await RealWorker(queue, graph).ProcessAsync(lease3, CancellationToken.None);

        var fim = await Request(pedido);
        fim.Status.Should().Be(KnightSyncStatus.Completed, "a avaliação vinculada é o desfecho, não uma interrupção");
        fim.RunId.Should().Be(run);
        fim.Message.Should().Contain("tentativa anterior").And.Contain("não foi repetida");
        (await Runs()).Should().Be(1, "a retomada não produz segundo resultado");
        graph.Calls.Should().Be(chamadas, "a retomada não coleta de novo");
    }

    [Fact]
    public async Task FalhaAoFinalizarOPedido_NaoAfirmaQueNadaFoiRegistrado_ERetomadaConclui()
    {
        var (queue, clock, pedido) = await PendingRequestAsync();
        var graph = new KnightGraphScenario.CountingHandler();
        var flaky = new FlakyCompleteQueue(queue);

        var lease1 = await flaky.TryClaimNextAsync();
        await RealWorker(flaky, graph).ProcessAsync(lease1!, CancellationToken.None);

        var meio = await Request(pedido);
        meio.Status.Should().NotBe(KnightSyncStatus.Failed, "há avaliação gravada para este pedido");
        meio.Message.Should().BeNull("nenhuma mensagem de falha foi afirmada");
        meio.RunId.Should().NotBeNull();
        (await Runs()).Should().Be(1);
        var chamadas = graph.Calls;

        clock.Advance(TimeSpan.FromSeconds(31));
        await RealWorker(queue, graph).ProcessAsync((await queue.TryClaimNextAsync())!, CancellationToken.None);

        var fim = await Request(pedido);
        fim.Status.Should().Be(KnightSyncStatus.Completed);
        fim.RunId.Should().Be(meio.RunId);
        (await Runs()).Should().Be(1);
        graph.Calls.Should().Be(chamadas);
    }

    [Fact]
    public async Task LeaseAssumidoPorOutroWorker_NaoProduzSegundoResultado()
    {
        var (queue, clock, pedido) = await PendingRequestAsync();
        var graph = new KnightGraphScenario.CountingHandler();

        var leaseA = await queue.TryClaimNextAsync();
        clock.Advance(TimeSpan.FromSeconds(31));                   // A deixou o lease vencer
        var leaseB = await queue.TryClaimNextAsync();
        await RealWorker(queue, graph).ProcessAsync(leaseB!, CancellationToken.None);
        var chamadas = graph.Calls;

        await RealWorker(queue, graph).ProcessAsync(leaseA!, CancellationToken.None);  // A volta com o lease vencido

        var fim = await Request(pedido);
        fim.Status.Should().Be(KnightSyncStatus.Completed);
        (await Runs()).Should().Be(1, "um pedido, um resultado aceito");
        graph.Calls.Should().Be(chamadas, "quem perdeu o lease não coleta para um pedido que já tem resultado");
        await using var db = Ctx(TenantA);
        (await RealService(db, graph).GetLatestAsync()).Assessment!.Id.Should().Be(fim.RunId!.Value,
            "a última avaliação é a do pedido — não um resultado órfão");
    }

    [Fact]
    public async Task WorkerQuePerdeOLeaseNoMeioDaColeta_DesfazAPropriaGravacao()
    {
        var (queue, clock, pedido) = await PendingRequestAsync();
        var graphA = new KnightGraphScenario.CountingHandler();
        graphA.HoldNextPolicyRead();

        // A adquire, confere o pedido e fica parado no meio da coleta.
        var leaseA = await queue.TryClaimNextAsync();
        var dbA = Ctx(TenantA);
        var tentativaA = RealService(dbA, graphA).RunForSyncRequestAsync(
            KnightSourceType.MicrosoftEntraId, new KnightSyncBinding(pedido, leaseA!.LeaseId));
        await graphA.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Enquanto isso o lease vence, B assume e conclui.
        clock.Advance(TimeSpan.FromSeconds(31));
        await RealWorker(queue, new KnightGraphScenario.CountingHandler()).ProcessAsync((await queue.TryClaimNextAsync())!, CancellationToken.None);
        var concluido = await Request(pedido);
        concluido.Status.Should().Be(KnightSyncStatus.Completed);

        // A termina a coleta: a gravação dela é desfeita, e o resultado aceito continua sendo o de B.
        graphA.Release();
        var resultadoA = await tentativaA;
        await dbA.DisposeAsync();
        resultadoA.Outcome.Should().Be(KnightSyncRunOutcome.AlreadyRegistered);
        resultadoA.Assessment!.Id.Should().Be(concluido.RunId!.Value);
        (await Runs()).Should().Be(1, "a execução de A não sobreviveu à transação guardada pelo lease");
        (await Request(pedido)).RunId.Should().Be(concluido.RunId);
    }

    [Fact]
    public async Task Fila_RespeitaOVinculo_NaoFalhaNemConcluiComOutraAvaliacao()
    {
        var (queue, _, pedido) = await PendingRequestAsync();
        var lease = await queue.TryClaimNextAsync();
        Guid run;
        await using (var db = Ctx(TenantA))
            run = (await RealService(db, new KnightGraphScenario.CountingHandler()).RunForSyncRequestAsync(
                KnightSourceType.MicrosoftEntraId, new KnightSyncBinding(pedido, lease!.LeaseId))).Assessment!.Id;

        (await queue.GetLinkedResultAsync(pedido)).Should().Be(new KnightSyncLinkedResult(run, KnightSourceState.PartialCollection),
            "o estado vem da própria execução vinculada (as capacidades de risco devolvem 403 no cenário)");
        (await queue.FailAsync(pedido, lease.LeaseId, "CollectionError", "Nenhuma avaliação nova foi registrada."))
            .Should().BeFalse("um pedido com avaliação vinculada nunca vira falha");
        (await queue.CompleteAsync(pedido, lease.LeaseId, Guid.NewGuid(), KnightSourceState.Completed, "x"))
            .Should().BeFalse("não se conclui com uma avaliação diferente da vinculada");
        (await Request(pedido)).Status.Should().Be(KnightSyncStatus.Running);
        (await queue.CompleteAsync(pedido, lease.LeaseId, run, KnightSourceState.PartialCollection, "ok")).Should().BeTrue();
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

    private async Task<(DurableKnightSyncQueue Queue, ManualClock Clock, Guid Request)> PendingRequestAsync()
    {
        var conector = await SeedAsync(TenantA);
        await using var db = Ctx(TenantA);
        var pedido = (await Requests(db, TenantA).EnqueueAsync(conector, KnightSourceType.MicrosoftEntraId, null)).Request.Id;
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        return (Queue(clock, leaseSeconds: 30), clock, pedido);
    }

    private async Task<KnightSyncRequest> Request(Guid id)
    {
        await using var db = Ctx(TenantA);
        return await db.KnightSyncRequests.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<int> Runs()
    {
        await using var db = Ctx(TenantA);
        return await db.KnightAssessmentRuns.CountAsync();
    }

    private static IAegisKnightAssessmentService RealService(AegisScoreDbContext db, KnightGraphScenario.CountingHandler graph) =>
        KnightMulticloudReportTests.ServiceFor(db, TenantA, graph);

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

    private static IOptions<KnightSyncOptions> WorkerOptions() =>
        Options.Create(new KnightSyncOptions { LeaseSeconds = 60, HeartbeatSeconds = 5, PollSeconds = 1, MaxAttempts = 2 });

    private KnightSyncWorker Worker(IKnightSyncQueue queue, FakeAssessments fake) =>
        new(Provider(queue, fake).GetRequiredService<IServiceScopeFactory>(), queue, WorkerOptions(),
            NullLogger<KnightSyncWorker>.Instance);

    /// <summary>Worker com o serviço de avaliação REAL, resolvido no escopo sob o tenant do pedido.</summary>
    private KnightSyncWorker RealWorker(IKnightSyncQueue queue, KnightGraphScenario.CountingHandler graph)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_options);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<TenantScopeOverride>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddScoped(sp => new AegisScoreDbContext(_options, new SystemTenantContext(sp.GetRequiredService<ITenantContext>().TenantId)));
        services.AddScoped(sp => KnightMulticloudReportTests.ServiceFor(
            sp.GetRequiredService<AegisScoreDbContext>(), sp.GetRequiredService<ITenantContext>().TenantId!.Value, graph));
        services.AddSingleton(queue);
        return new KnightSyncWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), queue,
            WorkerOptions(), NullLogger<KnightSyncWorker>.Instance);
    }

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
        public Task<KnightSyncRunResult> RunForSyncRequestAsync(KnightSourceType source, KnightSyncBinding binding, CancellationToken ct = default) =>
            Task.FromResult(new KnightSyncRunResult(KnightSyncRunOutcome.Registered, _run(Tenant)));
        public Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightLatestBySource> GetLatestBySourceAsync(CancellationToken ct = default) => throw new NotSupportedException();
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
        public Task<KnightSyncRunResult> RunForSyncRequestAsync(KnightSourceType source, KnightSyncBinding binding, CancellationToken ct = default)
        {
            _inner.Tenant = _tenant.TenantId;
            return _inner.RunForSyncRequestAsync(source, binding, ct);
        }
        public Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default) => _inner.GetLatestAsync(ct);
        public Task<KnightLatestBySource> GetLatestBySourceAsync(CancellationToken ct = default) => _inner.GetLatestBySourceAsync(ct);
        public Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default) => _inner.GetSourcesStatusAsync(ct);
        public Task<KnightAffectedObjectsPage?> GetAffectedObjectsAsync(Guid runId, string indicatorId, int page, int pageSize, string? search,
            CancellationToken ct = default, KnightObjectRelation relation = KnightObjectRelation.Affected) => _inner.GetAffectedObjectsAsync(runId, indicatorId, page, pageSize, search, ct, relation);
        public Task<KnightAffectedSummary?> GetAffectedSummaryAsync(Guid runId, CancellationToken ct = default) => _inner.GetAffectedSummaryAsync(runId, ct);
    }

    /// <summary>Fila real cuja finalização do pedido falha (banco indisponível naquele instante).</summary>
    private sealed class FlakyCompleteQueue : IKnightSyncQueue
    {
        private readonly IKnightSyncQueue _inner;
        public FlakyCompleteQueue(IKnightSyncQueue inner) => _inner = inner;
        public Task<KnightSyncLease?> TryClaimNextAsync(CancellationToken ct = default) => _inner.TryClaimNextAsync(ct);
        public Task<bool> RenewAsync(Guid r, Guid l, CancellationToken ct = default) => _inner.RenewAsync(r, l, ct);
        public Task<bool> CompleteAsync(Guid r, Guid l, Guid run, KnightSourceState s, string m, CancellationToken ct = default) =>
            throw new InvalidOperationException("conexão perdida ao finalizar o pedido");
        public Task<bool> FailAsync(Guid r, Guid l, string c, string m, CancellationToken ct = default) => _inner.FailAsync(r, l, c, m, ct);
        public Task<bool> ReleaseAsync(Guid r, Guid l, CancellationToken ct = default) => _inner.ReleaseAsync(r, l, ct);
        public Task<KnightSyncLinkedResult?> GetLinkedResultAsync(Guid r, CancellationToken ct = default) => _inner.GetLinkedResultAsync(r, ct);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
