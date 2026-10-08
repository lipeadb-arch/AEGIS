/**
 * [AEGIS-NIST-AI-ASSIST-01] Assistência contextual de IA da jornada NIST — tipos da API e regras PURAS da tela.
 *
 * A assistência devolve SUGESTÕES identificadas (motor real ou simulado, data, quem pediu, impressão digital do contexto,
 * fontes citáveis e notas da validação do servidor). Aplicar ao rascunho é só da tela: nada é gravado até a pessoa gravar
 * pelo fluxo normal, que leva a referência da sugestão para a procedência. Sugestão de outro contexto, desatualizada ou
 * vinda de resposta atrasada não preenche nada.
 */
import { NistAssistanceRef, NistAssistedField, NistEvaluationDraft } from './nist.models';

export type NistAssistKind = 'Subcategory' | 'Finding' | 'ExecutiveSummary';
export type NistAssistFocus = 'Explain' | 'Treatment';
export type NistAssistBasis = 'Fact' | 'AnalystReport' | 'Unconfirmed' | 'Reference' | 'NotExamined' | 'General';

export interface NistAssistSourceLink {
  target: string;
  id: string | null;
  code: string | null;
}

export interface NistAssistSource {
  key: string;
  kind: string;
  kindLabel: string;
  basis: NistAssistBasis | string;
  basisLabel: string;
  title: string;
  detail: string | null;
  date: string | null;
  status: string | null;
  isDemo: boolean;
  contentExamined: boolean;
  limitation: string | null;
  link: NistAssistSourceLink | null;
}

export interface NistAssistItem {
  text: string;
  sources: string[];
  basis: NistAssistBasis | string;
  basisLabel: string;
}

export interface NistAssistSection {
  key: string;
  title: string;
  hint: string;
  items: NistAssistItem[];
}

export interface NistAssistLevel {
  level: number;
  levelName: string;
  rationale: string;
  sources: string[];
  methodologyVersion: string;
}

export interface NistAssistProcedure {
  method: 'Examine' | 'Interview' | 'Test' | string;
  methodLabel: string;
  procedure: string;
  sources: string[];
}

export interface NistAssistView {
  id: string;
  kind: NistAssistKind;
  focus: NistAssistFocus | null;
  assessmentId: string;
  cycleId: string;
  scopeId: string;
  subcategoryCode: string | null;
  findingId: string | null;
  mode: 'Real' | 'Simulated' | string;
  availability: string;
  modeLabel: string;
  generatedAt: string;
  requestedByName: string | null;
  contextFingerprint: string;
  contextSummary: string;
  current: boolean;
  staleOnArrival: boolean;
  reused: boolean;
  sources: NistAssistSource[];
  sections: NistAssistSection[];
  level: NistAssistLevel | null;
  levelNote: string | null;
  procedures: NistAssistProcedure[];
  applicable: Record<string, string>;
  validationNotes: string[];
  disclaimer: string;
}

export interface NistAssistAvailability {
  state: 'Disabled' | 'Simulated' | 'ProviderNotConfigured' | 'ExternalBlockedForTenant' | 'Real' | string;
  label: string;
  detail: string;
  canGenerate: boolean;
}

export interface NistAssistContext {
  fingerprint: string;
  summary: string;
  sources: NistAssistSource[];
  availability: NistAssistAvailability;
  latest: NistAssistView | null;
}

export interface NistExecutiveSection {
  key: string;
  title: string;
  text: string;
}

export interface NistExecutiveSummary {
  id: string;
  sections: NistExecutiveSection[];
  origin: 'Assisted' | 'Manual' | string;
  mode: string | null;
  assistanceId: string | null;
  generatedAt: string | null;
  requestedByName: string | null;
  acceptedByName: string | null;
  acceptedAt: string;
  edited: boolean;
  staleAcknowledged: boolean;
  current: boolean;
  reviewedByName: string | null;
  reviewedAt: string | null;
  reviewCurrent: boolean;
  reviewNote: string | null;
  version: number;
}

