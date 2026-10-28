using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// Capacidade lógica de coleta de uma fonte (um grupo de consultas com uma permissão associada). O estado por
/// capacidade explicita o que foi obtido e o que faltou — base da cobertura e das "limitações" na UI e na IA.
/// </summary>
public enum KnightCapability
{
    /// <summary>Inventário de papéis/atribuições privilegiadas.</summary>
    PrivilegedRoleInventory = 0,

    /// <summary>Cobertura/registro de MFA dos usuários.</summary>
    MfaRegistration = 1,

    /// <summary>Contas de convidado e sua atividade.</summary>
    GuestAccounts = 2,

    /// <summary>Políticas de acesso condicional / baseline.</summary>
    ConditionalAccessPolicies = 3,

    /// <summary>Inventário de aplicações: credenciais (segredos/certificados) vencendo.</summary>
    ApplicationInventory = 4,

    /// <summary>Isenções de MFA de contas de serviço e seus controles compensatórios.</summary>
    ServiceAccountExemptions = 5,

    /// <summary>Configuração de segurança padrão da plataforma.</summary>
    SecurityBaseline = 6,

    /// <summary>Designação de contas de emergência/break-glass.</summary>
    BreakGlassDesignation = 7,

    /// <summary>
    /// Permissões de APLICATIVO efetivamente CONCEDIDAS (service principals + appRoleAssignments). Separada
    /// do inventário de credenciais para que a falha de uma consulta não invalide fatos da outra.
    /// </summary>
    ApplicationPermissions = 8,

    /// <summary>Consentimentos DELEGADOS tenant-wide (oauth2PermissionGrants, consentType=AllPrincipals).</summary>
    ApplicationConsents = 9,

    // ---- Google Workspace (Admin SDK Directory + Reports, somente leitura) ----
    /// <summary>Diretório de usuários: 2SV, superadministradores e atividade (Admin SDK Directory).</summary>
    DirectoryUsers = 10,

    /// <summary>Grupos e seus membros externos (Admin SDK Directory).</summary>
    DirectoryGroups = 11,

    /// <summary>Auditoria de compartilhamento externo no Drive (Reports API).</summary>
    DriveSharingAudit = 12,

    /// <summary>Auditoria de autorizações OAuth de terceiros (Reports API).</summary>
    OAuthTokenAudit = 13,

    // ---- [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade (Microsoft Entra ID Protection) ----
    /// <summary>
    /// Inventário AGREGADO de usuários marcados em risco pelo provedor de identidade
    /// (<c>IdentityRiskyUser.Read.All</c>). INDEPENDENTE de <see cref="IdentityRiskDetections"/>: uma falha
    /// aqui não invalida a outra.
    /// </summary>
    IdentityRiskyUsers = 14,

    /// <summary>
    /// Detecções/eventos de risco recentes em janela determinística (<c>IdentityRiskEvent.Read.All</c>).
    /// Conceito DISTINTO de "usuário em risco" — um usuário pode ter várias detecções, e detecções resolvidas
    /// não tornam o usuário seguro.
    /// </summary>
    IdentityRiskDetections = 15,

    // ---- [AEGIS-KNIGHT-COVERAGE-01] Configuração do locatário (Microsoft Entra ID, somente leitura) ----------
    // Cada capacidade é INDEPENDENTE: a falha de uma (permissão, licença, indisponibilidade) não invalida as
    // outras, e os controles que dependem dela ficam não avaliados com o motivo específico.

    /// <summary>Política de autorização do diretório (<c>Policy.Read.All</c>).</summary>
    AuthorizationPolicy = 16,

    /// <summary>Fluxo de consentimento do administrador (<c>Policy.Read.All</c>).</summary>
    AdminConsentPolicy = 17,

    /// <summary>Política padrão de gerenciamento de aplicações (<c>Policy.Read.All</c>).</summary>
    AppManagementPolicy = 18,

    /// <summary>Política de métodos de autenticação (<c>Policy.Read.All</c>).</summary>
    AuthenticationMethodsPolicy = 19,

    /// <summary>Configurações de diretório por modelo — senhas e grupos (<c>Directory.Read.All</c>).</summary>
    DirectorySettings = 20,

    /// <summary>Domínios do diretório (<c>Directory.Read.All</c>).</summary>
    Domains = 21,

    /// <summary>Sincronização híbrida e de hash de senha (<c>Directory.Read.All</c>, <c>OnPremDirectorySynchronization.Read.All</c>).</summary>
    DirectorySynchronization = 22,

    /// <summary>Política de registro e ingresso de dispositivos (<c>Policy.Read.DeviceConfiguration</c>).</summary>
    DeviceRegistrationPolicy = 23,

    /// <summary>Visibilidade dos grupos do Microsoft 365 (<c>Directory.Read.All</c>).</summary>
    GroupVisibility = 24,

    /// <summary>Origem e licenças das contas privilegiadas (<c>Directory.Read.All</c>).</summary>
    PrivilegedAccountDetails = 25,

    /// <summary>Atribuições e regras de ativação do PIM (<c>RoleManagement.Read.Directory</c>, <c>RoleManagementPolicy.Read.Directory</c>).</summary>
    PrivilegedIdentityManagement = 26,

