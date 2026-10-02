using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] O que só o PostgreSQL real prova para o Azure, sobre o schema MIGRADO:
///   • centenas de documentos de recurso gravam em jsonb e voltam pelo contrato: a avaliação relida do banco dá o MESMO
///     veredito da avaliação gravada, controle a controle;
///   • os textos gravados (identificador do recurso, nome, detalhe, configuração observada) cabem nas colunas — o SQLite
///     ignora HasMaxLength, o PostgreSQL recusa;
///   • o Azure compõe o relatório consolidado junto com outra fonte e é exportado em HTML, CSV e PDF.
/// </summary>
public sealed class KnightAzurePostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightAzurePostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Azure_JsonbReal_MesmoVeredito_TextosCabem_EConsolidadoExporta()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();
        var tenant = await SeedAsync(opt);

        KnightAssessment azure, intune;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            azure = await KnightAzureFlowTests.ServiceFor(db, tenant, new AzureScenario(AzureScenario.Variant.NonCompliant))
                .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
            intune = await KnightM365ServicesFlowTests.ServiceFor(db, tenant, new M365ServicesScenario(M365ServicesScenario.Variant.NonCompliant))
                .RunAssessmentAsync(KnightSourceType.MicrosoftIntune);
        }
        azure.SourceState.Should().Be(KnightSourceState.Completed);
        azure.Indicators.Count(i => i.Status == KnightIndicatorStatus.Exposed).Should().BeGreaterThan(180);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == azure.Id);
            var docs = await db.IdentityConfigurationObservations.AsNoTracking()
                .Where(o => o.AcquisitionId == entity.IdentityAcquisitionId).ToListAsync();
            docs.Should().HaveCountGreaterThan(50);
            var config = KnightTenantConfiguration.FromObserved(
                docs.Select(d => new IdentityObservedConfiguration(d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)),
                azure.Capabilities);
            var ctx = new KnightEvaluationContext(KnightFactSet.Empty, azure.Capabilities, null, config,
                Array.Empty<KnightAffectedObjectEvidence>(), azure.CompletedAt ?? azure.StartedAt);
            foreach (var d in KnightCatalog.Indicators.Where(d => d.Sources.Contains(KnightSourceType.MicrosoftAzure) && d.Evaluate is not null))
                d.Evaluate!(ctx).Status.Should().Be(azure.Indicators.Single(i => i.IndicatorId == d.Id).Status, $"{d.Id} relido do jsonb");

            // O afetado é gravado com o identificador compacto (≤ 200) e o detalhe do encontrado/esperado.
            var objects = await db.KnightAffectedObjects.AsNoTracking().Where(o => o.RunId == azure.Id).ToListAsync();
            objects.Should().Contain(o => o.Kind == KnightAffectedObjectKind.CloudResource);
            objects.Should().OnlyContain(o => o.ExternalId.Length <= 200);
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var detail = await new PostureSnapshotService(db, new SystemTenantContext(tenant), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
                .PublishConsolidatedKnightAsync(null);
            detail.Composition.Where(c => c.Included).Select(c => c.Source)
                .Should().BeEquivalentTo(new[] { KnightSourceType.MicrosoftAzure, KnightSourceType.MicrosoftIntune });

            var exporter = new PostureSnapshotExporter(db);
            var html = Encoding.UTF8.GetString((await exporter.ExportAsync(detail.Summary.Id, PostureExportFormat.Html))!.Content);
            html.Should().Contain("Microsoft Azure").And.Contain("Armazenamento do Azure").And.Contain("Recurso de nuvem");
            var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(detail.Summary.Id, PostureExportFormat.Csv))!.Content);
            csv.Should().Contain("AK-AZ-STO-018").And.Contain("AK-AZ-NET-001").And.Contain("AK-INT-001");
            (await exporter.ExportAsync(detail.Summary.Id, PostureExportFormat.Pdf))!.Content.Take(4).Should().Equal((byte)0x25, (byte)0x50, (byte)0x44, (byte)0x46);
        }
    }

    private static async Task<Guid> SeedAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        var tenant = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente Demo", Slug = $"t-{tenant:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
            });
            await db.SaveChangesAsync();
        }
        return tenant;
    }
}
