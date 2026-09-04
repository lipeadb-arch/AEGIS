/**
 * [AEGIS-MVP-MICROSOFT-HUB] Testes de LÓGICA PURA da conexão Microsoft unificada (frontend).
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Estas asserções cobrem as regras que
 * NÃO dependem do DOM: credencial comum informada uma vez, workspaceId só no Sentinel, serviços separados,
 * agrupamento sem duplicação e exclusão da família Microsoft do formulário genérico. São compiladas por `tsc`
 * (CommonJS) e executadas por `node` — ver o comando no cabeçalho de execução do PR.
 */
import {
  buildMicrosoftHubRequest,
  buildSiemSyncMessage,
  CHRONICLE_LOCATIONS,
  ConnectorConfig,
  GENERIC_PROVIDERS,
  isGuid,
  isMicrosoftFamily,
  MICROSOFT_HUB_SERVICES,
  microsoftServiceFor,
  MicrosoftServiceSelection,
  providerByKey,
  SiemCollectionState,
  SiemSyncSummary,
  siemDimensionText,
  SyncResult,
} from '../src/app/models/connector.models';

/** Conjunto OFICIAL de localidades do Google SecOps — o frontend deve usar EXATAMENTE este conjunto (igual ao backend). */
const OFFICIAL_SECOPS_LOCATIONS = [
  'us', 'eu', 'europe',
  'africa-south1',
  'asia-east1', 'asia-northeast1', 'asia-northeast3', 'asia-south1', 'asia-southeast1', 'asia-southeast2',
  'australia-southeast1',
  'europe-central2', 'europe-west2', 'europe-west3', 'europe-west6', 'europe-west9', 'europe-west12',
  'me-central1', 'me-central2', 'me-west1',
  'northamerica-northeast2', 'southamerica-east1',
];

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
function assert(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}
function eq<T>(actual: T, expected: T, msg: string): void {
  if (actual !== expected) throw new Error(`${msg}: esperado ${String(expected)}, obtido ${String(actual)}`);
}
function conn(provider: string, capability: string): ConnectorConfig {
  return {
    id: `${provider}-${capability}`, provider, capability, displayName: capability, authType: 'OAuthClientCredentials',
    enabled: true, syncIntervalMinutes: 360, lastSyncAt: null, lastStatus: 'Unknown', hasCredentials: true, hasIngestionKey: false,
  };
}

const CREDS = { tenantId: '  t-1  ', clientId: 'c-1', clientSecret: 's-1' };

function siemSummary(alertsState: SiemCollectionState, alertsObserved: number | null, isComplete = true): SiemSyncSummary {
  return {
    source: 'Microsoft Sentinel',
    isComplete,
    cases: {
      state: 'Available', period: 'RollingWindow', windowDays: 30, isComplete: true,
      observed: 10, open: 4, new: 6, closed: 3,
      openHighSeverity: 2, openMediumSeverity: 1, openLowSeverity: 1, openInformationalSeverity: 0,
      openByPriority: null, meanTimeToCloseHours: 2, lastEvidenceAt: null,
    },
    alerts: {
      state: alertsState, period: 'RollingWindow', windowDays: 30, isComplete: alertsState === 'Available',
      observed: alertsObserved, highSeverity: null, mediumSeverity: null, lastEvidenceAt: null,
    },
  };
}

// ---- 1) credencial comum informada UMA vez, aplicada a todos os serviços -----------------------
test('credencial comum é informada uma vez e vale para todos os serviços selecionados', () => {
  const sels: MicrosoftServiceSelection[] = [
    { key: 'SecureScore', syncIntervalMinutes: 360 },
    { key: 'IdentityPosture', syncIntervalMinutes: 360 },
    { key: 'VulnerabilityScanner', syncIntervalMinutes: 120 },
  ];
  const body = buildMicrosoftHubRequest(CREDS, sels);
  eq(body.tenantId, 't-1', 'tenantId é aparado e único');
  eq(body.clientId, 'c-1', 'clientId único');
  eq(body.clientSecret, 's-1', 'clientSecret único (não aparado — pode conter espaços significativos)');
  eq(body.services.length, 3, 'um item por serviço selecionado');
  eq(body.services[2].syncIntervalMinutes, 120, 'intervalo por serviço preservado');
});

