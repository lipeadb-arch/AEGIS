/**
 * [AEGIS-NIST-AI-ASSIST-01] Telas da assistência de IA na jornada NIST, com os COMPONENTES REAIS e serviço dublê (respostas
 * entregues na ordem que o teste escolher). O que este spec trava:
 *   (1) nada é gerado ao abrir: só o estado da IA é lido; IA desativada não gera;
 *   (2) uma geração por vez (clique repetido não dispara outra); cancelar não deixa a tela presa;
 *   (3) resposta ou erro pedidos para OUTRO contexto (avaliação, rodada, escopo, subcategoria) ou outro tenant são descartados;
 *   (4) o contexto mudou (refresh do pai): a sugestão vira DESATUALIZADA e não pode mais ser aproveitada;
 *   (5) planejar procedimentos sugeridos: conflito por sugestão desatualizada pede revisão declarada;
 *   (6) subcategoria: aplicar só muda o rascunho; a gravação leva a referência, a versão-base do rascunho e só os campos
 *       que continuam preenchidos; 409 de sugestão desatualizada preserva o rascunho e pede revisão declarada;
 *       duas sugestões não se misturam num rascunho;
 *   (7) resumo executivo: a sugestão vira rascunho editável; aceitar envia as seções, a sugestão de origem e a versão;
 *       resposta após trocar de rodada é descartada.
 */
import '@angular/compiler';
import {
  Injector,
  runInInjectionContext,
  signal,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
  ɵSIGNAL as SIGNAL,
} from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { NistAssistPanelComponent, resetAssistAvailabilityCache } from '../src/app/pages/nist/nist-assist-panel.component';
import { NistExecutiveSummaryComponent } from '../src/app/pages/nist/nist-executive-summary.component';
import { NistSubcategoryComponent } from '../src/app/pages/nist/nist-subcategory.component';
import { NistApiError, NistCtx, NistService } from '../src/app/services/nist.service';
import { AuthService } from '../src/app/services/auth.service';
import { GovernanceService } from '../src/app/services/governance.service';
import { AgentStateService } from '../src/app/services/agent-state.service';
import { NistAssistAvailability, NistAssistView } from '../src/app/models/nist-assist.models';
import { NistEvaluation, NistSubcategoryDetail } from '../src/app/models/nist.models';

// ---- micro-harness ---------------------------------------------------------------------------
let failures = 0;
let count = 0;
function test(name: string, fn: () => void): void {
  count++;
  try {
    resetAssistAvailabilityCache();
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

// ---- dublês ----------------------------------------------------------------------------------
interface Call {
  m: string;
  args: unknown[];
  s: Subject<unknown>;
  unsubscribed: boolean;
}
class FakeNist {
  readonly calls: Call[] = [];
  private call<T>(m: string, args: unknown[]): Observable<T> {
    const s = new Subject<unknown>();
    const c: Call = { m, args, s, unsubscribed: false };
    this.calls.push(c);
    return new Observable<T>((obs) => {
      const sub = s.subscribe(obs as never);
      return () => {
        c.unsubscribed = true;
        sub.unsubscribe();
      };
    });
  }
  assistAvailability() { return this.call('assistAvailability', []); }
  assistSubcategory(...a: unknown[]) { return this.call('assistSubcategory', a); }
  assistExecutive(...a: unknown[]) { return this.call('assistExecutive', a); }
  assistFinding(...a: unknown[]) { return this.call('assistFinding', a); }
  subcategoryAssistContext(...a: unknown[]) { return this.call('subcategoryAssistContext', a); }
  executiveAssistContext(...a: unknown[]) { return this.call('executiveAssistContext', a); }
  planProceduresFromAssistance(...a: unknown[]) { return this.call('planProceduresFromAssistance', a); }
  executiveSummary(...a: unknown[]) { return this.call('executiveSummary', a); }
  saveExecutiveSummary(...a: unknown[]) { return this.call('saveExecutiveSummary', a); }
  subcategory(...a: unknown[]) { return this.call('subcategory', a); }
  save(...a: unknown[]) { return this.call('save', a); }
  assignees() { return this.call('assignees', []); }
  list() { return this.call('list', []); }
  of(m: string): Call[] { return this.calls.filter((c) => c.m === m); }
  last(m: string): Call {
    const c = this.of(m).at(-1);
    if (!c) throw new Error(`nenhuma chamada a ${m}`);
    return c;
  }
}
function reply(c: Call, value: unknown): void {
  c.s.next(value);
  c.s.complete();
}
function fail(c: Call, err: unknown): void {
  c.s.error(err);
}

class FakeRoute {
  readonly params = new BehaviorSubject(convertToParamMap({}));
  readonly query = new BehaviorSubject(convertToParamMap({}));
  readonly frag = new BehaviorSubject<string | null>(null);
  readonly paramMap = this.params.asObservable();
  readonly queryParamMap = this.query.asObservable();
  readonly fragment = this.frag.asObservable();
  get snapshot() {
    return { queryParamMap: this.query.value };
  }
  go(params: Record<string, string>, query: Record<string, string>): void {
    this.params.next(convertToParamMap(params));
    this.query.next(convertToParamMap(query));
  }
}

function injector(api: FakeNist, tenant: ReturnType<typeof signal<string | null>>, route = new FakeRoute()): Injector {
  return Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: NistService, useValue: api },
      { provide: AuthService, useValue: { activeRole: () => 'Manager', activeTenantId: tenant } },
      { provide: GovernanceService, useValue: { listDocuments: () => new Subject().asObservable() } },
      { provide: AgentStateService, useValue: {} },
      { provide: ActivatedRoute, useValue: route },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });
}
function flush(inj: Injector): void {
  inj.get(EffectScheduler).flush();
}
function setInput(sig: unknown, value: unknown): void {
  const node = (sig as Record<symbol, { applyValueToInputSignal(n: unknown, v: unknown): void }>)[SIGNAL as unknown as symbol];
  node.applyValueToInputSignal(node, value);
}

