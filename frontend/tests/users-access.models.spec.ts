/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Testes de LÓGICA PURA da linguagem de acesso de usuário (frontend): "usuário
 * ativo" versus "acesso removido" — a operação segura remove o ACESSO ao ambiente, nunca apaga a identidade.
 * Também reafirma a regra do botão de redefinição de senha. Sem DOM — ver `test:logic`.
 */
import {
  accessStateLabel,
  accessStateTone,
  canResetLocalPassword,
} from '../src/app/models/users.models';

// ---- micro-harness -----------------------------------------------------------
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
function assert(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}
function eq<T>(actual: T, expected: T, msg: string): void {
  if (actual !== expected) throw new Error(`${msg}: esperado ${String(expected)}, obtido ${String(actual)}`);
}

// ---- 1) ativo vs acesso removido (nunca "excluído"/"inativo") ----------------
test('ativo e acesso removido usam a linguagem de acesso, não de exclusão', () => {
  eq(accessStateLabel(true), 'Ativo', 'ativo');
  eq(accessStateLabel(false), 'Acesso removido', 'removido — nunca "Excluído"');
  assert(accessStateLabel(false) !== 'Inativo', 'evita "Inativo", que soa a apagamento');
  assert(!/exclu/i.test(accessStateLabel(false)), 'não sugere exclusão da identidade');
  eq(accessStateTone(true), 'on', 'tom ativo');
  eq(accessStateTone(false), 'off', 'tom removido');
});

// ---- 2) redefinição de senha: regra preservada -------------------------------
test('redefinir senha só para PlatformAdmin, alvo com credencial local e nunca a própria conta', () => {
  assert(
    canResetLocalPassword({ viewerIsPlatformAdmin: true, targetHasLocalCredential: true, targetIsSelf: false }),
    'PlatformAdmin redefine credencial local de outra pessoa',
  );
  assert(
    !canResetLocalPassword({ viewerIsPlatformAdmin: false, targetHasLocalCredential: true, targetIsSelf: false }),
    'sem autoridade global, não redefine',
  );
  assert(
    !canResetLocalPassword({ viewerIsPlatformAdmin: true, targetHasLocalCredential: false, targetIsSelf: false }),
    'conta federada não tem senha local a redefinir',
  );
  assert(
    !canResetLocalPassword({ viewerIsPlatformAdmin: true, targetHasLocalCredential: true, targetIsSelf: true }),
    'a própria conta usa a troca normal (exige a senha atual)',
  );
});

console.log(`\n${count - failures}/${count} testes de linguagem de acesso aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de linguagem de acesso falharam`);
