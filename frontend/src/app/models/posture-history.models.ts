// Modelos da tela de HISTÓRICO auditável de postura — espelham /api/v1/posture/snapshots (camelCase). A
// fotografia é IMUTÁVEL e o score é o do INSTRUMENTO (AEGIS Score/NIST OU KNIGHT), nunca combinado. Funções
// PURAS de apresentação (pt-BR). "Não avaliado" é SEMPRE distinto de 0 e de NonCompliant.

import { KnightConsolidatedSource, KnightSourceType } from './knight.models';

/**
 * Instrumento da fotografia. [AEGIS-NIST-JOURNEY-02] `NistMaturity` = maturidade 1–5 de uma rodada e escopo do AEGIS NIST
 * (metodologia autoral; o `score` 0–100 é sempre nulo nesse tipo) — nunca somada à postura nem ao KNIGHT.
 */
export type PostureSnapshotType = 'AegisScoreNist' | 'Knight' | 'NistMaturity';

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
  /**
   * [AEGIS-MVP-PRODUCT-03] Cliente CONGELADO na publicação — `null` nas fotografias anteriores a este
   * formato, que continuam legíveis e com o hash preservado.
   */
  clientName: string | null;
  /** [AEGIS-MVP-PRODUCT-03] Avaliação KNIGHT EXATA congelada; `null` em AEGIS Score e no histórico antigo. */
  sourceRunId: string | null;
  /** [AEGIS-NIST-JOURNEY-02] Maturidade NIST (1–5) — só em fotografias NistMaturity; o `score` fica nulo. */
  maturityCurrent?: number | null;
  maturityTarget?: number | null;
  maturityGap?: number | null;
  nistAssessmentId?: string | null;
  nistCycleId?: string | null;
  nistScopeId?: string | null;
  nistCycleName?: string | null;
}

/**
 * [AEGIS-MVP-PRODUCT-03] Uma ação CONGELADA na fotografia. Etapa do plano, resultado observado no achado e
 * método de validação viajam SEPARADOS — o relatório não pode colapsá-los num "resolvido".
 */
export interface PostureSnapshotActionItem {
  actionPlanId: string;
  indicatorId: string;
  title: string;
  proposedAction: string | null;
  responsiblePerson: string | null;
  responsibleArea: string | null;
  dueDate: string | null;
  status: string;
  wasOverdue: boolean;
  nextStep: string;
  validationMethod: string | null;
  validationOutcome: string | null;
  validatedAt: string | null;
  observedBefore: number | null;
  observedAfter: number | null;
  comparedBySets: boolean;
  validationRationale: string | null;
  /**
   * Início do ciclo vigente da ação no instante da publicação. É também o DISCRIMINADOR: ausente nas
   * fotografias publicadas antes desta distinção, que por isso não podem afirmar nada sobre aplicabilidade.
   */
  cycleStartedAt?: string | null;
  /** A ação já havia sido REABERTA quando o relatório foi publicado. */
  wasReopened?: boolean | null;
  /**
   * A validação congelada acima fala pelo ciclo vigente? `false` a identifica como registro HISTÓRICO — ela
   * permanece no relatório, mas não comprova o trabalho em curso. Nulo quando não há validação alguma, ou
   * quando a fotografia é anterior à distinção: ausência de informação não vira inaplicabilidade.
   */
  validationAppliesToCurrentCycle?: boolean | null;
  /** Método da validação APLICÁVEL ao ciclo vigente — nulo quando o ciclo não tem nenhuma. */
  applicableValidationMethod?: string | null;
  /** Desfecho aplicável ao ciclo vigente: o único que responde pelo trabalho em curso. */
  applicableValidationOutcome?: string | null;
  /** Quando a validação aplicável ao ciclo foi decidida. */
  applicableValidatedAt?: string | null;
}

