/**
 * [AEGIS-ASSESSMENT-VISUALS-01] Confirmação de publicação presa à prévia VIGENTE, nos componentes reais (serviços dublês
 * com respostas entregues na ordem que o teste escolher).
 *
 * O que este spec trava:
 *   KNIGHT (aegis-knight.component):
 *     (1) prévia da fonte A, troca para a fonte B: a confirmação antiga não publica, e a resposta atrasada da prévia de A
 *         não reabre a prévia;
 *     (2) qualquer troca do resultado exibido que escape do descarte é barrada no PRÓPRIO método de confirmação;
 *     (3) prévia consolidada e composição alterada: não publica; a nova prévia publica exatamente as execuções pinadas
 *         da composição exibida agora;
 *     (4) publicação já enviada que responde depois da troca: não apaga a prévia nova, não oferece o relatório antigo
 *         como o da seleção atual, e a nova prévia ainda publica;
 *     (5) 409 de histórico continua relendo a prévia, com o aviso.
 *   NIST (nist-publish-panel.component):
 *     (6) período A carregado, troca para B em voo: Publicar bloqueado (botão e método); B falha: continua bloqueado;
 *         B conclui: publica com a impressão digital e o período da prévia de B;
 *     (7) resposta atrasada de A depois de B é descartada;
 *     (8) 409 continua remontando a prévia e bloqueando até ela chegar.
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import * as fs from 'fs';
import * as path from 'path';
import {
  Injector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subject } from 'rxjs';
import { AegisKnightComponent } from '../src/app/pages/aegis-knight.component';
import { NistPublishPanelComponent } from '../src/app/pages/nist/nist-publish-panel.component';
import { KnightService } from '../src/app/services/knight.service';
import { RemediationService } from '../src/app/services/remediation.service';
import { HistoryChangedError, PostureHistoryService } from '../src/app/services/posture-history.service';
import { IdentityRiskService } from '../src/app/services/identity-risk.service';
import { NistApiError, NistService } from '../src/app/services/nist.service';
import { KnightAssessment, KnightSourceType } from '../src/app/models/knight.models';
import { FrozenPostureHistory, HistoryWindowRequest } from '../src/app/models/posture-history.models';
import { NistPublicationPreview } from '../src/app/models/nist.models';

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

// ---- dublês: cada chamada devolve um Subject que o teste resolve quando quiser -----------------
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
  getAffectedSummary() { return this.call('getAffectedSummary', []); }
  getCompositionAffectedSummary() { return this.call('getCompositionAffectedSummary', []); }
  getReferenceCoverage() { return this.call('getReferenceCoverage', []); }
  getGlossary() { return this.call('getGlossary', []); }
  runDemo() { return this.call('runDemo', []); }
  runSource() { return this.call('runSource', []); }
}
class FakeHistory extends Recorder {
  historyPreview(...a: unknown[]) { return this.call('historyPreview', a); }
  consolidatedHistoryPreview(...a: unknown[]) { return this.call('consolidatedHistoryPreview', a); }
  publish(...a: unknown[]) { return this.call('publish', a); }
  publishConsolidated(...a: unknown[]) { return this.call('publishConsolidated', a); }
  exportSnapshot(...a: unknown[]) { return this.call('exportSnapshot', a); }
}
class FakeNist extends Recorder {
  publicationPreview(...a: unknown[]) { return this.call('publicationPreview', a); }
  publish(...a: unknown[]) { return this.call('publish', a); }
  publications(...a: unknown[]) { return this.call('publications', a); }
  compare(...a: unknown[]) { return this.call('compare', a); }
}
function reply(c: Call, value: unknown): void {
  c.s.next(value);
  c.s.complete();
}
function fail(c: Call, err: unknown): void {
  c.s.error(err);
}
const silent = () => new Subject<unknown>().asObservable();
const ROOT = [
  { provide: INJECTOR_SCOPE, useValue: 'root' },
  { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
];

// ---- dados sintéticos ------------------------------------------------------------------------
const RUN_ENTRA = 'aaaa1111-0000-0000-0000-000000000001';
const RUN_TEAMS = 'bbbb2222-0000-0000-0000-000000000002';

function assessment(id: string, sourceType: KnightSourceType, over: Partial<KnightAssessment> = {}): KnightAssessment {
  return {
    id, mode: 'Live', isDemo: false, sourceType, sourceState: 'Completed', source: sourceType, status: 'Completed',
    catalogVersion: 'ak-knight-v10', scoreFormulaVersion: 'knight-score-v1',
    startedAt: '2026-10-07T12:00:00Z', completedAt: '2026-10-07T12:00:20Z', score: 60, coverage: 90,
    counts: { passed: 1, exposed: 1, mitigated: 0, notEvaluated: 0, error: 0, notApplicable: 0 },
    indicators: [], capabilities: [], advisory: null, advisoryFromAi: false, ...over,
  } as KnightAssessment;
}
function block(source: KnightSourceType, runId: string) {
  return { source, slug: source, label: source, assessment: assessment(runId, source), unfinishedAttempt: null };
}
/** Composição consolidada como o servidor devolve: só as fontes incluídas têm a execução pinada. */
function consolidated(included: [KnightSourceType, string][]): KnightAssessment {
  return assessment('', 'Consolidated' as KnightSourceType, {
    sources: included.map(([source, runId]) => ({
      source, slug: source, label: source, included: true, availabilityState: 'Included', sourceRunId: runId,
      sourceState: 'Completed', catalogVersion: 'ak-knight-v10', capturedAt: '2026-10-07T12:00:00Z', score: 60,
      coverage: 90, counts: null, collectionLimitations: [],
    })) as KnightAssessment['sources'],
  });
}
function frozen(fp: string): FrozenPostureHistory {
  return {
    schema: 'posture-history-v1', criterion: 'last-of-month', from: '2025-11', until: '2026-10', months: [],
    series: {} as FrozenPostureHistory['series'], points: [], publicationMonth: '2026-10', includesThisPublication: true,
    relatedSeries: [], basisFingerprint: fp,
  };
}
function published(id: string) {
  return { summary: { id } };
}

