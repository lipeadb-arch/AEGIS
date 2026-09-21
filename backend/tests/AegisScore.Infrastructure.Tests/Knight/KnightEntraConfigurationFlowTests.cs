using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] O percurso INTEIRO dos controles de configuração do Microsoft Entra ID, com o coletor
/// REAL sobre respostas HTTP representativas do Microsoft Graph (sem rede): API de leitura → coletor → configuração
/// normalizada no ADM → persistência → releitura → avaliação → evidências e afetados → fotografia imutável → HTML,
/// CSV e PDF. Nada é montado direto no contexto de avaliação: a regra só vê o que passou pelo ADM.
/// </summary>
public sealed class KnightEntraConfigurationFlowTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-5151-5151-5151-515151515151");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-5252-5252-5252-525252525252");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightEntraConfigurationFlowTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static IReadOnlyList<string> ConfigurationControlIds =>
        EntraConfigurationControls.Definitions.Select(d => d.Id).ToList();

    [Fact]
    public async Task Conforme_TodosOsControlesDeConfiguracao_AprovadosPelaColetaRelidaDoAdm()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant));

        run.CatalogVersion.Should().Be("ak-knight-v5");
        run.SourceState.Should().Be(KnightSourceState.PartialCollection, "só as duas capacidades de risco de identidade devolvem 403 neste cenário");
        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        entity.IdentityAcquisitionId.Should().NotBeNull("a avaliação leu a aquisição persistida do ADM");

        // A configuração do locatário foi PERSISTIDA como evidência da aquisição, com contrato e versão.
        var kinds = await db.IdentityConfigurationObservations
            .Where(c => c.AcquisitionId == entity.IdentityAcquisitionId).Select(c => c.Kind).Distinct().ToListAsync();
        // Os tipos de configuração que o coletor do ENTRA produz — o catálogo de tipos também abriga os de
        // outras fontes (Microsoft Teams), que não têm por que aparecer numa aquisição do diretório.
        var doEntra = KnightConfigurationKinds.All
            .Where(spec => KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftEntraId).Contains(spec.Capability))
            .Select(spec => spec.Kind).ToList();
        kinds.Should().Contain(doEntra);
        kinds.Should().NotContain(ConfigurationObjectKind.TeamsMeetingPolicy,
            "uma aquisição do Entra ID não carrega configuração do Microsoft Teams");

        Dump(run);
        var notPassed = run.Indicators.Where(i => ConfigurationControlIds.Contains(i.IndicatorId) && i.Status != KnightIndicatorStatus.Passed)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.Evidence}").ToList();
        notPassed.Should().BeEmpty("no cenário conforme, cada controle de configuração tem evidência suficiente e aprovada");

        // Os controles originais que ganharam referência também aprovam pela mesma coleta.
        foreach (var id in new[] { "AK-ENTRA-006", "AK-ENTRA-007", "AK-ENTRA-008", "AK-ENTRA-014" })
            run.Indicators.Single(i => i.IndicatorId == id).Status.Should().Be(KnightIndicatorStatus.Passed, id);
    }

    [Fact]
    public async Task NaoConforme_TodosReprovados_ComAfetadosNomeadosPeloTipoReal()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant));
        Dump(run);

        var notExposed = run.Indicators.Where(i => ConfigurationControlIds.Contains(i.IndicatorId) && i.Status != KnightIndicatorStatus.Exposed)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.Evidence}").ToList();
        notExposed.Should().BeEmpty("no cenário não conforme, cada critério é violado de forma comprovável");

        var svc = ServiceFor(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant));
        async Task<IReadOnlyList<KnightAffectedObjectView>> Affected(string id) =>
            (await svc.GetAffectedObjectsAsync(run.Id, id, 1, 100, null))!.Items;

        // Cada afetado com o tipo REAL — aplicação não vira usuário, domínio não vira configuração genérica.
        (await Affected("AK-ENTRA-027")).Should().ContainSingle(o => o.Kind == KnightAffectedObjectKind.ServicePrincipal && o.ExternalId == "app-erp");
        (await Affected("AK-ENTRA-038")).Should().ContainSingle(o => o.Kind == KnightAffectedObjectKind.Domain && o.ExternalId == "demo.example.com");
        (await Affected("AK-ENTRA-046")).Should().ContainSingle(o => o.Kind == KnightAffectedObjectKind.Group && o.ExternalId == "grp-mkt");
        (await Affected("AK-ENTRA-047")).Should().ContainSingle(o => o.Kind == KnightAffectedObjectKind.User && o.ExternalId == "u1");
        (await Affected("AK-ENTRA-049")).Should().HaveCount(5).And.OnlyContain(o => o.Kind == KnightAffectedObjectKind.User);
        var permanent = await Affected("AK-ENTRA-050");
        permanent.Select(o => o.Kind).Should().BeEquivalentTo(new[] { KnightAffectedObjectKind.User, KnightAffectedObjectKind.ServicePrincipal });
        (await Affected("AK-ENTRA-054")).Should().HaveCount(5).And.OnlyContain(o => o.Kind == KnightAffectedObjectKind.DirectoryRole);

        // A composição dita na tela vem da MESMA definição das exportações.
        run.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-050").AffectedComposition
            .Should().Be("2 identidades: 1 conta de usuário e 1 aplicação");
        run.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-049").AffectedComposition.Should().Be("5 contas de usuário");

        // Configuração do locatário errada: a própria configuração é a evidência ("onde foi encontrado").
        var ev = (await svc.GetAffectedObjectsAsync(run.Id, "AK-ENTRA-016", 1, 50, null, default, KnightObjectRelation.Evidence))!.Items;
        ev.Should().ContainSingle(o => o.Kind == KnightAffectedObjectKind.TenantSetting && o.Detail!.Contains("Encontrado: Sim. Esperado: Não."));
    }

    [Fact]
    public async Task ReferenciasImplementadas_TemAprovacaoEReprovacaoComprovadasPeloColetor()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var ok = await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant));
        var bad = await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant));

        var coverage = KnightReferenceCatalog.Coverage();
        var implemented = coverage.Controls
            .Where(c => c.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial)
            .SelectMany(c => c.IndicatorIds.Select(id => (Reference: c.Control.Key, Id: id)))
            // Esta bateria corre o cenário do ENTRA ID. As referências sustentadas por outras fontes têm a
            // mesma prova na bateria da fonte delas (ver KnightTeamsConfigurationFlowTests).
            .Where(x => x.Id.StartsWith("AK-ENTRA-", StringComparison.Ordinal))
            .ToList();
        implemented.Should().NotBeEmpty();

        var gaps = new List<string>();
        foreach (var (reference, id) in implemented)
        {
            var pass = ok.Indicators.SingleOrDefault(i => i.IndicatorId == id)?.Status;
            var fail = bad.Indicators.SingleOrDefault(i => i.IndicatorId == id)?.Status;
            if (pass != KnightIndicatorStatus.Passed || fail != KnightIndicatorStatus.Exposed)
                gaps.Add($"{reference} → {id}: conforme={pass}, não conforme={fail}");
        }
        gaps.Should().BeEmpty("uma referência só conta como implementada quando o controle aprova e reprova pelo caminho do coletor");
    }

    [Fact]
    public async Task ColetaParcial_PermissaoOuLicencaAusente_NuncaAprova_EDizOMotivoEspecifico()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant, url =>
            url.Contains("roleAssignmentSchedules") ? EntraConfigurationScenario.Forbidden()
            : url.Contains("accessReviews/definitions") ? EntraConfigurationScenario.Forbidden("AadPremiumLicenseRequired")
            : url.Contains("/directory/onPremisesSynchronization") ? EntraConfigurationScenario.Forbidden()
            : url.Contains("/policies/deviceRegistrationPolicy") ? (HttpStatusCode.ServiceUnavailable, "{}")
            : null);
        var run = await RunAsync(db, TenantA, scenario);

        run.SourceState.Should().Be(KnightSourceState.PartialCollection);
        run.Capabilities.Single(c => c.Capability == KnightCapability.PrivilegedIdentityManagement).Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        run.Capabilities.Single(c => c.Capability == KnightCapability.AccessReviews).Outcome.Should().Be(KnightCapabilityOutcome.LimitedByLicense);
        run.Capabilities.Single(c => c.Capability == KnightCapability.DeviceRegistrationPolicy).Outcome.Should().Be(KnightCapabilityOutcome.Unavailable);
        run.Capabilities.Single(c => c.Capability == KnightCapability.DirectorySynchronization).Outcome.Should().Be(KnightCapabilityOutcome.Collected,
            "a organização foi lida; só o recurso de sincronização faltou, e isso é dito no documento");

        KnightIndicatorView I(string id) => run.Indicators.Single(i => i.IndicatorId == id);
        foreach (var id in new[] { "AK-ENTRA-050", "AK-ENTRA-051", "AK-ENTRA-052" })
            I(id).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
        I("AK-ENTRA-050").NotEvaluatedReason.Should().Contain("Permissão insuficiente");
        foreach (var id in new[] { "AK-ENTRA-053", "AK-ENTRA-054", "AK-ENTRA-030" })
        {
            I(id).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            I(id).NotEvaluatedReason.Should().Contain("licença");
        }
        I("AK-ENTRA-039").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        I("AK-ENTRA-039").NotEvaluatedReason.Should().Contain("OnPremDirectorySynchronization.Read.All");
        foreach (var id in new[] { "AK-ENTRA-040", "AK-ENTRA-041", "AK-ENTRA-042", "AK-ENTRA-043", "AK-ENTRA-044", "AK-ENTRA-045" })
            I(id).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);

        // O resto da coleta continua valendo: falha de uma capacidade não derruba as outras.
        I("AK-ENTRA-016").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.Coverage.Should().BeLessThan(100);
    }

    [Fact]
    public async Task FotografiaEExportacoes_ReconciliamOsMesmosNumeros_ECongelamImpactoECobertura()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant));

        var published = await new PostureSnapshotService(db, new SystemTenantContext(TenantA), new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
            .PublishAsync(PostureSnapshotType.Knight, null, run.Id);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == published.Summary.Id);

        PostureSnapshotHasher.Verify(snapshot).Should().BeTrue();
        snapshot.ReferenceCoverageJson.Should().NotBeNullOrWhiteSpace();
        snapshot.Indicators.Where(i => ConfigurationControlIds.Contains(i.IndicatorId))
            .Should().OnlyContain(i => i.Impact != null && i.Platform == "Microsoft Entra ID" && i.References.Any(r => r.Framework.StartsWith("CIS ")));

        var model = KnightReportModelBuilder.Build(snapshot, true);
        model.ReferenceCoverage.Should().NotBeNull();
        var live = KnightReferenceCatalog.Coverage().Total;
        model.ReferenceCoverage!.Total.Implemented.Should().Be(live.Implemented);
        model.ReferenceCoverage.Total.Partial.Should().Be(live.Partial);
        model.ReferenceCoverage.Total.Total.Should().Be(457);
        model.Controls.Should().HaveCount(snapshot.Indicators.Count);
        model.Controls.Single(c => c.Id == "AK-ENTRA-049").ProvenReach.Should().Be("Alcance comprovado nesta coleta: 5 contas de usuário.");
        model.Controls.Single(c => c.Id == "AK-ENTRA-016").ProvenReach.Should().StartWith("Alcance comprovado nesta coleta: configuração do locatário");

        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("Impacto potencial").And.Contain("referenceCoverage").And.Contain("afetam só a visualização");
        html.Should().NotContain("<script src").And.NotContain("<link");
        CspHashesMatchWhatTheBrowserHashes(html);

        var csvText = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Csv))!.Content).TrimStart('﻿');
        var rows = csvText.Split('\n').Where(l => l.Length > 0).ToList();
        var header = rows[0].TrimEnd('\r').Split(';');
        header.Should().ContainInOrder("ObjectConfiguration", "Platform", "Impact", "ProvenReach", "AffectedComposition");
        var occurrences = model.Controls.Where(c => c.Status is "Exposed" or "Mitigated").Sum(c => c.Objects.Count(o => o.Relation == "Affected"));
        occurrences.Should().Be(model.Kpis.Occurrences);
        csvText.Should().Contain("Conta de usuário").And.Contain("Domínio").And.NotContain("objeto(s)");

        var pdf = (await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Pdf))!.Content;
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");

        // Imutável: uma nova avaliação, com outro resultado, não muda o relatório publicado.
        await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant));
        var again = Encoding.UTF8.GetString((await new PostureSnapshotExporter(NewContext(TenantA)).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        again.Should().Be(html);
    }

    [Fact]
    public async Task IsolamentoEntreClientes_ConfiguracaoDoLocatarioNaoVazaParaOutroTenant()
    {
        await SeedAsync(TenantA);
        await SeedAsync(TenantB);
        await using (var db = NewContext(TenantA))
            await RunAsync(db, TenantA, new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant));

        await using var dbB = NewContext(TenantB);
        (await dbB.IdentityConfigurationObservations.CountAsync()).Should().Be(0);
        (await dbB.KnightAssessmentRuns.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// O navegador normaliza CRLF para LF ao ler o documento e só então calcula o hash que confere com a CSP. Um
    /// estilo ou script cujo hash não confira é bloqueado e o relatório abre sem funcionar — inclusive offline.
    /// </summary>
    private static void CspHashesMatchWhatTheBrowserHashes(string html)
    {
        static string Between(string s, string start, string end)
        {
            var i = s.IndexOf(start, StringComparison.Ordinal) + start.Length;
            return s[i..s.IndexOf(end, i, StringComparison.Ordinal)];
        }
        static string Hash(string content) => "sha256-" + Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n").Replace("\r", "\n"))));
        var csp = Between(html, "http-equiv=\"Content-Security-Policy\" content=\"", "\">");
        var style = Between(html, "<style>", "</style>");
        var script = Between(html, "<script>", "</script>");
        csp.Should().Contain("style-src '" + Hash(style) + "'");
        csp.Should().Contain("script-src '" + Hash(script) + "'");
        style.Should().NotContain("\r");
        script.Should().NotContain("\r");
    }

    // ---- Infraestrutura -----------------------------------------------------------------------------

    private async Task<KnightAssessment> RunAsync(AegisScoreDbContext db, Guid tenant, EntraConfigurationScenario scenario) =>
        await ServiceFor(db, tenant, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);

    private static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId, EntraConfigurationScenario scenario) =>
        KnightMulticloudReportTests.ServiceFor(db, tenantId, scenario.Handler());

    private void Dump(KnightAssessment run)
    {
        foreach (var i in run.Indicators.OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
            _output.WriteLine($"{i.IndicatorId} {i.Status} [{i.AffectedObjectCount}] {i.Evidence}");
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync(Guid tenantId)
    {
        await using (var db = NewContext(null))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Cliente Demo", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft Entra ID", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
