/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-02] Modelos de leitura da POSTURA DE CONFIGURAÇÃO E CONFORMIDADE DE DISPOSITIVOS.
 * Espelham o contrato `GET /api/v1/device-posture` — só agregados seguros; nunca identificador/nome de
 * dispositivo, usuário, payload de política ou credencial. É CONSULTIVO: nada aqui altera o AEGIS Score.
 *
 * Regra que atravessa todo o arquivo: um número `null` significa "não coletado / não autorizado", NUNCA zero.
 * Toda a lógica pura abaixo preserva essa distinção — é ela que impede a tela de dizer "0 dispositivos não
 * conformes" para um tenant cuja permissão sequer foi concedida.
 */

/** Estado geral da visão — o componente escolhe a tela por ele. */
export type DevicePostureState = 'NotConfigured' | 'NeverSynced' | 'Data';

/** Desfecho de UMA dimensão (ou sub-dimensão) de coleta. */
export type DevicePostureDimensionState =
  | 'NeverCollected'
  | 'Available'
  | 'Partial'
  | 'NotAuthorized'
  | 'NotLicensed'
  | 'Unavailable';

export type DevicePolicyKind = 'CompliancePolicy' | 'DeviceConfiguration';
export type DevicePolicyAssignmentState = 'Assigned' | 'Unassigned' | 'Unknown';

export type DeviceComplianceBucket =
  | 'Compliant'
  | 'Noncompliant'
  | 'InGracePeriod'
  | 'Conflict'
  | 'Error'
  | 'ManagedExternally'
  | 'Unknown';

export type DeviceEncryptionBucket = 'Encrypted' | 'NotEncrypted' | 'Unknown';
export type DeviceActivityBucket = 'Active' | 'Stale' | 'Unknown';

export interface DevicePostureDimension {
  state: string;
  storedState: string | null;
  label: string;
  hasData: boolean;
  isStale: boolean;
  requiredPermission: string | null;
  actionHint: string | null;
  lastAttemptAt: string | null;
  lastCollectionAt: string | null;
}

export interface DevicePostureConfigurationSummary {
  compliancePolicies: number | null;
  deviceConfigurations: number | null;
  totalPolicies: number | null;
  policiesAssigned: number | null;
  policiesUnassigned: number | null;
  policiesAssignmentUnknown: number | null;
}

export interface DevicePostureDeviceSummary {
  totalDevices: number | null;
  compliant: number | null;
  noncompliant: number | null;
  inGracePeriod: number | null;
  conflict: number | null;
  error: number | null;
  managedExternally: number | null;
  unknownCompliance: number | null;
  encrypted: number | null;
  notEncrypted: number | null;
  unknownEncryption: number | null;
  active: number | null;
  stale: number | null;
  unknownActivity: number | null;
  staleThresholdDays: number;
}

export interface DevicePolicy {
  externalId: string;
  kind: DevicePolicyKind;
  kindLabel: string;
  displayName: string;
  platformLabel: string | null;
  assignmentState: DevicePolicyAssignmentState;
  assignmentLabel: string;
  assignmentCount: number | null;
  lastModifiedAt: string | null;
}

export interface DeviceGroup {
  operatingSystem: string;
  compliance: DeviceComplianceBucket;
  complianceLabel: string;
  encryption: DeviceEncryptionBucket;
  encryptionLabel: string;
  activity: DeviceActivityBucket;
  activityLabel: string;
  deviceCount: number;
}

export interface DevicePostureCorrelation {
  deterministicCorrelationAvailable: boolean;
  devicesWithDirectoryId: number | null;
  explanation: string;
}

export interface DevicePostureView {
  state: DevicePostureState;
  source: string | null;
  configuration: DevicePostureDimension;
  assignment: DevicePostureDimension;
  devices: DevicePostureDimension;
  configurationSummary: DevicePostureConfigurationSummary;
  deviceSummary: DevicePostureDeviceSummary;
  policies: DevicePolicy[];
  deviceGroups: DeviceGroup[];
  correlation: DevicePostureCorrelation;
  affectsScore: boolean;
  scoreDisclaimer: string;
}