    /// <summary>Definições de revisão de acesso (<c>AccessReview.Read.All</c>).</summary>
    AccessReviews = 27,

    /// <summary>Locais nomeados do acesso condicional (<c>Policy.Read.All</c>).</summary>
    NamedLocations = 28,

    /// <summary>Estado de aplicações de serviço conhecidas do locatário (<c>Application.Read.All</c>).</summary>
    ServicePrincipalSettings = 29,

    // ---- [AEGIS-KNIGHT-COVERAGE-02] Configuração do Microsoft Teams (somente leitura) ----------------------
    // Coletadas pelo módulo oficial Teams PowerShell com autenticação de APLICATIVO (ver TeamsKnightCollector).
    // Cada capacidade é UM comando de leitura; a falha de uma não invalida as outras.

    /// <summary>Configuração do cliente do Teams (<c>Get-CsTeamsClientConfiguration</c>).</summary>
    TeamsClientConfiguration = 30,

    /// <summary>Configuração de federação do locatário (<c>Get-CsTenantFederationConfiguration</c>).</summary>
    TeamsFederationConfiguration = 31,

    /// <summary>Políticas de reunião, a padrão da organização e as personalizadas (<c>Get-CsTeamsMeetingPolicy</c>).</summary>
    TeamsMeetingPolicies = 32,

    /// <summary>Políticas de mensagens (<c>Get-CsTeamsMessagingPolicy</c>).</summary>
    TeamsMessagingPolicies = 33,

    /// <summary>Políticas de permissão de aplicativos (<c>Get-CsTeamsAppPermissionPolicy</c>).</summary>
    TeamsAppPermissionPolicies = 34,

    /// <summary>
    /// Atribuições de políticas do Teams a GRUPOS (<c>Get-CsGroupPolicyAssignment</c>). É o que permite dizer o
    /// ALCANCE de uma política personalizada sem enumerar usuário a usuário.
    /// </summary>
    TeamsPolicyAssignments = 35,

    // ---- [AEGIS-KNIGHT-COVERAGE-03] Configuração do Exchange Online (somente leitura) --------------------
    // Coletadas pelo módulo oficial Exchange Online PowerShell com autenticação de APLICATIVO por token de
    // acesso (ver ExchangeKnightCollector). Cada capacidade é UM comando de leitura; a falha de uma não
    // invalida as outras, e os controles que dependem dela ficam não avaliados COM O COMANDO NOMEADO.

    /// <summary>Configuração da organização (<c>Get-OrganizationConfig</c>).</summary>
    ExchangeOrganizationConfig = 36,

    /// <summary>Configuração de transporte da organização (<c>Get-TransportConfig</c>).</summary>
    ExchangeTransportConfig = 37,

    /// <summary>Políticas de compartilhamento (<c>Get-SharingPolicy</c>).</summary>
    ExchangeSharingPolicies = 38,

    /// <summary>Políticas de caixa de correio do Outlook na web (<c>Get-OwaMailboxPolicy</c>).</summary>
    ExchangeOwaMailboxPolicies = 39,

    /// <summary>Regras de transporte, ou regras de fluxo de emails (<c>Get-TransportRule</c>).</summary>
    ExchangeTransportRules = 40,

    /// <summary>Políticas de atribuição de função de usuário final (<c>Get-RoleAssignmentPolicy</c>).</summary>
    ExchangeRoleAssignmentPolicies = 41,

    /// <summary>Identificação de remetentes externos no Outlook (<c>Get-ExternalInOutlook</c>).</summary>
    ExchangeExternalSenderIdentification = 42,

    /// <summary>
    /// Políticas de filtro de spam de SAÍDA (<c>Get-HostedOutboundSpamFilterPolicy</c>). É onde vive o modo de
    /// encaminhamento automático — um dos mecanismos que o critério de encaminhamento exige ler. A leitura
    /// entra aqui porque o critério de Exchange depende dela; o catálogo do Microsoft Defender para Office 365
    /// continua fora deste bloco.
    /// </summary>
    ExchangeOutboundSpamFilterPolicies = 43,

    /// <summary>Enumeração das caixas de correio (<c>Get-Mailbox</c>) — compartilhadas, auditoria e encaminhamento.</summary>
    ExchangeMailboxes = 44,

    /// <summary>
    /// Enumeração das CONTAS (<c>Get-User</c>), para o estado de entrada. Separada de
    /// <see cref="ExchangeMailboxes"/> porque é outra leitura: sem ela, "caixa compartilhada" não vira
    /// "conta bloqueada" por suposição.
    /// </summary>
    ExchangeMailboxSignIn = 45,

    /// <summary>Configurações de acesso de cliente por caixa (<c>Get-CASMailbox</c>) — substituições de SMTP AUTH.</summary>
    ExchangeCasMailboxes = 46,

    /// <summary>Associações de DESVIO de auditoria (<c>Get-MailboxAuditBypassAssociation</c>).</summary>
    ExchangeAuditBypassAssociations = 47,

    // ---- [AEGIS-KNIGHT-COVERAGE-04] Microsoft Defender para Office 365 (sessão do Exchange Online) ----------
    // Os comandos de leitura das políticas de proteção executam na MESMA sessão de aplicativo do Exchange Online:
    // nenhuma permissão nova. Cada capacidade é um grupo de leituras da mesma política (política + regra que a aplica).

