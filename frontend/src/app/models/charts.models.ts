// [AEGIS-ASSESSMENT-VISUALS-01] Gráficos do app — funções PURAS sobre os contratos reais do backend. Nada aqui calcula nota,
// maturidade ou cobertura: os números vêm prontos das avaliações (KNIGHT), do perfil NIST e da evolução mensal; aqui eles só
// são organizados em categorias e séries, e a geometria do SVG é calculada (testável sem navegador). Ausência é nula — nunca
// zero —, cada gráfico declara escala, base e critério de ordenação, e KNIGHT (0–100) e NIST (1–5) nunca dividem um eixo.

import { KnightAssessment, distributionBy, overviewKpis } from './knight.models';
import { NIST_FUNCTIONS, NistProfile } from './nist.models';
import { MonthlyCell, PostureMonthlyPoint, PostureMonthlySeries, monthShort, monthlyCells, seriesValue } from './posture-history.models';

export type ChartKind = 'columns' | 'hbars' | 'stacked' | 'donut' | 'line' | 'radar';

/** Classes de cor (tokens do AEGIS no CSS do componente). A cor nunca é o único portador da informação. */
export type ChartTone =
  | 'acc' | 'ok' | 'bad' | 'warn' | 'ne' | 'err' | 'na' | 'cur' | 'tgt' | 'prog'
  | 'sev-Critical' | 'sev-High' | 'sev-Medium' | 'sev-Low' | 'sev-Informational';

/** Destino do detalhe de uma categoria: rota do app e parâmetros. */
export interface ChartLink {
  route: string[];
  query?: Record<string, string | null>;
}

export interface ChartSeries {
  key: string;
  label: string;
  tone: ChartTone;
  values: (number | null)[];
  /** Só em linha: `connect[i]` liga o ponto i ao i−1 (mês vizinho e comparável). */
  connect?: boolean[];
  dashed?: boolean;
}

export interface ChartSpec {
  id: string;
  kind: ChartKind;
  title: string;
  description: string;
  /** Unidade dos valores por extenso ("controles", "nível 1–5", "nota 0–100"). */
  unit: string;
  min: number;
  max: number;
  categories: string[];
  series: ChartSeries[];
  basis?: string | null;
  links?: (ChartLink | null)[];
  categoryTones?: ChartTone[];
  notes?: string[];
  decimals: number;
  /** Texto para valor ausente ("sem avaliação"). */
  emptyLabel: string;
  /** Nada a desenhar: a figura mostra só esta mensagem. */
  emptyMessage?: string | null;
}

// ---- Formatação (pt-BR) -----------------------------------------------------------------------------------------------

