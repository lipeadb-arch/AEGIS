/**
 * [AEGIS-JOURNEY-01 · revisão dirigida] Transições REAIS dos componentes da jornada — não só helpers.
 *
 * `DeviceCasePlanComponent` e `DevicePriorityComponent` são instanciados de verdade (injeção do Angular, sinais, inputs,
 * outputs e effects do próprio framework) com serviços dublês cujas respostas o teste controla uma a uma. Os handlers
 * exercitados são os mesmos que os botões chamam. Cobre o que um teste de helper isolado não alcança:
 *   (1) a MESMA instância do painel reutilizada entre planos/casos: rascunhos, avisos e "salvando" por contexto;
 *   (2) respostas tardias de escrita, conflito e releitura de outro contexto não tocam o painel atual;
 *   (3) 409 de reabertura abre o plano ativo anunciado — plano em foco, fixação e endereço coerentes com a mensagem;
 *   (4) 409 de versão relê sem repetir a escrita e sem apagar o que foi digitado;
 *   (5) troca de dispositivo com a MESMA CVE preserva o conjunto coerente dos inputs;
 *   (6) prioridade com falha (500) ou dispositivo ausente (404): estado próprio, criação bloqueada, plano acessível;
 *   (7) no template real, o painel do plano fica FORA dos estados de carga da prioridade (mesma view em carregando,
 *       falha, ausente e lido) e a criação não é oferecida sem a lista de planos ou sem a leitura do caso.
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import * as fs from 'fs';
import * as path from 'path';
import {
  Injector,
  SimpleChange,
  runInInjectionContext,
  signal,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
  ɵSIGNAL as SIGNAL,
} from '@angular/core';
import {
  ASTWithSource,
  TmplAstElement,
  TmplAstForLoopBlock,
  TmplAstIfBlock,
  TmplAstLetDeclaration,
  TmplAstNode,
  TmplAstSwitchBlock,
  parseTemplate,
} from '@angular/compiler';
import { Observable, Subject } from 'rxjs';
import { DeviceCasePlanComponent } from '../src/app/components/device-priority/device-case-plan.component';
import { DevicePriorityComponent } from '../src/app/components/device-priority/device-priority.component';
import { ActionPlanConflictError, RemediationService } from '../src/app/services/remediation.service';
import { AuthService } from '../src/app/services/auth.service';
import { AssetService } from '../src/app/services/asset.service';
import { ActionPlan, DeviceCaseOrigin } from '../src/app/models/remediation.models';

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
function contains(haystack: string | null | undefined, needle: string, msg: string): void {
  if (!(haystack ?? '').includes(needle)) throw new Error(`${msg}: "${needle}" não está em "${haystack}"`);
}

// ---- dublês: cada chamada devolve um Subject que o teste resolve quando quiser ------------------
interface Call {
  m: string;
  args: unknown[];
  s: Subject<unknown>;
}
class FakeRemediation {
  readonly calls: Call[] = [];
  private call<T>(m: string, ...args: unknown[]): Observable<T> {
    const s = new Subject<unknown>();
    this.calls.push({ m, args, s });
    return s.asObservable() as Observable<T>;
  }
  list(...a: unknown[]) { return this.call('list', ...a); }
  get(...a: unknown[]) { return this.call('get', ...a); }
  sourceReading(...a: unknown[]) { return this.call('sourceReading', ...a); }
  update(...a: unknown[]) { return this.call('update', ...a); }
  recordExecution(...a: unknown[]) { return this.call('recordExecution', ...a); }
  validate(...a: unknown[]) { return this.call('validate', ...a); }
  createForDeviceCase(...a: unknown[]) { return this.call('createForDeviceCase', ...a); }
  of(m: string): Call[] { return this.calls.filter((c) => c.m === m); }
  last(m: string): Call {
    const c = this.of(m).at(-1);
    if (!c) throw new Error(`nenhuma chamada a ${m}`);
    return c;
  }
}
class FakeAssets {
  readonly calls: { id: string; s: Subject<unknown> }[] = [];
  priority(id: string) {
    const s = new Subject<unknown>();
    this.calls.push({ id, s });
    return s.asObservable();
  }
  declareCriticality() { return new Subject<unknown>().asObservable(); }
}

function injectorWith(remediation: FakeRemediation, assets = new FakeAssets()): Injector {
  return Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: RemediationService, useValue: remediation },
      { provide: AssetService, useValue: assets },
      { provide: AuthService, useValue: { activeRole: signal<string | null>('Manager') } },
    ],
  });
}
/** Executa os effects pendentes — o que o Angular faz a cada ciclo. */
function flush(inj: Injector): void {
  inj.get(EffectScheduler).flush();
}
/** Atribui um input de sinal como o template do componente pai faria. */
function setInput(sig: unknown, value: unknown): void {
  const node = (sig as Record<symbol, { applyValueToInputSignal(n: unknown, v: unknown): void }>)[SIGNAL as unknown as symbol];
  node.applyValueToInputSignal(node, value);
}
function reply(c: { s: Subject<unknown> }, value: unknown): void {
  c.s.next(value);
  c.s.complete();
}
function fail(c: { s: Subject<unknown> }, err: unknown): void {
  c.s.error(err);
}