// ---- dados sintéticos ------------------------------------------------------------------------
const A = 'a0000000-0000-0000-0000-000000000001';
const C1 = 'c0000000-0000-0000-0000-000000000001';
const C2 = 'c0000000-0000-0000-0000-000000000002';
const S = 's0000000-0000-0000-0000-000000000001';
const CTX1: NistCtx = { assessmentId: A, cycleId: C1, scopeId: S };
const CTX2: NistCtx = { assessmentId: A, cycleId: C2, scopeId: S };
const REAL: NistAssistAvailability = { state: 'Real', label: 'IA real autorizada', detail: '', canGenerate: true };
const DISABLED: NistAssistAvailability = { state: 'Disabled', label: 'IA desativada', detail: 'A jornada segue manual.', canGenerate: false };

function view(code: string | null, over: Partial<NistAssistView> = {}): NistAssistView {
  return {
    id: `g-${code ?? 'exec'}`, kind: code ? 'Subcategory' : 'ExecutiveSummary', focus: null, assessmentId: A, cycleId: C1, scopeId: S,
    subcategoryCode: code, findingId: null, mode: 'Real', availability: 'Real', modeLabel: 'Gerada por IA', generatedAt: '2026-10-06T12:00:00Z',
    requestedByName: 'Gestora Demo', contextFingerprint: 'fp-1', contextSummary: 'avaliação v1', current: true, staleOnArrival: false,
    reused: false, sources: [], sections: [], level: null, levelNote: null,
    procedures: [{ method: 'Interview', methodLabel: 'Entrevistar', procedure: 'Entrevistar o responsável pela política.', sources: [] }],
    applicable: { rationale: '• texto sugerido', currentLevel: '3', 'procedure:0': 'Entrevistar o responsável pela política.' },
    validationNotes: [], disclaimer: '', ...over,
  };
}

interface Panel {
  c: NistAssistPanelComponent;
  v: any;
  api: FakeNist;
  inj: Injector;
  tenant: ReturnType<typeof signal<string | null>>;
  applied: { field: string; id: string }[];
}
function mountPanel(kind: 'Subcategory' | 'ExecutiveSummary', ctx: NistCtx, code: string | null, availability = REAL): Panel {
  resetAssistAvailabilityCache();
  const api = new FakeNist();
  const tenant = signal<string | null>('tenant-1');
  const inj = injector(api, tenant);
  const c = runInInjectionContext(inj, () => new NistAssistPanelComponent());
  setInput(c.kind, kind);
  setInput(c.ctx, ctx);
  setInput(c.code, code);
  setInput(c.canGenerate, true);
  setInput(c.canIncorporate, true);
  setInput(c.allowProcedures, true);
  const applied: { field: string; id: string }[] = [];
  c.apply.subscribe((e) => applied.push({ field: e.field, id: e.view.id }));
  flush(inj);
  reply(api.last('assistAvailability'), availability);
  return { c, v: c as any, api, inj, tenant, applied };
}

