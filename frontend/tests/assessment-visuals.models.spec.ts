/**
 * [AEGIS-ASSESSMENT-VISUALS-01] Testes de LÓGICA PURA do painel executivo, dos gráficos e da evolução mensal.
 *
 * Fixam o que a tela não pode errar:
 *   (1) a rosca do KNIGHT é a base aplicável (não aplicáveis fora); as empilhadas somam cada controle uma vez; o ranking diz o critério;
 *   (2) função NIST sem avaliação fica sem coluna (nunca zero) e o radar incompleto não fecha o perfil;
 *   (3) a linha mensal não interpola mês ausente nem liga pontos não vizinhos ou incomparáveis; KNIGHT 0–100 e NIST 1–5 em escalas próprias;
 *   (4) o resultado KNIGHT padrão segue uma regra explícita (consolidado das fontes elegíveis; demonstração nunca entra);
 *   (5) o tratamento conta cada plano uma vez e filtra pela origem; a atenção traz a origem de cada item;
 *   (6) a variação só aparece quando o servidor a calculou (pontos comparáveis), com o mês de referência.
 */
import {
  ChartSpec,
  chartSummary,
  historyLine,
  knightCoverageDonut,
  knightDomainBars,
  knightServiceStacked,
  layoutChart,
  niceMax,
  nistFunctionColumns,
  nistRadar,
  nistStatesDonut,
  ticks,
} from '../src/app/models/charts.models';
import { attentionItems, consolidableAssessed, defaultKnightView, knightStateLabel, planTreatment } from '../src/app/models/dashboards.models';
import { KnightAssessment, KnightIndicator, KnightSourceLatest } from '../src/app/models/knight.models';
import { NistProfile } from '../src/app/models/nist.models';
import { FrozenPostureHistory, PostureMonthlyPoint, deltaText, frozenAsSeries, monthOptions } from '../src/app/models/posture-history.models';
import { ActionPlan } from '../src/app/models/remediation.models';

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

console.log('assessment-visuals.models');

function ind(id: string, status: KnightIndicator['status'], severity: KnightIndicator['severity'], service: string, domain: string): KnightIndicator {
  return {
    indicatorId: id, title: `Controle ${id}`, category: 'PrivilegedAccess', severity, status, evidence: '', affectedObjectCount: status === 'Exposed' ? 2 : 0,
    nistCodes: [], mitreTechniques: [], recommendation: '', collectedAt: '2026-09-30T00:00:00Z', sourceType: 'MicrosoftEntraId', notEvaluatedReason: null,
    hasAffectedDetail: true, affectedDetailComplete: true, affectedDetailLimitation: null,
    presentation: { domain, domainLabel: domain, service, provider: 'Microsoft', platform: service } as never,
  };
}

function assessment(over: Partial<KnightAssessment> = {}): KnightAssessment {
  const indicators = [
    ind('AK-1', 'Exposed', 'Critical', 'Entra ID', 'Identidade'),
    ind('AK-2', 'Exposed', 'High', 'Exchange', 'Colaboração'),
    ind('AK-3', 'Passed', 'Medium', 'Entra ID', 'Identidade'),
    ind('AK-4', 'NotEvaluated', 'Low', 'Exchange', 'Colaboração'),
    ind('AK-5', 'Error', 'Low', 'Teams', 'Colaboração'),
    ind('AK-6', 'NotApplicable', 'Low', 'Teams', 'Colaboração'),
    ind('AK-7', 'Mitigated', 'Medium', 'Teams', 'Identidade'),
  ];
  return {
    id: 'run-1', mode: 'Live', isDemo: false, sourceType: 'MicrosoftEntraId', sourceState: 'Completed', source: 'Microsoft Entra ID', status: 'Completed',
    catalogVersion: 'ak-knight-v10', scoreFormulaVersion: 'knight-score-v1', startedAt: '2026-09-30T10:00:00Z', completedAt: '2026-09-30T10:05:00Z',
    score: 42, coverage: 66.7, counts: { passed: 1, exposed: 2, mitigated: 1, notEvaluated: 1, error: 1, notApplicable: 1 }, indicators,
    capabilities: [], advisory: null, advisoryFromAi: false, ...over,
  };
}