// ---- dados sintéticos -----------------------------------------------------------------------
const DEV_A = 'a1000000-0000-0000-0000-000000000001';
const DEV_B = 'b2000000-0000-0000-0000-000000000002';
const CVE_1 = 'CVE-2024-3001';
const CVE_2 = 'CVE-2024-3002';

function origin(assetId: string, cveId: string): DeviceCaseOrigin {
  return {
    schema: 'device-case-origin-v1', assetId, assetName: assetId === DEV_A ? 'pc-a.demo.example.com' : 'pc-b.demo.example.com',
    assetNameIsPlaceholder: false, cveId, cveTitle: null, evaluatedAt: '2026-09-12T12:00:00Z', policyCode: 'AEGIS-PRIO-DEV',
    policyVersion: 1, caseBand: 'p1', caseBandLabel: 'Prioridade 1', caseReason: '', wasDeterminingCase: true, deviceBand: 'p1',
    deviceBandLabel: 'Prioridade 1', devicePositionReason: '', severityLabel: '', cvssScore: 9.8, exploitLabel: '', epss: null,
    source: 'Microsoft Defender', firstSeenAt: '2026-09-12T10:00:00Z', acquiredAt: '2026-09-12T11:00:00Z', acquisitionLabel: '',
    factors: [], caveats: [], informationLabel: '', absenceLabel: null,
  };
}
function plan(id: string, assetId: string, cveId: string, over: Partial<ActionPlan> = {}): ActionPlan {
  return {
    id, knightIndicatorId: null, originRunId: null, originAffectedCount: null, originSourceType: null, originMode: null,
    title: `Plano ${id}`, proposedAction: null, responsiblePerson: null, responsibleArea: null, dueDate: null,
    status: 'Aberto', isOverdue: false, isActive: true, nextStep: '', executionNotes: null, executionEvidenceRef: null,
    executedAt: null, completedAt: null, createdAt: '2026-09-12T12:00:00Z', cycleStartedAt: '2026-09-12T12:00:00Z',
    version: 1, latestValidation: null, applicableValidation: null, allowedTransitions: ['EmAndamento'],
    closureBlockedReason: null, validations: [], events: [], originKind: 'DeviceVulnerability', deviceOrigin: origin(assetId, cveId),
    ...over,
  };
}

/** Campos de rascunho do painel (protegidos no componente; o teste lê e escreve como o ngModel faria). */
interface Drafts {
  editTitle: string;
  editProposal: string;
  execNotes: string;
  execEvidence: string;
  humanEvidence: string;
  humanNote: string;
  newTitle: string;
  toggleEdit(ev: { preventDefault(): void }): void;
}
const noEvent = { preventDefault() {} };

