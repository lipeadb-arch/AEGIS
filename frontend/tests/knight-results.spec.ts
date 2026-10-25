/**
 * [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Apresentação dos resultados do KNIGHT: lógica pura e o componente REAL dos Dashboards.
 *
 * Fixam o que a leitura executiva não pode errar:
 *   (1) fontes INCLUÍDAS ≠ fontes com controles AVALIADOS — contadas dos indicadores do resultado, respeitando a seleção;
 *   (2) a nota fica ao lado da cobertura (numerador e denominador) e a leitura parcial avisa; sem controle avaliado não há nota;
 *   (3) prioridades: só reprovados, com risco/impacto/orientação do modelo (nada inventado) e o caminho para o detalhe;
 *   (4) limitações equivalentes agrupadas — tempo esgotado ≠ circuito aberto, permissão ≠ licença — sem perder a capacidade;
 *   (5) a origem da interpretação é a do conteúdo que existe; sem conteúdo, nenhum rótulo de "fallback";
 *   (6) histórico: um ponto não é tendência e mês sem publicação não é continuidade;
 *   (7) Dashboards: o bloco KNIGHT segue a seleção (consolidado × fonte), abre o detalhe certo e a atenção não repete o KNIGHT;
 *   (8) a visão geral do módulo segue a ordem de leitura pedida e projeta as leituras consultivas antes do detalhamento.
 *
 * Compilado por `tsc` (CommonJS) e executado por `node`, no mesmo padrão dos demais specs.
 */
import * as fs from 'fs';
import * as path from 'path';
import {
  Injector,
  runInInjectionContext,
  ɵChangeDetectionScheduler as ChangeDetectionScheduler,
  ɵINJECTOR_SCOPE as INJECTOR_SCOPE,
} from '@angular/core';
import { parseTemplate } from '@angular/compiler';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';
import { historyLine, trendNotes } from '../src/app/models/charts.models';
import {
  KnightAssessment,
  KnightCapability,
  KnightConsolidatedSource,
  KnightIndicator,
  KnightSourceLatest,
  KnightSourceType,
} from '../src/app/models/knight.models';
import {
  advisoryOrigin,
  approvalBasis,
  findingQuery,
  limitationGroups,
  noLimitationText,
  priorityViews,
  scoreCoverage,
  sourceInclusion,
} from '../src/app/models/knight-summary.models';
import { PostureMonthlyPoint, PostureMonthlySeries } from '../src/app/models/posture-history.models';
import { DashboardsComponent } from '../src/app/pages/dashboards.component';
import { AgentStateService } from '../src/app/services/agent-state.service';
import { AuthService } from '../src/app/services/auth.service';
import { KnightService } from '../src/app/services/knight.service';
import { NistSelectionService, NistService } from '../src/app/services/nist.service';
import { PostureHistoryService } from '../src/app/services/posture-history.service';
import { RemediationService } from '../src/app/services/remediation.service';

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

console.log('knight-results');

// ---- dados controlados (sintéticos) ------------------------------------------------------------

function ind(id: string, status: KnightIndicator['status'], over: Partial<KnightIndicator> = {}, pres: Record<string, unknown> = {}): KnightIndicator {
  return {
    indicatorId: id, title: `Controle ${id}`, category: 'TenantConfiguration', severity: 'Medium', status, evidence: '',
    affectedObjectCount: 0, nistCodes: [], mitreTechniques: [], recommendation: '', collectedAt: '2026-10-20T00:00:00Z',
    sourceType: 'MicrosoftEntraId', notEvaluatedReason: null, hasAffectedDetail: true, affectedDetailComplete: true, affectedDetailLimitation: null,
    presentation: { domain: 'Identity', domainLabel: 'Identidade', service: 'Microsoft Entra ID', provider: 'Microsoft', platform: 'Microsoft Entra ID', requiredCapabilities: [], ...pres } as never,
    ...over,
  };
}

