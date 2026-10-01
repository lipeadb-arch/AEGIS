using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Reference;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] O que cada coletor REAL produz — a base para dizer se uma regra está no fluxo ativo.
/// Uma regra que consome uma capacidade que nenhum coletor real produz nunca é avaliada fora de testes, e por isso
/// não pode sustentar cobertura de implementação. A demonstração (dados sintéticos) não conta.
/// Os testes do coletor conferem que ele de fato devolve o desfecho de cada capacidade declarada aqui.
/// </summary>
public static class KnightCollectorCapabilities
{
    private static readonly IReadOnlySet<KnightCapability> Entra = new HashSet<KnightCapability>
    {
        KnightCapability.PrivilegedRoleInventory,
        KnightCapability.MfaRegistration,
        KnightCapability.GuestAccounts,
        KnightCapability.ConditionalAccessPolicies,
        KnightCapability.SecurityBaseline,
        KnightCapability.ApplicationInventory,
        KnightCapability.ApplicationPermissions,
        KnightCapability.ApplicationConsents,
        KnightCapability.IdentityRiskyUsers,
        KnightCapability.IdentityRiskDetections,
        KnightCapability.AuthorizationPolicy,
        KnightCapability.AdminConsentPolicy,
        KnightCapability.AppManagementPolicy,
        KnightCapability.AuthenticationMethodsPolicy,
        KnightCapability.DirectorySettings,
        KnightCapability.Domains,
        KnightCapability.DirectorySynchronization,
        KnightCapability.DeviceRegistrationPolicy,
        KnightCapability.GroupVisibility,
        KnightCapability.PrivilegedAccountDetails,
        KnightCapability.PrivilegedIdentityManagement,
        KnightCapability.AccessReviews,
        KnightCapability.NamedLocations,
        KnightCapability.ServicePrincipalSettings,
    };

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] O que o coletor do Microsoft Teams tenta em cada coleta. São SEIS comandos de
    /// leitura do módulo oficial; cada um é uma capacidade, e a falha de um não invalida os outros.
    /// </summary>
    private static readonly IReadOnlySet<KnightCapability> Teams = new HashSet<KnightCapability>
    {
        KnightCapability.TeamsClientConfiguration,
        KnightCapability.TeamsFederationConfiguration,
        KnightCapability.TeamsMeetingPolicies,
        KnightCapability.TeamsMessagingPolicies,
        KnightCapability.TeamsAppPermissionPolicies,
        KnightCapability.TeamsPolicyAssignments,
    };

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-03] O que o coletor do Exchange Online tenta em cada coleta. São DOZE comandos de
    /// leitura do módulo oficial; cada um é uma capacidade, e a falha de um não invalida os outros — o controle
    /// que dependia dele fica não avaliado com o COMANDO nomeado.
    /// </summary>
    private static readonly IReadOnlySet<KnightCapability> Exchange = new HashSet<KnightCapability>
    {
        KnightCapability.ExchangeOrganizationConfig,
        KnightCapability.ExchangeTransportConfig,
        KnightCapability.ExchangeSharingPolicies,
        KnightCapability.ExchangeOwaMailboxPolicies,
        KnightCapability.ExchangeTransportRules,
        KnightCapability.ExchangeRoleAssignmentPolicies,
        KnightCapability.ExchangeExternalSenderIdentification,
        KnightCapability.ExchangeOutboundSpamFilterPolicies,
        KnightCapability.ExchangeMailboxes,
        KnightCapability.ExchangeMailboxSignIn,
        KnightCapability.ExchangeCasMailboxes,
        KnightCapability.ExchangeAuditBypassAssociations,
    };

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Intune: duas leituras do Microsoft Graph (configuração do serviço e restrições de
    /// registro).
    /// </summary>
    private static readonly IReadOnlySet<KnightCapability> Intune = new HashSet<KnightCapability>
    {
        KnightCapability.IntuneServiceSettings,
        KnightCapability.IntuneEnrollmentRestrictions,
    };

    /// <summary>
    /// SharePoint/OneDrive: a leitura do Microsoft Graph e a da API administrativa do SharePoint. A segunda é TENTADA em
    /// toda coleta — sem certificado, o coletor registra "não tentada" com o motivo, e os controles que dependem dela
    /// ficam não avaliados dizendo por quê.
    /// </summary>
    private static readonly IReadOnlySet<KnightCapability> SharePoint = new HashSet<KnightCapability>
    {
        KnightCapability.SharePointTenantSettings,
        KnightCapability.SharePointAdminTenant,
    };

    private static readonly IReadOnlySet<KnightCapability> Fabric = new HashSet<KnightCapability>
    {
        KnightCapability.FabricTenantSettings,
    };

    /// <summary>
    /// Defender para Office 365: treze leituras do módulo oficial do Exchange Online e a consulta DNS (SPF e DMARC)
    /// feita pelo AEGIS sobre os domínios aceitos lidos na mesma coleta.
    /// </summary>
    private static readonly IReadOnlySet<KnightCapability> DefenderO365 = new HashSet<KnightCapability>
    {
        KnightCapability.DefenderAtpPolicy,
        KnightCapability.DefenderSafeLinks,
        KnightCapability.DefenderSafeAttachments,
        KnightCapability.DefenderMalwareFilter,
        KnightCapability.DefenderInboundSpam,
        KnightCapability.DefenderOutboundSpam,
        KnightCapability.DefenderConnectionFilter,
        KnightCapability.DefenderAntiPhish,
        KnightCapability.DefenderDkim,
        KnightCapability.DefenderAcceptedDomains,
        KnightCapability.DefenderDnsRecords,
        KnightCapability.DefenderTeamsProtection,
        KnightCapability.DefenderPriorityAccounts,
        KnightCapability.DefenderPresetPolicies,
    };

    /// <summary>Purview: auditoria (sessão do Exchange Online) e DLP/rótulos (sessão do Security &amp; Compliance).</summary>
    private static readonly IReadOnlySet<KnightCapability> Purview = new HashSet<KnightCapability>
    {
        KnightCapability.PurviewAuditConfig,
        KnightCapability.PurviewDlpPolicies,
        KnightCapability.PurviewLabelPolicies,
    };

    private static readonly IReadOnlySet<KnightCapability> Google = new HashSet<KnightCapability>
    {
        KnightCapability.DirectoryUsers,
        KnightCapability.DirectoryGroups,
        KnightCapability.DriveSharingAudit,
        KnightCapability.OAuthTokenAudit,
    };

    /// <summary>Capacidades que o coletor real da fonte tenta em cada coleta.</summary>
    public static IReadOnlySet<KnightCapability> Produces(KnightSourceType source) => source switch
    {
        KnightSourceType.MicrosoftEntraId => Entra,
        KnightSourceType.MicrosoftTeams => Teams,
        KnightSourceType.MicrosoftExchangeOnline => Exchange,
        KnightSourceType.MicrosoftIntune => Intune,
        KnightSourceType.MicrosoftSharePoint => SharePoint,
        KnightSourceType.MicrosoftFabric => Fabric,
        KnightSourceType.MicrosoftDefenderForOffice365 => DefenderO365,
        KnightSourceType.MicrosoftPurview => Purview,
        KnightSourceType.GoogleWorkspace => Google,
        _ => new HashSet<KnightCapability>(),
    };

    /// <summary>
    /// O controle está no fluxo ativo: aplicável a ao menos uma fonte real cujo coletor produz TODAS as capacidades
    /// que o perfil do controle declara consumir. Sem perfil (capacidades desconhecidas), não está.
    /// </summary>
    public static bool IsActive(KnightIndicatorDefinition def)
    {
        var required = KnightControlProfiles.RequiredCapabilitiesOf(def.Id);
        if (required.Count == 0) return false;
        return def.Sources.Any(s => s != KnightSourceType.Demo && required.All(Produces(s).Contains));
    }
}
