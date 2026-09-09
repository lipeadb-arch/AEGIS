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
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-ADM-01] As invariantes do ADM de identidade em PostgreSQL 18 REAL (gate <c>AEGIS_TEST_PG</c>).
///
/// O que SÓ o banco real prova: que a migration aplica; que os índices únicos e as FKs COMPOSTAS tenant-safe
/// existem e rejeitam de verdade; e que duas coletas simultâneas do mesmo diretório não criam duas identidades
/// para o mesmo objeto. Nenhuma dessas garantias é observável no SQLite da bateria relacional.
/// </summary>
public sealed class IdentityAcquisitionPostgresTests
{
    private const string DiretorioA = "contoso-directory-a";
    private const string DiretorioB = "contoso-directory-b";

    private readonly ITestOutputHelper _output;

    public IdentityAcquisitionPostgresTests(ITestOutputHelper output) => _output = output;

    // ---- (1) Migration, unicidade e namespaces distintos ---------------------------------------------

    [Fact]
    public async Task Migration_Unicidade_ENamespacesDistintos_NoPostgresReal()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, connectorId);

        // Duas aquisições do MESMO diretório: o objeto repetido não vira uma segunda identidade.
        await GravarAsync(opt, tenant, connectorId, DiretorioA, Guid.NewGuid(), Objetos("obj-1", "obj-2"), Instante(0));
        await GravarAsync(opt, tenant, connectorId, DiretorioA, Guid.NewGuid(), Objetos("obj-1", "obj-3"), Instante(1));

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.IdentityEntities.CountAsync()).Should().Be(3, "obj-1 foi observado duas vezes, e é UM objeto");
            (await db.IdentitySourceLinks.CountAsync()).Should().Be(3);
            (await db.IdentityAcquisitions.CountAsync()).Should().Be(2, "duas coletas são dois fatos");
        }

        // O MESMO identificador em OUTRO diretório é OUTRA identidade — invariante do índice natural.
        await GravarAsync(opt, tenant, connectorId, DiretorioB, Guid.NewGuid(), Objetos("obj-1"), Instante(2));

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.IdentityEntities.CountAsync()).Should().Be(4,
                "trocar o diretório de origem não reaproveita os vínculos do anterior");
            var vinculos = await db.IdentitySourceLinks.Where(l => l.ExternalId == "obj-1").ToListAsync();
            vinculos.Select(v => v.DirectoryNamespace).Should().BeEquivalentTo(new[] { DiretorioA, DiretorioB });
        }

        // O índice único do vínculo é invariante de BANCO, não promessa do código: uma inserção manual
        // duplicando (tenant, namespace, id externo) é REJEITADA.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var existente = await db.IdentitySourceLinks.AsNoTracking()
                .FirstAsync(l => l.ExternalId == "obj-1" && l.DirectoryNamespace == DiretorioA);

            db.IdentitySourceLinks.Add(new IdentitySourceLink
            {
                TenantId = tenant,
                IdentityEntityId = existente.IdentityEntityId,
                ConnectorConfigId = connectorId,
                Provider = KnightSourceType.MicrosoftEntraId,
                DirectoryNamespace = DiretorioA,
                ExternalId = "obj-1",
                FirstLinkedAt = Instante(3),
                LastObservedAt = Instante(3),
                LastAcquisitionId = Guid.NewGuid(),
            });

            var acao = async () => await db.SaveChangesAsync();
            await acao.Should().ThrowAsync<DbUpdateException>(
                "o índice UX_IdentitySourceLink_Natural impede duas identidades para o mesmo objeto");
        }
    }

    // ---- (2) Tenant estrangeiro: leitura, vínculo e escrita ------------------------------------------

    /// <summary>
    /// Um tenant estrangeiro não LÊ a aquisição de outro (ela é indistinguível de inexistente), não a
    /// VINCULA (a FK composta recusa) e não ESCREVE nela (o guard de isolamento recusa antes do banco).
    /// </summary>
    [Fact]
    public async Task TenantEstrangeiro_NaoLe_NaoVincula_ENaoEscreve()
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

        var aquisicaoAlfa = Guid.NewGuid();
        await GravarAsync(opt, alfa, conectorAlfa, DiretorioA, aquisicaoAlfa, Objetos("obj-1"), Instante(0));

        Guid entidadeAlfa;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(alfa)))
            entidadeAlfa = await db.IdentityEntities.Select(e => e.Id).SingleAsync();

        // LEITURA: para Beta, a aquisição de Alfa simplesmente não existe.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(beta)))
        {
            var store = new IdentityAcquisitionStore(db, new SystemTenantContext(beta));
            (await store.ReadAsync(aquisicaoAlfa)).Should().BeNull(
                "a aquisição de outro ambiente é indistinguível de inexistente");
            (await db.IdentityEntities.CountAsync()).Should().Be(0);
            (await db.IdentitySourceLinks.CountAsync()).Should().Be(0);
            (await db.IdentityEntityObservations.CountAsync()).Should().Be(0);
        }

        // VÍNCULO: Beta não consegue apontar um vínculo próprio para a entidade de Alfa — a FK COMPOSTA
        // (IdentityEntityId, TenantId) não encontra (entidade de Alfa, tenant Beta).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(beta)))
        {
            db.IdentitySourceLinks.Add(new IdentitySourceLink
            {
                TenantId = beta,
                IdentityEntityId = entidadeAlfa,
                ConnectorConfigId = conectorBeta,
                Provider = KnightSourceType.MicrosoftEntraId,
                DirectoryNamespace = DiretorioB,
                ExternalId = "obj-roubado",
                FirstLinkedAt = Instante(1),
                LastObservedAt = Instante(1),
                LastAcquisitionId = Guid.NewGuid(),
            });

            var acao = async () => await db.SaveChangesAsync();
            await acao.Should().ThrowAsync<DbUpdateException>(
                "o banco recusa vincular a identidade de OUTRO ambiente");
        }

        // ESCRITA: o guard fail-closed recusa gravar uma linha carimbada com o tenant de Alfa.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(beta)))
        {
            db.IdentityEntities.Add(new IdentityEntity
            {
                TenantId = alfa,
                Kind = IdentityEntityKind.User,
                DisplayName = "Escrita cruzada",
                FirstObservedAt = Instante(1),
                LastObservedAt = Instante(1),
                CurrentAsOf = Instante(1),
                CurrentAcquisitionId = Guid.NewGuid(),
            });

            var acao = async () => await db.SaveChangesAsync();
            await acao.Should().ThrowAsync<TenantSecurityException>(
                "escrever no tenant de outro ambiente é recusado antes de chegar ao banco");
        }

        // E o ambiente de Alfa continua exatamente como estava.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(alfa)))
        {
            (await db.IdentityEntities.CountAsync()).Should().Be(1);
            (await db.IdentitySourceLinks.CountAsync()).Should().Be(1);
        }
    }

    // ---- (3) Concorrência real na reconciliação -------------------------------------------------------

    /// <summary>
    /// Seis coletas SIMULTÂNEAS do mesmo diretório, pelo CAMINHO REAL (o serviço de evidência, com a sua
    /// própria recuperação de corrida — não uma reimplementação mais tolerante feita para o teste passar).
    ///
    /// Sem o índice único e sem essa recuperação, o resultado seriam duas identidades para o mesmo objeto — e
    /// todo o resto do modelo (correlação entre conjuntos, histórico, remediação) passaria a apontar para
    /// metades diferentes da mesma pessoa.
    /// </summary>
    [Fact]
    public async Task ColetasSimultaneas_DoMesmoDiretorio_NaoDuplicamIdentidade()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, connectorId);

        var ids = new[] { "obj-1", "obj-2", "obj-3", "obj-4", "obj-5" };
        var objetos = Objetos(ids);
        const int coletas = 6;

        // Cada tarefa tem o SEU DbContext (o EF não é thread-safe) e percorre o caminho de produção inteiro:
        // resolver a configuração, coletar, traduzir, gravar a aquisição e o snapshot, e reler. Cada uma
        // observa num instante diferente, e o NOME do objeto diz qual coleta o produziu — sem isso, só se
        // provaria que nada duplicou, e não que o estado final corresponde à aquisição certa.
        var idPorColeta = await Task.WhenAll(Enumerable.Range(0, coletas).Select(i =>
            ColetarPeloServicoAsync(
                opt, tenant, new ColetorSintetico(ObjetosComPrefixo($"Coleta {i}", ids), Instante(i)))));

        var idMaisRecente = idPorColeta[coletas - 1];

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));

        (await assert.IdentityEntities.CountAsync()).Should().Be(objetos.Count,
            "concorrência não pode produzir duas identidades para o mesmo objeto");
        (await assert.IdentitySourceLinks.CountAsync()).Should().Be(objetos.Count);
        (await assert.IdentityAcquisitions.CountAsync()).Should().Be(coletas,
            "cada coleta continua sendo um fato próprio, mesmo observando o mesmo conteúdo");
        (await assert.IdentityEntityObservations.CountAsync()).Should()
            .Be(objetos.Count * coletas, "cada aquisição preserva as SUAS observações");
        (await assert.IdentityEvidenceSnapshots.CountAsync()).Should()
            .Be(1, "o snapshot agregado continua sendo UM por (tenant, conector)");

        // Todas as observações do mesmo identificador externo apontam para UMA entidade.
        var porExternalId = await assert.IdentityEntityObservations.AsNoTracking()
            .GroupBy(o => o.ExternalId)
            .Select(g => new { g.Key, Entidades = g.Select(o => o.IdentityEntityId).Distinct().Count() })
            .ToListAsync();
        porExternalId.Should().OnlyContain(x => x.Entidades == 1);

        // E — o que uma contagem jamais provaria — o estado final corresponde à aquisição MAIS RECENTE,
        // qualquer que tenha sido a ordem em que as seis transações efetivamente gravaram.
        var entidades = await assert.IdentityEntities.AsNoTracking().ToListAsync();
        entidades.Should().OnlyContain(e => e.CurrentAcquisitionId == idMaisRecente,
            "o cadastro atual deriva da coleta mais recente, não da que terminou por último");
        entidades.Should().OnlyContain(e => e.CurrentAsOf == Instante(coletas - 1));
        entidades.Should().OnlyContain(e => e.DisplayName!.StartsWith($"Coleta {coletas - 1} "));

        var vinculos = await assert.IdentitySourceLinks.AsNoTracking().ToListAsync();
        vinculos.Should().OnlyContain(l => l.LastAcquisitionId == idMaisRecente);
        vinculos.Should().OnlyContain(l => l.LastObservedAt == Instante(coletas - 1));

        var snapshot = await assert.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.LastCollectionAt.Should().Be(Instante(coletas - 1));
        snapshot.LastAttemptAt.Should().Be(Instante(coletas - 1));

        (await assert.Connectors.AsNoTracking().SingleAsync(c => c.Id == connectorId))
            .LastSyncAt.Should().Be(Instante(coletas - 1));

        _output.WriteLine(
            $"Concorrência verificada pelo caminho real: {coletas} coletas simultâneas, {objetos.Count} objetos, "
            + $"uma identidade por objeto, e o estado atual sustentado pela aquisição {idMaisRecente}.");
    }

    // ---- (4) Interleaving CONTROLADO: a escrita antiga conclui depois da nova ------------------------

    /// <summary>
    /// [correção dirigida · achado 3] A prova de que a proteção contra regressão temporal está no BANCO, e
    /// não numa comparação em memória feita sobre uma leitura que já pode estar velha quando a gravação sai.
    ///
    /// O cenário é MONTADO, não sorteado:
    ///   1. a coleta MAIS NOVA (t+5h) conclui e vira o estado atual;
    ///   2. uma conexão de teste segura a linha do conector — a MESMA trava que o caminho de produção toma —
    ///      e a coleta MAIS ANTIGA (t+1h) é iniciada: ela fica PARADA no portão, antes de qualquer leitura
    ///      que informe decisão. Que ela realmente parou é VERIFICADO em <c>pg_locks</c>, não presumido: se a
    ///      seção crítica não existisse, ninguém bloquearia e o teste falharia ao montar o cenário;
    ///   3. o portão é solto e a antiga tenta CONCLUIR depois da nova.
    ///
    /// É o interleaving perigoso na sua forma decidível: a escrita antiga é a ÚLTIMA a gravar, e ainda assim
    /// não pode virar o presente. As leituras dela acontecem dentro da seção crítica, sobre o estado já
    /// atualizado — que é exatamente a propriedade que uma comparação feita antes da trava não teria.
    ///
    /// LIMITE declarado: enfileirar as DUAS coletas ao mesmo tempo (a nova à frente, a antiga atrás) não se
    /// mostrou reproduzível neste harness, e um cenário que só às vezes se monta não é prova. A disputa
    /// genuinamente simultânea entre duas gravações fica por conta de
    /// <see cref="DuasAtualizacoesConcorrentes_DeEntidadesExistentes_MantemAMaisRecente"/> e de
    /// <see cref="ColetasSimultaneas_DoMesmoDiretorio_NaoDuplicamIdentidade"/>, que comparam o estado final
    /// com a aquisição CERTA — e não apenas contagens.
    /// </summary>
    [Fact]
    public async Task EscritaAntiga_ConcluindoDepoisDaNova_NaoRegrideOEstadoAtual()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, connectorId);

        // A coleta antiga fica BLOQUEADA de propósito no portão, e o tempo limite de comando padrão do EF
        // (30 s) mataria exatamente a espera que este teste está montando. O limite maior vale só para a
        // conexão deste teste; o produto segue com o dele.
        var esperaLonga = new DbContextOptionsBuilder<AegisScoreDbContext>()
            .UseNpgsql(pg.ConnectionString, npg => npg.CommandTimeout(180)).Options;

        // Linha de base: as entidades e os vínculos JÁ EXISTEM quando a disputa começa. É o caso difícil —
        // atualizar projeções existentes, e não criar linhas novas (que a unicidade já protegeria).
        await ColetarPeloServicoAsync(
            opt, tenant,
            new ColetorSintetico(ObjetosComPrefixo("Estado da linha de base", "obj-1", "obj-2"), Instante(0)));

        // 1) A coleta MAIS NOVA conclui e vira o estado atual.
        var coletorNovo = new ColetorSintetico(ObjetosComPrefixo("Estado NOVO", "obj-1", "obj-2"), Instante(5));
        var idNova = await ColetarPeloServicoAsync(opt, tenant, coletorNovo);

        // 2) Portão: a MESMA trava de linha que LockOriginAsync toma em produção.
        await using var portao = new NpgsqlConnection(pg.ConnectionString);
        await portao.OpenAsync();
        await using var travando = await portao.BeginTransactionAsync();
        await using (var cmd = portao.CreateCommand())
        {
            cmd.Transaction = travando;
            cmd.CommandText = "SELECT 1 FROM \"Connectors\" WHERE \"Id\" = @id AND \"TenantId\" = @t FOR UPDATE";
            cmd.Parameters.AddWithValue("id", connectorId);
            cmd.Parameters.AddWithValue("t", tenant);
            (await cmd.ExecuteScalarAsync()).Should().NotBeNull("o conector semeado precisa existir");
        }

        var coletorAntigo = new ColetorSintetico(ObjetosComPrefixo("Estado ANTIGO", "obj-1", "obj-2"), Instante(1));
        var antiga = Task.Run(() => ColetarPeloServicoAsync(esperaLonga, tenant, coletorAntigo));

        await AguardarSessoesEmEsperaAsync(
            portao, 1, "a coleta antiga precisa ficar parada no portão ANTES de decidir qualquer coisa", antiga);

        // 3) Portão liberado: a antiga tenta concluir DEPOIS da nova, e é a última a gravar.
        await travando.CommitAsync();
        var idAntiga = await antiga;

        coletorAntigo.Chamadas.Should().Be(1, "esperar pela trava não dispara uma segunda consulta ao Graph");

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));

        var vinculos = await assert.IdentitySourceLinks.AsNoTracking().ToListAsync();
        vinculos.Should().HaveCount(2, "concorrência não pode duplicar a identidade de um objeto");

        foreach (var vinculo in vinculos)
        {
            var entidade = await assert.IdentityEntities.AsNoTracking()
                .SingleAsync(e => e.Id == vinculo.IdentityEntityId);

            entidade.DisplayName.Should().StartWith("Estado NOVO",
                "a escrita ANTIGA gravou por último e ainda assim não substituiu o estado mais recente");
            entidade.CurrentAsOf.Should().Be(Instante(5));
            entidade.CurrentAcquisitionId.Should().Be(idNova,
                "o estado atual aponta para a aquisição que efetivamente o sustenta");

            vinculo.LastObservedAt.Should().Be(Instante(5), "os metadados atuais do vínculo também não regridem");
            vinculo.LastAcquisitionId.Should().Be(idNova);
        }

        var snapshot = await assert.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.LastCollectionAt.Should().Be(Instante(5), "o snapshot agregado guarda o último dado VÁLIDO");
        snapshot.LastAttemptAt.Should().Be(Instante(5),
            "a última TENTATIVA é a mais recente, não a que terminou por último");

        var conector = await assert.Connectors.AsNoTracking().SingleAsync(c => c.Id == connectorId);
        conector.LastSyncAt.Should().Be(Instante(5), "a saúde da integração segue o mesmo critério temporal");

        // A evidência ANTIGA não foi descartada — ela apenas não virou o presente.
        var antigaGravada = await assert.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == idAntiga);
        antigaGravada.AcquiredAt.Should().Be(Instante(1));
        (await assert.IdentityEntityObservations.AsNoTracking().CountAsync(o => o.AcquisitionId == idAntiga))
            .Should().Be(2, "a coleta atrasada continua sendo evidência histórica completa");

        _output.WriteLine(
            $"Interleaving controlado: nova={idNova} (t+5h) concluiu antes; antiga={idAntiga} (t+1h) ficou "
            + "parada na trava, gravou por último e não regrediu entidade, vínculo, snapshot nem saúde.");
    }

    // ---- (5) Duas atualizações concorrentes de entidades JÁ EXISTENTES -------------------------------

    /// <summary>
    /// [correção dirigida · achado 3] Duas coletas simultâneas do mesmo diretório atualizando entidades que
    /// JÁ EXISTEM, com valores diferentes. Sem serialização, o resultado dependeria de qual transação gravou
    /// por último; com ela, o vencedor é sempre a coleta mais recente — a asserção é a MESMA em qualquer
    /// ordem de execução, e é isso que a torna determinística em vez de probabilística.
    /// </summary>
    [Fact]
    public async Task DuasAtualizacoesConcorrentes_DeEntidadesExistentes_MantemAMaisRecente()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, connectorId);

        await GravarAsync(opt, tenant, connectorId, DiretorioA, Guid.NewGuid(),
            ObjetosComPrefixo("Linha de base", "obj-1", "obj-2", "obj-3"), Instante(0));

        var coletorAntigo = new ColetorSintetico(ObjetosComPrefixo("Valor ANTIGO", "obj-1", "obj-2", "obj-3"), Instante(10));
        var coletorNovo = new ColetorSintetico(ObjetosComPrefixo("Valor NOVO", "obj-1", "obj-2", "obj-3"), Instante(11));

        var resultados = await Task.WhenAll(
            ColetarPeloServicoAsync(opt, tenant, coletorAntigo),
            ColetarPeloServicoAsync(opt, tenant, coletorNovo));

        var idNova = resultados[1];

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));

        (await assert.IdentityEntities.AsNoTracking().CountAsync()).Should().Be(3);
        (await assert.IdentityAcquisitions.AsNoTracking().CountAsync()).Should().Be(3,
            "a linha de base e as duas coletas concorrentes são três fatos distintos");

        var entidades = await assert.IdentityEntities.AsNoTracking().ToListAsync();
        entidades.Should().OnlyContain(e => e.CurrentAcquisitionId == idNova,
            "o cadastro atual é o da coleta mais recente, e não o da que gravou por último");
        entidades.Should().OnlyContain(e => e.DisplayName!.StartsWith("Valor NOVO"));
        entidades.Should().OnlyContain(e => e.CurrentAsOf == Instante(11));

        var snapshot = await assert.IdentityEvidenceSnapshots.AsNoTracking().SingleAsync();
        snapshot.LastCollectionAt.Should().Be(Instante(11));
    }

    // ---- (6) Retry CONCORRENTE do mesmo identificador de aquisição -----------------------------------

    /// <summary>
    /// [correção dirigida · achado 2] O MESMO identificador de aquisição, com o MESMO conteúdo, reapresentado
    /// por duas gravações simultâneas. O resultado tem de ser consistente: uma única aquisição, sem
    /// duplicação de observações, sem conflito — e sem que a segunda tentativa seja tratada como uma coleta
    /// nova, que é o erro que faria o mesmo fato ser contado duas vezes.
    /// </summary>
    [Fact]
    public async Task RetryConcorrente_DoMesmoIdentificador_ConvergeSemDuplicar_NemVirarAquisicaoNova()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var connectorId = Guid.NewGuid();
        await MigrarESemearAsync(opt, tenant, connectorId);

        var aquisicao = Guid.NewGuid();
        var objetos = ObjetosComPrefixo("Objeto reapresentado", "obj-1", "obj-2", "obj-3");

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            GravarAsync(opt, tenant, connectorId, DiretorioA, aquisicao, objetos, Instante(3))));

        await using var assert = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));

        (await assert.IdentityAcquisitions.AsNoTracking().CountAsync()).Should().Be(1,
            "quatro reapresentações do MESMO identificador são UMA aquisição");
        (await assert.IdentityEntityObservations.AsNoTracking().CountAsync()).Should().Be(objetos.Count,
            "o retry idempotente não duplica a evidência");
        (await assert.IdentityObservationSetStates.AsNoTracking().CountAsync()).Should().Be(1);
        (await assert.IdentityEntities.AsNoTracking().CountAsync()).Should().Be(objetos.Count);
        (await assert.IdentitySourceLinks.AsNoTracking().CountAsync()).Should().Be(objetos.Count);
    }

    // ---- Infraestrutura ------------------------------------------------------------------------------

    private static DateTimeOffset Instante(int horas) =>
        new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero).AddHours(horas);

    private static IReadOnlyList<IdentityObservedObject> Objetos(params string[] ids) =>
        ObjetosComPrefixo("Objeto", ids);

    /// <summary>
    /// Objetos sintéticos com um PREFIXO de nome. O prefixo é o que permite dizer, olhando o cadastro atual,
    /// QUAL coleta o produziu — uma contagem de linhas jamais responderia isso.
    ///
    /// Nome PRÓPRIO, e não uma sobrecarga de <c>Objetos</c>: com <c>params</c> as duas assinaturas competem, e
    /// <c>Objetos("obj-1")</c> passaria a significar "prefixo obj-1, nenhum objeto" — uma lista vazia que os
    /// testes leriam como coleta bem-sucedida sem objeto algum.
    /// </summary>
    private static IReadOnlyList<IdentityObservedObject> ObjetosComPrefixo(string prefixo, params string[] ids) =>
        ids.Select(id => new IdentityObservedObject(
            id, IdentityEntityKind.User, $"{prefixo} {id}", $"{id}@demo.example.com",
            new[] { "Administrador Global" }, "Constatação sintética da coleta.")).ToList();

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

    private static async Task GravarAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connectorId, string directoryNamespace,
        Guid acquisitionId, IReadOnlyList<IdentityObservedObject> objetos, DateTimeOffset em)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var store = new IdentityAcquisitionStore(db, new SystemTenantContext(tenant));

        // A MESMA unidade de trabalho do caminho de produção: transação, trava da origem e só então as
        // leituras que informam decisões. Escrever sem a trava aqui tornaria o teste mais tolerante do que o
        // produto — e é justamente a trava que está sob prova.
        await using var tx = await db.Database.BeginTransactionAsync();
        await store.LockOriginAsync(
            new IdentityAcquisitionOrigin(
                connectorId, KnightSourceType.MicrosoftEntraId, directoryNamespace, "Diretório sintético"));
        await store.PrepareAsync(Pedido(connectorId, directoryNamespace, acquisitionId, objetos, em));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Executa UMA coleta pelo caminho REAL (Evidence Fabric → ADM → snapshot) e devolve o identificador da
    /// aquisição gravada. Nada do AEGIS é simulado: só a fronteira do Microsoft Graph.
    /// </summary>
    private static async Task<Guid> ColetarPeloServicoAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, ColetorSintetico coletor)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        var contexto = new SystemTenantContext(tenant);
        var service = new IdentityEvidenceService(
            db,
            new AegisScore.Infrastructure.Knight.KnightCollectorRegistry(new IKnightCollector[] { coletor }),
            new ConfigSintetica(DiretorioA),
            new IdentityAcquisitionStore(db, contexto),
            contexto);

        var resultado = await service.CollectAsync();
        resultado.AcquisitionId.Should().NotBeNull("cada coleta grava a SUA aquisição");
        return resultado.AcquisitionId!.Value;
    }

    /// <summary>
    /// Espera, com prazo, até que exatamente <paramref name="esperadas"/> sessões estejam BLOQUEADAS em
    /// trava neste banco. É o que torna o interleaving montado, e não sorteado: se a seção crítica não
    /// existisse, ninguém bloquearia e o cenário falharia ao ser montado — em vez de passar por acaso.
    /// </summary>
    private static async Task AguardarSessoesEmEsperaAsync(
        NpgsqlConnection observador, int esperadas, string porque, params Task[] emCurso)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        var ultima = -1;

        while (DateTime.UtcNow < limite)
        {
            // Uma tarefa que terminou (com erro ou não) nunca vai bloquear. Esperar por ela em silêncio
            // transformaria a causa real num tempo esgotado sem explicação.
            foreach (var tarefa in emCurso)
            {
                if (tarefa.IsFaulted) await tarefa;
                if (tarefa.IsCompleted)
                    throw new InvalidOperationException(
                        $"{porque}: a coleta concluiu sem bloquear. Sem seção crítica no banco, o cenário de "
                        + "regressão temporal não existe — e o que passaria aqui seria a ausência da proteção.");
            }

            // Pedido de trava NÃO CONCEDIDO é o sinal direto de "esta sessão está esperando por aquela".
            await using (var cmd = observador.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid "
                    + "WHERE NOT l.granted AND a.datname = current_database() AND l.pid <> pg_backend_pid()";
                ultima = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            if (ultima >= esperadas) return;
            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"{porque}: esperava {esperadas} sessão(ões) bloqueada(s) em trava, observei {ultima}. "
            + "Sem bloqueio não há seção crítica — e o cenário de regressão temporal não pôde ser montado."
            + Environment.NewLine + $"Tarefas: {string.Join(", ", emCurso.Select(t => t.Status.ToString()))}"
            + Environment.NewLine + await DiagnosticoDeSessoesAsync(observador));
    }

    /// <summary>Retrato das sessões do banco — existe para que uma espera frustrada diga POR QUE frustrou.</summary>
    private static async Task<string> DiagnosticoDeSessoesAsync(NpgsqlConnection observador)
    {
        await using var cmd = observador.CreateCommand();
        cmd.CommandText =
            "SELECT a.pid, a.state, coalesce(a.wait_event_type,'-'), coalesce(a.wait_event,'-'), "
            + "left(coalesce(a.query,''), 90) FROM pg_stat_activity a WHERE a.datname = current_database()";

        var linhas = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            linhas.Add($"  pid={reader.GetInt32(0)} state={reader.GetString(1)} "
                       + $"wait={reader.GetString(2)}/{reader.GetString(3)} sql={reader.GetString(4)}");

        return "Sessões no banco:" + Environment.NewLine + string.Join(Environment.NewLine, linhas);
    }

    /// <summary>Coletor sintético que devolve os objetos combinados — a única fronteira substituída.</summary>
    private sealed class ColetorSintetico : IKnightCollector
    {
        private readonly IReadOnlyList<IdentityObservedObject> _objetos;
        private readonly DateTimeOffset _em;

        public ColetorSintetico(IReadOnlyList<IdentityObservedObject> objetos, DateTimeOffset em)
        {
            _objetos = objetos;
            _em = em;
        }

        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

        /// <summary>Quantas vezes a FRONTEIRA externa foi exercida — prova de "uma aquisição = uma coleta".</summary>
        public int Chamadas => _chamadas;

        private int _chamadas;

        public Task<KnightCollectionResult> CollectAsync(
            KnightCollectionContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _chamadas);
            return Task.FromResult(new KnightCollectionResult(
                KnightSourceType.MicrosoftEntraId,
                KnightSourceState.Completed,
                "Diretório sintético de validação",
                new KnightFactSet(new[]
                {
                    KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, _objetos.Count),
                }),
                new[]
                {
                    new KnightCapabilityStatus(
                        KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                },
                _em,
                "Fonte SINTÉTICA de validação; nenhuma consulta ao Microsoft Graph foi feita.",
                AffectedObjects: new[]
                {
                    new KnightAffectedObjectEvidence(
                        KnightSignalKey.PrivilegedAccountsTotal,
                        _objetos.Select(o => new KnightAffectedObjectFact(
                            o.ExternalId, IdentityKnightBoundary.ToKnightKind(o.Kind),
                            o.DisplayName, o.UserPrincipalName, o.Roles, o.Detail)).ToList(),
                        IsComplete: true),
                }));
        }
    }

    /// <summary>Configuração resolvida sintética — o <c>AzureTenantId</c> É o namespace do diretório.</summary>
    private sealed class ConfigSintetica : IKnightSourceConfigurationProvider
    {
        private readonly string _namespace;
        public ConfigSintetica(string ns) => _namespace = ns;

        public Task<KnightSourceConfiguration> ResolveAsync(
            Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(
                source == KnightSourceType.MicrosoftEntraId
                    ? new KnightEntraIdConfiguration(_namespace, "client", "segredo-sintetico")
                    : new KnightSourceNotConfigured(source));

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(
            Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private static IdentityAcquisitionRequest Pedido(
        Guid connectorId, string directoryNamespace, Guid acquisitionId,
        IReadOnlyList<IdentityObservedObject> objetos, DateTimeOffset em) =>
        new(
            acquisitionId,
            new IdentityAcquisitionOrigin(
                connectorId, KnightSourceType.MicrosoftEntraId, directoryNamespace, "Diretório sintético"),
            IdentityKnightBoundary.SchemaVersion,
            IdentityKnightBoundary.NormalizationVersion,
            em,
            ObservedAt: null,
            KnightSourceState.Completed,
            "Fonte SINTÉTICA de validação.",
            IdentityEvidenceFactsJson.Serialize(
                new[] { KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, objetos.Count) }, null, null),
            "[]",
            new[]
            {
                new IdentityObservedSet(
                    IdentityObservationSet.PrivilegedRoleMember,
                    IdentityObservationSetOutcome.Collected,
                    objetos.Count, objetos, IsComplete: true),
            });
}
