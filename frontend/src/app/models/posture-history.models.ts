// Modelos da tela de HISTÓRICO auditável de postura — espelham /api/v1/posture/snapshots (camelCase). A
// fotografia é IMUTÁVEL e o score é o do INSTRUMENTO (AEGIS Score/NIST OU KNIGHT), nunca combinado. Funções
// PURAS de apresentação (pt-BR). "Não avaliado" é SEMPRE distinto de 0 e de NonCompliant.

export type PostureSnapshotType = 'AegisScoreNist' | 'Knight';

/** Referência sanitizada de evidência (só metadado; nunca conteúdo bruto). */
export interface PostureEvidenceRef {
  kind: string; // 'telemetry' | 'document'
  source: string;
  reference: string;
  collectedAt: string | null;
}

/** Controle NIST congelado — do catálogo ATIVO completo. `evaluated=false` ⇒ status/veredito/instante nulos. */
export interface PostureSnapshotControl {
  subcategoryCode: string;
  functionCode: string;
  evaluated: boolean;
  status: string | null; // 'Compliant' | 'NonCompliant' | 'MitigatedByThirdParty' | null (não avaliado)
  achievedPoints: number;
  maxPoints: number;
  verdictSource: string | null;
  evaluatedAt: string | null;
  evidenceRefs: PostureEvidenceRef[];
}

/** Indicador KNIGHT congelado. */
export interface PostureSnapshotIndicator {
  indicatorId: string;
  title: string;
  category: string;
  severity: string;
  status: string;
  evidence: string;
  affectedObjectCount: number;
  nistCodes: string[];
  mitreTechniques: string[];
  sourceType: string;
  collectedAt: string;
}

export interface PostureSnapshotSummary {
  id: string;
  type: PostureSnapshotType;
  schemaVersion: string;
  formulaVersion: string;
  catalogVersion: string;
  semanticFamily: string;
  sourceType: string | null;
  sourceLabel: string | null;
  capturedAt: string;
  evaluationState: string; // 'Evaluated' | 'NotEvaluated'
  score: number | null; // null = sem avaliação (NUNCA 0 por ausência)
  coverage: number;
  evaluatedItems: number;
  eligibleItems: number;
  compliantCount: number;
  nonCompliantCount: number;
  mitigatedCount: number;
  notEvaluatedCount: number;
  errorCount: number;
  notApplicableCount: number;
  dataRecency: string | null;
  contentHash: string;
}

export interface PostureSnapshotDetail {
  summary: PostureSnapshotSummary;
  achievedPoints: number;
  possiblePoints: number;
  eligiblePoints: number;
  controls: PostureSnapshotControl[];
  indicators: PostureSnapshotIndicator[];
}

export interface PostureItemChange {
  code: string;
  title: string;
  previousStatus: string;
  currentStatus: string;
}

export interface PostureCountDelta {
  compliant: number;
  nonCompliant: number;
  mitigated: number;
  notEvaluated: number;
  error: number;
  notApplicable: number;
}

export interface PostureComparisonDelta {
  scoreDelta: number | null;
  scoreDeltaState: string; // 'Numeric' | 'BecameEvaluated' | 'BecameUnevaluated' | 'BothUnevaluated'
  coverageDelta: number;
  counts: PostureCountDelta;
  improved: PostureItemChange[];
  worsened: PostureItemChange[];
  nowEvaluated: PostureItemChange[];
  noLongerEvaluated: PostureItemChange[];
}

/** Compatível ⇒ delta; incompatível ⇒ motivos (sem delta enganoso). */
export interface PostureComparisonResult {
  compatible: boolean;
  incompatibilityReasons: string[];
  previous: PostureSnapshotSummary | null;
  current: PostureSnapshotSummary | null;
  delta: PostureComparisonDelta | null;
}

export interface PublishPostureSnapshotRequest {
  type: PostureSnapshotType;
  source?: string | null;
}

// ---- Apresentação (pt-BR) -----------------------------------------------------------------------

export function snapshotTypeLabel(type: PostureSnapshotType): string {
  return type === 'Knight' ? 'AEGIS KNIGHT' : 'AEGIS Score / NIST';
}

const CONTROL_STATUS_LABEL: Record<string, string> = {
  Compliant: 'Conforme',
  NonCompliant: 'Não conforme',
  MitigatedByThirdParty: 'Mitigado por terceiro',
};

const KNIGHT_STATUS_LABEL: Record<string, string> = {
  Passed: 'Conforme',
  Exposed: 'Exposto',
  Mitigated: 'Mitigado',
  NotEvaluated: 'Não avaliado',
  Error: 'Erro',
  NotApplicable: 'Não aplicável',
};

/** Rótulo de um status de item (controle NIST ou indicador KNIGHT). `null`/"NotEvaluated"/"—" ⇒ "Não avaliado". */
export function itemStatusLabel(status: string | null): string {
  if (!status || status === 'NotEvaluated') return 'Não avaliado';
  if (status === '—') return '—';
  return CONTROL_STATUS_LABEL[status] ?? KNIGHT_STATUS_LABEL[status] ?? status;
}

/** Classe CSS por status (compartilhada por controle/indicador) para a cor do chip. */
export function itemStatusClass(status: string | null): string {
  if (!status || status === 'NotEvaluated' || status === 'NotApplicable') return 'mute';
  if (status === 'Compliant' || status === 'Passed') return 'ok';
  if (status === 'NonCompliant' || status === 'Exposed') return 'bad';
  if (status === 'MitigatedByThirdParty' || status === 'Mitigated') return 'warn';
  if (status === 'Error') return 'err';
  return 'mute';
}

const VERDICT_SOURCE_LABEL: Record<string, string> = {
  Telemetry: 'Telemetria',
  Documentary: 'Documental',
};

export function verdictSourceLabel(source: string | null): string {
  return source ? VERDICT_SOURCE_LABEL[source] ?? source : '—';
}

export function evidenceKindLabel(kind: string): string {
  return kind === 'document' ? 'Documento' : kind === 'telemetry' ? 'Telemetria' : kind;
}

const INCOMPATIBILITY_LABEL: Record<string, string> = {
  DifferentType: 'Instrumentos diferentes (AEGIS Score × KNIGHT)',
  DifferentSemanticFamily: 'Famílias semânticas diferentes (fonte/framework)',
  DifferentFormulaVersion: 'Versões de fórmula incompatíveis',
  DifferentCatalogVersion: 'Versões de catálogo/framework incompatíveis',
  DifferentSchemaVersion: 'Versões de schema da fotografia incompatíveis',
};

export function incompatibilityLabel(reason: string): string {
  return INCOMPATIBILITY_LABEL[reason] ?? reason;
}

const SCORE_DELTA_STATE_LABEL: Record<string, string> = {
  Numeric: 'Variação numérica',
  BecameEvaluated: 'Passou a ser avaliado',
  BecameUnevaluated: 'Deixou de ser avaliado',
  BothUnevaluated: 'Sem avaliação nos dois',
};

export function scoreDeltaStateLabel(state: string): string {
  return SCORE_DELTA_STATE_LABEL[state] ?? state;
}

/** Score para exibição: "—" quando null (sem avaliação), nunca "0". */
export function scoreDisplay(score: number | null): string {
  return score === null ? '—' : String(Math.round(score));
}

/** Delta com sinal explícito (ex.: "+3,2" / "-1,0"); "—" quando não há número. */
export function signedDelta(value: number | null): string {
  if (value === null) return '—';
  const rounded = Math.round(value * 10) / 10;
  return (rounded > 0 ? '+' : '') + rounded.toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}
