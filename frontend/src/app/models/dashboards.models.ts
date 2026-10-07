// [AEGIS-NIST-JOURNEY-01] Leitura executiva dos assessments (Dashboards) — funções PURAS sobre os contratos reais do
// backend. Nada aqui calcula score: KNIGHT, postura NIST (aegis-score-v1) e maturidade NIST vêm prontos das suas fontes
// e NUNCA são somados ou combinados num indicador "geral".

import { CONSOLIDATION_CANDIDATES, KnightAssessment, KnightIndicator, KnightSourceLatest, KnightSourceType, severityLabel } from './knight.models';
import { SeverityLevel } from './scoring.models';
import { ActionPlan } from './remediation.models';
import { NistGap } from './nist.models';
import { STALE_AFTER_DAYS } from './dashboard-overview.models';
import { WorkspaceOverall } from './workspace.models';

/** O que a tela pode afirmar sobre uma leitura: existe? é zero real? parcial? antiga? */
export interface ReadingStatus {
  /** Há resultado (nota) para mostrar. */
  hasResult: boolean;
  /** Resultado avaliado e igual a zero — distinto de "sem avaliação". */
  zero: boolean;
  partial: boolean;
  stale: boolean;
  demo: boolean;
  /** Idade da leitura em dias (nula sem data). */
  ageDays: number | null;
  /** Etiquetas curtas, na ordem de importância. */
  labels: string[];
}

function ageInDays(iso: string | null | undefined, now: Date): number | null {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (!Number.isFinite(t)) return null;
  return Math.max(0, Math.floor((now.getTime() - t) / 86_400_000));
}

/** Leitura de UMA avaliação KNIGHT (por fonte). */
export function knightReading(a: KnightAssessment | null, now: Date): ReadingStatus {
  if (!a) return { hasResult: false, zero: false, partial: false, stale: false, demo: false, ageDays: null, labels: ['Sem avaliação concluída'] };
  const labels: string[] = [];
  const hasResult = a.score !== null && a.score !== undefined;
  const zero = hasResult && a.score === 0;
  if (!hasResult) labels.push('Sem nota: nenhum controle avaliado');
  if (zero) labels.push('Resultado zero (avaliado)');
  const partial = a.sourceState === 'PartialCollection' || a.counts.error > 0;
  if (a.sourceState === 'PartialCollection') labels.push('Coleta parcial');
  else if (a.counts.error > 0) labels.push(`${a.counts.error} controle(s) com erro de leitura`);
  if (a.counts.notEvaluated > 0) labels.push(`${a.counts.notEvaluated} controle(s) não avaliados`);
  const ageDays = ageInDays(a.completedAt ?? a.startedAt, now);
  const stale = ageDays !== null && ageDays >= STALE_AFTER_DAYS;
  if (stale) labels.push(`Desatualizada (${ageDays} dias)`);
  if (a.isDemo) labels.push('Demonstração (dados sintéticos)');
  return { hasResult, zero, partial, stale, demo: a.isDemo, ageDays, labels };
}

/** Leitura da postura do ambiente (aegis-score-v1). Cobertura parcial é dita; "não avaliado" nunca vira 0%. */
export function nistPostureReading(o: WorkspaceOverall | null, now: Date): ReadingStatus {
  if (!o || o.evaluationState === 'NotEvaluated' || o.percentage === null)
    return { hasResult: false, zero: false, partial: false, stale: false, demo: false, ageDays: null, labels: ['Sem controle avaliado'] };
  const labels: string[] = [];
  const zero = o.percentage === 0;
  if (zero) labels.push('Resultado zero (avaliado)');
  const partial = o.coveragePercentage < 100;
  if (partial) labels.push(`Cobertura parcial (${o.coveragePercentage.toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%)`);
  const ageDays = ageInDays(o.latestEvidenceAt, now);
  const stale = ageDays !== null && ageDays >= STALE_AFTER_DAYS;
  if (stale) labels.push(`Evidência mais recente há ${ageDays} dias`);
  if (ageDays === null) labels.push('Sem data de evidência');
  return { hasResult: true, zero, partial, stale, demo: false, ageDays, labels };
}

const SEVERITY_RANK: Record<SeverityLevel, number> = { Critical: 0, High: 1, Medium: 2, Low: 3, Informational: 4 };

/**
 * Findings prioritários de uma avaliação KNIGHT: só os EXPOSTOS (reprovados), por severidade e, no empate, pelo número
 * de objetos afetados. Mitigado e não avaliado não entram — não são achado aberto.
 */
