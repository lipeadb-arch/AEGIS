using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Reference;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] SERVIÇO onde a configuração avaliada vive
// ============================================================================
// O relatório separa três eixos: DOMÍNIO de segurança (o que é protegido), SERVIÇO (onde a configuração vive) e
// PROVEDOR (quem opera o serviço). Até o ciclo anterior o serviço era derivado só da fonte ("Microsoft Entra ID"),
// porque só havia controles de diretório. Com Microsoft 365 e Azure no catálogo, o serviço passa a ser um valor
// TIPADO do controle — e a PLATAFORMA (Entra ID, Microsoft 365, Azure, Google Workspace) é o agrupamento que a
// tela usa para filtrar sem misturar serviços de naturezas diferentes.

/// <summary>Serviço avaliado por um controle. Valores estáveis — são persistidos como texto nas fotografias.</summary>
public enum KnightService
{
    /// <summary>Sem serviço declarado: o controle é compartilhado entre fontes e o serviço vem da fonte.</summary>
    Unspecified = 0,

    EntraId = 1,
    ExchangeOnline = 2,
    DefenderForOffice365 = 3,
    Purview = 4,
    Intune = 5,
    SharePointOnline = 6,
    Teams = 7,
    Fabric = 8,
    Forms = 9,
    Sway = 10,

    AzureSubscription = 20,
    AzureMonitor = 21,
    AzureNetworking = 22,
    DefenderForCloud = 23,
    AzureKeyVault = 24,
    AzureStorage = 25,
    AzureAppService = 26,
    AzureCompute = 27,
    AzureDatabases = 28,
    AzureDatabricks = 29,

    GoogleWorkspace = 40,
    GoogleGroups = 41,
    GoogleDrive = 42,

    Demo = 90,
}

/// <summary>Plataforma que agrupa serviços — o primeiro nível de filtro do relatório.</summary>
public enum KnightPlatform
{
    EntraId = 0,
    Microsoft365 = 1,
    Azure = 2,
    GoogleWorkspace = 3,
    Demo = 9,
}

/// <summary>Descrição legível de um serviço: rótulo, plataforma e provedor.</summary>
public sealed record KnightServiceDescriptor(KnightService Service, string Label, KnightPlatform Platform, string Provider);

public static class KnightServices
{
    private static readonly IReadOnlyDictionary<KnightService, KnightServiceDescriptor> ByService =
        new[]
        {
            D(KnightService.EntraId, "Microsoft Entra ID", KnightPlatform.EntraId, "Microsoft"),
            D(KnightService.ExchangeOnline, "Exchange Online", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.DefenderForOffice365, "Microsoft Defender para Office 365", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.Purview, "Microsoft Purview", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.Intune, "Microsoft Intune", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.SharePointOnline, "SharePoint e OneDrive", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.Teams, "Microsoft Teams", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.Fabric, "Microsoft Fabric (Power BI)", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.Forms, "Microsoft Forms", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.Sway, "Microsoft Sway", KnightPlatform.Microsoft365, "Microsoft"),
            D(KnightService.AzureSubscription, "Assinaturas e IAM do Azure", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureMonitor, "Azure Monitor e logs", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureNetworking, "Rede do Azure", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.DefenderForCloud, "Microsoft Defender para Nuvem", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureKeyVault, "Azure Key Vault", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureStorage, "Armazenamento do Azure", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureAppService, "App Service e Functions", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureCompute, "Computação do Azure", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureDatabases, "Bancos de dados do Azure", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.AzureDatabricks, "Azure Databricks", KnightPlatform.Azure, "Microsoft"),
            D(KnightService.GoogleWorkspace, "Google Workspace", KnightPlatform.GoogleWorkspace, "Google"),
            D(KnightService.GoogleGroups, "Google Groups", KnightPlatform.GoogleWorkspace, "Google"),
            D(KnightService.GoogleDrive, "Google Drive", KnightPlatform.GoogleWorkspace, "Google"),
            D(KnightService.Demo, "Demonstração (sintético)", KnightPlatform.Demo, "Demonstração"),
        }.ToDictionary(d => d.Service);

    private static KnightServiceDescriptor D(KnightService s, string label, KnightPlatform p, string provider) =>
        new(s, label, p, provider);

    /// <summary>Descrição do serviço; <see cref="KnightService.Unspecified"/> não tem descrição própria.</summary>
    public static KnightServiceDescriptor? Describe(KnightService service) =>
        ByService.TryGetValue(service, out var d) ? d : null;

    public static IReadOnlyList<KnightServiceDescriptor> All { get; } =
        ByService.Values.OrderBy(d => (int)d.Service).ToList();

    public static string PlatformLabel(KnightPlatform platform) => platform switch
    {
        KnightPlatform.EntraId => "Microsoft Entra ID",
        KnightPlatform.Microsoft365 => "Microsoft 365",
        KnightPlatform.Azure => "Microsoft Azure",
        KnightPlatform.GoogleWorkspace => "Google Workspace",
        KnightPlatform.Demo => "Demonstração",
        _ => platform.ToString(),
    };

    /// <summary>Serviço padrão de uma FONTE, para os controles compartilhados que não declaram serviço.</summary>
    public static KnightService DefaultFor(KnightSourceType source) => source switch
    {
        KnightSourceType.MicrosoftEntraId => KnightService.EntraId,
        KnightSourceType.MicrosoftTeams => KnightService.Teams,
        KnightSourceType.GoogleWorkspace => KnightService.GoogleWorkspace,
        KnightSourceType.Demo => KnightService.Demo,
        _ => KnightService.Unspecified,
    };
}
