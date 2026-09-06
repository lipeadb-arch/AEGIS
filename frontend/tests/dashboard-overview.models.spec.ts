/**
 * [AEGIS-MVP-PRODUCT-01] Testes de LÓGICA PURA da leitura composta da tela inicial (frontend).
 *
 * Alvo: as duas correções da revisão do Codex sobre os CARTÕES.
 *   (2) recência honesta — `Available` prova que existe leitura, nunca que ela é recente. O rótulo antigo
 *       ("Leitura atual") afirmava frescor que o estado não sustenta;
 *   (3) `Undetermined` — inventário vazio não prova ausência de coleta, e o rótulo não pode dizer
 *       "ainda não coletado" quando a fonte já sincronizou.
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node`, no mesmo padrão de software-inventory.models.spec.ts.
 */
import {
  DashboardSignalState,
  STALE_AFTER_DAYS,
  metricFreshness,
  stateLabel,
} from '../src/app/models/dashboard-overview.models';

// ---- micro-harness (sem dependências externas) -------------------------------------------------
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
function ok(condition: boolean, msg: string): void {
  if (!condition) throw new Error(msg);
}

const GENERATED_AT = '2026-09-05T12:00:00.000Z';
/** Data local equivalente ao instante ISO — o rótulo formata no fuso do navegador, como o resto da tela. */
function day(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()}`;
}

console.log('dashboard-overview.models');

// ---- 1) disponível NÃO é sinônimo de atual -----------------------------------------------------
test('nenhum rótulo afirma que a leitura é "atual"', () => {
  const states: DashboardSignalState[] = ['NoSource', 'NeverCollected', 'Partial', 'Available', 'Undetermined'];
  for (const s of states)
    ok(!/atual/i.test(stateLabel(s)), `stateLabel('${s}') não pode afirmar atualidade: "${stateLabel(s)}"`);
});

test('leitura com data recente mostra a DATA, não uma afirmação de frescor', () => {
  const observedAt = '2026-09-04T09:00:00.000Z';
  const f = metricFreshness({ state: 'Available', observedAt }, GENERATED_AT);
  eq(f.stale, false, 'um dia de idade não é desatualizado');
  eq(f.dated, true, 'há data comprovada');
  eq(f.label, `Leitura de ${day(observedAt)}`, 'o rótulo carrega a data observada');
});

// ---- 2) leitura antiga é sinalizada pelo MESMO critério das fontes ------------------------------
test('leitura mais velha que o limiar aparece como desatualizada, com a idade', () => {
  const observedAt = '2026-08-20T12:00:00.000Z';   // 16 dias antes do generatedAt
  const f = metricFreshness({ state: 'Available', observedAt }, GENERATED_AT);
  eq(f.stale, true, `16 dias ultrapassa o limiar de ${STALE_AFTER_DAYS}`);
  ok(f.label.includes('desatualizada'), `o rótulo precisa dizer desatualizada: "${f.label}"`);
  ok(f.label.includes('16 dias'), `o rótulo precisa dizer a idade: "${f.label}"`);
});

test('o limiar do cartão é o MESMO do servidor — exatamente no limite já conta como antiga', () => {
  const observed = new Date(GENERATED_AT).getTime() - STALE_AFTER_DAYS * 86_400_000;
  const f = metricFreshness({ state: 'Available', observedAt: new Date(observed).toISOString() }, GENERATED_AT);
  eq(f.stale, true, 'no limiar exato a leitura já é antiga, como na lista de fontes');
});

// ---- 3) sem data, a tela não inventa recência --------------------------------------------------
test('leitura sem instante de observação não é apresentada como atual', () => {
  const f = metricFreshness({ state: 'Available', observedAt: null }, GENERATED_AT);
  eq(f.dated, false, 'não há data comprovada');
  eq(f.stale, false, 'sem data não se pode AFIRMAR que está desatualizada');
  ok(!/atual/i.test(f.label), `o rótulo não pode afirmar atualidade: "${f.label}"`);
  ok(f.label.includes('sem data de coleta'), `o rótulo diz o que falta: "${f.label}"`);
});

test('leitura PARCIAL continua marcada como parcial, com ou sem data', () => {
  const comData = metricFreshness({ state: 'Partial', observedAt: '2026-09-05T06:00:00.000Z' }, GENERATED_AT);
  ok(comData.label.startsWith('Leitura parcial'), `parcialidade preservada: "${comData.label}"`);
  const semData = metricFreshness({ state: 'Partial', observedAt: null }, GENERATED_AT);
  ok(semData.label.startsWith('Leitura parcial'), `parcialidade preservada sem data: "${semData.label}"`);
});

// ---- 4) inventário vazio ≠ ausência de coleta --------------------------------------------------
test('"coleta não comprovada" é um estado PRÓPRIO, distinto de "ainda não coletado"', () => {
  eq(stateLabel('Undetermined'), 'Coleta não comprovada', 'o rótulo descreve só o que está provado');
  ok(stateLabel('Undetermined') !== stateLabel('NeverCollected'),
    'afirmar "nunca coletado" quando a fonte já sincronizou seria uma afirmação sem prova');
  ok(stateLabel('Undetermined') !== stateLabel('NoSource'),
    'existe fonte — não é "sem fonte conectada"');
});

test('todos os cinco estados têm rótulos mutuamente distintos', () => {
  const states: DashboardSignalState[] = ['NoSource', 'NeverCollected', 'Partial', 'Available', 'Undetermined'];
  const labels = states.map(stateLabel);
  eq(new Set(labels).size, labels.length, 'sem dois estados compartilhando o mesmo rótulo');
});

test('estado sem leitura não ganha data nem marca de desatualização', () => {
  for (const s of ['NoSource', 'NeverCollected', 'Undetermined'] as DashboardSignalState[]) {
    const f = metricFreshness({ state: s, observedAt: '2026-01-01T00:00:00.000Z' }, GENERATED_AT);
    eq(f.label, stateLabel(s), `${s} mostra o estado, não uma data de leitura que não existe`);
    eq(f.stale, false, `${s} não é "desatualizado" — é ausência de leitura`);
    eq(f.dated, false, `${s} não tem data de leitura a exibir`);
  }
});

console.log(`\n${count - failures}/${count} testes passaram (dashboard-overview.models).`);
if (failures > 0) throw new Error(`${failures} teste(s) da leitura composta falharam`);
