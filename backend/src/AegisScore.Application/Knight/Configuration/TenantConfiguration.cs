using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AegisScore.Application.Identity.Adm;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] Configuração do LOCATÁRIO observada numa coleta
// ============================================================================
// Até o ciclo anterior o ADM preservava identidades (três populações), as políticas de acesso condicional e os
// papéis privilegiados ativos. Os controles de configuração do Entra ID que faltavam — política de autorização,
// consentimento, métodos de autenticação, regras de senha, domínios, dispositivos, grupos públicos, PIM,
// revisões de acesso, locais nomeados — leem objetos que também NÃO são identidades.
//
// Cada contrato abaixo é a NORMALIZAÇÃO tipada do que a fonte devolveu, sem interpretação: os nomes seguem os da
// fonte, valores ausentes permanecem nulos (nulo NUNCA vira "desabilitado" nem "conforme") e cada documento
// carrega o nome e a versão do contrato. A leitura só interpreta as versões que conhece.
//
// "Coletado, lista vazia" e "não coletado" são distinguidos pelo desfecho da CAPACIDADE que produz o tipo — a
// mesma regra que já vale para as políticas de acesso condicional.

/// <summary>Política de autorização do diretório — o que usuários e convidados podem fazer por padrão.</summary>
public sealed record EntraAuthorizationPolicyConfiguration(
    bool? AllowedToCreateApps,
    bool? AllowedToCreateSecurityGroups,
    bool? AllowedToCreateTenants,
    bool? AllowedToReadBitlockerKeysForOwnedDevice,
    bool? AllowedToReadOtherUsers,
    string? GuestUserRoleId,
    string? AllowInvitesFrom,
    IReadOnlyList<string> PermissionGrantPoliciesAssigned,
    bool? AllowEmailVerifiedUsersToJoinOrganization,
    bool? AllowUserConsentForRiskyApps,
    bool? BlockMsolPowerShell)
{
    public const string SchemaVersion = "aegis-config-entra-authorization-policy-v1";
    public const string ExternalId = "authorizationPolicy";
}

/// <summary>Política do fluxo de consentimento do administrador.</summary>
public sealed record EntraAdminConsentPolicyConfiguration(
    bool? IsEnabled,
    int ReviewerCount,
    bool? NotifyReviewers,
    bool? RemindersEnabled,
    int? RequestDurationInDays)
{
    public const string SchemaVersion = "aegis-config-entra-admin-consent-policy-v1";
    public const string ExternalId = "adminConsentRequestPolicy";
}

/// <summary>Uma restrição de credencial da política padrão de gerenciamento de aplicações.</summary>
public sealed record EntraAppCredentialRestriction(
    string RestrictionType,
    string? State,
    string? MaxLifetime,
    DateTimeOffset? RestrictForAppsCreatedAfter);

/// <summary>Política PADRÃO de gerenciamento de aplicações (vale para as aplicações sem política própria).</summary>
public sealed record EntraAppManagementPolicyConfiguration(
    bool? IsEnabled,
    IReadOnlyList<EntraAppCredentialRestriction> PasswordCredentials,
    IReadOnlyList<EntraAppCredentialRestriction> KeyCredentials)
{
    public const string SchemaVersion = "aegis-config-entra-app-management-policy-v1";
    public const string ExternalId = "defaultAppManagementPolicy";
}

/// <summary>Estado de UM método de autenticação e seus alvos (ids da fonte; "all_users" é preservado).</summary>
public sealed record EntraAuthenticationMethodState(
    string Id,
    string? State,
    IReadOnlyList<string> IncludeTargetIds,
    IReadOnlyList<string> ExcludeTargetIds);

/// <summary>Um recurso do Microsoft Authenticator (estado + alvo incluído e excluído).</summary>
public sealed record EntraAuthenticatorFeature(string? State, string? IncludeTargetId, string? ExcludeTargetId);

