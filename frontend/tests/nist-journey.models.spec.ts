/**
 * [AEGIS-NIST-JOURNEY-01] Testes de LÓGICA PURA da jornada NIST, dos Dashboards e da evolução mensal.
 *
 * Fixam o que a tela não pode errar:
 *   (1) ausência de nível nunca vira zero: lacuna e médias ausentes são "Indeterminada"/"—";
 *   (2) o formulário segue as regras do servidor (escala 1–5, "não se aplica" com justificativa e sem níveis);
 *   (3) a avaliação/escopo vigente vem da URL, depois da lembrança, depois da mais recente — id desconhecido é ignorado;
 *   (4) os Dashboards distinguem sem avaliação, zero, parcial, desatualizado e demonstração, e só listam findings
 *       reprovados por severidade;
 *   (5) a evolução mensal não inventa mês nem liga meses não consecutivos ou incomparáveis;
 *   (6) [JOURNEY-02] a rodada vem da URL, da lembrança ou da mais recente — nunca de outra avaliação; o período segue as
 *       regras do servidor; resultado de procedimento ≠ método; achado exige fundamentação; responsável é vínculo, contato
 *       externo ou texto — nunca os três misturados; a maturidade 1–5 tem série própria na evolução mensal.
 */
import {
  NIST_FUNCTIONS,
  NistAssessment,
  NistCycle,
  averageText,
  cyclePeriodProblem,
  cyclePeriodText,
  findingDraftFrom,
  findingProblem,
  monthPeriod,
  planTransitionLabel,
  procedureResultProblem,
  quarterPeriod,
  selectionKey,
  stateLabel,
  suggestNextCycle,
  toFindingRequest,
  toProcedureRequest,
  draftFrom,
  draftGap,
  draftProblem,
  gapText,
  levelLabel,
  nistCategoryLabel,
  nistFunctionBySlug,
  nistFunctionTitle,
  resolveSelection,
  scopeCoverage,
  selectionParams,
  toSaveRequest,
} from '../src/app/models/nist.models';
import { knightReading, nistPostureReading, prioritizedFindings, scoreText } from '../src/app/models/dashboards.models';
import { KnightAssessment, KnightIndicator } from '../src/app/models/knight.models';
import { WorkspaceOverall } from '../src/app/models/workspace.models';
import { PostureMonthlyPoint, monthShort, monthlyCells } from '../src/app/models/posture-history.models';

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

console.log('nist-journey.models');

test('seis funções na ordem oficial, com nome em português e código', () => {
  eq(NIST_FUNCTIONS.map((f) => f.code).join(','), 'GV,ID,PR,DE,RS,RC', 'ordem');
  eq(nistFunctionTitle(NIST_FUNCTIONS[0]), 'Govern — Governar', 'título');
  eq(nistFunctionBySlug('RC')?.label, 'Recuperar', 'slug em maiúscula');
  eq(nistFunctionBySlug('xx'), null, 'função desconhecida');
  eq(nistCategoryLabel('GV.OV'), 'Supervisão', 'categoria humanizada');
  eq(nistCategoryLabel('ZZ.QQ', 'Something (ZZ.QQ)'), 'Something', 'sem tradução usa o nome oficial sem o código');
});

test('ausência não é zero: textos de média, lacuna e nível', () => {
  eq(averageText(null), '—', 'média ausente');
  eq(gapText(null), 'Indeterminada', 'lacuna ausente');
  eq(gapText(0), '0', 'lacuna zero real');
  eq(gapText(2), '+2', 'lacuna positiva');
  eq(levelLabel(null), 'Sem nível', 'nível ausente');
  eq(levelLabel(2), '2 · Documentado', 'nível');
  eq(scoreText(null), '—', 'nota ausente');
  eq(scoreText(0), '0', 'nota zero avaliada');
});