interface Mounted {
  c: DeviceCasePlanComponent;
  d: Drafts;
  inj: Injector;
  api: FakeRemediation;
  opened: (string | null)[];
  changed: ActionPlan[];
}
function mountPanel(inputs: { assetId: string; cveId: string; plans: ActionPlan[]; pinnedPlanId?: string | null }): Mounted {
  const api = new FakeRemediation();
  const inj = injectorWith(api);
  const c = runInInjectionContext(inj, () => new DeviceCasePlanComponent());
  setInput(c.assetId, inputs.assetId);
  setInput(c.assetName, origin(inputs.assetId, inputs.cveId).assetName);
  setInput(c.cveId, inputs.cveId);
  setInput(c.plans, inputs.plans);
  setInput(c.pinnedPlanId, inputs.pinnedPlanId ?? null);
  const opened: (string | null)[] = [];
  const changed: ActionPlan[] = [];
  c.planOpened.subscribe((id) => opened.push(id));
  c.changed.subscribe((p) => changed.push(p));
  flush(inj);
  return { c, d: c as unknown as Drafts, inj, api, opened, changed };
}

console.log('device-case-plan.component');

// =============================== (1) rascunhos por contexto ===============================================

test('trocar de plano/caso na MESMA instância não transporta rascunhos; nova versão do mesmo plano os preserva', () => {
  const a = plan('pa', DEV_A, CVE_1, { title: 'Plano A' });
  const b = plan('pb', DEV_A, CVE_2, { title: 'Plano B' });
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [a, b] });
  eq(m.c.plan()?.id, 'pa', 'plano A em foco');

  m.d.toggleEdit(noEvent);
  m.d.editTitle = 'Rascunho de A';
  m.d.execNotes = 'executado em A';
  m.d.humanEvidence = 'EV-A';
  m.c.execOpen.set(true);
  m.c.valOpen.set(true);

  // A lista é relida (outra aba alterou A): mesma identidade de plano — nada do que está sendo digitado some.
  setInput(m.c.plans, [{ ...a, version: 2, responsiblePerson: 'Equipe de endpoints' }, b]);
  flush(m.inj);
  eq(m.c.plan()?.version, 2, 'versão nova do mesmo plano');
  eq(m.d.editTitle, 'Rascunho de A', 'edição preservada');
  eq(m.d.execNotes, 'executado em A', 'execução preservada');
  eq(m.d.humanEvidence, 'EV-A', 'atestação preservada');

  // Mesma instância, outro caso (a pessoa escolheu outra CVE do mesmo dispositivo).
  setInput(m.c.cveId, CVE_2);
  flush(m.inj);
  eq(m.c.plan()?.id, 'pb', 'plano B em foco');
  eq(m.d.editTitle, '', 'edição de A não segue para B');
  eq(m.d.execNotes, '', 'execução de A não segue para B');
  eq(m.d.humanEvidence, '', 'atestação de A não segue para B');
  eq(m.c.editOpen(), false, 'formulário de edição fechado');
  eq(m.c.execOpen(), false, 'formulário de execução fechado');
  eq(m.c.valOpen(), false, 'formulário de atestação fechado');

  // O envio a partir de B leva só o que foi digitado para B.
  m.d.execNotes = 'executado em B';
  m.c.recordExecution();
  const sent = m.api.last('recordExecution');
  eq(sent.args[0], 'pb', 'escrita dirigida a B');
  eq((sent.args[1] as { notes: string }).notes, 'executado em B', 'sem texto de A');
});

test('trocar de dispositivo com a MESMA CVE também é outro contexto', () => {
  const a = plan('pa', DEV_A, CVE_1);
  const b = plan('pb2', DEV_B, CVE_1);
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [a, b] });
  m.d.execNotes = 'executado em pc-a';
  setInput(m.c.assetId, DEV_B);
  flush(m.inj);
  eq(m.c.plan()?.id, 'pb2', 'plano do outro dispositivo');
  eq(m.d.execNotes, '', 'rascunho do dispositivo anterior não segue');
});

// =============================== (2) respostas tardias ====================================================

test('resposta de escrita de A que chega com B aberto não pinta, não fixa e não limpa nada em B', () => {
  const a = plan('pa', DEV_A, CVE_1);
  const b = plan('pb', DEV_A, CVE_2);
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [a, b] });
  m.d.execNotes = 'executado em A';
  m.c.recordExecution();
  const late = m.api.last('recordExecution');
  ok(m.c.busy(), '"salvando" em A');

  setInput(m.c.cveId, CVE_2);
  flush(m.inj);
  ok(!m.c.busy(), 'o "salvando" pertence a A, não a B');
  m.d.execNotes = 'rascunho de B';

  reply(late, { ...a, version: 2, status: 'AguardandoValidacao' });
  flush(m.inj);
  eq(m.c.plan()?.id, 'pb', 'B continua em foco');
  eq(m.c.notice(), null, 'nenhuma confirmação de A em B');
  eq(m.opened.length, 0, 'o endereço não passa a apontar para A');
  eq(m.d.execNotes, 'rascunho de B', 'o onDone de A não limpa o rascunho de B');
  eq(m.changed.length, 1, 'a escrita aconteceu: quem contém o painel relê a lista');
});

