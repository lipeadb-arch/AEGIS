// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Leitura executiva de UMA avaliação KNIGHT — funções PURAS sobre o contrato real,
// usadas pelo bloco KNIGHT dos Dashboards e pela visão geral do módulo. Nada aqui calcula nota: nota, cobertura e vereditos
// vêm do servidor; aqui eles só são contados e ditos com a base de cálculo. Lacuna nunca vira conformidade.

import {
  KnightAssessment,
  KnightIndicator,
  KnightLimitationView,
  KnightSourceType,
  findingTitle,
  failureKindLabel,
  isEvaluated,
  limitationViews,
  overviewKpis,
  priorityControls,
  severityLabel,
  sourceStateLabel,
  sourceTypeLabel,
} from './knight.models';

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

// ---- Fontes ------------------------------------------------------------------------------------------------------------

export interface IncludedSource {
  source: KnightSourceType;
  label: string;
  /** Controles com veredito (aprovado, reprovado ou mitigado) que vieram desta fonte. */
  evaluated: number;
  /** Estado da coleta da fonte, em palavras (nulo quando o registro não o tem). */
  state: string | null;
}

/**
 * Fontes INCLUÍDAS no resultado × fontes com controles EFETIVAMENTE avaliados. Incluir uma fonte não é avaliá-la: uma fonte
 * cuja coleta falhou entra na composição e pode não ter nenhum controle avaliado. As contagens saem dos indicadores do próprio
 * resultado (cada um preserva a fonte real), nunca de um número fixo; fonte e serviço são agrupamentos diferentes.
 */
export interface SourceInclusion {
  included: IncludedSource[];
  withEvaluated: IncludedSource[];
  withoutEvaluated: IncludedSource[];
  /** "9 fontes incluídas · 4 com controles avaliados". */
  text: string;
}

export function sourceInclusion(a: KnightAssessment): SourceInclusion {
  const evaluatedFrom = (source: KnightSourceType) => a.indicators.filter((i) => i.sourceType === source && isEvaluated(i)).length;
  const included: IncludedSource[] =
    a.sourceType === 'Consolidated' && a.sources
      ? a.sources
          .filter((s) => s.included)
          .map((s) => ({ source: s.source, label: s.label, evaluated: evaluatedFrom(s.source), state: s.sourceState ? sourceStateLabel(s.sourceState) : null }))
      : [{ source: a.sourceType, label: sourceTypeLabel(a.sourceType), evaluated: a.indicators.filter(isEvaluated).length, state: sourceStateLabel(a.sourceState) }];
  const withEvaluated = included.filter((s) => s.evaluated > 0);
  const withoutEvaluated = included.filter((s) => s.evaluated === 0);
  const head = plural(included.length, 'fonte incluída', 'fontes incluídas');
  const tail = withEvaluated.length === 0 ? 'nenhuma com controles avaliados' : `${withEvaluated.length} com controles avaliados`;
  return { included, withEvaluated, withoutEvaluated, text: `${head} · ${tail}` };
}

// ---- Nota e cobertura --------------------------------------------------------------------------------------------------

export interface ScoreCoverage {
  /** Nota do servidor (nula = sem controle avaliado; nunca zero por ausência). */
  score: number | null;
  evaluated: number;
  /** Base da cobertura: avaliados + não avaliados + erro (não aplicáveis ficam fora). */
  applicable: number;
  coverage: number;
  /** "78 de 374 controles aplicáveis avaliados". */
  fraction: string;
  partial: boolean;
  /** Aviso visível ao lado da nota quando a leitura é parcial ou não tem nota. */
  warning: string | null;
}

export function scoreCoverage(a: KnightAssessment): ScoreCoverage {
  const k = overviewKpis(a);
  const applicable = k.evaluated + k.notEvaluated + k.errors;
  const score = a.score ?? null;
  const fraction = `${k.evaluated} de ${plural(applicable, 'controle aplicável avaliado', 'controles aplicáveis avaliados')}`;
  const collectionProblem = a.sourceState !== 'Completed';
  const partial = k.evaluated < applicable || collectionProblem;
  let warning: string | null = null;
  if (score === null)
    warning = applicable === 0
      ? 'Sem nota: nenhum controle aplicável nesta avaliação.'
      : 'Sem nota: nenhum controle pôde ser avaliado. A ausência de resultado não indica conformidade nem ausência de risco.';
  else if (k.evaluated < applicable)
    warning = `Avaliação parcial: a nota considera ${k.evaluated} de ${applicable} controles aplicáveis. Os ${applicable - k.evaluated} restantes não foram avaliados e não contam como aprovados.`;
  else if (collectionProblem)
    warning = `Coleta com limitação (${sourceStateLabel(a.sourceState).toLowerCase()}): todos os controles aplicáveis foram avaliados, mas alguma leitura da fonte não foi concluída.`;
  return { score, evaluated: k.evaluated, applicable, coverage: a.coverage, fraction, partial, warning };
}

/** Base da aprovação, curta: aprovados ÷ avaliados (não é a nota, que também pesa a severidade). */
export function approvalBasis(a: KnightAssessment): string {
  const k = overviewKpis(a);
  return k.evaluated === 0
    ? 'Sem controle avaliado: não há aprovação a calcular.'
    : `${k.passed} aprovado(s) de ${plural(k.evaluated, 'controle avaliado', 'controles avaliados')}.`;
}

// ---- Prioridades -------------------------------------------------------------------------------------------------------

