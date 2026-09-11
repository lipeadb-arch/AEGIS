/** Modelo do inventário contínuo de ativos (pilar Identify / ID.AM do NIST CSF 2.0). */

/** Verticais de ativo do NIST CSF 2.0 (serializadas pela API como o nome do enum). */
export type AssetCategory =
  | 'Hardware'
  | 'Software'
  | 'Data'
  | 'People'
  | 'Facilities'
  | 'SupplyChain';

/** Nível de risco calculado pelo motor de IA (nulo enquanto não avaliado). */
export type RiskLevel = 'Baixo' | 'Medio' | 'Alto' | 'Critico';

/** Espelha o AssetDto do backend (Contracts/Dtos.cs). */
export interface AssetDto {
  id: string;
  name: string;
  category: AssetCategory;
  subType: string | null;
  description: string | null;
  criticality: number;
  ownerName: string | null;
  externalRef: string | null;
  businessProcessId: string | null;
  discoverySource: string;
  lastSeenAt: string | null;
  isActive: boolean;
  riskScore: number | null;
  riskLevel: RiskLevel | null;
  riskScoredAt: string | null;
  createdAt: string;
  /** [AEGIS-ENTITY-RESOLUTION-01] Nome provisório: a fonte não coleta nome (ex.: Intune). */
  nameIsPlaceholder?: boolean;
  /** [AEGIS-ENTITY-RESOLUTION-01] Resumo das fontes e do vínculo entre elas (sem identificadores técnicos). */
  sources?: AssetSourceSummary | null;
}

// ---- [AEGIS-ENTITY-RESOLUTION-01] Fontes do ativo e vínculo entre elas ----------------------------------------
// O backend é a AUTORIDADE do estado e do texto (AssetCrossSourceNarrative); aqui só há apresentação: nenhum
// estado é inferido no cliente e ausência de resumo nunca vira "0 fontes".

/** Estado do vínculo entre fontes (espelha AssetCrossSourceStates do backend). */
export type CrossSourceState = 'linked' | 'identifierOnly' | 'notLinked' | 'conflict' | 'notEvaluated' | 'noSource';

export interface AssetSourceSummary {
  activeSourceCount: number;
  activeSources: string[];
  sourceRecords: number;
  crossSourceState: CrossSourceState;
  crossSourceLabel: string;
  lastObservedAt: string | null;
}

/** Detalhe técnico — só vem da API para o administrador do tenant. */
export interface AssetSourceDiagnostics {
  externalId: string;
  directoryNamespace: string | null;
  directoryDeviceId: string | null;
  conflictDirectoryDeviceId: string | null;
  /** Diretório da observação contraditória — separado do vínculo estabelecido; nulo = não registrado. */
  conflictDirectoryNamespace: string | null;
  /** Identificadores contraditórios trazidos pela MESMA coleta para o registro (vazio fora dessa situação). */
  conflictObservedDeviceIds: string[];
  linkedAt: string | null;
  resolutionEvaluatedAt: string | null;
}

/** Par (diretório, identificador) da observação que contradiz o vínculo — mostrado à parte do vínculo estabelecido. */
export interface ConflictObservation {
  /** Diretório observado; "não registrado" quando o conflito é anterior ao registro do diretório (nada é inventado). */
  directory: string;
  directoryRecorded: boolean;
  identifier: string;
}

/**
 * Observação conflitante do diagnóstico restrito, ou nulo quando não há par observado (inclui a contradição na mesma
 * coleta, que traz a LISTA de identificadores e não um par). Diagnóstico ausente (papel sem acesso) = nulo.
 */
export function conflictObservation(diag: AssetSourceDiagnostics | null | undefined): ConflictObservation | null {
  if (!diag?.conflictDirectoryDeviceId) return null;
  const recorded = !!diag.conflictDirectoryNamespace;
  return {
    directory: recorded ? diag.conflictDirectoryNamespace! : 'não registrado',
    directoryRecorded: recorded,
    identifier: diag.conflictDirectoryDeviceId,
  };
}

/** Identificadores contraditórios da mesma coleta; vazio quando não há (ou quando a API antiga não envia o campo). */
export function contradictoryObservedIds(diag: AssetSourceDiagnostics | null | undefined): string[] {
  return diag?.conflictObservedDeviceIds ?? [];
}