    /// <summary>Configuração global do Defender para Office 365 (<c>Get-AtpPolicyForO365</c>).</summary>
    DefenderAtpPolicy = 48,

    /// <summary>Links Seguros: políticas e regras (<c>Get-SafeLinksPolicy</c>, <c>Get-SafeLinksRule</c>).</summary>
    DefenderSafeLinks = 49,

    /// <summary>Anexos Seguros: políticas e regras (<c>Get-SafeAttachmentPolicy</c>, <c>Get-SafeAttachmentRule</c>).</summary>
    DefenderSafeAttachments = 50,

    /// <summary>Antimalware: políticas e regras (<c>Get-MalwareFilterPolicy</c>, <c>Get-MalwareFilterRule</c>).</summary>
    DefenderMalwareFilter = 51,

    /// <summary>Antispam de entrada: políticas e regras (<c>Get-HostedContentFilterPolicy</c>, <c>Get-HostedContentFilterRule</c>).</summary>
    DefenderInboundSpam = 52,

    /// <summary>Filtro de conexão (<c>Get-HostedConnectionFilterPolicy</c>).</summary>
    DefenderConnectionFilter = 53,

    /// <summary>Antispam de saída: políticas e regras (<c>Get-HostedOutboundSpamFilterPolicy</c>, <c>Get-HostedOutboundSpamFilterRule</c>).</summary>
    DefenderOutboundSpam = 54,

    /// <summary>Antiphishing: políticas e regras (<c>Get-AntiPhishPolicy</c>, <c>Get-AntiPhishRule</c>).</summary>
    DefenderAntiPhish = 55,

    /// <summary>Assinatura DKIM por domínio (<c>Get-DkimSigningConfig</c>).</summary>
    DefenderDkim = 56,

    /// <summary>Domínios aceitos da organização (<c>Get-AcceptedDomain</c>).</summary>
    DefenderAcceptedDomains = 57,

    /// <summary>Registros DNS públicos SPF e DMARC dos domínios aceitos (consulta DNS, fora do Microsoft 365).</summary>
    DefenderDnsRecords = 58,

    /// <summary>Proteção do Teams — ZAP (<c>Get-TeamsProtectionPolicy</c>).</summary>
    DefenderTeamsProtection = 59,

    /// <summary>Contas prioritárias: proteção habilitada e contas marcadas (<c>Get-EmailTenantSettings</c>, <c>Get-User -IsVIP</c>).</summary>
    DefenderPriorityAccounts = 60,

    /// <summary>Políticas de segurança predefinidas (<c>Get-EOPProtectionPolicyRule</c>, <c>Get-ATPProtectionPolicyRule</c>).</summary>
    DefenderPresetPolicies = 61,

    // ---- [AEGIS-KNIGHT-COVERAGE-04] Microsoft Purview ------------------------------------------------------

    /// <summary>Ingestão do log de auditoria unificado (<c>Get-AdminAuditLogConfig</c>, sessão do Exchange Online).</summary>
    PurviewAuditConfig = 62,

    /// <summary>Políticas de DLP (<c>Get-DlpCompliancePolicy</c>, sessão do Security &amp; Compliance).</summary>
    PurviewDlpPolicies = 63,

    /// <summary>Políticas de rótulos de confidencialidade (<c>Get-LabelPolicy</c>, sessão do Security &amp; Compliance).</summary>
    PurviewLabelPolicies = 64,

    // ---- [AEGIS-KNIGHT-COVERAGE-04] SharePoint e OneDrive -----------------------------------------------

    /// <summary>Configurações do locatário pelo Microsoft Graph (<c>GET /admin/sharepoint/settings</c>).</summary>
    SharePointTenantSettings = 65,

    /// <summary>
    /// Configurações do locatário pela API de administração do SharePoint (<c>SPO.Tenant</c>). [AEGIS-KNIGHT-READONLY-01] Não
    /// é mais lida: a única permissão de aplicativo que a abre (<c>Sites.FullControl.All</c>) também concede escrita. A
    /// capacidade continua registrada — como não tentada, com o motivo — para que os controles fiquem não avaliados.
    /// </summary>
    SharePointAdminTenant = 66,

    // ---- [AEGIS-KNIGHT-COVERAGE-04] Microsoft Intune ---------------------------------------------------

    /// <summary>Configurações do serviço de conformidade (<c>GET /deviceManagement/settings</c>).</summary>
    IntuneServiceSettings = 67,

    /// <summary>Restrições de registro de dispositivos (<c>GET /deviceManagement/deviceEnrollmentConfigurations</c>).</summary>
    IntuneEnrollmentRestrictions = 68,

    // ---- [AEGIS-KNIGHT-COVERAGE-04] Microsoft Fabric ---------------------------------------------------

    /// <summary>Configurações do locatário do Fabric (<c>GET /v1/admin/tenantsettings</c>).</summary>
    FabricTenantSettings = 69,

    // ---- [AEGIS-KNIGHT-COVERAGE-04] Azure Resource Manager (somente leitura) -----------------------------
    // Cada capacidade é uma FAMÍLIA de leituras sobre todas as assinaturas do escopo. A falha numa assinatura não
    // invalida as outras: o que foi lido continua demonstrando violação, mas a aprovação exige o escopo inteiro.