/** Um controle reprovado prioritário com o que o modelo sustenta: risco, impacto, afetados e orientação (nada inventado). */
export interface PriorityView {
  id: string;
  title: string;
  severity: KnightIndicator['severity'];
  severityLabel: string;
  sourceType: KnightSourceType;
  service: string;
  risk: string | null;
  impact: string | null;
  affected: string | null;
  guidance: string | null;
}

export function priorityViews(a: KnightAssessment, max = 5): PriorityView[] {
  return priorityControls(a, max).map((i) => ({
    id: i.indicatorId,
    title: findingTitle(i),
    severity: i.severity,
    severityLabel: severityLabel(i.severity),
    sourceType: i.sourceType,
    service: i.presentation?.service ?? sourceTypeLabel(i.sourceType),
    risk: i.presentation?.rationale?.trim() || null,
    impact: i.presentation?.impact?.trim() || null,
    affected: i.affectedComposition ?? (i.affectedObjectCount > 0 ? plural(i.affectedObjectCount, 'item afetado', 'itens afetados') : null),
    guidance: i.recommendation?.trim() || null,
  }));
}

/**
 * Onde abrir um achado: na avaliação exibida ou, no consolidado, na avaliação da FONTE que o originou (o consolidado é uma
 * leitura ao vivo, sem Id próprio). Sem a execução da fonte, o endereço não fixa uma avaliação.
 */
export function findingQuery(
  a: KnightAssessment,
  indicatorId: string,
  runBySource: Partial<Record<KnightSourceType, string>> = {},
): Record<string, string | null> {
  const i = a.indicators.find((x) => x.indicatorId === indicatorId);
  const run = a.sourceType !== 'Consolidated' ? a.id : i ? runBySource[i.sourceType] ?? null : null;
  return { run, finding: indicatorId };
}

// ---- Limitações --------------------------------------------------------------------------------------------------------

/**
 * Limitações EQUIVALENTES agrupadas: mesmo desfecho, mesmo tipo de falha, mesma orientação e mesmas fontes. Falhas diferentes
 * com mensagens parecidas (tempo esgotado × circuito aberto, permissão × licença) ficam em grupos separados, e cada
 * capacidade continua consultável com o próprio detalhe técnico.
 */
export interface LimitationGroup {
  key: string;
  cause: string;
  /** Tipo sanitizado da falha, quando a coleta o registrou. */
  kind: string | null;
  guidance: string;
  sources: string[];
  items: KnightLimitationView[];
  affectedControls: string[];
}

export function limitationGroups(a: KnightAssessment): LimitationGroup[] {
  const sourcesOf = (l: KnightLimitationView): string[] => {
    if (a.sourceType !== 'Consolidated') return [sourceTypeLabel(a.sourceType)];
    const set = new Set(a.indicators.filter((i) => l.affectedControls.includes(i.indicatorId)).map((i) => sourceTypeLabel(i.sourceType)));
    return [...set].sort((x, y) => x.localeCompare(y));
  };
  const groups = new Map<string, LimitationGroup>();
  for (const l of limitationViews(a)) {
    const sources = sourcesOf(l);
    const key = [l.outcome, l.failureKind ?? '', l.guidance, sources.join('+')].join('|');
    const g = groups.get(key) ?? { key, cause: l.cause, kind: failureKindLabel(l.failureKind), guidance: l.guidance, sources, items: [], affectedControls: [] };
    g.items.push(l);
    for (const id of l.affectedControls) if (!g.affectedControls.includes(id)) g.affectedControls.push(id);
    groups.set(key, g);
  }
  return [...groups.values()].sort(
    (x, y) => y.affectedControls.length - x.affectedControls.length || y.items.length - x.items.length || x.cause.localeCompare(y.cause),
  );
}

/**
 * Texto para quando não há limitação a listar. Ausência de registro não é coleta comprovada: só se afirma que tudo foi lido quando
 * a avaliação registra capacidades e TODAS saíram coletadas; sem esse registro, diz-se apenas que nada foi registrado.
 */
export function noLimitationText(a: KnightAssessment): string {
  if (a.capabilities.length > 0 && a.capabilities.every((c) => c.outcome === 'Collected'))
    return a.capabilities.length === 1
      ? 'A única capacidade de coleta registrada nesta avaliação foi lida.'
      : `Todas as ${a.capabilities.length} capacidades de coleta registradas nesta avaliação foram lidas.`;
  const k = overviewKpis(a);
  const unevaluated = k.notEvaluated + k.errors;
  return 'Não há limitações de coleta registradas nesta avaliação.' + (unevaluated > 0
    ? ` Isso não comprova coleta completa: ${plural(unevaluated, 'controle aplicável ficou', 'controles aplicáveis ficaram')} sem avaliação.`
    : '');
}

// ---- Interpretação -----------------------------------------------------------------------------------------------------

/**
 * Origem da interpretação consultiva, dita pelo conteúdo que EXISTE: gerada por IA, resumo determinístico do AEGIS ou nenhuma.
 * Sem conteúdo não há o que rotular — nunca se anuncia um "fallback disponível" vazio.
 */
export type AdvisoryOrigin = { kind: 'ai' | 'deterministic'; label: string } | { kind: 'none'; label: null };

export function advisoryOrigin(a: KnightAssessment, finalized = true): AdvisoryOrigin {
  const ai = a.advisory;
  const content = !!ai && (!!ai.executiveSummary?.trim() || ai.priorityRisks.length + ai.recommendedActions.length + ai.collectionGaps.length > 0);
  if (!finalized || !content) return { kind: 'none', label: null };
  return a.advisoryFromAi ? { kind: 'ai', label: 'Gerada por IA' } : { kind: 'deterministic', label: 'Resumo determinístico do AEGIS' };
}
