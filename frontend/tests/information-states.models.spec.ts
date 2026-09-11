/**
 * [AEGIS-LANGUAGE-STATES-01] Testes de LÓGICA PURA dos estados de informação das fontes (frontend).
 *
 * O que está sendo provado é comportamento, não frase: ausência de coleta não vira zero; "sem integração",
 * "sem coleta" e "primeira tentativa falhou" são estados distintos; uma tentativa recente falha não esconde a
 * última leitura válida; coleta parcial não vira conclusão sobre o ambiente inteiro; e o ciclo de vida
 * "Resolved" do coletor não é apresentado como correção validada. Compiladas por `tsc`, executadas por `node`.
 */
import {
  PostureExposureSummary,
  recommendationLifecyclePt,
  recommendationReading,
  sourceStatePt,
} from '../src/app/models/posture-exposure.models';
import {
  VulnerabilitySummary,
  vulnerabilityLifecyclePt,
  vulnerabilityReading,
} from '../src/app/models/vulnerability.models';
import {
  SoftwareInventorySource,
  SoftwareInventorySummary,
  softwareLifecyclePt,
  softwareReading,
} from '../src/app/models/software-inventory.models';

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
function assert(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}
function eq<T>(actual: T, expected: T, msg: string): void {
  if (actual !== expected) throw new Error(`${msg}: esperado ${String(expected)}, obtido ${String(actual)}`);
}

// ---- fábricas ----------------------------------------------------------------------------------
function recSummary(over: Partial<PostureExposureSummary>): PostureExposureSummary {
  return {
    sourceLabel: 'Microsoft Secure Score',
    totalOpen: 0,
    totalResolved: 0,
    openByCategory: [],
    lastCollectedAt: null,
    latestSecureScorePercent: null,
    latestSecureScoreAt: null,
    sourceConfigured: false,
    lastAttemptStatus: null,
    ...over,
  };
}

function vulnSummary(over: Partial<VulnerabilitySummary>): VulnerabilitySummary {
  return {
    totalOpen: 0,
    totalResolved: 0,
    distinctCvesOpen: 0,
    affectedAssetsOpen: 0,
    openBySeverity: [],
    sources: [],
    lastCollectedAt: null,
    neverCollected: true,
    ...over,
  };
}

function swSource(over: Partial<SoftwareInventorySource>): SoftwareInventorySource {
  return {
    connectorConfigId: 'c1',
    provider: 'Microsoft',
    displayName: 'Defender',
    collectionState: 'Available',
    lastAttemptState: 'Available',
    lastAttemptAt: '2026-09-10T10:00:00Z',
    lastCollectionAt: '2026-09-10T10:00:00Z',
    lastAttemptDetail: null,
    ...over,
  };
}

function swSummary(over: Partial<SoftwareInventorySummary>): SoftwareInventorySummary {
  return {
    totalProducts: 0,
    productsWithWeaknesses: 0,
    productsWithPublicExploit: 0,
    productsWithActiveAlert: 0,
    exposedInstallations: 0,
    sources: [],
    lastCollectedAt: null,
    neverCollected: true,
    ...over,
  };
}

// ---- Recomendações de postura ------------------------------------------------------------------
console.log('recommendationReading');

test('sem integração, sem coleta e primeira falha são estados distintos — nenhum tem números', () => {
  const none = recommendationReading(recSummary({}));
  const pending = recommendationReading(recSummary({ sourceConfigured: true, lastAttemptStatus: 'Unknown' }));
  const failedFirst = recommendationReading(recSummary({ sourceConfigured: true, lastAttemptStatus: 'Failed' }));
  eq(none.state, 'NotConfigured', 'sem conector');
  eq(pending.state, 'NeverCollected', 'configurado sem coleta');
  eq(failedFirst.state, 'FailedBeforeFirstCollection', 'primeira tentativa falhou');
  assert(!none.hasData && !pending.hasData && !failedFirst.hasData, 'ausência de coleta NUNCA libera contagem (0)');
  assert(new Set([none.notice, pending.notice, failedFirst.notice]).size === 3, 'cada estado explica o próprio vazio');
});

