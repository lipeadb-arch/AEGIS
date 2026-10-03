using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] O percurso INTEIRO de Intune, SharePoint/OneDrive, Fabric, Defender para Office 365 e
/// Purview: resposta da fonte (HTTP ou adaptador PowerShell, sintéticos) → coletor REAL → documentos no ADM →
/// persistência → RELEITURA → avaliação. Nada é montado direto no contexto de avaliação: a regra só vê o que passou
/// pelo ADM. Controle escrito não é cobertura — é este percurso que a comprova.
/// </summary>
public sealed class KnightM365ServicesFlowTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0404-0404-0404-040404040404");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightM365ServicesFlowTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    public static IEnumerable<object[]> Sources() => new[]
    {
        new object[] { KnightSourceType.MicrosoftIntune, "AK-INT-", 2 },
        new object[] { KnightSourceType.MicrosoftSharePoint, "AK-SPO-", 13 },
        new object[] { KnightSourceType.MicrosoftFabric, "AK-FAB-", 12 },
        new object[] { KnightSourceType.MicrosoftDefenderForOffice365, "AK-MDO-", 19 },
        new object[] { KnightSourceType.MicrosoftPurview, "AK-PUR-", 5 },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Conforme_CadaControleDaFonteAprova_PelaColetaRelidaDoAdm(KnightSourceType source, string prefix, int count)
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant);
        var run = await ServiceFor(db, scenario).RunAssessmentAsync(source);
        Dump(run, prefix);

        run.SourceType.Should().Be(source);
        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.CatalogVersion.Should().Be(KnightCatalog.Version);
        run.Capabilities.Select(c => c.Capability).Should().BeEquivalentTo(KnightCollectorCapabilities.Produces(source),
            "o coletor real registra o desfecho de cada capacidade que o mapa declara — nem a mais, nem a menos");
        run.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected);

        // A configuração foi PERSISTIDA na aquisição da própria fonte e é dela que a avaliação leu.
        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        entity.IdentityAcquisitionId.Should().NotBeNull();
        var acquisition = await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == entity.IdentityAcquisitionId);
        acquisition.Provider.Should().Be(source);
        (await db.IdentityConfigurationObservations.AsNoTracking().CountAsync(o => o.AcquisitionId == acquisition.Id))
            .Should().BePositive();
        (await db.IdentityEntities.CountAsync()).Should().Be(0, "configuração não é identidade: nada é inventado no inventário");

        var own = run.Indicators.Where(i => i.IndicatorId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(count);
        own.Where(i => i.Status != KnightIndicatorStatus.Passed)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.NotEvaluatedReason ?? i.Evidence}")
            .Should().BeEmpty("no cenário conforme cada critério tem evidência suficiente e aprova");
        run.Indicators.Should().OnlyContain(i => i.IndicatorId.StartsWith(prefix, StringComparison.Ordinal),
            "nenhum controle de outra fonte entra nesta execução");
        run.Score.Should().Be(100);
        run.Coverage.Should().Be(100);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Inadequado_CadaControleDaFonteReprova_ComOQueFoiEncontradoEOEsperado(KnightSourceType source, string prefix, int count)
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.NonCompliant)).RunAssessmentAsync(source);
        Dump(run, prefix);

        var own = run.Indicators.Where(i => i.IndicatorId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(count);
        own.Where(i => i.Status != KnightIndicatorStatus.Exposed)
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.NotEvaluatedReason ?? i.Evidence}")
            .Should().BeEmpty("no cenário inadequado cada critério tem violação demonstrável");

        // Cada reprovação carrega a configuração encontrada (evidência ou afetado) — nunca um veredito sem objeto.
        own.Should().OnlyContain(i => i.AffectedObjectCount + i.EvidenceObjectCount > 0);
        own.Should().OnlyContain(i => i.Presentation != null && !string.IsNullOrWhiteSpace(i.Presentation.Impact),
            "o relatório mostra risco e impacto próprios de cada controle");
        run.Score.Should().Be(0);
    }

    /// <summary>
    /// Sem certificado, a API administrativa do SharePoint não é chamada: os controles que dependem dela ficam NÃO
    /// AVALIADOS com o motivo — nunca reprovados, nunca aprovados — e os que o Graph sustenta continuam avaliados.
    /// </summary>
    [Fact]
    public async Task SharePointSemCertificado_ControlesDaApiAdministrativa_NaoAvaliadosComOMotivo()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant);
        var run = await ServiceFor(db, scenario, withCertificate: false).RunAssessmentAsync(KnightSourceType.MicrosoftSharePoint);
        Dump(run, "AK-SPO-");

        scenario.Requests.Should().NotContain(r => r.RequestUri!.Host.EndsWith("-admin.sharepoint.com"),
            "sem certificado a API administrativa não é chamada");
        run.Capabilities.Single(c => c.Capability == KnightCapability.SharePointAdminTenant).Outcome
            .Should().Be(KnightCapabilityOutcome.NotAttempted);

        var byGraph = new[] { "AK-SPO-001", "AK-SPO-003", "AK-SPO-005", "AK-SPO-006", "AK-SPO-013" };
        foreach (var i in run.Indicators)
        {
            if (byGraph.Contains(i.IndicatorId))
                i.Status.Should().Be(KnightIndicatorStatus.Passed, i.IndicatorId);
            else
            {
                i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, i.IndicatorId);
                i.NotEvaluatedReason.Should().Contain("certificado", i.IndicatorId);
            }
        }
    }

    /// <summary>Com certificado, TODO pedido de token usa asserção assinada — o segredo não viaja.</summary>
    [Fact]
    public async Task ComCertificado_TokensPorAssercaoAssinada_ESemSegredoNoPedido()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant);
        await ServiceFor(db, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftSharePoint);
        await ServiceFor(db, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftFabric);

        scenario.TokenBodies.Should().NotBeEmpty();
        scenario.TokenBodies.Should().OnlyContain(b => b.Contains("client_assertion=") && !b.Contains("client_secret"));
        scenario.TokenBodies.Should().Contain(b => b.Contains(Uri.EscapeDataString("https://clientedemo-admin.sharepoint.com/.default")),
            "o token da API administrativa é pedido para o host do locatário derivado do domínio inicial");
        scenario.TokenBodies.Should().Contain(b => b.Contains(Uri.EscapeDataString("https://api.fabric.microsoft.com/.default")));
    }

    /// <summary>A lista do Fabric vem em páginas: a segunda (por <c>continuationUri</c>) é lida — senão 6 controles sumiriam.</summary>
    [Fact]
    public async Task Fabric_LeTodasAsPaginas_EIgnoraConfiguracaoSemControle()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant);
        var run = await ServiceFor(db, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftFabric);

        scenario.Requests.Count(r => r.RequestUri!.Host == "api.fabric.microsoft.com").Should().Be(2);
        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        (await db.IdentityConfigurationObservations.AsNoTracking()
                .CountAsync(o => o.AcquisitionId == entity.IdentityAcquisitionId && o.Kind == ConfigurationObjectKind.FabricTenantSetting))
            .Should().Be(13, "as 13 configurações das duas páginas foram preservadas, inclusive a que nenhum controle usa");
    }

    [Fact]
    public async Task Fabric_RecusadoPorPermissao_NaoAvaliado_ComORequisitoDoFabric()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.FabricForbidden))
            .RunAssessmentAsync(KnightSourceType.MicrosoftFabric);

        run.Capabilities.Single().Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        run.Indicators.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);
        run.Score.Should().BeNull("sem nenhum controle avaliado não há nota");
        KnightCapabilityLabels.RequiredPermission(KnightCapability.FabricTenantSettings, KnightSourceType.MicrosoftFabric)
            .Should().Contain("APIs de administração somente leitura");
    }

    /// <summary>DNS que não respondeu não é "sem registro": SPF e DMARC ficam sem veredito, e o motivo é dito.</summary>
    [Fact]
    public async Task DnsNaoResolvido_SpfEDmarc_NaoAvaliados_NuncaReprovados()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.DnsUnresolved))
            .RunAssessmentAsync(KnightSourceType.MicrosoftDefenderForOffice365);

        foreach (var id in new[] { "AK-MDO-008", "AK-MDO-010" })
        {
            var i = run.Indicators.Single(x => x.IndicatorId == id);
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            i.NotEvaluatedReason.Should().Contain("sem o valor informado", id);
        }
        run.Indicators.Single(x => x.IndicatorId == "AK-MDO-009").Status.Should().Be(KnightIndicatorStatus.Passed,
            "o DKIM vem do Exchange Online, não do DNS");
    }

    /// <summary>Mais domínios que o teto de consulta: nada é aprovado sobre uma população que não foi inteira consultada.</summary>
    [Fact]
    public async Task DominiosAcimaDoTeto_SpfEDmarc_NaoAprovamSobrePopulacaoIncompleta()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.ManyDomains))
            .RunAssessmentAsync(KnightSourceType.MicrosoftDefenderForOffice365);

        foreach (var id in new[] { "AK-MDO-008", "AK-MDO-010" })
        {
            var i = run.Indicators.Single(x => x.IndicatorId == id);
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            i.NotEvaluatedReason.Should().Contain("não foram consultados no DNS", id);
        }
    }

    /// <summary>
    /// Uma política personalizada inadequada com a regra DESLIGADA não alcança ninguém: não reprova o critério, e a
    /// padrão (conforme) continua valendo para todos.
    /// </summary>
    [Fact]
    public async Task PoliticaComRegraDesligada_NaoAlcancaNinguem_ENaoReprova()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.InertPolicy))
            .RunAssessmentAsync(KnightSourceType.MicrosoftDefenderForOffice365);

        var filtro = run.Indicators.Single(x => x.IndicatorId == "AK-MDO-002");
        filtro.Status.Should().Be(KnightIndicatorStatus.Passed);
        filtro.Evidence.Should().Contain("1 política(s) efetiva(s)");
    }

    /// <summary>
    /// Security &amp; Compliance recusado: DLP e rótulos ficam não avaliados com a permissão DAQUELA sessão; a auditoria,
    /// que vem da sessão do Exchange Online, continua avaliada.
    /// </summary>
    [Fact]
    public async Task SecurityComplianceRecusado_DlpERotulosNaoAvaliados_AuditoriaContinua()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.ComplianceDenied);
        var run = await ServiceFor(db, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftPurview);

        scenario.ProtectionRequests.Should().ContainSingle().Which.ComplianceToken.Should().NotBeNullOrEmpty(
            "o Purview pede o token do Security & Compliance separadamente");
        run.Indicators.Single(i => i.IndicatorId == "AK-PUR-001").Status.Should().Be(KnightIndicatorStatus.Passed);
        foreach (var id in new[] { "AK-PUR-002", "AK-PUR-003", "AK-PUR-004", "AK-PUR-005" })
        {
            var i = run.Indicators.Single(x => x.IndicatorId == id);
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            i.NotEvaluatedReason.Should().Contain("Security & Compliance", id).And.Contain("Microsoft Exchange Online Protection", id);
        }
        KnightCapabilityLabels.RequiredPermission(KnightCapability.PurviewDlpPolicies, KnightSourceType.MicrosoftPurview)
            .Should().Contain("Microsoft Exchange Online Protection");
    }

    /// <summary>
    /// O resumo de afetados da COMPOSIÇÃO (relatório consolidado) agrupa as execuções reais das fontes: um objeto que
    /// aparece em duas execuções é UM item, e as ocorrências somam. A composição nunca usa um id sintético.
    /// </summary>
    [Fact]
    public async Task ResumoDaComposicao_ObjetoEmDuasExecucoes_ContaUmaVez()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.NonCompliant));
        var defender = await svc.RunAssessmentAsync(KnightSourceType.MicrosoftDefenderForOffice365);
        var purview = await svc.RunAssessmentAsync(KnightSourceType.MicrosoftPurview);
        var defender2 = await svc.RunAssessmentAsync(KnightSourceType.MicrosoftDefenderForOffice365);

        var um = (await svc.GetAffectedSummaryAsync(defender.Id))!;
        um.UniqueObjects.Should().BePositive();

        var composicao = (await svc.GetAffectedSummaryAsync(new[] { defender.Id, defender2.Id, purview.Id }))!;
        composicao.RunId.Should().Be(Guid.Empty, "a composição não é uma execução");
        composicao.UniqueObjects.Should().Be(um.UniqueObjects, "os mesmos objetos nas duas execuções do Defender contam uma vez");
        composicao.Occurrences.Should().Be(2 * um.Occurrences, "cada execução registra as próprias ocorrências");
        composicao.ExposedControls.Should().Be(defender.ExposedCount + defender2.ExposedCount + purview.ExposedCount);

        (await svc.GetAffectedSummaryAsync(new[] { Guid.NewGuid() })).Should().BeNull("execução inexistente não vira resumo vazio");
    }

    /// <summary>
    /// O relatório publicado de uma fonte nova mostra as capacidades de coleta em português — o código da capacidade
    /// continua só como chave que liga uma limitação aos controles prejudicados.
    /// </summary>
    [Fact]
    public async Task RelatorioPublicado_CapacidadesEmPortugues_CodigoSoComoChave()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new M365ServicesScenario(M365ServicesScenario.Variant.NonCompliant))
            .RunAssessmentAsync(KnightSourceType.MicrosoftDefenderForOffice365);
        var detail = await new AegisScore.Infrastructure.Posture.PostureSnapshotService(db, new SystemTenantContext(TenantA),
                new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
            .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftDefenderForOffice365, run.Id);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == detail.Summary.Id);

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        var spf = model.Controls.Single(c => c.Id == "AK-MDO-008");
        spf.RequiredCapabilities.Should().Contain("DefenderDnsRecords", "o código continua sendo a chave das limitações");
        spf.RequiredCapabilityLabels.Should().BeEquivalentTo(new[] { "Domínios aceitos", "Registros SPF e DMARC (consulta DNS)" });
        model.Controls.SelectMany(c => c.RequiredCapabilityLabels ?? Array.Empty<string>())
            .Should().NotContain(l => l.StartsWith("Defender", StringComparison.Ordinal), "nenhum rótulo cai no nome do código");
        spf.SeverityLabel.Should().Be("Alto");
    }

    // ======================================================================================================

    private static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, M365ServicesScenario scenario, bool withCertificate = true) =>
        ServiceFor(db, TenantA, scenario, withCertificate);

    internal static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId, M365ServicesScenario scenario, bool withCertificate = true)
    {
        var tenant = new SystemTenantContext(tenantId);
        var http = new System.Net.Http.HttpClient(scenario.Handler);
        var graph = new EntraGraphClient(http);
        var tokens = new MicrosoftAppTokenClient(http);
        var rest = new MicrosoftRestClient(http);
        var exoTokens = new M365ServicesScenario.StubExchangeTokens();
        var registry = new KnightCollectorRegistry(new IKnightCollector[]
        {
            new IntuneKnightCollector(graph),
            new SharePointKnightCollector(graph, tokens, rest),
            new FabricKnightCollector(tokens, rest),
            new DefenderForOffice365KnightCollector(exoTokens, scenario.ProtectionReader, scenario.Dns),
            new PurviewKnightCollector(exoTokens, scenario.ProtectionReader, tokens),
        });
        var config = new ServiceConfig(withCertificate);
        var store = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, store, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new KnightMulticloudReportTests.SemIa(), evidence, store, tenant);
    }

    private sealed class ServiceConfig : IKnightSourceConfigurationProvider
    {
        private readonly bool _certificate;
        public ServiceConfig(bool certificate) => _certificate = certificate;

        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(M365ServicesScenario.Configuration(source, _certificate));

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private void Dump(KnightAssessment run, string prefix)
    {
        foreach (var c in run.Capabilities) _output.WriteLine($"cap {c.Capability} {c.Outcome} {c.Detail}");
        foreach (var i in run.Indicators.Where(i => i.IndicatorId.StartsWith(prefix)).OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
            _output.WriteLine($"{i.IndicatorId} {i.Status} [{i.AffectedObjectCount}/{i.EvidenceObjectCount}] {i.NotEvaluatedReason ?? i.Evidence}");
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync(Guid tenantId)
    {
        await using (var db = NewContext(null))
        {
            if (await db.Tenants.AnyAsync(t => t.Id == tenantId)) return;
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Cliente Demo", Slug = $"t-{tenantId:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(tenantId);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
