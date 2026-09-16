/**
 * [AEGIS-KNIGHT-DURABLE-01] Experiência do tempo limite do NAVEGADOR na tela do AEGIS KNIGHT.
 *
 * O servidor passou a gravar o veredito determinístico ANTES de pedir a narrativa à IA, então uma requisição
 * cortada pelo navegador quase sempre deixa um resultado concluído do outro lado. A tela precisa tratar isso
 * como o que é — uma espera interrompida, não uma avaliação que falhou — e oferecer a RECUPERAÇÃO por
 * leitura. O que este spec trava:
 *
 *   (1) `getLatest` devolve DUAS coisas: o resultado concluído e, à parte, a tentativa que não concluiu;
 *   (2) tempo limite da execução NÃO vira erro genérico, NÃO apaga a avaliação anterior e, acima de tudo,
 *       NÃO dispara outra coleta sozinho;
 *   (3) `recoverAfterTimeout` faz UMA leitura — nenhuma chamada de execução — e adota o resultado gravado;
 *   (4) recuperação sem nenhuma avaliação concluída diz isso, em vez de deixar a tela muda;
 *   (5) uma falha real (não tempo limite) continua no caminho de erro de sempre;
 *   (6) no template real: o botão de recuperar chama a LEITURA, e o aviso da tentativa não concluída não
 *       está aninhado no bloco que só existe quando há avaliação — senão ele sumiria justamente no caso
 *       em que não há resultado nenhum.
 *
 * Compilado por `tsc` (CommonJS, com decorators) e executado por `node`, como os demais specs de lógica.
 */
import * as fs from 'fs';
import * as path from 'path';
import {
  Injector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { TmplAstNode, parseTemplate } from '@angular/compiler';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subject } from 'rxjs';
import { AegisKnightComponent } from '../src/app/pages/aegis-knight.component';
import { KnightRunTimeoutError, KnightService } from '../src/app/services/knight.service';
import { RemediationService } from '../src/app/services/remediation.service';
import { PostureHistoryService } from '../src/app/services/posture-history.service';
import { IdentityRiskService } from '../src/app/services/identity-risk.service';
import { KnightAssessment, KnightLatest, KnightUnfinishedRun } from '../src/app/models/knight.models';

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
  s: Subject<unknown>;
}

class FakeKnight {
  readonly calls: Call[] = [];
  private call<T>(m: string): Observable<T> {
    const s = new Subject<unknown>();
    this.calls.push({ m, s });
    return s.asObservable() as Observable<T>;
  }
  getSources() { return this.call('getSources'); }
  getLatest() { return this.call('getLatest'); }
  getById() { return this.call('getById'); }
  runDemo() { return this.call('runDemo'); }
  runSource() { return this.call('runSource'); }
  getAffected() { return this.call('getAffected'); }
  of(m: string): Call[] { return this.calls.filter((c) => c.m === m); }
  last(m: string): Call {
    const c = this.of(m).at(-1);
    if (!c) throw new Error(`nenhuma chamada a ${m}`);
    return c;
  }
}

function silent<T>(): Observable<T> {
  return new Subject<unknown>().asObservable() as Observable<T>;
}

function injectorWith(knight: FakeKnight): Injector {
  return Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: KnightService, useValue: knight },
      { provide: RemediationService, useValue: { list: () => silent(), get: () => silent() } },
      { provide: PostureHistoryService, useValue: { publish: () => silent() } },
      { provide: IdentityRiskService, useValue: { get: () => silent() } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map<string, string>() } } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
    ],
  });
}

function reply(c: Call, value: unknown): void {
  c.s.next(value);
  c.s.complete();
}
function fail(c: Call, err: unknown): void {
  c.s.error(err);
}

// ---- dados sintéticos ------------------------------------------------------------------------
const RUN_A = 'aaaa1111-0000-0000-0000-000000000001';
const RUN_ORFA = 'bbbb2222-0000-0000-0000-000000000002';

function assessment(id: string): KnightAssessment {
  return {
    id,
    mode: 'Demo',
    isDemo: true,
    sourceType: 'Demo',
    sourceState: 'Completed',
    source: 'Demonstração',
    status: 'Completed',
    catalogVersion: 'ak-knight-v1',
    scoreFormulaVersion: 'knight-score-v1',
    startedAt: '2026-09-15T12:00:00Z',
    completedAt: '2026-09-15T12:00:20Z',
    score: 23,
    coverage: 100,
    counts: { passed: 1, exposed: 3, mitigated: 1, notEvaluated: 0, error: 0, notApplicable: 0 },
    indicators: [],
    capabilities: [],
    advisory: null,
    advisoryFromAi: false,
  };
}