test('rascunho: lacuna só com os dois níveis; não se aplica descarta níveis e exige justificativa', () => {
  const d = draftFrom(null);
  eq(draftProblem(d), 'Informe um nível, uma justificativa ou uma anotação.', 'vazio não grava');
  eq(draftGap({ ...d, currentLevel: 2 }), null, 'sem alvo: indeterminada');
  eq(draftGap({ ...d, currentLevel: 2, targetLevel: 4 }), 2, 'par completo');
  eq(draftProblem({ ...d, currentLevel: 6 }), 'Os níveis vão de 1 a 5.', 'fora da escala');
  eq(draftProblem({ ...d, notApplicable: true, rationale: 'curta' }) !== null, true, 'não se aplica sem justificativa');
  const na = { ...d, notApplicable: true, currentLevel: 3, targetLevel: 4, rationale: 'Sem federação no escopo avaliado.' };
  eq(draftProblem(na), null, 'não se aplica justificado');
  eq(draftGap(na), null, 'não se aplica não tem lacuna');
  const req = toSaveRequest(na, 3);
  eq(req.currentLevel, null, 'nível descartado (atual)');
  eq(req.targetLevel, null, 'nível descartado (alvo)');
  eq(req.expectedVersion, 3, 'versão lida vai no pedido');
  eq(toSaveRequest({ ...d, ownerName: '   ' }, 0).ownerName, null, 'texto em branco vira nulo');
});

const assessment = (id: string, scopes: string[]): NistAssessment => ({
  id, name: id, description: null, status: 'Draft', startDate: null, endDate: null, methodologyVersion: 'aegis-methodology-v1',
  frameworkName: 'NIST CSF 2.0', createdAt: '2026-10-01T00:00:00Z', lastReviewedAt: null,
  scopes: scopes.map((s) => ({ id: s, name: s, description: null, subcategories: 106, evaluated: 0, notApplicable: 0, inProgress: 0, withEvidence: 0, lastReviewedAt: null })),
});

test('seleção: URL vence, depois a lembrança, depois a mais recente; id desconhecido é ignorado', () => {
  const list = [assessment('a2', ['s21']), assessment('a1', ['s11', 's12'])];
  eq(resolveSelection(list, { assessmentId: 'a1', scopeId: 's12' }, null)?.scope?.id, 's12', 'pedido da URL');
  eq(resolveSelection(list, { assessmentId: 'a1', scopeId: 'xx' }, null)?.scope?.id, 's11', 'escopo desconhecido → primeiro');
  eq(resolveSelection(list, { assessmentId: 'zz', scopeId: null }, { assessmentId: 'a1', scopeId: 's12' })?.scope?.id, 's12', 'lembrança');
  eq(resolveSelection(list, { assessmentId: null, scopeId: null }, { assessmentId: 'apagada', scopeId: 'x' })?.assessment.id, 'a2', 'mais recente');
  eq(resolveSelection([], { assessmentId: null, scopeId: null }, null), null, 'sem avaliação');
  eq(selectionParams(resolveSelection(list, { assessmentId: 'a1', scopeId: 's11' }, null)).escopo, 's11', 'parâmetros de URL');
  eq(scopeCoverage({ subcategories: 106, evaluated: 10, notApplicable: 3 }), 12.3, 'cobertura conta não aplicáveis como decididas');
});

const indicator = (id: string, status: string, severity: string, affected: number): KnightIndicator =>
  ({ indicatorId: id, title: id, status, severity, affectedObjectCount: affected } as unknown as KnightIndicator);
const knight = (over: Partial<KnightAssessment>): KnightAssessment =>
  ({
    id: 'r1', isDemo: false, sourceState: 'Completed', score: 60, coverage: 90, startedAt: '2026-10-01T10:00:00Z', completedAt: '2026-10-01T10:05:00Z',
    counts: { passed: 5, exposed: 3, mitigated: 0, notEvaluated: 0, error: 0, notApplicable: 0 }, indicators: [], ...over,
  } as unknown as KnightAssessment);
const now = new Date('2026-10-03T12:00:00Z');

