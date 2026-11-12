/**
 * [AEGIS-KNIGHT-SCOPE-01] Escopo salvo e navegação da seção KNIGHT, no componente real (serviços dublês com respostas
 * entregues na ordem que o teste escolher).
 *
 * O que este spec trava:
 *   (1) a seção abre em "Geral" com o consolidado das fontes EM ESCOPO; as abas das fontes vêm depois, sem a fonte fora do escopo;
 *   (2) trocar de aba lê a avaliação daquela fonte (nota, cobertura, achados) e respostas atrasadas de abas anteriores são
 *       descartadas — inclusive a do consolidado;
 *   (3) `?fonte=` abre a aba pedida; um achado do endereço é aberto na aba; estados de carregamento, erro (com nova tentativa) e
 *       ausência de avaliação;
 *   (4) teclado: setas, Home e End percorrem as abas;
 *   (5) "Compor com" salva o escopo do tenant (só quem pode), relê "Geral" e não muda nada numa recusa;
 *   (6) a publicação de "Geral" pina só as execuções incluídas — a fonte fora do escopo nunca;
 *   (7) rótulos: "Fora do escopo" e o mapeamento de slugs do escopo.
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import {
  Injector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subject } from 'rxjs';
import { AegisKnightComponent } from '../src/app/pages/aegis-knight.component';
import { KnightService } from '../src/app/services/knight.service';
import { RemediationService } from '../src/app/services/remediation.service';
import { PostureHistoryService } from '../src/app/services/posture-history.service';
import { IdentityRiskService } from '../src/app/services/identity-risk.service';
import { AuthService } from '../src/app/services/auth.service';
import {
  KnightAssessment,
  KnightAssessmentScope,
  KnightConsolidatedSource,
  KnightSourceType,
  availabilityLabel,
  outOfScopeSources,
} from '../src/app/models/knight.models';

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

// ---- dublês ----------------------------------------------------------------------------------
interface Call {
  m: string;
  args: unknown[];
  s: Subject<unknown>;
}
class Recorder {
  readonly calls: Call[] = [];
  protected call<T>(m: string, args: unknown[]): Observable<T> {
    const s = new Subject<unknown>();
    this.calls.push({ m, args, s });
    return s.asObservable() as Observable<T>;
  }
  of(m: string): Call[] {
    return this.calls.filter((c) => c.m === m);
  }
  last(m: string): Call {
    const c = this.of(m).at(-1);
    if (!c) throw new Error(`nenhuma chamada a ${m}`);
    return c;
  }
}
class FakeKnight extends Recorder {
  getSources() { return this.call('getSources', []); }
  getLatestBySource() { return this.call('getLatestBySource', []); }
  getLatestState() { return this.call('getLatestState', []); }
  getById(...a: unknown[]) { return this.call('getById', a); }
  getConsolidated(...a: unknown[]) { return this.call('getConsolidated', a); }
  saveScope(...a: unknown[]) { return this.call('saveScope', a); }
  getAffectedSummary(...a: unknown[]) { return this.call('getAffectedSummary', a); }
  getCompositionAffectedSummary(...a: unknown[]) { return this.call('getCompositionAffectedSummary', a); }
  getReferenceCoverage() { return this.call('getReferenceCoverage', []); }
  getGlossary() { return this.call('getGlossary', []); }
}
class FakeHistory extends Recorder {
  historyPreview(...a: unknown[]) { return this.call('historyPreview', a); }
  consolidatedHistoryPreview(...a: unknown[]) { return this.call('consolidatedHistoryPreview', a); }
  publish(...a: unknown[]) { return this.call('publish', a); }
  publishConsolidated(...a: unknown[]) { return this.call('publishConsolidated', a); }
}
class FakeRouter {
  readonly navigations: Record<string, unknown>[] = [];
  constructor(private readonly query: Map<string, string>) {}
  navigate(_: unknown[], extras: { queryParams?: Record<string, unknown> }): Promise<boolean> {
    const qp = extras.queryParams ?? {};
    this.navigations.push(qp);
    for (const [k, v] of Object.entries(qp)) {
      if (v === null || v === undefined) this.query.delete(k);
      else this.query.set(k, String(v));
    }
    return Promise.resolve(true);
  }
}
function reply(c: Call, value: unknown): void {
  c.s.next(value);
  c.s.complete();
}
function fail(c: Call, err: unknown): void {
  c.s.error(err);
}
const silent = () => new Subject<unknown>().asObservable();

// ---- dados sintéticos: 201 avaliados de 255 aplicáveis, 12 do Fabric sem avaliação ----------------
const RUN: Record<string, string> = {
  MicrosoftEntraId: 'aaaa0000-0000-0000-0000-000000000001',
  MicrosoftTeams: 'aaaa0000-0000-0000-0000-000000000002',
  MicrosoftExchangeOnline: 'aaaa0000-0000-0000-0000-000000000003',
  MicrosoftAzure: 'aaaa0000-0000-0000-0000-000000000004',
  MicrosoftFabric: 'aaaa0000-0000-0000-0000-000000000005',
};
const SLUG: Record<string, string> = {
  MicrosoftEntraId: 'entra', MicrosoftTeams: 'teams', MicrosoftExchangeOnline: 'exchange', MicrosoftAzure: 'azure', MicrosoftFabric: 'fabric',
};

function assessment(id: string, sourceType: KnightSourceType, over: Partial<KnightAssessment> = {}): KnightAssessment {
  return {
    id, mode: 'Live', isDemo: false, sourceType, sourceState: 'Completed', source: sourceType, status: 'Completed',
    catalogVersion: 'ak-knight-v13', scoreFormulaVersion: 'knight-score-v1',
    startedAt: '2026-11-10T12:00:00Z', completedAt: '2026-11-10T12:00:20Z', score: 70, coverage: 85.4,
    counts: { passed: 30, exposed: 5, mitigated: 0, notEvaluated: 6, error: 0, notApplicable: 0 },
    indicators: [
      { indicatorId: `AK-${(SLUG[sourceType] ?? 'CONS').toUpperCase()}-001`, title: 'Controle', category: 'PrivilegedAccess', severity: 'High',
        status: 'Exposed', evidence: 'e', affectedObjectCount: 1, nistCodes: [], mitreTechniques: [], recommendation: 'r',
        collectedAt: '2026-11-10T12:00:10Z', sourceType },
    ] as unknown as KnightAssessment['indicators'],
    capabilities: [], advisory: null, advisoryFromAi: false, ...over,
  } as KnightAssessment;
}
function block(source: KnightSourceType, withAssessment = true) {
  return {
    source, slug: SLUG[source], label: source, unfinishedAttempt: null,
    assessment: withAssessment ? assessment(RUN[source], source) : null,
  };
}
const FIVE = ['MicrosoftEntraId', 'MicrosoftTeams', 'MicrosoftExchangeOnline', 'MicrosoftAzure', 'MicrosoftFabric'] as KnightSourceType[];
function scope(outOfScope: string[], updatedAt: string | null = '2026-11-11T09:00:00Z'): KnightAssessmentScope {
  return { inScope: [], outOfScope, updatedAt, updatedByName: updatedAt ? 'Pessoa Gestora' : null };
}
/** Consolidado como o servidor devolve com o Fabric fora do escopo: 201 de 243, 42 sem avaliação. */
function consolidated(included: KnightSourceType[], fabricOut = true): KnightAssessment {
  const sources: KnightConsolidatedSource[] = FIVE.map((s) => {
    const out = fabricOut && s === 'MicrosoftFabric';
    const inc = !out && included.includes(s);
    return {
      source: s, slug: SLUG[s], label: s, included: inc, availabilityState: out ? 'OutOfScope' : inc ? 'Included' : 'Available',
      sourceRunId: inc ? RUN[s] : null, sourceState: out ? null : 'Completed', catalogVersion: out ? null : 'ak-knight-v13',
      capturedAt: out ? null : '2026-11-10T12:00:00Z', score: out ? null : 70, coverage: out ? null : 85.4, counts: null, collectionLimitations: [],
    };
  });
  return assessment('00000000-0000-0000-0000-000000000000', 'Consolidated', {
    score: 74, coverage: 82.7, counts: { passed: 170, exposed: 25, mitigated: 6, notEvaluated: 42, error: 0, notApplicable: 0 }, sources,
  });
}

