/**
 * [AEGIS-AUDITOR-CONTEXT-01] Gravações e conversa com os COMPONENTES REAIS e serviços dublês (respostas entregues na ordem que o teste
 * escolher). O que este spec trava:
 *   (1) documentos e ativos: releitura da visão de evidências durante a vinculação não impede concluí-la nem deixa a tela presa;
 *   (2) documentos e ativos: troca efetiva da seleção pela URL (componente mantido) limpa o formulário e a operação anterior; a resposta
 *       antiga não altera a nova seleção nem libera a operação nova; o erro antigo também é descartado;
 *   (3) chat: "Nova conversa", saída e troca de tenant/conta descartam histórico, texto não enviado e resposta atrasada; navegar entre
 *       páginas do mesmo ambiente mantém a conversa e o rascunho.
 */
import '@angular/compiler';
import {
  Injector,
  runInInjectionContext,
  signal,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵEffectScheduler as EffectScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { ActivatedRoute, NavigationEnd, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Observable, Subject } from 'rxjs';
import { DocumentHubComponent } from '../src/app/pages/document-hub.component';
import { AssetInventoryComponent } from '../src/app/pages/asset-inventory.component';
import { AuditorChatComponent } from '../src/app/components/auditor-chat.component';
import { NistApiError, NistSelectionService, NistService } from '../src/app/services/nist.service';
import { AuthService } from '../src/app/services/auth.service';
import { GovernanceService } from '../src/app/services/governance.service';
import { AssetService } from '../src/app/services/asset.service';
import { AuditorService } from '../src/app/services/auditor.service';
import { AgentStateService } from '../src/app/services/agent-state.service';

// ---- micro-harness ---------------------------------------------------------------------------
let failures = 0;
function test(name: string, fn: () => void): void {
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

interface Call { m: string; args: unknown[]; s: Subject<unknown>; }
class Calls {
  readonly calls: Call[] = [];
  call<T>(m: string, args: unknown[]): Observable<T> {
    const s = new Subject<unknown>();
    this.calls.push({ m, args, s });
    return s.asObservable() as Observable<T>;
  }
  of(m: string): Call[] { return this.calls.filter((c) => c.m === m); }
  last(m: string): Call {
    const c = this.of(m).at(-1);
    if (!c) throw new Error(`nenhuma chamada a ${m}`);
    return c;
  }
}
class FakeNist extends Calls {
  list() { return this.call('list', []); }
  evidenceOverview(...a: unknown[]) { return this.call('evidenceOverview', a); }
  linkEvidence(...a: unknown[]) { return this.call('linkEvidence', a); }
}
class FakeAuditor extends Calls {
  chat(...a: unknown[]) { return this.call('chat', a); }
  assessBlastRadius(...a: unknown[]) { return this.call('blast', a); }
}
function reply(c: Call, value: unknown): void { c.s.next(value); c.s.complete(); }
function fail(c: Call, err: unknown): void { c.s.error(err); }

class FakeRoute {
  readonly query = new BehaviorSubject(convertToParamMap({}));
  readonly queryParamMap = this.query.asObservable();
  readonly paramMap = new BehaviorSubject(convertToParamMap({})).asObservable();
  readonly fragment = new BehaviorSubject<string | null>(null).asObservable();
  get snapshot() { return { queryParamMap: this.query.value }; }
  go(q: Record<string, string>): void { this.query.next(convertToParamMap(q)); }
}

const A = 'a0000000-0000-0000-0000-000000000001';
const C = 'c0000000-0000-0000-0000-000000000001';
const S1 = 's0000000-0000-0000-0000-000000000001';
const S2 = 's0000000-0000-0000-0000-000000000002';
const Q1 = { avaliacao: A, rodada: C, escopo: S1 };
const Q2 = { avaliacao: A, rodada: C, escopo: S2 };
function overview(scopeId: string): unknown {
  return { assessmentId: A, cycleId: C, scopeId, assessmentName: 'Avaliação', cycleName: 'Rodada 1', scopeName: scopeId === S1 ? 'Matriz' : 'Filial',
    cycleStatus: 'Open', documentLinks: [], inventory: [], rows: [], limitations: [] };
}

interface Mounted<T> { c: T; v: any; api: FakeNist; route: FakeRoute; inj: Injector; tenant: ReturnType<typeof signal<string | null>>; }
function mount<T>(make: () => T): Mounted<T> {
  const api = new FakeNist();
  const route = new FakeRoute();
  const tenant = signal<string | null>('tenant-1');
  const inj = Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: NistService, useValue: api },
      { provide: NistSelectionService, useValue: { read: () => null, write() {} } },
      { provide: AuthService, useValue: { activeRole: () => 'Manager', activeTenantId: tenant } },
      { provide: GovernanceService, useValue: { listDocuments: () => new Subject().asObservable() } },
      { provide: AssetService, useValue: { list: () => new Subject().asObservable() } },
      { provide: ActivatedRoute, useValue: route },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });
  route.go(Q1);
  const c = runInInjectionContext(inj, make);
  reply(api.last('evidenceOverview'), overview(S1));
  return { c, v: c as any, api, route, inj, tenant };
}

