using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-MVP-PRODUCT-02] Testes dos OBJETOS AFETADOS de um achado KNIGHT — concentrados nos riscos DESTA
/// entrega, sem repetir o que as suítes do catálogo/coletor já cobrem:
///
///   (1) contagem × lista: mesma unidade, mesma regra de dedupe — a tabela não pode contradizer o número;
///   (2) vínculo com a AVALIAÇÃO: a lista pertence à execução que a observou, e uma execução anterior à
///       preservação se declara "não preservada" em vez de receber a coleta atual;
///   (3) campos ausentes: nome/UPN nulos permanecem nulos, e o tipo do objeto nunca é presumido pessoa;
///   (4) coleta parcial: lista declaradamente incompleta, jamais um recorte disfarçado de conjunto inteiro;
///   (5) isolamento por tenant na leitura do detalhe;
///   (6) paginação e busca NO SERVIDOR.
/// </summary>
public sealed class KnightAffectedObjectTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public KnightAffectedObjectTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- (1) A lista sustenta a contagem: mesma unidade, mesma dedupe ------------------------------

    [Fact]
    public async Task Demo_ListaDeAfetados_TemExatamenteOTamanhoDaContagemDoVeredito()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        foreach (var indicatorId in KnightAffectedObjectScope.Indicators)
        {
            var indicator = assessment.Indicators.Single(i => i.IndicatorId == indicatorId);
            indicator.HasAffectedDetail.Should().BeTrue($"{indicatorId} está no escopo de detalhe desta entrega");

            var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(
                assessment.Id, indicatorId, 1, 100, null);

            page.Should().NotBeNull();
            page!.State.Should().Be(KnightAffectedDetailState.Available);
            page.TotalPreserved.Should().Be(indicator.AffectedObjectCount,
                "a lista e a contagem do veredito são o MESMO conjunto — divergir aqui faria a tela contradizer " +
                "o número exibido ao lado dela");
            page.Items.Should().HaveCount(indicator.AffectedObjectCount);
        }
    }

    [Fact]
    public async Task ObjetoRepetidoNaColeta_ContaUmaVez_MesmaRegraDeDedupeDaContagem()
    {
        // O coletor devolve o MESMO identificador duas vezes (um objeto com dois papéis, por exemplo). A
        // contagem do veredito (12) já vem deduplicada — a lista tem de chegar à mesma unidade.
        var objetos = Enumerable.Range(1, 12).Select(n => Obj($"obj-{n:00}", $"Pessoa {n:00}")).ToList();
        objetos.Insert(3, Obj("OBJ-01", "Duplicado por diferença de caixa"));

        var evidence = new KnightAffectedObjectEvidence(KnightSignalKey.PrivilegedAccountsTotal, objetos);

        await using var db = NewContext(TenantA);
        var assessment = await RunWithAffectedAsync(db, TenantA, privilegedTotal: 12, evidence);

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 50, null);

        page!.TotalPreserved.Should().Be(12, "identificador repetido é UM objeto — a dedupe é a mesma da contagem");
        page.State.Should().Be(KnightAffectedDetailState.Available);
    }

    [Fact]
    public async Task ListaMenorQueAContagem_DeclaraADivergencia_EmVezDeEsconde()
    {
        // Cenário honesto e desconfortável: a regra contou 12, mas a coleta só enumerou 2 objetos.
        var evidence = new KnightAffectedObjectEvidence(
            KnightSignalKey.PrivilegedAccountsTotal,
            new[] { Obj("obj-1", "Ana"), Obj("obj-2", "Bruno") });

        await using var db = NewContext(TenantA);
        var assessment = await RunWithAffectedAsync(db, TenantA, privilegedTotal: 12, evidence);

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 50, null);

        page!.State.Should().Be(KnightAffectedDetailState.Partial,
            "lista menor que a contagem é lista INCOMPLETA — nunca um conjunto apresentado como inteiro");
        page.AffectedObjectCount.Should().Be(12, "a contagem do veredito é preservada como está");
        page.TotalPreserved.Should().Be(2);
        page.Limitation.Should().Contain("2").And.Contain("12");
    }

    [Fact]
    public async Task IndicadorConforme_NaoRecebeLista_PorqueNaoHaObjetoSinalizado()
    {
        // 8 privilegiados estão DENTRO do teto: o veredito é Conforme e a contagem de afetados é zero.
        var evidence = new KnightAffectedObjectEvidence(
            KnightSignalKey.PrivilegedAccountsTotal,
            Enumerable.Range(1, 8).Select(n => Obj($"obj-{n:00}", $"Pessoa {n:00}")).ToList());

        await using var db = NewContext(TenantA);
        var assessment = await RunWithAffectedAsync(db, TenantA, privilegedTotal: 8, evidence);

        assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-002").Status
            .Should().Be(KnightIndicatorStatus.Passed);

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 50, null);

        page!.AffectedObjectCount.Should().Be(0);
        page.TotalPreserved.Should().Be(0,
            "num achado CONFORME nada foi sinalizado — anexar ali os objetos observados faria a tabela " +
            "contradizer o número exibido ao lado dela");
        page.State.Should().Be(KnightAffectedDetailState.Available,
            "conjunto vazio É a resposta certa, e não uma ausência de detalhe");
    }

    // ---- (2) Vínculo com a avaliação e compatibilidade de históricos -------------------------------

    [Fact]
    public async Task AvaliacaoAnteriorAPreservacao_DeclaraAusencia_ENaoRecebeAListaAtual()
    {
        await using var db = NewContext(TenantA);

        // Execução "antiga": gravada como as anteriores a esta entrega — sem detalhe preservado.
        var legacyRun = new KnightAssessmentRun
        {
            SourceType = KnightSourceType.Demo,
            SourceState = KnightSourceState.Completed,
            Source = "Execução anterior",
            Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v1",
            ScoreFormulaVersion = "knight-score-v1",
            StartedAt = DateTimeOffset.UnixEpoch,
        };
        legacyRun.Indicators.Add(new KnightIndicatorResult
        {
            RunId = legacyRun.Id,
            IndicatorId = "AK-ENTRA-002",
            Title = "Volume excessivo de contas privilegiadas",
            Category = KnightIndicatorCategory.IdentityGovernance,
            Severity = SeverityLevel.High,
            Status = KnightIndicatorStatus.Exposed,
            Evidence = "78 contas privilegiadas",
            AffectedObjectCount = 78,
            CollectedAt = DateTimeOffset.UnixEpoch,
        });
        db.KnightAssessmentRuns.Add(legacyRun);
        await db.SaveChangesAsync();

        // Uma coleta NOVA acontece depois, com detalhe.
        await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(legacyRun.Id, "AK-ENTRA-002", 1, 50, null);

        page!.State.Should().Be(KnightAffectedDetailState.NotPreserved,
            "o resultado histórico continua válido e NÃO é retropreenchido — mostrar a lista de hoje seria " +
            "apresentar o presente como prova do passado");
        page.AffectedObjectCount.Should().Be(78, "a contagem histórica é preservada exatamente");
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task IndicadorForaDoEscopoDeDetalhe_DizFaltaDeEscopo_NaoFalha()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-005", 1, 50, null);

        page!.State.Should().Be(KnightAffectedDetailState.OutOfScope);
        page.Items.Should().BeEmpty("um achado sem detalhe nesta entrega jamais recebe a lista de outro achado");
    }

    [Fact]
    public async Task AchadoInexistente_NaAvaliacao_Devolve404Logico()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-NAO-EXISTE", 1, 50, null);

        page.Should().BeNull();
    }

    // ---- (3) Campos ausentes: nada é inventado -----------------------------------------------------

    [Fact]
    public async Task NomeAusente_PermaneceAusente_ETipoNaoEPresumidoPessoa()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 100, null);

        var semNome = page!.Items.Where(i => i.DisplayName is null).ToList();
        semNome.Should().NotBeEmpty("o cenário cobre o objeto que a fonte devolve sem nome");
        semNome.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.ExternalId),
            "sem nome, o objeto é identificado pelo ID — nunca por um rótulo inventado");

        page.Items.Should().Contain(i => i.Kind == KnightAffectedObjectKind.ServicePrincipal,
            "um membro de papel privilegiado pode ser uma APLICAÇÃO — tratá-la como pessoa levaria a tela a " +
            "sugerir 'exigir MFA' de algo que não autentica com MFA");
        page.Items.Should().Contain(i => i.Kind == KnightAffectedObjectKind.Unknown,
            "tipo não reconhecido é declarado desconhecido, nunca 'usuário'");
        page.Limitation.Should().NotBeNullOrWhiteSpace("a ausência de nome é declarada");
    }

    // ---- (4) Coleta parcial ------------------------------------------------------------------------

    [Fact]
    public async Task ColetaQueNaoEnumerouTudo_ChegaComoParcial_ComALimitacaoPreservada()
    {
        var evidence = new KnightAffectedObjectEvidence(
            KnightSignalKey.InactiveGuestAccounts,
            new[] { Obj("guest-1", "Convidado 1", KnightAffectedObjectKind.Guest) },
            IsComplete: false,
            Limitation: "Falha na segunda página de convidados — enumeração interrompida.");

        await using var db = NewContext(TenantA);
        var assessment = await RunWithAffectedAsync(db, TenantA, privilegedTotal: 12, evidence, inactiveGuests: 1);

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-004", 1, 50, null);

        page!.State.Should().Be(KnightAffectedDetailState.Partial,
            "a coleta declarou que não enumerou tudo — a lista não pode parecer completa só porque " +
            "o número de itens bate com a contagem");
        page.Limitation.Should().Contain("segunda página");
    }

    // ---- (5) Isolamento por tenant -----------------------------------------------------------------

    [Fact]
    public async Task DetalheDeOutroTenant_EIndistinguivelDeInexistente()
    {
        await using var dbA = NewContext(TenantA);
        var assessmentA = await ServiceFor(dbA, TenantA).RunDemoAssessmentAsync();

        await using var dbB = NewContext(TenantB);
        var vistoPorB = await ServiceFor(dbB, TenantB).GetAffectedObjectsAsync(
            assessmentA.Id, "AK-ENTRA-002", 1, 50, null);

        vistoPorB.Should().BeNull("o tenant B não pode nem saber que a avaliação do tenant A existe");

        // E a busca do tenant B por um nome que só existe no tenant A não devolve nada.
        await using var dbB2 = NewContext(TenantB);
        var assessmentB = await ServiceFor(dbB2, TenantB).RunDemoAssessmentAsync();
        var proprio = await ServiceFor(dbB2, TenantB).GetAffectedObjectsAsync(
            assessmentB.Id, "AK-ENTRA-002", 1, 100, null);
        proprio!.Items.Should().NotBeEmpty("o tenant B vê o próprio detalhe normalmente");

        await using var dbTodos = NewContext(null);
        var semTenant = await dbTodos.KnightAffectedObjects.IgnoreQueryFilters().CountAsync();
        semTenant.Should().Be(proprio.TotalPreserved * 2 + OutrosAfetadosDemo * 2,
            "os dois tenants gravaram o próprio conjunto — nenhum enxerga o do outro pela leitura filtrada");
    }

    // ---- (6) Paginação e busca NO SERVIDOR ---------------------------------------------------------

    [Fact]
    public async Task Paginacao_EFeitaNoServidor_ComOrdemEstavelEntrePaginas()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        var p1 = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 5, null);
        var p2 = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 2, 5, null);
        var p3 = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 3, 5, null);

        p1!.Items.Should().HaveCount(5);
        p2!.Items.Should().HaveCount(5);
        p3!.Items.Should().HaveCount(2, "12 objetos em páginas de 5");
        p1.TotalPreserved.Should().Be(12, "o total é o do conjunto, não o da página");

        var todos = p1.Items.Concat(p2.Items).Concat(p3.Items).Select(i => i.ExternalId).ToList();
        todos.Should().OnlyHaveUniqueItems("uma ordem instável repetiria ou sumiria com objetos entre páginas");
    }

    [Fact]
    public async Task PageSizeAbsurdo_ESaneadoNoServidor()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();

        var page = await ServiceFor(db, TenantA).GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 0, 100_000, null);

        page!.PageSize.Should().Be(KnightAffectedObjectsPage.MaxPageSize,
            "o cliente não escolhe varrer a tabela inteira");
        page.Page.Should().Be(1, "página zero/negativa vira a primeira");
    }

    [Fact]
    public async Task Busca_EFeitaNoServidor_PorNomeUpnOuIdentificador()
    {
        await using var db = NewContext(TenantA);
        var assessment = await ServiceFor(db, TenantA).RunDemoAssessmentAsync();
        var svc = ServiceFor(db, TenantA);

        var porNome = await svc.GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 50, "Ana");
        porNome!.MatchCount.Should().Be(1);
        porNome.Items.Should().ContainSingle().Which.DisplayName.Should().Be("Ana Prado");
        porNome.TotalPreserved.Should().Be(12, "o total do conjunto continua visível durante a busca");

        var porId = await svc.GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 50, "demo-app-01");
        porId!.Items.Should().ContainSingle().Which.Kind.Should().Be(KnightAffectedObjectKind.ServicePrincipal);

        var semResultado = await svc.GetAffectedObjectsAsync(assessment.Id, "AK-ENTRA-002", 1, 50, "inexistente-zzz");
        semResultado!.MatchCount.Should().Be(0);
        semResultado.Items.Should().BeEmpty();
        semResultado.State.Should().Be(KnightAffectedDetailState.Available,
            "busca sem resultado NÃO é ausência de detalhe — são coisas diferentes na tela");
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>Afetados do Demo fora de AK-ENTRA-002 (2 sem MFA + 3 convidados) — usado na contagem global.</summary>
    private const int OutrosAfetadosDemo = 5;

    private static KnightAffectedObjectFact Obj(
        string id, string? name, KnightAffectedObjectKind kind = KnightAffectedObjectKind.User) =>
        new(id, kind, name, name is null ? null : $"{id}@demo.example.com", new[] { "Administrador Global" }, "Papel atribuído.");

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IAegisKnightAssessmentService ServiceFor(
        AegisScoreDbContext db, Guid tenantId, IKnightCollector? collector = null)
    {
        var registry = new KnightCollectorRegistry(new[] { collector ?? new DemoKnightCollector() });
        var tenant = new SystemTenantContext(tenantId);
        var config = new DemoOnlyConfigProvider();
        var aquisicoes = new AegisScore.Infrastructure.Identity.IdentityAcquisitionStore(db, tenant);
        var evidence = new AegisScore.Infrastructure.Identity.IdentityEvidenceService(db, registry, config, aquisicoes, tenant);
        return new AegisKnightAssessmentService(
            db, registry, config, new NoAdvisoryGenerator(), evidence, aquisicoes, tenant);
    }

    /// <summary>Executa uma avaliação Demo com os afetados EXATOS do cenário (contagem controlada pelo teste).</summary>
    private static Task<KnightAssessment> RunWithAffectedAsync(
        AegisScoreDbContext db, Guid tenantId, int privilegedTotal,
        KnightAffectedObjectEvidence evidence, int inactiveGuests = 0)
    {
        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, privilegedTotal),
            KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, inactiveGuests),
        });

        var result = new KnightCollectionResult(
            KnightSourceType.Demo, KnightSourceState.Completed, "Cenário de teste", facts,
            Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UnixEpoch,
            AffectedObjects: new[] { evidence });

        return ServiceFor(db, tenantId, new StubCollector(result)).RunDemoAssessmentAsync();
    }

    private sealed class StubCollector : IKnightCollector
    {
        private readonly KnightCollectionResult _result;
        public StubCollector(KnightCollectionResult result) => _result = result;
        public KnightSourceType Source => KnightSourceType.Demo;
        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }

    private sealed class DemoOnlyConfigProvider : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(new KnightDemoConfiguration());
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    /// <summary>A narrativa consultiva é irrelevante aqui — e a falha dela nunca invalida o assessment.</summary>
    private sealed class NoAdvisoryGenerator : IKnightAdvisoryGenerator
    {
        public Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default) =>
            Task.FromResult(new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false));
    }
}