test('tentativa recente falha preserva a última leitura e exige aviso', () => {
  const r = recommendationReading(
    recSummary({ sourceConfigured: true, lastAttemptStatus: 'Failed', lastCollectedAt: '2026-09-08T12:00:00Z', totalOpen: 7 }),
  );
  eq(r.state, 'Available', 'o dado anterior válido continua disponível');
  assert(r.hasData, 'os números continuam visíveis');
  assert(r.lastAttemptFailed, 'a falha é sinalizada');
  assert(!!r.notice, 'o aviso acompanha os números');
});

test('zero apurado numa coleta íntegra é leitura real, sem aviso', () => {
  const r = recommendationReading(
    recSummary({ sourceConfigured: true, lastAttemptStatus: 'Healthy', lastCollectedAt: '2026-09-10T08:00:00Z' }),
  );
  assert(r.hasData, 'coletado sem pendência é leitura (0), não ausência');
  eq(r.notice, null, 'coleta íntegra não carrega ressalva');
});

test('coleta com restrições (Degraded) mantém os números e traz a ressalva — sem afirmar população parcial', () => {
  const r = recommendationReading(
    recSummary({ sourceConfigured: true, lastAttemptStatus: 'Degraded', lastCollectedAt: '2026-09-10T08:00:00Z', totalOpen: 4 }),
  );
  eq(r.state, 'Available', 'dado disponível continua disponível');
  assert(r.hasData, 'os números continuam visíveis');
  assert(r.lastAttemptDegraded && !r.lastAttemptFailed, 'restrição é sinalizada e não é falha');
  assert(!!r.notice && r.notice.includes('restrições'), 'a ressalva do backend chega à tela');
  assert(!/parcia/i.test(r.notice!), 'Degraded não é apresentado como coleta parcial das recomendações');
});

test('contrato antigo sem sourceConfigured não afirma "sem integração"', () => {
  const legacy = recSummary({});
  delete legacy.sourceConfigured;
  eq(recommendationReading(legacy).state, 'NeverCollected', 'sem prova, não se afirma ausência de integração');
});

test('"Resolved" não é apresentado como resolvido; estado da fonte não é traduzido por suposição', () => {
  eq(recommendationLifecyclePt('Resolved'), 'Sem pendência na fonte', 'ciclo de vida do coletor');
  eq(recommendationLifecyclePt('Open'), 'Pendente', 'pendente');
  eq(sourceStatePt('Default'), null, 'Default não vira selo');
  eq(sourceStatePt('ThirdParty'), 'Atendida por terceiro (declarado na fonte)', 'estado conhecido');
  eq(sourceStatePt('Custom X'), 'Estado na fonte: Custom X', 'desconhecido passa identificado como da fonte');
});

// ---- Vulnerabilidades --------------------------------------------------------------------------
console.log('vulnerabilityReading');

test('sem fonte × fonte sem coleta — ambos sem números', () => {
  const none = vulnerabilityReading(vulnSummary({}));
  const pending = vulnerabilityReading(
    vulnSummary({ sources: [{ connectorConfigId: 'a', provider: 'Microsoft', displayName: 'Defender', lastSyncAt: null, status: 'Unknown' }] }),
  );
  eq(none.state, 'NoSource', 'nenhum scanner configurado');
  eq(pending.state, 'NeverCollected', 'scanner sem coleta');
  assert(!none.hasData && !pending.hasData, 'nenhum dos dois pode mostrar 0');
});

