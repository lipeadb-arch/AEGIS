// [AEGIS-NIST-JOURNEY-01] Modelos do AEGIS NIST — espelham /api/v1/nist/assessments (camelCase) — e funções PURAS de
// apresentação (pt-BR). Dois conceitos que esta camada mantém separados:
//   • MATURIDADE atual × alvo por avaliação (escala 1–5 autoral do AEGIS, aegis-methodology-v1);
//   • score de POSTURA do ambiente (aegis-score-v1, AEGIS Score) — lido como contexto, nunca somado à maturidade.
// Ausência de nível é ausência de avaliação: nada aqui converte nulo em zero.
// [AEGIS-NIST-JOURNEY-02] A RODADA é parte da identidade do contexto (avaliação → rodada → escopo): seleção, links,
// requisições e memória de navegação a carregam explicitamente; conteúdo herdado ou importado aguarda confirmação humana.

import { NIST_CATEGORY_NAMES } from './nist-glossary';
import type { FrozenPostureHistory } from './posture-history.models';

export type NistFunctionCode = 'GV' | 'ID' | 'PR' | 'DE' | 'RS' | 'RC';

export type NistSubcategoryState = 'NotEvaluated' | 'InProgress' | 'PendingConfirmation' | 'Evaluated' | 'NotApplicable';

/** Estados na ordem de leitura (gráficos e filtros). */
export const NIST_STATES: readonly NistSubcategoryState[] = ['Evaluated', 'NotApplicable', 'PendingConfirmation', 'InProgress', 'NotEvaluated'];

export interface NistFunctionMeta {
  code: NistFunctionCode;
  /** Segmento de URL (minúsculo): /nist/gv. */
  slug: string;
  /** Nome oficial em inglês (CSF 2.0). */
  name: string;
  /** Nome em português. */
  label: string;
}

/** As seis funções do NIST CSF 2.0, na ordem oficial. Fonte ÚNICA do menu, das telas e dos dashboards. */
export const NIST_FUNCTIONS: readonly NistFunctionMeta[] = [
  { code: 'GV', slug: 'gv', name: 'Govern', label: 'Governar' },
  { code: 'ID', slug: 'id', name: 'Identify', label: 'Identificar' },
  { code: 'PR', slug: 'pr', name: 'Protect', label: 'Proteger' },
  { code: 'DE', slug: 'de', name: 'Detect', label: 'Detectar' },
  { code: 'RS', slug: 'rs', name: 'Respond', label: 'Responder' },
  { code: 'RC', slug: 'rc', name: 'Recover', label: 'Recuperar' },
];

/** Função pelo segmento da URL ou pelo código ("gv", "GV"); nula quando desconhecida. */
export function nistFunctionBySlug(value: string | null | undefined): NistFunctionMeta | null {
  const v = (value ?? '').trim().toLowerCase();
  return NIST_FUNCTIONS.find((f) => f.slug === v) ?? null;
}

/** "Govern — Governar". */
export function nistFunctionTitle(f: NistFunctionMeta): string {
  return `${f.name} — ${f.label}`;
}

/** Nome da categoria em português (humanização existente), com o código oficial como reserva. */
export function nistCategoryLabel(code: string, officialName?: string): string {
  return NIST_CATEGORY_NAMES[code] ?? (officialName ? officialName.replace(/\s*\([A-Z]{2}\.[A-Z]{2}\)\s*$/, '') : code);
}

// ---- Escala de maturidade (metodologia AEGIS) ---------------------------------------------------------------

/** Rótulos em português da escala 1–5 do AEGIS (não são os Implementation Tiers do NIST). */
export const MATURITY_LEVEL_LABELS: Record<number, string> = {
  1: 'Executado',
  2: 'Documentado',
  3: 'Gerenciado',
  4: 'Medido',
  5: 'Otimizado',
};

/** Descrição curta em português de cada nível, para a escolha do analista. */
export const MATURITY_LEVEL_HINTS: Record<number, string> = {
  1: 'A prática acontece de forma pontual, sem padrão.',
  2: 'A prática está documentada e pode ser repetida.',
  3: 'Há revisão da aderência e recursos para manter a prática.',
  4: 'A eficácia é medida; desvios geram correção e são reportados à gestão.',
  5: 'A prática é padronizada, otimizada e melhorada continuamente.',
};

/** "2 · Documentado"; ausente → "Sem nível" (nunca 0). */
export function levelLabel(level: number | null | undefined): string {
  if (level === null || level === undefined) return 'Sem nível';
  return `${level} · ${MATURITY_LEVEL_LABELS[level] ?? ''}`.trim();
}

