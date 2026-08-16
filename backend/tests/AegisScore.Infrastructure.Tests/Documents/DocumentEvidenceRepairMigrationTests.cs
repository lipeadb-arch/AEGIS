using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Reparo idempotente da migration <c>DocumentEvidenceLifecycle</c> — validado contra um PostgreSQL
/// DESCARTÁVEL (o SQL usa alias em DELETE e <c>now()</c>, que não existem no SQLite). Gated por
/// <c>AEGIS_TEST_PG</c> (mesmo padrão do projeto): sem a variável, registra a ausência e retorna.
///
/// <para>Prova que o reparo: (1) RETRAI o estado documental legado sem prova literal; (2) PRESERVA
/// telemetria; (3) converte cobertura Both sem prova em Interview sem apagar a entrevista; (4) RE-ENFILEIRA
/// documentos existentes com binário. Aplica primeiro TODAS as migrations (o que já exercita a migration
/// real de ponta a ponta no PostgreSQL), depois semeia dados legados e re-executa o MESMO SQL do reparo
/// (idempotente) para observar o efeito sobre dados realistas.</para>
/// </summary>
public sealed class DocumentEvidenceRepairMigrationTests
{
    private readonly ITestOutputHelper _output;
    public DocumentEvidenceRepairMigrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Reparo_LimpaDerivadosDocumentaisLegados_EPreservaTelemetriaEEntrevista()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }

        var dbOptions = pg.DbOptions();
        var tenant = Guid.NewGuid();

        // Aplica TODAS as migrations (inclui DocumentEvidenceLifecycle → schema + reparo em banco vazio).
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        // Catálogo mínimo (não é tenant-owned): duas subcategorias.
        Guid poId, rrId;
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
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
            await db.SaveChangesAsync();
            poId = po.Id;
            rrId = rr.Id;
        }

        // Dados LEGADOS (sem prova literal — EvidenceQuote NULL, como toda linha anterior a este pacote).
        var interview = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(tenant)))
        {
            // (1) estado DOCUMENTAL sem prova → deve ser retraído.
            db.TenantControlStates.Add(new TenantControlState
            {
                SubcategoryId = poId, Status = ControlStatus.MitigatedByThirdParty, CurrentScore = 10,
                LastVerdictSource = VerdictSource.Documentary, OriginDocumentId = Guid.NewGuid(),
            });
            // (2) estado de TELEMETRIA → deve ser preservado.
            db.TenantControlStates.Add(new TenantControlState
            {
                SubcategoryId = rrId, Status = ControlStatus.Compliant, CurrentScore = 20,
                LastVerdictSource = VerdictSource.Telemetry,
            });
            // (3) cobertura Both sem prova → volta para Interview (entrevista preservada).
            db.SubcategoryCoverages.Add(new SubcategoryCoverage
            {
                SubcategoryCode = "GV.PO-01", Status = CoverageStatus.Coberto,
                EvidenceSource = CoverageEvidenceSource.Both,
                OriginDocumentId = Guid.NewGuid(), OriginInterviewSessionId = interview,
            });
            // (4) documento existente com binário → deve ser re-enfileirado.
            db.GovernanceDocuments.Add(new GovernanceDocument
            {
                Title = "PSI", FileName = "psi.pdf", StorageUri = "file://psi.pdf",
                AnalysisStatus = AiAnalysisStatus.Analyzed, AnalyzedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Re-executa o MESMO SQL do reparo (idempotente) sobre os dados legados semeados.
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(null)))
            foreach (var sql in DocumentEvidenceRepair.Statements)
                await db.Database.ExecuteSqlRawAsync(sql);

        // Verificação.
        await using (var db = new AegisScoreDbContext(dbOptions, new SystemTenantContext(tenant)))
        {
            var states = await db.TenantControlStates.ToListAsync();
            states.Should().ContainSingle("o estado documental sem prova foi retraído; só a telemetria sobrou");
            states[0].SubcategoryId.Should().Be(rrId);
            states[0].LastVerdictSource.Should().Be(VerdictSource.Telemetry);
            states[0].Status.Should().Be(ControlStatus.Compliant, "telemetria preservada integralmente");

            var cov = await db.SubcategoryCoverages.SingleAsync();
            cov.EvidenceSource.Should().Be(CoverageEvidenceSource.Interview, "Both sem documento → Interview");
            cov.OriginDocumentId.Should().BeNull();
            cov.OriginInterviewSessionId.Should().Be(interview, "evidência de entrevista nunca é apagada");

            var doc = await db.GovernanceDocuments.SingleAsync();
            doc.AnalysisStatus.Should().Be(AiAnalysisStatus.Queued, "documento com binário re-enfileirado para o novo pipeline");
        }
    }
}
