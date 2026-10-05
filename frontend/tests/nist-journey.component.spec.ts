/**
 * [AEGIS-NIST-JOURNEY-01] Telas do AEGIS NIST: respostas atrasadas e versão-base do rascunho.
 *
 * O que este spec trava, nos componentes reais (serviço dublê com respostas entregues na ordem que o teste escolher):
 *   (1) leitura de A substituída por B: B entregue primeiro e A depois — a tela mostra só B; erro atrasado de A não
 *       aparece; a navegação limpa o estado da seleção anterior (sugestão da IA, mensagens, conflito);
 *   (2) escrita já enviada em A (gravação, evidência, sugestão) que responde depois da troca para B não repovoa B nem
 *       altera o rascunho de B;
 *   (3) a versão enviada na gravação é a do rascunho: atualizar evidências com um detalhe de versão mais nova não muda
 *       essa base (o servidor acusa conflito); só o recarregamento explícito adota a versão nova; resposta de
 *       evidência não rebaixa a versão já conhecida;
 *   (4) função e entrada do NIST: leitura substituída, perfil substituído e resposta após sair da tela não navegam
 *       nem repovoam.
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import {
  Injector,
  runInInjectionContext,
  signal,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { NistSubcategoryComponent } from '../src/app/pages/nist/nist-subcategory.component';
import { NistFunctionComponent } from '../src/app/pages/nist/nist-function.component';
import { NistHomeComponent } from '../src/app/pages/nist/nist-home.component';
import { NistApiError, NistSelectionService, NistService } from '../src/app/services/nist.service';
import { AuthService } from '../src/app/services/auth.service';
import { GovernanceService } from '../src/app/services/governance.service';
import { AgentStateService } from '../src/app/services/agent-state.service';
import {
  NistAssessment,
  NistEvaluation,
  NistFunctionView,
  NistProfile,
  NistSubcategoryDetail,
} from '../src/app/models/nist.models';

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

// ---- dublês ----------------------------------------------------------------------------------
interface Call {
  m: string;
  args: unknown[];
  s: Subject<unknown>;
}

class FakeNist {
  readonly calls: Call[] = [];
  private call<T>(m: string, args: unknown[]): Observable<T> {
    const s = new Subject<unknown>();
    this.calls.push({ m, args, s });
    return s.asObservable() as Observable<T>;
  }
  list() { return this.call('list', []); }
  profile(...a: unknown[]) { return this.call('profile', a); }
  functionView(...a: unknown[]) { return this.call('functionView', a); }
  subcategory(...a: unknown[]) { return this.call('subcategory', a); }
  save(...a: unknown[]) { return this.call('save', a); }
  linkEvidence(...a: unknown[]) { return this.call('linkEvidence', a); }
  removeEvidence(...a: unknown[]) { return this.call('removeEvidence', a); }
  suggest(...a: unknown[]) { return this.call('suggest', a); }
  create(...a: unknown[]) { return this.call('create', a); }
  addScope(...a: unknown[]) { return this.call('addScope', a); }
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

class FakeRouter {
  readonly navigations: Record<string, unknown>[] = [];
  constructor(private readonly onNavigate: (qp: Record<string, unknown>) => void) {}
  navigate(_: unknown[], extras: { queryParams?: Record<string, unknown> }): Promise<boolean> {
    const qp = extras.queryParams ?? {};
    this.navigations.push(qp);
    this.onNavigate(qp);
    return Promise.resolve(true);
  }
}

/** Rota dublê: parâmetros e consulta como observáveis (o Angular reaproveita o componente ao navegar). */
class FakeRoute {
  readonly params = new BehaviorSubject(convertToParamMap({}));
  readonly query = new BehaviorSubject(convertToParamMap({}));
  readonly data = new BehaviorSubject<Record<string, unknown>>({});
  readonly paramMap = this.params.asObservable();
  readonly queryParamMap = this.query.asObservable();
  get snapshot() {
    return { queryParamMap: this.query.value };
  }
  go(params: Record<string, string>, query: Record<string, string>): void {
    this.params.next(convertToParamMap(params));
    this.query.next(convertToParamMap(query));
  }
}

