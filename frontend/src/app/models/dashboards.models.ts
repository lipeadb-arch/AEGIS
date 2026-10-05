// [AEGIS-NIST-JOURNEY-01] Leitura executiva dos assessments (Dashboards) — funções PURAS sobre os contratos reais do
// backend. Nada aqui calcula score: KNIGHT, postura NIST (aegis-score-v1) e maturidade NIST vêm prontos das suas fontes
// e NUNCA são somados ou combinados num indicador "geral".

import { KnightAssessment, KnightIndicator, SeverityLevel } from './knight.models';
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