export interface PostureSnapshotDetail {
  summary: PostureSnapshotSummary;
  achievedPoints: number;
  possiblePoints: number;
  eligiblePoints: number;
  controls: PostureSnapshotControl[];
  indicators: PostureSnapshotIndicator[];
  /** [AEGIS-MVP-PRODUCT-03] Limitações de COLETA congeladas — o que a avaliação não conseguiu ver. */
  collectionLimitations?: string[] | null;
  /** [AEGIS-MVP-PRODUCT-03] Ações congeladas no instante da publicação (nunca o estado atual dos planos). */
  actionItems?: PostureSnapshotActionItem[] | null;
  /**
   * [AEGIS-KNIGHT-CONSOLIDATED-01] Composição das fontes candidatas de um relatório KNIGHT consolidado — só
   * presente nesse tipo de fotografia.
   */
  composition?: KnightConsolidatedSource[] | null;
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
  /**
   * [AEGIS-MVP-PRODUCT-03] Avaliação KNIGHT EXATA a publicar. Sem ela o servidor congela a mais
   * recente — o que faria o relatório sair de uma coleta diferente da que está aberta na tela.
   */
  runId?: string;
}

/**
 * [AEGIS-KNIGHT-CONSOLIDATED-02] Uma fonte→execução PINADA na publicação — a execução EXATA que a tela mostrava
 * como incluída no instante da publicação. O servidor revalida tenant, fonte e conclusão; nunca substitui uma
 * execução inválida pela mais recente.
 */
export interface KnightConsolidatedSourceSelection {
  source: KnightSourceType;
  runId: string;
}

/**
 * [AEGIS-KNIGHT-CONSOLIDATED-01] Requisição de publicação do relatório KNIGHT consolidado. `selection` é a
 * composição EXATA e EXIBIDA no instante da publicação — cada item trava a execução daquela fonte. Uma lista
 * vazia é a seleção explícita "nenhuma fonte", bloqueada no servidor (nunca o padrão silencioso de "todas").
 */
export interface PublishConsolidatedKnightSnapshotRequest {
  selection: KnightConsolidatedSourceSelection[];
}

// ---- Apresentação (pt-BR) -----------------------------------------------------------------------

export function snapshotTypeLabel(type: PostureSnapshotType): string {
  return type === 'Knight' ? 'AEGIS KNIGHT' : type === 'NistMaturity' ? 'AEGIS NIST · maturidade' : 'AEGIS Score / NIST';
}