test('dashboards: sem avaliação, zero, parcial, desatualizada e demonstração são estados distintos', () => {
  const none = knightReading(null, now);
  ok(!none.hasResult && none.labels[0] === 'Sem avaliação concluída', 'sem avaliação');
  const zero = knightReading(knight({ score: 0 }), now);
  ok(zero.hasResult && zero.zero, 'zero é resultado avaliado');
  const notScored = knightReading(knight({ score: null }), now);
  ok(!notScored.hasResult && !notScored.zero, 'sem nota não é zero');
  ok(knightReading(knight({ sourceState: 'PartialCollection' }), now).partial, 'coleta parcial');
  const stale = knightReading(knight({ completedAt: '2026-09-20T00:00:00Z' }), now);
  ok(stale.stale && stale.labels.some((l) => l.startsWith('Desatualizada')), 'desatualizada (7+ dias)');
  ok(!knightReading(knight({}), now).stale, 'recente');
  ok(knightReading(knight({ isDemo: true }), now).demo, 'demonstração');

  const notEvaluated = { evaluationState: 'NotEvaluated', percentage: null, coveragePercentage: 0, latestEvidenceAt: null } as unknown as WorkspaceOverall;
  ok(!nistPostureReading(notEvaluated, now).hasResult, 'postura sem controle avaliado');
  const zeroPosture = nistPostureReading({ evaluationState: 'Evaluated', percentage: 0, coveragePercentage: 5, latestEvidenceAt: '2026-10-02T00:00:00Z' } as unknown as WorkspaceOverall, now);
  ok(zeroPosture.zero && zeroPosture.partial && !zeroPosture.stale, 'postura zero com cobertura parcial');
});

test('findings prioritários: só reprovados, por severidade e depois por afetados', () => {
  const a = knight({
    indicators: [
      indicator('A', 'Passed', 'Critical', 9),
      indicator('B', 'Exposed', 'Medium', 50),
      indicator('C', 'Exposed', 'Critical', 1),
      indicator('D', 'Exposed', 'Critical', 7),
      indicator('E', 'Mitigated', 'Critical', 9),
      indicator('F', 'NotEvaluated', 'High', 0),
    ],
  });
  eq(prioritizedFindings(a, 3).map((i) => i.indicatorId).join(','), 'D,C,B', 'ordem');
  eq(prioritizedFindings(null).length, 0, 'sem avaliação');
});

const point = (month: string, score: number | null, comparable: boolean): PostureMonthlyPoint =>
  ({ month, score, comparableWithPrevious: comparable, breakReasons: comparable ? [] : ['DifferentCatalogVersion'] } as unknown as PostureMonthlyPoint);

test('evolução mensal: mês sem publicação fica vazio; liga só vizinhos comparáveis', () => {
  const months = ['2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01'];
  const cells = monthlyCells(months, [point('2026-07-01', 40, false), point('2026-08-01', 45, true), point('2026-10-01', 50, true)]);
  eq(cells[2].point, null, 'setembro sem ponto (nada interpolado)');
  eq(cells[1].connected, true, 'agosto liga a julho');
  eq(cells[3].connected, false, 'outubro não liga a julho/agosto por cima de setembro');
  const restart = monthlyCells(months.slice(0, 2), [point('2026-07-01', 40, false), point('2026-08-01', 45, false)]);
  eq(restart[1].connected, false, 'catálogo diferente não liga');
  eq(monthShort('2026-10-01'), 'out/26', 'rótulo do mês');
});


// ---- [AEGIS-NIST-JOURNEY-02] rodadas, procedimentos, achados e responsáveis --------------------------

const cyc = (id: string, start: string, end: string, kind: NistCycle['periodKind'] = 'Monthly'): NistCycle => ({
  id, name: id, periodKind: kind, periodStart: start, periodEnd: end, status: 'Open', seedFromCycleId: null, seedFromCycleName: null, seedMode: 'None',
  createdAt: '2026-10-01T00:00:00Z', createdByName: null, closedAt: null, closedByName: null, version: 1, publications: 0,
});
const withCycles = (id: string, cycles: NistCycle[], progress?: string): NistAssessment => ({
  ...assessment(id, [`${id}-s`]), cycles, progressCycleId: progress ?? null,
});