console.log('nist-assist.component');

// =============================== (1) nada automático ===============================================

test('(1) abrir o painel só lê o estado da IA — nenhuma geração; IA desativada não gera', () => {
  const p = mountPanel('Subcategory', CTX1, 'GV.PO-01');
  eq(p.api.of('assistSubcategory').length, 0, 'nada gerado ao abrir');
  eq(p.api.of('assistAvailability').length, 1, 'só o estado da IA');
  const off = mountPanel('Subcategory', CTX1, 'GV.PO-01', DISABLED);
  off.v.generate(null);
  eq(off.api.of('assistSubcategory').length, 0, 'IA desativada: o botão não gera');
});

// =============================== (2) uma por vez, cancelamento ======================================

test('(2) clique repetido durante a geração não dispara outra; cancelar libera a tela sem gravar', () => {
  const p = mountPanel('Subcategory', CTX1, 'GV.PO-01');
  p.v.generate(null);
  p.v.generate(null);
  eq(p.api.of('assistSubcategory').length, 1, 'uma geração por vez');
  p.v.cancel();
  ok(p.api.last('assistSubcategory').unsubscribed, 'a requisição foi cancelada (o servidor recebe o cancelamento)');
  eq(p.v.running(), null, 'tela liberada');
  ok((p.v.note() ?? '').includes('Nada foi gravado'), 'dito que nada foi gravado');
});

// =============================== (3) contexto ======================================================

test('(3) resposta e erro pedidos para outra rodada, subcategoria ou tenant são descartados', () => {
  const p = mountPanel('Subcategory', CTX1, 'GV.PO-01');
  p.v.generate(null);
  const first = p.api.last('assistSubcategory');
  setInput(p.c.ctx, CTX2);
  flush(p.inj);
  reply(first, view('GV.PO-01'));
  eq(p.v.view(), null, 'resposta da rodada anterior não aparece');

  p.v.generate(null);
  const second = p.api.last('assistSubcategory');
  setInput(p.c.code, 'GV.PO-02');
  flush(p.inj);
  fail(second, new NistApiError('falha antiga', 503, 'Unavailable'));
  eq(p.v.error(), null, 'erro de outra subcategoria não aparece');

  p.v.generate(null);
  reply(p.api.last('assistSubcategory'), view('GV.PO-01', { cycleId: C2 }));
  eq(p.v.view(), null, 'resposta que não é desta subcategoria é descartada');

  p.v.generate(null);
  const third = p.api.last('assistSubcategory');
  p.tenant.set('tenant-2');
  reply(third, view('GV.PO-02', { cycleId: C2 }));
  eq(p.v.view(), null, 'resposta pedida em outro tenant é descartada');
});

// =============================== (4) desatualização ================================================

test('(4) o contexto mudou: a sugestão vira desatualizada e não pode ser aproveitada', () => {
  const p = mountPanel('Subcategory', CTX1, 'GV.PO-01');
  p.v.generate(null);
  reply(p.api.last('assistSubcategory'), view('GV.PO-01'));
  const v = p.v.view();
  p.v.emitApply(v, 'rationale');
  eq(p.applied.length, 1, 'sugestão atual pode ser aplicada');

  setInput(p.c.refresh, 1);
  flush(p.inj);
  reply(p.api.last('subcategoryAssistContext'), { fingerprint: 'fp-2', summary: '', sources: [], availability: REAL, latest: null });
  eq(p.v.stale(), true, 'impressão digital diferente: desatualizada');
  p.v.emitApply(v, 'gaps');
  eq(p.applied.length, 1, 'desatualizada não é aplicada');
  eq(p.v.canApply(), false, 'aproveitar bloqueado até gerar de novo');

  p.v.generate(null, false);
  ok(p.api.last('assistSubcategory').args[2] === false, '"Gerar novamente" pede geração nova, sem reaproveitar');
});

test('(4b) sugestão que nasceu desatualizada (alteração concorrente) já chega bloqueada', () => {
  const p = mountPanel('Subcategory', CTX1, 'GV.PO-01');
  p.v.generate(null);
  reply(p.api.last('assistSubcategory'), view('GV.PO-01', { current: false, staleOnArrival: true }));
  eq(p.v.stale(), true, 'desatualizada');
  eq(p.v.canApply(), false, 'não aproveitável');
});

