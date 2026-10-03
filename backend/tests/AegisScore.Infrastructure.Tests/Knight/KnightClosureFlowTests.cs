using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture.Export;
using AegisScore.Connectors.Microsoft.Knight.Azure;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-CLOSURE-01] Os desfechos que o fechamento precisa distinguir, pelo coletor REAL sobre respostas sintéticas:
///   • leitura em versão preview/beta fora do contrato documentado → limitação nomeada (versão e desvio), nunca aprovação;
///   • autorização recusada → permissão insuficiente com o requisito exato;
///   • população incompleta (conta não lida) → não avaliado, ou reprovado só pelo que foi lido — nunca aprovado;
///   • Databricks: habilitado lê o workspace, desligado não faz NENHUM pedido ao workspace, recusado fica não avaliado;
///   • nenhum segredo (configuração Spark, endereço, telefone, nome do segredo) chega ao ADM.
/// </summary>
public sealed class KnightClosureFlowTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-c106-c106-c106-0000000000a1");

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightClosureFlowTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static readonly KnightCapability[] AzurePreview =
        { KnightCapability.AzureActivityLogExport, KnightCapability.AzureResourceDiagnostics, KnightCapability.AzureSecurityContacts };

    private static IReadOnlyList<string> DependOn(KnightSourceType source, params KnightCapability[] caps) =>
        KnightCatalog.Indicators.Where(d => d.Sources.Contains(source)
                && KnightControlProfiles.RequiredCapabilitiesOf(d.Id).Any(caps.Contains))
            .Select(d => d.Id).ToList();

    // ---- Azure: versão preview ----------------------------------------------------------------------------

    [Fact]
    public async Task AzurePreviewForaDoContrato_ViraLimitacaoNomeada_EOsControlesFicamNaoAvaliados()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, new AzureScenario(AzureScenario.Variant.PreviewContractChanged))
            .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        Dump(run);

        foreach (var cap in AzurePreview)
        {
            var c = run.Capabilities.Single(x => x.Capability == cap);
            c.Outcome.Should().Be(KnightCapabilityOutcome.Error, cap.ToString());
            c.Detail.Should().Contain("-preview", "a limitação nomeia a versão preview lida").And.Contain("contrato");
        }

        var dependents = DependOn(KnightSourceType.MicrosoftAzure, AzurePreview);
        dependents.Should().HaveCountGreaterThanOrEqualTo(10);
        foreach (var id in dependents)
        {
            var i = run.Indicators.Single(x => x.IndicatorId == id);
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, $"{id}: resposta fora do contrato nunca aprova");
            KnightCollectorCapabilities.PreviewApisOf(id).Should().NotBeEmpty(id);
        }
        // A falha da leitura preview não contamina as famílias estáveis.
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-KV-005").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-STO-016").Status.Should().Be(KnightIndicatorStatus.Passed);
        run.SourceState.Should().Be(KnightSourceState.PartialCollection);
    }

    [Fact]
    public async Task AzurePreviewConforme_Aprova_EOControleIdentificaAVersaoPreview()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, new AzureScenario(AzureScenario.Variant.Compliant))
            .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        foreach (var id in DependOn(KnightSourceType.MicrosoftAzure, AzurePreview))
        {
            run.Indicators.Single(i => i.IndicatorId == id).Status.Should().BeOneOf(new[] { KnightIndicatorStatus.Passed, KnightIndicatorStatus.NotApplicable }, id);
            run.Indicators.Single(i => i.IndicatorId == id).Presentation!.PreviewApis.Should().Contain(p => p.Contains("-preview"), id);
        }
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-KV-005").Presentation!.PreviewApis.Should().BeEmpty("controle sobre versão estável não declara preview");
    }

    // ---- Azure: Databricks --------------------------------------------------------------------------------

    [Fact]
    public async Task Databricks_Habilitado_LeOWorkspace_SemGravarSegredo()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new AzureScenario(AzureScenario.Variant.Compliant);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, scenario, AzureScenario.Configuration(databricksWorkspaceApi: true))
            .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureDatabricksWorkspaceApi).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        scenario.TokenScopes.Should().Contain(AzureArmPlan.DatabricksScope, "o token do workspace é pedido para o recurso do Azure Databricks");
        scenario.Requests.Should().Contain(u => u.Contains(AzureScenario.DatabricksHost));
        foreach (var id in DependOn(KnightSourceType.MicrosoftAzure, KnightCapability.AzureDatabricksWorkspaceApi))
            run.Indicators.Single(i => i.IndicatorId == id).Status.Should().Be(KnightIndicatorStatus.Passed, id);

        var json = await AllObservedJson(db, run.Id);
        json.Should().NotContain(AzureScenario.Secret, "a configuração Spark pode carregar segredo: só o booleano calculado é gravado");
        json.Should().NotContain("spark_conf").And.NotContain("spark.authenticate");
        json.Should().NotContain("seguranca@clientedemo").And.NotContain("+55 11", "contatos de segurança: só a contagem de endereços");
        json.Should().NotContain("\"secretName\"").And.NotContain("SecretUri", "referência ao Key Vault: só cofre, estado e origem");
        json.Should().Contain("\"emails:count\":2");
    }

    [Fact]
    public async Task Databricks_Desligado_NenhumPedidoAoWorkspace_EControlesNaoAvaliadosComOMotivo()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var scenario = new AzureScenario(AzureScenario.Variant.Compliant);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, scenario, AzureScenario.Configuration(databricksWorkspaceApi: false))
            .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        scenario.Requests.Should().NotContain(u => u.Contains(".azuredatabricks.net"), "sem habilitação explícita, o workspace não é tocado");
        scenario.TokenScopes.Should().NotContain(AzureArmPlan.DatabricksScope);
        var cap = run.Capabilities.Single(c => c.Capability == KnightCapability.AzureDatabricksWorkspaceApi);
        cap.Outcome.Should().Be(KnightCapabilityOutcome.NotAttempted);
        cap.Detail.Should().Contain("Integrações");
        foreach (var id in DependOn(KnightSourceType.MicrosoftAzure, KnightCapability.AzureDatabricksWorkspaceApi))
        {
            var i = run.Indicators.Single(x => x.IndicatorId == id);
            i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
            i.NotEvaluatedReason.Should().Contain("Integrações", id);
        }
        // Os controles do Databricks pelo Resource Manager continuam avaliados.
        run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-AZ-DBR-", StringComparison.Ordinal)
                                  && !DependOn(KnightSourceType.MicrosoftAzure, KnightCapability.AzureDatabricksWorkspaceApi).Contains(i.IndicatorId))
            .Should().NotBeEmpty().And.OnlyContain(i => i.Status == KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task Databricks_Recusado_NaoAprova_EORequisitoEhNomeado()
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, new AzureScenario(AzureScenario.Variant.DatabricksDenied))
            .RunAssessmentAsync(KnightSourceType.MicrosoftAzure);
        Dump(run);

        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureDatabricksWorkspaceApi).Outcome
            .Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        foreach (var id in DependOn(KnightSourceType.MicrosoftAzure, KnightCapability.AzureDatabricksWorkspaceApi))
            run.Indicators.Single(i => i.IndicatorId == id).Status.Should().Be(KnightIndicatorStatus.NotEvaluated, id);
        KnightCapabilityLabels.RequiredPermission(KnightCapability.AzureDatabricksWorkspaceApi, KnightSourceType.MicrosoftAzure)
            .Should().Contain("workspace", "o acesso que falta é concedido dentro do Databricks");
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-KV-005").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    // ---- Entra: versão beta do Microsoft Graph -------------------------------------------------------------

    [Fact]
    public async Task EntraBetaRecusada_PermissaoInsuficiente_ComORequisitoExato()
    {
        var run = await EntraAsync(EntraConfigurationScenario.Variant.Compliant,
            url => url.Contains("/authentication/requirements") ? EntraConfigurationScenario.Forbidden() : null);

        run.Capabilities.Single(c => c.Capability == KnightCapability.PerUserMfaStates).Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        var i = run.Indicators.Single(x => x.IndicatorId == "AK-ENTRA-071");
        i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        i.NotEvaluatedReason.Should().Contain("Permissão insuficiente");
        KnightCapabilityLabels.RequiredPermission(KnightCapability.PerUserMfaStates, KnightSourceType.MicrosoftEntraId).Should().Contain("Policy.Read.All");
        run.Indicators.Single(x => x.IndicatorId == "AK-ENTRA-072").Status.Should().Be(KnightIndicatorStatus.Passed, "as outras leituras beta seguem valendo");
    }

    [Theory]
    [InlineData("/beta/policies/authenticationMethodsPolicy", """{"id":"authenticationMethodsPolicy"}""", KnightCapability.AuthenticationMethodsPolicyPreview, "AK-ENTRA-072")]
    [InlineData("/authentication/requirements", """{"id":"x","estado":"formato novo"}""", KnightCapability.PerUserMfaStates, "AK-ENTRA-071")]
    [InlineData("/beta/admin/forms", """{"value":{"id":"7ef97113"}}""", KnightCapability.M365FormsSettings, "AK-FORMS-001")]
    public async Task EntraBetaForaDoContrato_LimitacaoComAVersao_NuncaAprovacao(string route, string body, KnightCapability capability, string control)
    {
        var run = await EntraAsync(EntraConfigurationScenario.Variant.Compliant, url => url.Contains(route) ? (HttpStatusCode.OK, body) : null);

        var cap = run.Capabilities.Single(c => c.Capability == capability);
        cap.Outcome.Should().Be(KnightCapabilityOutcome.Error);
        cap.Detail.Should().Contain("fora do contrato documentado").And.Contain("versão beta");
        run.Indicators.Single(i => i.IndicatorId == control).Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
    }

    [Fact]
    public async Task MfaPorUsuario_ContaNaoLida_NaoAprova()
    {
        var run = await EntraAsync(EntraConfigurationScenario.Variant.Compliant,
            url => url.Contains("/beta/users/u3/") ? (HttpStatusCode.ServiceUnavailable, "{}") : null);

        run.Capabilities.Single(c => c.Capability == KnightCapability.PerUserMfaStates).Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        var i = run.Indicators.Single(x => x.IndicatorId == "AK-ENTRA-071");
        i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "cinco contas desabilitadas não aprovam uma população de seis");
        i.NotEvaluatedReason.Should().Contain("não lida");
    }

    [Fact]
    public async Task MfaPorUsuario_ContaNaoLida_ReprovaPeloQueFoiLido_ComALacunaDita()
    {
        var run = await EntraAsync(EntraConfigurationScenario.Variant.NonCompliant,
            url => url.Contains("/beta/users/u4/") ? (HttpStatusCode.TooManyRequests, "{}") : null);

        var i = run.Indicators.Single(x => x.IndicatorId == "AK-ENTRA-071");
        i.Status.Should().Be(KnightIndicatorStatus.Exposed);
        i.AffectedObjectCount.Should().Be(2, "u1 (imposta) e u2 (habilitada) foram lidas");
        i.Evidence.Should().Contain("5 de 6");
    }

    [Fact]
    public async Task MfaPorUsuario_ContaExcluidaDuranteALeitura_SaiDaPopulacao()
    {
        var run = await EntraAsync(EntraConfigurationScenario.Variant.Compliant,
            url => url.Contains("/beta/users/u6/") ? (HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"x"}}""") : null);

        var i = run.Indicators.Single(x => x.IndicatorId == "AK-ENTRA-071");
        i.Status.Should().Be(KnightIndicatorStatus.Passed);
        i.Evidence.Should().Contain("5 conta(s)");
    }

    // ---- Cobertura por composição ---------------------------------------------------------------------------

    [Fact]
    public void ReferenciaComposta_SoEhIntegral_ComTodosOsControlesDaComposicaoAtivos()
    {
        var controls = KnightReferenceCatalog.Coverage().Controls;
        foreach (var (key, ids) in KnightReferenceDispositions.AllComposites)
        {
            var s = controls.Single(c => c.Control.Key == key);
            s.Disposition.Should().Be(KnightReferenceDisposition.Implemented, key);
            s.IndicatorIds.Should().BeEquivalentTo(ids, key);
            s.Note.Should().Contain("composição");
            ids.Should().OnlyContain(id => KnightCatalog.Indicators.Any(d => d.Id == id && KnightCollectorCapabilities.IsActive(d)), key);
        }
    }

    // ======================================================================================================

    private async Task<KnightAssessment> EntraAsync(EntraConfigurationScenario.Variant variant, Func<string, (HttpStatusCode, string)?> failures)
    {
        await SeedAsync(TenantA);
        await using var db = NewContext(TenantA);
        var run = await KnightMulticloudReportTests.ServiceFor(db, TenantA, new EntraConfigurationScenario(variant, failures).Handler())
            .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        Dump(run);
        return run;
    }

    private static async Task<string> AllObservedJson(AegisScoreDbContext db, Guid runId)
    {
        var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        var docs = await db.IdentityConfigurationObservations.AsNoTracking().Where(o => o.AcquisitionId == run.IdentityAcquisitionId)
            .Select(o => o.ConfigurationJson).ToListAsync();
        return string.Join("\n", docs);
    }

    private void Dump(KnightAssessment run)
    {
        foreach (var c in run.Capabilities.Where(c => c.Outcome != KnightCapabilityOutcome.Collected)) _output.WriteLine($"cap {c.Capability} {c.Outcome} {c.Detail}");
        foreach (var i in run.Indicators.Where(i => i.Status != KnightIndicatorStatus.Passed).OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
            _output.WriteLine($"{i.IndicatorId} {i.Status} {i.NotEvaluatedReason ?? i.Evidence}");
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