// ---- montagem do KNIGHT ------------------------------------------------------------------------
interface Knight {
  c: AegisKnightComponent;
  api: FakeKnight;
  hist: FakeHistory;
}
function mountKnight(): Knight {
  const api = new FakeKnight();
  const hist = new FakeHistory();
  const query = new Map<string, string>();
  const inj = Injector.create({
    providers: [
      ...ROOT,
      { provide: KnightService, useValue: api },
      { provide: PostureHistoryService, useValue: hist },
      { provide: RemediationService, useValue: { list: silent, get: silent } },
      { provide: IdentityRiskService, useValue: { get: silent } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: query } } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });
  const c = runInInjectionContext(inj, () => new AegisKnightComponent());
  c.ngOnInit();
  reply(api.last('getLatestBySource'), {
    sources: [block('MicrosoftEntraId', RUN_ENTRA), block('MicrosoftTeams', RUN_TEAMS)],
  });
  return { c, api, hist };
}
/** Abre a prévia da avaliação exibida (por fonte) e, se pedido, entrega a resposta. */
function previewRun(k: Knight, fp: string | null): void {
  k.c.publishReport(k.c.assessment()!.id);
  if (fp) reply(k.hist.last('historyPreview'), frozen(fp));
}
function enterConsolidated(k: Knight, included: [KnightSourceType, string][]): void {
  k.c.showConsolidated();
  reply(k.api.last('getConsolidated'), consolidated(included));
}

const SRC = fs.readFileSync(path.join(process.cwd(), 'src/app/pages/aegis-knight.component.ts'), 'utf8').replace(/\r\n/g, '\n');

console.log('AEGIS-ASSESSMENT-VISUALS-01 · confirmação de publicação presa à prévia vigente');

