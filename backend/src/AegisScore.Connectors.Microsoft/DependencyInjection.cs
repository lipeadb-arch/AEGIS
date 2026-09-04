using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Services;
using AegisScore.Connectors.Microsoft.Defender;
using AegisScore.Connectors.Microsoft.Intune;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Sentinel;

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
        // Reusa o transporte VALIDADO do Graph (IEntraGraphClient) e o protetor de segredos existente. SCOPED (não
        // singleton): o IEntraGraphClient é um typed HttpClient (transient, gerido pelo IHttpClientFactory) — um
        // singleton o CAPTURARIA no root provider (o handler nunca rotacionaria). Scoped resolve um cliente fresco
        // por escopo (o IConnectorRegistry também é scoped), sem segunda infra de OAuth/HttpClient.
        services.AddScoped<IEvidenceConnector, MicrosoftSecureScoreConnector>();

        // [AEGIS-MVP-VULN-01] Microsoft Defender Vulnerability Management (Microsoft/VulnerabilityScanner): máquinas +
        // CVEs por máquina + catálogo de CVE, somente leitura. Transporte PRÓPRIO (IDefenderApiClient) — host
        // api.security.microsoft.com, token no recurso legado api.securitycenter.microsoft.com — pois o
        // EntraGraphClient é, corretamente, restrito ao Microsoft Graph. Typed HttpClient com a resiliência padrão
        // (retry/backoff, Retry-After no 429, circuit breaker), como o Graph client. SCOPED pelo mesmo motivo do
        // Secure Score (injeta um typed HttpClient — não pode ser capturado no root provider).
        services.AddHttpClient<IDefenderApiClient, DefenderApiClient>().AddStandardResilienceHandler();
        services.AddScoped<IEvidenceConnector, MicrosoftDefenderVulnerabilityConnector>();
        // services.AddSingleton<IEvidenceConnector, MicrosoftPurviewConnector>();
        // services.AddSingleton<IEvidenceConnector, AzureAdvisorConnector>();

        // [AEGIS-MVP-MICROSOFT-SENTINEL] Microsoft Sentinel (MicrosoftSentinel/Siem): coletor REAL, somente leitura,
        // via Azure Monitor Log Analytics Query API. Transporte PRÓPRIO (ILogAnalyticsClient) — host/recurso oficiais
        // api.loganalytics.azure.com, distintos do Graph e do Defender. Typed HttpClient com a resiliência padrão
        // (retry/backoff, Retry-After no 429, circuit breaker). SCOPED pelo mesmo motivo dos demais (injeta um typed
        // HttpClient — não pode ser capturado no root provider). NÃO emite sinais de score; expõe a postura
        // operacional por ISiemPostureCollector (fato consultivo), sem tocar a autoridade determinística.
        services.AddHttpClient<ILogAnalyticsClient, LogAnalyticsClient>().AddStandardResilienceHandler();
        services.AddScoped<IEvidenceConnector, MicrosoftSentinelConnector>();

        // [AEGIS-MVP-MICROSOFT-COVERAGE-02] Microsoft Intune (Microsoft/ConfigAnalyzer): postura de configuração e
        // conformidade de dispositivos, somente leitura, via Graph v1.0. REUSA o transporte endurecido do Graph
        // (IEntraGraphClient, já registrado abaixo como typed HttpClient com resiliência padrão) — sem segundo
        // pipeline de OAuth/paginação/allowlist. SCOPED pelo mesmo motivo dos demais (injeta um typed HttpClient
        // — não pode ser capturado no root provider). NÃO emite sinais de score: expõe a postura de dispositivos
        // por IDevicePostureCollector (fato consultivo), sem tocar a autoridade determinística.
        services.AddScoped<IEvidenceConnector, MicrosoftIntuneDevicePostureConnector>();

        // Govern → Provider Pattern de ingestão de documentos: o SharePoint/M365 como fonte de políticas.
        // A DocumentIntegrationFactory resolve esta estratégia por ConnectorProvider.Microsoft.
        services.AddSingleton<IDocumentIntegrationProvider, SharePointProvider>();

        // [AEGIS-MVP-EVIDENCE-FABRIC-01] O antigo EntraIdTelemetryProviderStub (números fictícios → PR.AA-01/
        // GV.RR-01) foi APOSENTADO: nenhuma rota de produção recebe telemetria de identidade simulada. A postura
        // de identidade agora vem da Evidence Fabric compartilhada (uma aquisição real do Entra ID via o coletor
        // do KNIGHT), consumida por IIdentityEvidenceService — registrada no AddAegisScoreInfrastructure.

        // AEGIS KNIGHT → coletor REAL do Microsoft Entra ID (somente leitura). HttpClient tipado com
        // resiliência padrão (retry/backoff, Retry-After no 429, circuit breaker) — reusa a fachada oficial,
        // sem infra própria. O coletor recebe a configuração DECIFRADA pelo contexto; não toca segredos.
        services.AddHttpClient<IEntraGraphClient, EntraGraphClient>().AddStandardResilienceHandler();
        services.AddScoped<IKnightCollector, EntraIdKnightCollector>();
        return services;
    }
}
