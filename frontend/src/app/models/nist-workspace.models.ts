// [AEGIS-NIST-RECOVER-GUIDE-01] Funções PURAS da área de trabalho de uma função do NIST com roteiro editorial (lista contextual
// de subcategorias + detalhe). Tudo aqui é APRESENTAÇÃO derivada de estados que já existem — avaliação, revisão, achados e
// planos —; nada cria etapa, aprovação ou pendência nova. Ausência de nível continua sendo ausência (nunca zero).

import { interviewGuideFor } from './nist-interview-guide';
import {
  NistReviewState,
  NistSubcategoryDetail,
  NistSubcategoryRow,
  levelLabel,
  reviewBadgeClass,
  reviewStateLabel,
  stateBadgeClass,
  stateLabel,
} from './nist.models';

export type NistTrackKey = 'avaliacao' | 'revisao' | 'tratamento';

/** Um item da faixa de acompanhamento: rótulo, situação em texto (a cor só reforça) e a seção da tela que o detalha. */
export interface NistTrackStep {
  key: NistTrackKey;
  label: string;
  status: string;
  detail: string | null;
  tone: string;
  target: string;
}

/**
 * Faixa Avaliação · Revisão · Tratamento. Não é uma sequência obrigatória: "sem achados" e "sem decisão do revisor" são
 * situações neutras; a revisão desatualizada e a devolução continuam distinguíveis pelos rótulos existentes.
 */
export function trackingSteps(d: NistSubcategoryDetail): NistTrackStep[] {
  const e = d.evaluation;
  const evaluation: NistTrackStep = e
    ? {
        key: 'avaliacao',
        label: 'Avaliação',
        status: stateLabel(e.state),
        detail: [
          e.notApplicable ? 'não se aplica ao escopo' : `atual ${levelLabel(e.currentLevel)} · alvo ${levelLabel(e.targetLevel)}`,
          e.humanConfirmed === false ? 'conteúdo a confirmar' : null,
        ].filter((x): x is string => !!x).join(' · '),
        tone: stateBadgeClass(e.state),
        target: 'sc-eval',
      }
    : { key: 'avaliacao', label: 'Avaliação', status: stateLabel('NotEvaluated'), detail: 'sem registro nesta rodada', tone: stateBadgeClass('NotEvaluated'), target: 'sc-eval' };

  const rs: NistReviewState = e?.reviewState ?? 'None';
  const review: NistTrackStep = {
    key: 'revisao',
    label: 'Revisão',
    status: reviewStateLabel(rs),
    detail: e?.reviewerName ? `revisor ${e.reviewerName}` : 'revisor não designado',
    tone: reviewBadgeClass(rs),
    target: 'sc-rev',
  };

  const findings = d.findings ?? [];
  let treatment: NistTrackStep;
  if (findings.length === 0) {
    treatment = { key: 'tratamento', label: 'Tratamento', status: 'Sem achados registrados', detail: null, tone: 'neutral', target: 'achados' };
  } else {
    const open = findings.filter((f) => f.status === 'Open').length;
    const withPlan = findings.filter((f) => !!f.plan).length;
    const overdue = findings.filter((f) => f.plan?.isOverdue).length;
    treatment = {
      key: 'tratamento',
      label: 'Tratamento',
      status: `${findings.length} achado(s)`,
      detail: [`${open} aberto(s)`, `${withPlan} com plano`, overdue ? `${overdue} plano(s) vencido(s)` : null]
        .filter((x): x is string => !!x)
        .join(' · '),
      tone: overdue ? 'warn' : 'neutral',
      target: 'achados',
    };
  }
  return [evaluation, review, treatment];
}

/** Título curto da lista contextual: o do roteiro editorial, quando existe; senão o título do catálogo. */
export function navTitle(row: Pick<NistSubcategoryRow, 'code' | 'title'>): string {
  return interviewGuideFor(row.code)?.title ?? row.title;
}

/** Níveis compactos de um item da lista ("atual 2 · alvo 4"); nulo quando não há nível algum ou não se aplica. */
export function navLevelText(row: Pick<NistSubcategoryRow, 'state' | 'currentLevel' | 'targetLevel'>): string | null {
  if (row.state === 'NotApplicable') return null;
  if (row.currentLevel === null && row.targetLevel === null) return null;
  const n = (v: number | null) => (v === null ? 'sem nível' : String(v));
  return `atual ${n(row.currentLevel)} · alvo ${n(row.targetLevel)}`;
}

/** Seções do detalhe, na ordem da tela — atalhos para superfícies que JÁ existem (nenhum formulário duplicado). */
export const NIST_DETAIL_SECTIONS: readonly { id: string; label: string }[] = [
  { id: 'sc-eval', label: 'Avaliação' },
  { id: 'sc-rev', label: 'Revisão' },
  { id: 'procedimentos', label: 'Procedimentos' },
  { id: 'assistencia', label: 'Assistência de IA' },
  { id: 'sc-ev', label: 'Evidências' },
  { id: 'achados', label: 'Achados e planos' },
  { id: 'historico', label: 'Histórico' },
];