// ---- montagem --------------------------------------------------------------------------------
interface Mounted {
  c: AegisKnightComponent;
  api: FakeKnight;
  hist: FakeHistory;
  router: FakeRouter;
  query: Map<string, string>;
}
function mount(opts: { query?: Record<string, string>; role?: string; out?: string[]; blocks?: ReturnType<typeof block>[] } = {}): Mounted {
  const api = new FakeKnight();
  const hist = new FakeHistory();
  const query = new Map(Object.entries(opts.query ?? {}));
  const router = new FakeRouter(query);
  const inj = Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: KnightService, useValue: api },
      { provide: PostureHistoryService, useValue: hist },
      { provide: RemediationService, useValue: { list: silent, get: silent } },
      { provide: IdentityRiskService, useValue: { get: silent } },
      { provide: AuthService, useValue: { activeRole: () => opts.role ?? 'TenantAdmin' } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: query } } },
      { provide: Router, useValue: router },
    ],
  });
  const c = runInInjectionContext(inj, () => new AegisKnightComponent());
  c.ngOnInit();
  reply(api.last('getLatestBySource'), {
    sources: opts.blocks ?? FIVE.map((s) => block(s)),
    scope: scope(opts.out ?? ['fabric']),
  });
  return { c, api, hist, router, query };
}
function key(c: AegisKnightComponent, k: string): void {
  c.viewKey({ key: k, preventDefault() {} } as unknown as KeyboardEvent);
}