export function prioritizedFindings(a: KnightAssessment | null, limit = 3): KnightIndicator[] {
  if (!a) return [];
  return a.indicators
    .filter((i) => i.status === 'Exposed')
    .slice()
    .sort(
      (x, y) =>
        (SEVERITY_RANK[x.severity] ?? 9) - (SEVERITY_RANK[y.severity] ?? 9) ||
        y.affectedObjectCount - x.affectedObjectCount ||
        x.indicatorId.localeCompare(y.indicatorId),
    )
    .slice(0, limit);
}

/** Nota formatada (uma casa) ou "—" quando não há nota. Nunca "0" por ausência. */
export function scoreText(score: number | null | undefined): string {
  if (score === null || score === undefined) return '—';
  return score.toLocaleString('pt-BR', { maximumFractionDigits: 1 });
}

// =====================================================================================================================
// [AEGIS-ASSESSMENT-VISUALS-01] Painel executivo dos dois módulos — escolha explícita do resultado exibido, estado da
// avaliação em uma palavra, tratamento contado uma vez por plano e necessidades de atenção com origem.
// =====================================================================================================================

/** Estado da avaliação em uma palavra (a ordem diz o que pesa mais): sem avaliação, demonstração, desatualizada, parcial, completa. */
export function knightStateLabel(r: ReadingStatus | null): { label: string; tone: 'neutral' | 'warn' | 'ok' } {
  if (!r || (!r.hasResult && r.ageDays === null)) return { label: 'Sem avaliação', tone: 'neutral' };
  if (r.demo) return { label: 'Demonstração', tone: 'warn' };
  if (r.stale) return { label: 'Desatualizada', tone: 'warn' };
  if (r.partial) return { label: 'Parcial', tone: 'warn' };
  if (!r.hasResult) return { label: 'Sem nota', tone: 'neutral' };
  return { label: 'Completa', tone: 'ok' };
}

/** O que o bloco KNIGHT mostra: o consolidado das fontes elegíveis avaliadas, ou uma fonte específica. */
export type KnightView = { kind: 'consolidated'; sources: KnightSourceType[] } | { kind: 'source'; source: KnightSourceType };

/** Fontes elegíveis ao consolidado que têm avaliação CONCLUÍDA (a demonstração nunca entra). */
export function consolidableAssessed(sources: readonly KnightSourceLatest[]): KnightSourceType[] {
  return CONSOLIDATION_CANDIDATES.filter((c) => sources.some((s) => s.source === c && !!s.assessment && !s.assessment.isDemo));
}

/**
 * Regra EXPLÍCITA do resultado padrão: o consolidado de todas as fontes elegíveis com avaliação concluída (o construtor do
 * servidor aplica a fórmula sobre a união dos controles — nunca média); sem fonte elegível, a avaliação concluída mais
 * recente. O motivo vai para a tela — nada é escolhido pela posição numa lista.
 */
export function defaultKnightView(sources: readonly KnightSourceLatest[]): { view: KnightView; reason: string } | null {
  const elig = consolidableAssessed(sources);
  if (elig.length > 0)
    return { view: { kind: 'consolidated', sources: elig }, reason: `Consolidado das ${elig.length} fonte(s) elegíveis com avaliação concluída.` };
  const done = sources
    .filter((s) => !!s.assessment)
    .sort((x, y) => Date.parse(y.assessment!.completedAt ?? y.assessment!.startedAt) - Date.parse(x.assessment!.completedAt ?? x.assessment!.startedAt));
  return done[0] ? { view: { kind: 'source', source: done[0].source }, reason: 'Avaliação concluída mais recente (nenhuma fonte elegível ao consolidado).' } : null;
}

export function knightViewKey(v: KnightView | null): string {
  return !v ? '' : v.kind === 'consolidated' ? `consolidated:${[...v.sources].sort().join('+')}` : `source:${v.source}`;
}

export type PlanOrigin = 'knight' | 'nist' | 'other';

export function planOrigin(p: ActionPlan): PlanOrigin {
  if (p.originKind === 'NistFinding' || p.nistOrigin) return 'nist';
  if (p.originKind === 'KnightFinding' || (!p.originKind && !!p.knightIndicatorId)) return 'knight';
  return 'other';
}