test('409 de duplicidade de A que chega com B aberto não abre o plano anunciado em B', () => {
  const b = plan('pb', DEV_A, CVE_2);
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [b] });
  eq(m.c.plan(), null, 'sem plano: criação');
  m.c.create();
  const late = m.api.last('createForDeviceCase');
  setInput(m.c.cveId, CVE_2);
  flush(m.inj);
  const gets = m.api.of('get').length;
  fail(late, new ActionPlanConflictError('Já existe um plano ativo.', 'p-outro'));
  flush(m.inj);
  eq(m.api.of('get').length, gets, 'nenhuma releitura disparada pela resposta de outro contexto');
  eq(m.c.plan()?.id, 'pb', 'B continua em foco');
  eq(m.c.error(), null, 'nenhum erro de A em B');
  eq(m.opened.length, 0, 'nenhuma fixação');
});

// =============================== (3) 409 de reabertura ====================================================

test('reabrir o plano concluído com outro ativo: abre o plano ANUNCIADO, fixa, atualiza o endereço e diz isso', () => {
  const concluido = plan('pa', DEV_A, CVE_1, {
    title: 'Ciclo 1', status: 'Concluido', isActive: false, allowedTransitions: ['EmAndamento'], version: 4,
  });
  const ativo = plan('pb', DEV_A, CVE_1, { title: 'Ciclo 2', createdAt: '2026-09-13T08:00:00Z' });
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [concluido, ativo], pinnedPlanId: 'pa' });
  reply(m.api.last('get'), concluido);
  flush(m.inj);
  eq(m.c.plan()?.id, 'pa', 'o concluído, fixado pelo endereço');
  m.d.toggleEdit(noEvent);
  m.d.editTitle = 'rascunho no ciclo 1';

  m.c.changeStatus('EmAndamento');
  const upd = m.api.last('update');
  eq(upd.args[0], 'pa', 'tentativa de reabrir A');
  fail(upd, new ActionPlanConflictError('Já existe um plano ativo para este caso.', 'pb'));
  const rd = m.api.last('get');
  eq(rd.args[0], 'pb', 'lê o plano anunciado — sem repetir a escrita');
  eq(m.api.of('update').length, 1, 'nenhuma escrita repetida');
  reply(rd, ativo);
  flush(m.inj);

  eq(m.c.plan()?.id, 'pb', 'o plano apresentado é o ativo anunciado');
  const p = m.c.pinned();
  eq(p.kind === 'carregada' ? p.id : null, 'pb', 'a fixação acompanha o plano apresentado');
  eq(m.opened.at(-1), 'pb', 'o endereço passa a nomear o plano apresentado');
  contains(m.c.notice(), 'O plano "Ciclo 1" não foi alterado', 'a mensagem diz que A não foi reaberto');
  contains(m.c.notice(), 'plano ativo "Ciclo 2"', 'e nomeia o plano realmente apresentado');
  eq(m.d.editTitle, '', 'o rascunho do ciclo 1 não segue para o ciclo 2');

  // O pai devolve o endereço como input: mesma seleção, sem nova leitura nem troca de contexto.
  const gets = m.api.of('get').length;
  setInput(m.c.pinnedPlanId, 'pb');
  flush(m.inj);
  eq(m.c.plan()?.id, 'pb', 'continua o ativo');
  eq(m.api.of('get').length, gets, 'sem releitura: o plano já está em memória');
  contains(m.c.notice(), 'Ciclo 2', 'o aviso sobrevive ao eco do endereço');
});

