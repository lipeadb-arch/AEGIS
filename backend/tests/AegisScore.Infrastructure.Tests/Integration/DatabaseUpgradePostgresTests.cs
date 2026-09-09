using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-MVP-PRE-ADM-01] ATUALIZAÇÃO VERIFICÁVEL de um banco que JÁ EXISTE.
///
/// As três migrations desta linha de entrega — <c>Product02_KnightAffectedObjects</c>,
/// <c>Product03_RemediationAndReport</c> e <c>Product03_FrozenCycleApplicability</c> — nunca foram aplicadas a
/// um ambiente real. O que estava provado até aqui era a migração sobre banco VAZIO (o smoke do container) e a
/// aplicação da sequência inteira de uma vez (<c>RemediationPostgresTests</c>). Faltava a prova que interessa a
/// quem opera: partir do PONTO ANTERIOR, com dados JÁ GRAVADOS, e chegar do outro lado sem perder nem inventar
/// nada.
///
/// O que este teste faz, sobre um PostgreSQL 18 descartável:
///   1. migra até EXATAMENTE a migration anterior às três (<c>DevicePosture_MicrosoftIntune</c>);
///   2. insere dados legados representativos por SQL cru — a forma que o schema tinha ANTES (usar o modelo EF
///      atual escreveria colunas que ainda não existem, e o teste provaria outra coisa);
///   3. aplica a atualização com o <c>AegisScore.DbMigrator</c> REAL, o mesmo binário do procedimento de
///      implantação, com migrations + seed + verificação final;
///   4. verifica que o dado antigo continua lá e LEGÍVEL pelo modelo novo;
///   5. verifica que o histórico antigo NÃO ganhou detalhe de objetos afetados, ação congelada nem
///      aplicabilidade de ciclo — ausência de informação não pode virar afirmação;
///   6. verifica a PRONTIDÃO (<c>--verify-only</c>) e a IDEMPOTÊNCIA de uma nova execução.
///
/// Nada aqui toca banco de cliente: o database é criado e destruído pela própria bateria.
/// </summary>
public sealed class DatabaseUpgradePostgresTests
{
    /// <summary>A última migration ANTERIOR ao pacote — o ponto de partida de um ambiente desatualizado.</summary>
    private const string PontoAnterior = "20260904131931_DevicePosture_MicrosoftIntune";

    /// <summary>
    /// O MARCO HISTÓRICO desta prova: as três migrations Product02/Product03 que nunca foram aplicadas a um
    /// ambiente real. Continuam sendo o que este teste garante que atravessa sem perder nem inventar nada — e
    /// permanecem as PRIMEIRAS da fila, na ordem exata do procedimento operacional.
    /// </summary>
    private static readonly string[] MarcoHistorico =
    {
        "20260906153231_Product02_KnightAffectedObjects",
        "20260907200205_Product03_RemediationAndReport",
        "20260908120427_Product03_FrozenCycleApplicability",
    };

    /// <summary>
    /// [AEGIS-ADM-01] A sequência CUMULATIVA que o procedimento precisa aplicar hoje: o marco histórico mais o
    /// que veio depois. A lista cresce a cada pacote que acrescenta schema; o que NÃO pode mudar é a
    /// verificação de preservação — a razão de este teste existir é provar que dado antigo sobrevive, não
    /// apenas que o migrator termina com código zero.
    /// </summary>
    private static readonly string[] Pendentes =
        MarcoHistorico.Concat(new[] { "20260909031324_Adm01_IdentityDataModel" }).ToArray();

    private readonly ITestOutputHelper _output;

    public DatabaseUpgradePostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Atualizacao_DoPontoAnterior_PreservaOHistorico_NaoInventaDetalhe_EEhIdempotente()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var opt = pg.DbOptions();

        var tenantId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var indicatorResultId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var riskId = Guid.NewGuid();
        var legacyPlanId = Guid.NewGuid();
        const string hashLegado = "1111111111111111111111111111111111111111111111111111111111111111";

        // ---- 1) O ambiente "de antes": schema exatamente no ponto anterior às três migrations ----------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PontoAnterior);

            var aplicadas = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            aplicadas.Should().Contain(PontoAnterior);
            aplicadas.Should().NotContain(Pendentes, "o ponto de partida é um ambiente ainda NÃO atualizado");

