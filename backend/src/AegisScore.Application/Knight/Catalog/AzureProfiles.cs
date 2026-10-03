using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Catalog;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Perfis e impactos dos controles do Azure: O PROBLEMA → POR QUE IMPORTA → O QUE SE ESPERA →
/// O QUE O ACHADO NÃO COMPROVA, e o impacto potencial no limite do que a condição permite. Textos autorais; a documentação
/// citada é só a oficial da Microsoft, com endereço conferido (sem redirecionamento).
/// </summary>
public static class AzureProfiles
{
    private static KnightControlReference Doc(string title, string url) => new("Microsoft Learn", null, title, url);
    private static KnightControlReference[] Docs(params KnightControlReference[] d) => d;

    private const string L = "https://learn.microsoft.com/en-us/";

    // ---- Documentação oficial (endereços finais conferidos em 01/10/2026) -----------------------------------
    private static readonly KnightControlReference DocElevate = Doc("Elevar o acesso para gerenciar todas as assinaturas e grupos de gerenciamento", L + "azure/role-based-access-control/elevate-access-global-admin");
    private static readonly KnightControlReference DocRoleAssignApi = Doc("API: listar atribuições de papel de uma assinatura", L + "rest/api/authorization/role-assignments/list-for-subscription?view=rest-authorization-2022-04-01");
    private static readonly KnightControlReference DocCustomRoles = Doc("Papéis personalizados do Azure", L + "azure/role-based-access-control/custom-roles");
    private static readonly KnightControlReference DocLocks = Doc("Bloquear recursos para impedir alterações inesperadas", L + "azure/azure-resource-manager/management/lock-resources");
    private static readonly KnightControlReference DocSubPolicy = Doc("Gerenciar a política de assinaturas do Azure", L + "azure/cost-management-billing/manage/manage-azure-subscription-policy");
    private static readonly KnightControlReference DocRbacBest = Doc("Práticas recomendadas do Azure RBAC", L + "azure/role-based-access-control/best-practices");
    private static readonly KnightControlReference DocNsgFlow = Doc("Logs de fluxo de grupos de segurança de rede", L + "azure/network-watcher/nsg-flow-logs-overview");
    private static readonly KnightControlReference DocVnetFlow = Doc("Logs de fluxo de rede virtual", L + "azure/network-watcher/vnet-flow-logs-overview");
    private static readonly KnightControlReference DocTraffic = Doc("Análise de tráfego do Network Watcher", L + "azure/network-watcher/traffic-analytics");
    private static readonly KnightControlReference DocGraphLogs = Doc("Logs de atividade do Microsoft Graph", L + "graph/microsoft-graph-activity-logs-overview");
    private static readonly KnightControlReference DocEntraDiag = Doc("Integrar os logs do Microsoft Entra ao Azure Monitor", L + "entra/identity/monitoring-health/howto-integrate-activity-logs-with-azure-monitor-logs");
    private static readonly KnightControlReference DocActivityAlert = Doc("Criar regra de alerta do log de atividades", L + "azure/azure-monitor/alerts/alerts-create-activity-log-alert-rule");
    private static readonly KnightControlReference DocServiceHealth = Doc("Alertas de notificações do Service Health", L + "azure/service-health/alerts-activity-log-service-notifications-portal");
    private static readonly KnightControlReference DocAppInsights = Doc("Visão geral do Application Insights", L + "azure/azure-monitor/app/app-insights-overview");
    private static readonly KnightControlReference DocMdc = Doc("O que é o Microsoft Defender para Nuvem", L + "azure/defender-for-cloud/defender-for-cloud-introduction");
    private static readonly KnightControlReference DocPricingApi = Doc("API: planos do Defender para Nuvem (pricings)", L + "rest/api/defenderforcloud/pricings/list?view=rest-defenderforcloud-2024-01-01");
    private static readonly KnightControlReference DocCspm = Doc("Gerenciamento da postura de segurança na nuvem (CSPM)", L + "azure/defender-for-cloud/concept-cloud-security-posture-management");
    private static readonly KnightControlReference DocDfApis = Doc("Defender para APIs", L + "azure/defender-for-cloud/defender-for-apis-introduction");
    private static readonly KnightControlReference DocDfServers = Doc("Defender para Servidores", L + "azure/defender-for-cloud/defender-for-servers-overview");
    private static readonly KnightControlReference DocDfContainers = Doc("Defender para Contêineres", L + "azure/defender-for-cloud/defender-for-containers-introduction");
    private static readonly KnightControlReference DocDfStorage = Doc("Defender para Armazenamento", L + "azure/defender-for-cloud/defender-for-storage-introduction");
    private static readonly KnightControlReference DocDfAppService = Doc("Defender para App Service", L + "azure/defender-for-cloud/defender-for-app-service-introduction");
    private static readonly KnightControlReference DocDfCosmos = Doc("Defender para Azure Cosmos DB", L + "azure/defender-for-cloud/concept-defender-for-cosmos");
    private static readonly KnightControlReference DocDfDatabases = Doc("Defender para bancos de dados", L + "azure/defender-for-cloud/defender-for-databases-introduction");
    private static readonly KnightControlReference DocDfSql = Doc("Defender para Azure SQL", L + "azure/defender-for-cloud/defender-for-sql-introduction");
    private static readonly KnightControlReference DocDfSqlVm = Doc("Defender para SQL em máquinas", L + "azure/defender-for-cloud/defender-for-sql-usage");
    private static readonly KnightControlReference DocDfKeyVault = Doc("Defender para Key Vault", L + "azure/defender-for-cloud/defender-for-key-vault-introduction");
    private static readonly KnightControlReference DocDfArm = Doc("Defender para Resource Manager", L + "azure/defender-for-cloud/defender-for-resource-manager-introduction");
    private static readonly KnightControlReference DocMdvm = Doc("Avaliação de vulnerabilidades com o Defender Vulnerability Management", L + "azure/defender-for-cloud/deploy-vulnerability-assessment-defender-vulnerability-management");
    private static readonly KnightControlReference DocMde = Doc("Integração do Defender para Nuvem com o Defender para Endpoint", L + "azure/defender-for-cloud/integration-defender-for-endpoint");
    private static readonly KnightControlReference DocAgentless = Doc("Coleta de dados sem agente", L + "azure/defender-for-cloud/concept-agentless-data-collection");
    private static readonly KnightControlReference DocFim = Doc("Monitoramento de integridade de arquivos", L + "azure/defender-for-cloud/file-integrity-monitoring-overview");
    private static readonly KnightControlReference DocMcsb = Doc("Políticas de segurança do Defender para Nuvem", L + "azure/defender-for-cloud/security-policy-concept");
    private static readonly KnightControlReference DocEasm = Doc("Microsoft Defender External Attack Surface Management", L + "azure/external-attack-surface-management/overview");
    private static readonly KnightControlReference DocIot = Doc("Defender para IoT para criadores de dispositivos", L + "azure/defender-for-iot/device-builders/overview");
    private static readonly KnightControlReference DocNsg = Doc("Grupos de segurança de rede", L + "azure/virtual-network/network-security-groups-overview");
    private static readonly KnightControlReference DocWatcher = Doc("Visão geral do Network Watcher", L + "azure/network-watcher/network-watcher-overview");
    private static readonly KnightControlReference DocNsgFlowManage = Doc("Gerenciar logs de fluxo de NSG (retenção)", L + "azure/network-watcher/nsg-flow-logs-manage");
    private static readonly KnightControlReference DocVnetFlowManage = Doc("Gerenciar logs de fluxo de rede virtual (retenção)", L + "azure/network-watcher/vnet-flow-logs-manage");
    private static readonly KnightControlReference DocVpnEntra = Doc("Autenticação do Microsoft Entra ID para VPN ponto a site", L + "azure/vpn-gateway/openvpn-azure-ad-tenant");
    private static readonly KnightControlReference DocWaf = Doc("WAF no Application Gateway", L + "azure/web-application-firewall/ag/ag-overview");
    private static readonly KnightControlReference DocAgwTls = Doc("Políticas de TLS do Application Gateway", L + "azure/application-gateway/application-gateway-ssl-policy-overview");
    private static readonly KnightControlReference DocAgwFeatures = Doc("Recursos do Application Gateway (inclui HTTP/2)", L + "azure/application-gateway/features");
    private static readonly KnightControlReference DocWafBody = Doc("Limites de tamanho e inspeção do corpo no WAF", L + "azure/web-application-firewall/ag/application-gateway-waf-request-size-limits");
    private static readonly KnightControlReference DocWafBot = Doc("Proteção contra bots no WAF do Application Gateway", L + "azure/web-application-firewall/ag/bot-protection-overview");
    private static readonly KnightControlReference DocBastion = Doc("Visão geral do Azure Bastion", L + "azure/bastion/bastion-overview");
    private static readonly KnightControlReference DocDdos = Doc("Visão geral da Proteção DDoS do Azure", L + "azure/ddos-protection/ddos-protection-overview");
    private static readonly KnightControlReference DocFileSoftDelete = Doc("Evitar a exclusão acidental de compartilhamentos de arquivos", L + "azure/storage/files/storage-files-prevent-file-share-deletion");
    private static readonly KnightControlReference DocSmb = Doc("Protocolo SMB no Azure Files (configurações de segurança)", L + "azure/storage/files/files-smb-protocol");
    private static readonly KnightControlReference DocBlobSoftDelete = Doc("Exclusão reversível de blobs", L + "azure/storage/blobs/soft-delete-blob-overview");
    private static readonly KnightControlReference DocContainerSoftDelete = Doc("Exclusão reversível de contêineres", L + "azure/storage/blobs/soft-delete-container-overview");
    private static readonly KnightControlReference DocVersioning = Doc("Controle de versão de blobs", L + "azure/storage/blobs/versioning-overview");
    private static readonly KnightControlReference DocStorageKeys = Doc("Gerenciar chaves de acesso da conta de armazenamento", L + "azure/storage/common/storage-account-keys-manage");
    private static readonly KnightControlReference DocSharedKey = Doc("Impedir a autorização por chave compartilhada", L + "azure/storage/common/shared-key-authorization-prevent");
    private static readonly KnightControlReference DocStoragePe = Doc("Endpoints privados para o Armazenamento do Azure", L + "azure/storage/common/storage-private-endpoints");
    private static readonly KnightControlReference DocStorageNet = Doc("Segurança de rede do Armazenamento do Azure", L + "azure/storage/common/storage-network-security");
    private static readonly KnightControlReference DocStoragePortalAuth = Doc("Autorização de dados de blob no portal", L + "azure/storage/blobs/authorize-data-operations-portal");
    private static readonly KnightControlReference DocSecureTransfer = Doc("Exigir transferência segura", L + "azure/storage/common/storage-require-secure-transfer");
    private static readonly KnightControlReference DocStorageTls = Doc("Versão mínima de TLS do armazenamento", L + "azure/storage/common/transport-layer-security-configure-minimum-version");
    private static readonly KnightControlReference DocCrossTenant = Doc("Impedir a replicação de objetos entre locatários", L + "azure/storage/blobs/object-replication-prevent-cross-tenant-policies");
    private static readonly KnightControlReference DocAnonymous = Doc("Impedir o acesso anônimo de leitura a blobs", L + "azure/storage/blobs/anonymous-read-access-prevent");
    private static readonly KnightControlReference DocStorageLock = Doc("Aplicar bloqueio do Resource Manager a uma conta de armazenamento", L + "azure/storage/common/lock-account-resource");
    private static readonly KnightControlReference DocStorageApi = Doc("API: propriedades da conta de armazenamento", L + "rest/api/storagerp/storage-accounts/get-properties?view=rest-storagerp-2026-06-01");
    private static readonly KnightControlReference DocKvSecure = Doc("Proteger o Azure Key Vault", L + "azure/key-vault/general/secure-key-vault");
    private static readonly KnightControlReference DocKvKeys = Doc("Detalhes das chaves do Key Vault (atributos)", L + "azure/key-vault/keys/about-keys-details");
    private static readonly KnightControlReference DocKvSecrets = Doc("Sobre segredos do Key Vault", L + "azure/key-vault/secrets/about-secrets");
    private static readonly KnightControlReference DocKvSoftDelete = Doc("Exclusão reversível e proteção contra expurgo do Key Vault", L + "azure/key-vault/general/soft-delete-overview");
    private static readonly KnightControlReference DocKvRbac = Doc("Acesso ao Key Vault com o Azure RBAC", L + "azure/key-vault/general/rbac-guide");
    private static readonly KnightControlReference DocKvNetwork = Doc("Segurança de rede do Key Vault", L + "azure/key-vault/general/network-security");
    private static readonly KnightControlReference DocKvPe = Doc("Private Link no Key Vault", L + "azure/key-vault/general/private-link-service");
    private static readonly KnightControlReference DocKvRotation = Doc("Configurar a rotação automática de chaves", L + "azure/key-vault/keys/how-to-configure-key-rotation");
    private static readonly KnightControlReference DocKvCerts = Doc("Sobre certificados do Key Vault", L + "azure/key-vault/certificates/about-certificates");
    private static readonly KnightControlReference DocKvCertPolicyApi = Doc("API: política de certificado do Key Vault", L + "rest/api/keyvault/certificates/get-certificate-policy/get-certificate-policy?view=rest-keyvault-certificates-2025-07-01");
    private static readonly KnightControlReference DocAciVnet = Doc("Implantar Container Instances em rede virtual", L + "azure/container-instances/container-instances-vnet");
    private static readonly KnightControlReference DocAciIdentity = Doc("Identidades gerenciadas no Container Instances", L + "azure/container-instances/container-instances-managed-identity");
    private static readonly KnightControlReference DocBatchCmk = Doc("Chaves gerenciadas pelo cliente no Batch", L + "azure/batch/batch-customer-managed-key");
    private static readonly KnightControlReference DocBatchDisk = Doc("Criptografia de disco nos pools do Batch", L + "azure/batch/disk-encryption");
    private static readonly KnightControlReference DocBatchAad = Doc("Autenticar o Batch com o Microsoft Entra ID", L + "azure/batch/batch-aad-auth");
    private static readonly KnightControlReference DocBatchPe = Doc("Conectividade privada do Batch", L + "azure/batch/private-connectivity");
    private static readonly KnightControlReference DocBatchPublic = Doc("Acesso público de rede do Batch", L + "azure/batch/public-network-access");
    private static readonly KnightControlReference DocManagedDisks = Doc("Visão geral dos discos gerenciados", L + "azure/virtual-machines/managed-disks-overview");
    private static readonly KnightControlReference DocDiskEncryption = Doc("Criptografia do lado do servidor dos discos (inclui criptografia no host)", L + "azure/virtual-machines/disk-encryption");
    private static readonly KnightControlReference DocDiskPrivate = Doc("Restringir importação e exportação de discos com Private Link", L + "azure/virtual-machines/disks-enable-private-links-for-import-export-portal");
    private static readonly KnightControlReference DocDiskRestrict = Doc("Restringir o acesso de importação e exportação de discos", L + "azure/virtual-machines/disks-restrict-import-export-overview");
    private static readonly KnightControlReference DocTrustedLaunch = Doc("Inicialização confiável de máquinas virtuais", L + "azure/virtual-machines/trusted-launch");
    private static readonly KnightControlReference DocAde = Doc("Opções de criptografia de disco", L + "azure/virtual-machines/disk-encryption-overview");
    private static readonly KnightControlReference DocVmExtensions = Doc("Extensões de máquina virtual", L + "azure/virtual-machines/extensions/overview");
    private static readonly KnightControlReference DocLangPolicy = Doc("Política de suporte de linguagens do App Service", L + "azure/app-service/language-support-policy");
    private static readonly KnightControlReference DocStacksApi = Doc("API: pilhas de runtime do App Service", L + "rest/api/appservice/provider/get-web-app-stacks?view=rest-appservice-2026-07-15");
    private static readonly KnightControlReference DocBasicAuth = Doc("Desabilitar a autenticação básica no App Service", L + "azure/app-service/configure-basic-auth-disable");
    private static readonly KnightControlReference DocFtp = Doc("Implantar com FTP/S no App Service", L + "azure/app-service/deploy-ftp");
    private static readonly KnightControlReference DocAppConfig = Doc("Configurar um aplicativo do App Service", L + "azure/app-service/configure-common");
    private static readonly KnightControlReference DocAppTls = Doc("TLS no App Service (inclui ponta a ponta)", L + "azure/app-service/overview-tls");
    private static readonly KnightControlReference DocAppHttps = Doc("Associações TLS e imposição de HTTPS", L + "azure/app-service/configure-ssl-bindings");
    private static readonly KnightControlReference DocAppMtls = Doc("Autenticação mútua TLS no App Service", L + "azure/app-service/app-service-web-configure-tls-mutual-auth");
    private static readonly KnightControlReference DocAppAuth = Doc("Autenticação e autorização no App Service", L + "azure/app-service/overview-authentication-authorization");
    private static readonly KnightControlReference DocAppIdentity = Doc("Identidades gerenciadas no App Service", L + "azure/app-service/overview-managed-identity");
    private static readonly KnightControlReference DocAppAccess = Doc("Restrições de acesso do App Service", L + "azure/app-service/overview-access-restrictions");
    private static readonly KnightControlReference DocAppPe = Doc("Endpoints privados no App Service", L + "azure/app-service/overview-private-endpoint");
    private static readonly KnightControlReference DocAppVnet = Doc("Integração do App Service com rede virtual", L + "azure/app-service/overview-vnet-integration");
    private static readonly KnightControlReference DocAppRouting = Doc("Roteamento da integração com rede virtual", L + "azure/app-service/configure-vnet-integration-routing");
    private static readonly KnightControlReference DocAppCors = Doc("CORS no App Service (tutorial de API REST)", L + "azure/app-service/app-service-web-tutorial-rest-api");
    private static readonly KnightControlReference DocAse = Doc("Visão geral do App Service Environment", L + "azure/app-service/environment/overview");
    private static readonly KnightControlReference DocAseSettings = Doc("Configurações personalizadas do App Service Environment", L + "azure/app-service/environment/app-service-app-service-environment-custom-settings");
    private static readonly KnightControlReference DocRemoteDebug = Doc("Depuração remota no Azure App Service", L + "visualstudio/debugger/remote-debugging-azure-app-service?view=visualstudio");
    private static readonly KnightControlReference DocRedisEntra = Doc("Autenticação do Microsoft Entra no Cache for Redis", L + "azure/azure-cache-for-redis/cache-azure-active-directory-for-authentication");
    private static readonly KnightControlReference DocRedisTls = Doc("Remover TLS 1.0 e 1.1 do Cache for Redis", L + "azure/azure-cache-for-redis/cache-remove-tls-10-11");
    private static readonly KnightControlReference DocRedisConfig = Doc("Configurar o Cache for Redis", L + "azure/azure-cache-for-redis/cache-configure");
    private static readonly KnightControlReference DocRedisIdentity = Doc("Identidade gerenciada no Cache for Redis", L + "azure/azure-cache-for-redis/cache-managed-identity");
    private static readonly KnightControlReference DocRedisPe = Doc("Private Link no Cache for Redis", L + "azure/azure-cache-for-redis/cache-private-link");
    private static readonly KnightControlReference DocRedisAdmin = Doc("Administração do Cache for Redis (canal de atualização)", L + "azure/azure-cache-for-redis/cache-administration");
    private static readonly KnightControlReference DocAmr = Doc("Visão geral do Azure Managed Redis (Redis Enterprise)", L + "azure/redis/overview");
    private static readonly KnightControlReference DocAmrEntra = Doc("Autenticação do Entra no Azure Managed Redis", L + "azure/redis/entra-for-authentication");
    private static readonly KnightControlReference DocAmrEncryption = Doc("Criptografia com chave do cliente no Azure Managed Redis", L + "azure/redis/how-to-encryption");
    private static readonly KnightControlReference DocAmrPe = Doc("Private Link no Azure Managed Redis", L + "azure/redis/private-link");
    private static readonly KnightControlReference DocCosmosFw = Doc("Firewall de IP do Azure Cosmos DB", L + "azure/cosmos-db/how-to-configure-firewall");
    private static readonly KnightControlReference DocCosmosPe = Doc("Endpoints privados do Azure Cosmos DB", L + "azure/cosmos-db/how-to-configure-private-endpoints");
    private static readonly KnightControlReference DocCosmosRbac = Doc("Conectar ao Cosmos DB com controle de acesso por papel", L + "azure/cosmos-db/how-to-connect-role-based-access-control");
    private static readonly KnightControlReference DocCosmosCmk = Doc("Chaves gerenciadas pelo cliente no Cosmos DB", L + "azure/cosmos-db/how-to-setup-customer-managed-keys");
    private static readonly KnightControlReference DocAdfCmk = Doc("Chave gerenciada pelo cliente no Data Factory", L + "azure/data-factory/enable-customer-managed-key");
    private static readonly KnightControlReference DocAdfKv = Doc("Guardar credenciais no Azure Key Vault (Data Factory)", L + "azure/data-factory/store-credentials-in-key-vault");
    private static readonly KnightControlReference DocPeDns = Doc("Valores de zona DNS privada dos endpoints privados", L + "azure/private-link/private-endpoint-dns");
    private static readonly KnightControlReference DocFuncNet = Doc("Opções de rede do Azure Functions", L + "azure/azure-functions/functions-networking-options");
    private static readonly KnightControlReference DocMdcCompute = Doc("Recomendações de computação do Defender para Nuvem", L + "azure/defender-for-cloud/recommendations-reference-compute");
    private static readonly KnightControlReference DocUpdateManager = Doc("Visão geral do Azure Update Manager", L + "azure/update-manager/overview");
    private static readonly KnightControlReference DocAdfIdentity = Doc("Identidade gerenciada do Data Factory", L + "azure/data-factory/data-factory-service-identity");
    private static readonly KnightControlReference DocMySqlCmk = Doc("Chave gerenciada pelo cliente no MySQL", L + "azure/mysql/security/security-customer-managed-key");
    private static readonly KnightControlReference DocMySqlEntra = Doc("Autenticação do Entra no MySQL", L + "azure/mysql/security/security-entra-authentication");
    private static readonly KnightControlReference DocMySqlPublic = Doc("Rede pública do MySQL servidor flexível", L + "azure/mysql/flexible-server/concepts-networking-public");
    private static readonly KnightControlReference DocMySqlPe = Doc("Private Link no MySQL servidor flexível", L + "azure/mysql/flexible-server/concepts-networking-private-link");
    private static readonly KnightControlReference DocMySqlAudit = Doc("Monitorar o MySQL (logs de auditoria)", L + "azure/mysql/flexible-server/concepts-monitor-mysql#track-database-activity-with-audit-logs");
    private static readonly KnightControlReference DocMySqlErrors = Doc("Logs de erros do MySQL servidor flexível", L + "azure/mysql/flexible-server/concepts-error-logs");
    private static readonly KnightControlReference DocMySqlTls = Doc("TLS no MySQL servidor flexível", L + "azure/mysql/security/security-tls-how-to-connect");
    private static readonly KnightControlReference DocPgCmk = Doc("Criptografia de dados do PostgreSQL", L + "azure/postgresql/security/security-data-encryption");
    private static readonly KnightControlReference DocPgEntra = Doc("Autenticação do Entra no PostgreSQL", L + "azure/postgresql/security/security-entra-concepts");
    private static readonly KnightControlReference DocPgPublic = Doc("Rede pública do PostgreSQL", L + "azure/postgresql/network/concepts-networking-public");
    private static readonly KnightControlReference DocPgPe = Doc("Private Link no PostgreSQL", L + "azure/postgresql/network/concepts-networking-private-link");
    private static readonly KnightControlReference DocPgParams = Doc("Parâmetros de servidor do PostgreSQL", L + "azure/postgresql/parameters/concepts-parameters");
    private static readonly KnightControlReference DocPgLogging = Doc("Logs do PostgreSQL servidor flexível", L + "azure/postgresql/monitor/concepts-logging");
    private static readonly KnightControlReference DocPgTls = Doc("TLS no PostgreSQL servidor flexível", L + "azure/postgresql/security/security-tls");
    private static readonly KnightControlReference DocSqlAudit = Doc("Auditoria do Azure SQL", L + "azure/azure-sql/database/auditing-overview?view=azuresql");
    private static readonly KnightControlReference DocSqlConnectivity = Doc("Configurações de conectividade do Azure SQL", L + "azure/azure-sql/database/connectivity-settings?view=azuresql");
    private static readonly KnightControlReference DocSqlFirewall = Doc("Regras de firewall de IP do Azure SQL", L + "azure/azure-sql/database/firewall-configure?view=azuresql");
    private static readonly KnightControlReference DocSqlByok = Doc("TDE com chave gerenciada pelo cliente", L + "azure/azure-sql/database/transparent-data-encryption-byok-overview?view=azuresql");
    private static readonly KnightControlReference DocSqlTde = Doc("Criptografia transparente de dados (TDE)", L + "azure/azure-sql/database/transparent-data-encryption-tde-overview?view=azuresql");
    private static readonly KnightControlReference DocSqlEntra = Doc("Autenticação do Entra no Azure SQL", L + "azure/azure-sql/database/authentication-aad-overview?view=azuresql");
    private static readonly KnightControlReference DocSqlMiTls = Doc("Versão mínima de TLS da instância gerenciada", L + "azure/azure-sql/managed-instance/minimal-tls-version-configure?view=azuresql");
    private static readonly KnightControlReference DocDbrVnet = Doc("Implantar o Databricks em rede virtual própria", L + "azure/databricks/security/network/classic/vnet-inject");
    private static readonly KnightControlReference DocDbrScc = Doc("Conectividade segura de cluster do Databricks", L + "azure/databricks/security/network/classic/secure-cluster-connectivity");
    private static readonly KnightControlReference DocDbrPl = Doc("Private Link no Databricks", L + "azure/databricks/security/network/concepts/privatelink-concepts");
    private static readonly KnightControlReference DocDbrCmk = Doc("Chaves gerenciadas pelo cliente no Databricks", L + "azure/databricks/security/keys/customer-managed-keys");
    private static readonly KnightControlReference DocDbrFrontEnd = Doc("Conectividade privada de front-end do Databricks", L + "azure/databricks/security/network/front-end/front-end-private-connect");