console.log('AEGIS-KNIGHT-SCOPE-01 · escopo salvo e abas da seção KNIGHT');

// ---- (1) abertura em Geral -------------------------------------------------------------------
test('abre em "Geral" com o consolidado das fontes em escopo; Fabric fora do escopo não tem aba', () => {
  const { c, api } = mount();
  eq(c.view(), 'geral', 'a primeira aba é Geral');
  eq(c.consolidatedMode(), true, 'Geral é o consolidado');
  eq(c.viewLoading(), true, 'estado de carregamento enquanto o consolidado é lido');
  const pedido = api.last('getConsolidated').args[0] as KnightSourceType[];
  eq(pedido.join(','), 'MicrosoftEntraId,MicrosoftTeams,MicrosoftExchangeOnline,MicrosoftAzure', 'só as fontes em escopo compõem');
  eq(c.viewTabs().map((s) => s.source).join(','), 'MicrosoftEntraId,MicrosoftTeams,MicrosoftExchangeOnline,MicrosoftAzure',
    'as abas das fontes em escopo, sem o Fabric');

  reply(api.last('getConsolidated'), consolidated(pedido));
  eq(c.viewLoading(), false, 'carregado');
  const a = c.assessment()!;
  eq(a.coverage, 82.7, 'cobertura do consolidado sem o Fabric');
  eq(a.counts.notEvaluated, 42, '42 sem avaliação');
  eq(a.sources!.find((s) => s.source === 'MicrosoftFabric')!.availabilityState, 'OutOfScope', 'Fabric aparece como fora do escopo');
  eq(c.consolidatedCandidates().find((x) => x.source === 'MicrosoftFabric')!.inScope, false, 'o seletor mostra o Fabric desmarcado');
});

// ---- (2) troca de abas e respostas atrasadas --------------------------------------------------
test('Geral → Teams → Azure com respostas fora de ordem: só a da aba atual grava', () => {
  const { c, api, query } = mount();
  const geral = api.last('getConsolidated');
  c.selectView('MicrosoftTeams');
  const teams = api.last('getById');
  eq(teams.args[0], RUN.MicrosoftTeams, 'a aba lê a avaliação do Teams por Id');
  eq(query.get('fonte'), 'teams', 'a aba vive no endereço');
  c.selectView('MicrosoftAzure');
  const azure = api.last('getById');
  eq(azure.args[0], RUN.MicrosoftAzure, 'a aba lê a avaliação do Azure por Id');

  reply(azure, assessment(RUN.MicrosoftAzure, 'MicrosoftAzure', { score: 55, coverage: 55.6 }));
  eq(c.assessment()?.id, RUN.MicrosoftAzure, 'Azure exibido');
  reply(teams, assessment(RUN.MicrosoftTeams, 'MicrosoftTeams'));
  reply(geral, consolidated(['MicrosoftEntraId']));
  eq(c.assessment()?.id, RUN.MicrosoftAzure, 'respostas atrasadas do Teams e do consolidado são descartadas');
  eq(c.assessment()?.score, 55, 'nota da fonte aberta');
  eq(c.viewLoading(), false, 'nenhum carregamento pendurado');
  eq(c.consolidatedMode(), false, 'fora de Geral');
});

