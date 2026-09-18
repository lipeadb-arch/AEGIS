/**
 * [AEGIS-KNIGHT-MULTICLOUD-01] Testes de LÓGICA PURA da visão geral, dos filtros e das limitações do assessment.
 *
 * Fixam as unidades que a tela e o relatório não podem misturar:
 *   (1) aprovação dos controles (aprovados ÷ avaliados) ≠ score KNIGHT ≠ cobertura;
 *   (2) findings = reprovado OU mitigado, contados por severidade uma vez cada;
 *   (3) distribuição por domínio/serviço conta cada controle UMA vez, mesmo com vários frameworks;
 *   (4) prioridades só com controles reprovados, por critério verificável (severidade, afetados, id);
 *   (5) resposta antiga sem perfil nunca vira "Microsoft" para Demo/Google;
 *   (6) limitação diz causa, controles prejudicados e orientação — permissão ≠ licença.
 *
 * Compilado por `tsc` (CommonJS) e executado por `node`, no mesmo padrão dos demais specs de modelo.
 */
import {
  EMPTY_FILTERS,
  KnightAssessment,
  KnightControlPresentation,
  KnightIndicator,
  axesOf,
  contributionText,
  describeFilters,
  distributionBy,
  filterOptions,
  frameworksOf,
  isEvaluated,
  isFinding,
  limitationViews,
  matchesFilters,
  overviewKpis,
  priorityControls,
  severityLabel,
  statusLabel,
} from '../src/app/models/knight.models';

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
function ok(condition: boolean, msg: string): void {
  if (!condition) throw new Error(msg);
}

function pres(over: Partial<KnightControlPresentation> = {}): KnightControlPresentation {
  return {
    domain: 'Identity',
    domainLabel: 'Identidade',
    service: 'Microsoft Entra ID',
    provider: 'Microsoft',
    description: null,
    rationale: null,
    expectedConfiguration: null,
    doesNotProve: null,
    criterion: null,
    references: [
      { framework: 'NIST CSF', version: '2.0', code: 'PR.AA-01', url: null },
      { framework: 'NIST CSF', version: '2.0', code: 'PR.AA-05', url: null },
      { framework: 'MITRE ATT&CK', version: null, code: 'T1078', url: null },
    ],
    requiredCapabilities: [],
    weight: 7,
    factor: 1,
    achievedPoints: 7,
    possiblePoints: 7,
    ...over,
  };
}

function ind(over: Partial<KnightIndicator> = {}): KnightIndicator {
  return {
    indicatorId: 'AK-ENTRA-001',
    title: 'Controle',
    category: 'PrivilegedAccess',
    severity: 'High',
    status: 'Passed',
    evidence: 'evidência',
    affectedObjectCount: 0,
    nistCodes: ['PR.AA-01'],
    mitreTechniques: ['T1078'],
    recommendation: 'rec',
    collectedAt: '2026-09-18T10:00:00Z',
    sourceType: 'MicrosoftEntraId',
    notEvaluatedReason: null,
    hasAffectedDetail: true,
    affectedDetailComplete: true,
    affectedDetailLimitation: null,
    presentation: pres(),
    ...over,
  };
}

function assessment(indicators: KnightIndicator[], over: Partial<KnightAssessment> = {}): KnightAssessment {
  return {
    id: 'run-1',
    mode: 'Live',
    isDemo: false,
    sourceType: 'MicrosoftEntraId',
    sourceState: 'Completed',
    source: 'Microsoft Entra ID',
    status: 'Completed',
    catalogVersion: 'ak-knight-v3',
    scoreFormulaVersion: 'knight-score-v1',
    startedAt: '2026-09-18T10:00:00Z',
    completedAt: '2026-09-18T10:01:00Z',
    score: 55,
    coverage: 80,
    counts: { passed: 0, exposed: 0, mitigated: 0, notEvaluated: 0, error: 0, notApplicable: 0 },
    indicators,
    capabilities: [],
    advisory: null,
    advisoryFromAi: false,
    ...over,
  };
}

