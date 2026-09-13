/**
 * [AEGIS-RISK-PRIORITIZATION-01] Regressão de TEMPLATE da Central: o detalhe da prioridade de tratamento não pode depender
 * da linha da tabela.
 *
 * O template REAL de `priorities.component.ts` é analisado pelo parser do próprio Angular (`@angular/compiler`) e os blocos
 * de controle da fila de dispositivos (@if/@for/@let) são avaliados sobre o estado do componente, com os helpers reais de
 * apresentação. Dois estados da mesma sessão: filtro "Prioridade 3" com o dispositivo aberto e, depois da declaração que o
 * promove, o mesmo filtro vazio. O caminho de ramos até `<app-device-priority>` precisa ser IDÊNTICO nos dois: é assim que
 * o Angular preserva a instância (mesmo ramo = mesma view; outro ramo = view destruída, com leitura nova e sem a
 * confirmação). O botão de fechar é localizado no template e o handler dele é executado sobre o estado.
 */
import * as fs from 'fs';
import * as path from 'path';
import {
  ASTWithSource,
  TmplAstElement,
  TmplAstForLoopBlock,
  TmplAstIfBlock,
  TmplAstLetDeclaration,
  TmplAstNode,
  parseTemplate,
} from '@angular/compiler';
import {
  DEVICE_PRIORITY_BAND_FILTERS,
  DevicePriorityItem,
  DevicePriorityList,
  detailOutsideListNote,
  devicePriorityListView,
} from '../src/app/models/device-priority.models';

// ---- micro-harness ---------------------------------------------------------------------------
let failures = 0;
let count = 0;
function test(name: string, fn: () => void): void {
  count++;
  try {
    fn();
    console.log(`  ok - ${name}`);
  } catch (e) {
    failures++;
    console.log(`  FAIL - ${name}\n      ${(e as Error).message}`);
  }
}
function eq<T>(actual: T, expected: T, msg: string): void {
  if (actual !== expected) throw new Error(`${msg}: esperado ${String(expected)}, obtido ${String(actual)}`);
}
function ok(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}

// ---- template real ---------------------------------------------------------------------------
const SOURCE = path.join(process.cwd(), 'src/app/pages/priorities.component.ts');
const TEMPLATE = /template:\s*`([\s\S]*?)`,\s*styles:/.exec(fs.readFileSync(SOURCE, 'utf8'))?.[1] ?? '';
const parsed = parseTemplate(TEMPLATE, SOURCE, { preserveWhitespaces: false });
const HEADING = 'Prioridade de tratamento · vulnerabilidades em dispositivos';

type Scope = Record<string, unknown>;
interface Rendered {
  el: TmplAstElement;
  /** Blocos e ramos atravessados — a identidade que decide se o Angular preserva ou recria a view. */
  blocks: string[];
  scope: Scope;
}

function children(n: TmplAstNode): TmplAstNode[] {
  const x = n as unknown as { children?: TmplAstNode[]; branches?: { children: TmplAstNode[] }[]; empty?: { children: TmplAstNode[] } | null };
  return [...(x.children ?? []), ...(x.branches ?? []).flatMap((b) => b.children), ...(x.empty?.children ?? [])];
}
function allElements(nodes: TmplAstNode[]): TmplAstElement[] {
  return nodes.flatMap((n) => [...(n instanceof TmplAstElement ? [n] : []), ...allElements(children(n))]);
}
function span(n: TmplAstNode): [number, number] {
  return [n.sourceSpan.start.offset, n.sourceSpan.end.offset];
}

/** A fila de dispositivos: o `div.queue` cujo cabeçalho é o da prioridade de tratamento. */
const REGION = allElements(parsed.nodes).find(
  (e) => e.name === 'div' && e.attributes.some((a) => a.name === 'class' && a.value === 'queue') &&
    TEMPLATE.slice(...span(e)).includes(HEADING),
);

/** Fonte de uma expressão do template, sem o operador de não nulo do TypeScript. */
function src(e: unknown): string {
  const s = (e as ASTWithSource | null)?.source;
  if (s == null) throw new Error('expressão do template sem fonte');
  return s.replace(/([\w)\]])!(?!=)/g, '$1');
}
function evaluate(expr: string, scope: Scope): unknown {
  const ctx = new Proxy(scope, {
    has: () => true,
    get: (t, k) => {
      if (k === Symbol.unscopables) return undefined;
      if (typeof k === 'string' && k in t) return t[k];
      throw new Error(`identificador fora do estado do teste: ${String(k)}`);
    },
  });
  return new Function('ctx', `with (ctx) { return (${expr}); }`)(ctx);
}

/**
 * "Renderiza" os blocos de controle: avalia @if (primeiro ramo verdadeiro), @for e @let; elementos entram na saída com o
 * caminho de blocos até eles. Só a fila de dispositivos (e os blocos que a contêm) é avaliada.
 */
