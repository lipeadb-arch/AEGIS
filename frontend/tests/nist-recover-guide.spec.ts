/**
 * [AEGIS-NIST-RECOVER-GUIDE-01] Roteiro editorial de RECOVER e área de trabalho da subcategoria.
 *
 * O que este spec trava:
 *   (1) integridade do conteúdo empacotado: exatamente as 8 subcategorias de RECOVER, sem código duplicado ou inventado;
 *       8 perguntas principais, 53 complementares e 40 situações ilustrativas; os exemplos de Felipe (RC.RP-06 e RC.CO-03)
 *       literais; as demais funções só recebem o próprio pacote; o catálogo ÚNICO de nomes de nível tem os rótulos do pacote;
 *   (2) faixa de acompanhamento e lista contextual derivam só dos estados existentes: sem avaliação não vira zero, sem
 *       achados é neutro, revisão desatualizada/devolvida continuam distinguíveis;
 *   (3) na tela real: atual e alvo independentes sobre o MESMO rascunho (lacuna e gravação existentes); a saída com
 *       rascunho não gravado pergunta antes e recusar mantém o rascunho; a lista é avisada só depois de gravar; sem
 *       contexto completo (avaliação e escopo) a tela segue sem área de trabalho e sem pergunta ao sair;
 *   (4) lista contextual: contexto substituído descarta a resposta antiga; releitura após gravação não apaga a lista e
 *       falha nela não derruba o que já se sabe; o seletor recusado volta à subcategoria aberta;
 *   (5) o template real compila e traz as seções pedidas (pergunta principal, aprofundamento, tabela com atual e alvo).
 *   (6) [AEGIS-NIST-RESPOND-GUIDE-01] RESPOND no MESMO catálogo: exatamente as 13 subcategorias de RS (sem preencher os
 *       intervalos ausentes), 13/78/65, impressão digital do texto integral do pacote respond-pt-BR-v1.0, distinções entre
 *       subcategorias vizinhas, orientações próprias do pacote e a mesma área de trabalho na tela real (lista RS.MA/AN/CO/MI,
 *       atual e alvo independentes, Sem nível × Não se aplica, papel e rodada encerrada). A infraestrutura comum (rascunho,
 *       respostas atrasadas, guards) continua coberta pelas seções (3) e (4); não é replicada por função.
 *   (7) DETECT no mesmo catálogo: 11 códigos DE.CM/DE.AE, 11/66/55, impressão digital do pacote detect-pt-BR-v1.0, distinções
 *       das notas, orientações próprias sem vazar para RS/RC, e a tela real percorrendo DE → RS → RC → GV.
 *   (8) PROTECT no mesmo catálogo: 22 códigos PR.AA/AT/DS/PS/IR, 22/132/110, impressão digital do pacote protect-pt-BR-v1.0,
 *       distinções das notas, nenhuma pergunta pedindo segredo, lista da função PR e a tela real percorrendo PR → DE → RS → RC → GV.
 *   (9) IDENTIFY no mesmo catálogo: 21 códigos ID.AM/RA/IM (sem ID.AM-06), 21/126/105, impressão digital do pacote
 *       identify-pt-BR-v1.0, distinções das notas, lista da função ID e a tela real percorrendo ID → PR → DE → RS → RC → GV.
 *  (10) GOVERN no mesmo catálogo: 31 códigos GV.OC/RM/RR/PO/OV/SC, 31/186/155, impressão digital do pacote govern-pt-BR-v1.0,
 *       distinções das notas; o conjunto cobre exatamente as 106 subcategorias do catálogo oficial; a tela real percorre
 *       GV → ID → PR → DE → RS → RC → GV e o rascunho de GV passa a ser protegido na saída.
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
  NIST_INTERVIEW_GUIDE_NOTES,
  NIST_INTERVIEW_GUIDE_VERSIONS,
  NistInterviewGuide,
  hasInterviewGuides,
  interviewGuideFor,
  interviewGuideNotes,
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
const RS_CATEGORIES: Record<string, string[]> = {
  'RS.MA': ['RS.MA-01', 'RS.MA-02', 'RS.MA-03', 'RS.MA-04', 'RS.MA-05'],
  'RS.AN': ['RS.AN-03', 'RS.AN-06', 'RS.AN-07', 'RS.AN-08'],
  'RS.CO': ['RS.CO-02', 'RS.CO-03'],
  'RS.MI': ['RS.MI-01', 'RS.MI-02'],
};
const RS_CODES = Object.values(RS_CATEGORIES).flat();
const DE_CATEGORIES: Record<string, string[]> = {
  'DE.CM': ['DE.CM-01', 'DE.CM-02', 'DE.CM-03', 'DE.CM-06', 'DE.CM-09'],
  'DE.AE': ['DE.AE-02', 'DE.AE-03', 'DE.AE-04', 'DE.AE-06', 'DE.AE-07', 'DE.AE-08'],
};
const DE_CODES = Object.values(DE_CATEGORIES).flat();
const PR_CATEGORIES: Record<string, string[]> = {
  'PR.AA': ['PR.AA-01', 'PR.AA-02', 'PR.AA-03', 'PR.AA-04', 'PR.AA-05', 'PR.AA-06'],
  'PR.AT': ['PR.AT-01', 'PR.AT-02'],
  'PR.DS': ['PR.DS-01', 'PR.DS-02', 'PR.DS-10', 'PR.DS-11'],
  'PR.PS': ['PR.PS-01', 'PR.PS-02', 'PR.PS-03', 'PR.PS-04', 'PR.PS-05', 'PR.PS-06'],
  'PR.IR': ['PR.IR-01', 'PR.IR-02', 'PR.IR-03', 'PR.IR-04'],
};
const PR_CODES = Object.values(PR_CATEGORIES).flat();
const ID_CATEGORIES: Record<string, string[]> = {
  'ID.AM': ['ID.AM-01', 'ID.AM-02', 'ID.AM-03', 'ID.AM-04', 'ID.AM-05', 'ID.AM-07', 'ID.AM-08'],
  'ID.RA': ['ID.RA-01', 'ID.RA-02', 'ID.RA-03', 'ID.RA-04', 'ID.RA-05', 'ID.RA-06', 'ID.RA-07', 'ID.RA-08', 'ID.RA-09', 'ID.RA-10'],
  'ID.IM': ['ID.IM-01', 'ID.IM-02', 'ID.IM-03', 'ID.IM-04'],
};
const ID_CODES = Object.values(ID_CATEGORIES).flat();
const GV_CATEGORIES: Record<string, string[]> = {
  'GV.OC': ['GV.OC-01', 'GV.OC-02', 'GV.OC-03', 'GV.OC-04', 'GV.OC-05'],
  'GV.RM': ['GV.RM-01', 'GV.RM-02', 'GV.RM-03', 'GV.RM-04', 'GV.RM-05', 'GV.RM-06', 'GV.RM-07'],
  'GV.RR': ['GV.RR-01', 'GV.RR-02', 'GV.RR-03', 'GV.RR-04'],
  'GV.PO': ['GV.PO-01', 'GV.PO-02'],
  'GV.OV': ['GV.OV-01', 'GV.OV-02', 'GV.OV-03'],
  'GV.SC': ['GV.SC-01', 'GV.SC-02', 'GV.SC-03', 'GV.SC-04', 'GV.SC-05', 'GV.SC-06', 'GV.SC-07', 'GV.SC-08', 'GV.SC-09', 'GV.SC-10'],
};
const GV_CODES = Object.values(GV_CATEGORIES).flat();
/** Roteiros de uma função (o catálogo é único para todas as funções com conteúdo editorial). */
const guidesOf = (fn: string): NistInterviewGuide[] => NIST_INTERVIEW_GUIDES.filter((g) => g.code.startsWith(fn + '.'));

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
function rsFunctionView(): NistFunctionView {
  const row = (code: string) => ({
    code, title: `Título ${code}`, state: 'NotEvaluated' as const, currentLevel: null, targetLevel: null, gap: null, ownerName: null, evidenceCount: 0, reviewedAt: null,
  });
  const profile = { code: 'RS', current: null, target: null, gap: null, subcategories: 13, withCurrent: 0, withTarget: 0, withGap: 0, notApplicable: 0, evaluated: 0 };
  return {
    assessmentId: ASSESS, scopeId: SCOPE, cycleId: CYCLE, code: 'RS', name: 'Respond', definition: '', profile,
    categories: Object.entries(RS_CATEGORIES).map(([code, codes]) => ({ code, name: `Categoria ${code}`, definition: '', profile, subcategories: codes.map(row) })),
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

test('catálogo único: 106 roteiros (RC 8 + RS 13 + DE 11 + PR 22 + ID 21 + GV 31), sem código duplicado e cada um com a categoria do próprio código', () => {
  const codes = NIST_INTERVIEW_GUIDES.map((g) => g.code);
  eq(codes.length, 106, 'cento e seis roteiros');
  eq(new Set(codes).size, 106, 'sem duplicata');
  eq([...codes].sort().join(','), [...RC_CODES, ...RS_CODES, ...DE_CODES, ...PR_CODES, ...ID_CODES, ...GV_CODES].sort().join(','), 'RC, RS, DE, PR, ID e GV');
  for (const g of NIST_INTERVIEW_GUIDES) eq(g.categoryCode, g.code.split('-')[0], `${g.code}: categoria coerente`);
});

test('cobertura exata de RECOVER: 8 códigos, sem duplicata nem código inventado', () => {
  const codes = guidesOf('RC').map((g) => g.code);
  eq(codes.length, 8, 'oito subcategorias');
  eq(new Set(codes).size, 8, 'sem duplicata');
  eq([...codes].sort().join(','), [...RC_CODES].sort().join(','), 'exatamente RC.RP-01..06, RC.CO-03 e RC.CO-04');
});

test('totais do pacote: 8 principais, 53 complementares, 40 situações ilustrativas — nenhum texto vazio', () => {
  const rc = guidesOf('RC');
  eq(rc.filter((g) => g.mainQuestion.trim().length > 20).length, 8, 'perguntas principais');
  eq(rc.reduce((n, g) => n + g.followUps.length, 0), 53, 'perguntas complementares');
  eq(rc.reduce((n, g) => n + g.levelExamples.length, 0), 40, 'situações ilustrativas');
  for (const g of rc) {
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

test('as seis funções têm roteiro; código desconhecido, nulo ou vazio continua sem texto genérico', () => {
  for (const f of NIST_FUNCTIONS) eq(hasInterviewGuides(f.code), true, `${f.code} tem roteiro`);
  eq(hasInterviewGuides(null), false, 'função ausente');
  eq(interviewGuideFor('GV.OC-01')?.code, 'GV.OC-01', 'GV.OC-01 com roteiro');
  eq(interviewGuideFor('GV.SC-07')?.code, 'GV.SC-07', 'GV.SC-07 (citada em ID.AM-04/ID.RA-10) com roteiro');
  eq(interviewGuideFor(''), null, 'código vazio');
  eq(interviewGuideFor('XX.YY-01'), null, 'código desconhecido');
  eq(interviewGuideFor(null), null, 'código ausente');
});

test('orientações e versão editorial por função: RECOVER mantém as seis comuns; RESPOND acrescenta as duas do pacote', () => {
  eq(interviewGuideNotes('RC'), NIST_INTERVIEW_GUIDE_NOTES, 'RECOVER sem mudança');
  eq(NIST_INTERVIEW_GUIDE_NOTES.length, 6, 'seis comuns');
  const rs = interviewGuideNotes('RS');
  eq(rs.length, 8, 'oito em RESPOND');
  eq(rs.slice(0, 6).join('|'), NIST_INTERVIEW_GUIDE_NOTES.join('|'), 'comuns primeiro, na mesma ordem');
  eq(rs[7], 'Não aplicar prazos legais universais: requisitos de notificação e preservação dependem do contexto e devem ser identificados pela organização.', 'prazos legais');
  eq(NIST_INTERVIEW_GUIDE_VERSIONS.RC, 'recover-pt-BR-v1.0', 'procedência de RECOVER preservada');
  eq(NIST_INTERVIEW_GUIDE_VERSIONS.RS, 'respond-pt-BR-v1.0', 'versão do conteúdo de RESPOND');
  eq(NIST_INTERVIEW_GUIDE_VERSIONS.DE, 'detect-pt-BR-v1.0', 'versão do conteúdo de DETECT');
  const de = interviewGuideNotes('DE');
  eq(de.length, 8, 'oito em DETECT');
  eq(de.slice(0, 6).join('|'), NIST_INTERVIEW_GUIDE_NOTES.join('|'), 'DETECT: comuns primeiro');
  ok(de[6].startsWith('Ausência de alertas não demonstra') && de[7].startsWith('Ferramenta instalada, licença'), 'DETECT: as duas do pacote');
  ok(!de.includes(rs[7]) && !rs.includes(de[6]), 'sem orientação de RS em DE nem de DE em RS');
  eq(NIST_INTERVIEW_GUIDE_VERSIONS.PR, 'protect-pt-BR-v1.0', 'versão do conteúdo de PROTECT');
  const pr = interviewGuideNotes('PR');
  eq(pr.length, 8, 'oito em PROTECT');
  eq(pr.slice(0, 6).join('|'), NIST_INTERVIEW_GUIDE_NOTES.join('|'), 'PROTECT: comuns primeiro');
  ok(pr[6].startsWith('Em PROTECT, a execução também pode ser demonstrada por registros operacionais') && pr[6].includes('não é necessário um incidente'), 'PROTECT: execução sem exigir incidente');
  ok(pr[7].includes('Evidências do KNIGHT apoiam a análise'), 'PROTECT: KNIGHT não atribui maturidade');
  ok(![...rs.slice(6), ...de.slice(6)].some((x) => pr.includes(x)) && !rs.includes(pr[6]) && !de.includes(pr[6]), 'orientações próprias não vazam entre funções');
  eq(NIST_INTERVIEW_GUIDE_VERSIONS.ID, 'identify-pt-BR-v1.0', 'versão do conteúdo de IDENTIFY');
  const id = interviewGuideNotes('ID');
  eq(id.length, 8, 'oito em IDENTIFY');
  eq(id.slice(0, 6).join('|'), NIST_INTERVIEW_GUIDE_NOTES.join('|'), 'IDENTIFY: comuns primeiro');
  ok(id[6].startsWith('Em IDENTIFY, inventários, avaliações de risco, registros operacionais, testes e revisões também demonstram práticas') && id[6].includes('ID.IM-01, ID.IM-02 e ID.IM-03 focalizam identificar melhorias'), 'IDENTIFY: práticas sem incidente; IM focaliza identificar');
  ok(id[7].startsWith('Diferencie cobertura de uma fonte técnica, criticidade do ativo, severidade da vulnerabilidade e risco do cenário') && id[7].includes('sem preencher automaticamente o ambiente desconhecido'), 'IDENTIFY: cobertura × criticidade × severidade × risco');
  ok(![...rs.slice(6), ...de.slice(6), ...pr.slice(6)].some((x) => id.includes(x)) && ![rs, de, pr].some((l) => l.includes(id[6]) || l.includes(id[7])), 'orientações de ID não vazam');
  eq(NIST_INTERVIEW_GUIDE_VERSIONS.GV, 'govern-pt-BR-v1.0', 'versão do conteúdo de GOVERN');
  const gv = interviewGuideNotes('GV');
  eq(gv.length, 8, 'oito em GOVERN');
  eq(gv.slice(0, 6).join('|'), NIST_INTERVIEW_GUIDE_NOTES.join('|'), 'GOVERN: comuns primeiro');
  ok(gv[6].startsWith('Em GOVERN, decisões, atas, responsabilidades exercidas, revisões e registros de acompanhamento também demonstram execução; não é necessário um incidente.'), 'GOVERN: execução sem incidente');
  ok(gv[7].startsWith('Considere porte, missão, riscos e relações da organização') && gv[7].includes('Requisitos legais e contratuais dependem do contexto'), 'GOVERN: proporcionalidade e requisitos do contexto');
  ok(![...rs.slice(6), ...de.slice(6), ...pr.slice(6), ...id.slice(6)].some((x) => gv.includes(x)) && ![rs, de, pr, id].some((l) => l.includes(gv[6]) || l.includes(gv[7])), 'orientações de GV não vazam');
  eq(Object.keys(NIST_INTERVIEW_GUIDE_VERSIONS).sort().join(','), 'DE,GV,ID,PR,RC,RS', 'uma versão editorial por função');
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
  eq(navTitle({ code: 'XX.YY-01', title: 'Catálogo' }), 'Catálogo', 'código sem roteiro usa o catálogo');
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

test('GOVERN com contexto completo: área de trabalho e rascunho protegido; cancelar mantém tudo com uma só pergunta', () => {
  const e = openSub('gv', 'GV.OC-01');
  eq(e.v.workspace(), true, 'GV com lista contextual');
  eq(e.v.guide()?.code, 'GV.OC-01', 'roteiro de GV.OC-01');
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  e.v.draft.currentLevel = 1;
  e.v.draft.targetLevel = 3;
  confirmCalls = [];
  confirmAnswer = false;
  eq(e.v.canLeave(), false, 'cancelar mantém a tela');
  eq(confirmCalls.length, 1, 'uma única confirmação');
  ok(confirmCalls[0].includes('GV.OC-01'), 'a pergunta nomeia a subcategoria');
  eq(`${e.v.draft.currentLevel}/${e.v.draft.targetLevel}`, '1/3', 'rascunho preservado');
  eq(JSON.stringify(e.v.params()), JSON.stringify(Q), 'mesma avaliação, rodada e escopo');
  confirmAnswer = true;
  eq(e.v.canLeave(), true, 'aceitar libera a saída');
  e.route.go({ fn: 'gv', code: 'GV.OC-02' }, Q);
  reply(e.api.last('subcategory'), detail('GV.OC-02', evaluation(1)));
  eq(e.v.detail()?.code, 'GV.OC-02', 'outra subcategoria');
  eq(`${e.v.draft.currentLevel}/${e.v.draft.targetLevel}`, '2/4', 'rascunho anterior não é reaproveitado');
  eq(e.v.canLeave(), true, 'sem edição sai direto');
});

test('sem contexto completo (sem avaliação ou escopo): sem área de trabalho e sem pergunta ao sair', () => {
  const e = openSub('gv', 'GV.OC-01');
  e.route.go({ fn: 'gv', code: 'GV.OC-01' }, { rodada: CYCLE });
  eq(e.v.workspace(), false, 'sem lista contextual');
  ok(!!e.v.error(), 'pede para abrir a partir de uma avaliação');
  confirmCalls = [];
  eq(e.v.canLeave(), true, 'nada a proteger');
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

// ---- (6) RESPOND ------------------------------------------------------------------------------

/** FNV-1a de 32 bits sobre as unidades UTF-16 — impressão digital do texto integral, conferida contra o pacote de entrada. */
function fnv1a(s: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 0x01000193) >>> 0;
  return h.toString(16).padStart(8, '0');
}

test('RESPOND: exatamente os 13 códigos do catálogo, na ordem e sem preencher os intervalos ausentes', () => {
  const rs = guidesOf('RS');
  eq(rs.map((g) => g.code).join(','), RS_CODES.join(','), 'RS.MA-01..05, RS.AN-03/06/07/08, RS.CO-02/03, RS.MI-01/02');
  eq(new Set(rs.map((g) => g.code)).size, 13, 'sem duplicata');
  for (const [cat, codes] of Object.entries(RS_CATEGORIES)) eq(rs.filter((g) => g.categoryCode === cat).length, codes.length, `${cat}: ${codes.length}`);
  for (const gap of ['RS.AN-01', 'RS.AN-02', 'RS.AN-04', 'RS.AN-05', 'RS.CO-01', 'RS.MI-03', 'RS.MA-06'])
    eq(interviewGuideFor(gap), null, `${gap} não é inventada`);
});

test('RESPOND: 13 principais, 78 complementares, 65 situações; texto integral idêntico ao pacote respond-pt-BR-v1.0', () => {
  const rs = guidesOf('RS');
  eq(rs.filter((g) => g.mainQuestion.trim().length > 20).length, 13, 'perguntas principais');
  eq(rs.reduce((n, g) => n + g.followUps.length, 0), 78, 'perguntas complementares');
  eq(rs.reduce((n, g) => n + g.levelExamples.length, 0), 65, 'situações ilustrativas');
  for (const g of rs) {
    eq(g.followUps.length, 6, `${g.code}: seis aprofundamentos`);
    ok(g.followUps.every((f) => !!f.question.trim() && !!f.focus.trim()), `${g.code}: pergunta e foco preenchidos`);
    ok(!!g.title.trim() && !!g.interpretationNote.trim() && !!g.officialOutcome.trim(), `${g.code}: título, nota e resultado oficial`);
  }
  const text = rs.flatMap((g) => [g.code, g.categoryCode, g.title, g.mainQuestion, ...g.followUps.flatMap((f) => [f.question, f.focus]),
    ...g.levelExamples, g.interpretationNote, g.officialOutcome]).join('\n');
  eq(text.length, 26047, 'tamanho do texto integral');
  eq(fnv1a(text), '17d9ed49', 'impressão digital do pacote (perguntas, focos, situações, notas e resultados oficiais)');
});

test('RESPOND: amostras literais (RS.MA-05 e RS.MI-02) e resultado oficial em inglês para conferência', () => {
  const ma05 = interviewGuideFor('rs.ma-05')!;
  eq(ma05.mainQuestion,
    'Como vocês avaliam e aplicam os critérios para decidir quando a recuperação de um incidente deve começar? Quem autoriza essa decisão e como são considerados os riscos e os impactos operacionais de iniciar a recuperação naquele momento?',
    'pergunta principal de RS.MA-05 (busca sem diferenciar caixa)');
  const mi02 = interviewGuideFor('RS.MI-02')!;
  eq(mi02.levelExamples[4], 'Os métodos de erradicação e de verificação são continuamente aprimorados com base em incidentes, exercícios, reincidências e resultados medidos.', 'nível 5 de RS.MI-02');
  eq(interviewGuideFor('RS.MA-01')!.officialOutcome, 'The incident response plan is executed in coordination with relevant third parties once an incident is declared', 'resultado oficial');
});

test('RESPOND: distinções entre subcategorias vizinhas continuam explícitas no título e na nota de interpretação', () => {
  const g = (c: string) => interviewGuideFor(c)!;
  eq(g('RS.MA-02').title, 'Triagem e validação dos relatos de incidente', 'triagem');
  eq(g('RS.MA-03').title, 'Categorização e priorização dos incidentes', 'classificação e prioridade');
  ok(g('RS.MA-02').interpretationNote.includes('RS.MA-03'), 'a triagem aponta para a categorização');
  eq(g('RS.MA-05').title, 'Decisão de iniciar a recuperação', 'decisão');
  ok(g('RS.MA-05').interpretationNote.includes('RC.RP-01'), 'a decisão aponta para a execução em RECOVER');
  ok(g('RC.RP-01').interpretationNote.includes('RS.MA-05'), 'e a execução aponta de volta para a decisão');
  eq(g('RS.AN-06').title, 'Registro das ações da investigação', 'registro das ações');
  eq(g('RS.AN-07').title, 'Coleta e preservação dos dados do incidente', 'coleta e preservação');
  ok(g('RS.AN-06').interpretationNote.includes('RS.AN-07'), 'o registro aponta para a coleta');
  eq(g('RS.CO-02').title, 'Notificação do incidente às partes interessadas', 'notificação');
  eq(g('RS.CO-03').title, 'Compartilhamento de informações durante a resposta', 'compartilhamento');
  ok(g('RS.CO-02').interpretationNote.includes('RS.CO-03') && g('RS.CO-03').interpretationNote.includes('RS.CO-02'), 'notificação × compartilhamento');
  eq(g('RS.MI-01').title, 'Contenção do incidente', 'contenção');
  eq(g('RS.MI-02').title, 'Erradicação do incidente', 'erradicação');
  ok(g('RS.MI-01').interpretationNote.includes('não significa erradicar'), 'conter não é erradicar');
});

test('RESPOND na tela real: área de trabalho, roteiro e orientações do pacote; atual e alvo independentes e gravados', () => {
  const e = openSub('rs', 'RS.MA-02');
  eq(e.v.workspace(), true, 'área de trabalho');
  eq(e.v.guide()?.code, 'RS.MA-02', 'roteiro da subcategoria');
  eq(e.v.guideNotes().length, 8, 'orientações comuns + do pacote');
  reply(e.api.last('subcategory'), detail('RS.MA-02', evaluation(1, { currentLevel: null, targetLevel: null, gap: null })));
  e.v.draft.targetLevel = 3;
  eq(e.v.draft.currentLevel, null, 'escolher o alvo não mexe no atual');
  eq(e.v.gap(), null, 'lacuna indeterminada sem atual');
  e.v.draft.currentLevel = 1;
  eq(e.v.draft.targetLevel, 3, 'escolher o atual não mexe no alvo');
  eq(e.v.gap(), draftGap(e.v.draft), 'mesmo cálculo de lacuna');
  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[2] as { currentLevel: number; targetLevel: number; expectedVersion: number };
  eq(`${sent.currentLevel}/${sent.targetLevel}/${sent.expectedVersion}`, '1/3/1', 'mesma gravação: atual, alvo e versão-base');
  reply(e.api.last('save'), detail('RS.MA-02', evaluation(2, { currentLevel: 1, targetLevel: 3, gap: 2 })));
  eq(`${e.v.draft.currentLevel}/${e.v.draft.targetLevel}`, '1/3', 'o que foi gravado volta ao rascunho');
  eq(e.v.navTick(), 1, 'a lista é avisada depois de gravar');
});

test('RESPOND: Não se aplica × Sem nível; papel somente leitura e rodada encerrada só consultam', () => {
  const na = openSub('rs', 'RS.CO-02');
  reply(na.api.last('subcategory'), detail('RS.CO-02', null));
  eq(na.v.draft.currentLevel, null, 'sem nível por padrão');
  eq(na.v.draft.notApplicable, false, 'sem nível não é "não se aplica"');
  na.v.draft.notApplicable = true;
  ok(!!na.v.problem(), 'não se aplica exige justificativa');
  const ro = openSub('rs', 'RS.AN-07', 'Analyst');
  reply(ro.api.last('subcategory'), detail('RS.AN-07', evaluation(1)));
  eq(ro.v.canEdit(), false, 'Analyst não edita');
  eq(ro.v.guide()?.code, 'RS.AN-07', 'mas consulta o roteiro');
  const closed = openSub('rs', 'RS.MI-01');
  reply(closed.api.last('subcategory'), { ...detail('RS.MI-01', evaluation(1)), cycleStatus: 'Closed' });
  eq(closed.v.canEdit(), false, 'rodada encerrada bloqueia a edição');
  eq(closed.v.workspace(), true, 'área de trabalho para consulta');
});

test('RESPOND ↔ RECOVER ↔ GOVERN: a mesma tela troca roteiro e orientações pela rota', () => {
  const e = openSub('rs', 'RS.MI-02');
  reply(e.api.last('subcategory'), detail('RS.MI-02', evaluation(1)));
  e.route.go({ fn: 'rc', code: 'RC.RP-01' }, Q);
  reply(e.api.last('subcategory'), detail('RC.RP-01', evaluation(1)));
  eq(e.v.guide()?.code, 'RC.RP-01', 'roteiro de RECOVER');
  eq(e.v.guideNotes().length, 6, 'orientações de RECOVER sem as do pacote de RESPOND');
  e.route.go({ fn: 'gv', code: 'GV.OC-01' }, Q);
  reply(e.api.last('subcategory'), detail('GV.OC-01', evaluation(1)));
  eq(e.v.workspace(), true, 'GOVERN com área de trabalho');
  eq(e.v.guide()?.code, 'GV.OC-01', 'roteiro de GOVERN');
  ok(e.v.guideNotes()[6].startsWith('Em GOVERN'), 'orientações de GOVERN');
});

test('RESPOND: lista contextual agrupada em RS.MA / RS.AN / RS.CO / RS.MI, com títulos do roteiro e a rota existente', () => {
  const n = openNav();
  n.c.fn = NIST_FUNCTIONS.find((f) => f.code === 'RS')!;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.code = 'RS.MA-01';
  eq((n.api.last('functionView').args[1] as string), 'RS', 'visão da função RS');
  reply(n.api.last('list'), ASSESSMENTS);
  reply(n.api.last('functionView'), rsFunctionView());
  eq(n.v.view()?.categories.map((c: { code: string }) => c.code).join(','), 'RS.MA,RS.AN,RS.CO,RS.MI', 'quatro grupos');
  eq(navTitle({ code: 'RS.MI-01', title: 'Catálogo' }), 'Contenção do incidente', 'título curto do roteiro');
  n.v.pick({ target: { value: 'RS.MI-02' } });
  eq((n.router.navigations[0].commands as string[]).join('/'), '/nist/rs/RS.MI-02', 'rota existente da subcategoria');
  eq(JSON.stringify(n.router.navigations[0].queryParams), JSON.stringify(Q), 'mesma avaliação, rodada e escopo');
});

// ---- (7) DETECT -------------------------------------------------------------------------------

test('DETECT: exatamente os 11 códigos do catálogo (DE.CM 5, DE.AE 6), 11/66/55 e texto integral idêntico ao pacote detect-pt-BR-v1.0', () => {
  const de = guidesOf('DE');
  eq(de.map((g) => g.code).join(','), DE_CODES.join(','), 'DE.CM-01/02/03/06/09 e DE.AE-02/03/04/06/07/08');
  for (const gap of ['DE.CM-04', 'DE.CM-05', 'DE.CM-07', 'DE.CM-08', 'DE.AE-01', 'DE.AE-05']) eq(interviewGuideFor(gap), null, `${gap} não é inventada`);
  eq(de.filter((g) => g.mainQuestion.trim().length > 20).length, 11, 'perguntas principais');
  eq(de.reduce((n, g) => n + g.followUps.length, 0), 66, 'perguntas complementares');
  eq(de.reduce((n, g) => n + g.levelExamples.length, 0), 55, 'situações ilustrativas');
  const text = de.flatMap((g) => [g.code, g.categoryCode, g.title, g.mainQuestion, ...g.followUps.flatMap((f) => [f.question, f.focus]),
    ...g.levelExamples, g.interpretationNote, g.officialOutcome]).join('\n');
  eq(text.length, 23234, 'tamanho do texto integral');
  eq(fnv1a(text), '9c5c9ca0', 'impressão digital do pacote');
});

test('DETECT: distinções do pacote preservadas nas notas de interpretação', () => {
  const n = (c: string) => interviewGuideFor(c)!.interpretationNote;
  ok(n('DE.CM-01').includes('Gerar ou armazenar logs não comprova') && n('DE.CM-01').includes('PR.IR-01'), 'gerar registros × usar sinais');
  ok(n('DE.CM-02').includes('PR.AA-06'), 'proteção física × observação de eventos físicos');
  ok(n('DE.AE-02').includes('DE.AE-08'), 'análise × declaração');
  ok(n('DE.AE-03').startsWith('Centralizar logs no mesmo local não comprova'), 'centralização não é correlação');
  ok(n('DE.AE-04').includes('RS.AN-08'), 'estimativa × magnitude validada');
  ok(n('DE.AE-06').includes('RS.CO-02'), 'disponibilização × notificação');
  ok(n('DE.AE-07').includes('ID.RA-02'), 'receber × incorporar inteligência');
  ok(n('DE.AE-08').includes('RS.MA-01') && n('DE.AE-08').includes('RS.MA-02'), 'declarar × executar × triar');
  ok(n('DE.CM-09').includes('KNIGHT') && n('DE.CM-09').includes('não concede automaticamente maturidade NIST'), 'evidência do KNIGHT não atribui maturidade');
});

test('DETECT na tela real: área de trabalho e orientações próprias; atual e alvo independentes; DE → RS → RC → GV sem vazamento', () => {
  const e = openSub('de', 'DE.CM-09');
  eq(e.v.workspace(), true, 'área de trabalho');
  eq(e.v.guide()?.code, 'DE.CM-09', 'roteiro da subcategoria');
  ok(e.v.guideNotes()[7].startsWith('Ferramenta instalada'), 'orientações de DETECT');
  reply(e.api.last('subcategory'), detail('DE.CM-09', evaluation(1, { currentLevel: null, targetLevel: null, gap: null })));
  e.v.draft.targetLevel = 5;
  eq(e.v.draft.currentLevel, null, 'alvo não mexe no atual');
  e.v.draft.currentLevel = 3;
  eq(e.v.draft.targetLevel, 5, 'atual não mexe no alvo');
  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[2] as { currentLevel: number; targetLevel: number };
  eq(`${sent.currentLevel}/${sent.targetLevel}`, '3/5', 'mesma gravação');
  reply(e.api.last('save'), detail('DE.CM-09', evaluation(2, { currentLevel: 3, targetLevel: 5, gap: 2 })));
  const steps: [string, string, string | null, number][] = [['rs', 'RS.MA-01', 'RS.MA-01', 8], ['rc', 'RC.RP-01', 'RC.RP-01', 6], ['gv', 'GV.OC-01', 'GV.OC-01', 8]];
  for (const [fn, code, guide, notes] of steps) {
    e.route.go({ fn, code }, Q);
    reply(e.api.last('subcategory'), detail(code, evaluation(1)));
    eq(e.v.guide()?.code ?? null, guide, `${code}: roteiro`);
    eq(e.v.guideNotes().length, notes, `${code}: orientações da função`);
    ok(!e.v.guideNotes().some((x: string) => x.startsWith('Ferramenta instalada')), `${code}: sem orientação de DETECT`);
    eq(e.v.draft.currentLevel, 2, `${code}: rascunho é o da subcategoria aberta`);
  }
  eq(e.v.workspace(), true, 'GOVERN com área de trabalho');
});

// ---- (8) PROTECT ------------------------------------------------------------------------------

test('PROTECT: exatamente os 22 códigos do catálogo, 22/132/110 e texto integral idêntico ao pacote protect-pt-BR-v1.0', () => {
  const pr = guidesOf('PR');
  eq(pr.map((g) => g.code).sort().join(','), [...PR_CODES].sort().join(','), 'PR.AA-01..06, PR.AT-01/02, PR.DS-01/02/10/11, PR.PS-01..06, PR.IR-01..04');
  for (const [cat, codes] of Object.entries(PR_CATEGORIES)) eq(pr.filter((g) => g.categoryCode === cat).length, codes.length, `${cat}: ${codes.length}`);
  for (const gap of ['PR.DS-03', 'PR.DS-09', 'PR.AA-07', 'PR.AT-03', 'PR.PS-07', 'PR.IR-05']) eq(interviewGuideFor(gap), null, `${gap} não é inventada`);
  eq(pr.filter((g) => g.mainQuestion.trim().length > 20).length, 22, 'perguntas principais');
  eq(pr.reduce((n, g) => n + g.followUps.length, 0), 132, 'perguntas complementares');
  eq(pr.reduce((n, g) => n + g.levelExamples.length, 0), 110, 'situações ilustrativas');
  const text = pr.flatMap((g) => [g.code, g.categoryCode, g.title, g.mainQuestion, ...g.followUps.flatMap((f) => [f.question, f.focus]),
    ...g.levelExamples, g.interpretationNote, g.officialOutcome]).join('\n');
  eq(text.length, 44781, 'tamanho do texto integral');
  eq(fnv1a(text), '403ba4e5', 'impressão digital do pacote');
});

test('PROTECT: distinções das notas preservadas e nenhuma pergunta pede a entrega de segredos', () => {
  const n = (c: string) => interviewGuideFor(c)!.interpretationNote;
  ok(['PR.AA-02', 'PR.AA-03', 'PR.AA-05'].every((c) => n('PR.AA-01').includes(c)), 'AA-01 distingue vínculo, autenticação e autorização');
  ok(n('PR.AA-03').includes('PR.AA-05') && n('PR.AA-04').includes('PR.AA-01'), 'autenticar × autorizar; declarações × credenciais');
  ok(n('PR.AA-06').includes('DE.CM-02'), 'acesso físico × eventos físicos');
  ok(n('PR.AT-01').includes('PR.AT-02') && n('PR.AT-02').includes('não significa apenas equipe de segurança'), 'capacitação geral × especializada');
  ok(['PR.DS-02', 'PR.DS-10', 'PR.DS-11'].every((c) => n('PR.DS-01').includes(c)), 'repouso × trânsito × uso × backup');
  ok(n('PR.DS-11').includes('RC.RP-03') && n('PR.DS-11').includes('RC.RP-05'), 'backups × verificações de RECOVER');
  ok(n('PR.PS-04').includes('DE.AE-03'), 'gerar registros × correlacionar');
  ok(n('PR.PS-03').includes('PR.PS-02') && n('PR.IR-02').includes('PR.AA-06') && n('PR.IR-03').includes('RC.RP-01'), 'hardware × software; ambiental × físico; resiliência × recuperação');
  ok(n('PR.AA-01').includes('Não solicitar senhas, chaves privadas ou tokens completos como evidência'), 'evidência sem segredo');
  const asks = guidesOf('PR').flatMap((g) => [g.mainQuestion, ...g.followUps.map((f) => f.question)]);
  ok(!asks.some((q) => /(apresentar|enviar|entregar|fornecer|mostrar)[^?]*(senha|chave privada|token)/i.test(q) && !/sem expor/i.test(q)), 'nenhuma pergunta pede segredo utilizável');
});

test('PROTECT na tela real: lista da função PR; PR → DE → RS → RC → GV troca roteiro e orientações; atual e alvo independentes', () => {
  const n = openNav();
  n.c.fn = NIST_FUNCTIONS.find((f) => f.code === 'PR')!;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.code = 'PR.AA-04';
  eq(n.api.last('functionView').args[1] as string, 'PR', 'visão da função PR');
  eq(navTitle({ code: 'PR.IR-04', title: 'Catálogo' }), 'Capacidade de recursos para manter a disponibilidade', 'título curto do roteiro');

  const e = openSub('pr', 'PR.DS-10');
  eq(e.v.workspace(), true, 'área de trabalho');
  reply(e.api.last('subcategory'), detail('PR.DS-10', evaluation(1, { currentLevel: null, targetLevel: null, gap: null })));
  e.v.draft.currentLevel = 1;
  eq(e.v.draft.targetLevel, null, 'atual não mexe no alvo');
  e.v.draft.targetLevel = 4;
  eq(e.v.draft.currentLevel, 1, 'alvo não mexe no atual');
  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[2] as { currentLevel: number; targetLevel: number };
  eq(`${sent.currentLevel}/${sent.targetLevel}`, '1/4', 'mesma gravação');
  reply(e.api.last('save'), detail('PR.DS-10', evaluation(2, { currentLevel: 1, targetLevel: 4, gap: 3 })));
  const steps: [string, string, string | null, number, string | null][] = [
    ['pr', 'PR.DS-10', 'PR.DS-10', 8, 'Em PROTECT'], ['de', 'DE.CM-09', 'DE.CM-09', 8, 'Ausência de alertas'],
    ['rs', 'RS.MA-01', 'RS.MA-01', 8, 'O roteiro não exige'], ['rc', 'RC.RP-01', 'RC.RP-01', 6, null], ['gv', 'GV.OC-01', 'GV.OC-01', 8, 'Em GOVERN'],
  ];
  for (const [fn, code, guide, notes, own] of steps) {
    if (code !== 'PR.DS-10') {
      e.route.go({ fn, code }, Q);
      reply(e.api.last('subcategory'), detail(code, evaluation(1)));
    }
    const list: readonly string[] = e.v.guideNotes();
    eq(e.v.guide()?.code ?? null, guide, `${code}: roteiro`);
    eq(list.length, notes, `${code}: orientações`);
    eq(own === null ? list[6] ?? null : (list[6]?.startsWith(own) ? own : list[6]), own, `${code}: orientação própria da função`);
    ok(code === 'PR.DS-10' || !list.some((x) => x.startsWith('Em PROTECT')), `${code}: sem orientação de PROTECT`);
  }
  eq(e.v.workspace(), true, 'GOVERN com área de trabalho');
});

// ---- (9) IDENTIFY -----------------------------------------------------------------------------

test('IDENTIFY: exatamente os 21 códigos do catálogo, 21/126/105 e texto integral idêntico ao pacote identify-pt-BR-v1.0', () => {
  const id = guidesOf('ID');
  eq(id.map((g) => g.code).sort().join(','), [...ID_CODES].sort().join(','), 'ID.AM-01..05/07/08, ID.RA-01..10, ID.IM-01..04');
  for (const [cat, codes] of Object.entries(ID_CATEGORIES)) eq(id.filter((g) => g.categoryCode === cat).length, codes.length, `${cat}: ${codes.length}`);
  for (const gap of ['ID.AM-06', 'ID.AM-09', 'ID.RA-11', 'ID.IM-05']) eq(interviewGuideFor(gap), null, `${gap} não é inventada`);
  eq(id.filter((g) => g.mainQuestion.trim().length > 20).length, 21, 'perguntas principais');
  eq(id.reduce((n, g) => n + g.followUps.length, 0), 126, 'perguntas complementares');
  eq(id.reduce((n, g) => n + g.levelExamples.length, 0), 105, 'situações ilustrativas');
  const text = id.flatMap((g) => [g.code, g.categoryCode, g.title, g.mainQuestion, ...g.followUps.flatMap((f) => [f.question, f.focus]),
    ...g.levelExamples, g.interpretationNote, g.officialOutcome]).join('\n');
  eq(text.length, 42832, 'tamanho do texto integral');
  eq(fnv1a(text), '6e93fa22', 'impressão digital do pacote');
});

test('IDENTIFY: distinções das notas preservadas e nenhuma pergunta pede cópia de dados sensíveis', () => {
  const n = (c: string) => interviewGuideFor(c)!.interpretationNote;
  ok(n('ID.AM-01').includes('KNIGHT') && n('ID.AM-01').includes('não comprova sozinha cobertura'), 'fonte do KNIGHT não prova cobertura total');
  ok(n('ID.AM-02').includes('ID.AM-04') && n('ID.AM-02').includes('sem que isso exija inventários físicos separados'), 'gerenciados × fornecedores; registro integrado');
  ok(n('ID.AM-04').includes('ID.RA-10') && n('ID.AM-04').includes('GV.SC-07'), 'serviços de fornecedores × risco do fornecedor');
  ok(n('ID.AM-05').includes('ID.RA-05') && n('ID.RA-05').includes('ID.AM-05'), 'prioridade do ativo × risco do cenário');
  ok(n('ID.RA-05').includes('não introduz nova fórmula nem muda o score do AEGIS'), 'sem fórmula nova');
  ok(n('ID.AM-07').includes('não solicitar cópias de dados sensíveis'), 'metadados e conteúdo ocultado');
  ok(n('ID.AM-08').includes('gestão coordenada do ciclo de vida'), 'ciclo de vida × cadastro');
  ok(n('ID.RA-01').includes('não comprova correção concluída') && n('ID.RA-03').includes('ID.RA-01'), 'achado × correção; ameaça × vulnerabilidade');
  ok(n('ID.RA-02').includes('DE.AE-07') && n('ID.RA-04').includes('DE.AE-04') && n('ID.RA-06').includes('RS.MA-01'), 'receber × incorporar; cenário × evento; risco × incidente');
  ok(n('ID.RA-08').includes('ID.RA-01') && n('ID.RA-09').includes('ID.RA-10'), 'divulgação × varredura; produto × fornecedor');
  ok(n('ID.RA-10').includes('GV.SC-06') && n('ID.RA-10').includes('GV.SC-07'), 'interfaces com GV.SC preservadas');
  ok(n('ID.IM-01').includes('ID.IM-02') && n('ID.IM-01').includes('ID.IM-03') && n('ID.IM-01').includes('sem exigir que todas já estejam implementadas'), 'avaliações × testes × operação');
  ok(n('ID.IM-04').includes('RS.MA-01') && n('ID.IM-04').includes('RC.RP-01'), 'ciclo dos planos × execução');
  const asks = guidesOf('ID').flatMap((g) => [g.mainQuestion, ...g.followUps.map((f) => f.question)]);
  ok(!asks.some((q) => /(enviar|entregar|fornecer|anexar)[^?]*cópia/i.test(q)), 'nenhuma pergunta pede cópia de dados');
});

test('IDENTIFY na tela real: lista da função ID; ID → PR → DE → RS → RC → GV troca roteiro e orientações; atual e alvo independentes', () => {
  const n = openNav();
  n.c.fn = NIST_FUNCTIONS.find((f) => f.code === 'ID')!;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.code = 'ID.AM-07';
  eq(n.api.last('functionView').args[1] as string, 'ID', 'visão da função ID');
  eq(navTitle({ code: 'ID.AM-05', title: 'Catálogo' }), 'Priorização dos ativos conforme sua relevância', 'título curto do roteiro');

  const e = openSub('id', 'ID.RA-05');
  eq(e.v.workspace(), true, 'área de trabalho');
  reply(e.api.last('subcategory'), detail('ID.RA-05', evaluation(1, { currentLevel: null, targetLevel: null, gap: null })));
  e.v.draft.targetLevel = 3;
  eq(e.v.draft.currentLevel, null, 'alvo não mexe no atual');
  e.v.draft.currentLevel = 2;
  eq(e.v.draft.targetLevel, 3, 'atual não mexe no alvo');
  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[2] as { currentLevel: number; targetLevel: number };
  eq(`${sent.currentLevel}/${sent.targetLevel}`, '2/3', 'mesma gravação');
  reply(e.api.last('save'), detail('ID.RA-05', evaluation(2, { currentLevel: 2, targetLevel: 3, gap: 1 })));
  const steps: [string, string, string | null, number, string | null][] = [
    ['id', 'ID.RA-05', 'ID.RA-05', 8, 'Em IDENTIFY'], ['pr', 'PR.DS-10', 'PR.DS-10', 8, 'Em PROTECT'],
    ['de', 'DE.CM-09', 'DE.CM-09', 8, 'Ausência de alertas'], ['rs', 'RS.MA-01', 'RS.MA-01', 8, 'O roteiro não exige'],
    ['rc', 'RC.RP-01', 'RC.RP-01', 6, null], ['gv', 'GV.OC-01', 'GV.OC-01', 8, 'Em GOVERN'],
  ];
  for (const [fn, code, guide, notes, own] of steps) {
    if (code !== 'ID.RA-05') {
      e.route.go({ fn, code }, Q);
      reply(e.api.last('subcategory'), detail(code, evaluation(1)));
    }
    const list: readonly string[] = e.v.guideNotes();
    eq(e.v.guide()?.code ?? null, guide, `${code}: roteiro`);
    eq(list.length, notes, `${code}: orientações`);
    eq(own === null ? list[6] ?? null : (list[6]?.startsWith(own) ? own : list[6]), own, `${code}: orientação própria da função`);
    ok(code === 'ID.RA-05' || !list.some((x) => x.startsWith('Em IDENTIFY')), `${code}: sem orientação de IDENTIFY`);
  }
  eq(e.v.workspace(), true, 'GOVERN com área de trabalho');
});

// ---- (10) GOVERN e o conjunto das seis funções -----------------------------------------------

test('GOVERN: exatamente os 31 códigos do catálogo, 31/186/155 e texto integral idêntico ao pacote govern-pt-BR-v1.0', () => {
  const gv = guidesOf('GV');
  eq(gv.map((g) => g.code).sort().join(','), [...GV_CODES].sort().join(','), 'GV.OC-01..05, GV.RM-01..07, GV.RR-01..04, GV.PO-01/02, GV.OV-01..03, GV.SC-01..10');
  for (const [cat, codes] of Object.entries(GV_CATEGORIES)) eq(gv.filter((g) => g.categoryCode === cat).length, codes.length, `${cat}: ${codes.length}`);
  for (const gap of ['GV.OC-06', 'GV.RM-08', 'GV.RR-05', 'GV.PO-03', 'GV.OV-04', 'GV.SC-11']) eq(interviewGuideFor(gap), null, `${gap} não é inventada`);
  eq(gv.filter((g) => g.mainQuestion.trim().length > 20).length, 31, 'perguntas principais');
  eq(gv.reduce((n, g) => n + g.followUps.length, 0), 186, 'perguntas complementares');
  eq(gv.reduce((n, g) => n + g.levelExamples.length, 0), 155, 'situações ilustrativas');
  const text = gv.flatMap((g) => [g.code, g.categoryCode, g.title, g.mainQuestion, ...g.followUps.flatMap((f) => [f.question, f.focus]),
    ...g.levelExamples, g.interpretationNote, g.officialOutcome]).join('\n');
  eq(text.length, 60262, 'tamanho do texto integral');
  eq(fnv1a(text), '40498713', 'impressão digital do pacote');
});

test('as seis funções: 106 roteiros = as 106 subcategorias do catálogo oficial, com o resultado oficial de cada uma', () => {
  const official = new Map<string, string>();
  const catalog = JSON.parse(readFileSync(join(process.cwd(), '..', 'backend', 'src', 'AegisScore.Api', 'Data', 'nist_csf_2_0_catalog.json'), 'utf8').replace(/^﻿/, '')) as
    { functions: { categories: { subcategories: { code: string; description: string }[] }[] }[] };
  for (const f of catalog.functions) for (const c of f.categories) for (const s of c.subcategories) official.set(s.code, s.description);
  eq(official.size, 106, 'catálogo oficial com 106 subcategorias');
  eq(NIST_INTERVIEW_GUIDES.map((g) => g.code).sort().join(','), [...official.keys()].sort().join(','), 'nenhuma falta nem sobra');
  for (const g of NIST_INTERVIEW_GUIDES) eq(g.officialOutcome, official.get(g.code), `${g.code}: resultado oficial`);
  eq(NIST_FUNCTIONS.map((f) => `${f.code} ${guidesOf(f.code).length}`).join(' · '), 'GV 31 · ID 21 · PR 22 · DE 11 · RS 13 · RC 8', 'distribuição por função');
});

test('GOVERN: distinções das notas preservadas, sem obrigação jurídica, fórmula nova ou pedido de conteúdo protegido', () => {
  const n = (c: string) => interviewGuideFor(c)!.interpretationNote;
  ok(n('GV.OC-01').includes('informa as decisões de risco') && n('GV.OC-02').includes('GV.OC-04'), 'missão nas decisões × partes interessadas');
  ok(n('GV.OC-03').includes('não estabelece leis, prazos ou conclusões jurídicas universais') && n('GV.OC-03').includes('Não presumir conformidade'), 'requisitos gerenciados sem conclusão jurídica');
  ok(n('GV.OC-04').includes('GV.OC-05') && n('GV.OC-05').includes('GV.OC-04'), 'entregas a terceiros × dependências próprias');
  ok(n('GV.RM-01').includes('GV.OV-01') && n('GV.RM-02').includes('ID.RA-06') && n('GV.RM-04').includes('ID.RA-06'), 'objetivos × revisão; apetite × aceite individual; direção × resposta concreta');
  ok(n('GV.RM-05').includes('RS.CO-02') && n('GV.RM-07').startsWith('Risco positivo'), 'comunicação regular × incidente; riscos positivos');
  ok(n('GV.RM-06').includes('ID.RA-04') && n('GV.RM-06').includes('ID.RA-05') && n('GV.RM-06').includes('não cria fórmula de risco nem altera os cálculos do AEGIS'), 'método em GV × aplicação em ID.RA; sem fórmula');
  ok(n('GV.RR-01').includes('liderança') && n('GV.RR-02').includes('exercício das atribuições') && n('GV.RR-03').includes('PR.IR-04') && n('GV.RR-04').includes('PR.AA-01'), 'liderança × atribuições × recursos × pessoas');
  ok(n('GV.PO-01').includes('GV.PO-02') && n('GV.PO-02').includes('fazer cumprir a política atualizada'), 'estabelecer e aplicar × revisar e fazer cumprir');
  ok(n('GV.OV-01').includes('Ter um painel não comprova revisão nem decisão') && n('GV.OV-02').includes('GV.OV-03') && n('GV.OV-03').includes('GV.OV-01/02'), 'direção × cobertura × desempenho');
  ok(n('GV.SC-04').startsWith('Criticidade do fornecedor não é a mesma coisa que seu risco'), 'criticidade não é risco');
  ok(n('GV.SC-06').includes('ID.RA-09') && n('GV.SC-06').includes('ID.RA-10'), 'diligência prévia × ID.RA-09/10');
  ok(n('GV.SC-07').includes('apenas na contratação') && n('GV.SC-07').includes('DE.CM-06'), 'risco durante a relação × avaliação inicial × monitoramento');
  ok(n('GV.SC-09').includes('Não limitar este resultado à avaliação inicial') && n('GV.SC-10').includes('mesmo sem encerramento real ocorrido'), 'ciclo tecnológico; previsões pós-término');
  ok(n('GV.SC-01').includes('proporcional') && n('GV.SC-02').includes('GV.SC-08') && n('GV.SC-03').includes('GV.SC-01') && n('GV.SC-05').includes('GV.SC-07'), 'programa, coordenação, integração e acordos');
  const asks = guidesOf('GV').flatMap((g) => [g.mainQuestion, ...g.followUps.map((f) => f.question)]);
  ok(!asks.some((q) => /(enviar|entregar|fornecer|anexar)[^?]*(cópia|conteúdo)/i.test(q)), 'nenhuma pergunta pede cópia de conteúdo protegido');
});

test('GOVERN na tela real: lista da função GV; GV → ID → PR → DE → RS → RC → GV troca roteiro e orientações; atual e alvo independentes', () => {
  const n = openNav();
  n.c.fn = NIST_FUNCTIONS.find((f) => f.code === 'GV')!;
  n.c.ctx = { assessmentId: ASSESS, cycleId: CYCLE, scopeId: SCOPE };
  n.c.code = 'GV.OC-04';
  eq(n.api.last('functionView').args[1] as string, 'GV', 'visão da função GV');
  eq(navTitle({ code: 'GV.SC-10', title: 'Catálogo' }), 'Segurança após o encerramento das relações', 'título curto do roteiro');

  const e = openSub('gv', 'GV.RM-02');
  eq(e.v.workspace(), true, 'área de trabalho');
  reply(e.api.last('subcategory'), detail('GV.RM-02', evaluation(1, { currentLevel: null, targetLevel: null, gap: null })));
  e.v.draft.targetLevel = 4;
  eq(e.v.draft.currentLevel, null, 'alvo não mexe no atual');
  e.v.draft.currentLevel = 2;
  eq(e.v.draft.targetLevel, 4, 'atual não mexe no alvo');
  e.v.save(e.v.detail());
  const sent = e.api.last('save').args[2] as { currentLevel: number; targetLevel: number };
  eq(`${sent.currentLevel}/${sent.targetLevel}`, '2/4', 'mesma gravação');
  reply(e.api.last('save'), detail('GV.RM-02', evaluation(2, { currentLevel: 2, targetLevel: 4, gap: 2 })));
  const steps: [string, string, number, string | null][] = [
    ['gv', 'GV.RM-02', 8, 'Em GOVERN'], ['id', 'ID.RA-05', 8, 'Em IDENTIFY'], ['pr', 'PR.DS-10', 8, 'Em PROTECT'],
    ['de', 'DE.CM-09', 8, 'Ausência de alertas'], ['rs', 'RS.MA-01', 8, 'O roteiro não exige'], ['rc', 'RC.RP-01', 6, null], ['gv', 'GV.SC-10', 8, 'Em GOVERN'],
  ];
  for (const [i, [fn, code, notes, own]] of steps.entries()) {
    if (i > 0) {
      e.route.go({ fn, code }, Q);
      reply(e.api.last('subcategory'), detail(code, evaluation(1)));
    }
    const list: readonly string[] = e.v.guideNotes();
    eq(e.v.workspace(), true, `${code}: área de trabalho`);
    eq(e.v.guide()?.code ?? null, code, `${code}: roteiro`);
    eq(list.length, notes, `${code}: orientações`);
    eq(own === null ? list[6] ?? null : (list[6]?.startsWith(own) ? own : list[6]), own, `${code}: orientação própria da função`);
    ok(fn === 'gv' || !list.some((x) => x.startsWith('Em GOVERN') || x.startsWith('Considere porte, missão')), `${code}: sem orientação de GOVERN`);
  }
  eq(e.v.guideNotes().join('|'), interviewGuideNotes('GV').join('|'), 'de volta a GV com os textos de GOVERN');
});

test('GOVERN: Não se aplica × Sem nível; papel somente leitura e rodada encerrada só consultam', () => {
  const e = openSub('gv', 'GV.SC-10');
  reply(e.api.last('subcategory'), detail('GV.SC-10', null));
  eq(e.v.draft.currentLevel, null, 'sem nível por padrão');
  eq(e.v.draft.notApplicable, false, 'não é "não se aplica"');
  e.v.draft.notApplicable = true;
  ok(!!e.v.problem(), 'não se aplica exige justificativa');
  const ro = openSub('gv', 'GV.OV-01', 'Analyst');
  reply(ro.api.last('subcategory'), detail('GV.OV-01', evaluation(1)));
  eq(ro.v.canEdit(), false, 'somente leitura');
  eq(ro.v.guide()?.code, 'GV.OV-01', 'mas consulta o roteiro');
  const closed = openSub('gv', 'GV.PO-02');
  reply(closed.api.last('subcategory'), { ...detail('GV.PO-02', evaluation(1)), cycleStatus: 'Closed' });
  eq(closed.v.canEdit(), false, 'rodada encerrada bloqueia a edição');
  eq(closed.v.workspace(), true, 'área de trabalho para consulta');
});

// Testes assíncronos (o último) terminam depois do laço: o resumo sai no próximo ciclo.
setTimeout(() => {
  console.log(`\n${count - failures}/${count} ok`);
  if (failures > 0) process.exit(1);
}, 50);