// ---- (1) troca de fonte ---------------------------------------------------------------------------
test('KNIGHT: prévia da fonte A, troca para B — a confirmação antiga não publica', () => {
  const k = mountKnight();
  const fonteA = k.c.assessment()!.id;
  previewRun(k, 'fp-a');
  ok(k.c.pubPreview()?.history?.basisFingerprint === 'fp-a', 'a prévia de A está aberta');

  k.c.selectSource(fonteA === RUN_ENTRA ? 'MicrosoftTeams' : 'MicrosoftEntraId');
  eq(k.c.pubPreview(), null, 'mudar o resultado exibido descarta a prévia');
  k.c.confirmPublish();
  eq(k.hist.of('publish').length, 0, 'nada é publicado');
});

test('KNIGHT: resposta atrasada da prévia de A, depois da troca para B, não reabre a prévia', () => {
  const k = mountKnight();
  const fonteA = k.c.assessment()!.id;
  previewRun(k, null);
  k.c.selectSource(fonteA === RUN_ENTRA ? 'MicrosoftTeams' : 'MicrosoftEntraId');
  reply(k.hist.last('historyPreview'), frozen('fp-a-atrasada'));
  eq(k.c.pubPreview(), null, 'a resposta de A é descartada');
  k.c.confirmPublish();
  eq(k.hist.of('publish').length, 0, 'nada é publicado');
});

// ---- (2) conferência no método --------------------------------------------------------------------
test('KNIGHT: troca do resultado exibido que escape do descarte é barrada no próprio método', () => {
  const k = mountKnight();
  previewRun(k, 'fp-a');
  // Simula uma via que trocasse o resultado sem passar pelo descarte: a prévia continua, o alvo não.
  k.c.assessment.set(assessment('cccc3333-0000-0000-0000-000000000003', 'MicrosoftEntraId'));
  k.c.confirmPublish();
  eq(k.hist.of('publish').length, 0, 'o método confere o alvo e não publica');
  eq(k.c.pubPreview(), null, 'a prévia obsoleta é descartada');
  contains(k.c.error(), 'mudou desde a prévia', 'a tela diz por que nada foi publicado');
});

test('KNIGHT: sem troca, a prévia vigente publica exatamente a execução aberta', () => {
  const k = mountKnight();
  const aberta = k.c.assessment()!.id;
  previewRun(k, 'fp-a');
  k.c.confirmPublish();
  eq(k.hist.of('publish').length, 1, 'publica');
  const req = k.hist.last('publish').args[0] as { runId: string; expectedHistoryFingerprint: string };
  eq(req.runId, aberta, 'a execução da prévia');
  eq(req.expectedHistoryFingerprint, 'fp-a', 'a impressão digital da prévia');
});

// ---- (3) consolidado ------------------------------------------------------------------------------
test('KNIGHT: prévia consolidada e composição alterada — não publica; a nova prévia publica a composição atual', () => {
  const k = mountKnight();
  enterConsolidated(k, [['MicrosoftEntraId', RUN_ENTRA], ['MicrosoftTeams', RUN_TEAMS]]);
  k.c.publishConsolidatedReport();
  reply(k.hist.last('consolidatedHistoryPreview'), frozen('fp-ab'));
  ok(!!k.c.pubPreview()?.history, 'prévia da composição A+B aberta');

  k.c.toggleConsolidatedSource('MicrosoftTeams');
  eq(k.c.pubPreview(), null, 'mudar a composição descarta a prévia');
  k.c.confirmPublish();
  eq(k.hist.of('publishConsolidated').length, 0, 'a composição antiga não é publicada');

  reply(k.api.last('getConsolidated'), consolidated([['MicrosoftEntraId', RUN_ENTRA]]));
  k.c.publishConsolidatedReport();
  const pedido = k.hist.last('consolidatedHistoryPreview').args[0] as { source: string; runId: string }[];
  eq(JSON.stringify(pedido), JSON.stringify([{ source: 'MicrosoftEntraId', runId: RUN_ENTRA }]), 'prévia da composição exibida');
  reply(k.hist.last('consolidatedHistoryPreview'), frozen('fp-a'));
  k.c.confirmPublish();
  eq(k.hist.of('publishConsolidated').length, 1, 'a prévia vigente publica');
  const req = k.hist.last('publishConsolidated').args[0] as { selection: unknown; expectedHistoryFingerprint: string };
  eq(JSON.stringify(req.selection), JSON.stringify([{ source: 'MicrosoftEntraId', runId: RUN_ENTRA }]),
    'publica as execuções PINADAS da composição exibida');
  eq(req.expectedHistoryFingerprint, 'fp-a', 'com a impressão digital da prévia nova');
});

