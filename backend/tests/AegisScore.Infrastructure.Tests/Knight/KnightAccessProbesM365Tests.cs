using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] "Testar conexão" do conector do AEGIS KNIGHT para as fontes novas, com os clientes HTTP
/// REAIS (Graph, token de aplicativo, REST) sobre respostas sintéticas: uma leitura real por fonte, o método de
/// autenticação dito na mensagem e a leitura NÃO TENTADA que não conta como falha.
/// </summary>
public sealed class KnightAccessProbesM365Tests
{
    private static ConnectorConfig Config() => new()
    {
        TenantId = Guid.Parse("ac0e5000-0000-4000-8000-000000000404"),
        Provider = ConnectorProvider.Microsoft,
        Capability = ConnectorCapability.IdentityPosture,
        EncryptedSettings = "cifrado-sintetico",
    };

    private static KnightIdentityPostureConnector ConnectorFor(M365ServicesScenario scenario, bool certificate)
    {
        var http = new HttpClient(scenario.Handler);
        return new KnightIdentityPostureConnector(
            new SettingsProtector(certificate),
            new EntraGraphClient(http),
            new KnightIdentityPostureConnectorTests.FakeTeamsTokens(),
            new KnightIdentityPostureConnectorTests.FakeTeamsReader(connected: true, readOk: true),
            new KnightIdentityPostureConnectorTests.FakeExchangeTokens(),
            new KnightIdentityPostureConnectorTests.FakeExchangeReader(connected: true, readOk: true),
            appTokens: new MicrosoftAppTokenClient(http),
            rest: new MicrosoftRestClient(http),
            protection: scenario.ProtectionReader);
    }

    [Fact]
    public async Task SoCertificado_TodasAsFontesVerificadas_ComAssercaoAssinada()
    {
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant);
        var health = await ConnectorFor(scenario, certificate: true).TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Healthy, health.Message);
        foreach (var fonte in new[] { "Microsoft Defender para Office 365", "Microsoft Purview", "SharePoint e OneDrive (Microsoft Graph)",
                     "SharePoint e OneDrive (API administrativa)", "Microsoft Intune", "Microsoft Fabric" })
            health.Message.Should().Contain(fonte + ": ", fonte);
        health.Message.Should().Contain("Autenticação usada: certificado da aplicação");
        scenario.TokenBodies.Should().OnlyContain(b => b.Contains("client_assertion=") && !b.Contains("client_secret="),
            "o conector guarda só o certificado: nenhum pedido pode levar segredo");
        scenario.ProtectionRequests.Select(r => r.Profile).Should().BeEquivalentTo(new[] { "defender", "purview" });
        scenario.ProtectionRequests.Single(r => r.Profile == "purview").ComplianceToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SemCertificado_ApiAdministrativaDoSharePointNaoTentada_ENaoRebaixaOEstado()
    {
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.Compliant);
        var health = await ConnectorFor(scenario, certificate: false).TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Healthy, "a leitura não tentada não é falha da credencial");
        health.Message.Should().Contain("SharePoint e OneDrive (API administrativa): não tentada — exige certificado");
        health.Message.Should().Contain("Autenticação usada: segredo de cliente.");
        scenario.Requests.Should().NotContain(r => r.RequestUri!.Host.EndsWith("-admin.sharepoint.com"));
    }

    [Fact]
    public async Task FabricRecusado_Degraded_ComORequisitoDoFabric()
    {
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.FabricForbidden);
        var health = await ConnectorFor(scenario, certificate: true).TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Microsoft Fabric: autorização recusada").And.Contain("APIs de administração somente leitura");
    }

    [Fact]
    public async Task SecurityComplianceRecusado_PurviewNomeiaAPermissaoDaquelaSessao()
    {
        var scenario = new M365ServicesScenario(M365ServicesScenario.Variant.ComplianceDenied);
        var health = await ConnectorFor(scenario, certificate: true).TestAsync(Config(), CancellationToken.None);

        health.Status.Should().Be(ConnectorStatus.Degraded);
        health.Message.Should().Contain("Microsoft Purview:").And.Contain("Microsoft Exchange Online Protection");
    }

    /// <summary>Settings sintéticos do conector: credencial comum e, quando pedido, SÓ o certificado (sem segredo).</summary>
    private sealed class SettingsProtector : IConnectorSecretProtector
    {
        private readonly bool _certificate;
        public SettingsProtector(bool certificate) => _certificate = certificate;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => _certificate
            ? JsonSerializer.Serialize(new
            {
                tenantId = "dir-demo-0001", clientId = "client-demo",
                certificatePfxBase64 = M365ServicesScenario.ClientCertificate.PfxBase64,
                certificatePassword = M365ServicesScenario.ClientCertificate.Password,
            })
            : """{"tenantId":"dir-demo-0001","clientId":"client-demo","clientSecret":"segredo-sintetico"}""";
    }
}