const MIXED = assessment([
  ind({ indicatorId: 'AK-ENTRA-001', status: 'Passed', severity: 'Critical' }),
  ind({ indicatorId: 'AK-ENTRA-002', status: 'Exposed', severity: 'High', affectedObjectCount: 3 }),
  ind({ indicatorId: 'AK-ENTRA-003', status: 'Exposed', severity: 'Critical', affectedObjectCount: 1 }),
  ind({ indicatorId: 'AK-ENTRA-004', status: 'Mitigated', severity: 'Medium' }),
  ind({ indicatorId: 'AK-ENTRA-005', status: 'NotEvaluated', severity: 'High', presentation: pres({ factor: null, requiredCapabilities: ['ConditionalAccessPolicies'] }) }),
  ind({ indicatorId: 'AK-ENTRA-006', status: 'Error', severity: 'Low', presentation: pres({ factor: null }) }),
  ind({ indicatorId: 'AK-ENTRA-007', status: 'NotApplicable', severity: 'Low', presentation: pres({ factor: null }) }),
  ind({ indicatorId: 'AK-ENTRA-008', status: 'Exposed', severity: 'High', affectedObjectCount: 9, presentation: pres({ service: 'Microsoft 365', domain: 'Collaboration', domainLabel: 'Colaboração' }) }),
]);

// ---- (1)(2) KPIs ------------------------------------------------------------------------------
test('aprovação = aprovados ÷ avaliados, independente do score e da cobertura', () => {
  const k = overviewKpis(MIXED);
  eq(k.total, 8, 'total de controles');
  eq(k.evaluated, 5, 'avaliados = aprovado + reprovado + mitigado');
  eq(k.passed, 1, 'aprovados');
  eq(k.failed, 3, 'reprovados');
  eq(k.mitigated, 1, 'mitigados');
  eq(k.notEvaluated, 1, 'não avaliados');
  eq(k.errors, 1, 'erros');
  eq(k.notApplicable, 1, 'não aplicáveis');
  eq(k.approvalPercent, 20, '1 de 5 avaliados');
  ok(k.approvalPercent !== MIXED.score && k.approvalPercent !== MIXED.coverage, 'aprovação não reaproveita score nem cobertura');
});

test('sem controle avaliado, aprovação é null (nunca 0 nem 100)', () => {
  const k = overviewKpis(assessment([ind({ status: 'NotEvaluated' }), ind({ indicatorId: 'X', status: 'Error' })]));
  eq(k.approvalPercent, null, 'aprovação sem base');
  eq(k.findings, 0, 'nenhum finding');
});

test('mitigado é finding (atenção), não aprovação — sem crédito parcial na aprovação', () => {
  const k = overviewKpis(assessment([ind({ status: 'Mitigated' })]));
  eq(k.approvalPercent, 0, 'mitigado não conta como aprovado');
  eq(k.findings, 1, 'mitigado conta como finding');
  ok(isFinding(ind({ status: 'Mitigated' })) && isEvaluated(ind({ status: 'Mitigated' })), 'mitigado é avaliado e finding');
  ok(!isEvaluated(ind({ status: 'NotApplicable' })), 'não aplicável não é avaliado');
});

test('findings por severidade: ordem fixa Crítico→Informativo, soma igual ao total de findings', () => {
  const k = overviewKpis(MIXED);
  eq(k.findingsBySeverity.map((s) => s.label).join(','), 'Crítico,Alto,Médio,Baixo,Informativo', 'ordem e rótulos');
  eq(k.findingsBySeverity.map((s) => s.count).join(','), '1,2,1,0,0', 'contagem por severidade');
  eq(k.findingsBySeverity.reduce((n, s) => n + s.count, 0), k.findings, 'soma reconciliada');
});