interface Env<C> {
  c: C;
  // Acesso aos membros protegidos da tela no teste.
  v: any;
  api: FakeNist;
  route: FakeRoute;
  router: FakeRouter;
  tenant: ReturnType<typeof signal<string | null>>;
  destroy: () => void;
}

function mount<C>(ctor: new () => C, init?: (route: FakeRoute) => void): Env<C> {
  const api = new FakeNist();
  const route = new FakeRoute();
  init?.(route);
  const router = new FakeRouter((qp) => {
    const merged: Record<string, string> = {};
    for (const k of route.query.value.keys) merged[k] = route.query.value.get(k)!;
    for (const [k, v] of Object.entries(qp)) if (v !== null && v !== undefined) merged[k] = String(v);
    route.query.next(convertToParamMap(merged));
  });
  const tenant = signal<string | null>('tenant-1');
  const inj = Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: NistService, useValue: api },
      { provide: NistSelectionService, useValue: { read: () => null, write: () => {} } },
      { provide: AuthService, useValue: { activeRole: () => 'Manager', activeTenantId: tenant } },
      { provide: GovernanceService, useValue: { listDocuments: () => new Subject().asObservable() } },
      { provide: AgentStateService, useValue: {} },
      { provide: ActivatedRoute, useValue: route },
      { provide: Router, useValue: router },
    ],
  });
  const c = runInInjectionContext(inj, () => new ctor());
  const destroy = () => (inj as unknown as { destroy(): void }).destroy();
  return { c, v: c as any, api, route, router, tenant, destroy };
}

// ---- dados sintéticos ------------------------------------------------------------------------
const ASSESS = 'a0000000-0000-0000-0000-000000000001';
const SCOPE = 's0000000-0000-0000-0000-000000000001';

function evaluation(version: number, over: Partial<NistEvaluation> = {}): NistEvaluation {
  return {
    id: `e-${version}`,
    state: 'Evaluated' as NistEvaluation['state'],
    currentLevel: 2,
    targetLevel: 4,
    gap: 2,
    notApplicable: false,
    currentComments: null,
    targetComments: null,
    rationale: `justificativa v${version}`,
    gaps: null,
    riskImpact: null,
    improvementGuidance: null,
    ownerName: null,
    evaluatedBy: 'analista@demo.example.com',
    reviewedByName: 'Analista Demo',
    reviewedAt: '2026-10-03T12:00:00Z',
    version,
    ...over,
  };
}

function detail(code: string, ev: NistEvaluation | null, evidence: string[] = []): NistSubcategoryDetail {
  return {
    assessmentId: ASSESS,
    scopeId: SCOPE,
    code,
    functionCode: code.slice(0, 2) as NistSubcategoryDetail['functionCode'],
    functionName: 'Govern',
    categoryCode: code.split('-')[0],
    categoryName: 'Categoria',
    title: `Título ${code}`,
    summary: '',
    impact: '',
    initialAction: '',
    officialOutcome: '',
    implementationExamples: null,
    evaluation: ev,
    evidence: evidence.map((id) => ({
      id,
      originKind: 'Manual',
      type: 'Interview',
      title: `Evidência ${id}`,
      notes: null,
      uri: null,
      originRef: null,
      originLabel: null,
      originScope: null,
      collectedAt: '2026-10-01T00:00:00Z',
      linkedAt: '2026-10-03T12:00:00Z',
      recordedByName: 'Analista Demo',
    })),
    availableEvidence: [],
    posture: null,
    maturityScale: [],
    methodologyVersion: 'aegis-methodology-v1',
  };
}

const SUB = { fn: 'gv', code: 'GV.OC-01' };
const SUB_B = { fn: 'gv', code: 'GV.OC-02' };
const Q = { avaliacao: ASSESS, escopo: SCOPE };

function openSub(code: { fn: string; code: string } = SUB): Env<NistSubcategoryComponent> {
  return mount(NistSubcategoryComponent, (r) => r.go(code, Q));
}

console.log('AEGIS-NIST-JOURNEY-01 · telas do NIST (ordem das respostas e versão do rascunho)');

// ---- (1) leitura substituída -----------------------------------------------------------------