test('voltar a Geral relê o consolidado; trocar de aba descarta o achado aberto', () => {
  const { c, api, query } = mount();
  reply(api.last('getConsolidated'), consolidated(['MicrosoftEntraId', 'MicrosoftTeams']));
  c.selectView('MicrosoftTeams');
  reply(api.last('getById'), assessment(RUN.MicrosoftTeams, 'MicrosoftTeams'));
  c.selectFinding('AK-TEAMS-001');
  eq(c.selected(), 'AK-TEAMS-001', 'achado aberto no Teams');
  const antes = api.of('getConsolidated').length;
  c.selectView('geral');
  eq(api.of('getConsolidated').length, antes + 1, 'Geral é relido');
  eq(c.selected(), null, 'o achado de outra aba não fica aberto');
  eq(query.get('fonte'), undefined, 'Geral é a aba padrão do endereço');
  c.selectView('geral');
  eq(api.of('getConsolidated').length, antes + 1, 'clicar na aba já aberta não relê');
});

// ---- (3) endereço, erro e ausência -----------------------------------------------------------
test('?fonte=azure abre a aba do Azure; ?finding= abre o achado nela', () => {
  const { c, api } = mount({ query: { fonte: 'azure', finding: 'AK-AZURE-001' } });
  eq(c.view(), 'MicrosoftAzure', 'aba do endereço');
  eq(api.of('getConsolidated').length, 0, 'Geral não é lido sem necessidade');
  reply(api.last('getById'), assessment(RUN.MicrosoftAzure, 'MicrosoftAzure'));
  eq(c.selected(), 'AK-AZURE-001', 'o achado do link é aberto');
  eq(c.tab(), 'controls', 'na aba de controles');
});

test('?fonte= de uma fonte fora do escopo cai em Geral', () => {
  const { c } = mount({ query: { fonte: 'fabric' } });
  eq(c.view(), 'geral', 'sem aba para o Fabric');
});

test('erro ao ler a aba: estado próprio com nova tentativa; as outras abas não são afetadas', () => {
  const { c, api } = mount();
  c.selectView('MicrosoftTeams');
  fail(api.last('getById'), new Error('Não foi possível carregar a avaliação.'));
  contains(c.viewError(), 'Teams', 'o erro diz a aba');
  eq(c.assessment(), null, 'nenhum resultado de outra aba ocupa a tela');
  c.retryView();
  eq(c.viewError(), null, 'nova tentativa limpa o erro');
  eq(c.viewLoading(), true, 'e mostra o carregamento');
  reply(api.last('getById'), assessment(RUN.MicrosoftTeams, 'MicrosoftTeams'));
  eq(c.assessment()?.id, RUN.MicrosoftTeams, 'a avaliação chega');
});

test('fonte em escopo sem avaliação concluída: aba com estado de ausência, sem leitura', () => {
  const blocks = [block('MicrosoftEntraId'), block('MicrosoftTeams', false)];
  const { c, api } = mount({ blocks, out: [] });
  const leituras = api.of('getById').length;
  c.selectView('MicrosoftTeams');
  eq(api.of('getById').length, leituras, 'nada a ler');
  eq(c.assessment(), null, 'sem avaliação');
  contains(c.viewEmptyText(), 'sem avaliação concluída', 'a ausência é dita na aba');
});

test('nenhuma fonte do escopo avaliada: Geral diz isso em vez de um consolidado vazio', () => {
  const { c, api } = mount({ blocks: [block('MicrosoftTeams', false)], out: [] });
  eq(api.of('getConsolidated').length, 0, 'nada a consolidar');
  c.selectView('geral');
  eq(c.assessment(), null, 'sem resultado inventado');
  contains(c.viewEmptyText(), 'Nenhuma fonte do escopo', 'estado vazio de Geral');
});

// ---- (4) teclado ------------------------------------------------------------------------------
test('teclado: setas, Home e End percorrem Geral e as abas em escopo', () => {
  const { c } = mount();
  key(c, 'ArrowRight');
  eq(c.view(), 'MicrosoftEntraId', 'seta para a direita');
  key(c, 'End');
  eq(c.view(), 'MicrosoftAzure', 'End vai à última aba em escopo (não ao Fabric)');
  key(c, 'ArrowRight');
  eq(c.view(), 'geral', 'em ciclo');
  key(c, 'ArrowLeft');
  eq(c.view(), 'MicrosoftAzure', 'seta para a esquerda');
  key(c, 'Home');
  eq(c.view(), 'geral', 'Home');
  key(c, 'Tab');
  eq(c.view(), 'geral', 'outras teclas não trocam a aba');
  eq(c.viewTabId('MicrosoftTeams'), 'knight-view-teams', 'id estável da aba');
});

