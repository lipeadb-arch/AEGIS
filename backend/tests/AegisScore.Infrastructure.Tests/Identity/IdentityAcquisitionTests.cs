using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Identity;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Identity;

/// <summary>
/// [AEGIS-ADM-01] O ADM por identidade no caminho REAL de aquisição e consumo.
///
/// Estes casos não exercitam tabelas: cada um prova um COMPORTAMENTO que só existe porque a aquisição é
/// persistida e porque a avaliação passou a lê-la. O que está sob teste é a identidade — quando dois objetos
/// são o mesmo, quando não são, e o que continua desconhecido quando a coleta não conseguiu ler.
///
/// LIMITE declarado: a fronteira do Microsoft Graph é substituída por um coletor roteirizado. Uma execução
/// verde aqui não afirma nada sobre a integração com um tenant Microsoft real.
/// </summary>
public sealed class IdentityAcquisitionTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Secret = "segredo-sintetico-que-nunca-e-persistido";
    private const string DiretorioA = "contoso-directory-a";
    private const string DiretorioB = "contoso-directory-b";

    private readonly SqliteConnection _connection;

    public IdentityAcquisitionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- (1) A MESMA identidade em dois conjuntos ---------------------------------------------------

    /// <summary>
    /// O caso que motiva o pacote inteiro: a conta que aparece na lista de privilegiados É a mesma que aparece
    /// na lista de "sem método capaz de MFA registrado". Uma entidade canônica, um vínculo de origem, DUAS
    /// observações — e as duas observações continuam separadas, porque pertencem a populações diferentes.
    /// </summary>
    [Fact]
    public async Task MesmoObjeto_EmDoisConjuntos_UmaEntidade_DuasObservacoes()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ColetarAsync(db, Cenario.Padrao());

        (await db.IdentityEntities.CountAsync()).Should().Be(3,
            "o diretório sintético tem três objetos distintos — e o privilegiado sem MFA não é um quarto");
        (await db.IdentitySourceLinks.CountAsync()).Should().Be(3,
            "cada objeto tem UM vínculo com a origem, ainda que apareça em vários conjuntos");

        var link = await db.IdentitySourceLinks.SingleAsync(l => l.ExternalId == "obj-admin-1");
        var observacoes = await db.IdentityEntityObservations
            .Where(o => o.IdentityEntityId == link.IdentityEntityId).ToListAsync();

        observacoes.Should().HaveCount(2, "o mesmo objeto foi observado em dois conjuntos");
        observacoes.Select(o => o.Set).Should().BeEquivalentTo(new[]
        {
            IdentityObservationSet.PrivilegedRoleMember,
            IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability,
        });
        observacoes.Select(o => o.IdentityEntityId).Distinct().Should().ContainSingle(
            "as duas observações apontam para a MESMA entidade canônica");
    }

    // ---- (2) Renomear não cria outra identidade -----------------------------------------------------

    /// <summary>
    /// Trocar o nome de exibição e o UPN atualiza a PROJEÇÃO e não a identidade. A observação da coleta
    /// anterior continua dizendo o nome de então — é evidência, e evidência não se reescreve.
    /// </summary>
    [Fact]
    public async Task MudancaDeNome_MantemAIdentidade_ESemReescreverAEvidenciaAnterior()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        await ColetarAsync(db, Cenario.Padrao() with { Em = t0 });

        var idAntes = (await db.IdentitySourceLinks.SingleAsync(l => l.ExternalId == "obj-admin-1")).IdentityEntityId;

        await ColetarAsync(db, (Cenario.Padrao() with { Em = t0.AddDays(1) })
            .Renomeando("obj-admin-1", "Ana Sobrenome Novo", "ana.novo@demo.example.com"));

        (await db.IdentityEntities.CountAsync()).Should().Be(3, "renomear NÃO cria uma segunda identidade");

        var entidade = await db.IdentityEntities.SingleAsync(e => e.Id == idAntes);
        entidade.DisplayName.Should().Be("Ana Sobrenome Novo", "o cadastro atual acompanha a coleta mais recente");
        entidade.UserPrincipalName.Should().Be("ana.novo@demo.example.com");

        // Ordenação no cliente: o SQLite não ordena DateTimeOffset no servidor, e o ponto aqui é o CONTEÚDO
        // preservado — não o plano de consulta.
        var todas = await db.IdentityEntityObservations.AsNoTracking()
            .Where(o => o.IdentityEntityId == idAntes && o.Set == IdentityObservationSet.PrivilegedRoleMember)
            .ToListAsync();
        var primeira = todas.OrderBy(o => o.ObservedAt).First();
        primeira.DisplayNameObserved.Should().Be("Ana Silva",
            "a observação preserva o nome COMO FOI OBSERVADO; o presente não reescreve o passado");
    }

    // ---- (3) Homônimos e namespaces distintos --------------------------------------------------------

    /// <summary>
    /// Dois objetos com o MESMO nome de exibição e o MESMO UPN, com identificadores diferentes, são duas
    /// identidades. Nome e UPN não são chave — nunca foram, e é isso que impede fundir pessoas distintas.
    /// </summary>
    [Fact]
    public async Task Homonimos_ComIdentificadoresDistintos_NaoSaoUnificados()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ColetarAsync(db, new Cenario(
            DiretorioA,
            new[]
            {
                Obj("obj-homonimo-1", "Carlos Souza", "carlos.souza@demo.example.com"),
                Obj("obj-homonimo-2", "Carlos Souza", "carlos.souza@demo.example.com"),
            },
            SemMfa: Array.Empty<string>(),
            Convidados: Array.Empty<ObjetoSintetico>()));

        (await db.IdentityEntities.CountAsync()).Should().Be(2,
            "mesmo nome e mesmo UPN em objetos diferentes NÃO os unifica");
        (await db.IdentitySourceLinks.Select(l => l.ExternalId).ToListAsync())
            .Should().BeEquivalentTo(new[] { "obj-homonimo-1", "obj-homonimo-2" });
    }

    /// <summary>
    /// O MESMO identificador em DOIS diretórios são duas identidades. É a proteção contra reconfigurar o
    /// conector para outro diretório e herdar, sem perceber, os vínculos do anterior.
    /// </summary>
    [Fact]
    public async Task MesmoIdentificador_EmNamespacesDistintos_NaoEhUnificado()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var objeto = new[] { Obj("obj-repetido", "Objeto do Diretório", "obj@demo.example.com") };

        await ColetarAsync(db, new Cenario(DiretorioA, objeto, Array.Empty<string>(), Array.Empty<ObjetoSintetico>()));
        await ColetarAsync(db, new Cenario(DiretorioB, objeto, Array.Empty<string>(), Array.Empty<ObjetoSintetico>()));

        (await db.IdentityEntities.CountAsync()).Should().Be(2,
            "trocar a configuração para OUTRO diretório não pode reaproveitar os vínculos do anterior");

        var vinculos = await db.IdentitySourceLinks.Where(l => l.ExternalId == "obj-repetido").ToListAsync();
        vinculos.Should().HaveCount(2);
        vinculos.Select(v => v.DirectoryNamespace).Should().BeEquivalentTo(new[] { DiretorioA, DiretorioB });
        vinculos.Select(v => v.IdentityEntityId).Distinct().Should().HaveCount(2,
            "namespaces distintos apontam para entidades distintas");
    }

    /// <summary>
    /// Um convidado B2B não é fundido com uma identidade de outro diretório por e-mail — o e-mail coincide, o
    /// namespace e o identificador não.
    /// </summary>
    [Fact]
    public async Task ConvidadoB2B_NaoEhFundidoPorEmail_ComIdentidadeDeOutroDiretorio()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ColetarAsync(db, new Cenario(
            DiretorioA,
            new[] { Obj("obj-interno", "Marina Alves", "marina@parceiro.example.com") },
            Array.Empty<string>(),
            Array.Empty<ObjetoSintetico>()));

        await ColetarAsync(db, new Cenario(
            DiretorioB,
            Array.Empty<ObjetoSintetico>(),
            Array.Empty<string>(),
            new[] { Obj("obj-convidado", "Marina Alves", "marina@parceiro.example.com", IdentityEntityKind.Guest) }));

        (await db.IdentityEntities.CountAsync()).Should().Be(2,
            "e-mail igual não é vínculo forte: convidado B2B e conta de outro diretório permanecem separados");
    }

    // ---- (4) Retry × aquisição nova ------------------------------------------------------------------

    /// <summary>
    /// Reprocessar a MESMA aquisição não duplica nada. Duas aquisições DISTINTAS que observaram exatamente o
    /// mesmo conteúdo produzem duas linhas com o mesmo fingerprint — porque observar de novo é um fato novo,
    /// e apagá-lo destruiria a prova de que a coleta aconteceu.
    /// </summary>
    [Fact]
    public async Task Retry_DaMesmaAquisicao_EhIdempotente_EDuasColetasIguais_NaoSaoDeduplicadas()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA));
        var connectorId = await db.Connectors.Select(c => c.Id).SingleAsync();
        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        var mesmaAquisicao = Guid.NewGuid();
        var pedido = Pedido(mesmaAquisicao, connectorId, Cenario.Padrao() with { Em = t0 });

        await store.PrepareAsync(pedido);
        await db.SaveChangesAsync();
        await store.PrepareAsync(pedido);   // RETRY: mesma aquisição, mesmo conteúdo
        await db.SaveChangesAsync();

        (await db.IdentityAcquisitions.CountAsync()).Should().Be(1, "o retry reaproveita o mesmo registro");
        var observacoesAposRetry = await db.IdentityEntityObservations.CountAsync();
        observacoesAposRetry.Should().Be(4, "o retry não duplica observações");

        // Aquisição NOVA com conteúdo idêntico (só o instante muda).
        var outra = Pedido(Guid.NewGuid(), connectorId, Cenario.Padrao() with { Em = t0.AddHours(6) });
        await store.PrepareAsync(outra);
        await db.SaveChangesAsync();

        var aquisicoes = await db.IdentityAcquisitions.AsNoTracking().ToListAsync();
        aquisicoes.Should().HaveCount(2, "duas coletas distintas são dois fatos, mesmo com o mesmo conteúdo");
        aquisicoes.Select(a => a.ContentFingerprint).Distinct().Should().ContainSingle(
            "o fingerprint RECONHECE que o conteúdo observado é o mesmo — e não serve para deduplicar");
        (await db.IdentityEntities.CountAsync()).Should().Be(3, "nenhuma identidade nova foi criada");
        (await db.IdentityEntityObservations.CountAsync()).Should().Be(8,
            "cada aquisição preserva as SUAS observações");
    }

    // ---- (5) Coleta parcial, falha e atrasada --------------------------------------------------------

    /// <summary>
    /// Um conjunto que não pôde ser lido permanece DESCONHECIDO: sem objetos registrados, sem completude e
    /// sem afirmar ausência. Ler zero convidados sinalizados e não conseguir ler os convidados não podem
    /// parecer a mesma coisa.
    /// </summary>
    [Fact]
    public async Task ConjuntoNaoColetado_PermaneceDesconhecido_SemAfirmarAusencia()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var aquisicao = await ColetarAsync(db, Cenario.Padrao() with { ConvidadosNegados = true });

        var conjunto = aquisicao.Sets.Single(s => s.Set == IdentityObservationSet.InactiveGuest);
        conjunto.Outcome.Should().Be(IdentityObservationSetOutcome.InsufficientPermission);
        conjunto.IsComplete.Should().BeFalse();
        conjunto.ObservedCount.Should().Be(0);
        conjunto.Objects.Should().BeEmpty("resultado parcial não prova a ausência dos objetos não recebidos");
        conjunto.Limitation.Should().NotBeNullOrWhiteSpace("o motivo da limitação é declarado");

        var privilegiados = aquisicao.Sets.Single(s => s.Set == IdentityObservationSet.PrivilegedRoleMember);
        privilegiados.Outcome.Should().Be(IdentityObservationSetOutcome.Collected,
            "a falha em um conjunto não contamina os demais");
    }

    /// <summary>
    /// Uma coleta que FALHA registra a tentativa com o estado real e preserva intacta a última evidência
    /// válida. Nada é apagado, e a nova avaliação não pode usar silenciosamente os fatos antigos como se
    /// tivessem sido coletados agora — a aquisição da falha não carrega fato nenhum.
    /// </summary>
    [Fact]
    public async Task ColetaQueFalha_PreservaAEvidenciaAnterior_ESeRegistraComoFalha()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var boa = await ColetarAsync(db, Cenario.Padrao() with { Em = t0 });
        var falha = await ColetarAsync(db, Cenario.Falha(t0.AddDays(1)));

        falha.State.Should().Be(KnightSourceState.AuthenticationFailure);
        falha.Sets.Should().OnlyContain(s => s.Outcome != IdentityObservationSetOutcome.Collected,
            "uma coleta que falhou não tem conjunto algum comprovado");
        falha.Sets.Should().OnlyContain(s => s.Objects.Count == 0);

        var anterior = await new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA))
            .ReadAsync(boa.AcquisitionId);
        anterior.Should().NotBeNull("a evidência anterior continua identificada como anterior, e íntegra");
        anterior!.Sets.Single(s => s.Set == IdentityObservationSet.PrivilegedRoleMember)
            .Objects.Should().HaveCount(3);

        var snapshot = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.DataState.Should().Be(KnightSourceState.Completed,
            "os DADOS armazenados continuam sendo os da última coleta que produziu dados");
        snapshot.LastCollectionAt.Should().Be(t0, "o instante da evidência preservada não avança com a falha");
        snapshot.LastAttemptState.Should().Be(KnightSourceState.AuthenticationFailure,
            "a degradação aparece na última TENTATIVA, sem destruir a evidência");
    }

    /// <summary>
    /// Uma coleta ATRASADA — recebida depois, mas observada antes — registra a sua evidência e NÃO substitui o
    /// cadastro atual mais recente. A ordenação é pelo instante da aquisição, não pela ordem de chegada.
    /// </summary>
    [Fact]
    public async Task ColetaAtrasada_RegistraEvidencia_MasNaoSubstituiOEstadoAtualMaisRecente()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        await ColetarAsync(db, (Cenario.Padrao() with { Em = t0.AddDays(2) })
            .Renomeando("obj-admin-1", "Ana Nome Atual", "ana.atual@demo.example.com"));

        var atrasada = await ColetarAsync(db, (Cenario.Padrao() with { Em = t0 })
            .Renomeando("obj-admin-1", "Ana Nome Antigo", "ana.antigo@demo.example.com"));

        var link = await db.IdentitySourceLinks.AsNoTracking().SingleAsync(l => l.ExternalId == "obj-admin-1");
        var entidade = await db.IdentityEntities.AsNoTracking().SingleAsync(e => e.Id == link.IdentityEntityId);

        entidade.DisplayName.Should().Be("Ana Nome Atual",
            "uma coleta atrasada não sobrescreve silenciosamente o estado atual mais recente");
        entidade.CurrentAsOf.Should().Be(t0.AddDays(2));

        var registro = await new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA))
            .ReadAsync(atrasada.AcquisitionId);
        registro!.Sets.SelectMany(s => s.Objects).Should()
            .Contain(o => o.DisplayNameObserved == "Ana Nome Antigo",
                "a evidência da coleta atrasada é registrada — ela apenas não vira o cadastro atual");
    }

    // ---- (6) A avaliação lê a aquisição persistida ---------------------------------------------------

    /// <summary>
    /// O ponto central do pacote: o KNIGHT avalia a aquisição PERSISTIDA, a execução guarda qual foi, e a
    /// coleta acontece UMA vez — a avaliação não dispara uma segunda consulta ao Graph.
    /// </summary>
    [Fact]
    public async Task Avaliacao_LeAAquisicaoPersistida_EColetaUmaUnicaVez()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var collector = new ContandoColetor(Cenario.Padrao());
        var assessment = await AvaliarAsync(db, collector);

        collector.Chamadas.Should().Be(1, "uma aquisição lógica = uma coleta");

        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == assessment.Id);
        run.IdentityAcquisitionId.Should().NotBeNull("a avaliação registra qual coleta a sustentou");

        var aquisicao = await db.IdentityAcquisitions.AsNoTracking()
            .SingleAsync(a => a.Id == run.IdentityAcquisitionId!.Value);
        aquisicao.DirectoryNamespace.Should().Be(DiretorioA);
        aquisicao.State.Should().Be(KnightSourceState.Completed);

        // Os objetos do veredito vieram da aquisição — mesmos identificadores, mesma contagem.
        var afetados = await db.KnightAffectedObjects.AsNoTracking()
            .Where(o => o.IndicatorId == "AK-ENTRA-001").Select(o => o.ExternalId).ToListAsync();
        var observados = await db.IdentityEntityObservations.AsNoTracking()
            .Where(o => o.AcquisitionId == aquisicao.Id
                     && o.Set == IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability)
            .Select(o => o.ExternalId).ToListAsync();
        afetados.Should().BeEquivalentTo(observados,
            "a comprovação congelada do assessment é exatamente o que a aquisição registrou");
    }

    /// <summary>
    /// A comprovação histórica não acompanha o cadastro: atualizar o nome atual de uma identidade não altera
    /// o que uma avaliação anterior registrou. São artefatos com finalidades diferentes.
    /// </summary>
    [Fact]
    public async Task MudancaNoCadastroAtual_NaoAlteraAComprovacaoHistorica()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var antiga = await AvaliarAsync(db, new ContandoColetor(Cenario.Padrao() with { Em = t0 }));

        await AvaliarAsync(db, new ContandoColetor((Cenario.Padrao() with { Em = t0.AddDays(1) })
            .Renomeando("obj-admin-1", "Ana Renomeada", "ana.renomeada@demo.example.com")));

        var congelado = await db.KnightAffectedObjects.AsNoTracking()
            .SingleAsync(o => o.RunId == antiga.Id && o.ExternalId == "obj-admin-1"
                           && o.IndicatorId == "AK-ENTRA-001");
        congelado.DisplayName.Should().Be("Ana Silva",
            "a comprovação da avaliação antiga permanece com o nome de então");

        var entidade = await db.IdentityEntities.AsNoTracking()
            .SingleAsync(e => e.DisplayName == "Ana Renomeada");
        entidade.Should().NotBeNull("o cadastro atual seguiu em frente, sem tocar no histórico");
    }

    /// <summary>
    /// Mesma entrada, mesmo resultado: score, cobertura e vereditos não mudam por causa da troca do caminho de
    /// leitura. Se mudassem, este pacote teria alterado a avaliação — o que ele não pode fazer.
    /// </summary>
    [Fact]
    public async Task MesmaEntrada_MantemScore_Cobertura_EVeredito()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var t0 = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var primeira = await AvaliarAsync(db, new ContandoColetor(Cenario.Padrao() with { Em = t0 }));
        var segunda = await AvaliarAsync(db, new ContandoColetor(Cenario.Padrao() with { Em = t0.AddDays(1) }));

        segunda.Score.Should().Be(primeira.Score);
        segunda.Coverage.Should().Be(primeira.Coverage);
        segunda.ScoreFormulaVersion.Should().Be(primeira.ScoreFormulaVersion);
        segunda.Indicators.Select(i => (i.IndicatorId, i.Status)).Should()
            .BeEquivalentTo(primeira.Indicators.Select(i => (i.IndicatorId, i.Status)));
    }

    // ---- (7) Compatibilidade -------------------------------------------------------------------------

    /// <summary>
    /// A fonte de demonstração continua funcionando e NÃO entra no ADM: nenhuma entidade, nenhum vínculo,
    /// nenhuma aquisição. Dados sintéticos não podem se misturar aos objetos reais do tenant.
    /// </summary>
    [Fact]
    public async Task Demo_ContinuaFuncionando_ESeusDados_NaoEntramNoAdm()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var tenant = new SystemTenantContext(TenantA);
        var registry = new KnightCollectorRegistry(new IKnightCollector[] { new DemoKnightCollector() });
        var config = new ConfigSintetica(DiretorioA);
        var aquisicoes = new IdentityAcquisitionStore(db, tenant);
        var evidence = new IdentityEvidenceService(db, registry, config, aquisicoes, tenant);
        var service = new AegisKnightAssessmentService(
            db, registry, config, new SemNarrativa(), evidence, aquisicoes, tenant);

        var assessment = await service.RunDemoAssessmentAsync();

        assessment.Status.Should().Be(KnightRunStatus.Completed);
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == assessment.Id);
        run.IdentityAcquisitionId.Should().BeNull("Demo não passa pelo ADM nesta entrega");
        (await db.IdentityAcquisitions.CountAsync()).Should().Be(0);
        (await db.IdentityEntities.CountAsync()).Should().Be(0,
            "objetos de demonstração jamais se misturam às identidades reais");
    }

    /// <summary>
    /// O envelope de fatos v1 (array nu) e o v2 (objeto) continuam legíveis pela MESMA autoridade — a que o
    /// snapshot e a aquisição compartilham. Um v1 não ganha zeros inventados para os agregados que não tinha.
    /// </summary>
    [Fact]
    public void EnvelopeDeFatos_V1_E_V2_PermanecemLegiveis()
    {
        const string v1 = """[{"key":"PrivilegedAccountsTotal","outcome":"Collected","count":9}]""";
        var lidoV1 = IdentityEvidenceFactsJson.Deserialize(v1);
        lidoV1.SchemaVersion.Should().Be(IdentityEvidenceFactsJson.LegacySchemaVersion);
        lidoV1.Observations.Should().ContainSingle(o => o.Key == KnightSignalKey.PrivilegedAccountsTotal && o.Count == 9);
        lidoV1.IdentityRisk.Should().BeNull("um snapshot v1 não ganha agregados que ele nunca teve");
        lidoV1.AuthenticationPosture.Should().BeNull();

        var v2 = IdentityEvidenceFactsJson.Serialize(
            new[] { KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 4) }, null, null);
        var lidoV2 = IdentityEvidenceFactsJson.Deserialize(v2);
        lidoV2.SchemaVersion.Should().Be(IdentityEvidenceFactsJson.CurrentSchemaVersion);
        lidoV2.Observations.Should().ContainSingle(o => o.Key == KnightSignalKey.InactiveGuestAccounts && o.Count == 4);

        IdentityEvidenceFactsJson.Deserialize("{ isto não é json }").Observations.Should()
            .BeEmpty("JSON ilegível degrada para 'sem fatos', nunca para números falsos");
    }

    /// <summary>
    /// A tradução de tipos entre o ADM e a superfície histórica do KNIGHT é TOTAL e reversível. Um tipo novo
    /// de um dos lados sem correspondência no outro apareceria aqui, e não em produção como "usuário".
    /// </summary>
    [Fact]
    public void TraducaoDeTipoDeObjeto_EhTotal_EReversivel()
    {
        foreach (var kind in Enum.GetValues<IdentityEntityKind>())
            IdentityKnightBoundary.ToIdentityKind(IdentityKnightBoundary.ToKnightKind(kind)).Should().Be(kind);

        foreach (var kind in Enum.GetValues<KnightAffectedObjectKind>())
            IdentityKnightBoundary.ToKnightKind(IdentityKnightBoundary.ToIdentityKind(kind)).Should().Be(kind);
    }

    // ==================================================================================================
    //  [AEGIS-ADM-01 · correção dirigida] Regressões dos três achados da revisão
    // ==================================================================================================

    // ---- (A) A evidência de um conjunto não é substituída pela de outro -------------------------------

    /// <summary>
    /// O achado: o mesmo objeto aparece em duas populações com constatações e papéis DIFERENTES, e a
    /// resolução por identificador escolhia UM objeto "vencedor" para escrever as observações de TODOS os
    /// conjuntos. As duas provas viravam a mesma, e a explicação de por que a conta entrou em cada lista se
    /// perdia.
    ///
    /// A correção separa três coisas: a ENTIDADE é compartilhada (é a mesma conta), o CONTEÚDO de cada
    /// observação vem do conjunto que a produziu, e a PROJEÇÃO dos atributos atuais tem precedência própria e
    /// declarada. Inverter a ordem dos conjuntos na coleta não pode mudar nada disso.
    /// </summary>
    [Fact]
    public async Task ObservacoesDeConjuntosDistintos_PreservamOProprioConteudo_EIndependemDaOrdem()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ColetarAsync(db, Cenario.Padrao());

        var link = await db.IdentitySourceLinks.AsNoTracking().SingleAsync(l => l.ExternalId == "obj-admin-1");
        var observacoes = await db.IdentityEntityObservations.AsNoTracking()
            .Where(o => o.IdentityEntityId == link.IdentityEntityId).ToListAsync();

        observacoes.Should().HaveCount(2);

        var privilegiada = observacoes.Single(o => o.Set == IdentityObservationSet.PrivilegedRoleMember);
        var semMfa = observacoes.Single(o => o.Set == IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability);

        privilegiada.Detail.Should().Be(Cenario.DetalhePrivilegiado,
            "a observação do conjunto de privilegiados guarda a constatação DELE");
        privilegiada.RolesObserved.Should().BeEquivalentTo(new[] { "Administrador Global" });

        semMfa.Detail.Should().Be(Cenario.DetalheSemMfa,
            "a observação do conjunto sem MFA guarda a constatação DELE — e não a do outro conjunto");
        semMfa.RolesObserved.Should().BeEquivalentTo(
            new[] { "Administrador Global", "Administrador de Autenticacao" });

        // ---- A MESMA coleta com os conjuntos em ordem INVERTIDA, em outro diretório -------------------
        var connectorId = await db.Connectors.Select(c => c.Id).SingleAsync();
        var invertida = PedidoComConjuntos(
            Guid.NewGuid(), connectorId, DiretorioB, Instante(0), Conjuntos().Reverse().ToList());

        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA));
        await store.PrepareAsync(invertida);
        await db.SaveChangesAsync();

        var linkB = await db.IdentitySourceLinks.AsNoTracking()
            .SingleAsync(l => l.ExternalId == "obj-admin-1" && l.DirectoryNamespace == DiretorioB);
        var observacoesB = await db.IdentityEntityObservations.AsNoTracking()
            .Where(o => o.IdentityEntityId == linkB.IdentityEntityId).ToListAsync();

        observacoesB.Should().HaveCount(2, "a inversão não cria nem perde observação");
        observacoesB.Single(o => o.Set == IdentityObservationSet.PrivilegedRoleMember)
            .Detail.Should().Be(Cenario.DetalhePrivilegiado,
                "a evidência preservada não muda quando a coleta devolve os conjuntos em outra ordem");
        observacoesB.Single(o => o.Set == IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability)
            .Detail.Should().Be(Cenario.DetalheSemMfa);

        var entidadeB = await db.IdentityEntities.AsNoTracking().SingleAsync(e => e.Id == linkB.IdentityEntityId);
        entidadeB.DisplayName.Should().Be("Ana Silva",
            "a precedência da projeção é a ordem CANÔNICA dos conjuntos, não a ordem em que eles chegaram");
        entidadeB.Kind.Should().Be(IdentityEntityKind.User);
    }

    // ---- (B) Imutabilidade da aquisição --------------------------------------------------------------

    /// <summary>
    /// O achado: reapresentar um identificador de aquisição já gravado sobrescrevia origem, versões, datas,
    /// fatos e capacidades — ou seja, permitia alterar a evidência que uma avaliação já pode citar.
    ///
    /// O contrato agora é explícito: mesmo id e mesmo conteúdo é idempotente; mesmo id com QUALQUER
    /// divergência é conflito, recusado sem alteração parcial; conteúdo igual sob id novo é uma aquisição
    /// nova e legítima. Depois de cada recusa, a aquisição original tem de continuar idêntica — inclusive na
    /// releitura feita pelo consumidor.
    /// </summary>
    [Fact]
    public async Task ReapresentacaoIncompativel_EhRecusada_EAquisicaoOriginalPermaneceIntegra()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA));
        var connectorId = await db.Connectors.Select(c => c.Id).SingleAsync();
        var id = Guid.NewGuid();
        var original = Pedido(id, connectorId, Cenario.Padrao() with { Em = Instante(0) });

        await store.PrepareAsync(original);
        await db.SaveChangesAsync();

        var antes = await store.ReadAsync(id);
        antes.Should().NotBeNull();

        // 1) Repetição SEM mudança: idempotente, e nada é reescrito.
        await store.PrepareAsync(original);
        await db.SaveChangesAsync();
        (await db.IdentityAcquisitions.CountAsync()).Should().Be(1);
        (await db.IdentityEntityObservations.CountAsync()).Should().Be(4);

        // 2) Repetições INCOMPATÍVEIS — uma por dimensão do conteúdo imutável.
        var incompativeis = new (string Motivo, IdentityAcquisitionRequest Pedido)[]
        {
            ("fatos agregados",
                Pedido(id, connectorId, Cenario.Padrao() with
                {
                    Em = Instante(0),
                    Privilegiados = Cenario.Padrao().Privilegiados.Take(2).ToList(),
                })),
            ("origem: namespace do diretório",
                original with { Origin = original.Origin with { DirectoryNamespace = DiretorioB } }),
            ("origem: conector",
                original with { Origin = original.Origin with { ConnectorConfigId = Guid.NewGuid() } }),
            ("detalhe da tentativa",
                original with { Detail = "Outro relato da mesma coleta." }),
            ("horário de aquisição",
                Pedido(id, connectorId, Cenario.Padrao() with { Em = Instante(1) })),
            ("atributos preservados de um objeto",
                PedidoComConjuntos(id, connectorId, DiretorioA, Instante(0),
                    Conjuntos(detalheSemMfa: "Outra constatação para o mesmo objeto."))),
        };

        foreach (var (motivo, pedido) in incompativeis)
        {
            var acao = async () => await store.PrepareAsync(pedido);
            var erro = await acao.Should().ThrowAsync<IdentityAcquisitionConflictException>(
                "reescrever uma aquisição gravada mudaria a prova de uma avaliação já publicada ({0})", motivo);
            erro.Which.Divergences.Should().NotBeEmpty("o conflito nomeia o campo que divergiu");
        }

        // 3) Nenhuma alteração parcial sobreviveu a nenhuma das recusas.
        await db.SaveChangesAsync();
        var depois = await store.ReadAsync(id);
        depois.Should().BeEquivalentTo(antes,
            "a aquisição original permanece integralmente igual, inclusive na releitura pelo consumidor");

        (await db.IdentityAcquisitions.CountAsync()).Should().Be(1);
        (await db.IdentityEntityObservations.CountAsync()).Should().Be(4);
        (await db.IdentityEntities.CountAsync()).Should().Be(3);

        // 4) O MESMO conteúdo sob um id NOVO é uma aquisição nova e legítima.
        await store.PrepareAsync(Pedido(Guid.NewGuid(), connectorId, Cenario.Padrao() with { Em = Instante(0) }));
        await db.SaveChangesAsync();
        (await db.IdentityAcquisitions.CountAsync()).Should().Be(2,
            "observar de novo é um fato novo; conteúdo igual não é motivo para deduplicar");
    }

    // ---- (C) Nenhuma projeção regride no tempo -------------------------------------------------------

    /// <summary>
    /// O achado: o snapshot agregado, o estado da última tentativa e a saúde do conector eram sobrescritos
    /// sem verificar se a entrada era mais recente. Uma coleta ATRASADA — observada antes, concluída depois —
    /// apagava o presente.
    ///
    /// A regressão verifica as QUATRO projeções de uma vez: atributos atuais da entidade, metadados atuais do
    /// vínculo, snapshot agregado e saúde do conector. A aquisição atrasada continua gravada como evidência
    /// histórica — o que ela não faz é virar o presente.
    /// </summary>
    [Fact]
    public async Task ColetaAtrasada_NaoRegride_Entidade_Vinculo_Snapshot_NemSaudeDoConector()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var recente = await ColetarAsync(db, (Cenario.Padrao() with { Em = Instante(48) })
            .Renomeando("obj-admin-1", "Ana Nome Atual", "ana.atual@demo.example.com"));

        var atrasada = await ColetarAsync(db, new Cenario(
            DiretorioA,
            new[] { Obj("obj-admin-1", "Ana Nome Antigo", "ana.antigo@demo.example.com") },
            new[] { "obj-admin-1" },
            Array.Empty<ObjetoSintetico>(),
            Em: Instante(0)));

        var link = await db.IdentitySourceLinks.AsNoTracking().SingleAsync(l => l.ExternalId == "obj-admin-1");
        var entidade = await db.IdentityEntities.AsNoTracking().SingleAsync(e => e.Id == link.IdentityEntityId);

        entidade.DisplayName.Should().Be("Ana Nome Atual", "o cadastro atual continua sendo o mais recente");
        entidade.CurrentAsOf.Should().Be(Instante(48));
        entidade.CurrentAcquisitionId.Should().Be(recente.AcquisitionId,
            "o estado atual aponta para a aquisição que efetivamente o sustenta");
        entidade.FirstObservedAt.Should().Be(Instante(0),
            "a coleta atrasada recua o PRIMEIRO avistamento — isso é história, não regressão do presente");

        link.LastObservedAt.Should().Be(Instante(48), "os metadados atuais do vínculo também não regridem");
        link.LastAcquisitionId.Should().Be(recente.AcquisitionId);

        var snapshot = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.LastCollectionAt.Should().Be(Instante(48), "o último dado VÁLIDO é o mais recente");
        snapshot.LastAttemptAt.Should().Be(Instante(48), "a última TENTATIVA também é a mais recente");
        IdentityEvidenceFactsJson.Deserialize(snapshot.FactsJson).Observations
            .Single(o => o.Key == KnightSignalKey.PrivilegedAccountsTotal).Count.Should().Be(3,
                "os fatos agregados continuam sendo os da coleta mais recente, não os da atrasada");

        var conector = await db.Connectors.AsNoTracking().SingleAsync();
        conector.LastSyncAt.Should().Be(Instante(48));
        conector.LastStatus.Should().Be(ConnectorStatus.Healthy);

        // A evidência atrasada existe, é identificável e é lida como o que é: um registro do passado.
        var registro = await new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA))
            .ReadAsync(atrasada.AcquisitionId);
        registro!.AcquiredAt.Should().Be(Instante(0));
        registro.Sets.SelectMany(s => s.Objects).Should()
            .Contain(o => o.DisplayNameObserved == "Ana Nome Antigo");
    }

    /// <summary>
    /// A mesma proteção para o caso oposto: uma FALHA atrasada não pode rebaixar a saúde da integração nem se
    /// apresentar como "a última tentativa". Último dado VÁLIDO e última TENTATIVA continuam sendo duas
    /// perguntas diferentes, com relógios próprios.
    /// </summary>
    [Fact]
    public async Task FalhaAtrasada_NaoRebaixaASaude_NemSePassaPorUltimaTentativa()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ColetarAsync(db, Cenario.Padrao() with { Em = Instante(24) });
        var falhaAntiga = await ColetarAsync(db, Cenario.Falha(Instante(1)));

        var snapshot = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.LastAttemptState.Should().Be(KnightSourceState.Completed,
            "uma tentativa ANTIGA que terminou tarde não é a última tentativa");
        snapshot.LastAttemptAt.Should().Be(Instante(24));
        snapshot.DataState.Should().Be(KnightSourceState.Completed);
        snapshot.LastCollectionAt.Should().Be(Instante(24), "a última evidência válida segue intacta");

        var conector = await db.Connectors.AsNoTracking().SingleAsync();
        conector.LastStatus.Should().Be(ConnectorStatus.Healthy,
            "uma falha atrasada não rebaixa a saúde de uma integração cuja tentativa mais recente teve êxito");
        conector.LastSyncAt.Should().Be(Instante(24));

        // A falha continua REGISTRADA — ela não é apagada, apenas não redefine o presente.
        var registro = await new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA))
            .ReadAsync(falhaAntiga.AcquisitionId);
        registro!.State.Should().Be(KnightSourceState.AuthenticationFailure);
        registro.AcquiredAt.Should().Be(Instante(1));
    }

    /// <summary>
    /// PARCIALIDADE fora de ordem. Uma coleta parcial PRODUZ dados — e por isso é a que mais facilmente
    /// substituiria a evidência boa se a gravação não comparasse instantes. Chegando atrasada, ela é
    /// registrada como aquisição própria, com o seu desfecho e a sua limitação, e não rebaixa o último dado
    /// válido nem a completude já comprovada.
    /// </summary>
    [Fact]
    public async Task ColetaParcialAtrasada_NaoRebaixaOUltimoDadoValido()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        await ColetarAsync(db, Cenario.Padrao() with { Em = Instante(24) });

        var parcial = await ColetarAsync(db, Cenario.Padrao() with
        {
            Em = Instante(2),
            Estado = KnightSourceState.PartialCollection,
            ConvidadosNegados = true,
            Privilegiados = Cenario.Padrao().Privilegiados.Take(1).ToList(),
            SemMfa = Array.Empty<string>(),
        });

        parcial.State.Should().Be(KnightSourceState.PartialCollection);
        parcial.Sets.Single(s => s.Set == IdentityObservationSet.InactiveGuest)
            .Outcome.Should().Be(IdentityObservationSetOutcome.InsufficientPermission,
                "resultado parcial não prova a ausência dos objetos que não vieram");

        var snapshot = await db.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.DataState.Should().Be(KnightSourceState.Completed,
            "uma parcialidade ATRASADA não rebaixa a completude já comprovada");
        snapshot.LastCollectionAt.Should().Be(Instante(24));
        IdentityEvidenceFactsJson.Deserialize(snapshot.FactsJson).Observations
            .Single(o => o.Key == KnightSignalKey.PrivilegedAccountsTotal).Count.Should().Be(3,
                "os fatos preservados continuam sendo os da coleta completa mais recente");

        var conector = await db.Connectors.AsNoTracking().SingleAsync();
        conector.LastStatus.Should().Be(ConnectorStatus.Healthy,
            "a integração não passa a degradada por causa de uma tentativa antiga");

        // A parcial continua sendo evidência: ela existe, é identificável e diz o que conseguiu ler.
        var registro = await new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA))
            .ReadAsync(parcial.AcquisitionId);
        registro!.AcquiredAt.Should().Be(Instante(2));
        registro.Sets.Single(s => s.Set == IdentityObservationSet.PrivilegedRoleMember)
            .Objects.Should().HaveCount(1, "a aquisição parcial preserva o que ELA observou");
    }

    /// <summary>
    /// Empate temporal exato: duas aquisições com o MESMO instante e conteúdos diferentes. O vencedor é
    /// decidido pela ordem ordinal do identificador da aquisição — arbitrário de propósito, mas igual em
    /// qualquer ordem de chegada. Aplicado nos dois sentidos, o estado final é o mesmo.
    /// </summary>
    [Fact]
    public async Task EmpateTemporal_TemResultadoDeterministico_EmQualquerOrdemDeChegada()
    {
        await SeedConnectorAsync(TenantA);
        await using var db = NewContext(TenantA);

        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA));
        var connectorId = await db.Connectors.Select(c => c.Id).SingleAsync();

        // Cada rodada usa o SEU par de identificadores: reutilizar os mesmos seria reapresentar uma
        // aquisição já gravada com outro conteúdo — recusado, e com razão. O que se repete entre as rodadas
        // é a RELAÇÃO ordinal, que é o que decide o empate.
        var menorA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
        var maiorA = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
        var menorB = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
        var maiorB = Guid.Parse("00000000-0000-0000-0000-0000000000b4");
        maiorA.CompareTo(menorA).Should().BePositive("o cenário exige um vencedor ordinal conhecido");
        maiorB.CompareTo(menorB).Should().BePositive("o cenário exige um vencedor ordinal conhecido");

        async Task<string?> AplicarAsync(string ns, Guid maior, params Guid[] ordem)
        {
            foreach (var id in ordem)
            {
                var nome = id == maior ? "Nome do id maior" : "Nome do id menor";
                await store.PrepareAsync(PedidoComConjuntos(
                    id, connectorId, ns, Instante(7),
                    new[]
                    {
                        new IdentityObservedSet(
                            IdentityObservationSet.PrivilegedRoleMember,
                            IdentityObservationSetOutcome.Collected, 1,
                            new[]
                            {
                                new IdentityObservedObject(
                                    "obj-empate", IdentityEntityKind.User, nome, "empate@demo.example.com",
                                    new[] { "Administrador Global" }, "Constatação sintética."),
                            },
                            IsComplete: true),
                    }));
                await db.SaveChangesAsync();
            }

            var vinculo = await db.IdentitySourceLinks.AsNoTracking()
                .SingleAsync(l => l.ExternalId == "obj-empate" && l.DirectoryNamespace == ns);
            return (await db.IdentityEntities.AsNoTracking().SingleAsync(e => e.Id == vinculo.IdentityEntityId))
                .DisplayName;
        }

        var crescente = await AplicarAsync(DiretorioA, maiorA, menorA, maiorA);
        var decrescente = await AplicarAsync(DiretorioB, maiorB, maiorB, menorB);

        crescente.Should().Be("Nome do id maior");
        decrescente.Should().Be(crescente,
            "o desempate não depende de qual aquisição chegou primeiro — se dependesse, duas réplicas "
            + "divergiriam sobre o mesmo par de coletas");
    }

    // ---- Apoio das regressões ------------------------------------------------------------------------

    private static DateTimeOffset Instante(int horas) =>
        new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero).AddHours(horas);

    /// <summary>Os dois conjuntos do cenário padrão, em ordem CANÔNICA, com conteúdos próprios.</summary>
    private static IReadOnlyList<IdentityObservedSet> Conjuntos(string? detalheSemMfa = null)
    {
        var privilegiados = new[]
        {
            new IdentityObservedObject("obj-admin-1", IdentityEntityKind.User, "Ana Silva",
                "ana.silva@demo.example.com", new[] { "Administrador Global" }, Cenario.DetalhePrivilegiado),
            new IdentityObservedObject("obj-admin-2", IdentityEntityKind.User, "Bruno Costa",
                "bruno.costa@demo.example.com", new[] { "Administrador Global" }, Cenario.DetalhePrivilegiado),
        };

        return new[]
        {
            new IdentityObservedSet(
                IdentityObservationSet.PrivilegedRoleMember,
                IdentityObservationSetOutcome.Collected, privilegiados.Length, privilegiados, IsComplete: true),
            new IdentityObservedSet(
                IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability,
                IdentityObservationSetOutcome.Collected, 1,
                new[]
                {
                    privilegiados[0] with
                    {
                        Roles = new[] { "Administrador Global", "Administrador de Autenticacao" },
                        Detail = detalheSemMfa ?? Cenario.DetalheSemMfa,
                    },
                },
                IsComplete: true),
        };
    }

    /// <summary>Aquisição montada CONJUNTO A CONJUNTO — é o que permite controlar a ordem em que chegam.</summary>
    private static IdentityAcquisitionRequest PedidoComConjuntos(
        Guid id, Guid connectorId, string ns, DateTimeOffset em, IReadOnlyList<IdentityObservedSet> conjuntos) =>
        new(
            id,
            new IdentityAcquisitionOrigin(connectorId, KnightSourceType.MicrosoftEntraId, ns, "Diretório sintético"),
            IdentityKnightBoundary.SchemaVersion,
            IdentityKnightBoundary.NormalizationVersion,
            em,
            ObservedAt: null,
            KnightSourceState.Completed,
            "Fonte SINTÉTICA de validação.",
            IdentityEvidenceFactsJson.Serialize(
                conjuntos.Select(c => KnightObservation.OfCount(
                    IdentityKnightBoundary.SignalFor(c.Set), c.ObservedCount)).ToList(), null, null),
            "[]",
            conjuntos);

    // ---- Infraestrutura do teste ---------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private async Task SeedConnectorAsync(Guid tenantId)
    {
        await using var db = NewContext(null);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
        await db.SaveChangesAsync();

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

    /// <summary>Executa UMA aquisição pelo caminho real e devolve o registro PERSISTIDO relido do banco.</summary>
    private async Task<IdentityAcquisitionRecord> ColetarAsync(AegisScoreDbContext db, Cenario cenario)
    {
        var tenant = new SystemTenantContext(TenantA);
        var service = new IdentityEvidenceService(
            db,
            new KnightCollectorRegistry(new IKnightCollector[] { new ContandoColetor(cenario) }),
            new ConfigSintetica(cenario.Namespace),
            new IdentityAcquisitionStore(db, tenant),
            tenant);

        var acquisition = await service.CollectAsync();
        acquisition.AcquisitionId.Should().NotBeNull();
        return await new IdentityAcquisitionStore(db, tenant).ReadAsync(acquisition.AcquisitionId!.Value)
            ?? throw new InvalidOperationException("A aquisição não foi persistida.");
    }

    private async Task<KnightAssessment> AvaliarAsync(AegisScoreDbContext db, ContandoColetor collector)
    {
        var tenant = new SystemTenantContext(TenantA);
        var registry = new KnightCollectorRegistry(new IKnightCollector[] { collector });
        var config = new ConfigSintetica(collector.Cenario.Namespace);
        var aquisicoes = new IdentityAcquisitionStore(db, tenant);
        var evidence = new IdentityEvidenceService(db, registry, config, aquisicoes, tenant);
        return await new AegisKnightAssessmentService(db, registry, config, new SemNarrativa(), evidence, aquisicoes, tenant)
            .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
    }

    private static IdentityAcquisitionRequest Pedido(Guid id, Guid connectorId, Cenario cenario) =>
        IdentityKnightBoundary.ToAcquisition(
            id,
            new IdentityAcquisitionOrigin(connectorId, KnightSourceType.MicrosoftEntraId, cenario.Namespace, "Diretório sintético"),
            cenario.ToResult(),
            cenario.Em ?? DateTimeOffset.UtcNow);

    private static ObjetoSintetico Obj(
        string id, string? nome, string? upn, IdentityEntityKind kind = IdentityEntityKind.User) =>
        new(id, nome, upn, kind);

    // ---- Cenário sintético ---------------------------------------------------------------------------

    private sealed record ObjetoSintetico(string ExternalId, string? Nome, string? Upn, IdentityEntityKind Kind);

    /// <summary>
    /// O que o diretório sintético devolve numa coleta. É a única coisa simulada aqui: a Evidence Fabric, o
    /// ADM, o avaliador, a persistência e as regras seguem reais.
    /// </summary>
    private sealed record Cenario(
        string Namespace,
        IReadOnlyList<ObjetoSintetico> Privilegiados,
        IReadOnlyList<string> SemMfa,
        IReadOnlyList<ObjetoSintetico> Convidados,
        DateTimeOffset? Em = null,
        bool ConvidadosNegados = false,
        KnightSourceState Estado = KnightSourceState.Completed)
    {
        public static Cenario Padrao() => new(
            DiretorioA,
            new[]
            {
                new ObjetoSintetico("obj-admin-1", "Ana Silva", "ana.silva@demo.example.com", IdentityEntityKind.User),
                new ObjetoSintetico("obj-admin-2", "Bruno Costa", "bruno.costa@demo.example.com", IdentityEntityKind.User),
                new ObjetoSintetico("obj-app-1", "Integração de backup", null, IdentityEntityKind.ServicePrincipal),
            },
            new[] { "obj-admin-1" },
            Array.Empty<ObjetoSintetico>());

        public static Cenario Falha(DateTimeOffset em) => new(
            DiretorioA, Array.Empty<ObjetoSintetico>(), Array.Empty<string>(), Array.Empty<ObjetoSintetico>(),
            em, Estado: KnightSourceState.AuthenticationFailure);

        /// <summary>Reescreve nome/UPN de UM objeto — o diretório mudou, a identidade não.</summary>
        public Cenario Renomeando(string externalId, string nome, string upn) => this with
        {
            Privilegiados = Privilegiados
                .Select(p => p.ExternalId == externalId ? p with { Nome = nome, Upn = upn } : p)
                .ToList(),
        };

        public KnightCollectionResult ToResult()
        {
            var agora = Em ?? DateTimeOffset.UtcNow;

            if (Estado is not (KnightSourceState.Completed or KnightSourceState.PartialCollection))
                return new KnightCollectionResult(
                    KnightSourceType.MicrosoftEntraId, Estado, "Diretório sintético de validação",
                    KnightFactSet.Empty, Array.Empty<KnightCapabilityStatus>(), agora,
                    "Fonte sintética: falha roteirizada pelo teste.");

            var semMfa = Privilegiados.Where(p => SemMfa.Contains(p.ExternalId)).ToList();

            var observacoes = new List<KnightObservation>
            {
                KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, Privilegiados.Count),
                KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, semMfa.Count),
            };

            var capacidades = new List<KnightCapabilityStatus>
            {
                new(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                new(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected),
            };

            // Os dois conjuntos descrevem o MESMO objeto com constatações e papéis DIFERENTES — é essa
            // diferença que explica por que ele entrou em cada população, e é ela que a evidência precisa
            // preservar separadamente.
            var conjuntos = new List<KnightAffectedObjectEvidence>
            {
                new(KnightSignalKey.PrivilegedAccountsTotal, Privilegiados.Select(Fato).ToList(), IsComplete: true),
                new(KnightSignalKey.PrivilegedAccountsWithoutMfa, semMfa.Select(FatoSemMfa).ToList(), IsComplete: true),
            };

            if (ConvidadosNegados)
            {
                capacidades.Add(new KnightCapabilityStatus(
                    KnightCapability.GuestAccounts, KnightCapabilityOutcome.InsufficientPermission,
                    "Permissão de leitura de convidados não consentida."));
            }
            else
            {
                capacidades.Add(new KnightCapabilityStatus(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Collected));
                observacoes.Add(KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, Convidados.Count));
                conjuntos.Add(new KnightAffectedObjectEvidence(
                    KnightSignalKey.InactiveGuestAccounts, Convidados.Select(Fato).ToList(), IsComplete: true));
            }

            return new KnightCollectionResult(
                KnightSourceType.MicrosoftEntraId, Estado, "Diretório sintético de validação",
                new KnightFactSet(observacoes), capacidades, agora,
                "Fonte SINTÉTICA de validação; nenhuma consulta ao Microsoft Graph foi feita.",
                AffectedObjects: conjuntos);
        }

        private static KnightAffectedObjectFact Fato(ObjetoSintetico o) => new(
            o.ExternalId,
            IdentityKnightBoundary.ToKnightKind(o.Kind),
            o.Nome,
            o.Upn,
            new[] { "Administrador Global" },
            DetalhePrivilegiado);

        /// <summary>O MESMO objeto, visto pela outra população: outra constatação e outros papéis.</summary>
        private static KnightAffectedObjectFact FatoSemMfa(ObjetoSintetico o) => Fato(o) with
        {
            Roles = new[] { "Administrador Global", "Administrador de Autenticacao" },
            Detail = DetalheSemMfa,
        };

        public const string DetalhePrivilegiado = "Constatação sintética da coleta.";

        public const string DetalheSemMfa =
            "Sem método capaz de MFA no relatório de registro do diretório sintético.";
    }

    private sealed class ContandoColetor : IKnightCollector
    {
        public ContandoColetor(Cenario cenario) => Cenario = cenario;
        public Cenario Cenario { get; }
        public int Chamadas { get; private set; }
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
        {
            Chamadas++;
            return Task.FromResult(Cenario.ToResult());
        }
    }

    /// <summary>Configuração resolvida sintética — o <c>AzureTenantId</c> É o namespace do diretório.</summary>
    private sealed class ConfigSintetica : IKnightSourceConfigurationProvider
    {
        private readonly string _namespace;
        public ConfigSintetica(string ns) => _namespace = ns;

        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(source switch
            {
                KnightSourceType.MicrosoftEntraId => new KnightEntraIdConfiguration(_namespace, "client", Secret),
                KnightSourceType.Demo => new KnightDemoConfiguration(),
                _ => new KnightSourceNotConfigured(source),
            });

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed class SemNarrativa : IKnightAdvisoryGenerator
    {
        public Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default) =>
            Task.FromResult(new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false));
    }
}