/** Média de maturidade 1–5 com uma casa ("2,5"); ausente → "—" (nunca 0). */
export function maturityText(v: number | null | undefined): string {
  return v === null || v === undefined ? '—' : v.toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
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

// ---- Exportação PDF/CSV (AEGIS-AUD-034) ----------------------------------------------------------

// [AEGIS-KNIGHT-MULTICLOUD-01] 'html' = relatório interativo autocontido (fotografias do AEGIS KNIGHT).
export type PostureExportFormat = 'pdf' | 'csv' | 'html';

/** Sanitiza um nome de arquivo: remove separadores de caminho, controles e caracteres inválidos; nunca vazio. */
export function sanitizeExportFilename(name: string): string {
  const cleaned = name
    .replace(/[\\/]/g, '_') // separadores de caminho
    // eslint-disable-next-line no-control-regex
    .replace(/[\u0000-\u001f\u007f]/g, '') // caracteres de controle
    .replace(/[<>:"|?*]/g, '_') // inválidos em nomes de arquivo
    .trim();
  return cleaned.length ? cleaned : 'aegis-postura';
}

/** Extrai o filename de um cabeçalho Content-Disposition (RFC 5987 `filename*` tem precedência); `null` se ausente. */
export function parseContentDispositionFilename(header: string | null): string | null {
  if (!header) return null;
  const star = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(header);
  if (star?.[1]) {
    try {
      return sanitizeExportFilename(decodeURIComponent(star[1]));
    } catch {
      /* codificação inválida — cai para o filename simples */
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
  return plain?.[1] ? sanitizeExportFilename(plain[1]) : null;
}

/** Nome de arquivo de fallback quando o servidor não envia Content-Disposition legível. */
export function fallbackExportFilename(id: string, format: PostureExportFormat): string {
  return `aegis-postura-${id.slice(0, 8)}.${format}`;
}

/** Delta com sinal explícito (ex.: "+3,2" / "-1,0"); "—" quando não há número. */
export function signedDelta(value: number | null): string {
  if (value === null) return '—';
  const rounded = Math.round(value * 10) / 10;
  return (rounded > 0 ? '+' : '') + rounded.toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

// ---- [AEGIS-NIST-JOURNEY-01] Evolução mensal (GET /posture/snapshots/monthly) --------------------------------------

/** Um mês de uma série: a última fotografia publicada no mês. `comparableWithPrevious` vem da regra única do servidor. */
export interface PostureMonthlyPoint {
  month: string; // "2026-10-01"
  snapshotId: string;
  capturedAt: string;
  evaluationState: string;
  score: number | null;
  coverage: number;
  evaluatedItems: number;
  eligibleItems: number;
  formulaVersion: string;
  catalogVersion: string;
  schemaVersion: string;
  sourceLabel: string | null;
  sourceRunId: string | null;
  publishedInMonth: number;
  comparableWithPrevious: boolean;
  breakReasons: string[];
  /** [AEGIS-NIST-JOURNEY-02] Só na série de maturidade NIST (1–5). */
  maturityCurrent?: number | null;
  maturityTarget?: number | null;
  applicableItems?: number | null;
  cycleName?: string | null;
  notes?: string[] | null;
}

export interface PostureMonthlySeries {
  type: PostureSnapshotType;
  semanticFamily: string;
  sourceType: string | null;
  label: string;
  points: PostureMonthlyPoint[];
}

export interface PostureMonthlyHistory {
  criterion: string;
  months: string[];
  series: PostureMonthlySeries[];
}

/** Uma célula do eixo mensal: o ponto do mês (ou nulo — mês sem publicação, NUNCA interpolado). */
export interface MonthlyCell {
  month: string;
  point: PostureMonthlyPoint | null;
  /** Liga ao mês anterior: só quando os dois meses têm ponto, são consecutivos e comparáveis. */
  connected: boolean;
}

/**
 * Projeta a série no eixo de meses: mês sem ponto fica vazio; ligação só entre vizinhos comparáveis. `value` diz qual
 * grandeza a série desenha (o score 0–100, ou a maturidade 1–5 na série NIST) — ausência nunca liga.
 */
export function monthlyCells(
  months: readonly string[],
  points: readonly PostureMonthlyPoint[],
  value: (p: PostureMonthlyPoint) => number | null | undefined = (p) => p.score,
): MonthlyCell[] {
  const byMonth = new Map(points.map((p) => [p.month, p]));
  return months.map((month, i) => {
    const point = byMonth.get(month) ?? null;
    const prev = i > 0 ? byMonth.get(months[i - 1]) ?? null : null;
    const has = (p: PostureMonthlyPoint | null) => !!p && value(p) !== null && value(p) !== undefined;
    return { month, point, connected: has(point) && has(prev) && point!.comparableWithPrevious };
  });
}

/** [AEGIS-NIST-JOURNEY-02] Valor desenhado de um ponto: maturidade atual (1–5) na série NIST; score nas demais. */
export function seriesValue(type: PostureSnapshotType, p: PostureMonthlyPoint): number | null {
  return type === 'NistMaturity' ? p.maturityCurrent ?? null : p.score;
}

/** "out/26". */
export function monthShort(month: string): string {
  const [y, m] = month.split('-').map(Number);
  const names = ['jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez'];
  return `${names[(m ?? 1) - 1]}/${String(y).slice(2)}`;
}
