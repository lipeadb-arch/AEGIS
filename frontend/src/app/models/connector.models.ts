/**
 * Contratos da tela de Integrações. Espelham os enums do backend (`AegisScore.Domain/Common.cs`) —
 * a API os serializa como INTEIRO na entrada e como STRING na saída, então os catálogos abaixo
 * carregam os dois lados.
 */

/**
 * Chave do SELECT da tela. Espelha `ConnectorProvider` (o `value` numérico é o que o POST envia), mas os
 * conectores GENÉRICOS de push aparecem como duas opções distintas (SIEM/EDR) sobre o mesmo provider 99.
 */
export type ProviderKey =
  | 'Microsoft'
  | 'MicrosoftDefenderVuln'
  | 'MicrosoftEntraKnight'
  | 'GoogleWorkspaceKnight'
  | 'GoogleCloudVuln'
  | 'Google'
  | 'Aws'
  | 'MicrosoftSentinel'
  | 'CrowdStrike'
  | 'Splunk'
  | 'GenericSiem'
  | 'GenericEdr';

/** Espelha `ConnectorAuthType`. */
export type AuthTypeKey = 'OAuthClientCredentials' | 'ApiKey' | 'ServiceAccount';

/** Espelha `ConnectorCapability`. */
export type CapabilityKey =
  | 'SecureScore'
  | 'DefenderExposure'
  | 'PurviewCompliance'
  | 'AzureAdvisor'
  | 'ConfigAnalyzer'
  | 'Siem'
  | 'Edr'
  | 'Cmdb'
  | 'VulnerabilityScanner'
  | 'PolicyDocuments'
  | 'IdentityPosture';

/** Um campo de credencial exigido por um provedor. `secret: true` ⇒ input mascarado. */
export interface CredentialField {
  key: string;
  label: string;
  secret: boolean;
  placeholder?: string;
  /**
   * Quando presente, o campo é uma SELEÇÃO CONTROLADA (dropdown) — a UI não aceita texto livre. Usado para
   * localidades/endpoints oficialmente suportados (ex.: Google SecOps), fechando o vetor de URL/host arbitrário.
   */
  options?: { value: string; label: string }[];
}

/**
 * [AEGIS-MVP-GOOGLE-SECOPS-01] Localidades OFICIALMENTE suportadas do Google SecOps (Chronicle). Espelha a allowlist
 * interna do backend (ChronicleRegions): o backend é a autoridade final; a seleção controlada aqui só impede que o
 * usuário digite uma região/host arbitrário. O host regional é derivado no servidor ({location}-chronicle...).
 */
export const CHRONICLE_LOCATIONS: { value: string; label: string }[] = [
  { value: 'us', label: 'US (multirregião)' },
  { value: 'eu', label: 'UE (multirregião)' },
  { value: 'europe', label: 'Europa (multirregião)' },
  { value: 'africa-south1', label: 'África — Joanesburgo (africa-south1)' },
  { value: 'asia-east1', label: 'Ásia — Taiwan (asia-east1)' },
  { value: 'asia-northeast1', label: 'Ásia — Tóquio (asia-northeast1)' },
  { value: 'asia-northeast3', label: 'Ásia — Seul (asia-northeast3)' },
  { value: 'asia-south1', label: 'Ásia — Mumbai (asia-south1)' },
  { value: 'asia-southeast1', label: 'Ásia — Singapura (asia-southeast1)' },
  { value: 'asia-southeast2', label: 'Ásia — Jacarta (asia-southeast2)' },
  { value: 'australia-southeast1', label: 'Austrália — Sydney (australia-southeast1)' },
  { value: 'europe-central2', label: 'Europa — Varsóvia (europe-central2)' },
  { value: 'europe-west2', label: 'Europa — Londres (europe-west2)' },
  { value: 'europe-west3', label: 'Europa — Frankfurt (europe-west3)' },
  { value: 'europe-west6', label: 'Europa — Zurique (europe-west6)' },
  { value: 'europe-west9', label: 'Europa — Paris (europe-west9)' },
  { value: 'europe-west12', label: 'Europa — Turim (europe-west12)' },
  { value: 'me-central1', label: 'Oriente Médio — Doha (me-central1)' },
  { value: 'me-central2', label: 'Oriente Médio — Damã (me-central2)' },
  { value: 'me-west1', label: 'Oriente Médio — Tel Aviv (me-west1)' },
  { value: 'northamerica-northeast2', label: 'América do Norte — Toronto (northamerica-northeast2)' },
  { value: 'southamerica-east1', label: 'América do Sul — São Paulo (southamerica-east1)' },
];

