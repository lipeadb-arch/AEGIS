using Microsoft.Extensions.DependencyInjection;
using Stars.Application.Abstractions;

namespace Stars.Connectors.Microsoft;

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
        return services;
    }
}
