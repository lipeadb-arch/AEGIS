using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// TRANSIÇÃO REAL da migration <c>DocumentEvidenceLifecycle</c> sobre um banco LEGADO com dados — validada
/// contra um PostgreSQL DESCARTÁVEL. Migra até a migration ANTERIOR, semeia dados no schema legado (raw SQL
/// nas tabelas que ganham colunas; EF nas demais), aplica a migration real e observa o reparo. É o que o
/// container-smoke (banco vazio) não cobre.
///
/// Gated por <c>AEGIS_TEST_PG</c> (mesmo padrão do projeto): sem a variável, registra a ausência e retorna.
/// No CI, o job focado <c>document-evidence-repair-pg</c> define a variável e roda SOMENTE este teste.
/// </summary>
public sealed class DocumentEvidenceRepairMigrationTests
{
    private const string PreviousMigration = "20260811143617_Auditable_Posture_Snapshots";
    private const string TargetMigration = "20260816192614_DocumentEvidenceLifecycle";

    private readonly ITestOutputHelper _output;
    public DocumentEvidenceRepairMigrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Migration_SobreBancoLegado_RetraiDocumental_PreservaTelemetriaEEntrevista_LimpaMetadados()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }

        var dbOptions = pg.DbOptions();
        var tenant = Guid.NewGuid();
        var interview = Guid.NewGuid();

        // 1) Migra SOMENTE até a migration anterior — o schema LEGADO (sem EvidenceQuote/OriginDocumentId).
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        // 2a) Catálogo (não tenant-owned) + documentos + cobertura via EF — tabelas SEM colunas novas.
        Guid poId, rrId, docWithBinary, docNoBinary;
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(tenant)))
        {
            var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
            var fn = new NistFunction { Code = "GV", Name = "GOVERN" };
            var cat = new NistCategory { Code = "GV.XX", Name = "Cat" };
            var po = new NistSubcategory { Code = "GV.PO-01", Description = "Policy", MaxScorePoints = 20 };
            var rr = new NistSubcategory { Code = "GV.RR-01", Description = "Roles", MaxScorePoints = 20 };
            cat.Subcategories.Add(po);
            cat.Subcategories.Add(rr);
            fn.Categories.Add(cat);
            fv.Functions.Add(fn);
            db.FrameworkVersions.Add(fv);

            var docBin = new GovernanceDocument
            {
                Title = "PSI", FileName = "psi.pdf", StorageUri = "file://psi.pdf",
                AnalysisStatus = AiAnalysisStatus.Analyzed, AnalyzedAt = DateTimeOffset.UtcNow,
                AnalysisSummary = "conclusão legada", ModelUsed = "claude-legacy", AnalysisError = null,
            };
            var docNo = new GovernanceDocument
            {
                Title = "Sem binário", FileName = "x.pdf", StorageUri = null,
                AnalysisStatus = AiAnalysisStatus.Analyzed, AnalyzedAt = DateTimeOffset.UtcNow,
                AnalysisSummary = "conclusão legada sem binário", ModelUsed = "claude-legacy",
            };
            db.GovernanceDocuments.Add(docBin);
            db.GovernanceDocuments.Add(docNo);

            // Cobertura Both (com entrevista) — deve virar Interview após o reparo, sem apagar a entrevista.
            db.SubcategoryCoverages.Add(new SubcategoryCoverage
            {
                SubcategoryCode = "GV.PO-01", Status = CoverageStatus.Coberto,
                EvidenceSource = CoverageEvidenceSource.Both, OriginInterviewSessionId = interview,
            });
            await db.SaveChangesAsync();
            poId = po.Id; rrId = rr.Id; docWithBinary = docBin.Id; docNoBinary = docNo.Id;
        }

        // 2b) Estados do ledger + mapping LEGADO via RAW SQL (essas tabelas ainda não têm as colunas novas).
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
        {
            var now = DateTimeOffset.UtcNow;
            // Estado DOCUMENTAL legado (deve ser retraído).
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""TenantControlStates""
                    (""Id"",""TenantId"",""SubcategoryId"",""Status"",""CurrentScore"",""LastVerdictSource"",""LastEvaluatedAt"",""MissingRequirements"",""CreatedAt"")
                  VALUES ({0},{1},{2},{3},{4},{5},{6},'[]'::jsonb,{7})",
                Guid.NewGuid(), tenant, poId, (int)ControlStatus.MitigatedByThirdParty, 10, (int)VerdictSource.Documentary, now, now);
            // Estado de TELEMETRIA (deve ser preservado).
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""TenantControlStates""
                    (""Id"",""TenantId"",""SubcategoryId"",""Status"",""CurrentScore"",""LastVerdictSource"",""LastEvaluatedAt"",""MissingRequirements"",""CreatedAt"")
                  VALUES ({0},{1},{2},{3},{4},{5},{6},'[]'::jsonb,{7})",
                Guid.NewGuid(), tenant, rrId, (int)ControlStatus.Compliant, 20, (int)VerdictSource.Telemetry, now, now);
            // Mapping LEGADO (sem trecho — a coluna EvidenceQuote nem existe ainda; deve ser removido).
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""DocumentControlMappings""
                    (""Id"",""TenantId"",""GovernanceDocumentId"",""SubcategoryCode"",""Confidence"",""AnalystConfirmed"",""CreatedAt"")
                  VALUES ({0},{1},{2},{3},{4},{5},{6})",
                Guid.NewGuid(), tenant, docWithBinary, "GV.PO-01", 0.72, false, now);
        }

        // 3) Aplica a migration REAL (schema novo + FK + REPARO idempotente inline).
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync();   // até a última
        }

        // 4) Verificação do estado final.
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(tenant)))
        {
            var states = await db.TenantControlStates.ToListAsync();
            states.Should().ContainSingle("o estado documental legado foi retraído; só a telemetria sobrou");
            states[0].SubcategoryId.Should().Be(rrId);
            states[0].LastVerdictSource.Should().Be(VerdictSource.Telemetry);
            states[0].Status.Should().Be(ControlStatus.Compliant, "telemetria preservada integralmente");

            (await db.DocumentControlMappings.AnyAsync())
                .Should().BeFalse("mapping legado sem trecho literal foi removido");

            var cov = await db.SubcategoryCoverages.SingleAsync();
            cov.EvidenceSource.Should().Be(CoverageEvidenceSource.Interview, "Both sem documento → Interview");
            cov.OriginDocumentId.Should().BeNull();
            cov.OriginInterviewSessionId.Should().Be(interview, "evidência de entrevista nunca é apagada");

            var docBin = await db.GovernanceDocuments.SingleAsync(d => d.Id == docWithBinary);
            docBin.AnalysisStatus.Should().Be(AiAnalysisStatus.Queued, "documento com binário re-enfileirado");
            docBin.AnalyzedAt.Should().BeNull("metadados legados limpos");
            docBin.AnalysisSummary.Should().BeNull();
            docBin.ModelUsed.Should().BeNull();

            var docNo = await db.GovernanceDocuments.SingleAsync(d => d.Id == docNoBinary);
            docNo.AnalysisStatus.Should().Be(AiAnalysisStatus.Pending, "documento sem binário volta a Pending, sem conclusão antiga");
            docNo.AnalyzedAt.Should().BeNull();
            docNo.AnalysisSummary.Should().BeNull();
        }

        // 5) A migration foi de fato registrada no histórico do EF.
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
        {
            var applied = await db.Database.GetAppliedMigrationsAsync();
            applied.Should().Contain(TargetMigration, "a migration real ficou registrada em __EFMigrationsHistory");
        }
    }
}
