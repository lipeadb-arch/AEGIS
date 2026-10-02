using System;
using System.Collections.Generic;
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
/// [AEGIS-KNIGHT-COVERAGE-04] O que só o PostgreSQL real prova para Intune, SharePoint/OneDrive, Fabric, Defender para
/// Office 365 e Purview, sobre o schema MIGRADO:
///   • os documentos tipados de cada fonte gravam em jsonb e voltam pelo contrato: a avaliação relida do banco dá o
///     MESMO veredito da avaliação gravada;
///   • os textos gravados (motivo, detalhe, configuração observada, identificador) cabem nas colunas — o SQLite ignora
///     HasMaxLength, o PostgreSQL recusa;
///   • as cinco aquisições convivem no mesmo tenant e compõem UM relatório consolidado, exportado em HTML, CSV e PDF.
/// </summary>
public sealed class KnightM365ServicesPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightM365ServicesPostgresTests(ITestOutputHelper output) => _output = output;

    private static readonly KnightSourceType[] Fontes =
    {
        KnightSourceType.MicrosoftIntune, KnightSourceType.MicrosoftSharePoint, KnightSourceType.MicrosoftFabric,
        KnightSourceType.MicrosoftDefenderForOffice365, KnightSourceType.MicrosoftPurview,
    };

    [Fact]
    public async Task CincoFontes_JsonbReal_MesmoVeredito_TextosCabem_EConsolidadoExporta()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();
        var tenant = await SeedAsync(opt);

        // Inadequado: é o cenário que grava afetados, detalhes e configurações observadas mais longos.
        var runs = new Dictionary<KnightSourceType, KnightAssessment>();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            foreach (var fonte in Fontes)
                runs[fonte] = await KnightM365ServicesFlowTests
                    .ServiceFor(db, tenant, new M365ServicesScenario(M365ServicesScenario.Variant.NonCompliant))
                    .RunAssessmentAsync(fonte);

        foreach (var (fonte, run) in runs)
        {
            run.SourceState.Should().Be(KnightSourceState.Completed, fonte.ToString());
            run.Indicators.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.Exposed, fonte.ToString());
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            // Cinco aquisições, uma por fonte, no mesmo tenant.
            (await db.IdentityAcquisitions.AsNoTracking().Select(a => a.Provider).ToListAsync())
                .Should().BeEquivalentTo(Fontes);

            // Relida do jsonb, a configuração dá o MESMO veredito que a avaliação gravou.
            foreach (var (fonte, run) in runs)
            {
                var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
                var docs = await db.IdentityConfigurationObservations.AsNoTracking()
                    .Where(o => o.AcquisitionId == entity.IdentityAcquisitionId).ToListAsync();
                var config = KnightTenantConfiguration.FromObserved(
                    docs.Select(d => new IdentityObservedConfiguration(d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)),
                    run.Capabilities);
                var ctx = new KnightEvaluationContext(KnightFactSet.Empty, run.Capabilities, null, config,
                    Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);
                foreach (var d in KnightCatalog.Indicators.Where(d => d.Sources.Contains(fonte) && d.Evaluate is not null))
                    d.Evaluate!(ctx).Status.Should().Be(run.Indicators.Single(i => i.IndicatorId == d.Id).Status, $"{d.Id} relido do jsonb");
            }
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var detail = await new PostureSnapshotService(db, new SystemTenantContext(tenant), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
                .PublishConsolidatedKnightAsync(null);
            detail.Composition.Where(c => c.Included).Select(c => c.Source).Should().BeEquivalentTo(Fontes);

            var exporter = new PostureSnapshotExporter(db);
            var html = Encoding.UTF8.GetString((await exporter.ExportAsync(detail.Summary.Id, PostureExportFormat.Html))!.Content);
            foreach (var rotulo in new[] { "Microsoft Intune", "SharePoint e OneDrive", "Microsoft Fabric (Power BI)", "Microsoft Defender para Office 365", "Microsoft Purview" })
                html.Should().Contain(rotulo);
            var csv = Encoding.UTF8.GetString((await exporter.ExportAsync(detail.Summary.Id, PostureExportFormat.Csv))!.Content);
            csv.Should().Contain("AK-MDO-008").And.Contain("AK-PUR-005").And.Contain("AK-FAB-012");
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