            var pendentes = (await db.Database.GetPendingMigrationsAsync()).ToList();
            pendentes.Should().BeEquivalentTo(Pendentes, options => options.WithStrictOrdering(),
                "a lista pendente é exatamente, e nesta ordem, o que o procedimento de atualização vai aplicar");
            pendentes.Take(MarcoHistorico.Length).Should().Equal(MarcoHistorico,
                "o marco histórico Product02/Product03 continua sendo a PRIMEIRA coisa que um ambiente "
                + "desatualizado aplica — os pacotes seguintes se empilham depois dele, nunca no lugar dele");

            await SemearLegadoAsync(db, tenantId, runId, indicatorResultId, snapshotId, riskId, legacyPlanId, hashLegado);
        }

        // ---- 2) A ATUALIZAÇÃO, pelo migrator real -----------------------------------------------------
        var exit = await AegisApiHarness.RunMigratorAsync(pg.ConnectionString);
        exit.Should().Be(AegisScore.DbMigrator.MigratorExitCode.Success,
            "a atualização de um ambiente existente precisa terminar com verificação APROVADA");

        // ---- 3) O dado antigo continua lá, e legível pelo modelo NOVO ---------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantId)))
        {
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(Pendentes);
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            run.Source.Should().Be("Coleta legada");
            run.Score.Should().Be(23);

            var indicador = await db.KnightIndicatorResults.AsNoTracking().SingleAsync(i => i.Id == indicatorResultId);
            indicador.IndicatorId.Should().Be("AK-ENTRA-001");
            indicador.AffectedObjectCount.Should().Be(2, "a contagem apurada na época permanece a que foi apurada");
            indicador.Evidence.Should().Be("2 de 12 contas privilegiadas sem MFA.");

            var foto = await db.PostureSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId);
            foto.ContentHash.Should().Be(hashLegado, "a fotografia é append-only: a migração não a reescreve");
            foto.Score.Should().Be(23);
            (await db.PostureSnapshotIndicators.AsNoTracking().CountAsync(i => i.SnapshotId == snapshotId))
                .Should().Be(1, "os itens congelados da fotografia legada continuam íntegros");

            var planoLegado = await db.ActionPlans.AsNoTracking().SingleAsync(p => p.Id == legacyPlanId);
            planoLegado.RiskId.Should().Be(riskId, "o plano legado continua sendo tratamento de um RISCO");
            planoLegado.Description.Should().Be("Plano de tratamento anterior a esta linha de entrega.");
        }

        // ---- 4) O histórico antigo NÃO ganha detalhe, ação ou aplicabilidade inventados ---------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantId)))
        {
            var indicador = await db.KnightIndicatorResults.AsNoTracking().SingleAsync(i => i.Id == indicatorResultId);
            indicador.HasAffectedDetail.Should().BeFalse(
                "uma avaliação anterior à preservação de objetos NÃO passa a ter lista; ela se declara sem detalhe");
            indicador.AffectedDetailComplete.Should().BeFalse();
            indicador.AffectedDetailLimitation.Should().BeNull("campo ausente permanece ausente");
            (await db.KnightAffectedObjects.AsNoTracking().CountAsync()).Should()
                .Be(0, "nada é retropreenchido com a coleta de hoje — isso seria apresentar o presente como prova do passado");

            // [AEGIS-ADM-01] A avaliação legada não passa a ter procedência de coleta. Ela é anterior ao ADM:
            // carimbá-la com a aquisição de hoje seria apresentar o presente como prova do passado.
            var runLegado = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            runLegado.IdentityAcquisitionId.Should().BeNull(
                "uma avaliação anterior ao ADM não sabe de qual coleta nasceu, e não é retropreenchida");
            (await db.IdentityAcquisitions.AsNoTracking().CountAsync()).Should()
                .Be(0, "a migração cria estrutura, não evidência: nenhuma aquisição é inventada");
            (await db.IdentityEntities.AsNoTracking().CountAsync()).Should()
                .Be(0, "nenhuma identidade canônica nasce de uma migração de schema");
            (await db.IdentitySourceLinks.AsNoTracking().CountAsync()).Should().Be(0);
            (await db.IdentityEntityObservations.AsNoTracking().CountAsync()).Should().Be(0);
            (await db.IdentityObservationSetStates.AsNoTracking().CountAsync()).Should().Be(0);

            var foto = await db.PostureSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId);
            foto.SourceRunId.Should().BeNull(
                "a fotografia legada não sabe de qual execução nasceu, e dizer isso é melhor do que adivinhar");
            foto.ClientName.Should().BeNull("o nome do cliente não é buscado hoje para carimbar um relatório de ontem");
            (await db.PostureSnapshotActionItems.AsNoTracking().CountAsync(a => a.SnapshotId == snapshotId))
                .Should().Be(0, "uma fotografia publicada antes das ações não passa a ter ações congeladas");

            // A leitura de PRODUTO concorda com o banco: a fotografia legada não exibe ação nem aplicabilidade.
            var servico = new PostureSnapshotService(db, new SystemTenantContext(tenantId), new NistSignalMapper(db));
            var detalhe = await servico.GetAsync(snapshotId);
            detalhe.Should().NotBeNull();
            (detalhe!.ActionItems is null || detalhe.ActionItems.Count == 0).Should().BeTrue(
                "sem ações congeladas, o relatório legado não inventa nenhuma");
            detalhe.Summary.SourceRunId.Should().BeNull();
            detalhe.Indicators.Should().ContainSingle(i => i.IndicatorId == "AK-ENTRA-001");

            // O plano legado de RISCO não é reapresentado como remediação de achado do KNIGHT.
            var planoLegado = await db.ActionPlans.AsNoTracking().SingleAsync(p => p.Id == legacyPlanId);
            planoLegado.KnightIndicatorId.Should().BeNull();
            planoLegado.OriginRunId.Should().BeNull();
            planoLegado.OriginSourceType.Should().BeNull("procedência não existia; ela não é adivinhada");
            planoLegado.OriginMode.Should().BeNull();
            planoLegado.CycleStartedAt.Should().BeNull("um ciclo que nunca foi pactuado não recebe data retroativa");

            var remediacao = new AegisScore.Infrastructure.Remediation.RemediationService(
                db, new SystemTenantContext(tenantId), TimeProvider.System);
            (await remediacao.ListAsync(new ActionPlanFilter(null, false, null, null))).Should()
                .BeEmpty("a Central de ações mostra remediação de ACHADO — não o registro de riscos legado");
        }

        // ---- 5) Prontidão e nova execução idempotente -------------------------------------------------
        var verificacao = await AegisApiHarness.RunMigratorAsync(pg.ConnectionString, "--verify-only");
        verificacao.Should().Be(AegisScore.DbMigrator.MigratorExitCode.Success,
            "a verificação de prontidão é o gate que a API repete no boot");

        var reexecucao = await AegisApiHarness.RunMigratorAsync(pg.ConnectionString);
        reexecucao.Should().Be(AegisScore.DbMigrator.MigratorExitCode.Success,
            "rodar o migrator de novo é o caso normal de uma implantação repetida");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantId)))
        {
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.KnightAssessmentRuns.AsNoTracking().CountAsync()).Should().Be(1);
            (await db.KnightIndicatorResults.AsNoTracking().CountAsync()).Should().Be(1);
            (await db.PostureSnapshots.AsNoTracking().CountAsync()).Should().Be(1);
            (await db.ActionPlans.AsNoTracking().CountAsync()).Should().Be(1);
            (await db.PostureSnapshots.AsNoTracking().SingleAsync()).ContentHash.Should().Be(hashLegado,
                "a segunda execução não altera um único byte do que já estava publicado");

            // O catálogo semeado também é idempotente: uma segunda execução não duplica a referência.
            (await db.FrameworkVersions.AsNoTracking().CountAsync(f => f.IsActive)).Should()
                .Be(1, "duas framework ativas quebrariam o boot da API — é o que o índice parcial impede");
        }

        _output.WriteLine(
            $"Atualização verificada: {PontoAnterior} -> {string.Join(" -> ", Pendentes)} " +
            $"(marco histórico preservado: {string.Join(" -> ", MarcoHistorico)}; " +
            "migrator real, dados legados preservados, reexecução idempotente).");
    }

    /// <summary>
    /// Dados legados por SQL cru, com as colunas que o schema tinha ANTES das três migrations. É cru de
    /// propósito: o modelo EF de hoje escreveria colunas inexistentes naquele ponto, e o INSERT falharia — ou,
    /// pior, o teste passaria a exercitar um schema que o ambiente do cliente não tem.
    /// </summary>
    private static async Task SemearLegadoAsync(
        AegisScoreDbContext db, Guid tenantId, Guid runId, Guid indicatorResultId,
        Guid snapshotId, Guid riskId, Guid legacyPlanId, string hashLegado)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tenants" ("Id","Name","Slug","Status","CreatedAt")
            VALUES ({0}, 'Cliente Legado', 'cliente-legado', 1, now());
            """, tenantId);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "KnightAssessmentRuns"
              ("Id","TenantId","Mode","SourceType","SourceState","Source","Status","CatalogVersion",
               "ScoreFormulaVersion","StartedAt","CompletedAt","Score","Coverage","PassedCount","ExposedCount",
               "MitigatedCount","NotEvaluatedCount","ErrorCount","NotApplicableCount","AdvisoryFromAi","CreatedAt")
            VALUES ({0}, {1}, 0, 0, 0, 'Coleta legada', 1, 'legado-1.0', 'legado-1.0',
                    now() - interval '90 days', now() - interval '90 days', 23, 1, 1, 3, 1, 0, 0, 0, false,
                    now() - interval '90 days');
            """, runId, tenantId);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "KnightIndicatorResults"
              ("Id","TenantId","RunId","IndicatorId","Title","Category","Severity","Status","Evidence",
               "AffectedObjectCount","NistCodes","MitreTechniques","Recommendation","SourceType","CollectedAt","CreatedAt")
            VALUES ({0}, {1}, {2}, 'AK-ENTRA-001', 'Contas privilegiadas sem MFA', 0, 3, 1,
                    '2 de 12 contas privilegiadas sem MFA.', 2, '["PR.AA-03"]'::jsonb, '["T1078"]'::jsonb,
                    'Registrar metodo resistente a phishing.', 0, now() - interval '90 days', now() - interval '90 days');
            """, indicatorResultId, tenantId, runId);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "PostureSnapshots"
              ("Id","TenantId","Type","SchemaVersion","FormulaVersion","CatalogVersion","SemanticFamily",
               "SourceType","SourceLabel","CapturedAt","Score","Coverage","EvaluatedItems","EligibleItems",
               "CompliantCount","NonCompliantCount","MitigatedCount","NotEvaluatedCount","ErrorCount",
               "NotApplicableCount","AchievedPoints","PossiblePoints","EligiblePoints","DataRecency",
               "ContentHash","CreatedAt")
            VALUES ({0}, {1}, 1, 'knight-1.0', 'legado-1.0', 'legado-1.0', 'knight|legado-1.0|legado-1.0',
                    0, 'Coleta legada', now() - interval '89 days', 23, 1, 5, 5, 1, 3, 1, 0, 0, 0,
                    23, 100, 100, now() - interval '90 days', {2}, now() - interval '89 days');
            """, snapshotId, tenantId, hashLegado);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "PostureSnapshotIndicators"
              ("Id","TenantId","SnapshotId","IndicatorId","Title","Category","Severity","Status","Evidence",
               "AffectedObjectCount","NistCodes","MitreTechniques","SourceType","CollectedAt","CreatedAt")
            VALUES ({0}, {1}, {2}, 'AK-ENTRA-001', 'Contas privilegiadas sem MFA', 0, 3, 1,
                    '2 de 12 contas privilegiadas sem MFA.', 2, '["PR.AA-03"]'::jsonb, '["T1078"]'::jsonb,
                    0, now() - interval '90 days', now() - interval '89 days');
            """, Guid.NewGuid(), tenantId, snapshotId);

        // Um plano de ação LEGADO — tratamento de risco, o único tipo que existia. A migration torna RiskId
        // anulável; provar que a linha antiga (com risco) sobrevive é a razão de ela estar aqui.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Risks" ("Id","TenantId","Code","Title","Classification","RegisteredAt","CreatedAt")
            VALUES ({0}, {1}, 'R-001', 'Risco legado', 2, now() - interval '120 days', now() - interval '120 days');
            """, riskId, tenantId);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "ActionPlans"
              ("Id","TenantId","RiskId","Treatment","Status","Description","ResponsiblePerson","CreatedAt")
            VALUES ({0}, {1}, {2}, 1, 0, 'Plano de tratamento anterior a esta linha de entrega.',
                    'Equipe legada', now() - interval '120 days');
            """, legacyPlanId, tenantId, riskId);
    }
}
