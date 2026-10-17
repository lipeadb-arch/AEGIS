using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-READONLY-01] O KNIGHT opera só com autorização de leitura. O que estes testes travam:
///   • o transporte REST recusa os destinos retirados (administração do SharePoint e workspaces do Databricks) antes de
///     enviar qualquer pedido — nenhum coletor ou teste de conexão consegue voltar a chamá-los;
///   • as capacidades não solicitadas e os controles que dependem delas estão declarados, e cada controle consome só as
///     leituras de que de fato depende;
///   • os scripts PowerShell embutidos só executam verbos de LEITURA (lista permitida, não lista proibida);
///   • uma configuração antiga com a habilitação da API do Databricks continua legível e não reativa caminho nenhum.
/// Testes simulados: não comprovam a autorização real no provedor.
/// </summary>
public sealed class KnightReadOnlyAccessTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0e01-0e01-0e01-0000000000a1");
    private readonly SqliteConnection _connection;

    public KnightReadOnlyAccessTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Transporte ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://clientedemo-admin.sharepoint.com/_api/SPO.Tenant")]
    [InlineData("https://adb-0000000000000001.1.azuredatabricks.net/api/2.0/global-init-scripts")]
    [InlineData("https://adb-0000000000000001.1.azuredatabricks.net/api/2.0/permissions/authorization/tokens")]
    public async Task Transporte_RecusaOsDestinosRetirados_SemEnviarPedido(string url)
    {
        var handler = new CountingHandler();
        var rest = new MicrosoftRestClient(new HttpClient(handler));

        MicrosoftRestClient.IsAllowed(new Uri(url)).Should().BeFalse();
        await FluentActions.Awaiting(() => rest.GetAsync("token-sintetico", url, CancellationToken.None))
            .Should().ThrowAsync<EntraGraphException>();
        handler.Calls.Should().Be(0, "o pedido é recusado antes de sair — o token não segue para o destino");
    }

    [Theory]
    [InlineData("https://management.azure.com/subscriptions?api-version=2022-12-01")]
    [InlineData("https://api.fabric.microsoft.com/v1/admin/tenantsettings")]
    [InlineData("https://kv-demo.vault.azure.net/certificates?api-version=7.4")]
    [InlineData("https://graph.microsoft.com/v1.0/admin/sharepoint/settings")]
    public void Transporte_MantemOsDestinosDeLeitura(string url) =>
        MicrosoftRestClient.IsAllowed(new Uri(url)).Should().BeTrue();

    // ---- Política e dependências -----------------------------------------------------------------------------

    [Fact]
    public void Politica_CapacidadesNaoSolicitadas_ESeusControlesDeclarados()
    {
        KnightCollectorCapabilities.NotRequested.Should().BeEquivalentTo(new[]
            { KnightCapability.SharePointAdminTenant, KnightCapability.AzureDatabricksWorkspaceApi });

        // O coletor continua registrando-as (não tentadas, com o motivo): os controles ficam não avaliados, não somem.
        KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftSharePoint).Should().Contain(KnightCapability.SharePointAdminTenant);
        KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftAzure).Should().Contain(KnightCapability.AzureDatabricksWorkspaceApi);

        var dependents = KnightCatalog.Indicators
            .Where(d => KnightControlProfiles.RequiredCapabilitiesOf(d.Id).Any(KnightCollectorCapabilities.NotRequested.Contains))
            .Select(d => d.Id).ToList();
        dependents.Should().BeEquivalentTo(new[]
        {
            "AK-SPO-002", "AK-SPO-004", "AK-SPO-007", "AK-SPO-008", "AK-SPO-009", "AK-SPO-010", "AK-SPO-011", "AK-SPO-012",
            "AK-AZ-DBR-009", "AK-AZ-DBR-010", "AK-AZ-DBR-011",
        });

        // Sem caminho de leitura: nenhum veredito possível com o que o AEGIS lê.
        KnightCollectorCapabilities.ConclusiveOnlyWithNotRequested.Should().BeEquivalentTo(new[]
            { "AK-SPO-007", "AK-SPO-011", "AK-SPO-012", "AK-AZ-DBR-009", "AK-AZ-DBR-010", "AK-AZ-DBR-011" });
        KnightCollectorCapabilities.ConclusiveOnlyWithNotRequested.Should().BeSubsetOf(dependents);

        // Os demais dependentes concluem em parte pelo Microsoft Graph — e por isso o consomem.
        foreach (var id in dependents.Except(KnightCollectorCapabilities.ConclusiveOnlyWithNotRequested))
            KnightControlProfiles.RequiredCapabilitiesOf(id).Should().Contain(KnightCapability.SharePointTenantSettings, id);
    }

    public static IEnumerable<object[]> SharePointDependencies() => new[]
    {
        new object[] { "AK-SPO-001", new[] { KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-003", new[] { KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-005", new[] { KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-006", new[] { KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-013", new[] { KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-002", new[] { KnightCapability.SharePointAdminTenant, KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-004", new[] { KnightCapability.SharePointAdminTenant, KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-008", new[] { KnightCapability.SharePointAdminTenant, KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-009", new[] { KnightCapability.SharePointAdminTenant, KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-010", new[] { KnightCapability.SharePointAdminTenant, KnightCapability.SharePointTenantSettings } },
        new object[] { "AK-SPO-007", new[] { KnightCapability.SharePointAdminTenant } },
        new object[] { "AK-SPO-011", new[] { KnightCapability.SharePointAdminTenant } },
        new object[] { "AK-SPO-012", new[] { KnightCapability.SharePointAdminTenant } },
    };

    /// <summary>Cada controle do SharePoint consome exatamente as leituras das quais depende.</summary>
    [Theory]
    [MemberData(nameof(SharePointDependencies))]
    public void SharePoint_CadaControleConsomeSoOQueUsa(string id, KnightCapability[] expected) =>
        KnightControlProfiles.RequiredCapabilitiesOf(id).Should().BeEquivalentTo(expected, id);

    [Fact]
    public void Databricks_UnityCatalogPeloResourceManager_DemaisPelaApiDoWorkspace()
    {
        KnightControlProfiles.RequiredCapabilitiesOf("AK-AZ-DBR-008").Should().Contain(KnightCapability.AzureDatabricks)
            .And.NotContain(KnightCapability.AzureDatabricksWorkspaceApi);
        foreach (var id in new[] { "AK-AZ-DBR-009", "AK-AZ-DBR-010", "AK-AZ-DBR-011" })
            KnightControlProfiles.RequiredCapabilitiesOf(id).Should()
                .Contain(KnightCapability.AzureDatabricks).And.Contain(KnightCapability.AzureDatabricksWorkspaceApi, id);
    }

    // ---- Scripts PowerShell: lista PERMITIDA de verbos ---------------------------------------------------------

    /// <summary>Utilitários da própria linguagem usados pelos scripts (nenhum fala com o provedor).</summary>
    private static readonly HashSet<string> LanguageUtilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForEach-Object", "Where-Object", "Select-Object", "ConvertTo-Json", "ConvertFrom-Json", "Out-Null", "Set-StrictMode",
        "New-Object", "Import-Module",
    };

    /// <summary>Abertura e encerramento das sessões de aplicativo — sem eles não há leitura.</summary>
    private static readonly HashSet<string> SessionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connect-ExchangeOnline", "Connect-IPPSSession", "Connect-MicrosoftTeams", "Disconnect-ExchangeOnline", "Disconnect-MicrosoftTeams",
    };

    [Theory]
    [InlineData("AegisScore.Knight.Exchange.Collect.ps1")]
    [InlineData("AegisScore.Knight.Teams.Collect.ps1")]
    [InlineData("AegisScore.Knight.Protection.Collect.ps1")]
    public void ScriptEmbutido_SoExecutaVerbosDeLeitura(string resource)
    {
        using var stream = typeof(PowerShellExchangeAdminReader).Assembly.GetManifestResourceStream(resource);
        stream.Should().NotBeNull(resource);
        using var reader = new StreamReader(stream!, Encoding.UTF8);
        // Só as linhas EXECUTADAS: comentários explicam decisões e não contam.
        var executed = string.Join("\n", reader.ReadToEnd().Split('\n').Where(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)));

        var local = Regex.Matches(executed, @"(?im)^\s*function\s+([A-Za-z]+-[A-Za-z0-9]+)")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commands = Regex.Matches(executed, @"\b[A-Z][a-zA-Z]+-[A-Z][A-Za-z0-9]+\b").Select(m => m.Value)
            .Where(t => t != "AEGIS-KNIGHT").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        commands.Should().Contain(c => c.StartsWith("Get-", StringComparison.Ordinal), resource);

        // Fora das funções do próprio script, dos utilitários da linguagem e da sessão, só Get-*.
        commands.Where(c => !c.StartsWith("Get-", StringComparison.Ordinal) && !local.Contains(c)
                            && !LanguageUtilities.Contains(c) && !SessionCommands.Contains(c))
            .Should().BeEmpty($"{resource} só pode executar comandos de leitura");
        // As funções locais também não podem ter verbo de escrita no nome.
        local.Should().OnlyContain(f => !Regex.IsMatch(f, @"^(Set|New|Remove|Add-Role|Enable|Disable|Update|Grant|Revoke|Start|Stop)-",
            RegexOptions.IgnoreCase), resource);
    }

    // ---- Configuração antiga ---------------------------------------------------------------------------------

    [Fact]
    public async Task ConfiguracaoAntigaComDatabricks_Legivel_SemReativarAApiDoWorkspace()
    {
        await using (var seed = NewContext(null))
        {
            seed.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Demo", Slug = "t-readonly", Status = TenantStatus.Active });
            await seed.SaveChangesAsync();
        }
        await using (var seed = NewContext(TenantA))
        {
            seed.Connectors.Add(new ConnectorConfig
            {
                TenantId = TenantA, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Microsoft 365", Enabled = true,
                EncryptedSettings = $$"""
                    {"tenantId":"dir-demo-0001","clientId":"client-demo","clientSecret":"segredo-sintetico",
                     "azureSubscriptionIds":["{{AzureScenario.SubA}}","{{AzureScenario.SubB}}"],"databricksWorkspaceApi":true}
                    """,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(TenantA);
        var resolved = await new KnightSourceConfigurationProvider(db, new IdentityProtector()).ResolveAsync(TenantA, KnightSourceType.MicrosoftAzure);
        var azure = resolved.Should().BeOfType<KnightMicrosoftServiceConfiguration>().Subject;
        azure.SubscriptionScope.Should().Equal(AzureScenario.SubA, AzureScenario.SubB);

        var scenario = new AzureScenario(AzureScenario.Variant.Compliant);
        var run = await KnightAzureFlowTests.ServiceFor(db, TenantA, scenario, azure).RunAssessmentAsync(KnightSourceType.MicrosoftAzure);

        scenario.Requests.Should().NotContain(u => u.Contains(".azuredatabricks.net"));
        scenario.TokenScopes.Should().NotContain(AzureScenario.DatabricksTokenScope);
        run.Capabilities.Single(c => c.Capability == KnightCapability.AzureDatabricksWorkspaceApi).Outcome
            .Should().Be(KnightCapabilityOutcome.NotAttempted);
        run.Indicators.Single(i => i.IndicatorId == "AK-AZ-DBR-008").Status.Should().Be(KnightIndicatorStatus.Passed,
            "o Unity Catalog é avaliado pelo Resource Manager");
    }

    // ---- Infra ------------------------------------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class IdentityProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
