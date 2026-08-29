/**
 * [AEGIS-MVP-LANGUAGE-01] Testes de LÓGICA PURA do modelo de scoring (frontend).
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Estas asserções cobrem as regras que
 * NÃO dependem do DOM: os controles NotEvaluated agora aparecem na lista do pilar (Respond/Recover deixam de
 * ficar vazios), MAS seguem fora do score (numerador e denominador); nada avaliado não vira 0% enganoso; a
 * ordenação leva o risco ao topo e o não avaliado ao fim; o título específico substitui o nome da categoria;
 * fonte/data nulas atravessam como nulas; e os Pontos Cegos passam a incluir o não avaliado, distinguindo
 * Unsupported (sem fingir lacuna de telemetria/documento). Compiladas por `tsc` (CommonJS) e executadas por `node`.
 */
import {
  PILLARS,
  TenantControlStateDto,
  buildGapBalance,
  buildPillarGapAnalysis,
  buildPillarView,
  notEvaluatedLabel,
  sourceLabelOf,
  toControlView,
} from '../src/app/models/scoring.models';

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

/** Constrói um DTO com defaults seguros (NotEvaluated puro), sobrescrevendo só o que o teste precisa. */
function mkDto(over: Partial<TenantControlStateDto> & { subcategoryCode: string }): TenantControlStateDto {
  return {
    subcategoryId: `id-${over.subcategoryCode}`,
    subcategoryCode: over.subcategoryCode,
    scorePoints: 0,
    maxScorePoints: 10,
    controlStatus: 'NotEvaluated',
    reason: null,
    aiEvidence: null,
    lastEvaluatedAt: null,
    lastVerdictSource: null,
    checks: [],
    severity: 'Informational',
    historicalCompliance: [],
    telemetryEvidence: null,
    remediationPlan: null,
    aiConfidenceScore: null,
    threatLandscape: [],
    mttdMinutes: null,
    mttrMinutes: null,
    missingRequirements: [],
    title: null,
    summary: null,
    impact: null,
    initialAction: null,
    officialDescription: null,
    notEvaluatedReason: null,
    ...over,
  };
}

// ---- 1) buildPillarView PRESERVA NotEvaluated na lista -----------------------------------------
test('buildPillarView mantém os controles NotEvaluated na lista (não os descarta)', () => {
  const view = buildPillarView(PILLARS.RS, [
    mkDto({ subcategoryCode: 'RS.MA-01', controlStatus: 'Compliant', scorePoints: 10, maxScorePoints: 10 }),
    mkDto({ subcategoryCode: 'RS.MA-02', controlStatus: 'NotEvaluated', notEvaluatedReason: 'TelemetryRequired' }),
  ]);
  eq(view.controls.length, 2, 'os dois controles aparecem na lista');
  assert(view.controls.some((c) => c.status === 'NotEvaluated'), 'o NotEvaluated está presente');
  eq(view.notEvaluated, 1, 'contagem de não avaliados');
});

// ---- 2) NotEvaluated FORA do numerador e do denominador do score -------------------------------
test('o percentual usa somente os avaliados — NotEvaluated não entra no denominador', () => {
  const view = buildPillarView(PILLARS.RS, [
    mkDto({ subcategoryCode: 'RS.MA-01', controlStatus: 'Compliant', scorePoints: 10, maxScorePoints: 10 }),
    mkDto({ subcategoryCode: 'RS.MA-02', controlStatus: 'NotEvaluated', maxScorePoints: 10 }),
  ]);
  // 10/10 = 100%. Se o NotEvaluated entrasse no denominador, seria 10/20 = 50% — o bug que corrigimos.
  eq(view.compliancePct, 100, 'NotEvaluated não dilui o percentual (fora do denominador)');
});

// ---- 3) nada avaliado → compliancePct NULL (não 0% enganoso) -----------------------------------
test('nenhum controle avaliado não produz 0% — o percentual é nulo', () => {
  const view = buildPillarView(PILLARS.RC, [
    mkDto({ subcategoryCode: 'RC.RP-01', controlStatus: 'NotEvaluated' }),
    mkDto({ subcategoryCode: 'RC.RP-02', controlStatus: 'NotEvaluated' }),
  ]);
  eq(view.compliancePct, null, '0/0 é "não avaliado", não 0%');
  eq(view.total, 2, 'os controles seguem na lista');
  eq(view.notEvaluated, 2, 'ambos contam como não avaliados');
});

