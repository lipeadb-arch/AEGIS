using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Locatário Microsoft Entra ID SINTÉTICO (sem rede, sem dado real) com respostas no
/// formato documentado da versão estável do Microsoft Graph para TODOS os recursos lidos pelo coletor: identidade
/// (papéis, registro de MFA, convidados, aplicações) e configuração do locatário (autorização, consentimento,
/// métodos de autenticação, regras de senha, domínios, sincronização, dispositivos, grupos, licenças, PIM, revisões
/// de acesso, locais nomeados, acesso condicional). Duas variantes: <see cref="Variant.Compliant"/> — tudo como a
/// referência pede — e <see cref="Variant.NonCompliant"/> — cada critério violado de forma comprovável. Falhas
/// (permissão, licença) são injetadas por URL.
/// </summary>
internal sealed class EntraConfigurationScenario
{
    internal enum Variant { Compliant, NonCompliant }

    internal const string Ga = EntraConfigurationControls.GlobalAdministratorTemplateId;
    internal const string Pra = EntraConfigurationControls.PrivilegedRoleAdministratorTemplateId;
    internal const string Sa = "194ae4cb-b126-40b2-bd5b-6091b380977d";
    internal const string TenantCreator = EntraConfigurationControls.TenantCreatorTemplateId;
    internal const string P2Sku = "eec0eb4f-6444-4f95-aba0-50c24d67f998";
    internal const string E3Sku = "6fd2c87f-b296-42f0-b197-1e91e994b900";
    internal const string CountryLocation = "loc-paises";
    internal const string TrustedLocation = "loc-escritorio";

    private readonly Variant _v;
    private readonly Func<string, (HttpStatusCode, string)?>? _override;

    internal EntraConfigurationScenario(Variant variant, Func<string, (HttpStatusCode, string)?>? failures = null)
    {
        _v = variant;
        _override = failures;
    }

    private bool Ok => _v == Variant.Compliant;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static string Iso(DateTimeOffset d) => d.ToString("o");

    internal static (HttpStatusCode, string) Forbidden(string code = "Authorization_RequestDenied") =>
        (HttpStatusCode.Forbidden, JsonSerializer.Serialize(new { error = new { code, message = "x" } }));

    internal HttpMessageHandler Handler() => new StubHandler(this);

    /// <summary>Usuários de papéis: GA = u1, u2 (conforme) ou u1, u2, u4, u5, u6 (não conforme); SA = u3 e uma aplicação.</summary>
    private IEnumerable<(string Id, string Name)> GaMembers() => Ok
        ? new[] { ("u1", "Ana Prado"), ("u2", "Bruno Costa") }
        : new[] { ("u1", "Ana Prado"), ("u2", "Bruno Costa"), ("u4", "Diego Lima"), ("u5", "Elisa Rocha"), ("u6", "Fábio Nunes") };

    internal (HttpStatusCode, string) Respond(HttpRequestMessage req)
    {
        var url = Uri.UnescapeDataString(req.RequestUri!.AbsoluteUri);
        if (req.Method == HttpMethod.Post && url.Contains("/oauth2/v2.0/token"))
            return (HttpStatusCode.OK, """{"access_token":"t","expires_in":3600,"token_type":"Bearer"}""");
        if (_override?.Invoke(url) is { } forced) return forced;
        return Identity(url) ?? Configuration(url) ?? (HttpStatusCode.NotFound, "{}");
    }

    // ---- Identidade -----------------------------------------------------------------------------------

