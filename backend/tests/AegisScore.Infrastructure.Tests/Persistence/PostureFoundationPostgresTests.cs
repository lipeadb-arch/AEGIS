using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-POSTURE-01] Migration ADITIVA + seed do pacote sobre um PostgreSQL DESCARTÁVEL real — há
/// mudança de schema (coluna <c>EvidenceType</c> + tabela de proveniência) e de seed. Gated por
/// <c>AEGIS_TEST_PG</c> (mesmo padrão do projeto): sem a variável, PULA. Os artefatos REAIS são carregados
/// do diretório <c>Data/</c> do output (linkados no csproj de teste).
/// </summary>
public sealed class PostureFoundationPostgresTests
{
    private const string PreviousMigration = "20260816192614_DocumentEvidenceLifecycle";
    private const string TargetMigration = "20260821155051_PostureFoundation_ProvenanceAndEvidenceType";

    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static string CatalogPath => Path.Combine(DataDir, "nist_csf_2_0_catalog.json");
    private static string MethodologyPath => Path.Combine(DataDir, "aegis_methodology.json");
    private static string RulesPath => Path.Combine(DataDir, "aegis_assessment_rules.json");

    private readonly ITestOutputHelper _output;
    public PostureFoundationPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MigracaoESeed_NoPostgresReal_PopulaPacote_ReconciliaEvidencia_EProntidao()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opts = pg.DbOptions();

        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        // Seed do pacote real (catálogo + metodologia + regras + mapeamentos).
        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            await FrameworkSeeder.SeedAsync(db, CatalogPath, MethodologyPath);
            await FrameworkSeeder.SeedAssessmentRulesAsync(db, RulesPath, MethodologyPath);
            await FrameworkSeeder.SeedSignalMappingsAsync(db);
        }

        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            (await db.Subcategories.CountAsync()).Should().Be(106);
            (await db.AssessmentRules.CountAsync()).Should().Be(99);
            (await db.AssessmentRules.CountAsync(r => r.EvidenceType == RuleEvidenceType.Documentation)).Should().Be(41);
            (await db.AssessmentRules.CountAsync(r => r.EvidenceType == RuleEvidenceType.Telemetry)).Should().Be(58);
            (await db.ReferenceDatasetProvenances.CountAsync(p => p.IsCurrent)).Should().Be(3);

            var ready = await SchemaReadinessGuard.CheckActivePackageAsync(db);
            ready.IsReady.Should().BeTrue(ready.Describe());
        }

        // Idempotência: repetir o seed não cria nova revisão de proveniência nem duplica.
        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            await FrameworkSeeder.SeedAsync(db, CatalogPath, MethodologyPath);
            await FrameworkSeeder.SeedAssessmentRulesAsync(db, RulesPath, MethodologyPath);
            (await db.ReferenceDatasetProvenances.CountAsync()).Should().Be(3, "conteúdo idêntico → sem nova revisão");
            (await db.FrameworkVersions.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task MigracaoAditiva_SobreRegraLegada_AdicionaEvidenceTypeDefault_ECriaTabelaDeProveniencia()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opts = pg.DbOptions();

        // 1) Schema LEGADO (antes da coluna EvidenceType e da tabela de proveniência).
        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        // 2) Catálogo mínimo via EF (tabelas inalteradas) + regra LEGADA via RAW SQL (sem EvidenceType).
        Guid ruleId = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
            var fn = new NistFunction { Code = "PR", Name = "PROTECT" };
            var cat = new NistCategory { Code = "PR.AA", Name = "Identity" };
            var sub = new NistSubcategory { Code = "PR.AA-01", Description = "x", MaxScorePoints = 20 };
            cat.Subcategories.Add(sub);
            fn.Categories.Add(cat);
            fv.Functions.Add(fn);
            db.FrameworkVersions.Add(fv);
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""AssessmentRules""
                    (""Id"",""SubcategoryId"",""SubcategoryCode"",""EvaluationMetrics"",""CalculationLogic"",""EvidenceRequirements"",""CreatedAt"")
                  VALUES ({0},{1},{2},'[]'::jsonb,{3},'[]'::jsonb,{4})",
                ruleId, sub.Id, "PR.AA-01", "rubrica legada", DateTimeOffset.UtcNow);
        }

        // 3) Aplica a migration REAL (adiciona EvidenceType default 0 + tabela de proveniência).
        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync();
        }

        // 4) Verificação: coluna aditiva com default seguro; tabela de proveniência vazia; migration registrada.
        await using (var db = new AegisScoreDbContext(opts, new SystemTenantContext(null)))
        {
            var rule = await db.AssessmentRules.SingleAsync(r => r.Id == ruleId);
            rule.EvidenceType.Should().Be(RuleEvidenceType.Telemetry, "default 0 seguro para linha legada (o seed reconcilia depois)");

            (await db.ReferenceDatasetProvenances.AnyAsync()).Should().BeFalse("tabela criada e vazia até o seed rodar");
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(TargetMigration);
        }
    }
}
