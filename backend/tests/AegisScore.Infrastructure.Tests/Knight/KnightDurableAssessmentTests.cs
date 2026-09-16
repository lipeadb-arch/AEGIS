using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-DURABLE-01] A conclusão de uma avaliação KNIGHT não pode depender da narrativa da IA.
///
/// O defeito que estes testes reproduzem: o veredito determinístico era gravado como <c>Running</c>, a
/// narrativa consultiva era pedida à IA com o MESMO token da requisição e só depois a execução virava
/// <c>Completed</c>. Um cancelamento nessa janela — o navegador abortando a requisição no seu próprio
/// tempo limite, por exemplo — propagava a exceção e deixava o registro em <c>Running</c> para sempre:
/// nenhum caminho grava <c>Failed</c> e não existe rotina que feche execução órfã.
///
/// O que fica coberto aqui:
///   (1) cancelamento DURANTE a narrativa → a avaliação já nasce concluída e durável;
///   (2) narrativa bem-sucedida → procedência da IA registrada, sem alterar veredito;
///   (3) falha da IA → fallback determinístico, avaliação concluída (falha consultiva ≠ falha do veredito);
///   (4) cancelamento ANTES da persistência → nada é gravado (não há meia avaliação);
///   (5) falha ao SALVAR o enriquecimento → o resultado já confirmado é preservado, in memoriam e no banco;
///   (6) citação da aquisição ADM preservada mesmo com a IA cancelada;
///   (7) leitura da última avaliação: uma execução não concluída nunca é apresentada como resultado, e
///       tampouco é escondida atrás de uma avaliação antiga apresentada como se fosse a atual.
/// </summary>
public sealed class KnightDurableAssessmentTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-aaaa-4aaa-8aaa-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-bbbb-4bbb-8bbb-222222222222");

    private const string DiretorioA = "00000000-0000-0000-0000-00000000dead";
    private const string Secret = "segredo-sintetico-de-teste";

    private const string NarrativaValida =
        """{"executiveSummary":"narrativa da IA","priorityRisks":[{"title":"R","rationale":"x","indicatorIds":["AK-ENTRA-001"]}],"recommendedActions":[],"correlations":[],"collectionGaps":[]}""";

    private readonly SqliteConnection _connection;

    public KnightDurableAssessmentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- (1) REPRODUÇÃO: cancelamento durante a narrativa ------------------------------------------

    /// <summary>
    /// O caso do HOMOLOG-01. A coleta e a avaliação determinística terminam; a IA é chamada e, nesse
    /// instante, o chamador desiste (o navegador corta a requisição). ANTES da correção esta chamada
    /// lançava <see cref="OperationCanceledException"/> e deixava o registro em <c>Running</c>, sem
    /// <c>CompletedAt</c> e sem narrativa — exatamente as três linhas encontradas no ambiente real.
    /// DEPOIS da correção o veredito já está concluído e gravado quando a IA é chamada, então o
    /// cancelamento apenas descarta o ENRIQUECIMENTO.
    /// </summary>
    [Fact]
    public async Task Cancelamento_DuranteANarrativa_NaoDeixaExecucaoEmRunning()
    {
        using var cts = new CancellationTokenSource();

        KnightAssessment avaliacao;
        await using (var db = NewContext(TenantA))
            avaliacao = await ServicoDemo(db, new IaQueCancelaOChamador(cts))
                .RunDemoAssessmentAsync(cts.Token);

        avaliacao.Status.Should().Be(KnightRunStatus.Completed,
            "o veredito determinístico é a avaliação; a narrativa da IA é enriquecimento opcional");
        avaliacao.CompletedAt.Should().NotBeNull();
        avaliacao.Score.Should().Be(23d, "o cancelamento da IA não mexe no score determinístico");
        avaliacao.AdvisoryFromAi.Should().BeFalse();
        avaliacao.Advisory.Should().NotBeNull("o fallback determinístico entra ANTES de depender da IA");

        await using var conferencia = NewContext(TenantA);
        var run = await conferencia.KnightAssessmentRuns.AsNoTracking()
            .Include(r => r.Indicators).SingleAsync();

        run.Status.Should().Be(KnightRunStatus.Completed, "nenhum registro pode ficar abandonado em Running");
        run.CompletedAt.Should().NotBeNull();
        run.AdvisoryJson.Should().NotBeNullOrWhiteSpace();
        run.AdvisoryFromAi.Should().BeFalse();
        run.Indicators.Should().HaveCount(5);
        run.ExposedCount.Should().Be(3);
    }

    /// <summary>
    /// As EVIDÊNCIAS congeladas da execução sobrevivem ao cancelamento junto com o veredito: os objetos
    /// afetados foram gravados na mesma escrita que concluiu a avaliação, não numa segunda passagem que o
    /// cancelamento pudesse ter cortado pela metade.
    /// </summary>
    [Fact]
    public async Task Cancelamento_DuranteANarrativa_PreservaObjetosAfetadosCongelados()
    {
        using var cts = new CancellationTokenSource();

        KnightAssessment avaliacao;
        await using (var db = NewContext(TenantA))
            avaliacao = await ServicoDemo(db, new IaQueCancelaOChamador(cts))
                .RunDemoAssessmentAsync(cts.Token);

        await using var conferencia = NewContext(TenantA);
        foreach (var indicatorId in KnightAffectedObjectScope.Indicators)
        {
            var indicador = avaliacao.Indicators.Single(i => i.IndicatorId == indicatorId);
            if (indicador.AffectedObjectCount == 0) continue;

            indicador.HasAffectedDetail.Should().BeTrue();
            var pagina = await ServicoDemo(conferencia, new IaQueCancelaOChamador(cts))
                .GetAffectedObjectsAsync(avaliacao.Id, indicatorId, 1, 50, null);

            pagina.Should().NotBeNull();
            pagina!.State.Should().Be(KnightAffectedDetailState.Available);
            pagina.TotalPreserved.Should().Be(indicador.AffectedObjectCount);
        }
    }

    /// <summary>Isolamento por tenant intacto: a avaliação salva sob cancelamento não vaza para outro tenant.</summary>
    [Fact]
    public async Task Cancelamento_DuranteANarrativa_NaoQuebraOIsolamentoPorTenant()
    {
        using var cts = new CancellationTokenSource();

        Guid runId;
        await using (var db = NewContext(TenantA))
            runId = (await ServicoDemo(db, new IaQueCancelaOChamador(cts)).RunDemoAssessmentAsync(cts.Token)).Id;

        await using var outro = NewContext(TenantB);
        (await ServicoDemo(outro, new IaFixa(NarrativaValida)).GetByIdAsync(runId)).Should().BeNull();
    }

    // ---- (2) Narrativa bem-sucedida ----------------------------------------------------------------

    [Fact]
    public async Task NarrativaDaIa_BemSucedida_EnriqueceSemAlterarOVeredito()
    {
        KnightAssessment avaliacao;
        await using (var db = NewContext(TenantA))
            avaliacao = await ServicoDemo(db, new IaFixa(NarrativaValida)).RunDemoAssessmentAsync();

        avaliacao.Status.Should().Be(KnightRunStatus.Completed);
        avaliacao.AdvisoryFromAi.Should().BeTrue();
        avaliacao.Advisory!.ExecutiveSummary.Should().Be("narrativa da IA");
        avaliacao.Score.Should().Be(23d, "a IA não decide score");
        avaliacao.ExposedCount.Should().Be(3);

        await using var conferencia = NewContext(TenantA);
        var run = await conferencia.KnightAssessmentRuns.AsNoTracking().SingleAsync();
        run.AdvisoryFromAi.Should().BeTrue("o enriquecimento é GRAVADO, não só devolvido");
        run.AdvisoryJson.Should().Contain("narrativa da IA");
        run.Status.Should().Be(KnightRunStatus.Completed);
    }

    // ---- (3) Falha da IA ---------------------------------------------------------------------------

    /// <summary>
    /// Falha consultiva não vira falha da avaliação determinística: a execução conclui com o fallback e o
    /// veredito intacto — e isso é DECLARADO (<c>AdvisoryFromAi=false</c>), não escondido.
    /// </summary>
    [Fact]
    public async Task FalhaDaIa_DuranteANarrativa_ConcluiComFallbackDeterministico()
    {
        KnightAssessment avaliacao;
        await using (var db = NewContext(TenantA))
            avaliacao = await ServicoDemo(db, new IaQueFalha()).RunDemoAssessmentAsync();

        avaliacao.Status.Should().Be(KnightRunStatus.Completed);
        avaliacao.AdvisoryFromAi.Should().BeFalse();
        avaliacao.Advisory.Should().NotBeNull();
        avaliacao.Score.Should().Be(23d);

        await using var conferencia = NewContext(TenantA);
        var run = await conferencia.KnightAssessmentRuns.AsNoTracking().SingleAsync();
        run.Status.Should().Be(KnightRunStatus.Completed);
        run.CompletedAt.Should().NotBeNull();
        run.AdvisoryJson.Should().NotBeNullOrWhiteSpace();
    }

    // ---- (4) Cancelamento ANTES da persistência ----------------------------------------------------

    /// <summary>
    /// Desistir durante a COLETA é outra coisa: não existe veredito ainda, então não há o que tornar
    /// durável. O cancelamento propaga e nada é gravado — meia avaliação seria pior que nenhuma.
    /// </summary>
    [Fact]
    public async Task Cancelamento_AntesDaPersistencia_NaoGravaExecucao()
    {
        using var cts = new CancellationTokenSource();

        await using var db = NewContext(TenantA);
        var servico = Servico(
            db, TenantA,
            new IKnightCollector[] { new DemoKnightCollector(), new ColetorQueCancela(cts) },
            new ConfigSintetica(DiretorioA),
            new IaFixa(NarrativaValida));

        var act = () => servico.RunAssessmentAsync(KnightSourceType.GoogleWorkspace, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await db.KnightAssessmentRuns.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    // ---- (5) Falha ao salvar o ENRIQUECIMENTO ------------------------------------------------------

    /// <summary>
    /// A gravação do veredito passou; a gravação do enriquecimento falha. O resultado ANTERIORMENTE
    /// CONFIRMADO tem de sobreviver — no banco e no que é devolvido ao chamador. Devolver a narrativa da
    /// IA aqui seria mostrar como salva uma narrativa que não foi gravada.
    /// </summary>
    [Fact]
    public async Task FalhaAoSalvarOEnriquecimento_PreservaOResultadoJaConfirmado()
    {
        var interceptador = new FalhaNaEnesimaGravacao(2);

        KnightAssessment avaliacao;
        await using (var db = NewContext(TenantA, interceptador))
            avaliacao = await ServicoDemo(db, new IaFixa(NarrativaValida)).RunDemoAssessmentAsync();

        interceptador.Gravacoes.Should().BeGreaterThanOrEqualTo(2, "a 2ª gravação é a do enriquecimento");

        avaliacao.Status.Should().Be(KnightRunStatus.Completed);
        avaliacao.AdvisoryFromAi.Should().BeFalse("a narrativa da IA não chegou ao banco");
        avaliacao.Advisory!.ExecutiveSummary.Should().NotBe("narrativa da IA");
        avaliacao.Score.Should().Be(23d);

        await using var conferencia = NewContext(TenantA);
        var run = await conferencia.KnightAssessmentRuns.AsNoTracking().SingleAsync();
        run.Status.Should().Be(KnightRunStatus.Completed);
        run.AdvisoryFromAi.Should().BeFalse();
        run.AdvisoryJson.Should().NotContain("narrativa da IA");
    }

    // ---- (6) Referência à aquisição do ADM ---------------------------------------------------------

    /// <summary>
    /// No caminho do Entra ID a execução CITA a aquisição do ADM, e essa citação é fixada na mesma
    /// transação da gravação do veredito. Concluir antes da IA não pode afrouxar isso: a execução
    /// cancelada durante a narrativa continua concluída E continua apontando para a aquisição que a
    /// sustenta.
    /// </summary>
    [Fact]
    public async Task Cancelamento_DuranteANarrativa_PreservaACitacaoDaAquisicaoAdm()
    {
        await SemearConectorAsync(TenantA);
        using var cts = new CancellationTokenSource();

        KnightAssessment avaliacao;
        await using (var db = NewContext(TenantA))
        {
            var servico = Servico(
                db, TenantA,
                new IKnightCollector[] { new DemoKnightCollector(), new ColetorEntraSintetico() },
                new ConfigSintetica(DiretorioA),
                new IaQueCancelaOChamador(cts));
            avaliacao = await servico.RunAssessmentAsync(KnightSourceType.MicrosoftEntraId, cts.Token);
        }

        avaliacao.Status.Should().Be(KnightRunStatus.Completed);
        avaliacao.SourceType.Should().Be(KnightSourceType.MicrosoftEntraId);

        await using var conferencia = NewContext(TenantA);
        var run = await conferencia.KnightAssessmentRuns.AsNoTracking().SingleAsync();
        run.Status.Should().Be(KnightRunStatus.Completed);
        run.IdentityAcquisitionId.Should().NotBeNull("a procedência da evidência não se perde no cancelamento");
        (await conferencia.IdentityAcquisitions.AsNoTracking()
            .AnyAsync(a => a.Id == run.IdentityAcquisitionId)).Should().BeTrue();
    }

    // ---- (7) Leitura da ÚLTIMA avaliação -----------------------------------------------------------

    /// <summary>
    /// As três execuções do ambiente real ainda existem em <c>Running</c>. A leitura da "última avaliação"
    /// não pode apresentá-las como resultado concluído — nem pode fingir que não aconteceram, mostrando a
    /// avaliação anterior como se fosse a atual. Devolve a última CONCLUÍDA e DECLARA a tentativa mais
    /// recente que não concluiu.
    /// </summary>
    [Fact]
    public async Task UltimaAvaliacao_ComTentativaRunningMaisRecente_DevolveAConcluidaEDeclaraATentativa()
    {
        Guid concluida;
        await using (var db = NewContext(TenantA))
            concluida = (await ServicoDemo(db, new IaFixa(NarrativaValida)).RunDemoAssessmentAsync()).Id;

        var orfa = await SemearExecucaoAbandonadaAsync(TenantA, DateTimeOffset.UtcNow.AddMinutes(5));

        await using var leitura = NewContext(TenantA);
        var ultima = await ServicoDemo(leitura, new IaFixa(NarrativaValida)).GetLatestAsync();

        ultima.Assessment.Should().NotBeNull();
        ultima.Assessment!.Id.Should().Be(concluida, "o resultado exibido é o último CONCLUÍDO");
        ultima.Assessment.Status.Should().Be(KnightRunStatus.Completed);

        ultima.UnfinishedAttempt.Should().NotBeNull("a tentativa mais recente não pode sumir em silêncio");
        ultima.UnfinishedAttempt!.Id.Should().Be(orfa);
        ultima.UnfinishedAttempt.Status.Should().Be(KnightRunStatus.Running);
    }

    /// <summary>Sem nenhuma execução concluída, a leitura não inventa resultado: declara só a tentativa.</summary>
    [Fact]
    public async Task UltimaAvaliacao_SemNenhumaConcluida_NaoApresentaATentativaComoResultado()
    {
        var orfa = await SemearExecucaoAbandonadaAsync(TenantA, DateTimeOffset.UtcNow);

        await using var leitura = NewContext(TenantA);
        var ultima = await ServicoDemo(leitura, new IaFixa(NarrativaValida)).GetLatestAsync();

        ultima.Assessment.Should().BeNull("uma execução em Running não é um resultado");
        ultima.UnfinishedAttempt.Should().NotBeNull();
        ultima.UnfinishedAttempt!.Id.Should().Be(orfa);
    }

    /// <summary>
    /// Uma execução abandonada ANTERIOR à última concluída é história, não pendência: a tela não fica
    /// avisando para sempre sobre uma tentativa que já foi sucedida por um resultado.
    /// </summary>
    [Fact]
    public async Task UltimaAvaliacao_ComTentativaAnteriorAConcluida_NaoDeclaraPendencia()
    {
        await SemearExecucaoAbandonadaAsync(TenantA, DateTimeOffset.UtcNow.AddMinutes(-10));

        Guid concluida;
        await using (var db = NewContext(TenantA))
            concluida = (await ServicoDemo(db, new IaFixa(NarrativaValida)).RunDemoAssessmentAsync()).Id;

        await using var leitura = NewContext(TenantA);
        var ultima = await ServicoDemo(leitura, new IaFixa(NarrativaValida)).GetLatestAsync();

        ultima.Assessment!.Id.Should().Be(concluida);
        ultima.UnfinishedAttempt.Should().BeNull();
    }

    /// <summary>O acesso por ID continua devolvendo a execução pedida, inclusive uma não concluída legada.</summary>
    [Fact]
    public async Task AcessoPorId_ContinuaAlcancandoAExecucaoNaoConcluida()
    {
        var orfa = await SemearExecucaoAbandonadaAsync(TenantA, DateTimeOffset.UtcNow);

        await using var leitura = NewContext(TenantA);
        var lida = await ServicoDemo(leitura, new IaFixa(NarrativaValida)).GetByIdAsync(orfa);

        lida.Should().NotBeNull();
        lida!.Status.Should().Be(KnightRunStatus.Running, "o estado real é mostrado como é");
        lida.CompletedAt.Should().BeNull();
    }

    /// <summary>A tentativa declarada é a do PRÓPRIO tenant — nunca a de outro.</summary>
    [Fact]
    public async Task UltimaAvaliacao_TentativaDeOutroTenant_NaoAtravessaOIsolamento()
    {
        await SemearExecucaoAbandonadaAsync(TenantB, DateTimeOffset.UtcNow.AddMinutes(5));

        await using (var db = NewContext(TenantA))
            await ServicoDemo(db, new IaFixa(NarrativaValida)).RunDemoAssessmentAsync();

        await using var leitura = NewContext(TenantA);
        var ultima = await ServicoDemo(leitura, new IaFixa(NarrativaValida)).GetLatestAsync();

        ultima.Assessment.Should().NotBeNull();
        ultima.UnfinishedAttempt.Should().BeNull("a tentativa abandonada é do tenant B");
    }

    // ---- Infraestrutura do teste -------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AegisScoreDbContext(options.Options, new SystemTenantContext(tenantId));
    }

    private static IAegisKnightAssessmentService ServicoDemo(AegisScoreDbContext db, ILLMClient ia) =>
        Servico(db, TenantA, new IKnightCollector[] { new DemoKnightCollector() },
            new ConfigSintetica(DiretorioA), ia);

    private static IAegisKnightAssessmentService Servico(
        AegisScoreDbContext db, Guid? tenantId, IEnumerable<IKnightCollector> coletores,
        IKnightSourceConfigurationProvider config, ILLMClient ia)
    {
        var registry = new KnightCollectorRegistry(coletores);
        var tenant = new SystemTenantContext(tenantId);
        var aquisicoes = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, aquisicoes, tenant);
        return new AegisKnightAssessmentService(
            db, registry, config, new KnightAdvisoryGenerator(ia), evidence, aquisicoes, tenant);
    }

    private async Task SemearConectorAsync(Guid tenantId)
    {
        await using var db = NewContext(null);
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId,
            Provider = ConnectorProvider.Microsoft,
            Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft Entra ID · AEGIS KNIGHT",
            Enabled = true,
            EncryptedSettings = "{\"clientSecret\":\"" + Secret + "\"}",
        });
        await dbt.SaveChangesAsync();
    }

    /// <summary>
    /// Semeia uma execução ABANDONADA em <c>Running</c> — a forma exata das três encontradas no ambiente
    /// real: sem <c>CompletedAt</c>, sem narrativa. Nenhum teste as "conserta"; eles só garantem que a
    /// leitura as trate como o que são.
    /// </summary>
    private async Task<Guid> SemearExecucaoAbandonadaAsync(Guid tenantId, DateTimeOffset iniciadaEm)
    {
        await using var db = NewContext(tenantId);
        var run = new KnightAssessmentRun
        {
            TenantId = tenantId,
            Mode = KnightAssessmentMode.Live,
            SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID",
            Status = KnightRunStatus.Running,
            CatalogVersion = KnightCatalog.Version,
            ScoreFormulaVersion = "teste",
            StartedAt = iniciadaEm,
            Coverage = 0,
        };
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    // ---- Dublês ------------------------------------------------------------------------------------

    /// <summary>IA que devolve SEMPRE a mesma resposta bruta.</summary>
    private sealed class IaFixa : ILLMClient
    {
        private readonly string _resposta;
        public IaFixa(string resposta) => _resposta = resposta;
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(_resposta);
    }

    /// <summary>IA indisponível (falha de transporte/serviço).</summary>
    private sealed class IaQueFalha : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new InvalidOperationException("IA indisponível (teste).");
    }

    /// <summary>
    /// O chamador desiste EXATAMENTE durante a narrativa — o navegador cortando a requisição. Sem espera:
    /// o cancelamento acontece na própria chamada à IA, de forma determinística.
    /// </summary>
    private sealed class IaQueCancelaOChamador : ILLMClient
    {
        private readonly CancellationTokenSource _cts;
        public IaQueCancelaOChamador(CancellationTokenSource cts) => _cts = cts;

        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("");
        }
    }

    /// <summary>O chamador desiste durante a COLETA — antes de existir qualquer veredito.</summary>
    private sealed class ColetorQueCancela : IKnightCollector
    {
        private readonly CancellationTokenSource _cts;
        public ColetorQueCancela(CancellationTokenSource cts) => _cts = cts;
        public KnightSourceType Source => KnightSourceType.GoogleWorkspace;

        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<KnightCollectionResult>(null!);
        }
    }

    /// <summary>Coletor sintético do Entra ID: fatos suficientes para a aquisição do ADM ser gravada.</summary>
    private sealed class ColetorEntraSintetico : IKnightCollector
    {
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(new KnightCollectionResult(
                KnightSourceType.MicrosoftEntraId,
                KnightSourceState.Completed,
                "Microsoft Entra ID (sintético)",
                new KnightFactSet(new[]
                {
                    KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 3),
                    KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 1),
                    KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 0),
                }),
                new[] { new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected) },
                DateTimeOffset.UtcNow));
    }

    /// <summary>Configuração resolvida sintética — o <c>AzureTenantId</c> é o namespace do diretório.</summary>
    private sealed class ConfigSintetica : IKnightSourceConfigurationProvider
    {
        private readonly string _namespace;
        public ConfigSintetica(string ns) => _namespace = ns;

        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(source switch
            {
                KnightSourceType.MicrosoftEntraId => new KnightEntraIdConfiguration(_namespace, "client", Secret),
                KnightSourceType.Demo => new KnightDemoConfiguration(),
                KnightSourceType.GoogleWorkspace => new TesteConfigurada(source),
                _ => new KnightSourceNotConfigured(source),
            });

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed record TesteConfigurada(KnightSourceType Src) : KnightSourceConfiguration
    {
        public override KnightSourceType Source => Src;
    }

    /// <summary>
    /// Faz a N-ésima gravação do contexto falhar, de forma determinística — sem rede, sem espera e sem
    /// depender de concorrência. É assim que se prova o que acontece quando o ENRIQUECIMENTO não consegue
    /// ser gravado depois de o veredito já estar confirmado.
    /// </summary>
    private sealed class FalhaNaEnesimaGravacao : SaveChangesInterceptor
    {
        private readonly int _quando;
        public FalhaNaEnesimaGravacao(int quando) => _quando = quando;
        public int Gravacoes { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Gravacoes++;
            if (Gravacoes == _quando)
                throw new DbUpdateException("Falha sintética ao gravar o enriquecimento consultivo.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