    private (HttpStatusCode, string)? Identity(string url)
    {
        if (url.Contains("identityProtection")) return Forbidden();
        if (url.Contains("/directoryRoles/r-ga/members"))
            return Json(new { value = GaMembers().Select(m => User(m.Id, m.Name)).ToArray() });
        if (url.Contains("/directoryRoles/r-sa/members"))
            return Json(new
            {
                value = new object[]
                {
                    User("u3", "Carla Dias"),
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.servicePrincipal", ["id"] = "sp-auto", ["displayName"] = "Automação de chamados" },
                },
            });
        if (url.Contains("/directoryRoles?"))
            return Json(new
            {
                value = new[]
                {
                    new { id = "r-ga", displayName = "Global Administrator", roleTemplateId = Ga },
                    new { id = "r-sa", displayName = "Security Administrator", roleTemplateId = Sa },
                },
            });
        if (url.Contains("userRegistrationDetails"))
        {
            var ids = new[] { "u1", "u2", "u3", "u4", "u5", "u6" };
            return Json(new { value = ids.Select((id, i) => new { id, isMfaCapable = Ok || i % 2 == 0, isMfaRegistered = Ok || i % 2 == 0 }).ToArray() });
        }
        if (url.Contains("/users") && url.Contains("Guest"))
            return Json(new
            {
                value = Ok ? Array.Empty<object>() : new object[]
                {
                    new { id = "g1", displayName = "Parceiro Externo", userPrincipalName = "parceiro_example.com#EXT#@demo.example.com", signInActivity = new { lastSignInDateTime = Iso(Now.AddDays(-200)) } },
                },
            });
        if (url.Contains("conditionalAccess/policies")) return (HttpStatusCode.OK, Ok ? CompliantPolicies() : NonCompliantPolicies());
        if (url.Contains("identitySecurityDefaultsEnforcementPolicy")) return Json(new { isEnabled = false });
        if (url.Contains("appRoleAssignedTo")) return Json(new { value = Array.Empty<object>() });
        if (url.Contains("oauth2PermissionGrants")) return Json(new { value = Array.Empty<object>() });
        if (url.Contains("/applications?"))
        {
            var start = Now.AddDays(-10);
            var end = Ok ? Now.AddDays(80) : Now.AddDays(720);
            return Json(new
            {
                value = new[]
                {
                    new
                    {
                        id = "app-erp", displayName = "Integração ERP",
                        passwordCredentials = Array.Empty<object>(),
                        keyCredentials = new[] { new { startDateTime = Iso(start), endDateTime = Iso(end) } },
                    },
                },
            });
        }
        return null;
    }

    private static Dictionary<string, object?> User(string id, string name) => new()
    {
        ["@odata.type"] = "#microsoft.graph.user",
        ["id"] = id,
        ["userType"] = "Member",
        ["displayName"] = name,
        ["userPrincipalName"] = id + "@demo.example.com",
        ["signInActivity"] = new { lastSignInDateTime = Iso(Now.AddDays(-2)) },
    };

    // ---- Configuração do locatário ---------------------------------------------------------------------

    /// <summary>Só os recursos de configuração do locatário (para compor com outros cenários de identidade).</summary>
    internal static (HttpStatusCode, string)? ConfigurationOnly(HttpRequestMessage req, Variant variant = Variant.Compliant) =>
        new EntraConfigurationScenario(variant).Configuration(Uri.UnescapeDataString(req.RequestUri!.AbsoluteUri));