test('KNIGHT: prévia consolidada e saída para uma fonte — não publica', () => {
  const k = mountKnight();
  enterConsolidated(k, [['MicrosoftEntraId', RUN_ENTRA], ['MicrosoftTeams', RUN_TEAMS]]);
  k.c.publishConsolidatedReport();
  reply(k.hist.last('consolidatedHistoryPreview'), frozen('fp-ab'));
  k.c.exitConsolidated();
  eq(k.c.pubPreview(), null, 'sair do consolidado descarta a prévia');
  k.c.confirmPublish();
  eq(k.hist.of('publishConsolidated').length + k.hist.of('publish').length, 0, 'nada é publicado');
});

test('KNIGHT: composição relida com outra execução pinada — a prévia anterior não confirma', () => {
  const k = mountKnight();
  enterConsolidated(k, [['MicrosoftEntraId', RUN_ENTRA]]);
  k.c.publishConsolidatedReport();
  reply(k.hist.last('consolidatedHistoryPreview'), frozen('fp-a'));
  // A composição exibida passa a apontar outra execução da mesma fonte (sem passar pelo descarte).
  k.c.assessment.set(consolidated([['MicrosoftEntraId', 'dddd4444-0000-0000-0000-000000000004']]));
  k.c.confirmPublish();
  eq(k.hist.of('publishConsolidated').length, 0, 'execução pinada diferente da prévia: não publica');
});

// ---- (4) publicação já enviada --------------------------------------------------------------------
test('KNIGHT: publicação enviada que responde depois da troca não apaga a prévia nova nem vira o relatório atual', () => {
  const k = mountKnight();
  const fonteA = k.c.assessment()!.id;
  previewRun(k, 'fp-a');
  k.c.confirmPublish();
  eq(k.c.publishing(), true, 'publicação de A enviada');

  k.c.selectSource(fonteA === RUN_ENTRA ? 'MicrosoftTeams' : 'MicrosoftEntraId');
  eq(k.c.publishing(), false, 'a tela de B não fica presa à publicação de A');
  const fonteB = k.c.assessment()!.id;
  previewRun(k, 'fp-b');

  reply(k.hist.of('publish')[0], published('snap-a'));
  ok(k.c.pubPreview()?.history?.basisFingerprint === 'fp-b', 'a prévia de B continua aberta');
  eq(k.c.publishedId(), null, 'o relatório de A não é oferecido como o da seleção atual');
  contains(k.c.publishNotice(), 'não ao atual', 'a conclusão de A é dita como da seleção anterior');

  k.c.confirmPublish();
  eq(k.hist.of('publish').length, 2, 'a prévia de B ainda publica');
  eq((k.hist.last('publish').args[0] as { runId: string }).runId, fonteB, 'publica B');
  reply(k.hist.last('publish'), published('snap-b'));
  eq(k.c.publishedId(), 'snap-b', 'o relatório oferecido é o de B');
});

test('KNIGHT: erro atrasado de uma publicação superada não aparece na seleção atual', () => {
  const k = mountKnight();
  const fonteA = k.c.assessment()!.id;
  previewRun(k, 'fp-a');
  k.c.confirmPublish();
  k.c.selectSource(fonteA === RUN_ENTRA ? 'MicrosoftTeams' : 'MicrosoftEntraId');
  previewRun(k, 'fp-b');
  fail(k.hist.of('publish')[0], new HistoryChangedError('A série mudou.'));
  eq(k.c.error(), null, 'sem erro de A na tela de B');
  eq(k.c.pubPreview()?.history?.basisFingerprint, 'fp-b', 'a prévia de B não é relida pelo 409 de A');
  eq(k.hist.of('historyPreview').length, 2, 'nenhuma releitura disparada pelo 409 de A');
});