// Os dois formulários de vínculo: mesma regra, componentes distintos.
const pages: { name: string; make: () => unknown; start: (v: any) => void; confirm: (v: any) => void; draft?: (v: any) => string }[] = [
  {
    name: 'documentos',
    make: () => new DocumentHubComponent(),
    start: (v) => { v.startLink('doc-1', 'GV.PO-01'); v.linkNoteDraft.set('Política aprovada.'); },
    confirm: (v) => v.confirmLink({ id: 'doc-1', title: 'Política' }, 'GV.PO-01'),
    draft: (v) => v.linkNoteDraft(),
  },
  {
    name: 'ativos',
    make: () => new AssetInventoryComponent(),
    start: (v) => v.startLink('ID.AM-01'),
    confirm: (v) => v.linkInventory('ID.AM-01'),
  },
];

const mounted: Injector[] = [];
console.log('auditor-context.component');

for (const p of pages) {
  test(`(1) ${p.name}: releitura da visão durante a vinculação não invalida a conclusão nem prende a tela`, () => {
    const m = mount(p.make);
    mounted.push(m.inj);
    p.start(m.v);
    p.confirm(m.v);
    eq(m.v.linking(), true, 'gravação em curso');
    const link = m.api.last('linkEvidence');
    m.v.nist.reload();                                   // releitura do MESMO contexto (muda a geração das leituras)
    reply(m.api.last('evidenceOverview'), overview(S1));
    reply(link, { id: 'ev-1' });
    eq(m.v.linking(), false, 'a conclusão libera a tela');
    eq(m.v.confirming(), null, 'formulário fechado');
    eq(typeof m.v.linkNote(), 'string', 'confirmação do vínculo exibida');
    eq(m.api.of('linkEvidence').length, 1, 'uma gravação só');
  });

  test(`(2) ${p.name}: trocar a seleção pela URL limpa o formulário; resposta antiga não altera a nova seleção nem libera a operação nova`, () => {
    const m = mount(p.make);
    mounted.push(m.inj);
    p.start(m.v);
    p.confirm(m.v);
    const old = m.api.last('linkEvidence');
    m.route.go(Q2);                                       // troca efetiva, componente mantido
    eq(m.v.linking(), false, 'operação anterior descartada');
    eq(m.v.confirming(), null, 'formulário anterior fechado');
    if (p.draft) eq(p.draft(m.v), '', 'texto do formulário anterior descartado');
    reply(m.api.last('evidenceOverview'), overview(S2));
    p.start(m.v);
    p.confirm(m.v);
    eq(m.v.linking(), true, 'gravação nova em curso');
    const fresh = m.api.last('linkEvidence');
    eq((fresh.args[0] as { scopeId: string }).scopeId, S2, 'a gravação nova é da nova seleção');
    reply(old, { id: 'ev-antigo' });
    eq(m.v.linking(), true, 'a resposta antiga não libera a operação nova');
    eq(m.v.linkNote(), null, 'a resposta antiga não aparece na nova seleção');
    eq(m.api.of('evidenceOverview').length, 2, 'a resposta antiga não relê a nova seleção');
    fail(fresh, new NistApiError('Rodada encerrada.', 400, 'Invalid'));
    eq(m.v.linking(), false, 'o erro da operação atual libera a tela');
    eq(m.v.linkError(), 'Rodada encerrada.', 'erro da operação atual exibido');
  });

  test(`(2b) ${p.name}: erro da operação antiga depois da troca de seleção é descartado`, () => {
    const m = mount(p.make);
    mounted.push(m.inj);
    p.start(m.v);
    p.confirm(m.v);
    const old = m.api.last('linkEvidence');
    m.route.go(Q2);
    reply(m.api.last('evidenceOverview'), overview(S2));
    fail(old, new NistApiError('Já vinculado.', 409, 'Conflict'));
    eq(m.v.linkError(), null, 'erro antigo não aparece');
    eq(m.api.of('evidenceOverview').length, 2, 'erro antigo não relê a nova seleção');
  });
}

