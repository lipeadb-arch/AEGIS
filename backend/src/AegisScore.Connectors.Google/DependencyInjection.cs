using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Google.Cloud;
using AegisScore.Connectors.Google.SecOps;

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

        // [AEGIS-MVP-GOOGLE-SECOPS-01] Google SecOps / Chronicle (Google/Siem): coletor REAL, somente leitura, da
        // postura operacional (casos + alertas) via Chronicle API unificada. Transporte PRÓPRIO (IChronicleApiClient) —
        // hosts regionais oficiais *-chronicle.googleapis.com derivados de uma allowlist por localidade, sem baseUrl do
        // tenant. Auto-redirect DESABILITADO no handler primário (o bearer nunca segue um Location para outro host);
        // resiliência padrão (retry/backoff, Retry-After no 429, circuit breaker). A autenticação (biblioteca oficial,
        // sem HttpClient injetado) é singleton, como os demais autenticadores Google. SCOPED como os outros conectores
        // (injeta um typed HttpClient — não pode ser capturado no root provider). NÃO emite sinais de score; expõe a
        // postura por ISiemPostureCollector e, [AEGIS-MVP-GOOGLE-SECOPS-02], a COBERTURA DE DETECÇÃO (regras × MITRE
        // via rules.list CONFIG_ONLY) por IDetectionCoverageCollector — ambas CONSULTIVAS, validando técnicas pelo
        // catálogo MITRE fixado (IMitreAttackCatalog, injetado da Infrastructure), sem tocar a autoridade determinística.
        services.AddSingleton<IGoogleSecOpsAuthenticator, GoogleSecOpsAuthenticator>();
        services.AddHttpClient<IChronicleApiClient, ChronicleApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddStandardResilienceHandler();
        services.AddScoped<IEvidenceConnector, GoogleSecOpsConnector>();
        return services;
    }
}