test('rosca KNIGHT: avaliados × não avaliados × erro sobre a base aplicável, não aplicáveis fora e ditos', () => {
  const d = knightCoverageDonut(assessment());
  eq(d.series[0].values.join(','), '4,1,1', 'avaliados (aprovado + reprovados + mitigado), não avaliado, erro');
  eq(d.max, 6, 'base aplicável');
  ok(d.basis!.includes('1 não aplicável'), 'não aplicáveis ditos na base');
  ok(chartSummary(d).includes('Total 6 controles'), 'alternativa textual com o total');
  eq(layoutChart(d).shapes.filter((s) => s.k === 'path').length, 3, 'uma fatia por parcela com valor');
});

test('empilhadas por serviço: cada controle uma vez, ordem por reprovados; ranking de domínios com critério', () => {
  const s = knightServiceStacked(assessment());
  const totals = s.categories.map((_, i) => s.series.reduce((t, x) => t + (x.values[i] ?? 0), 0));
  eq(totals.reduce((a, b) => a + b, 0), 7, 'soma = controles');
  eq(s.categories[0], 'Entra ID', 'empate em reprovados desempata pelo total');
  const d = knightDomainBars(assessment());
  eq(d.categories.join(','), 'Colaboração,Identidade', 'só domínios com reprovado');
  ok(d.description.includes('Ranking'), 'critério dito');
  ok(d.links!.every((l) => l?.query?.['status'] === 'Exposed'), 'link filtra os reprovados do domínio');
});

const profile: NistProfile = {
  assessmentId: 'a', scopeId: 's', cycleId: 'c', methodologyVersion: 'aegis-methodology-v1',
  overall: { code: 'ALL', current: 2.4, target: 3.6, gap: 1.2, subcategories: 24, withCurrent: 14, withTarget: 14, withGap: 14, notApplicable: 2, evaluated: 14 },
  functions: ['GV', 'ID', 'PR', 'DE', 'RS'].map((code, i) => ({ code, current: 2 + i * 0.2, target: 3.5, gap: 1.5 - i * 0.2, subcategories: 4, withCurrent: 3, withTarget: 3, withGap: 3, notApplicable: 0, evaluated: 3 })),
  categories: [], gaps: [{ code: 'DE.CM-01', title: 'Monitoramento', currentLevel: 1, targetLevel: 3, gap: 2, ownerName: null, improvementGuidance: null, openFindings: 1 }],
  indeterminateGaps: 0,
  states: { code: 'ALL', subcategories: 24, notEvaluated: 6, inProgress: 1, pendingConfirmation: 1, evaluated: 14, notApplicable: 2, reviewApproved: 14, reviewOutdated: 0 },
};
const ctx = { avaliacao: 'a', rodada: 'c', escopo: 's' };

test('NIST: função sem avaliação fica sem coluna (nunca zero); radar incompleto não fecha o perfil', () => {
  const c = nistFunctionColumns(profile, ctx);
  eq(c.categories.join(','), 'GV,ID,PR,DE,RS,RC', 'ordem oficial');
  eq(c.series[0].values[5], null, 'RC sem avaliação = nulo');
  eq(c.max, 5, 'escala 1–5');
  const l = layoutChart(c);
  ok(l.shapes.some((s) => s.k === 'text' && s.text === '— = sem avaliação'), 'ausência dita no gráfico');
  const r = layoutChart(nistRadar(profile, ctx));
  ok(!r.shapes.some((s) => s.k === 'polygon' && s.cls.startsWith('r-cur')), 'perfil atual incompleto não vira polígono fechado');
  ok(r.shapes.some((s) => s.k === 'line' && s.cls.startsWith('l-cur')), 'lados entre funções com valor');
  const d = nistStatesDonut(profile)!;
  eq(d.series[0].values.reduce<number>((a, b) => a + (b ?? 0), 0), 24, 'rosca soma o catálogo do escopo (inclui não se aplicam)');
  eq(ticks(c).join(','), '0,1,2,3,4,5', 'marcas inteiras na escala de maturidade');
  eq(niceMax([7]), 10, 'eixo de contagem arredondado');
});

