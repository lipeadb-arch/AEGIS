using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-ADM-02] O que SÓ o PostgreSQL real prova sobre o histórico mensal e a retenção do ADM (gate
/// <c>AEGIS_TEST_PG</c>).
///
/// Três famílias de garantia, nenhuma delas observável na bateria relacional em SQLite:
///   • a MIGRATION aplica sobre um banco que já tem dados do ADM-01, e a chave de varredura derivada é
///     preenchida a partir do que já estava lá — sem inventar detalhe expirado no histórico;
///   • os índices únicos e as FKs COMPOSTAS tenant-safe REJEITAM de verdade, e um tenant estrangeiro não lê
///     nem escreve consolidação alheia;
///   • as corridas acontecem: retenção × referenciamento por avaliação, e duas passadas simultâneas da
///     manutenção sobre a mesma origem.
/// </summary>
public sealed class IdentityHistoryRetentionPostgresTests
{
    /// <summary>A migration do ADM-01 — o ponto de partida de um ambiente que ainda não tem histórico.</summary>
    private const string PontoAnterior = "20260909031324_Adm01_IdentityDataModel";

    private const string DiretorioA = "contoso-directory-a";

    private static readonly DateTimeOffset Agora = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public IdentityHistoryRetentionPostgresTests(ITestOutputHelper output) => _output = output;

    // ---- (1) Migration sobre banco com dados do ADM-01 -----------------------------------------------

    /// <summary>
    /// A atualização de um ambiente que JÁ tem aquisições gravadas. A chave de varredura é DERIVADA da coluna
    /// que já existe — se ela ficasse com a data sentinela, toda a evidência antiga seria varrida como se
    /// tivesse sido adquirida no ano 1 e o primeiro ciclo de retenção a levaria embora.
    ///
    /// E o carimbo de detalhe expirado permanece NULO no histórico: nada foi expurgado, e afirmar que foi
    /// seria inventar uma perda que não aconteceu.
    /// </summary>
    [Fact]
    public async Task Migration_SobreBancoComDadosDoAdm01_DerivaAVarredura_ESemInventarExpurgo()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var conector = Guid.NewGuid();
        var aquisicaoLegada = Guid.NewGuid();
        var adquiridaEm = new DateTimeOffset(2026, 3, 7, 22, 40, 0, TimeSpan.Zero);

        // Uma segunda linha legada COLADA na virada do dia UTC. É onde um backfill que passe por conversão de
        // fuso escorrega: o instante mudaria de dia (e de mês, na virada certa), e a evidência apareceria numa
        // janela que não é a dela.
        var aquisicaoNaVirada = Guid.NewGuid();
        var naViradaEm = new DateTimeOffset(2026, 2, 28, 23, 59, 59, 999, TimeSpan.Zero);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PontoAnterior);

            (await db.Database.GetPendingMigrationsAsync()).Should().Contain(
                m => m.EndsWith("Adm02_IdentityMonthlyHistoryRetention", StringComparison.Ordinal),
                "o ponto de partida é um ambiente ainda SEM histórico mensal");

            await SemearLegadoPorSqlAsync(db, tenant, conector, aquisicaoLegada, adquiridaEm);
            await SemearAquisicaoLegadaAsync(db, tenant, conector, aquisicaoNaVirada, naViradaEm);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            var legada = await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == aquisicaoLegada);
            legada.AcquiredAtUtc.Should().Be(adquiridaEm.UtcDateTime,
                "a chave de varredura é derivada do instante que já estava gravado");
            legada.DetailRetiredAt.Should().BeNull("nada foi expurgado — a migration não afirma perda");
            legada.AcquiredAt.Should().Be(adquiridaEm, "o instante original não é reescrito");

            var naVirada = await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == aquisicaoNaVirada);
            naVirada.AcquiredAtUtc.Should().Be(naViradaEm.UtcDateTime,
                "o último milissegundo de fevereiro continua em fevereiro — nenhum fuso entra no caminho");
            IdentityAdmRetentionPolicy.MonthOf(naVirada.AcquiredAt).Should().Be(new DateOnly(2026, 2, 1));

            (await db.IdentityEntityObservations.CountAsync(o => o.AcquisitionId == aquisicaoLegada))
                .Should().Be(1, "a evidência antiga continua lá");
            (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(0,
                "a migration cria a estrutura; consolidar é trabalho da manutenção, não dela");
        }

        // E a manutenção enxerga a aquisição legada pela chave derivada — o mês dela aparece na série.
        await ManterAsync(opt, consolidar: true, remover: false);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var marco = await db.IdentityMonthlyRollups.AsNoTracking().Include(r => r.Sets)
                .SingleAsync(r => r.Month == new DateOnly(2026, 3, 1));
            marco.SnapshotAcquisitionId.Should().Be(aquisicaoLegada);
            marco.Sets.Single().ObservedCount.Should().Be(1);
        }
    }

    // ---- (2) Índices únicos e FKs compostas rejeitam de verdade ---------------------------------------

    /// <summary>
    /// As invariantes do histórico moram no BANCO, não numa promessa do código: um segundo "abril" para a
    /// mesma origem é rejeitado, um valor por conjunto de tenant divergente é rejeitado, e remover a
    /// consolidação leva os conjuntos dela junto.
    /// </summary>
    [Fact]
    public async Task IndiceNatural_EFkComposta_DoHistorico_RejeitamNoBanco()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var alfa = Guid.NewGuid();
        var beta = Guid.NewGuid();
        var conectorAlfa = Guid.NewGuid();
        await MigrarESemearAsync(opt, alfa, conectorAlfa);
        await SemearTenantAsync(opt, beta, Guid.NewGuid());

        await GravarAsync(opt, alfa, conectorAlfa, DiretorioA, Instante(-40), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await ManterAsync(opt, consolidar: true, remover: false);

        Guid rollupId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(alfa)))
        {
            var existente = await db.IdentityMonthlyRollups.AsNoTracking()
                .FirstAsync(r => r.Month == IdentityAdmRetentionPolicy.MonthOf(Instante(-40)));
            rollupId = existente.Id;

            db.IdentityMonthlyRollups.Add(new IdentityMonthlyRollup
            {
                TenantId = alfa,
                Provider = KnightSourceType.MicrosoftEntraId,
                DirectoryNamespace = DiretorioA,
                Month = existente.Month,
                ConsolidatedAt = Agora,
                ConsolidationVersion = IdentityAdmRetentionPolicy.ConsolidationVersion,
            });

            var duplicar = async () => await db.SaveChangesAsync();
            await duplicar.Should().ThrowAsync<DbUpdateException>(
                "UX_IdentityMonthlyRollup_Natural impede dois meses iguais para a mesma origem");
        }

        // FK COMPOSTA tenant-safe: um valor por conjunto de OUTRO tenant não se prende a esta consolidação.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(beta)))
        {
            db.IdentityMonthlyRollupSets.Add(new IdentityMonthlyRollupSet
            {
                TenantId = beta,
                RollupId = rollupId,
                Set = IdentityObservationSet.PrivilegedRoleMember,
                Outcome = IdentityObservationSetOutcome.Collected,
                ObservedCount = 999,
                IsComplete = true,
            });

            var cruzar = async () => await db.SaveChangesAsync();
            await cruzar.Should().ThrowAsync<DbUpdateException>(
                "a FK (RollupId, TenantId) faz o próprio banco recusar filho de tenant divergente");
        }

        // Cascade: a consolidação some e leva os valores por conjunto.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(alfa)))
        {
            db.IdentityMonthlyRollups.Remove(await db.IdentityMonthlyRollups.FirstAsync(r => r.Id == rollupId));
            await db.SaveChangesAsync();
            (await db.IdentityMonthlyRollupSets.CountAsync(s => s.RollupId == rollupId)).Should().Be(0);
        }
    }

    /// <summary>Um tenant estrangeiro não LÊ a consolidação de outro: ela é indistinguível de inexistente.</summary>
    [Fact]
    public async Task TenantEstrangeiro_NaoLeConsolidacaoAlheia()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var alfa = Guid.NewGuid();
        var beta = Guid.NewGuid();
        var conectorAlfa = Guid.NewGuid();
        var conectorBeta = Guid.NewGuid();
        await MigrarESemearAsync(opt, alfa, conectorAlfa);
        await SemearTenantAsync(opt, beta, conectorBeta);

        await GravarAsync(opt, alfa, conectorAlfa, DiretorioA, Instante(-40), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        await ManterAsync(opt, consolidar: true, remover: false);

        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(beta));
        (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(0,
            "as consolidações do outro ambiente não existem para este");
        (await db.IdentityMonthlyRollupSets.CountAsync()).Should().Be(0);
    }

    // ---- (3) Retenção × referenciamento por avaliação -------------------------------------------------

    /// <summary>
    /// A corrida que a trava existe para eliminar. A retenção já decidiu, numa consulta anterior, que a
    /// aquisição não é referenciada por ninguém; no intervalo, uma avaliação passa a citá-la.
    ///
    /// O cenário é MONTADO, não sorteado: a "avaliação" abre a transação, FIXA a aquisição e segura; a
    /// retenção começa e precisa BLOQUEAR na trava exclusiva. Se não bloqueasse, a proteção não existiria — e o
    /// teste falharia ao montar o cenário, em vez de passar por acaso.
    /// </summary>
    [Fact]
    public async Task Retencao_ERefereciamentoConcorrente_NaoSeCruzam_EAEvidenciaCitadaSobrevive()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var conector = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, conector);

        var vencida = await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-120),
            KnightSourceState.Completed, Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        await ManterAsync(opt, consolidar: true, remover: false);

        // A "avaliação": transação aberta, aquisição FIXADA, execução ainda não gravada.
        await using var avaliacao = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        await using var tx = await avaliacao.Database.BeginTransactionAsync();
        var store = new IdentityAcquisitionStore(avaliacao, new SystemTenantContext(tenant));
        var fixada = await store.PinForReferenceAsync(vencida);
        fixada.Exists.Should().BeTrue("a aquisição existe e foi fixada");
        fixada.DetailAvailable.Should().BeTrue("neste instante o detalhe dela ainda está íntegro");

        // A retenção entra agora e tem de esperar pela trava exclusiva. Ela consolida na MESMA transação em
        // que remove — pedir remoção sem consolidação é recusado, e por bons motivos.
        var retencao = Task.Run(() => ManterAsync(opt, consolidar: true, remover: true));

        await using (var observador = new NpgsqlConnection(pg.ConnectionString))
        {
            await observador.OpenAsync();
            await AguardarSessaoBloqueadaAsync(observador, retencao);
        }

        avaliacao.KnightAssessmentRuns.Add(NovaExecucao(vencida));
        await avaliacao.SaveChangesAsync();
        await tx.CommitAsync();

        var relatorio = await retencao;

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        (await assert.IdentityAcquisitions.AnyAsync(a => a.Id == vencida)).Should().BeTrue(
            "a citação foi confirmada antes de a retenção decidir — a evidência que a sustenta fica");

        var registro = await assert.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == vencida);
        registro.DetailRetiredAt.Should().NotBeNull("o detalhe operacional venceu, e isso é declarado");
        (await assert.IdentityObservationSetStates.SingleAsync(s => s.AcquisitionId == vencida)).PreservedCount
            .Should().Be(2, "a contagem original da coleta permanece");

        var origem = relatorio.Directories.Single();
        origem.AcquisitionsRemoved.Should().Be(0);
        origem.Protected.Should().ContainSingle().Which.AcquisitionId.Should().Be(vencida);
    }

    // ---- (4) Duas passadas simultâneas da manutenção --------------------------------------------------

    /// <summary>
    /// Duas manutenções na mesma origem ao mesmo tempo: a trava consultiva do diretório as serializa. Sem ela,
    /// as duas leriam o mesmo estado, ambas concluiriam que o mês não existe e a segunda esbarraria no índice
    /// único — ou pior, produziria dois "abril" se o índice não estivesse lá.
    /// </summary>
    [Fact]
    public async Task DuasManutencoesSimultaneas_NaMesmaOrigem_ConvergemSemDuplicar()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var conector = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, conector);

        await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-40), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-10), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        await Task.WhenAll(
            Task.Run(() => ManterAsync(opt, consolidar: true, remover: true)),
            Task.Run(() => ManterAsync(opt, consolidar: true, remover: true)));

        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(
            IdentityAdmRetentionPolicy.RetainedMonths, "a janela tem 12 meses — nem um a mais");

        var mesRecente = await db.IdentityMonthlyRollups.AsNoTracking().Include(r => r.Sets)
            .SingleAsync(r => r.Month == IdentityAdmRetentionPolicy.MonthOf(Instante(-10)));
        mesRecente.Sets.Should().ContainSingle("cada mês tem um valor por conjunto, não dois");
        mesRecente.Sets.Single().ObservedCount.Should().Be(1);
    }

    // ---- (5) Consolidação sobrevive ao expurgo; replay recusado --------------------------------------

    /// <summary>
    /// A consolidação carrega a própria cópia agregada — e é por isso que ela sobrevive quando a aquisição que
    /// a originou é removida. Uma reconsolidação POSTERIOR ao expurgo não pode esvaziar o mês: o histórico de
    /// 12 meses não pode depender de uma evidência cujo prazo é de 90 dias.
    ///
    /// E reapresentar uma aquisição com detalhe expirado é recusado no banco real, como no relacional.
    /// </summary>
    [Fact]
    public async Task Consolidacao_SobreviveAoExpurgo_EReplayDeDetalheExpirado_ERecusado()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var conector = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, conector);

        var vencidaEm = Instante(-150);
        var semReferencia = await GravarAsync(opt, tenant, conector, DiretorioA, vencidaEm,
            KnightSourceState.Completed, Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));
        var citada = await GravarAsync(opt, tenant, conector, DiretorioA, vencidaEm.AddHours(2),
            KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3"));

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.KnightAssessmentRuns.Add(NovaExecucao(citada));
            await db.SaveChangesAsync();
        }

        await ManterAsync(opt, consolidar: true, remover: true);

        // Uma SEGUNDA passada, agora sobre um mês cuja evidência já foi expurgada.
        await ManterAsync(opt, consolidar: true, remover: true);

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));

        (await assert.IdentityAcquisitions.AnyAsync(a => a.Id == semReferencia)).Should().BeFalse();
        (await assert.IdentityAcquisitions.AnyAsync(a => a.Id == citada)).Should().BeTrue();

        var mes = await assert.IdentityMonthlyRollups.AsNoTracking().Include(r => r.Sets)
            .SingleAsync(r => r.Month == IdentityAdmRetentionPolicy.MonthOf(vencidaEm));
        mes.SnapshotAcquisitionId.Should().Be(citada);
        mes.AcquisitionCount.Should().Be(2, "a consolidação não regride quando a evidência que a alimentou sai");
        mes.Sets.Single().ObservedCount.Should().Be(3);

        // Identidades e vínculos ficam: ausência numa população não é exclusão de identidade.
        (await assert.IdentityEntities.CountAsync()).Should().Be(3);
        (await assert.IdentitySourceLinks.CountAsync()).Should().Be(3);

        var store = new IdentityAcquisitionStore(assert, new SystemTenantContext(tenant));
        var replay = async () => await store.PrepareAsync(Pedido(
            conector, DiretorioA, citada, vencidaEm.AddHours(2), KnightSourceState.Completed,
            new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2", "obj-3") }));

        await replay.Should().ThrowAsync<IdentityAcquisitionDetailRetiredException>();

        // E o replay do que saiu por INTEIRO: a linha não existe mais, então a recusa por detalhe expirado não
        // alcança este caso. Quem responde é a fronteira gravada no mês.
        var replayDoRemovido = async () => await store.PrepareAsync(Pedido(
            conector, DiretorioA, semReferencia, vencidaEm, KnightSourceState.Completed,
            new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2") }));

        await replayDoRemovido.Should().ThrowAsync<IdentityAcquisitionRetentionSweptException>();

        assert.ChangeTracker.Clear();
        (await assert.IdentityAcquisitions.CountAsync(a => a.Id == semReferencia)).Should().Be(0,
            "a recusa acontece ANTES de qualquer escrita");
    }

    // ---- (6) A pré-condição da retenção, no banco real ------------------------------------------------

    /// <summary>
    /// Remover sem consolidar é recusado; e uma FALHA na consolidação impede a remoção CORRESPONDENTE — não
    /// "adia", impede: consolidação e retenção correm na mesma transação, e o rollback é do conjunto.
    ///
    /// A falha é injetada onde ela realmente aparece — no BANCO, num gatilho que aborta a escrita da
    /// consolidação — em vez de num seam de teste dentro do serviço. Depois de removido o gatilho, a mesma
    /// passada faz o que sempre faria: é a prova de que nada além da falha estava impedindo o expurgo.
    /// </summary>
    [Fact]
    public async Task RetencaoSemConsolidacaoSegura_ERecusada_EFalhaNaConsolidacao_ImpedeARemocao()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var conector = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, conector);

        var vencida = await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-150),
            KnightSourceState.Completed, Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));
        await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-5),
            KnightSourceState.Completed, Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        // (a) Remoção pedida SEM consolidação: recusada antes de tocar em qualquer linha.
        var semConsolidar = async () => await ManterAsync(opt, consolidar: false, remover: true);
        await semConsolidar.Should().ThrowAsync<ArgumentException>();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.IdentityAcquisitions.AnyAsync(a => a.Id == vencida)).Should().BeTrue();
            (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(0);
        }

        // (b) Consolidação que FALHA no banco: a remoção correspondente não acontece.
        await ExecutarSqlAsync(opt,
            "CREATE FUNCTION aegis_falha_consolidacao() RETURNS trigger LANGUAGE plpgsql AS "
            + "$$ BEGIN RAISE EXCEPTION 'falha injetada na consolidacao'; END $$;");
        await ExecutarSqlAsync(opt,
            "CREATE TRIGGER aegis_falha_consolidacao BEFORE INSERT OR UPDATE ON \"IdentityMonthlyRollups\" "
            + "FOR EACH ROW EXECUTE FUNCTION aegis_falha_consolidacao();");

        var comFalha = await ManterAsync(opt, consolidar: true, remover: true);

        comFalha.Failed.Should().ContainSingle("a origem com problema é reportada, não silenciada")
            .Which.Directory.DirectoryNamespace.Should().Be(DiretorioA);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.IdentityMonthlyRollups.CountAsync()).Should().Be(0, "nada foi consolidado");
            (await db.IdentityAcquisitions.AnyAsync(a => a.Id == vencida)).Should().BeTrue(
                "e por isso nada foi removido — a evidência vencida continua inteira");
            (await db.IdentityEntityObservations.CountAsync(o => o.AcquisitionId == vencida)).Should().Be(1,
                "nem o detalhe dela foi tocado");
        }

        // (c) Sem a falha, a MESMA passada faz o que deveria.
        await ExecutarSqlAsync(opt, "DROP TRIGGER aegis_falha_consolidacao ON \"IdentityMonthlyRollups\";");

        var normal = await ManterAsync(opt, consolidar: true, remover: true);
        normal.Failed.Should().BeEmpty();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.IdentityMonthlyRollups.CountAsync())
                .Should().Be(IdentityAdmRetentionPolicy.RetainedMonths);
            (await db.IdentityAcquisitions.AnyAsync(a => a.Id == vencida)).Should().BeFalse();
        }
    }

    // ---- (7) Coleta ATRASADA concorrente entre consolidar e remover -----------------------------------

    /// <summary>
    /// A janela que uma consolidação commitada ANTES da retenção deixaria aberta: uma coleta atrasada daquele
    /// mês entra depois de a consolidação fechar e é removida pela retenção que acaba de começar — evidência
    /// que sai sem nunca ter entrado no histórico, e sem nada de onde recalculá-la.
    ///
    /// O cenário é MONTADO: uma coleta abre a transação, toma a trava do diretório e grava a atrasada sem
    /// commitar; a manutenção entra e precisa BLOQUEAR. Só depois do commit ela segue — e então tem de
    /// consolidar a atrasada ANTES de removê-la. A prova é a fotografia do mês: se a atrasada não tivesse
    /// participado, o mês exibiria a coleta anterior e contaria uma só.
    /// </summary>
    [Fact]
    public async Task ColetaAtrasadaConcorrente_ParticipaDaConsolidacao_AntesDeSerRemovida()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var conector = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, conector);

        var anterior = await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-150),
            KnightSourceState.Completed, Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1"));

        // Uma coleta RECENTE assume o cadastro atual dos dois objetos: assim nenhuma das vencidas sustenta
        // projeção alguma, e a fronteira sob teste é só a da consolidação.
        await GravarAsync(opt, tenant, conector, DiretorioA, Instante(-5), KnightSourceState.Completed,
            Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2"));

        var atrasada = Guid.NewGuid();
        await using var coleta = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        await using var txColeta = await coleta.Database.BeginTransactionAsync();
        var storeColeta = new IdentityAcquisitionStore(coleta, new SystemTenantContext(tenant));
        await storeColeta.LockOriginAsync(new IdentityAcquisitionOrigin(
            conector, KnightSourceType.MicrosoftEntraId, DiretorioA, "Diretório sintético"));
        await storeColeta.PrepareAsync(Pedido(
            conector, DiretorioA, atrasada, Instante(-140), KnightSourceState.Completed,
            new[] { Coletado(IdentityObservationSet.PrivilegedRoleMember, "obj-1", "obj-2") }));
        await coleta.SaveChangesAsync();

        var manutencao = Task.Run(() => ManterAsync(opt, consolidar: true, remover: true));

        await using (var observador = new NpgsqlConnection(pg.ConnectionString))
        {
            await observador.OpenAsync();
            await AguardarSessaoBloqueadaAsync(observador, manutencao);
        }

        await txColeta.CommitAsync();
        await manutencao;

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));

        var mes = await assert.IdentityMonthlyRollups.AsNoTracking().Include(r => r.Sets)
            .SingleAsync(r => r.Month == IdentityAdmRetentionPolicy.MonthOf(Instante(-150)));

        mes.SnapshotAcquisitionId.Should().Be(atrasada,
            "a atrasada foi consolidada ANTES de a retenção decidir sobre ela");
        mes.Sets.Single().ObservedCount.Should().Be(2, "e os valores do mês são os dela");
        mes.AcquisitionCount.Should().Be(2, "as duas coletas do mês estão contadas");
        mes.RetiredAcquisitionCount.Should().Be(2, "e as duas saíram, cada uma contabilizada uma única vez");

        (await assert.IdentityAcquisitions.AnyAsync(a => a.Id == anterior)).Should().BeFalse();
        (await assert.IdentityAcquisitions.AnyAsync(a => a.Id == atrasada)).Should().BeFalse();
        (await assert.IdentityEntities.CountAsync()).Should().Be(2,
            "identidades canônicas não saem por retenção — ausência numa população não é exclusão");
    }

    // ---- Infraestrutura ------------------------------------------------------------------------------

    private static DateTimeOffset Instante(int dias) => Agora.AddDays(dias);

    private static Task<IdentityAdmMaintenanceReport> ManterAsync(
        DbContextOptions<AegisScoreDbContext> opt, bool consolidar, bool remover) =>
        new IdentityAdmMaintenanceService(opt, new FakeTimeProvider(Agora))
            .RunAsync(new IdentityAdmMaintenanceRequest(Now: Agora, Consolidate: consolidar, Remove: remover));

    private static KnightAssessmentRun NovaExecucao(Guid acquisitionId) => new()
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

    /// <summary>
    /// Espera até que ALGUMA sessão esteja bloqueada em trava neste banco. É o que torna o interleaving
    /// montado: se a retenção não bloqueasse, a tarefa concluiria e o cenário falharia ao ser construído — em
    /// vez de passar por acaso.
    /// </summary>
    private static async Task AguardarSessaoBloqueadaAsync(NpgsqlConnection observador, Task emCurso)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < limite)
        {
            if (emCurso.IsFaulted) await emCurso;
            if (emCurso.IsCompleted)
                throw new InvalidOperationException(
                    "A retenção concluiu sem bloquear. Sem trava exclusiva sobre os candidatos, retenção e "
                    + "referenciamento decidiriam sobre a mesma foto antiga — e o que passaria aqui seria a "
                    + "ausência da proteção.");

            await using (var cmd = observador.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid "
                    + "WHERE NOT l.granted AND a.datname = current_database() AND l.pid <> pg_backend_pid()";
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) >= 1) return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            "Esperava a retenção bloqueada em trava e não observei bloqueio algum: o cenário de corrida não "
            + "pôde ser montado.");
    }

    private static async Task ExecutarSqlAsync(DbContextOptions<AegisScoreDbContext> opt, string sql)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task MigrarESemearAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connectorId)
    {
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        await SemearTenantAsync(opt, tenant, connectorId);
    }

    private static async Task SemearTenantAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connectorId)
    {
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenant, Name = $"Cliente {tenant:N}", Slug = $"t-{tenant:N}"[..20], Status = TenantStatus.Active,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(new ConnectorConfig
            {
                Id = connectorId, TenantId = tenant,
                Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Microsoft Entra ID · AEGIS KNIGHT",
                AuthType = ConnectorAuthType.OAuthClientCredentials,
                Enabled = true, EncryptedSettings = "{\"clientSecret\":\"s\"}",
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Dados do ADM-01 gravados por SQL CRU, na forma que o schema tinha ANTES desta migration. Usar o modelo
    /// EF atual escreveria colunas que ainda não existem, e o teste provaria outra coisa.
    /// </summary>
    private static async Task SemearLegadoPorSqlAsync(
        AegisScoreDbContext db, Guid tenant, Guid conector, Guid aquisicao, DateTimeOffset adquiridaEm)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tenants" ("Id","Name","Slug","Status","CreatedAt")
            VALUES ({0}, 'Cliente Legado', 'cliente-legado', 1, now());
            """, tenant);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Connectors"
              ("Id","TenantId","Provider","Capability","DisplayName","AuthType","EncryptedSettings",
               "Enabled","SyncIntervalMinutes","LastStatus","CreatedAt")
            VALUES ({0}, {1}, 0, 3, 'Microsoft Entra ID', 1, '{{"clientSecret":"s"}}', true, 360, 0, now());
            """, conector, tenant);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "IdentityAcquisitions"
              ("Id","TenantId","ConnectorConfigId","Provider","DirectoryNamespace","SourceLabel",
               "SchemaVersion","NormalizationVersion","AcquiredAt","State","FactsJson","CapabilitiesJson",
               "ContentFingerprint","CreatedAt")
            VALUES ({0}, {1}, {2}, 0, {3}, 'Microsoft Entra ID', 'aegis-adm-identity-v1',
                    'aegis-adm-identity-normalization-v1', {4}, 3, '{{}}', '[]', 'legado', now());
            """, aquisicao, tenant, conector, DiretorioA, adquiridaEm);

        var entidade = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "IdentityEntities"
              ("Id","TenantId","Kind","FirstObservedAt","LastObservedAt","CurrentAsOf","CurrentAcquisitionId","CreatedAt")
            VALUES ({0}, {1}, 0, {2}, {2}, {2}, {3}, now());
            """, entidade, tenant, adquiridaEm, aquisicao);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "IdentitySourceLinks"
              ("Id","TenantId","IdentityEntityId","ConnectorConfigId","Provider","DirectoryNamespace",
               "ExternalId","FirstLinkedAt","LastObservedAt","LastAcquisitionId","CreatedAt")
            VALUES ({0}, {1}, {2}, {3}, 0, {4}, 'obj-legado', {5}, {5}, {6}, now());
            """, Guid.NewGuid(), tenant, entidade, conector, DiretorioA, adquiridaEm, aquisicao);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "IdentityEntityObservations"
              ("Id","TenantId","AcquisitionId","IdentityEntityId","Set","ExternalId","Kind","RolesObserved",
               "ObservedAt","CreatedAt")
            VALUES ({0}, {1}, {2}, {3}, 0, 'obj-legado', 0, '[]', {4}, now());
            """, Guid.NewGuid(), tenant, aquisicao, entidade, adquiridaEm);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "IdentityObservationSetStates"
              ("Id","TenantId","AcquisitionId","Set","Outcome","ObservedCount","PreservedCount","IsComplete","CreatedAt")
            VALUES ({0}, {1}, {2}, 0, 1, 1, 1, true, now());
            """, Guid.NewGuid(), tenant, aquisicao);
    }

    /// <summary>Só o CABEÇALHO de uma aquisição legada, por SQL cru — para testar a fronteira do tempo.</summary>
    private static Task SemearAquisicaoLegadaAsync(
        AegisScoreDbContext db, Guid tenant, Guid conector, Guid aquisicao, DateTimeOffset adquiridaEm) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "IdentityAcquisitions"
              ("Id","TenantId","ConnectorConfigId","Provider","DirectoryNamespace","SourceLabel",
               "SchemaVersion","NormalizationVersion","AcquiredAt","State","FactsJson","CapabilitiesJson",
               "ContentFingerprint","CreatedAt")
            VALUES ({0}, {1}, {2}, 0, {3}, 'Microsoft Entra ID', 'aegis-adm-identity-v1',
                    'aegis-adm-identity-normalization-v1', {4}, 3, '{{}}', '[]', 'legado-virada', now());
            """, aquisicao, tenant, conector, DiretorioA, adquiridaEm);

    private static async Task<Guid> GravarAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connectorId, string ns,
        DateTimeOffset em, KnightSourceState state, params IdentityObservedSet[] conjuntos)
    {
        var id = Guid.NewGuid();
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(tenant));

        await using var tx = await db.Database.BeginTransactionAsync();
        await store.LockOriginAsync(
            new IdentityAcquisitionOrigin(connectorId, KnightSourceType.MicrosoftEntraId, ns, "Diretório sintético"));
        await store.PrepareAsync(Pedido(connectorId, ns, id, em, state, conjuntos));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return id;
    }

    private static IdentityAcquisitionRequest Pedido(
        Guid connectorId, string ns, Guid id, DateTimeOffset em, KnightSourceState state,
        IReadOnlyList<IdentityObservedSet> conjuntos) =>
        new(id,
            new IdentityAcquisitionOrigin(connectorId, KnightSourceType.MicrosoftEntraId, ns, "Diretório sintético"),
            IdentityKnightBoundary.SchemaVersion,
            IdentityKnightBoundary.NormalizationVersion,
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
        new(set, IdentityObservationSetOutcome.Collected, ids.Length,
            ids.Select(id => new IdentityObservedObject(
                    id, IdentityEntityKind.User, $"Objeto {id}", $"{id}@demo.example.com",
                    new[] { "Administrador Global" }, "Constatação sintética da coleta."))
                .ToList(),
            IsComplete: true, null);
}