// ---- 2) workspaceId SOMENTE no Sentinel --------------------------------------------------------
test('workspaceId só entra no serviço Sentinel', () => {
  const body = buildMicrosoftHubRequest(CREDS, [
    { key: 'SecureScore', syncIntervalMinutes: 360, workspaceId: 'nao-deveria-ir' },
    { key: 'Sentinel', syncIntervalMinutes: 360, workspaceId: '  ws-123  ' },
  ]);
  const secureScore = body.services.find((s) => s.capability === 0)!;
  const sentinel = body.services.find((s) => s.capability === 5)!;
  eq(secureScore.workspaceId, undefined, 'Secure Score nunca recebe workspaceId (não contamina)');
  eq(sentinel.workspaceId, 'ws-123', 'Sentinel recebe o workspaceId aparado');
});

// [AEGIS-MVP-MICROSOFT-COVERAGE-02] O Intune entra no MESMO corpo, com a MESMA credencial e SEM workspaceId.
test('o Intune reusa a credencial comum e nunca recebe workspaceId', () => {
  const body = buildMicrosoftHubRequest(CREDS, [
    { key: 'IntunePosture', syncIntervalMinutes: 720, workspaceId: 'nao-deveria-ir' },
    { key: 'Sentinel', syncIntervalMinutes: 360, workspaceId: 'ws-123' },
  ]);
  const intune = body.services.find((s) => s.capability === 4)!;
  assert(intune !== undefined, 'o Intune viaja como capacidade ConfigAnalyzer (4)');
  eq(intune.workspaceId, undefined, 'o Intune nunca recebe workspaceId (exclusivo do Sentinel)');
  eq(body.clientSecret, CREDS.clientSecret, 'nenhum segredo adicional é pedido para o Intune');
  eq(body.services.length, 2, 'um serviço por seleção — nada duplicado');
});

// ---- 3) serviços exibidos separadamente (5 capacidades distintas) ------------------------------
test('a família Microsoft tem cinco serviços distintos', () => {
  eq(MICROSOFT_HUB_SERVICES.length, 5, 'cinco serviços');
  const caps = new Set(MICROSOFT_HUB_SERVICES.map((s) => s.capabilityValue));
  eq(caps.size, 5, 'capacidades distintas');
  const sentinel = MICROSOFT_HUB_SERVICES.find((s) => s.key === 'Sentinel')!;
  eq(sentinel.capabilityValue, 5, 'Sentinel = Siem (5)');
  eq(sentinel.providerValue, 3, 'Sentinel provider = MicrosoftSentinel (3)');
  eq(sentinel.needsWorkspaceId, true, 'só o Sentinel exige workspaceId');
  assert(MICROSOFT_HUB_SERVICES.filter((s) => s.needsWorkspaceId).length === 1, 'apenas UM serviço exige workspaceId');
  const secure = MICROSOFT_HUB_SERVICES.find((s) => s.key === 'SecureScore')!;
  eq(secure.providerValue, 0, 'os demais serviços usam provider Microsoft (0)');

  // [AEGIS-MVP-MICROSOFT-COVERAGE-02] O Intune declara AS DUAS permissões, nomeando a dimensão de cada uma —
  // é o que permite a tela dizer o que já funciona e o que ainda falta conceder.
  const intune = MICROSOFT_HUB_SERVICES.find((s) => s.key === 'IntunePosture')!;
  eq(intune.capabilityValue, 4, 'Intune = ConfigAnalyzer (4)');
  eq(intune.providerValue, 0, 'Intune provider = Microsoft (0)');
  eq(intune.needsWorkspaceId, false, 'o Intune não usa workspaceId');
  eq(intune.appPermissions.length, 2, 'duas permissões distintas, uma por dimensão');
  assert(
    intune.appPermissions.some((p) => p.startsWith('DeviceManagementConfiguration.Read.All')),
    'declara a permissão de políticas',
  );
  assert(
    intune.appPermissions.some((p) => p.startsWith('DeviceManagementManagedDevices.Read.All')),
    'declara a permissão de estado efetivo dos dispositivos',
  );
  // Nenhuma permissão de ESCRITA é pedida em nenhum serviço da família.
  for (const svc of MICROSOFT_HUB_SERVICES)
    for (const perm of svc.appPermissions)
      assert(!/ReadWrite/.test(perm), `${svc.key} não pede permissão de escrita: ${perm}`);
});