test('subcategoria: A pedida, troca para B, B chega antes e A depois — a tela mostra só B', () => {
  const e = openSub();
  const readA = e.api.last('subcategory');
  e.route.go(SUB_B, Q);
  const readB = e.api.last('subcategory');
  ok(readA !== readB, 'B abriu sua própria leitura');
  reply(readB, detail('GV.OC-02', evaluation(3)));
  reply(readA, detail('GV.OC-01', evaluation(7)));

  eq(e.v.detail()?.code, 'GV.OC-02', 'o detalhe exibido é o de B');
  eq(e.v.draft.rationale, 'justificativa v3', 'o rascunho é o de B');
  eq(e.v.loading(), false, 'a carga de B terminou');
});

test('subcategoria: erro atrasado de A não aparece na tela de B', () => {
  const e = openSub();
  const readA = e.api.last('subcategory');
  e.route.go(SUB_B, Q);
  reply(e.api.last('subcategory'), detail('GV.OC-02', null));
  fail(readA, new NistApiError('falha de A', 500));

  eq(e.v.error(), null, 'nenhum erro de A');
  eq(e.v.detail()?.code, 'GV.OC-02', 'B continua exibida');
});

test('subcategoria: ao navegar, o estado da seleção anterior é limpo (IA, mensagens, conflito)', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.suggest(e.v.detail());
  reply(e.api.last('suggest'), { suggestedCurrentLevel: 3, confidence: 0.6, rationale: 'r', simulated: true, generatedAt: '' });
  e.v.save(e.v.detail());
  fail(e.api.last('save'), new NistApiError('versão desatualizada', 409));
  ok(e.v.suggestion() !== null && e.v.conflict(), 'pré-condição: sugestão e conflito em A');

  e.route.go(SUB_B, Q);
  eq(e.v.suggestion(), null, 'sugestão de A some');
  eq(e.v.conflict(), false, 'conflito de A some');
  eq(e.v.saveError(), null, 'mensagem de gravação de A some');
  eq(e.v.detail(), null, 'detalhe de A não fica na tela de B');
  eq(e.v.loading(), true, 'B carregando');
});

test('subcategoria: troca de tenant invalida a leitura em curso', () => {
  const e = openSub();
  const read = e.api.last('subcategory');
  e.tenant.set('tenant-2');
  reply(read, detail('GV.OC-01', evaluation(1)));
  eq(e.v.detail(), null, 'resposta pedida no tenant anterior é descartada');
});

// ---- (2) escrita já enviada ------------------------------------------------------------------

test('subcategoria: gravação de A que termina depois da troca não repovoa B nem altera o rascunho de B', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.draft.rationale = 'editado em A';
  e.v.save(e.v.detail());
  const saveA = e.api.last('save');

  e.route.go(SUB_B, Q);
  reply(e.api.last('subcategory'), detail('GV.OC-02', evaluation(5)));
  e.v.draft.rationale = 'editando B';
  reply(saveA, detail('GV.OC-01', evaluation(2, { rationale: 'editado em A' })));

  eq(e.v.detail()?.code, 'GV.OC-02', 'B continua exibida');
  eq(e.v.draft.rationale, 'editando B', 'rascunho de B intacto');
  eq(e.v.savedNote(), null, 'nenhuma confirmação de A na tela de B');
  eq(e.v.saving(), false, 'B pode gravar');
});

test('subcategoria: conflito (409) de A que chega depois da troca não aparece em B', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.save(e.v.detail());
  const saveA = e.api.last('save');
  e.route.go(SUB_B, Q);
  reply(e.api.last('subcategory'), detail('GV.OC-02', evaluation(5)));
  fail(saveA, new NistApiError('versão desatualizada', 409));

  eq(e.v.conflict(), false, 'sem conflito em B');
  eq(e.v.saveError(), null, 'sem erro em B');
});