// ---- 4) ordenação por status e depois por código ----------------------------------------------
test('ordena NonCompliant → Mitigated → Compliant → NotEvaluated, empate por código', () => {
  const view = buildPillarView(PILLARS.RS, [
    mkDto({ subcategoryCode: 'RS.MA-05', controlStatus: 'NotEvaluated' }),
    mkDto({ subcategoryCode: 'RS.MA-03', controlStatus: 'Compliant', scorePoints: 5, maxScorePoints: 5 }),
    mkDto({ subcategoryCode: 'RS.MA-02', controlStatus: 'NonCompliant', maxScorePoints: 5 }),
    mkDto({ subcategoryCode: 'RS.MA-04', controlStatus: 'MitigatedByThirdParty', scorePoints: 2, maxScorePoints: 5 }),
    mkDto({ subcategoryCode: 'RS.MA-01', controlStatus: 'NonCompliant', maxScorePoints: 5 }),
  ]);
  const order = view.controls.map((c) => c.code).join(',');
  eq(order, 'RS.MA-01,RS.MA-02,RS.MA-04,RS.MA-03,RS.MA-05', 'ordem por status e código');
});

// ---- 5) título ESPECÍFICO substitui o nome repetido da categoria -------------------------------
test('o título específico do backend é o título do controle (não o nome da categoria)', () => {
  const cv = toControlView(
    mkDto({
      subcategoryCode: 'PR.AA-01',
      controlStatus: 'NotEvaluated',
      title: 'Controlar o ciclo de vida de identidades e credenciais',
      summary: 'Garante que contas e credenciais sejam criadas, revisadas e removidas.',
    }),
  );
  eq(cv.title, 'Controlar o ciclo de vida de identidades e credenciais', 'título específico preservado');
  assert(cv.title !== 'Identidade e Acesso', 'não é o nome genérico da categoria');
  eq(cv.summary, 'Garante que contas e credenciais sejam criadas, revisadas e removidas.', 'resumo preservado');
});

// ---- 6) fonte e data NULAS atravessam como nulas (sem rótulos falsos) --------------------------
test('fonte e data nulas do NotEvaluated chegam ao ControlView como null', () => {
  const cv = toControlView(mkDto({ subcategoryCode: 'RC.CO-04', controlStatus: 'NotEvaluated' }));
  eq(cv.source, null, 'sem fonte de veredito — a tela não rotula "Documental"');
  eq(cv.evaluatedAt, null, 'sem data — a tela não mostra data inválida');
});

// ---- 7) Respond e Recover NÃO ficam vazios com DTOs NotEvaluated -------------------------------
test('Respond e Recover mostram os controles do catálogo mesmo sem nenhum avaliado', () => {
  const rs = buildPillarView(PILLARS.RS, [
    mkDto({ subcategoryCode: 'RS.MA-01', controlStatus: 'NotEvaluated' }),
    mkDto({ subcategoryCode: 'RS.CO-02', controlStatus: 'NotEvaluated' }),
  ]);
  const rc = buildPillarView(PILLARS.RC, [mkDto({ subcategoryCode: 'RC.RP-01', controlStatus: 'NotEvaluated' })]);
  assert(rs.controls.length === 2, 'Respond lista seus controles não avaliados');
  assert(rc.controls.length === 1, 'Recover lista seus controles não avaliados');
});

// ---- 8) buildPillarGapAnalysis INCLUI os não avaliados como pontos cegos -----------------------
test('a análise de pontos cegos inclui os controles NotEvaluated', () => {
  const gap = buildPillarGapAnalysis(PILLARS.RS, [
    mkDto({ subcategoryCode: 'RS.MA-01', controlStatus: 'Compliant', scorePoints: 5, maxScorePoints: 5 }),
    mkDto({ subcategoryCode: 'RS.MA-02', controlStatus: 'NotEvaluated', notEvaluatedReason: 'Unsupported' }),
    mkDto({
      subcategoryCode: 'RS.MA-03',
      controlStatus: 'NotEvaluated',
      notEvaluatedReason: 'TelemetryRequired',
      missingRequirements: [{ type: 'Telemetry', sourceIdentifier: 'telemetria', description: 'Sem sinal.' }],
    }),
  ]);
  const blindCodes = gap.blindSpots.map((c) => c.code).sort().join(',');
  eq(blindCodes, 'RS.MA-02,RS.MA-03', 'os dois NotEvaluated são pontos cegos');
  eq(gap.covered.length, 1, 'o Compliant com evidência fica em "monitorados"');
  eq(gap.covered[0].code, 'RS.MA-01', 'o coberto é o avaliado com evidência');
});