function render(nodes: TmplAstNode[], scope: Scope, blocks: string[] = [], out: Rendered[] = []): Rendered[] {
  const [rs, re] = span(REGION!);
  for (const n of nodes) {
    const [s, e] = span(n);
    if (e < rs || s > re) continue;   // fora da fila de dispositivos (outras filas da Central)
    if (n instanceof TmplAstElement) {
      out.push({ el: n, blocks, scope });
      render(n.children, scope, blocks, out);
    } else if (n instanceof TmplAstLetDeclaration) {
      scope = { ...scope, [n.name]: evaluate(src(n.value), scope) };
    } else if (n instanceof TmplAstIfBlock) {
      for (let i = 0; i < n.branches.length; i++) {
        const b = n.branches[i];
        const value = b.expression === null ? true : evaluate(src(b.expression), scope);
        if (!value) continue;
        const inner = b.expressionAlias ? { ...scope, [b.expressionAlias.name]: value } : scope;
        render(b.children, inner, [...blocks, `@if:${s}#${i}`], out);
        break;
      }
    } else if (n instanceof TmplAstForLoopBlock) {
      const items = evaluate(src(n.expression), scope) as unknown[];
      items.forEach((item, i) =>
        render(n.children, { ...scope, [n.item.name]: item }, [...blocks, `@for:${s}[${i}]`], out));
      if (!items.length && n.empty) render(n.empty.children, scope, [...blocks, `@empty:${s}`], out);
    }
  }
  return out;
}

// ---- estado do componente (helpers REAIS de apresentação) --------------------------------------
const DEVICE = 'a1000000-0000-0000-0000-000000000001';

function item(over: Partial<DevicePriorityItem> = {}): DevicePriorityItem {
  return {
    assetId: DEVICE, assetName: 'nb-diretoria-03', position: 1, nameIsPlaceholder: false, prioritizableCases: 1,
    casesByBand: [], insufficientCases: 0, dispositionCases: 0, band: 'p3', bandLabel: 'Prioridade 3', tiedAssets: 0,
    determiningCase: null, positionReason: '', nextAction: '', deviceContextLabel: '', criticalityLabel: '',
    informationLabel: '', caveats: [],
    ...over,
  } as DevicePriorityItem;
}

function list(over: Partial<DevicePriorityList>, p3: number): DevicePriorityList {
  return {
    evaluatedAt: '2026-09-12T12:00:00Z', heading: '', scope: '',
    policy: {
      code: 'AEGIS-PRIO-DEV', version: 1, name: '', rationale: '', table: [], aggravation: [], tieBreaks: [], notUsed: [],
      temporal: { maxEvidenceAgeDays: 7, maxSourceActivityAgeDays: 30, acquisitionGapCaveatHours: 24, description: '' },
    },
    summary: {
      readingState: 'Available', readingNote: null, candidateAssets: 5, assetsEvaluated: 5, evaluationTruncated: false,
      completeThroughBand: null, truncationNote: null,
      assetsByBand: [
        { band: 'p1', label: '', count: 2 }, { band: 'p2', label: '', count: 3 - p3 }, { band: 'p3', label: '', count: p3 },
        { band: 'p4', label: '', count: 0 }, { band: 'insufficient', label: '', count: 0 },
      ],
      casesByBand: [], dispositions: [], outOfScopeSources: 0, outOfScopeNote: null, absenceState: 'conclusive', absenceNote: null,
    },
    bandFilter: 'p3', items: [], total: 0, page: 1, pageSize: 10,
    ...over,
  } as DevicePriorityList;
}

interface State {
  list: DevicePriorityList;
  expanded: string | null;
  name: string | null;
  reads: number;
  declarations: number;
}

/** O que o template enxerga do componente — mesmos nomes do `PrioritiesComponent`. */
function component(st: State): Scope {
  const view = () => devicePriorityListView(st.list);
  return {
    loading: () => false, error: () => null, data: () => ({}), tab: () => 'achados',
    dp: () => st.list, dpView: view, dpText: () => { const v = view(); return 'text' in v ? v.text : ''; },
    dpRefreshError: () => null, dpRefreshing: () => false, dpSummary: () => '', dpRange: () => '', dpDispositions: () => null,
    dpBands: DEVICE_PRIORITY_BAND_FILTERS, dpFilter: () => ({ band: st.list.bandFilter, page: 1, pageSize: 10 }),
    dpExpanded: () => st.expanded, dpExpandedName: () => st.name,
    dpDetailNote: () => detailOutsideListNote(st.list, st.expanded),
    tie: () => null, cases: () => '', bandCounts: () => '', dpTone: () => 'info', fmtDate: () => '',
    closeDp: () => { st.expanded = null; st.name = null; },
    toggleDp: (id: string, name: string) => { st.expanded = st.expanded === id ? null : id; st.name = st.expanded ? name : null; },
    loadDevicePriority: () => { st.reads++; },
    goDp: () => { st.reads++; },
    setDpBand: () => { st.reads++; },
    onDeviceCriticalityDeclared: () => { st.declarations++; },
  };
}

