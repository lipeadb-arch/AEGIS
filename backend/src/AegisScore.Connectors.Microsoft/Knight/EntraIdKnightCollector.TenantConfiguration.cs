using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] Coleta da CONFIGURAÇÃO do locatário (somente leitura)
// ============================================================================
// Cada capacidade lê UM recurso da versão estável (v1.0) do Microsoft Graph, com o MESMO token e o MESMO cliente
// da coleta de identidade, e o normaliza num contrato tipado — sem interpretação. As regras (veredito) ficam no
// catálogo; aqui só se registra o que a fonte devolveu. Falha de uma capacidade (permissão, licença,
// indisponibilidade) não invalida as outras, e os controles dependentes ficam não avaliados com o motivo.
//
// Permissões de aplicativo (Application), todas de LEITURA:
//   • Policy.Read.All                       → autorização, consentimento do administrador, gerenciamento de
//                                             aplicações, métodos de autenticação, locais nomeados;
//   • Directory.Read.All                    → configurações de diretório, domínios, organização, grupos,
//                                             licenças (subscribedSkus) e contas privilegiadas;
//   • OnPremDirectorySynchronization.Read.All → recursos da sincronização híbrida (hash de senha);
//   • Policy.Read.DeviceConfiguration       → política de registro e ingresso de dispositivos;
//   • RoleManagement.Read.Directory         → atribuições ativas e elegíveis de papéis (PIM);
//   • RoleManagementPolicy.Read.Directory   → regras de ativação de papéis (aprovação);
//   • AccessReview.Read.All                 → definições de revisão de acesso;
//   • Application.Read.All                  → estado de aplicações de serviço conhecidas.
// Nenhuma chamada altera configuração do cliente. Nenhuma permissão é concedida pelo AEGIS.

public sealed partial class EntraIdKnightCollector
{
    internal const string AuthorizationPolicyUrl = "policies/authorizationPolicy";
    internal const string AdminConsentPolicyUrl = "policies/adminConsentRequestPolicy";
    internal const string AppManagementPolicyUrl = "policies/defaultAppManagementPolicy";
    internal const string AuthenticationMethodsPolicyUrl = "policies/authenticationMethodsPolicy";
    internal const string GroupSettingsUrl = "groupSettings";
    internal const string GroupSettingTemplatesUrl = "groupSettingTemplates";
    internal const string DomainsUrl = "domains?$select=id,isVerified,isDefault,isInitial,authenticationType,passwordValidityPeriodInDays,supportedServices";
    internal const string OrganizationUrl = "organization?$select=id,onPremisesSyncEnabled,onPremisesLastSyncDateTime";
    internal const string OnPremisesSynchronizationUrl = "directory/onPremisesSynchronization";
    internal const string DeviceRegistrationPolicyUrl = "policies/deviceRegistrationPolicy";
    internal const string UnifiedGroupsUrl = "groups?$filter=groupTypes/any(c:c eq 'Unified')&$select=id,displayName,visibility&$top=999";
    internal const string SubscribedSkusUrl = "subscribedSkus?$select=skuId,skuPartNumber,servicePlans";
    internal const string RoleAssignmentSchedulesUrl = "roleManagement/directory/roleAssignmentSchedules?$select=id,principalId,roleDefinitionId,directoryScopeId,assignmentType,memberType,scheduleInfo";
    internal const string RoleEligibilitySchedulesUrl = "roleManagement/directory/roleEligibilitySchedules?$select=id,principalId,roleDefinitionId,directoryScopeId";
    internal const string AccessReviewDefinitionsUrl = "identityGovernance/accessReviews/definitions?$top=100";
    internal const string NamedLocationsUrl = "identity/conditionalAccess/namedLocations";

    internal static string RoleManagementPolicyUrl(string roleTemplateId) =>
        $"policies/roleManagementPolicyAssignments?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '{roleTemplateId}'&$expand=policy($expand=rules)";