function point(month: string, score: number | null, comparable: boolean, extra: Partial<PostureMonthlyPoint> = {}): PostureMonthlyPoint {
  return {
    month, snapshotId: month, capturedAt: `${month.slice(0, 7)}-20T00:00:00Z`, evaluationState: score === null ? 'NotEvaluated' : 'Evaluated', score, coverage: 80,
    evaluatedItems: 8, eligibleItems: 10, formulaVersion: 'knight-score-v1', catalogVersion: 'v10', schemaVersion: '2', sourceLabel: null, sourceRunId: null,
    publishedInMonth: 1, comparableWithPrevious: comparable, breakReasons: comparable ? [] : month === '2026-06-01' ? ['DifferentCatalogVersion'] : [], ...extra,
  };
}

test('linha mensal: mês ausente sem ponto, só vizinhos comparáveis ligados, escalas próprias', () => {
  const months = ['2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01', '2026-07-01'];
  const series = {
    type: 'Knight' as const, semanticFamily: 'knight:x', sourceType: 'MicrosoftEntraId', label: 'KNIGHT · Entra ID',
    points: [point('2026-03-01', 50, false), point('2026-04-01', 55, true), point('2026-06-01', 52, false), point('2026-07-01', 58, true)],
  };
  const c = historyLine(series, months);
  eq(c.series[0].values.join(','), '50,55,,52,58', 'maio sem publicação = nulo');
  eq(c.series[0].connect!.join(','), 'false,true,false,false,true', 'liga só mar→abr e jun→jul');
  eq(layoutChart(c).shapes.filter((s) => s.k === 'line' && s.cls.startsWith('l-')).length, 2, 'dois segmentos');
  eq(c.max, 100, 'KNIGHT 0–100');
  const mat = historyLine({ ...series, type: 'NistMaturity', points: [point('2026-03-01', null, false, { maturityCurrent: 2.1, maturityTarget: 3.5 })] }, months);
  eq(`${mat.min}-${mat.max}`, '1-5', 'NIST 1–5 em gráfico próprio');
  eq(mat.series.map((s) => s.label).join(','), 'Maturidade atual,Alvo', 'alvo como segunda série');
  ok(mat.series[1].dashed === true, 'alvo tracejado');
});

test('variação só com valor do servidor e mês de referência; período e histórico congelado', () => {
  eq(deltaText({ delta: 3, deltaFrom: '2026-04-01' }, 1), '+3,0 desde abr/26', 'variação com mês de referência');
  eq(deltaText({ delta: null, deltaFrom: null }, 1), '—', 'sem variação comparável');
  const opts = monthOptions(new Date(Date.UTC(2026, 9, 7)), 3);
  eq(opts.map((o) => o.value).join(','), '2026-10,2026-09,2026-08', 'meses de fim do período');
  const frozen = {
    schema: 'posture-history-v1', criterion: 'c', from: '2025-10-01', until: '2026-09-01', months: [], publicationMonth: '2026-09-01', includesThisPublication: true,
    relatedSeries: [], basisFingerprint: 'x', points: [point('2026-09-01', 60, false, { isThisPublication: true })],
    series: { type: 'Knight', semanticFamily: 'knight:x', label: 'KNIGHT', instrument: 'AEGIS KNIGHT · nota 0–100', scaleMin: 0, scaleMax: 100, coverageBasis: 'b',
      formulaVersion: 'f', catalogVersion: 'c', sourceType: null, sourceLabel: null, composition: null, nistAssessmentId: null, nistScopeId: null },
  } as FrozenPostureHistory;
  const s = frozenAsSeries(frozen);
  eq(s.points[0].isThisPublication, true, 'o ponto desta publicação segue marcado');
  eq(s.scaleMax, 100, 'escala da série congelada');
});

function latest(source: KnightSourceLatest['source'], a: KnightAssessment | null): KnightSourceLatest {
  return { source, slug: source, label: source, assessment: a, unfinishedAttempt: null };
}