    private static readonly List<KnightControlProfile> Built = new();
    private static readonly Dictionary<string, string> BuiltImpacts = new(StringComparer.Ordinal);

    private static void P(string id, KnightSecurityDomain d, string description, string rationale, string expected, string? doesNotProve, string impact,
        KnightControlReference[] docs, params KnightCapability[] caps)
    {
        Built.Add(new KnightControlProfile(id, d, description, rationale, expected, doesNotProve, docs,
            caps.Prepend(KnightCapability.AzureSubscriptions).Distinct().ToArray()));
        BuiltImpacts[id] = impact;
    }

    private const string Scope = "Avalia as assinaturas do escopo da coleta lidas com o papel Leitor; recursos em assinaturas fora do escopo não entram.";

    static AzureProfiles()
    {
        // ======== Assinaturas e IAM ==========================================================================
        P("AK-AZ-IAM-001", KnightSecurityDomain.IamRbac,
            "O papel Administrador de Acesso do Usuário está atribuído no escopo raiz do locatário.",
            "Na raiz, o papel permite conceder a si mesmo ou a outra identidade qualquer papel em qualquer assinatura e grupo de gerenciamento; é o que sobra quando a elevação de acesso de um Administrador Global não é desfeita.",
            "Nenhuma atribuição na raiz; elevação feita só quando necessária e removida em seguida.",
            "Vê a atribuição na raiz pela listagem das assinaturas do escopo; não determina se a elevação foi legítima nem há quanto tempo está ativa.",
            "Quem mantém a atribuição pode se tornar proprietário de assinaturas que não administra, inclusive em produção, sem passar por aprovação.",
            Docs(DocElevate, DocRoleAssignApi), KnightCapability.AzureAuthorization);
        P("AK-AZ-IAM-002", KnightSecurityDomain.IamRbac,
            "Contas de usuário desabilitadas no Microsoft Entra ID continuam com atribuições de papel no Azure.",
            "Uma conta desabilitada costuma ser de alguém que saiu; se for reabilitada por engano ou por um atacante com acesso ao diretório, as permissões no Azure voltam imediatamente, sem nova aprovação.",
            "Atribuições removidas quando a conta é desabilitada (processo de desligamento cobre o Azure RBAC).",
            "Lê só atribuições de USUÁRIO visíveis nas assinaturas do escopo; grupos e aplicações não entram. Uma conta não encontrada no diretório (atribuição órfã) fica como evidência, não como afetada.",
            "A reativação de uma conta antiga devolve a ela leitura ou alteração de recursos de nuvem que ninguém revisou desde o desligamento.",
            Docs(DocRbacBest, DocRoleAssignApi), KnightCapability.AzureAuthorization);
        P("AK-AZ-IAM-003", KnightSecurityDomain.IamRbac,
            "Existe papel personalizado com a ação “*”, equivalente ao Proprietário, atribuível a assinaturas do escopo.",
            "Um papel personalizado com todas as ações escapa das revisões feitas sobre os papéis internos privilegiados (Proprietário, Colaborador) e concede o mesmo poder sob um nome que não chama atenção.",
            "Papéis personalizados com as ações estritamente necessárias; o acesso pleno só pelos papéis internos, revisados.",
            "Não diz quem tem o papel atribuído nem em qual escopo; avalia a definição.",
            "Quem recebe esse papel pode alterar ou apagar recursos e permissões da assinatura sem aparecer nas listas de proprietários revisadas.",
            Docs(DocCustomRoles, DocRbacBest), KnightCapability.AzureAuthorization);
        P("AK-AZ-IAM-004", KnightSecurityDomain.Governance,
            "Não há papel personalizado dedicado a administrar bloqueios de recurso.",
            "Sem um papel específico, remover um bloqueio exige Proprietário ou Administrador de Acesso; os bloqueios acabam administrados por quem também pode fazer todo o resto, e a separação de funções se perde.",
            "Papel personalizado com Microsoft.Authorization/locks/*, atribuído só a quem administra bloqueios.",
            "Verifica a existência do papel atribuível à assinatura, não a quem foi atribuído.",
            "Uma exclusão acidental ou maliciosa protegida por bloqueio pode ser feita por qualquer proprietário, que remove o bloqueio e o recurso na mesma sessão.",
            Docs(DocLocks, DocCustomRoles), KnightCapability.AzureAuthorization);
        P("AK-AZ-IAM-005", KnightSecurityDomain.Governance,
            "A política do locatário permite que assinaturas saiam para outro diretório ou entrem nele.",
            "Transferir uma assinatura para outro diretório leva junto recursos e dados para fora da governança, das políticas e dos logs da organização; trazer uma de fora introduz recursos que não passaram pelos controles da casa.",
            "Saída e entrada de assinaturas bloqueadas, com isenções nominais para quem administra as transferências.",
            "Lê a política do locatário; não diz se alguma transferência já aconteceu.",
            "Uma assinatura com dados de produção pode passar a responder a outro diretório, onde a organização não tem mais acesso nem auditoria.",
            Docs(DocSubPolicy), KnightCapability.AzureAuthorization);
        P("AK-AZ-IAM-006", KnightSecurityDomain.IamRbac,
            "O número de proprietários atribuídos diretamente na assinatura está fora da faixa de dois a três.",
            "Com um só proprietário, a saída ou indisponibilidade dessa pessoa deixa a assinatura sem quem a administre; com mais de três, o acesso pleno fica distribuído além do necessário e cada conta extra é mais um alvo.",
            "De dois a três principais com o papel Proprietário no escopo da assinatura.",
            "Conta principais distintos com atribuição direta na assinatura; herdados de grupos de gerenciamento não entram, e um grupo conta como um principal.",
            "A assinatura pode ficar sem administração numa emergência, ou ter o acesso pleno espalhado por contas que ninguém acompanha.",
            Docs(DocRbacBest, DocRoleAssignApi), KnightCapability.AzureAuthorization);

        // ======== Azure Monitor ==============================================================================
        P("AK-AZ-MON-001", KnightSecurityDomain.Logging,
            "Grupos de segurança de rede não têm log de fluxo com análise de tráfego no Log Analytics.",
            "Sem o registro dos fluxos que cada NSG permitiu ou negou, não há como reconstruir de onde veio uma conexão suspeita nem para onde os dados saíram.",
            "Log de fluxo por NSG com análise de tráfego num workspace do Log Analytics (ou a migração para logs de fluxo de rede virtual).",
            "Os logs de fluxo de NSG estão em aposentadoria; um NSG coberto por log de fluxo da rede virtual aparece aqui como sem log próprio. " + Scope,
            "Numa investigação, a equipe não consegue dizer quais endereços externos falaram com as máquinas protegidas por esses NSGs.",
            Docs(DocNsgFlow, DocTraffic), KnightCapability.AzureNetworking);
        P("AK-AZ-MON-002", KnightSecurityDomain.Logging,
            "Redes virtuais não têm log de fluxo com análise de tráfego no Log Analytics.",
            "O log de fluxo da rede virtual é o registro de tráfego que substitui o de NSG; sem ele, movimentação lateral e exfiltração dentro da rede não deixam trilha consultável.",
            "Log de fluxo por rede virtual com análise de tráfego habilitada num workspace do Log Analytics.",
            Scope,
            "Conexões internas entre sub-redes e saídas para a internet ficam sem registro, e o alcance de um incidente não pode ser medido.",
            Docs(DocVnetFlow, DocTraffic), KnightCapability.AzureNetworking);
        P("AK-AZ-MON-003", KnightSecurityDomain.Logging,
            "Os logs de atividade do Microsoft Graph não são enviados a nenhum destino pelas configurações de diagnóstico do Entra ID.",
            "Esses logs registram cada requisição à API do Graph feita por usuários e aplicações; sem exportá-los, a leitura em massa de caixas de correio, arquivos ou diretório por uma aplicação abusada não deixa registro pesquisável.",
            "Categoria MicrosoftGraphActivityLogs habilitada numa configuração de diagnóstico com destino (de preferência Log Analytics).",
            "Lê as configurações de diagnóstico do Entra ID; não verifica a retenção do destino nem se alguém consulta os registros. A categoria exige licença Entra ID P1 ou P2.",
            "O uso indevido de permissões de aplicação sobre e-mail e arquivos pode passar despercebido e sem como ser reconstruído depois.",
            Docs(DocGraphLogs, DocEntraDiag), KnightCapability.AzureTenantDiagnostics);
        P("AK-AZ-MON-004", KnightSecurityDomain.Logging,
            "Os logs de auditoria e de entrada do Entra ID não são enviados a um destino retido.",
            "O Entra ID guarda esses registros por pouco tempo; sem exportação, entradas suspeitas, consentimentos e alterações de papéis anteriores a essa janela simplesmente deixam de existir para a investigação.",
            "AuditLogs, SignInLogs, NonInteractiveUserSignInLogs, ServicePrincipalSignInLogs e ManagedIdentitySignInLogs enviados a um destino retido.",
            "Considera qualquer destino (Log Analytics, armazenamento ou hub de eventos); não verifica por quanto tempo o destino retém.",
            "Uma conta invadida meses atrás não pode ter a origem do acesso confirmada, e alterações de privilégio daquele período ficam sem autor conhecido.",
            Docs(DocEntraDiag), KnightCapability.AzureTenantDiagnostics);

        Alert("AK-AZ-MON-005", "criação de atribuição de política",
            "Atribuir uma política nova pode alterar o que é permitido em toda a assinatura — inclusive negar recursos de segurança ou liberar o que estava bloqueado.",
            "Uma política que permite implantações fora do padrão pode entrar em vigor sem que a governança perceba até o próximo incidente.");
        Alert("AK-AZ-MON-006", "exclusão de atribuição de política",
            "Remover uma atribuição de política retira de uma vez as proteções que ela aplicava (regiões permitidas, criptografia obrigatória, auditoria).",
            "Os recursos criados depois da remoção ficam sem as exigências de conformidade, e a lacuna só aparece numa auditoria posterior.");
        Alert("AK-AZ-MON-007", "criação ou alteração de NSG",
            "Uma regra nova ou alterada num grupo de segurança de rede pode abrir uma porta administrativa para a internet em segundos.",
            "Um serviço interno pode ficar exposto à internet durante dias sem que a equipe de rede saiba que a regra mudou.");
        Alert("AK-AZ-MON-008", "exclusão de NSG",
            "Excluir um grupo de segurança de rede deixa sub-redes e interfaces sem a filtragem que ele aplicava.",
            "Máquinas antes isoladas passam a aceitar o tráfego que a rede deixar passar, sem um aviso para quem responde pela segmentação.");
        Alert("AK-AZ-MON-009", "criação ou alteração de solução de segurança",
            "As soluções de segurança integradas ao Defender para Nuvem (proteção de endpoint, firewalls de parceiros) podem ser reconfiguradas para deixar de proteger.",
            "Uma proteção de terceiros pode ser desativada ou redirecionada sem chamar a atenção do time de operações de segurança.");
        Alert("AK-AZ-MON-010", "exclusão de solução de segurança",
            "Excluir uma solução de segurança é um passo típico de quem quer agir sem ser detectado.",
            "Recursos antes monitorados por uma solução de parceiro passam a operar sem ela, e o primeiro sinal pode ser o próprio incidente.");
        Alert("AK-AZ-MON-011", "criação ou alteração de regra de firewall do SQL",
            "Uma regra de firewall do SQL nova ou ampliada pode liberar o banco de dados para endereços que não deveriam alcançá-lo.",
            "Um banco com dados de clientes pode passar a aceitar conexões de uma faixa de IPs desconhecida sem revisão.");
        Alert("AK-AZ-MON-012", "exclusão de regra de firewall do SQL",
            "Excluir regras de firewall do SQL altera quem consegue se conectar e pode interromper aplicações ou mascarar uma troca de regras.",
            "Aplicações podem perder acesso ao banco, ou uma substituição de regras pode ocorrer sem que ninguém compare o antes e o depois.");
        Alert("AK-AZ-MON-013", "criação ou alteração de IP público",
            "Um IP público novo ou reassociado cria um ponto de entrada da internet para um recurso que podia ser interno.",
            "Um recurso interno pode ganhar endereço acessível da internet e ser varrido por scanners antes de a equipe notar.");
        Alert("AK-AZ-MON-014", "exclusão de IP público",
            "A exclusão de um IP público interrompe serviços publicados e libera o endereço, que pode ser tomado por outro cliente da nuvem.",
            "Clientes e parceiros podem perder acesso a um serviço publicado, e registros DNS podem passar a apontar para um endereço que não é mais da organização.");
        P("AK-AZ-MON-015", KnightSecurityDomain.Logging,
            "Assinaturas não têm alerta do log de atividades para eventos de integridade do serviço (Service Health).",
            "Incidentes, manutenções planejadas e avisos de segurança da própria Microsoft chegam pelo Service Health; sem alerta, a equipe só descobre quando usuários reclamam.",
            "Alerta do log de atividades da categoria ServiceHealth, habilitado no escopo de cada assinatura, com grupo de ações.",
            "Verifica a existência do alerta habilitado no escopo da assinatura; não confere quem recebe a notificação.",
            "Uma interrupção regional ou um aviso de segurança da plataforma pode afetar a operação por horas antes de alguém ser avisado.",
            Docs(DocServiceHealth, DocActivityAlert), KnightCapability.AzureMonitor);
        P("AK-AZ-MON-016", KnightSecurityDomain.Logging,
            "Assinaturas não têm nenhum recurso do Application Insights.",
            "Sem telemetria de aplicação, falhas, picos de erro e padrões anômalos de requisição das aplicações hospedadas não ficam registrados para diagnóstico nem para investigação.",
            "Application Insights configurado para as aplicações hospedadas em cada assinatura.",
            "Verifica a existência de ao menos um recurso por assinatura; não confere quais aplicações enviam telemetria a ele.",
            "Um ataque à camada de aplicação (força bruta, abuso de API) pode durar sem que haja dados para identificá-lo ou dimensioná-lo.",
            Docs(DocAppInsights), KnightCapability.AzureMonitor);

        // ======== Defender para Nuvem ========================================================================
        Plan("AK-AZ-MDC-001", "Defender CSPM", DocCspm,
            "O CSPM pago analisa caminhos de ataque, exposição à internet e dados sensíveis entre recursos; sem ele, só a postura básica gratuita é avaliada.",
            "Combinações perigosas entre recursos (uma VM exposta com acesso a um cofre) continuam sem ser apontadas até serem exploradas.");
        Plan("AK-AZ-MDC-002", "Defender para APIs", DocDfApis,
            "As APIs publicadas no Gerenciamento de API ficam sem inventário de exposição e sem detecção de abuso específica de APIs.",
            "Abuso de uma API publicada — extração de dados em massa, uso de credencial vazada — pode correr sem detecção específica.");
        Plan("AK-AZ-MDC-003", "Defender para Servidores", DocDfServers,
            "Sem o plano, máquinas virtuais não recebem detecção de ameaças, avaliação de vulnerabilidades nem a integração automática com o Defender para Endpoint.",
            "Malware ou um invasor numa máquina virtual pode agir sem alerta, e vulnerabilidades conhecidas ficam sem inventário.");
        Plan("AK-AZ-MDC-004", "Defender para Contêineres", DocDfContainers,
            "Clusters Kubernetes e registros de contêiner ficam sem detecção de ameaças em tempo de execução e sem varredura de vulnerabilidades das imagens.",
            "Uma imagem vulnerável pode ir para produção e um contêiner comprometido pode ser usado para minerar criptomoedas ou mover-se no cluster sem alerta.");
        Plan("AK-AZ-MDC-005", "Defender para Armazenamento", DocDfStorage,
            "As contas de armazenamento ficam sem detecção de acesso anômalo, de upload de malware e de exfiltração.",
            "Arquivos maliciosos enviados a um contêiner ou uma cópia em massa por credencial vazada podem passar sem aviso.");
        Plan("AK-AZ-MDC-006", "Defender para App Service", DocDfAppService,
            "Os aplicativos do App Service ficam sem detecção de ataques à aplicação e de uso do serviço por invasores (por exemplo, varreduras e DNS pendurado).",
            "Uma aplicação web atacada ou usada como ponto de partida não gera alerta no Defender para Nuvem.");
        Plan("AK-AZ-MDC-007", "Defender para Cosmos DB", DocDfCosmos,
            "As contas do Cosmos DB ficam sem detecção de injeção de SQL, de acessos de origens suspeitas e de extração anômala.",
            "Uma consulta maliciosa ou o uso de uma chave vazada pode extrair documentos do banco sem disparar alerta.");
        Plan("AK-AZ-MDC-008", "Defender para bancos relacionais de código aberto", DocDfDatabases,
            "PostgreSQL, MySQL e MariaDB ficam sem detecção de força bruta e de acessos anômalos.",
            "Uma tentativa de adivinhar senhas do banco pode durar até dar certo sem que a equipe seja alertada.");
        Plan("AK-AZ-MDC-009", "Defender para Azure SQL", DocDfSql,
            "Bancos do Azure SQL ficam sem avaliação de vulnerabilidades e sem detecção de injeção de SQL e de acesso anômalo.",
            "Uma injeção de SQL explorada pela aplicação pode extrair tabelas inteiras sem alerta.");
        Plan("AK-AZ-MDC-010", "Defender para SQL em máquinas", DocDfSqlVm,
            "Instâncias de SQL Server em máquinas virtuais ficam sem as mesmas detecções e avaliações oferecidas ao SQL gerenciado.",
            "Servidores SQL instalados em VMs, muitas vezes os mais antigos, ficam sem detecção de ataque e sem lista de vulnerabilidades.");
        Plan("AK-AZ-MDC-011", "Defender para Key Vault", DocDfKeyVault,
            "Os cofres ficam sem detecção de acesso incomum a segredos, de origens suspeitas e de enumeração.",
            "A leitura de segredos por uma identidade comprometida pode acontecer sem nenhum aviso.");
        Plan("AK-AZ-MDC-012", "Defender para Resource Manager", DocDfArm,
            "As operações de gerenciamento (criação de recursos, alterações de permissão) ficam sem detecção de padrões de ataque e de ferramentas de exploração.",
            "Uma conta administrativa comprometida pode criar recursos, alterar permissões ou desativar proteções sem alerta.");

        P("AK-AZ-MDC-013", KnightSecurityDomain.CloudProtection,
            "A avaliação de vulnerabilidades de máquinas está desligada em assinaturas do escopo.",
            "Sem a avaliação, o Defender para Nuvem não lista software vulnerável nem configurações fracas das máquinas, e a correção fica sem fila.",
            "Configuração de avaliação de vulnerabilidades com o provedor MdeTvm (gerenciamento de vulnerabilidades do Defender) em cada assinatura.",
            "Lê a configuração da assinatura; não diz quantas máquinas foram efetivamente avaliadas.",
            "Uma vulnerabilidade com exploração pública pode permanecer em servidores expostos sem constar de nenhum relatório.",
            Docs(DocMdvm, DocDfServers), KnightCapability.AzureDefenderForCloud);
        P("AK-AZ-MDC-014", KnightSecurityDomain.CloudProtection,
            "A integração do Defender para Nuvem com o Defender para Endpoint está desligada.",
            "É a integração que implanta o Defender para Endpoint nas máquinas e traz os alertas dele para o Defender para Nuvem; desligada, as máquinas novas ficam sem o agente.",
            "Configuração WDATP habilitada em cada assinatura.",
            "Lê a configuração; não verifica o estado do agente em cada máquina (ver o controle de proteção de endpoint).",
            "Máquinas novas entram em operação sem EDR, e um ataque nelas só é percebido por outros meios.",
            Docs(DocMde), KnightCapability.AzureDefenderForCloud);
        P("AK-AZ-MDC-015", KnightSecurityDomain.CloudProtection,
            "A varredura sem agente de máquinas está desligada no plano Defender para Servidores.",
            "A varredura sem agente lê os discos das máquinas por instantâneos e encontra vulnerabilidades, segredos e malware mesmo onde o agente não está instalado.",
            "Extensão AgentlessVmScanning habilitada no plano VirtualMachines.",
            "Exige o plano Defender para Servidores ligado (P2 ou CSPM); com o plano desligado, o controle reprova pela ausência da extensão.",
            "Máquinas sem agente — esquecidas, recém-criadas ou de laboratório — ficam fora de qualquer inventário de vulnerabilidades e segredos expostos.",
            Docs(DocAgentless), KnightCapability.AzureDefenderForCloud);
        P("AK-AZ-MDC-016", KnightSecurityDomain.CloudProtection,
            "O monitoramento de integridade de arquivos está desligado no plano Defender para Servidores.",
            "Sem ele, alterações em arquivos de sistema, registro e binários críticos — típicas de persistência de um invasor — não são registradas.",
            "Extensão FileIntegrityMonitoring habilitada no plano VirtualMachines.",
            "Não verifica quais caminhos são monitorados nem para qual workspace os eventos vão.",
            "Um invasor pode substituir binários ou criar persistência nas máquinas sem que a mudança seja percebida.",
            Docs(DocFim), KnightCapability.AzureDefenderForCloud);
        P("AK-AZ-MDC-017", KnightSecurityDomain.Governance,
            "A iniciativa do Microsoft Cloud Security Benchmark está ausente da assinatura ou tem políticas com efeito desabilitado.",
            "É essa iniciativa que gera as recomendações do Defender para Nuvem; cada efeito desabilitado é uma verificação que deixa de existir para a assinatura inteira.",
            "Atribuição SecurityCenterBuiltIn presente em cada assinatura, sem parâmetros de efeito com valor Disabled.",
            "Lê a atribuição na própria assinatura; uma atribuição herdada de grupo de gerenciamento não é considerada.",
            "Recomendações de segurança deixam de aparecer para a equipe, que passa a ver uma postura melhor do que a real.",
            Docs(DocMcsb), KnightCapability.AzurePolicy);
        P("AK-AZ-MDC-018", KnightSecurityDomain.CloudProtection,
            "Não há workspace do Defender EASM nas assinaturas do escopo.",
            "O EASM descobre ativos da organização expostos na internet (domínios, IPs, certificados, serviços) que não constam dos inventários internos.",
            "Workspace do Microsoft Defender EASM implantado e configurado com os ativos-semente da organização.",
            "Verifica a existência do workspace; não verifica sua configuração nem a cobertura dos ativos.",
            "Serviços esquecidos e expostos na internet continuam desconhecidos até serem encontrados por quem os ataca.",
            Docs(DocEasm), KnightCapability.AzureDefenderForCloud);
        P("AK-AZ-MDC-019", KnightSecurityDomain.CloudProtection,
            "Assinaturas com hubs IoT não têm o Defender para IoT habilitado.",
            "Sem a solução de segurança de IoT, os dispositivos conectados ao hub ficam sem recomendações e sem detecção de comportamento anômalo.",
            "Solução de segurança de IoT (Defender para IoT) habilitada cobrindo cada hub IoT.",
            "Confere a lista de hubs de cada solução lida pelo Resource Manager; não verifica a configuração dos agentes nos dispositivos.",
            "Um dispositivo IoT comprometido pode ser usado como ponto de entrada ou enviar dados falsos sem alerta.",
            Docs(DocIot), KnightCapability.AzureDefenderForCloud);
        P("AK-AZ-MDC-020", KnightSecurityDomain.CloudProtection,
            "Máquinas virtuais com atualizações de sistema, segurança ou críticas pendentes segundo o Defender para Nuvem.",
            "Falhas corrigidas pelos fabricantes e ainda não aplicadas são as mais exploradas, porque o caminho de ataque já é público.",
            "Avaliação de atualizações do sistema com estado saudável (ou não aplicável) em cada máquina.",
            "Depende do Defender para Nuvem avaliar a máquina; sem a avaliação, o controle não aprova nem reprova. Não avalia máquinas fora do Azure.",
            "Uma máquina com correção de segurança pendente pode ser invadida por uma falha já conhecida e documentada.",
            Docs(DocMdcCompute, DocUpdateManager), KnightCapability.AzureCompute);

        // ======== Rede =======================================================================================
        Port("AK-AZ-NET-001", "RDP", "TCP 3389",
            "O RDP exposto é dos alvos mais varridos da internet: força bruta de senhas e vulnerabilidades do protocolo levam diretamente ao controle da máquina Windows.",
            "Uma máquina Windows pode ser tomada por força bruta e usada para criptografar dados ou entrar na rede interna.");
        Port("AK-AZ-NET-002", "SSH", "TCP 22",
            "O SSH aberto para qualquer origem recebe tentativas contínuas de senha e de chaves roubadas contra as máquinas Linux.",
            "Uma máquina Linux pode ser tomada por credencial adivinhada ou vazada e usada como base para ataques à rede.");
        Port("AK-AZ-NET-003", "DNS", "UDP 53",
            "Um resolvedor DNS aberto à internet pode ser usado para amplificar ataques de negação de serviço contra terceiros e para consultas não autorizadas.",
            "O servidor pode participar de ataques de amplificação contra outras organizações e ter a banda consumida.");
        Port("AK-AZ-NET-004", "NTP", "UDP 123",
            "Servidores NTP abertos respondem a consultas que amplificam tráfego — um vetor clássico de negação de serviço refletida.",
            "A rede pode ser usada como refletor em ataques de negação de serviço e sofrer degradação por isso.");
        Port("AK-AZ-NET-005", "SNMP", "UDP 161",
            "O SNMP exposto revela configuração e inventário do equipamento e, com comunidades fracas, permite alterá-los.",
            "Informações de rede internas podem ser coletadas por terceiros e usadas para planejar um ataque.");
        Port("AK-AZ-NET-006", "CLDAP", "UDP 389",
            "O CLDAP aberto responde a consultas com respostas muito maiores que a pergunta, fator de amplificação explorado em ataques volumétricos.",
            "O serviço de diretório pode ser usado para amplificar ataques de negação de serviço e ficar indisponível.");
        Port("AK-AZ-NET-007", "SSDP", "UDP 1900",
            "O SSDP não tem razão para receber tráfego da internet e é usado como refletor em ataques de amplificação.",
            "A máquina pode servir de refletor em ataques de negação de serviço e ser bloqueada por provedores.");
        Port("AK-AZ-NET-008", "HTTP", "TCP 80",
            "O HTTP sem criptografia exposto diretamente na máquina, sem WAF ou balanceador na frente, expõe o servidor web a ataques e trafega dados em claro.",
            "Credenciais e dados de formulário podem ser capturados em trânsito, e o servidor fica sujeito a ataques web sem filtragem.");
        Port("AK-AZ-NET-009", "HTTPS", "TCP 443",
            "Publicar HTTPS direto na máquina dispensa as camadas de proteção (WAF, balanceador) que filtram ataques à aplicação antes de chegarem ao servidor.",
            "Ataques à aplicação chegam ao servidor sem filtragem prévia e podem explorar falhas do software publicado.");
        P("AK-AZ-NET-010", KnightSecurityDomain.Logging,
            "Logs de fluxo de NSG são retidos por menos de 90 dias.",
            "Incidentes costumam ser descobertos semanas depois do início; uma retenção curta apaga justamente o período em que o invasor entrou.",
            "Retenção de ao menos 90 dias (ou 0, indefinida) em cada log de fluxo de NSG.",
            "Com a política de retenção desligada, a exclusão depende do ciclo de vida da conta de armazenamento, que não é lido — o log fica como não avaliado.",
            "A investigação de um acesso de dois meses atrás encontra o armazenamento já sem os registros de tráfego daquela época.",
            Docs(DocNsgFlowManage), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-011", KnightSecurityDomain.Network,
            "Há regiões com rede virtual sem Network Watcher.",
            "O Network Watcher é o pré-requisito dos logs de fluxo, da captura de pacotes e do diagnóstico de conectividade; sem ele na região, nenhuma dessas ferramentas existe para as redes de lá.",
            "Network Watcher em cada região onde a assinatura tem rede virtual.",
            "Considera regiões com rede virtual; recursos sem rede virtual não definem região em uso.",
            "Na hora de investigar uma conexão naquela região, a equipe não tem captura nem registro de fluxo para usar.",
            Docs(DocWatcher), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-012", KnightSecurityDomain.Logging,
            "Logs de fluxo de rede virtual são retidos por menos de 90 dias.",
            "O log de fluxo da rede virtual é o registro de tráfego atual da plataforma; retê-lo por pouco tempo limita a investigação ao que aconteceu nas últimas semanas.",
            "Retenção de ao menos 90 dias (ou 0, indefinida) em cada log de fluxo de rede virtual.",
            "Mesma ressalva da retenção desligada: o ciclo de vida da conta de armazenamento não é lido.",
            "O caminho percorrido por um invasor dentro da rede deixa de poder ser reconstruído quando a descoberta é tardia.",
            Docs(DocVnetFlowManage), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-013", KnightSecurityDomain.Identity,
            "A VPN ponto a site aceita certificado ou RADIUS além do Microsoft Entra ID.",
            "Certificados de cliente e RADIUS ficam fora do acesso condicional e da MFA do Entra; um certificado copiado de um notebook dá acesso à rede sem segundo fator.",
            "Tipo de autenticação da VPN ponto a site exclusivamente AAD (Microsoft Entra ID).",
            "Avalia gateways com VPN ponto a site configurada; não verifica as políticas de acesso condicional aplicadas ao aplicativo da VPN.",
            "Um certificado de VPN extraído de um dispositivo perdido permite entrar na rede corporativa sem MFA.",
            Docs(DocVpnEntra), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-014", KnightSecurityDomain.Network,
            "Gateways de aplicativo publicam aplicações sem firewall de aplicativo web habilitado.",
            "Sem WAF, injeções, cross-site scripting e exploração de vulnerabilidades conhecidas chegam à aplicação sem nenhuma filtragem na borda.",
            "Camada WAF/WAF_v2 com política de WAF associada e habilitada.",
            "Não verifica o modo (detecção ou prevenção) nem as regras da política; ver os controles de inspeção do corpo e de bots.",
            "Uma vulnerabilidade da aplicação publicada pode ser explorada por ataques automatizados sem barreira intermediária.",
            Docs(DocWaf), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-015", KnightSecurityDomain.Network,
            "Sub-redes estão sem grupo de segurança de rede associado.",
            "Sem NSG, a sub-rede aceita todo o tráfego que a rede virtual roteia até ela; a segmentação depende só de cada máquina estar bem configurada.",
            "NSG associado a cada sub-rede, exceto as reservadas da plataforma (GatewaySubnet, AzureFirewallSubnet, AzureFirewallManagementSubnet, RouteServerSubnet).",
            "Não avalia NSGs associados diretamente às interfaces de rede, que também filtram o tráfego.",
            "Uma máquina comprometida numa sub-rede vizinha alcança livremente os serviços desta.",
            Docs(DocNsg), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-016", KnightSecurityDomain.Network,
            "Gateways de aplicativo aceitam TLS 1.0 ou 1.1.",
            "TLS 1.0 e 1.1 têm fraquezas conhecidas e não são mais aceitos por navegadores; mantê-los habilitados só serve a clientes que deveriam ter sido atualizados.",
            "Política de TLS com mínimo 1.2 (predefinida AppGwSslPolicy20220101 ou superior, ou personalizada).",
            "Sem política explícita, o padrão depende da versão da API usada na criação; o gateway fica como não avaliado.",
            "Conexões de clientes antigos podem ser rebaixadas e interceptadas, expondo sessões e credenciais.",
            Docs(DocAgwTls), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-017", KnightSecurityDomain.Network,
            "Gateways de aplicativo estão sem HTTP/2.",
            "O HTTP/2 é a versão atual do protocolo para clientes modernos; mantê-lo desligado mantém o tráfego em HTTP/1.1 e afasta a configuração da referência recomendada.",
            "Propriedade enableHttp2 verdadeira.",
            "É um controle de configuração de baixa severidade; não indica exposição por si só.",
            "Os clientes perdem a multiplexação do HTTP/2, e a configuração diverge da linha de base usada nas demais publicações.",
            Docs(DocAgwFeatures), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-018", KnightSecurityDomain.Network,
            "Políticas de WAF não inspecionam o corpo das requisições.",
            "Boa parte das injeções e cargas maliciosas viaja no corpo de requisições POST; sem inspecioná-lo, o WAF só olha cabeçalhos e URL.",
            "requestBodyCheck habilitado em cada política de WAF.",
            "Não verifica limites de tamanho do corpo nem o modo da política.",
            "Ataques enviados em formulários e APIs passam pelo WAF como se fossem tráfego legítimo.",
            Docs(DocWafBody), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-019", KnightSecurityDomain.Network,
            "Políticas de WAF não têm o conjunto de regras de proteção contra bots.",
            "Bots maliciosos fazem varredura, raspagem de conteúdo e testes de credenciais em escala; o conjunto gerenciado identifica e bloqueia os conhecidos.",
            "Conjunto de regras Microsoft_BotManagerRuleSet na política de WAF.",
            "Não verifica a ação configurada para cada categoria de bot.",
            "Raspagem de dados e testes automatizados de senhas contra a aplicação seguem sem bloqueio na borda.",
            Docs(DocWafBot), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-020", KnightSecurityDomain.Network,
            "Assinaturas não têm Azure Bastion.",
            "Sem um ponto de acesso administrativo gerenciado, o acesso às máquinas tende a ser feito expondo RDP e SSH ou por soluções paralelas sem a mesma proteção.",
            "Azure Bastion implantado para o acesso administrativo às máquinas virtuais.",
            "Verifica a existência do Bastion por assinatura; um Bastion compartilhado de outra assinatura emparelhada não é reconhecido.",
            "Administradores acabam abrindo portas de acesso remoto à internet para conseguir trabalhar, criando a exposição que o Bastion evitaria.",
            Docs(DocBastion), KnightCapability.AzureNetworking);
        P("AK-AZ-NET-021", KnightSecurityDomain.Network,
            "Redes virtuais estão sem plano de Proteção DDoS de Rede.",
            "A proteção básica da plataforma não é ajustada ao perfil de tráfego de cada aplicação; o plano dá mitigação adaptativa, telemetria e suporte durante o ataque.",
            "Rede virtual associada a um plano de Proteção DDoS com enableDdosProtection verdadeiro.",
            "Avalia todas as redes virtuais; redes sem recurso exposto à internet podem não precisar do plano.",
            "Um ataque volumétrico pode tirar do ar aplicações publicadas por horas, sem telemetria para orientar a resposta.",
            Docs(DocDdos), KnightCapability.AzureNetworking);

        // ======== Armazenamento ==============================================================================
        P("AK-AZ-STO-001", KnightSecurityDomain.Storage,
            "Compartilhamentos de arquivos podem ser excluídos sem possibilidade de recuperação.",
            "Sem exclusão reversível, apagar um compartilhamento — por engano, por script ou por ransomware — elimina todo o conteúdo imediatamente.",
            "Exclusão reversível de compartilhamentos habilitada no serviço de arquivos.",
            "Não verifica o período de retenção configurado.",
            "Um compartilhamento usado pelas equipes pode ser perdido de forma definitiva com um único comando.",
            Docs(DocFileSoftDelete), KnightCapability.AzureStorage);
        P("AK-AZ-STO-002", KnightSecurityDomain.Storage,
            "O serviço de arquivos aceita versões do SMB anteriores a 3.1.1.",
            "O SMB 3.1.1 traz verificação de integridade antes da autenticação e criptografia mais forte; versões anteriores permitem rebaixamento e têm proteções menores.",
            "Versões do SMB restritas a SMB3.1.1.",
            "Quando a propriedade não é definida, vale o padrão documentado de máxima compatibilidade (SMB 2.1, 3.0 e 3.1.1).",
            "Conexões aos compartilhamentos podem ser rebaixadas para versões mais fracas, facilitando interceptação e adulteração.",
            Docs(DocSmb), KnightCapability.AzureStorage);
        P("AK-AZ-STO-003", KnightSecurityDomain.Storage,
            "O canal SMB aceita cifras abaixo de AES-256-GCM.",
            "As cifras AES-128 continuam aceitas por compatibilidade; restringir a AES-256-GCM garante a proteção mais forte do tráfego de arquivos.",
            "Criptografia de canal SMB restrita a AES-256-GCM.",
            "Quando a propriedade não é definida, vale o padrão documentado (AES-128-CCM, AES-128-GCM e AES-256-GCM).",
            "Arquivos trafegam com criptografia mais fraca do que a disponível, abaixo do que políticas internas costumam exigir.",
            Docs(DocSmb), KnightCapability.AzureStorage);
        P("AK-AZ-STO-004", KnightSecurityDomain.Storage,
            "Blobs excluídos não podem ser recuperados.",
            "Sem exclusão reversível de blobs, exclusões e sobrescritas acidentais ou maliciosas são definitivas.",
            "Exclusão reversível de blobs habilitada (sete dias ou mais).",
            "Não verifica o período de retenção configurado.",
            "Documentos, backups ou dados de aplicação apagados por erro ou ataque não podem ser restaurados.",
            Docs(DocBlobSoftDelete), KnightCapability.AzureStorage);
        P("AK-AZ-STO-005", KnightSecurityDomain.Storage,
            "Contêineres de blobs excluídos não podem ser recuperados.",
            "Excluir um contêiner apaga de uma vez todos os blobs que ele guarda; sem exclusão reversível de contêineres, não há volta.",
            "Exclusão reversível de contêineres habilitada.",
            "Não verifica o período de retenção configurado.",
            "Um contêiner inteiro de dados pode ser perdido de forma definitiva por um único comando.",
            Docs(DocContainerSoftDelete), KnightCapability.AzureStorage);
        P("AK-AZ-STO-006", KnightSecurityDomain.Storage,
            "Blobs estão sem controle de versão.",
            "Sem versões anteriores, uma sobrescrita — inclusive a criptografia de arquivos por ransomware — substitui o conteúdo sem deixar cópia.",
            "Controle de versão de blobs habilitado.",
            "Não verifica políticas de ciclo de vida que apaguem versões antigas.",
            "Arquivos cifrados por ransomware ou alterados indevidamente não podem voltar ao estado anterior.",
            Docs(DocVersioning), KnightCapability.AzureStorage);
        P("AK-AZ-STO-007", KnightSecurityDomain.Secrets,
            "Contas de armazenamento não têm lembrete de rotação de chaves.",
            "Sem política de expiração, o Azure não sinaliza chaves antigas, e a rotação depende de alguém lembrar.",
            "Política de expiração de chaves (keyExpirationPeriodInDays) definida.",
            "A política só gera lembretes e avaliações; não rotaciona as chaves.",
            "Chaves de acesso podem permanecer válidas por anos, inclusive cópias esquecidas em scripts e estações.",
            Docs(DocStorageKeys), KnightCapability.AzureStorage);
        P("AK-AZ-STO-008", KnightSecurityDomain.Secrets,
            "Chaves de acesso das contas de armazenamento não são regeneradas há mais de 90 dias.",
            "A chave de acesso dá controle total sobre os dados da conta; quanto mais tempo ela vive, mais cópias existem fora do controle de quem a administra.",
            "As duas chaves regeneradas nos últimos 90 dias (ou acesso por chave compartilhada desligado).",
            "Contas antigas não registram a data de criação das chaves e ficam como não avaliadas.",
            "Uma chave vazada há meses continua abrindo a conta de armazenamento para leitura e alteração de qualquer objeto.",
            Docs(DocStorageKeys), KnightCapability.AzureStorage);
        P("AK-AZ-STO-009", KnightSecurityDomain.Storage,
            "Contas de armazenamento aceitam requisições autorizadas por chave compartilhada.",
            "Requisições com a chave da conta ou SAS assinadas por ela não passam pelo Entra ID: não há identidade, acesso condicional nem trilha de quem fez a operação.",
            "allowSharedKeyAccess = false, com autorização pelo Microsoft Entra ID.",
            "Quando a propriedade é nula, vale o documentado: equivale a verdadeiro.",
            "Quem obtém a chave da conta ou uma SAS acessa os dados sem deixar identidade nos registros.",
            Docs(DocSharedKey), KnightCapability.AzureStorage);
        P("AK-AZ-STO-010", KnightSecurityDomain.Network,
            "Contas de armazenamento não têm endpoint privado aprovado.",
            "Sem endpoint privado, o acesso de redes internas ao armazenamento usa o endereço público do serviço, e fechar o acesso público fica inviável.",
            "Ao menos uma conexão de endpoint privado aprovada por conta.",
            "Não verifica a resolução DNS do endpoint privado.",
            "O armazenamento continua dependendo de exposição pública para atender os sistemas internos.",
            Docs(DocStoragePe), KnightCapability.AzureStorage);
        P("AK-AZ-STO-011", KnightSecurityDomain.Network,
            "Contas de armazenamento aceitam acesso pela rede pública.",
            "Com acesso público habilitado, o ponto de extremidade do armazenamento é alcançável da internet, protegido só pelas regras de firewall e pelas credenciais.",
            "publicNetworkAccess = Disabled, com acesso por endpoints privados.",
            "Um valor ausente é desconhecido; o efeito depende das regras de rede.",
            "Uma credencial vazada pode ser usada de qualquer lugar da internet contra a conta.",
            Docs(DocStorageNet), KnightCapability.AzureStorage);
        P("AK-AZ-STO-012", KnightSecurityDomain.Network,
            "A regra de rede padrão das contas de armazenamento permite conexões de qualquer rede.",
            "Com ação padrão Allow, as regras de rede não restringem nada: qualquer origem chega ao serviço.",
            "networkAcls.defaultAction = Deny, com redes e IPs liberados nominalmente.",
            "Não avalia as exceções liberadas.",
            "Os dados ficam acessíveis de qualquer rede para quem tiver uma credencial válida.",
            Docs(DocStorageNet), KnightCapability.AzureStorage);
        P("AK-AZ-STO-013", KnightSecurityDomain.Identity,
            "O portal do Azure usa a chave da conta, e não o Entra ID, como autorização padrão para os dados do armazenamento.",
            "Com a chave como padrão, quem navega pelos dados no portal o faz com permissão total da conta, independentemente do papel de dados que tem.",
            "defaultToOAuthAuthentication = true.",
            "Quando a propriedade é nula, vale o documentado: interpretação padrão falsa.",
            "Usuários com papel de leitura de gerenciamento podem acabar operando dados com a chave da conta, sem registro de identidade.",
            Docs(DocStoragePortalAuth), KnightCapability.AzureStorage);
        P("AK-AZ-STO-014", KnightSecurityDomain.Storage,
            "Contas de armazenamento aceitam tráfego sem HTTPS.",
            "Sem transferência segura obrigatória, dados e assinaturas de acesso podem trafegar em claro.",
            "supportsHttpsTrafficOnly = true.",
            null,
            "Dados lidos ou gravados pela rede podem ser capturados em trânsito, inclusive tokens SAS.",
            Docs(DocSecureTransfer), KnightCapability.AzureStorage);
        P("AK-AZ-STO-015", KnightSecurityDomain.Network,
            "O firewall do armazenamento não abre exceção para os serviços confiáveis da Microsoft.",
            "Com o firewall restrito e sem a exceção, serviços como backup, Log Analytics e o próprio Defender não conseguem acessar a conta, e a tendência é abrir a rede inteira para resolver.",
            "networkAcls.bypass contendo AzureServices.",
            "Não verifica quais serviços efetivamente precisam do acesso.",
            "Backups e coletas de logs falham silenciosamente, ou a conta é aberta para todas as redes para contornar o bloqueio.",
            Docs(DocStorageNet), KnightCapability.AzureStorage);
        P("AK-AZ-STO-016", KnightSecurityDomain.Storage,
            "Contas de armazenamento aceitam TLS abaixo de 1.2.",
            "TLS 1.0 e 1.1 têm fraquezas conhecidas; aceitá-los permite que clientes antigos ou rebaixados negociem conexões mais fracas.",
            "minimumTlsVersion = TLS1_2 ou superior.",
            "Quando a propriedade é nula, vale o documentado: interpretação padrão TLS 1.0.",
            "Conexões ao armazenamento podem ser negociadas com protocolos fracos e interceptadas.",
            Docs(DocStorageTls), KnightCapability.AzureStorage);
        P("AK-AZ-STO-017", KnightSecurityDomain.DataProtection,
            "Contas de armazenamento permitem replicar objetos para outro locatário.",
            "A replicação entre locatários copia dados continuamente para contas fora do diretório da organização, onde os controles e a auditoria são de outra pessoa.",
            "allowCrossTenantReplication = false.",
            "Um valor ausente é desconhecido (o padrão mudou ao longo das versões da plataforma).",
            "Dados podem ser copiados de forma contínua para uma conta de outra organização sem passar por aprovação.",
            Docs(DocCrossTenant), KnightCapability.AzureStorage);
        P("AK-AZ-STO-018", KnightSecurityDomain.DataProtection,
            "Contas de armazenamento permitem acesso anônimo a blobs.",
            "Com a opção ligada, basta um contêiner configurado como público para que qualquer pessoa leia seus blobs sem credencial.",
            "allowBlobPublicAccess = false.",
            "Avalia a permissão da conta; não lista quais contêineres estão públicos. Um valor ausente é desconhecido.",
            "Arquivos internos podem ficar disponíveis na internet para quem souber o endereço, sem registro de quem baixou.",
            Docs(DocAnonymous), KnightCapability.AzureStorage);
        P("AK-AZ-STO-019", KnightSecurityDomain.Governance,
            "Contas de armazenamento podem ser excluídas sem remover antes um bloqueio.",
            "Excluir uma conta de armazenamento apaga todos os dados dela; um bloqueio CanNotDelete obriga um passo deliberado antes da exclusão.",
            "Bloqueio CanNotDelete (ou ReadOnly) no recurso, no grupo de recursos ou na assinatura.",
            "Considera bloqueios herdados do grupo e da assinatura.",
            "Uma automação errada ou um operador com pressa pode apagar a conta e seus dados de uma vez.",
            Docs(DocStorageLock, DocLocks), KnightCapability.AzureStorage, KnightCapability.AzureAuthorization);

        // ======== Key Vault ==================================================================================
        P("AK-AZ-KV-001", KnightSecurityDomain.Secrets,
            "Chaves habilitadas em cofres com RBAC não têm data de expiração.",
            "Sem expiração, uma chave criptográfica permanece válida indefinidamente; o ciclo de vida não força a troca nem sinaliza chaves esquecidas.",
            "Toda chave habilitada com atributo de expiração.",
            "Lê os metadados pelo Resource Manager; não lê material de chave.",
            "Chaves antigas continuam protegendo (ou assinando) dados muito além do previsto, e uma chave exposta não perde a validade sozinha.",
            Docs(DocKvKeys), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-002", KnightSecurityDomain.Secrets,
            "Chaves habilitadas em cofres com políticas de acesso não têm data de expiração.",
            "Nos cofres com políticas de acesso, a governança costuma ser mais frouxa; chaves sem expiração ali tendem a ser as mais esquecidas.",
            "Toda chave habilitada com atributo de expiração.",
            "Lê os metadados pelo Resource Manager; não lê material de chave.",
            "Uma chave antiga continua válida por tempo indeterminado num cofre cujo acesso é revisado com menos frequência.",
            Docs(DocKvKeys), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-003", KnightSecurityDomain.Secrets,
            "Segredos habilitados em cofres com RBAC não têm data de expiração.",
            "Segredos sem expiração — senhas, cadeias de conexão, tokens — ficam válidos indefinidamente e raramente são trocados.",
            "Todo segredo habilitado com atributo de expiração.",
            "Lê metadados; nenhum valor de segredo é lido.",
            "Uma senha guardada no cofre e copiada para outro lugar continua funcionando anos depois.",
            Docs(DocKvSecrets), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-004", KnightSecurityDomain.Secrets,
            "Segredos habilitados em cofres com políticas de acesso não têm data de expiração.",
            "O mesmo risco dos segredos sem expiração, agravado por um modelo de permissão mais difícil de revisar.",
            "Todo segredo habilitado com atributo de expiração.",
            "Lê metadados; nenhum valor de segredo é lido.",
            "Credenciais de aplicações podem ficar válidas por tempo indeterminado sem que ninguém seja lembrado de trocá-las.",
            Docs(DocKvSecrets), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-005", KnightSecurityDomain.Secrets,
            "Cofres de chaves sem proteção contra expurgo.",
            "Sem proteção contra expurgo, um item ou o cofre inteiro excluído pode ser apagado de forma definitiva antes do fim da retenção.",
            "enablePurgeProtection = true (irreversível depois de ligado).",
            "A proteção só vale com a exclusão reversível, que é obrigatória nos cofres atuais.",
            "Um invasor ou operador pode destruir chaves de criptografia e tornar dados cifrados irrecuperáveis.",
            Docs(DocKvSoftDelete), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-006", KnightSecurityDomain.IamRbac,
            "Cofres de chaves usam políticas de acesso em vez do Azure RBAC.",
            "As políticas de acesso não suportam escopo por item, PIM nem as revisões do Azure RBAC, e quem tem papel de colaborador no cofre pode conceder a si mesmo acesso aos segredos.",
            "enableRbacAuthorization = true.",
            "Não avalia as atribuições de papel de dados do cofre.",
            "Um colaborador do grupo de recursos pode se incluir na política do cofre e ler segredos sem aprovação.",
            Docs(DocKvRbac), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-007", KnightSecurityDomain.Network,
            "Cofres de chaves aceitam acesso pela rede pública.",
            "Com o acesso público, o cofre é alcançável da internet e protegido apenas pela autenticação de quem pede o segredo.",
            "publicNetworkAccess = Disabled, com acesso por endpoint privado.",
            "Não avalia as exceções de firewall do cofre.",
            "Uma identidade comprometida pode ler segredos do cofre de qualquer lugar da internet.",
            Docs(DocKvNetwork), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-008", KnightSecurityDomain.Network,
            "Cofres de chaves não têm endpoint privado aprovado.",
            "Sem endpoint privado, as aplicações internas falam com o cofre pelo endereço público, e não é possível fechar o acesso público.",
            "Ao menos uma conexão de endpoint privado aprovada por cofre.",
            "Não verifica a resolução DNS do endpoint privado.",
            "O cofre continua exposto na internet porque os sistemas internos dependem do endereço público.",
            Docs(DocKvPe), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-009", KnightSecurityDomain.Secrets,
            "Chaves habilitadas não têm política de rotação automática.",
            "Sem rotação automática, a troca da chave depende de processo manual, que costuma ser adiado; a mesma versão protege dados por tempo demais.",
            "Política de rotação com a ação Rotate em cada chave habilitada.",
            "A API omite a política quando ela está vazia; a ausência é lida como “sem política”.",
            "Uma versão de chave em uso há anos aumenta o volume de dados expostos se ela vazar.",
            Docs(DocKvRotation), KnightCapability.AzureKeyVault);
        P("AK-AZ-KV-010", KnightSecurityDomain.Secrets,
            "Certificados dos cofres têm validade acima de 12 meses.",
            "Certificados longos ficam mais tempo expostos se a chave privada vazar, e os emissores públicos já não emitem certificados com validade tão longa.",
            "Política do certificado com validity_months ≤ 12.",
            "Lê a política pelo plano de dados do cofre, o que exige papel próprio no cofre; sem ele, o controle fica não avaliado com o motivo.",
            "Um certificado com chave privada vazada pode ser usado para se passar pelo serviço por anos.",
            Docs(DocKvCerts, DocKvCertPolicyApi), KnightCapability.AzureKeyVault, KnightCapability.AzureKeyVaultCertificates);

        // ======== Computação =================================================================================
        P("AK-AZ-CMP-001", KnightSecurityDomain.Network,
            "Grupos de contêineres estão fora de rede virtual privada.",
            "Contêineres com IP público ficam diretamente alcançáveis da internet e não podem ser protegidos por NSGs e rotas da rede da organização.",
            "Grupo de contêineres implantado em sub-rede, sem IP público.",
            null,
            "Um serviço em contêiner pode ser explorado diretamente da internet, sem a filtragem da rede corporativa.",
            Docs(DocAciVnet), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-002", KnightSecurityDomain.Identity,
            "Grupos de contêineres não têm identidade gerenciada.",
            "Sem identidade gerenciada, contêineres que acessam outros recursos precisam de segredos em variáveis de ambiente, que vazam com facilidade.",
            "Identidade gerenciada atribuída ao grupo de contêineres.",
            "Não determina se o contêiner acessa outros recursos.",
            "Segredos embutidos na definição do contêiner podem ser lidos por quem tem acesso de leitura ao recurso.",
            Docs(DocAciIdentity), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-003", KnightSecurityDomain.DataProtection,
            "Contas do Batch usam chave gerenciada pela Microsoft.",
            "Com chave do cliente, a organização controla a revogação e a rotação da chave que protege os dados da conta.",
            "encryption.keySource = Microsoft.KeyVault.",
            null,
            "A organização não consegue revogar sozinha o acesso aos dados da conta numa saída do serviço ou num incidente.",
            Docs(DocBatchCmk), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-004", KnightSecurityDomain.Compute,
            "Pools do Batch estão sem criptografia de disco.",
            "Os nós dos pools gravam dados temporários e de tarefas em disco; sem criptografia, esses dados ficam legíveis em caso de acesso ao armazenamento subjacente.",
            "diskEncryptionConfiguration com disco do SO e temporário.",
            "Lê os pools pelo Resource Manager; pools de configuração de serviço de nuvem (legado) não têm a propriedade.",
            "Dados processados pelas tarefas podem permanecer legíveis nos discos dos nós.",
            Docs(DocBatchDisk), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-005", KnightSecurityDomain.Identity,
            "Contas do Batch aceitam autenticação por chave compartilhada.",
            "A chave da conta do Batch dá controle de jobs e pools sem identidade; com o Entra ID, o acesso fica atribuído a pessoas e aplicações.",
            "allowedAuthenticationModes sem SharedKey.",
            null,
            "Quem obtém a chave da conta pode submeter tarefas e executar código nos nós sem deixar identidade.",
            Docs(DocBatchAad), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-006", KnightSecurityDomain.Network,
            "Contas do Batch não têm endpoint privado.",
            "Sem endpoint privado, o gerenciamento da conta do Batch passa pelo endereço público do serviço.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            null,
            "O plano de controle da conta fica alcançável da internet para quem tiver credenciais.",
            Docs(DocBatchPe), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-007", KnightSecurityDomain.Network,
            "Contas do Batch aceitam acesso pela rede pública.",
            "Com acesso público, a conta aceita chamadas de qualquer rede, e a proteção depende só da autenticação.",
            "publicNetworkAccess = Disabled.",
            null,
            "Uma credencial vazada da conta pode ser usada de qualquer lugar para submeter trabalhos.",
            Docs(DocBatchPublic), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-008", KnightSecurityDomain.Compute,
            "Máquinas virtuais usam discos não gerenciados (VHD em conta de armazenamento).",
            "Discos não gerenciados dependem da segurança da conta de armazenamento que os guarda e não têm os recursos dos discos gerenciados (criptografia com chave do cliente, controle de exportação).",
            "Disco do SO gerenciado.",
            "Os discos não gerenciados estão em aposentadoria pela plataforma.",
            "Quem tiver a chave da conta de armazenamento pode copiar o disco inteiro da máquina.",
            Docs(DocManagedDisks), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-009", KnightSecurityDomain.DataProtection,
            "Discos anexados a máquinas virtuais usam chave gerenciada pela plataforma.",
            "Com a chave do cliente (conjunto de criptografia de disco), a organização controla a revogação e a rotação da chave dos discos.",
            "encryption.type com chave do cliente nos discos anexados.",
            "Avalia discos gerenciados; quais guardam dados sensíveis é decisão organizacional.",
            "A organização não consegue tornar ilegíveis os discos de uma máquina revogando a própria chave.",
            Docs(DocDiskEncryption), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-010", KnightSecurityDomain.DataProtection,
            "Discos não anexados usam chave gerenciada pela plataforma.",
            "Discos soltos costumam ser cópias de máquinas apagadas; sem chave do cliente, continuam legíveis para quem puder exportá-los.",
            "Discos não anexados com chave do cliente, ou excluídos quando desnecessários.",
            "Não determina se o disco ainda é necessário.",
            "Dados de máquinas desativadas permanecem acessíveis em discos esquecidos.",
            Docs(DocDiskEncryption), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-011", KnightSecurityDomain.Network,
            "Discos gerenciados aceitam exportação e importação de qualquer rede.",
            "Com acesso de todas as redes, quem gerar uma URL de exportação pode baixar o disco inteiro de qualquer lugar.",
            "networkAccessPolicy diferente de AllowAll, ou acesso público desabilitado.",
            "Um valor ausente é desconhecido.",
            "Uma cópia completa do disco de uma máquina pode ser baixada para fora da organização.",
            Docs(DocDiskPrivate, DocDiskRestrict), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-012", KnightSecurityDomain.Identity,
            "Discos permitem exportar dados sem autenticação do Entra ID.",
            "Sem dataAccessAuthMode, a URL de exportação (SAS) basta para baixar o disco, sem identidade nem permissão de dados.",
            "dataAccessAuthMode = AzureActiveDirectory.",
            null,
            "Uma URL de exportação vazada permite baixar o disco sem que se saiba quem o fez.",
            Docs(DocDiskRestrict), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-013", KnightSecurityDomain.Compute,
            "Máquinas virtuais sem extensão de proteção de endpoint reconhecida.",
            "Sem proteção de endpoint, malware e atividade de invasores dentro da máquina não são detectados nem bloqueados.",
            "Extensão MDE.Windows/MDE.Linux (Defender para Endpoint) ou IaaSAntimalware provisionada.",
            "Uma solução de terceiros instalada sem extensão não é visível pelo Resource Manager: a máquina fica como não avaliada, nunca como reprovada.",
            "Um malware executado na máquina pode persistir e se espalhar sem detecção.",
            Docs(DocMde, DocVmExtensions), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-014", KnightSecurityDomain.DataProtection,
            "Máquinas com VHD não gerenciado não cifram o disco.",
            "O VHD fica como blob numa conta de armazenamento; sem Azure Disk Encryption, quem copiar o blob lê o disco.",
            "encryptionSettings habilitado no disco do SO em VHD.",
            "Só se aplica a máquinas com disco não gerenciado.",
            "Uma cópia do blob do disco expõe o sistema e os dados da máquina.",
            Docs(DocAde), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-015", KnightSecurityDomain.Compute,
            "Máquinas virtuais sem inicialização confiável.",
            "A inicialização confiável (inicialização segura e vTPM) impede bootkits e rootkits de carregarem antes do sistema e permite atestar a integridade da máquina.",
            "securityType = TrustedLaunch ou ConfidentialVM.",
            "Máquinas de geração 1 não suportam inicialização confiável sem recriação.",
            "Um rootkit pode se instalar abaixo do sistema operacional e sobreviver a reinstalações.",
            Docs(DocTrustedLaunch), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-016", KnightSecurityDomain.DataProtection,
            "Máquinas virtuais sem criptografia no host.",
            "A criptografia no host cifra discos temporários e caches no próprio host, que a criptografia do armazenamento não cobre.",
            "encryptionAtHost = true.",
            "Exige o recurso registrado na assinatura e tamanhos de VM compatíveis.",
            "Dados em discos temporários e caches da máquina ficam sem criptografia no host físico.",
            Docs(DocDiskEncryption), KnightCapability.AzureCompute);
        P("AK-AZ-CMP-017", KnightSecurityDomain.Network,
            "Endpoints privados das contas do Batch sem a zona DNS privada do serviço.",
            "Sem a zona privatelink.batch.azure.com, o nome da conta continua resolvendo para o endereço público dentro da rede, e o tráfego não usa o endpoint privado.",
            "Grupo de zonas DNS com privatelink.batch.azure.com em cada endpoint privado aprovado.",
            "Um servidor DNS próprio da organização, que também resolve o nome, não é visível pelo Resource Manager.",
            "Clientes e nós podem acessar a conta pelo caminho público mesmo com o endpoint privado criado.",
            Docs(DocPeDns, DocBatchPe), KnightCapability.AzureCompute, KnightCapability.AzureNetworking);

        // ======== App Service ================================================================================
        Runtime("AK-AZ-APP-001", "Java",
            "Versões do Java fora de suporte deixam de receber correções de segurança da plataforma e do runtime.",
            "Uma vulnerabilidade do runtime Java pode ser explorada nas aplicações sem que haja correção disponível para a versão em uso.");
        Runtime("AK-AZ-APP-002", "Python",
            "Versões do Python fora de suporte não recebem correções de segurança, e o App Service deixa de atualizar a imagem correspondente.",
            "Bibliotecas e o interpretador em versões encerradas acumulam falhas conhecidas exploráveis.");
        Runtime("AK-AZ-APP-003", "PHP",
            "Versões do PHP fora de suporte acumulam vulnerabilidades sem correção e são alvo frequente de ataques automatizados.",
            "Sites em PHP sem suporte podem ser tomados por ataques automatizados que exploram falhas antigas do interpretador.");
        P("AK-AZ-APP-004", KnightSecurityDomain.Identity,
            "Aplicativos aceitam publicação com usuário e senha (credenciais básicas de FTP e SCM).",
            "As credenciais básicas de publicação não passam pelo Entra ID nem pela MFA; quem obtém o perfil de publicação implanta código no aplicativo.",
            "Políticas ftp e scm com allow = false.",
            "Avalia aplicativos, funções e slots.",
            "Um perfil de publicação vazado permite substituir o código em produção por uma versão maliciosa.",
            Docs(DocBasicAuth), KnightCapability.AzureAppService);
        P("AK-AZ-APP-005", KnightSecurityDomain.Network,
            "Aplicativos aceitam FTP sem criptografia.",
            "No FTP sem TLS, credenciais e arquivos do aplicativo trafegam em claro.",
            "ftpsState = FtpsOnly ou Disabled.",
            null,
            "Credenciais de publicação e código podem ser capturados na rede.",
            Docs(DocFtp), KnightCapability.AzureAppService);
        P("AK-AZ-APP-006", KnightSecurityDomain.Network,
            "Aplicativos estão com o HTTP/2 desabilitado.",
            "O HTTP/2 é a versão atual do protocolo; mantê-lo desligado afasta a configuração da referência recomendada para aplicativos web.",
            "http20Enabled = true.",
            "Controle de configuração; não indica exposição por si só.",
            "Os clientes ficam em HTTP/1.1, e a configuração do aplicativo diverge da linha de base dos demais.",
            Docs(DocAppConfig), KnightCapability.AzureAppService);
        P("AK-AZ-APP-007", KnightSecurityDomain.Network,
            "Aplicativos aceitam requisições HTTP sem redirecioná-las para HTTPS.",
            "Sem “Somente HTTPS”, clientes que acessam pelo endereço http enviam cookies e credenciais em claro.",
            "httpsOnly = true.",
            null,
            "Sessões e credenciais de usuários podem ser capturadas em redes não confiáveis.",
            Docs(DocAppHttps), KnightCapability.AzureAppService);
        P("AK-AZ-APP-008", KnightSecurityDomain.Network,
            "Aplicativos aceitam TLS de entrada abaixo de 1.2.",
            "TLS 1.0 e 1.1 têm fraquezas conhecidas; aceitá-los permite negociações mais fracas com clientes desatualizados.",
            "minTlsVersion = 1.2 ou 1.3.",
            null,
            "Conexões ao aplicativo podem ser rebaixadas e interceptadas.",
            Docs(DocAppTls), KnightCapability.AzureAppService);
        P("AK-AZ-APP-009", KnightSecurityDomain.Network,
            "Aplicativos estão sem criptografia TLS de ponta a ponta dentro do App Service.",
            "Por padrão o TLS termina no front-end do App Service; sem a opção de ponta a ponta, o trecho até o worker segue sem criptografia na rede da plataforma.",
            "endToEndEncryptionEnabled = true.",
            null,
            "Dados sensíveis trafegam sem criptografia num trecho interno da plataforma, em desacordo com exigências de criptografia contínua.",
            Docs(DocAppTls), KnightCapability.AzureAppService);
        P("AK-AZ-APP-010", KnightSecurityDomain.Compute,
            "Aplicativos estão com a depuração remota ligada.",
            "A depuração remota abre portas adicionais e permite anexar um depurador ao processo, com acesso à memória e ao fluxo da aplicação.",
            "remoteDebuggingEnabled = false.",
            "A plataforma desliga a depuração remota automaticamente depois de 48 horas; o controle vê o estado no momento da coleta.",
            "Quem tiver credenciais de publicação pode inspecionar e alterar o comportamento do aplicativo em execução.",
            Docs(DocRemoteDebug), KnightCapability.AzureAppService);
        P("AK-AZ-APP-011", KnightSecurityDomain.Identity,
            "Aplicativos não exigem certificado de cliente na entrada.",
            "Sem certificado de cliente, a aplicação depende só da autenticação da própria aplicação para saber quem é o cliente; o mTLS acrescenta uma prova forte de origem.",
            "clientCertEnabled = true e clientCertMode = Required.",
            "Nem toda aplicação atende clientes capazes de apresentar certificado; o critério é adequado a APIs entre sistemas.",
            "APIs entre sistemas podem ser chamadas por qualquer cliente que obtenha uma credencial de aplicação.",
            Docs(DocAppMtls), KnightCapability.AzureAppService);
        P("AK-AZ-APP-012", KnightSecurityDomain.Identity,
            "A autenticação do App Service está desligada em aplicativos e slots.",
            "A autenticação integrada exige usuário autenticado antes de a requisição chegar ao código; sem ela, a proteção depende inteiramente da implementação da aplicação.",
            "authsettingsV2 com platform.enabled = true e provedor Microsoft Entra ID.",
            "Aplicações que implementam a própria autenticação podem atender o critério sem a funcionalidade da plataforma; o controle não lê o código.",
            "Uma falha de autenticação no código pode expor páginas internas a qualquer visitante.",
            Docs(DocAppAuth), KnightCapability.AzureAppService);
        P("AK-AZ-APP-013", KnightSecurityDomain.Identity,
            "Aplicativos não têm identidade gerenciada.",
            "Sem identidade gerenciada, o aplicativo acessa bancos, cofres e armazenamento com segredos em configuração, que vazam e raramente são trocados.",
            "Identidade gerenciada atribuída.",
            "Não determina se o aplicativo acessa outros recursos.",
            "Uma cadeia de conexão vazada da configuração dá acesso direto aos dados do aplicativo.",
            Docs(DocAppIdentity), KnightCapability.AzureAppService);
        P("AK-AZ-APP-014", KnightSecurityDomain.Network,
            "Aplicativos aceitam acesso pela rede pública.",
            "Aplicações internas expostas na internet ficam ao alcance de varreduras e ataques, protegidas só pelas regras de acesso e pela autenticação.",
            "publicNetworkAccess = Disabled para aplicações que só devem ser acessadas por redes privadas.",
            "Aplicações públicas por natureza atendem a outro critério (WAF, autenticação); a decisão de quais devem ser privadas é organizacional.",
            "Uma aplicação interna pode ser descoberta e atacada a partir da internet.",
            Docs(DocAppAccess), KnightCapability.AzureAppService);
        P("AK-AZ-APP-015", KnightSecurityDomain.Network,
            "Aplicativos web sem endpoint privado aprovado.",
            "Sem endpoint privado, a aplicação só é alcançável pelo endereço público, e fechar o acesso público fica inviável.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            "O plano do App Service precisa suportar endpoints privados (camadas Basic e superiores).",
            "A aplicação interna continua dependendo de exposição pública para atender os usuários da organização.",
            Docs(DocAppPe), KnightCapability.AzureAppService);
        P("AK-AZ-APP-016", KnightSecurityDomain.Network,
            "Aplicativos não estão integrados a uma rede virtual.",
            "Sem integração, o tráfego de saída do aplicativo sai pelo endereço público da plataforma e não pode ser controlado por NSGs, rotas ou firewall da organização.",
            "virtualNetworkSubnetId definido.",
            null,
            "Um aplicativo comprometido pode enviar dados para qualquer destino da internet sem passar pelos controles de saída.",
            Docs(DocAppVnet), KnightCapability.AzureAppService);
        P("AK-AZ-APP-017", KnightSecurityDomain.Network,
            "Imagens de contêiner e conteúdo dos aplicativos são buscados fora da rede virtual.",
            "Mesmo integrado, o aplicativo busca imagens e o compartilhamento de conteúdo pela rede pública, a menos que o roteamento correspondente esteja ligado — o que obriga a manter registro e armazenamento acessíveis publicamente.",
            "vnetImagePullEnabled = true e vnetContentShareEnabled = true.",
            "O roteamento de conteúdo só se aplica a aplicativos que usam compartilhamento de conteúdo (funções em planos elásticos).",
            "O registro de contêiner e a conta de conteúdo continuam abertos à internet para atender o aplicativo.",
            Docs(DocAppRouting), KnightCapability.AzureAppService);
        P("AK-AZ-APP-018", KnightSecurityDomain.Network,
            "Parte do tráfego de saída dos aplicativos não passa pela rede virtual.",
            "Sem rotear todo o tráfego, só destinos privados usam a rede virtual; o restante sai direto, fora do firewall e da inspeção da organização.",
            "vnetRouteAllEnabled = true.",
            null,
            "Conexões do aplicativo com a internet escapam do firewall e dos registros de saída.",
            Docs(DocAppRouting), KnightCapability.AzureAppService);
        P("AK-AZ-APP-019", KnightSecurityDomain.Network,
            "O CORS dos aplicativos aceita requisições de qualquer origem.",
            "Com “*”, qualquer site pode chamar a API a partir do navegador do usuário; se a API depender de cookies ou tokens do navegador, outro site age em nome do usuário.",
            "Lista de origens do CORS com domínios nominais.",
            null,
            "Um site malicioso pode ler respostas da API usando a sessão de um usuário que o visite.",
            Docs(DocAppCors), KnightCapability.AzureAppService);
        P("AK-AZ-APP-020", KnightSecurityDomain.Network,
            "Ambientes do App Service expõem os aplicativos por balanceador externo.",
            "Com balanceador externo, os aplicativos do ambiente recebem tráfego da internet; o balanceador interno restringe o acesso à rede virtual.",
            "internalLoadBalancingMode diferente de None para ambientes de aplicações internas.",
            "Ambientes que hospedam aplicações públicas podem usar balanceador externo por decisão.",
            "Aplicações internas do ambiente ficam acessíveis da internet.",
            Docs(DocAse), KnightCapability.AzureAppService);
        P("AK-AZ-APP-021", KnightSecurityDomain.Compute,
            "Ambientes do App Service estão em versão anterior à v3.",
            "As versões 1 e 2 do App Service Environment foram aposentadas e não recebem mais atualizações.",
            "Ambientes ASEV3.",
            null,
            "Os aplicativos do ambiente rodam numa plataforma sem atualizações de segurança e sem suporte.",
            Docs(DocAse), KnightCapability.AzureAppService);
        P("AK-AZ-APP-022", KnightSecurityDomain.DataProtection,
            "Ambientes do App Service estão sem criptografia interna.",
            "Sem a configuração InternalEncryption, o tráfego interno entre os componentes do ambiente e os arquivos temporários não são cifrados.",
            "clusterSettings InternalEncryption = true.",
            null,
            "Dados processados pelos aplicativos podem trafegar e permanecer sem criptografia dentro do ambiente.",
            Docs(DocAseSettings), KnightCapability.AzureAppService);
        P("AK-AZ-APP-023", KnightSecurityDomain.Network,
            "Ambientes do App Service aceitam TLS 1.0 e 1.1.",
            "Os protocolos legados continuam aceitos pelos front-ends do ambiente, permitindo conexões com criptografia fraca.",
            "clusterSettings DisableTls1.0 = 1.",
            null,
            "Clientes podem negociar protocolos fracos com os aplicativos do ambiente.",
            Docs(DocAseSettings), KnightCapability.AzureAppService);
        P("AK-AZ-APP-024", KnightSecurityDomain.Network,
            "Ambientes do App Service não definem a ordem das cifras TLS.",
            "Sem ordem definida, os front-ends aceitam a lista padrão da plataforma, que pode incluir cifras fracas por compatibilidade.",
            "clusterSettings FrontEndSSLCipherSuiteOrder com cifras fortes.",
            "Verifica a existência da configuração; não avalia as cifras escolhidas.",
            "Conexões podem ser estabelecidas com cifras mais fracas do que a política da organização permite.",
            Docs(DocAseSettings), KnightCapability.AzureAppService);
        P("AK-AZ-APP-025", KnightSecurityDomain.Network,
            "Endpoints privados dos aplicativos sem a zona DNS privada do App Service.",
            "Sem a zona privatelink.azurewebsites.net, o nome do aplicativo resolve para o endereço público dentro da rede: o endpoint privado existe, mas não é usado.",
            "Grupo de zonas DNS com privatelink.azurewebsites.net em cada endpoint privado aprovado.",
            "Um servidor DNS próprio da organização, que também resolve o nome, não é visível pelo Resource Manager.",
            "O tráfego interno para o aplicativo continua saindo pelo caminho público, e fechar o acesso público quebra o acesso interno.",
            Docs(DocPeDns, DocAppPe), KnightCapability.AzureAppService, KnightCapability.AzureNetworking);
        P("AK-AZ-APP-026", KnightSecurityDomain.Network,
            "Planos do App Service em camada que não suporta endpoint privado.",
            "Aplicativos nesses planos só podem ser alcançados pelo endereço público: o isolamento por Private Link não está disponível sem mudar de camada.",
            "Plano Basic ou superior (ou Functions Premium/Flex Consumption), conforme a documentação.",
            "Uma camada fora da lista documentada não aprova nem reprova. Nem todo aplicativo precisa de endpoint privado.",
            "Aplicativos internos hospedados nesses planos ficam necessariamente expostos à internet.",
            Docs(DocAppPe, DocFuncNet), KnightCapability.AzureAppService);

        // ======== Bancos de dados ============================================================================
        P("AK-AZ-DB-001", KnightSecurityDomain.Identity,
            "Caches do Redis estão sem autenticação do Microsoft Entra ID.",
            "Sem o Entra ID, o acesso ao cache depende de chaves compartilhadas, sem identidade nem revogação individual.",
            "redisConfiguration aad-enabled = true.",
            null,
            "Uma chave de acesso vazada dá acesso ao cache a qualquer um, sem registro de quem foi.",
            Docs(DocRedisEntra), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-002", KnightSecurityDomain.Network,
            "Caches do Redis aceitam conexões sem SSL.",
            "Na porta sem SSL, comandos, dados e a chave de acesso trafegam em claro.",
            "enableNonSslPort = false.",
            "Quando a propriedade é nula, vale o padrão documentado: porta sem SSL desabilitada.",
            "Dados de sessão e a própria chave do cache podem ser capturados na rede.",
            Docs(DocRedisConfig), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-003", KnightSecurityDomain.Network,
            "Caches do Redis aceitam TLS abaixo de 1.2.",
            "TLS 1.0 e 1.1 foram retirados pela plataforma por fraquezas conhecidas; manter o mínimo abaixo de 1.2 permite conexões mais fracas.",
            "minimumTlsVersion = 1.2.",
            "Um valor ausente é desconhecido.",
            "Clientes antigos podem negociar conexões fracas e expor os dados do cache.",
            Docs(DocRedisTls), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-004", KnightSecurityDomain.Network,
            "Clusters do Redis Enterprise aceitam TLS abaixo de 1.2.",
            "O mesmo risco do protocolo fraco, nos clusters do Redis Enterprise (Azure Managed Redis).",
            "minimumTlsVersion = 1.2 no cluster.",
            null,
            "Conexões ao cluster podem ser negociadas com protocolo fraco e interceptadas.",
            Docs(DocAmr), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-005", KnightSecurityDomain.Identity,
            "Caches do Redis não têm identidade gerenciada do sistema.",
            "A identidade gerenciada permite ao cache acessar armazenamento (importação, exportação, persistência) sem chaves guardadas na configuração.",
            "identity.type contendo SystemAssigned.",
            null,
            "Operações de persistência e exportação dependem de chaves de armazenamento guardadas no cache.",
            Docs(DocRedisIdentity), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-006", KnightSecurityDomain.Identity,
            "Clusters do Redis Enterprise não têm identidade gerenciada.",
            "Sem identidade gerenciada, o cluster não pode usar chave do cliente no Key Vault nem acessar armazenamento sem segredos.",
            "identity.type diferente de None.",
            null,
            "A criptografia com chave do cliente fica indisponível e o acesso a outros recursos depende de segredos.",
            Docs(DocAmrEncryption), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-007", KnightSecurityDomain.Network,
            "Caches do Redis aceitam acesso pela rede pública.",
            "Com acesso público, o cache é alcançável da internet, protegido só pela chave ou pela identidade.",
            "publicNetworkAccess = Disabled.",
            null,
            "Uma chave vazada permite ler e alterar o cache a partir de qualquer lugar.",
            Docs(DocRedisPe), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-008", KnightSecurityDomain.Network,
            "Clusters do Redis Enterprise aceitam acesso pela rede pública.",
            "O cluster fica alcançável da internet, e a proteção depende só da autenticação.",
            "publicNetworkAccess = Disabled no cluster.",
            null,
            "Credenciais vazadas do cluster podem ser usadas de qualquer rede.",
            Docs(DocAmrPe), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-009", KnightSecurityDomain.Network,
            "Caches do Redis sem endpoint privado aprovado.",
            "Sem Private Link, as aplicações internas usam o endereço público do cache, e o acesso público não pode ser fechado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            null,
            "O cache permanece exposto na internet para atender os sistemas internos.",
            Docs(DocRedisPe), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-010", KnightSecurityDomain.Network,
            "Clusters do Redis Enterprise sem endpoint privado aprovado.",
            "O cluster continua dependendo do endereço público para atender as aplicações internas.",
            "Ao menos uma conexão de endpoint privado aprovada no cluster.",
            null,
            "O fechamento do acesso público fica bloqueado pela dependência do endereço público.",
            Docs(DocAmrPe), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-011", KnightSecurityDomain.DataProtection,
            "Clusters do Redis Enterprise usam chave gerenciada pela Microsoft.",
            "Com chave do cliente, a organização controla revogação e rotação da chave que protege os dados persistidos do cluster.",
            "keyEncryptionKeyUrl configurado.",
            null,
            "A organização não consegue revogar sozinha o acesso aos dados persistidos do cluster.",
            Docs(DocAmrEncryption), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-012", KnightSecurityDomain.Identity,
            "Caches do Redis aceitam autenticação por chave de acesso.",
            "Enquanto a chave de acesso for aceita, o Entra ID não é obrigatório: a chave dá acesso completo sem identidade.",
            "disableAccessKeyAuthentication = true.",
            "Quando a propriedade é nula, vale o padrão documentado: chaves de acesso habilitadas.",
            "A chave do cache copiada para uma aplicação ou script continua abrindo o cache sem que se saiba quem a usou.",
            Docs(DocRedisEntra), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-013", KnightSecurityDomain.Identity,
            "Bancos dos clusters do Redis Enterprise aceitam autenticação por chave de acesso.",
            "As chaves de acesso do banco dão acesso completo sem identidade, e coexistem com o Entra ID enquanto não forem desabilitadas.",
            "accessKeysAuthentication = Disabled em cada banco.",
            null,
            "Uma chave do banco vazada continua funcionando mesmo com o Entra ID configurado.",
            Docs(DocAmrEntra), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-014", KnightSecurityDomain.Governance,
            "Caches do Redis recebem atualizações antes do canal estável.",
            "O canal Preview recebe atualizações do Redis antes da validação ampla; adequado a ambientes de teste, não a produção.",
            "updateChannel = Stable.",
            "Quando a propriedade é nula, vale o padrão documentado: Stable.",
            "Uma atualização ainda não estabilizada pode afetar a disponibilidade de caches de produção.",
            Docs(DocRedisAdmin), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-015", KnightSecurityDomain.Network,
            "Contas do Cosmos DB aceitam conexões de todas as redes.",
            "Sem filtro de rede virtual nem regras de IP, a conta aceita conexões de qualquer endereço da internet.",
            "Acesso público desabilitado, ou filtro de rede virtual / regras de IP.",
            null,
            "Uma chave ou token vazado pode ser usado de qualquer lugar contra a conta.",
            Docs(DocCosmosFw), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-016", KnightSecurityDomain.Network,
            "Contas do Cosmos DB sem endpoint privado aprovado.",
            "Sem endpoint privado, as aplicações internas dependem do endereço público do Cosmos DB.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            null,
            "A conta continua exposta publicamente para atender sistemas internos.",
            Docs(DocCosmosPe), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-017", KnightSecurityDomain.Identity,
            "Contas do Cosmos DB aceitam chaves da conta (autenticação local).",
            "A chave primária do Cosmos DB dá acesso completo aos dados sem identidade; com o Entra ID, o acesso é atribuído e revogável.",
            "disableLocalAuth = true.",
            "Quando a propriedade é nula, vale o padrão documentado: chaves aceitas.",
            "Uma chave da conta vazada permite ler e apagar documentos sem deixar identidade.",
            Docs(DocCosmosRbac), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-018", KnightSecurityDomain.Network,
            "Contas do Cosmos DB aceitam acesso pela rede pública.",
            "Com acesso público, a conta é alcançável da internet e protegida só pelas regras de firewall e pelas credenciais.",
            "publicNetworkAccess = Disabled.",
            null,
            "Credenciais vazadas podem ser usadas da internet contra o banco.",
            Docs(DocCosmosPe), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-019", KnightSecurityDomain.DataProtection,
            "Contas do Cosmos DB usam chave gerenciada pela Microsoft.",
            "Com chave do cliente, a organização controla revogação e rotação da chave dos dados críticos.",
            "keyVaultKeyUri definido.",
            "Quais contas guardam dados críticos é decisão organizacional.",
            "A organização não consegue revogar sozinha o acesso aos dados da conta.",
            Docs(DocCosmosCmk), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-020", KnightSecurityDomain.Network,
            "O firewall do Cosmos DB tem regra que libera toda a internet ou todos os datacenters do Azure.",
            "A regra 0.0.0.0 libera qualquer serviço do Azure, de qualquer cliente; 0.0.0.0/0 libera a internet inteira — o firewall deixa de restringir.",
            "Nenhuma regra de IP 0.0.0.0/0, ::/0 ou 0.0.0.0.",
            null,
            "Recursos de outras organizações hospedados no Azure, ou qualquer endereço da internet, alcançam a conta.",
            Docs(DocCosmosFw), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-021", KnightSecurityDomain.DataProtection,
            "Fábricas do Data Factory usam chave gerenciada pela Microsoft.",
            "Com chave do cliente, a organização controla a revogação da chave que protege metadados e credenciais da fábrica.",
            "encryption.vaultBaseUrl e keyName definidos.",
            null,
            "A organização não consegue revogar sozinha o acesso às definições e credenciais da fábrica.",
            Docs(DocAdfCmk), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-022", KnightSecurityDomain.Identity,
            "Fábricas do Data Factory não têm identidade gerenciada.",
            "Sem identidade gerenciada, os serviços vinculados precisam de senhas e chaves guardadas na fábrica.",
            "Identidade gerenciada atribuída e usada nos serviços vinculados.",
            "Não verifica se os serviços vinculados usam a identidade.",
            "Credenciais de fontes de dados ficam guardadas na fábrica e podem ser extraídas por quem a administra.",
            Docs(DocAdfIdentity), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-023", KnightSecurityDomain.DataProtection,
            "Servidores MySQL usam chave gerenciada pela Microsoft.",
            "Com chave do cliente no Key Vault, a organização decide quando revogar ou girar a chave que cifra os dados do MySQL.",
            "dataEncryption.type = AzureKeyVault.",
            null,
            "A organização não consegue revogar sozinha o acesso aos dados do servidor MySQL.",
            Docs(DocMySqlCmk), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-024", KnightSecurityDomain.Identity,
            "Servidores MySQL aceitam usuários locais além do Entra ID.",
            "Usuários do próprio MySQL têm senhas fora do diretório, sem MFA, sem acesso condicional e sem desligamento automático.",
            "Administrador do Entra ID configurado e aad_auth_only = ON.",
            null,
            "Contas locais esquecidas continuam com acesso ao banco depois da saída de quem as usava.",
            Docs(DocMySqlEntra), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-025", KnightSecurityDomain.Network,
            "Servidores MySQL aceitam acesso pela rede pública.",
            "Com acesso público, o servidor fica alcançável da internet, protegido só pelo firewall e pelas senhas.",
            "network.publicNetworkAccess = Disabled.",
            null,
            "Ataques de força bruta contra o MySQL podem ser feitos de qualquer lugar.",
            Docs(DocMySqlPublic), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-026", KnightSecurityDomain.Network,
            "Servidores MySQL sem endpoint privado aprovado.",
            "Sem endpoint privado, as aplicações chegam ao MySQL pelo endereço público, que por isso não pode ser fechado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            "Servidores com acesso privado por integração de rede virtual (sub-rede delegada) não usam endpoint privado e aparecem como sem endpoint.",
            "O acesso público do MySQL não pode ser fechado sem interromper as aplicações.",
            Docs(DocMySqlPe), KnightCapability.AzureDatabases);
        DbParam("AK-AZ-DB-027", "MySQL", "audit_log_enabled", DocMySqlAudit,
            "Sem o log de auditoria, conexões e consultas ao MySQL não ficam registradas para investigação.",
            "Um acesso indevido ao banco não deixa registro de quem consultou o quê.");
        DbParam("AK-AZ-DB-028", "MySQL", "audit_log_events", DocMySqlAudit,
            "Se a auditoria não inclui o evento CONNECTION, entradas e falhas de login no MySQL não são registradas.",
            "Tentativas de senha contra o banco e acessos bem-sucedidos de origens estranhas passam sem registro.");
        DbParam("AK-AZ-DB-029", "MySQL", "error_server_log_file", DocMySqlErrors,
            "O arquivo de log de erros registra falhas de inicialização, conexões rejeitadas e erros de replicação úteis ao diagnóstico e à investigação.",
            "Problemas operacionais e sinais de ataque no servidor ficam sem registro consultável.");
        DbParam("AK-AZ-DB-030", "MySQL", "require_secure_transport", DocMySqlTls,
            "Sem transporte seguro obrigatório, clientes podem se conectar ao MySQL sem TLS e trafegar consultas e senhas em claro.",
            "Credenciais do banco e resultados de consultas podem ser capturados na rede.");
        DbParam("AK-AZ-DB-031", "MySQL", "tls_version", DocMySqlTls,
            "Aceitar TLS 1.0 ou 1.1 no MySQL permite negociações com protocolos que têm fraquezas conhecidas.",
            "Conexões ao MySQL podem ser rebaixadas e interceptadas.");
        P("AK-AZ-DB-032", KnightSecurityDomain.DataProtection,
            "Servidores PostgreSQL usam chave gerenciada pela Microsoft.",
            "Com chave do cliente no Key Vault, a organização decide quando revogar ou girar a chave que cifra os dados do PostgreSQL.",
            "dataEncryption.type = AzureKeyVault.",
            null,
            "A organização não consegue revogar sozinha o acesso aos dados do servidor PostgreSQL.",
            Docs(DocPgCmk), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-033", KnightSecurityDomain.Identity,
            "Servidores PostgreSQL aceitam autenticação por senha.",
            "Usuários locais do PostgreSQL têm senhas fora do diretório, sem MFA e sem desligamento junto com a conta corporativa.",
            "activeDirectoryAuth = Enabled e passwordAuth = Disabled.",
            null,
            "Senhas de banco compartilhadas ou esquecidas continuam válidas indefinidamente.",
            Docs(DocPgEntra), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-034", KnightSecurityDomain.Network,
            "Servidores PostgreSQL aceitam acesso pela rede pública.",
            "Com acesso público, o servidor é alcançável da internet, protegido só pelo firewall e pelas senhas.",
            "network.publicNetworkAccess = Disabled.",
            null,
            "Ataques de força bruta e exploração do PostgreSQL podem vir de qualquer lugar.",
            Docs(DocPgPublic), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-035", KnightSecurityDomain.Network,
            "Servidores PostgreSQL sem endpoint privado aprovado.",
            "Sem endpoint privado, as aplicações chegam ao PostgreSQL pelo endereço público, que por isso não pode ser fechado.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            "Servidores com integração de rede virtual (sub-rede delegada) não usam endpoint privado e aparecem como sem endpoint.",
            "O acesso público do PostgreSQL não pode ser fechado sem interromper as aplicações.",
            Docs(DocPgPe), KnightCapability.AzureDatabases);
        DbParam("AK-AZ-DB-036", "PostgreSQL", "connection_throttle.enable", DocPgParams,
            "A limitação de conexões reduz o ritmo de tentativas de login falhas de um mesmo IP, dificultando força bruta.",
            "Tentativas de senha em alta velocidade contra o PostgreSQL seguem sem atraso.");
        DbParam("AK-AZ-DB-037", "PostgreSQL", "logfiles.retention_days", DocPgLogging,
            "Com retenção de até três dias, os logs do servidor somem antes que a maioria dos incidentes seja percebida.",
            "A investigação de um acesso ao banco encontra os logs já apagados.");
        DbParam("AK-AZ-DB-038", "PostgreSQL", "log_checkpoints", DocPgLogging,
            "O registro de checkpoints ajuda a identificar picos de carga e comportamentos anômalos de escrita no banco.",
            "Picos de escrita incomuns — como uma alteração em massa — ficam sem registro para análise.");
        DbParam("AK-AZ-DB-039", "PostgreSQL", "log_disconnections", DocPgLogging,
            "Sem registrar desconexões, não se sabe quanto durou cada sessão no banco.",
            "A duração de uma sessão suspeita no banco não pode ser determinada.");
        DbParam("AK-AZ-DB-040", "PostgreSQL", "log_connections", DocPgLogging,
            "Sem registrar conexões, tentativas de login e acessos ao PostgreSQL não ficam nos logs.",
            "Acessos ao banco de origens desconhecidas passam sem registro.");
        DbParam("AK-AZ-DB-041", "PostgreSQL", "require_secure_transport", DocPgTls,
            "Sem transporte seguro obrigatório, clientes podem se conectar ao PostgreSQL sem TLS.",
            "Senhas e dados do PostgreSQL podem trafegar em claro e ser capturados.");
        DbParam("AK-AZ-DB-042", "PostgreSQL", "ssl_min_protocol_version", DocPgTls,
            "Aceitar versões de TLS abaixo de 1.2 no PostgreSQL permite negociações com protocolos fracos.",
            "Conexões ao PostgreSQL podem ser rebaixadas e interceptadas.");
        P("AK-AZ-DB-043", KnightSecurityDomain.Logging,
            "Servidores SQL estão sem auditoria no nível do servidor.",
            "Sem auditoria, consultas, logins e alterações de permissões nos bancos não ficam registrados fora do próprio banco.",
            "auditingSettings do servidor com state = Enabled.",
            "Auditoria configurada só nos bancos, e não no servidor, não é considerada.",
            "Um acesso indevido aos dados não deixa registro de quem consultou ou alterou o quê.",
            Docs(DocSqlAudit), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-044", KnightSecurityDomain.Network,
            "Servidores SQL aceitam acesso pela rede pública.",
            "Com acesso público, o endpoint do servidor é alcançável da internet, protegido só pelo firewall e pela autenticação.",
            "publicNetworkAccess = Disabled.",
            null,
            "Credenciais do banco vazadas podem ser usadas da internet.",
            Docs(DocSqlConnectivity), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-045", KnightSecurityDomain.Network,
            "O firewall do SQL tem regra que libera toda a internet.",
            "A faixa 0.0.0.0–255.255.255.255 anula o firewall: qualquer endereço chega ao servidor.",
            "Nenhuma regra de firewall cobrindo toda a internet.",
            null,
            "O banco fica exposto a varreduras e força bruta de qualquer lugar.",
            Docs(DocSqlFirewall), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-046", KnightSecurityDomain.Network,
            "O firewall do SQL libera todos os serviços do Azure.",
            "A opção “Permitir serviços do Azure” (regra 0.0.0.0) aceita conexões de qualquer recurso hospedado no Azure — inclusive de outros clientes.",
            "Nenhuma regra 0.0.0.0–0.0.0.0; acesso por endpoint privado ou regras de rede virtual.",
            null,
            "Um atacante com uma máquina no Azure alcança o servidor como se fosse um serviço autorizado.",
            Docs(DocSqlFirewall), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-047", KnightSecurityDomain.DataProtection,
            "O protetor do TDE dos servidores SQL é gerenciado pelo serviço.",
            "Com o protetor no Key Vault, a organização controla a revogação da chave que protege os bancos.",
            "encryptionProtector.serverKeyType = AzureKeyVault.",
            null,
            "A organização não consegue revogar sozinha o acesso aos dados dos bancos do servidor.",
            Docs(DocSqlByok), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-048", KnightSecurityDomain.DataProtection,
            "O protetor do TDE das instâncias gerenciadas é gerenciado pelo serviço.",
            "O mesmo controle de revogação da chave, nas instâncias gerenciadas de SQL.",
            "encryptionProtector.serverKeyType = AzureKeyVault na instância.",
            null,
            "A organização não consegue revogar sozinha o acesso aos bancos da instância gerenciada.",
            Docs(DocSqlByok), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-049", KnightSecurityDomain.Identity,
            "Servidores SQL não têm administrador do Microsoft Entra ID.",
            "Sem administrador do Entra ID, só logins SQL com senha existem: sem MFA, sem acesso condicional e sem desligamento automático.",
            "Administrador do Entra ID configurado (de preferência com autenticação somente pelo Entra ID).",
            "A autenticação somente pelo Entra ID aparece como evidência; o critério exige o administrador.",
            "Logins SQL com senha compartilhada continuam funcionando depois da saída de quem os conhecia.",
            Docs(DocSqlEntra), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-050", KnightSecurityDomain.DataProtection,
            "Bancos SQL estão sem criptografia transparente de dados.",
            "Sem TDE, arquivos de dados, logs e backups ficam legíveis para quem obtiver uma cópia.",
            "transparentDataEncryption.state = Enabled em cada banco.",
            "O banco master é excluído.",
            "Uma cópia de backup do banco pode ser restaurada e lida fora da organização.",
            Docs(DocSqlTde), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-051", KnightSecurityDomain.DataProtection,
            "Bancos de instâncias gerenciadas estão sem criptografia transparente de dados.",
            "O mesmo risco da ausência de TDE, nos bancos das instâncias gerenciadas.",
            "transparentDataEncryption.state = Enabled em cada banco da instância.",
            null,
            "Backups dos bancos da instância podem ser lidos se copiados.",
            Docs(DocSqlTde), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-052", KnightSecurityDomain.Logging,
            "A auditoria do SQL é retida por 90 dias ou menos.",
            "Investigações costumam alcançar meses atrás; uma retenção curta apaga o período em que o acesso indevido começou.",
            "retentionDays = 0 (sem limite) ou acima de 90.",
            "A retenção vale para o destino de armazenamento; num workspace do Log Analytics, vale a retenção do workspace, que não é lida.",
            "A trilha de consultas de um incidente descoberto tardiamente já não existe.",
            Docs(DocSqlAudit), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-053", KnightSecurityDomain.Network,
            "Servidores SQL aceitam TLS abaixo de 1.2.",
            "TLS 1.0 e 1.1 permitem negociações fracas com o servidor.",
            "minimalTlsVersion = 1.2 ou 1.3.",
            null,
            "Conexões de clientes antigos ao SQL podem ser interceptadas.",
            Docs(DocSqlConnectivity), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-054", KnightSecurityDomain.Network,
            "Instâncias gerenciadas de SQL aceitam TLS abaixo de 1.2.",
            "O mesmo risco do protocolo fraco, nas instâncias gerenciadas.",
            "minimalTlsVersion = 1.2 ou 1.3 na instância.",
            null,
            "Conexões às instâncias gerenciadas podem ser rebaixadas e interceptadas.",
            Docs(DocSqlMiTls), KnightCapability.AzureDatabases);
        P("AK-AZ-DB-055", KnightSecurityDomain.Identity,
            "Serviços vinculados do Data Factory guardam credencial fora do Key Vault.",
            "Segredos guardados na própria fábrica não têm rotação, auditoria de acesso e revogação centralizadas como no Key Vault.",
            "Credenciais por referência AzureKeyVaultSecret ou identidade gerenciada; nenhum SecureString, encryptedCredential ou credencial em texto.",
            "O AEGIS grava só a origem de cada credencial, nunca o valor. Credencial em cadeia de conexão é reconhecida pelos marcadores de senha ou chave no texto.",
            "Quem altera ou exporta a fábrica leva junto credenciais de bancos e serviços que ela acessa.",
            Docs(DocAdfKv), KnightCapability.AzureDatabases);

        // ======== Databricks =================================================================================
        P("AK-AZ-DBR-001", KnightSecurityDomain.Network,
            "Workspaces do Databricks usam rede virtual gerenciada pelo Databricks, não pela organização.",
            "Na rede gerenciada pelo serviço, a organização não aplica seus NSGs, rotas, firewall nem endpoints privados ao tráfego dos clusters.",
            "Injeção em rede virtual da organização (customVirtualNetworkId).",
            "A rede só pode ser escolhida na criação do workspace.",
            "O tráfego dos clusters, inclusive para a internet, fica fora dos controles de rede da organização.",
            Docs(DocDbrVnet), KnightCapability.AzureDatabricks);
        P("AK-AZ-DBR-002", KnightSecurityDomain.Network,
            "Sub-redes usadas pelo Databricks estão sem NSG.",
            "Sem NSG nas sub-redes do workspace, os nós dos clusters não têm filtragem de tráfego da organização.",
            "NSG associado às sub-redes pública e privada do workspace.",
            "Exige a rede virtual do workspace numa assinatura lida.",
            "Nós de cluster podem receber e iniciar conexões sem filtragem.",
            Docs(DocDbrVnet, DocNsg), KnightCapability.AzureDatabricks, KnightCapability.AzureNetworking);
        P("AK-AZ-DBR-003", KnightSecurityDomain.DataProtection,
            "Workspaces do Databricks usam só chaves gerenciadas pela Microsoft.",
            "Com chave do cliente, a organização controla a revogação da chave que protege notebooks, consultas e o DBFS.",
            "Chave do cliente nos serviços gerenciados ou no DBFS.",
            "Quais dados são críticos é decisão organizacional.",
            "A organização não consegue revogar sozinha o acesso a notebooks e dados do workspace.",
            Docs(DocDbrCmk), KnightCapability.AzureDatabricks);
        P("AK-AZ-DBR-004", KnightSecurityDomain.Network,
            "Clusters do Databricks recebem IP público.",
            "Com IP público, os nós dos clusters são alcançáveis da internet; a conectividade segura de cluster elimina essa exposição.",
            "enableNoPublicIp = true.",
            null,
            "Nós dos clusters ficam expostos a varreduras e ataques diretos da internet.",
            Docs(DocDbrScc), KnightCapability.AzureDatabricks);
        P("AK-AZ-DBR-005", KnightSecurityDomain.Network,
            "Workspaces do Databricks aceitam acesso pela rede pública.",
            "Com acesso público, a interface e as APIs do workspace são alcançáveis da internet.",
            "publicNetworkAccess = Disabled (Private Link de front-end).",
            null,
            "Credenciais ou tokens pessoais vazados podem ser usados da internet contra o workspace.",
            Docs(DocDbrFrontEnd), KnightCapability.AzureDatabricks);
        P("AK-AZ-DBR-006", KnightSecurityDomain.Network,
            "Workspaces do Databricks sem endpoint privado aprovado.",
            "Sem endpoint privado, o acesso ao workspace e entre o plano de controle e os clusters depende de rotas públicas.",
            "Ao menos uma conexão de endpoint privado aprovada.",
            null,
            "O workspace continua dependendo de exposição pública para funcionar.",
            Docs(DocDbrPl), KnightCapability.AzureDatabricks);
    }