// ---- (5) 409 preservado ---------------------------------------------------------------------------
test('KNIGHT: 409 de histórico na publicação vigente relê a prévia com o aviso', () => {
  const k = mountKnight();
  previewRun(k, 'fp-a');
  k.c.confirmPublish();
  fail(k.hist.last('publish'), new HistoryChangedError('O histórico mudou desde a prévia.'));
  eq(k.hist.of('historyPreview').length, 2, 'a prévia é relida');
  eq(k.c.pubPreview()?.loading, true, 'e fica bloqueada até a resposta');
  k.c.confirmPublish();
  eq(k.hist.of('publish').length, 1, 'sem nova publicação enquanto a prévia carrega');
  reply(k.hist.last('historyPreview'), frozen('fp-a2'));
  contains(k.c.pubPreview()?.notice, 'série atual', 'aviso do 409');
});

test('KNIGHT: o descarte cobre fonte, consolidado, composição, releitura e nova execução', () => {
  const corpo = (nome: string) => {
    const i = SRC.indexOf(nome);
    ok(i >= 0, `método ${nome}`);
    return SRC.slice(i, SRC.indexOf('\n  }\n', i));
  };
  for (const m of ['selectSource(source: KnightSourceType): void', 'showConsolidated(): void', 'exitConsolidated(): void',
    'private loadConsolidated(): void', 'private execute(']) {
    contains(corpo(m), 'this.discardPublishPreview()', m);
  }
  contains(corpo('reload(): void'), 'this.discardPublishPreview()', 'reload');
});

// ---- montagem do painel NIST ---------------------------------------------------------------------
const CTX = { assessmentId: 'n-1', cycleId: 'c-1', scopeId: 's-1' };
const W_A: HistoryWindowRequest = { months: 12, until: null };
const W_B: HistoryWindowRequest = { months: 6, until: '2026-08' };

interface Panel {
  c: NistPublishPanelComponent;
  p: {
    preview(): NistPublicationPreview | null;
    previewCurrent(): boolean;
    previewError(): string | null;
    publishError(): string | null;
    publishing(): boolean;
    loadPreview(): void;
    setHistoryWindow(w: HistoryWindowRequest): void;
    /** O template antigo passava a prévia EXIBIDA; o atual ignora o argumento e confere a vigente. */
    publish(pv?: NistPublicationPreview | null): void;
  };
  api: FakeNist;
}
function mountPanel(): Panel {
  const api = new FakeNist();
  const inj = Injector.create({
    providers: [...ROOT, { provide: NistService, useValue: api }, { provide: PostureHistoryService, useValue: new FakeHistory() }],
  });
  const c = runInInjectionContext(inj, () => new NistPublishPanelComponent());
  // Entradas do componente (sem template): leitura direta, como o pai faria.
  Object.assign(c, { ctx: () => CTX, cycles: () => [], params: () => ({}), canWrite: () => true });
  return { c, p: c as unknown as Panel['p'], api };
}
function nistPreview(fp: string, historyFp: string): NistPublicationPreview {
  return {
    ...CTX, contentFingerprint: fp, findings: 0, limitations: [], warnings: [], functions: [],
    summary: {} as NistPublicationPreview['summary'], history: frozen(historyFp),
  } as NistPublicationPreview;
}