    private (HttpStatusCode, string)? Configuration(string url)
    {
        if (url.Contains("/policies/authorizationPolicy"))
            return Json(new
            {
                id = "authorizationPolicy",
                allowInvitesFrom = Ok ? "adminsAndGuestInviters" : "everyone",
                guestUserRoleId = Ok ? "2af84b1e-32c8-42b7-82bc-daa82404023b" : "a0b1b346-4d3e-4e8b-98f8-753987be4970",
                allowEmailVerifiedUsersToJoinOrganization = false,
                blockMsolPowerShell = true,
                defaultUserRolePermissions = new
                {
                    allowedToCreateApps = !Ok,
                    allowedToCreateSecurityGroups = !Ok,
                    allowedToCreateTenants = !Ok,
                    allowedToReadBitlockerKeysForOwnedDevice = !Ok,
                    allowedToReadOtherUsers = true,
                    permissionGrantPoliciesAssigned = Ok
                        ? new[] { "ManagePermissionGrantsForOwnedResource.microsoft-dynamically-managed-permissions-for-team" }
                        : new[] { "ManagePermissionGrantsForSelf.microsoft-user-default-legacy" },
                },
            });
        if (url.Contains("/policies/adminConsentRequestPolicy"))
            return Json(new
            {
                isEnabled = Ok, notifyReviewers = true, remindersEnabled = true, requestDurationInDays = 30,
                reviewers = Ok ? new object[] { new { query = "/users/u1", queryType = "MicrosoftGraph" } } : Array.Empty<object>(),
            });
        if (url.Contains("/policies/defaultAppManagementPolicy"))
            return Json(new
            {
                id = "00000000-0000-0000-0000-000000000000", isEnabled = Ok,
                applicationRestrictions = new
                {
                    passwordCredentials = Ok
                        ? new object[]
                        {
                            new { restrictionType = "passwordAddition", state = "enabled", maxLifetime = (string?)null },
                            new { restrictionType = "passwordLifetime", state = "enabled", maxLifetime = "P90D" },
                            new { restrictionType = "customPasswordAddition", state = "enabled", maxLifetime = (string?)null },
                        }
                        : new object[] { new { restrictionType = "passwordLifetime", state = "enabled", maxLifetime = "P365D" } },
                    keyCredentials = Array.Empty<object>(),
                },
            });
        if (url.Contains("/policies/authenticationMethodsPolicy"))
            return (HttpStatusCode.OK, AuthenticationMethods());
        if (url.Contains("/groupSettingTemplates"))
            return Json(new
            {
                value = new object[]
                {
                    Template(EntraDirectorySettingConfiguration.PasswordRuleTemplateId, "Password Rule Settings", new()
                    {
                        ["BannedPasswordCheckOnPremisesMode"] = "Audit", ["EnableBannedPasswordCheckOnPremises"] = "True",
                        ["EnableBannedPasswordCheck"] = "True", ["LockoutDurationInSeconds"] = "60", ["LockoutThreshold"] = "10",
                        ["BannedPasswordList"] = "",
                    }),
                    Template(EntraDirectorySettingConfiguration.GroupUnifiedTemplateId, "Group.Unified", new()
                    {
                        ["EnableGroupCreation"] = "true", ["GroupCreationAllowedGroupId"] = "", ["AllowGuestsToAccessGroups"] = "true",
                    }),
                },
            });
        if (url.Contains("/groupSettings"))
            return Json(new
            {
                value = Ok
                    ? new object[]
                    {
                        Setting(EntraDirectorySettingConfiguration.PasswordRuleTemplateId, new()
                        {
                            ["BannedPasswordList"] = "demo\texemplo\tsenha2026", ["BannedPasswordCheckOnPremisesMode"] = "Enforce",
                        }),
                        Setting(EntraDirectorySettingConfiguration.GroupUnifiedTemplateId, new() { ["EnableGroupCreation"] = "false" }),
                    }
                    : new object[]
                    {
                        Setting(EntraDirectorySettingConfiguration.PasswordRuleTemplateId, new()
                        {
                            ["LockoutThreshold"] = "20", ["LockoutDurationInSeconds"] = "30",
                        }),
                    },
            });
        if (url.Contains("/domains?"))
            return Json(new
            {
                value = new object[]
                {
                    new { id = "demo.example.com", isVerified = true, isDefault = true, isInitial = false, authenticationType = "Managed",
                          passwordValidityPeriodInDays = Ok ? 2147483647 : 90, supportedServices = new[] { "Email" } },
                    new { id = "demo.onmicrosoft.example.com", isVerified = true, isDefault = false, isInitial = true, authenticationType = "Managed",
                          passwordValidityPeriodInDays = 2147483647, supportedServices = new[] { "Email" } },
                },
            });
        if (url.Contains("/organization?"))
            return Json(new { value = new[] { new { id = "org", onPremisesSyncEnabled = true, onPremisesLastSyncDateTime = Iso(Now.AddHours(-1)) } } });
        if (url.Contains("/directory/onPremisesSynchronization"))
            return Json(new { value = new[] { new { id = "sync", features = new { passwordSyncEnabled = Ok } } } });
        if (url.Contains("/policies/deviceRegistrationPolicy"))
            return Json(new
            {
                id = "deviceRegistrationPolicy", userDeviceQuota = Ok ? 10 : 50,
                multiFactorAuthConfiguration = Ok ? "required" : "notRequired",
                azureADRegistration = new { isAdminConfigurable = false, allowedToRegister = new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.allDeviceRegistrationMembership" } },
                azureADJoin = new
                {
                    isAdminConfigurable = true,
                    allowedToJoin = Ok
                        ? new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.enumeratedDeviceRegistrationMembership", ["users"] = new[] { "u1" }, ["groups"] = Array.Empty<string>() }
                        : new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.allDeviceRegistrationMembership" },
                    localAdmins = new
                    {
                        enableGlobalAdmins = !Ok,
                        registeringUsers = Ok
                            ? new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.noDeviceRegistrationMembership" }
                            : new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.allDeviceRegistrationMembership" },
                    },
                },
                localAdminPassword = new { isEnabled = Ok },
            });
        if (url.Contains("/groups?") && url.Contains("groupTypes"))
            return Json(new
            {
                value = new[]
                {
                    new { id = "grp-fin", displayName = "Financeiro", visibility = "Private" },
                    new { id = "grp-mkt", displayName = "Marketing", visibility = Ok ? "Private" : "Public" },
                },
            });
        if (url.Contains("/subscribedSkus"))
            return Json(new
            {
                value = new object[]
                {
                    new { skuId = P2Sku, skuPartNumber = "AAD_PREMIUM_P2", servicePlans = new[]
                    {
                        new { servicePlanId = "p2-plan", servicePlanName = "AAD_PREMIUM_P2", appliesTo = "User" },
                        new { servicePlanId = "exo-foundation", servicePlanName = "EXCHANGE_S_FOUNDATION", appliesTo = "Company" },
                    } },
                    new { skuId = E3Sku, skuPartNumber = "ENTERPRISEPACK", servicePlans = new[]
                    {
                        new { servicePlanId = "exo", servicePlanName = "EXCHANGE_S_ENTERPRISE", appliesTo = "User" },
                        new { servicePlanId = "teams", servicePlanName = "TEAMS1", appliesTo = "User" },
                        new { servicePlanId = "spo", servicePlanName = "SHAREPOINTENTERPRISE", appliesTo = "User" },
                    } },
                },
            });
        if (url.Contains("/users?") && url.Contains("id in ("))
        {
            var inner = url[(url.IndexOf("id in (", StringComparison.Ordinal) + 7)..];
            inner = inner[..inner.IndexOf(')')];
            var ids = inner.Split(',').Select(x => x.Trim().Trim('\'')).ToList();
            return Json(new
            {
                value = ids.Select(id => new Dictionary<string, object?>
                {
                    ["id"] = id, ["displayName"] = "Pessoa " + id, ["userPrincipalName"] = id + "@demo.example.com", ["accountEnabled"] = true,
                    ["onPremisesSyncEnabled"] = !Ok && id == "u1" ? true : null,
                    ["assignedLicenses"] = new[] { new { skuId = Ok ? P2Sku : E3Sku, disabledPlans = Array.Empty<string>() } },
                }).ToArray(),
            });
        }
        if (url.Contains("roleAssignmentSchedules"))
            return Json(new
            {
                value = Ok
                    ? new object[]
                    {
                        Assignment("u1", Ga, "Activated", "afterDateTime", Now.AddHours(4)),
                        Assignment("u2", Ga, "Assigned", "afterDateTime", Now.AddDays(30)),
                        Assignment("u3", Sa, "Activated", "afterDateTime", Now.AddHours(2)),
                    }
                    : new object[]
                    {
                        Assignment("u1", Ga, "Assigned", "noExpiration", null),
                        Assignment("sp-auto", Sa, "Assigned", "noExpiration", null),
                    },
            });
        if (url.Contains("roleEligibilitySchedules"))
            return Json(new { value = new[] { new { id = "e1", principalId = "u1", roleDefinitionId = Ga, directoryScopeId = "/" } } });
        if (url.Contains("roleManagementPolicyAssignments"))
            return Json(new
            {
                value = new[]
                {
                    new
                    {
                        policyId = "pol", roleDefinitionId = url.Contains(Ga) ? Ga : Pra,
                        policy = new
                        {
                            rules = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule",
                                    ["id"] = "Approval_EndUser_Assignment",
                                    ["setting"] = new
                                    {
                                        isApprovalRequired = Ok,
                                        approvalStages = new[] { new { primaryApprovers = Ok ? new object[] { new { userId = "u1" }, new { userId = "u2" } } : Array.Empty<object>() } },
                                    },
                                },
                                new Dictionary<string, object?>
                                {
                                    ["@odata.type"] = "#microsoft.graph.unifiedRoleManagementPolicyEnablementRule",
                                    ["id"] = "Enablement_EndUser_Assignment",
                                    ["enabledRules"] = new[] { "MultiFactorAuthentication", "Justification" },
                                },
                            },
                        },
                    },
                },
            });
        if (url.Contains("accessReviews/definitions"))
            return Json(new { value = Ok ? CompliantReviews() : Array.Empty<object>() });
        if (url.Contains("namedLocations"))
            return Json(new
            {
                value = new object[]
                {
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["id"] = TrustedLocation, ["displayName"] = "Escritório", ["isTrusted"] = Ok, ["ipRanges"] = new[] { new { cidrAddress = "192.0.2.0/24" } } },
                    new Dictionary<string, object?> { ["@odata.type"] = "#microsoft.graph.countryNamedLocation", ["id"] = CountryLocation, ["displayName"] = "Países sem operação", ["countriesAndRegions"] = new[] { "KP", "IR" }, ["includeUnknownCountriesAndRegions"] = false },
                },
            });
        if (url.Contains("servicePrincipals(appId='" + EntraServicePrincipalState.ThirdPartyStorageAppId))
            return Ok
                ? Json(new { id = "sp-3p", appId = EntraServicePrincipalState.ThirdPartyStorageAppId, displayName = "Armazenamento de terceiros", accountEnabled = false })
                : (HttpStatusCode.NotFound, JsonSerializer.Serialize(new { error = new { code = "Request_ResourceNotFound", message = "x" } }));
        if (url.Contains("servicePrincipals(appId=")) return Json(new { id = "graph-sp" });
        return null;
    }

    private static object Template(string id, string name, Dictionary<string, string> defaults) => new
    {
        id, displayName = name,
        values = defaults.Select(kv => new { name = kv.Key, type = "System.String", defaultValue = kv.Value }).ToArray(),
    };

    private static object Setting(string templateId, Dictionary<string, string> values) => new
    {
        id = "set-" + templateId[..8], templateId,
        values = values.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray(),
    };

    private static object Assignment(string principal, string role, string type, string expiration, DateTimeOffset? end) => new
    {
        id = "as-" + principal + "-" + role[..4], principalId = principal, roleDefinitionId = role, directoryScopeId = "/",
        assignmentType = type, memberType = "Direct",
        scheduleInfo = new { startDateTime = Iso(Now.AddDays(-5)), expiration = new { type = expiration, endDateTime = end is { } e ? Iso(e) : null } },
    };

    private string AuthenticationMethods()
    {
        var feature = (string state) => new { state, includeTarget = new { targetType = "group", id = "all_users" }, excludeTarget = new { targetType = "group", id = "00000000-0000-0000-0000-000000000000" } };
        return JsonSerializer.Serialize(new
        {
            id = "authenticationMethodsPolicy", policyMigrationState = "migrationComplete",
            registrationEnforcement = new { authenticationMethodsRegistrationCampaign = new { state = "enabled" } },
            authenticationMethodConfigurations = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.microsoftAuthenticatorAuthenticationMethodConfiguration",
                    ["id"] = "MicrosoftAuthenticator", ["state"] = "enabled",
                    ["includeTargets"] = new[] { new { targetType = "group", id = "all_users" } },
                    ["excludeTargets"] = Array.Empty<object>(),
                    ["featureSettings"] = new
                    {
                        displayAppInformationRequiredState = feature(Ok ? "enabled" : "default"),
                        displayLocationInformationRequiredState = feature(Ok ? "enabled" : "disabled"),
                    },
                },
                new { id = "Sms", state = Ok ? "disabled" : "enabled", includeTargets = new[] { new { targetType = "group", id = "all_users" } }, excludeTargets = Array.Empty<object>() },
                new { id = "Voice", state = "disabled", includeTargets = Array.Empty<object>(), excludeTargets = Array.Empty<object>() },
                new { id = "Email", state = Ok ? "disabled" : "enabled", includeTargets = Array.Empty<object>(), excludeTargets = Array.Empty<object>() },
                new { id = "Fido2", state = "enabled", includeTargets = new[] { new { targetType = "group", id = "all_users" } }, excludeTargets = Array.Empty<object>() },
            },
        });
    }

    private static object[] CompliantReviews()
    {
        object Review(string id, string name, object scope) => new
        {
            id, displayName = name, status = "InProgress", scope,
            reviewers = new[] { new { query = "/users/u1", queryType = "MicrosoftGraph" } },
            settings = new
            {
                instanceDurationInDays = 14, autoApplyDecisionsEnabled = true, justificationRequiredOnApproval = true, mailNotificationsEnabled = true,
                recurrence = new { pattern = new { type = "absoluteMonthly", interval = 1 } },
                applyActions = new[] { new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.removeAccessApplyAction" } },
            },
        };
        object RoleScope(string role) => new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.principalResourceMembershipsScope",
            ["principalScopes"] = new[] { new { query = "/users", queryType = "MicrosoftGraph" } },
            ["resourceScopes"] = new[] { new { query = "/roleManagement/directory/roleDefinitions/" + role, queryType = "MicrosoftGraph" } },
        };

        var list = new List<object>
        {
            Review("ar-guests", "Revisão mensal de convidados", new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
                ["query"] = "./members/microsoft.graph.user/?$count=true&$filter=(userType eq 'Guest')", ["queryType"] = "MicrosoftGraph",
            }),
            Review("ar-tenant-creator", "Revisão do Criador de Locatário", RoleScope(TenantCreator)),
        };
        foreach (var (template, name, _) in EntraConfigurationControls.ReviewedRoles)
            list.Add(Review("ar-" + template[..8], "Revisão de " + name, RoleScope(template)));
        return list.ToArray();
    }

    // ---- Acesso condicional ---------------------------------------------------------------------------

    private static string CompliantPolicies() => $$$$"""
        {"value":[
          {"id":"p-mfa-todos","displayName":"MFA para todos","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["mfa"]},"sessionControls":null},
          {"id":"p-legado","displayName":"Bloquear autenticação legada","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["exchangeActiveSync","other"]},
           "grantControls":{"operator":"OR","builtInControls":["block"]}},
          {"id":"p-admins","displayName":"Administradores: MFA resistente a phishing e sessão curta","state":"enabled",
           "conditions":{"users":{"includeRoles":["{{{{Ga}}}}","{{{{Sa}}}}"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":[],"authenticationStrength":{"id":"00000000-0000-0000-0000-000000000004","displayName":"MFA resistente a phishing","allowedCombinations":["fido2","windowsHelloForBusiness","x509CertificateMultiFactor"]}},
           "sessionControls":{"signInFrequency":{"isEnabled":true,"value":4,"type":"hours","frequencyInterval":"timeBased","authenticationType":"primaryAndSecondaryAuthentication"},"persistentBrowser":{"isEnabled":true,"mode":"never"}}},
          {"id":"p-risco-usuario","displayName":"Risco alto de usuário","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"userRiskLevels":["high"],"clientAppTypes":["all"]},
           "grantControls":{"operator":"AND","builtInControls":["mfa","passwordChange"]},
           "sessionControls":{"signInFrequency":{"isEnabled":true,"frequencyInterval":"everyTime","authenticationType":"primaryAndSecondaryAuthentication"}}},
          {"id":"p-risco-entrada","displayName":"Risco médio e alto de entrada","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"signInRiskLevels":["high","medium"],"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["mfa"]},
           "sessionControls":{"signInFrequency":{"isEnabled":true,"frequencyInterval":"everyTime","authenticationType":"primaryAndSecondaryAuthentication"}}},
          {"id":"p-bloqueio-risco","displayName":"Bloquear entrada de risco","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"signInRiskLevels":["high","medium"],"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["block"]}},
          {"id":"p-dispositivo","displayName":"Dispositivo gerenciado","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["compliantDevice","domainJoinedDevice"]}},
          {"id":"p-info-seguranca","displayName":"Registro de informações de segurança","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeUserActions":["urn:user:registersecurityinfo"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["compliantDevice"]}},
          {"id":"p-intune","displayName":"Registro no Intune","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["d4ebce55-015a-49b5-a083-c84d1797ae8c"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["mfa"]},
           "sessionControls":{"signInFrequency":{"isEnabled":true,"frequencyInterval":"everyTime","authenticationType":"primaryAndSecondaryAuthentication"}}},
          {"id":"p-fluxos","displayName":"Bloquear fluxos de transferência","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"],"authenticationFlows":{"transferMethods":"deviceCodeFlow,authenticationTransfer"}},
           "grantControls":{"operator":"OR","builtInControls":["block"]}},
          {"id":"p-reautenticacao","displayName":"Reautenticação semanal","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["mfa"]},
           "sessionControls":{"signInFrequency":{"isEnabled":true,"value":7,"type":"days","frequencyInterval":"timeBased","authenticationType":"primaryAndSecondaryAuthentication"}}},
          {"id":"p-ociosa","displayName":"Sessão ociosa no navegador","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["Office365"]},"clientAppTypes":["browser"]},
           "grantControls":null,"sessionControls":{"applicationEnforcedRestrictions":{"isEnabled":true}}},
          {"id":"p-paises","displayName":"Bloquear países sem operação","state":"enabled",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"],"locations":{"includeLocations":["{{{{CountryLocation}}}}"],"excludeLocations":[]}},
           "grantControls":{"operator":"OR","builtInControls":["block"]}}
        ]}
        """;

    private static string NonCompliantPolicies() => $$$$"""
        {"value":[
          {"id":"p-admins","displayName":"MFA para administradores globais","state":"enabled",
           "conditions":{"users":{"includeRoles":["{{{{Ga}}}}"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
           "grantControls":{"operator":"OR","builtInControls":["mfa"]}},
          {"id":"p-legado","displayName":"Bloquear autenticação legada","state":"enabledForReportingButNotEnforced",
           "conditions":{"users":{"includeUsers":["All"]},"applications":{"includeApplications":["All"]},"clientAppTypes":["exchangeActiveSync","other"]},
           "grantControls":{"builtInControls":["block"]}}
        ]}
        """;

    private static (HttpStatusCode, string) Json(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly EntraConfigurationScenario _s;
        public StubHandler(EntraConfigurationScenario s) => _s = s;
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.AbsoluteUri);
            var (status, body) = _s.Respond(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