/**
 * Catálogo de provedores. Define, por provedor, quais credenciais a tela pede — é o que evita um
 * formulário genérico "cole aqui um JSON", hostil para quem opera o SOC.
 *
 * ⚠️ Os campos viram um objeto JSON que vai no `settings` (texto). O backend NÃO interpreta esse
 * conteúdo: ele apenas o CIFRA (Data Protection) e o guarda. Quem o lê é o conector, na coleta.
 */
export interface ProviderSpec {
  key: ProviderKey;
  value: number;
  label: string;
  authType: AuthTypeKey;
  authTypeValue: number;
  capability: CapabilityKey;
  capabilityValue: number;
  fields: CredentialField[];
  /** [AEGIS-AUD-020] Conector genérico de PUSH autenticado (recebe eventos; não faz coleta pull). */
  push?: boolean;
  /**
   * Marcação HONESTA do estado do adaptador específico. Ausente = push genérico operacional. Presente =
   * o fornecedor ainda NÃO tem adaptador real (ou é demonstração/stub) — a tela não finge conexão real.
   */
  adapterNote?: string;
  /**
   * AEGIS KNIGHT (IdentityPosture): coletor REAL somente-leitura que NÃO usa o pipeline genérico
   * IConnectorRegistry/IEvidenceIngestionExecutor. A tela não mostra "Testar"/"Coletar" (retornariam 501):
   * mostra "Abrir AEGIS KNIGHT" (rota /identity), onde a coleta real é disparada.
   */
  knight?: boolean;
  /** Nota informativa (não é aviso de stub) — usada pelo coletor real do KNIGHT. */
  infoNote?: string;
  /** Permissões de aplicativo (somente leitura) exigidas — exibidas no formulário do KNIGHT. */
  appPermissions?: string[];
}

/** Campo único da chave de ingestão (genéricos de push): mascarado, mín. 24 chars, escrita-apenas. */
const INGESTION_KEY_FIELD: CredentialField = {
  key: 'ingestionKey',
  label: 'Chave de ingestão (mín. 24 caracteres)',
  secret: true,
  placeholder: 'segredo de alta entropia gerado por você',
};

