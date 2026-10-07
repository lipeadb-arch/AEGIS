using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Tests.Documents;
using AegisScore.Infrastructure.Tests.Integration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] O que só o PostgreSQL real prova para o histórico congelado:
///   • a atualização pelo migrator real a partir da baseline publicada aplica a coluna nova sem tocar fotografias existentes,
///     que continuam com o hash íntegro e exportadas sem painel;
///   • a fotografia com histórico volta do banco com o hash íntegro, o relatório sai com a série congelada e o gatilho de
///     imutabilidade recusa alterar o histórico depois;
///   • o filtro de tenant isola a fotografia no banco.
/// </summary>
public sealed class AssessmentVisualsPostgresTests
{
    private const string Baseline = "20261005224354_Nist02_CompleteJourney";
    private const string NewMigration = "20261007135256_AssessmentVisuals01_FrozenHistory";

    private readonly ITestOutputHelper _output;
    public AssessmentVisualsPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AtualizacaoDaBaseline_FotografiaAntigaIntacta_HistoricoCongeladoIntegroEImutavel()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // ---- 1) Banco na baseline publicada, com uma fotografia KNIGHT já publicada (sem histórico) ---------------------
        PostureSnapshot legacy;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(Baseline);
            (await db.Database.GetPendingMigrationsAsync()).Should().Contain(NewMigration);
            foreach (var t in new[] { tenantA, tenantB })
                db.Tenants.Add(new Tenant { Id = t, Name = "Organização Fictícia", Slug = $"t-{t:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            // Só o cabeçalho (sem filhos), com o hash calculado sobre ele — como uma fotografia publicada antes deste pacote.
            var source = AssessmentVisualsFixtures.Knight(withHistory: false);
            source.Indicators.Clear();
            source.Objects.Clear();
            legacy = Owned(source, tenantA, Guid.NewGuid());
            // A baseline não conhece a coluna: grava-se a fotografia antiga sem ela (o modelo atual escreveria HistoryJson).
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""PostureSnapshots"" (""Id"",""TenantId"",""Type"",""SchemaVersion"",""FormulaVersion"",""CatalogVersion"",""SemanticFamily"",
                  ""SourceType"",""SourceLabel"",""CapturedAt"",""Score"",""AchievedPoints"",""PossiblePoints"",""EligiblePoints"",""Coverage"",
                  ""EvaluatedItems"",""EligibleItems"",""CompliantCount"",""NonCompliantCount"",""MitigatedCount"",""NotEvaluatedCount"",""ErrorCount"",
                  ""NotApplicableCount"",""DataRecency"",""ContentHash"",""ClientName"",""CollectionLimitations"",""CompositionJson"",""ProfileCatalogVersion"",""CreatedAt"")
                VALUES ({legacy.Id},{tenantA},1,{legacy.SchemaVersion},{legacy.FormulaVersion},{legacy.CatalogVersion},{legacy.SemanticFamily},
                  {(int)legacy.SourceType!},{legacy.SourceLabel},{legacy.CapturedAt},{legacy.Score},0,0,0,{legacy.Coverage},
                  {legacy.EvaluatedItems},{legacy.EligibleItems},{legacy.CompliantCount},{legacy.NonCompliantCount},{legacy.MitigatedCount},
                  {legacy.NotEvaluatedCount},{legacy.ErrorCount},{legacy.NotApplicableCount},{legacy.DataRecency},{legacy.ContentHash},{legacy.ClientName},
                  '[]'::jsonb,{legacy.CompositionJson},{legacy.ProfileCatalogVersion},{legacy.CreatedAt})");
        }

        // ---- 2) Atualização pelo migrator real -------------------------------------------------------------------------
        (await AegisApiHarness.RunMigratorAsync(pg.ConnectionString)).Should().Be(AegisScore.DbMigrator.MigratorExitCode.Success);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(NewMigration);
            var old = await db.PostureSnapshots.AsNoTracking().SingleAsync(s => s.Id == legacy.Id);
            old.HistoryJson.Should().BeNull("a fotografia antiga não é enriquecida com histórico");
            PostureSnapshotHasher.Verify(old).Should().BeTrue("a coluna nova não muda o hash de quem já foi publicado");
            Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(old.Id, PostureExportFormat.Html))!.Content)
                .Should().NotContain("id=\"aegis-visuals\"", "fotografia antiga sai sem painel");
        }

        // ---- 3) Fotografia com histórico congelado: grava, relê, hash íntegro, exporta a série --------------------------
        var visual = Owned(AssessmentVisualsFixtures.Knight(withHistory: true), tenantA, Guid.NewGuid());
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            db.PostureSnapshots.Add(visual);
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var read = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
                .AsSplitQuery().SingleAsync(s => s.Id == visual.Id);
            read.HistoryJson.Should().Be(visual.HistoryJson);
            PostureSnapshotHasher.Verify(read).Should().BeTrue("o histórico volta do PostgreSQL idêntico e coberto pelo hash");
            var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(visual.Id, PostureExportFormat.Html))!.Content);
            html.Should().Contain("id=\"ch-knight-historico\"").And.Contain("Sem publicação neste mês");

            var tamper = () => db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""PostureSnapshots"" SET ""HistoryJson"" = '{{}}' WHERE ""Id"" = {visual.Id}");
            await tamper.Should().ThrowAsync<Exception>("a fotografia publicada é imutável no banco, inclusive o histórico");
        }

        // ---- 4) Isolamento ---------------------------------------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
            (await db.PostureSnapshots.AsNoTracking().AnyAsync(s => s.Id == visual.Id)).Should().BeFalse();
    }

    /// <summary>A fixture com o tenant e o id deste banco (o hash cobre o tenant, então é recalculado).</summary>
    private static PostureSnapshot Owned(PostureSnapshot s, Guid tenant, Guid id)
    {
        s.Id = id;
        s.TenantId = tenant;
        foreach (var i in s.Indicators) { i.TenantId = tenant; i.Id = Guid.NewGuid(); }
        foreach (var o in s.Objects) { o.TenantId = tenant; o.Id = Guid.NewGuid(); }
        foreach (var a in s.ActionItems) a.TenantId = tenant;
        s.ContentHash = PostureSnapshotHasher.Compute(s);
        return s;
    }
}