// ---- (3) distribuição ---------------------------------------------------------------------------
test('distribuição conta cada controle uma vez, mesmo mapeado a vários frameworks', () => {
  const rows = distributionBy(MIXED, 'service');
  eq(rows.reduce((n, r) => n + r.total, 0), MIXED.indicators.length, 'soma = número de controles');
  eq(rows[0].key, 'Microsoft Entra ID', 'mais reprovações primeiro');
  eq(rows[0].counts.Exposed, 2, 'reprovados no Entra');
  const dom = distributionBy(MIXED, 'domain');
  ok(dom.some((r) => r.key === 'Collaboration' && r.label === 'Colaboração' && r.total === 1), 'domínio com rótulo');
});

test('frameworks do controle são deduplicados (dois códigos NIST = um framework)', () => {
  const f = frameworksOf(ind());
  eq(f.length, 2, 'NIST CSF 2.0 + MITRE ATT&CK');
  ok(f.includes('NIST CSF 2.0') && f.includes('MITRE ATT&CK'), 'nomes dos frameworks');
});

// ---- (4) prioridades ----------------------------------------------------------------------------
test('prioridades: só reprovados, por severidade, depois afetados, depois id', () => {
  const p = priorityControls(MIXED);
  eq(p.map((i) => i.indicatorId).join(','), 'AK-ENTRA-003,AK-ENTRA-008,AK-ENTRA-002', 'ordem verificável');
  ok(p.every((i) => i.status === 'Exposed'), 'mitigado/não avaliado nunca entram');
});

test('prioridades limitadas a cinco', () => {
  const many = assessment(Array.from({ length: 8 }, (_, n) => ind({ indicatorId: `AK-X-${n}`, status: 'Exposed' })));
  eq(priorityControls(many).length, 5, 'no máximo cinco');
});

// ---- (5) eixos sem perfil -----------------------------------------------------------------------
test('resposta antiga sem perfil cai na fonte real — Demo e Google nunca viram Microsoft', () => {
  const demo = axesOf(ind({ sourceType: 'Demo', presentation: null }));
  eq(demo.provider, 'Demonstração', 'demo');
  eq(demo.service, 'Demonstração', 'serviço demo');
  const g = axesOf(ind({ sourceType: 'GoogleWorkspace', presentation: undefined }));
  eq(g.provider, 'Google', 'google');
  eq(g.service, 'Google Workspace', 'serviço google');
  eq(axesOf(ind({ presentation: null })).provider, 'Microsoft', 'entra continua Microsoft');
});

test('frameworks sem perfil vêm dos códigos legados, sem inventar', () => {
  eq(frameworksOf(ind({ presentation: null, mitreTechniques: [] })).join(','), 'NIST CSF 2.0', 'só NIST');
  eq(frameworksOf(ind({ presentation: null, nistCodes: [], mitreTechniques: [] })).length, 0, 'nenhum');
});

// ---- filtros --------------------------------------------------------------------------------------
test('filtro "findings" = reprovado ou mitigado; filtros combinam', () => {
  const f = { ...EMPTY_FILTERS, status: 'findings' as const };
  eq(MIXED.indicators.filter((i) => matchesFilters(i, f)).length, 4, 'três reprovados + um mitigado');
  const g = { ...f, severity: 'High' as const, service: 'Microsoft Entra ID' };
  eq(MIXED.indicators.filter((i) => matchesFilters(i, g)).map((i) => i.indicatorId).join(','), 'AK-ENTRA-002', 'combinação');
});

test('filtro por framework e pesquisa textual (id, sem diferenciar maiúsculas)', () => {
  const noMitre = ind({ indicatorId: 'AK-ENTRA-009', presentation: pres({ references: [{ framework: 'NIST CSF', version: '2.0', code: 'GV.RR-01', url: null }] }) });
  ok(!matchesFilters(noMitre, { ...EMPTY_FILTERS, framework: 'MITRE ATT&CK' }), 'sem MITRE fica de fora');
  ok(matchesFilters(noMitre, { ...EMPTY_FILTERS, framework: 'NIST CSF 2.0' }), 'NIST entra');
  ok(matchesFilters(noMitre, { ...EMPTY_FILTERS, q: '  ak-entra-009 ' }), 'pesquisa pelo id');
  ok(!matchesFilters(noMitre, { ...EMPTY_FILTERS, q: 'inexistente' }), 'pesquisa sem acerto');
  ok(matchesFilters(noMitre, EMPTY_FILTERS), 'sem filtro tudo passa');
});