test('409 de duplicidade na criação abre o plano existente e diz que nada foi criado', () => {
  const ativo = plan('pb', DEV_A, CVE_1, { title: 'Ciclo em curso' });
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [] });
  m.c.create();
  fail(m.api.last('createForDeviceCase'), new ActionPlanConflictError('Já existe um plano ativo.', 'pb'));
  reply(m.api.last('get'), ativo);
  flush(m.inj);
  eq(m.c.plan()?.id, 'pb', 'plano existente em foco');
  eq(m.opened.at(-1), 'pb', 'endereço aponta para ele');
  contains(m.c.notice(), 'Nenhum plano novo foi criado', 'o que aconteceu');
});

test('plano anunciado de OUTRO caso é recusado — nada é substituído', () => {
  const a = plan('pa', DEV_A, CVE_1, { status: 'Concluido', isActive: false });
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [a], pinnedPlanId: 'pa' });
  reply(m.api.last('get'), a);
  flush(m.inj);
  m.c.changeStatus('EmAndamento');
  fail(m.api.last('update'), new ActionPlanConflictError('conflito', 'px'));
  reply(m.api.last('get'), plan('px', DEV_B, CVE_1));
  flush(m.inj);
  eq(m.c.plan()?.id, 'pa', 'o plano em foco não é trocado');
  eq(m.opened.length, 0, 'endereço intacto');
  contains(m.c.error(), 'outro dispositivo', 'recusa dita');
});

// =============================== (4) 409 de versão ========================================================

test('409 de versão relê o MESMO plano, não repete a escrita e mantém o que foi digitado', () => {
  const a = plan('pa', DEV_A, CVE_1, { version: 1 });
  const m = mountPanel({ assetId: DEV_A, cveId: CVE_1, plans: [a], pinnedPlanId: 'pa' });
  reply(m.api.last('get'), a);
  flush(m.inj);
  m.d.toggleEdit(noEvent);
  m.d.editTitle = 'Novo título digitado';
  m.c.saveEdit();
  fail(m.api.last('update'), new ActionPlanConflictError('O plano foi alterado por outra pessoa.', null));
  const rd = m.api.last('get');
  eq(rd.args[0], 'pa', 'relê o mesmo plano');
  reply(rd, { ...a, version: 3, responsiblePerson: 'Outra pessoa' });
  flush(m.inj);
  eq(m.c.plan()?.version, 3, 'estado atual exibido');
  eq(m.d.editTitle, 'Novo título digitado', 'rascunho mantido para conferência');
  contains(m.c.notice(), 'o que você digitou foi mantido', 'aviso de conferência');
  eq(m.api.of('update').length, 1, 'nenhuma escrita repetida');
  eq(m.opened.length, 0, 'nenhuma troca de plano');
});

// =============================== (5)(6) DevicePriorityComponent ===========================================

interface DpView {
  selected(): string | null;
  pinned(): string | null;
  state(): string;
  creationBlock(): string | null;
}
function mountDevice(assetId: string, cve: string | null, planId: string | null) {
  const api = new FakeRemediation();
  const assets = new FakeAssets();
  const inj = injectorWith(api, assets);
  const c = runInInjectionContext(inj, () => new DevicePriorityComponent());
  c.assetId = assetId;
  c.selectedCve = cve;
  c.pinnedPlanId = planId;
  c.ngOnChanges({
    assetId: new SimpleChange(undefined, assetId, true),
    selectedCve: new SimpleChange(undefined, cve, true),
    pinnedPlanId: new SimpleChange(undefined, planId, true),
  });
  return { c, v: c as unknown as DpView, api, assets };
}

test('trocar só o dispositivo com a MESMA CVE e plano no endereço reaplica o conjunto dos inputs', () => {
  const { c, v, assets } = mountDevice(DEV_A, 'cve-2024-3001', 'pa');
  eq(v.selected(), CVE_1, 'CVE do endereço');
  eq(v.pinned(), 'pa', 'plano do endereço');
  c.assetId = DEV_B;
  c.pinnedPlanId = 'pb';
  c.ngOnChanges({
    assetId: new SimpleChange(DEV_A, DEV_B, false),
    pinnedPlanId: new SimpleChange('pa', 'pb', false),
  });
  eq(v.selected(), CVE_1, 'a CVE continua selecionada (o input dela não mudou)');
  eq(v.pinned(), 'pb', 'o plano do endereço para o novo dispositivo');
  c.assetId = DEV_A;
  c.pinnedPlanId = 'pa';
  c.ngOnChanges({ assetId: new SimpleChange(DEV_B, DEV_A, false), pinnedPlanId: new SimpleChange('pb', 'pa', false) });
  eq(v.selected(), CVE_1, 'volta com a mesma CVE');
  eq(assets.calls.at(-1)?.id, DEV_A, 'a prioridade relida é a do dispositivo atual');
  c.selectedCve = null;
  c.pinnedPlanId = null;
  c.ngOnChanges({ selectedCve: new SimpleChange(CVE_1, null, false), pinnedPlanId: new SimpleChange('pa', null, false) });
  eq(v.selected(), null, 'endereço sem caso fecha o painel');
  eq(v.pinned(), null, 'e solta o plano');
});

