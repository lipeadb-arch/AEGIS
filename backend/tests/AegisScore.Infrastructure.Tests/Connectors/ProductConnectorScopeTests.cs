using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Services;
using AegisScore.Connectors.Google;
using AegisScore.Connectors.Microsoft;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

public sealed class ProductConnectorScopeTests
{
    [Theory]
    [InlineData(ConnectorProvider.Microsoft, ConnectorCapability.IdentityPosture, true)]
    [InlineData(ConnectorProvider.Google, ConnectorCapability.IdentityPosture, true)]
    [InlineData(ConnectorProvider.Microsoft, ConnectorCapability.PolicyDocuments, true)]
    [InlineData(ConnectorProvider.Microsoft, ConnectorCapability.VulnerabilityScanner, false)]
    [InlineData(ConnectorProvider.Google, ConnectorCapability.VulnerabilityScanner, false)]
    [InlineData(ConnectorProvider.Microsoft, ConnectorCapability.SecureScore, false)]
    [InlineData(ConnectorProvider.MicrosoftSentinel, ConnectorCapability.Siem, false)]
    [InlineData(ConnectorProvider.Generic, ConnectorCapability.Edr, false)]
    public void OnlyProductSourcesAreSupported(ConnectorProvider provider, ConnectorCapability capability, bool expected)
        => Assert.Equal(expected, ProductConnectorScope.Supports(provider, capability));

    [Fact]
    public void RegisteredCollectorsKeepKnightAndNoVulnerabilityScanner()
    {
        var services = new ServiceCollection().AddMicrosoftConnectors().AddGoogleConnectors();
        var evidence = services.Where(s => s.ServiceType == typeof(IEvidenceConnector)).ToList();
        Assert.Single(evidence);
        Assert.Equal("KnightIdentityPostureConnector", evidence[0].ImplementationType!.Name);
        var names = services.Where(s => s.ServiceType == typeof(IKnightCollector)).Select(s => s.ImplementationType!.Name).ToList();
        Assert.Contains("EntraIdKnightCollector", names);
        Assert.Contains("GoogleWorkspaceKnightCollector", names);
        Assert.Contains("DefenderForOffice365KnightCollector", names);
        Assert.Contains("IntuneKnightCollector", names);
        Assert.DoesNotContain(services, s => s.ImplementationType?.Name.Contains("Vulnerability") == true);
    }

    /// <summary>
    /// Composição real (infraestrutura + pacotes Microsoft e Google), sem rede nem banco: todo serviço registrado precisa ser
    /// construível. Pega dependência removida junto da abordagem antiga que ainda é exigida por um serviço preservado.
    /// </summary>
    [Fact]
    public void ProductCompositionResolvesEveryRegisteredService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Connection string dummy: AddDbContext apenas registra (não abre conexão).
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=aegis_test;Username=test;Password=test",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddDataProtection();
        services.AddAegisScoreInfrastructure(config);
        services.AddMicrosoftConnectors();
        services.AddGoogleConnectors();
        // O host registra o ITenantContext (Program.cs); o container do teste o provê.
        services.AddScoped<ITenantContext>(_ => new SystemTenantContext(null));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPostureSnapshotService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPostureSnapshotExporter>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IConnectorRegistry>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAuditorContextBuilder>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAegisKnightAssessmentService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AegisScore.Application.Nist.INistPublicationService>());
    }
}
