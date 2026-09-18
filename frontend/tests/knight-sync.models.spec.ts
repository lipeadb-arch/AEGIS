/**
 * [AEGIS-KNIGHT-MULTICLOUD-01] Testes de LÓGICA PURA do acompanhamento da sincronização em Integrações.
 *
 * Fixam: (1) só o pedido acompanhado, do mesmo tenant e conector, escreve na tela; (2) pedido já ativo é
 * acompanhado, não duplicado; (3) coleta parcial não aparece como sucesso pleno; (4) falha nunca apresenta a
 * avaliação anterior como resultado do pedido que falhou.
 */
import { KnightSyncRequest, isActiveSync, isCurrentSyncResponse, syncView } from '../src/app/models/knight-sync.models';

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

function req(over: Partial<KnightSyncRequest> = {}): KnightSyncRequest {
  return {
    id: 'req-1',
    connectorId: 'conn-1',
    source: 'MicrosoftEntraId',
    status: 'Pending',
    requestedAt: '2026-09-18T10:00:00Z',
    startedAt: null,
    completedAt: null,
    attempts: 0,
    runId: null,
    resultSourceState: null,
    failureCategory: null,
    message: null,
    lastCompletedRunId: null,
    lastCompletedAt: null,
    alreadyActive: false,
    ...over,
  };
}

const W = { tenantId: 'tenant-a', connectorId: 'conn-1', requestId: 'req-1' };

test('pendente e em execução são ativos; concluído e falho não', () => {
  ok(isActiveSync(req({ status: 'Pending' })) && isActiveSync(req({ status: 'Running' })), 'ativos');
  ok(!isActiveSync(req({ status: 'Completed' })) && !isActiveSync(req({ status: 'Failed' })), 'terminais');
  ok(!isActiveSync(null) && !isActiveSync(undefined), 'ausente');
});

test('resposta só vale para o mesmo tenant, conector e pedido acompanhados', () => {
  ok(isCurrentSyncResponse(W, 'tenant-a', req()), 'mesmo contexto');
  ok(!isCurrentSyncResponse(W, 'tenant-b', req()), 'troca de tenant descarta resposta atrasada');
  ok(!isCurrentSyncResponse(W, 'tenant-a', req({ id: 'req-0' })), 'pedido anterior não sobrescreve o atual');
  ok(!isCurrentSyncResponse(W, 'tenant-a', req({ connectorId: 'conn-2' })), 'outro conector');
  ok(!isCurrentSyncResponse(null, 'tenant-a', req()), 'nada acompanhado');
});

test('tenant desconhecido (null) nunca aceita resposta, mesmo se ambos forem null', () => {
  ok(!isCurrentSyncResponse({ ...W, tenantId: null }, null, req()), 'null === null não autoriza');
  ok(!isCurrentSyncResponse(W, null, req()), 'tenant atual ausente');
});

test('pedido já ativo é acompanhado, sem prometer nova coleta', () => {
  const v = syncView(req({ alreadyActive: true }));
  eq(v.tone, 'busy', 'em andamento');
  ok(v.title.startsWith('Já havia uma sincronização'), 'diz que reaproveitou o pedido');
  ok((v.detail ?? '').includes('Nenhuma coleta duplicada'), 'sem duplicação');
  eq(v.runId, null, 'sem avaliação ainda');
});

test('em execução mostra tentativa quando houve retomada', () => {
  const v = syncView(req({ status: 'Running', startedAt: '2026-09-18T10:00:05Z', attempts: 2 }));
  ok((v.detail ?? '').includes('tentativa 2'), 'tentativa');
  eq(syncView(req({ status: 'Running', attempts: 1 })).detail?.includes('tentativa'), false, 'primeira tentativa não é destacada');
});

test('em execução COM avaliação vinculada oferece o resultado, sem dizer que acabou', () => {
  const v = syncView(req({ status: 'Running', runId: 'run-9', attempts: 1 }));
  eq(v.tone, 'busy', 'o pedido ainda não foi finalizado');
  eq(v.runId, 'run-9', 'o resultado determinístico já gravado pode ser aberto');
  ok(v.title.includes('finalizando'), 'diz que falta finalizar');
  ok(isActiveSync(req({ status: 'Running', runId: 'run-9' })), 'o acompanhamento continua até o desfecho');
});

test('concluído íntegro leva à avaliação produzida por ESTE pedido', () => {
  const v = syncView(req({ status: 'Completed', completedAt: '2026-09-18T10:02:00Z', runId: 'run-9', resultSourceState: 'Completed' }));
  eq(v.tone, 'ok', 'sucesso');
  eq(v.runId, 'run-9', 'link direto');
  eq(v.detail, null, 'sem ressalva');
  eq(v.previousRunId, null, 'sem anterior');
});

test('concluído com coleta parcial é alerta, não sucesso pleno', () => {
  const v = syncView(req({ status: 'Completed', runId: 'run-9', resultSourceState: 'PartialCollection' }));
  eq(v.tone, 'warn', 'alerta');
  ok(v.title.includes('coleta parcial'), 'estado da fonte');
  ok((v.detail ?? '').includes('nunca aprovados'), 'não avaliado ≠ aprovado');
  eq(v.runId, 'run-9', 'a avaliação parcial existe e é acessível');
});

test('falha mantém a avaliação anterior como anterior, nunca como resultado', () => {
  const v = syncView(req({ status: 'Failed', message: 'Credencial inválida.', lastCompletedRunId: 'run-5', lastCompletedAt: '2026-09-17T09:00:00Z' }));
  eq(v.tone, 'err', 'erro');
  eq(v.title, 'Credencial inválida.', 'mensagem do servidor');
  eq(v.runId, null, 'falha não tem resultado');
  eq(v.previousRunId, 'run-5', 'anterior segue disponível');
  ok((v.detail ?? '').includes('Nenhuma avaliação nova'), 'declara que nada novo foi registrado');
});

test('falha sem avaliação anterior não oferece link', () => {
  const v = syncView(req({ status: 'Failed' }));
  eq(v.previousRunId, null, 'sem anterior');
  eq(v.title, 'A sincronização não foi concluída.', 'mensagem padrão');
  eq(v.detail, 'Nenhuma avaliação foi registrada.', 'detalhe');
});

test('falha cuja última concluída é a própria execução não a oferece como anterior', () => {
  eq(syncView(req({ status: 'Failed', runId: 'run-5', lastCompletedRunId: 'run-5' })).previousRunId, null, 'mesma execução');
});

test('falha com avaliação vinculada nunca afirma que nada foi registrado', () => {
  const v = syncView(req({ status: 'Failed', runId: 'run-5', message: 'x' }));
  eq(v.runId, 'run-5', 'a avaliação do pedido é oferecida');
  ok(!(v.detail ?? '').includes('Nenhuma avaliação'), 'sem afirmação falsa');
});

console.log(`\n${count - failures}/${count} testes passaram (knight-sync.models).`);
if (failures > 0) throw new Error(`${failures} teste(s) da sincronização do KNIGHT falharam`);