test('prioridade com falha (500) e dispositivo ausente (404): estados próprios, seleção mantida, criação bloqueada', () => {
  const { v, assets } = mountDevice(DEV_A, CVE_1, 'pa');
  eq(v.state(), 'loading', 'carregando');
  contains(v.creationBlock(), 'ainda está sendo lida', 'criação espera a leitura');
  fail(assets.calls.at(-1)!, { status: 500 });
  eq(v.state(), 'error', 'falha');
  eq(v.selected(), CVE_1, 'o caso continua aberto');
  eq(v.pinned(), 'pa', 'e o plano do endereço também');
  contains(v.creationBlock(), 'A leitura da prioridade falhou', 'criação bloqueada, com o motivo');

  const gone = mountDevice(DEV_B, CVE_1, 'pg');
  fail(gone.assets.calls.at(-1)!, { status: 404 });
  eq(gone.v.state(), 'notFound', 'ausente ≠ falha');
  contains(gone.v.creationBlock(), 'não foi encontrado', 'criação bloqueada para dispositivo ausente');
  eq(gone.v.pinned(), 'pg', 'o plano nomeado continua acessível');
});

// =============================== (7) templates reais ======================================================

const ROOT = process.cwd();
function templateOf(file: string, selector: string): string {
  const src = fs.readFileSync(path.join(ROOT, file), 'utf8');
  const at = src.indexOf(`selector: '${selector}'`);
  const m = /template:\s*`([\s\S]*?)`,\s*styles:/.exec(src.slice(at));
  if (at < 0 || !m) throw new Error(`template de ${selector} não encontrado`);
  return m[1];
}

type Scope = Record<string, unknown>;
interface Rendered {
  el: TmplAstElement;
  blocks: string[];
  scope: Scope;
}
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
/** Avalia @if/@switch/@for/@let; elementos saem com o caminho de ramos (a identidade que decide se a view é mantida). */
function render(nodes: TmplAstNode[], scope: Scope, blocks: string[] = [], out: Rendered[] = []): Rendered[] {
  for (const n of nodes) {
    const s = n.sourceSpan.start.offset;
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
    } else if (n instanceof TmplAstSwitchBlock) {
      const value = evaluate(src(n.expression), scope);
      const i = n.cases.findIndex((c) => c.expression !== null && evaluate(src(c.expression), scope) === value);
      const j = i >= 0 ? i : n.cases.findIndex((c) => c.expression === null);
      if (j >= 0) render(n.cases[j].children, scope, [...blocks, `@switch:${s}#${j}`], out);
    } else if (n instanceof TmplAstForLoopBlock) {
      const items = evaluate(src(n.expression), scope) as unknown[];
      items.forEach((item, i) => render(n.children, { ...scope, [n.item.name]: item }, [...blocks, `@for:${s}[${i}]`], out));
      if (!items.length && n.empty) render(n.empty.children, scope, [...blocks, `@empty:${s}`], out);
    }
  }
  return out;
}
const clickOf = (r: Rendered) => {
  const o = r.el.outputs.find((x) => x.name === 'click');
  return o ? src(o.handler) : null;
};

const DP_TEMPLATE = templateOf('src/app/components/device-priority/device-priority.component.ts', 'app-device-priority');
const DP = parseTemplate(DP_TEMPLATE, 'device-priority.component.ts', { preserveWhitespaces: false });

