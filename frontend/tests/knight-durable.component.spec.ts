/**
 * [AEGIS-KNIGHT-DURABLE-01] Tela do AEGIS KNIGHT: última avaliação, execução não finalizada e tempo limite
 * do NAVEGADOR.
 *
 * O que este spec trava:
 *   (1) a leitura composta (`getLatestState`) separa o resultado concluído da tentativa não finalizada, e o
 *       aviso da tentativa muda conforme exista, ou não, uma avaliação concluída abaixo;
 *   (2) tempo limite da execução NÃO vira erro genérico, NÃO apaga a avaliação anterior e NÃO dispara outra
 *       coleta sozinho;
 *   (3) depois do tempo limite, a tela CONSULTA a última avaliação disponível — uma leitura — e afirma só o
 *       que ela comprova: a MESMA avaliação já exibida, OUTRA (inclusive de outra fonte/modo, sem ser dada
 *       como a tentativa), nenhuma, ou falha da consulta. O desfecho da tentativa segue não confirmado;
 *   (4) uma falha real (não tempo limite) continua no caminho de erro de sempre;
 *   (5) execução aberta por ID sem conclusão registrada: exibida como NÃO finalizada, sem publicação nem
 *       plano, e o aviso da tentativa abre essa execução de fato (relendo a tela);
 *   (6) no template real: o botão do aviso CONSULTA (não coleta), nada promete "recuperar", e os textos não
 *       afirmam "não produziu veredito" nem um resultado concluído que não existe.
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
const pending: Promise<void>[] = [];
function test(name: string, fn: () => void | Promise<void>): void {
  count++;
  const report = (e?: unknown) => {
    if (e === undefined) console.log(`  ok - ${name}`);
    else {
      failures++;
      console.log(`  FAIL - ${name}\n      ${(e as Error).message}`);
    }
  };
  try {
    const r = fn();
    if (r instanceof Promise) pending.push(r.then(() => report(), report));
    else report();
  } catch (e) {
    report(e);
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
function lacks(haystack: string | null | undefined, needle: RegExp, msg: string): void {
  if (needle.test(haystack ?? '')) throw new Error(`${msg}: "${haystack}"`);
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
  getLatestState() { return this.call('getLatestState'); }
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

/** Roteador dublê: a navegação ATUALIZA o snapshot, como o Angular faz, e é registrada. */
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

interface Mounted {
  c: AegisKnightComponent;
  api: FakeKnight;
  router: FakeRouter;
  query: Map<string, string>;
}

function mountRaw(query: Record<string, string> = {}): Mounted {
  const api = new FakeKnight();
  const map = new Map(Object.entries(query));
  const router = new FakeRouter(map);
  const inj = Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: KnightService, useValue: api },
      { provide: RemediationService, useValue: { list: () => silent(), get: () => silent() } },
      { provide: PostureHistoryService, useValue: { publish: () => silent() } },
      { provide: IdentityRiskService, useValue: { get: () => silent() } },
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: map } } },
      { provide: Router, useValue: router },
    ],
  });
  const c = runInInjectionContext(inj, () => new AegisKnightComponent());
  c.ngOnInit();
  return { c, api, router, query: map };
}

/** Monta a tela e resolve a 1ª leitura (composta) com o que o teste pedir. */
function mount(latest: KnightLatest): Mounted {
  const m = mountRaw();
  reply(m.api.last('getLatestState'), latest);
  return m;
}

function reply(c: Call, value: unknown): void {
  c.s.next(value);
  c.s.complete();
}
function fail(c: Call, err: unknown): void {
  c.s.error(err);
}
function timeoutOn(api: FakeKnight, m: 'runDemo' | 'runSource'): void {
  fail(api.last(m), new KnightRunTimeoutError('O navegador deixou de aguardar a resposta desta execução.'));
}

// ---- dados sintéticos ------------------------------------------------------------------------
const RUN_A = 'aaaa1111-0000-0000-0000-000000000001';
const RUN_B = 'bbbb2222-0000-0000-0000-000000000002';
const RUN_ORFA = 'cccc3333-0000-0000-0000-000000000003';

function assessment(id: string, over: Partial<KnightAssessment> = {}): KnightAssessment {
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
    ...over,
  };
}