export const PROVIDERS: ProviderSpec[] = [
  {
    key: 'GenericSiem',
    value: 99,
    label: 'Generic SIEM — push autenticado',
    authType: 'ApiKey',
    authTypeValue: 1,
    capability: 'Siem',
    capabilityValue: 5,
    push: true,
    fields: [INGESTION_KEY_FIELD],
  },
  {
    key: 'GenericEdr',
    value: 99,
    label: 'Generic EDR — push autenticado',
    authType: 'ApiKey',
    authTypeValue: 1,
    capability: 'Edr',
    capabilityValue: 6,
    push: true,
    fields: [INGESTION_KEY_FIELD],
  },
  {
    key: 'MicrosoftSentinel',
    value: 3,
    label: 'Microsoft Sentinel · SIEM',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'Siem',
    capabilityValue: 5,
    infoNote:
      'Coleta REAL somente leitura do Microsoft Sentinel via Azure Monitor Log Analytics (KQL fixa no servidor). “Testar” valida autenticação e executa uma consulta mínima no workspace; “Sincronizar” lê a postura operacional (incidentes/alertas agregados). O destino é a API oficial do Log Analytics — não há URL configurável. Exige o Log Analytics Workspace ID e Azure RBAC de leitura no workspace. Não altera o AEGIS Score (fato consultivo).',
    appPermissions: ['Log Analytics Reader (Azure RBAC de leitura no workspace) — ou permissão mínima equivalente'],
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
      { key: 'workspaceId', label: 'Log Analytics Workspace ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
    ],
  },
  {
    key: 'MicrosoftEntraKnight',
    value: 0,
    label: 'Microsoft Entra ID · AEGIS KNIGHT',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'IdentityPosture',
    capabilityValue: 10,
    knight: true,
    infoNote:
      'Coletor REAL somente-leitura do Microsoft Entra ID (client credentials). Após salvar, dispare a coleta em Abrir AEGIS KNIGHT → “Coletar do Entra ID”. O destino é o Microsoft Graph oficial — não há URL configurável.',
    appPermissions: ['Directory.Read.All', 'AuditLog.Read.All', 'User.Read.All', 'Policy.Read.All', 'Application.Read.All'],
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
    ],
  },
  {
    key: 'GoogleWorkspaceKnight',
    value: 1,
    label: 'Google Workspace · AEGIS KNIGHT',
    authType: 'ServiceAccount',
    authTypeValue: 2,
    capability: 'IdentityPosture',
    capabilityValue: 10,
    knight: true,
    infoNote:
      'Coletor REAL somente-leitura do Google Workspace (service account com domain-wide delegation). Após salvar, dispare a coleta em Abrir AEGIS KNIGHT → “Coletar do Google Workspace”. Coleta apenas metadados administrativos/auditoria — nunca conteúdo de Gmail, Drive ou Chat.',
    appPermissions: [
      'admin.directory.user.readonly',
      'admin.directory.group.readonly',
      'admin.directory.group.member.readonly',
      'admin.directory.domain.readonly',
      'admin.reports.audit.readonly',
    ],
    fields: [
      { key: 'customerId', label: 'Customer ID', secret: false, placeholder: 'C0xxxxxxx' },
      { key: 'delegatedAdminEmail', label: 'E-mail do administrador delegado', secret: false, placeholder: 'admin@sua-org.example.com' },
      { key: 'serviceAccountJson', label: 'Service Account JSON (com domain-wide delegation)', secret: true },
    ],
  },
  {
    key: 'Microsoft',
    value: 0,
    label: 'Microsoft 365 · Secure Score',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'SecureScore',
    capabilityValue: 0,
    infoNote:
      'Coleta REAL somente leitura do Microsoft Secure Score (client credentials). “Testar” valida autenticação e leitura ($top=1); “Coletar” atualiza o Secure Score e as exposições de configuração. O destino é o Microsoft Graph oficial — não há URL configurável. Veja os achados em Exposições.',
    appPermissions: ['SecurityEvents.Read.All'],
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
    ],
  },
  {
    key: 'MicrosoftDefenderVuln',
    value: 0,
    label: 'Microsoft Defender Vulnerability Management',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'VulnerabilityScanner',
    capabilityValue: 8,
    infoNote:
      'Coleta REAL somente leitura de vulnerabilidades associadas a ativos (máquinas × CVEs). “Testar” valida a autenticação e as permissões; “Sincronizar” atualiza ativos, CVEs e exposições — numa ÚNICA aquisição (mesmo token/inventário de máquinas). O destino é a API oficial do Defender — não há URL configurável. Exige licença/capacidade compatível, máquinas onboardadas e consentimento administrativo. Veja os achados em Vulnerabilidades. [AEGIS-MVP-MICROSOFT-COVERAGE-01] O MESMO conector também coleta o inventário de software instalado (produtos, versões e exposição por dispositivo) quando a permissão Software.Read.All estiver disponível — veja a aba “Software exposto”, dentro de Vulnerabilidades; a ausência dessa permissão degrada só essa dimensão, sem afetar máquinas/vulnerabilidades.',
    appPermissions: ['Machine.Read.All', 'Vulnerability.Read.All', 'Software.Read.All (opcional — habilita a aba Software exposto)'],
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
    ],
  },
  {
    key: 'GoogleCloudVuln',
    value: 1,
    label: 'Google Cloud · VM Manager (Vulnerability Reports)',
    authType: 'ServiceAccount',
    authTypeValue: 2,
    capability: 'VulnerabilityScanner',
    capabilityValue: 8,
    infoNote:
      'Coleta REAL somente leitura de vulnerabilidades por instância de VM (recurso × CVE) via VM Manager / OS Config Vulnerability Reports. “Testar” valida autenticação e leitura (pageSize=1); “Sincronizar” atualiza ativos, CVEs e exposições. O destino é a API oficial osconfig.googleapis.com — não há URL configurável. Pré-requisitos: habilitar a API OS Config, ativar o VM Manager e ter o agente OS Config com inventário de SO nas VMs. A API direta do VM Manager/OS Config não exige o nível Premium do Security Command Center; permanecem aplicáveis os pré-requisitos, quotas e eventuais custos dos recursos Google Cloud utilizados. Service account SEM domain-wide delegation (a leitura efetiva vem do papel IAM). Veja os achados em Vulnerabilidades.',
    appPermissions: ['roles/osconfig.vulnerabilityReportViewer (osconfig.vulnerabilityReports.list)'],
    fields: [
      { key: 'projectId', label: 'Project ID', secret: false, placeholder: 'meu-projeto-123' },
      { key: 'locations', label: 'Localizações / zonas (separadas por vírgula)', secret: false, placeholder: 'southamerica-east1-a, us-central1-a' },
      { key: 'serviceAccountJson', label: 'Service Account JSON (somente leitura — SEM domain-wide delegation)', secret: true },
    ],
  },
  {
    key: 'Google',
    value: 1,
    label: 'Google SecOps (Chronicle)',
    authType: 'ServiceAccount',
    authTypeValue: 2,
    capability: 'Siem',
    capabilityValue: 5,
    infoNote:
      'Coleta operacional somente leitura pela Chronicle API do Google SecOps — casos (inventário atual) e alertas (últimos 30 dias). “Testar” valida autenticação e leitura da instância (instances.get); “Sincronizar” lê os agregados de casos e alertas. O destino é o endpoint regional oficial *-chronicle.googleapis.com, derivado da localidade — não há URL configurável. O OAuth scope é chronicle (aceito por casos e alertas); o conector é operacionalmente somente leitura porque só executa HTTP GET, e o menor privilégio efetivo depende das permissões IAM concedidas à conta de serviço. Não altera o AEGIS Score (fato consultivo). Ainda pendente de validação contra uma instância real do Google SecOps.',
    appPermissions: [
      'OAuth scope: https://www.googleapis.com/auth/chronicle',
      'IAM: chronicle.instances.get',
      'IAM: chronicle.cases.get',
      'IAM: chronicle.legacies.legacySearchEnterpriseWideAlerts',
    ],
    fields: [
      { key: 'projectId', label: 'Project ID', secret: false, placeholder: 'meu-projeto-123' },
      { key: 'location', label: 'Localidade / região', secret: false, options: CHRONICLE_LOCATIONS },
      { key: 'instanceId', label: 'Instance ID do SecOps', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'serviceAccountJson', label: 'Service Account JSON (somente leitura — SEM domain-wide delegation)', secret: true },
    ],
  },
  {
    key: 'CrowdStrike',
    value: 4,
    label: 'CrowdStrike Falcon',
    authType: 'ApiKey',
    authTypeValue: 1,
    capability: 'Edr',
    capabilityValue: 6,
    adapterNote: 'Adaptador específico ainda não implementado. Envie eventos pelo Generic EDR (push).',
    fields: [
      { key: 'clientId', label: 'Client ID', secret: false },
      { key: 'clientSecret', label: 'Client secret', secret: true },
      { key: 'baseUrl', label: 'Base URL', secret: false, placeholder: 'https://api.crowdstrike.com' },
    ],
  },
  {
    key: 'Aws',
    value: 2,
    label: 'AWS Security Hub',
    authType: 'ApiKey',
    authTypeValue: 1,
    capability: 'ConfigAnalyzer',
    capabilityValue: 4,
    adapterNote: 'Adaptador específico ainda não implementado.',
    fields: [
      { key: 'accessKeyId', label: 'Access Key ID', secret: false },
      { key: 'secretAccessKey', label: 'Secret Access Key', secret: true },
      { key: 'region', label: 'Região', secret: false, placeholder: 'us-east-1' },
    ],
  },
  {
    key: 'Splunk',
    value: 5,
    label: 'Splunk',
    authType: 'ApiKey',
    authTypeValue: 1,
    capability: 'Siem',
    capabilityValue: 5,
    adapterNote: 'Adaptador específico ainda não implementado. Envie eventos pelo Generic SIEM (push).',
    fields: [
      { key: 'baseUrl', label: 'Base URL', secret: false, placeholder: 'https://splunk.demo.example.com:8089' },
      { key: 'token', label: 'Authentication token', secret: true },
    ],
  },
];