test('subcategoria: evidência e sugestão de A que chegam depois da troca não repovoam B', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.suggest(e.v.detail());
  const sugA = e.api.last('suggest');
  e.v.linkAvailable(e.v.detail(), { originKind: 'AssetInventory', documentId: null, knightRunId: null, knightIndicatorId: null });
  const linkA = e.api.last('linkEvidence');

  e.route.go(SUB_B, Q);
  reply(e.api.last('subcategory'), detail('GV.OC-02', evaluation(5)));
  reply(sugA, { suggestedCurrentLevel: 4, confidence: 0.9, rationale: 'A', simulated: false, generatedAt: '' });
  reply(linkA, detail('GV.OC-01', evaluation(1), ['ev-a']));

  eq(e.v.suggestion(), null, 'sugestão de A descartada');
  eq(e.v.detail()?.code, 'GV.OC-02', 'detalhe de A descartado');
  eq(e.v.detail()?.evidence.length, 0, 'evidência de A não aparece em B');
  eq(e.v.busy(), false, 'B não fica bloqueada');
});

// ---- (3) versão-base do rascunho -------------------------------------------------------------

test('versão: evidência com detalhe mais novo não muda a base — a gravação envia a versão do rascunho', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.draft.rationale = 'rascunho de A sobre a v1';
  // Usuário B grava a v2 no servidor; A vincula evidência e recebe o detalhe já na v2.
  e.v.linkAvailable(e.v.detail(), { originKind: 'AssetInventory', documentId: null, knightRunId: null, knightIndicatorId: null });
  reply(e.api.last('linkEvidence'), detail('GV.OC-01', evaluation(2, { rationale: 'gravado por B' }), ['ev-1']));

  eq(e.v.detail()?.evidence.length, 1, 'a lista de evidências foi atualizada');
  eq(e.v.draft.rationale, 'rascunho de A sobre a v1', 'o rascunho de A foi preservado');
  ok(e.v.staleBase(), 'a tela avisa que o rascunho é de uma versão anterior');

  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[3] as { expectedVersion: number; rationale: string };
  eq(sent.expectedVersion, 1, 'a gravação envia a versão em que o rascunho se baseia');
  eq(sent.rationale, 'rascunho de A sobre a v1', 'e o conteúdo do rascunho');

  fail(e.api.last('save'), new NistApiError('versão desatualizada', 409));
  eq(e.v.conflict(), true, 'conflito apresentado');
  eq(e.v.draft.rationale, 'rascunho de A sobre a v1', 'a edição continua no formulário');
});

test('versão: retirada de evidência também preserva a base do rascunho', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1), ['ev-1']));
  e.v.remove(e.v.detail(), 'ev-1');
  reply(e.api.last('removeEvidence'), detail('GV.OC-01', evaluation(2), []));
  e.v.save(e.v.detail());
  eq((e.api.last('save').args[3] as { expectedVersion: number }).expectedVersion, 1, 'base continua na v1');
});

test('versão: só o recarregamento explícito adota a versão nova', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.linkAvailable(e.v.detail(), { originKind: 'AssetInventory', documentId: null, knightRunId: null, knightIndicatorId: null });
  reply(e.api.last('linkEvidence'), detail('GV.OC-01', evaluation(2, { rationale: 'gravado por B' }), ['ev-1']));
  e.v.reload();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(2, { rationale: 'gravado por B' }), ['ev-1']));

  eq(e.v.draft.rationale, 'gravado por B', 'o rascunho passa a ser o da versão recarregada');
  eq(e.v.staleBase(), false, 'sem aviso depois de recarregar');
  e.v.save(e.v.detail());
  eq((e.api.last('save').args[3] as { expectedVersion: number }).expectedVersion, 2, 'base agora é a v2');
});

test('versão: resposta de evidência com versão anterior não rebaixa a versão conhecida', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.save(e.v.detail());
  reply(e.api.last('save'), detail('GV.OC-01', evaluation(2, { rationale: 'gravado' })));
  e.v.linkAvailable(e.v.detail(), { originKind: 'AssetInventory', documentId: null, knightRunId: null, knightIndicatorId: null });
  reply(e.api.last('linkEvidence'), detail('GV.OC-01', evaluation(1, { rationale: 'antigo' }), ['ev-1']));

  eq(e.v.detail()?.evaluation?.version, 2, 'a versão exibida não volta para a v1');
  eq(e.v.detail()?.evaluation?.rationale, 'gravado', 'nem o conteúdo antigo');
  eq(e.v.detail()?.evidence.length, 1, 'a evidência nova aparece');
  e.v.save(e.v.detail());
  eq((e.api.last('save').args[3] as { expectedVersion: number }).expectedVersion, 2, 'base continua na v2');
});