/** Execução legada abandonada: vereditos gravados, sem conclusão nem narrativa. */
function orfa(): KnightAssessment {
  return assessment(RUN_ORFA, {
    mode: 'Live',
    isDemo: false,
    sourceType: 'MicrosoftEntraId',
    source: 'Microsoft Entra ID',
    status: 'Running',
    startedAt: '2026-09-15T18:29:41Z',
    completedAt: null,
    advisory: null,
  });
}

const TENTATIVA: KnightUnfinishedRun = {
  id: RUN_ORFA,
  status: 'Running',
  sourceType: 'MicrosoftEntraId',
  mode: 'Live',
  startedAt: '2026-09-15T18:29:41Z',
};

const SRC = fs.readFileSync(path.join(process.cwd(), 'src/app/pages/aegis-knight.component.ts'), 'utf8');

console.log('AEGIS-KNIGHT-DURABLE-01 · tela do KNIGHT');

// ---- (1) leitura composta ----------------------------------------------------------------------

test('a tela lê a rota COMPOSTA e separa o resultado concluído da tentativa não finalizada', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: TENTATIVA });

  eq(api.of('getLatest').length, 0, 'a tela não depende do formato antigo de /latest');
  eq(c.assessment()?.id, RUN_A, 'o resultado exibido é o concluído');
  eq(c.unfinishedAttempt()?.id, RUN_ORFA, 'a tentativa é declarada à parte');
  eq(c.unfinishedView(), false, 'o resultado exibido é uma avaliação concluída');
  eq(c.loading(), false, 'a carga terminou');
});

test('tentativa COM avaliação concluída anterior: o aviso aponta o resultado concluído abaixo', () => {
  const { c } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: TENTATIVA });
  const msg = c.unfinishedAttemptMessage();

  contains(msg, 'conclusão não foi registrada', 'a conclusão da execução é o que falta');
  contains(msg, 'pode ter gravado resultados determinísticos', 'resultado registrado ≠ conclusão');
  contains(msg, 'não tem narrativa consultiva registrada', 'narrativa é a terceira coisa');
  contains(msg, 'O resultado mostrado abaixo é o da última avaliação concluída', 'há resultado concluído abaixo');
  lacks(msg, /não produziu veredito/, 'os vereditos eram gravados antes da IA');
});

test('tentativa SEM avaliação concluída: o aviso não promete resultado abaixo', () => {
  const { c } = mount({ assessment: null, unfinishedAttempt: TENTATIVA });
  const msg = c.unfinishedAttemptMessage();

  eq(c.assessment(), null, 'uma execução não finalizada não ocupa o lugar do resultado');
  contains(msg, 'Não há avaliação concluída para mostrar', 'a ausência é dita');
  lacks(msg, /mostrado abaixo/, 'não existe resultado concluído abaixo');
  lacks(msg, /não produziu veredito/, 'os vereditos eram gravados antes da IA');
});

test('sem tentativa, não há aviso', () => {
  const { c } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });
  eq(c.unfinishedAttemptMessage(), null, 'nada a declarar');
});

// ---- (2) tempo limite do navegador -------------------------------------------------------------

test('tempo limite não vira erro genérico, não apaga a avaliação e declara o desfecho desconhecido', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runDemo();
  timeoutOn(api, 'runDemo');

  eq(c.running(), false, 'a execução não fica pendurada');
  eq(c.error(), null, 'tempo limite do cliente não é falha da avaliação');
  contains(c.timeoutNotice(), 'não foi confirmado', 'o desfecho é desconhecido');
  contains(c.timeoutNotice(), 'não identifica a tentativa', 'a consulta não reconhece a tentativa');
  lacks(c.timeoutNotice(), /[Rr]ecuper/, 'nada é prometido como recuperação');
  eq(c.assessment()?.id, RUN_A, 'a avaliação anterior continua visível');
});

test('tempo limite NÃO dispara outra coleta sozinho', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runSource('MicrosoftEntraId');
  timeoutOn(api, 'runSource');

  eq(api.of('runSource').length, 1, 'nenhuma segunda coleta automática');
  eq(api.of('runDemo').length, 0, 'e nenhuma execução de demonstração');
});

// ---- (3) consulta após o tempo limite ----------------------------------------------------------