export const PLAN_ORIGIN_LABEL: Record<PlanOrigin | 'all', string> = {
  all: 'Todas as origens',
  knight: 'AEGIS KNIGHT',
  nist: 'AEGIS NIST',
  other: 'Dispositivos e outros',
};

export interface PlanTreatment {
  total: number;
  active: number;
  overdue: number;
  awaitingValidation: number;
  inProgress: number;
  completed: number;
  byOrigin: Record<PlanOrigin, number>;
}

/** Tratamento: cada plano conta UMA vez (o id é a identidade), no recorte de origem pedido. */
export function planTreatment(plans: readonly ActionPlan[], origin: PlanOrigin | 'all'): PlanTreatment {
  const unique = [...new Map(plans.map((p) => [p.id, p])).values()];
  const byOrigin: Record<PlanOrigin, number> = { knight: 0, nist: 0, other: 0 };
  for (const p of unique) byOrigin[planOrigin(p)]++;
  const shown = origin === 'all' ? unique : unique.filter((p) => planOrigin(p) === origin);
  return {
    total: shown.length,
    active: shown.filter((p) => p.isActive).length,
    overdue: shown.filter((p) => p.isActive && p.isOverdue).length,
    awaitingValidation: shown.filter((p) => p.status === 'AguardandoValidacao').length,
    inProgress: shown.filter((p) => p.status === 'EmAndamento').length,
    completed: shown.filter((p) => p.status === 'Concluido').length,
    byOrigin,
  };
}

export interface AttentionItem {
  origin: 'KNIGHT' | 'NIST' | 'Plano';
  title: string;
  detail: string;
  route: string[];
  query: Record<string, string | null>;
}

/**
 * Lista curta do que pede atenção, com a origem explícita e pelos critérios determinísticos que já existem: KNIGHT — controles
 * reprovados por severidade e objetos afetados; NIST — maiores lacunas confirmadas com achado aberto; planos atrasados pelo prazo.
 */
export function attentionItems(
  knight: KnightAssessment | null,
  nist: { gaps: NistGap[]; query: Record<string, string | null> } | null,
  plans: readonly ActionPlan[],
  max = 6,
  /** No consolidado, cada achado abre na avaliação da SUA fonte (a que o originou). */
  runBySource: Partial<Record<KnightSourceType, string>> = {},
): AttentionItem[] {
  const k: AttentionItem[] = prioritizedFindings(knight, 3).map((i) => ({
    origin: 'KNIGHT',
    title: i.title,
    detail: `Reprovado · severidade ${severityLabel(i.severity).toLowerCase()}` + (i.affectedObjectCount > 0 ? ` · ${i.affectedComposition ?? `${i.affectedObjectCount} afetado(s)`}` : ''),
    route: ['/knight'],
    query: { run: knight && knight.sourceType !== 'Consolidated' ? knight.id : runBySource[i.sourceType] ?? null, finding: i.indicatorId },
  }));
  const n: AttentionItem[] = (nist?.gaps ?? [])
    .filter((g) => (g.openFindings ?? 0) > 0)
    .sort((x, y) => y.gap - x.gap || x.code.localeCompare(y.code))
    .slice(0, 2)
    .map((g) => ({
      origin: 'NIST',
      title: `${g.code} — ${g.title}`,
      detail: `Lacuna de ${g.gap} nível(is) (atual ${g.currentLevel}, alvo ${g.targetLevel}) · ${g.openFindings} achado(s) aberto(s)`,
      route: ['/nist', g.code.slice(0, 2).toLowerCase(), g.code],
      query: nist!.query,
    }));
  const p: AttentionItem[] = [...plans]
    .filter((x) => x.isActive && x.isOverdue)
    .sort((x, y) => (x.dueDate ?? '').localeCompare(y.dueDate ?? '') || x.id.localeCompare(y.id))
    .slice(0, 2)
    .map((x) => ({
      origin: 'Plano',
      title: x.title,
      detail: `Atrasado desde ${x.dueDate ? x.dueDate.split('-').reverse().join('/') : '—'} · ${PLAN_ORIGIN_LABEL[planOrigin(x)]}`,
      // Plano do KNIGHT abre no próprio KNIGHT; os demais, na visão consolidada de planos (Central de Prioridades).
      route: planOrigin(x) === 'knight' ? ['/knight'] : ['/nist/id/prioridades'],
      query: (planOrigin(x) === 'knight' ? { plan: x.id } : { tab: 'planos' }) as Record<string, string | null>,
    }));
  return [...k, ...n, ...p].slice(0, max);
}
