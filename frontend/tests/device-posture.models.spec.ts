/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-02] Testes de LÓGICA PURA da postura de dispositivos (frontend).
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node` (ver `npm run test:logic`), mesmo padrão de connector.models.spec.ts.
 *
 * O que estes testes travam: a tela NUNCA transforma "não coletado / não autorizado" em zero, NUNCA conta
 * atribuição desconhecida como "sem atribuição", e os filtros operam sobre grupos AGREGADOS (sem identificador
 * de dispositivo).
 */
import {
  DeviceGroup,
  DevicePolicy,
  DevicePostureDimension,
  EMPTY_DEVICE_FILTERS,
  canShowNumbers,
  countOrDash,
  dimensionStatePt,
  filterDeviceGroups,
  operatingSystems,
  totalDevices,
  unassignedPolicies,
  unknownAssignmentPolicies,
} from '../src/app/models/device-posture.models';

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
function assert(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}

// ---- fixtures ----------------------------------------------------------------------------------
function group(
  os: string,
  compliance: DeviceGroup['compliance'],
  encryption: DeviceGroup['encryption'],
  activity: DeviceGroup['activity'],
  deviceCount: number,
): DeviceGroup {
  return {
    operatingSystem: os,
    compliance,
    complianceLabel: compliance,
    encryption,
    encryptionLabel: encryption,
    activity,
    activityLabel: activity,
    deviceCount,
  };
}

function policy(id: string, assignmentState: DevicePolicy['assignmentState']): DevicePolicy {
  return {
    externalId: id,
    kind: 'CompliancePolicy',
    kindLabel: 'Política de conformidade',
    displayName: `Política ${id}`,
    platformLabel: 'Windows',
    assignmentState,
    assignmentLabel: assignmentState,
    assignmentCount: assignmentState === 'Assigned' ? 1 : assignmentState === 'Unassigned' ? 0 : null,
    lastModifiedAt: null,
  };
}

function dimension(over: Partial<DevicePostureDimension>): DevicePostureDimension {
  return {
    state: 'Available',
    storedState: 'Available',
    label: 'Disponível',
    hasData: true,
    isStale: false,
    requiredPermission: null,
    actionHint: null,
    lastAttemptAt: null,
    lastCollectionAt: null,
    ...over,
  };
}

const GROUPS: DeviceGroup[] = [
  group('Windows', 'Compliant', 'Encrypted', 'Active', 40),
  group('Windows', 'Noncompliant', 'NotEncrypted', 'Active', 7),
  group('Windows', 'Compliant', 'Encrypted', 'Stale', 3),
  group('iOS', 'Compliant', 'Unknown', 'Active', 12),
  group('Android', 'Unknown', 'Unknown', 'Unknown', 5),
];

// ---- 1) estados: falha nunca compartilha rótulo com sucesso ------------------------------------
test('dimensionStatePt traduz cada estado do backend sem confundir falha com sucesso', () => {
  eq(dimensionStatePt('Available'), 'Disponível', 'Available é honesto');
  eq(dimensionStatePt('Partial'), 'Parcial', 'Partial é distinto de Available');
  eq(dimensionStatePt('NotAuthorized'), 'Bloqueada por permissão', 'nomeia a permissão, não "erro genérico"');
  eq(dimensionStatePt('NotLicensed'), 'Indisponível por licença', 'distingue licença de permissão');
  eq(dimensionStatePt('Unavailable'), 'Indisponível', 'indisponibilidade não vira "disponível"');
  eq(dimensionStatePt('NeverCollected'), 'Nunca coletada', 'o próprio enum do backend mapeia 1:1');
});

test('um estado NÃO reconhecido cai no fallback honesto, jamais em "Disponível"', () => {
  eq(dimensionStatePt(null), 'Nunca coletada', 'null não vira Disponível');
  eq(dimensionStatePt(undefined), 'Nunca coletada', 'undefined não vira Disponível');
  eq(dimensionStatePt(''), 'Nunca coletada', 'string vazia não vira Disponível');
  eq(
    dimensionStatePt('AlgumEstadoFuturoDaMicrosoft'),
    'Nunca coletada',
    'um estado novo do backend não é interpretado como sucesso',
  );
});

test('todos os rótulos de estado são mutuamente distintos', () => {
  const states = ['Available', 'Partial', 'NotAuthorized', 'NotLicensed', 'Unavailable', 'NeverCollected'];
  const labels = states.map((s) => dimensionStatePt(s));
  eq(new Set(labels).size, labels.length, 'sem dois estados compartilhando o mesmo rótulo pt-BR');
});

// ---- 2) indisponível NUNCA vira zero -----------------------------------------------------------
test('countOrDash nunca transforma ausência em zero', () => {
  eq(countOrDash(0), '0', 'um zero REAL é exibido como zero');
  eq(countOrDash(7), '7', 'número presente é exibido');
  eq(countOrDash(null), '—', 'null vira traço, jamais 0');
  eq(countOrDash(undefined), '—', 'undefined vira traço, jamais 0');
});

test('canShowNumbers só libera números quando o backend afirmou ter dados', () => {
  assert(canShowNumbers(dimension({ hasData: true })), 'dimensão com dados mostra números');
  for (const state of ['NotAuthorized', 'NotLicensed', 'Unavailable', 'NeverCollected']) {
    const d = dimension({ state, storedState: null, hasData: false, label: dimensionStatePt(state) });
    assert(!canShowNumbers(d), `${state} não libera números`);
    assert(!/\b0\b/.test(d.label), `${state} não apresenta 0 como evidência`);
  }
  assert(!canShowNumbers(null), 'ausência de dimensão não libera números');
  assert(!canShowNumbers(undefined), 'dimensão indefinida não libera números');
});

test('dimensão com inventário preservado após falha continua mostrando números, marcada como defasada', () => {
  const stale = dimension({ state: 'NotAuthorized', storedState: 'Available', hasData: true, isStale: true });
  assert(canShowNumbers(stale), 'o inventário preservado continua legível');
  assert(stale.isStale, 'a tela sabe avisar que os números podem estar defasados');
});

// ---- 3) filtros sobre grupos AGREGADOS ---------------------------------------------------------
test('sem filtros, todos os grupos passam e o total bate com a soma', () => {
  const all = filterDeviceGroups(GROUPS, EMPTY_DEVICE_FILTERS);
  eq(all.length, GROUPS.length, 'nenhum eixo restringe');
  eq(totalDevices(all), 67, 'a soma dos grupos é o total do recorte');
});

test('cada eixo de filtro restringe independentemente', () => {
  eq(
    totalDevices(filterDeviceGroups(GROUPS, { ...EMPTY_DEVICE_FILTERS, compliance: 'Noncompliant' })),
    7,
    'filtro de conformidade',
  );
  eq(
    totalDevices(filterDeviceGroups(GROUPS, { ...EMPTY_DEVICE_FILTERS, operatingSystem: 'Windows' })),
    50,
    'filtro de sistema operacional',
  );
  eq(totalDevices(filterDeviceGroups(GROUPS, { ...EMPTY_DEVICE_FILTERS, activity: 'Stale' })), 3, 'filtro de atividade');
  eq(
    totalDevices(filterDeviceGroups(GROUPS, { ...EMPTY_DEVICE_FILTERS, encryption: 'NotEncrypted' })),
    7,
    'filtro de criptografia',
  );
});

test('os filtros se combinam por conjunção e um recorte vazio é vazio — não é "zero dispositivos"', () => {
  const combined = filterDeviceGroups(GROUPS, {
    compliance: 'Compliant',
    operatingSystem: 'Windows',
    activity: 'Active',
    encryption: 'Encrypted',
  });
  eq(totalDevices(combined), 40, 'conjunção dos quatro eixos');

  const impossible = filterDeviceGroups(GROUPS, {
    ...EMPTY_DEVICE_FILTERS,
    operatingSystem: 'iOS',
    compliance: 'Noncompliant',
  });
  eq(impossible.length, 0, 'recorte sem correspondência devolve lista vazia');
  eq(totalDevices(impossible), 0, 'o total do RECORTE é zero — não o total do inventário');
});

test('operatingSystems devolve os valores distintos em ordem determinística', () => {
  eq(operatingSystems(GROUPS).join('|'), 'Android|iOS|Windows', 'ordem estável e sem duplicatas');
  eq(operatingSystems([]).length, 0, 'inventário vazio não inventa opções');
});

// ---- 4) atribuição desconhecida NUNCA conta como "sem atribuição" -------------------------------
test('só a atribuição comprovadamente vazia conta como "sem atribuição"', () => {
  const policies = [policy('a', 'Assigned'), policy('b', 'Unassigned'), policy('c', 'Unknown'), policy('d', 'Unknown')];
  eq(unassignedPolicies(policies).length, 1, 'apenas a política com coleção vazia');
  eq(unknownAssignmentPolicies(policies).length, 2, 'as desconhecidas ficam num balde separado');
  assert(
    unassignedPolicies(policies).every((p) => p.assignmentState === 'Unassigned'),
    'nenhuma política de atribuição desconhecida vaza para "sem atribuição"',
  );
});

test('sem nenhuma política, os dois baldes são vazios (e não "tudo sem atribuição")', () => {
  eq(unassignedPolicies([]).length, 0, 'nada é presumido sem dado');
  eq(unknownAssignmentPolicies([]).length, 0, 'nada é presumido sem dado');
});

console.log(`\n${count - failures}/${count} testes passaram (device-posture.models)`);
if (failures > 0) process.exit(1);
