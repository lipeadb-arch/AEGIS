using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Nist;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] A migration e a concorrência da jornada NIST em PostgreSQL real:
///   • uma avaliação de subcategoria gravada ANTES da migration herda o tenant do escopo e continua legível e isolada;
///   • havendo duplicata (escopo, subcategoria), a migration ABORTA e nada é alterado;
///   • duas gravações simultâneas da mesma versão — ou duas criações da mesma subcategoria — terminam com exatamente
///     uma vencedora e um conflito, decidido pelo banco (token de versão / índice único).
/// </summary>
public sealed class NistJourneyPostgresTests
{
    private const string Before = "20261003125320_Closure01_KnightManualAssessments";
    private const string Migration = "20261003220304_Nist01_AssessmentJourney";
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly RemediationActor Gestor = new(Guid.NewGuid(), "Gestora Demo");

    private readonly ITestOutputHelper _output;
    public NistJourneyPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AvaliacaoLegada_HerdaOTenantDoEscopo_ELidaPelaJornadaIsolada()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        var (assessment, scope, evaluation) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(Before);
            var sub = await SeedLegacyAsync(db, tenantA, tenantB, assessment, scope);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Evaluations" ("Id","AssessmentScopeId","SubcategoryId","CurrentLevel","CurrentScore","TargetLevel","TargetScore",
                                           "EvaluatedBy","EvidenceRefs","CreatedAt")
                VALUES ({0},{1},{2},2,2,4,4,0,'[]',now())
                """, evaluation, scope, sub);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(Migration);
            var row = await db.Evaluations.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == evaluation);
            row.TenantId.Should().Be(tenantA, "o tenant é herdado do escopo, de forma determinística");
            row.CurrentLevel.Should().Be(2, "o dado existente não é alterado");
            row.Version.Should().Be(0);
            row.NotApplicable.Should().BeFalse();
            (await db.Assessments.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == assessment)).MethodologyVersion
                .Should().Be(AssessmentMethodology.Version);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var svc = new NistAssessmentService(db, new SystemTenantContext(tenantA), new FakeTimeProvider(DateTimeOffset.UtcNow));
            // [AEGIS-NIST-JOURNEY-02] A avaliação legada pertence à rodada inicial criada pela migration seguinte.
            var cycle = (await db.NistCycles.AsNoTracking().SingleAsync(c => c.AssessmentId == assessment)).Id;
            var detail = await svc.GetSubcategoryAsync(assessment, cycle, scope, "GV.OC-01");
            detail.Evaluation!.Gap.Should().Be(2);
            detail.Evaluation.State.Should().Be(NistSubcategoryStates.PendingConfirmation,
                "o registro legado não tem revisão humana gravada: aparece, mas aguarda confirmação e fica fora das médias");
            (await svc.ListAsync()).Single().Scopes.Single().Name.Should().Be("Escopo sem nome", "escopo antigo sem nome continua legível");
            // A primeira gravação pela jornada parte da versão legada (0).
            (await svc.SaveEvaluationAsync(assessment, cycle, scope, "GV.OC-01",
                new SaveNistEvaluationCommand(3, 4, false, null, null, null, null, null, null, null, 0), Gestor)).Evaluation!.Version.Should().Be(1);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
        {
            (await db.Evaluations.CountAsync()).Should().Be(0, "o filtro próprio da avaliação isola no banco");
            await FluentActions.Awaiting(() => new NistAssessmentService(db, new SystemTenantContext(tenantB), TimeProvider.System)
                    .GetSubcategoryAsync(assessment, Guid.NewGuid(), scope, "GV.OC-01"))
                .Should().ThrowAsync<NistAssessmentNotFoundException>();
        }
    }

    [Fact]
    public async Task DuplicataLegada_AbortaAMigration_SemAlterarNada()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var (tenantA, assessment, scope) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(Before);
            var sub = await SeedLegacyAsync(db, tenantA, Guid.NewGuid(), assessment, scope);
            foreach (var level in new[] { 2, 3 })
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "Evaluations" ("Id","AssessmentScopeId","SubcategoryId","CurrentLevel","CurrentScore","EvaluatedBy","EvidenceRefs","CreatedAt")
                    VALUES ({0},{1},{2},{3},{3},0,'[]',now())
                    """, Guid.NewGuid(), scope, sub, level);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var act = () => db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("AEGIS-NIST-JOURNEY-01");
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(Migration, "a migration abortada não fica registrada");
            await using var conn = new NpgsqlConnection(pg.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'Evaluations' AND column_name = 'TenantId'", conn);
            ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(0, "a transação desfez as colunas novas");
            await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM \"Evaluations\"", conn);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(2, "nenhuma linha foi apagada ou escolhida arbitrariamente");
        }
    }

    [Fact]
    public async Task GravacoesSimultaneas_UmaVence_OutraConflita()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            await FrameworkSeeder.SeedAsync(db, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"));
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente Demo", Slug = $"t-{tenant:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        NistAssessmentService Svc(AegisScoreDbContext db) => new(db, new SystemTenantContext(tenant), TimeProvider.System);
        Guid a, c, s;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var created = await Svc(db).CreateAsync(new CreateNistAssessmentCommand("Avaliação", null, null, null, "Matriz", null), Gestor);
            (a, c, s) = (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
            await Svc(db).SaveEvaluationAsync(a, c, s, "DE.CM-01", new SaveNistEvaluationCommand(2, 4, false, null, null, null, null, null, null, null, 0), Gestor);
        }

        async Task<string> Attempt(string code, int level, int expected)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            try
            {
                await Svc(db).SaveEvaluationAsync(a, c, s, code, new SaveNistEvaluationCommand(level, 4, false, null, null, null, null, null, null, null, expected), Gestor);
                return "ok";
            }
            catch (NistAssessmentConflictException)
            {
                return "conflict";
            }
        }

        // Mesma versão lida por duas pessoas.
        var update = await Task.WhenAll(Attempt("DE.CM-01", 1, 1), Attempt("DE.CM-01", 3, 1));
        update.OrderBy(x => x).Should().Equal("conflict", "ok");
        // Duas criações da mesma subcategoria.
        var create = await Task.WhenAll(Attempt("DE.CM-02", 1, 0), Attempt("DE.CM-02", 3, 0));
        create.OrderBy(x => x).Should().Equal("conflict", "ok");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.Evaluations.AsNoTracking().SingleAsync(e => e.Subcategory!.Code == "DE.CM-01")).Version.Should().Be(2);
            (await db.Evaluations.AsNoTracking().CountAsync(e => e.Subcategory!.Code == "DE.CM-02")).Should().Be(1);
        }
    }

    /// <summary>Catálogo, tenants e uma avaliação/escopo no formato ANTERIOR à migration (SQL cru: o modelo atual já tem as colunas novas).</summary>
    private static async Task<Guid> SeedLegacyAsync(AegisScoreDbContext db, Guid tenantA, Guid tenantB, Guid assessment, Guid scope)
    {
        await FrameworkSeeder.SeedAsync(db, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"));
        foreach (var t in new[] { tenantA, tenantB })
            db.Tenants.Add(new Tenant { Id = t, Name = "Cliente Demo", Slug = $"t-{t:N}", Status = TenantStatus.Active });
        await db.SaveChangesAsync();
        var fv = await db.FrameworkVersions.AsNoTracking().Where(f => f.IsActive).Select(f => f.Id).SingleAsync();
        var sub = await db.Subcategories.AsNoTracking().Where(x => x.Code == "GV.OC-01").Select(x => x.Id).SingleAsync();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Assessments" ("Id","TenantId","FrameworkVersionId","Name","Status","CreatedAt") VALUES ({0},{1},{2},'Diagnóstico legado',1,now());
            INSERT INTO "Scopes" ("Id","TenantId","AssessmentId","BusinessProcessId","BusinessUnitId","Status","CreatedAt") VALUES ({3},{1},{0},{4},{5},3,now());
            """, assessment, tenantA, fv, scope, Guid.NewGuid(), Guid.NewGuid());
        return sub;
    }
}