export function providerByKey(key: string | null | undefined): ProviderSpec | undefined {
  return PROVIDERS.find((p) => p.key === key);
}

// ============================================================================
// [AEGIS-MVP-MICROSOFT-HUB] Conexão Microsoft unificada
// ----------------------------------------------------------------------------
// UMA credencial comum (tenantId/clientId/clientSecret) informada uma vez, aplicada+cifrada no servidor a cada
// serviço Microsoft selecionado. Cada serviço permanece um conector INDEPENDENTE (estado/sincronização/falha
// próprios). O workspaceId é EXCLUSIVO do Sentinel — a lógica pura abaixo garante que ele nunca vaze aos demais.
// ============================================================================

/** Chave de um serviço Microsoft dentro do hub. */
export type MicrosoftServiceKey =
  | 'SecureScore'
  | 'IdentityPosture'
  | 'VulnerabilityScanner'
  | 'Sentinel'
  | 'IntunePosture';

/** Especificação de um serviço Microsoft filho: capacidade/provider (para o POST) + apresentação. */
export interface MicrosoftServiceSpec {
  key: MicrosoftServiceKey;
  capability: CapabilityKey;
  capabilityValue: number;
  provider: ProviderKey;
  providerValue: number;
  label: string;
  description: string;
  /** Só o Sentinel exige/usa o workspaceId (Log Analytics). */
  needsWorkspaceId: boolean;
  appPermissions: string[];
}