const TENTATIVA: KnightUnfinishedRun = {
  id: RUN_ORFA,
  status: 'Running',
  sourceType: 'MicrosoftEntraId',
  mode: 'Live',
  startedAt: '2026-09-15T18:29:41Z',
};

interface Mounted {
  c: AegisKnightComponent;
  api: FakeKnight;
}

/** Monta a tela e resolve a 1ª leitura com o que o teste pedir. */
function mount(latest: KnightLatest | null): Mounted {
  const api = new FakeKnight();
  const inj = injectorWith(api);
  const c = runInInjectionContext(inj, () => new AegisKnightComponent());
  c.ngOnInit();
  if (latest) reply(api.last('getLatest'), latest);
  return { c, api };
}

// ---- (1) a leitura separa resultado de tentativa ----------------------------------------------

console.log('AEGIS-KNIGHT-DURABLE-01 · tela do KNIGHT');

test('leitura inicial separa o resultado concluído da tentativa que não concluiu', () => {
  const { c } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: TENTATIVA });

  eq(c.assessment()?.id, RUN_A, 'o resultado exibido é o concluído');
  eq(c.unfinishedAttempt()?.id, RUN_ORFA, 'a tentativa é declarada à parte');
  eq(c.loading(), false, 'a carga terminou');
});

test('sem nenhuma avaliação concluída, a tentativa não ocupa o lugar do resultado', () => {
  const { c } = mount({ assessment: null, unfinishedAttempt: TENTATIVA });

  eq(c.assessment(), null, 'uma execução em Running não é resultado');
  eq(c.unfinishedAttempt()?.status, 'Running', 'mas a existência dela aparece');
});

// ---- (2) tempo limite do navegador -------------------------------------------------------------

test('tempo limite da execução não vira erro genérico nem apaga a avaliação anterior', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runDemo();
  fail(api.last('runDemo'), new KnightRunTimeoutError('O navegador parou de esperar pela resposta.'));

  eq(c.running(), false, 'a execução não fica pendurada');
  eq(c.error(), null, 'tempo limite do cliente não é falha da avaliação');
  ok(c.timeoutNotice() !== null, 'a tela avisa que a espera foi cortada');
  contains(c.timeoutNotice(), 'Recupere o resultado gravado', 'o aviso oferece a recuperação');
  eq(c.assessment()?.id, RUN_A, 'a avaliação anterior continua visível');
});

test('tempo limite NÃO dispara outra coleta sozinho', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runDemo();
  fail(api.last('runDemo'), new KnightRunTimeoutError('cortado'));

  eq(api.of('runDemo').length, 1, 'nenhuma segunda execução automática');
  eq(api.of('runSource').length, 0, 'e nenhuma coleta de fonte real');
});

// ---- (3) recuperação é LEITURA ----------------------------------------------------------------

test('recuperar depois do tempo limite lê a última avaliação e não executa nada', () => {
  const { c, api } = mount({ assessment: null, unfinishedAttempt: null });

  c.runDemo();
  fail(api.last('runDemo'), new KnightRunTimeoutError('cortado'));

  const leiturasAntes = api.of('getLatest').length;
  c.recoverAfterTimeout();

  eq(api.of('getLatest').length, leiturasAntes + 1, 'exatamente UMA leitura');
  eq(api.of('runDemo').length, 1, 'a recuperação nunca reexecuta a avaliação');
  eq(api.of('runSource').length, 0, 'nem dispara coleta na fonte');

  reply(api.last('getLatest'), { assessment: assessment(RUN_A), unfinishedAttempt: null });

  eq(c.assessment()?.id, RUN_A, 'o resultado gravado é adotado');
  eq(c.timeoutNotice(), null, 'o aviso sai depois de recuperar');
  eq(c.loading(), false, 'a tela volta ao normal');
});