/** Seções do resumo executivo, na ordem do servidor (NistAssistSections). */
export const EXECUTIVE_SECTIONS: { key: string; title: string }[] = [
  { key: 'situation', title: 'Situação atual e alvo' },
  { key: 'coverage', title: 'Cobertura e base dos resultados' },
  { key: 'gaps', title: 'Principais lacunas' },
  { key: 'risks', title: 'Riscos registrados' },
  { key: 'treatment', title: 'Andamento do tratamento' },
  { key: 'limitations', title: 'Limitações e o que ainda não está comprovado' },
  { key: 'priorities', title: 'Ações prioritárias (ordem do AEGIS)' },
  { key: 'nextSteps', title: 'Próximos passos' },
];

/** Botões de aproveitamento por tipo de assistência: seção que mostra o botão → campo do registro que recebe o texto. */
export interface NistAssistApplyTarget {
  section: string;
  field: string;
  label: string;
}

export const SUBCATEGORY_APPLY: NistAssistApplyTarget[] = [
  { section: 'justification', field: 'rationale', label: 'Usar como justificativa' },
  { section: 'unproven', field: 'gaps', label: 'Usar como lacunas observadas' },
  { section: 'risks', field: 'riskImpact', label: 'Usar como risco ou impacto' },
  { section: 'recommendations', field: 'improvementGuidance', label: 'Usar como orientação de melhoria' },
];

export const FINDING_APPLY: NistAssistApplyTarget[] = [
  { section: 'treatment', field: 'recommendation', label: 'Usar como recomendação do achado' },
  { section: 'treatment', field: 'proposedAction', label: 'Usar como ação proposta de um plano novo' },
];

export interface NistAssistApplyEvent {
  view: NistAssistView;
  field: string;
}

/** Os campos de texto da avaliação que recebem conteúdo assistido (o nível é tratado à parte). */
export type AssistDraftField = 'rationale' | 'gaps' | 'riskImpact' | 'improvementGuidance' | 'currentLevel';

/**
 * Aplica um campo da sugestão ao RASCUNHO (cópia nova). Não grava nada. Nível só se a sugestão trouxer um nível válido
 * (o servidor já descartou nível sem base); aplicar o nível tira o "não se aplica".
 */
export function applyAssistToDraft(draft: NistEvaluationDraft, view: NistAssistView, field: string): NistEvaluationDraft | null {
  const text = view.applicable[field];
  if (text === undefined) return null;
  switch (field) {
    case 'rationale':
      return { ...draft, rationale: text };
    case 'gaps':
      return { ...draft, gaps: text };
    case 'riskImpact':
      return { ...draft, riskImpact: text };
    case 'improvementGuidance':
      return { ...draft, improvementGuidance: text };
    case 'currentLevel': {
      const level = Number(text);
      return Number.isInteger(level) && level >= 1 && level <= 5 ? { ...draft, notApplicable: false, currentLevel: level } : null;
    }
    default:
      return null;
  }
}

/**
 * Campos que ainda vão como conteúdo assistido na gravação: só os aplicados que continuam com conteúdo no rascunho (um
 * campo esvaziado não tem procedência a registrar).
 */
export function assistedFieldsToSend(draft: NistEvaluationDraft, applied: Iterable<string>): string[] {
  const has = (f: string): boolean => {
    switch (f) {
      case 'rationale':
        return draft.rationale.trim() !== '';
      case 'gaps':
        return draft.gaps.trim() !== '';
      case 'riskImpact':
        return draft.riskImpact.trim() !== '';
      case 'improvementGuidance':
        return draft.improvementGuidance.trim() !== '';
      case 'currentLevel':
        return !draft.notApplicable && draft.currentLevel !== null;
      default:
        return false;
    }
  };
  return [...new Set(applied)].filter(has).sort();
}

export function assistanceRef(assistanceId: string, fields: string[], acknowledgeStale = false): NistAssistanceRef | null {
  return fields.length === 0 ? null : { assistanceId, fields, acknowledgeStale };
}

/** Uma linha legível da procedência dos campos: "justificativa (editada) · nível — sugestão SIMULADA de 06/10/2026, …". */
export function assistedFieldsText(fields: NistAssistedField[] | null | undefined): string | null {
  if (!fields || fields.length === 0) return null;
  const f = fields[0];
  const labels = fields.map((x) => `${x.label}${x.edited ? ' (editada)' : ''}`).join(', ');
  const mode = f.mode === 'Real' ? 'da IA' : 'SIMULADA (demonstração)';
  return `${labels} — sugestão ${mode} de ${dateTimeBr(f.generatedAt)}, incorporada por ${f.incorporatedByName ?? 'autor não identificado'} em ${dateTimeBr(f.incorporatedAt)}` +
    (fields.some((x) => x.staleAcknowledged) ? ' — sugestão desatualizada, revisada pela pessoa' : '') +
    '. Não é aprovação nem revisão.';
}