/**
 * Os cinco serviços da família Microsoft. O provider é derivado da capacidade (Siem ⇒ MicrosoftSentinel; demais
 * ⇒ Microsoft) — igual ao backend. IdentityPosture (AEGIS KNIGHT) coleta pela tela /identity, mas a credencial é
 * a mesma da conexão unificada. [AEGIS-MVP-MICROSOFT-COVERAGE-02] IntunePosture entra como o quinto serviço, com
 * DUAS permissões distintas: uma para as políticas (já concedida no ambiente atual) e outra para o estado efetivo
 * dos dispositivos — a tela deixa explícito qual dimensão cada uma destrava.
 */
export const MICROSOFT_HUB_SERVICES: MicrosoftServiceSpec[] = [
  {
    key: 'SecureScore',
    capability: 'SecureScore',
    capabilityValue: 0,
    provider: 'Microsoft',
    providerValue: 0,
    label: 'Microsoft 365 · Secure Score',
    description: 'Sinais e exposições de configuração do Secure Score (Microsoft Graph).',
    needsWorkspaceId: false,
    appPermissions: ['SecurityEvents.Read.All'],
  },
  {
    key: 'IdentityPosture',
    capability: 'IdentityPosture',
    capabilityValue: 10,
    provider: 'Microsoft',
    providerValue: 0,
    label: 'Microsoft Entra ID · AEGIS KNIGHT',
    description: 'Postura de identidade (somente leitura). A coleta é disparada em Abrir AEGIS KNIGHT.',
    needsWorkspaceId: false,
    appPermissions: ['Directory.Read.All', 'AuditLog.Read.All', 'User.Read.All', 'Policy.Read.All', 'Application.Read.All'],
  },
  {
    key: 'VulnerabilityScanner',
    capability: 'VulnerabilityScanner',
    capabilityValue: 8,
    provider: 'Microsoft',
    providerValue: 0,
    label: 'Microsoft Defender Vulnerability Management',
    description:
      'Vulnerabilidades associadas a ativos (máquinas × CVEs), somente leitura. O mesmo conector também coleta exposição de software (aba “Software exposto”, em Vulnerabilidades) quando Software.Read.All estiver disponível.',
    needsWorkspaceId: false,
    appPermissions: ['Machine.Read.All', 'Vulnerability.Read.All', 'Software.Read.All (opcional)'],
  },
  {
    key: 'Sentinel',
    capability: 'Siem',
    capabilityValue: 5,
    provider: 'MicrosoftSentinel',
    providerValue: 3,
    label: 'Microsoft Sentinel · SIEM',
    description: 'Postura operacional (incidentes/alertas) via Azure Monitor Log Analytics, somente leitura.',
    needsWorkspaceId: true,
    appPermissions: ['Log Analytics Reader (Azure RBAC de leitura no workspace) — ou permissão mínima equivalente'],
  },
  {
    key: 'IntunePosture',
    capability: 'ConfigAnalyzer',
    capabilityValue: 4,
    provider: 'Microsoft',
    providerValue: 0,
    label: 'Microsoft Intune · Configuração e Conformidade',
    description:
      'Políticas de conformidade e configuração de dispositivos e — quando a permissão de dispositivos for ' +
      'concedida — o estado efetivo dos aparelhos gerenciados (seção “Dispositivos gerenciados”, em Protect). ' +
      'As duas dimensões são independentes: sem a permissão de dispositivos, as políticas continuam sendo lidas ' +
      'e o estado dos aparelhos aparece como bloqueado — nunca como zero.',
    needsWorkspaceId: false,
    appPermissions: [
      'DeviceManagementConfiguration.Read.All (políticas e configurações)',
      'DeviceManagementManagedDevices.Read.All (estado efetivo dos dispositivos)',
    ],
  },
];

export function microsoftServiceByKey(key: string | null | undefined): MicrosoftServiceSpec | undefined {
  return MICROSOFT_HUB_SERVICES.find((s) => s.key === key);
}

/** Chaves de PROVIDERS que a conexão Microsoft unificada absorve — removidas do formulário genérico. */
const MICROSOFT_HUB_PROVIDER_KEYS: ProviderKey[] = [
  'Microsoft',
  'MicrosoftDefenderVuln',
  'MicrosoftEntraKnight',
  'MicrosoftSentinel',
];

/** Provedores do formulário GENÉRICO (a família Microsoft sai daqui — vai para o hub unificado). */
export const GENERIC_PROVIDERS: ProviderSpec[] = PROVIDERS.filter(
  (p) => !MICROSOFT_HUB_PROVIDER_KEYS.includes(p.key),
);