    // ---- Famílias parametrizadas ---------------------------------------------------------------------------
    private static void Plan(string id, string label, KnightControlReference doc, string rationale, string impact) =>
        P(id, KnightSecurityDomain.CloudProtection,
            $"O plano {label} do Microsoft Defender para Nuvem está desligado em assinaturas do escopo.",
            rationale,
            $"Plano {label} ligado (camada Standard) em cada assinatura que tem o tipo de recurso protegido.",
            "Avalia o plano por assinatura; uma assinatura sem o tipo de recurso protegido também aparece com o plano desligado.",
            impact, Docs(doc, DocPricingApi), KnightCapability.AzureDefenderForCloud);

    private static void Alert(string id, string what, string rationale, string impact) =>
        P(id, KnightSecurityDomain.Logging,
            $"Assinaturas não têm alerta do log de atividades para {what}.",
            rationale,
            $"Alerta do log de atividades habilitado, com escopo na assinatura, para {what}, com grupo de ações.",
            "Verifica a existência do alerta habilitado no escopo da assinatura; não confere quem recebe a notificação nem se ela é tratada.",
            impact, Docs(DocActivityAlert), KnightCapability.AzureMonitor);

    private static void Port(string id, string service, string port, string rationale, string impact) =>
        P(id, KnightSecurityDomain.Network,
            $"Grupos de segurança de rede permitem {service} ({port}) de qualquer origem da internet.",
            rationale,
            $"Nenhuma regra de entrada permitindo {port} de *, Internet, Any ou 0.0.0.0/0; acesso administrativo pelo Azure Bastion ou por VPN.",
            "Avalia as regras do NSG; não verifica se há um serviço escutando na porta nem se outro NSG (sub-rede × interface) bloqueia o tráfego.",
            impact, Docs(DocNsg), KnightCapability.AzureNetworking);