export function fmt(v: number | null | undefined, decimals = 0): string {
  if (v === null || v === undefined) return '—';
  return v.toLocaleString('pt-BR', { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

export function pct(v: number, total: number): string {
  return total <= 0 ? '—' : `${(Math.round((1000 * v) / total) / 10).toLocaleString('pt-BR', { maximumFractionDigits: 1 })}%`;
}

/** Máximo "redondo" (≥ 1) do eixo de contagens. */
export function niceMax(values: number[]): number {
  const max = Math.max(0, ...values);
  if (max <= 4) return Math.max(1, Math.ceil(max));
  const mag = Math.pow(10, Math.floor(Math.log10(max)));
  for (const step of [1, 2, 2.5, 5, 10]) if (step * mag >= max) return step * mag;
  return Math.ceil(max);
}

/** Marcas do eixo: inteiros na escala 1–5; cinco divisões nas contagens e na nota 0–100. */
export function ticks(c: Pick<ChartSpec, 'min' | 'max'>): number[] {
  if (c.max - c.min <= 5 && Number.isInteger(c.max)) {
    const out: number[] = [];
    for (let t = c.min; t <= c.max; t++) out.push(t);
    return out;
  }
  const step = (c.max - c.min) / 4;
  return [0, 1, 2, 3, 4].map((i) => Math.round((c.min + step * i) * 10) / 10);
}

export function categoryTotal(c: ChartSpec, i: number): number {
  return c.series.reduce((s, x) => s + (x.values[i] ?? 0), 0);
}

/** Alternativa textual: os valores na ordem do desenho. */
export function chartSummary(c: ChartSpec): string {
  if (c.kind === 'donut') {
    const total = c.series[0].values.reduce<number>((s, v) => s + (v ?? 0), 0);
    return `Total ${fmt(total)} ${c.unit}: ` + c.categories.map((cat, i) => `${cat} ${fmt(c.series[0].values[i] ?? 0)} (${pct(c.series[0].values[i] ?? 0, total)})`).join('; ') + '.';
  }
  return (
    c.categories
      .map((cat, i) => `${cat}: ` + c.series.map((s) => (c.series.length > 1 ? `${s.label} ` : '') + (s.values[i] === null ? c.emptyLabel : fmt(s.values[i], c.decimals))).join(', '))
      .join('; ') + `. Escala: ${c.unit}.`
  );
}

// ---- KNIGHT -----------------------------------------------------------------------------------------------------------

const STATUS_SERIES: { key: 'Exposed' | 'Mitigated' | 'Error' | 'NotEvaluated' | 'Passed' | 'NotApplicable'; label: string; tone: ChartTone }[] = [
  { key: 'Exposed', label: 'Reprovado', tone: 'bad' },
  { key: 'Mitigated', label: 'Mitigado (atenção)', tone: 'warn' },
  { key: 'Error', label: 'Erro na avaliação', tone: 'err' },
  { key: 'NotEvaluated', label: 'Não avaliado', tone: 'ne' },
  { key: 'Passed', label: 'Aprovado', tone: 'ok' },
  { key: 'NotApplicable', label: 'Não aplicável', tone: 'na' },
];

/** O detalhe no KNIGHT: a mesma avaliação (ou o modo consolidado) com a aba de controles já filtrada. */
function knightLink(a: KnightAssessment, filters: Record<string, string>): ChartLink {
  const consolidated = a.sourceType === 'Consolidated';
  return { route: ['/knight'], query: { run: consolidated ? null : a.id, modo: consolidated ? 'consolidado' : null, ...filters } };
}

/** Rosca: avaliados × não avaliados × erro DENTRO da base aplicável (não aplicáveis ficam fora e são ditos). */
export function knightCoverageDonut(a: KnightAssessment): ChartSpec {
  const k = overviewKpis(a);
  const applicable = k.evaluated + k.notEvaluated + k.errors;
  return {
    id: 'knight-cobertura',
    kind: 'donut',
    title: 'Cobertura da avaliação',
    description: `Controles avaliados dentro da base aplicável. Cobertura ${fmt(a.coverage, 1)}%.`,
    unit: 'controles',
    min: 0,
    max: applicable,
    categories: ['Avaliados', 'Não avaliados', 'Erro na avaliação'],
    series: [{ key: 'controles', label: 'Controles', tone: 'acc', values: [k.evaluated, k.notEvaluated, k.errors] }],
    categoryTones: ['acc', 'ne', 'err'],
    basis: `Base: ${applicable} controle(s) aplicável(is); ${k.notApplicable} não aplicável(is) ficam fora da base.`,
    links: [null, knightLink(a, { status: 'NotEvaluated' }), knightLink(a, { status: 'Error' })],
    decimals: 0,
    emptyLabel: 'sem dado',
    emptyMessage: applicable === 0 ? 'Nenhum controle aplicável nesta avaliação.' : null,
  };
}

/** Barras empilhadas: resultado dos controles por serviço, em contagem absoluta, com o total da linha. */
export function knightServiceStacked(a: KnightAssessment, limit = 10): ChartSpec {
  const rows = distributionBy(a, 'service');
  const shown = rows.slice(0, limit);
  return {
    id: 'knight-servicos',
    kind: 'stacked',
    title: 'Resultado dos controles por serviço',
    description: 'Contagem absoluta por resultado em cada serviço; o número à direita é o total do serviço.',
    unit: 'controles',
    min: 0,
    max: Math.max(1, ...shown.map((r) => r.total)),
    categories: shown.map((r) => r.label),
    series: STATUS_SERIES.map((s) => ({ key: s.key, label: s.label, tone: s.tone, values: shown.map((r) => r.counts[s.key]) })),
    basis:
      `Ordenado pela quantidade de reprovados e, no empate, pelo total. ${a.indicators.length} controle(s) no total` +
      (rows.length > shown.length ? `; mostrando ${shown.length} de ${rows.length} serviços.` : '.'),
    links: shown.map((r) => knightLink(a, { service: r.key })),
    decimals: 0,
    emptyLabel: 'sem dado',
    emptyMessage: rows.length === 0 ? 'Nenhum controle nesta avaliação.' : null,
  };
}

/** Colunas: controles com achados (reprovados ou mitigados) por severidade. */
export function knightSeverityColumns(a: KnightAssessment): ChartSpec {
  const k = overviewKpis(a);
  return {
    id: 'knight-severidade',
    kind: 'columns',
    title: 'Controles com achados por severidade',
    description: 'Controles reprovados ou mitigados em cada severidade (contagem de controles, não de itens afetados).',
    unit: 'controles',
    min: 0,
    max: niceMax(k.findingsBySeverity.map((x) => x.count)),
    categories: k.findingsBySeverity.map((x) => x.label),
    series: [{ key: 'achados', label: 'Controles com achados', tone: 'bad', values: k.findingsBySeverity.map((x) => x.count) }],
    categoryTones: k.findingsBySeverity.map((x) => `sev-${x.key}` as ChartTone),
    basis: `Total: ${k.findings} controle(s) com achados (${k.failed} reprovado(s), ${k.mitigated} mitigado(s)).`,
    links: k.findingsBySeverity.map((x) => knightLink(a, { status: 'findings', severity: x.key })),
    decimals: 0,
    emptyLabel: 'sem dado',
    emptyMessage: k.findings === 0 ? 'Nenhum controle com achado nesta avaliação.' : null,
  };
}

/** Barras horizontais: ranking dos domínios pela quantidade de controles reprovados (critério explícito). */
export function knightDomainBars(a: KnightAssessment, limit = 8): ChartSpec {
  const all = distributionBy(a, 'domain');
  const rows = all
    .filter((r) => r.counts.Exposed > 0)
    .sort((x, y) => y.counts.Exposed - x.counts.Exposed || y.total - x.total || x.label.localeCompare(y.label))
    .slice(0, limit);
  return {
    id: 'knight-dominios',
    kind: 'hbars',
    title: 'Domínios com mais controles reprovados',
    description: `Ranking pela quantidade de controles reprovados (até ${limit}; empate pelo total de controles).`,
    unit: 'controles reprovados',
    min: 0,
    max: niceMax(rows.map((r) => r.counts.Exposed)),
    categories: rows.map((r) => r.label),
    series: [{ key: 'reprovados', label: 'Controles reprovados', tone: 'bad', values: rows.map((r) => r.counts.Exposed) }],
    basis: `${all.filter((r) => r.counts.Exposed > 0).length} de ${all.length} domínio(s) têm controle reprovado.`,
    links: rows.map((r) => knightLink(a, { status: 'Exposed', domain: r.key })),
    decimals: 0,
    emptyLabel: 'sem dado',
    emptyMessage: rows.length === 0 ? 'Nenhum controle reprovado nesta avaliação.' : null,
  };
}

// ---- NIST -------------------------------------------------------------------------------------------------------------

export interface NistLinkContext {
  avaliacao: string;
  rodada: string | null;
  escopo: string;
}

function nistQuery(ctx: NistLinkContext, extra: Record<string, string> = {}): Record<string, string | null> {
  return { avaliacao: ctx.avaliacao, rodada: ctx.rodada, escopo: ctx.escopo, ...extra };
}

/** Colunas agrupadas: atual × alvo (1–5) nas seis funções, na ordem oficial; função sem avaliação fica sem coluna. */
export function nistFunctionColumns(p: NistProfile, ctx: NistLinkContext): ChartSpec {
  const fns = NIST_FUNCTIONS.map((f) => ({ meta: f, score: p.functions.find((x) => x.code === f.code) ?? null }));
  return {
    id: 'nist-funcoes',
    kind: 'columns',
    title: 'Atual × alvo nas seis funções',
    description: 'Médias confirmadas por revisão humana, na escala 1 a 5 da metodologia do AEGIS. Função sem avaliação fica sem coluna.',
    unit: 'nível 1–5',
    min: 0,
    max: 5,
    categories: fns.map((f) => f.meta.code),
    series: [
      { key: 'atual', label: 'Atual', tone: 'cur', values: fns.map((f) => f.score?.current ?? null) },
      { key: 'alvo', label: 'Alvo', tone: 'tgt', values: fns.map((f) => f.score?.target ?? null) },
    ],
    basis: fns.map((f) => `${f.meta.code}: ${f.score?.evaluated ?? 0}/${f.score?.subcategories ?? 0} avaliadas`).join(' · '),
    links: fns.map((f) => ({ route: ['/nist', f.meta.slug], query: nistQuery(ctx) })),
    decimals: 1,
    emptyLabel: 'sem avaliação',
    emptyMessage: fns.every((f) => f.score?.current == null && f.score?.target == null) ? 'Nenhuma função tem avaliação confirmada nesta rodada.' : null,
  };
}

/** Radar complementar: as MESMAS médias das colunas (no máximo duas séries, escala 1–5 fixa). */
export function nistRadar(p: NistProfile, ctx: NistLinkContext): ChartSpec {
  return {
    ...nistFunctionColumns(p, ctx),
    id: 'nist-radar',
    kind: 'radar',
    title: 'Perfil atual × alvo (radar)',
    description: 'As mesmas médias das colunas, em perfil. Função sem avaliação não vira zero: o perfil fica aberto nela.',
  };
}

const NIST_STATE_PARTS: { key: 'evaluated' | 'notApplicable' | 'pendingConfirmation' | 'inProgress' | 'notEvaluated'; label: string; tone: ChartTone }[] = [
  { key: 'evaluated', label: 'Avaliadas', tone: 'acc' },
  { key: 'notApplicable', label: 'Não se aplicam', tone: 'na' },
  { key: 'pendingConfirmation', label: 'Aguardando confirmação', tone: 'warn' },
  { key: 'inProgress', label: 'Em andamento', tone: 'prog' },
  { key: 'notEvaluated', label: 'Sem avaliação', tone: 'ne' },
];

/** Rosca: situação das subcategorias do escopo nesta rodada (inclui "não se aplicam"), sobre o total do catálogo. */
export function nistStatesDonut(p: NistProfile): ChartSpec | null {
  const st = p.states;
  if (!st) return null;
  return {
    id: 'nist-situacao',
    kind: 'donut',
    title: 'Situação das subcategorias',
    description: 'Estado de cada subcategoria do catálogo nesta rodada e escopo.',
    unit: 'subcategorias',
    min: 0,
    max: st.subcategories,
    categories: NIST_STATE_PARTS.map((x) => x.label),
    series: [{ key: 'subcategorias', label: 'Subcategorias', tone: 'acc', values: NIST_STATE_PARTS.map((x) => st[x.key]) }],
    categoryTones: NIST_STATE_PARTS.map((x) => x.tone),
    basis: `Base: ${st.subcategories} subcategoria(s) do catálogo no escopo.`,
    decimals: 0,
    emptyLabel: 'sem dado',
    emptyMessage: st.subcategories === 0 ? 'O escopo não tem subcategorias.' : null,
  };
}

/** Barras empilhadas: andamento da avaliação por função (contagem absoluta, ordem oficial das funções). */
export function nistProgressStacked(p: NistProfile, ctx: NistLinkContext): ChartSpec | null {
  const fs = p.functionStates;
  if (!fs || fs.length === 0) return null;
  const rows = NIST_FUNCTIONS.map((f) => ({ meta: f, st: fs.find((x) => x.code === f.code) ?? null }));
  return {
    id: 'nist-andamento',
    kind: 'stacked',
    title: 'Andamento da avaliação por função',
    description: 'Subcategorias por situação em cada função; o número à direita é o total da função.',
    unit: 'subcategorias',
    min: 0,
    max: Math.max(1, ...rows.map((r) => r.st?.subcategories ?? 0)),
    categories: rows.map((r) => `${r.meta.code} — ${r.meta.label}`),
    series: NIST_STATE_PARTS.map((x) => ({ key: x.key, label: x.label, tone: x.tone, values: rows.map((r) => r.st?.[x.key] ?? 0) })),
    basis: 'Ordem fixa das funções do NIST CSF 2.0 (GV, ID, PR, DE, RS, RC).',
    links: rows.map((r) => ({ route: ['/nist', r.meta.slug], query: nistQuery(ctx) })),
    decimals: 0,
    emptyLabel: 'sem dado',
  };
}

/** Barras horizontais: maiores lacunas confirmadas (alvo − atual), com o critério de ordenação dito. */
export function nistGapBars(p: NistProfile, ctx: NistLinkContext, limit = 8): ChartSpec {
  const gaps = [...p.gaps].sort((x, y) => y.gap - x.gap || x.code.localeCompare(y.code)).slice(0, limit);
  return {
    id: 'nist-lacunas',
    kind: 'hbars',
    title: 'Maiores lacunas confirmadas',
    description: 'Distância entre alvo e atual, em níveis, nas subcategorias com os dois valores confirmados. Lacuna é meta de melhoria, não falha por si.',
    unit: 'níveis de lacuna',
    min: 0,
    max: 4,
    categories: gaps.map((g) => `${g.code} — ${g.title}`),
    series: [{ key: 'lacuna', label: 'Lacuna (alvo − atual)', tone: 'tgt', values: gaps.map((g) => g.gap) }],
    basis: `Ordenado pela lacuna e, no empate, pelo código (até ${limit}).` + (p.indeterminateGaps > 0 ? ` ${p.indeterminateGaps} subcategoria(s) sem atual ou alvo confirmado ficam fora.` : ''),
    links: gaps.map((g) => ({ route: ['/nist', g.code.slice(0, 2).toLowerCase(), g.code], query: nistQuery(ctx) })),
    decimals: 0,
    emptyLabel: 'sem dado',
    emptyMessage: gaps.length === 0 ? 'Nenhuma lacuna confirmada (atual e alvo registrados por revisão humana).' : null,
  };
}

// ---- Histórico (os dois instrumentos, sempre em gráficos separados) ---------------------------------------------------

/**
 * Linha mensal de UMA série: mês sem publicação fica sem ponto e só meses vizinhos comparáveis são ligados. Na maturidade NIST
 * há a linha do alvo (tracejada) — nunca no mesmo eixo de uma nota 0–100.
 */
export function historyLine(series: PostureMonthlySeries, months: string[]): ChartSpec {
  const maturity = series.type === 'NistMaturity';
  const cells: MonthlyCell[] = monthlyCells(months, series.points, (p) => seriesValue(series.type, p));
  const value = (c: MonthlyCell, f: (p: PostureMonthlyPoint) => number | null | undefined) => (c.point ? f(c.point) ?? null : null);
  const connectOf = (f: (p: PostureMonthlyPoint) => number | null | undefined) =>
    cells.map((c, i) => i > 0 && !!c.point && !!cells[i - 1].point && c.point.comparableWithPrevious && value(c, f) !== null && value(cells[i - 1], f) !== null);
  const s: ChartSeries[] = maturity
    ? [
        { key: 'atual', label: 'Maturidade atual', tone: 'cur', values: cells.map((c) => value(c, (p) => p.maturityCurrent)), connect: connectOf((p) => p.maturityCurrent) },
        { key: 'alvo', label: 'Alvo', tone: 'tgt', values: cells.map((c) => value(c, (p) => p.maturityTarget)), connect: connectOf((p) => p.maturityTarget), dashed: true },
      ]
    : [{ key: 'valor', label: series.type === 'Knight' ? 'Nota KNIGHT' : 'AEGIS Score', tone: 'acc', values: cells.map((c) => value(c, (p) => p.score)), connect: connectOf((p) => p.score) }];
  const breaks = series.points.filter((p) => p.breakReasons.length > 0).map((p) => `${monthShort(p.month)}: série recomeça.`);
  return {
    id: `historico-${series.semanticFamily}`,
    kind: 'line',
    title: maturity ? 'Maturidade (1–5)' : series.type === 'Knight' ? 'Nota KNIGHT (0–100)' : 'AEGIS Score (0–100)',
    description: `${series.label}. Cada ponto é a última publicação do mês; mês sem publicação fica sem ponto.`,
    unit: maturity ? 'nível 1–5' : 'nota 0–100',
    min: series.scaleMin ?? (maturity ? 1 : 0),
    max: series.scaleMax ?? (maturity ? 5 : 100),
    categories: months.map(monthShort),
    series: s,
    basis: series.coverageBasis ?? null,
    notes: breaks,
    decimals: maturity ? 1 : 0,
    emptyLabel: 'sem publicação',
    emptyMessage: series.points.length === 0 ? 'Nenhuma publicação desta série no período escolhido.' : null,
  };
}

// ---- Geometria --------------------------------------------------------------------------------

// Desenhada na LARGURA REAL do container (1 unidade = 1 px): o texto tem o tamanho do CSS em qualquer tela, sem o SVG encolher
// em 375 px. Abaixo de ~400 px a rosca empilha a legenda e a linha mensal alterna os rótulos dos meses.
export const CHART_W = 440;
export const CHART_MIN_W = 280;

export type Shape =
  | { k: 'rect'; cls: string; x: number; y: number; w: number; h: number; title?: string }
  | { k: 'line'; cls: string; x1: number; y1: number; x2: number; y2: number }
  | { k: 'circle'; cls: string; x: number; y: number; r: number; title?: string }
  | { k: 'path'; cls: string; d: string; title?: string }
  | { k: 'polygon'; cls: string; points: string }
  | { k: 'text'; cls: string; x: number; y: number; text: string; anchor: 'start' | 'middle' | 'end' };

/** Área clicável/focável de uma categoria com link (desenhada por cima, transparente). */
export interface HitArea {
  i: number;
  x: number;
  y: number;
  w: number;
  h: number;
  label: string;
}

export interface ChartLayout {
  width: number;
  height: number;
  shapes: Shape[];
  hits: HitArea[];
}

const r1 = (v: number) => Math.round(v * 10) / 10;

function yOf(v: number, c: ChartSpec, y0: number, y1: number): number {
  const clamped = Math.min(c.max, Math.max(c.min, v));
  return y1 - ((clamped - c.min) / Math.max(1e-9, c.max - c.min)) * (y1 - y0);
}

function clip(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max - 1).trimEnd() + '…';
}

function grid(c: ChartSpec, x0: number, x1: number, y0: number, y1: number, out: Shape[]): void {
  for (const t of ticks(c)) {
    const y = r1(yOf(t, c, y0, y1));
    out.push({ k: 'line', cls: 'g', x1: x0, y1: y, x2: x1, y2: y });
    out.push({ k: 'text', cls: 'sm', x: x0 - 5, y: y + 3.5, text: fmt(t, Number.isInteger(t) ? 0 : 1), anchor: 'end' });
  }
}

export function layoutChart(c: ChartSpec, width = CHART_W): ChartLayout {
  const W = Math.max(CHART_MIN_W, Math.round(width));
  const l = (() => {
    switch (c.kind) {
      case 'columns':
        return columns(c, W);
      case 'hbars':
        return bars(c, false, W);
      case 'stacked':
        return bars(c, true, W);
      case 'donut':
        return donut(c, W);
      case 'line':
        return line(c, W);
      case 'radar':
        return radar(c, W);
    }
  })();
  return { width: W, ...l };
}

function hasLink(c: ChartSpec, i: number): boolean {
  return !!c.links && !!c.links[i];
}

type Raw = Omit<ChartLayout, 'width'>;

function columns(c: ChartSpec, W: number): Raw {
  const h = 230, x0 = 34, y0 = 18, y1 = h - 42, x1 = W - 6;
  const out: Shape[] = [];
  const hits: HitArea[] = [];
  grid(c, x0, x1, y0, y1, out);
  const n = c.categories.length;
  const group = (x1 - x0) / Math.max(1, n);
  const k = c.series.length;
  const bw = Math.min(34, (group * 0.72) / Math.max(1, k));
  for (let i = 0; i < n; i++) {
    const gx = x0 + group * i + (group - bw * k) / 2;
    c.series.forEach((s, j) => {
      const x = r1(gx + bw * j);
      const v = s.values[i];
      const tone = k === 1 && c.categoryTones ? c.categoryTones[i] : s.tone;
      if (v !== null) {
        const y = r1(yOf(v, c, y0, y1));
        out.push({ k: 'rect', cls: `f-${tone}`, x, y, w: r1(bw - 2), h: r1(Math.max(0.5, y1 - y)), title: `${c.categories[i]} · ${s.label}: ${fmt(v, c.decimals)}` });
        out.push({ k: 'text', cls: 'val', x: r1(x + (bw - 2) / 2), y: y - 4, text: fmt(v, c.decimals), anchor: 'middle' });
      } else {
        out.push({ k: 'text', cls: 'sm', x: r1(x + (bw - 2) / 2), y: y1 - 4, text: '—', anchor: 'middle' });
      }
    });
    out.push({ k: 'text', cls: 'txt', x: r1(x0 + group * i + group / 2), y: y1 + 16, text: clip(c.categories[i], Math.max(4, Math.floor(group / 7))), anchor: 'middle' });
    if (hasLink(c, i)) hits.push({ i, x: r1(x0 + group * i), y: y0, w: r1(group), h: y1 - y0 + 22, label: c.categories[i] });
  }
  if (c.series.some((s) => s.values.some((v) => v === null))) out.push({ k: 'text', cls: 'sm', x: x1, y: h - 6, text: `— = ${c.emptyLabel}`, anchor: 'end' });
  return { height: h, shapes: out, hits };
}

function bars(c: ChartSpec, stacked: boolean, W: number): Raw {
  const row = 36, top = 6, x0 = 2, barH = 14, x1 = W - 44;
  const n = c.categories.length;
  const h = top + row * n + 6;
  const out: Shape[] = [];
  const hits: HitArea[] = [];
  const max = c.max <= 0 ? 1 : c.max;
  for (let i = 0; i < n; i++) {
    const y = top + row * i;
    out.push({ k: 'text', cls: 'txt', x: x0, y: y + 11, text: clip(c.categories[i], Math.floor((W - 6) / 6.6)), anchor: 'start' });
    out.push({ k: 'rect', cls: 'track', x: x0, y: y + 16, w: x1 - x0, h: barH });
    let x = x0;
    let total = 0;
    for (const s of c.series) {
      const v = s.values[i];
      if (v === null || v <= 0) continue;
      total += v;
      const w = ((x1 - x0) * v) / max;
      const tone = !stacked && c.series.length === 1 && c.categoryTones ? c.categoryTones[i] : s.tone;
      out.push({ k: 'rect', cls: `f-${tone}`, x: r1(x), y: y + 16, w: r1(w), h: barH, title: `${c.categories[i]} · ${s.label}: ${fmt(v, c.decimals)}` });
      if (stacked && w >= 18) out.push({ k: 'text', cls: `in in-${s.tone}`, x: r1(x + w / 2), y: y + 27, text: fmt(v, c.decimals), anchor: 'middle' });
      if (stacked) x += w;
    }
    const v0 = c.series[0].values[i] ?? 0;
    const end = stacked ? x0 + ((x1 - x0) * total) / max : x0 + ((x1 - x0) * v0) / max;
    out.push({ k: 'text', cls: 'val', x: r1(Math.min(end + 5, x1 + 4)), y: y + 27, text: stacked ? fmt(total) : fmt(c.series[0].values[i], c.decimals), anchor: 'start' });
    if (hasLink(c, i)) hits.push({ i, x: 0, y, w: W, h: row - 2, label: c.categories[i] });
  }
  return { height: h, shapes: out, hits };
}

function arc(cx: number, cy: number, r: number, inner: number, start: number, sweep: number): string {
  if (sweep >= 359.999) return `${arc(cx, cy, r, inner, start, 180)} ${arc(cx, cy, r, inner, start + 180, 180)}`;
  const rad = (a: number) => (a * Math.PI) / 180;
  const pt = (rr: number, a: number) => [r1(cx + rr * Math.cos(rad(a))), r1(cy + rr * Math.sin(rad(a)))];
  const end = start + sweep;
  const large = sweep > 180 ? 1 : 0;
  const [ax, ay] = pt(r, start);
  const [bx, by] = pt(r, end);
  const [cx2, cy2] = pt(inner, end);
  const [dx, dy] = pt(inner, start);
  return `M${ax} ${ay}A${r} ${r} 0 ${large} 1 ${bx} ${by}L${cx2} ${cy2}A${inner} ${inner} 0 ${large} 0 ${dx} ${dy}Z`;
}

function donut(c: ChartSpec, W: number): Raw {
  // Largo: rosca à esquerda e legenda ao lado. Estreito: rosca centralizada e legenda embaixo.
  const stackedLegend = W < 400;
  const cx = stackedLegend ? W / 2 : 96, cy = 98, r = 76, inner = 48;
  const values = c.series[0].values;
  const total = values.reduce<number>((s, v) => s + (v ?? 0), 0);
  const out: Shape[] = [];
  const hits: HitArea[] = [];
  let angle = -90;
  values.forEach((v, i) => {
    if (v === null || v <= 0 || total <= 0) return;
    const sweep = (360 * v) / total;
    out.push({ k: 'path', cls: `f-${c.categoryTones?.[i] ?? c.series[0].tone}`, d: arc(cx, cy, r, inner, angle, sweep), title: `${c.categories[i]}: ${fmt(v)} de ${fmt(total)} (${pct(v, total)})` });
    angle += sweep;
  });
  out.push({ k: 'text', cls: 'big', x: cx, y: cy + 2, text: fmt(total), anchor: 'middle' });
  out.push({ k: 'text', cls: 'sm', x: cx, y: cy + 18, text: c.unit, anchor: 'middle' });
  const lx = stackedLegend ? 4 : 192;
  let ly = stackedLegend ? 200 : 22;
  c.categories.forEach((cat, i) => {
    const v = values[i] ?? 0;
    out.push({ k: 'rect', cls: `f-${c.categoryTones?.[i] ?? c.series[0].tone}`, x: lx, y: ly - 9, w: 10, h: 10 });
    out.push({ k: 'text', cls: 'txt', x: lx + 16, y: ly, text: cat, anchor: 'start' });
    out.push({ k: 'text', cls: 'val', x: W - 4, y: ly, text: `${fmt(v)} · ${pct(v, total)}`, anchor: 'end' });
    if (hasLink(c, i)) hits.push({ i, x: lx - 4, y: ly - 15, w: W - lx + 4, h: 22, label: cat });
    ly += 26;
  });
  return { height: Math.max(196, ly), shapes: out, hits };
}

function line(c: ChartSpec, W: number): Raw {
  const h = 238, x0 = 34, y0 = 18, y1 = h - 38, x1 = W - 10;
  const out: Shape[] = [];
  grid(c, x0, x1, y0, y1, out);
  const n = c.categories.length;
  const X = (i: number) => r1(n <= 1 ? (x0 + x1) / 2 : x0 + 8 + ((x1 - x0 - 16) * i) / (n - 1));
  for (const s of c.series)
    for (let i = 1; i < n; i++) {
      const a = s.values[i - 1], b = s.values[i];
      if (!s.connect?.[i] || a === null || b === null) continue;
      out.push({ k: 'line', cls: `l-${s.tone}${s.dashed ? ' dash' : ''}`, x1: X(i - 1), y1: r1(yOf(a, c, y0, y1)), x2: X(i), y2: r1(yOf(b, c, y0, y1)) });
    }
  c.series.forEach((s, j) =>
    s.values.forEach((v, i) => {
      if (v === null) return;
      const y = r1(yOf(v, c, y0, y1));
      out.push({ k: 'circle', cls: `f-${s.tone}`, x: X(i), y, r: s.dashed ? 3 : 4, title: `${c.categories[i]} · ${s.label}: ${fmt(v, c.decimals)}` });
      if (j === 0) out.push({ k: 'text', cls: 'val', x: X(i), y: y - 8, text: fmt(v, c.decimals), anchor: 'middle' });
    }),
  );
  // Rótulos de mês alternados quando não cabem (≈ 44 px por rótulo); a tabela traz todos os meses.
  const step = n > 1 && (x1 - x0) / n < 44 ? 2 : 1;
  for (let i = 0; i < n; i += step) out.push({ k: 'text', cls: 'sm', x: X(i), y: y1 + 16, text: c.categories[i], anchor: 'middle' });
  return { height: h, shapes: out, hits: [] };
}

function radar(c: ChartSpec, W: number): Raw {
  const h = 300, cy = 152, R = Math.min(104, W / 2 - 52), cx = W / 2;
  const n = c.categories.length;
  const out: Shape[] = [];
  const P = (i: number, v: number) => {
    const a = ((-90 + (360 * i) / n) * Math.PI) / 180;
    return [r1(cx + R * (v / c.max) * Math.cos(a)), r1(cy + R * (v / c.max) * Math.sin(a))];
  };
  for (let ring = 1; ring <= c.max; ring++) {
    out.push({ k: 'polygon', cls: 'ring', points: Array.from({ length: n }, (_, i) => P(i, ring).join(',')).join(' ') });
    const [lx, ly] = P(0, ring);
    out.push({ k: 'text', cls: 'sm', x: lx + 4, y: ly + 3, text: String(ring), anchor: 'start' });
  }
  for (let i = 0; i < n; i++) {
    const [ex, ey] = P(i, c.max);
    out.push({ k: 'line', cls: 'g', x1: cx, y1: cy, x2: ex, y2: ey });
    const [lx, ly] = P(i, c.max * 1.17);
    out.push({ k: 'text', cls: 'txt', x: lx, y: ly + 4, text: c.categories[i], anchor: Math.abs(lx - cx) < 8 ? 'middle' : lx > cx ? 'start' : 'end' });
  }
  for (const s of c.series) {
    if (s.values.every((v) => v !== null)) {
      out.push({ k: 'polygon', cls: `r-${s.tone}${s.dashed ? ' dash' : ''}`, points: s.values.map((v, i) => P(i, v!).join(',')).join(' ') });
    } else {
      // Perfil incompleto: só os lados entre funções vizinhas COM valor — nunca fechado nem levado a zero.
      for (let i = 0; i < n; i++) {
        const j = (i + 1) % n;
        const a = s.values[i], b = s.values[j];
        if (a === null || b === null) continue;
        const [ax, ay] = P(i, a);
        const [bx, by] = P(j, b);
        out.push({ k: 'line', cls: `l-${s.tone}${s.dashed ? ' dash' : ''}`, x1: ax, y1: ay, x2: bx, y2: by });
      }
    }
    s.values.forEach((v, i) => {
      if (v === null) return;
      const [px, py] = P(i, v);
      out.push({ k: 'circle', cls: `f-${s.tone}`, x: px, y: py, r: 3.5, title: `${c.categories[i]} · ${s.label}: ${fmt(v, c.decimals)}` });
    });
  }
  return { height: h, shapes: out, hits: [] };
}