// ---- (6) período em voo, falha e conclusão --------------------------------------------------------
test('NIST: troca de período em voo bloqueia; falha de B continua bloqueando; só a prévia de B confirma', () => {
  const { p, api } = mountPanel();
  p.loadPreview();
  eq(JSON.stringify(api.last('publicationPreview').args[1]), JSON.stringify(W_A), 'prévia do período A');
  reply(api.last('publicationPreview'), nistPreview('conteudo-a', 'hist-a'));
  eq(p.preview()?.contentFingerprint, 'conteudo-a', 'prévia de A exibida');

  p.setHistoryWindow(W_B);
  p.publish(p.preview());
  eq(api.of('publish').length, 0, 'B em voo: o método não publica a prévia de A com o período B');
  eq(p.previewCurrent(), false, 'B em voo: Publicar indisponível');

  fail(api.last('publicationPreview'), new Error('Falha ao montar a prévia.'));
  eq(p.previewCurrent(), false, 'B falhou: continua bloqueado');
  eq(p.preview(), null, 'a prévia de A não fica disponível para confirmar no lugar de B');
  p.publish(p.preview());
  eq(api.of('publish').length, 0, 'nada publicado após a falha');

  p.loadPreview();
  eq(JSON.stringify(api.last('publicationPreview').args[1]), JSON.stringify(W_B), 'nova leitura do período B');
  reply(api.last('publicationPreview'), nistPreview('conteudo-b', 'hist-b'));
  eq(p.previewCurrent(), true, 'prévia de B vigente');
  p.publish(p.preview());
  eq(api.of('publish').length, 1, 'publica');
  const [, fp, hfp, w] = api.last('publish').args;
  eq(fp, 'conteudo-b', 'impressão digital do conteúdo de B');
  eq(hfp, 'hist-b', 'impressão digital do histórico de B');
  eq(JSON.stringify(w), JSON.stringify(W_B), 'o período que a prévia de B apresentou');
});

// ---- (7) resposta atrasada ------------------------------------------------------------------------
test('NIST: resposta atrasada do período A, depois de B, é descartada', () => {
  const { p, api } = mountPanel();
  p.loadPreview();
  const pedidoA = api.last('publicationPreview');
  p.setHistoryWindow(W_B);
  reply(api.last('publicationPreview'), nistPreview('conteudo-b', 'hist-b'));
  reply(pedidoA, nistPreview('conteudo-a', 'hist-a'));
  eq(p.preview()?.contentFingerprint, 'conteudo-b', 'a tela mantém B');
  p.publish(p.preview());
  eq(JSON.stringify(api.last('publish').args[3]), JSON.stringify(W_B), 'confirma B com o período B');
  eq(api.last('publish').args[1], 'conteudo-b', 'e a impressão digital de B');
});

// ---- (8) 409 preservado ---------------------------------------------------------------------------
test('NIST: 409 remonta a prévia e bloqueia a confirmação até ela chegar', () => {
  const { p, api } = mountPanel();
  p.loadPreview();
  reply(api.last('publicationPreview'), nistPreview('conteudo-a', 'hist-a'));
  p.publish(p.preview());
  fail(api.last('publish'), new NistApiError('O histórico mudou desde a prévia.', 409));
  contains(p.publishError(), 'Nada foi publicado', 'o 409 é dito');
  eq(api.of('publicationPreview').length, 2, 'a prévia é remontada');
  p.publish(p.preview());
  eq(api.of('publish').length, 1, 'sem nova publicação');
  eq(p.previewCurrent(), false, 'sem prévia vigente enquanto ela carrega');
  reply(api.last('publicationPreview'), nistPreview('conteudo-a2', 'hist-a2'));
  eq(p.previewCurrent(), true, 'a prévia nova volta a permitir a confirmação');
  contains(p.publishError(), 'Nada foi publicado', 'o aviso do 409 permanece');
});

test('NIST: o botão Publicar depende da prévia vigente, não só de "publicando"', () => {
  const src = fs.readFileSync(path.join(process.cwd(), 'src/app/pages/nist/nist-publish-panel.component.ts'), 'utf8');
  contains(src, '(click)="publish()" [disabled]="publishing() || !previewCurrent()"', 'botão');
});

console.log(`\n${count - failures}/${count} verificações aprovadas`);
if (failures > 0) process.exit(1);