test('versão: gravação e evidência não correm juntas (a segunda espera a primeira terminar)', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.save(e.v.detail());
  e.v.linkAvailable(e.v.detail(), { originKind: 'AssetInventory', documentId: null, knightRunId: null, knightIndicatorId: null });
  eq(e.api.of('linkEvidence').length, 0, 'vínculo bloqueado durante a gravação');
  reply(e.api.last('save'), detail('GV.OC-01', evaluation(2)));
  e.v.linkAvailable(e.v.detail(), { originKind: 'AssetInventory', documentId: null, knightRunId: null, knightIndicatorId: null });
  e.v.save(e.v.detail());
  eq(e.api.of('save').length, 1, 'gravação bloqueada durante o vínculo');
});

test('fluxo normal: carregar, editar, vincular evidência e gravar', () => {
  const e = openSub();
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.draft.currentLevel = 3;
  e.v.draft.rationale = 'nova justificativa';
  e.v.linkManual(e.v.detail());
  eq(e.api.of('linkEvidence').length, 0, 'registro manual exige título');
  e.v.manual.title = 'Entrevista com o CISO';
  e.v.linkManual(e.v.detail());
  reply(e.api.last('linkEvidence'), detail('GV.OC-01', evaluation(1), ['ev-1']));
  eq(e.v.manual.title, '', 'formulário manual limpo após o vínculo');
  eq(e.v.draft.currentLevel, 3, 'edição preservada');
  eq(e.v.staleBase(), false, 'sem aviso de versão');

  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[3] as { expectedVersion: number; currentLevel: number };
  eq(sent.expectedVersion, 1, 'versão enviada');
  eq(sent.currentLevel, 3, 'nível enviado');
  reply(e.api.last('save'), detail('GV.OC-01', evaluation(2, { currentLevel: 3, rationale: 'nova justificativa' }), ['ev-1']));
  eq(e.v.detail()?.evaluation?.version, 2, 'versão nova exibida');
  eq(e.v.draft.rationale, 'nova justificativa', 'rascunho alinhado ao gravado');
  eq(e.v.savedNote(), 'Avaliação gravada.', 'confirmação exibida');
  e.v.save(e.v.detail());
  eq((e.api.last('save').args[3] as { expectedVersion: number }).expectedVersion, 2, 'próxima gravação parte da v2');
});

// ---- (4) função e entrada --------------------------------------------------------------------

function assessment(): NistAssessment {
  return {
    id: ASSESS,
    name: 'Avaliação demo',
    scopes: [{ id: SCOPE, name: 'Corporativo' }],
  } as unknown as NistAssessment;
}
function fnView(code: string): NistFunctionView {
  return { assessmentId: ASSESS, scopeId: SCOPE, code, name: code, definition: '', profile: {}, categories: [] } as unknown as NistFunctionView;
}

test('função: GV pedida, troca para ID, ID chega antes e GV depois — a tela mostra só ID', () => {
  const e = mount(NistFunctionComponent, (r) => r.go({ fn: 'gv' }, Q));
  reply(e.api.last('list'), [assessment()]);
  const viewGv = e.api.last('functionView');
  e.route.go({ fn: 'id' }, Q);
  reply(e.api.last('list'), [assessment()]);
  const viewId = e.api.last('functionView');
  reply(viewId, fnView('ID'));
  reply(viewGv, fnView('GV'));

  eq(e.v.view()?.code, 'ID', 'a função exibida é ID');
  eq(e.v.fn()?.code, 'ID', 'cabeçalho de ID');
});