function assessment(indicators: KnightIndicator[], over: Partial<KnightAssessment> = {}): KnightAssessment {
  const c = (s: KnightIndicator['status']) => indicators.filter((i) => i.status === s).length;
  return {
    id: 'run-entra', mode: 'Live', isDemo: false, sourceType: 'MicrosoftEntraId', sourceState: 'Completed', source: 'Microsoft Entra ID',
    status: 'Completed', catalogVersion: 'ak-knight-v10', scoreFormulaVersion: 'knight-score-v1', startedAt: '2026-10-20T10:00:00Z',
    completedAt: '2026-10-20T10:05:00Z', score: 72, coverage: 100,
    counts: { passed: c('Passed'), exposed: c('Exposed'), mitigated: c('Mitigated'), notEvaluated: c('NotEvaluated'), error: c('Error'), notApplicable: c('NotApplicable') },
    indicators, capabilities: [], advisory: null, advisoryFromAi: false, ...over,
  };
}

function src(source: KnightSourceType, label: string, included: boolean, state: KnightConsolidatedSource['sourceState'] = 'Completed'): KnightConsolidatedSource {
  return {
    source, slug: source.toLowerCase(), label, included, availabilityState: included ? 'Included' : 'Available', sourceRunId: `run-${source}`,
    sourceState: state, catalogVersion: 'ak-knight-v10', capturedAt: '2026-10-20T10:00:00Z', score: null, coverage: null, counts: null, collectionLimitations: [],
  };
}

/** Consolidado: 4 fontes incluídas (Entra e Azure com controles avaliados; Teams e Purview só com não avaliados) e 1 fora. */
function consolidated(): KnightAssessment {
  const indicators = [
    ind('AK-ENTRA-001', 'Exposed', { severity: 'High', affectedObjectCount: 3, affectedComposition: '3 contas de usuário', recommendation: 'Exigir MFA resistente a phishing para administradores.' },
      { rationale: 'Conta privilegiada sem MFA pode ser tomada com uma senha vazada.', impact: 'Controle administrativo do locatário.' }),
    ind('AK-ENTRA-002', 'Passed'),
    ind('AK-AZ-001', 'Exposed', { severity: 'Critical', sourceType: 'MicrosoftAzure' }, { service: 'Microsoft Azure', requiredCapabilities: [] }),
    ind('AK-AZ-002', 'Mitigated', { sourceType: 'MicrosoftAzure' }, { service: 'Microsoft Azure' }),
    ind('AK-TEAMS-001', 'NotEvaluated', { sourceType: 'MicrosoftTeams' }, { service: 'Microsoft Teams', requiredCapabilities: ['TeamsClientConfiguration'] }),
    ind('AK-TEAMS-002', 'NotEvaluated', { sourceType: 'MicrosoftTeams' }, { service: 'Microsoft Teams', requiredCapabilities: ['TeamsFederationConfiguration'] }),
    ind('AK-PV-001', 'Error', { sourceType: 'MicrosoftPurview' }, { service: 'Microsoft Purview', requiredCapabilities: ['PurviewDlpPolicies'] }),
    ind('AK-PV-002', 'NotApplicable', { sourceType: 'MicrosoftPurview' }, { service: 'Microsoft Purview' }),
  ];
  const caps: KnightCapability[] = [
    { capability: 'TeamsClientConfiguration', outcome: 'Error', detail: 'Processo encerrado com código 137.', diagnostic: { stage: 'read', kind: 'ProcessExit', durationMs: 4100, httpStatus: null, code: '137', endpoint: null, correlationId: 'c-1', serviceRequestId: null } },
    { capability: 'TeamsFederationConfiguration', outcome: 'Error', detail: 'Processo encerrado com código 137.', diagnostic: { stage: 'read', kind: 'ProcessExit', durationMs: 4100, httpStatus: null, code: '137', endpoint: null, correlationId: 'c-1', serviceRequestId: null } },
    { capability: 'PurviewDlpPolicies', outcome: 'PlatformUnsupported', detail: null },
  ];
  return assessment(indicators, {
    id: '', sourceType: 'Consolidated', source: 'Consolidado', sourceState: 'PartialCollection', score: 41, coverage: 57.1, capabilities: caps,
    sources: [
      src('MicrosoftEntraId', 'Microsoft Entra ID', true),
      src('MicrosoftAzure', 'Microsoft Azure', true),
      src('MicrosoftTeams', 'Microsoft Teams', true, 'PartialCollection'),
      src('MicrosoftPurview', 'Microsoft Purview', true, 'PartialCollection'),
      src('MicrosoftExchangeOnline', 'Exchange Online', false),
    ],
  });
}

// ---- (1) fontes -------------------------------------------------------------------------------

