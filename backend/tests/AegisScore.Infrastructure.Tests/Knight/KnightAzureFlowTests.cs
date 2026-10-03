using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Azure;
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
/// [AEGIS-KNIGHT-COVERAGE-04] O percurso INTEIRO do Azure: respostas sintéticas do Resource Manager, do plano de dados
/// do Key Vault e do Graph → coletor REAL (paginação, filhas, listagem genérica, leitura por id, escopo do locatário) →
/// documentos no ADM → persistência → RELEITURA → avaliação. A regra só vê o que passou pelo ADM.
/// </summary>
public sealed class KnightAzureFlowTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0404-0404-0404-0000000000a2");

    /// <summary>Controles que, no cenário conforme, não têm população (o cenário não pode tê-la sem reprovar outro controle).</summary>
    private static readonly string[] NotApplicableWhenCompliant =
    {
        "AK-AZ-KV-002", "AK-AZ-KV-004", // cofre sem RBAC reprovaria AK-AZ-KV-006
        "AK-AZ-CMP-014",                // VHD não gerenciado reprovaria AK-AZ-CMP-008
    };

    /// <summary>Controles que, por construção, nunca reprovam: o dado que reprovaria não é visível pelo Resource Manager.</summary>
    private static readonly string[] NeverExposed = { "AK-AZ-CMP-013" };

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightAzureFlowTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static IReadOnlyList<string> AzureIds =>
        KnightCatalog.Indicators.Where(d => d.Sources.Contains(KnightSourceType.MicrosoftAzure)).Select(d => d.Id).ToList();

    [Fact]
    public async Task Conforme_CadaControleAzureAprova_PelaColetaRelidaDoAdm()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new AzureScenario(AzureScenario.Variant.Compliant);
        var run = await ServiceFor(db, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        Dump(run);

        run.SourceState.Should().Be(KnightSourceState.Completed);
        run.CatalogVersion.Should().Be(KnightCatalog.Version);
        run.Capabilities.Select(c => c.Capability).Should().BeEquivalentTo(KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftAzure),
            "o coletor real registra o desfecho de cada capacidade que o mapa declara");
        run.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected);

        var entity = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        var acquisition = await db.IdentityAcquisitions.AsNoTracking().SingleAsync(a => a.Id == entity.IdentityAcquisitionId);
        acquisition.Provider.Should().Be(KnightSourceType.MicrosoftAzure);
        (await db.IdentityConfigurationObservations.AsNoTracking().CountAsync(o => o.AcquisitionId == acquisition.Id)).Should().BeGreaterThan(50);
        (await db.IdentityEntities.CountAsync()).Should().Be(0, "recurso de nuvem não é identidade: nada entra no inventário de identidades");

        var own = run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-", StringComparison.Ordinal)).ToList();
        own.Select(i => i.IndicatorId).Should().BeEquivalentTo(AzureIds);
        own.Where(i => i.Status != KnightIndicatorStatus.Passed && !NotApplicableWhenCompliant.Contains(i.IndicatorId))
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.NotEvaluatedReason ?? i.Evidence}")
            .Should().BeEmpty("no cenário conforme cada critério tem evidência suficiente e aprova");
        own.Where(i => NotApplicableWhenCompliant.Contains(i.IndicatorId)).Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotApplicable);
        run.Indicators.Should().OnlyContain(i => i.IndicatorId.StartsWith("AK-AZ-", StringComparison.Ordinal));
        run.Score.Should().Be(100);
    }

    [Fact]
    public async Task Inadequado_CadaControleAzureReprova_ComOQueFoiEncontradoEOEsperado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, new AzureScenario(AzureScenario.Variant.NonCompliant));
        var run = await svc.RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        Dump(run);

        var own = run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-", StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(AzureIds.Count);
        own.Where(i => i.Status != KnightIndicatorStatus.Exposed && !NeverExposed.Contains(i.IndicatorId))
            .Select(i => $"{i.IndicatorId}={i.Status}: {i.NotEvaluatedReason ?? i.Evidence}")
            .Should().BeEmpty("no cenário inadequado cada critério tem violação demonstrável");
        own.Single(i => i.IndicatorId == "AK-AZ-CMP-013").Status.Should().Be(KnightIndicatorStatus.NotEvaluated,
            "sem extensão reconhecida a proteção de endpoint é desconhecida — nunca reprovada");
        own.Where(i => i.Status == KnightIndicatorStatus.Exposed)
            .Should().OnlyContain(i => i.AffectedObjectCount + i.EvidenceObjectCount > 0);
        own.Should().OnlyContain(i => i.Presentation != null && !string.IsNullOrWhiteSpace(i.Presentation.Impact));
        run.Score.Should().Be(0);

        // O afetado é o RECURSO, com o encontrado e o esperado — não uma contagem solta.
        var tls = (await svc.GetAffectedObjectsAsync(run.Id, "AK-AZ-STO-016", 1, 50, null))!.Items;
        tls.Should().ContainSingle();
        tls[0].Kind.Should().Be(KnightAffectedObjectKind.CloudResource);
        tls[0].DisplayName.Should().Contain("stclientedemo").And.Contain("conta de armazenamento");
        tls[0].Detail.Should().Contain("Encontrado: versão mínima: TLS1_0").And.Contain("Esperado: TLS1_2 ou superior");

        // NSG aberto: as nove portas reprovam pela mesma regra, nomeada.
        foreach (var id in Enumerable.Range(1, 9).Select(n => $"AK-AZ-NET-{n:000}"))
            (await svc.GetAffectedObjectsAsync(run.Id, id, 1, 10, null))!.Items.Should().ContainSingle(o => o.Detail!.Contains("permitir-tudo"), id);

        // Atribuição herdada da raiz aparece nas duas assinaturas e é gravada UMA vez; o afetado é o principal.
        var uaa = (await svc.GetAffectedObjectsAsync(run.Id, "AK-AZ-IAM-001", 1, 10, null))!.Items;
        uaa.Should().ContainSingle(o => o.ExternalId == "u-elevated" && o.UserPrincipalName == "admin.global@clientedemo.example.com");

        // A conta desabilitada é identificada pelo Graph, com o escopo da atribuição.
        var disabled = (await svc.GetAffectedObjectsAsync(run.Id, "AK-AZ-IAM-002", 1, 10, null))!.Items;
        disabled.Should().ContainSingle(o => o.ExternalId == "u-reader-1" && o.Kind == KnightAffectedObjectKind.User)
            .Which.Detail.Should().Contain("DESABILITADA");

        // Versão de runtime obsoleta: comparada com a pilha publicada, não com uma lista fixa.
        var py = (await svc.GetAffectedObjectsAsync(run.Id, "AK-AZ-APP-002", 1, 10, null))!.Items;
        py.Select(o => o.DisplayName).Should().Contain(n => n!.StartsWith("app-demo")).And.Contain(n => n!.StartsWith("staging"))
            .And.Contain(n => n!.StartsWith("func-demo"), "aplicativo, slot e função entram na mesma população");
    }

    [Fact]
    public async Task EscopoExplicito_AssinaturaForaDoEscopo_EhVistaMasNaoLida()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new AzureScenario(AzureScenario.Variant.Compliant);
        var run = await ServiceFor(db, scenario).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        scenario.Requests.Where(u => u.Contains(AzureScenario.SubC, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(
            "uma assinatura fora do escopo pedido não tem nenhum recurso lido");
        var docs = await Observations(db, run.Id);
        var inventories = docs.Where(d => d.Kind == ConfigurationObjectKind.AzureSubscription).ToList();
        inventories.Should().HaveCount(3);
        inventories.Single(d => d.ExternalId == AzureScenario.SubC).ConfigurationJson.Should().Contain("\"inScope\":false");
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions).Detail.Should().Contain("2 assinatura(s) no escopo de 3");

        // Tokens: Resource Manager, plano de dados do cofre e Graph — cada um para o próprio recurso.
        scenario.TokenScopes.Should().Contain("https://management.azure.com/.default").And.Contain("https://vault.azure.net/.default");
    }

    [Fact]
    public async Task NadaAlemDaListaFechada_ChegaAoAdm_NemSegredoNemVariavelDeAmbiente()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new AzureScenario(AzureScenario.Variant.NonCompliant)).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        var json = string.Join("\n", (await Observations(db, run.Id)).Select(d => d.ConfigurationJson));

        json.Should().NotContain(AzureScenario.Secret);
        json.Should().NotContain("environmentVariables").And.NotContain("connectionStrings").And.NotContain("primaryKey");
        // [AEGIS-KNIGHT-COVERAGE-04] Serviços vinculados do Data Factory: só o resumo da origem das credenciais chega ao ADM.
        json.Should().Contain("\"factorySecrets\":1").And.Contain("\"plainCredentials\":1");
        json.Should().NotContain("Server=tcp:sql-demo").And.NotContain("User ID=app").And.NotContain("typeProperties");
        (await Observations(db, run.Id)).Count(d => d.ExternalId == "/providers/Microsoft.Authorization/roleAssignments/root-uaa").Should().Be(1);
    }

    [Fact]
    public async Task LeituraRecusadaNumaAssinatura_NaoAprova_ComAAssinaturaNomeada()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new AzureScenario(AzureScenario.Variant.StorageDeniedInB)).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        Dump(run);

        var cap = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureStorage);
        cap.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        cap.Detail.Should().Contain("1 de 2");
        foreach (var i in run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-STO-", StringComparison.Ordinal)))
        {
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, $"{i.IndicatorId}: a conta conforme de A não aprova o escopo que inclui B, não lida");
            i.NotEvaluatedReason.Should().Contain("Assinatura Demo Desenvolvimento");
        }
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-KV-005").Status.Should().Be(KnightIndicatorStatus.Passed,
            "a falha de uma família não contamina as outras");
        run.SourceState.Should().Be(KnightSourceState.PartialCollection);
    }

    [Fact]
    public async Task ViolacaoLida_ContinuaViolacao_QuandoOutraAssinaturaFalha()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new AzureScenario(AzureScenario.Variant.NonCompliantStorageDeniedInB)).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        var anon = run.Indicators.Single(i => i.IndicatorId == "AK-AZ-STO-018");
        anon.Status.Should().Be(KnightIndicatorStatus.Exposed);
        anon.AffectedObjectCount.Should().Be(1);
    }

    [Fact]
    public async Task PlanoDeDadosDoCofreRecusado_SoOsCertificadosFicamNaoAvaliados()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new AzureScenario(AzureScenario.Variant.VaultDataDenied)).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureKeyVaultCertificates).Outcome
            .Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-KV-010").Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-KV-", StringComparison.Ordinal) && i.IndicatorId != "AK-AZ-KV-010")
            .Should().OnlyContain(i => i.Status == KnightIndicatorStatus.Passed || i.Status == KnightIndicatorStatus.NotApplicable);
    }

    [Theory]
    [InlineData(AzureScenario.Variant.NoSubscriptions, "nenhuma assinatura no escopo")]
    [InlineData(AzureScenario.Variant.DiscoveryDenied, "")]
    public async Task SemAssinaturaLida_TodoControleFicaNaoAvaliado_NuncaAprovado(AzureScenario.Variant variant, string reason)
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new AzureScenario(variant)).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        Dump(run);

        var own = run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-", StringComparison.Ordinal)).ToList();
        own.Should().HaveCount(AzureIds.Count);
        own.Should().OnlyContain(i => i.Status == KnightIndicatorStatus.NotEvaluated);
        if (reason.Length > 0) own.Should().OnlyContain(i => i.NotEvaluatedReason!.Contains(reason));
        run.Score.Should().BeNull();
        if (variant == AzureScenario.Variant.DiscoveryDenied)
            run.Capabilities.Single(c => c.Capability == KnightCapability.AzureSubscriptions).Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
    }

    /// <summary>
    /// Cada referência do Azure no catálogo de 457 tem destino: avaliada por controle ATIVO, limitação de API
    /// fundamentada, verificação manual, outro acesso — ou pendente COM a pesquisa declarada. Nenhuma fica pendente sem
    /// dizer o que falta.
    /// </summary>
    [Fact]
    public void ReferenciasDoAzure_TodasComDestino_EPendentesSoComPesquisaDeclarada()
    {
        var azure = KnightReferenceCatalog.Coverage().Controls
            .Where(c => KnightServices.Describe(c.Control.Service)?.Platform == KnightPlatform.Azure).ToList();
        azure.Should().HaveCount(285);
        foreach (var g in azure.GroupBy(c => c.Disposition).OrderBy(g => g.Key))
            _output.WriteLine($"{g.Key}: {g.Count()}");

        azure.Where(c => c.Disposition == KnightReferenceDisposition.Pending).Should().BeEmpty(
            "as sete pesquisas do Azure foram fechadas: controle, avaliação parcial ou verificação manual declarada");
        // [AEGIS-KNIGHT-CLOSURE-01] As 12 "só em preview" (diagnóstico e contatos de segurança) passaram a ser avaliadas pela versão
        // preview, identificada nas informações técnicas; os 4 do Databricks e o 2.5 do App Service ganharam método.
        azure.Count(c => c.Disposition == KnightReferenceDisposition.PreviewOnly).Should().Be(0);
        azure.Count(c => c.Disposition == KnightReferenceDisposition.RequiresAccess).Should().Be(0);
        azure.Count(c => c.PreviewApis is { Count: > 0 }).Should().Be(12, "diagnóstico e contatos de segurança dependem de versão preview");
        azure.Count(c => c.Disposition == KnightReferenceDisposition.ApiLimitation).Should().Be(1, "diagnóstico do Intune: nem a preview tem a operação");
        azure.Where(c => c.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial)
            .Should().OnlyContain(c => c.IndicatorIds.All(id => id.StartsWith("AK-AZ-", StringComparison.Ordinal)));
        azure.Count(c => c.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial)
            .Should().BeGreaterThanOrEqualTo(230);
    }

    [Fact]
    public async Task RelatorioPublicado_CapacidadesDoAzureEmPortugues_ComOAcessoQueFalta()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await ServiceFor(db, new AzureScenario(AzureScenario.Variant.VaultDataDenied)).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        var detail = await new AegisScore.Infrastructure.Posture.PostureSnapshotService(db, new SystemTenantContext(TenantA),
                new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
            .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftAzure, run.Id);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == detail.Summary.Id);

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.Controls.SelectMany(c => c.RequiredCapabilityLabels ?? Array.Empty<string>())
            .Should().NotContain(l => l.StartsWith("Azure", StringComparison.Ordinal), "nenhum rótulo cai no código da capacidade");
        model.Controls.Single(c => c.Id == "AK-AZ-KV-010").RequiredCapabilityLabels
            .Should().Contain("Políticas de certificado dos cofres (plano de dados)");
        model.Limitations.Should().Contain(l => l.RequiredPermission != null && l.RequiredPermission.Contains("Leitor do Key Vault"));
        model.Limitations.Where(l => l.Capability.StartsWith("Azure", StringComparison.Ordinal))
            .Should().OnlyContain(l => l.Guidance.Contains("Azure RBAC") && !l.Guidance.Contains("consentimento"),
                "no Azure a autorização é atribuição de papel no escopo recusado");
    }

    // ======================================================================================================

    internal static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId, AzureScenario scenario,
        KnightMicrosoftServiceConfiguration? configuration = null)
    {
        var tenant = new SystemTenantContext(tenantId);
        var http = new System.Net.Http.HttpClient(scenario.Handler);
        var registry = new KnightCollectorRegistry(new IKnightCollector[]
        {
            new AzureKnightCollector(new MicrosoftAppTokenClient(http), new MicrosoftRestClient(http), new EntraGraphClient(http)),
        });
        var config = new Config(configuration ?? AzureScenario.Configuration());
        var store = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, store, tenant);
        return new AegisKnightAssessmentService(db, registry, config, new KnightMulticloudReportTests.SemIa(), evidence, store, tenant);
    }

    private IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, AzureScenario scenario) => ServiceFor(db, TenantA, scenario);

    private sealed class Config : IKnightSourceConfigurationProvider
    {
        private readonly KnightMicrosoftServiceConfiguration _configuration;
        public Config(KnightMicrosoftServiceConfiguration configuration) => _configuration = configuration;

        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(_configuration);

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, System.Threading.CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private static async Task<List<IdentityConfigurationObservation>> Observations(AegisScoreDbContext db, Guid runId)
    {
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        return await db.IdentityConfigurationObservations.AsNoTracking().Where(o => o.AcquisitionId == run.IdentityAcquisitionId).ToListAsync();
    }

    private void Dump(KnightAssessment run)
    {
        foreach (var c in run.Capabilities) _output.WriteLine($"cap {c.Capability} {c.Outcome} {c.Detail}");
        foreach (var i in run.Indicators.OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
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