test('seleção com rodada: URL vence; desconhecida → a do andamento; rodada de outra avaliação nunca é aplicada', () => {
  const a1 = withCycles('a1', [cyc('c12', '2026-10-01', '2026-10-31'), cyc('c11', '2026-09-01', '2026-09-30')], 'c12');
  const a2 = withCycles('a2', [cyc('c21', '2026-10-01', '2026-10-31')]);
  const list = [a2, a1];
  eq(resolveSelection(list, { assessmentId: 'a1', cycleId: 'c11', scopeId: null }, null)?.cycle?.id, 'c11', 'rodada pedida');
  eq(resolveSelection(list, { assessmentId: 'a1', cycleId: 'xx', scopeId: null }, null)?.cycle?.id, 'c12', 'desconhecida → andamento');
  eq(resolveSelection(list, { assessmentId: 'a1', cycleId: 'c21', scopeId: null }, null)?.cycle?.id, 'c12', 'rodada de outra avaliação ignorada');
  eq(resolveSelection(list, { assessmentId: null, scopeId: null }, { assessmentId: 'a1', cycleId: 'c11', scopeId: 'a1-s' })?.cycle?.id, 'c11', 'lembrança');
  eq(resolveSelection(list, { assessmentId: null, scopeId: null }, null)?.cycle?.id, 'c21', 'mais recente sem progresso → primeira');
  const sel = resolveSelection(list, { assessmentId: 'a1', cycleId: 'c11', scopeId: 'a1-s' }, null);
  eq(selectionParams(sel).rodada, 'c11', 'URL carrega a rodada');
  ok(selectionKey(sel) !== selectionKey(resolveSelection(list, { assessmentId: 'a1', cycleId: 'c12', scopeId: 'a1-s' }, null)), 'rodada muda a identidade do contexto');
  eq(resolveSelection([assessment('legado', ['s'])], { assessmentId: 'legado', scopeId: 's' }, null)?.cycle, null, 'sem rodadas: sem rodada (nunca inventada)');
});

test('período da rodada: mês e trimestre civis inteiros; outro até três anos; textos', () => {
  eq(monthPeriod(2024, 2).end, '2024-02-29', 'fevereiro bissexto');
  eq(quarterPeriod(2026, 4).start + '|' + quarterPeriod(2026, 4).end, '2026-10-01|2026-12-31', 'T4');
  eq(cyclePeriodProblem('Monthly', '2026-10-01', '2026-10-31'), null, 'mês inteiro');
  ok(cyclePeriodProblem('Monthly', '2026-10-02', '2026-10-31') !== null, 'mês parcial recusado');
  ok(cyclePeriodProblem('Quarterly', '2026-10-01', '2026-11-30') !== null, 'trimestre parcial recusado');
  ok(cyclePeriodProblem('Other', '2026-10-10', '2026-10-01') !== null, 'fim antes do início');
  ok(cyclePeriodProblem('Other', '2026-01-01', '2029-06-01') !== null, 'acima de três anos');
  eq(cyclePeriodProblem('Other', '2026-10-10', '2026-11-20'), null, 'intervalo livre');
  eq(cyclePeriodText(cyc('x', '2026-10-01', '2026-10-31')), 'out/2026', 'mensal');
  eq(cyclePeriodText(cyc('x', '2026-10-01', '2026-12-31', 'Quarterly')), 'T4 2026', 'trimestral');
  eq(cyclePeriodText(cyc('x', '2026-10-10', '2026-11-20', 'Other')), '10/10/2026 a 20/11/2026', 'outro');
  const next = suggestNextCycle([cyc('a', '2026-11-01', '2026-11-30'), cyc('b', '2026-12-01', '2026-12-31')], 'Monthly', '2026-10-06');
  eq(next.start + '|' + next.end, '2027-01-01|2027-01-31', 'próxima rodada depois da mais recente (virada de ano)');
  eq(suggestNextCycle([], 'Quarterly', '2026-10-06').name, 'T4 2026', 'sem rodada: trimestre atual');
});

test('procedimento: escolher o método não é resultado; realizado exige data, observação e conclusão', () => {
  const today = '2026-10-06';
  const base = { status: 'Planned' as const, outcome: null, observation: '', performedOn: '', evidenceIds: [] };
  eq(procedureResultProblem(base, today), null, 'planejado não exige resultado');
  eq(procedureResultProblem({ ...base, status: 'Performed' }, today), 'Um procedimento realizado precisa da conclusão observada.', 'sem conclusão');
  ok(procedureResultProblem({ ...base, status: 'Performed', outcome: 'Unsatisfactory', performedOn: '2026-10-07', observation: '30% sem dono.' }, today)!.includes('futuro'), 'data futura');
  ok(procedureResultProblem({ ...base, status: 'NotPerformed', observation: 'curto' }, today) !== null, 'não realizado exige motivo');
  const req = toProcedureRequest({ ...base, status: 'InProgress', outcome: 'Satisfactory', performedOn: '2026-10-01', observation: 'em curso' }, 4);
  eq(req.outcome, null, 'em execução não leva conclusão');
  eq(req.performedOn, null, 'nem data de realização');
  eq(req.expectedVersion, 4, 'versão lida');
});

