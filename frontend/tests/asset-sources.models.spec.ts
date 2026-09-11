/**
 * [AEGIS-ENTITY-RESOLUTION-01] Testes de LÓGICA PURA da apresentação das fontes de um ativo (frontend).
 *
 * O que está sendo provado é comportamento: o estado vem do backend e não é reinterpretado; ausência de resumo não
 * vira "0 fontes"; falta de evidência NÃO é apresentada como conflito; só o vínculo confirmado recebe o aviso de que
 * vínculo não é segurança; e fontes desconhecidas não recebem nome inventado. Compiladas por `tsc`, executadas por `node`.
 */
import {
  AssetSourceDiagnostics,
  AssetSourceSummary,
  LINK_MEANING_SHORT,
  conflictObservation,
  contradictoryObservedIds,
  crossSourceTone,
  joinSourceFacts,
  recordResolutionTone,
  shortSourceLabel,
  sourcesCell,
} from '../src/app/models/asset.models';

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

function summary(over: Partial<AssetSourceSummary>): AssetSourceSummary {
  return {
    activeSourceCount: 0,
    activeSources: [],
    sourceRecords: 0,
    crossSourceState: 'notLinked',
    crossSourceLabel: 'Ainda sem vínculo entre fontes',
    lastObservedAt: null,
    ...over,
  };
}

console.log('asset-sources.models');

test('vínculo confirmado: as duas fontes numa célula, rótulo do backend e aviso de que vínculo não é segurança', () => {
  const cell = sourcesCell(
    summary({
      activeSourceCount: 2,
      activeSources: ['Microsoft Defender Vulnerability Management', 'Microsoft Intune'],
      sourceRecords: 2,
      crossSourceState: 'linked',
      crossSourceLabel: 'Vínculo confirmado entre fontes',
    }),
  );
  eq(cell.text, 'Defender + Intune', 'um ativo, as duas fontes identificadas');
  eq(cell.badge, 'Vínculo confirmado entre fontes', 'o texto é o do backend');
  eq(cell.tone, 'ok', 'tom do vínculo');
  eq(cell.title, LINK_MEANING_SHORT, 'aviso de que vínculo não é segurança');
  eq(LINK_MEANING_SHORT.includes('Não confirma a segurança'), true, 'o aviso diz o que o vínculo NÃO confirma');
});

test('sem resumo da API: desconhecido ("—"), nunca "0 fontes"', () => {
  const cell = sourcesCell(undefined);
  eq(cell.text, '—', 'sem resposta não vira zero');
  eq(cell.badge, null, 'sem selo inventado');
  eq(sourcesCell(null).text, '—', 'nulo idem');
});

test('ativo sem fonte integrada é dito como tal, sem selo de vínculo', () => {
  const cell = sourcesCell(summary({ crossSourceState: 'noSource', crossSourceLabel: 'Sem fonte integrada' }));
  eq(cell.text, 'Sem fonte integrada', 'cadastro manual');
  eq(cell.badge, null, 'nada a vincular');
});

test('falta de evidência não é conflito: tom neutro; conflito é aviso', () => {
  eq(crossSourceTone('notLinked'), 'muted', 'sem identificador ≠ conflito');
  eq(crossSourceTone('notEvaluated'), 'muted', 'não avaliado ≠ conflito');
  eq(crossSourceTone('conflict'), 'warn', 'contradição é aviso');
  eq(crossSourceTone('identifierOnly'), 'info', 'uma fonte com identificador');
  eq(recordResolutionTone('NoIdentifier'), 'muted', 'registro sem identificador');
  eq(recordResolutionTone('InvalidIdentifier'), 'muted', 'identificador recusado');
  eq(recordResolutionTone('Conflict'), 'warn', 'registro em conflito');
  eq(recordResolutionTone('Linked'), 'ok', 'registro vinculado');
});

test('só o vínculo confirmado leva o aviso; conflito e ausência não recebem o selo de confirmação', () => {
  const conflict = sourcesCell(
    summary({ activeSourceCount: 1, activeSources: ['Microsoft Intune'], crossSourceState: 'conflict', crossSourceLabel: 'Conflito de vínculo preservado' }),
  );
  eq(conflict.title, null, 'sem aviso de vínculo confirmado');
  eq(conflict.badge!.includes('confirmado'), false, 'conflito nunca é chamado de confirmado');
  const noLink = sourcesCell(summary({ activeSourceCount: 1, activeSources: ['Microsoft Intune'] }));
  eq(noLink.tone, 'muted', 'ausência de vínculo é neutra');
});