function dateTimeBr(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** Classe do selo da classificação de uma fonte ou afirmação. */
export function basisBadgeClass(basis: string): string {
  switch (basis) {
    case 'Fact':
      return 'ok';
    case 'AnalystReport':
      return 'info';
    case 'Unconfirmed':
    case 'NotExamined':
      return 'warn';
    default:
      return 'neutral';
  }
}

/** Selo do estado da IA para a assistência NIST. */
export function availabilityBadgeClass(state: string): string {
  switch (state) {
    case 'Real':
      return 'ok';
    case 'Disabled':
      return 'bad';
    case 'ProviderNotConfigured':
    case 'ExternalBlockedForTenant':
      return 'warn';
    default:
      return 'neutral';
  }
}

/** Mensagem por motivo da indisponibilidade (503) — a jornada manual segue em todos. */
export function assistErrorText(reason: string | null | undefined, fallback: string): string {
  switch (reason) {
    case 'Disabled':
      return 'A IA está desativada neste ambiente. A avaliação segue manual.';
    case 'Timeout':
      return 'A IA não respondeu no tempo limite. Nada foi gravado; tente de novo ou siga manualmente.';
    case 'InvalidResponse':
      return 'A IA respondeu fora do formato esperado e a resposta foi descartada. Nada foi gravado.';
    default:
      return fallback;
  }
}

/**
 * Onde a fonte está na tela. Âncoras da própria página (evidência, procedimento, achado, avaliação, resultado esperado,
 * referência) ou um link de rota (resumo executivo → subcategoria). Nunca um link externo vindo da resposta.
 */
export function sourceAnchorId(s: NistAssistSource): string | null {
  const l = s.link;
  if (!l) return null;
  switch (l.target) {
    case 'Evidence':
      return l.id ? `ev-${l.id}` : null;
    case 'Procedure':
      return l.id ? `proc-${l.id}` : null;
    case 'Finding':
      return l.id ? `achado-${l.id}` : null;
    case 'Evaluation':
      return 'sc-eval';
    case 'Outcome':
      return 'sc-outcome';
    case 'Reference':
      return 'sc-ref';
    default:
      return null;
  }
}

/** A sugestão exibida ainda vale para o contexto lido agora? (mesma impressão digital e não nasceu desatualizada). */
export function assistIsCurrent(view: NistAssistView, fingerprint: string): boolean {
  return !view.staleOnArrival && view.contextFingerprint === fingerprint;
}

/** Seções do resumo para edição: ordem do AEGIS, com o texto vindo da sugestão (ou do resumo aceito). */
export function executiveDraftFrom(source: Record<string, string> | NistExecutiveSection[] | null): { key: string; title: string; text: string }[] {
  const map: Record<string, string> = {};
  if (Array.isArray(source)) for (const s of source) map[s.key] = s.text;
  else if (source) Object.assign(map, source);
  return EXECUTIVE_SECTIONS.map((s) => ({ key: s.key, title: s.title, text: map[s.key] ?? '' }));
}

/** Procedência do resumo executivo em uma linha. */
export function executiveProvenance(s: NistExecutiveSummary): string {
  const origin =
    s.origin === 'Manual' ? 'Redigido pela pessoa' : s.mode === 'Real' ? 'Sugerido pela IA' : 'Sugerido pelo motor SIMULADO (demonstração)';
  return `${origin}; aceito por ${s.acceptedByName ?? 'autor não identificado'} em ${dateTimeBr(s.acceptedAt)}` +
    (s.origin === 'Manual' ? '' : s.edited ? ', com edição' : ', sem edição') +
    (s.staleAcknowledged ? ' (sugestão desatualizada, revisada)' : '') +
    (s.reviewCurrent && s.reviewedByName ? `; revisão humana posterior de ${s.reviewedByName}` : '; sem revisão humana posterior') + '.';
}

// Reexporta para quem só precisa dos tipos de procedência.
export type { NistAssistedField, NistAssistanceRef };