test('consolidado: fontes incluídas ≠ fontes com controles avaliados, pelos indicadores do resultado', () => {
  const inc = sourceInclusion(consolidated());
  eq(inc.text, '4 fontes incluídas · 2 com controles avaliados', 'o texto separa as duas contagens');
  eq(inc.withoutEvaluated.map((s) => s.label).join(','), 'Microsoft Teams,Microsoft Purview', 'as incluídas sem avaliação ficam consultáveis');
  ok(!inc.included.some((s) => s.label === 'Exchange Online'), 'fonte disponível e não incluída não conta como incluída');
  eq(inc.withoutEvaluated[0].state, 'Coleta parcial', 'o estado da coleta da fonte acompanha');
});

test('fonte única: uma fonte incluída; sem veredito, "nenhuma com controles avaliados"', () => {
  eq(sourceInclusion(assessment([ind('A', 'Passed')])).text, '1 fonte incluída · 1 com controles avaliados', 'fonte avaliada');
  eq(sourceInclusion(assessment([ind('A', 'NotEvaluated')], { score: null, coverage: 0 })).text, '1 fonte incluída · nenhuma com controles avaliados', 'fonte sem veredito');
});

// ---- (2) nota e cobertura -----------------------------------------------------------------------

test('parcial: a nota diz sobre quantos controles foi calculada e avisa os não avaliados', () => {
  const sc = scoreCoverage(consolidated());
  eq(sc.fraction, '4 de 7 controles aplicáveis avaliados', 'numerador e denominador (não aplicável fora da base)');
  ok(sc.partial, 'leitura parcial');
  ok((sc.warning ?? '').includes('considera 4 de 7') && (sc.warning ?? '').includes('não contam como aprovados'), `aviso visível: ${sc.warning}`);
});

test('completa: sem aviso; coleta com limitação mas tudo avaliado avisa a coleta, não a nota', () => {
  const full = scoreCoverage(assessment([ind('A', 'Passed'), ind('B', 'Exposed')]));
  ok(!full.partial && full.warning === null, 'avaliação completa não tem aviso');
  const lim = scoreCoverage(assessment([ind('A', 'Passed')], { sourceState: 'PartialCollection' }));
  ok(lim.partial && (lim.warning ?? '').startsWith('Coleta com limitação'), `aviso da coleta: ${lim.warning}`);
});

test('nenhum controle avaliado: ausência de nota, nunca zero simulado', () => {
  const sc = scoreCoverage(assessment([ind('A', 'NotEvaluated'), ind('B', 'Error')], { score: null, coverage: 0 }));
  eq(sc.score, null, 'sem nota');
  ok((sc.warning ?? '').startsWith('Sem nota') && (sc.warning ?? '').includes('não indica conformidade'), 'diz que a lacuna não é conformidade');
  eq(approvalBasis(assessment([ind('A', 'NotEvaluated')], { score: null })), 'Sem controle avaliado: não há aprovação a calcular.', 'aprovação sem base');
  eq(approvalBasis(assessment([ind('A', 'Passed'), ind('B', 'Exposed')])), '1 aprovado(s) de 2 controles avaliados.', 'base da aprovação');
});

// ---- (3) prioridades ------------------------------------------------------------------------------

test('prioridades: só reprovados, severidade primeiro, com o que o modelo sustenta e nada inventado', () => {
  const p = priorityViews(consolidated());
  eq(p.map((x) => x.id).join(','), 'AK-AZ-001,AK-ENTRA-001', 'mitigado não entra; crítico antes de alto');
  const entra = p[1];
  eq(entra.risk, 'Conta privilegiada sem MFA pode ser tomada com uma senha vazada.', 'risco do modelo');
  eq(entra.impact, 'Controle administrativo do locatário.', 'impacto do modelo');
  eq(entra.affected, '3 contas de usuário', 'população afetada pelo tipo real');
  eq(p[0].risk, null, 'sem risco no modelo, nada é preenchido');
  eq(p[0].guidance, null, 'sem orientação no modelo, nada é preenchido');
});