    internal static string ServicePrincipalByAppIdUrl(string appId) =>
        $"servicePrincipals(appId='{appId}')?$select=id,appId,displayName,accountEnabled";

    internal static string UsersByIdUrl(IEnumerable<string> ids) =>
        "users?$filter=id in (" + string.Join(",", ids.Select(i => $"'{i}'")) + ")"
        + "&$select=id,displayName,userPrincipalName,onPremisesSyncEnabled,accountEnabled,assignedLicenses";

    /// <summary>Máximo de identificadores num filtro <c>in</c> (limite documentado de 15 valores).</summary>
    private const int MaxIdsPerFilter = 15;

    /// <summary>
    /// Planos de serviço de PRODUTIVIDADE: e-mail, SharePoint/OneDrive, Teams e aplicativos do Office. O plano
    /// EXCHANGE_S_FOUNDATION existe em licenças sem caixa de correio e não entra.
    /// </summary>
    private static bool IsProductivityPlan(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Equals("EXCHANGE_S_FOUNDATION", StringComparison.OrdinalIgnoreCase)
        && (name.StartsWith("EXCHANGE_S_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SHAREPOINTSTANDARD", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SHAREPOINTENTERPRISE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("TEAMS", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OFFICESUBSCRIPTION", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OFFICE_BUSINESS", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ONEDRIVE", StringComparison.OrdinalIgnoreCase));

    private async Task CollectTenantConfigurationAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator privileged,
        List<KnightObservation> obs, List<KnightCapabilityStatus> caps, List<KnightConfigurationDocument> docs, CancellationToken ct)
    {
        var none = Array.Empty<KnightSignalKey>();

        await RunConfigAsync(KnightCapability.AuthorizationPolicy, docs, async add =>
        {
            var root = Single(await _graph.GetJsonAsync(token, cfg, AuthorizationPolicyUrl, ct));
            var perms = Obj(root, "defaultUserRolePermissions");
            add(KnightTenantConfiguration.Document(EntraAuthorizationPolicyConfiguration.ExternalId, "Política de autorização",
                new EntraAuthorizationPolicyConfiguration(
                    Bool(perms, "allowedToCreateApps"), Bool(perms, "allowedToCreateSecurityGroups"),
                    Bool(perms, "allowedToCreateTenants"), Bool(perms, "allowedToReadBitlockerKeysForOwnedDevice"),
                    Bool(perms, "allowedToReadOtherUsers"), Str(root, "guestUserRoleId"), Str(root, "allowInvitesFrom"),
                    ArrayStrings(perms, "permissionGrantPoliciesAssigned"),
                    Bool(root, "allowEmailVerifiedUsersToJoinOrganization"), Bool(root, "allowUserConsentForRiskyApps"),
                    Bool(root, "blockMsolPowerShell"))));
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.AdminConsentPolicy, docs, async add =>
        {
            var root = await _graph.GetJsonAsync(token, cfg, AdminConsentPolicyUrl, ct);
            var reviewers = Obj(root, "reviewers");
            add(KnightTenantConfiguration.Document(EntraAdminConsentPolicyConfiguration.ExternalId, "Fluxo de consentimento do administrador",
                new EntraAdminConsentPolicyConfiguration(
                    Bool(root, "isEnabled"),
                    reviewers.ValueKind == JsonValueKind.Array ? reviewers.GetArrayLength() : 0,
                    Bool(root, "notifyReviewers"), Bool(root, "remindersEnabled"), Int(root, "requestDurationInDays"))));
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.AppManagementPolicy, docs, async add =>
        {
            var root = await _graph.GetJsonAsync(token, cfg, AppManagementPolicyUrl, ct);
            var restrictions = Obj(root, "applicationRestrictions");
            add(KnightTenantConfiguration.Document(EntraAppManagementPolicyConfiguration.ExternalId, "Política padrão de gerenciamento de aplicações",
                new EntraAppManagementPolicyConfiguration(
                    Bool(root, "isEnabled"),
                    Restrictions(restrictions, "passwordCredentials"),
                    Restrictions(restrictions, "keyCredentials"))));
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.AuthenticationMethodsPolicy, docs, async add =>
        {
            var root = await _graph.GetJsonAsync(token, cfg, AuthenticationMethodsPolicyUrl, ct);
            var methods = new List<EntraAuthenticationMethodState>();
            EntraAuthenticatorFeature? app = null, location = null;
            var configs = Obj(root, "authenticationMethodConfigurations");
            if (configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in configs.EnumerateArray())
                {
                    var id = Str(m, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    methods.Add(new EntraAuthenticationMethodState(id!, Str(m, "state"), TargetIds(m, "includeTargets"), TargetIds(m, "excludeTargets")));
                    if (id!.Equals("MicrosoftAuthenticator", StringComparison.OrdinalIgnoreCase))
                    {
                        var features = Obj(m, "featureSettings");
                        app = Feature(Obj(features, "displayAppInformationRequiredState"));
                        location = Feature(Obj(features, "displayLocationInformationRequiredState"));
                    }
                }
            }
            var campaign = Obj(Obj(root, "registrationEnforcement"), "authenticationMethodsRegistrationCampaign");
            add(KnightTenantConfiguration.Document(EntraAuthenticationMethodsConfiguration.ExternalId, "Política de métodos de autenticação",
                new EntraAuthenticationMethodsConfiguration(Str(root, "policyMigrationState"), Str(campaign, "state"), methods, app, location)));
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.DirectorySettings, docs, async add =>
        {
            var templates = new Dictionary<string, (string? Name, Dictionary<string, string?> Defaults)>(StringComparer.OrdinalIgnoreCase);
            await foreach (var t in _graph.GetPagedAsync(token, cfg, GroupSettingTemplatesUrl, ct))
            {
                var id = Str(t, "id");
                if (id is null) continue;
                var defaults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in Items(t, "values"))
                    if (Str(v, "name") is { } n) defaults[n] = Str(v, "defaultValue");
                templates[id] = (Str(t, "displayName"), defaults);
            }

            var settings = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
            await foreach (var st in _graph.GetPagedAsync(token, cfg, GroupSettingsUrl, ct))
            {
                var templateId = Str(st, "templateId");
                if (templateId is null) continue;
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in Items(st, "values"))
                    if (Str(v, "name") is { } n) values[n] = Str(v, "value");
                settings[templateId] = values;
            }

            // Os modelos que os controles leem: valor EFETIVO = personalizado no locatário, senão o padrão do modelo
            // (e o nome entra em DefaultedNames — o relatório diz que o valor não foi personalizado).
            foreach (var templateId in new[] { EntraDirectorySettingConfiguration.PasswordRuleTemplateId, EntraDirectorySettingConfiguration.GroupUnifiedTemplateId })
            {
                if (!templates.TryGetValue(templateId, out var template) && !settings.ContainsKey(templateId)) continue;
                var custom = settings.TryGetValue(templateId, out var c) ? c : null;
                var effective = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var defaulted = new List<string>();
                foreach (var (name, value) in template.Defaults ?? new Dictionary<string, string?>())
                {
                    if (custom is not null && custom.TryGetValue(name, out var cv)) effective[name] = cv;
                    else { effective[name] = value; defaulted.Add(name); }
                }
                if (custom is not null)
                    foreach (var (name, value) in custom)
                        effective.TryAdd(name, value);
                add(KnightTenantConfiguration.Document(templateId, template.Name,
                    new EntraDirectorySettingConfiguration(templateId, template.Name, custom is not null, effective, defaulted)));
            }
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.Domains, docs, async add =>
        {
            await foreach (var d in _graph.GetPagedAsync(token, cfg, DomainsUrl, ct))
            {
                var id = Str(d, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                add(KnightTenantConfiguration.Document(id!, id,
                    new EntraDomainConfiguration(id!, Bool(d, "isVerified"), Bool(d, "isDefault"), Bool(d, "isInitial"),
                        Str(d, "authenticationType"), Int(d, "passwordValidityPeriodInDays"), ArrayStrings(d, "supportedServices"))));
            }
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.DirectorySynchronization, docs, async add =>
        {
            var org = Single(await _graph.GetJsonAsync(token, cfg, OrganizationUrl, ct));
            // Documentação: onPremisesSyncEnabled é null quando o locatário NUNCA sincronizou, false quando deixou de
            // sincronizar e true quando sincroniza. Os dois primeiros significam "não híbrido hoje".
            var hybrid = Bool(org, "onPremisesSyncEnabled") == true;
            bool? phs = null;
            string? limitation = null;
            if (hybrid)
            {
                try
                {
                    var sync = Single(await _graph.GetJsonAsync(token, cfg, OnPremisesSynchronizationUrl, ct));
                    phs = Bool(Obj(sync, "features"), "passwordSyncEnabled");
                    if (phs is null) limitation = "A fonte não informou o estado da sincronização de hash de senha.";
                }
                catch (EntraGraphException ex) when (ex.Kind is EntraGraphErrorKind.InsufficientPermission)
                {
                    limitation = "Sem permissão para ler os recursos da sincronização híbrida: conceda OnPremDirectorySynchronization.Read.All.";
                }
            }
            add(KnightTenantConfiguration.Document(EntraDirectorySynchronizationConfiguration.ExternalId, "Sincronização com o diretório local",
                new EntraDirectorySynchronizationConfiguration(hybrid, Date(org, "onPremisesLastSyncDateTime"), phs, limitation)));
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.DeviceRegistrationPolicy, docs, async add =>
        {
            var root = await _graph.GetJsonAsync(token, cfg, DeviceRegistrationPolicyUrl, ct);
            var join = Obj(root, "azureADJoin");
            var allowed = Obj(join, "allowedToJoin");
            var admins = Obj(join, "localAdmins");
            var registering = Obj(admins, "registeringUsers");
            add(KnightTenantConfiguration.Document(EntraDeviceRegistrationConfiguration.ExternalId, "Política de registro de dispositivos",
                new EntraDeviceRegistrationConfiguration(
                    Int(root, "userDeviceQuota"), Str(root, "multiFactorAuthConfiguration"),
                    Membership(allowed), ArrayStrings(allowed, "users").Count, ArrayStrings(allowed, "groups").Count,
                    Bool(admins, "enableGlobalAdmins"), Membership(registering),
                    ArrayStrings(registering, "users").Count, ArrayStrings(registering, "groups").Count,
                    Bool(Obj(root, "localAdminPassword"), "isEnabled"),
                    Membership(Obj(Obj(root, "azureADRegistration"), "allowedToRegister")))));
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.GroupVisibility, docs, async add =>
        {
            var total = 0;
            var publicTotal = 0;
            var listed = new List<EntraGroupReference>();
            await foreach (var g in _graph.GetPagedAsync(token, cfg, UnifiedGroupsUrl, ct))
            {
                total++;
                if (!string.Equals(Str(g, "visibility"), "Public", StringComparison.OrdinalIgnoreCase)) continue;
                publicTotal++;
                if (listed.Count < EntraGroupVisibilityInventory.MaxListed && Str(g, "id") is { } id)
                    listed.Add(new EntraGroupReference(id, Str(g, "displayName")));
            }
            add(KnightTenantConfiguration.Document(EntraGroupVisibilityInventory.ExternalId, "Visibilidade dos grupos do Microsoft 365",
                new EntraGroupVisibilityInventory(total, publicTotal, listed, publicTotal <= EntraGroupVisibilityInventory.MaxListed)));
        }, obs, caps, none);

        // Perfil das contas privilegiadas: depende do inventário de papéis da MESMA coleta.
        if (!privileged.Collected)
        {
            caps.Add(new KnightCapabilityStatus(KnightCapability.PrivilegedAccountDetails, KnightCapabilityOutcome.NotAttempted,
                "Depende do inventário de papéis privilegiados, que não foi coletado nesta execução."));
        }
        else
        {
            await RunConfigAsync(KnightCapability.PrivilegedAccountDetails, docs, async add =>
            {
                var skus = new Dictionary<string, (string? PartNumber, List<(string Id, string? Name)> Plans)>(StringComparer.OrdinalIgnoreCase);
                await foreach (var sku in _graph.GetPagedAsync(token, cfg, SubscribedSkusUrl, ct))
                {
                    if (Str(sku, "skuId") is not { } skuId) continue;
                    var plans = Items(sku, "servicePlans")
                        .Where(p => !string.Equals(Str(p, "appliesTo"), "Company", StringComparison.OrdinalIgnoreCase))
                        .Select(p => (Id: Str(p, "servicePlanId") ?? "", Name: Str(p, "servicePlanName"))).ToList();
                    skus[skuId] = (Str(sku, "skuPartNumber"), plans);
                }

                foreach (var batch in privileged.PrivilegedUsers.Chunk(MaxIdsPerFilter))
                {
                    var byId = batch.ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
                    await foreach (var u in _graph.GetPagedAsync(token, cfg, UsersByIdUrl(batch.Select(b => b.Id)), ct))
                    {
                        if (Str(u, "id") is not { } id || !byId.TryGetValue(id, out var member)) continue;
                        var parts = new List<string>();
                        var productivity = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        var resolved = true;
                        foreach (var lic in Items(u, "assignedLicenses"))
                        {
                            if (Str(lic, "skuId") is not { } skuId || !skus.TryGetValue(skuId, out var sku)) { resolved = false; continue; }
                            if (sku.PartNumber is { } pn) parts.Add(pn);
                            var disabled = ArrayStrings(lic, "disabledPlans").ToHashSet(StringComparer.OrdinalIgnoreCase);
                            foreach (var plan in sku.Plans.Where(p => !disabled.Contains(p.Id) && IsProductivityPlan(p.Name)))
                                productivity.Add(plan.Name!);
                        }
                        add(KnightTenantConfiguration.Document(id, Str(u, "displayName") ?? member.DisplayName,
                            new EntraPrivilegedAccountProfile(id, Str(u, "displayName") ?? member.DisplayName,
                                Str(u, "userPrincipalName") ?? member.UserPrincipalName, Array.Empty<string>(), member.Roles,
                                // null = conta que nunca foi sincronizada (somente em nuvem), pela documentação do recurso user.
                                Bool(u, "onPremisesSyncEnabled") == true,
                                Bool(u, "accountEnabled"), parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), productivity.ToList(), resolved)));
                    }
                }
            }, obs, caps, none);
        }

        await RunConfigAsync(KnightCapability.PrivilegedIdentityManagement, docs, async add =>
        {
            var active = new Dictionary<string, List<EntraRoleAssignment>>(StringComparer.OrdinalIgnoreCase);
            await foreach (var a in _graph.GetPagedAsync(token, cfg, RoleAssignmentSchedulesUrl, ct))
            {
                var role = Str(a, "roleDefinitionId");
                var principal = Str(a, "principalId");
                if (role is null || principal is null || !string.Equals(Str(a, "directoryScopeId") ?? "/", "/", StringComparison.Ordinal)) continue;
                var expiration = Obj(Obj(a, "scheduleInfo"), "expiration");
                var end = Date(expiration, "endDateTime");
                var permanent = string.Equals(Str(a, "assignmentType"), "Assigned", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(Str(expiration, "type"), "noExpiration", StringComparison.OrdinalIgnoreCase) || (expiration.ValueKind == JsonValueKind.Object && end is null && Str(expiration, "type") is null));
                var kind = privileged.MemberKinds.TryGetValue(principal, out var k) ? k : KnightAffectedObjectKind.Unknown;
                var type = kind switch
                {
                    KnightAffectedObjectKind.User or KnightAffectedObjectKind.Guest => "User",
                    KnightAffectedObjectKind.ServicePrincipal => "ServicePrincipal",
                    KnightAffectedObjectKind.Group => "Group",
                    _ => null,
                };
                if (!active.TryGetValue(role, out var list)) active[role] = list = new();
                list.Add(new EntraRoleAssignment(principal, type, permanent, end));
            }

            var eligible = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await foreach (var e in _graph.GetPagedAsync(token, cfg, RoleEligibilitySchedulesUrl, ct))
                if (Str(e, "roleDefinitionId") is { } role)
                    eligible[role] = eligible.TryGetValue(role, out var n) ? n + 1 : 1;

            foreach (var (templateId, name) in EntraConfigurationControls.PrivilegedRoleTemplates)
            {
                bool? approval = null;
                var approvers = 0;
                bool? mfa = null;
                string? limitation = null;
                if (templateId.Equals(EntraConfigurationControls.GlobalAdministratorTemplateId, StringComparison.OrdinalIgnoreCase)
                    || templateId.Equals(EntraConfigurationControls.PrivilegedRoleAdministratorTemplateId, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        (approval, approvers, mfa) = await ReadActivationRulesAsync(token, cfg, templateId, ct);
                        if (approval is null) limitation = "A regra de ativação do papel não foi devolvida pela fonte.";
                    }
                    catch (EntraGraphException ex) when (ex.Kind is EntraGraphErrorKind.InsufficientPermission)
                    {
                        limitation = MentionsLicense(ex.GraphErrorCode)
                            ? "O locatário não tem a licença que as regras de ativação do PIM exigem."
                            : "Sem permissão para ler as regras de ativação: conceda RoleManagementPolicy.Read.Directory.";
                    }
                }
                else
                {
                    limitation = "Regra de ativação não lida: nenhum controle do catálogo a avalia para este papel.";
                }

                add(KnightTenantConfiguration.Document(templateId, name,
                    new EntraPrivilegedRoleGovernance(templateId, name, IsPrivileged: true,
                        active.TryGetValue(templateId, out var assignments) ? assignments : new List<EntraRoleAssignment>(),
                        eligible.TryGetValue(templateId, out var count) ? count : 0,
                        approval, approvers, mfa, limitation)));
            }
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.AccessReviews, docs, async add =>
        {
            await foreach (var d in _graph.GetPagedAsync(token, cfg, AccessReviewDefinitionsUrl, ct))
            {
                var id = Str(d, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var queries = ScopeQueries(Obj(d, "scope"), EntraAccessReviewScopeQuery.OriginScope)
                    .Concat(ScopeQueries(Obj(d, "instanceEnumerationScope"), EntraAccessReviewScopeQuery.OriginInstanceEnumeration))
                    .ToList();
                var roles = queries
                    .Where(q => q.Origin != EntraAccessReviewScopeQuery.OriginPrincipal)
                    .SelectMany(q => RoleIdPattern().Matches(q.Query).Select(m => m.Groups[1].Value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var settings = Obj(d, "settings");
                var recurrence = Obj(settings, "recurrence");
                var pattern = Obj(recurrence, "pattern");
                var range = Obj(recurrence, "range");
                var applyActions = Items(settings, "applyActions").ToList();
                var stages = Items(d, "stageSettings").Select(s => new EntraAccessReviewStage(
                    Str(s, "stageId"), Items(s, "reviewers").Count(), Items(s, "fallbackReviewers").Count(),
                    Int(s, "durationInDays"), ArrayStrings(s, "dependsOn"))).ToList();
                add(KnightTenantConfiguration.Document(id!, Str(d, "displayName"),
                    new EntraAccessReviewDefinition(
                        id!, Str(d, "displayName"), Str(d, "status"),
                        EntraAccessReviewCoverage.ClassifyKind(queries, roles),
                        queries, roles,
                        pattern.ValueKind == JsonValueKind.Object || range.ValueKind == JsonValueKind.Object
                            ? new EntraAccessReviewRecurrence(Str(pattern, "type"), Int(pattern, "interval"), Int(pattern, "dayOfMonth"),
                                Str(range, "type"), DayOnly(range, "startDate"), DayOnly(range, "endDate"), Int(range, "numberOfOccurrences"))
                            : null,
                        Items(d, "reviewers").Count(), stages, Int(settings, "instanceDurationInDays"),
                        Bool(settings, "autoApplyDecisionsEnabled"),
                        applyActions.Any(a => string.Equals(Str(a, "@odata.type"), "#microsoft.graph.removeAccessApplyAction", StringComparison.OrdinalIgnoreCase)),
                        Bool(settings, "justificationRequiredOnApproval"), Bool(settings, "mailNotificationsEnabled"))));
            }
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.NamedLocations, docs, async add =>
        {
            await foreach (var l in _graph.GetPagedAsync(token, cfg, NamedLocationsUrl, ct))
            {
                var id = Str(l, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var isCountry = (Str(l, "@odata.type") ?? "").Contains("countryNamedLocation", StringComparison.OrdinalIgnoreCase);
                add(KnightTenantConfiguration.Document(id!, Str(l, "displayName"),
                    new EntraNamedLocationConfiguration(id!, Str(l, "displayName"), isCountry ? "country" : "ip",
                        isCountry ? false : Bool(l, "isTrusted"), Items(l, "ipRanges").Count(),
                        ArrayStrings(l, "countriesAndRegions").Count, Bool(l, "includeUnknownCountriesAndRegions"))));
            }
        }, obs, caps, none);

        await RunConfigAsync(KnightCapability.ServicePrincipalSettings, docs, async add =>
        {
            var appId = EntraServicePrincipalState.ThirdPartyStorageAppId;
            try
            {
                var sp = await _graph.GetJsonAsync(token, cfg, ServicePrincipalByAppIdUrl(appId), ct);
                add(KnightTenantConfiguration.Document(appId, Str(sp, "displayName"),
                    new EntraServicePrincipalState(appId, Str(sp, "displayName"), Present: true, Bool(sp, "accountEnabled"))));
            }
            catch (EntraGraphException ex) when (ex.HttpStatusCode == 404)
            {
                // Documentação do Microsoft 365: sem a aplicação de serviço no locatário, o armazenamento de terceiros
                // segue o padrão (permitido). A ausência é um fato observado, não uma falha de coleta.
                add(KnightTenantConfiguration.Document(appId, null,
                    new EntraServicePrincipalState(appId, null, Present: false, AccountEnabled: null)));
            }
        }, obs, caps, none);
    }

    private async Task<(bool? Approval, int Approvers, bool? Mfa)> ReadActivationRulesAsync(
        string token, KnightEntraIdConfiguration cfg, string roleTemplateId, CancellationToken ct)
    {
        bool? approval = null;
        var approvers = 0;
        bool? mfa = null;
        await foreach (var assignment in _graph.GetPagedAsync(token, cfg, RoleManagementPolicyUrl(roleTemplateId), ct))
        {
            foreach (var rule in Items(Obj(assignment, "policy"), "rules"))
            {
                var id = Str(rule, "id");
                if (string.Equals(id, "Approval_EndUser_Assignment", StringComparison.OrdinalIgnoreCase))
                {
                    var setting = Obj(rule, "setting");
                    approval = Bool(setting, "isApprovalRequired");
                    approvers = Items(setting, "approvalStages")
                        .SelectMany(s => Items(s, "primaryApprovers")).Count();
                }
                else if (string.Equals(id, "Enablement_EndUser_Assignment", StringComparison.OrdinalIgnoreCase))
                {
                    mfa = ArrayStrings(rule, "enabledRules").Contains("MultiFactorAuthentication", StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        return (approval, approvers, mfa);
    }

    /// <summary>Executa UMA capacidade de configuração: só publica os documentos quando ela conclui.</summary>
    private Task RunConfigAsync(
        KnightCapability capability, List<KnightConfigurationDocument> docs,
        Func<Action<KnightConfigurationDocument>, Task> collect,
        List<KnightObservation> obs, List<KnightCapabilityStatus> caps, KnightSignalKey[] keys)
    {
        var pending = new List<KnightConfigurationDocument>();
        return RunCapabilityAsync(capability, keys, async () =>
        {
            await collect(pending.Add);
            docs.AddRange(pending);
        }, obs, caps);
    }

    // ---- Normalização --------------------------------------------------------------------------------

    /// <summary>Recurso único devolvido às vezes como coleção (<c>value</c>): o primeiro elemento.</summary>
    private static JsonElement Single(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().FirstOrDefault()
            : root;

    private static IEnumerable<JsonElement> Items(JsonElement parent, string prop) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    private static IReadOnlyList<EntraAppCredentialRestriction> Restrictions(JsonElement parent, string prop) =>
        Items(parent, prop)
            .Where(r => Str(r, "restrictionType") is not null)
            .Select(r => new EntraAppCredentialRestriction(Str(r, "restrictionType")!, Str(r, "state"), Str(r, "maxLifetime"),
                Date(r, "restrictForAppsCreatedAfterDateTime")))
            .ToList();

    private static IReadOnlyList<string> TargetIds(JsonElement method, string prop) =>
        Items(method, prop).Select(t => Str(t, "id")).Where(i => i is not null).Select(i => i!).ToList();

    private static EntraAuthenticatorFeature? Feature(JsonElement f) =>
        f.ValueKind != JsonValueKind.Object ? null
            : new EntraAuthenticatorFeature(Str(f, "state"), Str(Obj(f, "includeTarget"), "id"), Str(Obj(f, "excludeTarget"), "id"));

    /// <summary>Tipo de pertencimento do registro de dispositivos: todos, selecionados ou ninguém.</summary>
    private static string? Membership(JsonElement m)
    {
        var type = Str(m, "@odata.type");
        if (type is null) return null;
        if (type.Contains("allDeviceRegistrationMembership", StringComparison.OrdinalIgnoreCase)) return "all";
        if (type.Contains("enumeratedDeviceRegistrationMembership", StringComparison.OrdinalIgnoreCase)) return "selected";
        if (type.Contains("noDeviceRegistrationMembership", StringComparison.OrdinalIgnoreCase)) return "none";
        return type;
    }

    /// <summary>
    /// Consultas de escopo de uma revisão de acesso, com a ORIGEM preservada: escopo simples, escopo por recurso e
    /// principal (principalResourceMembershipsScope) e enumeração de instâncias. Sem a origem não há como separar
    /// "quem é revisado" de "o que é revisado" — nem como saber quais recursos a série enumera.
    /// </summary>
    private static IEnumerable<EntraAccessReviewScopeQuery> ScopeQueries(JsonElement scope, string origin)
    {
        if (scope.ValueKind != JsonValueKind.Object) yield break;
        if (Str(scope, "query") is { } q)
            yield return new EntraAccessReviewScopeQuery(origin, q, Str(scope, "queryType"), Str(scope, "queryRoot"),
                Str(scope, "@odata.type"), Str(scope, "inactiveDuration"));
        foreach (var nested in Items(scope, "principalScopes"))
            foreach (var n in ScopeQueries(nested, EntraAccessReviewScopeQuery.OriginPrincipal))
                yield return n;
        foreach (var nested in Items(scope, "resourceScopes"))
            foreach (var n in ScopeQueries(nested, EntraAccessReviewScopeQuery.OriginResource))
                yield return n;
    }

    /// <summary>Data (sem hora) devolvida pela fonte; valor ausente ou ilegível permanece nulo.</summary>
    private static DateOnly? DayOnly(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
        && DateOnly.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    [GeneratedRegex(@"roleDefinitions/([0-9a-fA-F-]{36})", RegexOptions.CultureInvariant)]
    private static partial Regex RoleIdPattern();
}