test('consulta devolve a MESMA avaliação antiga: a tela diz isso e não dá a tentativa por recuperada', () => {
  const { c, api, router } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runSource('MicrosoftEntraId');
  timeoutOn(api, 'runSource');
  const leituras = api.of('getLatestState').length;
  c.consultLatestAfterTimeout();
  eq(api.of('getLatestState').length, leituras + 1, 'exatamente UMA leitura');
  reply(api.last('getLatestState'), { assessment: assessment(RUN_A), unfinishedAttempt: null });

  const msg = c.consultNotice();
  contains(msg, 'mesma já exibida antes da tentativa', 'a leitura trouxe a mesma avaliação');
  contains(msg, 'Nenhuma avaliação concluída mais recente', 'não há resultado novo');
  contains(msg, 'continua não confirmado', 'o desfecho da tentativa segue desconhecido');
  contains(msg, 'Demonstração (demonstração)', 'procedência do resultado exibido');
  contains(msg, RUN_A.slice(0, 8), 'identificação do resultado exibido');
  lacks(msg, /[Rr]ecuperad/, 'nada foi recuperado');
  eq(c.assessment()?.id, RUN_A, 'a tela continua na mesma avaliação');
  eq(c.timeoutNotice(), null, 'o aviso do corte cede lugar ao resultado da consulta');
  eq(router.navigations.length, 0, 'o endereço não muda');
  eq(api.of('runSource').length, 1, 'a consulta nunca reexecuta');
});

test('consulta devolve OUTRA execução, de outra fonte/modo: exibida, mas NÃO como a tentativa', () => {
  const { c, api, query } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  // A tentativa era uma coleta REAL do Entra ID; a última disponível passou a ser uma DEMONSTRAÇÃO.
  c.runSource('MicrosoftEntraId');
  timeoutOn(api, 'runSource');
  c.consultLatestAfterTimeout();
  const outra = assessment(RUN_B, { startedAt: '2026-09-15T13:05:00Z' });
  reply(api.last('getLatestState'), { assessment: outra, unfinishedAttempt: null });

  const msg = c.consultNotice();
  contains(msg, 'Exibindo a última avaliação disponível', 'a tela diz o que está mostrando');
  contains(msg, 'não é possível confirmar que corresponde à tentativa', 'sem afirmar correspondência');
  contains(msg, 'outra execução pode', 'uma execução concorrente é possível');
  contains(msg, 'Demonstração (demonstração)', 'procedência: fonte e modo da exibida');
  contains(msg, RUN_B.slice(0, 8), 'identificação da exibida');
  contains(msg, 'continua não confirmado', 'o desfecho da tentativa segue desconhecido');
  eq(c.assessment()?.id, RUN_B, 'a última disponível é a exibida');
  eq(c.pinnedRun(), RUN_B, 'a tela fixa a avaliação exibida');
  eq(query.get('run'), RUN_B, 'o endereço aponta para a avaliação exibida');
  eq(api.of('runSource').length, 1, 'nenhuma nova coleta');
});

test('consulta devolve outra execução REAL da mesma fonte: ainda assim, sem afirmar que é a tentativa', () => {
  const { c, api } = mount({ assessment: null, unfinishedAttempt: null });

  c.runSource('MicrosoftEntraId');
  timeoutOn(api, 'runSource');
  c.consultLatestAfterTimeout();
  const real = assessment(RUN_B, {
    mode: 'Live', isDemo: false, sourceType: 'MicrosoftEntraId', source: 'Microsoft Entra ID',
  });
  reply(api.last('getLatestState'), { assessment: real, unfinishedAttempt: null });

  const msg = c.consultNotice();
  contains(msg, 'Microsoft Entra ID (coleta real)', 'procedência');
  contains(msg, 'não é possível confirmar que corresponde à tentativa',
    'fonte e horário, sozinhos, não identificam a execução');
  eq(c.assessment()?.id, RUN_B, 'o resultado novo é exibido');
});