// ---- 4) conectores existentes agrupados sob "Microsoft" SEM duplicação -------------------------
test('conectores Microsoft são agrupados sem duplicar capacidade', () => {
  const all = [
    conn('Microsoft', 'SecureScore'),
    conn('Microsoft', 'IdentityPosture'),
    conn('Microsoft', 'VulnerabilityScanner'),
    conn('MicrosoftSentinel', 'Siem'),
    conn('Microsoft', 'ConfigAnalyzer'), // [COVERAGE-02] Intune — quinto filho do hub
    conn('Generic', 'Siem'), // push genérico — NÃO é família Microsoft
    conn('Google', 'IdentityPosture'), // Google KNIGHT — NÃO é família Microsoft
    conn('Aws', 'ConfigAnalyzer'), // MESMA capacidade, outro provider — NÃO é família Microsoft
  ];
  const family = all.filter(isMicrosoftFamily);
  eq(family.length, 5, 'exatamente os cinco serviços Microsoft');
  assert(!isMicrosoftFamily(conn('Generic', 'Siem')), 'Generic/Siem não é família Microsoft');
  assert(!isMicrosoftFamily(conn('Google', 'IdentityPosture')), 'Google/IdentityPosture não é família Microsoft');
  // A capacidade ConfigAnalyzer NÃO é exclusiva da Microsoft: o par provider+capability é que decide.
  assert(!isMicrosoftFamily(conn('Aws', 'ConfigAnalyzer')), 'Aws/ConfigAnalyzer não é família Microsoft');

  // Cada membro mapeia para um serviço distinto (sem duplicidade de provider/capability).
  const specs = family.map((c) => microsoftServiceFor(c));
  assert(specs.every((s) => s !== undefined), 'todo membro da família resolve seu serviço');
  const keys = new Set(specs.map((s) => s!.key));
  eq(keys.size, 5, 'cinco serviços distintos — sem duplicação');
});

// ---- 5) formulário genérico exclui a família Microsoft -----------------------------------------
test('o formulário genérico não lista a família Microsoft', () => {
  const genericKeys = GENERIC_PROVIDERS.map((p) => p.key);
  for (const k of ['Microsoft', 'MicrosoftDefenderVuln', 'MicrosoftEntraKnight', 'MicrosoftSentinel']) {
    assert(!genericKeys.includes(k as never), `${k} não aparece no formulário genérico (vai para o hub)`);
  }
  // Provedores não-Microsoft continuam disponíveis no genérico.
  assert(genericKeys.includes('GenericSiem'), 'GenericSiem continua no formulário genérico');
  assert(genericKeys.includes('GoogleCloudVuln'), 'GoogleCloudVuln continua no formulário genérico');
});

