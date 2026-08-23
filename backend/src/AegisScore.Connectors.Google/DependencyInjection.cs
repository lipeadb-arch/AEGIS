using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Google.Cloud;

namespace AegisScore.Connectors.Google;

public static class DependencyInjection
{
    /// <summary>
    /// Registra o stack do Google Workspace para o AEGIS KNIGHT: autenticador (biblioteca oficial), cliente HTTP
    /// tipado do Admin SDK/Reports (com resiliência padrão) e o coletor como <see cref="IKnightCollector"/> —
    /// o <c>KnightCollectorRegistry</c> o resolve por <see cref="AegisScore.Domain.KnightSourceType"/>.
    ///
    /// [AEGIS-MVP-MULTICLOUD-01] Registra também o stack do GOOGLE CLOUD (VM Manager / OS Config Vulnerability
    /// Reports): autenticador de service account SEM domain-wide delegation, cliente HTTP tipado da API OS Config
    /// (host oficial fixo, resiliência padrão) e o coletor como <see cref="IEvidenceConnector"/> — o
    /// <c>ConnectorRegistry</c> o resolve por provider+capability (Google/VulnerabilityScanner) no MESMO pipeline
    /// pull do Defender, sem reconciliador nem tabelas específicas do Google.
    /// </summary>
    public static IServiceCollection AddGoogleConnectors(this IServiceCollection services)
    {
        services.AddSingleton<IGoogleWorkspaceAuthenticator, GoogleWorkspaceAuthenticator>();
        services.AddHttpClient<IGoogleWorkspaceApiClient, GoogleWorkspaceApiClient>().AddStandardResilienceHandler();
        services.AddScoped<IKnightCollector, GoogleWorkspaceKnightCollector>();

        // Google Cloud VM Manager (Google/VulnerabilityScanner). SCOPED como o Defender: injeta um typed HttpClient
        // (transient, gerido pelo IHttpClientFactory) — um singleton o capturaria no root provider. A autenticação
        // (biblioteca oficial, sem HttpClient injetado) é singleton, como o autenticador do Workspace.
        services.AddSingleton<IGoogleCloudOsConfigAuthenticator, GoogleCloudOsConfigAuthenticator>();
        services.AddHttpClient<IGoogleCloudOsConfigApiClient, GoogleCloudOsConfigApiClient>().AddStandardResilienceHandler();
        services.AddScoped<IEvidenceConnector, GoogleCloudVulnerabilityConnector>();
        return services;
    }
}
