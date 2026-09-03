/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Testes de LÓGICA PURA da administração de tenants (frontend): rótulos de
 * estado e as ações PERMITIDAS por estado (suspender/reativar). Sem DOM — compilados por `tsc`, executados
 * por `node` (ver `test:logic`).
 */
import {
  RenameTenantRequest,
  TenantAdmin,
  canReactivate,
  canSuspend,
  tenantStatusLabel,
  tenantStatusTone,
} from '../src/app/models/tenant-admin.models';

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

function tenant(over: Partial<TenantAdmin>): TenantAdmin {
  return { id: 't1', name: 'Cliente', slug: 'cliente', status: 'Active', createdAt: '2026-01-01', updatedAt: null, ...over };
}

// ---- 1) rótulos e tom de cada estado -----------------------------------------
test('rótulos e tom por estado', () => {
  eq(tenantStatusLabel('Active'), 'Ativo', 'Active');
  eq(tenantStatusLabel('Onboarding'), 'Em implantação', 'Onboarding');
  eq(tenantStatusLabel('Suspended'), 'Suspenso', 'Suspended');
  eq(tenantStatusTone('Active'), 'ok', 'tom Active');
  eq(tenantStatusTone('Suspended'), 'warn', 'tom Suspended');
  eq(tenantStatusTone('Onboarding'), 'idle', 'tom Onboarding');
});

// ---- 2) ações permitidas por estado ------------------------------------------
test('suspender vale para Active/Onboarding; reativar só para Suspended', () => {
  assert(canSuspend(tenant({ status: 'Active' })), 'Active pode ser suspenso');
  assert(canSuspend(tenant({ status: 'Onboarding' })), 'Onboarding pode ser suspenso');
  assert(!canSuspend(tenant({ status: 'Suspended' })), 'Suspended não é suspenso de novo');

  assert(canReactivate(tenant({ status: 'Suspended' })), 'Suspended pode ser reativado');
  assert(!canReactivate(tenant({ status: 'Active' })), 'Active não é reativado');
  assert(!canReactivate(tenant({ status: 'Onboarding' })), 'Onboarding não é reativado');
});

// ---- 3) as ações são mutuamente exclusivas por estado ------------------------
test('cada estado oferece no máximo uma das duas ações de estado', () => {
  for (const status of ['Active', 'Onboarding', 'Suspended'] as const) {
    const t = tenant({ status });
    assert(!(canSuspend(t) && canReactivate(t)), `${status} não oferece suspender E reativar ao mesmo tempo`);
  }
});

// ---- 4) a renomeação carrega SÓ o nome (slug imutável, fora do contrato) ------
test('RenameTenantRequest carrega apenas o nome — o slug não trafega', () => {
  const body: RenameTenantRequest = { name: 'Novo Nome' };
  const keys = Object.keys(body);
  eq(keys.length, 1, 'um único campo');
  eq(keys[0], 'name', 'só o nome');
  assert(!keys.includes('slug'), 'o slug é imutável e não entra no corpo da renomeação');
});

console.log(`\n${count - failures}/${count} testes de administração de tenants aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de administração de tenants falharam`);