// ---- 6) validação de GUID do workspaceId (borda do formulário) ---------------------------------
test('isGuid aceita GUID válido e rejeita texto não-GUID', () => {
  assert(isGuid('abcdefab-1234-5678-9abc-abcdefabcdef'), 'GUID canônico é aceito');
  assert(isGuid('  ABCDEFAB-1234-5678-9ABC-ABCDEFABCDEF  '), 'GUID com espaços/maiúsculas é aceito (aparado)');
  assert(!isGuid('ws-1234'), 'texto não-GUID é rejeitado');
  assert(!isGuid(''), 'vazio é rejeitado');
  assert(!isGuid(null), 'null é rejeitado');
  assert(!isGuid('abcdefab-1234-5678-9abc-abcdefabcde'), 'GUID curto/malformado é rejeitado');
});

// ---- 7) dimensão Available mostra a contagem, inclusive zero; mensagem completa --------------------
test('dimensão Available mostra a contagem (inclusive zero) e a mensagem preserva os casos', () => {
  eq(siemDimensionText('Available', 25, 'alerta'), '25 alerta(s)', 'Available com dados mostra o número');
  eq(siemDimensionText('Available', 0, 'alerta'), '0 alerta(s)', 'Available com zero mostra 0 (fato)');

  const msg = buildSiemSyncMessage(siemSummary('Available', 0));
  assert(msg.includes('0 alerta(s)'), 'a mensagem completa mostra 0 alertas quando Available');
  assert(msg.includes('10 caso(s)'), 'preserva os agregados de casos');
  assert(msg.includes('Microsoft Sentinel'), 'identifica a fonte (provider-neutral)');
  assert(msg.includes('Não altera o AEGIS Score'), 'deixa explícito que não altera o score');
  assert(!msg.includes('(coleta parcial/degradada)'), 'coleta completa não sinaliza degradação');
});

// ---- 8) estados não-Available → indisponibilidade, NUNCA zero; mensagem degradada ------------------
test('dimensão não-Available mostra indisponibilidade e a mensagem fica degradada, nunca zero', () => {
  const states: [SiemCollectionState, string][] = [
    ['Unsupported', 'alertas não disponíveis nesta fonte'],
    ['PermissionDenied', 'sem permissão para consultar alertas'],
    ['Throttled', 'consulta de alertas temporariamente limitada'],
    ['Timeout', 'consulta de alertas excedeu o tempo'],
    ['Unavailable', 'alertas indisponíveis'],
  ];
  for (const [state, expected] of states) {
    const text = siemDimensionText(state, null, 'alerta');
    eq(text, expected, `${state} produz a frase de indisponibilidade`);
    assert(!text.includes('alerta(s)'), `${state} nunca mostra 'N alerta(s)'`);
    assert(!/\b0\b/.test(text), `${state} nunca apresenta 0 como evidência`);

    const msg = buildSiemSyncMessage(siemSummary(state, null, false));
    assert(msg.includes(expected), `mensagem completa inclui a indisponibilidade (${state})`);
    assert(msg.includes('(coleta parcial/degradada)'), `mensagem degradada sinaliza a coleta parcial (${state})`);
    assert(!msg.includes('0 alerta'), `mensagem nunca mostra '0 alerta' em ${state}`);
    assert(msg.includes('10 caso(s)'), `preserva casos mesmo com alertas indisponíveis (${state})`);
  }
});

// ---- 9) dimensão Partial mostra piso, nunca finge total ----------------------------------------
test('dimensão Partial mostra piso e nunca inventa total', () => {
  eq(siemDimensionText('Partial', 3, 'alerta'), '≥ 3 alerta(s) (parcial)', 'Partial com dados mostra piso');
  eq(siemDimensionText('Partial', null, 'alerta'), 'alertas parciais', 'Partial sem dados não inventa número');
  const msg = buildSiemSyncMessage(siemSummary('Partial', 3, false));
  assert(msg.includes('≥ 3 alerta(s) (parcial)'), 'mensagem mostra piso parcial');
  assert(msg.includes('(coleta parcial/degradada)'), 'mensagem marca degradação');
});

