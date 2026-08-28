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
  ConnectorConfig,
  GENERIC_PROVIDERS,
  isMicrosoftFamily,
  MICROSOFT_HUB_SERVICES,
  microsoftServiceFor,
  MicrosoftServiceSelection,
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

console.log(`\n${count - failures}/${count} testes de lógica do frontend aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend falharam`);
