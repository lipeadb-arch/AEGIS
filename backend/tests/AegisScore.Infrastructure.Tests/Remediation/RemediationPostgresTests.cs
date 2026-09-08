using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Remediation;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Remediation;

/// <summary>
/// [AEGIS-MVP-PRODUCT-03] Validação INTEGRADA da remediação e do relatório em PostgreSQL 18 REAL (gate
/// <c>AEGIS_TEST_PG</c>, banco descartável do <see cref="PostgresProbe"/>).
///
/// Por que ela existe: a suíte SQLite prova a SEMÂNTICA (duplicação, transições, suficiência de evidência),
/// mas não prova que a MIGRATION aplica sobre o histórico, que o ÍNDICE PARCIAL único existe de fato no banco
/// (é ele que barra a corrida que a checagem em código deixaria passar), que as FKs compostas tenant-safe
/// impedem um filho de tenant divergente, nem que o relatório publicado continua consistente ao ser
/// reexportado. Aqui a migration roda de verdade e o PDF é gerado a partir da linha persistida.
///
/// LIMITE declarado: sem harness de integração HTTP (<c>WebApplicationFactory</c>), o pipeline HTTP e a
/// autenticação JWT NÃO são executados. Nenhuma chamada externa: a fonte é sintética e o PostgreSQL é o
/// descartável do CI.
/// </summary>
public sealed class RemediationPostgresTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RemediationActor Actor = new(Guid.NewGuid(), "Analista");

    private readonly ITestOutputHelper _output;

    public RemediationPostgresTests(ITestOutputHelper output) => _output = output;

    // ---- (1) Migration + jornada completa + relatório consistente na reexportação -------------------

    [Fact]
    public async Task MigrationAplica_EAJornadaCompletaSustentaORelatorioPublicado()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrateAndSeedAsync(opt, tenant, "Cliente Relatório");

        Guid runOrigem, runNova, snapshotId, planoId;

        // --- achado inicial -------------------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runOrigem = (await RunAsync(db, tenant, withoutMfa: 3, at: T0)).Id;

        // --- ação: responsável e prazo ---------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var svc = RemediationFor(db, tenant);
            var criada = await svc.CreateForFindingAsync(
                new CreateFindingActionPlanCommand(
                    runOrigem, "AK-ENTRA-001", "Registrar segundo fator nas contas administrativas",
                    "Registrar método resistente a phishing.", "Equipe de Identidade", "TI",
                    new DateOnly(2026, 10, 15)),
                Actor);

            criada.Should().NotBeNull();
            planoId = criada!.Id;
            criada.OriginRunId.Should().Be(runOrigem);
            criada.OriginAffectedCount.Should().Be(3);
        }

        // --- execução relatada ------------------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var svc = RemediationFor(db, tenant);
            var atual = await svc.GetAsync(planoId);
            var apos = await svc.RecordExecutionAsync(
                planoId, new RecordExecutionCommand(atual!.Version, "MFA registrado nas três contas.", "CHAMADO-9001"),
                Actor);
            apos!.Status.Should().Be(ActionPlanStatus.AguardandoValidacao);
            apos.LatestValidation.Should().BeNull("relatar execução não comprova correção");
        }

        // --- nova evidência ---------------------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runNova = (await RunAsync(db, tenant, withoutMfa: 0, at: T0.AddDays(10))).Id;

        // --- decisão de validação ---------------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var svc = RemediationFor(db, tenant);
            var atual = await svc.GetAsync(planoId);
            var validado = await svc.ValidateAsync(
                planoId, new ValidateActionPlanCommand(atual!.Version, runNova, null, null), Actor);

            var v = validado!.LatestValidation!;
            v.Outcome.Should().Be(ActionPlanValidationOutcome.ExposureCleared);
            v.ValidationRunId.Should().Be(runNova, "a prova aponta para a coleta posterior");
            validado.OriginRunId.Should().Be(runOrigem, "a origem não é reescrita pela validação");
            v.ObservedBefore.Should().Be(3);
            v.ObservedAfter.Should().Be(0);
            v.ObjectsNoLongerPresent.Should().Be(3);

            var concluida = await svc.UpdateAsync(
                planoId, new UpdateActionPlanCommand(validado.Version, null, null, null, null, null, ActionPlanStatus.Concluido),
                Actor);
            concluida!.Status.Should().Be(ActionPlanStatus.Concluido);
        }

        // --- relatório publicado a partir da avaliação NOVA -------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var detail = await PostureFor(db, tenant).PublishAsync(PostureSnapshotType.Knight, null, runNova);
            snapshotId = detail.Summary.Id;

            detail.Summary.SourceRunId.Should().Be(runNova);
            detail.Summary.ClientName.Should().Be("Cliente Relatório");
            var acao = detail.ActionItems.Should().ContainSingle().Which;
            acao.Status.Should().Be(nameof(ActionPlanStatus.Concluido));
            acao.ValidationOutcome.Should().Be(nameof(ActionPlanValidationOutcome.ExposureCleared));
            acao.ComparedBySets.Should().BeTrue();
        }

        // --- exportação: o hash confere e o conteúdo é o CONGELADO ------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var exporter = new PostureSnapshotExporter(db);
            var pdf1 = await exporter.ExportAsync(snapshotId, PostureExportFormat.Pdf);
            pdf1.Should().NotBeNull("hash divergente bloquearia a exportação");
            pdf1!.Content.Length.Should().BeGreaterThan(1000);

            // Depois de publicado, o plano MUDA. O relatório histórico não pode absorver isso.
            var svc = RemediationFor(db, tenant);
            var atual = await svc.GetAsync(planoId);
            await svc.UpdateAsync(
                planoId,
                new UpdateActionPlanCommand(atual!.Version, "Título reescrito depois da publicação", null, null, null, null, null),
                Actor);

            var pdf2 = await exporter.ExportAsync(snapshotId, PostureExportFormat.Pdf);
            pdf2.Should().NotBeNull("o conteúdo publicado continua íntegro — o hash cobre o que foi congelado");

            var relido = await PostureFor(db, tenant).GetAsync(snapshotId);
            relido!.ActionItems!.Single().Title.Should().Be("Registrar segundo fator nas contas administrativas",
                "reexportar traz o que foi publicado, não o estado de agora");
        }

        _output.WriteLine($"Jornada completa em PostgreSQL real: achado {runOrigem} → plano {planoId} → " +
                          $"evidência {runNova} → relatório {snapshotId}.");
    }

    // ---- (2) O BANCO barra a ação ativa duplicada — e libera um novo ciclo -------------------------

    [Fact]
    public async Task IndiceParcialUnico_BarraSegundaAcaoATIVA_ELiberaAOrigemQuandoEncerrada()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrateAndSeedAsync(opt, tenant, "Cliente Índice");

        Guid runId, planoId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runId = (await RunAsync(db, tenant, withoutMfa: 2, at: T0)).Id;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var criada = await RemediationFor(db, tenant).CreateForFindingAsync(
                new CreateFindingActionPlanCommand(runId, "AK-ENTRA-001", "Ação", null, null, null, null), Actor);
            planoId = criada!.Id;
        }

        // Um INSERT CRU não passa pela checagem do serviço — é para isso que o índice parcial existe: a
        // corrida entre duas requisições simultâneas criaria duas ações ativas para o mesmo achado.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var inserirParalela = async () => await db.Database.ExecuteSqlRawAsync(
                InsertPlanSql, Guid.NewGuid(), tenant, "AK-ENTRA-001", (int)ActionPlanStatus.Aberto,
                (int)KnightSourceType.Demo, (int)KnightAssessmentMode.Demo);

            var violacao = (await inserirParalela.Should().ThrowAsync<PostgresException>(
                "o próprio banco precisa recusar uma segunda ação ATIVA para o mesmo achado")).Which;
            violacao.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            violacao.ConstraintName.Should().Be("UX_ActionPlans_ActiveByFinding",
                "é ESTE índice que o serviço traduz em conflito de negócio — nenhum outro");
        }

        // Uma ação de PROCEDÊNCIA diferente (coleta real) para o MESMO achado convive com a de demonstração:
        // a chave inclui fonte e modo justamente para que um treino não ocupe a origem do trabalho real.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var inserirReal = async () => await db.Database.ExecuteSqlRawAsync(
                InsertPlanSql, Guid.NewGuid(), tenant, "AK-ENTRA-001", (int)ActionPlanStatus.Aberto,
                (int)KnightSourceType.MicrosoftEntraId, (int)KnightAssessmentMode.Live);

            await inserirReal.Should().NotThrowAsync(
                "demonstração e coleta real são dois problemas distintos sob o mesmo identificador de achado");
        }

        // Encerrada a primeira, a origem fica livre: o problema pode reaparecer e merecer um ciclo novo.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var svc = RemediationFor(db, tenant);
            var atual = await svc.GetAsync(planoId);
            // Encerrar exige BASE registrada: relato de execução e uma decisão de validação sobre ele.
            var executado = await svc.RecordExecutionAsync(planoId,
                new RecordExecutionCommand(atual!.Version, "MFA registrado.", null), Actor);
            var validado = await svc.ValidateAsync(planoId,
                new ValidateActionPlanCommand(executado!.Version, null, "CHAMADO-77", null), Actor);
            await svc.UpdateAsync(planoId,
                new UpdateActionPlanCommand(validado!.Version, null, null, null, null, null, ActionPlanStatus.Concluido), Actor);

            var novoCiclo = await svc.CreateForFindingAsync(
                new CreateFindingActionPlanCommand(runId, "AK-ENTRA-001", "Segundo ciclo", null, null, null, null), Actor);
            novoCiclo.Should().NotBeNull("o índice é PARCIAL de propósito — ação encerrada não bloqueia o futuro");
        }

        // E um plano LEGADO de risco (indicador nulo) nunca colide com outro: o índice o exclui.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var risco = new Risk { TenantId = tenant, Code = "SEC0001", Title = "Risco legado" };
            db.Risks.Add(risco);
            db.ActionPlans.Add(new ActionPlan { RiskId = risco.Id, Description = "A", Status = ActionPlanStatus.Aberto });
            db.ActionPlans.Add(new ActionPlan { RiskId = risco.Id, Description = "B", Status = ActionPlanStatus.Aberto });
            var gravar = async () => await db.SaveChangesAsync();
            await gravar.Should().NotThrowAsync("dois planos de risco sem indicador continuam válidos");
        }
    }

    // ---- (3) FK COMPOSTA tenant-safe na trilha e nas validações ------------------------------------

    [Fact]
    public async Task TrilhaEValidacao_DeTenantDivergente_SaoRecusadasPeloBanco()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await MigrateAndSeedAsync(opt, tenantA, "Cliente A");
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "Cliente B", Slug = "b-" + tenantB.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        Guid planoA;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var runId = (await RunAsync(db, tenantA, withoutMfa: 2, at: T0)).Id;
            planoA = (await RemediationFor(db, tenantA).CreateForFindingAsync(
                new CreateFindingActionPlanCommand(runId, "AK-ENTRA-001", "Ação de A", null, null, null, null), Actor))!.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var trilhaCruzada = async () => await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""ActionPlanEvents""
                  (""Id"", ""TenantId"", ""ActionPlanId"", ""Kind"", ""At"", ""ActorName"", ""CreatedAt"")
                  VALUES ({0}, {1}, {2}, 1, now(), 'intruso', now())",
                Guid.NewGuid(), tenantB, planoA);

            (await trilhaCruzada.Should().ThrowAsync<PostgresException>(
                "o query filter apenas ESCONDERIA a linha; a FK composta impede que ela exista"))
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

            var validacaoCruzada = async () => await db.Database.ExecuteSqlRawAsync(
                // PrecedesReportedExecution entra explicitamente: a coluna e NOT NULL sem default, e omiti-la
                // faria o banco recusar por NOT NULL (23502) ANTES de chegar a FK — o teste passaria a
                // "provar" uma restricao que nao chegou a ser exercida.
                @"INSERT INTO ""ActionPlanValidations""
                  (""Id"", ""TenantId"", ""ActionPlanId"", ""IndicatorId"", ""Method"", ""Outcome"",
                   ""PrecedesReportedExecution"", ""ComparedBySets"", ""Rationale"", ""DecidedAt"",
                   ""DecidedByName"", ""CreatedAt"")
                  VALUES ({0}, {1}, {2}, 'AK-ENTRA-001', 0, 0, false, false, 'forjada', now(), 'intruso', now())",
                Guid.NewGuid(), tenantB, planoA);

            (await validacaoCruzada.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }
    }

    // ---- (4) O relatório executivo não expõe lista nominal de identidades --------------------------

    [Fact]
    public async Task RelatorioExecutivo_NaoLevaListaNominalDeIdentidades_EIdentificaAsTresDatas()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrateAndSeedAsync(opt, tenant, "ClientePdfSmoke");

        Guid runId, snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runId = (await RunAsync(db, tenant, withoutMfa: 2, at: T0)).Id;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            await RemediationFor(db, tenant).CreateForFindingAsync(
                new CreateFindingActionPlanCommand(
                    runId, "AK-ENTRA-001", "Registrar segundo fator", null, "Equipe de Identidade", "TI",
                    new DateOnly(2026, 10, 15)),
                Actor);
            snapshotId = (await PostureFor(db, tenant).PublishAsync(PostureSnapshotType.Knight, null, runId)).Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshot = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Indicators).Include(s => s.ActionItems)
                .FirstAsync(s => s.Id == snapshotId);
            var texto = Deaccent(ExtractPdfText(PostureSnapshotPdfWriter.Write(snapshot)));

            // ⚠️ A extração textual de um PDF INTERCALA as colunas de uma TABELA, e o recorte depende da fonte
            // instalada (no Linux do CI a quebra é outra). Por isso as asserções sobre a tabela usam TOKENS
            // ÚNICOS, que nunca se partem, e as frases inteiras só são exigidas em PARÁGRAFOS.
            texto.Should().Contain("ClientePdfSmoke", "o cliente CONGELADO abre o relatório");
            texto.Should().Contain("15/10/2026", "o prazo acordado viaja congelado com a ação");
            texto.Should().Contain("Identidade", "o responsável congelado aparece na linha da ação");

            // A lista nominal existe no produto, sob autorização — não num PDF que circula por e-mail.
            texto.Should().NotContain("demo.example.com", "nenhum UPN atravessa o relatório executivo");
            texto.Should().NotContain("conta01", "nem o identificador de um objeto afetado");

            // Parágrafos: aqui a frase inteira é estável, porque nada intercala colunas.
            texto.Should().Contain("Nenhuma validacao registrada",
                "o relatório não promete comprovação que não houve");
            texto.Should().Contain("Data da avaliacao",
                "a data da avaliação é identificada, distinta da data da publicação");
            texto.Should().Contain("Data da publicacao");
        }
    }

    // ---- (5) A corrida do banco vira conflito de NEGÓCIO — e só ela ------------------------------

    [Fact]
    public async Task CriacaoCONCORRENTE_ViraConflitoTratado_SemMascararOutrosErrosDoBanco()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrateAndSeedAsync(opt, tenant, "Cliente Corrida");

        Guid runId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runId = (await RunAsync(db, tenant, withoutMfa: 2, at: T0)).Id;

        // A linha concorrente é inserida por FORA do serviço — é exatamente o que uma segunda requisição
        // simultânea faria depois de passar pela checagem em memória.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            await db.Database.ExecuteSqlRawAsync(
                InsertPlanSql, Guid.NewGuid(), tenant, "AK-ENTRA-001", (int)ActionPlanStatus.Aberto,
                (int)KnightSourceType.Demo, (int)KnightAssessmentMode.Demo);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var criar = async () => await RemediationFor(db, tenant).CreateForFindingAsync(
                new CreateFindingActionPlanCommand(runId, "AK-ENTRA-001", "Ação", null, null, null, null), Actor);

            // A checagem em memória já encontra a linha e recusa antes do banco — que é o caminho normal.
            await criar.Should().ThrowAsync<ActionPlanConflictException>(
                "o produto responde 409 com a ação existente, não 500");
        }

        // E a tradução é do índice ESPECÍFICO, não de "qualquer chave duplicada": outra unicidade violada
        // continua sendo o erro que é, em vez de virar uma mensagem de negócio plausível e falsa.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var slug = "dup-" + Guid.NewGuid().ToString("N");
            db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "X", Slug = slug, Status = TenantStatus.Active });
            await db.SaveChangesAsync();

            db.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = "Y", Slug = slug, Status = TenantStatus.Active });
            var outraUnicidade = async () => await db.SaveChangesAsync();

            (await outraUnicidade.Should().ThrowAsync<DbUpdateException>())
                .Which.Should().NotBeOfType<ActionPlanConflictException>();
        }
    }

    // ---- (6) Repactuar prazo e responsável, com conflito de versão tratado -------------------------

    [Fact]
    public async Task RepactuarPrazoEResponsavel_NaoRecriaAAcao_EConflitoDeVersaoERecusado()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrateAndSeedAsync(opt, tenant, "Cliente Edição");

        Guid runId, planoId;
        int versaoAntiga;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runId = (await RunAsync(db, tenant, withoutMfa: 2, at: T0)).Id;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var criada = await RemediationFor(db, tenant).CreateForFindingAsync(
                new CreateFindingActionPlanCommand(
                    runId, "AK-ENTRA-001", "Registrar segundo fator", "Proposta original.",
                    "Equipe de Identidade", "TI", new DateOnly(2026, 10, 15)),
                Actor);
            planoId = criada!.Id;
            versaoAntiga = criada.Version;
        }

        // Repactuação: a MESMA ação muda de responsável e de prazo, sem recriar nada.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var svc = RemediationFor(db, tenant);
            var editada = await svc.UpdateAsync(
                planoId,
                new UpdateActionPlanCommand(versaoAntiga, null, null, "Ana Souza", "Segurança",
                    new DateOnly(2026, 11, 30), null),
                Actor);

            editada!.Id.Should().Be(planoId, "repactuar prazo não pode nascer uma ação nova");
            editada.ResponsiblePerson.Should().Be("Ana Souza");
            editada.DueDate.Should().Be(new DateOnly(2026, 11, 30));
            editada.OriginRunId.Should().Be(runId, "a origem permanece a mesma");
            editada.Version.Should().BeGreaterThan(versaoAntiga);
            editada.Events.Should().Contain(e => e.Kind == ActionPlanEventKind.Edited,
                "a trilha registra quem mudou o quê");
        }

        // A tela que ficou aberta com a versão antiga recebe 409 em vez de sobrescrever o trabalho alheio.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var telaVelha = async () => await RemediationFor(db, tenant).UpdateAsync(
                planoId,
                new UpdateActionPlanCommand(versaoAntiga, null, null, "Sobrescrito", null, null, null),
                Actor);

            await telaVelha.Should().ThrowAsync<ActionPlanConflictException>();
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            (await RemediationFor(db, tenant).GetAsync(planoId))!.ResponsiblePerson
                .Should().Be("Ana Souza", "a escrita recusada não deixou rastro");
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    /// <summary>
    /// INSERT cru de uma ação de achado, COM a procedência. Ela precisa estar aqui: o índice único parcial é
    /// por (tenant, fonte, modo, achado), e um insert sem fonte/modo cairia num outro ponto do espaço de
    /// chaves — passaria, e o teste "provaria" uma unicidade que não foi exercida.
    /// </summary>
    private const string InsertPlanSql =
        @"INSERT INTO ""ActionPlans""
          (""Id"", ""TenantId"", ""Treatment"", ""Status"", ""KnightIndicatorId"",
           ""OriginSourceType"", ""OriginMode"", ""Version"", ""CreatedAt"")
          VALUES ({0}, {1}, 1, {3}, {2}, {4}, {5}, 1, now())";

    private static async Task MigrateAndSeedAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, string name)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        // A migration deste pacote aplica sobre o histórico inteiro — é o que prova que as colunas aditivas,
        // as tabelas novas e o índice parcial convivem com o schema existente.
        await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant { Id = tenant, Name = name, Slug = "t-" + tenant.ToString("N"), Status = TenantStatus.Active });
        await db.SaveChangesAsync();
    }

    private static IRemediationService RemediationFor(AegisScoreDbContext db, Guid tenantId) =>
        new RemediationService(db, new SystemTenantContext(tenantId), TimeProvider.System);

    private static IPostureSnapshotService PostureFor(AegisScoreDbContext db, Guid tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new NistSignalMapper(db));

    private static Task<KnightAssessment> RunAsync(
        AegisScoreDbContext db, Guid tenantId, int withoutMfa, DateTimeOffset at)
    {
        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 12),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, withoutMfa),
        });

        var objetos = Enumerable.Range(1, withoutMfa)
            .Select(i => new KnightAffectedObjectFact(
                $"conta-{i:00}", KnightAffectedObjectKind.User, $"Conta {i:00}", $"conta{i:00}@demo.example.com",
                new[] { "Administrador Global" }, "Sem método capaz de MFA no relatório de registro."))
            .ToList();

        var result = new KnightCollectionResult(
            KnightSourceType.Demo, KnightSourceState.Completed, "Provedor de Demonstração AEGIS KNIGHT", facts,
            new[]
            {
                new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
                new KnightCapabilityStatus(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Collected),
            },
            at,
            AffectedObjects: new[]
            {
                new KnightAffectedObjectEvidence(KnightSignalKey.PrivilegedAccountsWithoutMfa, objetos, IsComplete: true),
            });

        var registry = new KnightCollectorRegistry(new[] { (IKnightCollector)new StubCollector(result) });
        var tenant = new SystemTenantContext(tenantId);
        var config = new DemoOnlyConfigProvider();
        var evidence = new AegisScore.Infrastructure.Identity.IdentityEvidenceService(db, registry, config, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new NoAdvisoryGenerator(), evidence, tenant)
            .RunDemoAssessmentAsync();
    }

    /// <summary>Extração textual do PDF — a MESMA abordagem (PdfPig) da suíte de exportação já existente.</summary>
    private static string ExtractPdfText(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    /// <summary>Remove acentos: a extração do PDF quebra a acentuação, e a asserção é sobre o CONTEÚDO.</summary>
    private static string Deaccent(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
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

    private sealed class NoAdvisoryGenerator : IKnightAdvisoryGenerator
    {
        public Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default) =>
            Task.FromResult(new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false));
    }
}