test('consulta sem nenhuma avaliação concluída diz isso, mantém a tentativa declarada e não coleta', () => {
  const { c, api } = mount({ assessment: null, unfinishedAttempt: null });

  c.runDemo();
  timeoutOn(api, 'runDemo');
  c.consultLatestAfterTimeout();
  reply(api.last('getLatestState'), { assessment: null, unfinishedAttempt: TENTATIVA });

  eq(c.assessment(), null, 'não inventa resultado');
  contains(c.consultNotice(), 'Não há avaliação concluída disponível', 'a ausência é dita');
  contains(c.consultNotice(), 'continua não confirmado', 'desfecho desconhecido');
  contains(c.consultNotice(), 'Nada foi coletado', 'nenhuma coleta');
  eq(c.unfinishedAttempt()?.id, RUN_ORFA, 'a tentativa não finalizada continua declarada');
  eq(api.of('runDemo').length, 1, 'nenhuma nova execução');
});

test('falha da consulta: a avaliação exibida não muda e a consulta continua disponível', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runDemo();
  timeoutOn(api, 'runDemo');
  c.consultLatestAfterTimeout();
  fail(api.last('getLatestState'), new Error('Não foi possível consultar a última avaliação disponível.'));

  eq(c.loading(), false, 'a tela sai do carregamento');
  eq(c.assessment()?.id, RUN_A, 'a avaliação exibida não muda');
  ok(c.timeoutNotice() !== null, 'o aviso do corte (com o botão de consulta) permanece');
  contains(c.consultNotice(), 'Não foi possível consultar', 'a falha é dita');
  contains(c.consultNotice(), 'continua não confirmado', 'desfecho desconhecido');
  eq(api.of('runDemo').length, 1, 'nenhuma nova execução');
});

test('uma nova execução limpa os avisos da consulta anterior', () => {
  const { c, api } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: null });

  c.runDemo();
  timeoutOn(api, 'runDemo');
  c.consultLatestAfterTimeout();
  reply(api.last('getLatestState'), { assessment: assessment(RUN_A), unfinishedAttempt: null });
  ok(c.consultNotice() !== null, 'há aviso da consulta');

  c.runDemo();
  eq(c.consultNotice(), null, 'o aviso é da tentativa anterior');
  eq(c.timeoutNotice(), null, 'e o do corte também');
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

// ---- (5) execução não finalizada aberta por ID -------------------------------------------------

test('acesso por ID a uma execução não finalizada: exibida para inspeção, marcada como não finalizada', () => {
  const { c, api } = mountRaw({ run: RUN_ORFA });

  eq(api.of('getLatestState').length, 0, 'com ?run= a tela lê exatamente aquela execução');
  reply(api.last('getById'), orfa());

  eq(c.assessment()?.id, RUN_ORFA, 'o acesso histórico por ID é preservado');
  eq(c.unfinishedView(), true, 'o estado não finalizado é explícito');
  eq(c.pinnedRun(), RUN_ORFA, 'o endereço identifica a execução exibida');
  eq(c.unfinishedAttempt(), null, 'o aviso de tentativa não se sobrepõe à própria execução aberta');
});

test('acesso por ID a uma avaliação concluída continua normal', () => {
  const { c, api } = mountRaw({ run: RUN_A });
  reply(api.last('getById'), assessment(RUN_A));

  eq(c.assessment()?.id, RUN_A, 'avaliação aberta');
  eq(c.unfinishedView(), false, 'concluída não recebe a marca de não finalizada');
});

test('o aviso da tentativa ABRE a execução não finalizada de fato (relê a tela por ID)', async () => {
  const { c, api, query, router } = mount({ assessment: assessment(RUN_A), unfinishedAttempt: TENTATIVA });

  c.openRun(RUN_ORFA);
  await Promise.resolve();
  await Promise.resolve();

  eq(router.navigations.at(-1)?.['run'], RUN_ORFA, 'o endereço passa a nomear a execução');
  eq(query.get('run'), RUN_ORFA, 'snapshot atualizado');
  ok(api.of('getById').length === 1, 'a tela relê a execução pedida (mudar só o link não bastaria)');
  reply(api.last('getById'), orfa());
  eq(c.assessment()?.id, RUN_ORFA, 'a execução aberta é a pedida');
  eq(c.unfinishedView(), true, 'e aparece como não finalizada');
});

// ---- (6) template real -------------------------------------------------------------------------

