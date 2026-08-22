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
}

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
  // ---- Genéricos de PUSH (operacionais no MVP): recebem eventos por endpoint autenticado ----
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
  // ---- Adaptadores específicos: honestamente marcados (ainda não implementados / demonstração) ----
  {
    key: 'MicrosoftSentinel',
    value: 3,
    label: 'Microsoft Sentinel',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'Siem',
    capabilityValue: 5,
    adapterNote: 'Adaptador específico ainda não implementado. Envie eventos pelo Generic SIEM (push).',
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
      { key: 'workspaceId', label: 'Log Analytics Workspace ID', secret: false },
    ],
  },
  // ---- AEGIS KNIGHT: coletor REAL somente-leitura do Microsoft Entra ID (IdentityPosture) ----
  {
    key: 'MicrosoftEntraKnight',
    value: 0, // ConnectorProvider.Microsoft
    label: 'Microsoft Entra ID · AEGIS KNIGHT',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'IdentityPosture',
    capabilityValue: 10, // ConnectorCapability.IdentityPosture
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
    value: 1, // ConnectorProvider.Google
    label: 'Google Workspace · AEGIS KNIGHT',
    authType: 'ServiceAccount',
    authTypeValue: 2,
    capability: 'IdentityPosture',
    capabilityValue: 10, // ConnectorCapability.IdentityPosture
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
    value: 0, // ConnectorProvider.Microsoft
    label: 'Microsoft Defender Vulnerability Management',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'VulnerabilityScanner',
    capabilityValue: 8, // ConnectorCapability.VulnerabilityScanner
    infoNote:
      'Coleta REAL somente leitura de vulnerabilidades associadas a ativos (máquinas × CVEs). “Testar” valida a autenticação e as duas permissões; “Sincronizar” atualiza ativos, CVEs e exposições. O destino é a API oficial do Defender — não há URL configurável. Exige licença/capacidade compatível, máquinas onboardadas e consentimento administrativo. Veja os achados em Vulnerabilidades.',
    appPermissions: ['Machine.Read.All', 'Vulnerability.Read.All'],
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
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
    adapterNote: 'Adaptador específico ainda não implementado. Envie eventos pelo Generic SIEM (push).',
    fields: [
      { key: 'customerId', label: 'Customer ID', secret: false },
      { key: 'region', label: 'Região', secret: false, placeholder: 'us / europe / asia-southeast1' },
      { key: 'serviceAccountJson', label: 'Service Account JSON', secret: true },
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

/** Espelha `ConnectorConfigDto`. NUNCA carrega o segredo — só os booleanos `hasCredentials`/`hasIngestionKey`. */
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
  /** [AEGIS-AUD-020] Há chave de ingestão configurada? A chave em si NUNCA volta para a tela. */
  hasIngestionKey: boolean;
}

/** É um conector GENÉRICO de push (Generic/Siem ou Generic/Edr)? Deriva dos rótulos do DTO. */
export function isGenericPush(c: ConnectorConfig): boolean {
  return c.provider === 'Generic' && (c.capability === 'Siem' || c.capability === 'Edr');
}

/**
 * É o conector do AEGIS KNIGHT (postura de identidade)? Deriva da capacidade IdentityPosture. Não usa o
 * pipeline genérico de evidências — a tela mostra "Abrir AEGIS KNIGHT" em vez de "Testar"/"Coletar".
 */
export function isKnightConnector(c: ConnectorConfig): boolean {
  return c.capability === 'IdentityPosture';
}

/** Corpo de `POST /api/v1/tenants/connectors`. O TenantId NÃO trafega: vem do JWT. */
export interface SaveConnectorRequest {
  provider: number;
  capability: number;
  displayName: string;
  authType: number;
  settings: string;
  syncIntervalMinutes: number;
}

/** Espelha `ConnectorHealthDto`. */
export interface ConnectorHealth {
  status: string;
  message: string | null;
}

/** [AEGIS-MVP-VULN-01] Espelha `VulnerabilitySyncSummaryDto` — contagens de uma sincronização de vulnerabilidades. */
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

/** Espelha `SyncResultDto`. `vulnerabilities` só vem preenchido para conectores de vulnerabilidade. */
export interface SyncResult {
  signalsCollected: number;
  vulnerabilities?: VulnerabilitySyncSummary | null;
}

/** Rótulo PT-BR do status operacional do conector (texto exibido ao usuário). */
export function statusLabel(status: string): string {
  switch (status) {
    case 'Healthy':
      return 'Operacional';
    case 'Degraded':
      return 'Degradado';
    case 'Failed':
      return 'Com falha';
    default:
      return 'Não verificado';
  }
}

/** Faixa de cor do HUD por status — mesma régua dos painéis de pilar (cyan/âmbar/vermelho). */
export function statusTone(status: string): 'ok' | 'warn' | 'bad' | 'idle' {
  switch (status) {
    case 'Healthy':
      return 'ok';
    case 'Degraded':
      return 'warn';
    case 'Failed':
      return 'bad';
    default:
      return 'idle';
  }
}