// =============================== (5) procedimentos =================================================

test('(5) planejar procedimentos escolhidos; 409 de sugestão desatualizada pede revisão declarada', () => {
  const p = mountPanel('Subcategory', CTX1, 'GV.PO-01');
  let changed = 0;
  p.c.changed.subscribe(() => changed++);
  p.v.generate(null);
  reply(p.api.last('assistSubcategory'), view('GV.PO-01'));
  const v = p.v.view();
  p.v.toggle(0);
  p.v.planSelected(v, false);
  const call = p.api.last('planProceduresFromAssistance');
  const body = call.args[2] as { assistanceId: string; procedures: unknown[]; acknowledgeStale: boolean };
  ok(body.assistanceId === v.id && body.procedures.length === 1 && !body.acknowledgeStale, 'pedido com a sugestão de origem');
  fail(call, new NistApiError('A sugestão foi gerada sobre um contexto que mudou depois.', 409, 'AssistanceStale'));
  eq(p.v.planError()?.stale, true, 'conflito por sugestão desatualizada');
  p.v.planSelected(v, true);
  const ack = p.api.last('planProceduresFromAssistance');
  ok((ack.args[2] as { acknowledgeStale: boolean }).acknowledgeStale, 'revisão declarada pela pessoa');
  reply(ack, [{ id: 'p1' }]);
  eq(changed, 1, 'o pai recarrega a subcategoria');
  ok((p.v.note() ?? '').includes('Planejar não comprova'), 'planejar não é realizar');
});

// =============================== (6) subcategoria: aplicar e gravar =================================

function evaluation(version: number, over: Partial<NistEvaluation> = {}): NistEvaluation {
  return {
    id: 'e1', state: 'Evaluated' as NistEvaluation['state'], currentLevel: 2, targetLevel: 4, gap: 2, notApplicable: false,
    currentComments: null, targetComments: null, rationale: `justificativa v${version}`, gaps: null, riskImpact: null,
    improvementGuidance: null, ownerName: null, evaluatedBy: 'Analyst', reviewedByName: 'Analista Demo', reviewedAt: '2026-10-03T12:00:00Z',
    version, humanConfirmed: true, ...over,
  };
}
function detail(code: string, ev: NistEvaluation | null, cycleId = C1): NistSubcategoryDetail {
  return {
    assessmentId: A, scopeId: S, cycleId, cycleName: 'out/2026', cycleStatus: 'Open', procedures: [], findings: [], code,
    functionCode: 'GV' as NistSubcategoryDetail['functionCode'], functionName: 'Govern', categoryCode: 'GV.PO', categoryName: 'Política',
    title: `Título ${code}`, summary: '', impact: '', initialAction: '', officialOutcome: '', implementationExamples: null, evaluation: ev,
    evidence: [], availableEvidence: [], posture: null, maturityScale: [], methodologyVersion: 'aegis-methodology-v1',
  } as unknown as NistSubcategoryDetail;
}
function mountSub(): { v: any; api: FakeNist; route: FakeRoute } {
  const api = new FakeNist();
  const route = new FakeRoute();
  const tenant = signal<string | null>('tenant-1');
  const inj = injector(api, tenant, route);
  const c = runInInjectionContext(inj, () => new NistSubcategoryComponent());
  route.go({ fn: 'gv', code: 'GV.PO-01' }, { avaliacao: A, rodada: C1, escopo: S });
  reply(api.last('subcategory'), detail('GV.PO-01', evaluation(3)));
  return { v: c as any, api, route };
}

test('(6) aplicar muda só o rascunho; a gravação leva a referência, a versão-base e os campos ainda preenchidos', () => {
  const s = mountSub();
  s.v.applyAssist({ view: view('GV.PO-01'), field: 'rationale' });
  s.v.applyAssist({ view: view('GV.PO-01'), field: 'currentLevel' });
  eq(s.api.of('save').length, 0, 'aplicar não grava');
  eq(s.v.draft.rationale, '• texto sugerido', 'rascunho com o texto sugerido');
  eq(s.v.draft.currentLevel, 3, 'rascunho com o nível sugerido');
  s.v.draft.currentLevel = null;   // a pessoa desfez o nível antes de gravar
  s.v.save(s.v.detail());
  const req = s.api.last('save').args[2] as { expectedVersion: number; assistance: { assistanceId: string; fields: string[]; acknowledgeStale: boolean } };
  eq(req.expectedVersion, 3, 'versão-base do rascunho');
  eq(req.assistance.assistanceId, 'g-GV.PO-01', 'referência da sugestão');
  eq(req.assistance.fields.join(','), 'rationale', 'só os campos ainda preenchidos');
  eq(req.assistance.acknowledgeStale, false, 'sem revisão declarada');
});