test('recuperação sem nenhuma avaliação concluída diz isso em vez de calar', () => {
  const { c, api } = mount({ assessment: null, unfinishedAttempt: null });

  c.runDemo();
  fail(api.last('runDemo'), new KnightRunTimeoutError('cortado'));
  c.recoverAfterTimeout();
  reply(api.last('getLatest'), { assessment: null, unfinishedAttempt: TENTATIVA });

  eq(c.assessment(), null, 'não inventa resultado');
  contains(c.timeoutNotice(), 'Nenhuma avaliação concluída', 'a tela explica o que encontrou');
  contains(c.timeoutNotice(), 'Nada foi coletado de novo', 'e deixa claro que não bateu na fonte');
  eq(c.unfinishedAttempt()?.id, RUN_ORFA, 'a tentativa continua declarada');
});

// ---- (4) falha real segue sendo falha ----------------------------------------------------------

test('falha real da execução continua no caminho de erro, sem aviso de tempo limite', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runSource('MicrosoftEntraId');
  fail(api.last('runSource'), new Error('A fonte MicrosoftEntraId não está configurada para este tenant.'));

  contains(c.error(), 'não está configurada', 'o erro real aparece como erro');
  eq(c.timeoutNotice(), null, 'e não é confundido com espera cortada');
  eq(c.assessment()?.id, RUN_A, 'uma falha real nunca substitui a avaliação por outra');
});

test('uma execução bem-sucedida limpa a tentativa pendente', () => {
  const { c, api } = mount({ assessment: null, unfinishedAttempt: TENTATIVA });

  c.runDemo();
  reply(api.last('runDemo'), assessment(RUN_A));

  eq(c.assessment()?.id, RUN_A, 'o novo resultado entra');
  eq(c.unfinishedAttempt(), null, 'a tentativa foi sucedida por um resultado');
});

// ---- (5) template real -------------------------------------------------------------------------

function templateOf(): TmplAstNode[] {
  const src = fs.readFileSync(
    path.join(process.cwd(), 'src/app/pages/aegis-knight.component.ts'),
    'utf8',
  );
  const m = /template:\s*`([\s\S]*?)`,\n\s{2}styles:/.exec(src);
  if (!m) throw new Error('não foi possível isolar o template do componente');
  const parsed = parseTemplate(m[1], 'aegis-knight.component.html');
  if (parsed.errors?.length) throw new Error(`template inválido: ${parsed.errors[0].msg}`);
  return parsed.nodes;
}

function flatten(nodes: TmplAstNode[]): string {
  return JSON.stringify(nodes, (k, v) => (k === 'sourceSpan' || k === 'span' ? undefined : v));
}

test('o template real chama a RECUPERAÇÃO — e não uma nova execução — no botão do aviso', () => {
  const src = fs.readFileSync(
    path.join(process.cwd(), 'src/app/pages/aegis-knight.component.ts'),
    'utf8',
  );
  const bloco = /@if \(timeoutNotice\(\); as tmsg\) \{[\s\S]*?\n {8}\}/.exec(src);
  ok(bloco !== null, 'o aviso de tempo limite existe no template');
  contains(bloco![0], 'recoverAfterTimeout()', 'o botão do aviso recupera por leitura');
  ok(!/runDemo\(\)|runSource\(/.test(bloco![0]), 'o aviso NUNCA oferece disparar outra coleta');
});

test('o aviso da tentativa não concluída não depende de existir avaliação', () => {
  const src = fs.readFileSync(
    path.join(process.cwd(), 'src/app/pages/aegis-knight.component.ts'),
    'utf8',
  );
  const iAviso = src.indexOf('@if (unfinishedAttempt(); as tent)');
  const iAvaliacao = src.indexOf('@if (assessment(); as a)');
  ok(iAviso > 0, 'o aviso da tentativa existe');
  ok(iAvaliacao > 0, 'o bloco da avaliação existe');
  ok(
    iAviso < iAvaliacao,
    'o aviso vem ANTES do bloco da avaliação — dentro dele, sumiria justo quando não há resultado',
  );
});

test('o template do componente continua compilando', () => {
  const nodes = templateOf();
  ok(nodes.length > 0, 'o template tem nós');
  contains(flatten(nodes), 'recoverAfterTimeout', 'o handler de recuperação está ligado no template');
});

// ---- resultado ---------------------------------------------------------------------------------
console.log(`\n${count - failures}/${count} verificações aprovadas`);
if (failures > 0) process.exit(1);