/** Um conector configurado pertence à família Microsoft (agrupado sob “Microsoft”)? */
export function isMicrosoftFamily(c: ConnectorConfig): boolean {
  return (
    (c.provider === 'Microsoft' &&
      (c.capability === 'SecureScore' ||
        c.capability === 'IdentityPosture' ||
        c.capability === 'VulnerabilityScanner' ||
        // [AEGIS-MVP-MICROSOFT-COVERAGE-02] Microsoft Intune (postura de dispositivos) — quinto filho do hub.
        c.capability === 'ConfigAnalyzer')) ||
    (c.provider === 'MicrosoftSentinel' && c.capability === 'Siem')
  );
}

/** Especificação do serviço Microsoft que corresponde a um conector configurado (para rótulo/agrupamento). */
export function microsoftServiceFor(c: ConnectorConfig): MicrosoftServiceSpec | undefined {
  return MICROSOFT_HUB_SERVICES.find((s) => s.provider === c.provider && s.capability === c.capability);
}

/** Uma seleção de serviço no formulário do hub (marcada pelo usuário). */
export interface MicrosoftServiceSelection {
  key: MicrosoftServiceKey;
  syncIntervalMinutes: number;
  workspaceId?: string | null;
}

/** Um serviço no corpo do POST do hub (capacidade numérica + extras). */
export interface MicrosoftHubServiceInput {
  capability: number;
  syncIntervalMinutes: number;
  workspaceId?: string;
  displayName?: string;
}

/** Corpo do POST /tenants/connectors/microsoft — credencial comum informada UMA vez + serviços. */
export interface MicrosoftHubRequest {
  tenantId: string;
  clientId: string;
  clientSecret: string;
  services: MicrosoftHubServiceInput[];
}

/**
 * Monta o corpo do hub a partir de UMA credencial comum + as seleções. Regra central (testada): o `workspaceId`
 * só entra no serviço Sentinel — nunca contamina Secure Score, Entra ID ou Vulnerability Management. A mesma
 * credencial é aplicada a todos os serviços selecionados (informada uma única vez).
 */
export function buildMicrosoftHubRequest(
  creds: { tenantId: string; clientId: string; clientSecret: string },
  selections: MicrosoftServiceSelection[],
): MicrosoftHubRequest {
  const services: MicrosoftHubServiceInput[] = selections.map((sel) => {
    const spec = microsoftServiceByKey(sel.key);
    if (!spec) throw new Error(`Serviço Microsoft desconhecido: ${sel.key}`);
    const input: MicrosoftHubServiceInput = {
      capability: spec.capabilityValue,
      syncIntervalMinutes: sel.syncIntervalMinutes,
      displayName: spec.label,
    };
    // workspaceId SOMENTE no Sentinel — os demais nunca o recebem.
    if (spec.needsWorkspaceId) input.workspaceId = (sel.workspaceId ?? '').trim();
    return input;
  });
  return {
    tenantId: creds.tenantId.trim(),
    clientId: creds.clientId.trim(),
    clientSecret: creds.clientSecret,
    services,
  };
}

/** Formato GUID canônico (8-4-4-4-12 hex). Valida o workspaceId do Sentinel na borda do formulário (o backend
 *  continua sendo a autoridade final). Função pura e testável. */
const GUID_RE = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
export function isGuid(value: string | null | undefined): boolean {
  return !!value && GUID_RE.test(value.trim());
}

export interface ConnectorConfig {
  id: string;
  provider: string;
  capability: string;
  displayName: string;
  authType: string;
  enabled: boolean;
  syncIntervalMinutes: number;
  lastSyncAt: string | null;
  lastStatus: string;
  hasCredentials: boolean;
  hasIngestionKey: boolean;
}

export function isGenericPush(c: ConnectorConfig): boolean {
  return c.provider === 'Generic' && (c.capability === 'Siem' || c.capability === 'Edr');
}

export function isKnightConnector(c: ConnectorConfig): boolean {
  return c.capability === 'IdentityPosture';
}

// ============================================================================
// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Estado de CONEXÃO ATUAL (derivado) e ações administrativas
// ----------------------------------------------------------------------------
// O estado de conexão é DERIVADO dos campos que o backend já expõe (enabled + credencial), e é a AUTORIDADE
// sobre o que a UI apresenta como situação atual — distinto do `lastStatus`, que é evidência histórica da
// última coleta. Assim, uma coleta antiga marcada "Operacional" jamais é apresentada como saúde atual de um
// conector desconectado. Funções PURAS e testáveis (sem DOM).
// ============================================================================

