using Microsoft.Extensions.DependencyInjection;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;

namespace AegisScore.Connectors.Microsoft;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Microsoft stack adapters. Each is exposed as an <see cref="IEvidenceConnector"/>
    /// so the registry can resolve it by provider+capability. Add Defender/Purview/Azure adapters here.
    /// </summary>
    public static IServiceCollection AddMicrosoftConnectors(this IServiceCollection services)
    {
        services.AddSingleton<IEvidenceConnector, MicrosoftSecureScoreConnector>();
        // services.AddSingleton<IEvidenceConnector, MicrosoftDefenderExposureConnector>();
        // services.AddSingleton<IEvidenceConnector, MicrosoftPurviewConnector>();
        // services.AddSingleton<IEvidenceConnector, AzureAdvisorConnector>();

        // Govern → Provider Pattern de ingestão de documentos: o SharePoint/M365 como fonte de políticas.
        // A DocumentIntegrationFactory resolve esta estratégia por ConnectorProvider.Microsoft.
        services.AddSingleton<IDocumentIntegrationProvider, SharePointProvider>();
        return services;
    }
}
