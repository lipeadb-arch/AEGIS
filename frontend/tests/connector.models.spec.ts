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
  buildSentinelSyncMessage,
  ConnectorConfig,
  GENERIC_PROVIDERS,
  isGuid,
  isMicrosoftFamily,
  MICROSOFT_HUB_SERVICES,
  microsoftServiceFor,
  MicrosoftServiceSelection,
  SentinelAlertsState,
  SentinelSyncSummary,
  sentinelAlertsText,
} from '../src/app/models/connector.models';

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

function sentinelSummary(alertsState: SentinelAlertsState, alertsObserved: number, isComplete = true): SentinelSyncSummary {
  return {
    windowDays: 30, incidentsObserved: 10, openIncidents: 4, newIncidents: 6, closedIncidents: 3,
    openHighSeverity: 2, openMediumSeverity: 1, openLowSeverity: 1, openInformationalSeverity: 0,
    meanTimeToCloseHours: 2, alertsState, alertsObserved, alertsHighSeverity: 0, alertsMediumSeverity: 0,
    lastEvidenceAt: null, isComplete,
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

// ---- 3) serviços exibidos separadamente (4 capacidades distintas) ------------------------------
test('a família Microsoft tem quatro serviços distintos', () => {
  eq(MICROSOFT_HUB_SERVICES.length, 4, 'quatro serviços');
  const caps = new Set(MICROSOFT_HUB_SERVICES.map((s) => s.capabilityValue));
  eq(caps.size, 4, 'capacidades distintas');
  const sentinel = MICROSOFT_HUB_SERVICES.find((s) => s.key === 'Sentinel')!;
  eq(sentinel.capabilityValue, 5, 'Sentinel = Siem (5)');
  eq(sentinel.providerValue, 3, 'Sentinel provider = MicrosoftSentinel (3)');
  eq(sentinel.needsWorkspaceId, true, 'só o Sentinel exige workspaceId');
  assert(MICROSOFT_HUB_SERVICES.filter((s) => s.needsWorkspaceId).length === 1, 'apenas UM serviço exige workspaceId');
  const secure = MICROSOFT_HUB_SERVICES.find((s) => s.key === 'SecureScore')!;
  eq(secure.providerValue, 0, 'os demais serviços usam provider Microsoft (0)');
});

// ---- 4) conectores existentes agrupados sob "Microsoft" SEM duplicação -------------------------
test('conectores Microsoft são agrupados sem duplicar capacidade', () => {
  const all = [
    conn('Microsoft', 'SecureScore'),
    conn('Microsoft', 'IdentityPosture'),
    conn('Microsoft', 'VulnerabilityScanner'),
    conn('MicrosoftSentinel', 'Siem'),
    conn('Generic', 'Siem'), // push genérico — NÃO é família Microsoft
    conn('Google', 'IdentityPosture'), // Google KNIGHT — NÃO é família Microsoft
  ];
  const family = all.filter(isMicrosoftFamily);
  eq(family.length, 4, 'exatamente os quatro serviços Microsoft');
  assert(!isMicrosoftFamily(conn('Generic', 'Siem')), 'Generic/Siem não é família Microsoft');
  assert(!isMicrosoftFamily(conn('Google', 'IdentityPosture')), 'Google/IdentityPosture não é família Microsoft');

  // Cada membro mapeia para um serviço distinto (sem duplicidade de provider/capability).
  const specs = family.map((c) => microsoftServiceFor(c));
  assert(specs.every((s) => s !== undefined), 'todo membro da família resolve seu serviço');
  const keys = new Set(specs.map((s) => s!.key));
  eq(keys.size, 4, 'quatro serviços distintos — sem duplicação');
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

// ---- 7) alertas: Available mostra a contagem, inclusive zero -----------------------------------
test('alertas Available mostram a contagem, inclusive zero', () => {
  eq(sentinelAlertsText(sentinelSummary('Available', 25)), '25 alerta(s)', 'Available com dados mostra o número');
  eq(sentinelAlertsText(sentinelSummary('Available', 0)), '0 alerta(s)', 'Available com zero mostra 0 (fato)');

  const msg = buildSentinelSyncMessage(sentinelSummary('Available', 0));
  assert(msg.includes('0 alerta(s)'), 'a mensagem completa mostra 0 quando Available');
  assert(msg.includes('10 incidente(s)'), 'preserva os agregados de incidentes');
  assert(msg.includes('Não altera o AEGIS Score'), 'deixa explícito que não altera o score');
});

// ---- 8) alertas: estados não-Available → indisponibilidade, NUNCA zero como evidência ----------
test('alertas não-Available mostram indisponibilidade e nunca zero como evidência', () => {
  const states: [SentinelAlertsState, string][] = [
    ['TableUnavailable', 'tabela de alertas não disponível'],
    ['PermissionDenied', 'sem permissão para consultar alertas'],
    ['Throttled', 'consulta de alertas temporariamente limitada'],
    ['Timeout', 'consulta de alertas excedeu o tempo'],
    ['Partial', 'resultado de alertas parcial'],
    ['Unavailable', 'alertas indisponíveis'],
  ];
  for (const [state, expected] of states) {
    const text = sentinelAlertsText(sentinelSummary(state, 0));
    eq(text, expected, `${state} produz a frase de indisponibilidade`);
    assert(!text.includes('alerta(s)'), `${state} nunca mostra 'N alerta(s)'`);
    assert(!/\b0\b/.test(text), `${state} nunca apresenta 0 como evidência`);

    const msg = buildSentinelSyncMessage(sentinelSummary(state, 0, false));
    assert(msg.includes(expected), `mensagem completa inclui a indisponibilidade (${state})`);
    assert(!msg.includes('0 alerta'), `mensagem nunca mostra '0 alerta' em ${state}`);
    assert(msg.includes('10 incidente(s)'), `preserva incidentes mesmo com alertas indisponíveis (${state})`);
  }
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend falharam`);
