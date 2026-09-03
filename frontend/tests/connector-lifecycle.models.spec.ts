/**
 * [AEGIS-MVP-ADMIN-LIFECYCLE-01] Testes de LÓGICA PURA do ciclo de vida do conector (frontend). Cobrem as
 * regras que NÃO dependem do DOM: o estado de conexão DERIVADO (conectado/desabilitado/desconectado) e os
 * gates de ação (testar/sincronizar), que espelham as recusas 409 do backend. Compilados por `tsc` e
 * executados por `node` — ver o comando `test:logic`.
 */
import {
  canSyncConnector,
  canTestConnector,
  connectionState,
  connectionStateLabel,
  connectionStateTone,
  ConnectorConfig,
} from '../src/app/models/connector.models';

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

function conn(over: Partial<ConnectorConfig>): ConnectorConfig {
  return {
    id: 'c1', provider: 'Microsoft', capability: 'SecureScore', displayName: 'Graph',
    authType: 'OAuthClientCredentials', enabled: true, syncIntervalMinutes: 360,
    lastSyncAt: null, lastStatus: 'Unknown', hasCredentials: true, hasIngestionKey: false, ...over,
  };
}

// ---- 1) conectado: habilitado + credencial -----------------------------------
test('conector habilitado com credencial está CONECTADO', () => {
  const c = conn({ enabled: true, hasCredentials: true });
  eq(connectionState(c), 'connected', 'estado');
  eq(connectionStateLabel('connected'), 'Conectado', 'rótulo');
  eq(connectionStateTone('connected'), 'ok', 'tom');
  assert(canTestConnector(c), 'conectado pode testar');
  assert(canSyncConnector(c), 'conectado pode sincronizar');
});

// ---- 2) desabilitado: credencial preservada, coleta pausada ------------------
test('conector desabilitado (credencial preservada) NÃO sincroniza, mas pode testar', () => {
  const c = conn({ enabled: false, hasCredentials: true });
  eq(connectionState(c), 'disabled', 'estado');
  eq(connectionStateLabel('disabled'), 'Desabilitado', 'rótulo');
  eq(connectionStateTone('disabled'), 'warn', 'tom');
  assert(canTestConnector(c), 'desabilitado ainda tem credencial → pode testar');
  assert(!canSyncConnector(c), 'desabilitado NÃO sincroniza (coleta pausada)');
});

// ---- 3) desconectado: sem credencial -----------------------------------------
test('conector sem credencial está DESCONECTADO e não testa nem sincroniza', () => {
  const c = conn({ enabled: false, hasCredentials: false, hasIngestionKey: false });
  eq(connectionState(c), 'disconnected', 'estado');
  eq(connectionStateLabel('disconnected'), 'Desconectado', 'rótulo');
  eq(connectionStateTone('disconnected'), 'idle', 'tom');
  assert(!canTestConnector(c), 'desconectado não pode testar');
  assert(!canSyncConnector(c), 'desconectado não pode sincronizar');
});

// ---- 4) push com chave de ingestão conta como credencial ---------------------
test('push com chave de ingestão (sem hasCredentials) é conexão válida', () => {
  const c = conn({ provider: 'Generic', capability: 'Siem', enabled: true, hasCredentials: false, hasIngestionKey: true });
  eq(connectionState(c), 'connected', 'a chave de ingestão conta como credencial');
  assert(canTestConnector(c), 'push com chave pode testar (prontidão)');

  const semChave = conn({ provider: 'Generic', capability: 'Siem', enabled: true, hasCredentials: false, hasIngestionKey: false });
  eq(connectionState(semChave), 'disconnected', 'push sem chave está desconectado');
});

// ---- 5) o estado ATUAL não depende do lastStatus histórico -------------------
test('desconectado NUNCA aparece como operacional mesmo com lastStatus=Healthy antigo', () => {
  // Evidência histórica (última coleta) diz Healthy, mas a credencial foi eliminada: estado atual = desconectado.
  const c = conn({ enabled: false, hasCredentials: false, hasIngestionKey: false, lastStatus: 'Healthy' });
  eq(connectionState(c), 'disconnected', 'o estado atual deriva da credencial, não do lastStatus histórico');
  assert(!canSyncConnector(c), 'não sincroniza mesmo com coleta antiga bem-sucedida');
});

console.log(`\n${count - failures}/${count} testes de ciclo de vida do conector aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de ciclo de vida do conector falharam`);
