// [AEGIS-NIST-JOURNEY-01] Modelos do AEGIS NIST — espelham /api/v1/nist/assessments (camelCase) — e funções PURAS de
// apresentação (pt-BR). Dois conceitos que esta camada mantém separados:
//   • MATURIDADE atual × alvo por avaliação (escala 1–5 autoral do AEGIS, aegis-methodology-v1);
//   • score de POSTURA do ambiente (aegis-score-v1, AEGIS Score) — lido como contexto, nunca somado à maturidade.
// Ausência de nível é ausência de avaliação: nada aqui converte nulo em zero.

import { NIST_CATEGORY_NAMES } from './nist-glossary';

export type NistFunctionCode = 'GV' | 'ID' | 'PR' | 'DE' | 'RS' | 'RC';

export type NistSubcategoryState = 'NotEvaluated' | 'InProgress' | 'Evaluated' | 'NotApplicable';

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
    case 'NotApplicable':
      return 'neutral';
    default:
      return 'neutral';
  }
}

// ---- Contratos da API -------------------------------------------------------------------------------------------

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
}

export interface NistProfile {
  assessmentId: string;
  scopeId: string;
  methodologyVersion: string;
  overall: NistProfileScore;
  functions: NistProfileScore[];
  categories: NistProfileScore[];
  gaps: NistGap[];
  indeterminateGaps: number;
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

export interface NistSubcategoryDetail {
  assessmentId: string;
  scopeId: string;
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
}

export interface NistAiSuggestion {
  suggestedCurrentLevel: number;
  confidence: number;
  rationale: string;
  simulated: boolean;
  generatedAt: string;
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
}

export interface CreateNistAssessmentRequest {
  name: string;
  description?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  initialScopeName?: string | null;
  initialScopeDescription?: string | null;
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

// ---- Formulário da subcategoria (puro) ---------------------------------------------------------------------------

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
}

export function draftFrom(e: NistEvaluation | null): NistEvaluationDraft {
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
    ownerName: e?.ownerName ?? '',
  };
}

/** Pedido de gravação a partir do rascunho: texto vazio vira nulo; "não se aplica" descarta os níveis. */
export function toSaveRequest(d: NistEvaluationDraft, expectedVersion: number): SaveNistEvaluationRequest {
  const t = (v: string) => (v.trim() === '' ? null : v.trim());
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
    ownerName: t(d.ownerName),
    expectedVersion,
  };
}

/** Motivo pelo qual o rascunho ainda não pode ser gravado (mesmas regras do servidor), ou nulo. */
export function draftProblem(d: NistEvaluationDraft): string | null {
  const inScale = (v: number | null) => v === null || (Number.isInteger(v) && v >= 1 && v <= 5);
  if (!inScale(d.currentLevel) || !inScale(d.targetLevel)) return 'Os níveis vão de 1 a 5.';
  if (d.notApplicable && d.rationale.trim().length < 10)
    return 'Explique por que o resultado não se aplica (pelo menos 10 caracteres).';
  const any =
    d.notApplicable ||
    d.currentLevel !== null ||
    d.targetLevel !== null ||
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
export function scopeProgressText(s: Pick<NistScope, 'subcategories' | 'evaluated' | 'notApplicable' | 'inProgress'>): string {
  const parts = [`${s.evaluated} de ${s.subcategories} avaliadas`];
  if (s.notApplicable > 0) parts.push(`${s.notApplicable} não se aplica${s.notApplicable === 1 ? '' : 'm'}`);
  if (s.inProgress > 0) parts.push(`${s.inProgress} em andamento`);
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

// ---- Seleção de avaliação e escopo (pura) --------------------------------------------------------------------------

export interface NistSelection {
  assessment: NistAssessment;
  scope: NistScope | null;
}

/**
 * Avaliação e escopo vigentes: o pedido da URL vence; senão a última escolha lembrada; senão a avaliação mais recente
 * (a lista já vem ordenada pelo servidor). Um id desconhecido (apagado, de outro tenant) nunca é usado.
 */
export function resolveSelection(
  assessments: readonly NistAssessment[],
  requested: { assessmentId: string | null; scopeId: string | null },
  remembered: { assessmentId: string; scopeId: string } | null,
): NistSelection | null {
  const pick = (assessmentId: string | null, scopeId: string | null): NistSelection | null => {
    const assessment = assessments.find((a) => a.id === assessmentId);
    if (!assessment) return null;
    const scope = assessment.scopes.find((s) => s.id === scopeId) ?? assessment.scopes[0] ?? null;
    return { assessment, scope };
  };
  return (
    pick(requested.assessmentId, requested.scopeId) ??
    (remembered ? pick(remembered.assessmentId, remembered.scopeId) : null) ??
    (assessments[0] ? { assessment: assessments[0], scope: assessments[0].scopes[0] ?? null } : null)
  );
}

/** Parâmetros de URL que preservam a seleção entre as telas do NIST. */
export function selectionParams(s: NistSelection | null): Record<string, string> {
  if (!s) return {};
  return s.scope ? { avaliacao: s.assessment.id, escopo: s.scope.id } : { avaliacao: s.assessment.id };
}