test('(6b) 409 de sugestão desatualizada preserva o rascunho e pede revisão declarada; sugestões não se misturam', () => {
  const s = mountSub();
  s.v.applyAssist({ view: view('GV.PO-01'), field: 'rationale' });
  s.v.applyAssist({ view: view('GV.PO-01', { id: 'g-outra' }), field: 'gaps' });
  ok((s.v.saveError() ?? '').includes('outra sugestão'), 'segunda sugestão não se mistura no mesmo rascunho');
  eq(s.v.appliedFields().join(','), 'rationale', 'só a primeira sugestão no rascunho');

  s.v.save(s.v.detail());
  fail(s.api.last('save'), new NistApiError('A sugestão foi gerada sobre um contexto que mudou depois.', 409, 'AssistanceStale'));
  eq(s.v.assistStale(), true, 'conflito por sugestão desatualizada');
  eq(s.v.conflict(), false, 'não é o conflito de versão');
  eq(s.v.draft.rationale, '• texto sugerido', 'rascunho preservado');
  s.v.save(s.v.detail(), true);
  const req = s.api.last('save').args[2] as { assistance: { acknowledgeStale: boolean } };
  eq(req.assistance.acknowledgeStale, true, 'revisão declarada pela pessoa');
  reply(s.api.last('save'), detail('GV.PO-01', evaluation(4, { rationale: '• texto sugerido' })));
  eq(s.v.appliedFields().length, 0, 'depois de gravar, nada fica pendente');
});

// =============================== (7) resumo executivo =============================================

test('(7) resumo: sugestão vira rascunho editável; aceitar envia seções, origem e versão; resposta de outra rodada é descartada', () => {
  const api = new FakeNist();
  const tenant = signal<string | null>('tenant-1');
  const inj = injector(api, tenant);
  const c = runInInjectionContext(inj, () => new NistExecutiveSummaryComponent());
  const v = c as any;
  setInput(c.ctx, CTX1);
  setInput(c.canWrite, true);
  let changed = 0;
  c.changed.subscribe(() => changed++);
  flush(inj);
  reply(api.last('executiveSummary'), null);

  v.useAssist({ view: view(null, { applicable: { situation: 'Atual 1,5 frente a alvo 3,5.', priorities: '1. Tratar a política.' } }), field: '*' });
  eq(api.of('saveExecutiveSummary').length, 0, 'usar a sugestão não aceita nada');
  const draft = v.draft();
  draft.find((x: { key: string }) => x.key === 'nextSteps').text = 'Apresentar à diretoria.';
  v.accept(false);
  const body = api.last('saveExecutiveSummary').args[1] as { sections: { key: string }[]; assistanceId: string; expectedVersion: number };
  eq(body.sections.map((x) => x.key).join(','), 'situation,priorities,nextSteps', 'só seções com texto, na ordem do AEGIS');
  eq(body.assistanceId, 'g-exec', 'origem assistida');
  eq(body.expectedVersion, 0, 'primeiro aceite');
  fail(api.last('saveExecutiveSummary'), new NistApiError('O resumo foi preparado sobre um estado anterior.', 409, 'AssistanceStale'));
  eq(v.assistStale(), true, 'pede revisão declarada');
  ok(v.draft() !== null, 'rascunho preservado');

  v.accept(true);
  const pending = api.last('saveExecutiveSummary');
  ok((pending.args[1] as { acknowledgeStale: boolean }).acknowledgeStale, 'revisão declarada');
  setInput(c.ctx, CTX2);
  flush(inj);
  reply(pending, { id: 'x', sections: [], origin: 'Assisted', version: 1 });
  eq(v.summary(), null, 'aceite da rodada anterior não aparece na nova');
  eq(changed, 0, 'nenhum aviso de mudança para a rodada nova');
  eq(v.draft(), null, 'rascunho da rodada anterior não passa para a nova');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);