function templateOf(): TmplAstNode[] {
  const m = /template:\s*`([\s\S]*?)`,\n\s{2}styles:/.exec(SRC);
  if (!m) throw new Error('não foi possível isolar o template do componente');
  const parsed = parseTemplate(m[1], 'aegis-knight.component.html');
  if (parsed.errors?.length) throw new Error(`template inválido: ${parsed.errors[0].msg}`);
  return parsed.nodes;
}

function flatten(nodes: TmplAstNode[]): string {
  return JSON.stringify(nodes, (k, v) => (k === 'sourceSpan' || k === 'span' ? undefined : v));
}

test('o template do componente compila', () => {
  ok(templateOf().length > 0, 'o template tem nós');
});

test('o botão do aviso de tempo limite CONSULTA — não coleta e não promete recuperar', () => {
  const bloco = /@if \(timeoutNotice\(\); as tmsg\) \{[\s\S]*?\n {8}\}/.exec(SRC)?.[0] ?? '';
  ok(bloco !== '', 'o aviso de tempo limite existe no template');
  contains(bloco, 'consultLatestAfterTimeout()', 'o botão faz a consulta');
  contains(bloco, 'Consultar a última avaliação disponível', 'o rótulo diz o que o botão faz');
  ok(!/runDemo\(\)|runSource\(/.test(bloco), 'o aviso nunca oferece outra coleta');
  ok(!/Recuperar resultado gravado/.test(SRC), 'nenhum rótulo promete recuperar a tentativa');
});

test('o aviso da tentativa usa o texto condicional, abre por ID e fica fora do bloco da avaliação', () => {
  const bloco = /@if \(unfinishedAttempt\(\); as tent\) \{[\s\S]*?\n {8}\}/.exec(SRC)?.[0] ?? '';
  contains(bloco, 'unfinishedAttemptMessage()', 'o texto vem do computed condicional');
  contains(bloco, 'openRun(tent.id)', 'a ação abre a execução e relê a tela');
  ok(!/produziu veredito/.test(SRC), 'nenhum texto afirma ausência de veredito');
  ok(SRC.indexOf('@if (unfinishedAttempt(); as tent)') < SRC.indexOf('@if (assessment(); as a)'),
    'o aviso vem antes do bloco da avaliação — dentro dele sumiria sem resultado concluído');
});

test('execução não finalizada: sem publicação, com aviso próprio, e o detalhe sabe disso', () => {
  const pub = /@if \(assessment\(\); as pub\) \{[\s\S]*?Publicar relatório/.exec(SRC)?.[0] ?? '';
  contains(pub, '!unfinishedView()', 'a publicação só é oferecida para avaliação concluída');
  contains(SRC, 'Execução não finalizada.', 'aviso próprio da execução aberta por ID');
  contains(SRC, '[runFinalized]="!unfinishedView()"', 'o detalhe do achado recebe o estado');
  const nodes = flatten(templateOf());
  contains(nodes, 'unfinishedView', 'o estado está ligado no template');
});

test('estado vazio distingue "nenhuma executada" de "nenhuma concluída"', () => {
  contains(SRC, "unfinishedAttempt() ? 'Nenhuma avaliação concluída.' : 'Nenhuma avaliação executada ainda.'",
    'com tentativa registrada, a tela não diz que nada foi executado');
});

test('detalhe e plano não oferecem criação nem validação a partir de execução não finalizada', () => {
  const det = fs.readFileSync(
    path.join(process.cwd(), 'src/app/components/knight/finding-detail.component.ts'), 'utf8');
  const plan = fs.readFileSync(
    path.join(process.cwd(), 'src/app/components/knight/action-plan.component.ts'), 'utf8');
  contains(det, '} @else if (!runFinalized()) {', 'o resumo do achado não propõe criar plano');
  contains(det, '[runFinalized]="runFinalized()"', 'o estado chega ao painel do plano');
  contains(plan, '} @else if (!runFinalized()) {', 'o formulário de criação não aparece');
  contains(plan, 'this.runFinalized() && this.currentRunId() !== p.originRunId',
    'a execução não finalizada não serve de evidência de validação');
});

// ---- resultado ---------------------------------------------------------------------------------
void Promise.all(pending).then(() => {
  console.log(`\n${count - failures}/${count} verificações aprovadas`);
  if (failures > 0) process.exit(1);
});