test('fonte presente em nenhuma leitura atual: dito explicitamente, sem lista vazia', () => {
  const cell = sourcesCell(summary({ activeSourceCount: 0, activeSources: [], sourceRecords: 2, crossSourceState: 'identifierOnly', crossSourceLabel: 'x' }));
  eq(cell.text, 'Nenhuma fonte na última leitura', 'registros existem, mas nenhum presente');
});

test('nomes curtos só para fontes conhecidas; o resto passa como veio', () => {
  eq(shortSourceLabel('Microsoft Defender Vulnerability Management'), 'Defender', 'Defender');
  eq(shortSourceLabel('Microsoft Intune'), 'Intune', 'Intune');
  eq(shortSourceLabel('Scanner Sintético'), 'Scanner Sintético', 'desconhecida não é renomeada');
});

test('fatos da fonte ignoram ausentes', () => {
  eq(joinSourceFacts('Conforme, segundo a fonte', null, undefined, ' '), 'Conforme, segundo a fonte', 'só o que existe');
  eq(joinSourceFacts(null, null), '', 'nada a dizer');
});

function diag(over: Partial<AssetSourceDiagnostics>): AssetSourceDiagnostics {
  return {
    externalId: 'maq-sintetica-01',
    directoryNamespace: 'dir-a',
    directoryDeviceId: 'id-x',
    conflictDirectoryDeviceId: null,
    conflictDirectoryNamespace: null,
    conflictObservedDeviceIds: [],
    linkedAt: null,
    resolutionEvaluatedAt: null,
    ...over,
  };
}

test('conflito: o par observado (diretório, identificador) aparece à parte do vínculo estabelecido', () => {
  const changedDir = conflictObservation(diag({ conflictDirectoryNamespace: 'dir-b', conflictDirectoryDeviceId: 'id-x' }));
  eq(changedDir!.directory, 'dir-b', 'diretório observado, não o do vínculo');
  eq(changedDir!.identifier, 'id-x', 'identificador observado');
  eq(changedDir!.directoryRecorded, true, 'diretório registrado');
  const both = conflictObservation(diag({ conflictDirectoryNamespace: 'dir-b', conflictDirectoryDeviceId: 'id-y' }));
  eq(`${both!.directory}|${both!.identifier}`, 'dir-b|id-y', 'diretório e identificador mudaram: o par inteiro');
});

test('conflito legado: diretório não registrado é dito como tal, nunca herdado do vínculo', () => {
  const legacy = conflictObservation(diag({ conflictDirectoryNamespace: null, conflictDirectoryDeviceId: 'id-y' }));
  eq(legacy!.directory, 'não registrado', 'não inventa o diretório da observação');
  eq(legacy!.directoryRecorded, false, 'marcado como não registrado');
  eq(legacy!.directory === 'dir-a', false, 'o diretório do vínculo não é reaproveitado');
});

test('contradição na mesma coleta: lista de identificadores, sem par inventado; sem diagnóstico, nada', () => {
  const d = diag({ conflictDirectoryNamespace: 'dir-a', conflictObservedDeviceIds: ['id-x', 'id-y'] });
  eq(conflictObservation(d), null, 'contradição não vira par observado');
  eq(contradictoryObservedIds(d).join(','), 'id-x,id-y', 'os dois valores, na ordem do backend');
  eq(conflictObservation(diag({})), null, 'sem conflito, sem par');
  eq(contradictoryObservedIds(diag({})).length, 0, 'sem contradição, lista vazia');
  eq(conflictObservation(null), null, 'papel sem diagnóstico: nada');
  eq(contradictoryObservedIds(undefined).length, 0, 'papel sem diagnóstico: nada');
  const oldApi = { ...diag({}), conflictObservedDeviceIds: undefined } as unknown as AssetSourceDiagnostics;
  eq(contradictoryObservedIds(oldApi).length, 0, 'campo ausente não quebra a tela');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend (asset-sources) aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend (asset-sources) falharam`);
