/**
 * Contratos da tela de Integrações. Espelham os enums do backend (`AegisScore.Domain/Common.cs`) —
 * a API os serializa como INTEIRO na entrada e como STRING na saída, então os catálogos abaixo
 * carregam os dois lados.
 */

/** Espelha `ConnectorProvider`. O valor numérico é o que o POST envia. */
export type ProviderKey =
  | 'Microsoft'
  | 'Google'
  | 'Aws'
  | 'MicrosoftSentinel'
  | 'CrowdStrike'
  | 'Splunk'
  | 'Generic';

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
  | 'PolicyDocuments';

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
}

export const PROVIDERS: ProviderSpec[] = [
  {
    key: 'MicrosoftSentinel',
    value: 3,
    label: 'Microsoft Sentinel',
    authType: 'OAuthClientCredentials',
    authTypeValue: 0,
    capability: 'Siem',
    capabilityValue: 5,
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientId', label: 'Application (client) ID', secret: false, placeholder: '00000000-0000-0000-0000-000000000000' },
      { key: 'clientSecret', label: 'Client secret', secret: true },
      { key: 'workspaceId', label: 'Log Analytics Workspace ID', secret: false },
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
    fields: [
      { key: 'tenantId', label: 'Directory (tenant) ID', secret: false },
      { key: 'clientId', label: 'Application (client) ID', secret: false },
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
    fields: [
      { key: 'baseUrl', label: 'Base URL', secret: false, placeholder: 'https://splunk.cliente.com:8089' },
      { key: 'token', label: 'Authentication token', secret: true },
    ],
  },
];

export function providerByKey(key: string | null | undefined): ProviderSpec | undefined {
  return PROVIDERS.find((p) => p.key === key);
}

/** Espelha `ConnectorConfigDto`. NUNCA carrega o segredo — só o booleano `hasCredentials`. */
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

/** Rótulo PT-BR do status operacional, no idioma do glossário NIST (models/nist-glossary.ts). */
export function statusLabel(status: string): string {
  switch (status) {
    case 'Healthy':
      return 'Saudável';
    case 'Degraded':
      return 'Degradado';
    case 'Failed':
      return 'Falhou';
    default:
      return 'Nunca testado';
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
