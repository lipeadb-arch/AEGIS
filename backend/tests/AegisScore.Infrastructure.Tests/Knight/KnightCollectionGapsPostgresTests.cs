using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
using AegisScore.Application.Knight;
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
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] O percurso inteiro sobre o PostgreSQL MIGRADO: coletores reais (Graph e Resource Manager
/// sintéticos) → documentos no ADM em jsonb (pertencimento dos grupos, papel v2, população das contas habilitadas, resumo da
/// descoberta do Azure) → releitura → avaliação → relatório consolidado em HTML. O SQLite não prova jsonb nem os limites das
/// colunas; este teste prova.
/// </summary>
public sealed class KnightCollectionGapsPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightCollectionGapsPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EntraAzureIntune_DadosNovosPassamPeloAdmPostgres_EChegamAoHtml()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();
        var tenant = await SeedAsync(opt);

        KnightAssessment entra, azure, intune;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var scenario = new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant,
                new KnightEntraGroupMembershipTests.GroupTenant().Route);
            entra = await KnightMulticloudReportTests.ServiceFor(db, tenant, scenario.Handler()).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
            azure = await KnightAzureFlowTests.ServiceFor(db, tenant, new AzureScenario(AzureScenario.Variant.AllDisabled),
                    new KnightMicrosoftServiceConfiguration(KnightSourceType.MicrosoftAzure, "dir-demo-0001", "client-demo", "segredo-sintetico"))
                .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
            intune = await KnightM365ServicesFlowTests.ServiceFor(db, tenant,
                    new M365ServicesScenario(M365ServicesScenario.Variant.Compliant) { IntuneSettingsBody = """{"settings":{"deviceComplianceCheckinThresholdDays":30}}""" })
                .RunAssessmentAsync(KnightSourceType.MicrosoftIntune);
        }

        string Status(KnightAssessment run, string id) => run.Indicators.Single(i => i.IndicatorId == id).Status.ToString();
        new[] { Status(entra, "AK-ENTRA-001"), Status(entra, "AK-ENTRA-008"), Status(entra, "AK-ENTRA-011"), Status(entra, "AK-ENTRA-014") }
            .Should().NotContain(nameof(KnightIndicatorStatus.NotEvaluated), "grupos e contas resolvidos no jsonb do PostgreSQL");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            // ADM: documentos gravados em jsonb e relidos pelo contrato, com a releitura que a avaliação usa.
            var acquisition = (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == entra.Id)).IdentityAcquisitionId;
            var docs = await db.IdentityConfigurationObservations.AsNoTracking().Where(o => o.AcquisitionId == acquisition).ToListAsync();
            var observed = docs.Select(d => new IdentityObservedConfiguration(d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)).ToList();
            var tenantConfig = KnightTenantConfiguration.FromObserved(observed, entra.Capabilities);
            var directory = DirectoryConfigurationDocuments.FromObserved(observed, true, true, true, IdentityKnightBoundary.EnabledAccountIds(tenantConfig));
            directory.GroupMemberships!.Select(g => g.GroupId).Should().BeEquivalentTo(new[] { "g-ga", "g-dev", "g-todos", "g-bg" });
            directory.GroupMemberships!.Should().OnlyContain(g => g.State == EntraGroupMembershipState.Complete);
            directory.PrivilegedRoles!.Should().OnlyContain(r => r.MemberTypesCaptured);
            directory.EnabledAccountIds.Should().HaveCount(9);
            var reread = ConditionalAccessAnalyzer.Analyze(directory)!;
            reread.Baseline.BaselinePolicyIds.Should().Equal("p-grupo");
            reread.AdminMfa.UncoveredRoleCount.Should().Be(0);

            var azureAcq = (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == azure.Id)).IdentityAcquisitionId;
            var summary = await db.IdentityConfigurationObservations.AsNoTracking()
                .SingleAsync(o => o.AcquisitionId == azureAcq && o.Kind == ConfigurationObjectKind.AzureDiscovery);
            JsonSerializer.Deserialize<AzureDiscoverySummary>(summary.ConfigurationJson, IdentityEvidenceFactsJson.Options)!
                .Should().BeEquivalentTo(new { Listed = 2, Selected = 0, Disabled = 2 });

            // Textos novos cabem nas colunas reais (o PostgreSQL recusa o que o SQLite aceita).
            var objects = await db.KnightAffectedObjects.AsNoTracking().Where(o => o.RunId == entra.Id && o.IndicatorId == "AK-ENTRA-014").ToListAsync();
            objects.Should().Contain(o => o.Detail!.Contains("9 de 9 conta(s) habilitada(s) alcançada(s)"));
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var detail = await new PostureSnapshotService(db, new SystemTenantContext(tenant)).PublishConsolidatedKnightAsync(null);
            detail.Composition.Where(c => c.Included).Select(c => c.Source).Should().BeEquivalentTo(new[]
                { KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftAzure, KnightSourceType.MicrosoftIntune });
            var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(detail.Summary.Id, PostureExportFormat.Html))!.Content);
            // O relatório renderiza a partir do JSON autoritativo embutido (script#aegis-data), com acentos escapados: confere decodificado.
            var data = System.Text.RegularExpressions.Regex.Match(html, "<script[^>]*id=\"aegis-data\"[^>]*>(.*?)</script>",
                System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
            var text = JsonSerializer.Serialize(JsonDocument.Parse(data).RootElement,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            html.Should().NotContain("token-sintetico");
            text.Should().Contain("9 de 9 conta(s) habilitada(s) alcançada(s)", "a evidência da base mínima conta a conta chega ao HTML")
                .And.Contain("2 assinatura(s) listada(s) e nenhuma selecionada (2 desabilitada(s))", "o motivo do Azure diz qual caso ocorreu")
                .And.Contain("propriedade secureByDefault ausente em settings", "o motivo do Intune diz o que faltou")
                .And.Contain("Pertencimento dos grupos referenciados", "a capacidade nova aparece com rótulo legível");
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
