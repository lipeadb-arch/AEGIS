using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Identity;

/// <summary>
/// [AEGIS-ADM-02] Histórico mensal e retenção operacional do ADM de identidade.
///
/// O que está sob teste não são tabelas, e sim as distinções que este pacote existe para não deixar colapsar:
/// "sem coleta" × "coletou e não achou ninguém" × "não guardamos mais o detalhe"; "o último dado válido" × "a
/// última tentativa"; "a origem mudou" × "nós passamos a ler a origem de outro jeito". Cada uma delas, se
/// colapsar, vira uma afirmação sobre o ambiente do cliente que ninguém apurou.
///
/// LIMITE declarado: bateria RELACIONAL em SQLite. Travas de linha, travas consultivas e corridas genuínas de
/// escrita são invisíveis aqui — elas têm bateria própria em PostgreSQL real
/// (<c>IdentityHistoryRetentionPostgresTests</c>).
/// </summary>
public sealed class IdentityHistoryRetentionTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TenantB = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string DiretorioA = "contoso-directory-a";
    private const string DiretorioB = "contoso-directory-b";

    /// <summary>Referência de "agora" para todos os casos: meio de junho, longe de qualquer virada de mês.</summary>
    private static readonly DateTimeOffset Agora = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// [AEGIS-ADM-02] Relógio CONTROLÁVEL do store. A janela de ADMISSÃO do ADM (o piso temporal que recusa
    /// uma coleta anterior ao mês mais antigo retido) é calculada a partir dele — usar o relógio do sistema
    /// aqui faria estes casos passarem ou falharem conforme a data em que a bateria rodasse.
    /// </summary>
    private static readonly TimeProvider Relogio = new FakeTimeProvider(Agora);

    private readonly SqliteConnection _connection;
    private readonly Guid _conectorA = Guid.NewGuid();
    private readonly Guid _conectorB = Guid.NewGuid();

    public IdentityHistoryRetentionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- (1) A fotografia do mês, e a última tentativa em separado ------------------------------------

    /// <summary>
    /// O mês guarda a ÚLTIMA coleta que produziu dados — não a última coleta, e muito menos a soma delas.
    /// Somar as populações de duas coletas contaria as mesmas identidades duas vezes; usar a última tentativa
    /// como fotografia esvaziaria o mês toda vez que a integração quebrasse no fim dele.
    /// </summary>
    [Fact]
    public async Task FotografiaDoMes_EUltimaColeta_ComDados_ComATentativaPreservadaAParte()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 3), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 12), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        var falha = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 20), KnightSourceState.Unavailable,
            NaoColetado(IdentityObservationSet.PrivilegedRoleMember, "Diretório indisponível na janela."));

        await ManterAsync(consolidar: true, remover: false);

        var maio = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        maio.AcquisitionCount.Should().Be(3, "três coletas são três fatos, mesmo que só duas tenham dado certo");
        maio.DataProducingCount.Should().Be(2);
        maio.SnapshotAcquiredAt.Should().Be(Em(5, 12), "a fotografia é a última coleta COM DADOS do mês");
        maio.Sets.Single().ObservedCount.Should().Be(2, "os valores vêm INTEIROS daquela coleta, sem somar as outras");

        maio.LastAttemptAcquisitionId.Should().Be(falha, "a última tentativa é outra pergunta, e fica em separado");
        maio.LastAttemptState.Should().Be(KnightSourceState.Unavailable);
        maio.IsProvisional.Should().BeFalse("maio já fechou em relação a 15/06");

        var junho = await MesAsync(TenantA, new DateOnly(2026, 6, 1));
        junho.IsProvisional.Should().BeTrue("o mês corrente ainda pode receber coletas");
    }

    // ---- (2) Sem coleta × zero comprovado -------------------------------------------------------------

    /// <summary>
    /// Um mês sem nenhuma coleta e um mês em que a coleta olhou e não encontrou ninguém são leituras
    /// OPOSTAS: a primeira não diz nada sobre o ambiente; a segunda é uma afirmação apurada. Preencher a
    /// primeira com zero transformaria silêncio em elogio.
    /// </summary>
    [Fact]
    public async Task MesSemColeta_NaoViraZero_EZeroComprovadoPermaneceZero()
    {
        await SemearAsync(TenantA, _conectorA);

        // Abril: nada. Maio: coleta que apurou ZERO privilegiados sem MFA registrado.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedWithoutRegisteredMfaCapability));

        await ManterAsync(consolidar: true, remover: false);

        var abril = await MesAsync(TenantA, new DateOnly(2026, 4, 1));
        abril.AcquisitionCount.Should().Be(0);
        abril.HasData.Should().BeFalse();
        abril.Sets.Should().BeEmpty("sem coleta não há conjunto — nem com contagem zero");

        var maio = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        maio.HasData.Should().BeTrue();
        var conjunto = maio.Sets.Single();
        conjunto.Outcome.Should().Be(IdentityObservationSetOutcome.Collected);
        conjunto.ObservedCount.Should().Be(0);
        conjunto.IsComplete.Should().BeTrue("a lista vazia É o conjunto — foi apurado");

        var serie = await LerHistoricoAsync(TenantA);
        var meses = serie.Directories.Single().Months.ToDictionary(m => m.Month);
        meses["2026-04"].State.Should().Be(IdentityHistoryMonthState.NoCollection);
        meses["2026-05"].State.Should().Be(IdentityHistoryMonthState.Collected);
        meses["2026-04"].Comparable.Should().BeFalse("não há valores para comparar num mês sem coleta");
    }

    /// <summary>
    /// Um mês em que TODAS as tentativas falharam não é um mês sem coleta: houve tentativa, e o motivo dela
    /// ter falhado é a informação mais útil do mês.
    /// </summary>
    [Fact]
    public async Task MesComTentativasQueFalharam_NaoSeConfundeComMesSemColeta()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 8), KnightSourceState.InsufficientPermission,
            NaoColetado(IdentityObservationSet.PrivilegedRoleMember, "Consentimento ausente para o relatório."));

        await ManterAsync(consolidar: true, remover: false);

        var maio = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        maio.AcquisitionCount.Should().Be(1);
        maio.DataProducingCount.Should().Be(0);
        maio.HasData.Should().BeFalse();
        maio.LastAttemptState.Should().Be(KnightSourceState.InsufficientPermission);

        var meses = (await LerHistoricoAsync(TenantA)).Directories.Single().Months.ToDictionary(m => m.Month);
        meses["2026-05"].State.Should().Be(IdentityHistoryMonthState.NoData);
        meses["2026-05"].LastAttempt!.State.Should().Be(nameof(KnightSourceState.InsufficientPermission));
        meses["2026-04"].State.Should().Be(IdentityHistoryMonthState.NoCollection);
    }

    // ---- (3) Parcialidade ----------------------------------------------------------------------------

    /// <summary>
    /// Uma coleta parcial PERMANECE parcial. Voltar à coleta completa anterior do mesmo mês apresentaria um
    /// mês saudável que ninguém observou — e o número exibido seria maior justamente porque a última coleta
    /// enxergou menos.
    /// </summary>
    [Fact]
    public async Task ColetaParcial_NaoVoltaAColetaCompletaAnterior_DoMesmoMes()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 4), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3", "obj-4"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 25), KnightSourceState.PartialCollection,
            Parcial(IdentityObservationSet.PrivilegedRoleMember, 4, "obj-1", "obj-2"));

        await ManterAsync(consolidar: true, remover: false);

        var maio = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        maio.SnapshotAcquiredAt.Should().Be(Em(5, 25));
        maio.SnapshotState.Should().Be(KnightSourceState.PartialCollection);

        var conjunto = maio.Sets.Single();
        conjunto.IsComplete.Should().BeFalse("a parcialidade viaja junto do número");
        conjunto.PreservedCount.Should().Be(2);
        conjunto.Limitation.Should().NotBeNullOrWhiteSpace();

        var mes = (await LerHistoricoAsync(TenantA)).Directories.Single().Months.Single(m => m.Month == "2026-05");
        mes.Sets.Single().IsComplete.Should().BeFalse();
    }

    // ---- (4) Reexecução e coleta atrasada -------------------------------------------------------------

    /// <summary>
    /// Reconsolidar não duplica nem muda nada; uma coleta ATRASADA entra pelo instante em que foi adquirida,
    /// e não pela ordem em que chegou. Uma coleta antiga chegando depois AUMENTA a contagem do mês sem
    /// derrubar a fotografia — do contrário a série regrediria por ordem de processamento.
    /// </summary>
    [Fact]
    public async Task Reconsolidar_EIdempotente_EAquisicaoAtrasada_NaoRegrideAFotografia()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 20), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        await ManterAsync(consolidar: true, remover: false);
        await ManterAsync(consolidar: true, remover: false);

        await using (var db = NewContext(TenantA))
        {
            (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(
                IdentityAdmRetentionPolicy.RetainedMonths, "a janela tem 12 meses, e reconsolidar não cria linha nova");
            (await db.IdentityMonthlyRollupSets.CountAsync()).Should().Be(1);
        }

        // Chega DEPOIS, mas foi adquirida ANTES da fotografia: entra como fato do mês e não redefine o presente.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 2), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-9"));
        await ManterAsync(consolidar: true, remover: false);

        var maio = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        maio.AcquisitionCount.Should().Be(2, "a coleta atrasada é um fato do mês");
        maio.SnapshotAcquiredAt.Should().Be(Em(5, 20), "mas não passa a ser a fotografia — ela é mais antiga");
        maio.Sets.Single().ObservedCount.Should().Be(2);

        // Agora uma atrasada POSTERIOR à fotografia: essa sim redefine o mês.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 28), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await ManterAsync(consolidar: true, remover: false);

        var revisto = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        revisto.SnapshotAcquiredAt.Should().Be(Em(5, 28));
        revisto.Sets.Single().ObservedCount.Should().Be(1);
        revisto.AcquisitionCount.Should().Be(3);
    }

    // ---- (5) Versões incompatíveis e namespaces distintos ---------------------------------------------

    /// <summary>
    /// Mudar a versão da normalização é mudar COMO se lê o diretório. O número pode subir ou descer sem que
    /// nada tenha acontecido no ambiente do cliente — e apresentar isso como evolução de postura seria uma
    /// conclusão inventada.
    /// </summary>
    [Fact]
    public async Task VersaoIncompativelEntreMeses_NaoParecEvolucaoDePostura()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(4, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"),
            normalizacao: "aegis-adm-identity-normalization-v2");

        await ManterAsync(consolidar: true, remover: false);

        var meses = (await LerHistoricoAsync(TenantA)).Directories.Single().Months.ToDictionary(m => m.Month);
        meses["2026-04"].Comparable.Should().BeFalse("é o primeiro mês com dados — não há anterior");
        meses["2026-05"].Comparable.Should().BeFalse();
        meses["2026-05"].ComparabilityNote.Should().Contain("normalização");
    }

    /// <summary>
    /// Dois diretórios do MESMO tenant são duas séries. Somá-los apresentaria uma população que nunca foi
    /// observada junta — o mesmo erro que o vínculo de origem do ADM-01 existe para impedir.
    /// </summary>
    [Fact]
    public async Task DiretoriosDistintos_SaoSeriesSeparadas_ENuncaSeSomam()
    {
        await SemearAsync(TenantA, _conectorA);
        await SemearConectorAsync(TenantA, _conectorB, ConnectorCapability.ConfigAnalyzer);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        await GravarAsync(TenantA, _conectorB, DiretorioB, Em(5, 11), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3", "obj-4", "obj-5"));

        await ManterAsync(consolidar: true, remover: false);

        var serie = await LerHistoricoAsync(TenantA);
        serie.Directories.Should().HaveCount(2);

        var a = serie.Directories.Single(d => d.DirectoryNamespace == DiretorioA);
        var b = serie.Directories.Single(d => d.DirectoryNamespace == DiretorioB);
        a.Months.Single(m => m.Month == "2026-05").Sets.Single().ObservedCount.Should().Be(2);
        b.Months.Single(m => m.Month == "2026-05").Sets.Single().ObservedCount.Should().Be(5);
    }

    // ---- (6) Limites: 90 dias e fronteira de mês UTC --------------------------------------------------

    /// <summary>
    /// Os limites são fronteiras declaradas, não aproximações silenciosas — e o corte dos 90 dias é EXATO no
    /// instante, sem arredondar para o dia. Um prazo anunciado como "90 dias" que na prática varia entre 90 e
    /// 91 conforme a hora da coleta é um prazo que ninguém consegue conferir. A aquisição EXATAMENTE no
    /// instante do corte ainda tem 90 dias completos e fica; um segundo antes dela, não.
    ///
    /// A virada do mês é UTC: o último instante de maio é maio; o primeiro de junho é junho.
    /// </summary>
    [Fact]
    public async Task Limites_De90Dias_EDeMesUtc_SaoFronteirasDeclaradas()
    {
        await SemearAsync(TenantA, _conectorA);

        var corte = IdentityAdmRetentionPolicy.DetailCutoff(Agora);

        // As duas observam o MESMO objeto: assim a mais nova assume o cadastro atual e a mais antiga fica sem
        // nenhuma referência — o que isola a fronteira dos 90 dias como única razão de sair ou ficar.
        var umSegundoAntes = await GravarAsync(
            TenantA, _conectorA, DiretorioA, corte.AddSeconds(-1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-fronteira"));
        var exatamenteNoCorte = await GravarAsync(
            TenantA, _conectorA, DiretorioA, corte, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-fronteira"));

        // Fronteira de mês em UTC: o último instante de maio pertence a maio; o primeiro de junho, a junho.
        var fimDeMaio = new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero);
        var inicioDeJunho = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await GravarAsync(TenantA, _conectorA, DiretorioA, fimDeMaio, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.InactiveGuest, "obj-maio"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, inicioDeJunho, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.InactiveGuest, "obj-junho"));

        var relatorio = await ManterAsync(consolidar: true, remover: true);

        await using var db = NewContext(TenantA);
        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == exatamenteNoCorte)).Should().BeTrue(
            "no instante do corte ela ainda tem 90 dias completos — o prazo é cumprido, não antecipado");
        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == umSegundoAntes)).Should().BeFalse(
            "um segundo antes do corte é evidência vencida, e nada a referencia");

        relatorio.DetailCutoff.Should().Be(corte, "o limite é explícito no relatório, não implícito no código");

        (await MesAsync(TenantA, new DateOnly(2026, 5, 1))).Sets.Single().Set
            .Should().Be(IdentityObservationSet.InactiveGuest);
        (await MesAsync(TenantA, new DateOnly(2026, 6, 1))).SnapshotAcquiredAt.Should().Be(inicioDeJunho);
    }

    /// <summary>Consolidações fora dos 12 meses saem; o mês mais antigo AINDA retido permanece.</summary>
    [Fact]
    public async Task Consolidacoes_ForaDaJanelaDe12Meses_SaoRemovidas()
    {
        await SemearAsync(TenantA, _conectorA);
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await ManterAsync(consolidar: true, remover: false);

        var maisAntigo = IdentityAdmRetentionPolicy.OldestRetainedMonth(Agora);

        // Uma consolidação de um mês JÁ fora da janela, como as que sobram de uma janela que andou.
        await using (var db = NewContext(TenantA))
        {
            db.IdentityMonthlyRollups.Add(new IdentityMonthlyRollup
            {
                Provider = KnightSourceType.MicrosoftEntraId,
                DirectoryNamespace = DiretorioA,
                Month = maisAntigo.AddMonths(-1),
                ConsolidatedAt = Agora,
                ConsolidationVersion = IdentityAdmRetentionPolicy.ConsolidationVersion,
            });
            await db.SaveChangesAsync();
        }

        await ManterAsync(consolidar: true, remover: true);

        await using var assert = NewContext(TenantA);
        (await assert.IdentityMonthlyRollups.AnyAsync(r => r.Month < maisAntigo)).Should().BeFalse();
        (await assert.IdentityMonthlyRollups.AnyAsync(r => r.Month == maisAntigo)).Should().BeTrue(
            "o mês mais antigo AINDA retido não é o primeiro a sair");
    }

    // ---- (7) Retenção: o que sai, o que fica e o que continua legível --------------------------------

    /// <summary>
    /// O caso central da retenção. Uma aquisição vencida SEM referência sai por inteiro (cabeçalho incluído —
    /// remover só observações não limitaria o crescimento). Uma aquisição vencida que sustenta uma avaliação
    /// perde apenas o DETALHE e fica identificada como tal. A consolidação daquele mês sobrevive às duas, e
    /// identidades, vínculos, afetados do KNIGHT e fotografias publicadas não são tocados.
    /// </summary>
    [Fact]
    public async Task Retencao_RemoveOVencidoSemReferencia_EPreservaOQueSustentaUmaAvaliacao()
    {
        await SemearAsync(TenantA, _conectorA);

        var vencidaEm = Agora.AddDays(-120);
        var semReferencia = await GravarAsync(TenantA, _conectorA, DiretorioA, vencidaEm, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        var citada = await GravarAsync(
            TenantA, _conectorA, DiretorioA, vencidaEm.AddHours(1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3"));

        var (runId, afetadosAntes, hashAntes) = await SemearAvaliacaoCongeladaAsync(TenantA, citada);

        await ManterAsync(consolidar: true, remover: false);
        var mesAntes = await MesAsync(TenantA, IdentityAdmRetentionPolicy.MonthOf(vencidaEm));
        mesAntes.SnapshotAcquisitionId.Should().Be(citada);

        var relatorio = await ManterAsync(consolidar: true, remover: true);
        var origem = relatorio.Directories.Single(d => d.Directory.DirectoryNamespace == DiretorioA);
        origem.AcquisitionsRemoved.Should().Be(1);
        origem.DetailExpired.Should().Be(1);
        origem.ProtectedRetained.Should().Be(1, "a exceção é CONTADA, não deduzida");
        origem.Protected.Single().AcquisitionId.Should().Be(citada);
        origem.Protected.Single().Reason.Should().Contain("KNIGHT");

        await using var db = NewContext(TenantA);

        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == semReferencia)).Should().BeFalse(
            "nada no produto dependia dela — sai inteira, cabeçalho e fatos");
        (await db.IdentityObservationSetStates.AnyAsync(s => s.AcquisitionId == semReferencia)).Should().BeFalse(
            "a cascata leva os estados por conjunto junto");

        var preservada = await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == citada);
        preservada.DetailRetiredAt.Should().NotBeNull("o detalhe saiu, e isso é declarado");
        preservada.FactsJson.Should().NotBe("{}", "os fatos que sustentam o veredito ficam");
        (await db.IdentityEntityObservations.AnyAsync(o => o.AcquisitionId == citada)).Should().BeFalse();
        (await db.IdentityObservationSetStates.SingleAsync(s => s.AcquisitionId == citada)).PreservedCount
            .Should().Be(3, "a contagem ORIGINAL da coleta não é reescrita por um expurgo");

        // Identidades e vínculos NUNCA saem: ausência numa população não é exclusão de identidade.
        (await db.IdentityEntities.CountAsync()).Should().Be(3);
        (await db.IdentitySourceLinks.CountAsync()).Should().Be(3);

        // A prova congelada do produto não é tocada por nenhum caminho desta manutenção.
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.IdentityAcquisitionId.Should().Be(citada);
        (await db.KnightAffectedObjects.CountAsync()).Should().Be(afetadosAntes);
        (await db.PostureSnapshots.AsNoTracking().SingleAsync()).ContentHash.Should().Be(hashAntes);

        // E a consolidação daquele mês continua de pé — ela guarda a própria cópia agregada.
        var mesDepois = await MesAsync(TenantA, IdentityAdmRetentionPolicy.MonthOf(vencidaEm));
        mesDepois.SnapshotAcquisitionId.Should().Be(citada);
        mesDepois.Sets.Single().ObservedCount.Should().Be(3);
        mesDepois.AcquisitionCount.Should().Be(2, "a consolidação não regride quando a evidência expira");
    }

    /// <summary>
    /// Depois do expurgo, a releitura precisa continuar dizendo a verdade. Detalhe expirado e conjunto
    /// genuinamente vazio produzem a MESMA lista vazia — e é justamente por isso que os dois não podem chegar
    /// ao avaliador com a mesma cara.
    /// </summary>
    [Fact]
    public async Task DetalheExpirado_NaoSeConfundeComConjuntoVazio_NaReleitura()
    {
        await SemearAsync(TenantA, _conectorA);

        var vencidaEm = Agora.AddDays(-120);
        var comDetalhe = await GravarAsync(TenantA, _conectorA, DiretorioA, vencidaEm, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        var vazioComprovado = await GravarAsync(
            TenantA, _conectorA, DiretorioA, Agora.AddDays(-1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember));

        await SemearAvaliacaoCongeladaAsync(TenantA, comDetalhe);
        await ManterAsync(consolidar: true, remover: true);

        await using var db = NewContext(TenantA);
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA), Relogio);

        var expirada = (await store.ReadAsync(comDetalhe))!;
        expirada.DetailRetiredAt.Should().NotBeNull();
        var conjuntoExpirado = expirada.Sets.Single();
        conjuntoExpirado.Objects.Should().BeEmpty();
        conjuntoExpirado.PreservedCount.Should().Be(2, "quantos objetos ERAM continua registrado");

        var reprojetada = IdentityKnightBoundary.ToCollectionResult(expirada);
        var evidencia = reprojetada.AffectedObjectSets.Single();
        evidencia.IsComplete.Should().BeFalse("uma lista vazia por expurgo não é uma lista completa");
        evidencia.Limitation.Should().Contain("expirado por retenção");

        var vazia = (await store.ReadAsync(vazioComprovado))!;
        vazia.DetailRetiredAt.Should().BeNull();
        vazia.Sets.Single().PreservedCount.Should().Be(0);
        var reprojetadaVazia = IdentityKnightBoundary.ToCollectionResult(vazia);
        reprojetadaVazia.AffectedObjectSets.Single().IsComplete.Should().BeTrue(
            "olhar e não encontrar ninguém é um resultado completo");
        (reprojetadaVazia.AffectedObjectSets.Single().Limitation ?? "").Should().NotContain("expirado");
    }

    /// <summary>
    /// Reapresentar uma aquisição cujo detalhe expirou é RECUSADO. Aceitar repovoaria observações removidas de
    /// propósito, e o registro passaria a exibir um detalhe reconstruído depois — indistinguível do original
    /// para quem lesse a avaliação que o cita.
    /// </summary>
    [Fact]
    public async Task ReplayDeAquisicaoComDetalheExpirado_ERecusado_SemRepovoarNada()
    {
        await SemearAsync(TenantA, _conectorA);

        var vencidaEm = Agora.AddDays(-120);
        var citada = await GravarAsync(TenantA, _conectorA, DiretorioA, vencidaEm, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        await SemearAvaliacaoCongeladaAsync(TenantA, citada);
        await ManterAsync(consolidar: true, remover: true);

        await using var db = NewContext(TenantA);
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA), Relogio);

        var replay = async () => await store.PrepareAsync(
            Pedido(_conectorA, DiretorioA, citada, vencidaEm, KnightSourceState.Completed,
                new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2") }));

        (await replay.Should().ThrowAsync<IdentityAcquisitionDetailRetiredException>())
            .Which.AcquisitionId.Should().Be(citada);

        db.ChangeTracker.Clear();
        (await db.IdentityEntityObservations.CountAsync(o => o.AcquisitionId == citada)).Should().Be(0,
            "a recusa acontece ANTES de qualquer escrita");
        (await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == citada)).DetailRetiredAt
            .Should().NotBeNull();
    }

    // ---- (8) Simulação -------------------------------------------------------------------------------

    /// <summary>
    /// A simulação mostra candidatos, protegidos e motivos — e não escreve NADA. É o que permite decidir sobre
    /// um expurgo antes de executá-lo, em vez de descobrir o que saiu depois.
    /// </summary>
    [Fact]
    public async Task Simulacao_MostraCandidatosEProtegidos_SemEscreverNada()
    {
        await SemearAsync(TenantA, _conectorA);

        var vencidaEm = Agora.AddDays(-120);
        await GravarAsync(TenantA, _conectorA, DiretorioA, vencidaEm, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        var citada = await GravarAsync(
            TenantA, _conectorA, DiretorioA, vencidaEm.AddHours(1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        await SemearAvaliacaoCongeladaAsync(TenantA, citada);

        var antes = await ContagensAsync(TenantA);

        var relatorio = await ManterAsync(consolidar: true, remover: true, simular: true);

        relatorio.Simulated.Should().BeTrue();
        var origem = relatorio.Directories.Single(d => d.Directory.DirectoryNamespace == DiretorioA);
        origem.AcquisitionsExamined.Should().Be(2);
        origem.AcquisitionsRemoved.Should().Be(1, "o que SAIRIA");
        origem.DetailExpired.Should().Be(1);
        origem.Protected.Single().Reason.Should().NotBeNullOrWhiteSpace();
        origem.ObservationsRemoved.Should().Be(3, "as observações que a passada real removeria");

        (await ContagensAsync(TenantA)).Should().BeEquivalentTo(antes, "simular não escreve — nem consolida");
    }

    // ---- (9) Isolamento entre tenants e a superfície de leitura ---------------------------------------

    /// <summary>
    /// A série de um tenant não enxerga a do outro, e a rota de consulta NÃO dispara expurgo — uma consulta
    /// que apaga dados é uma armadilha, porque quem a chama não sabe que está escrevendo.
    /// </summary>
    [Fact]
    public async Task Historico_EIsoladoPorTenant_EALeituraNaoDisparaExpurgo()
    {
        await SemearAsync(TenantA, _conectorA);
        await SemearAsync(TenantB, _conectorB);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-a"));
        await GravarAsync(TenantB, _conectorB, DiretorioB, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-b1", "obj-b2"));

        // Uma aquisição VENCIDA e sem referência: se a leitura disparasse expurgo, ela sumiria aqui.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-200), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.InactiveGuest, "obj-antigo"));

        await ManterAsync(consolidar: true, remover: false);

        var serieA = await LerHistoricoAsync(TenantA);
        serieA.Directories.Should().ContainSingle().Which.DirectoryNamespace.Should().Be(DiretorioA);
        serieA.Directories.Single().Months.Single(m => m.Month == "2026-05")
            .Sets.Single().ObservedCount.Should().Be(1, "os valores do outro ambiente não vazam para cá");

        var serieB = await LerHistoricoAsync(TenantB);
        serieB.Directories.Should().ContainSingle().Which.DirectoryNamespace.Should().Be(DiretorioB);

        await using var db = NewContext(TenantA);
        (await db.IdentityAcquisitions.CountAsync()).Should().Be(2, "a leitura não removeu nada");
        (await db.IdentityEntityObservations.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// A superfície HTTP do histórico: autenticada, tenant IMPLÍCITO e SOMENTE LEITURA. Verificado por
    /// reflexão porque o pipeline HTTP tem bateria própria — aqui o que se prova é que a rota nasceu
    /// protegida e sem verbo de escrita, e não que o servidor está de pé.
    /// </summary>
    [Fact]
    public void RotaDoHistorico_EAutenticada_ESomenteLeitura()
    {
        var controller = typeof(AegisScore.Api.Controllers.IdentityHistoryController);

        controller.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull(
            "a leitura do histórico exige usuário autenticado");
        controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();

        var acoes = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        acoes.Should().ContainSingle("o histórico tem exatamente uma leitura e nenhuma mutação");
        acoes.Single().GetCustomAttribute<HttpGetAttribute>().Should().NotBeNull();

        // Tenant IMPLÍCITO: nenhum parâmetro de rota/query carrega tenant.
        acoes.Single().GetParameters().Select(p => p.Name!.ToLowerInvariant())
            .Should().NotContain(n => n.Contains("tenant"));
    }

    // ---- (10) A retenção não roda sem consolidação segura ---------------------------------------------

    /// <summary>
    /// Remover sem consolidar é a única combinação capaz de apagar evidência sem deixar histórico no lugar
    /// dela. É recusada nos DOIS pontos em que alguém poderia pedi-la: no serviço e na configuração do worker.
    /// Descobrir isso só na hora do expurgo seria descobrir tarde demais.
    /// </summary>
    [Fact]
    public async Task Remocao_SemConsolidacao_ERecusada_NoServico_ENaConfiguracao()
    {
        await SemearAsync(TenantA, _conectorA);
        await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-120), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        var pedido = async () => await ManterAsync(consolidar: false, remover: true);
        (await pedido.Should().ThrowAsync<ArgumentException>())
            .Which.Message.Should().Contain("Consolidate=true");

        var opcoes = new IdentityAdmMaintenanceOptions { ConsolidationEnabled = false, RemovalEnabled = true };
        opcoes.TryValidate(out var erro).Should().BeFalse(
            "o worker não pode subir com uma combinação que o serviço recusaria a cada ciclo");
        erro.Should().Contain("ConsolidationEnabled");

        await using var db = NewContext(TenantA);
        (await db.IdentityAcquisitions.CountAsync()).Should().Be(1, "a recusa acontece antes de qualquer escrita");
    }

    /// <summary>
    /// Uma origem que FALHA não pode arrastar as outras — nem deixar meia manutenção aplicada nela. A falha é
    /// isolada, declarada no relatório, e a evidência vencida daquela origem continua inteira: consolidação e
    /// retenção correm na mesma transação, e o que falha volta atrás por completo.
    /// </summary>
    [Fact]
    public async Task OrigemComFalha_NaoImpedeAsDemais_ENadaEscreveParaEla()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-a"));

        // A origem B tem uma coleta VENCIDA e sem referência — exatamente o que a retenção removeria se a
        // consolidação dela tivesse dado certo.
        var vencidaDeB = await GravarAsync(TenantA, _conectorA, DiretorioB, Em(2, 5), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-b"));
        await GravarAsync(TenantA, _conectorA, DiretorioB, Em(5, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-b"));

        FazerConsolidacaoFalharEm(DiretorioB);

        var relatorio = await ManterAsync(consolidar: true, remover: true);

        relatorio.Directories.Should().HaveCount(2, "a origem com problema é VISITADA e reportada, não omitida");
        relatorio.Failed.Should().ContainSingle()
            .Which.Directory.DirectoryNamespace.Should().Be(DiretorioB);
        relatorio.Directories.Single(d => d.Directory.DirectoryNamespace == DiretorioA)
            .MonthsConsolidated.Should().Be(IdentityAdmRetentionPolicy.RetainedMonths);

        await using var db = NewContext(TenantA);
        (await db.IdentityMonthlyRollups.CountAsync(r => r.DirectoryNamespace == DiretorioA))
            .Should().Be(IdentityAdmRetentionPolicy.RetainedMonths);
        (await db.IdentityMonthlyRollups.CountAsync(r => r.DirectoryNamespace == DiretorioB))
            .Should().Be(0, "nada foi gravado para a origem que falhou");
        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == vencidaDeB)).Should().BeTrue(
            "e nada foi REMOVIDO dela: sem consolidação confirmada não há expurgo");
    }

    /// <summary>
    /// Cancelar entre origens para de forma graciosa: nada é consolidado, nada é removido, e a passada se
    /// declara incompleta em vez de fingir que varreu tudo.
    /// </summary>
    [Fact]
    public async Task Cancelamento_ParaGraciosamente_SemConsolidarNemRemover()
    {
        await SemearAsync(TenantA, _conectorA);
        await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-120), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var relatorio = await ManterAsync(consolidar: true, remover: true, ct: cts.Token);

        relatorio.Completed.Should().BeFalse("uma passada cancelada não é uma passada concluída");
        relatorio.Directories.Should().BeEmpty();

        await using var db = NewContext(TenantA);
        (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(0);
        (await db.IdentityAcquisitions.CountAsync()).Should().Be(1);
    }

    // ---- (11) Referências do cadastro atual ----------------------------------------------------------

    /// <summary>
    /// A avaliação do KNIGHT não é a única referência que importa. A aquisição que estabeleceu os atributos
    /// ATUAIS de uma identidade canônica — e a que foi a última observação de um vínculo de origem — também
    /// sustentam algo vivo: removê-las por inteiro deixaria o cadastro apontando para uma coleta inexistente.
    /// O DETALHE delas expira; o cabeçalho fica, e a exceção é declarada com motivo.
    /// </summary>
    [Fact]
    public async Task ReferenciaDoCadastroAtual_ImpedeARemocaoIntegral_MasNaoOExpurgoDoDetalhe()
    {
        await SemearAsync(TenantA, _conectorA);

        var vencidaEm = Agora.AddDays(-120);
        // As duas observam o MESMO objeto: a segunda assume o cadastro atual, e a primeira fica órfã.
        var superada = await GravarAsync(TenantA, _conectorA, DiretorioA, vencidaEm, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        var sustentaOCadastro = await GravarAsync(
            TenantA, _conectorA, DiretorioA, vencidaEm.AddHours(1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        var relatorio = await ManterAsync(consolidar: true, remover: true);
        var origem = relatorio.Directories.Single(d => d.Directory.DirectoryNamespace == DiretorioA);

        origem.AcquisitionsRemoved.Should().Be(1, "a coleta superada não sustenta mais nada");
        origem.DetailExpired.Should().Be(1);
        origem.Protected.Should().ContainSingle()
            .Which.AcquisitionId.Should().Be(sustentaOCadastro);
        origem.Protected.Single().Reason.Should().Contain("cadastro ATUAL");
        origem.ProtectedRetained.Should().Be(1, "a exceção é CONTADA, e não deduzida de um relatório vazio");

        await using var db = NewContext(TenantA);
        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == superada)).Should().BeFalse();

        var preservada = await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == sustentaOCadastro);
        preservada.DetailRetiredAt.Should().NotBeNull("o detalhe operacional expira mesmo no que fica");

        var entidade = await db.IdentityEntities.AsNoTracking().SingleAsync();
        entidade.CurrentAcquisitionId.Should().Be(sustentaOCadastro, "o cadastro continua apontando para algo que existe");
        (await db.IdentitySourceLinks.AsNoTracking().SingleAsync()).LastAcquisitionId
            .Should().Be(sustentaOCadastro);
    }

    // ---- (12) Reconsolidação DEPOIS do expurgo --------------------------------------------------------

    /// <summary>
    /// O caso que uma guarda por CONTAGEM erra. Um mês consolidado com três coletas cujos originais foram
    /// removidos volta a recalcular "uma" — e uma regra do tipo "só substitua se vier mais informado" jogaria
    /// fora a coleta atrasada legítima porque 1 &lt; 3, perdendo justamente a evidência mais recente do mês.
    ///
    /// O que decide é a proveniência: quem chega depois da fotografia preservada assume; quem chega antes não
    /// derruba nada. E as contagens são conservadas — o passado não encolhe porque o expurgo passou, nem
    /// cresce porque a manutenção rodou de novo.
    /// </summary>
    [Fact]
    public async Task ReconsolidacaoAposExpurgo_ConservaOHistorico_EIncorporaAtrasadaMaisRecente()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 3), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        var fotografia = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 20), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        // Uma coleta RECENTE reobserva os dois objetos: assim nenhuma das três de fevereiro sustenta o
        // cadastro atual, e o mês inteiro fica elegível ao expurgo.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        await ManterAsync(consolidar: true, remover: true);

        var fevereiro = new DateOnly(2026, 2, 1);
        var depoisDoExpurgo = await MesAsync(TenantA, fevereiro);
        depoisDoExpurgo.AcquisitionCount.Should().Be(3, "o mês teve três coletas, e isso não muda porque elas saíram");
        depoisDoExpurgo.RetiredAcquisitionCount.Should().Be(3, "quantas saíram é a outra metade da resposta");
        depoisDoExpurgo.SnapshotAcquisitionId.Should().Be(fotografia, "a fotografia sobrevive à remoção do original");
        depoisDoExpurgo.Sets.Single().ObservedCount.Should().Be(2);
        depoisDoExpurgo.RetentionSweptThroughAt.Should().Be(Em(2, 20));

        await using (var db = NewContext(TenantA))
        {
            (await db.IdentityAcquisitions.CountAsync(a => a.AcquiredAtUtc < Em(3, 1).UtcDateTime)).Should().Be(0);
        }

        // Reexecutar não reconta nem esvazia: as duas parcelas apenas se recontabilizam.
        await ManterAsync(consolidar: true, remover: true);
        var reexecutado = await MesAsync(TenantA, fevereiro);
        reexecutado.AcquisitionCount.Should().Be(3, "a mesma aquisição não é contada de novo a cada manutenção");
        reexecutado.RetiredAcquisitionCount.Should().Be(3);
        reexecutado.SnapshotAcquisitionId.Should().Be(fotografia);
        reexecutado.Sets.Single().ObservedCount.Should().Be(2);

        // Atrasada MAIS RECENTE que a fotografia preservada: assume o mês, ainda que sozinha contra três.
        var atrasada = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 25), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await ManterAsync(consolidar: true, remover: false);

        var comAtrasada = await MesAsync(TenantA, fevereiro);
        comAtrasada.SnapshotAcquisitionId.Should().Be(atrasada, "ela é comprovadamente mais recente");
        comAtrasada.Sets.Single().ObservedCount.Should().Be(1);
        comAtrasada.AcquisitionCount.Should().Be(4, "e é um fato novo do mês, somado às três já contadas");

        // O histórico separa as duas coisas. A ATIVIDADE de retenção no mês é real (três coletas saíram); a
        // fotografia exibida, porém, é a atrasada — que continua aqui, íntegra. Apresentá-la como "detalhe
        // expirado" só porque o expurgo passou pelo mês seria afirmar sobre ela algo que não aconteceu.
        var mes = (await LerHistoricoAsync(TenantA)).Directories.Single()
            .Months.Single(m => m.Month == "2026-02");
        mes.AcquisitionCount.Should().Be(4);
        mes.RetiredAcquisitionCount.Should().Be(3);
        mes.MonthRetentionNote.Should().Contain("3 de 4", "a atividade de retenção NO MÊS continua declarada");
        mes.SnapshotDetail.Should().Be(IdentityHistoryDetailAvailability.Available,
            "a fotografia é a coleta atrasada, que continua registrada e com o detalhe íntegro");
        mes.DetailRetentionNote.Should().BeNull("não há o que ressalvar sobre uma fotografia preservada");
        mes.State.Should().Be(IdentityHistoryMonthState.Collected);
    }

    /// <summary>
    /// Empate exato de instante entre a fotografia gravada e uma coleta que chega depois: vence o maior
    /// identificador, e não "a última que apareceu". Sem um desempate determinístico, duas manutenções
    /// poderiam escolher fotografias diferentes para o mesmo mês.
    /// </summary>
    [Fact]
    public async Task EmpateDeInstante_EDesempatadoPeloIdentificador_EmQualquerOrdem()
    {
        await SemearAsync(TenantA, _conectorA);

        var instante = Em(5, 12);
        var menor = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var maior = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        await GravarComIdAsync(maior, TenantA, _conectorA, DiretorioA, instante, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        await ManterAsync(consolidar: true, remover: false);
        (await MesAsync(TenantA, new DateOnly(2026, 5, 1))).SnapshotAcquisitionId.Should().Be(maior);

        // Chega depois, no MESMO instante, com identificador menor: não assume o mês.
        await GravarComIdAsync(menor, TenantA, _conectorA, DiretorioA, instante, KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-9"));
        await ManterAsync(consolidar: true, remover: false);

        var maio = await MesAsync(TenantA, new DateOnly(2026, 5, 1));
        maio.SnapshotAcquisitionId.Should().Be(maior, "o desempate é o mesmo do ADM-01, e não a ordem de chegada");
        maio.Sets.Single().ObservedCount.Should().Be(2);
        maio.AcquisitionCount.Should().Be(2);
    }

    /// <summary>
    /// Replay de uma aquisição removida por INTEIRO. A recusa por detalhe expirado não alcança este caso — a
    /// linha não existe mais, e a reapresentação cairia no caminho de criação como se fosse coleta nova,
    /// recriando em silêncio uma evidência que o produto removeu por vencimento.
    ///
    /// A memória que impede isso é a FRONTEIRA gravada no mês, e não uma lista de identificadores removidos:
    /// uma lista dessas cresceria para sempre. A mesma fronteira recusa também uma coleta INÉDITA anterior a
    /// ela — ela nasceria já vencida, e sairia na varredura seguinte.
    /// </summary>
    [Fact]
    public async Task ReplayDeAquisicaoRemovidaPorInteiro_ERecusado_PelaFronteiraDoMes()
    {
        await SemearAsync(TenantA, _conectorA);

        var removida = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        await ManterAsync(consolidar: true, remover: true);

        await using var db = NewContext(TenantA);
        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == removida)).Should().BeFalse();

        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA), Relogio);

        var replay = async () => await store.PrepareAsync(
            Pedido(_conectorA, DiretorioA, removida, Em(2, 10), KnightSourceState.Completed,
                new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1") }));

        (await replay.Should().ThrowAsync<IdentityAcquisitionRetentionSweptException>())
            .Which.AcquisitionId.Should().Be(removida);

        var inedita = async () => await store.PrepareAsync(
            Pedido(_conectorA, DiretorioA, Guid.NewGuid(), Em(2, 4), KnightSourceState.Completed,
                new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1") }));

        await inedita.Should().ThrowAsync<IdentityAcquisitionRetentionSweptException>();

        db.ChangeTracker.Clear();
        (await db.IdentityAcquisitions.CountAsync(a => a.AcquiredAtUtc < Em(3, 1).UtcDateTime)).Should().Be(0,
            "a recusa acontece ANTES de qualquer escrita");
        (await MesAsync(TenantA, new DateOnly(2026, 2, 1))).AcquisitionCount.Should().Be(1,
            "e o mês continua contando a coleta que houve, sem recontá-la nem ressuscitá-la");
    }

    // ---- (13) Lotes, retomada e o orçamento de uma passada -------------------------------------------

    /// <summary>
    /// Com o lote de origens em UMA por passada, a varredura precisa VISITAR TODAS — cada uma exatamente uma
    /// vez — e só então se declarar completa. Recomeçar do início a cada passada faria as primeiras origens
    /// consumirem o orçamento para sempre, e as últimas jamais seriam mantidas.
    /// </summary>
    [Fact]
    public async Task Retomada_PorCursor_VisitaCadaOrigemUmaUnicaVez()
    {
        await SemearAsync(TenantA, _conectorA);

        foreach (var ns in new[] { "dir-alfa", "dir-beta", "dir-gama" })
            await GravarAsync(TenantA, _conectorA, ns, Em(5, 10), KnightSourceState.Completed,
                Coletado(IdentityObservationSet.PrivilegedRoleMember, $"obj-{ns}"));

        var visitadas = new List<string>();
        IdentityAdmCursor? cursor = null;

        for (var passada = 0; passada < 10; passada++)
        {
            var relatorio = await ManterAsync(new IdentityAdmMaintenanceRequest(
                Now: Agora, Consolidate: true, MaxDirectories: 1, ResumeAfter: cursor));

            visitadas.AddRange(relatorio.Directories.Select(d => d.Directory.DirectoryNamespace));
            if (relatorio.Completed) break;

            relatorio.NextCursor.Should().NotBeNull("uma passada incompleta precisa dizer por onde continuar");
            cursor = relatorio.NextCursor;
        }

        visitadas.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        visitadas.Should().BeEquivalentTo(new[] { "dir-alfa", "dir-beta", "dir-gama" });

        await using var db = NewContext(TenantA);
        (await db.IdentityMonthlyRollups.CountAsync())
            .Should().Be(3 * IdentityAdmRetentionPolicy.RetainedMonths);
    }

    /// <summary>
    /// Uma origem com mais candidatos do que o teto por passada permite não pode nem monopolizar o ciclo nem
    /// ficar pela metade para sempre: a passada se declara incompleta, e as seguintes DRENAM o resto.
    /// </summary>
    [Fact]
    public async Task LoteDeAquisicoes_Esgotado_DrenaEmPassadasSucessivas()
    {
        await SemearAsync(TenantA, _conectorA);

        foreach (var dia in new[] { 3, 10, 17 })
            await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, dia), KnightSourceState.Completed,
                Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        var passadas = 0;
        for (; passadas < 10; passadas++)
        {
            var relatorio = await ManterAsync(new IdentityAdmMaintenanceRequest(
                Now: Agora, Consolidate: true, Remove: true, MaxAcquisitionsPerDirectory: 1));
            if (relatorio.Completed) break;
        }

        passadas.Should().BeGreaterThan(1, "com teto de uma por passada, três candidatas não cabem numa só");

        await using var db = NewContext(TenantA);
        (await db.IdentityAcquisitions.CountAsync(a => a.AcquiredAtUtc < Em(3, 1).UtcDateTime)).Should().Be(0,
            "o expurgo converge em várias passadas em vez de uma transação gigante");
        (await MesAsync(TenantA, new DateOnly(2026, 2, 1))).AcquisitionCount.Should().Be(3,
            "e o histórico do mês continua contando as três");
    }

    // ---- (14) Fixação da aquisição citada -------------------------------------------------------------

    /// <summary>
    /// "A linha existe" e "a evidência está íntegra" são perguntas diferentes. Uma avaliação pode citar uma
    /// aquisição cujo detalhe expirou — o veredito continua sustentado pelos fatos e pela completude por
    /// conjunto —, mas quem cita precisa poder distinguir os dois casos em vez de receber um booleano.
    /// </summary>
    [Fact]
    public async Task FixacaoDaAquisicaoCitada_DistingueLinhaExistente_DeDetalheIntegro()
    {
        await SemearAsync(TenantA, _conectorA);

        var citada = await GravarAsync(TenantA, _conectorA, DiretorioA, Agora.AddDays(-120),
            KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        var (runId, afetados, _) = await SemearAvaliacaoCongeladaAsync(TenantA, citada);

        await ManterAsync(consolidar: true, remover: true);

        await using var db = NewContext(TenantA);
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA), Relogio);

        var fixada = await store.PinForReferenceAsync(citada);
        fixada.Exists.Should().BeTrue("a fixação continua impedindo a remoção integral da linha");
        fixada.DetailRetiredAt.Should().NotBeNull();
        fixada.DetailAvailable.Should().BeFalse("o detalhe observado não está mais disponível, e isso é dito");

        var inexistente = await store.PinForReferenceAsync(Guid.NewGuid());
        inexistente.Exists.Should().BeFalse();
        inexistente.DetailAvailable.Should().BeFalse();

        // A reprodução do veredito não depende do detalhe expirado: os afetados daquela execução estão
        // congelados, e é por eles que a avaliação se sustenta.
        (await db.KnightAffectedObjects.CountAsync(o => o.RunId == runId)).Should().Be(afetados);
    }

    /// <summary>
    /// Uma origem que ficou SEM NENHUMA aquisição — o conector foi desconectado e a cascata levou as coletas
    /// embora — ainda tem consolidações penduradas. Ela precisa continuar sendo VISITADA, senão as linhas
    /// mensais vencidas dela nunca expirariam: crescimento silencioso exatamente onde a retenção deveria agir.
    ///
    /// E não pode ser CONSOLIDADA: doze meses vazios nasceriam sempre dentro da janela, e a origem morta
    /// ficaria se reconstruindo para sempre. "Mês sem coleta" é uma afirmação sobre uma origem viva.
    /// </summary>
    [Fact]
    public async Task OrigemSemAquisicoes_ContinuaVarrida_ESoExpiraOQuePassouDosDozeMeses()
    {
        await SemearAsync(TenantA, _conectorA);

        var maisAntigo = IdentityAdmRetentionPolicy.OldestRetainedMonth(Agora);

        await using (var db = NewContext(TenantA))
        {
            foreach (var mes in new[] { maisAntigo.AddMonths(-1), maisAntigo })
            {
                db.IdentityMonthlyRollups.Add(new IdentityMonthlyRollup
                {
                    Provider = KnightSourceType.MicrosoftEntraId,
                    DirectoryNamespace = "dir-abandonado",
                    Month = mes,
                    ConsolidatedAt = Agora.AddDays(-30),
                    ConsolidationVersion = IdentityAdmRetentionPolicy.ConsolidationVersion,
                });
            }
            await db.SaveChangesAsync();
        }

        var relatorio = await ManterAsync(consolidar: true, remover: true);

        var orfa = relatorio.Directories.Should().ContainSingle(
            d => d.Directory.DirectoryNamespace == "dir-abandonado").Subject;
        orfa.RollupsRemoved.Should().Be(1, "só a consolidação fora dos 12 meses sai");
        orfa.MonthsConsolidated.Should().Be(0, "não há o que consolidar numa origem sem coletas");

        await using var assert = NewContext(TenantA);
        (await assert.IdentityMonthlyRollups.CountAsync(r => r.DirectoryNamespace == "dir-abandonado"))
            .Should().Be(1, "a origem morta não se reconstrói com doze meses vazios a cada passada");
        (await assert.IdentityMonthlyRollups.AnyAsync(
            r => r.DirectoryNamespace == "dir-abandonado" && r.Month == maisAntigo)).Should().BeTrue(
            "o mês mais antigo AINDA retido não é o primeiro a sair");
    }

    // ---- (16) Exclusão da origem, reconexão e o denominador do mês -----------------------------------

    /// <summary>
    /// A cascata da exclusão do conector leva as aquisições embora sem passar pela retenção — e um mês cujo
    /// total fosse definido como "sobreviventes + removidas pela retenção" desabaria exatamente aqui: as duas
    /// parcelas iriam a zero, e o passado exibiria menos coletas do que houve.
    ///
    /// Excluir a fonte não é o mesmo que nunca ter coletado. O total do mês é ACUMULADO — cada coleta entra
    /// uma vez, quando aparece pela primeira vez — e por isso não depende nem da existência da linha nem da
    /// CAUSA do desaparecimento. Reconectar o mesmo namespace e coletar de novo acrescenta o que é novo, uma
    /// única vez, sem ressuscitar nem recontar o que já estava contado.
    /// </summary>
    [Fact]
    public async Task ExclusaoDoConector_NaoEncolheOHistorico_EAReconexaoIncorporaANovaColetaUmaVez()
    {
        await SemearAsync(TenantA, _conectorA);

        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 3), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        var fotografia = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 20), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        await ManterAsync(consolidar: true, remover: false);

        var fevereiro = new DateOnly(2026, 2, 1);
        (await MesAsync(TenantA, fevereiro)).AcquisitionCount.Should().Be(3);

        // A exclusão do conector, pelo comportamento que já existe: a FK em cascata leva a evidência junto.
        await ExcluirConectorAsync(TenantA, _conectorA);
        (await ContagensAsync(TenantA)).Aquisicoes.Should().Be(0, "a cascata levou TODA a evidência da origem");

        var orfao = await MesAsync(TenantA, fevereiro);
        orfao.AcquisitionCount.Should().Be(3, "o mês teve três coletas, e a exclusão da fonte não desfaz isso");
        orfao.RetiredAcquisitionCount.Should().Be(0,
            "e nenhuma delas saiu por RETENÇÃO — chamar a cascata de expurgo afirmaria uma causa não apurada");
        orfao.RetentionSweptThroughAt.Should().BeNull();

        // RECONEXÃO do MESMO namespace, com outro conector: a origem deixa de ser órfã e volta a coletar.
        var reconectado = Guid.NewGuid();
        await SemearConectorAsync(TenantA, reconectado, ConnectorCapability.IdentityPosture);
        await GravarAsync(TenantA, reconectado, DiretorioA, Agora.AddDays(-1), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        await ManterAsync(consolidar: true, remover: false);

        var reconsolidado = await MesAsync(TenantA, fevereiro);
        reconsolidado.AcquisitionCount.Should().Be(3, "fevereiro não recebeu coleta nova: continua com três");
        reconsolidado.SnapshotAcquisitionId.Should().Be(fotografia, "a fotografia preservada não cai por ausência");
        reconsolidado.Sets.Single().ObservedCount.Should().Be(2, "nem os conjuntos que ela guardou");
        reconsolidado.SnapshotSchemaVersion.Should().Be(IdentityKnightBoundary.SchemaVersion,
            "a proveniência gravada é a da coleta que originou a fotografia");

        await ManterAsync(consolidar: true, remover: false);
        (await MesAsync(TenantA, fevereiro)).AcquisitionCount.Should().Be(3, "reexecutar não muda nada");

        // Uma coleta NOVA de fevereiro, depois da reconexão: entra uma vez, e a fotografia passa a ser dela.
        var depoisDaReconexao = await GravarAsync(
            TenantA, reconectado, DiretorioA, Em(2, 25), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        await ManterAsync(consolidar: true, remover: false);
        var comNova = await MesAsync(TenantA, fevereiro);
        comNova.AcquisitionCount.Should().Be(4, "quatro coletas houve em fevereiro, contando a que veio depois");
        comNova.SnapshotAcquisitionId.Should().Be(depoisDaReconexao, "e ela é comprovadamente a mais recente");

        await ManterAsync(consolidar: true, remover: false);
        (await MesAsync(TenantA, fevereiro)).AcquisitionCount.Should().Be(4,
            "a mesma coleta não entra duas vezes na conta");
    }

    // ---- (17) A disponibilidade do detalhe é apurada NA FOTOGRAFIA ------------------------------------

    /// <summary>
    /// O marcador de retenção do MÊS não responde pela fotografia exibida, e as quatro situações abaixo
    /// divergem justamente onde ele erraria:
    ///
    ///   • mês com uma coleta PROTEGIDA que perdeu só o detalhe — o marcador de remoção integral nem se move;
    ///   • mês cuja fotografia saiu por INTEIRO — aí sim o expurgo responde por ela;
    ///   • mês cuja fotografia sumiu por EXCLUSÃO DA ORIGEM — a retenção não tem nada a ver com isso, e
    ///     atribuir a ela seria afirmar uma causa que ninguém apurou;
    ///   • mês com outras coletas removidas e a fotografia ÍNTEGRA — o marcador está aceso e não significa
    ///     nada sobre o que está sendo exibido.
    ///
    /// Em todos, os valores e a completude continuam sendo os APURADOS na coleta.
    /// </summary>
    [Fact]
    public async Task DisponibilidadeDoDetalhe_EApuradaNaFotografia_ENaoNoMarcadorDoMes()
    {
        await SemearAsync(TenantA, _conectorA);
        await SemearConectorAsync(TenantA, _conectorB, ConnectorCapability.ConfigAnalyzer);

        // Janeiro: uma coleta só, CITADA por uma avaliação — fica, e perde apenas o detalhe.
        var citada = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(1, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await SemearAvaliacaoCongeladaAsync(TenantA, citada);

        // Fevereiro: duas coletas, nenhuma referenciada — as duas saem, inclusive a fotografia do mês.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 5), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(2, 20), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        // Março: a de 5/3 está vencida; a de 25/3 está DENTRO dos 90 dias e é a fotografia do mês.
        await GravarAsync(TenantA, _conectorA, DiretorioA, Em(3, 5), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        var integra = await GravarAsync(TenantA, _conectorA, DiretorioA, Em(3, 25), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        // Abril, em OUTRA origem: dentro dos 90 dias, e portanto fora de qualquer expurgo.
        await GravarAsync(TenantA, _conectorB, DiretorioB, Em(4, 10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-9"));

        await ManterAsync(consolidar: true, remover: true);

        var origemA = (await LerHistoricoAsync(TenantA)).Directories
            .Single(d => d.DirectoryNamespace == DiretorioA);

        var janeiro = origemA.Months.Single(m => m.Month == "2026-01");
        janeiro.SnapshotDetail.Should().Be(IdentityHistoryDetailAvailability.DetailRetired);
        janeiro.DetailRetentionNote.Should().Contain("EXPIRADO POR RETENÇÃO");
        janeiro.DetailRetentionNote.Should().Contain("permanece registrada");
        janeiro.MonthRetentionNote.Should().BeNull(
            "nada saiu por inteiro em janeiro — o marcador de remoção integral do mês não se move por expurgo "
            + "de detalhe, e é por isso que ele não podia responder pela fotografia");
        janeiro.AcquisitionCount.Should().Be(1);

        var fevereiro = origemA.Months.Single(m => m.Month == "2026-02");
        fevereiro.SnapshotDetail.Should().Be(IdentityHistoryDetailAvailability.RemovedByRetention);
        fevereiro.DetailRetentionNote.Should().Contain("REMOVIDA POR RETENÇÃO");
        fevereiro.MonthRetentionNote.Should().Contain("2 de 2");
        fevereiro.AcquisitionCount.Should().Be(2, "o denominador do mês é o histórico, e ele não encolheu");

        var marco = origemA.Months.Single(m => m.Month == "2026-03");
        marco.SnapshotDetail.Should().Be(IdentityHistoryDetailAvailability.Available,
            "a coleta removida foi a antiga; a fotografia é a de 25/3, que continua íntegra");
        marco.DetailRetentionNote.Should().BeNull();
        marco.MonthRetentionNote.Should().Contain("1 de 2", "a atividade de retenção do mês é real, e é do MÊS");
        marco.Snapshot!.AcquisitionId.Should().Be(integra);
        marco.Sets.Single().ObservedCount.Should().Be(2);

        (await LerHistoricoAsync(TenantA)).Directories
            .Single(d => d.DirectoryNamespace == DiretorioB)
            .Months.Single(m => m.Month == "2026-04")
            .SnapshotDetail.Should().Be(IdentityHistoryDetailAvailability.Available);

        // A origem B é EXCLUÍDA: a fotografia de abril some por cascata, e não por vencimento.
        await ExcluirConectorAsync(TenantA, _conectorB);

        var abril = (await LerHistoricoAsync(TenantA)).Directories
            .Single(d => d.DirectoryNamespace == DiretorioB)
            .Months.Single(m => m.Month == "2026-04");

        abril.SnapshotDetail.Should().Be(IdentityHistoryDetailAvailability.Unavailable);
        abril.DetailRetentionNote.Should().Contain("não é conhecida");
        abril.DetailRetentionNote.Should().NotContain("EXPIRADO POR RETENÇÃO");
        abril.MonthRetentionNote.Should().BeNull("a retenção não passou por aqui, e não responde por essa ausência");
        abril.AcquisitionCount.Should().Be(1, "e o que o mês apurou continua declarado");
    }

    // ---- (18) A janela de ADMISSÃO, depois que a consolidação do mês expira ---------------------------

    /// <summary>
    /// A fronteira que recusa o replay mora na consolidação do mês — e a consolidação EXPIRA com os 12 meses.
    /// Bastava esperar a linha mensal vencer para uma coleta removida voltar a entrar pelo caminho de criação
    /// como se fosse novidade.
    ///
    /// O fecho é uma janela de ADMISSÃO temporal, calculada no relógio: abaixo do começo do mês mais antigo
    /// retido nada é aceito. Não é um registro de identificadores expurgados (que cresceria para sempre), e a
    /// recusa NÃO afirma que aquele identificador já havia sido processado — ela é sobre o instante declarado.
    /// </summary>
    [Fact]
    public async Task ReplayDepoisDeAConsolidacaoExpirar_ERecusadoPelaAdmissao_SemRepovoarProjecoes()
    {
        await SemearAsync(TenantA, _conectorA);

        // "Então": um passado em que a coleta de abril/2025 ainda era admissível e ainda tinha mês consolidado.
        var entao = new DateTimeOffset(2025, 8, 15, 10, 0, 0, TimeSpan.Zero);
        var emAbril = new DateTimeOffset(2025, 4, 10, 9, 0, 0, TimeSpan.Zero);

        var removida = await GravarEmAsync(entao, TenantA, _conectorA, DiretorioA, emAbril,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await GravarEmAsync(entao, TenantA, _conectorA, DiretorioA, entao.AddDays(-1),
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        await ManterAsync(new IdentityAdmMaintenanceRequest(Now: entao, Consolidate: true, Remove: true));

        await using (var db = NewContext(TenantA))
        {
            (await db.IdentityAcquisitions.AnyAsync(a => a.Id == removida)).Should().BeFalse();
            (await db.IdentityMonthlyRollups.SingleAsync(r => r.Month == new DateOnly(2025, 4, 1)))
                .RetentionSweptThroughAt.Should().Be(emAbril, "é a fronteira que recusa o replay HOJE");
        }

        // O tempo passa: abril/2025 sai da janela de 12 meses, e a consolidação dele — com a fronteira — some.
        await ManterAsync(consolidar: true, remover: true);

        await using var depois = NewContext(TenantA);
        (await depois.IdentityMonthlyRollups.AnyAsync(r => r.Month == new DateOnly(2025, 4, 1)))
            .Should().BeFalse("a consolidação expirou, e com ela a única memória por mês daquele expurgo");

        var entidadesAntes = await depois.IdentityEntities.AsNoTracking()
            .Select(e => new { e.Id, e.CurrentAcquisitionId }).ToListAsync();

        var store = new IdentityAcquisitionStore(depois, new SystemTenantContext(TenantA), Relogio);

        var replay = async () => await store.PrepareAsync(
            Pedido(_conectorA, DiretorioA, removida, emAbril, KnightSourceState.Completed,
                new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1") }));

        var recusa = (await replay.Should().ThrowAsync<IdentityAcquisitionOutsideAdmissionWindowException>())
            .Which;
        recusa.AcquisitionId.Should().Be(removida);
        recusa.AdmissionFloor.Should().Be(new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero));
        recusa.Message.Should().Contain("não afirma que este identificador já havia sido registrado",
            "janela fechada é uma afirmação sobre o TEMPO, e não prova que esta coleta já passou por aqui");

        depois.ChangeTracker.Clear();
        (await depois.IdentityAcquisitions.AnyAsync(a => a.Id == removida)).Should().BeFalse(
            "a recusa acontece ANTES de qualquer escrita");
        (await depois.IdentityEntityObservations.CountAsync(o => o.AcquisitionId == removida)).Should().Be(0);

        var entidadesDepois = await depois.IdentityEntities.AsNoTracking()
            .Select(e => new { e.Id, e.CurrentAcquisitionId }).ToListAsync();
        entidadesDepois.Should().BeEquivalentTo(entidadesAntes,
            "nem a projeção do cadastro atual pode se mexer por causa de uma tentativa recusada");
    }

    /// <summary>
    /// Os limites da janela de admissão, exatos e nos dois sentidos: o piso é aceito, um milissegundo antes
    /// dele não é, e uma coleta ATRASADA dentro da janela continua entrando — inclusive vencida, porque
    /// "vencida" é assunto da retenção e não da admissão.
    ///
    /// O instante declarado JAMAIS é ajustado para caber na janela: a recusa preserva o horário apresentado,
    /// que é o que a evidência existe para registrar.
    /// </summary>
    [Fact]
    public async Task JanelaDeAdmissao_TemPisoExato_EAceitaAColetaAtrasadaDentroDela()
    {
        await SemearAsync(TenantA, _conectorA);

        var piso = IdentityAdmRetentionPolicy.AdmissionFloor(Agora);
        piso.Should().Be(new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero));

        await using var db = NewContext(TenantA);
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(TenantA), Relogio);

        var abaixo = Guid.NewGuid();
        var forcar = async () => await store.PrepareAsync(
            Pedido(_conectorA, DiretorioA, abaixo, piso.AddMilliseconds(-1), KnightSourceState.Completed,
                new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1") }));

        (await forcar.Should().ThrowAsync<IdentityAcquisitionOutsideAdmissionWindowException>())
            .Which.AcquiredAt.Should().Be(piso.AddMilliseconds(-1),
                "o instante recusado é o APRESENTADO — nada é reescrito para fazer a coleta parecer recente");

        db.ChangeTracker.Clear();
        (await db.IdentityAcquisitions.AnyAsync(a => a.Id == abaixo)).Should().BeFalse();

        // No piso EXATO: aceita. A janela é fechada por baixo, e não "quase".
        await store.PrepareAsync(
            Pedido(_conectorA, DiretorioA, Guid.NewGuid(), piso, KnightSourceState.Completed,
                new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1") }));
        await db.SaveChangesAsync();

        // ATRASADA e já vencida pelos 90 dias, mas DENTRO da janela: entra, e é consolidada no mês dela.
        var atrasada = await GravarAsync(TenantA, _conectorA, DiretorioA,
            new DateTimeOffset(2025, 9, 10, 9, 0, 0, TimeSpan.Zero), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        await ManterAsync(consolidar: true, remover: false);

        var setembro = await MesAsync(TenantA, new DateOnly(2025, 9, 1));
        setembro.SnapshotAcquisitionId.Should().Be(atrasada);
        setembro.AcquisitionCount.Should().Be(1, "a coleta atrasada é um fato do mês dela, e entra uma vez");
    }

    // ---- Infraestrutura do teste ---------------------------------------------------------------------

    private static DateTimeOffset Em(int mes, int dia) => new(2026, mes, dia, 9, 0, 0, TimeSpan.Zero);

    private DbContextOptions<AegisScoreDbContext> Options =>
        new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(Options, new SystemTenantContext(tenantId));

    private Task<IdentityAdmMaintenanceReport> ManterAsync(
        bool consolidar, bool remover, bool simular = false, CancellationToken ct = default) =>
        ManterAsync(new IdentityAdmMaintenanceRequest(
            Now: Agora, Simulate: simular, Consolidate: consolidar, Remove: remover), ct);

    private Task<IdentityAdmMaintenanceReport> ManterAsync(
        IdentityAdmMaintenanceRequest pedido, CancellationToken ct = default) =>
        new IdentityAdmMaintenanceService(Options, new FakeTimeProvider(Agora))
            .RunAsync(pedido with { Now = pedido.Now ?? Agora }, ct);

    /// <summary>
    /// Faz UMA origem falhar dentro do banco, sem seam de teste na produção: um gatilho que aborta a escrita
    /// da consolidação daquele diretório. É a falha que a manutenção precisa isolar — e é assim, no banco, que
    /// ela acontece de verdade.
    /// </summary>
    private void FazerConsolidacaoFalharEm(string ns)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "CREATE TRIGGER falha_consolidacao BEFORE INSERT ON \"IdentityMonthlyRollups\" "
            + $"WHEN NEW.\"DirectoryNamespace\" = '{ns}' "
            + "BEGIN SELECT RAISE(ABORT, 'falha injetada na consolidacao'); END;";
        cmd.ExecuteNonQuery();
    }

    private async Task<IdentityHistoryViewDto> LerHistoricoAsync(Guid tenantId)
    {
        await using var db = NewContext(tenantId);
        var query = new IdentityHistoryQuery(db, new SystemTenantContext(tenantId), new FakeTimeProvider(Agora));
        return await query.GetAsync(new IdentityHistoryRangeRequest());
    }

    private async Task<IdentityMonthlyRollup> MesAsync(Guid tenantId, DateOnly mes)
    {
        await using var db = NewContext(tenantId);
        return await db.IdentityMonthlyRollups.AsNoTracking().Include(r => r.Sets)
            .SingleAsync(r => r.Month == mes);
    }

    private async Task<(int Aquisicoes, int Observacoes, int Estados, int Consolidacoes)> ContagensAsync(Guid tenantId)
    {
        await using var db = NewContext(tenantId);
        return (
            await db.IdentityAcquisitions.CountAsync(),
            await db.IdentityEntityObservations.CountAsync(),
            await db.IdentityObservationSetStates.CountAsync(),
            await db.IdentityMonthlyRollups.CountAsync());
    }

    private async Task SemearAsync(Guid tenantId, Guid connectorId)
    {
        await using (var db = NewContext(null))
        {
            if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
            {
                db.Tenants.Add(new Tenant
                {
                    Id = tenantId, Name = "Cliente", Slug = $"t-{tenantId:N}"[..20], Status = TenantStatus.Active,
                });
                await db.SaveChangesAsync();
            }
        }

        await SemearConectorAsync(tenantId, connectorId, ConnectorCapability.IdentityPosture);
    }

    private async Task SemearConectorAsync(Guid tenantId, Guid connectorId, ConnectorCapability capability)
    {
        await using var db = NewContext(tenantId);
        db.Connectors.Add(new ConnectorConfig
        {
            Id = connectorId,
            TenantId = tenantId,
            Provider = ConnectorProvider.Microsoft,
            Capability = capability,
            DisplayName = "Microsoft Entra ID · AEGIS KNIGHT",
            Enabled = true,
            EncryptedSettings = "{\"clientSecret\":\"segredo-sintetico\"}",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Uma avaliação KNIGHT que CITA a aquisição, com afetados preservados e uma fotografia publicada com
    /// hash. É o conjunto de provas congeladas que a retenção não pode tocar.
    /// </summary>
    private async Task<(Guid RunId, int Afetados, string Hash)> SemearAvaliacaoCongeladaAsync(
        Guid tenantId, Guid acquisitionId)
    {
        await using var db = NewContext(tenantId);

        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live,
            SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID",
            Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v2",
            ScoreFormulaVersion = "knight-score-v1",
            StartedAt = Agora.AddDays(-120),
            CompletedAt = Agora.AddDays(-120),
            IdentityAcquisitionId = acquisitionId,
        };

        var indicador = new KnightIndicatorResult
        {
            RunId = run.Id,
            IndicatorId = "AK-ENTRA-002",
            Title = "Privilegiados sem método capaz de MFA registrado",
            Status = KnightIndicatorStatus.Exposed,
            Evidence = "Evidência sintética.",
            AffectedObjectCount = 1,
            SourceType = KnightSourceType.MicrosoftEntraId,
            HasAffectedDetail = true,
            AffectedDetailComplete = true,
            CollectedAt = Agora.AddDays(-120),
        };
        indicador.AffectedObjects.Add(new KnightAffectedObject
        {
            RunId = run.Id,
            IndicatorResultId = indicador.Id,
            IndicatorId = "AK-ENTRA-002",
            ExternalId = "obj-1",
            Kind = KnightAffectedObjectKind.User,
        });
        run.Indicators.Add(indicador);
        db.KnightAssessmentRuns.Add(run);

        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        db.PostureSnapshots.Add(new PostureSnapshot
        {
            Type = PostureSnapshotType.Knight,
            SchemaVersion = "posture-snapshot-v1",
            FormulaVersion = "knight-score-v1",
            CatalogVersion = "ak-knight-v2",
            SemanticFamily = "knight:MicrosoftEntraId",
            SourceType = KnightSourceType.MicrosoftEntraId,
            CapturedAt = Agora.AddDays(-119),
            SourceRunId = run.Id,
            ContentHash = hash,
        });

        await db.SaveChangesAsync();
        return (run.Id, 1, hash);
    }

    /// <summary>
    /// Exclui o conector pelo comportamento que já existe — a FK em cascata leva a evidência de identidade
    /// junto. É o desaparecimento que NÃO passa pela retenção, e é por isso que ele precisa estar aqui.
    /// </summary>
    private async Task ExcluirConectorAsync(Guid tenantId, Guid connectorId)
    {
        await using var db = NewContext(tenantId);
        var conector = await db.Connectors.SingleAsync(c => c.Id == connectorId);
        db.Connectors.Remove(conector);
        await db.SaveChangesAsync();
    }

    /// <summary>O mesmo registro, com o RELÓGIO da admissão fixado em outro instante.</summary>
    private async Task<Guid> GravarEmAsync(
        DateTimeOffset relogio, Guid tenantId, Guid connectorId, string ns, DateTimeOffset em,
        IdentityObservedSet conjunto)
    {
        var id = Guid.NewGuid();
        await using var db = NewContext(tenantId);
        var store = new IdentityAcquisitionStore(
            db, new SystemTenantContext(tenantId), new FakeTimeProvider(relogio));

        await store.PrepareAsync(Pedido(
            connectorId, ns, id, em, KnightSourceState.Completed, new[] { conjunto }));
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>Grava UMA aquisição pela porta real do ADM e devolve o identificador dela.</summary>
    private async Task<Guid> GravarAsync(
        Guid tenantId, Guid connectorId, string ns, DateTimeOffset em, KnightSourceState state,
        IdentityObservedSet conjunto, string? normalizacao = null) =>
        await GravarAsync(tenantId, connectorId, ns, em, state, new[] { conjunto }, normalizacao);

    /// <summary>O mesmo, com o identificador ESCOLHIDO — o desempate por Guid precisa ser observável.</summary>
    private async Task GravarComIdAsync(
        Guid id, Guid tenantId, Guid connectorId, string ns, DateTimeOffset em, KnightSourceState state,
        IdentityObservedSet conjunto)
    {
        await using var db = NewContext(tenantId);
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(tenantId), Relogio);
        await store.PrepareAsync(Pedido(connectorId, ns, id, em, state, new[] { conjunto }));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> GravarAsync(
        Guid tenantId, Guid connectorId, string ns, DateTimeOffset em, KnightSourceState state,
        IReadOnlyList<IdentityObservedSet> conjuntos, string? normalizacao = null)
    {
        var id = Guid.NewGuid();
        await using var db = NewContext(tenantId);
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(tenantId), Relogio);
        await store.PrepareAsync(Pedido(connectorId, ns, id, em, state, conjuntos, normalizacao));
        await db.SaveChangesAsync();
        return id;
    }

    private static IdentityAcquisitionRequest Pedido(
        Guid connectorId, string ns, Guid id, DateTimeOffset em, KnightSourceState state,
        IReadOnlyList<IdentityObservedSet> conjuntos, string? normalizacao = null) =>
        new(id,
            new IdentityAcquisitionOrigin(connectorId, KnightSourceType.MicrosoftEntraId, ns, "Diretório sintético"),
            IdentityKnightBoundary.SchemaVersion,
            normalizacao ?? IdentityKnightBoundary.NormalizationVersion,
            em,
            ObservedAt: null,
            state,
            "Fonte SINTÉTICA de validação.",
            IdentityEvidenceFactsJson.Serialize(
                conjuntos.Select(c => KnightObservation.OfCount(
                    IdentityKnightBoundary.SignalFor(c.Set), c.ObservedCount)).ToList(), null, null),
            "[]",
            conjuntos);

    private static IdentityObservedSet Coletado(IdentityObservationSet set, params string[] ids) =>
        new(set, IdentityObservationSetOutcome.Collected, ids.Length, Objetos(ids), IsComplete: true, null);

    private static IdentityObservedSet Parcial(IdentityObservationSet set, int apurado, params string[] ids) =>
        new(set, IdentityObservationSetOutcome.Partial, apurado, Objetos(ids), IsComplete: false,
            "Enumeração truncada pela fonte.");

    private static IdentityObservedSet NaoColetado(IdentityObservationSet set, string motivo) =>
        IdentityObservedSet.NotCollected(set, IdentityObservationSetOutcome.Unavailable, motivo);

    private static IReadOnlyList<IdentityObservedObject> Objetos(params string[] ids) =>
        ids.Select(id => new IdentityObservedObject(
                id, IdentityEntityKind.User, $"Objeto {id}", $"{id}@demo.example.com",
                new[] { "Administrador Global" }, "Constatação sintética da coleta."))
            .ToList();
}