/** Situação ATUAL da conexão de um conector. */
export type ConnectorConnectionState = 'connected' | 'disabled' | 'disconnected';

/** Há alguma credencial guardada? (segredo pull OU chave de ingestão push). */
function hasAnyCredential(c: ConnectorConfig): boolean {
  return c.hasCredentials || c.hasIngestionKey;
}

/**
 * Estado de conexão derivado: sem credencial ⇒ `disconnected` (desconectado, credencial eliminada ou nunca
 * configurada); com credencial e habilitado ⇒ `connected`; com credencial e desabilitado ⇒ `disabled`.
 */
export function connectionState(c: ConnectorConfig): ConnectorConnectionState {
  if (!hasAnyCredential(c)) return 'disconnected';
  return c.enabled ? 'connected' : 'disabled';
}

/** Rótulo INEQUÍVOCO do estado de conexão (o que o operador lê como situação atual). */
export function connectionStateLabel(state: ConnectorConnectionState): string {
  switch (state) {
    case 'connected':
      return 'Conectado';
    case 'disabled':
      return 'Desabilitado';
    case 'disconnected':
    default:
      return 'Desconectado';
  }
}

/** Tom visual do estado de conexão (reusa a paleta ok/warn/idle da tela). */
export function connectionStateTone(state: ConnectorConnectionState): 'ok' | 'warn' | 'idle' {
  switch (state) {
    case 'connected':
      return 'ok';
    case 'disabled':
      return 'warn';
    case 'disconnected':
    default:
      return 'idle';
  }
}

/** Testar exige credencial (não faz sentido testar um desconectado) — espelha a recusa 409 do backend. */
export function canTestConnector(c: ConnectorConfig): boolean {
  return connectionState(c) !== 'disconnected';
}

/** Sincronizar exige conexão ATIVA (habilitado + credencial) — espelha a recusa 409 do backend. */
export function canSyncConnector(c: ConnectorConfig): boolean {
  return connectionState(c) === 'connected';
}

export interface SaveConnectorRequest {
  provider: number;
  capability: number;
  displayName: string;
  authType: number;
  settings: string;
  syncIntervalMinutes: number;
}

/**
 * Corpo da edição administrativa (PUT /connectors/{id}): SÓ nome e intervalo. ⚠️ NÃO há campo de segredo —
 * editar jamais reescreve a credencial; reconectar (informar credencial de novo) é o fluxo de (re)configuração.
 */
export interface UpdateConnectorRequest {
  displayName: string;
  syncIntervalMinutes: number;
}

export interface ConnectorHealth {
  status: string;
  message: string | null;
}

export interface VulnerabilitySyncSummary {
  machinesObserved: number;
  assetsCreated: number;
  cvesUpserted: number;
  exposuresCreated: number;
  observationsOpened: number;
  observationsReopened: number;
  observationsResolved: number;
  bindingsDeactivated: number;
  assetsDeactivated: number;
  wasComplete: boolean;
  invalidMachines: number;
  invalidCves: number;
  invalidRelations: number;
}

/**
 * [AEGIS-MVP-SIEM] Estado EXPLÍCITO e PROVIDER-NEUTRAL da coleta de UMA dimensão de SIEM (espelha o enum do backend).
 * Só `Available` (e `Partial`, como piso) trazem contagem; os demais estados significam "não comprovado" — a UI
 * mostra indisponibilidade, NUNCA "0". Serve Microsoft Sentinel, Google SecOps e futuros SIEMs.
 */
export type SiemCollectionState =
  | 'Available'
  | 'Partial'
  | 'Unsupported'
  | 'PermissionDenied'
  | 'Throttled'
  | 'Timeout'
  | 'Unavailable';

/** [AEGIS-MVP-SIEM] Semântica do período de uma dimensão: janela deslizante (com dias) ou inventário atual (sem janela). */
export type SiemPeriodKind = 'RollingWindow' | 'CurrentInventory';

/** [AEGIS-MVP-SIEM] Uma contagem de casos por prioridade declarada pela fonte. */
export interface SiemPriorityCount {
  priority: string;
  count: number;
}

/**
 * [AEGIS-MVP-SIEM] Dimensão de CASOS/INCIDENTES. Contagens ANULÁVEIS (null = não coletada/não aplicável, nunca zero
 * sintético). `openByPriority` (Google SecOps) e o desmembramento por severidade (Sentinel) coexistem — cada
 * provedor preenche o eixo que expõe e deixa o resto nulo.
 */