// ---- (5) escopo salvo -------------------------------------------------------------------------
test('desmarcar uma fonte salva o escopo do tenant e relê Geral sem ela', () => {
  const { c, api } = mount({ out: [] });
  reply(api.last('getConsolidated'), consolidated(FIVE, false));
  c.toggleConsolidatedSource('MicrosoftFabric');
  eq(c.scopeSaving(), true, 'gravação em andamento');
  const enviado = api.last('saveScope').args[0] as KnightSourceType[];
  ok(!enviado.includes('MicrosoftFabric') && enviado.includes('MicrosoftAzure'), 'envia as fontes DENTRO do escopo, sem o Fabric');
  c.toggleConsolidatedSource('MicrosoftAzure');
  eq(api.of('saveScope').length, 1, 'outra mudança espera a gravação terminar');

  reply(api.last('saveScope'), scope(['fabric']));
  eq(c.scopeSaving(), false, 'gravado');
  eq(c.outOfScope().join(','), 'MicrosoftFabric', 'escopo atualizado');
  const pedido = api.last('getConsolidated').args[0] as KnightSourceType[];
  ok(!pedido.includes('MicrosoftFabric'), 'Geral relido sem o Fabric');
  contains(c.scopeNote(), 'Escopo salvo para este tenant', 'a origem do escopo é dita');
  contains(c.scopeNote(), 'por Pessoa Gestora', 'e quem salvou');
});

test('recusa ao salvar: o escopo salvo não muda e o erro é dito', () => {
  const { c, api } = mount();
  c.toggleConsolidatedSource('MicrosoftFabric');
  fail(api.last('saveScope'), new Error('Seu papel não permite alterar o escopo da avaliação.'));
  eq(c.outOfScope().join(','), 'MicrosoftFabric', 'continua como estava');
  contains(c.scopeError(), 'não permite', 'erro exibido');
});

test('papel sem permissão: o seletor não grava nada e diz o motivo', () => {
  const { c, api } = mount({ role: 'Analyst' });
  eq(c.canEditScope(), false, 'Analyst não altera o escopo');
  const box = { checked: true } as HTMLInputElement;
  c.toggleConsolidatedSource('MicrosoftAzure', { target: box } as unknown as Event);
  eq(api.of('saveScope').length, 0, 'nenhuma gravação');
  eq(box.checked, true, 'o checkbox volta ao escopo salvo (Azure continua dentro)');
  contains(c.scopeNote(), 'Manager ou TenantAdmin', 'o motivo é dito');
});

// ---- (6) publicação --------------------------------------------------------------------------
test('publicar Geral pina só as execuções incluídas — nunca a fonte fora do escopo', () => {
  const { c, api, hist } = mount();
  const pedido = api.last('getConsolidated').args[0] as KnightSourceType[];
  reply(api.last('getConsolidated'), consolidated(pedido));
  eq(c.consolidatedPublishable(), true, 'composição estável e publicável');
  c.publishConsolidatedReport();
  const sel = hist.last('consolidatedHistoryPreview').args[0] as { source: string; runId: string }[];
  eq(sel.map((s) => s.source).join(','), 'MicrosoftEntraId,MicrosoftTeams,MicrosoftExchangeOnline,MicrosoftAzure', 'execuções pinadas');
  ok(!sel.some((s) => s.runId === RUN.MicrosoftFabric), 'o Fabric não entra na prévia nem na publicação');
});

test('enquanto o escopo é salvo, a publicação de Geral fica bloqueada', () => {
  const { c, api } = mount();
  reply(api.last('getConsolidated'), consolidated(api.last('getConsolidated').args[0] as KnightSourceType[]));
  c.toggleConsolidatedSource('MicrosoftAzure');
  eq(c.consolidatedPublishable(), false, 'composição prestes a mudar');
});

// ---- (7) rótulos e modelo --------------------------------------------------------------------
test('rótulos da composição e mapeamento do escopo por slug', () => {
  eq(availabilityLabel('OutOfScope'), 'Fora do escopo', 'fora do escopo');
  eq(availabilityLabel('Available'), 'Disponível, não incluída', 'disponível');
  eq(availabilityLabel('NotAssessed'), 'Sem avaliação concluída', 'sem avaliação');
  eq(availabilityLabel('Included'), 'Incluída', 'incluída');
  eq(outOfScopeSources(scope(['fabric', 'inexistente', 'google'])).join(','), 'MicrosoftFabric', 'slug desconhecido ou não elegível é ignorado');
  eq(outOfScopeSources(null).length, 0, 'sem escopo salvo, todas dentro');
});

console.log(`\n${count - failures}/${count} verificações aprovadas`);
if (failures > 0) process.exit(1);