    private static void Runtime(string id, string language, string rationale, string impact) =>
        P(id, KnightSecurityDomain.Compute,
            $"Aplicativos do App Service usam versão do {language} marcada como obsoleta ou já fora do suporte.",
            rationale,
            $"Versão do {language} presente nas pilhas de runtime publicadas pela Microsoft, sem marca de obsoleta e dentro da data de suporte.",
            "Compara com as pilhas publicadas pela própria Microsoft na coleta; uma versão que não aparece nelas (contêiner personalizado, valor livre) fica como não avaliada.",
            impact, Docs(DocLangPolicy, DocStacksApi), KnightCapability.AzureAppService);

    private static void DbParam(string id, string engine, string param, KnightControlReference doc, string rationale, string impact) =>
        P(id, param.Contains("tls", StringComparison.OrdinalIgnoreCase) || param.Contains("transport", StringComparison.OrdinalIgnoreCase)
                ? KnightSecurityDomain.Network : KnightSecurityDomain.Logging,
            $"O parâmetro de servidor {param} do {engine} não está no valor recomendado.",
            rationale,
            $"Parâmetro {param} no valor recomendado em cada servidor {engine}.",
            "Lê o parâmetro pelo Resource Manager; um parâmetro não lido deixa o servidor como não avaliado.",
            impact, Docs(doc), KnightCapability.AzureDatabases);

    public static IReadOnlyList<KnightControlProfile> All => Built;

    public static IReadOnlyDictionary<string, string> Impacts => BuiltImpacts;
}