const detailOf = (r: Rendered[]) => r.filter((x) => x.el.name === 'app-device-priority');
const hasClass = (el: TmplAstElement, c: string) => el.attributes.some((a) => a.name === 'class' && a.value.split(/\s+/).includes(c));
const handler = (el: TmplAstElement, event: string) => {
  const o = el.outputs.find((x) => x.name === event);
  return o ? src(o.handler) : null;
};

console.log('device-priority.template');

test('o template real da Central é analisado e contém a fila de dispositivos', () => {
  ok(TEMPLATE.length > 0, 'template extraído do componente');
  eq((parsed.errors ?? []).length, 0, 'sem erros de análise');
  ok(!!REGION, 'fila de prioridade de tratamento localizada');
});

test('filtro que esvazia depois da declaração: a lista mostra o vazio e o MESMO detalhe continua aberto', () => {
  const st: State = {
    list: list({ items: [item()], total: 1 }, 1), expanded: DEVICE, name: 'nb-diretoria-03', reads: 0, declarations: 0,
  };
  const before = render(parsed.nodes, component(st));
  const [d1] = detailOf(before);
  ok(!!d1, 'detalhe aberto com a linha na tabela');
  ok(before.some((x) => x.el.name === 'table' && hasClass(x.el, 'dp-table')), 'tabela exibida antes');

  // Releitura em segundo plano depois da declaração confirmada: o dispositivo foi para P2; o filtro P3 ficou vazio.
  st.list = list({ items: [], total: 0 }, 0);
  eq(devicePriorityListView(st.list).kind, 'filterEmpty', 'estado de filtro vazio');
  const after = render(parsed.nodes, component(st));
  ok(!after.some((x) => x.el.name === 'table' && hasClass(x.el, 'dp-table')), 'a tabela dá lugar ao vazio');
  const [d2] = detailOf(after);
  ok(!!d2, 'o detalhe continua renderizado com o filtro vazio');
  eq(d2.blocks.join(' > '), d1.blocks.join(' > '), 'mesmo caminho de ramos: a instância é preservada (sem recriar)');
  eq(src(d2.el.inputs.find((i) => i.name === 'assetId')?.value), 'id', 'mesmo ativo no detalhe');
  eq(d2.scope['id'], DEVICE, 'id do detalhe vem do estado aberto, não da linha');
  eq(handler(d2.el, 'criticalityDeclared'), 'onDeviceCriticalityDeclared()', 'confirmação avisa a Central');

  const note = detailOutsideListNote(st.list, st.expanded) ?? '';
  ok(note.includes('saiu do filtro "Prioridade 3"'), 'explica que o dispositivo saiu do filtro');
  ok(after.some((x) => x.el.name === 'p' && x.blocks.some((b) => d2.blocks.includes(b))
    && x.el.attributes.some((a) => a.name === 'role' && a.value === 'status')), 'nota exibida junto do detalhe');
  eq(st.reads, 0, 'renderizar o novo estado não relê a fila');
  eq(st.declarations, 0, 'nem emite nova declaração');
});

test('fechar o detalhe sem a linha na tabela: ação explícita, sem releitura', () => {
  const st: State = { list: list({ items: [], total: 0 }, 0), expanded: DEVICE, name: 'nb-diretoria-03', reads: 0, declarations: 0 };
  const scope = component(st);
  const close = render(parsed.nodes, scope).find((x) => x.el.name === 'button' && hasClass(x.el, 'dp-detail-close'));
  ok(!!close, 'botão de fechar renderizado com o filtro vazio');
  ok(!close!.blocks.some((b) => b.startsWith('@for')), 'o botão não pertence a uma linha da tabela');
  const h = handler(close!.el, 'click');
  ok(!!h, 'botão com ação de clique');
  evaluate(h!, close!.scope);
  eq(st.expanded, null, 'detalhe fechado');
  eq(detailOf(render(parsed.nodes, component(st))).length, 0, 'detalhe removido depois do clique');
  eq(st.reads, 0, 'fechar não relê a fila');
});

test('sem filtro, dispositivo que mudou de página: detalhe preservado com a nota de posição', () => {
  const other = item({ assetId: 'b2000000-0000-0000-0000-000000000002', assetName: 'srv-arquivos-02', band: 'p1' });
  const st: State = {
    list: list({ bandFilter: null, items: [item(), other], total: 2 }, 1), expanded: DEVICE, name: 'nb-diretoria-03',
    reads: 0, declarations: 0,
  };
  const [d1] = detailOf(render(parsed.nodes, component(st)));
  st.list = list({ bandFilter: null, items: [other], total: 11, page: 2 }, 1);
  const [d2] = detailOf(render(parsed.nodes, component(st)));
  ok(!!d1 && !!d2, 'detalhe renderizado nos dois estados');
  eq(d2.blocks.join(' > '), d1.blocks.join(' > '), 'mesma instância');
  ok((detailOutsideListNote(st.list, st.expanded) ?? '').includes('não está mais nesta página da fila'), 'nota de posição');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) {
  console.log(`${failures} falha(s)`);
  process.exit(1);
}