export interface SiemCasePosture {
  state: SiemCollectionState;
  period: SiemPeriodKind;
  windowDays: number | null;
  isComplete: boolean;
  observed: number | null;
  open: number | null;
  'new': number | null;
  closed: number | null;
  openHighSeverity: number | null;
  openMediumSeverity: number | null;
  openLowSeverity: number | null;
  openInformationalSeverity: number | null;
  openByPriority: SiemPriorityCount[] | null;
  meanTimeToCloseHours: number | null;
  lastEvidenceAt: string | null;
}

/** [AEGIS-MVP-SIEM] Dimensão de ALERTAS. Contagens ANULÁVEIS (null = não coletada/não aplicável, nunca zero sintético). */
export interface SiemAlertPosture {
  state: SiemCollectionState;
  period: SiemPeriodKind;
  windowDays: number | null;
  isComplete: boolean;
  observed: number | null;
  highSeverity: number | null;
  mediumSeverity: number | null;
  lastEvidenceAt: string | null;
}

/**
 * [AEGIS-MVP-SIEM] Fotografia operacional PROVIDER-NEUTRAL de uma sincronização de SIEM, em DUAS DIMENSÕES
 * INDEPENDENTES. FATO CONSULTIVO: não vira sinal nem altera o AEGIS Score. `isComplete` falso = alguma dimensão
 * parcial/degradada. `source` identifica a origem ("Microsoft Sentinel", "Google SecOps").
 */
export interface SiemSyncSummary {
  source: string;
  isComplete: boolean;
  cases: SiemCasePosture;
  alerts: SiemAlertPosture;
}

/**
 * Texto de UMA dimensão para o toast. Função PURA e testável: só `Available` mostra a contagem (inclusive `0`);
 * `Partial` mostra a contagem como PISO; qualquer outro estado produz uma frase de indisponibilidade — nunca "0".
 */
export function siemDimensionText(state: SiemCollectionState, observed: number | null, noun: string): string {
  switch (state) {
    case 'Available':
      return `${observed ?? 0} ${noun}(s)`;
    case 'Partial':
      return observed !== null ? `≥ ${observed} ${noun}(s) (parcial)` : `${noun}s parciais`;
    case 'Unsupported':
      return `${noun}s não disponíveis nesta fonte`;
    case 'PermissionDenied':
      return `sem permissão para consultar ${noun}s`;
    case 'Throttled':
      return `consulta de ${noun}s temporariamente limitada`;
    case 'Timeout':
      return `consulta de ${noun}s excedeu o tempo`;
    case 'Unavailable':
    default:
      return `${noun}s indisponíveis`;
  }
}

/** Rótulo curto do período de uma dimensão (janela em dias ou inventário atual). */
export function siemPeriodText(period: SiemPeriodKind, windowDays: number | null): string {
  return period === 'RollingWindow' ? `últimos ${windowDays ?? 30}d` : 'inventário atual';
}

/**
 * Mensagem CURTA da sincronização de SIEM: fonte + casos + alertas (com período/inventário), aviso de coleta
 * parcial/degradada quando aplicável, e a nota explícita de que NÃO altera o AEGIS Score. Função pura e testável.
 */
export function buildSiemSyncMessage(s: SiemSyncSummary): string {
  const degradada = s.isComplete ? '' : ' (coleta parcial/degradada)';
  const casos = `${siemDimensionText(s.cases.state, s.cases.observed, 'caso')} (${siemPeriodText(s.cases.period, s.cases.windowDays)})`;
  const alertas = `${siemDimensionText(s.alerts.state, s.alerts.observed, 'alerta')} (${siemPeriodText(s.alerts.period, s.alerts.windowDays)})`;
  return `Postura de ${s.source}${degradada}: ${casos}; ${alertas}. Não altera o AEGIS Score.`;
}

export interface SyncResult {
  signalsCollected: number;
  vulnerabilities?: VulnerabilitySyncSummary | null;
  siem?: SiemSyncSummary | null;
}

export function statusLabel(status: string): string {
  switch (status) {
    case 'Healthy':
      return 'Operacional';
    case 'Syncing':
      return 'Sincronizando';
    case 'Degraded':
      return 'Degradado';
    case 'Failed':
      return 'Com falha';
    default:
      return 'Não verificado';
  }
}

export function statusTone(status: string): 'ok' | 'warn' | 'bad' | 'idle' {
  switch (status) {
    case 'Healthy':
      return 'ok';
    case 'Syncing':
    case 'Degraded':
      return 'warn';
    case 'Failed':
      return 'bad';
    default:
      return 'idle';
  }
}
