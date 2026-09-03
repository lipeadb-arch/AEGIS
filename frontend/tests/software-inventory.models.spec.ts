/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-01] Testes de LÓGICA PURA do inventário de software (frontend).
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node` — ver o comando no cabeçalho de execução do PR (mesmo padrão de connector.models.spec.ts).
 */
import { softwareCollectionStatePt } from '../src/app/models/software-inventory.models';

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

// ---- 1) cada estado do backend tem um rótulo pt-BR distinto e honesto --------------------------
test('softwareCollectionStatePt traduz cada estado do backend sem zero sintético', () => {
  eq(softwareCollectionStatePt('Available'), 'Disponível', 'Available é honesto');
  eq(softwareCollectionStatePt('Partial'), 'Parcial', 'Partial é distinto de Available');
  eq(softwareCollectionStatePt('InsufficientPermission'), 'Permissão insuficiente', 'menciona a permissão, não "erro genérico"');
  eq(softwareCollectionStatePt('Unsupported'), 'Licença/capacidade insuficiente', 'distingue licença de permissão');
  eq(softwareCollectionStatePt('Unavailable'), 'Indisponível', 'indisponibilidade não vira "disponível"');
});

// ---- 2) ausência/estado desconhecido nunca aparenta sucesso -------------------------------------
test('softwareCollectionStatePt trata ausência/estado desconhecido como "nunca coletado"', () => {
  eq(softwareCollectionStatePt(null), 'Nunca coletado', 'null não vira Disponível/vazio silencioso');
  eq(softwareCollectionStatePt(undefined), 'Nunca coletado', 'undefined não vira Disponível/vazio silencioso');
  eq(softwareCollectionStatePt(''), 'Nunca coletado', 'string vazia não vira Disponível');
  eq(softwareCollectionStatePt('NeverCollected'), 'Nunca coletado', 'o próprio enum do backend mapeia 1:1');
  eq(softwareCollectionStatePt('AlgumEstadoFuturoDesconhecido'), 'Nunca coletado',
    'um estado NÃO reconhecido cai no fallback honesto, nunca em Disponível');
});

// ---- 3) os cinco estados são todos MUTUAMENTE distintos (nenhum rótulo colidindo) ---------------
test('todos os rótulos de estado são distintos entre si', () => {
  const states = ['Available', 'Partial', 'InsufficientPermission', 'Unsupported', 'Unavailable', 'NeverCollected'];
  const labels = states.map((s) => softwareCollectionStatePt(s));
  eq(new Set(labels).size, labels.length, 'sem dois estados compartilhando o mesmo rótulo pt-BR');
});

console.log(`\n${count - failures}/${count} testes de lógica de inventário de software aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica de inventário de software falharam`);
