using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AegisScore.Application.Knight;

namespace AegisScore.Connectors.Microsoft.Knight.Azure;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] PLANO de leitura do Azure Resource Manager
// ============================================================================
// Tabela FECHADA: para cada família de leitura, que lista é feita em cada assinatura (versão ESTÁVEL da API — conferida
// em Azure/azure-rest-api-specs), que caminhos de cada recurso são copiados e que configurações filhas são lidas. Só GET.
// Um caminho fora desta tabela nunca é gravado — é o que impede que, por exemplo, variáveis de ambiente de contêiner ou
// cadeias de conexão acabem no ADM.

/// <summary>Como uma leitura filha vira fato.</summary>
internal enum ArmChildMode
{
    /// <summary>GET de um objeto (<c>{id}/blobServices/default</c>): os caminhos viram fatos <c>chave:caminho</c>.</summary>
    Object,

    /// <summary>GET de uma lista (<c>{id}/firewallRules</c>): os itens, reduzidos aos caminhos, viram o fato <c>chave:items</c>.</summary>
    ListToFact,

    /// <summary>GET de uma lista cujos itens são recursos avaliados por si (slots, bancos): viram documentos próprios.</summary>
    ExpandAsResources,
}

/// <summary>Lista EMBUTIDA na resposta (regras de um NSG, sub-redes de uma VNet): os itens, reduzidos aos caminhos, viram o fato <c>chave:items</c>.</summary>
internal sealed record ArmInline(string Key, string ArrayPath, string[] Keep);

internal sealed record ArmChild(
    string Key,
    string Suffix,
    string ApiVersion,
    string[] Keep,
    ArmChildMode Mode = ArmChildMode.Object,
    ArmChild[]? ItemChildren = null,
    Func<string, bool>? ItemNameFilter = null,
    // [AEGIS-KNIGHT-COVERAGE-04] Lista cujo item NÃO pode ser copiado nem por caminho (pode conter segredo): o item vira um
    // resumo calculado no coletor, e só o resumo é gravado.
    Func<JsonElement, JsonObject>? Summarize = null);

/// <summary>
/// Uma leitura de família. <see cref="ListPath"/> é relativo à assinatura (<c>providers/Microsoft.Storage/storageAccounts</c>)
/// ou, com <see cref="TenantScope"/>, absoluto a partir da raiz da ARM. <see cref="GenericType"/> usa a listagem genérica
/// de recursos (estável) quando o provedor não lista por assinatura ou só tem versão preview: só a identidade é lida,
/// e, havendo <see cref="GetByIdApiVersion"/>, o recurso é lido pelo id.
/// </summary>
internal sealed record ArmRead(
    KnightCapability Family,
    string Label,
    string ListPath,
    string ApiVersion,
    string[] Keep,
    ArmChild[] Children,
    bool TenantScope = false,
    string? GenericType = null,
    string? GetByIdApiVersion = null,
    Func<string, bool>? ItemNameFilter = null,
    int Max = 1000,
    ArmInline[]? Inline = null);

internal static class AzureArmPlan
{
    public const string ArmHost = "https://management.azure.com";
    public const string ArmScope = "https://management.azure.com/.default";
    public const string VaultScope = "https://vault.azure.net/.default";
    public const string SubscriptionsApi = "2022-12-01";
    public const string GenericResourcesApi = "2021-04-01";
    public const string VaultDataApi = "7.4";

    private static string[] K(params string[] paths) => paths;

    private const string Net = "2023-11-01";
    private const string Web = "2023-12-01";
    private const string Sql = "2023-08-01";

    private static readonly string[] Pe = { "properties.privateEndpointConnections" };

    // ---- App Service: o mesmo conjunto vale para aplicativos, funções e slots -------------------------------
    private static readonly string[] SiteKeep = K(
        "properties.httpsOnly", "properties.clientCertEnabled", "properties.clientCertMode", "properties.publicNetworkAccess",
        "properties.virtualNetworkSubnetId", "properties.vnetRouteAllEnabled", "properties.vnetImagePullEnabled",
        "properties.vnetContentShareEnabled", "properties.endToEndEncryptionEnabled", "properties.serverFarmId",
        "properties.hostingEnvironmentProfile.id", "properties.privateEndpointConnections", "identity.type");

