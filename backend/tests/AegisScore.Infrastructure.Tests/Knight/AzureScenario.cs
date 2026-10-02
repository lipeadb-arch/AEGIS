using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Ambiente SINTÉTICO do Azure ("Cliente Demo", domínios example.com): respostas do Azure
/// Resource Manager, do plano de dados do Key Vault e do Microsoft Graph montadas a partir de uma tabela de recursos.
/// Duas assinaturas no escopo e uma visível fora dele. Cada variante liga um desfecho que o produto precisa distinguir.
/// Nenhum identificador, nome ou endereço real.
/// </summary>
public sealed class AzureScenario
{
    public enum Variant
    {
        /// <summary>Tudo conforme.</summary>
        Compliant,
        /// <summary>Tudo inadequado.</summary>
        NonCompliant,
        /// <summary>Conforme, mas a leitura de armazenamento é recusada (403) na segunda assinatura.</summary>
        StorageDeniedInB,
        /// <summary>Inadequado, com a mesma recusa: a violação lida continua violação, com a lacuna dita.</summary>
        NonCompliantStorageDeniedInB,
        /// <summary>Conforme, mas o plano de dados dos cofres recusa a aplicação (403).</summary>
        VaultDataDenied,
        /// <summary>A aplicação não enxerga nenhuma assinatura.</summary>
        NoSubscriptions,
        /// <summary>A listagem de assinaturas é recusada (403).</summary>
        DiscoveryDenied,
    }

    public const string SubA = "00000000-aaaa-4000-8000-00000000000a";
    public const string SubB = "00000000-bbbb-4000-8000-00000000000b";
    public const string SubC = "00000000-cccc-4000-8000-00000000000c";
    public const string Rg = "rg-cliente-demo";
    public const string Secret = "segredo-sintetico-nao-gravar";

    private const string Owner = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    private const string Uaa = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9";
    private const string Reader = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    private readonly Variant _v;
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<object>> _generic = new(StringComparer.OrdinalIgnoreCase);

    public AzureScenario(Variant v)
    {
        _v = v;
        Build();
    }

    private bool Ok => _v is Variant.Compliant or Variant.StorageDeniedInB or Variant.VaultDataDenied;

    /// <summary>Pedidos recebidos, na ordem (URL absoluta).</summary>
    public List<string> Requests { get; } = new();

    /// <summary>Escopos pedidos nos tokens (ARM, plano de dados do cofre, Graph).</summary>
    public List<string> TokenScopes { get; } = new();

    public static KnightMicrosoftServiceConfiguration Configuration() =>
        new(KnightSourceType.MicrosoftAzure, "dir-demo-0001", "client-demo", "segredo-sintetico", null, new[] { SubA, SubB });

    public HttpMessageHandler Handler => new StubHandler(this);

    // ---- Montagem ---------------------------------------------------------------------------------------

    private static string Id(string sub, string provider, string type, string name) =>
        $"/subscriptions/{sub}/resourceGroups/{Rg}/providers/{provider}/{type}/{name}";

    private static string J(object o) => JsonSerializer.Serialize(o);

