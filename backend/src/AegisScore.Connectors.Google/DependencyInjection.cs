using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Google;

public static class DependencyInjection
{
    /// <summary>
    /// Registra o stack do Google Workspace para o AEGIS KNIGHT: autenticador (biblioteca oficial), cliente HTTP
    /// tipado do Admin SDK/Reports (com resiliência padrão) e o coletor como <see cref="IKnightCollector"/> —
    /// o <c>KnightCollectorRegistry</c> o resolve por <see cref="AegisScore.Domain.KnightSourceType"/>.
    /// </summary>
    public static IServiceCollection AddGoogleConnectors(this IServiceCollection services)
    {
        services.AddSingleton<IGoogleWorkspaceAuthenticator, GoogleWorkspaceAuthenticator>();
        services.AddHttpClient<IGoogleWorkspaceApiClient, GoogleWorkspaceApiClient>().AddStandardResilienceHandler();
        services.AddScoped<IKnightCollector, GoogleWorkspaceKnightCollector>();
        return services;
    }
}