// ---- 10) catálogo do Google SecOps: quatro campos canônicos, sem "não implementado" -------------
test('o catálogo do Google SecOps traz os quatro campos canônicos e não exibe "adaptador não implementado"', () => {
  const g = providerByKey('Google');
  assert(!!g, 'provedor Google SecOps presente no catálogo');
  eq(g!.value, 1, 'provider Google = 1');
  eq(g!.capability, 'Siem', 'capacidade Siem');
  eq(g!.capabilityValue, 5, 'Siem = 5');
  eq(g!.authType, 'ServiceAccount', 'auth = service account');
  assert(g!.adapterNote === undefined, 'não há mais aviso "adaptador ainda não implementado"');

  const keys = g!.fields.map((f) => f.key);
  eq(JSON.stringify(keys), JSON.stringify(['projectId', 'location', 'instanceId', 'serviceAccountJson']),
    'exatamente os quatro campos canônicos, nessa ordem');
  assert(!keys.includes('customerId'), 'customerId não é mais campo do formulário (só compatibilidade de entrada no backend)');

  const sa = g!.fields.find((f) => f.key === 'serviceAccountJson')!;
  assert(sa.secret === true, 'o JSON da service account é mascarado (segredo, escrita-apenas — nunca retorna)');
  const loc = g!.fields.find((f) => f.key === 'location')!;
  assert((loc.options?.length ?? 0) > 0, 'a localidade é seleção controlada (sem URL/host arbitrário)');

  // Honestidade de produto: sem "REAL" e sem afirmar "readonly"; scope chronicle + GET-only/IAM + pendência de validação.
  assert(!/\bREAL\b/.test(g!.infoNote ?? ''), 'não afirma coleta "REAL" enquanto não há validação contra instância viva');
  assert((g!.infoNote ?? '').includes('somente leitura'), 'descreve coleta operacional somente leitura');
  assert((g!.infoNote ?? '').includes('scope é chronicle'), 'declara o OAuth scope chronicle');
  assert((g!.infoNote ?? '').toLowerCase().includes('pendente de validação'), 'admite a validação pendente contra instância real');
  const perms = (g!.appPermissions ?? []).join(' ');
  assert(perms.includes('auth/chronicle') && !perms.includes('chronicle.readonly'), 'lista o scope chronicle (não readonly)');
  assert(perms.includes('chronicle.instances.get') && perms.includes('chronicle.cases.get')
    && perms.includes('chronicle.legacies.legacySearchEnterpriseWideAlerts'), 'lista as permissões IAM mínimas');
});

// ---- 12) allowlist de localidades: frontend == conjunto oficial (igual ao backend) --------------
test('CHRONICLE_LOCATIONS usa exatamente o conjunto oficial de localidades', () => {
  const values = CHRONICLE_LOCATIONS.map((l) => l.value);
  eq(values.length, OFFICIAL_SECOPS_LOCATIONS.length, 'mesma quantidade de localidades oficiais');
  const front = [...values].sort();
  const official = [...OFFICIAL_SECOPS_LOCATIONS].sort();
  eq(JSON.stringify(front), JSON.stringify(official), 'valores idênticos ao conjunto oficial (sem sobra nem falta)');
  eq(new Set(values).size, values.length, 'sem localidade duplicada');
});

// ---- 11) SyncResult expõe a postura de SIEM em `siem` (não `sentinel`) --------------------------
test('SyncResult expõe a postura de SIEM em `siem`', () => {
  const r: SyncResult = { signalsCollected: 0, siem: siemSummary('Available', 5) };
  assert(r.siem !== null && r.siem !== undefined, 'o campo siem carrega a fotografia');
  eq(r.siem!.source, 'Microsoft Sentinel', 'a fonte é identificada dentro do resumo neutro');
  eq(r.siem!.alerts.observed, 5, 'a dimensão de alertas traz a contagem');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend falharam`);
