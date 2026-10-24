using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Services;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Exchange;
using AegisScore.Connectors.Microsoft.Knight.Protection;
using AegisScore.Connectors.Microsoft.Knight.Azure;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Connectors.Microsoft.Knight.Teams;

namespace AegisScore.Connectors.Microsoft;

public static class DependencyInjection
{
    /// <summary>
    /// Coletores de configuração do AEGIS KNIGHT e documentos para o AEGIS NIST.
    /// </summary>
    /// <param name="teamsPowerShell">
    /// [AEGIS-KNIGHT-COVERAGE-02] Seção de configuração do adaptador de coleta do Microsoft Teams
    /// (<c>Knight:Teams</c>): executável do PowerShell, caminho do módulo pré-instalado na imagem e tempo
    /// limite. É configuração do AMBIENTE DE IMPLANTAÇÃO — nunca do locatário. Ausente, valem os padrões.
    /// </param>
    /// <param name="exchangePowerShell">
    /// [AEGIS-KNIGHT-COVERAGE-03] Seção de configuração do adaptador de coleta do Exchange Online
    /// (<c>Knight:Exchange</c>): executável do PowerShell, caminho do módulo pré-instalado na imagem, tempo limite
    /// e TETO DE ENUMERAÇÃO. Também é configuração do AMBIENTE DE IMPLANTAÇÃO — nunca do locatário.
    /// </param>
    /// <param name="http">
    /// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Seção <c>Knight:Http</c>: tempo por tentativa, tempo total, novas tentativas e
    /// circuito dos clientes HTTP do KNIGHT (ver <see cref="KnightHttpOptions"/>). Ambiente, nunca locatário. Ausente, valem
    /// os padrões documentados lá.
    /// </param>
    public static IServiceCollection AddMicrosoftConnectors(
        this IServiceCollection services,
        IConfiguration? teamsPowerShell = null,
        IConfiguration? exchangePowerShell = null,
        IConfiguration? http = null)
    {
        var httpOptions = new KnightHttpOptions();
        http?.Bind(httpOptions);
        services.AddSingleton(httpOptions);

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
        services.AddHttpClient<IEntraGraphClient, EntraGraphClient>().AddKnightResilience(httpOptions);
        services.AddScoped<IKnightCollector, EntraIdKnightCollector>();

        // [AEGIS-KNIGHT-COVERAGE-02] AEGIS KNIGHT → coletor REAL do Microsoft Teams (somente leitura). A
        // configuração de Teams do locatário não tem leitura na versão estável do Microsoft Graph: o caminho
        // oficial é o módulo Teams PowerShell com autenticação de APLICATIVO. Duas peças:
        //   • ITeamsTokenClient — typed HttpClient que obtém os DOIS tokens exigidos (Graph e a API de
        //     administração do Teams), com as MESMAS client credentials do conector Microsoft;
        //   • ITeamsAdminReader — executa o script EMBUTIDO num processo isolado e descartável por coleta.
        // As opções do adaptador são do AMBIENTE (executável, caminho do módulo na imagem, tempo limite), nunca
        // do locatário. Singleton nas opções e no leitor: não carregam estado de tenant nem HttpClient.
        services.AddHttpClient<ITeamsTokenClient, TeamsTokenClient>().AddKnightResilience(httpOptions);
        services.AddSingleton(_ =>
        {
            var options = new TeamsPowerShellOptions();
            teamsPowerShell?.Bind(options);
            return options;
        });
        services.AddSingleton<ITeamsAdminReader>(sp => new PowerShellTeamsAdminReader(
            sp.GetRequiredService<TeamsPowerShellOptions>(),
            sp.GetService<ILogger<PowerShellTeamsAdminReader>>()));
        services.AddScoped<IKnightCollector, TeamsKnightCollector>();

        // [AEGIS-KNIGHT-COVERAGE-03] AEGIS KNIGHT → coletor REAL do Exchange Online (somente leitura). Mesmas duas
        // peças do Teams, com as diferenças que importam:
        //   • IExchangeTokenClient — obtém UM token, emitido para o recurso do Exchange Online (o token do Graph
        //     não vale aqui), e resolve o domínio inicial do locatário, que a conexão de aplicativo exige;
        //   • IExchangeAdminReader — executa o script EMBUTIDO num processo isolado e descartável por coleta,
        //     pelo MESMO transporte do adaptador do Teams.
        // As opções são do AMBIENTE (executável, caminho do módulo na imagem, tempo limite, teto de enumeração).
        services.AddHttpClient<IExchangeTokenClient, ExchangeTokenClient>().AddKnightResilience(httpOptions);
        services.AddSingleton(_ =>
        {
            var options = new ExchangePowerShellOptions();
            exchangePowerShell?.Bind(options);
            return options;
        });
        services.AddSingleton<IExchangeAdminReader>(sp => new PowerShellExchangeAdminReader(
            sp.GetRequiredService<ExchangePowerShellOptions>(),
            sp.GetService<ILogger<PowerShellExchangeAdminReader>>()));
        services.AddScoped<IKnightCollector, ExchangeKnightCollector>();

        // [AEGIS-KNIGHT-COVERAGE-04] Demais serviços do Microsoft 365 e Azure. Mesma credencial do conector Microsoft
        // (segredo ou certificado), um coletor por fonte:
        //   • IMicrosoftAppTokenClient — token de aplicativo para qualquer recurso oficial (Fabric, administração do
        //     SharePoint, Security & Compliance, Azure Resource Manager);
        //   • IMicrosoftRestClient — GET somente leitura com destino restrito aos hosts oficiais;
        //   • IProtectionAdminReader — o adaptador PowerShell embutido do Defender para Office 365 e do Purview;
        //   • IDnsTxtResolver — registros SPF e DMARC públicos dos domínios aceitos.
        services.AddHttpClient<IMicrosoftAppTokenClient, MicrosoftAppTokenClient>().AddKnightResilience(httpOptions);
        services.AddHttpClient<IMicrosoftRestClient, MicrosoftRestClient>().AddKnightResilience(httpOptions);
        services.AddSingleton(sp =>
        {
            // O adaptador de proteção usa o MESMO runtime do Exchange Online (executável e módulo na imagem).
            var exo = sp.GetRequiredService<ExchangePowerShellOptions>();
            return new ProtectionPowerShellOptions
            {
                Executable = exo.Executable, ModulePath = exo.ModulePath, Timeout = exo.Timeout,
                MaxOutputBytes = exo.MaxOutputBytes, EnumerationLimit = exo.EnumerationLimit,
            };
        });
        services.AddSingleton<IProtectionAdminReader>(sp => new PowerShellProtectionAdminReader(
            sp.GetRequiredService<ProtectionPowerShellOptions>(),
            sp.GetService<ILogger<PowerShellProtectionAdminReader>>()));
        services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();
        services.AddScoped<IKnightCollector, DefenderForOffice365KnightCollector>();
        services.AddScoped<IKnightCollector, PurviewKnightCollector>();
        services.AddScoped<IKnightCollector, SharePointKnightCollector>();
        services.AddScoped<IKnightCollector, IntuneKnightCollector>();
        services.AddScoped<IKnightCollector, FabricKnightCollector>();
        // [AEGIS-KNIGHT-COVERAGE-04] Azure Resource Manager (papel Leitor), plano de dados dos cofres e Graph (estado das contas).
        services.AddScoped<IKnightCollector, AzureKnightCollector>();

        // [AEGIS-KNIGHT-ACCESS-01] "Testar conexão" (IEvidenceConnector) para Microsoft/IdentityPosture: antes
        // desta linha não havia NENHUM adaptador registrado para essa combinação, e o botão da tela de
        // Integrações devolvia 501 para a credencial que alimenta as três fontes do KNIGHT. SCOPED pelo mesmo
        // motivo dos demais (depende de typed HttpClients via IEntraGraphClient/ITeamsTokenClient/IExchangeTokenClient).
        services.AddScoped<IEvidenceConnector, KnightIdentityPostureConnector>();
        return services;
    }
}