/** Média de nível com uma casa ("2,5"); ausente → "—". */
export function averageText(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  return value.toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

/** Lacuna com sinal ("+2" / "0" / "−1"); indeterminada → "Indeterminada". */
export function gapText(gap: number | null | undefined): string {
  if (gap === null || gap === undefined) return 'Indeterminada';
  const n = Math.round(gap * 10) / 10;
  const s = n.toLocaleString('pt-BR', { maximumFractionDigits: 1 });
  return n > 0 ? `+${s}` : n < 0 ? `−${s.replace('-', '')}` : '0';
}

export function stateLabel(state: NistSubcategoryState | string): string {
  switch (state) {
    case 'Evaluated':
      return 'Avaliada';
    case 'InProgress':
      return 'Em andamento';
    case 'PendingConfirmation':
      return 'Aguarda confirmação';
    case 'NotApplicable':
      return 'Não se aplica';
    default:
      return 'Não avaliada';
  }
}

export function stateBadgeClass(state: NistSubcategoryState | string): string {
  switch (state) {
    case 'Evaluated':
      return 'info';
    case 'InProgress':
      return 'warn';
    case 'PendingConfirmation':
      return 'violet';
    case 'NotApplicable':
      return 'neutral';
    default:
      return 'neutral';
  }
}

// ---- Contratos da API -------------------------------------------------------------------------------------------

export type NistPeriodKind = 'Monthly' | 'Quarterly' | 'Other';
export type NistCycleStatus = 'Open' | 'Closed';
export type NistSeedMode = 'None' | 'Reference' | 'Draft';
export type NistContentOrigin = 'Analyst' | 'CarriedForward' | 'Imported';
export type NistReviewState = 'None' | 'Approved' | 'ChangesRequested' | 'Outdated';

/** [AEGIS-NIST-JOURNEY-02] Uma rodada da avaliação (mais recente primeiro). */
export interface NistCycle {
  id: string;
  name: string;
  periodKind: NistPeriodKind;
  periodStart: string;
  periodEnd: string;
  status: NistCycleStatus;
  seedFromCycleId: string | null;
  seedFromCycleName: string | null;
  seedMode: NistSeedMode;
  createdAt: string;
  createdByName: string | null;
  closedAt: string | null;
  closedByName: string | null;
  version: number;
  publications: number;
}

export interface NistScope {
  id: string;
  name: string;
  description: string | null;
  subcategories: number;
  evaluated: number;
  notApplicable: number;
  inProgress: number;
  withEvidence: number;
  lastReviewedAt: string | null;
  /** Rodada a que se referem os números acima (a mais recente) — nunca a rodada selecionada por suposição. */
  cycleId?: string | null;
  pendingConfirmation?: number;
}

export interface NistAssessment {
  id: string;
  name: string;
  description: string | null;
  status: string;
  startDate: string | null;
  endDate: string | null;
  methodologyVersion: string;
  frameworkName: string;
  createdAt: string;
  lastReviewedAt: string | null;
  scopes: NistScope[];
  cycles?: NistCycle[] | null;
  progressCycleId?: string | null;
}

export interface NistProfileScore {
  code: string;
  current: number | null;
  target: number | null;
  gap: number | null;
  subcategories: number;
  withCurrent: number;
  withTarget: number;
  withGap: number;
  notApplicable: number;
  evaluated: number;
}

export interface NistSubcategoryRow {
  code: string;
  title: string;
  state: NistSubcategoryState;
  currentLevel: number | null;
  targetLevel: number | null;
  gap: number | null;
  ownerName: string | null;
  evidenceCount: number;
  reviewedAt: string | null;
  reviewState?: NistReviewState;
  proceduresPlanned?: number;
  proceduresPerformed?: number;
  openFindings?: number;
  assessorName?: string | null;
  reviewerName?: string | null;
}

export interface NistCategory {
  code: string;
  name: string;
  definition: string;
  profile: NistProfileScore;
  subcategories: NistSubcategoryRow[];
}

export interface NistFunctionView {
  assessmentId: string;
  scopeId: string;
  cycleId?: string | null;
  code: NistFunctionCode;
  name: string;
  definition: string;
  profile: NistProfileScore;
  categories: NistCategory[];
}

export interface NistGap {
  code: string;
  title: string;
  currentLevel: number;
  targetLevel: number;
  gap: number;
  ownerName: string | null;
  improvementGuidance: string | null;
  openFindings?: number;
  riskImpact?: string | null;
}

export interface NistStateCounts {
  code: string;
  subcategories: number;
  notEvaluated: number;
  inProgress: number;
  pendingConfirmation: number;
  evaluated: number;
  notApplicable: number;
  reviewApproved: number;
  reviewOutdated: number;
}

export interface NistProcedureProgress {
  method: NistTestMethod;
  planned: number;
  inProgress: number;
  performed: number;
  notPerformed: number;
  satisfactory: number;
  partiallySatisfactory: number;
  unsatisfactory: number;
  inconclusive: number;
}

export interface NistTreatment {
  findingsOpen: number;
  findingsRiskAccepted: number;
  findingsClosed: number;
  openBySeverity: Record<string, number>;
  findingsWithoutPlan: number;
  plansOpen: number;
  plansInProgress: number;
  plansAwaitingValidation: number;
  plansCompleted: number;
  plansOverdue: number;
}

export interface NistProfile {
  assessmentId: string;
  scopeId: string;
  cycleId?: string | null;
  methodologyVersion: string;
  overall: NistProfileScore;
  functions: NistProfileScore[];
  categories: NistProfileScore[];
  gaps: NistGap[];
  indeterminateGaps: number;
  states?: NistStateCounts | null;
  functionStates?: NistStateCounts[] | null;
  procedures?: NistProcedureProgress[] | null;
  treatment?: NistTreatment | null;
}

export type NistResponsibleKind = 'User' | 'External' | 'Text' | 'None';

export interface NistResponsible {
  name: string | null;
  userId: string | null;
  kind: NistResponsibleKind;
  contact: string | null;
  userActive: boolean;
}

export interface NistEvaluation {
  id: string;
  state: NistSubcategoryState;
  currentLevel: number | null;
  targetLevel: number | null;
  gap: number | null;
  notApplicable: boolean;
  currentComments: string | null;
  targetComments: string | null;
  rationale: string | null;
  gaps: string | null;
  riskImpact: string | null;
  improvementGuidance: string | null;
  ownerName: string | null;
  evaluatedBy: string;
  reviewedByName: string | null;
  reviewedAt: string | null;
  version: number;
  contentOrigin?: NistContentOrigin;
  originNote?: string | null;
  humanConfirmed?: boolean;
  owner?: NistResponsible | null;
  assessorUserId?: string | null;
  assessorName?: string | null;
  reviewerUserId?: string | null;
  reviewerName?: string | null;
  reviewState?: NistReviewState;
  reviewDecisionByName?: string | null;
  reviewDecisionAt?: string | null;
  reviewDecisionNote?: string | null;
  /** [AEGIS-NIST-AI-ASSIST-01] Campos cujo texto VIGENTE veio de uma sugestão da IA (editado ou não). */
  assistedFields?: NistAssistedField[] | null;
}

/**
 * [AEGIS-NIST-AI-ASSIST-01] Procedência de um campo: o texto vigente veio de uma geração (motor real ou simulado), incorporado por
 * uma pessoa pela gravação normal — nunca é aprovação nem revisão.
 */
export interface NistAssistedField {
  field: string;
  label: string;
  assistanceId: string;
  mode: 'Real' | 'Simulated' | string;
  generatedAt: string;
  requestedByName: string | null;
  incorporatedByName: string | null;
  incorporatedAt: string;
  edited: boolean;
  staleAcknowledged: boolean;
}

/** [AEGIS-NIST-AI-ASSIST-01] Referência à sugestão de origem do conteúdo aplicado, enviada com a gravação normal. */
export interface NistAssistanceRef {
  assistanceId: string;
  fields: string[];
  acknowledgeStale: boolean;
}

export type NistEvidenceOrigin = 'Manual' | 'GovernanceDocument' | 'KnightIndicator' | 'AssetInventory';

export interface NistEvidence {
  id: string;
  originKind: NistEvidenceOrigin;
  type: string;
  title: string;
  notes: string | null;
  uri: string | null;
  originRef: string | null;
  originLabel: string | null;
  originScope: string | null;
  collectedAt: string;
  linkedAt: string;
  recordedByName: string | null;
}

export interface NistAvailableEvidence {
  originKind: NistEvidenceOrigin;
  title: string;
  originLabel: string | null;
  originScope: string | null;
  collectedAt: string | null;
  status: string | null;
  criterion: string;
  limitation: string | null;
  documentId: string | null;
  knightRunId: string | null;
  knightIndicatorId: string | null;
  isDemo: boolean;
  alreadyLinked: boolean;
}

export interface NistPostureReading {
  status: string;
  verdictSource: string;
  achievedPoints: number;
  maxPoints: number;
  lastEvaluatedAt: string;
}

export interface NistMaturityLevel {
  level: number;
  name: string;
  description: string;
}

/** A mesma subcategoria na rodada de ORIGEM — referência de leitura, nunca a avaliação desta rodada. */
export interface NistCycleReference {
  cycleId: string;
  cycleName: string;
  state: NistSubcategoryState;
  currentLevel: number | null;
  targetLevel: number | null;
  notApplicable: boolean;
  rationale: string | null;
  gaps: string | null;
  reviewedByName: string | null;
  reviewedAt: string | null;
  evidenceCount: number;
  proceduresPerformed: number;
  findings: number;
}

export interface NistSubcategoryDetail {
  assessmentId: string;
  scopeId: string;
  cycleId?: string | null;
  cycleName?: string | null;
  cycleStatus?: NistCycleStatus | null;
  code: string;
  functionCode: NistFunctionCode;
  functionName: string;
  categoryCode: string;
  categoryName: string;
  title: string;
  summary: string;
  impact: string;
  initialAction: string;
  officialOutcome: string;
  implementationExamples: string | null;
  evaluation: NistEvaluation | null;
  evidence: NistEvidence[];
  availableEvidence: NistAvailableEvidence[];
  posture: NistPostureReading | null;
  maturityScale: NistMaturityLevel[];
  methodologyVersion: string;
  procedures?: NistProcedure[] | null;
  findings?: NistFinding[] | null;
  reference?: NistCycleReference | null;
  findingBlockedReason?: string | null;
}

export interface NistHistoryItem {
  assessmentId: string;
  assessmentName: string;
  scopeId: string;
  scopeName: string;
  methodologyVersion: string;
  referenceMonth: string;
  referenceCriterion: string;
  lastReviewedAt: string | null;
  subcategories: number;
  evaluated: number;
  notApplicable: number;
  current: number | null;
  target: number | null;
  cycleId?: string | null;
  cycleName?: string | null;
  periodStart?: string | null;
  periodEnd?: string | null;
}

export interface NistAssignee {
  userId: string;
  displayName: string;
  role: string;
}

export interface NistFieldChange {
  field: string;
  label: string;
  from: string | null;
  to: string | null;
}

export interface NistAuditEntry {
  id: string;
  at: string;
  actorName: string;
  subject: string;
  subjectId: string | null;
  action: string;
  summary: string;
  cycleId: string | null;
  scopeId: string | null;
  subcategoryCode: string | null;
  changes: NistFieldChange[];
}

// ---- Procedimentos, achados e planos ---------------------------------------------------------------------------

export type NistTestMethod = 'Examine' | 'Interview' | 'Test';
export type NistProcedureStatus = 'Planned' | 'InProgress' | 'Performed' | 'NotPerformed';
export type NistProcedureOutcome = 'Satisfactory' | 'PartiallySatisfactory' | 'Unsatisfactory' | 'Inconclusive';
export type NistFindingStatus = 'Open' | 'RiskAccepted' | 'Closed';
export type NistFindingSeverity = 'Critical' | 'High' | 'Medium' | 'Low';
export type NistFindingPriority = 'Urgent' | 'High' | 'Medium' | 'Low';
export type NistPlanStatus = 'Aberto' | 'EmAndamento' | 'AguardandoValidacao' | 'Concluido' | 'Vencido';

export interface NistProcedure {
  id: string;
  method: NistTestMethod;
  procedure: string;
  status: NistProcedureStatus;
  outcome: NistProcedureOutcome | null;
  observation: string | null;
  performedOn: string | null;
  resultRecordedByName: string | null;
  resultRecordedAt: string | null;
  evidenceIds: string[];
  contentOrigin: NistContentOrigin;
  originNote: string | null;
  createdByName: string | null;
  createdAt: string;
  version: number;
  /** [AEGIS-NIST-AI-ASSIST-01] O texto planejado veio de uma sugestão da IA. */
  assistedFrom?: NistAssistedField | null;
}

export interface NistFindingOrigin {
  schema: string;
  cycleName: string;
  scopeName: string;
  subcategoryCode: string;
  subcategoryTitle: string;
  evaluationVersion: number;
  currentLevel: number | null;
  targetLevel: number | null;
  gap: number | null;
  gapsText: string | null;
  procedureFindings: string[];
  recordedAt: string;
}

export interface NistPlanValidation {
  method: string;
  methodLabel: string;
  outcome: string;
  outcomeLabel: string;
  evidenceReference: string | null;
  rationale: string;
  decidedAt: string;
  decidedByName: string;
  appliesToCurrentCycle: boolean;
}

export interface NistPlanEvent {
  kind: string;
  at: string;
  actorName: string;
  fromStatus: string | null;
  toStatus: string | null;
  note: string | null;
}

export interface NistPlanOrigin {
  findingId: string;
  assessmentId: string;
  cycleId: string;
  scopeId: string;
  subcategoryCode: string;
}

export interface NistPlan {
  id: string;
  originKind: string;
  nistOrigin: NistPlanOrigin | null;
  title: string;
  proposedAction: string | null;
  responsiblePerson: string | null;
  responsibleUserId: string | null;
  responsibleIsExternal: boolean;
  responsibleContact: string | null;
  responsibleArea: string | null;
  dueDate: string | null;
  status: NistPlanStatus;
  statusLabel: string;
  isOverdue: boolean;
  isActive: boolean;
  nextStep: string;
  executionNotes: string | null;
  executionEvidenceRef: string | null;
  executedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  cycleStartedAt: string;
  wasReopened: boolean;
  version: number;
  allowedTransitions: NistPlanStatus[];
  closureBlockedReason: string | null;
  latestValidation: NistPlanValidation | null;
  applicableValidation: NistPlanValidation | null;
  events: NistPlanEvent[];
}

export interface NistFinding {
  id: string;
  assessmentId: string;
  cycleId: string;
  cycleName: string;
  scopeId: string;
  scopeName: string;
  subcategoryCode: string;
  subcategoryTitle: string;
  title: string;
  condition: string;
  risk: string;
  impact: string;
  severity: NistFindingSeverity;
  severityRationale: string;
  priority: NistFindingPriority;
  priorityRationale: string;
  recommendation: string;
  evidenceIds: string[];
  status: NistFindingStatus;
  statusNote: string | null;
  statusChangedAt: string | null;
  statusChangedByName: string | null;
  origin: NistFindingOrigin | null;
  createdByName: string | null;
  createdAt: string;
  version: number;
  plan: NistPlan | null;
  treatmentLabel: string;
  /** [AEGIS-NIST-AI-ASSIST-01] Recomendação e/ou ação do plano vindas de sugestão da IA. */
  assistedFields?: NistAssistedField[] | null;
}

// ---- Publicação, comparação e importação -------------------------------------------------------------------------

export interface NistReportSummary {
  current: number | null;
  target: number | null;
  gap: number | null;
  subcategories: number;
  evaluated: number;
  notApplicable: number;
  inProgress: number;
  pendingConfirmation: number;
  notEvaluated: number;
  coverage: number;
  withCurrent: number;
  withTarget: number;
  withGap: number;
  indeterminateGaps: number;
  reviewApproved: number;
  reviewChangesRequested: number;
  reviewOutdated: number;
  evidenceLinked: number;
  proceduresPlanned: number;
  proceduresPerformed: number;
  proceduresNotPerformed: number;
  findingsOpen: number;
  findingsRiskAccepted: number;
  findingsClosed: number;
  plansActive: number;
  plansCompleted: number;
  plansOverdue: number;
  lastReviewedAt: string | null;
}

export interface NistReportProfile {
  code: string;
  name: string;
  functionCode: string | null;
  current: number | null;
  target: number | null;
  gap: number | null;
  subcategories: number;
  withCurrent: number;
  withTarget: number;
  withGap: number;
  notApplicable: number;
  evaluated: number;
  inProgress: number;
  pendingConfirmation: number;
  notEvaluated: number;
}

export interface NistPublicationPreview {
  assessmentId: string;
  cycleId: string;
  scopeId: string;
  contentFingerprint: string;
  summary: NistReportSummary;
  functions: NistReportProfile[];
  findings: number;
  limitations: string[];
  warnings: string[];
  /**
   * [AEGIS-ASSESSMENT-VISUALS-01] Histórico mensal (mesma avaliação e escopo) que a publicação congelaria, com a impressão digital
   * PRÓPRIA — separada da do conteúdo avaliativo. A publicação devolve as duas.
   */
  history?: FrozenPostureHistory | null;
  /** [AEGIS-NIST-AI-ASSIST-01] O resumo executivo aceito entra nesta publicação. */
  interpretationIncluded?: boolean;
}

export interface NistPublication {
  snapshotId: string;
  assessmentId: string;
  cycleId: string;
  cycleName: string;
  scopeId: string;
  scopeName: string;
  capturedAt: string;
  contentHash: string;
  contentFingerprint: string;
  current: number | null;
  target: number | null;
  gap: number | null;
  coverage: number;
  evaluated: number;
  notApplicable: number;
  subcategories: number;
  publishedByName: string | null;
  methodologyVersion: string;
}

export interface NistFunctionDelta {
  code: string;
  name: string;
  baseCurrent: number | null;
  targetCurrent: number | null;
  currentDelta: number | null;
  baseTarget: number | null;
  targetTarget: number | null;
  baseEvaluated: number;
  targetEvaluated: number;
}

export interface NistSubcategoryChange {
  code: string;
  title: string;
  kind: string;
  description: string;
}

export interface NistCycleComparison {
  compatible: boolean;
  incompatibilityReasons: string[];
  baseCycleId: string;
  baseCycleName: string;
  targetCycleId: string;
  targetCycleName: string;
  currentDelta: number | null;
  targetDelta: number | null;
  coverageDelta: number;
  baseApplicable: number;
  targetApplicable: number;
  functions: NistFunctionDelta[];
  changes: NistSubcategoryChange[];
  notes: string[];
}

export type NistImportAction = 'Create' | 'Update' | 'Unchanged' | 'Error' | 'Conflict';

export interface NistImportRow {
  line: number;
  code: string | null;
  title: string | null;
  action: NistImportAction;
  messages: string[];
  changes: NistFieldChange[];
}

export interface NistImportPreview {
  token: string;
  fileName: string | null;
  rows: number;
  creates: number;
  updates: number;
  unchanged: number;
  errors: number;
  conflicts: number;
  canApply: boolean;
  items: NistImportRow[];
  fileErrors: string[];
  updateRule: string;
}

export interface NistImportResult {
  created: number;
  updated: number;
  unchanged: number;
  items: NistImportRow[];
}

// ---- Pedidos ----------------------------------------------------------------------------------------------------

export interface CreateNistAssessmentRequest {
  name: string;
  description?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  initialScopeName?: string | null;
  initialScopeDescription?: string | null;
  initialCycleName?: string | null;
  initialCyclePeriodKind?: NistPeriodKind | null;
  initialCyclePeriodStart?: string | null;
  initialCyclePeriodEnd?: string | null;
}

export interface CreateNistCycleRequest {
  name: string;
  periodKind: NistPeriodKind;
  periodStart: string;
  periodEnd: string;
  seedFromCycleId: string | null;
  seedMode: NistSeedMode;
}

export interface SaveNistEvaluationRequest {
  currentLevel: number | null;
  targetLevel: number | null;
  notApplicable: boolean;
  currentComments: string | null;
  targetComments: string | null;
  rationale: string | null;
  gaps: string | null;
  riskImpact: string | null;
  improvementGuidance: string | null;
  ownerName: string | null;
  expectedVersion: number;
  ownerUserId?: string | null;
  ownerIsExternal?: boolean;
  ownerContact?: string | null;
  assistance?: NistAssistanceRef | null;
}

export interface LinkNistEvidenceRequest {
  kind: NistEvidenceOrigin;
  documentId?: string | null;
  knightRunId?: string | null;
  knightIndicatorId?: string | null;
  title?: string | null;
  uri?: string | null;
  notes?: string | null;
  collectedOn?: string | null;
  manualType?: string | null;
}

export interface NistResponsibleRequest {
  userId: string | null;
  name: string | null;
  isExternal: boolean;
  contact: string | null;
}

export interface UpdateNistProcedureRequest {
  procedure: string | null;
  status: NistProcedureStatus;
  outcome: NistProcedureOutcome | null;
  observation: string | null;
  performedOn: string | null;
  evidenceIds: string[] | null;
  expectedVersion: number;
}

export interface NistPlanRequest {
  title: string;
  proposedAction: string | null;
  responsible: NistResponsibleRequest | null;
  responsibleArea: string | null;
  dueDate: string | null;
  assistance?: NistAssistanceRef | null;
}

/** Edição do achado (campos nulos permanecem). */
export interface UpdateNistFindingRequest {
  title?: string | null;
  condition?: string | null;
  risk?: string | null;
  impact?: string | null;
  severity?: string | null;
  severityRationale?: string | null;
  priority?: string | null;
  priorityRationale?: string | null;
  recommendation?: string | null;
  evidenceIds?: string[] | null;
  expectedVersion: number;
  assistance?: NistAssistanceRef | null;
}

export interface CreateNistFindingRequest {
  title: string;
  condition: string;
  risk: string;
  impact: string;
  severity: NistFindingSeverity;
  severityRationale: string;
  priority: NistFindingPriority;
  priorityRationale: string;
  recommendation: string;
  evidenceIds: string[] | null;
  plan: NistPlanRequest | null;
}

export interface UpdateNistPlanRequest {
  expectedVersion: number;
  title?: string | null;
  proposedAction?: string | null;
  responsible?: NistResponsibleRequest | null;
  responsibleArea?: string | null;
  dueDate?: string | null;
  status?: NistPlanStatus | null;
}

// ---- Formulário da subcategoria (puro) ---------------------------------------------------------------------------

/** Como o responsável pela prática é identificado: usuário do tenant, contato externo ou texto (legado). */
export type NistOwnerMode = 'none' | 'user' | 'external' | 'text';

export interface NistEvaluationDraft {
  currentLevel: number | null;
  targetLevel: number | null;
  notApplicable: boolean;
  currentComments: string;
  targetComments: string;
  rationale: string;
  gaps: string;
  riskImpact: string;
  improvementGuidance: string;
  ownerName: string;
  ownerMode: NistOwnerMode;
  ownerUserId: string;
  ownerContact: string;
}

export function draftFrom(e: NistEvaluation | null): NistEvaluationDraft {
  const owner = e?.owner ?? null;
  const mode: NistOwnerMode =
    owner?.kind === 'User' ? 'user' : owner?.kind === 'External' ? 'external' : (e?.ownerName ?? '').trim() !== '' ? 'text' : 'none';
  return {
    currentLevel: e?.currentLevel ?? null,
    targetLevel: e?.targetLevel ?? null,
    notApplicable: e?.notApplicable ?? false,
    currentComments: e?.currentComments ?? '',
    targetComments: e?.targetComments ?? '',
    rationale: e?.rationale ?? '',
    gaps: e?.gaps ?? '',
    riskImpact: e?.riskImpact ?? '',
    improvementGuidance: e?.improvementGuidance ?? '',
    ownerName: mode === 'user' ? '' : e?.ownerName ?? owner?.name ?? '',
    ownerMode: mode,
    ownerUserId: owner?.kind === 'User' ? owner.userId ?? '' : '',
    ownerContact: owner?.kind === 'External' ? owner.contact ?? '' : '',
  };
}

/** A mais recente entre duas leituras da mesma avaliação (pela versão): uma resposta atrasada não rebaixa a exibida. */
export function newerEvaluation(known: NistEvaluation | null, incoming: NistEvaluation | null): NistEvaluation | null {
  if (!known) return incoming;
  if (!incoming) return known;
  return incoming.version >= known.version ? incoming : known;
}

/**
 * Pedido de gravação a partir do rascunho: texto vazio vira nulo; "não se aplica" descarta os níveis. O responsável vai
 * como vínculo (usuário verificado no servidor), contato externo ou texto — nunca as três coisas misturadas.
 */
export function toSaveRequest(d: NistEvaluationDraft, expectedVersion: number): SaveNistEvaluationRequest {
  const t = (v: string) => (v.trim() === '' ? null : v.trim());
  const mode = d.ownerMode ?? (d.ownerName.trim() ? 'text' : 'none');
  return {
    currentLevel: d.notApplicable ? null : d.currentLevel,
    targetLevel: d.notApplicable ? null : d.targetLevel,
    notApplicable: d.notApplicable,
    currentComments: t(d.currentComments),
    targetComments: t(d.targetComments),
    rationale: t(d.rationale),
    gaps: t(d.gaps),
    riskImpact: t(d.riskImpact),
    improvementGuidance: t(d.improvementGuidance),
    ownerName: mode === 'text' || mode === 'external' ? t(d.ownerName) : null,
    ownerUserId: mode === 'user' ? t(d.ownerUserId ?? '') : null,
    ownerIsExternal: mode === 'external',
    ownerContact: mode === 'external' ? t(d.ownerContact ?? '') : null,
    expectedVersion,
  };
}

/** Motivo pelo qual o rascunho ainda não pode ser gravado (mesmas regras do servidor), ou nulo. */
export function draftProblem(d: NistEvaluationDraft): string | null {
  const inScale = (v: number | null) => v === null || (Number.isInteger(v) && v >= 1 && v <= 5);
  if (!inScale(d.currentLevel) || !inScale(d.targetLevel)) return 'Os níveis vão de 1 a 5.';
  if (d.notApplicable && d.rationale.trim().length < 10)
    return 'Explique por que o resultado não se aplica (pelo menos 10 caracteres).';
  if (d.ownerMode === 'user' && !(d.ownerUserId ?? '').trim()) return 'Escolha o usuário responsável ou outra forma de identificá-lo.';
  if (d.ownerMode === 'external' && !d.ownerName.trim()) return 'Informe o nome do contato externo responsável.';
  const any =
    d.notApplicable ||
    d.currentLevel !== null ||
    d.targetLevel !== null ||
    (d.ownerMode === 'user' && !!(d.ownerUserId ?? '').trim()) ||
    [d.currentComments, d.targetComments, d.rationale, d.gaps, d.riskImpact, d.improvementGuidance, d.ownerName].some(
      (x) => x.trim() !== '',
    );
  return any ? null : 'Informe um nível, uma justificativa ou uma anotação.';
}

/** Lacuna do rascunho: só com os dois níveis e aplicável — senão indeterminada (nulo). */
export function draftGap(d: NistEvaluationDraft): number | null {
  if (d.notApplicable || d.currentLevel === null || d.targetLevel === null) return null;
  return d.targetLevel - d.currentLevel;
}

/** Linha do progresso de um escopo: "12 de 106 avaliadas · 3 não se aplicam · 4 em andamento". */
export function scopeProgressText(
  s: Pick<NistScope, 'subcategories' | 'evaluated' | 'notApplicable' | 'inProgress'> & { pendingConfirmation?: number },
): string {
  const parts = [`${s.evaluated} de ${s.subcategories} avaliadas`];
  if (s.notApplicable > 0) parts.push(`${s.notApplicable} não se aplica${s.notApplicable === 1 ? '' : 'm'}`);
  if (s.inProgress > 0) parts.push(`${s.inProgress} em andamento`);
  if ((s.pendingConfirmation ?? 0) > 0) parts.push(`${s.pendingConfirmation} aguardando confirmação`);
  return parts.join(' · ');
}

/** Cobertura da avaliação (avaliadas + não aplicáveis) / catálogo, em %; catálogo vazio → nulo. */
export function scopeCoverage(s: Pick<NistScope, 'subcategories' | 'evaluated' | 'notApplicable'>): number | null {
  if (s.subcategories <= 0) return null;
  return Math.round(((s.evaluated + s.notApplicable) / s.subcategories) * 1000) / 10;
}

/** Texto do perfil: "Atual 2,5 · Alvo 4,0 (calculado sobre 3 de 31)". */
export function profileBasisText(p: NistProfileScore): string {
  if (p.withCurrent === 0 && p.withTarget === 0) return 'Sem níveis registrados';
  return `atual sobre ${p.withCurrent} · alvo sobre ${p.withTarget} · lacuna sobre ${p.withGap} de ${p.subcategories}`;
}

export function evidenceOriginLabel(kind: NistEvidenceOrigin | string): string {
  switch (kind) {
    case 'GovernanceDocument':
      return 'Documento';
    case 'KnightIndicator':
      return 'AEGIS KNIGHT';
    case 'AssetInventory':
      return 'Inventário';
    default:
      return 'Registro do analista';
  }
}

// ---- Rótulos da jornada (pt-BR) -------------------------------------------------------------------------------

const label = <T extends string>(map: Record<T, string>) => (v: T | string | null | undefined): string =>
  v === null || v === undefined ? '—' : (map as Record<string, string>)[v] ?? v;

export const NIST_METHODS: readonly NistTestMethod[] = ['Examine', 'Interview', 'Test'];
export const methodLabel = label<NistTestMethod>({ Examine: 'Examinar', Interview: 'Entrevistar', Test: 'Testar' });
export const METHOD_HINTS: Record<NistTestMethod, string> = {
  Examine: 'Revisar documentos, registros, configurações ou evidências existentes.',
  Interview: 'Conversar com quem executa ou responde pela prática.',
  Test: 'Exercitar o controle e observar o comportamento real.',
};
export const procedureStatusLabel = label<NistProcedureStatus>({
  Planned: 'Planejado',
  InProgress: 'Em execução',
  Performed: 'Realizado',
  NotPerformed: 'Não realizado',
});
export const NIST_OUTCOMES: readonly NistProcedureOutcome[] = ['Satisfactory', 'PartiallySatisfactory', 'Unsatisfactory', 'Inconclusive'];
export const outcomeLabel = label<NistProcedureOutcome>({
  Satisfactory: 'Satisfatório',
  PartiallySatisfactory: 'Parcialmente satisfatório',
  Unsatisfactory: 'Insatisfatório',
  Inconclusive: 'Inconclusivo',
});
export function outcomeBadgeClass(o: NistProcedureOutcome | null | undefined): string {
  return o === 'Satisfactory' ? 'ok' : o === 'Unsatisfactory' ? 'bad' : o === 'PartiallySatisfactory' ? 'warn' : 'neutral';
}
export const NIST_SEVERITIES: readonly NistFindingSeverity[] = ['Critical', 'High', 'Medium', 'Low'];
export const severityLabel = label<NistFindingSeverity>({ Critical: 'Crítica', High: 'Alta', Medium: 'Média', Low: 'Baixa' });
export function severityBadgeClass(s: NistFindingSeverity | string): string {
  return s === 'Critical' ? 'danger' : s === 'High' ? 'risk' : s === 'Medium' ? 'warn' : 'neutral';
}
export const NIST_PRIORITIES: readonly NistFindingPriority[] = ['Urgent', 'High', 'Medium', 'Low'];
export const priorityLabel = label<NistFindingPriority>({ Urgent: 'Urgente', High: 'Alta', Medium: 'Média', Low: 'Baixa' });
export const findingStatusLabel = label<NistFindingStatus>({ Open: 'Aberto', RiskAccepted: 'Risco aceito', Closed: 'Encerrado' });
export const planStatusLabel = label<NistPlanStatus>({
  Aberto: 'Aberto',
  EmAndamento: 'Em andamento',
  AguardandoValidacao: 'Aguardando validação',
  Concluido: 'Concluído',
  Vencido: 'Vencido (legado)',
});
/** Rótulo do BOTÃO que leva o plano a uma etapa (a etapa anterior a "Concluído" é reabrir quando já concluído). */
export function planTransitionLabel(from: NistPlanStatus, to: NistPlanStatus): string {
  if (from === 'Concluido' && to !== 'Concluido') return 'Reabrir o plano';
  switch (to) {
    case 'EmAndamento':
      return from === 'AguardandoValidacao' ? 'Voltar para execução' : 'Iniciar execução';
    case 'Aberto':
      return 'Voltar para aberto';
    case 'AguardandoValidacao':
      return 'Enviar para validação';
    case 'Concluido':
      return 'Concluir';
    default:
      return planStatusLabel(to);
  }
}
export const reviewStateLabel = label<NistReviewState>({
  None: 'Sem decisão do revisor',
  Approved: 'Aprovada pelo revisor',
  ChangesRequested: 'Devolvida para ajuste',
  Outdated: 'Decisão anterior à versão vigente',
});
export function reviewBadgeClass(s: NistReviewState | string | undefined): string {
  return s === 'Approved' ? 'ok' : s === 'ChangesRequested' ? 'warn' : s === 'Outdated' ? 'violet' : 'neutral';
}
export const contentOriginLabel = label<NistContentOrigin>({
  Analyst: 'Registro do analista',
  CarriedForward: 'Trazido de outra rodada — rascunho a confirmar',
  Imported: 'Importado por CSV — rascunho a confirmar',
});
export const periodKindLabel = label<NistPeriodKind>({ Monthly: 'Mensal', Quarterly: 'Trimestral', Other: 'Outro período' });
export const cycleStatusLabel = label<NistCycleStatus>({ Open: 'Aberta', Closed: 'Encerrada' });
export const seedModeLabel = label<NistSeedMode>({
  None: 'Começar sem dados da rodada anterior',
  Reference: 'Mostrar a rodada anterior como referência de leitura',
  Draft: 'Trazer a rodada anterior como rascunho a confirmar',
});
export const importActionLabel = label<NistImportAction>({
  Create: 'Cria',
  Update: 'Atualiza',
  Unchanged: 'Sem mudança',
  Error: 'Erro',
  Conflict: 'Conflito de versão',
});
export const auditSubjectLabel = label<string>({
  Assessment: 'Avaliação',
  Scope: 'Escopo',
  Cycle: 'Rodada',
  Evaluation: 'Avaliação da subcategoria',
  Assignment: 'Designação',
  Review: 'Revisão',
  Evidence: 'Evidência',
  Procedure: 'Procedimento',
  Finding: 'Achado',
  Plan: 'Plano de tratamento',
  Publication: 'Publicação',
  Import: 'Importação CSV',
});
export const comparisonChangeLabel = label<string>({
  LevelUp: 'Nível subiu',
  LevelDown: 'Nível caiu',
  NowEvaluated: 'Passou a ser avaliada',
  NoLongerEvaluated: 'Deixou de ter avaliação confirmada',
  ApplicabilityChanged: 'Aplicabilidade mudou',
  TargetChanged: 'Alvo mudou',
});

/** "Usuário do tenant", "Contato externo", "Texto (registro anterior)" — de onde vem a identificação do responsável. */
export function responsibleText(r: NistResponsible | null | undefined, fallback?: string | null): string {
  if (!r || r.kind === 'None') return (fallback ?? '').trim() || 'Sem responsável';
  if (r.kind === 'User') return `${r.name ?? 'Usuário'}${r.userActive ? '' : ' (usuário inativo)'}`;
  if (r.kind === 'External') return `${r.name ?? 'Contato externo'} · externo${r.contact ? ` · ${r.contact}` : ''}`;
  return `${r.name ?? ''} · texto livre`;
}

// ---- Procedimentos e achados (puro) -----------------------------------------------------------------------------

export interface NistProcedureResultDraft {
  status: NistProcedureStatus;
  outcome: NistProcedureOutcome | null;
  observation: string;
  performedOn: string;
  evidenceIds: string[];
}

export function procedureDraftFrom(p: NistProcedure): NistProcedureResultDraft {
  return {
    status: p.status,
    outcome: p.outcome,
    observation: p.observation ?? '',
    performedOn: p.performedOn ?? '',
    evidenceIds: [...p.evidenceIds],
  };
}

/**
 * Regras do RESULTADO (as mesmas do servidor): escolher o método não comprova o teste. Realizado exige data (não futura),
 * observação e conclusão; não realizado exige o motivo e não tem conclusão; planejado/em execução não têm conclusão nem data.
 */
export function procedureResultProblem(d: NistProcedureResultDraft, today: string): string | null {
  const obs = d.observation.trim();
  if (d.status === 'Performed') {
    if (!d.outcome) return 'Um procedimento realizado precisa da conclusão observada.';
    if (!d.performedOn) return 'Informe a data em que o procedimento foi realizado.';
    if (d.performedOn > today) return 'A data de realização não pode estar no futuro.';
    if (obs.length < 10) return 'Registre o que foi observado (pelo menos 10 caracteres).';
    return null;
  }
  if (d.status === 'NotPerformed') {
    if (obs.length < 10) return 'Explique por que o procedimento não foi realizado (pelo menos 10 caracteres).';
    return null;
  }
  return null;
}

export function toProcedureRequest(d: NistProcedureResultDraft, expectedVersion: number): UpdateNistProcedureRequest {
  const performed = d.status === 'Performed';
  return {
    procedure: null,
    status: d.status,
    outcome: performed ? d.outcome : null,
    observation: d.observation.trim() || null,
    performedOn: performed ? d.performedOn || null : null,
    evidenceIds: d.evidenceIds,
    expectedVersion,
  };
}

export interface NistFindingDraft {
  title: string;
  condition: string;
  risk: string;
  impact: string;
  severity: NistFindingSeverity | '';
  severityRationale: string;
  priority: NistFindingPriority | '';
  priorityRationale: string;
  recommendation: string;
  evidenceIds: string[];
  withPlan: boolean;
  planTitle: string;
  planAction: string;
  planOwnerMode: NistOwnerMode;
  planOwnerUserId: string;
  planOwnerName: string;
  planOwnerContact: string;
  planArea: string;
  planDue: string;
}

/** Rascunho do achado semeado com o que a subcategoria já documentou — texto para o analista editar, não decisão. */
export function findingDraftFrom(d: NistSubcategoryDetail | null): NistFindingDraft {
  const e = d?.evaluation ?? null;
  const unsatisfied = (d?.procedures ?? []).filter((p) => p.status === 'Performed' && (p.outcome === 'Unsatisfactory' || p.outcome === 'PartiallySatisfactory'));
  return {
    title: '',
    condition: [e?.gaps ?? '', ...unsatisfied.map((p) => p.observation ?? '')].filter((x) => x.trim()).join('\n'),
    risk: e?.riskImpact ?? '',
    impact: '',
    severity: '',
    severityRationale: '',
    priority: '',
    priorityRationale: '',
    recommendation: e?.improvementGuidance ?? '',
    evidenceIds: [],
    withPlan: true,
    planTitle: '',
    planAction: '',
    planOwnerMode: 'none',
    planOwnerUserId: '',
    planOwnerName: '',
    planOwnerContact: '',
    planArea: '',
    planDue: '',
  };
}

export function findingProblem(f: NistFindingDraft): string | null {
  if (!f.title.trim()) return 'Descreva o problema (título do achado).';
  if (!f.condition.trim()) return 'Descreva a condição observada.';
  if (!f.risk.trim()) return 'Fundamente o risco: o que pode acontecer por causa da condição.';
  if (!f.impact.trim()) return 'Descreva o impacto, no limite do que a condição permite afirmar.';
  if (!f.severity) return 'Escolha a severidade.';
  if (f.severityRationale.trim().length < 10) return 'Justifique a severidade (pelo menos 10 caracteres).';
  if (!f.priority) return 'Escolha a prioridade.';
  if (f.priorityRationale.trim().length < 10) return 'Justifique a prioridade (pelo menos 10 caracteres).';
  if (!f.recommendation.trim()) return 'Registre a recomendação.';
  if (f.withPlan) {
    if (!f.planTitle.trim()) return 'Dê um título ao plano de tratamento (ou desmarque o plano).';
    if (f.planOwnerMode === 'user' && !f.planOwnerUserId) return 'Escolha o usuário responsável pelo plano.';
    if (f.planOwnerMode === 'external' && !f.planOwnerName.trim()) return 'Informe o nome do contato externo.';
  }
  return null;
}

export function responsibleRequest(mode: NistOwnerMode, userId: string, name: string, contact: string): NistResponsibleRequest | null {
  switch (mode) {
    case 'user':
      return userId ? { userId, name: null, isExternal: false, contact: null } : null;
    case 'external':
      return { userId: null, name: name.trim() || null, isExternal: true, contact: contact.trim() || null };
    case 'text':
      return name.trim() ? { userId: null, name: name.trim(), isExternal: false, contact: null } : null;
    default:
      return null;
  }
}

export function toFindingRequest(f: NistFindingDraft): CreateNistFindingRequest {
  return {
    title: f.title.trim(),
    condition: f.condition.trim(),
    risk: f.risk.trim(),
    impact: f.impact.trim(),
    severity: f.severity as NistFindingSeverity,
    severityRationale: f.severityRationale.trim(),
    priority: f.priority as NistFindingPriority,
    priorityRationale: f.priorityRationale.trim(),
    recommendation: f.recommendation.trim(),
    evidenceIds: f.evidenceIds.length ? f.evidenceIds : null,
    plan: f.withPlan
      ? {
          title: f.planTitle.trim(),
          proposedAction: f.planAction.trim() || null,
          responsible: responsibleRequest(f.planOwnerMode, f.planOwnerUserId, f.planOwnerName, f.planOwnerContact),
          responsibleArea: f.planArea.trim() || null,
          dueDate: f.planDue || null,
        }
      : null,
  };
}

// ---- Rodadas: períodos (puro) ---------------------------------------------------------------------------------

const pad = (n: number) => String(n).padStart(2, '0');
const iso = (y: number, m: number, d: number) => `${y}-${pad(m)}-${pad(d)}`;
const lastDay = (y: number, m: number) => new Date(Date.UTC(y, m, 0)).getUTCDate();
const MONTHS = ['jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez'];

/** Mês civil inteiro (m = 1..12). */
export function monthPeriod(year: number, month: number): { start: string; end: string } {
  return { start: iso(year, month, 1), end: iso(year, month, lastDay(year, month)) };
}

/** Trimestre civil inteiro (q = 1..4). */
export function quarterPeriod(year: number, quarter: number): { start: string; end: string } {
  const m0 = (quarter - 1) * 3 + 1;
  return { start: iso(year, m0, 1), end: iso(year, m0 + 2, lastDay(year, m0 + 2)) };
}

/** "dd/mm/aaaa" a partir de "aaaa-mm-dd" (sem fuso: data civil). */
export function dateBr(d: string | null | undefined): string {
  if (!d) return '—';
  const [y, m, day] = d.slice(0, 10).split('-');
  return `${day}/${m}/${y}`;
}

/** "out/2026", "T4 2026" ou "01/10/2026 a 15/11/2026". */
export function cyclePeriodText(c: Pick<NistCycle, 'periodKind' | 'periodStart' | 'periodEnd'>): string {
  const [y, m] = c.periodStart.split('-').map(Number);
  if (c.periodKind === 'Monthly') return `${MONTHS[m - 1]}/${y}`;
  if (c.periodKind === 'Quarterly') return `T${Math.floor((m - 1) / 3) + 1} ${y}`;
  return `${dateBr(c.periodStart)} a ${dateBr(c.periodEnd)}`;
}

/** "T4 2026" quando o nome já é o período; senão "Rodada piloto · out/2026". */
export function cycleLabel(c: Pick<NistCycle, 'name' | 'periodKind' | 'periodStart' | 'periodEnd'>): string {
  const period = cyclePeriodText(c);
  return c.name.trim() === period ? period : `${c.name} · ${period}`;
}

/** Mesmas regras do servidor para o período da rodada. */
export function cyclePeriodProblem(kind: NistPeriodKind, start: string, end: string): string | null {
  if (!start || !end) return 'Informe o início e o fim do período.';
  if (end < start) return 'O fim do período não pode ser anterior ao início.';
  const [y, m, d] = start.split('-').map(Number);
  if (kind === 'Monthly') {
    const p = monthPeriod(y, m);
    if (d !== 1 || end !== p.end) return 'Uma rodada mensal cobre um mês civil inteiro (do dia 1 ao último dia).';
  }
  if (kind === 'Quarterly') {
    const q = Math.floor((m - 1) / 3) + 1;
    const p = quarterPeriod(y, q);
    if (start !== p.start || end !== p.end) return 'Uma rodada trimestral cobre um trimestre civil inteiro.';
  }
  if (kind === 'Other') {
    const [ey, em, ed] = end.split('-').map(Number);
    const limit = new Date(Date.UTC(y + 3, m - 1, d));
    if (new Date(Date.UTC(ey, em - 1, ed)) > limit) return 'O período de uma rodada vai até três anos.';
  }
  return null;
}

/** Sugestão de próxima rodada a partir da mais recente (o mês/trimestre seguinte; senão, o mês atual). */
export function suggestNextCycle(
  cycles: readonly NistCycle[],
  kind: NistPeriodKind,
  today: string,
): { name: string; start: string; end: string } {
  const latest = [...cycles].sort((a, b) => b.periodEnd.localeCompare(a.periodEnd))[0];
  const base = latest ? latest.periodEnd : null;
  let y: number, m: number;
  if (base) {
    [y, m] = base.split('-').map(Number);
    m += 1;
    if (m > 12) {
      m = 1;
      y += 1;
    }
  } else {
    [y, m] = today.split('-').map(Number);
  }
  if (kind === 'Quarterly') {
    const q = Math.floor((m - 1) / 3) + 1;
    const p = quarterPeriod(y, q);
    return { name: `T${q} ${y}`, ...p };
  }
  const p = monthPeriod(y, m);
  return { name: kind === 'Monthly' ? `${MONTHS[m - 1]}/${y}` : `Rodada ${MONTHS[m - 1]}/${y}`, ...p };
}

// ---- Seleção de avaliação, rodada e escopo (pura) ---------------------------------------------------------------

export interface NistSelection {
  assessment: NistAssessment;
  cycle: NistCycle | null;
  scope: NistScope | null;
}

export interface NistSelectionRequest {
  assessmentId: string | null;
  cycleId?: string | null;
  scopeId: string | null;
}

export interface NistRememberedSelection {
  assessmentId: string;
  cycleId?: string | null;
  scopeId: string;
}

/** A rodada de referência quando nenhuma é pedida: a do andamento (mais recente), depois a primeira da lista. */
function defaultCycle(a: NistAssessment): NistCycle | null {
  const cycles = a.cycles ?? [];
  return cycles.find((c) => c.id === a.progressCycleId) ?? cycles[0] ?? null;
}

/**
 * Avaliação, rodada e escopo vigentes: o pedido da URL vence; senão a última escolha lembrada; senão a avaliação mais
 * recente (a lista já vem ordenada pelo servidor) na sua rodada mais recente. Um id desconhecido (apagado, de outro
 * tenant, de outra avaliação) nunca é usado — a rodada de uma avaliação jamais é aplicada a outra.
 */
export function resolveSelection(
  assessments: readonly NistAssessment[],
  requested: NistSelectionRequest,
  remembered: NistRememberedSelection | null,
): NistSelection | null {
  const pick = (assessmentId: string | null, cycleId: string | null | undefined, scopeId: string | null): NistSelection | null => {
    const assessment = assessments.find((a) => a.id === assessmentId);
    if (!assessment) return null;
    const scope = assessment.scopes.find((s) => s.id === scopeId) ?? assessment.scopes[0] ?? null;
    const cycle = (assessment.cycles ?? []).find((c) => c.id === cycleId) ?? defaultCycle(assessment);
    return { assessment, cycle, scope };
  };
  return (
    pick(requested.assessmentId, requested.cycleId, requested.scopeId) ??
    (remembered ? pick(remembered.assessmentId, remembered.cycleId, remembered.scopeId) : null) ??
    (assessments[0] ? pick(assessments[0].id, null, null) : null)
  );
}

/** Parâmetros de URL que preservam a seleção entre as telas do NIST (avaliação, rodada e escopo). */
export function selectionParams(s: NistSelection | null): Record<string, string> {
  if (!s) return {};
  const p: Record<string, string> = { avaliacao: s.assessment.id };
  if (s.cycle) p['rodada'] = s.cycle.id;
  if (s.scope) p['escopo'] = s.scope.id;
  return p;
}

/**
 * Identidade do CONTEXTO de trabalho: uma resposta só pode preencher a tela se pertence à mesma avaliação, rodada e
 * escopo (e subcategoria/função, quando houver). Resposta de outra rodada é descartada.
 */
export function selectionKey(s: NistSelection | null, extra = ''): string {
  if (!s) return '';
  return [s.assessment.id, s.cycle?.id ?? '-', s.scope?.id ?? '-', extra].join('|');
}

/** Rodada encerrada não aceita edição de avaliação, procedimentos ou achados (planos continuam). */
export function cycleIsClosed(c: Pick<NistCycle, 'status'> | null | undefined): boolean {
  return c?.status === 'Closed';
}

/** Função da subcategoria pelo código ("ID.AM-01" → "id"). */
export function functionSlugOf(code: string): string {
  return code.split('.')[0].toLowerCase();
}
