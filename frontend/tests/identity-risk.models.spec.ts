/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-03] Testes de LÓGICA PURA do risco de identidade (frontend).
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node` (ver `npm run test:logic`), mesmo padrão de device-posture.models.spec.ts.
 *
 * O que estes testes travam:
 *  • "não coletado" NUNCA vira 0 na tela, e leitura incompleta é sinalizada como piso ("≥ n");
 *  • completo, parcial, sem permissão, licença insuficiente, nunca coletado e fotografia preservada após
 *    falha são estados DISTINTOS, com mensagens distintas;
 *  • nível `hidden` e estado `unknown` jamais viram "sem risco" nem "resolvido";
 *  • nenhum rótulo carrega PII, e a visão inicial não exibe nomes crus de enum;
 *  • UserAuthenticationMethod.Read.All fica FORA das permissões exigidas por este pacote.
 */
import {
  ENTRA_IDENTITY_CAPABILITIES,
  PERMISSION_CATALOG_CAVEAT,
  licenseDependentCapabilities,
  permissionUsageLabel,
  permissionsByUsage,
  requiredPermissions,
} from '../src/app/models/connector.models';
import {
  CONSULTATIVE_CAVEAT,
  IdentityEvidenceProjection,
  IdentityRiskCapability,
  IdentityRiskDetections,
  IdentityRiskLevels,
  IdentityRiskStates,
  IdentityRiskyUsers,
  NO_DETECTION_CAVEAT,
  countDisplay,
  detectionTypeLabel,
  isLimited,
  levelSlices,
  methodLabel,
  outcomeGuidance,
  outcomeLabel,
  riskCount,
  sectionMessage,
  sectionState,
  stateSlices,
  topDetectionTypes,
} from '../src/app/models/identity-risk.models';

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
function cap(
  outcome: IdentityRiskCapability['outcome'],
  hasData: boolean,
  isComplete = true,
  detail: string | null = null,
): IdentityRiskCapability {
  return { outcome, detail, hasData, isComplete };
}

function levels(p: Partial<IdentityRiskLevels> = {}): IdentityRiskLevels {
  return { high: 0, medium: 0, low: 0, none: 0, hidden: 0, unknown: 0, ...p };
}

function states(p: Partial<IdentityRiskStates> = {}): IdentityRiskStates {
  const base = {
    atRisk: 0,
    confirmedCompromised: 0,
    remediated: 0,
    dismissed: 0,
    confirmedSafe: 0,
    none: 0,
    unknown: 0,
    ...p,
  };
  return {
    ...base,
    active: base.atRisk + base.confirmedCompromised,
    resolved: base.remediated + base.dismissed + base.confirmedSafe,
  };
}

function users(p: Partial<IdentityRiskyUsers> = {}): IdentityRiskyUsers {
  return {
    total: 0,
    deleted: 0,
    processing: 0,
    live: 0,
    active: 0,
    highRiskActive: 0,
    levels: levels(),
    states: states(),
    mostRecentRiskUpdateAt: null,
    isComplete: true,
    ...p,
  };
}

function detections(p: Partial<IdentityRiskDetections> = {}): IdentityRiskDetections {
  return {
    windowDays: 30,
    windowStart: '2026-08-05T00:00:00Z',
    windowEnd: '2026-09-04T00:00:00Z',
    totalInWindow: 0,
    outsideWindow: 0,
    undated: 0,
    inRecentWindow: 0,
    active: 0,
    resolved: 0,
    highRiskActive: 0,
    premiumDetailWithheld: 0,
    realtime: 0,
    nearRealtime: 0,
    offline: 0,
    timingNotDefined: 0,
    timingUnknown: 0,
    levels: levels(),
    states: states(),
    topTypes: [],
    mostRecentDetectionAt: null,
    isComplete: true,
    ...p,
  };
}

function projection(p: Partial<IdentityEvidenceProjection> = {}): IdentityEvidenceProjection {
  return {
    connectorState: 'Configured',
    collectionState: 'Complete',
    lastAttemptState: 'Completed',
    isDegraded: false,
    source: 'Microsoft Entra ID',
    schemaVersion: 'aegis-identity-evidence-v2',
    collectedAt: '2026-09-04T10:00:00Z',
    lastAttemptAt: '2026-09-04T10:00:00Z',
    lastAttemptDetail: null,
    capabilities: [],
    controls: [],
    identityRisk: null,
    authenticationPosture: null,
    ...p,
  };
}

function withRisk(
  riskyCap: IdentityRiskCapability,
  detectionCap: IdentityRiskCapability,
  extra: Partial<IdentityEvidenceProjection> = {},
): IdentityEvidenceProjection {
  return projection({
    identityRisk: {
      riskyUsersCapability: riskyCap,
      riskDetectionsCapability: detectionCap,
      riskyUsers: riskyCap.hasData ? users({ total: 3, live: 3, active: 2, highRiskActive: 1 }) : null,
      detections: detectionCap.hasData ? detections({ totalInWindow: 5, active: 2 }) : null,
      evaluatedAt: '2026-09-04T10:00:00Z',
    },
    ...extra,
  });
}

console.log('identity-risk.models');

// ---- 1) ausência de dado nunca vira zero -------------------------------------------------------

test('capacidade sem dado devolve null — a tela mostra "—", nunca 0', () => {
  const c = cap('InsufficientPermission', false);
  eq(riskCount(c, 0), null, 'sem dado a contagem é indefinida');
  eq(countDisplay(c, 0), '—', 'exibição de contagem indisponível');
  eq(countDisplay(null, 7), '—', 'sem capacidade não há número a exibir');
});

test('zero real só aparece quando a coleta produziu dados', () => {
  const c = cap('Collected', true);
  eq(riskCount(c, 0), 0, 'zero coletado é um fato');
  eq(countDisplay(c, 0), '0', 'zero coletado é exibido como zero');
});

test('leitura incompleta é sinalizada como PISO, não como total', () => {
  const c = cap('Collected', true, false);
  eq(countDisplay(c, 42), '≥ 42', 'coleta truncada mostra piso explícito');
  assert(isLimited(c), 'coleta incompleta é uma limitação');
});

test('capacidade coletada e completa não é tratada como limitada', () => {
  assert(!isLimited(cap('Collected', true, true)), 'coleta íntegra não é limitação');
  assert(isLimited(cap('LimitedByLicense', false)), 'licença insuficiente é limitação');
  assert(isLimited(null), 'ausência de capacidade é limitação');
});

// ---- 2) estados da seção são distintos ---------------------------------------------------------

test('sem conector, sem coleta e coleta completa são estados distintos', () => {
  eq(sectionState(null), 'NoConnector', 'projeção ausente');
  eq(sectionState(projection({ connectorState: 'NotConfigured' })), 'NoConnector', 'conector ausente');
  eq(sectionState(projection()), 'NeverCollected', 'configurado, mas sem bloco de risco');
  eq(
    sectionState(withRisk(cap('NotAttempted', false), cap('NotAttempted', false))),
    'NeverCollected',
    'bloco presente porém sem dado algum',
  );
  eq(sectionState(withRisk(cap('Collected', true), cap('Collected', true))), 'Complete', 'coleta íntegra');
});

test('permissão ausente numa dimensão vira coleta PARCIAL, não "sem risco"', () => {
  const p = withRisk(cap('Collected', true), cap('InsufficientPermission', false));
  eq(sectionState(p), 'Partial', 'uma dimensão sem permissão degrada a seção para parcial');
  assert(sectionMessage('Partial').includes('piso'), 'a mensagem parcial avisa que os números são um piso');
});

test('licença insuficiente numa dimensão não invalida a outra', () => {
  const p = withRisk(cap('Collected', true), cap('LimitedByLicense', false));
  eq(sectionState(p), 'Partial', 'licença limitada degrada, não zera');
  assert(p.identityRisk!.riskyUsers !== null, 'a dimensão de usuários continua com dados');
  eq(p.identityRisk!.detections, null, 'a dimensão sem licença não inventa números');
});

test('fotografia preservada após falha tem estado e mensagem próprios', () => {
  const p = withRisk(cap('Collected', true), cap('Collected', true), { isDegraded: true });
  eq(sectionState(p), 'PreservedAfterFailure', 'degradação com evidência preservada');
  assert(
    sectionMessage('PreservedAfterFailure').includes('preservados'),
    'a mensagem diz que os dados anteriores foram preservados, não apagados',
  );
});

test('nunca coletado não afirma ausência de risco', () => {
  const msg = sectionMessage('NeverCollected');
  assert(msg.includes('Sem coleta'), 'a mensagem reconhece que não houve coleta');
  assert(!/sem risco|nenhum risco|seguro/i.test(msg), 'não afirma ausência de risco');
});

// ---- 3) semântica dos buckets ------------------------------------------------------------------

test('nível oculto pela licença é separado de "sem nível"', () => {
  const slices = levelSlices(levels({ hidden: 3, none: 1 }));
  const hidden = slices.find((s) => s.key === 'hidden');
  const none = slices.find((s) => s.key === 'none');
  assert(!!hidden && hidden.count === 3, 'hidden é uma fatia própria');
  assert(!!none && none.count === 1, 'none é uma fatia própria');
  assert(hidden!.label !== none!.label, 'rótulos distintos para conceitos distintos');
  assert(!/sem risco/i.test(hidden!.label), 'nível oculto nunca é apresentado como ausência de risco');
});

test('estado desconhecido aparece e nunca é somado a resolvido', () => {
  const st = states({ unknown: 4, remediated: 2 });
  eq(st.resolved, 2, 'desconhecido fora de resolvido');
  eq(st.active, 0, 'desconhecido fora de em aberto');
  const unknown = stateSlices(st).find((s) => s.key === 'unknown');
  assert(!!unknown && unknown.count === 4, 'o bucket desconhecido é exibido, nunca descartado');
});

test('fatias vazias não poluem a distribuição', () => {
  eq(levelSlices(levels()).length, 0, 'sem contagem não há fatia');
  eq(stateSlices(null).length, 0, 'sem distribuição não há fatia');
});

test('tipos de detecção vêm ordenados por volume, com desempate estável', () => {
  const d = detections({
    topTypes: [
      { category: 'newcountry', count: 2 },
      { category: 'leakedcredentials', count: 9 },
      { category: 'anomaloustoken', count: 2 },
    ],
  });
  const top = topDetectionTypes(d, 2);
  eq(top.length, 2, 'respeita o limite pedido');
  eq(top[0].category, 'leakedcredentials', 'maior volume primeiro');
  eq(top[1].category, 'anomaloustoken', 'empate resolvido pelo nome');
  eq(topDetectionTypes(null).length, 0, 'sem detecções não há tipos');
});

// ---- 4) linguagem operacional ------------------------------------------------------------------

test('a visão inicial não expõe nomes crus de enum', () => {
  const visible = [
    ...stateSlices(states({ atRisk: 1, confirmedCompromised: 1, remediated: 1, dismissed: 1, confirmedSafe: 1 })).map(
      (s) => s.label,
    ),
    ...levelSlices(levels({ high: 1, hidden: 1 })).map((s) => s.label),
    outcomeLabel('InsufficientPermission'),
    outcomeLabel('LimitedByLicense'),
    sectionMessage('Complete'),
  ];
  for (const label of visible) {
    assert(
      !/confirmedCompromised|atRisk|confirmedSafe|unknownFutureValue|InsufficientPermission|LimitedByLicense/.test(label),
      `rótulo cru vazou para a visão inicial: ${label}`,
    );
  }
});

test('o AEGIS não afirma ter confirmado comprometimento', () => {
  const label = stateSlices(states({ confirmedCompromised: 1 })).find((s) => s.key === 'confirmedCompromised')!.label;
  assert(/Marcadas como potencialmente comprometidas/.test(label), 'a marcação é atribuída à fonte, não ao AEGIS');
  assert(!/AEGIS confirmou|confirmado pelo AEGIS/i.test(label), 'o AEGIS não reivindica a confirmação');
});

test('cada desfecho tem rótulo e ação objetiva próprios', () => {
  eq(outcomeLabel('InsufficientPermission'), 'Permissão ainda não concedida', 'permissão ausente');
  eq(outcomeLabel('LimitedByLicense'), 'Licença insuficiente', 'licença insuficiente');
  assert(
    outcomeGuidance('InsufficientPermission', 'IdentityRiskyUser.Read.All').includes('IdentityRiskyUser.Read.All'),
    'a orientação nomeia a permissão a conceder',
  );
  assert(
    /licença/i.test(outcomeGuidance('LimitedByLicense', 'IdentityRiskEvent.Read.All')),
    'a orientação de licença fala de licença, não de permissão',
  );
  assert(
    outcomeGuidance('Throttled', 'x').includes('preservados'),
    'throttling avisa que os dados anteriores foram preservados',
  );
});

test('a ressalva de eficácia e a de autoridade estão presentes e são explícitas', () => {
  assert(
    NO_DETECTION_CAVEAT === 'A ausência de detecções não comprova que os controles estejam eficazes.',
    'ressalva de eficácia literal',
  );
  assert(/não alteram o AEGIS Score/.test(CONSULTATIVE_CAVEAT), 'ressalva de autoridade cita o AEGIS Score');
  assert(/AEGIS KNIGHT Score/.test(CONSULTATIVE_CAVEAT), 'ressalva de autoridade cita o KNIGHT Score');
});

test('tipos e métodos desconhecidos aparecem pelo próprio nome, sem quebrar', () => {
  eq(detectionTypeLabel('leakedCredentials'), 'Credenciais vazadas', 'tipo conhecido é traduzido');
  eq(detectionTypeLabel('generic'), 'Detecção sem detalhe (requer plano superior)', 'generic explica a limitação');
  eq(detectionTypeLabel('brandNewSignal'), 'brandNewSignal', 'tipo novo é preservado');
  eq(methodLabel('fido2SecurityKey'), 'Chave de segurança FIDO2', 'método conhecido é traduzido');
  eq(methodLabel('somethingNew'), 'somethingNew', 'método novo é preservado');
});

// ---- 5) ausência de PII ------------------------------------------------------------------------

test('nenhum contrato de risco carrega campo pessoal', () => {
  const p = withRisk(cap('Collected', true), cap('Collected', true));
  const serialized = JSON.stringify(p);
  for (const forbidden of [
    'userPrincipalName',
    'userDisplayName',
    'userId',
    'ipAddress',
    'location',
    'requestId',
    'correlationId',
    'additionalInfo',
    'userAgent',
  ]) {
    assert(!serialized.includes(forbidden), `campo pessoal presente no contrato: ${forbidden}`);
  }
});

// ---- 6) matriz de permissões -------------------------------------------------------------------

test('as duas permissões novas são exigidas', () => {
  const required = requiredPermissions(ENTRA_IDENTITY_CAPABILITIES);
  assert(required.includes('IdentityRiskyUser.Read.All'), 'risky users exigida');
  assert(required.includes('IdentityRiskEvent.Read.All'), 'risk detections exigida');
});

test('as cinco permissões já consumidas continuam na matriz', () => {
  const consumed = permissionsByUsage(ENTRA_IDENTITY_CAPABILITIES, 'Consumed').map((c) => c.permission);
  for (const p of [
    'Directory.Read.All',
    'AuditLog.Read.All',
    'User.Read.All',
    'Policy.Read.All',
    'Application.Read.All',
  ]) {
    assert(consumed.includes(p), `permissão consumida ausente da matriz: ${p}`);
  }
  eq(consumed.length, 5, 'exatamente cinco permissões já consumidas');
});

test('UserAuthenticationMethod.Read.All está na matriz como NÃO necessária e fora das exigidas', () => {
  const entry = ENTRA_IDENTITY_CAPABILITIES.find((c) => c.permission === 'UserAuthenticationMethod.Read.All');
  assert(!!entry, 'a decisão está registrada na matriz, não omitida');
  eq(entry!.usage, 'NotRequired', 'marcada como não necessária');
  assert(/N\+1/.test(entry!.purpose), 'a justificativa técnica (N+1) está registrada');
  assert(/NÃO conceda/.test(entry!.action), 'a ação orienta a não conceder por causa do AEGIS');
  assert(
    !requiredPermissions(ENTRA_IDENTITY_CAPABILITIES).includes('UserAuthenticationMethod.Read.All'),
    'jamais entra na lista de permissões exigidas',
  );
});

test('a dependência de licença das capacidades de risco é declarada', () => {
  const licensed = licenseDependentCapabilities(ENTRA_IDENTITY_CAPABILITIES).map((c) => c.permission);
  assert(licensed.includes('IdentityRiskyUser.Read.All'), 'risky users declara dependência de licença');
  assert(licensed.includes('IdentityRiskEvent.Read.All'), 'risk detections declara dependência de licença');
  const detection = ENTRA_IDENTITY_CAPABILITIES.find((c) => c.permission === 'IdentityRiskEvent.Read.All')!;
  assert(/P1 ou P2/.test(detection.licenseNote!), 'a nota cita o requisito oficial P1/P2');
  assert(/parcial/.test(detection.licenseNote!), 'a nota reconhece cobertura parcial com P1');
});

test('o catálogo não afirma que o consentimento foi concedido', () => {
  assert(/não afirma que o consentimento foi concedido/.test(PERMISSION_CATALOG_CAVEAT), 'ressalva explícita');
  assert(/após a coleta/.test(PERMISSION_CATALOG_CAVEAT), 'aponta a tentativa tipada como fonte da verdade');
  const keys = Object.keys(ENTRA_IDENTITY_CAPABILITIES[0]);
  assert(!keys.includes('granted'), 'a matriz não carrega um campo de "concedida"');
});

test('cada uso da matriz tem rótulo legível', () => {
  eq(permissionUsageLabel('Consumed'), 'Já consumida', 'rótulo de consumida');
  eq(permissionUsageLabel('NewForIdentityRisk'), 'Nova — risco de identidade', 'rótulo de nova');
  eq(permissionUsageLabel('NotRequired'), 'Não necessária neste pacote', 'rótulo de dispensada');
});

// ---- resultado ---------------------------------------------------------------------------------
console.log(`\n${count - failures}/${count} testes passaram.`);
if (failures > 0) {
  process.exit(1);
}