    private static readonly ArmChild[] SiteChildren =
    {
        new("web", "/config/web", Web, K("properties.minTlsVersion", "properties.ftpsState", "properties.http20Enabled",
            "properties.remoteDebuggingEnabled", "properties.javaVersion", "properties.pythonVersion", "properties.phpVersion",
            "properties.linuxFxVersion", "properties.windowsFxVersion", "properties.cors.allowedOrigins", "properties.vnetRouteAllEnabled")),
        new("ftp", "/basicPublishingCredentialsPolicies/ftp", Web, K("properties.allow")),
        new("scm", "/basicPublishingCredentialsPolicies/scm", Web, K("properties.allow")),
        new("auth", "/config/authsettingsV2", Web, K("properties.platform.enabled")),
        // Os endpoints privados de um aplicativo são uma SUBCOLEÇÃO (não vêm no recurso do site). O id do endpoint liga a
        // conexão ao recurso Microsoft.Network/privateEndpoints e aos grupos de zonas DNS dele.
        new("pe", "/privateEndpointConnections", Web, K("name", "properties.privateLinkServiceConnectionState.status",
            "properties.privateEndpoint.id"), ArmChildMode.ListToFact),
    };

    public static IReadOnlyList<ArmRead> Reads { get; } = new ArmRead[]
    {
        // ---- Autorização e governança da assinatura ------------------------------------------------------
        new(KnightCapability.AzureAuthorization, "atribuições de papel",
            // Sem filtro: a lista da assinatura traz as atribuições na assinatura, abaixo dela (grupos, recursos) e as herdadas
            // de cima (grupos de gerenciamento e raiz) — o escopo de cada uma fica no fato properties.scope.
            "providers/Microsoft.Authorization/roleAssignments", "2022-04-01",
            K("properties.roleDefinitionId", "properties.principalId", "properties.principalType", "properties.scope"),
            Array.Empty<ArmChild>(), Max: 5000),
        new(KnightCapability.AzureAuthorization, "papéis personalizados",
            "providers/Microsoft.Authorization/roleDefinitions?$filter=type%20eq%20'CustomRole'", "2022-04-01",
            K("properties.roleName", "properties.type", "properties.permissions", "properties.assignableScopes"),
            Array.Empty<ArmChild>()),
        new(KnightCapability.AzureAuthorization, "bloqueios de recurso",
            "providers/Microsoft.Authorization/locks", "2020-05-01", K("properties.level"), Array.Empty<ArmChild>(), Max: 5000),
        new(KnightCapability.AzurePolicy, "atribuição de política do Defender para Nuvem",
            "providers/Microsoft.Authorization/policyAssignments", "2023-04-01",
            K("properties.displayName", "properties.policyDefinitionId", "properties.enforcementMode", "properties.parameters"),
            Array.Empty<ArmChild>(), ItemNameFilter: n => string.Equals(n, "SecurityCenterBuiltIn", StringComparison.OrdinalIgnoreCase)),

        // ---- Defender para Nuvem -------------------------------------------------------------------------
        new(KnightCapability.AzureDefenderForCloud, "planos do Defender para Nuvem",
            "providers/Microsoft.Security/pricings", "2024-01-01",
            K("properties.pricingTier", "properties.subPlan", "properties.extensions", "properties.deprecated"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureDefenderForCloud, "avaliação de vulnerabilidades de servidores",
            "providers/Microsoft.Security/serverVulnerabilityAssessmentsSettings", "2023-05-01",
            K("kind", "properties.selectedProvider"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureDefenderForCloud, "integrações do Defender para Nuvem",
            "providers/Microsoft.Security/settings", "2022-05-01", K("kind", "properties.enabled"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureDefenderForCloud, "workspaces do EASM", "", GenericResourcesApi, Array.Empty<string>(),
            Array.Empty<ArmChild>(), GenericType: "Microsoft.Easm/workspaces"),
        new(KnightCapability.AzureDefenderForCloud, "hubs IoT", "", GenericResourcesApi, Array.Empty<string>(),
            Array.Empty<ArmChild>(), GenericType: "Microsoft.Devices/IotHubs"),
        new(KnightCapability.AzureDefenderForCloud, "soluções do Defender para IoT", "", GenericResourcesApi, Array.Empty<string>(),
            Array.Empty<ArmChild>(), GenericType: "Microsoft.Security/iotSecuritySolutions"),

        // ---- Azure Monitor -------------------------------------------------------------------------------
        new(KnightCapability.AzureMonitor, "alertas do log de atividades",
            "providers/Microsoft.Insights/activityLogAlerts", "2020-10-01",
            K("properties.enabled", "properties.condition", "properties.scopes", "properties.actions.actionGroups"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureMonitor, "Application Insights",
            "providers/Microsoft.Insights/components", "2020-02-02", K("kind"), Array.Empty<ArmChild>()),

        // ---- Rede ----------------------------------------------------------------------------------------
        new(KnightCapability.AzureNetworking, "grupos de segurança de rede",
            "providers/Microsoft.Network/networkSecurityGroups", Net, K("properties.subnets"), Array.Empty<ArmChild>(),
            Inline: new[] { new ArmInline("rules", "properties.securityRules", K("name", "properties.direction", "properties.access",
                "properties.protocol", "properties.priority", "properties.sourceAddressPrefix", "properties.sourceAddressPrefixes",
                "properties.destinationPortRange", "properties.destinationPortRanges")) }),
        new(KnightCapability.AzureNetworking, "redes virtuais",
            "providers/Microsoft.Network/virtualNetworks", Net,
            K("properties.enableDdosProtection", "properties.ddosProtectionPlan.id"), Array.Empty<ArmChild>(),
            Inline: new[] { new ArmInline("subnets", "properties.subnets", K("id", "name", "properties.networkSecurityGroup.id",
                "properties.addressPrefix", "properties.delegations")) }),
        new(KnightCapability.AzureNetworking, "IPs públicos",
            "providers/Microsoft.Network/publicIPAddresses", Net, K("properties.ipConfiguration.id"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureNetworking, "gateways de aplicativo",
            "providers/Microsoft.Network/applicationGateways", Net,
            K("properties.sku", "properties.sslPolicy", "properties.enableHttp2", "properties.webApplicationFirewallConfiguration",
              "properties.firewallPolicy.id"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureNetworking, "políticas de WAF",
            "providers/Microsoft.Network/ApplicationGatewayWebApplicationFirewallPolicies", Net,
            K("properties.policySettings", "properties.managedRules.managedRuleSets", "properties.applicationGateways"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureNetworking, "Network Watcher",
            "providers/Microsoft.Network/networkWatchers", Net, Array.Empty<string>(),
            new[] { new ArmChild("flowLogs", "/flowLogs", Net, K("properties.targetResourceId", "properties.enabled",
                "properties.retentionPolicy", "properties.flowAnalyticsConfiguration"), ArmChildMode.ListToFact) }),
        // [AEGIS-KNIGHT-COVERAGE-04] Endpoints privados e os grupos de zonas DNS privadas de cada um (Microsoft.Network,
        // versão estável): é o que diz se o nome do serviço resolve para o endereço privado dentro da rede.
        new(KnightCapability.AzureNetworking, "endpoints privados",
            "providers/Microsoft.Network/privateEndpoints", Net, Array.Empty<string>(),
            new[] { new ArmChild("dns", "/privateDnsZoneGroups", Net, K("name", "properties.privateDnsZoneConfigs"), ArmChildMode.ListToFact) },
            Inline: new[]
            {
                new ArmInline("links", "properties.privateLinkServiceConnections", K("properties.privateLinkServiceId", "properties.groupIds")),
                new ArmInline("manualLinks", "properties.manualPrivateLinkServiceConnections", K("properties.privateLinkServiceId", "properties.groupIds")),
            }),
        new(KnightCapability.AzureNetworking, "Azure Bastion",
            "providers/Microsoft.Network/bastionHosts", Net, Array.Empty<string>(), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureNetworking, "gateways de rede virtual", "", GenericResourcesApi,
            K("properties.gatewayType", "properties.vpnClientConfiguration.vpnAuthenticationTypes",
              "properties.vpnClientConfiguration.vpnClientAddressPool"),
            Array.Empty<ArmChild>(), GenericType: "Microsoft.Network/virtualNetworkGateways", GetByIdApiVersion: Net),

        // ---- Armazenamento -------------------------------------------------------------------------------
        new(KnightCapability.AzureStorage, "contas de armazenamento",
            "providers/Microsoft.Storage/storageAccounts", "2023-05-01",
            K("properties.minimumTlsVersion", "properties.allowBlobPublicAccess", "properties.supportsHttpsTrafficOnly",
              "properties.allowSharedKeyAccess", "properties.publicNetworkAccess", "properties.networkAcls.defaultAction",
              "properties.networkAcls.bypass", "properties.allowCrossTenantReplication", "properties.defaultToOAuthAuthentication",
              "properties.keyPolicy.keyExpirationPeriodInDays", "properties.keyCreationTime", "properties.privateEndpointConnections",
              "properties.encryption.keySource"),
            new[]
            {
                new ArmChild("blob", "/blobServices/default", "2023-05-01", K("properties.deleteRetentionPolicy",
                    "properties.containerDeleteRetentionPolicy", "properties.isVersioningEnabled")),
                new ArmChild("file", "/fileServices/default", "2023-05-01", K("properties.shareDeleteRetentionPolicy",
                    "properties.protocolSettings.smb")),
            }),

        // ---- Key Vault (metadados pelo Resource Manager — nenhum valor de segredo é lido) ---------------------
        new(KnightCapability.AzureKeyVault, "cofres de chaves",
            "providers/Microsoft.KeyVault/vaults", "2023-07-01",
            K("properties.enablePurgeProtection", "properties.enableSoftDelete", "properties.enableRbacAuthorization",
              "properties.publicNetworkAccess", "properties.networkAcls.defaultAction", "properties.privateEndpointConnections",
              "properties.vaultUri"),
            new[]
            {
                new ArmChild("keys", "/keys", "2023-07-01", K("name", "properties.attributes.enabled", "properties.attributes.exp",
                    "properties.rotationPolicy.lifetimeActions"), ArmChildMode.ListToFact),
                new ArmChild("secrets", "/secrets", "2023-07-01", K("name", "properties.attributes.enabled", "properties.attributes.exp"),
                    ArmChildMode.ListToFact),
            }),

        // ---- Computação ----------------------------------------------------------------------------------
        new(KnightCapability.AzureCompute, "máquinas virtuais",
            "providers/Microsoft.Compute/virtualMachines", "2024-07-01",
            K("properties.storageProfile.osDisk.managedDisk.id", "properties.storageProfile.osDisk.vhd.uri",
              "properties.storageProfile.osDisk.encryptionSettings.enabled", "properties.securityProfile"),
            new[]
            {
                new ArmChild("ext", "/extensions", "2024-07-01", K("name", "properties.publisher", "properties.type",
                    "properties.provisioningState"), ArmChildMode.ListToFact),
                // [AEGIS-KNIGHT-COVERAGE-04] A avaliação do Defender para Nuvem "atualizações do sistema devem ser instaladas"
                // da máquina (a mesma que a política interna f85bf3e0 audita). 404 = a avaliação não existe para a máquina.
                new ArmChild("updates", "/providers/Microsoft.Security/assessments/" + SystemUpdatesAssessment, "2021-06-01",
                    K("properties.status.code", "properties.status.cause", "properties.displayName")),
            }),
        new(KnightCapability.AzureCompute, "discos gerenciados",
            "providers/Microsoft.Compute/disks", "2023-10-02",
            K("managedBy", "properties.encryption.type", "properties.diskState", "properties.networkAccessPolicy",
              "properties.publicNetworkAccess", "properties.dataAccessAuthMode"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureCompute, "Container Instances",
            "providers/Microsoft.ContainerInstance/containerGroups", "2023-05-01",
            K("properties.ipAddress.type", "properties.subnetIds", "identity.type"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureCompute, "contas do Batch",
            "providers/Microsoft.Batch/batchAccounts", "2024-02-01",
            K("properties.encryption.keySource", "properties.allowedAuthenticationModes", "properties.publicNetworkAccess",
              "properties.privateEndpointConnections"),
            new[] { new ArmChild("pools", "/pools", "2024-02-01", K("name",
                "properties.deploymentConfiguration.virtualMachineConfiguration.diskEncryptionConfiguration.targets"), ArmChildMode.ListToFact) }),

        // ---- App Service ---------------------------------------------------------------------------------
        new(KnightCapability.AzureAppService, "aplicativos e funções do App Service",
            "providers/Microsoft.Web/sites", Web, SiteKeep,
            SiteChildren.Append(new ArmChild("slots", "/slots", Web, SiteKeep, ArmChildMode.ExpandAsResources, SiteChildren)).ToArray()),
        new(KnightCapability.AzureAppService, "planos do App Service",
            "providers/Microsoft.Web/serverfarms", Web, K("sku.tier", "sku.name"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureAppService, "ambientes do App Service (ASE)",
            "providers/Microsoft.Web/hostingEnvironments", Web,
            K("properties.internalLoadBalancingMode", "properties.clusterSettings"), Array.Empty<ArmChild>()),

        // ---- Bancos de dados -----------------------------------------------------------------------------
        new(KnightCapability.AzureDatabases, "servidores SQL",
            "providers/Microsoft.Sql/servers", Sql,
            K("properties.publicNetworkAccess", "properties.minimalTlsVersion", "properties.administrators"),
            new[]
            {
                new ArmChild("audit", "/auditingSettings/default", Sql, K("properties.state", "properties.retentionDays")),
                new ArmChild("fw", "/firewallRules", Sql, K("name", "properties.startIpAddress", "properties.endIpAddress"), ArmChildMode.ListToFact),
                new ArmChild("tdeProtector", "/encryptionProtector/current", Sql, K("properties.serverKeyType")),
                new ArmChild("aadOnly", "/azureADOnlyAuthentications/Default", Sql, K("properties.azureADOnlyAuthentication")),
                new ArmChild("databases", "/databases", Sql, Array.Empty<string>(), ArmChildMode.ExpandAsResources,
                    new[] { new ArmChild("tde", "/transparentDataEncryption/current", Sql, K("properties.state")) },
                    ItemNameFilter: n => !string.Equals(n, "master", StringComparison.OrdinalIgnoreCase)),
            }),
        new(KnightCapability.AzureDatabases, "instâncias gerenciadas de SQL",
            "providers/Microsoft.Sql/managedInstances", Sql, K("properties.minimalTlsVersion", "properties.publicDataEndpointEnabled"),
            new[]
            {
                new ArmChild("tdeProtector", "/encryptionProtector/current", Sql, K("properties.serverKeyType")),
                new ArmChild("databases", "/databases", Sql, Array.Empty<string>(), ArmChildMode.ExpandAsResources,
                    new[] { new ArmChild("tde", "/transparentDataEncryption/current", Sql, K("properties.state")) }),
            }),
        new(KnightCapability.AzureDatabases, "servidores flexíveis de PostgreSQL",
            "providers/Microsoft.DBforPostgreSQL/flexibleServers", "2024-08-01",
            K("properties.dataEncryption.type", "properties.network.publicNetworkAccess", "properties.authConfig",
              "properties.privateEndpointConnections"),
            ConfigChildren("2024-08-01", new[] { "connection_throttle.enable", "logfiles.retention_days", "log_checkpoints",
                "log_disconnections", "log_connections", "require_secure_transport", "ssl_min_protocol_version" })),
        new(KnightCapability.AzureDatabases, "servidores flexíveis de MySQL",
            "providers/Microsoft.DBforMySQL/flexibleServers", "2023-12-30",
            K("properties.dataEncryption.type", "properties.network.publicNetworkAccess", "properties.privateEndpointConnections"),
            ConfigChildren("2023-12-30", new[] { "audit_log_enabled", "audit_log_events", "error_server_log_file", "require_secure_transport",
                "tls_version", "aad_auth_only" }, new ArmChild("admins", "/administrators", "2023-12-30",
                    K("properties.administratorType"), ArmChildMode.ListToFact))),
        new(KnightCapability.AzureDatabases, "contas do Cosmos DB",
            "providers/Microsoft.DocumentDB/databaseAccounts", "2024-08-15",
            K("properties.isVirtualNetworkFilterEnabled", "properties.ipRules", "properties.publicNetworkAccess",
              "properties.disableLocalAuth", "properties.keyVaultKeyUri", "properties.privateEndpointConnections",
              "properties.virtualNetworkRules"), Array.Empty<ArmChild>()),
        new(KnightCapability.AzureDatabases, "caches do Redis",
            "providers/Microsoft.Cache/redis", "2024-03-01",
            K("properties.enableNonSslPort", "properties.minimumTlsVersion", "properties.publicNetworkAccess",
              "properties.privateEndpointConnections", "properties.redisConfiguration.aad-enabled",
              "properties.disableAccessKeyAuthentication", "properties.updateChannel", "identity.type"),
            new[] { new ArmChild("policies", "/accessPolicies", "2024-03-01", K("name", "properties.type"), ArmChildMode.ListToFact) }),
        new(KnightCapability.AzureDatabases, "fábricas do Data Factory",
            "providers/Microsoft.DataFactory/factories", "2018-06-01",
            K("properties.encryption.vaultBaseUrl", "properties.encryption.keyName", "identity.type"),
            // [AEGIS-KNIGHT-COVERAGE-04] Serviços vinculados: só o RESUMO de onde vem cada credencial (Key Vault, segredo
            // guardado na fábrica, credencial criptografada, texto) — nenhum valor, cadeia de conexão ou nome de segredo.
            new[] { new ArmChild("linked", "/linkedservices", "2018-06-01", Array.Empty<string>(), ArmChildMode.ListToFact,
                Summarize: LinkedServiceCredentials) }),
        new(KnightCapability.AzureDatabases, "clusters do Redis Enterprise",
            "providers/Microsoft.Cache/redisEnterprise", "2025-07-01",
            K("properties.minimumTlsVersion", "properties.publicNetworkAccess", "properties.privateEndpointConnections",
              "properties.encryption.customerManagedKeyEncryption.keyEncryptionKeyUrl", "identity.type"),
            new[] { new ArmChild("databases", "/databases", "2025-07-01", K("name", "properties.accessKeysAuthentication"),
                ArmChildMode.ListToFact) }),

        // ---- Databricks ----------------------------------------------------------------------------------
        new(KnightCapability.AzureDatabricks, "workspaces do Databricks",
            "providers/Microsoft.Databricks/workspaces", "2024-05-01",
            K("properties.parameters.customVirtualNetworkId", "properties.parameters.enableNoPublicIp",
              "properties.parameters.customPublicSubnetName", "properties.parameters.customPrivateSubnetName",
              "properties.encryption", "properties.parameters.encryption", "properties.publicNetworkAccess",
              "properties.requiredNsgRules", "properties.privateEndpointConnections"), Array.Empty<ArmChild>()),
    };

    /// <summary>
    /// Chave da avaliação "System updates should be installed on your machines (powered by Update Center)" do Defender para
    /// Nuvem — a mesma que a política interna f85bf3e0-d513-442e-89c3-1784ad63382b audita em Microsoft.Compute/virtualMachines.
    /// </summary>
    public const string SystemUpdatesAssessment = "e1145ab1-eb4f-43d8-911b-36ddf771d13f";

    /// <summary>Campos que, como TEXTO, guardam credencial (nomes da documentação dos conectores do Data Factory).</summary>
    private static readonly HashSet<string> CredentialFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "accountKey", "servicePrincipalKey", "servicePrincipalCredential", "clientSecret", "sasToken", "sasUri",
        "accessToken", "apiToken", "secretAccessKey", "sessionToken", "connectionString", "credString", "encryptedCredential",
        "key", "pwd", "privateKey", "passPhrase", "clientKey", "securityToken", "applicationKey", "functionKey",
    };

    private static readonly string[] SecretMarkers =
        { "password=", "pwd=", "accountkey=", "sharedaccesskey=", "sharedaccesssignature=", "sig=", "secret=" };

    /// <summary>
    /// Resumo de UM serviço vinculado do Data Factory: tipo e quantas credenciais vêm de cada origem. Uma referência
    /// AzureKeyVaultSecret é a forma esperada; SecureString (segredo guardado na própria fábrica), encryptedCredential
    /// (credencial criptografada pelo runtime de integração) e texto com credencial não são. O valor nunca sai daqui.
    /// </summary>
    internal static JsonObject LinkedServiceCredentials(JsonElement item)
    {
        int kv = 0, secure = 0, plain = 0, encrypted = 0;
        var fields = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var type = item.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                   && props.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        void Walk(JsonElement e, string? name, int depth)
        {
            if (depth > 8) return;
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var kind = e.TryGetProperty("type", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                    if (string.Equals(kind, "AzureKeyVaultSecret", StringComparison.OrdinalIgnoreCase)) { kv++; if (name is not null) fields.Add(name); return; }
                    if (string.Equals(kind, "SecureString", StringComparison.OrdinalIgnoreCase)) { secure++; if (name is not null) fields.Add(name); return; }
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name, depth + 1);
                    return;
                case JsonValueKind.Array:
                    foreach (var x in e.EnumerateArray()) Walk(x, name, depth + 1);
                    return;
                case JsonValueKind.String when name is not null && CredentialFields.Contains(name):
                    var v = e.GetString() ?? "";
                    if (v.Length == 0 || v.StartsWith("@", StringComparison.Ordinal)) return; // vazio ou expressão (parâmetro)
                    if (string.Equals(name, "encryptedCredential", StringComparison.OrdinalIgnoreCase)) { encrypted++; fields.Add(name); return; }
                    if (string.Equals(name, "connectionString", StringComparison.OrdinalIgnoreCase)
                        && !SecretMarkers.Any(m => v.Contains(m, StringComparison.OrdinalIgnoreCase))) return; // sem credencial no texto
                    plain++; fields.Add(name);
                    return;
            }
        }

        if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty("typeProperties", out var tp)) Walk(tp, null, 0);
        return new JsonObject
        {
            ["name"] = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            ["type"] = type,
            ["keyVaultRefs"] = kv,
            ["factorySecrets"] = secure,
            ["encryptedCredentials"] = encrypted,
            ["plainCredentials"] = plain,
            ["credentialFields"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        };
    }

    /// <summary>Lê cada parâmetro de servidor pelo nome (só o valor), e uma leitura extra opcional.</summary>
    private static ArmChild[] ConfigChildren(string api, string[] names, ArmChild? extra = null)
    {
        var list = new List<ArmChild>();
        foreach (var name in names)
            list.Add(new ArmChild("cfg." + name, "/configurations/" + name, api, K("properties.value")));
        if (extra is not null) list.Add(extra);
        return list.ToArray();
    }

    /// <summary>Configuração de diagnóstico do Microsoft Entra ID (escopo do locatário, versão estável 2017-04-01).</summary>
    public static readonly ArmRead EntraDiagnostics = new(KnightCapability.AzureTenantDiagnostics, "configurações de diagnóstico do Microsoft Entra ID",
        "providers/microsoft.aadiam/diagnosticSettings", "2017-04-01",
        K("properties.logs", "properties.workspaceId", "properties.storageAccountId", "properties.eventHubAuthorizationRuleId"),
        Array.Empty<ArmChild>(), TenantScope: true);

    /// <summary>Política do locatário para assinaturas entrando e saindo (escopo do locatário, versão estável 2021-10-01).</summary>
    public const string SubscriptionPolicyPath = "providers/Microsoft.Subscription/policies/default";
    public const string SubscriptionPolicyApi = "2021-10-01";

    /// <summary>
    /// Pilhas de runtime do App Service (versões suportadas, obsoletas e fim de suporte), no escopo do PROVEDOR — a mesma
    /// para todo locatário. É a régua das regras de versão de linguagem: a versão em uso é comparada com o que a própria
    /// Microsoft publica, nunca com uma lista fixa no código.
    /// </summary>
    public static readonly (string Type, string Path)[] RuntimeStacks =
    {
        ("Microsoft.Web/webAppStacks", "providers/Microsoft.Web/webAppStacks"),
        ("Microsoft.Web/functionAppStacks", "providers/Microsoft.Web/functionAppStacks"),
    };
}