test('o achado do consolidado abre na avaliação da fonte que o originou; o da fonte única, nela mesma', () => {
  const runs = { MicrosoftAzure: 'run-az', MicrosoftEntraId: 'run-en' };
  eq(findingQuery(consolidated(), 'AK-AZ-001', runs)['run'], 'run-az', 'execução da fonte de origem');
  eq(findingQuery(assessment([ind('A', 'Exposed')]), 'A')['run'], 'run-entra', 'execução exibida');
});

// ---- (4) limitações -------------------------------------------------------------------------------

test('limitações equivalentes agrupadas, diferentes separadas, sem perder a capacidade nem o detalhe técnico', () => {
  const g = limitationGroups(consolidated());
  eq(g.length, 2, 'Teams (mesma falha) num grupo; Purview noutro');
  const teams = g.find((x) => x.items.length === 2)!;
  eq(teams.kind, 'o processo do adaptador terminou com código de erro', 'tipo sanitizado da falha');
  eq(teams.affectedControls.join(','), 'AK-TEAMS-001,AK-TEAMS-002', 'controles prejudicados (união)');
  eq(teams.sources.join(','), 'Microsoft Teams', 'a fonte é preservada');
  ok(teams.items.every((i) => i.technical.some((t) => t.includes('Correlação da execução: c-1'))), 'cada capacidade guarda o detalhe técnico');
  const a = consolidated();
  a.capabilities = [
    { capability: 'TeamsClientConfiguration', outcome: 'Error', detail: null, diagnostic: { stage: 'read', kind: 'Timeout', durationMs: 30000, httpStatus: null, code: null, endpoint: null, correlationId: null, serviceRequestId: null } },
    { capability: 'TeamsFederationConfiguration', outcome: 'Error', detail: null, diagnostic: { stage: 'read', kind: 'CircuitOpen', durationMs: 1, httpStatus: null, code: null, endpoint: null, correlationId: null, serviceRequestId: null } },
    { capability: 'PurviewDlpPolicies', outcome: 'InsufficientPermission', detail: null },
    { capability: 'PurviewSensitivityLabels', outcome: 'LimitedByLicense', detail: null },
  ];
  eq(limitationGroups(a).length, 4, 'tempo esgotado ≠ circuito aberto; permissão ≠ licença');
});

test('sem limitação listada: ausência de registro não vira coleta comprovada', () => {
  const semRegistro = assessment([ind('A', 'Passed'), ind('B', 'NotEvaluated'), ind('C', 'Error')]);
  const t = noLimitationText(semRegistro);
  ok(!t.includes('foram lidas'), 'sem capacidades registradas não se afirma leitura completa');
  eq(t, 'Não há limitações de coleta registradas nesta avaliação. Isso não comprova coleta completa: 2 controles aplicáveis ficaram sem avaliação.', 'texto fiel ao registro');
  eq(noLimitationText(assessment([ind('A', 'Passed')])), 'Não há limitações de coleta registradas nesta avaliação.', 'sem controle pendente, sem ressalva');
  const lidas = assessment([ind('A', 'Passed')], { capabilities: [
    { capability: 'EntraPolicies', outcome: 'Collected', detail: null }, { capability: 'EntraUsers', outcome: 'Collected', detail: null }] as KnightCapability[] });
  eq(noLimitationText(lidas), 'Todas as 2 capacidades de coleta registradas nesta avaliação foram lidas.', 'só com registro explícito de coleta');
});

// ---- (5) interpretação ----------------------------------------------------------------------------

test('interpretação: a origem é a do conteúdo; sem conteúdo, nenhum rótulo', () => {
  const base = assessment([ind('A', 'Passed')]);
  eq(advisoryOrigin(base).kind, 'none', 'sem narrativa não há o que rotular');
  eq(advisoryOrigin({ ...base, advisory: { executiveSummary: ' ', priorityRisks: [], recommendedActions: [], correlations: [], collectionGaps: [] } }).label, null, 'narrativa vazia também não');
  const det = { ...base, advisory: { executiveSummary: 'Resumo.', priorityRisks: [], recommendedActions: [], correlations: [], collectionGaps: [] } };
  eq(advisoryOrigin(det).label, 'Resumo determinístico do AEGIS', 'texto determinístico identificado');
  eq(advisoryOrigin({ ...det, advisoryFromAi: true }).label, 'Gerada por IA', 'texto da IA identificado');
  eq(advisoryOrigin(det, false).kind, 'none', 'execução não finalizada não tem interpretação');
});