    private void Route(string path, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        _routes[path.TrimEnd('/')] = (status, body as string ?? J(body));

    private void List(string path, params object[] items) => Route(path, new { value = items });

    private void Deny(string path) =>
        Route(path, """{"error":{"code":"AuthorizationFailed","message":"sintético"}}""", HttpStatusCode.Forbidden);

    private static object Pe(bool approved) => approved
        ? new[] { new { name = "pe-demo", properties = new { privateLinkServiceConnectionState = new { status = "Approved" } } } }
        : Array.Empty<object>();

    private static object Res(string id, string type, object properties, string? kind = null, object? identity = null, object? sku = null,
        string location = "brazilsouth")
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = id, ["name"] = id.Split('/').Last(), ["type"] = type, ["location"] = location, ["properties"] = properties,
        };
        if (kind is not null) d["kind"] = kind;
        if (identity is not null) d["identity"] = identity;
        if (sku is not null) d["sku"] = sku;
        return d;
    }

    private void Build()
    {
        if (_v == Variant.DiscoveryDenied) { Deny("/subscriptions"); return; }
        if (_v == Variant.NoSubscriptions) { List("/subscriptions"); return; }

        List("/subscriptions",
            new { subscriptionId = SubA, displayName = "Assinatura Demo Produção", state = "Enabled" },
            new { subscriptionId = SubB, displayName = "Assinatura Demo Desenvolvimento", state = "Enabled" },
            new { subscriptionId = SubC, displayName = "Assinatura Demo Fora do Escopo", state = "Enabled" });

        foreach (var sub in new[] { SubA, SubB }) SubscriptionLevel(sub);
        Workloads(SubA);
        // A segunda assinatura não tem cargas de trabalho: as listas existem e vêm vazias.
        foreach (var p in new[]
                 {
                     "Microsoft.Network/networkSecurityGroups", "Microsoft.Network/virtualNetworks", "Microsoft.Network/publicIPAddresses",
                     "Microsoft.Network/applicationGateways", "Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies",
                     "Microsoft.Network/networkWatchers", "Microsoft.Storage/storageAccounts", "Microsoft.KeyVault/vaults",
                     "Microsoft.Compute/virtualMachines", "Microsoft.Compute/disks", "Microsoft.ContainerInstance/containerGroups",
                     "Microsoft.Batch/batchAccounts", "Microsoft.Web/sites", "Microsoft.Web/serverfarms", "Microsoft.Web/hostingEnvironments",
                     "Microsoft.Sql/servers", "Microsoft.Sql/managedInstances", "Microsoft.DBforPostgreSQL/flexibleServers",
                     "Microsoft.DBforMySQL/flexibleServers", "Microsoft.DocumentDB/databaseAccounts", "Microsoft.Cache/redis",
                     "Microsoft.DataFactory/factories", "Microsoft.Cache/redisEnterprise", "Microsoft.Databricks/workspaces",
                 })
            List($"/subscriptions/{SubB}/providers/{p}");
        if (_v is Variant.StorageDeniedInB or Variant.NonCompliantStorageDeniedInB)
            Deny($"/subscriptions/{SubB}/providers/Microsoft.Storage/storageAccounts");

        Tenant();
    }

    private void SubscriptionLevel(string sub)
    {
        var scope = $"/subscriptions/{sub}";
        // ---- Autorização: duas páginas na assinatura A (paginação real) e a atribuição herdada da raiz nas duas ----
        var assignments = new List<object>
        {
            new { id = $"{scope}/providers/Microsoft.Authorization/roleAssignments/owner-1", name = "owner-1", type = "Microsoft.Authorization/roleAssignments",
                properties = new { roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{Owner}", principalId = "u-owner-1", principalType = "User", scope } },
            new { id = $"{scope}/providers/Microsoft.Authorization/roleAssignments/owner-2", name = "owner-2", type = "Microsoft.Authorization/roleAssignments",
                properties = new { roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{Owner}", principalId = "u-owner-2", principalType = "User", scope } },
            new { id = $"{scope}/resourceGroups/{Rg}/providers/Microsoft.Authorization/roleAssignments/reader-1", name = "reader-1", type = "Microsoft.Authorization/roleAssignments",
                properties = new { roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{Reader}", principalId = "u-reader-1", principalType = "User", scope = $"{scope}/resourceGroups/{Rg}" } },
        };
        if (!Ok)
        {
            foreach (var n in new[] { 3, 4 })
                assignments.Add(new { id = $"{scope}/providers/Microsoft.Authorization/roleAssignments/owner-{n}", name = $"owner-{n}", type = "Microsoft.Authorization/roleAssignments",
                    properties = new { roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{Owner}", principalId = $"u-owner-{n}", principalType = "User", scope } });
            assignments.Add(new { id = "/providers/Microsoft.Authorization/roleAssignments/root-uaa", name = "root-uaa", type = "Microsoft.Authorization/roleAssignments",
                properties = new { roleDefinitionId = $"/providers/Microsoft.Authorization/roleDefinitions/{Uaa}", principalId = "u-elevated", principalType = "User", scope = "/" } });
        }
        var path = $"{scope}/providers/Microsoft.Authorization/roleAssignments";
        if (sub == SubA)
        {
            Route(path, new { value = assignments.Take(2).ToArray(),
                nextLink = $"https://management.azure.com{path}?api-version=2022-04-01&$skiptoken=pagina-2" });
            Route(path + "#pagina-2", new { value = assignments.Skip(2).ToArray() });
        }
        else List(path, assignments.ToArray());

        var roles = new List<object>();
        if (Ok)
            roles.Add(new { id = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/lock-admin", name = "lock-admin", type = "Microsoft.Authorization/roleDefinitions",
                properties = new { roleName = "Demo - Administrador de bloqueios", type = "CustomRole", assignableScopes = new[] { scope },
                    permissions = new[] { new { actions = new[] { "Microsoft.Authorization/locks/*", "Microsoft.Resources/subscriptions/resourceGroups/read" } } } } });
        else
            roles.Add(new { id = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/full-admin", name = "full-admin", type = "Microsoft.Authorization/roleDefinitions",
                properties = new { roleName = "Demo - Administrador total", type = "CustomRole", assignableScopes = new[] { scope },
                    permissions = new[] { new { actions = new[] { "*" } } } } });
        List($"{scope}/providers/Microsoft.Authorization/roleDefinitions", roles.ToArray());

        List($"{scope}/providers/Microsoft.Authorization/locks", Ok && sub == SubA
            ? new object[] { new { id = Id(sub, "Microsoft.Storage", "storageAccounts", "stclientedemo") + "/providers/Microsoft.Authorization/locks/nao-excluir",
                name = "nao-excluir", type = "Microsoft.Authorization/locks", properties = new { level = "CanNotDelete" } } }
            : Array.Empty<object>());

        // ---- Política: iniciativa padrão do Defender para Nuvem ----------------------------------------------------
        List($"{scope}/providers/Microsoft.Authorization/policyAssignments",
            new { id = $"{scope}/providers/Microsoft.Authorization/policyAssignments/SecurityCenterBuiltIn", name = "SecurityCenterBuiltIn",
                type = "Microsoft.Authorization/policyAssignments",
                properties = new { displayName = "ASC Default", enforcementMode = "Default",
                    parameters = new Dictionary<string, object> { [Ok ? "identityDesignateMoreThanOneOwnerMonitoringEffect" : "systemUpdatesMonitoringEffect"] =
                        new { value = Ok ? "AuditIfNotExists" : "Disabled" } } } },
            new { id = $"{scope}/providers/Microsoft.Authorization/policyAssignments/outra", name = "outra", type = "Microsoft.Authorization/policyAssignments",
                properties = new { displayName = "Não lida (filtrada)", enforcementMode = "Default" } });

        // ---- Defender para Nuvem -----------------------------------------------------------------------------------
        var plans = new[] { "CloudPosture", "Api", "VirtualMachines", "Containers", "StorageAccounts", "AppServices", "CosmosDbs",
            "OpenSourceRelationalDatabases", "SqlServers", "SqlServerVirtualMachines", "KeyVaults", "Arm" };
        List($"{scope}/providers/Microsoft.Security/pricings", plans.Select(p => (object)new
        {
            id = $"{scope}/providers/Microsoft.Security/pricings/{p}", name = p, type = "Microsoft.Security/pricings",
            properties = p == "VirtualMachines"
                ? new { pricingTier = Ok ? "Standard" : "Free", subPlan = Ok ? "P2" : (string?)null,
                    extensions = new[] { new { name = "AgentlessVmScanning", isEnabled = "True" }, new { name = "FileIntegrityMonitoring", isEnabled = "True" } } }
                : (object)new { pricingTier = Ok ? "Standard" : "Free" },
        }).ToArray());
        List($"{scope}/providers/Microsoft.Security/serverVulnerabilityAssessmentsSettings", Ok
            ? new object[] { new { id = $"{scope}/providers/Microsoft.Security/serverVulnerabilityAssessmentsSettings/AzureServersSetting",
                name = "AzureServersSetting", type = "Microsoft.Security/serverVulnerabilityAssessmentsSettings", kind = "AzureServersSetting",
                properties = new { selectedProvider = "MdeTvm" } } }
            : Array.Empty<object>());
        List($"{scope}/providers/Microsoft.Security/settings",
            new { id = $"{scope}/providers/Microsoft.Security/settings/WDATP", name = "WDATP", type = "Microsoft.Security/settings",
                kind = "DataExportSettings", properties = new { enabled = Ok } });
        Generic(sub, "Microsoft.Easm/workspaces", Ok ? new[] { Res(Id(sub, "Microsoft.Easm", "workspaces", "easm-demo"), "Microsoft.Easm/workspaces", new { }) } : Array.Empty<object>());
        Generic(sub, "Microsoft.Devices/IotHubs", sub == SubA ? new[] { Res(Id(sub, "Microsoft.Devices", "IotHubs", "iot-demo"), "Microsoft.Devices/IotHubs", new { }) } : Array.Empty<object>());
        Generic(sub, "Microsoft.Security/iotSecuritySolutions", Ok && sub == SubA
            ? new[] { Res(Id(sub, "Microsoft.Security", "iotSecuritySolutions", "iotsec-demo"), "Microsoft.Security/iotSecuritySolutions", new { }) }
            : Array.Empty<object>());

        // ---- Azure Monitor -----------------------------------------------------------------------------------------
        var ops = new[]
        {
            "Microsoft.Authorization/policyAssignments/write", "Microsoft.Authorization/policyAssignments/delete",
            "Microsoft.Network/networkSecurityGroups/write", "Microsoft.Network/networkSecurityGroups/delete",
            "Microsoft.Security/securitySolutions/write", "Microsoft.Security/securitySolutions/delete",
            "Microsoft.Sql/servers/firewallRules/write", "Microsoft.Sql/servers/firewallRules/delete",
            "Microsoft.Network/publicIPAddresses/write", "Microsoft.Network/publicIPAddresses/delete",
        };
        object Alert(string name, object[] allOf, bool enabled) => Res($"/subscriptions/{sub}/resourceGroups/{Rg}/providers/Microsoft.Insights/activityLogAlerts/{name}",
            "Microsoft.Insights/activityLogAlerts", new { enabled, scopes = new[] { scope }, condition = new { allOf },
                actions = new { actionGroups = new[] { new { actionGroupId = $"{scope}/resourceGroups/{Rg}/providers/microsoft.insights/actionGroups/ag-seguranca" } } } },
            location: "global");
        var alerts = Ok
            ? ops.Select((o, i) => Alert($"alerta-{i + 1}", new object[] { new { field = "category", equals = "Administrative" }, new { field = "operationName", equals = o } }, true))
                .Append(Alert("alerta-service-health", new object[] { new { field = "category", equals = "ServiceHealth" } }, true)).ToArray()
            : new[] { Alert("alerta-desligado", new object[] { new { field = "category", equals = "Administrative" }, new { field = "operationName", equals = ops[0] } }, false) };
        List($"{scope}/providers/Microsoft.Insights/activityLogAlerts", alerts);
        List($"{scope}/providers/Microsoft.Insights/components", Ok
            ? new[] { Res(Id(sub, "Microsoft.Insights", "components", "appi-demo"), "microsoft.insights/components", new { }, kind: "web") }
            : Array.Empty<object>());

        // ---- Bastion (uma por assinatura no cenário conforme) ---------------------------------------------------------
        List($"{scope}/providers/Microsoft.Network/bastionHosts", Ok
            ? new[] { Res(Id(sub, "Microsoft.Network", "bastionHosts", "bas-demo"), "Microsoft.Network/bastionHosts", new { }) }
            : Array.Empty<object>());
        Generic(sub, "Microsoft.Network/virtualNetworkGateways", Array.Empty<object>());
    }

    private void Generic(string sub, string type, object[] items) => _generic[$"{sub}|{type}"] = items.ToList();

    private void Workloads(string sub)
    {
        var s = $"/subscriptions/{sub}/providers";
        var vnetId = Id(sub, "Microsoft.Network", "virtualNetworks", "vnet-demo");
        var nsgId = Id(sub, "Microsoft.Network", "networkSecurityGroups", "nsg-demo");
        var wsId = $"/subscriptions/{sub}/resourceGroups/{Rg}/providers/Microsoft.OperationalInsights/workspaces/law-demo";

        // ---- Rede ------------------------------------------------------------------------------------------------
        List($"{s}/Microsoft.Network/networkSecurityGroups", Res(nsgId, "Microsoft.Network/networkSecurityGroups", new
        {
            securityRules = Ok
                ? new object[]
                {
                    new { name = "rdp-da-rede-interna", properties = new { direction = "Inbound", access = "Allow", protocol = "Tcp", priority = 100,
                        sourceAddressPrefix = "10.0.0.0/8", destinationPortRange = "3389" } },
                    new { name = "negar-ssh-internet", properties = new { direction = "Inbound", access = "Deny", protocol = "Tcp", priority = 110,
                        sourceAddressPrefix = "Internet", destinationPortRange = "22" } },
                }
                : new object[]
                {
                    new { name = "permitir-tudo", properties = new { direction = "Inbound", access = "Allow", protocol = "*", priority = 100,
                        sourceAddressPrefix = "Internet", destinationPortRange = "*" } },
                },
        }));
        object Subnet(string name, bool nsg) => new
        {
            id = $"{vnetId}/subnets/{name}", name,
            properties = nsg ? new { addressPrefix = "10.1.0.0/24", networkSecurityGroup = new { id = nsgId } } : (object)new { addressPrefix = "10.1.1.0/24" },
        };
        List($"{s}/Microsoft.Network/virtualNetworks", Res(vnetId, "Microsoft.Network/virtualNetworks", new
        {
            enableDdosProtection = Ok,
            ddosProtectionPlan = Ok ? new { id = $"/subscriptions/{sub}/resourceGroups/{Rg}/providers/Microsoft.Network/ddosProtectionPlans/ddos-demo" } : null,
            subnets = new[] { Subnet("snet-app", Ok), Subnet("GatewaySubnet", false), Subnet("snet-dbr-pub", Ok), Subnet("snet-dbr-priv", Ok) },
        }));
        List($"{s}/Microsoft.Network/publicIPAddresses", Res(Id(sub, "Microsoft.Network", "publicIPAddresses", "pip-demo"), "Microsoft.Network/publicIPAddresses", new { }));
        List($"{s}/Microsoft.Network/applicationGateways", Res(Id(sub, "Microsoft.Network", "applicationGateways", "agw-demo"), "Microsoft.Network/applicationGateways", new
        {
            sku = new { name = Ok ? "WAF_v2" : "Standard_v2", tier = Ok ? "WAF_v2" : "Standard_v2" },
            sslPolicy = new { policyType = "Predefined", policyName = Ok ? "AppGwSslPolicy20220101" : "AppGwSslPolicy20150501" },
            enableHttp2 = Ok,
            firewallPolicy = Ok ? new { id = Id(sub, "Microsoft.Network", "ApplicationGatewayWebApplicationFirewallPolicies", "wafp-demo") } : null,
        }));
        List($"{s}/Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies",
            Res(Id(sub, "Microsoft.Network", "ApplicationGatewayWebApplicationFirewallPolicies", "wafp-demo"),
                "Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies", new
                {
                    policySettings = new { state = "Enabled", mode = "Prevention", requestBodyCheck = Ok },
                    managedRules = new { managedRuleSets = Ok
                        ? new[] { new { ruleSetType = "OWASP", ruleSetVersion = "3.2" }, new { ruleSetType = "Microsoft_BotManagerRuleSet", ruleSetVersion = "1.0" } }
                        : new[] { new { ruleSetType = "OWASP", ruleSetVersion = "3.2" } } },
                }));
        var watcherId = $"/subscriptions/{sub}/resourceGroups/NetworkWatcherRG/providers/Microsoft.Network/networkWatchers/NetworkWatcher_{(Ok ? "brazilsouth" : "eastus")}";
        List($"{s}/Microsoft.Network/networkWatchers", Res(watcherId, "Microsoft.Network/networkWatchers", new { }, location: Ok ? "brazilsouth" : "eastus"));
        object Flow(string target) => new
        {
            id = $"{watcherId}/flowLogs/fl-{target.Split('/').Last()}", name = $"fl-{target.Split('/').Last()}",
            properties = new
            {
                targetResourceId = target, enabled = true,
                retentionPolicy = new { enabled = true, days = Ok ? 90 : 7 },
                flowAnalyticsConfiguration = new { networkWatcherFlowAnalyticsConfiguration = new { enabled = Ok, workspaceResourceId = wsId } },
            },
        };
        List($"{watcherId}/flowLogs", Flow(nsgId), Flow(vnetId));
        var gw = Id(sub, "Microsoft.Network", "virtualNetworkGateways", "vgw-demo");
        Generic(sub, "Microsoft.Network/virtualNetworkGateways", new[] { Res(gw, "Microsoft.Network/virtualNetworkGateways", new { }) });
        Route(gw, Res(gw, "Microsoft.Network/virtualNetworkGateways", new
        {
            gatewayType = "Vpn",
            vpnClientConfiguration = new
            {
                vpnClientAddressPool = new { addressPrefixes = new[] { "172.16.0.0/24" } },
                vpnAuthenticationTypes = Ok ? new[] { "AAD" } : new[] { "Certificate", "AAD" },
            },
        }));

        // ---- Armazenamento ---------------------------------------------------------------------------------------
        var st = Id(sub, "Microsoft.Storage", "storageAccounts", "stclientedemo");
        var now = DateTimeOffset.UtcNow;
        List($"{s}/Microsoft.Storage/storageAccounts", Res(st, "Microsoft.Storage/storageAccounts", Ok
            ? new
            {
                minimumTlsVersion = "TLS1_2", allowBlobPublicAccess = false, supportsHttpsTrafficOnly = true, allowSharedKeyAccess = false,
                publicNetworkAccess = "Disabled", networkAcls = new { defaultAction = "Deny", bypass = "AzureServices" },
                allowCrossTenantReplication = false, defaultToOAuthAuthentication = true,
                keyPolicy = new { keyExpirationPeriodInDays = 90 },
                keyCreationTime = new { key1 = now.AddDays(-10).ToString("O"), key2 = now.AddDays(-12).ToString("O") },
                privateEndpointConnections = Pe(true), encryption = new { keySource = "Microsoft.Keyvault" },
            }
            : (object)new
            {
                minimumTlsVersion = "TLS1_0", allowBlobPublicAccess = true, supportsHttpsTrafficOnly = false, allowSharedKeyAccess = true,
                publicNetworkAccess = "Enabled", networkAcls = new { defaultAction = "Allow", bypass = "None" },
                allowCrossTenantReplication = true, defaultToOAuthAuthentication = false,
                keyCreationTime = new { key1 = now.AddDays(-200).ToString("O"), key2 = now.AddDays(-12).ToString("O") },
                privateEndpointConnections = Pe(false), encryption = new { keySource = "Microsoft.Storage" },
            }, kind: "StorageV2"));
        Route($"{st}/blobServices/default", new { id = $"{st}/blobServices/default", name = "default", properties = new
        {
            deleteRetentionPolicy = new { enabled = Ok, days = 7 }, containerDeleteRetentionPolicy = new { enabled = Ok, days = 7 }, isVersioningEnabled = Ok,
        } });
        Route($"{st}/fileServices/default", new { id = $"{st}/fileServices/default", name = "default", properties = Ok
            ? new { shareDeleteRetentionPolicy = new { enabled = true, days = 14 },
                protocolSettings = new { smb = new { versions = "SMB3.1.1", channelEncryption = "AES-256-GCM" } } }
            : (object)new { shareDeleteRetentionPolicy = new { enabled = false } } });

        // ---- Key Vault -------------------------------------------------------------------------------------------
        var vaults = new List<object>();
        void Vault(string name, bool rbac)
        {
            var id = Id(sub, "Microsoft.KeyVault", "vaults", name);
            vaults.Add(Res(id, "Microsoft.KeyVault/vaults", new
            {
                enablePurgeProtection = Ok ? true : (bool?)null, enableSoftDelete = true, enableRbacAuthorization = rbac,
                publicNetworkAccess = Ok ? "Disabled" : "Enabled", networkAcls = new { defaultAction = Ok ? "Deny" : "Allow" },
                privateEndpointConnections = Pe(Ok), vaultUri = $"https://{name}.vault.azure.net/",
            }));
            var exp = now.AddDays(180).ToUnixTimeSeconds();
            List($"{id}/keys", new
            {
                id = $"{id}/keys/chave-app", name = "chave-app", type = "Microsoft.KeyVault/vaults/keys",
                properties = Ok
                    ? new { attributes = new { enabled = true, exp = (long?)exp }, rotationPolicy = new { lifetimeActions = new[] { new { action = new { type = "rotate" } } } } }
                    : (object)new { attributes = new { enabled = true } },
            });
            List($"{id}/secrets", new
            {
                id = $"{id}/secrets/senha-app", name = "senha-app", type = "Microsoft.KeyVault/vaults/secrets",
                properties = Ok ? new { attributes = new { enabled = true, exp = (long?)exp } } : (object)new { attributes = new { enabled = true } },
            });
            var host = $"/{name}.vault.azure.net";
            if (_v == Variant.VaultDataDenied) { Deny(host + "/certificates"); return; }
            List(host + "/certificates", new { id = $"https://{name}.vault.azure.net/certificates/cert-app" });
            Route(host + "/certificates/cert-app/policy", new Dictionary<string, object>
            {
                ["id"] = $"https://{name}.vault.azure.net/certificates/cert-app/policy",
                ["x509_props"] = new Dictionary<string, object> { ["validity_months"] = Ok ? 12 : 24 },
            });
        }
        Vault("kv-demo-rbac", true);
        if (!Ok) Vault("kv-demo-ap", false);
        List($"{s}/Microsoft.KeyVault/vaults", vaults.ToArray());

        // ---- Computação ------------------------------------------------------------------------------------------
        var vm = Id(sub, "Microsoft.Compute", "virtualMachines", "vm-demo");
        List($"{s}/Microsoft.Compute/virtualMachines", Res(vm, "Microsoft.Compute/virtualMachines", new
        {
            storageProfile = new { osDisk = Ok
                ? new { managedDisk = new { id = Id(sub, "Microsoft.Compute", "disks", "disk-os") } }
                : (object)new { vhd = new { uri = "https://stclientedemo.blob.core.windows.net/vhds/vm-demo.vhd" }, encryptionSettings = new { enabled = false } } },
            securityProfile = Ok ? new { securityType = "TrustedLaunch", encryptionAtHost = true } : null,
        }));
        List($"{vm}/extensions", Ok
            ? new { id = $"{vm}/extensions/MDE.Windows", name = "MDE.Windows",
                properties = new { publisher = "Microsoft.Azure.AzureDefenderForServers", type = "MDE.Windows", provisioningState = "Succeeded" } }
            : new { id = $"{vm}/extensions/CustomScript", name = "CustomScript",
                properties = new { publisher = "Microsoft.Compute", type = "CustomScriptExtension", provisioningState = "Succeeded" } });
        object Disk(string name, string? managedBy)
        {
            var d = (Dictionary<string, object?>)Res(Id(sub, "Microsoft.Compute", "disks", name), "Microsoft.Compute/disks", Ok
                ? new { encryption = new { type = "EncryptionAtRestWithCustomerKey" }, diskState = managedBy is null ? "Unattached" : "Attached",
                    networkAccessPolicy = "DenyAll", publicNetworkAccess = "Disabled", dataAccessAuthMode = "AzureActiveDirectory" }
                : (object)new { encryption = new { type = "EncryptionAtRestWithPlatformKey" }, diskState = managedBy is null ? "Unattached" : "Attached",
                    networkAccessPolicy = "AllowAll", publicNetworkAccess = "Enabled" });
            if (managedBy is not null) d["managedBy"] = managedBy;
            return d;
        }
        List($"{s}/Microsoft.Compute/disks", Disk("disk-os", vm), Disk("disk-solto", null));
        List($"{s}/Microsoft.ContainerInstance/containerGroups", Res(Id(sub, "Microsoft.ContainerInstance", "containerGroups", "aci-demo"),
            "Microsoft.ContainerInstance/containerGroups", new
            {
                ipAddress = new { type = Ok ? "Private" : "Public", ip = "10.1.0.9" },
                subnetIds = Ok ? new[] { new { id = $"{vnetId}/subnets/snet-app" } } : Array.Empty<object>(),
                // Variáveis de ambiente com segredo: NUNCA podem chegar ao ADM.
                containers = new[] { new { name = "app", properties = new { environmentVariables = new[] { new { name = "SENHA_BANCO", value = Secret } } } } },
            }, identity: Ok ? new { type = "SystemAssigned" } : null));
        var batch = Id(sub, "Microsoft.Batch", "batchAccounts", "batchdemo");
        List($"{s}/Microsoft.Batch/batchAccounts", Res(batch, "Microsoft.Batch/batchAccounts", new
        {
            encryption = new { keySource = Ok ? "Microsoft.KeyVault" : "Microsoft.Batch" },
            allowedAuthenticationModes = Ok ? new[] { "AAD" } : new[] { "SharedKey", "AAD" },
            publicNetworkAccess = Ok ? "Disabled" : "Enabled", privateEndpointConnections = Pe(Ok),
        }));
        List($"{batch}/pools", new
        {
            id = $"{batch}/pools/pool-demo", name = "pool-demo",
            properties = new { deploymentConfiguration = new { virtualMachineConfiguration = Ok
                ? new { diskEncryptionConfiguration = new { targets = new[] { "OsDisk", "TemporaryDisk" } } }
                : (object)new { imageReference = new { offer = "ubuntu" } } } },
        });

        // ---- App Service -----------------------------------------------------------------------------------------
        var sites = new List<object>();
        void Site(string name, string kind, string fx, string badFx)
        {
            var id = Id(sub, "Microsoft.Web", "sites", name);
            sites.Add(SiteBody(id, kind, $"{vnetId}/subnets/snet-app"));
            SiteChildren(id, fx, badFx);
            if (name == "app-demo")
            {
                var slot = $"{id}/slots/staging";
                List($"{id}/slots", With((Dictionary<string, object?>)SiteBody(slot, kind, $"{vnetId}/subnets/snet-app"), "type", "Microsoft.Web/sites/slots"));
                SiteChildren(slot, fx, badFx);
            }
            else List($"{id}/slots");
        }
        Site("app-demo", "app,linux", "PYTHON|3.12", "PYTHON|3.7");
        Site("app-java", "app,linux", "JAVA|17-java17", "JAVA|8-jre8");
        Site("app-php", "app,linux", "PHP|8.3", "PHP|7.4");
        Site("func-demo", "functionapp,linux", "Python|3.11", "Python|3.7");
        List($"{s}/Microsoft.Web/sites", sites.ToArray());
        List($"{s}/Microsoft.Web/serverfarms", Res(Id(sub, "Microsoft.Web", "serverfarms", "plan-demo"), "Microsoft.Web/serverfarms", new { },
            sku: new { name = "P1v3", tier = "PremiumV3" }));
        List($"{s}/Microsoft.Web/hostingEnvironments", Res(Id(sub, "Microsoft.Web", "hostingEnvironments", "ase-demo"), "Microsoft.Web/hostingEnvironments", new
        {
            internalLoadBalancingMode = Ok ? "Web, Publishing" : "None",
            clusterSettings = Ok
                ? new[] { new { name = "InternalEncryption", value = "true" }, new { name = "DisableTls1.0", value = "1" },
                    new { name = "FrontEndSSLCipherSuiteOrder", value = "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384" } }
                : Array.Empty<object>(),
        }, kind: Ok ? "ASEV3" : "ASEV2"));

        // ---- Bancos de dados --------------------------------------------------------------------------------------
        SqlServer(sub, "sql-demo", auditOn: Ok);
        SqlServer(sub, "sql-demo-auditado", auditOn: true);
        List($"{s}/Microsoft.Sql/servers", Res(Id(sub, "Microsoft.Sql", "servers", "sql-demo"), "Microsoft.Sql/servers", SqlProps()),
            Res(Id(sub, "Microsoft.Sql", "servers", "sql-demo-auditado"), "Microsoft.Sql/servers", SqlProps()));
        var mi = Id(sub, "Microsoft.Sql", "managedInstances", "sqlmi-demo");
        List($"{s}/Microsoft.Sql/managedInstances", Res(mi, "Microsoft.Sql/managedInstances", new { minimalTlsVersion = Ok ? "1.2" : "1.0", publicDataEndpointEnabled = !Ok }));
        Route($"{mi}/encryptionProtector/current", new { properties = new { serverKeyType = Ok ? "AzureKeyVault" : "ServiceManaged" } });
        List($"{mi}/databases", new { id = $"{mi}/databases/db-mi", name = "db-mi", type = "Microsoft.Sql/managedInstances/databases", properties = new { } });
        Route($"{mi}/databases/db-mi/transparentDataEncryption/current", new { properties = new { state = Ok ? "Enabled" : "Disabled" } });

        var pg = Id(sub, "Microsoft.DBforPostgreSQL", "flexibleServers", "pg-demo");
        List($"{s}/Microsoft.DBforPostgreSQL/flexibleServers", Res(pg, "Microsoft.DBforPostgreSQL/flexibleServers", new
        {
            dataEncryption = new { type = Ok ? "AzureKeyVault" : "SystemManaged" },
            network = new { publicNetworkAccess = Ok ? "Disabled" : "Enabled" },
            authConfig = new { activeDirectoryAuth = Ok ? "Enabled" : "Disabled", passwordAuth = Ok ? "Disabled" : "Enabled" },
            privateEndpointConnections = Pe(Ok),
        }));
        foreach (var (name, good, bad) in new[]
                 {
                     ("connection_throttle.enable", "on", "off"), ("logfiles.retention_days", "7", "3"), ("log_checkpoints", "on", "off"),
                     ("log_disconnections", "on", "off"), ("log_connections", "on", "off"), ("require_secure_transport", "on", "off"),
                     ("ssl_min_protocol_version", "TLSv1.2", "TLSv1"),
                 })
            Route($"{pg}/configurations/{name}", new { name, properties = new { value = Ok ? good : bad } });
        var my = Id(sub, "Microsoft.DBforMySQL", "flexibleServers", "mysql-demo");
        List($"{s}/Microsoft.DBforMySQL/flexibleServers", Res(my, "Microsoft.DBforMySQL/flexibleServers", new
        {
            dataEncryption = new { type = Ok ? "AzureKeyVault" : "SystemManaged" },
            network = new { publicNetworkAccess = Ok ? "Disabled" : "Enabled" },
            privateEndpointConnections = Pe(Ok),
        }));
        foreach (var (name, good, bad) in new[]
                 {
                     ("audit_log_enabled", "ON", "OFF"), ("audit_log_events", "CONNECTION,ADMIN,DDL", "ADMIN,DDL"), ("error_server_log_file", "ON", "OFF"),
                     ("require_secure_transport", "ON", "OFF"), ("tls_version", "TLSv1.2,TLSv1.3", "TLSv1,TLSv1.1,TLSv1.2"), ("aad_auth_only", "ON", "OFF"),
                 })
            Route($"{my}/configurations/{name}", new { name, properties = new { value = Ok ? good : bad } });
        List($"{my}/administrators", new { name = "ActiveDirectory", properties = new { administratorType = "ActiveDirectory" } });

        List($"{s}/Microsoft.DocumentDB/databaseAccounts", Res(Id(sub, "Microsoft.DocumentDB", "databaseAccounts", "cosmos-demo"),
            "Microsoft.DocumentDB/databaseAccounts", Ok
                ? new
                {
                    isVirtualNetworkFilterEnabled = true, ipRules = new[] { new { ipAddressOrRange = "203.0.113.10" } }, publicNetworkAccess = "Disabled",
                    disableLocalAuth = true, keyVaultKeyUri = "https://kv-demo-rbac.vault.azure.net/keys/chave-cosmos", privateEndpointConnections = Pe(true),
                }
                : (object)new
                {
                    isVirtualNetworkFilterEnabled = false, ipRules = new[] { new { ipAddressOrRange = "0.0.0.0/0" } }, publicNetworkAccess = "Enabled",
                    disableLocalAuth = false, privateEndpointConnections = Pe(false),
                }, kind: "GlobalDocumentDB"));
        var redis = Id(sub, "Microsoft.Cache", "Redis", "redis-demo");
        List($"{s}/Microsoft.Cache/redis", Res(redis, "Microsoft.Cache/Redis", new
        {
            enableNonSslPort = !Ok, minimumTlsVersion = Ok ? "1.2" : "1.0", publicNetworkAccess = Ok ? "Disabled" : "Enabled",
            privateEndpointConnections = Pe(Ok), redisConfiguration = new Dictionary<string, object> { ["aad-enabled"] = Ok ? "true" : "false" },
            disableAccessKeyAuthentication = Ok, updateChannel = Ok ? "Stable" : "Preview",
            // A chave de acesso nunca é pedida; se viesse na resposta, não poderia ser gravada.
            accessKeys = new { primaryKey = Secret },
        }, identity: Ok ? new { type = "SystemAssigned" } : null));
        List($"{redis}/accessPolicies", new { name = "Data Owner", properties = new { type = "BuiltIn" } });
        var amr = Id(sub, "Microsoft.Cache", "redisEnterprise", "amr-demo");
        List($"{s}/Microsoft.Cache/redisEnterprise", Res(amr, "Microsoft.Cache/redisEnterprise", new
        {
            minimumTlsVersion = Ok ? "1.2" : "1.0", publicNetworkAccess = Ok ? "Disabled" : "Enabled", privateEndpointConnections = Pe(Ok),
            encryption = Ok ? new { customerManagedKeyEncryption = new { keyEncryptionKeyUrl = "https://kv-demo-rbac.vault.azure.net/keys/chave-redis" } } : null,
        }, identity: Ok ? new { type = "UserAssigned" } : null));
        List($"{amr}/databases", new { name = "default", properties = new { accessKeysAuthentication = Ok ? "Disabled" : "Enabled" } });
        List($"{s}/Microsoft.DataFactory/factories", Res(Id(sub, "Microsoft.DataFactory", "factories", "adf-demo"), "Microsoft.DataFactory/factories", new
        {
            encryption = Ok ? new { vaultBaseUrl = "https://kv-demo-rbac.vault.azure.net", keyName = "chave-adf" } : null,
        }, identity: Ok ? new { type = "SystemAssigned" } : null));

        // ---- Databricks ------------------------------------------------------------------------------------------
        List($"{s}/Microsoft.Databricks/workspaces", Res(Id(sub, "Microsoft.Databricks", "workspaces", "dbw-demo"), "Microsoft.Databricks/workspaces", Ok
            ? new
            {
                parameters = new
                {
                    customVirtualNetworkId = new { value = vnetId }, customPublicSubnetName = new { value = "snet-dbr-pub" },
                    customPrivateSubnetName = new { value = "snet-dbr-priv" }, enableNoPublicIp = new { value = true },
                },
                encryption = new { entities = new { managedServices = new { keySource = "Microsoft.Keyvault" } } },
                publicNetworkAccess = "Disabled", privateEndpointConnections = Pe(true),
            }
            : (object)new
            {
                parameters = new { enableNoPublicIp = new { value = false } }, publicNetworkAccess = "Enabled", privateEndpointConnections = Pe(false),
            }));
    }

    private object SqlProps() => new
    {
        publicNetworkAccess = Ok ? "Disabled" : "Enabled", minimalTlsVersion = Ok ? "1.2" : "1.0",
        administrators = Ok ? new { administratorType = "ActiveDirectory", login = "grp-dba@clientedemo.example.com" } : null,
    };

    private void SqlServer(string sub, string name, bool auditOn)
    {
        var id = Id(sub, "Microsoft.Sql", "servers", name);
        Route($"{id}/auditingSettings/default", new { properties = new { state = auditOn ? "Enabled" : "Disabled", retentionDays = Ok ? 0 : 30 } });
        List($"{id}/firewallRules", Ok
            ? new object[] { new { name = "escritorio", properties = new { startIpAddress = "203.0.113.1", endIpAddress = "203.0.113.20" } } }
            : new object[]
            {
                new { name = "AllowAll", properties = new { startIpAddress = "0.0.0.0", endIpAddress = "255.255.255.255" } },
                new { name = "AllowAllWindowsAzureIps", properties = new { startIpAddress = "0.0.0.0", endIpAddress = "0.0.0.0" } },
            });
        Route($"{id}/encryptionProtector/current", new { properties = new { serverKeyType = Ok ? "AzureKeyVault" : "ServiceManaged" } });
        Route($"{id}/azureADOnlyAuthentications/Default", new { properties = new { azureADOnlyAuthentication = Ok } });
        List($"{id}/databases",
            new { id = $"{id}/databases/master", name = "master", type = "Microsoft.Sql/servers/databases", properties = new { } },
            new { id = $"{id}/databases/db-app", name = "db-app", type = "Microsoft.Sql/servers/databases", properties = new { } });
        Route($"{id}/databases/db-app/transparentDataEncryption/current", new { properties = new { state = Ok ? "Enabled" : "Disabled" } });
    }

    private object SiteBody(string id, string kind, string subnetId) => Res(id, "Microsoft.Web/sites", Ok
        ? new
        {
            httpsOnly = true, clientCertEnabled = true, clientCertMode = "Required", publicNetworkAccess = "Disabled",
            virtualNetworkSubnetId = subnetId,
            vnetRouteAllEnabled = true, vnetImagePullEnabled = true, vnetContentShareEnabled = true, endToEndEncryptionEnabled = true,
        }
        : (object)new
        {
            httpsOnly = false, clientCertEnabled = false, publicNetworkAccess = "Enabled", vnetRouteAllEnabled = false,
            vnetImagePullEnabled = false, vnetContentShareEnabled = false, endToEndEncryptionEnabled = false,
        }, kind: kind, identity: Ok ? new { type = "SystemAssigned" } : null);

    private void SiteChildren(string id, string fx, string badFx)
    {
        Route($"{id}/config/web", new
        {
            properties = new
            {
                minTlsVersion = Ok ? "1.2" : "1.0", ftpsState = Ok ? "Disabled" : "AllAllowed", http20Enabled = Ok, remoteDebuggingEnabled = !Ok,
                linuxFxVersion = Ok ? fx : badFx,
                cors = new { allowedOrigins = Ok ? new[] { "https://portal.clientedemo.example.com" } : new[] { "*" } },
                // Cadeias de conexão com segredo não são lidas pela regra e NUNCA podem ser gravadas.
                connectionStrings = new[] { new { name = "banco", connectionString = $"Server=sql-demo;Password={Secret}" } },
            },
        });
        Route($"{id}/basicPublishingCredentialsPolicies/ftp", new { properties = new { allow = !Ok } });
        Route($"{id}/basicPublishingCredentialsPolicies/scm", new { properties = new { allow = !Ok } });
        Route($"{id}/config/authsettingsV2", new { properties = new { platform = new { enabled = Ok } } });
        List($"{id}/privateEndpointConnections", Ok
            ? new object[] { new { name = "pe-app", properties = new { privateLinkServiceConnectionState = new { status = "Approved" } } } }
            : Array.Empty<object>());
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> d, string key, object? value)
    {
        d[key] = value;
        return d;
    }

    private void Tenant()
    {
        string[] all = { "AuditLogs", "SignInLogs", "NonInteractiveUserSignInLogs", "ServicePrincipalSignInLogs", "ManagedIdentitySignInLogs", "MicrosoftGraphActivityLogs" };
        List("/providers/microsoft.aadiam/diagnosticSettings", new
        {
            id = "/providers/microsoft.aadiam/diagnosticSettings/exportar-entra", name = "exportar-entra", type = "microsoft.aadiam/diagnosticSettings",
            properties = new
            {
                workspaceId = $"/subscriptions/{SubA}/resourceGroups/{Rg}/providers/Microsoft.OperationalInsights/workspaces/law-demo",
                logs = (Ok ? all : new[] { "AuditLogs" }).Select(c => new { category = c, enabled = true }).ToArray(),
            },
        });
        Route("/providers/Microsoft.Subscription/policies/default", new
        {
            properties = new { blockSubscriptionsLeavingTenant = Ok, blockSubscriptionsIntoTenant = Ok, exemptedPrincipals = Array.Empty<string>() },
        });
        object Stack(string name, params (string Runtime, bool Deprecated, string Eol)[] versions) => new
        {
            name,
            properties = new
            {
                majorVersions = versions.Select(v => new
                {
                    minorVersions = new[]
                    {
                        new { stackSettings = new { linuxRuntimeSettings = new
                        {
                            runtimeVersion = v.Runtime, isDeprecated = v.Deprecated, endOfLifeDate = v.Eol,
                            siteConfigPropertiesDictionary = new { linuxFxVersion = v.Runtime },
                        } } },
                    },
                }).ToArray(),
            },
        };
        List("/providers/Microsoft.Web/webAppStacks",
            Stack("python", ("PYTHON|3.12", false, "2028-10-31T00:00:00Z"), ("PYTHON|3.7", true, "2023-06-27T00:00:00Z")),
            Stack("java", ("JAVA|17-java17", false, "2029-09-30T00:00:00Z"), ("JAVA|8-jre8", false, "2025-03-31T00:00:00Z")),
            Stack("php", ("PHP|8.3", false, "2027-11-23T00:00:00Z"), ("PHP|7.4", true, "2022-11-28T00:00:00Z")));
        List("/providers/Microsoft.Web/functionAppStacks",
            Stack("python", ("Python|3.11", false, "2027-10-31T00:00:00Z"), ("Python|3.7", true, "2023-06-27T00:00:00Z")));
    }

    // ---- Graph: estado das contas com papel -----------------------------------------------------------------------

    private string? GraphUser(string id) => id switch
    {
        "u-owner-1" => J(new { id, accountEnabled = true, userPrincipalName = "dono.um@clientedemo.example.com", displayName = "Dono Um (sintético)" }),
        "u-owner-2" => J(new { id, accountEnabled = true, userPrincipalName = "dono.dois@clientedemo.example.com", displayName = "Dono Dois (sintético)" }),
        "u-owner-3" or "u-owner-4" => J(new { id, accountEnabled = true, userPrincipalName = $"{id}@clientedemo.example.com", displayName = $"{id} (sintético)" }),
        "u-reader-1" => J(new { id, accountEnabled = Ok, userPrincipalName = "ex.colaborador@clientedemo.example.com", displayName = "Ex-colaborador (sintético)" }),
        "u-elevated" => J(new { id, accountEnabled = true, userPrincipalName = "admin.global@clientedemo.example.com", displayName = "Admin Global (sintético)" }),
        _ => null,
    };

    // ---- Roteamento ------------------------------------------------------------------------------------------

    private (HttpStatusCode, string) Respond(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!;
        lock (Requests) Requests.Add(uri.AbsoluteUri);
        var host = uri.Host.ToLowerInvariant();
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        var query = Uri.UnescapeDataString(uri.Query);

        if (host == "login.microsoftonline.com")
        {
            var scope = (body ?? "").Split('&').Select(Uri.UnescapeDataString).FirstOrDefault(p => p.StartsWith("scope=", StringComparison.Ordinal));
            lock (TokenScopes) TokenScopes.Add(scope?["scope=".Length..] ?? "");
            return (HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3600,"access_token":"token-sintetico"}""");
        }
        if (host == "graph.microsoft.com")
        {
            var id = path.Split('/').Last();
            return GraphUser(id) is { } u ? (HttpStatusCode.OK, u) : (HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound","message":"sintético"}}""");
        }
        if (host.EndsWith(".vault.azure.net", StringComparison.Ordinal))
            path = "/" + host + path;
        else if (host != "management.azure.com")
            return (HttpStatusCode.NotFound, "{}");

        if (path.EndsWith("/resources", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var sub = path.Split('/')[2];
            var i = query.IndexOf("resourceType eq '", StringComparison.Ordinal);
            var type = i < 0 ? "" : query[(i + "resourceType eq '".Length)..].Split('\'')[0];
            return _generic.TryGetValue($"{sub}|{type}", out var items) ? (HttpStatusCode.OK, J(new { value = items })) : (HttpStatusCode.OK, """{"value":[]}""");
        }
        if (query.Contains("$skiptoken=pagina-2", StringComparison.Ordinal) && _routes.TryGetValue(path + "#pagina-2", out var page2))
            return page2;
        return _routes.TryGetValue(path, out var r)
            ? r
            : (HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"rota sintética inexistente"}}""");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly AzureScenario _s;
        public StubHandler(AzureScenario s) => _s = s;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var (status, json) = _s.Respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
