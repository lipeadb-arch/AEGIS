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
        KnightCapability.TeamsAppAvailability,
        KnightCapability.TeamsPolicyAssignments,
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