    /// <summary>Descoberta das assinaturas no escopo (<c>GET /subscriptions</c>).</summary>
    AzureSubscriptions = 70,

    /// <summary>Autorização: atribuições e definições de papel e bloqueios de recurso.</summary>
    AzureAuthorization = 71,

    /// <summary>Atribuições de política (inclusive a iniciativa padrão do Defender para Nuvem).</summary>
    AzurePolicy = 72,

    /// <summary>Microsoft Defender para Nuvem: planos, contatos, configurações e integrações.</summary>
    AzureDefenderForCloud = 73,

    /// <summary>Azure Monitor: configurações de diagnóstico da assinatura, alertas do log de atividades e Application Insights.</summary>
    AzureMonitor = 74,

    /// <summary>Rede: NSGs, redes virtuais, Network Watcher, logs de fluxo, IPs públicos, gateways e perímetros.</summary>
    AzureNetworking = 75,

    /// <summary>Contas de armazenamento e seus serviços (blob e arquivos).</summary>
    AzureStorage = 76,

    /// <summary>Key Vaults, chaves e segredos (metadados pelo Resource Manager — nenhum valor é lido).</summary>
    AzureKeyVault = 77,

    /// <summary>Políticas de certificado dos Key Vaults (plano de dados — exige papel próprio no cofre).</summary>
    AzureKeyVaultCertificates = 78,

    /// <summary>Computação: máquinas virtuais, discos, extensões, Container Instances e Batch.</summary>
    AzureCompute = 79,

    /// <summary>App Service e Functions: aplicativos, slots, configurações, planos e ambientes (ASE).</summary>
    AzureAppService = 80,

    /// <summary>Bancos de dados: SQL, instâncias gerenciadas, PostgreSQL, MySQL, Cosmos DB, Redis e Data Factory.</summary>
    AzureDatabases = 81,

    /// <summary>Workspaces do Azure Databricks (configuração pelo Resource Manager).</summary>
    AzureDatabricks = 82,

    /// <summary>
    /// [AEGIS-KNIGHT-CLOSURE-01] Configurações de diagnóstico dos RECURSOS (<c>{recurso}/providers/Microsoft.Insights/diagnosticSettings</c>).
    /// A listagem só existe em versão PREVIEW da API do Azure Monitor (2021-05-01-preview): a capacidade é separada das
    /// famílias estáveis para que indisponibilidade ou mudança de contrato da versão preview não derrube mais nada.
    /// </summary>
    AzureResourceDiagnostics = 83,

    /// <summary>Configurações de diagnóstico do Microsoft Entra ID (escopo do locatário, <c>microsoft.aadiam</c>).</summary>
    AzureTenantDiagnostics = 84,

    // ---- [AEGIS-KNIGHT-CLOSURE-01] Leituras em versão PREVIEW, acesso adicional e complementos ------------------
    // Cada leitura em versão preview (Microsoft Graph beta, Azure Resource Manager *-preview) é uma capacidade PRÓPRIA:
    // indisponibilidade, recusa ou resposta fora do contrato documentado vira limitação dessa capacidade — nunca aprovação.

    /// <summary>Exportação do log de atividades: configurações de diagnóstico da ASSINATURA (Azure Monitor, 2021-05-01-preview).</summary>
    AzureActivityLogExport = 85,

    /// <summary>Contatos de segurança e notificações do Defender para Nuvem (<c>Microsoft.Security/securityContacts</c>, 2023-12-01-preview).</summary>
    AzureSecurityContacts = 86,

    /// <summary>
    /// Referências ao Key Vault nas configurações dos aplicativos do App Service (<c>config/configreferences/appsettings</c>,
    /// versão estável). Devolve só a referência e o estado de resolução — nunca o valor de uma configuração.
    /// </summary>
    AzureAppSettingsKeyVaultReferences = 87,

    /// <summary>
    /// API REST do PRÓPRIO workspace do Azure Databricks (tokens pessoais, SCIM, clusters). [AEGIS-KNIGHT-READONLY-01] Não é
    /// mais lida: não há identidade somente leitura documentada para ela. A capacidade continua registrada — como não
    /// tentada, com o motivo, nas assinaturas com workspace — para que os controles fiquem não avaliados.
    /// </summary>
    AzureDatabricksWorkspaceApi = 88,

    /// <summary>Estado da MFA por usuário (legado) — <c>/users/{id}/authentication/requirements</c>, Microsoft Graph beta.</summary>
    PerUserMfaStates = 89,

    /// <summary>
    /// Campos da política de métodos de autenticação que só a versão beta do Microsoft Graph expõe (MFA preferencial do
    /// sistema e Authenticator em aplicativos complementares).
    /// </summary>
    AuthenticationMethodsPolicyPreview = 90,

    /// <summary>Aplicativos e serviços próprios dos usuários (<c>/admin/appsAndServices</c>, Microsoft Graph beta).</summary>
    M365AppsAndServicesSettings = 91,

    /// <summary>Configurações do Microsoft Forms (<c>/admin/forms</c>, Microsoft Graph beta).</summary>
    M365FormsSettings = 92,

    /// <summary>Tempo limite de sessão ociosa do Microsoft 365 (<c>/policies/activityBasedTimeoutPolicies</c>, versão estável).</summary>
    ActivityBasedTimeoutPolicy = 93,

