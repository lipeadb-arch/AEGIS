using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Tests.Documents;
using AegisScore.Infrastructure.Tests.Integration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-CLOSURE-01] O que só o PostgreSQL real prova para o resultado manual:
///   • a ATUALIZAÇÃO a partir da baseline publicada (<c>KnightConsolidated01</c>) aplica só a migration nova, pelo migrator
///     real, sem tocar o que já existe;
///   • o registro persiste e volta igual (textos no limite das colunas, data de validade, carimbo em microssegundos);
///   • substituição no mesmo instante mantém a ordem; o filtro de tenant isola no banco;
///   • a fotografia publicada congela o resultado e continua íntegra depois de relida do banco.
/// </summary>
public sealed class KnightManualResultPostgresTests
{
    private const string Baseline = "20260924012046_KnightConsolidated01_CompositionJson";
    private const string NewMigration = "20261003125320_Closure01_KnightManualAssessments";

    private readonly ITestOutputHelper _output;
    public KnightManualResultPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AtualizacaoDaBaseline_ResultadoPersisteERelido_IsoladoECongeladoNaPublicacao()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        Guid docId;

        // ---- 1) Banco na baseline publicada, com dado existente ------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(Baseline);
            (await db.Database.GetPendingMigrationsAsync()).Should().Equal(new[] { NewMigration },
                "da baseline publicada, a única migration pendente deste pacote é a do resultado manual");
            foreach (var t in new[] { tenantA, tenantB })
                db.Tenants.Add(new Tenant { Id = t, Name = "Cliente Demo", Slug = $"t-{t:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var doc = new GovernanceDocument { Title = "Politica de acesso privilegiado", Sha256 = new string('b', 64), FileName = "politica.pdf" };
            db.GovernanceDocuments.Add(doc);
            db.Connectors.Add(new ConnectorConfig
            {
                TenantId = tenantA, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Microsoft Entra ID", Enabled = true, EncryptedSettings = "cifrado",
            });
            await db.SaveChangesAsync();
            docId = doc.Id;
        }

        // ---- 2) Atualização pelo migrator real -----------------------------------------------------------------
        (await AegisApiHarness.RunMigratorAsync(pg.ConnectionString)).Should().Be(AegisScore.DbMigrator.MigratorExitCode.Success);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var key = KnightReferenceCatalog.Coverage().Controls.First(c => c.Disposition == KnightReferenceDisposition.ManualOnly).Control.Key;
        var justification = "Verificado: " + new string('j', 1988);   // 2000 caracteres, o limite da coluna
        var evidence = "CHG-" + new string('e', 496);                  // 500
        var responsible = new string('r', 200);                         // 200
        var until = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(90);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            (await db.GovernanceDocuments.AsNoTracking().SingleAsync(d => d.Id == docId)).Title.Should().Be("Politica de acesso privilegiado",
                "a atualização não toca o dado existente");
            (await db.KnightManualAssessments.CountAsync()).Should().Be(0);

            var svc = new KnightManualResultService(db, new SystemTenantContext(tenantA), clock);
            await svc.RecordAsync(new RecordKnightManualResultCommand(key, "NonCompliant", justification, responsible, evidence, docId, until),
                new RemediationActor(Guid.NewGuid(), "Gestor Demo"));
            // Substituição no MESMO instante: a ordem continua determinística no banco.
            await svc.RecordAsync(new RecordKnightManualResultCommand(key, "Compliant", justification, responsible, evidence, docId, until),
                new RemediationActor(Guid.NewGuid(), "Gestor Demo"));
        }

        // ---- 3) Releitura num contexto novo ---------------------------------------------------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var svc = new KnightManualResultService(db, new SystemTenantContext(tenantA), clock);
            var current = (await svc.CurrentAsync()).Should().ContainSingle().Subject;
            current.Result.Should().Be(KnightManualResult.Compliant, "o registro mais recente é o vigente, mesmo no mesmo instante");
            current.Justification.Should().Be(justification);
            current.EvidenceReference.Should().Be(evidence);
            current.ResponsibleName.Should().Be(responsible);
            current.ValidUntil.Should().Be(until);
            current.EvidenceDocumentTitle.Should().Be("Politica de acesso privilegiado");
            current.EvidenceDocumentSha256.Should().Be(new string('b', 64));
            current.CatalogVersion.Should().Be(KnightCatalog.Version);
            (await svc.HistoryAsync(key)).Select(h => h.Result).Should().Equal(KnightManualResult.Compliant, KnightManualResult.NonCompliant);
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
        {
            (await db.KnightManualAssessments.CountAsync()).Should().Be(0, "o filtro de tenant isola no banco");
            (await new KnightManualResultService(db, new SystemTenantContext(tenantB), clock).HistoryAsync(key)).Should().BeEmpty();
        }

        // ---- 4) Publicação congela, e a fotografia relida continua íntegra ---------------------------------------
        Guid snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var run = await KnightMulticloudReportTests.ServiceFor(db, tenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant).Handler())
                .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenantA), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
                .PublishAsync(PostureSnapshotType.Knight, null, run.Id)).Summary.Id;
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            await new KnightManualResultService(db, new SystemTenantContext(tenantA), clock)
                .RecordAsync(new RecordKnightManualResultCommand(key, "Withdrawn", "Retirado apos a publicacao para teste.", "Responsavel Demo", null, null, null),
                    new RemediationActor(null, "Gestor Demo"));
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var snapshot = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            PostureSnapshotHasher.Verify(snapshot).Should().BeTrue();
            var frozen = KnightReportModelBuilder.Build(snapshot, true).ReferenceCoverage!.ManualResults;
            frozen.Should().ContainSingle().Which.Result.Should().Be("Compliant", "a retirada posterior não altera o que foi publicado");
            frozen![0].Justification.Should().Be(justification);
        }
    }
}
