using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Nist;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Remediation;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] A jornada completa em PostgreSQL REAL:
///   • a migration dá a cada avaliação do #87 UMA rodada inicial e liga a ela as avaliações e evidências existentes, sem
///     apagar nem escolher nada (dados preservados, tenant conferido);
///   • dois planos simultâneos para o mesmo achado e dois resultados simultâneos do mesmo procedimento: um vence, o outro
///     é conflito — decidido pelo banco (índice único parcial / token de versão);
///   • a trilha NIST é append-only no banco (UPDATE e DELETE recusados);
///   • a rodada é FK tenant-safe: o banco recusa avaliação apontando para rodada de outro tenant.
/// </summary>
public sealed class NistJourney02PostgresTests
{
    private const string Before = "20261003220304_Nist01_AssessmentJourney";
    private const string Migration = "20261005224354_Nist02_CompleteJourney";
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly RemediationActor Gestora = new(Guid.NewGuid(), "Gestora Demo");

    private readonly ITestOutputHelper _output;
    public NistJourney02PostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Migration_DaAvaliacaoDo87_CriaUmaRodadaInicial_ELigaAvaliacoesEEvidencias_SemPerderDados()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var (a1, a2, s1, s2, s3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (e1, e2, e3, ev1) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(Before);
            await FrameworkSeeder.SeedAsync(db, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"));
            foreach (var t in new[] { tenantA, tenantB })
                db.Tenants.Add(new Tenant { Id = t, Name = "Cliente Sintético", Slug = $"t-{t:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
            var fv = await db.FrameworkVersions.AsNoTracking().Where(f => f.IsActive).Select(f => f.Id).SingleAsync();
            var subs = await db.Subcategories.AsNoTracking().Where(x => x.Code == "GV.OC-01" || x.Code == "ID.AM-01")
                .ToDictionaryAsync(x => x.Code, x => x.Id);
            // Estado do #87: avaliação A1 (com período, dois escopos) do tenant A e A2 (sem período) do tenant B.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Assessments" ("Id","TenantId","FrameworkVersionId","Name","Status","StartDate","EndDate","CreatedAt") VALUES ({0},{2},{4},'Avaliação 2026',1,'2026-07-01','2026-09-30',now());
                INSERT INTO "Assessments" ("Id","TenantId","FrameworkVersionId","Name","Status","CreatedAt") VALUES ({1},{3},{4},'Avaliação B',1,'2026-08-15T10:00:00Z');
                INSERT INTO "Scopes" ("Id","TenantId","AssessmentId","Status","Name","CreatedAt") VALUES ({5},{2},{0},3,'Matriz',now()),({6},{2},{0},3,'Filial',now()),({7},{3},{1},3,'Única',now());
                """, a1, a2, tenantA, tenantB, fv, s1, s2, s3);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Evaluations" ("Id","TenantId","AssessmentScopeId","SubcategoryId","CurrentLevel","CurrentScore","TargetLevel","TargetScore",
                                           "EvaluatedBy","EvidenceRefs","ReviewedAt","ReviewedByName","Version","CreatedAt")
                VALUES ({0},{3},{5},{8},2,2,4,4,0,'[]',now(),'Gestora',1,now()),
                       ({1},{3},{6},{8},3,3,3,3,0,'[]',now(),'Gestora',1,now()),
                       ({2},{4},{7},{9},1,1,2,2,0,'[]',now(),'Outro',1,now());
                INSERT INTO "Evidence" ("Id","TenantId","AssessmentScopeId","SubcategoryCode","Type","Source","CollectedAt","CreatedAt","Title","OriginKind")
                VALUES ({10},{3},{5},'GV.OC-01',4,3,now(),now(),'Entrevista',0);
                """, e1, e2, e3, tenantA, tenantB, s1, s2, s3, subs["GV.OC-01"], subs["ID.AM-01"], ev1);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(Migration);

            var cycles = await db.NistCycles.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            cycles.Should().HaveCount(2, "uma rodada inicial por avaliação existente — nem mais, nem menos");
            var c1 = cycles.Single(c => c.AssessmentId == a1);
            (c1.TenantId, c1.Name, c1.PeriodStart, c1.PeriodEnd, c1.Status).Should().Be(
                (tenantA, "Rodada inicial", new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), NistCycleStatus.Open));
            var c2 = cycles.Single(c => c.AssessmentId == a2);
            (c2.TenantId, c2.PeriodStart, c2.PeriodEnd).Should().Be((tenantB, new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 15)),
                "sem período informado, a rodada usa a data de criação da avaliação");

            var evals = await db.Evaluations.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            evals.Should().HaveCount(3, "nenhuma avaliação é apagada");
            evals.Single(e => e.Id == e1).CycleId.Should().Be(c1.Id);
            evals.Single(e => e.Id == e2).CycleId.Should().Be(c1.Id, "os dois escopos da mesma avaliação apontam para a mesma rodada");
            evals.Single(e => e.Id == e3).CycleId.Should().Be(c2.Id);
            evals.Single(e => e.Id == e1).CurrentLevel.Should().Be(2, "o conteúdo não muda");
            evals.Should().OnlyContain(e => e.ContentOrigin == NistContentOrigin.Analyst && e.HumanConfirmed, "a revisão humana gravada continua valendo");
            (await db.Evidence.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == ev1)).CycleId.Should().Be(c1.Id);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var svc = new NistAssessmentService(db, new SystemTenantContext(tenantA), TimeProvider.System);
            var view = (await svc.ListAsync()).Single();
            view.Cycles!.Single().Name.Should().Be("Rodada inicial");
            view.Scopes.Should().OnlyContain(s => s.Evaluated == 1);
            var profile = await svc.GetProfileAsync(a1, view.Cycles!.Single().Id, s1);
            profile.Overall.Current.Should().Be(2, "a leitura do #87 continua a mesma depois da migration");
        }
    }

    [Fact]
    public async Task Concorrencia_PlanoDoMesmoAchado_EResultadoDoMesmoProcedimento_UmVence()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        await SeedAsync(opt, tenant);

        Guid a, c, s, finding, procedure;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var (nist, work) = Services(db, tenant);
            var created = await nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação", null, null, null, "Matriz", null), Gestora);
            (a, c, s) = (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
            await nist.SaveEvaluationAsync(a, c, s, "ID.AM-01", new SaveNistEvaluationCommand(2, 4, false, null, null, null, "Inventário parcial.", null, null, null, 0), Gestora);
            finding = (await work.CreateFindingAsync(a, c, s, "ID.AM-01", new CreateNistFindingCommand("Inventário incompleto", "Planilha sem 30% dos servidores.",
                "Ativo fora do inventário não é corrigido.", "Exposição não tratada.", "High", "Servidores de produção afetados.", "High",
                "Base dos demais controles.", "Completar o inventário.", null, null), Gestora)).Id;
            procedure = (await work.AddProcedureAsync(a, c, s, "ID.AM-01", new AddNistProcedureCommand("Examine", "Examinar o inventário de ativos."), Gestora)).Id;
        }

        async Task<string> Attempt(Func<NistWorkService, Task> act)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            try
            {
                await act(Services(db, tenant).Work);
                return "ok";
            }
            catch (NistAssessmentConflictException)
            {
                return "conflict";
            }
        }

        var plans = await Task.WhenAll(
            Attempt(w => w.CreatePlanAsync(a, c, s, finding, new NistPlanInput("Plano 1", null, null, null, null), Gestora)),
            Attempt(w => w.CreatePlanAsync(a, c, s, finding, new NistPlanInput("Plano 2", null, null, null, null), Gestora)));
        plans.OrderBy(x => x).Should().Equal("conflict", "ok");

        var results = await Task.WhenAll(
            Attempt(w => w.UpdateProcedureAsync(a, c, s, "ID.AM-01", procedure, new UpdateNistProcedureCommand(null, "Performed", "Unsatisfactory",
                "Inventário sem 30% dos servidores.", new DateOnly(2026, 10, 1), null, 1), Gestora)),
            Attempt(w => w.UpdateProcedureAsync(a, c, s, "ID.AM-01", procedure, new UpdateNistProcedureCommand(null, "NotPerformed", null,
                "Planilha indisponível no dia da visita.", null, null, 1), Gestora)));
        results.OrderBy(x => x).Should().Equal("conflict", "ok");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.ActionPlans.CountAsync(p => p.OriginNistFindingId == finding)).Should().Be(1);
            (await db.NistProcedures.SingleAsync(p => p.Id == procedure)).Version.Should().Be(2);
        }
    }

    [Fact]
    public async Task Trilha_AppendOnlyNoBanco_ERodadaComFkTenantSafe()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(opt, tenantA, tenantB);

        Guid a, c, s, cycleB;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var created = await Services(db, tenantA).Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação", null, null, null, "Matriz", null), Gestora);
            (a, c, s) = (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
            cycleB = (await Services(db, tenantB).Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação B", null, null, null, "Única", null), Gestora)).Cycles!.Single().Id;

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM \"NistAuditEntries\"", conn))
            ((long)(await count.ExecuteScalarAsync())!).Should().BeGreaterThan(0);
        foreach (var sql in new[] { "UPDATE \"NistAuditEntries\" SET \"Summary\" = 'reescrito'", "DELETE FROM \"NistAuditEntries\"" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var act = () => cmd.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        }

        var sub = Guid.Empty;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            sub = await db.Subcategories.AsNoTracking().Where(x => x.Code == "GV.OC-01").Select(x => x.Id).SingleAsync();
        await using (var cross = new NpgsqlCommand(
            """
            INSERT INTO "Evaluations" ("Id","TenantId","AssessmentScopeId","SubcategoryId","CycleId","EvaluatedBy","EvidenceRefs","Version","CreatedAt")
            VALUES (@id, @tenant, @scope, @sub, @cycle, 0, '[]', 0, now())
            """, conn))
        {
            cross.Parameters.AddWithValue("id", Guid.NewGuid());
            cross.Parameters.AddWithValue("tenant", tenantA);
            cross.Parameters.AddWithValue("scope", s);
            cross.Parameters.AddWithValue("sub", sub);
            cross.Parameters.AddWithValue("cycle", cycleB);
            var act = () => cross.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503", "rodada de outro tenant viola a FK composta");
        }
    }

    private static async Task SeedAsync(DbContextOptions<AegisScoreDbContext> opt, params Guid[] tenants)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        await db.Database.MigrateAsync();
        await FrameworkSeeder.SeedAsync(db, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"));
        foreach (var t in tenants)
            db.Tenants.Add(new Tenant { Id = t, Name = "Cliente Sintético", Slug = $"t-{t:N}", Status = TenantStatus.Active });
        await db.SaveChangesAsync();
    }

    private static (NistAssessmentService Nist, NistWorkService Work) Services(AegisScoreDbContext db, Guid tenant)
    {
        var ctx = new SystemTenantContext(tenant);
        var remediation = new RemediationService(db, ctx, TimeProvider.System,
            new AegisScore.Infrastructure.Queries.DevicePriorityQuery(db, TimeProvider.System,
                Microsoft.Extensions.Options.Options.Create(new AegisScore.Application.Queries.CrossSourceCorrelationOptions())));
        return (new NistAssessmentService(db, ctx, TimeProvider.System), new NistWorkService(db, ctx, TimeProvider.System, remediation));
    }
}