    /// <summary>Destino das mensagens denunciadas pelos usuários (<c>Get-ReportSubmissionPolicy</c>, sessão do Exchange Online).</summary>
    DefenderReportSubmissionPolicy = 94,

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Pertencimento TRANSITIVO dos grupos referenciados pelas políticas de acesso condicional
    /// habilitadas (inclusão e exclusão) e dos grupos atribuídos a papéis privilegiados (<c>/groups/{id}/transitiveMembers</c>,
    /// Directory.Read.All). Cada grupo guarda o próprio desfecho: um grupo recusado não apaga os outros.
    /// </summary>
    EntraGroupMembership = 95,
}

/// <summary>
/// Desfecho da coleta de UMA capacidade. Os desfechos de FALHA são distintos (não colapsam em "Unavailable"):
/// a agregação em <see cref="KnightSourceState"/> depende disso para preservar Throttled/AuthenticationFailure/Error.
/// </summary>
public enum KnightCapabilityOutcome
{
    Collected = 0,
    InsufficientPermission = 1,
    Unavailable = 2,
    NotAttempted = 3,
    Throttled = 4,
    AuthenticationFailure = 5,
    Error = 6,

    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] A permissão existe, mas a LICENÇA do tenant não habilita a
    /// capacidade (ou entrega dados limitados). É honestamente distinto de <see cref="InsufficientPermission"/>
    /// (falta consentimento) e de <see cref="Unavailable"/> (a fonte falhou): aqui a fonte respondeu que o
    /// recurso não está licenciado. NUNCA vira coleção vazia nem "conforme".
    /// </summary>
    LimitedByLicense = 7,

    /// <summary>
    /// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] A leitura oficial NÃO está disponível no ambiente em que o AEGIS executa
    /// (a Microsoft documenta que Security &amp; Compliance PowerShell não existe no PowerShell 7 em Linux, por exemplo).
    /// Não é falha da fonte, nem recusa de autorização, nem licença: nenhuma concessão resolve. A capacidade fica
    /// NÃO coletada — os controles que dependem dela ficam não avaliados, nunca aprovados.
    /// </summary>
    PlatformUnsupported = 8,
}

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Natureza TÉCNICA da falha de uma leitura, separada do desfecho exibido. O
/// desfecho diz ao avaliador o que aconteceu com o dado; o tipo diz ao operador onde procurar. Nenhum deles carrega texto
/// da fonte.
/// </summary>
public enum KnightFailureKind
{
    /// <summary>A fonte respondeu com um status HTTP de erro (401, 403, 404, 429, 5xx…).</summary>
    HttpStatus = 0,

    /// <summary>A leitura não terminou no tempo previsto (tempo da tentativa, tempo total ou tempo do processo).</summary>
    Timeout = 1,

    /// <summary>O circuito do cliente HTTP estava aberto depois de falhas recentes: a leitura nem foi enviada.</summary>
    CircuitOpen = 2,

    /// <summary>Falha de rede ou de transporte (DNS, conexão, TLS) antes de haver resposta.</summary>
    Transport = 3,

    /// <summary>A resposta chegou, mas não segue o contrato documentado da versão lida.</summary>
    Contract = 4,

    /// <summary>Defeito interno do AEGIS ao processar a leitura — não é atribuído ao provedor, à rede nem à permissão.</summary>
    Internal = 5,

    /// <summary>O processo do adaptador terminou com código diferente de zero.</summary>
    ProcessExit = 6,

    /// <summary>O processo do adaptador terminou sem emitir o documento de resultado.</summary>
    ProcessNoResult = 7,

    /// <summary>A saída do processo do adaptador ultrapassou o limite aceito e o processo foi encerrado.</summary>
    OutputLimit = 8,

    /// <summary>O runtime do adaptador não pôde ser iniciado neste ambiente.</summary>
    RuntimeUnavailable = 9,

    /// <summary>A leitura oficial não está disponível nesta plataforma de execução.</summary>
    PlatformUnsupported = 10,

    /// <summary>A sessão do serviço não foi estabelecida (o motivo é a categoria declarada pelo adaptador).</summary>
    Connection = 11,

    /// <summary>O comando de leitura falhou dentro de uma sessão estabelecida.</summary>
    Command = 12,

    /// <summary>A leitura parou antes do fim (teto, orçamento de tempo, interrupção) — o que foi lido é parcial.</summary>
    Incomplete = 13,

    /// <summary>Não havia o que consultar (ex.: nenhuma assinatura no escopo) — não é falha nem ausência comprovada.</summary>
    NoScope = 14,
}

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Diagnóstico TÉCNICO e seguro de uma capacidade: onde parou, quanto durou, o
/// tipo sanitizado da falha, o status/código quando existem e o identificador de correlação da execução. Nunca carrega
/// token, segredo, certificado, corpo de resposta nem mensagem livre da fonte — só valores de vocabulário fechado ou já
/// restritos a um conjunto fixo de caracteres.
/// </summary>
public sealed record KnightCapabilityDiagnostic(
    string Stage,
    KnightFailureKind Kind,
    long? DurationMs = null,
    int? HttpStatus = null,
    string? Code = null,
    string? Endpoint = null,
    string? CorrelationId = null,
    string? ServiceRequestId = null,
    // [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] correlation_id do endpoint de token (GUID). Omitido quando nulo, para que o JSON
    // dos diagnósticos já gravados continue idêntico.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServiceCorrelationId = null);

/// <summary>
/// Estado por capacidade — o que a fonte conseguiu (ou não) coletar, com detalhe sanitizado.
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] <paramref name="Diagnostic"/> é opcional e só técnico: coletas anteriores
/// a este pacote não o têm, e a avaliação nunca depende dele.
/// </summary>
public sealed record KnightCapabilityStatus(
    KnightCapability Capability, KnightCapabilityOutcome Outcome, string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KnightCapabilityDiagnostic? Diagnostic = null);