function dpScope(state: string): Scope {
  const data = state === 'loaded'
    ? {
        assetId: DEV_A, assetName: 'pc-a.demo.example.com', determiningCase: null, caveats: [], absenceLabel: null, factors: [],
        couldChange: [], situations: [], cases: null, noLongerReported: 0, excludedOutOfPolicy: 0, limitations: [],
        evaluatedAt: '', band: 'p1', bandLabel: '', informationLabel: '', positionReason: '', nextAction: '',
        criticality: { label: '', note: null }, policy: {}, scope: '',
      }
    : null;
  return {
    state: () => state, data: () => data, saveNotice: () => null, selected: () => CVE_1, canDeclare: () => false,
    declError: () => null, plansError: () => null, disposition: () => null, assetId: DEV_A,
  };
}

test('template real: o painel do plano é a MESMA view em carregando, falha, ausente e lido (releitura não o destrói)', () => {
  eq((DP.errors ?? []).length, 0, 'template analisado sem erros');
  const paths = ['loading', 'error', 'notFound', 'loaded'].map((st) => {
    const found = render(DP.nodes, dpScope(st)).filter((r) => r.el.name === 'app-device-case-plan');
    eq(found.length, 1, `painel presente em "${st}"`);
    ok(!found[0].blocks.some((b) => b.startsWith('@switch')), `painel fora dos estados de carga em "${st}"`);
    return found[0].blocks.join(' > ');
  });
  ok(paths.every((p) => p === paths[0]), `mesmo caminho de ramos em todos os estados: ${paths.join(' | ')}`);
  const panel = render(DP.nodes, dpScope('error')).find((r) => r.el.name === 'app-device-case-plan')!;
  const input = (name: string) => src(panel.el.inputs.find((i) => i.name === name)?.value);
  eq(input('assetId'), 'assetId', 'o dispositivo vem do input, não da leitura da prioridade');
  eq(input('assetName'), 'data()?.assetName ?? null', 'nome da leitura só quando ela existe');
  eq(input('creationBlockedReason'), 'creationBlock()', 'motivo de bloqueio da criação vem do estado da leitura');
  ok(panel.el.outputs.some((o) => o.name === 'retryPlans'), 'pedido de releitura dos planos ligado');
  ok(render(DP.nodes, dpScope('notFound')).some((r) => r.el.name === 'button' && clickOf(r) === 'retry()'), 'ausente tem "ler de novo"');
});

const CP = parseTemplate(
  templateOf('src/app/components/device-priority/device-case-plan.component.ts', 'app-device-case-plan'),
  'device-case-plan.component.ts', { preserveWhitespaces: false },
);
function cpScope(over: Scope): Scope {
  return {
    cveId: () => CVE_1, displayName: () => 'pc-a', busy: () => false, notice: () => null, error: () => null, plansError: () => null,
    pinned: () => ({ kind: 'livre' }), pinnedReason: () => '', plan: () => null, canManage: () => true,
    creationBlockedReason: () => null, previous: () => [],
    ...over,
  };
}
const offersCreation = (sc: Scope) => render(CP.nodes, sc).some((r) => clickOf(r) === 'create()');

test('template real do painel: criação só quando a ausência de plano foi lida e o caso pode ser lido agora', () => {
  eq((CP.errors ?? []).length, 0, 'template analisado sem erros');
  ok(offersCreation(cpScope({})), 'com lista de planos e prioridade lidas, cria');
  const semLista = cpScope({ plansError: () => 'API inacessível.' });
  ok(!offersCreation(semLista), 'lista de planos com falha: não oferece criação');
  ok(render(CP.nodes, semLista).some((r) => clickOf(r) === 'retryPlans.emit()'), 'oferece reler a lista');
  ok(!offersCreation(cpScope({ creationBlockedReason: () => 'A leitura da prioridade falhou.' })), 'prioridade com falha: não cria');
  ok(!offersCreation(cpScope({ pinned: () => ({ kind: 'indisponivel', id: 'px', reason: 'x' }) })), 'plano do endereço inacessível: não cria');
  ok(!offersCreation(cpScope({ pinned: () => ({ kind: 'carregando', id: 'px' }) })), 'enquanto o plano do endereço abre: não cria');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) {
  console.log(`${failures} falha(s)`);
  process.exit(1);
}
