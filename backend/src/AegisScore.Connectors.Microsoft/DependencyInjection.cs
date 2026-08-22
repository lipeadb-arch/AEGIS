using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Services;
using AegisScore.Application.Telemetry.Providers;
using AegisScore.Connectors.Microsoft.Knight;

namespace AegisScore.Connectors.Microsoft;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Microsoft stack adapters. Each is exposed as an <see cref="IEvidenceConnector"/>
    /// so the registry can resolve it by provider+capability. Add Defender/Purview/Azure adapters here.
    /// </summary>
    public static IServiceCollection AddMicrosoftConnectors(this IServiceCollection services)
    {
        // [AEGIS-MVP-POSTURE-02] Coletor REAL do Microsoft Secure Score (sinais + exposições de configuração).
        // Reusa o transporte VALIDADO do Graph (IEntraGraphClient) e o protetor de segredos existente. Singleton,
        // como os demais adaptadores do registry: o IEntraGraphClient injetado é um typed HttpClient — a captura
        // por um singleton é aceitável aqui (host oficial fixo, volume baixo de sync), sem segunda infra de OAuth.
        services.AddSingleton<IEvidenceConnector, MicrosoftSecureScoreConnector>();
        // services.AddSingleton<IEvidenceConnector, MicrosoftDefenderExposureConnector>();
        // services.AddSingleton<IEvidenceConnector, MicrosoftPurviewConnector>();
        // services.AddSingleton<IEvidenceConnector, AzureAdvisorConnector>();

        // Govern → Provider Pattern de ingestão de documentos: o SharePoint/M365 como fonte de políticas.
        // A DocumentIntegrationFactory resolve esta estratégia por ConnectorProvider.Microsoft.
        services.AddSingleton<IDocumentIntegrationProvider, SharePointProvider>();

        // Identify/Protect/Govern → telemetria de identidade do Entra ID (postura de IAM). STUB por ora
        // (dados de alto risco); troca-se por Microsoft Graph + OAuth client credentials mantendo a porta.
        services.AddSingleton<IEntraIdTelemetryProvider, EntraIdTelemetryProviderStub>();

        // AEGIS KNIGHT → coletor REAL do Microsoft Entra ID (somente leitura). HttpClient tipado com
        // resiliência padrão (retry/backoff, Retry-After no 429, circuit breaker) — reusa a fachada oficial,
        // sem infra própria. O coletor recebe a configuração DECIFRADA pelo contexto; não toca segredos.
        services.AddHttpClient<IEntraGraphClient, EntraGraphClient>().AddStandardResilienceHandler();
        services.AddScoped<IKnightCollector, EntraIdKnightCollector>();
        return services;
    }
}