// ---- Lógica PURA (testável sem runner Angular) -----------------------------------------------------

/**
 * Rótulo pt-BR de um estado de dimensão. Um estado NÃO reconhecido (inclusive um futuro estado novo do
 * backend) cai no fallback HONESTO "Nunca coletada" — jamais em "Disponível".
 */
export function dimensionStatePt(state: string | null | undefined): string {
  switch (state) {
    case 'Available':
      return 'Disponível';
    case 'Partial':
      return 'Parcial';
    case 'NotAuthorized':
      return 'Bloqueada por permissão';
    case 'NotLicensed':
      return 'Indisponível por licença';
    case 'Unavailable':
      return 'Indisponível';
    default:
      return 'Nunca coletada';
  }
}

/**
 * Uma dimensão pode mostrar NÚMEROS? Só quando o backend afirmou ter dados armazenados. Sem isso, a tela
 * mostra o estado (bloqueada/indisponível/nunca coletada) — nunca um zero fabricado.
 */
export function canShowNumbers(dimension: Pick<DevicePostureDimension, 'hasData'> | null | undefined): boolean {
  return dimension?.hasData === true;
}

/**
 * Formata um número que pode ser "não coletado". `null`/`undefined` viram o traço de ausência — jamais "0".
 * É a última barreira contra a tela afirmar "0 dispositivos não conformes" sem ter coletado nada.
 */
export function countOrDash(value: number | null | undefined): string {
  return typeof value === 'number' ? String(value) : '—';
}

/** Filtros da lista de dispositivos. `null` = "todos" naquele eixo. */
export interface DeviceGroupFilters {
  compliance: DeviceComplianceBucket | null;
  operatingSystem: string | null;
  activity: DeviceActivityBucket | null;
  encryption: DeviceEncryptionBucket | null;
}

export const EMPTY_DEVICE_FILTERS: DeviceGroupFilters = {
  compliance: null,
  operatingSystem: null,
  activity: null,
  encryption: null,
};

/** Aplica os quatro eixos de filtro (conjunção). Um eixo nulo não restringe nada. */
export function filterDeviceGroups(groups: DeviceGroup[], filters: DeviceGroupFilters): DeviceGroup[] {
  return groups.filter(
    (g) =>
      (filters.compliance === null || g.compliance === filters.compliance) &&
      (filters.operatingSystem === null || g.operatingSystem === filters.operatingSystem) &&
      (filters.activity === null || g.activity === filters.activity) &&
      (filters.encryption === null || g.encryption === filters.encryption),
  );
}

/** Soma de dispositivos de um conjunto de grupos (a lista já filtrada, por exemplo). */
export function totalDevices(groups: DeviceGroup[]): number {
  return groups.reduce((sum, g) => sum + g.deviceCount, 0);
}

/** Valores distintos de sistema operacional presentes, em ordem determinística — alimenta o filtro. */
export function operatingSystems(groups: DeviceGroup[]): string[] {
  return [...new Set(groups.map((g) => g.operatingSystem))].sort((a, b) => a.localeCompare(b, 'pt-BR'));
}

/**
 * Políticas comprovadamente SEM atribuição. Só conta o que o backend afirmou objetivamente: uma política com
 * atribuição desconhecida NUNCA entra aqui (ausência de dado não é prova de ausência de alcance).
 */
export function unassignedPolicies(policies: DevicePolicy[]): DevicePolicy[] {
  return policies.filter((p) => p.assignmentState === 'Unassigned');
}

/** Políticas cuja atribuição a fonte não permitiu afirmar — exibidas SEPARADAMENTE das "sem atribuição". */
export function unknownAssignmentPolicies(policies: DevicePolicy[]): DevicePolicy[] {
  return policies.filter((p) => p.assignmentState === 'Unknown');
}