test('achado: rascunho semeado com o documentado; exige fundamentação; plano opcional com responsável vinculado', () => {
  const d = {
    evaluation: { gaps: 'Planilha sem dono.', riskImpact: 'Ativo sem dono não é corrigido.', improvementGuidance: 'Atribuir donos.' },
    procedures: [{ status: 'Performed', outcome: 'Unsatisfactory', observation: '30% dos servidores sem dono.' }, { status: 'Planned', outcome: null, observation: 'x' }],
  } as never;
  const f = findingDraftFrom(d);
  eq(f.condition, 'Planilha sem dono.\n30% dos servidores sem dono.', 'condição com lacuna e procedimento insatisfatório');
  eq(f.severity, '', 'severidade nunca pré-escolhida');
  eq(findingProblem(f), 'Descreva o problema (título do achado).', 'título exigido');
  const full = { ...f, title: 'Inventário sem dono', impact: 'Vulnerabilidade sem tratamento.', severity: 'High' as const, severityRationale: 'curta',
    priority: 'High' as const, priorityRationale: 'Pré-requisito de outros controles.', planTitle: 'Atribuir donos' };
  eq(findingProblem(full), 'Justifique a severidade (pelo menos 10 caracteres).', 'severidade justificada');
  const ready = { ...full, severityRationale: 'Servidores de produção.', planOwnerMode: 'user' as const, planOwnerUserId: '' };
  eq(findingProblem(ready), 'Escolha o usuário responsável pelo plano.', 'usuário exigido no modo vínculo');
  const req = toFindingRequest({ ...ready, planOwnerUserId: 'u-1', planOwnerName: 'texto ignorado' });
  eq(req.plan?.responsible?.userId, 'u-1', 'vínculo enviado');
  eq(req.plan?.responsible?.name, null, 'sem texto misturado ao vínculo');
  eq(toFindingRequest({ ...ready, withPlan: false }).plan, null, 'sem plano');
});

test('responsável da prática: vínculo, externo ou texto — nunca misturados; texto legado continua texto', () => {
  const legacy = draftFrom({ ownerName: 'Fulano (texto antigo)', owner: { kind: 'Text', name: 'Fulano (texto antigo)', userId: null, contact: null, userActive: false } } as never);
  eq(legacy.ownerMode, 'text', 'texto legado não vira vínculo');
  const linked = draftFrom({ ownerName: 'Analista', owner: { kind: 'User', name: 'Analista', userId: 'u-9', contact: null, userActive: true } } as never);
  eq(linked.ownerMode, 'user', 'vínculo reconhecido');
  const req = toSaveRequest({ ...linked, rationale: 'x' }, 2);
  eq(req.ownerUserId, 'u-9', 'vínculo enviado');
  eq(req.ownerName, null, 'sem texto junto do vínculo');
  const ext = toSaveRequest({ ...linked, ownerMode: 'external', ownerName: 'Consultoria Demo', ownerContact: 'contato@demo.example.com' }, 2);
  eq(ext.ownerIsExternal, true, 'externo');
  eq(ext.ownerUserId, null, 'externo sem vínculo');
  eq(draftProblem({ ...linked, ownerMode: 'external', ownerName: '' }), 'Informe o nome do contato externo responsável.', 'externo exige nome');
  eq(stateLabel('PendingConfirmation'), 'Aguarda confirmação', 'estado de conteúdo herdado/importado');
  eq(planTransitionLabel('Concluido', 'EmAndamento'), 'Reabrir o plano', 'reabertura nomeada');
});

console.log(`\n${count - failures}/${count} testes passaram (nist-journey.models).`);
if (failures > 0) process.exit(1);