// ---- (6) histórico --------------------------------------------------------------------------------

function point(month: string, score: number | null, comparable = true): PostureMonthlyPoint {
  return {
    month, snapshotId: `s-${month}`, capturedAt: `${month}-15T00:00:00Z`, score, coverage: 50, evaluatedItems: 5, eligibleItems: 10,
    formulaVersion: 'knight-score-v1', catalogVersion: 'ak-knight-v10', comparableWithPrevious: comparable, breakReasons: [], publishedInMonth: 1,
  } as unknown as PostureMonthlyPoint;
}
function series(points: PostureMonthlyPoint[]): PostureMonthlySeries {
  return { type: 'Knight', semanticFamily: 'knight:consolidated', label: 'KNIGHT consolidado', points } as unknown as PostureMonthlySeries;
}
const MONTHS = ['2026-06', '2026-07', '2026-08', '2026-09', '2026-10'];

test('histórico: um ponto não é tendência; meses ausentes não sugerem continuidade; vizinhos comparáveis não geram aviso', () => {
  ok(historyLine(series([point('2026-10', 70)]), MONTHS).notes!.includes('Um único mês com publicação: ainda não há tendência.'), 'um ponto');
  const gap = historyLine(series([point('2026-06', 60), point('2026-09', 70)]), MONTHS).notes!;
  ok(gap.some((n) => n.startsWith('2 mês(es) sem publicação')), `meses ausentes: ${gap.join(' | ')}`);
  ok(gap.some((n) => n.startsWith('Nenhum par de meses vizinhos')), 'pontos isolados não formam linha');
  eq(historyLine(series([point('2026-09', 60), point('2026-10', 70)]), MONTHS).notes!.length, 0, 'vizinhos comparáveis: sem aviso');
  eq(trendNotes([], []).length, 0, 'sem pontos, sem nota (a mensagem de vazio é do gráfico)');
});

// ---- (7) Dashboards: componente real -------------------------------------------------------------

function silent<T>(): Observable<T> {
  return new Subject<T>().asObservable();
}

function mountDashboards(sources: KnightSourceLatest[], cons: KnightAssessment) {
  const consolidatedCalls: KnightSourceType[][] = [];
  const inj = Injector.create({
    providers: [
      { provide: INJECTOR_SCOPE, useValue: 'root' },
      { provide: ChangeDetectionScheduler, useValue: { notify() {}, runningTick: false } },
      { provide: KnightService, useValue: { getLatestBySource: () => of({ sources }), getConsolidated: (s: KnightSourceType[]) => (consolidatedCalls.push(s), of(cons)) } },
      { provide: NistService, useValue: { list: () => of([]) } },
      { provide: NistSelectionService, useValue: { read: () => null, write: () => {} } },
      { provide: AuthService, useValue: { activeTenantId: () => 't-demo' } },
      { provide: PostureHistoryService, useValue: { monthly: () => silent() } },
      { provide: RemediationService, useValue: { list: () => of([]) } },
      { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap({})), snapshot: { queryParamMap: convertToParamMap({}) } } },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
      { provide: AgentStateService, useValue: { requestAudit: () => {} } },
    ],
  });
  const c = runInInjectionContext(inj, () => new DashboardsComponent()) as unknown as Record<string, () => unknown> & { selectKnight(k: string): void };
  return { c, consolidatedCalls };
}

function latest(source: KnightSourceType, label: string, a: KnightAssessment | null): KnightSourceLatest {
  return { source, slug: source.toLowerCase(), label, assessment: a, unfinishedAttempt: null };
}