// ---- 9) Unsupported NÃO é lacuna de telemetria/documentação — conta em unsupportedGaps ----------
test('Unsupported não é contado como lacuna de telemetria nem de documentação', () => {
  const gap = buildPillarGapAnalysis(PILLARS.RS, [
    mkDto({ subcategoryCode: 'RS.MA-02', controlStatus: 'NotEvaluated', notEvaluatedReason: 'Unsupported' }),
    mkDto({
      subcategoryCode: 'RS.MA-03',
      controlStatus: 'NotEvaluated',
      notEvaluatedReason: 'TelemetryRequired',
      missingRequirements: [{ type: 'Telemetry', sourceIdentifier: 'telemetria', description: 'Sem sinal.' }],
    }),
  ]);
  eq(gap.unsupportedGaps, 1, 'o Unsupported conta à parte');
  eq(gap.telemetryGaps, 1, 'só o TelemetryRequired conta como lacuna de telemetria');
  eq(gap.documentationGaps, 0, 'nenhuma lacuna documental foi inventada');
});

// ---- 10) rótulos curtos dos quatro motivos de não-avaliação ------------------------------------
test('notEvaluatedLabel mapeia os quatro motivos deterministicamente', () => {
  eq(notEvaluatedLabel('TelemetryRequired'), 'Aguardando telemetria', 'telemetria');
  eq(notEvaluatedLabel('DocumentationRequired'), 'Aguardando validação documental', 'documentação');
  eq(notEvaluatedLabel('BothRequired'), 'Aguardando telemetria e documento', 'ambos');
  eq(notEvaluatedLabel('Unsupported'), 'Avaliação ainda não suportada', 'não suportado');
});

// ---- 11) balanço executivo usa TÍTULO específico, não o nome da categoria ----------------------
test('buildGapBalance: dois controles da mesma categoria geram títulos executivos distintos', () => {
  const gap = (): { type: 'Telemetry'; sourceIdentifier: string; description: string } => ({
    type: 'Telemetry',
    sourceIdentifier: 'ELIGIBLE_TELEMETRY_SOURCE',
    description: 'Nenhuma telemetria elegível foi avaliada para este controle.',
  });
  const bal = buildGapBalance([
    mkDto({
      subcategoryCode: 'PR.AA-01',
      controlStatus: 'NotEvaluated',
      title: 'Controlar o ciclo de vida de identidades e credenciais',
      maxScorePoints: 10,
      missingRequirements: [gap()],
    }),
    mkDto({
      subcategoryCode: 'PR.AA-03',
      controlStatus: 'NotEvaluated',
      title: 'Autenticar usuários, serviços e dispositivos',
      maxScorePoints: 10,
      missingRequirements: [gap()],
    }),
  ]);
  const labels = bal.topBlindSpots.map((b) => b.label);
  eq(labels.length, 2, 'dois pontos cegos no balanço');
  assert(labels[0] !== labels[1], 'a mesma categoria (PR.AA) gera títulos executivos DISTINTOS');
  assert(!labels.includes('Identidade e Acesso'), 'nunca o nome genérico da categoria como título');
  assert(labels.includes('Autenticar usuários, serviços e dispositivos'), 'usa o título específico do backend');
});

// ---- 12) sourceLabelOf traduz os tokens genéricos provider-neutral -----------------------------
test('sourceLabelOf traduz os identificadores genéricos sem vazar token de máquina', () => {
  eq(sourceLabelOf('ELIGIBLE_TELEMETRY_SOURCE'), 'Fonte de telemetria compatível', 'telemetria genérica');
  eq(sourceLabelOf('TELEMETRY_AND_VALIDATION'), 'Telemetria e validação', 'híbrida genérica');
  eq(sourceLabelOf('MANUAL_AUDIT_REQUIRED'), 'Validação manual', 'documental');
  eq(sourceLabelOf('Entra ID'), 'Entra ID', 'nome real de ferramenta passa direto (controle avaliado)');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend (scoring) aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend (scoring) falharam`);