// ---- Configuração de fonte (tipada por fonte; sem dicionário de strings) --------------------------------

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Credenciais mínimas de client credentials para o Microsoft Graph (tenant + app id +
/// segredo). Extraída para que o transporte VALIDADO do Graph (<c>EntraGraphClient</c>) seja reutilizável por
/// mais de um coletor Microsoft (KNIGHT/Entra e Secure Score) SEM uma segunda implementação ingênua de OAuth,
/// paginação ou validação de destino — e SEM acoplar o transporte a um tipo específico de coletor. As bases de
/// login/Graph continuam CONSTANTES oficiais no cliente HTTP; o tenant nunca fornece URL de destino.
/// </summary>
public interface IMicrosoftGraphCredentials
{
    string AzureTenantId { get; }
    string ClientId { get; }
    string ClientSecret { get; }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Certificado da aplicação, quando o conector o guarda. Com ele o token é pedido por
    /// ASSERÇÃO DE CLIENTE assinada — a forma que a Microsoft documenta para a autenticação de aplicativo do Exchange
    /// Online, do Security &amp; Compliance e da administração do SharePoint. Sem ele, o segredo continua valendo.
    /// </summary>
    MicrosoftClientCertificate? ClientCertificate => null;
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Certificado de aplicação (PFX com a chave privada, em base64, e a senha do arquivo),
/// DECIFRADO em memória a partir do conector. Nunca é gravado nem registrado: o <c>ToString</c> não imprime nada dele.
/// </summary>
public sealed record MicrosoftClientCertificate(string PfxBase64, string? Password)
{
    public override string ToString() => "MicrosoftClientCertificate { PfxBase64 = ***, Password = *** }";
}

/// <summary>Configuração RESOLVIDA de uma fonte para um tenant. Subtipos tipados por fonte; nunca um dict solto.</summary>
public abstract record KnightSourceConfiguration
{
    public abstract KnightSourceType Source { get; }
    public bool IsConfigured => this is not KnightSourceNotConfigured;
}

/// <summary>Não há configuração aplicável para a fonte neste tenant.</summary>
public sealed record KnightSourceNotConfigured(KnightSourceType SourceType) : KnightSourceConfiguration
{
    public override KnightSourceType Source => SourceType;
}

/// <summary>Fonte de demonstração (sem credenciais — dados sintéticos).</summary>
public sealed record KnightDemoConfiguration : KnightSourceConfiguration
{
    public override KnightSourceType Source => KnightSourceType.Demo;
}

/// <summary>
/// Configuração do coletor real do Microsoft Entra ID — client credentials por tenant (segredo DECIFRADO em
/// memória, nunca persistido/logado aqui). As bases de login/Graph são CONSTANTES oficiais no cliente HTTP —
/// o tenant NUNCA fornece URL de destino (evita exfiltrar o bearer token para uma origem arbitrária).
/// </summary>
public sealed record KnightEntraIdConfiguration(
    string AzureTenantId,
    string ClientId,
    string ClientSecret,
    MicrosoftClientCertificate? ClientCertificate = null) : KnightSourceConfiguration, IMicrosoftGraphCredentials
{
    public override KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

    // Um record gera ToString()/PrintMembers() que imprimem TODAS as propriedades — inclusive o ClientSecret.
    // Sobrescrevemos para o segredo NUNCA aparecer num dump/log acidental do objeto. (Gap: ToString de record.)
    public override string ToString() =>
        $"KnightEntraIdConfiguration {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = ***, ClientCertificate = {(ClientCertificate is null ? "não" : "***")} }}";
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Configuração do coletor real do Microsoft Teams — as MESMAS client credentials do
/// conector Microsoft já configurado (nenhuma credencial nova é pedida ao cliente). O transporte não é o
/// Microsoft Graph: é o módulo oficial Teams PowerShell, que a documentação autoriza a autenticar como
/// APLICATIVO por tokens de acesso. Dois tokens são necessários e são de RECURSOS DIFERENTES — o do Graph NÃO é
/// reaproveitado no recurso do Teams. As autoridades de login e os identificadores de recurso são CONSTANTES
/// oficiais no adaptador; o locatário nunca fornece destino.
/// </summary>
public sealed record KnightTeamsConfiguration(
    string AzureTenantId,
    string ClientId,
    string ClientSecret,
    MicrosoftClientCertificate? ClientCertificate = null) : KnightSourceConfiguration, IMicrosoftGraphCredentials
{
    public override KnightSourceType Source => KnightSourceType.MicrosoftTeams;

    // Um record imprime TODAS as propriedades no ToString() — inclusive o segredo. Sobrescrito pelo mesmo motivo
    // de KnightEntraIdConfiguration: o segredo nunca pode aparecer num dump/log acidental.
    public override string ToString() =>
        $"KnightTeamsConfiguration {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = ***, ClientCertificate = {(ClientCertificate is null ? "não" : "***")} }}";
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-03] Configuração do coletor real do Exchange Online — as MESMAS client credentials do
/// conector Microsoft já configurado (nenhuma credencial nova, nenhum certificado a emitir). O transporte é o
/// módulo oficial Exchange Online PowerShell, autenticando como APLICATIVO por TOKEN DE ACESSO.
///
/// <para><b>O token é de OUTRO recurso.</b> O Exchange Online PowerShell exige um token emitido para
/// <c>https://outlook.office365.com</c>; o token do Microsoft Graph e o da administração do Teams NÃO valem
/// aqui e não são reaproveitados. A autoridade de login e o identificador de recurso são CONSTANTES oficiais
/// no adaptador — o locatário nunca fornece um destino.</para>
///
/// <para><b>O domínio inicial não é opcional.</b> A conexão de aplicativo exige o parâmetro de organização, e o
/// valor documentado é o domínio <c>.onmicrosoft.com</c> principal — não o identificador do locatário. Ele é
/// resolvido na PRÓPRIA aquisição (ver <c>ExchangeKnightCollector</c>), nunca herdado de uma coleta anterior.</para>
/// </summary>
public sealed record KnightExchangeOnlineConfiguration(
    string AzureTenantId,
    string ClientId,
    string ClientSecret,
    MicrosoftClientCertificate? ClientCertificate = null) : KnightSourceConfiguration, IMicrosoftGraphCredentials
{
    public override KnightSourceType Source => KnightSourceType.MicrosoftExchangeOnline;

    // Mesmo motivo de KnightEntraIdConfiguration: o ToString() de um record imprimiria o segredo.
    public override string ToString() =>
        $"KnightExchangeOnlineConfiguration {{ AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = ***, ClientCertificate = {(ClientCertificate is null ? "não" : "***")} }}";
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Configuração das demais fontes Microsoft (Defender para Office 365, Purview, SharePoint,
/// Intune, Fabric e Azure): as MESMAS credenciais do conector Microsoft já configurado — nenhuma aplicação nova. O que
/// muda por fonte é o recurso do token, a permissão e o papel exigidos, e cada coletor os declara.
/// <para><paramref name="AzureSubscriptionIds"/> é o ESCOPO explícito do Azure: vazio significa "todas as assinaturas
/// que a aplicação enxerga", e o relatório diz qual foi o escopo avaliado.</para>
/// <para>[AEGIS-KNIGHT-READONLY-01] Não há opção que habilite leitura com privilégio de administração ou escrita (API de
/// administração do SharePoint, API dos workspaces do Databricks): essas capacidades ficam não tentadas, com o motivo.</para>
/// </summary>
public sealed record KnightMicrosoftServiceConfiguration(
    KnightSourceType SourceType,
    string AzureTenantId,
    string ClientId,
    string ClientSecret,
    MicrosoftClientCertificate? ClientCertificate = null,
    IReadOnlyList<string>? AzureSubscriptionIds = null) : KnightSourceConfiguration, IMicrosoftGraphCredentials
{
    public override KnightSourceType Source => SourceType;

    /// <summary>Assinaturas pedidas explicitamente (vazio = todas as visíveis à aplicação).</summary>
    public IReadOnlyList<string> SubscriptionScope => AzureSubscriptionIds ?? Array.Empty<string>();

    public override string ToString() =>
        $"KnightMicrosoftServiceConfiguration {{ Source = {SourceType}, AzureTenantId = {AzureTenantId}, ClientId = {ClientId}, ClientSecret = ***, ClientCertificate = {(ClientCertificate is null ? "não" : "***")} }}";
}

/// <summary>
/// Configuração do coletor real do Google Workspace — service account com DOMAIN-WIDE DELEGATION (o JSON da
/// service account contém a CHAVE PRIVADA; recebido DECIFRADO em memória, NUNCA persistido/logado aqui). Os
/// endpoints do Google são CONSTANTES oficiais no cliente HTTP / na biblioteca oficial de autenticação — o
/// tenant NÃO fornece URL de destino.
/// </summary>
public sealed record KnightGoogleWorkspaceConfiguration(
    string CustomerId,
    string DelegatedAdminEmail,
    string ServiceAccountJson) : KnightSourceConfiguration
{
    public override KnightSourceType Source => KnightSourceType.GoogleWorkspace;

    // O ServiceAccountJson carrega a CHAVE PRIVADA da service account — jamais deve aparecer num dump/log.
    public override string ToString() =>
        $"KnightGoogleWorkspaceConfiguration {{ CustomerId = {CustomerId}, DelegatedAdminEmail = {DelegatedAdminEmail}, ServiceAccountJson = *** }}";
}

// ---- Coletor -------------------------------------------------------------------------------------------

/// <summary>Contexto de uma coleta: o tenant e a configuração RESOLVIDA da fonte.</summary>
public sealed record KnightCollectionContext(Guid TenantId, KnightSourceConfiguration Configuration)
{
    public KnightSourceType Source => Configuration.Source;
}

/// <summary>
/// Resultado NORMALIZADO de uma coleta: fatos tipados + estado da fonte + estado por capacidade. As regras
/// determinísticas avaliam os fatos; a fonte é sempre identificada. Dados não obtidos ficam Missing (viram
/// NotEvaluated na avaliação), nunca aprovação.
/// </summary>
public sealed record KnightCollectionResult(
    KnightSourceType Source,
    KnightSourceState State,
    string SourceLabel,
    KnightFactSet Facts,
    IReadOnlyList<KnightCapabilityStatus> Capabilities,
    DateTimeOffset CollectedAt,
    string? Detail = null,
    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Postura AGREGADA de risco de identidade da MESMA coleta lógica
    /// (provider-neutral, sem PII). Opcional: fontes que não a produzem deixam <c>null</c>, e o contrato
    /// anterior segue intacto. NÃO entra na fórmula do KNIGHT Score — é fato consultivo.
    /// </summary>
    AegisScore.Application.Identity.IdentityRiskPosture? IdentityRisk = null,
    /// <summary>
    /// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Postura AGREGADA de registro de métodos de autenticação, derivada do
    /// MESMO relatório agregado já autorizado (sem chamadas por usuário e sem permissão nova).
    /// </summary>
    AegisScore.Application.Identity.IdentityAuthenticationPosture? AuthenticationPosture = null,
    /// <summary>
    /// [AEGIS-MVP-PRODUCT-02] Objetos que SUSTENTAM os achados, preservados pela MESMA coleta que produziu as
    /// contagens — nunca uma segunda aquisição. Opcional: uma fonte que não os produz deixa vazio, e a tela
    /// declara a ausência em vez de inventar uma lista. Estes objetos NÃO entram no snapshot agregado da
    /// Evidence Fabric (que segue sem PII): quem os persiste é a superfície dedicada do assessment.
    /// </summary>
    IReadOnlyList<KnightAffectedObjectEvidence>? AffectedObjects = null,
    /// <summary>
    /// [AEGIS-KNIGHT-MULTICLOUD-01] Configuração de diretório observada pela MESMA coleta (políticas de acesso
    /// condicional normalizadas, papéis privilegiados ativos). Persistida no ADM como objetos de configuração e
    /// relida de lá antes da avaliação. <c>null</c> quando a fonte não a produz.
    /// </summary>
    AegisScore.Application.Knight.Configuration.KnightDirectoryConfiguration? DirectoryConfiguration = null,
    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-01] Configuração do LOCATÁRIO observada pela mesma coleta (política de autorização,
    /// métodos de autenticação, regras de senha, domínios, dispositivos, PIM, revisões de acesso…), como
    /// documentos de contratos tipados. Persistida no ADM e relida de lá antes da avaliação. <c>null</c> quando a
    /// fonte não a produz.
    /// </summary>
    AegisScore.Application.Knight.Configuration.KnightTenantConfiguration? TenantConfiguration = null)
{
    /// <summary>Conjuntos de objetos afetados desta coleta — vazio quando a fonte não preserva detalhe.</summary>
    public IReadOnlyList<KnightAffectedObjectEvidence> AffectedObjectSets =>
        AffectedObjects ?? Array.Empty<KnightAffectedObjectEvidence>();

    /// <summary>Configuração do locatário desta coleta — vazia (tudo "não coletado") quando a fonte não a produz.</summary>
    public AegisScore.Application.Knight.Configuration.KnightTenantConfiguration TenantConfigurationOrEmpty =>
        TenantConfiguration ?? new AegisScore.Application.Knight.Configuration.KnightTenantConfiguration(
            Array.Empty<AegisScore.Application.Knight.Configuration.KnightConfigurationDocument>(), Capabilities);

    public static KnightCollectionResult NotConfigured(KnightSourceType source, string label) => new(
        source, KnightSourceState.NotConfigured, label, KnightFactSet.Empty,
        Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow, "Fonte não configurada.");
}

/// <summary>
/// Coletor do AEGIS KNIGHT: colhe de UMA fonte e devolve fatos normalizados + estado. O núcleo só conhece
/// esta porta — acrescentar uma fonte (Google Workspace, AD, Okta…) é registrar outro coletor, sem refatorar.
/// </summary>
public interface IKnightCollector
{
    KnightSourceType Source { get; }
    Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default);
}

/// <summary>Registro/factory de coletores por <see cref="KnightSourceType"/>.</summary>
public interface IKnightCollectorRegistry
{
    bool TryResolve(KnightSourceType source, out IKnightCollector? collector);
    IKnightCollector Resolve(KnightSourceType source);
    IReadOnlyList<KnightSourceType> RegisteredSources { get; }
}

/// <summary>Disponibilidade de uma fonte real para um tenant (para a UI escolher Demo × real).</summary>
public sealed record KnightSourceAvailability(KnightSourceType Source, string Label, bool Configured, bool Enabled);

/// <summary>
/// Resolve a CONFIGURAÇÃO de uma fonte para um tenant — lê o <c>ConnectorConfig</c> e DECIFRA os segredos
/// (Infrastructure). Demo é sempre disponível; fontes reais dependem de configuração por tenant.
/// </summary>
public interface IKnightSourceConfigurationProvider
{
    Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default);
    Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default);
}