// =============================== (3) chat ===============================================

function mountChat() {
  const auditor = new FakeAuditor();
  const tenant = signal<string | null>('tenant-1');
  const account = signal<string | null>('conta-1');
  const events = new Subject<unknown>();
  const inj = Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: AuditorService, useValue: auditor },
      { provide: AuthService, useValue: { activeTenantId: tenant, accountId: account } },
      { provide: Router, useValue: { url: '/nist/pr/PR.AA-01', events } },
    ],
  });
  mounted.push(inj);
  const c = runInInjectionContext(inj, () => new AuditorChatComponent());
  const flush = () => inj.get(EffectScheduler).flush();
  flush();
  return { c, v: c as any, auditor, tenant, account, events, flush, agent: inj.get(AgentStateService) };
}
const chatReply = { reply: 'resposta', conversationId: 'conv-1', mode: 'Simulated', focusLabel: 'Foco', sources: [], limitations: [], notes: [] };

test('(3) chat: "Nova conversa" descarta histórico, rascunho e resposta atrasada', () => {
  const m = mountChat();
  m.v.draft.set('primeira pergunta');
  m.v.send();
  const late = m.auditor.last('chat');
  m.v.draft.set('texto ainda não enviado');
  m.v.newConversation();
  m.flush();
  eq(m.v.draft(), '', 'rascunho descartado');
  eq(m.v.history().length, 0, 'histórico descartado');
  reply(late, chatReply);
  eq(m.v.history().length, 0, 'resposta atrasada não entra');
  eq(m.agent.conversationId(), null, 'a conversa antiga não é adotada');
});

test('(3b) chat: navegar no mesmo ambiente mantém conversa e rascunho; trocar de tenant ou sair descarta', () => {
  const m = mountChat();
  m.v.draft.set('pergunta');
  m.v.send();
  reply(m.auditor.last('chat'), chatReply);
  m.v.draft.set('rascunho em andamento');
  m.events.next(new NavigationEnd(1, '/dashboard', '/dashboard'));
  m.flush();
  eq(m.v.history().length, 2, 'conversa mantida ao navegar');
  eq(m.v.draft(), 'rascunho em andamento', 'rascunho mantido ao navegar');
  eq(m.agent.conversationId(), 'conv-1', 'mesma conversa');

  m.v.send();
  const late = m.auditor.last('chat');
  m.v.draft.set('outro rascunho');
  m.tenant.set('tenant-2');
  m.flush();
  eq(m.v.draft(), '', 'troca de tenant descarta o rascunho');
  eq(m.v.history().length, 0, 'troca de tenant descarta o histórico');
  reply(late, { ...chatReply, conversationId: 'conv-2' });
  eq(m.v.history().length, 0, 'resposta do tenant anterior não entra');
  eq(m.agent.conversationId(), null, 'conversa do tenant anterior não é adotada');

  m.v.draft.set('rascunho antes de sair');
  m.account.set(null);
  m.tenant.set(null);
  m.flush();
  eq(m.v.draft(), '', 'sair descarta o rascunho');
});

for (const inj of mounted) (inj as unknown as { destroy(): void }).destroy();
console.log(failures === 0 ? '\nok' : `\n${failures} falha(s)`);
process.exit(failures === 0 ? 0 : 1);