test('recorte aplicado é descrito em palavras', () => {
  eq(describeFilters(EMPTY_FILTERS, MIXED), 'sem filtros (avaliação completa)', 'vazio');
  const d = describeFilters({ ...EMPTY_FILTERS, status: 'Exposed', severity: 'Critical', domain: 'Collaboration', q: 'mfa' }, MIXED);
  eq(d, 'resultado: Reprovado · severidade: Crítico · domínio: Colaboração · pesquisa: “mfa”', 'descrição');
});

test('opções de filtro vêm só do que a avaliação contém', () => {
  const o = filterOptions(MIXED);
  eq(o.services.join(','), 'Microsoft 365,Microsoft Entra ID', 'serviços');
  eq(o.domains.map((d) => d.label).join(','), 'Colaboração,Identidade', 'domínios');
  eq(o.frameworks.join(','), 'MITRE ATT&CK,NIST CSF 2.0', 'frameworks');
});

// ---- (6) limitações -------------------------------------------------------------------------------
test('limitação: permissão ≠ licença, com controles prejudicados e orientação', () => {
  const a = assessment(MIXED.indicators, {
    capabilities: [
      { capability: 'ConditionalAccessPolicies', outcome: 'InsufficientPermission', detail: 'Policy.Read.All' },
      { capability: 'IdentityRiskDetections', outcome: 'LimitedByLicense', detail: null },
      { capability: 'DirectoryUsers', outcome: 'Collected', detail: null },
    ],
  });
  const v = limitationViews(a);
  eq(v.length, 2, 'só capacidades não coletadas');
  eq(v[0].cause, 'Permissão ausente', 'causa de permissão');
  eq(v[0].label, 'Políticas de acesso condicional', 'rótulo legível');
  eq(v[0].affectedControls.join(','), 'AK-ENTRA-005', 'controle não avaliado que dependia da capacidade');
  eq(v[1].cause, 'Licença insuficiente', 'causa de licença');
  ok(v[1].guidance.includes('nunca aprovados'), 'licença não vira aprovação');
  eq(v[1].affectedControls.length, 0, 'nenhum controle declarado dependente');
});

test('capacidade desconhecida degrada para identificador e causa genérica', () => {
  const v = limitationViews(assessment([], { capabilities: [{ capability: 'Nova', outcome: 'Error', detail: null }] }));
  eq(v[0].label, 'Nova', 'identificador');
  eq(v[0].cause, 'Erro de coleta', 'erro');
});

// ---- contribuição e rótulos -------------------------------------------------------------------------
test('contribuição: peso × fator; fora da nota quando não avaliado; ausente em resposta antiga', () => {
  eq(contributionText(ind({ presentation: pres({ weight: 4, factor: 0.5, achievedPoints: 2, possiblePoints: 4 }) })),
    'Peso 4 × fator 0,5 = 2 de 4 ponto(s).', 'mitigado');
  ok(contributionText(ind({ presentation: pres({ factor: null }) })).startsWith('Fora da nota'), 'não avaliado');
  eq(contributionText(ind({ presentation: null })), 'Contribuição não disponível nesta resposta.', 'legado');
});

test('rótulos em português, com severidade Informativo e Mitigado como atenção', () => {
  eq(statusLabel('Exposed'), 'Reprovado', 'reprovado');
  eq(statusLabel('Mitigated'), 'Mitigado (atenção)', 'mitigado');
  eq(statusLabel('NotEvaluated'), 'Não avaliado', 'não avaliado');
  eq(statusLabel('Error'), 'Erro na avaliação', 'erro');
  eq(severityLabel('Informational'), 'Informativo', 'informativo');
});

console.log(`\n${count - failures}/${count} testes passaram (knight-assessment.models).`);
if (failures > 0) throw new Error(`${failures} teste(s) do assessment do KNIGHT falharam`);