export interface AssetSourceRecord {
  sourceLabel: string;
  isActive: boolean;
  presenceLabel: string;
  firstObservedAt: string;
  lastObservedAt: string;
  sourceLastSeenAt: string | null;
  noLongerObservedSince: string | null;
  observedName: string | null;
  platform: string | null;
  resolutionState: string;
  resolutionLabel: string;
  resolutionExplanation: string;
  identifierStatus: string;
  identifierStatusLabel: string;
  conflictKind: string | null;
  relatedAssetId: string | null;
  relatedAssetName: string | null;
  sourceCompliance: string | null;
  sourceComplianceLabel: string | null;
  sourceEncryption: string | null;
  sourceEncryptionLabel: string | null;
  diagnostics: AssetSourceDiagnostics | null;
}

export interface AssetSources {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  crossSourceState: CrossSourceState;
  crossSourceLabel: string;
  explanation: string;
  linkMeaning: string;
  directoryNote: string;
  curatedNote: string;
  activeSourceCount: number;
  sourceRecords: number;
  truncated: boolean;
  diagnosticsIncluded: boolean;
  sources: AssetSourceRecord[];
}

export type SourceTone = 'ok' | 'info' | 'warn' | 'muted';

/** Dica curta da coluna — o texto completo vem do backend no detalhe. */
export const LINK_MEANING_SHORT =
  'Vínculo confirmado: os registros das fontes se referem ao mesmo dispositivo. Não confirma a segurança do dispositivo.';

/** Tom visual do vínculo. Conflito é aviso; falta de evidência NÃO é conflito (tom neutro). */
export function crossSourceTone(state: CrossSourceState | string): SourceTone {
  switch (state) {
    case 'linked':
      return 'ok';
    case 'identifierOnly':
      return 'info';
    case 'conflict':
      return 'warn';
    default:
      return 'muted';
  }
}

/** Tom de UM registro de fonte (estado do backend: Linked, Conflict, NoIdentifier…). */
export function recordResolutionTone(state: string): SourceTone {
  if (state === 'Linked') return 'ok';
  if (state === 'Conflict') return 'warn';
  return 'muted';
}

/** Nome curto da fonte para a coluna; rótulo desconhecido passa como veio. */
export function shortSourceLabel(label: string): string {
  if (/defender/i.test(label)) return 'Defender';
  if (/intune/i.test(label)) return 'Intune';
  return label;
}

export interface SourcesCell {
  text: string;
  badge: string | null;
  tone: SourceTone;
  title: string | null;
}

/** Célula "Fontes · vínculo" da listagem. Sem resumo da API = desconhecido ("—"), nunca "0 fontes". */
export function sourcesCell(summary: AssetSourceSummary | null | undefined): SourcesCell {
  if (!summary) return { text: '—', badge: null, tone: 'muted', title: null };
  if (summary.crossSourceState === 'noSource')
    return { text: 'Sem fonte integrada', badge: null, tone: 'muted', title: null };
  const names = summary.activeSources.map(shortSourceLabel);
  return {
    text: names.length > 0 ? names.join(' + ') : 'Nenhuma fonte na última leitura',
    badge: summary.crossSourceLabel,
    tone: crossSourceTone(summary.crossSourceState),
    title: summary.crossSourceState === 'linked' ? LINK_MEANING_SHORT : null,
  };
}

/** Junta fatos informados pela fonte, ignorando ausentes. */
export function joinSourceFacts(...labels: (string | null | undefined)[]): string {
  return labels.filter((l): l is string => !!l && l.trim().length > 0).join(' · ');
}

/** Envelope de paginação genérico (espelha PagedResult<T>). */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** Filtros combinados da grid tática (todos por AND; categorias por OR entre si). */
export interface AssetQuery {
  category?: AssetCategory[];
  riskLevel?: RiskLevel | null;
  criticality?: number | null;
  isActive?: boolean | null;
  search?: string;
  page: number;
  pageSize: number;
}

/** Rótulos PT das verticais NIST para os chips de filtro e a coluna Categoria. */
export const ASSET_CATEGORIES: ReadonlyArray<{ value: AssetCategory; label: string }> = [
  { value: 'Hardware', label: 'Hardware' },
  { value: 'Software', label: 'Software' },
  { value: 'Data', label: 'Dados' },
  { value: 'People', label: 'Pessoas' },
  { value: 'Facilities', label: 'Instalações' },
  { value: 'SupplyChain', label: 'Cadeia de Supr.' },
];

const CATEGORY_LABELS = new Map<string, string>(ASSET_CATEGORIES.map((c) => [c.value, c.label]));

/** Rótulo PT de uma categoria (fallback: o próprio valor). */
export function categoryLabel(value: string): string {
  return CATEGORY_LABELS.get(value) ?? value;
}

export const RISK_LEVELS: ReadonlyArray<RiskLevel> = ['Baixo', 'Medio', 'Alto', 'Critico'];
