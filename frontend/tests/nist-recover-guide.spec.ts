/**
 * [AEGIS-NIST-RECOVER-GUIDE-01] Roteiro editorial de RECOVER e área de trabalho da subcategoria.
 *
 * O que este spec trava:
 *   (1) integridade do conteúdo empacotado: exatamente as 8 subcategorias de RECOVER, sem código duplicado ou inventado;
 *       8 perguntas principais, 53 complementares e 40 situações ilustrativas; os exemplos de Felipe (RC.RP-06 e RC.CO-03)
 *       literais; nenhuma outra função recebe roteiro; o catálogo ÚNICO de nomes de nível tem os rótulos do pacote;
 *   (2) faixa de acompanhamento e lista contextual derivam só dos estados existentes: sem avaliação não vira zero, sem
 *       achados é neutro, revisão desatualizada/devolvida continuam distinguíveis;
 *   (3) na tela real: atual e alvo independentes sobre o MESMO rascunho (lacuna e gravação existentes); a saída com
 *       rascunho não gravado pergunta antes e recusar mantém o rascunho; a lista é avisada só depois de gravar; uma
 *       subcategoria de outra função segue sem área de trabalho e sem pergunta ao sair;
 *   (4) lista contextual: contexto substituído descarta a resposta antiga; releitura após gravação não apaga a lista e
 *       falha nela não derruba o que já se sabe; o seletor recusado volta à subcategoria aberta;
 *   (5) o template real compila e traz as seções pedidas (pergunta principal, aprofundamento, tabela com atual e alvo).
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import { TmplAstNode, parseTemplate } from '@angular/compiler';
import {
  Injector,
  runInInjectionContext,
  signal,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import {
  NIST_INTERVIEW_GUIDES,
  hasInterviewGuides,
  interviewGuideFor,
} from '../src/app/models/nist-interview-guide';
import { navLevelText, navTitle, trackingSteps } from '../src/app/models/nist-workspace.models';
import {
  MATURITY_LEVEL_LABELS,
  NIST_FUNCTIONS,
  NistEvaluation,
  NistFinding,
  NistFunctionView,
  NistSubcategoryDetail,
  draftGap,
  levelLabel,
} from '../src/app/models/nist.models';
import { NistSubcategoryComponent, nistSubcategoryContextChanged } from '../src/app/pages/nist/nist-subcategory.component';
import { NistSubcategoryNavComponent, NistWorkspaceContext } from '../src/app/pages/nist/nist-subcategory-nav.component';
import { AuthService } from '../src/app/services/auth.service';
import { GovernanceService } from '../src/app/services/governance.service';
import { NistApiError, NistSelectionService, NistService } from '../src/app/services/nist.service';

// ---- micro-harness ---------------------------------------------------------------------------
let failures = 0;
let count = 0;
function test(name: string, fn: () => void | Promise<void>): void {
  count++;
  const failed = (e: unknown) => {
    failures++;
    console.log(`  FAIL - ${name}\n      ${(e as Error).message}`);
  };
  try {
    const r = fn();
    if (r instanceof Promise) r.then(() => console.log(`  ok - ${name}`), failed);
    else console.log(`  ok - ${name}`);
  } catch (e) {
    failed(e);
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
  functionView(...a: unknown[]) { return this.call('functionView', a); }
  subcategory(...a: unknown[]) { return this.call('subcategory', a); }
  save(...a: unknown[]) { return this.call('save', a); }
  assignees() { return this.call('assignees', []); }
  assign(...a: unknown[]) { return this.call('assign', a); }
  review(...a: unknown[]) { return this.call('review', a); }
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
  go(params: Record<string, string>, query: Record<string, string>): void {
    this.params.next(convertToParamMap(params));
    this.query.next(convertToParamMap(query));
  }
}
class FakeRouter {
  readonly navigations: { commands: unknown[]; queryParams: unknown }[] = [];
  answer = true;
  navigate(commands: unknown[], extras: { queryParams?: unknown } = {}): Promise<boolean> {
    this.navigations.push({ commands, queryParams: extras.queryParams });
    return Promise.resolve(this.answer);
  }
}

function injector(api: FakeNist, route: FakeRoute, router: FakeRouter, tenant: ReturnType<typeof signal<string | null>>, role = 'Manager'): Injector {
  return Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: NistService, useValue: api },
      { provide: NistSelectionService, useValue: { read: () => null, write: () => {} } },
      { provide: AuthService, useValue: { activeRole: () => role, activeTenantId: tenant } },
      { provide: GovernanceService, useValue: { listDocuments: () => new Subject().asObservable() } },
      { provide: ActivatedRoute, useValue: route },
      { provide: Router, useValue: router },
    ],
  });
}

let confirmAnswer = true;
let confirmCalls: string[] = [];
(globalThis as unknown as { confirm: (m: string) => boolean }).confirm = (m: string) => {
  confirmCalls.push(m);
  return confirmAnswer;
};

// ---- dados sintéticos ------------------------------------------------------------------------
const ASSESS = 'a0000000-0000-0000-0000-000000000001';
const SCOPE = 's0000000-0000-0000-0000-000000000001';
const CYCLE = 'c0000000-0000-0000-0000-000000000002';
const CYCLE_B = 'c0000000-0000-0000-0000-000000000003';
const Q = { avaliacao: ASSESS, rodada: CYCLE, escopo: SCOPE };
const RC_CODES = ['RC.RP-01', 'RC.RP-02', 'RC.RP-03', 'RC.RP-04', 'RC.RP-05', 'RC.RP-06', 'RC.CO-03', 'RC.CO-04'];

function evaluation(version: number, over: Partial<NistEvaluation> = {}): NistEvaluation {
  return {
    id: `e-${version}`, state: 'Evaluated', currentLevel: 2, targetLevel: 4, gap: 2, notApplicable: false, currentComments: null,
    targetComments: null, rationale: `justificativa v${version}`, gaps: null, riskImpact: null, improvementGuidance: null, ownerName: null,
    evaluatedBy: 'analista@demo.example.com', reviewedByName: 'Analista Demo', reviewedAt: '2026-10-03T12:00:00Z', version,
    humanConfirmed: true, reviewState: 'None', ...over,
  };
}
function detail(code: string, ev: NistEvaluation | null, findings: Partial<NistFinding>[] = [], cycleId = CYCLE): NistSubcategoryDetail {
  return {
    assessmentId: ASSESS, scopeId: SCOPE, cycleId, cycleName: 'out/2026', cycleStatus: 'Open', procedures: [],
    findings: findings as NistFinding[], code, functionCode: code.slice(0, 2) as NistSubcategoryDetail['functionCode'], functionName: 'Recover',
    categoryCode: code.split('-')[0], categoryName: 'Categoria', title: `Título ${code}`, summary: '', impact: '', initialAction: '',
    officialOutcome: '', implementationExamples: null, evaluation: ev, evidence: [], availableEvidence: [], posture: null, maturityScale: [],
    methodologyVersion: 'aegis-methodology-v1',
  };
}
function functionView(cycleId = CYCLE, over: Partial<{ state: string; currentLevel: number | null }> = {}): NistFunctionView {
  const row = (code: string) => ({
    code, title: `Título ${code}`, state: (code === 'RC.RP-06' && over.state ? over.state : 'NotEvaluated') as 'NotEvaluated',
    currentLevel: code === 'RC.RP-06' ? (over.currentLevel ?? null) : null, targetLevel: null, gap: null, ownerName: null, evidenceCount: 0, reviewedAt: null,
  });
  const profile = { code: 'RC', current: null, target: null, gap: null, subcategories: 8, withCurrent: 0, withTarget: 0, withGap: 0, notApplicable: 0, evaluated: 0 };
  return {
    assessmentId: ASSESS, scopeId: SCOPE, cycleId, code: 'RC', name: 'Recover', definition: '', profile,
    categories: [
      { code: 'RC.RP', name: 'Incident Recovery Plan Execution', definition: '', profile, subcategories: RC_CODES.slice(0, 6).map(row) },
      { code: 'RC.CO', name: 'Incident Recovery Communication', definition: '', profile, subcategories: RC_CODES.slice(6).map(row) },
    ],
  };
}
const ASSESSMENTS = [{
  id: ASSESS, name: 'Avaliação sintética', description: null, status: 'Active', startDate: null, endDate: null,
  methodologyVersion: 'aegis-methodology-v1', frameworkName: 'NIST CSF 2.0', createdAt: '2026-10-01T00:00:00Z', lastReviewedAt: null,
  scopes: [{ id: SCOPE, name: 'Escopo sintético', description: null, subcategories: 106, evaluated: 0, notApplicable: 0, inProgress: 0, withEvidence: 0, lastReviewedAt: null }],
  cycles: [{ id: CYCLE, name: 'out/2026', periodKind: 'Monthly', periodStart: '2026-10-01', periodEnd: '2026-10-31', status: 'Open', seedFromCycleId: null,
    seedFromCycleName: null, seedMode: 'None', createdAt: '2026-10-01T00:00:00Z', createdByName: null, closedAt: null, closedByName: null, version: 1, publications: 0 }],
}];

function openSub(fn: string, code: string, role = 'Manager') {
  const api = new FakeNist();
  const route = new FakeRoute();
  route.go({ fn, code }, Q);
  const router = new FakeRouter();
  const tenant = signal<string | null>('tenant-1');
  const c = runInInjectionContext(injector(api, route, router, tenant, role), () => new NistSubcategoryComponent());
  return { c, v: c as any, api, route, router, tenant };
}

function openNav() {
  const api = new FakeNist();
  const router = new FakeRouter();
  const tenant = signal<string | null>('tenant-1');
  const c = runInInjectionContext(injector(api, new FakeRoute(), router, tenant), () => new NistSubcategoryNavComponent());
  const emitted: NistWorkspaceContext[] = [];
  c.context.subscribe((x) => emitted.push(x));
  return { c, v: c as any, api, router, tenant, emitted };
}

console.log('AEGIS-NIST-RECOVER-GUIDE-01 · roteiro de RECOVER e área de trabalho');

// ---- (1) integridade do conteúdo -------------------------------------------------------------

test('cobertura exata de RECOVER: 8 códigos, sem duplicata nem código inventado', () => {
  const codes = NIST_INTERVIEW_GUIDES.map((g) => g.code);
  eq(codes.length, 8, 'oito subcategorias');
  eq(new Set(codes).size, 8, 'sem duplicata');
  eq([...codes].sort().join(','), [...RC_CODES].sort().join(','), 'exatamente RC.RP-01..06, RC.CO-03 e RC.CO-04');
  for (const g of NIST_INTERVIEW_GUIDES) eq(g.categoryCode, g.code.split('-')[0], `${g.code}: categoria coerente`);
});

test('totais do pacote: 8 principais, 53 complementares, 40 situações ilustrativas — nenhum texto vazio', () => {
  eq(NIST_INTERVIEW_GUIDES.filter((g) => g.mainQuestion.trim().length > 20).length, 8, 'perguntas principais');
  eq(NIST_INTERVIEW_GUIDES.reduce((n, g) => n + g.followUps.length, 0), 53, 'perguntas complementares');
  eq(NIST_INTERVIEW_GUIDES.reduce((n, g) => n + g.levelExamples.length, 0), 40, 'situações ilustrativas');
  for (const g of NIST_INTERVIEW_GUIDES) {
    ok(g.followUps.length >= 6 && g.followUps.length <= 7, `${g.code}: seis ou sete aprofundamentos`);
    ok(g.followUps.every((f) => !!f.question.trim() && !!f.focus.trim()), `${g.code}: pergunta e foco preenchidos`);
    ok(g.levelExamples.every((x) => x.trim().length > 20), `${g.code}: cinco situações preenchidas`);
    ok(!!g.title.trim() && !!g.interpretationNote.trim() && !!g.officialOutcome.trim(), `${g.code}: título, nota e resultado oficial`);
  }
});

test('exemplos de Felipe preservados literalmente (RC.RP-06 e RC.CO-03)', () => {
  const rp06 = interviewGuideFor('RC.RP-06')!;
  eq(rp06.mainQuestion,
    'Após a recuperação de um incidente de segurança, como vocês determinam que todas as atividades foram concluídas e que o processo pode ser formalmente encerrado? Existem critérios documentados e alguém responsável por aprovar esse encerramento?',
    'pergunta principal de RC.RP-06');
  eq(rp06.followUps[0].question, 'Existem critérios para considerar a recuperação concluída?', '1ª complementar de RC.RP-06');
  eq(rp06.followUps[6].focus, 'Evidência de aplicação', 'foco da 7ª complementar de RC.RP-06');
  eq(rp06.levelExamples[0], 'O encerramento ocorre verbalmente, sem critérios definidos ou documentação consistente.', 'nível 1 de RC.RP-06');
  const co03 = interviewGuideFor('rc.co-03')!;
  eq(co03.mainQuestion,
    'Durante a recuperação de um incidente de segurança, como vocês comunicam o andamento das atividades e o restabelecimento dos serviços às áreas internas e às partes externas envolvidas? Existem responsáveis, canais e critérios definidos para essas atualizações?',
    'pergunta principal de RC.CO-03 (busca sem diferenciar caixa)');
  eq(co03.followUps.map((f) => f.focus).join('|'),
    'Partes interessadas designadas|Responsabilidades|Meios de comunicação|Regularidade|Formalização|Rastreabilidade|Execução prática', 'focos de RC.CO-03');
});

test('nenhuma outra função ganha roteiro; subcategoria sem roteiro não recebe texto genérico', () => {
  eq(hasInterviewGuides('RC'), true, 'RECOVER tem roteiro');
  for (const f of NIST_FUNCTIONS.filter((x) => x.code !== 'RC')) eq(hasInterviewGuides(f.code), false, `${f.code} sem roteiro`);
  eq(interviewGuideFor('GV.OC-01'), null, 'GV.OC-01 sem roteiro');
  eq(interviewGuideFor('RS.MA-05'), null, 'RS.MA-05 (citada na nota) sem roteiro');
  eq(interviewGuideFor(null), null, 'código ausente');
});

test('catálogo único de nomes de nível: Executado, Documentado, Gerenciado, Quantitativo, Otimização', () => {
  eq([1, 2, 3, 4, 5].map((l) => MATURITY_LEVEL_LABELS[l]).join(','), 'Executado,Documentado,Gerenciado,Quantitativo,Otimização', 'rótulos');
  eq(levelLabel(4), '4 · Quantitativo', 'apresentação do nível 4');
  eq(levelLabel(null), 'Sem nível', 'ausência continua sem nível');
  eq(Object.keys(MATURITY_LEVEL_LABELS).length, 5, 'mesma escala de cinco valores');
});

// ---- (2) faixa e lista derivadas dos estados existentes --------------------------------------

test('faixa: sem avaliação, sem revisor e sem achados são situações neutras (nada vira zero ou erro)', () => {
  const [a, r, t] = trackingSteps(detail('RC.RP-06', null));
  eq(a.status, 'Não avaliada', 'avaliação');
  ok(!/\b0\b/.test(`${a.status} ${a.detail}`), 'sem nota zero');
  eq(r.status, 'Sem decisão do revisor', 'revisão');
  eq(r.detail, 'revisor não designado', 'revisor');
  eq(t.status, 'Sem achados registrados', 'tratamento');
  eq(t.tone, 'neutral', 'sem plano não é erro');
});

test('faixa: revisão desatualizada e devolvida continuam distinguíveis; plano vencido aparece no tratamento', () => {
  const out = trackingSteps(detail('RC.RP-06', evaluation(3, { reviewState: 'Outdated', reviewerName: 'Revisora Demo' })))[1];
  eq(out.status, 'Decisão anterior à versão vigente', 'desatualizada');
  eq(out.tone, 'violet', 'tom próprio');
  const back = trackingSteps(detail('RC.RP-06', evaluation(3, { reviewState: 'ChangesRequested' })))[1];
  eq(back.status, 'Devolvida para ajuste', 'devolvida');
  const findings = [
    { status: 'Open', plan: null },
    { status: 'Open', plan: { isOverdue: true } },
    { status: 'Closed', plan: { isOverdue: false } },
  ] as unknown as Partial<NistFinding>[];
  const t = trackingSteps(detail('RC.RP-06', evaluation(3), findings))[2];
  eq(t.status, '3 achado(s)', 'contagem');
  eq(t.detail, '2 aberto(s) · 2 com plano · 1 plano(s) vencido(s)', 'detalhe');
  const a = trackingSteps(detail('RC.RP-06', evaluation(3, { humanConfirmed: false, state: 'PendingConfirmation' })))[0];
  eq(a.detail, 'atual 2 · Documentado · alvo 4 · Quantitativo · conteúdo a confirmar', 'conteúdo a confirmar');
});

test('lista: título curto do roteiro; sem nível não vira número; não se aplica sem níveis', () => {
  eq(navTitle({ code: 'RC.RP-06', title: 'Declarar o fim da recuperação' }), 'Encerramento formal da recuperação', 'título do roteiro');
  eq(navTitle({ code: 'GV.OC-01', title: 'Catálogo' }), 'Catálogo', 'fora do roteiro usa o catálogo');
  eq(navLevelText({ state: 'NotEvaluated', currentLevel: null, targetLevel: null }), null, 'nada a mostrar');
  eq(navLevelText({ state: 'InProgress', currentLevel: 2, targetLevel: null }), 'atual 2 · alvo sem nível', 'alvo ausente');
  eq(navLevelText({ state: 'NotApplicable', currentLevel: null, targetLevel: null }), null, 'não se aplica');
});

// ---- (3) tela real ---------------------------------------------------------------------------

test('RECOVER abre na área de trabalho com o roteiro; atual e alvo independentes no mesmo rascunho', () => {
  const e = openSub('rc', 'RC.RP-06');
  eq(e.v.workspace(), true, 'área de trabalho');
  eq(e.v.guide()?.code, 'RC.RP-06', 'roteiro da subcategoria');
  reply(e.api.last('subcategory'), detail('RC.RP-06', evaluation(1, { currentLevel: null, targetLevel: null, gap: null })));
  e.v.draft.currentLevel = 2;
  eq(e.v.draft.targetLevel, null, 'escolher o atual não mexe no alvo');
  eq(e.v.gap(), null, 'lacuna indeterminada sem alvo');
  e.v.draft.targetLevel = 4;
  eq(e.v.draft.currentLevel, 2, 'escolher o alvo não mexe no atual');
  eq(e.v.gap(), draftGap(e.v.draft), 'mesmo cálculo de lacuna');
  eq(e.v.gap(), 2, 'lacuna 2');
  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[2] as { currentLevel: number; targetLevel: number; expectedVersion: number };
  eq(`${sent.currentLevel}/${sent.targetLevel}/${sent.expectedVersion}`, '2/4/1', 'a gravação existente leva atual, alvo e versão-base');
});

test('sair com rascunho não gravado pergunta; recusar mantém o rascunho; gravado sai sem perguntar', () => {
  const e = openSub('rc', 'RC.CO-03');
  reply(e.api.last('subcategory'), detail('RC.CO-03', evaluation(1)));
  confirmCalls = [];
  eq(e.v.canLeave(), true, 'sem edição sai direto');
  eq(confirmCalls.length, 0, 'sem pergunta');
  e.v.draft.targetLevel = 5;
  confirmAnswer = false;
  eq(e.v.canLeave(), false, 'recusar mantém a tela');
  ok(confirmCalls[0]?.includes('RC.CO-03'), 'a pergunta nomeia a subcategoria');
  eq(e.v.draft.targetLevel, 5, 'o rascunho continua');
  confirmAnswer = true;
  eq(e.v.canLeave(), true, 'aceitar descarta por decisão da pessoa');
  e.v.save(e.v.detail());
  reply(e.api.last('save'), detail('RC.CO-03', evaluation(2, { targetLevel: 5, gap: 3 })));
  confirmCalls = [];
  eq(e.v.canLeave(), true, 'depois de gravar sai sem perguntar');
  eq(confirmCalls.length, 0, 'sem pergunta após gravar');
});

test('a lista é avisada depois de gravar ou revisar — não a cada leitura nem a cada evidência', () => {
  const e = openSub('rc', 'RC.RP-01');
  reply(e.api.last('subcategory'), detail('RC.RP-01', evaluation(1)));
  eq(e.v.navTick(), 0, 'carregar não relê a lista');
  e.v.refreshSoft();
  reply(e.api.last('subcategory'), detail('RC.RP-01', evaluation(1)));
  eq(e.v.navTick(), 0, 'atualizar evidências/achados não relê a lista');
  e.v.draft.rationale = 'nova justificativa';
  e.v.save(e.v.detail());
  reply(e.api.last('save'), detail('RC.RP-01', evaluation(2)));
  eq(e.v.navTick(), 1, 'gravação avisa a lista');
  e.v.review(e.v.detail(), 'Approved');
  reply(e.api.last('review'), detail('RC.RP-01', evaluation(3, { reviewState: 'Approved' })));
  eq(e.v.navTick(), 2, 'revisão avisa a lista');
});

test('gravação de A que termina depois da troca para B não avisa a lista nem repovoa B', () => {
  const e = openSub('rc', 'RC.RP-01');
  reply(e.api.last('subcategory'), detail('RC.RP-01', evaluation(1)));
  e.v.draft.currentLevel = 3;
  e.v.save(e.v.detail());
  const saveA = e.api.last('save');
  e.route.go({ fn: 'rc', code: 'RC.RP-02' }, Q);
  reply(e.api.last('subcategory'), detail('RC.RP-02', evaluation(4)));
  reply(saveA, detail('RC.RP-01', evaluation(2, { currentLevel: 3 })));
  eq(e.v.detail()?.code, 'RC.RP-02', 'B continua');
  eq(e.v.guide()?.code, 'RC.RP-02', 'roteiro de B');
  eq(e.v.navTick(), 0, 'resposta descartada não relê a lista');
});

test('rodada encerrada: só consulta (canEdit falso) e a área de trabalho segue disponível', () => {
  const e = openSub('rc', 'RC.RP-06');
  reply(e.api.last('subcategory'), { ...detail('RC.RP-06', evaluation(1)), cycleStatus: 'Closed' });
  eq(e.v.canEdit(), false, 'rodada encerrada bloqueia a edição');
  eq(e.v.workspace(), true, 'lista e roteiro continuam para consulta');
});

test('papel somente leitura (Analyst): consulta o roteiro e a tabela, sem edição', () => {
  const e = openSub('rc', 'RC.RP-03', 'Analyst');
  reply(e.api.last('subcategory'), detail('RC.RP-03', evaluation(1)));
  eq(e.v.canWrite(), false, 'sem papel de escrita');
  eq(e.v.canEdit(), false, 'sem edição');
  eq(e.v.workspace(), true, 'área de trabalho para consulta');
  eq(e.v.guide()?.code, 'RC.RP-03', 'roteiro visível');
});

test('Não se aplica e Sem nível continuam distintos e com as validações existentes', () => {
  const e = openSub('rc', 'RC.CO-04');
  reply(e.api.last('subcategory'), detail('RC.CO-04', null));
  eq(e.v.draft.currentLevel, null, 'sem nível por padrão');
  eq(e.v.draft.notApplicable, false, 'não é "não se aplica"');
  e.v.draft.notApplicable = true;
  ok(!!e.v.problem(), 'não se aplica exige justificativa (validação existente)');
  e.v.draft.rationale = 'Organização sem canal público; sem divulgação pública prevista.';
  eq(e.v.problem(), null, 'com justificativa a gravação é permitida');
});

test('subcategoria de outra função: sem área de trabalho, sem roteiro e sem pergunta ao sair', () => {
  const e = openSub('gv', 'GV.OC-01');
  eq(e.v.workspace(), false, 'GV segue sem lista contextual');
  eq(e.v.guide(), null, 'sem roteiro');
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.draft.rationale = 'edição não gravada';
  confirmCalls = [];
  eq(e.v.canLeave(), true, 'comportamento anterior preservado');
  eq(confirmCalls.length, 0, 'nenhuma pergunta');
});

test('rota: guards rodam quando muda avaliação, rodada, escopo, função ou subcategoria — não por fragmento ou outro parâmetro', () => {
  const snap = (params: Record<string, string>, query: Record<string, string>) =>
    ({ paramMap: convertToParamMap(params), queryParamMap: convertToParamMap(query) }) as any;
  const base = snap({ fn: 'rc', code: 'RC.RP-06' }, Q);
  eq(nistSubcategoryContextChanged(base, snap({ fn: 'rc', code: 'RC.RP-06' }, { ...Q, rodada: CYCLE_B })), true, 'rodada');
  eq(nistSubcategoryContextChanged(base, snap({ fn: 'rc', code: 'RC.RP-06' }, { ...Q, escopo: 'outro' })), true, 'escopo');
  eq(nistSubcategoryContextChanged(base, snap({ fn: 'rc', code: 'RC.RP-06' }, { ...Q, avaliacao: 'outra' })), true, 'avaliação');
  eq(nistSubcategoryContextChanged(base, snap({ fn: 'rc', code: 'RC.CO-03' }, Q)), true, 'subcategoria');
  eq(nistSubcategoryContextChanged(base, snap({ fn: 'rc', code: 'rc.rp-06' }, Q)), false, 'mesma subcategoria em outra caixa');
  eq(nistSubcategoryContextChanged(base, snap({ fn: 'rc', code: 'RC.RP-06' }, { ...Q, situacao: 'Evaluated' })), false, 'outro parâmetro');
});

// ---- (4) lista contextual --------------------------------------------------------------------

const RC_META = NIST_FUNCTIONS.find((f) => f.code === 'RC')!;

test('lista: lê a visão da função e os nomes do contexto; emite só para o contexto pedido', () => {
  const n = openNav();
  n.c.fn = RC_META;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.code = 'RC.RP-06';
  n.c.refresh = 0;
  eq(n.api.of('functionView').length, 1, 'uma leitura da função');
  eq((n.api.last('functionView').args[1] as string), 'RC', 'função RC');
  reply(n.api.last('list'), ASSESSMENTS);
  reply(n.api.last('functionView'), functionView());
  eq(n.v.view()?.categories.length, 2, 'RC.RP e RC.CO');
  eq(n.emitted.length, 1, 'contexto emitido');
  eq(n.emitted[0].assessment, 'Avaliação sintética', 'nome da avaliação');
  eq(n.emitted[0].scope, 'Escopo sintético', 'nome do escopo');
  ok(n.emitted[0].cycle.startsWith('out/2026'), 'rodada');
  n.c.code = 'RC.RP-01';
  eq(n.api.of('functionView').length, 1, 'trocar de subcategoria não relê a lista');
});

test('lista: contexto substituído descarta a resposta antiga', () => {
  const n = openNav();
  n.c.fn = RC_META;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  const oldList = n.api.last('list');
  const oldView = n.api.last('functionView');
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE_B, scopeId: SCOPE };
  reply(n.api.last('list'), ASSESSMENTS);
  reply(n.api.last('functionView'), functionView(CYCLE_B));
  reply(oldList, ASSESSMENTS);
  reply(oldView, functionView(CYCLE));
  eq(n.v.view()?.cycleId, CYCLE_B, 'a lista é da rodada atual');
});

test('lista: releitura após gravar mantém a lista até chegar; falha não apaga o que já se sabe', () => {
  const n = openNav();
  n.c.fn = RC_META;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.refresh = 0;
  reply(n.api.last('list'), ASSESSMENTS);
  reply(n.api.last('functionView'), functionView());
  n.c.refresh = 1;
  eq(n.api.of('functionView').length, 2, 'relê a visão da função');
  eq(n.api.of('list').length, 1, 'sem reler a lista de avaliações');
  ok(n.v.view() !== null, 'a lista continua exibida durante a releitura');
  reply(n.api.last('functionView'), functionView(CYCLE, { state: 'Evaluated', currentLevel: 3 }));
  eq(n.v.view()?.categories[0].subcategories[5].state, 'Evaluated', 'situação atualizada');
  n.c.refresh = 2;
  fail(n.api.last('functionView'), new NistApiError('falhou', 500));
  eq(n.v.view()?.categories[0].subcategories[5].state, 'Evaluated', 'a falha não apaga a lista');
  ok(!!n.v.softError(), 'aviso discreto');
});

test('lista: seletor de tela estreita navega na mesma avaliação; recusa (rascunho) volta a seleção', async () => {
  const n = openNav();
  n.c.fn = RC_META;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.code = 'RC.RP-06';
  const sel = { value: 'RC.CO-03' };
  n.router.answer = false;
  n.v.pick({ target: sel });
  const nav = n.router.navigations[0];
  eq((nav.commands as string[]).join('/'), '/nist/rc/RC.CO-03', 'rota existente da subcategoria');
  eq(JSON.stringify(nav.queryParams), JSON.stringify(Q), 'mesma avaliação, rodada e escopo');
  await Promise.resolve();
  await Promise.resolve();
  eq(sel.value, 'RC.RP-06', 'navegação recusada volta para a subcategoria aberta');
});

// ---- (5) template real -----------------------------------------------------------------------

function templateText(file: string): string {
  const src = readFileSync(join(process.cwd(), 'src', 'app', 'pages', 'nist', file), 'utf8').replace(/\r\n/g, '\n');
  const m = /template:\s*`([\s\S]*?)`,\n\s{2}styles:/.exec(src);
  if (!m) throw new Error(`template de ${file} não isolado`);
  const parsed = parseTemplate(m[1], file);
  if (parsed.errors?.length) throw new Error(`template inválido: ${parsed.errors[0].msg}`);
  ok((parsed.nodes as TmplAstNode[]).length > 0, 'template tem nós');
  return m[1];
}

test('template da subcategoria: pergunta, aprofundamento e tabela com atual e alvo', () => {
  const t = templateText('nist-subcategory.component.ts');
  for (const s of ['Pergunta principal', 'Aprofundar a entrevista', 'Pergunta complementar', 'O que investigar', 'Situação ilustrativa',
    '>Atual<', '>Alvo<', 'Sem nível', 'não se aplica ao escopo', 'Acompanhamento da subcategoria']) ok(t.includes(s), `contém "${s}"`);
  ok(t.indexOf('g.mainQuestion') < t.indexOf('name="cur"'), 'a pergunta principal vem antes da escolha de maturidade');
});

test('template da lista: seletor acessível e item atual marcado sem depender só de cor', () => {
  const t = templateText('nist-subcategory-nav.component.ts');
  ok(t.includes("aria-current") && t.includes('<select'), 'aria-current e seletor');
});

// Testes assíncronos (o último) terminam depois do laço: o resumo sai no próximo ciclo.
setTimeout(() => {
  console.log(`\n${count - failures}/${count} ok`);
  if (failures > 0) process.exit(1);
}, 50);