test('função: erro atrasado de GV não aparece em ID e a troca limpa a função anterior', () => {
  const e = mount(NistFunctionComponent, (r) => r.go({ fn: 'gv' }, Q));
  reply(e.api.last('list'), [assessment()]);
  const viewGv = e.api.last('functionView');
  e.route.go({ fn: 'id' }, Q);
  eq(e.v.view(), null, 'a função anterior não fica na tela');
  reply(e.api.last('list'), [assessment()]);
  reply(e.api.last('functionView'), fnView('ID'));
  fail(viewGv, new NistApiError('falha de GV', 500));
  eq(e.v.error(), null, 'sem erro de GV');
});

test('função: lista que chega depois de sair da tela não navega', () => {
  const e = mount(NistFunctionComponent, (r) => r.go({ fn: 'gv' }, {}));
  const list = e.api.last('list');
  e.destroy();
  reply(list, [assessment()]);
  eq(e.router.navigations.length, 0, 'nenhuma navegação a partir da tela destruída');
  eq(e.api.of('functionView').length, 0, 'nenhuma leitura da função');
});

function profile(name: string): NistProfile {
  return { assessmentId: ASSESS, scopeId: name, methodologyVersion: 'v1', overall: {}, functions: [], categories: [], gaps: [], indeterminateGaps: 0 } as unknown as NistProfile;
}
function twoAssessments(): NistAssessment[] {
  return [
    { id: 'X', name: 'X', scopes: [{ id: 'sx', name: 'sx' }] },
    { id: 'Y', name: 'Y', scopes: [{ id: 'sy', name: 'sy' }] },
  ] as unknown as NistAssessment[];
}

test('entrada: escolha X e depois Y; Y chega antes e X depois — o perfil é o de Y', () => {
  const e = mount(NistHomeComponent);
  e.v.ngOnInit();
  reply(e.api.last('list'), twoAssessments());
  e.v.choose('X', null);
  const px = e.api.last('profile');
  e.v.choose('Y', null);
  const py = e.api.last('profile');
  reply(py, profile('sy'));
  reply(px, profile('sx'));
  fail(px, new NistApiError('falha de X', 500));

  eq(e.v.selection()?.assessment.id, 'Y', 'seleção em Y');
  eq(e.v.profile()?.scopeId, 'sy', 'perfil de Y');
  eq(e.v.profileError(), null, 'sem erro de X');
});

test('entrada: erro atrasado do perfil de X não aparece com Y selecionada', () => {
  const e = mount(NistHomeComponent);
  e.v.ngOnInit();
  reply(e.api.last('list'), twoAssessments());
  e.v.choose('X', null);
  const px = e.api.last('profile');
  e.v.choose('Y', null);
  fail(px, new NistApiError('falha de X', 500));
  eq(e.v.profileError(), null, 'sem erro de X');
});

test('entrada: avaliação criada que responde depois de sair da tela não navega', () => {
  const e = mount(NistHomeComponent);
  e.v.ngOnInit();
  reply(e.api.last('list'), twoAssessments());
  const before = e.router.navigations.length;
  e.v.form = { name: 'Nova', description: '', startDate: '', endDate: '', scopeName: 'Escopo', scopeDescription: '' };
  e.v.create();
  const create = e.api.last('create');
  e.destroy();
  reply(create, { id: 'Z', name: 'Nova', scopes: [{ id: 'sz', name: 'Escopo' }] });
  eq(e.router.navigations.length, before, 'nenhuma navegação a partir da tela destruída');
  eq(e.api.of('profile').filter((c) => c.args[0] === 'Z').length, 0, 'nenhuma leitura do perfil da nova avaliação');
});

test('entrada: avaliação criada enquanto o usuário escolheu outra não troca a seleção', () => {
  const e = mount(NistHomeComponent);
  e.v.ngOnInit();
  reply(e.api.last('list'), twoAssessments());
  e.v.form = { name: 'Nova', description: '', startDate: '', endDate: '', scopeName: 'Escopo', scopeDescription: '' };
  e.v.create();
  const create = e.api.last('create');
  e.v.choose('Y', null);
  reply(create, { id: 'Z', name: 'Nova', scopes: [{ id: 'sz', name: 'Escopo' }] });
  eq(e.v.selection()?.assessment.id, 'Y', 'seleção do usuário mantida');
  ok(e.v.assessments().some((a: NistAssessment) => a.id === 'Z'), 'a avaliação criada entra na lista');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);