test('Dashboards: o consolidado é o padrão, conta fontes incluídas × avaliadas e abre cada prioridade na fonte de origem', () => {
  const entra = assessment([ind('AK-ENTRA-001', 'Exposed')], { id: 'run-en' });
  const azure = assessment([ind('AK-AZ-001', 'Exposed', { sourceType: 'MicrosoftAzure' })], { id: 'run-az', sourceType: 'MicrosoftAzure', source: 'Microsoft Azure' });
  const { c, consolidatedCalls } = mountDashboards(
    [latest('MicrosoftEntraId', 'Microsoft Entra ID', entra), latest('MicrosoftAzure', 'Microsoft Azure', azure)],
    consolidated(),
  );
  eq(consolidatedCalls.length, 1, 'o consolidado é pedido ao servidor (nunca média feita aqui)');
  eq((c['knightInclusion']() as { text: string }).text, '4 fontes incluídas · 2 com controles avaliados', 'contagem da seleção atual');
  const prios = c['knightPriorities']() as { id: string; query: Record<string, string | null> }[];
  eq(prios[0].query['run'], 'run-az', 'prioridade do Azure abre a avaliação do Azure');
  eq(prios[1].query['run'], 'run-en', 'prioridade do Entra abre a avaliação do Entra');
  eq((c['knightDetailQuery']() as Record<string, string | null>)['modo'], 'consolidado', 'o detalhe abre o modo consolidado');
  ok(!(c['attention']() as { origin: string }[]).some((i) => i.origin === 'KNIGHT'), 'a atenção não repete as prioridades do KNIGHT');

  c.selectKnight('source:MicrosoftAzure');
  eq((c['knightInclusion']() as { text: string }).text, '1 fonte incluída · 1 com controles avaliados', 'a contagem acompanha a troca de seleção');
  eq((c['knightDetailQuery']() as Record<string, string | null>)['run'], 'run-az', 'o detalhe abre a avaliação escolhida');
  eq((c['knightScore']() as { warning: string | null }).warning, null, 'fonte completa sem aviso');
});

test('Dashboards: nenhuma avaliação concluída não monta resumo nem pede consolidado', () => {
  const { c, consolidatedCalls } = mountDashboards([latest('MicrosoftEntraId', 'Microsoft Entra ID', null)], consolidated());
  eq(c['knightView'](), null, 'sem resultado exibido');
  eq(c['knightInclusion'](), null, 'sem contagem de fontes');
  eq((c['knightPriorities']() as unknown[]).length, 0, 'sem prioridades');
  eq(consolidatedCalls.length, 0, 'nada a consolidar');
});

// ---- (8) ordem de leitura da visão geral e da página ----------------------------------------------

const ROOT = path.resolve(__dirname, '..', '..', '..');
function template(rel: string): string {
  const src = fs.readFileSync(path.join(ROOT, rel), 'utf8');
  const m = /template:\s*`([\s\S]*?)`,\r?\n\s{2}styles:/.exec(src);
  if (!m) throw new Error(`template não isolado em ${rel}`);
  const parsed = parseTemplate(m[1], rel);
  if (parsed.errors?.length) throw new Error(`template inválido em ${rel}: ${parsed.errors[0].msg}`);
  return m[1];
}

test('visão geral: identificação → indicadores → riscos → serviços → limitações → leituras consultivas → detalhamento recolhido', () => {
  const t = template('src/app/components/knight/knight-overview.component.ts');
  const order = ['id="ov-ident"', 'class="metric-grid"', 'id="ov-prio"', 'id="ov-svc"', 'id="ov-lim"', '<ng-content', 'class="panel tech-section"'];
  const at = order.map((x) => t.indexOf(x));
  ok(at.every((i) => i >= 0), `todas as seções existem: ${order.filter((_, i) => at[i] < 0).join(', ')}`);
  ok(at.every((v, i) => i === 0 || v > at[i - 1]), `ordem de leitura: ${at.join(' < ')}`);
  ok(t.indexOf('Três medidas') > t.indexOf('tech-section'), 'cobertura do catálogo fica no detalhamento, em posição secundária');
  ok(/<details class="panel tech-section">/.test(t), 'o detalhamento é uma expansão (fechada por padrão)');
});

test('página KNIGHT: interpretação e risco de identidade projetados na visão geral, separados da avaliação', () => {
  const t = template('src/app/pages/aegis-knight.component.ts');
  const open = t.indexOf('(open)="openControl($event)">');
  const close = t.indexOf('</app-knight-overview>');
  ok(open > 0 && close > open, 'a visão geral recebe conteúdo projetado');
  const inner = t.slice(open, close);
  ok(inner.indexOf('advisoryOrigin(a, !unfinishedView())') >= 0, 'a origem da interpretação vem do conteúdo existente');
  ok(inner.indexOf('<app-identity-risk-panel') > inner.indexOf('panel ai'), 'o risco de identidade (leitura atual) vem depois da interpretação');
  ok(!/Fallback determinístico/.test(t), 'nenhum "fallback" anunciado sem conteúdo');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);