/// <summary>
/// Política de métodos de autenticação do locatário, como a versão ESTÁVEL (v1.0) do Microsoft Graph a expõe.
/// MFA preferencial do sistema, Authenticator em aplicativos complementares e denúncia de atividade suspeita só
/// existem na versão beta — ficam fora do contrato, e os controles de referência correspondentes são declarados
/// como limitação de API, não como avaliados.
/// </summary>
public sealed record EntraAuthenticationMethodsConfiguration(
    string? PolicyMigrationState,
    string? RegistrationCampaignState,
    IReadOnlyList<EntraAuthenticationMethodState> Methods,
    EntraAuthenticatorFeature? DisplayAppInformation,
    EntraAuthenticatorFeature? DisplayLocationInformation)
{
    public const string SchemaVersion = "aegis-config-entra-authentication-methods-v1";
    public const string ExternalId = "authenticationMethodsPolicy";

    public EntraAuthenticationMethodState? Method(string id) =>
        Methods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Configuração de diretório de UM modelo, com os valores EFETIVOS: os personalizados no locatário e, para os que
/// não foram personalizados, o padrão declarado pelo próprio modelo (<see cref="DefaultedNames"/>).
/// </summary>
public sealed record EntraDirectorySettingConfiguration(
    string TemplateId,
    string? TemplateName,
    bool IsCustomized,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<string> DefaultedNames)
{
    public const string SchemaVersion = "aegis-config-entra-directory-setting-v1";

    /// <summary>Modelo "Password Rule Settings" (bloqueio de conta e senhas proibidas).</summary>
    public const string PasswordRuleTemplateId = "5cf42378-d67d-4f36-ba46-e8b86229381d";

    /// <summary>Modelo "Group.Unified" (criação e acesso de grupos do Microsoft 365).</summary>
    public const string GroupUnifiedTemplateId = "62375ab9-6b52-47ed-826b-58e47e0e304b";

    public string? Value(string name) =>
        Values.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool IsDefault(string name) => DefaultedNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Domínio do diretório.</summary>
public sealed record EntraDomainConfiguration(
    string Id,
    bool? IsVerified,
    bool? IsDefault,
    bool? IsInitial,
    string? AuthenticationType,
    int? PasswordValidityPeriodInDays,
    IReadOnlyList<string> SupportedServices)
{
    public const string SchemaVersion = "aegis-config-entra-domain-v1";

    /// <summary>Valor que a fonte usa para "senhas nunca expiram".</summary>
    public const int NeverExpires = int.MaxValue;
}

/// <summary>Sincronização com o diretório local e sincronização de hash de senha.</summary>
public sealed record EntraDirectorySynchronizationConfiguration(
    bool? OnPremisesSyncEnabled,
    DateTimeOffset? OnPremisesLastSyncDateTime,
    bool? PasswordHashSyncEnabled,
    string? PasswordHashSyncLimitation)
{
    public const string SchemaVersion = "aegis-config-entra-directory-sync-v1";
    public const string ExternalId = "directorySynchronization";
}

/// <summary>Política de registro e ingresso de dispositivos. Tipos de pertencimento: "all", "none", "selected".</summary>
public sealed record EntraDeviceRegistrationConfiguration(
    int? UserDeviceQuota,
    string? MultiFactorAuthConfiguration,
    string? AllowedToJoin,
    int AllowedToJoinUsers,
    int AllowedToJoinGroups,
    bool? GlobalAdminsAreLocalAdmins,
    string? RegisteringUsersAreLocalAdmins,
    int LocalAdminUsers,
    int LocalAdminGroups,
    bool? LocalAdminPasswordEnabled,
    string? AllowedToRegister)
{
    public const string SchemaVersion = "aegis-config-entra-device-registration-v1";
    public const string ExternalId = "deviceRegistrationPolicy";
}

/// <summary>Grupo referenciado por um inventário (identificador e nome como observados).</summary>
public sealed record EntraGroupReference(string Id, string? DisplayName);

/// <summary>Inventário de visibilidade dos grupos do Microsoft 365 da coleta.</summary>
public sealed record EntraGroupVisibilityInventory(
    int UnifiedGroupsTotal,
    int PublicGroupsTotal,
    IReadOnlyList<EntraGroupReference> PublicGroups,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-entra-group-visibility-v1";
    public const string ExternalId = "groupVisibility";

    /// <summary>Limite de grupos públicos preservados nominalmente numa coleta (o total é sempre registrado).</summary>
    public const int MaxListed = 500;
}

/// <summary>
/// Atributos de UMA conta privilegiada (usuário) observados na coleta: origem (sincronizada do diretório local ou
/// só em nuvem) e licenças, com os planos de produtividade efetivos. Não cria identidade — descreve a que já é
/// preservada no conjunto de membros privilegiados.
/// </summary>
public sealed record EntraPrivilegedAccountProfile(
    string UserId,
    string? DisplayName,
    string? UserPrincipalName,
    IReadOnlyList<string> RoleTemplateIds,
    IReadOnlyList<string> RoleNames,
    bool? OnPremisesSyncEnabled,
    bool? AccountEnabled,
    IReadOnlyList<string> SkuPartNumbers,
    IReadOnlyList<string> ProductivityServicePlans,
    bool LicensesResolved)
{
    public const string SchemaVersion = "aegis-config-entra-privileged-account-v1";
}

/// <summary>Uma atribuição ATIVA de papel observada no PIM.</summary>
public sealed record EntraRoleAssignment(string PrincipalId, string? PrincipalType, bool Permanent, DateTimeOffset? EndDateTime);

/// <summary>Governança de UM papel privilegiado: atribuições ativas, elegíveis e regras de ativação.</summary>
public sealed record EntraPrivilegedRoleGovernance(
    string RoleDefinitionId,
    string? DisplayName,
    bool IsPrivileged,
    IReadOnlyList<EntraRoleAssignment> ActiveAssignments,
    int EligibleAssignments,
    bool? ActivationRequiresApproval,
    int ActivationApproverCount,
    bool? ActivationRequiresMfa,
    string? PolicyLimitation)
{
    public const string SchemaVersion = "aegis-config-entra-privileged-role-governance-v1";
}

/// <summary>
/// Uma consulta de escopo da revisão de acesso, com a ORIGEM — é a origem que diz o que a consulta delimita:
/// <c>scope</c> e <c>resourceScope</c> dizem O QUE é revisado, <c>principalScope</c> QUEM entra na revisão e
/// <c>instanceEnumerationScope</c> QUAIS recursos a série enumera (a documentação exige essa última para revisar
/// convidados em todos os grupos do Microsoft 365).
/// </summary>
public sealed record EntraAccessReviewScopeQuery(
    string Origin,
    string Query,
    string? QueryType,
    string? QueryRoot,
    string? OdataType,
    string? InactiveDuration)
{
    /// <summary>
    /// Escopo restrito aos usuários INATIVOS (<c>accessReviewInactiveUsersQueryScope</c>). A consulta é idêntica à
    /// de uma revisão sem restrição: a diferença está no tipo e em <c>inactiveDuration</c>.
    /// </summary>
    public bool RestrictedToInactiveUsers =>
        !string.IsNullOrWhiteSpace(InactiveDuration)
        || (OdataType?.Contains("accessReviewInactiveUsersQueryScope", StringComparison.OrdinalIgnoreCase) ?? false);

    public const string OriginScope = "scope";
    public const string OriginPrincipal = "principalScope";
    public const string OriginResource = "resourceScope";
    public const string OriginInstanceEnumeration = "instanceEnumerationScope";
}

/// <summary>
/// Uma etapa de revisão multi-etapas. A documentação é explícita: quando <c>stageSettings</c> existe, os valores
/// da etapa substituem os correspondentes do nível superior — inclusive os revisores.
/// </summary>
public sealed record EntraAccessReviewStage(
    string? StageId,
    int ReviewerCount,
    int FallbackReviewerCount,
    int? DurationInDays,
    IReadOnlyList<string> DependsOn);

/// <summary>
/// Recorrência da série: o PADRÃO (com que frequência repete, e em que dia do mês) e a FAIXA (por quanto tempo
/// repete). <c>DayOfMonth</c> é o que permite situar as ocorrências de um padrão mensal no calendário — a primeira
/// ocorrência pode ser posterior ao início da faixa.
/// </summary>
public sealed record EntraAccessReviewRecurrence(
    string? PatternType,
    int? Interval,
    int? DayOfMonth,
    string? RangeType,
    DateOnly? StartDate,
    DateOnly? EndDate,
    int? NumberOfOccurrences);

/// <summary>
/// Definição de revisão de acesso, normalizada no que os controles leem. O escopo é preservado consulta a consulta,
/// com a origem, porque existir uma revisão não comprova QUAL população ela alcança.
/// </summary>
public sealed record EntraAccessReviewDefinition(
    string Id,
    string? DisplayName,
    string? Status,
    string ScopeKind,
    IReadOnlyList<EntraAccessReviewScopeQuery> Queries,
    IReadOnlyList<string> RoleDefinitionIds,
    EntraAccessReviewRecurrence? Recurrence,
    int ReviewerCount,
    IReadOnlyList<EntraAccessReviewStage> Stages,
    int? InstanceDurationInDays,
    bool? AutoApplyDecisionsEnabled,
    bool RemovesAccessWhenApplied,
    bool? JustificationRequiredOnApproval,
    bool? MailNotificationsEnabled)
{
    public const string SchemaVersion = "aegis-config-entra-access-review-v3";

    public const string ScopeGuests = "guests";
    public const string ScopeDirectoryRole = "directoryRole";
    public const string ScopeOther = "other";
}

/// <summary>Local nomeado do acesso condicional.</summary>
public sealed record EntraNamedLocationConfiguration(
    string Id,
    string? DisplayName,
    string Kind,
    bool? IsTrusted,
    int IpRangeCount,
    int CountryCount,
    bool? IncludeUnknownCountriesAndRegions)
{
    public const string SchemaVersion = "aegis-config-entra-named-location-v1";
}

/// <summary>Estado de uma aplicação de serviço do locatário, lida por identificador de aplicação conhecido.</summary>
public sealed record EntraServicePrincipalState(string AppId, string? DisplayName, bool Present, bool? AccountEnabled)
{
    public const string SchemaVersion = "aegis-config-entra-service-principal-state-v1";

    /// <summary>Aplicação da Microsoft que controla o armazenamento de terceiros no Microsoft 365 na web.</summary>
    public const string ThirdPartyStorageAppId = "c1f33bc0-bdb4-4248-ba9b-096807ddb43e";
}

/// <summary>Credenciais de UMA aplicação registrada com vigência longa (certificados acima do limite).</summary>
public sealed record EntraApplicationCredentialSummary(
    string ApplicationId,
    string? DisplayName,
    int CertificateCount,
    int LongLivedCertificateCount,
    int? LongestCertificateValidityDays,
    int PasswordCount,
    int? LongestPasswordValidityDays);

/// <summary>Inventário das credenciais das aplicações registradas (só as que têm credencial).</summary>
public sealed record EntraApplicationCredentialInventory(
    int ApplicationsTotal,
    int ApplicationsWithCredentials,
    int LongLivedThresholdDays,
    IReadOnlyList<EntraApplicationCredentialSummary> Applications,
    bool ListComplete,
    int ApplicationsWithLongLivedCertificates)
{
    public const string SchemaVersion = "aegis-config-entra-application-credentials-v1";
    public const string ExternalId = "applicationCredentials";
    public const int MaxListed = 1000;
}

// ---- Documento, registro de tipos e contêiner --------------------------------------------------------

/// <summary>Um documento de configuração da coleta: tipo, identificador na fonte, nome observado e contrato.</summary>
public sealed record KnightConfigurationDocument(
    ConfigurationObjectKind Kind,
    string ExternalId,
    string? DisplayName,
    string SchemaVersion,
    string Json);

/// <summary>Leitura de um tipo de configuração: coletado (lista autoritativa, possivelmente vazia) ou ausente com motivo.</summary>
public sealed record KnightConfigurationRead<T>(bool Collected, IReadOnlyList<T> Items, string? MissingReason)
{
    public T? Single => Items.Count > 0 ? Items[0] : default;
}

/// <summary>Qual capacidade de coleta produz cada tipo de configuração, e em qual contrato.</summary>
public static class KnightConfigurationKinds
{
    public sealed record Spec(ConfigurationObjectKind Kind, KnightCapability Capability, string SchemaVersion, Type Contract);

    public static IReadOnlyList<Spec> All { get; } = new[]
    {
        new Spec(ConfigurationObjectKind.AuthorizationPolicy, KnightCapability.AuthorizationPolicy,
            EntraAuthorizationPolicyConfiguration.SchemaVersion, typeof(EntraAuthorizationPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.AdminConsentRequestPolicy, KnightCapability.AdminConsentPolicy,
            EntraAdminConsentPolicyConfiguration.SchemaVersion, typeof(EntraAdminConsentPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.AppManagementPolicy, KnightCapability.AppManagementPolicy,
            EntraAppManagementPolicyConfiguration.SchemaVersion, typeof(EntraAppManagementPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.AuthenticationMethodsPolicy, KnightCapability.AuthenticationMethodsPolicy,
            EntraAuthenticationMethodsConfiguration.SchemaVersion, typeof(EntraAuthenticationMethodsConfiguration)),
        new Spec(ConfigurationObjectKind.DirectorySetting, KnightCapability.DirectorySettings,
            EntraDirectorySettingConfiguration.SchemaVersion, typeof(EntraDirectorySettingConfiguration)),
        new Spec(ConfigurationObjectKind.Domain, KnightCapability.Domains,
            EntraDomainConfiguration.SchemaVersion, typeof(EntraDomainConfiguration)),
        new Spec(ConfigurationObjectKind.DirectorySynchronization, KnightCapability.DirectorySynchronization,
            EntraDirectorySynchronizationConfiguration.SchemaVersion, typeof(EntraDirectorySynchronizationConfiguration)),
        new Spec(ConfigurationObjectKind.DeviceRegistrationPolicy, KnightCapability.DeviceRegistrationPolicy,
            EntraDeviceRegistrationConfiguration.SchemaVersion, typeof(EntraDeviceRegistrationConfiguration)),
        new Spec(ConfigurationObjectKind.GroupVisibilityInventory, KnightCapability.GroupVisibility,
            EntraGroupVisibilityInventory.SchemaVersion, typeof(EntraGroupVisibilityInventory)),
        new Spec(ConfigurationObjectKind.PrivilegedAccountProfile, KnightCapability.PrivilegedAccountDetails,
            EntraPrivilegedAccountProfile.SchemaVersion, typeof(EntraPrivilegedAccountProfile)),
        new Spec(ConfigurationObjectKind.PrivilegedRoleGovernance, KnightCapability.PrivilegedIdentityManagement,
            EntraPrivilegedRoleGovernance.SchemaVersion, typeof(EntraPrivilegedRoleGovernance)),
        new Spec(ConfigurationObjectKind.AccessReviewDefinition, KnightCapability.AccessReviews,
            EntraAccessReviewDefinition.SchemaVersion, typeof(EntraAccessReviewDefinition)),
        new Spec(ConfigurationObjectKind.NamedLocation, KnightCapability.NamedLocations,
            EntraNamedLocationConfiguration.SchemaVersion, typeof(EntraNamedLocationConfiguration)),
        new Spec(ConfigurationObjectKind.ServicePrincipalState, KnightCapability.ServicePrincipalSettings,
            EntraServicePrincipalState.SchemaVersion, typeof(EntraServicePrincipalState)),
        new Spec(ConfigurationObjectKind.ApplicationCredentialInventory, KnightCapability.ApplicationInventory,
            EntraApplicationCredentialInventory.SchemaVersion, typeof(EntraApplicationCredentialInventory)),

        // ---- [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams ----------------------------------------------------
        new Spec(ConfigurationObjectKind.TeamsClientConfiguration, KnightCapability.TeamsClientConfiguration,
            TeamsClientConfiguration.SchemaVersion, typeof(TeamsClientConfiguration)),
        new Spec(ConfigurationObjectKind.TeamsFederationConfiguration, KnightCapability.TeamsFederationConfiguration,
            TeamsFederationConfiguration.SchemaVersion, typeof(TeamsFederationConfiguration)),
        new Spec(ConfigurationObjectKind.TeamsMeetingPolicy, KnightCapability.TeamsMeetingPolicies,
            TeamsMeetingPolicyConfiguration.SchemaVersion, typeof(TeamsMeetingPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.TeamsMessagingPolicy, KnightCapability.TeamsMessagingPolicies,
            TeamsMessagingPolicyConfiguration.SchemaVersion, typeof(TeamsMessagingPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.TeamsAppPermissionPolicy, KnightCapability.TeamsAppPermissionPolicies,
            TeamsAppPermissionPolicyConfiguration.SchemaVersion, typeof(TeamsAppPermissionPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.TeamsPolicyAssignment, KnightCapability.TeamsPolicyAssignments,
            TeamsPolicyAssignment.SchemaVersion, typeof(TeamsPolicyAssignment)),

        // ---- [AEGIS-KNIGHT-COVERAGE-03] Exchange Online ---------------------------------------------------
        // Mais de um contrato pode nascer da MESMA capacidade (a enumeração de caixas alimenta o inventário de
        // compartilhadas, o de auditoria, o de encaminhamento e o de alcance). Isso é deliberado: são leituras
        // do mesmo comando, e a falha desse comando deixa os quatro sem dado — que é exatamente o correto.
        new Spec(ConfigurationObjectKind.ExchangeOrganizationConfiguration, KnightCapability.ExchangeOrganizationConfig,
            ExchangeOrganizationConfiguration.SchemaVersion, typeof(ExchangeOrganizationConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeTransportConfiguration, KnightCapability.ExchangeTransportConfig,
            ExchangeTransportConfiguration.SchemaVersion, typeof(ExchangeTransportConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeSharingPolicy, KnightCapability.ExchangeSharingPolicies,
            ExchangeSharingPolicyConfiguration.SchemaVersion, typeof(ExchangeSharingPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeOwaMailboxPolicy, KnightCapability.ExchangeOwaMailboxPolicies,
            ExchangeOwaMailboxPolicyConfiguration.SchemaVersion, typeof(ExchangeOwaMailboxPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeTransportRule, KnightCapability.ExchangeTransportRules,
            ExchangeTransportRuleConfiguration.SchemaVersion, typeof(ExchangeTransportRuleConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeRoleAssignmentPolicy, KnightCapability.ExchangeRoleAssignmentPolicies,
            ExchangeRoleAssignmentPolicyConfiguration.SchemaVersion, typeof(ExchangeRoleAssignmentPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeExternalSenderIdentification, KnightCapability.ExchangeExternalSenderIdentification,
            ExchangeExternalSenderIdentification.SchemaVersion, typeof(ExchangeExternalSenderIdentification)),
        new Spec(ConfigurationObjectKind.ExchangeOutboundSpamFilterPolicy, KnightCapability.ExchangeOutboundSpamFilterPolicies,
            ExchangeOutboundSpamPolicyConfiguration.SchemaVersion, typeof(ExchangeOutboundSpamPolicyConfiguration)),
        new Spec(ConfigurationObjectKind.ExchangeSharedMailboxInventory, KnightCapability.ExchangeMailboxes,
            ExchangeSharedMailboxInventory.SchemaVersion, typeof(ExchangeSharedMailboxInventory)),
        new Spec(ConfigurationObjectKind.ExchangeMailboxAuditInventory, KnightCapability.ExchangeMailboxes,
            ExchangeMailboxAuditInventory.SchemaVersion, typeof(ExchangeMailboxAuditInventory)),
        new Spec(ConfigurationObjectKind.ExchangeMailboxForwardingInventory, KnightCapability.ExchangeMailboxes,
            ExchangeMailboxForwardingInventory.SchemaVersion, typeof(ExchangeMailboxForwardingInventory)),
        new Spec(ConfigurationObjectKind.ExchangePolicyReachInventory, KnightCapability.ExchangeMailboxes,
            ExchangePolicyReachInventory.SchemaVersion, typeof(ExchangePolicyReachInventory)),
        new Spec(ConfigurationObjectKind.ExchangeSmtpAuthOverrideInventory, KnightCapability.ExchangeCasMailboxes,
            ExchangeSmtpAuthOverrideInventory.SchemaVersion, typeof(ExchangeSmtpAuthOverrideInventory)),
        new Spec(ConfigurationObjectKind.ExchangeOwaPolicyReachInventory, KnightCapability.ExchangeCasMailboxes,
            ExchangeOwaPolicyReachInventory.SchemaVersion, typeof(ExchangeOwaPolicyReachInventory)),
        new Spec(ConfigurationObjectKind.ExchangeAuditBypassInventory, KnightCapability.ExchangeAuditBypassAssociations,
            ExchangeAuditBypassInventory.SchemaVersion, typeof(ExchangeAuditBypassInventory)),
    };

    public static Spec? For(ConfigurationObjectKind kind) => All.FirstOrDefault(s => s.Kind == kind);

    public static Spec? For(Type contract) => All.FirstOrDefault(s => s.Contract == contract);
}

/// <summary>
/// A configuração de locatário de UMA coleta: documentos tipados + o desfecho das capacidades que os produziram.
/// É o que a avaliação lê — nunca a resposta crua da fonte.
/// </summary>
public sealed class KnightTenantConfiguration
{
    private readonly IReadOnlyList<KnightConfigurationDocument> _documents;
    private readonly IReadOnlyList<KnightCapabilityStatus> _capabilities;

    public KnightTenantConfiguration(
        IEnumerable<KnightConfigurationDocument> documents, IEnumerable<KnightCapabilityStatus> capabilities)
    {
        _documents = (documents ?? Array.Empty<KnightConfigurationDocument>()).ToList();
        _capabilities = (capabilities ?? Array.Empty<KnightCapabilityStatus>()).ToList();
    }

    public static KnightTenantConfiguration Empty { get; } =
        new(Array.Empty<KnightConfigurationDocument>(), Array.Empty<KnightCapabilityStatus>());

    public IReadOnlyList<KnightConfigurationDocument> Documents => _documents;

    /// <summary>
    /// Documentos de um contrato. Coletado só quando a capacidade que o produz foi COLETADA nesta aquisição;
    /// senão a leitura devolve o motivo (falha declarada pela coleta ou aquisição anterior à ampliação).
    /// Documento ilegível ou de versão desconhecida é omitido e declarado — nunca completado por suposição.
    /// </summary>
    public KnightConfigurationRead<T> Read<T>() where T : class
    {
        var spec = KnightConfigurationKinds.For(typeof(T))
            ?? throw new InvalidOperationException($"Contrato de configuração não registrado: {typeof(T).Name}.");

        var capability = _capabilities.FirstOrDefault(c => c.Capability == spec.Capability);
        if (capability is null)
            return new(false, Array.Empty<T>(),
                "Esta configuração não foi coletada nesta aquisição (coleta anterior à ampliação do catálogo ou fonte que não a produz).");
        if (capability.Outcome != KnightCapabilityOutcome.Collected)
            return new(false, Array.Empty<T>(), capability.Detail ?? $"Coleta não concluída ({capability.Outcome}).");

        var items = new List<T>();
        var unreadable = 0;
        foreach (var d in _documents.Where(d => d.Kind == spec.Kind))
        {
            if (!string.Equals(d.SchemaVersion, spec.SchemaVersion, StringComparison.Ordinal)) { unreadable++; continue; }
            try
            {
                if (JsonSerializer.Deserialize<T>(d.Json, IdentityEvidenceFactsJson.Options) is { } item) items.Add(item);
                else unreadable++;
            }
            catch (JsonException) { unreadable++; }
        }

        return unreadable == 0
            ? new(true, items, null)
            : new(false, items, $"{unreadable} documento(s) de configuração ilegível(is) ou de versão desconhecida — a leitura não é completada por suposição.");
    }

    // ---- Tradução para o ADM e de volta --------------------------------------------------------------

    public static KnightConfigurationDocument Document<T>(string externalId, string? displayName, T contract) where T : class
    {
        var spec = KnightConfigurationKinds.For(typeof(T))
            ?? throw new InvalidOperationException($"Contrato de configuração não registrado: {typeof(T).Name}.");
        return new KnightConfigurationDocument(spec.Kind, externalId, displayName, spec.SchemaVersion,
            JsonSerializer.Serialize(contract, IdentityEvidenceFactsJson.Options));
    }

    public IReadOnlyList<IdentityObservedConfiguration> ToObserved() =>
        _documents
            .Where(d => !string.IsNullOrWhiteSpace(d.ExternalId))
            .Select(d => new IdentityObservedConfiguration(d.Kind, d.ExternalId.Trim(),
                d.DisplayName is { Length: > 300 } n ? n[..299] + "…" : d.DisplayName, d.SchemaVersion, d.Json))
            .ToList();

    /// <summary>Reconstrói a configuração a partir dos objetos PERSISTIDOS e das capacidades da mesma aquisição.</summary>
    public static KnightTenantConfiguration FromObserved(
        IEnumerable<IdentityObservedConfiguration> objects, IEnumerable<KnightCapabilityStatus> capabilities)
    {
        var kinds = KnightConfigurationKinds.All.Select(s => s.Kind).ToHashSet();
        var docs = (objects ?? Array.Empty<IdentityObservedConfiguration>())
            .Where(o => kinds.Contains(o.Kind))
            .Select(o => new KnightConfigurationDocument(o.Kind, o.ExternalId, o.DisplayName, o.SchemaVersion, o.ConfigurationJson))
            .ToList();
        return new KnightTenantConfiguration(docs, capabilities);
    }
}