test('resultado KNIGHT padrão: consolidado das elegíveis avaliadas (sem demonstração), senão a mais recente — com motivo', () => {
  const srcs = [
    latest('MicrosoftEntraId', assessment()),
    latest('MicrosoftAzure', assessment({ id: 'run-az', sourceType: 'MicrosoftAzure' })),
    latest('MicrosoftTeams', null),
    latest('Demo', assessment({ id: 'demo', isDemo: true, sourceType: 'Demo' })),
  ];
  eq(consolidableAssessed(srcs).join(','), 'MicrosoftEntraId,MicrosoftAzure', 'só elegíveis com avaliação concluída');
  const d = defaultKnightView(srcs)!;
  eq(d.view.kind, 'consolidated', 'consolidado');
  ok(d.reason.includes('2 fonte'), 'motivo explícito');
  const g = defaultKnightView([latest('GoogleWorkspace', assessment({ id: 'g', sourceType: 'GoogleWorkspace', completedAt: '2026-09-01T00:00:00Z' })),
    latest('Demo', assessment({ id: 'demo', isDemo: true, sourceType: 'Demo', completedAt: '2026-09-20T00:00:00Z' }))])!;
  eq(g.view.kind === 'source' ? g.view.source : '', 'Demo', 'sem elegível: a concluída mais recente');
  eq(defaultKnightView([]), null, 'sem avaliação');
  eq(knightStateLabel({ hasResult: true, zero: false, partial: true, stale: true, demo: false, ageDays: 9, labels: [] }).label, 'Desatualizada', 'desatualizada pesa mais que parcial');
  eq(knightStateLabel(null).label, 'Sem avaliação', 'sem avaliação');
});

function plan(id: string, kind: ActionPlan['originKind'], status: ActionPlan['status'], overdue: boolean): ActionPlan {
  return { id, originKind: kind, status, isOverdue: overdue, isActive: status !== 'Concluido', title: `Plano ${id}`, dueDate: '2026-08-01', knightIndicatorId: kind === 'KnightFinding' ? 'AK-1' : null } as ActionPlan;
}

test('tratamento: cada plano uma vez, filtro de origem; atenção com origem e no máximo seis itens', () => {
  const plans = [plan('1', 'KnightFinding', 'EmAndamento', true), plan('1', 'KnightFinding', 'EmAndamento', true), plan('2', 'NistFinding', 'AguardandoValidacao', false),
    plan('3', 'DeviceVulnerability', 'Concluido', false)];
  const all = planTreatment(plans, 'all');
  eq(all.total, 3, 'plano repetido conta uma vez');
  eq(`${all.byOrigin.knight}/${all.byOrigin.nist}/${all.byOrigin.other}`, '1/1/1', 'por origem');
  eq(planTreatment(plans, 'nist').awaitingValidation, 1, 'filtro NIST');
  eq(planTreatment(plans, 'knight').overdue, 1, 'atrasado do KNIGHT');
  const items = attentionItems(assessment(), { gaps: profile.gaps, query: { avaliacao: 'a' } }, plans);
  ok(items.length <= 6, 'lista curta');
  eq(items[0].origin, 'KNIGHT', 'reprovado crítico primeiro');
  ok(items.some((i) => i.origin === 'NIST' && i.route.join('/') === '/nist/de/DE.CM-01'), 'lacuna NIST com achado aberto e link da subcategoria');
  ok(items.some((i) => i.origin === 'Plano'), 'plano atrasado');
});

test('cada gráfico tem título, descrição, escala e alternativa textual', () => {
  const specs: ChartSpec[] = [knightCoverageDonut(assessment()), knightServiceStacked(assessment()), nistFunctionColumns(profile, ctx)];
  for (const s of specs) ok(!!s.title && !!s.description && !!s.unit && chartSummary(s).length > 10, `${s.id} acessível`);
});

console.log(`\n${count - failures}/${count} testes passaram (assessment-visuals.models).`);
if (failures > 0) process.exit(1);