test('coleta parcial entre fontes: zero vem com ressalva de escopo', () => {
  const r = vulnerabilityReading(
    vulnSummary({
      neverCollected: false,
      lastCollectedAt: '2026-09-10T09:00:00Z',
      sources: [
        { connectorConfigId: 'a', provider: 'Microsoft', displayName: 'Defender', lastSyncAt: '2026-09-10T09:00:00Z', status: 'Healthy' },
        { connectorConfigId: 'b', provider: 'Google', displayName: 'VM Manager', lastSyncAt: null, status: 'Unknown' },
      ],
    }),
  );
  assert(r.hasData, 'o zero das fontes coletadas é leitura real');
  assert(!!r.notice && r.notice.includes('1 fonte(s)'), 'o zero parcial não pode virar conclusão sobre o ambiente inteiro');
});

test('falha antes da primeira leitura é dita — e é distinta de "ainda sem coleta"', () => {
  const src = (status: string) => ({ connectorConfigId: 'a', provider: 'Microsoft', displayName: 'Defender', lastSyncAt: null, status });
  const failedFirst = vulnerabilityReading(vulnSummary({ sources: [src('Failed')] }));
  const waiting = vulnerabilityReading(vulnSummary({ sources: [src('Unknown')] }));
  eq(failedFirst.state, 'NeverCollected', 'sem leitura');
  assert(!failedFirst.hasData, 'falha antes da primeira leitura não vira 0');
  assert(!!failedFirst.notice && failedFirst.notice.includes('falhou'), 'a causa conhecida aparece');
  assert(failedFirst.notice !== waiting.notice, 'falha e espera não são a mesma frase');
});

test('fonte com coleta concluída sob restrições: números mantidos, ressalva neutra', () => {
  const r = vulnerabilityReading(
    vulnSummary({
      neverCollected: false,
      lastCollectedAt: '2026-09-10T09:00:00Z',
      sources: [{ connectorConfigId: 'a', provider: 'Microsoft', displayName: 'Defender', lastSyncAt: '2026-09-10T09:00:00Z', status: 'Degraded' }],
    }),
  );
  assert(r.hasData, 'dados disponíveis continuam visíveis');
  assert(!!r.notice && r.notice.includes('restrições'), 'a restrição é dita');
});

test('ciclo de vida "Resolved" = não mais reportada', () => {
  eq(vulnerabilityLifecyclePt('Resolved'), 'Não mais reportada', 'não é correção validada');
});

// ---- Inventário de software --------------------------------------------------------------------
console.log('softwareReading');

test('coleta parcial é contagem parcial dos observados, e tentativa sem sucesso preserva os dados com aviso', () => {
  const partial = softwareReading(swSummary({ neverCollected: false, sources: [swSource({ collectionState: 'Partial', lastAttemptState: 'Partial' })] }));
  assert(partial.hasData && !!partial.notice && partial.notice.includes('contagem parcial'), 'parcial não é o inventário inteiro');
  assert(!partial.notice!.includes('piso'), 'não sugere limite inferior do inventário atual');

  const stale = softwareReading(swSummary({ neverCollected: false, sources: [swSource({ lastAttemptState: 'Unavailable' })] }));
  assert(stale.hasData, 'dados preservados continuam visíveis');
  assert(!!stale.notice && stale.notice.includes('última leitura'), 'a tentativa sem sucesso é dita');

  const clean = softwareReading(swSummary({ neverCollected: false, sources: [swSource({})] }));
  eq(clean.notice, null, 'coleta íntegra sem ressalva');
});

test('nunca coletado com permissão ausente explica o motivo, sem números', () => {
  const r = softwareReading(swSummary({ sources: [swSource({ collectionState: 'NeverCollected', lastAttemptState: 'InsufficientPermission', lastCollectionAt: null })] }));
  assert(!r.hasData, 'sem leitura');
  assert(!!r.notice && r.notice.includes('permissão insuficiente'), 'o motivo real aparece');
  eq(softwareLifecyclePt('Resolved'), 'Não mais observado', 'não é remoção validada');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend (information-states) aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend (information-states) falharam`);
