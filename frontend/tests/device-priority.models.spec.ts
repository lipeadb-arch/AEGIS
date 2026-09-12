/**
 * [AEGIS-RISK-PRIORITIZATION-01] Testes de LÓGICA PURA da apresentação da prioridade de tratamento (frontend).
 *
 * O que está sendo provado é comportamento: a faixa vem do backend e não é recalculada; ausência de contagem não vira 0;
 * empate real se declara como tal; EPSS nunca vira "chance de ataque"; e os estados de linguagem (carregando, falha, sem
 * fonte, sem leitura, sem candidatos, só não priorizáveis, filtro vazio) são distintos. Compiladas por `tsc`, executadas
 * por `node`.
 */
import {
  DEVICE_PRIORITY_BAND_FILTERS,
  DevicePriorityCriticality,
  DevicePriorityList,
  DevicePrioritySummary,
  applyDeclaredCriticality,
  bandCountsText,
  bandShort,
  bandTone,
  canDeclareCriticality,
  casePageText,
  casesText,
  declarationErrorText,
  declarationSavedNotRefreshedText,
  devicePriorityListView,
  devicePriorityRangeText,
  devicePrioritySummaryText,
  dispositionText,
  epssText,
  factorEffectLabel,
  factorKindLabel,
  pageAfterRefresh,
  tieText,
} from '../src/app/models/device-priority.models';

// ---- micro-harness ---------------------------------------------------------------------------
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
function ok(cond: boolean, msg: string): void {
  if (!cond) throw new Error(msg);
}

function summary(over: Partial<DevicePrioritySummary>): DevicePrioritySummary {
  return {
    readingState: 'Available',
    readingNote: null,
    candidateAssets: 3,
    assetsEvaluated: 3,
    evaluationTruncated: false,
    completeThroughBand: null,
    truncationNote: null,
    assetsByBand: [
      { band: 'p1', label: 'Prioridade 1', count: 1 },
      { band: 'p2', label: 'Prioridade 2', count: 2 },
      { band: 'p3', label: 'Prioridade 3', count: 0 },
      { band: 'p4', label: 'Prioridade 4', count: 0 },
      { band: 'insufficient', label: 'Não priorizável', count: 0 },
    ],
    casesByBand: [],
    dispositions: [],
    outOfScopeSources: 0,
    outOfScopeNote: null,
    absenceState: 'conclusive',
    absenceNote: null,
    ...over,
  };
}

function list(over: Partial<DevicePriorityList>, s: Partial<DevicePrioritySummary> = {}): DevicePriorityList {
  return {
    evaluatedAt: '2026-09-12T12:00:00Z',
    heading: 'Prioridade de tratamento',
    scope: '',
    policy: {
      code: 'AEGIS-PRIO-DEV', version: 1, name: '', rationale: '', table: [], aggravation: [], tieBreaks: [], notUsed: [],
      temporal: { maxEvidenceAgeDays: 7, maxSourceActivityAgeDays: 30, acquisitionGapCaveatHours: 24, description: '' },
    },
    summary: summary(s),
    bandFilter: null,
    items: [],
    total: 0,
    page: 1,
    pageSize: 10,
    ...over,
  };
}

console.log('device-priority.models');

test('faixas: tons nunca dizem "seguro" e rótulos curtos preservam a faixa do backend', () => {
  eq(bandTone('p1'), 'attention', 'P1');
  eq(bandTone('p2'), 'warn', 'P2');
  eq(bandTone('p3'), 'info', 'P3');
  eq(bandTone('p4'), 'muted', 'P4');
  eq(bandTone('insufficient'), 'muted', 'não priorizável não é "ok"');
  eq(bandShort('p3'), 'P3', 'curto');
  eq(bandShort('insufficient'), 'Não priorizável', 'curto insuficiente');
  eq(DEVICE_PRIORITY_BAND_FILTERS[0].value, null, 'padrão = todas as faixas');
  ok(DEVICE_PRIORITY_BAND_FILTERS.some((f) => f.value === 'insufficient'), 'filtro próprio de não priorizáveis');
});

test('natureza e efeito dos fatores têm rótulos explícitos (desconhecido não vira favorável)', () => {
  eq(factorKindLabel('sourceFact'), 'Fato da fonte', 'fato');
  eq(factorKindLabel('declared'), 'Informação declarada', 'declarado');
  eq(factorKindLabel('inferred'), 'Inferência identificada', 'inferido');
  eq(factorKindLabel('unknown'), 'Desconhecido', 'desconhecido');
  eq(factorEffectLabel('aggravating'), 'Antecipou uma faixa', 'agravante');
  eq(factorEffectLabel('notUsed'), 'Não usado nesta versão', 'fora da política');
});

test('contagens: só as presentes; nenhuma = "—", nunca "0" inventado; unidade explícita', () => {
  eq(bandCountsText([{ band: 'p1', label: '', count: 2 }, { band: 'p2', label: '', count: 0 }, { band: 'p3', label: '', count: 1 }]),
    'P1 2 · P3 1', 'presentes');
  eq(bandCountsText([]), '—', 'vazio');
  eq(bandCountsText(null), '—', 'nulo');
  eq(casesText(1), '1 caso', 'singular');
  eq(casesText(3), '3 casos', 'plural');
  eq(casesText(null), '—', 'ausente');
});

test('disposições humanas: só as registradas; nenhuma = null', () => {
  eq(dispositionText([
    { status: 'mitigated', label: 'Mitigação informada', count: 0 },
    { status: 'accepted', label: 'Risco aceito', count: 2 },
    { status: 'falsePositive', label: 'Falso positivo', count: 1 },
  ]), '2 · risco aceito · 1 · falso positivo', 'presentes');
  eq(dispositionText([{ status: 'accepted', label: 'Risco aceito', count: 0 }]), null, 'nenhuma');
});

test('empate real se declara como empate — ordem alfabética só para estabilidade', () => {
  eq(tieText(0), null, 'sem empate');
  ok((tieText(1) ?? '').includes('1 outro dispositivo'), 'singular');
  ok((tieText(3) ?? '').includes('só para estabilidade'), 'sem fingir diferença de risco');
});

test('EPSS é probabilidade GLOBAL de 30 dias — nunca chance de ataque a este ambiente', () => {
  eq(epssText(0.9731), 'EPSS 97.3% (probabilidade global, 30 dias)', 'valor');
  eq(epssText(null), 'EPSS não informado', 'ausente');
  ok(!epssText(0.5).toLowerCase().includes('ataque'), 'sem "ataque"');
});

test('resumo com unidades: dispositivos, faixas presentes, não priorizáveis e recorte', () => {
  eq(devicePrioritySummaryText(summary({})), '3 dispositivos com vulnerabilidade em aberto · P1 1 · P2 2', 'simples');
  const s = summary({
    candidateAssets: 10, assetsEvaluated: 4, evaluationTruncated: true,
    assetsByBand: [
      { band: 'p1', label: '', count: 1 }, { band: 'insufficient', label: '', count: 2 },
    ],
  });
  eq(devicePrioritySummaryText(s),
    '10 dispositivos com vulnerabilidade em aberto · P1 1 · 2 sem informação suficiente para priorizar · recorte parcial: 4 avaliados',
    'com recorte');
});

test('estados da fila são distintos: carregando, falha, sem fonte, sem leitura, sem candidatos, só insuficientes, filtro', () => {
  eq(devicePriorityListView(null, true).kind, 'loading', 'carregando');
  const err = devicePriorityListView(null, false, 'API inacessível.');
  eq(err.kind, 'error', 'falha');
  eq('text' in err ? err.text : '', 'API inacessível.', 'texto da falha');
  eq(devicePriorityListView(list({}, { readingState: 'NoSource', readingNote: 'sem Defender' })).kind, 'noSource', 'sem fonte');
  eq(devicePriorityListView(list({}, { readingState: 'NeverCollected' })).kind, 'neverCollected', 'sem leitura');
  const none = devicePriorityListView(list({}, { candidateAssets: 0 }));
  eq(none.kind, 'noCandidates', 'sem candidatos');
  ok(('text' in none ? none.text : '').includes('não comprova'), 'zero não é "seguro"');
  eq(devicePriorityListView(list({}, {
    assetsByBand: [{ band: 'p1', label: '', count: 0 }, { band: 'insufficient', label: '', count: 2 }],
  })).kind, 'onlyInsufficient', 'só não priorizáveis');
  eq(devicePriorityListView(list({ bandFilter: 'p4' })).kind, 'filterEmpty', 'filtro vazio');
  eq(devicePriorityListView(list({
    items: [{} as DevicePriorityList['items'][number]], total: 1,
  })).kind, 'items', 'com itens');
});

test('paginação com unidade e página de casos que não finge ser o total', () => {
  eq(devicePriorityRangeText({ total: 23, page: 2, pageSize: 10 }), '11–20 de 23 dispositivos', 'faixa');
  eq(devicePriorityRangeText({ total: 1, page: 1, pageSize: 10 }), '1–1 de 1 dispositivo', 'singular');
  eq(casePageText(null), 'Sem evidência de vulnerabilidade elegível.', 'sem evidência');
  eq(casePageText({ total: 0, page: 1, pageSize: 10, items: [] }), 'Nenhum caso em aberto publicado nesta leitura.', 'zero é fato, não ausência');
  eq(casePageText({ total: 12, page: 1, pageSize: 10, items: [] }), '1–10 de 12 casos', 'página');
});

test('declaração de criticidade: gate de apresentação só para Manager e TenantAdmin', () => {
  eq(canDeclareCriticality('Manager'), true, 'Manager');
  eq(canDeclareCriticality('TenantAdmin'), true, 'TenantAdmin');
  eq(canDeclareCriticality('Analyst'), false, 'Analyst');
  eq(canDeclareCriticality(null), false, 'sem papel');
});

test('zero candidatos: só a completude da coleta permite chamar de ausência', () => {
  const unverified = devicePriorityListView(list({}, {
    candidateAssets: 0, absenceState: 'notVerifiable', absenceNote: 'Microsoft Defender Vulnerability Management: a aquisição mais recente foi parcial.',
  }));
  eq(unverified.kind, 'noCandidatesUnverified', 'parcial/não concluída ≠ ausência');
  const ut = 'text' in unverified ? unverified.text : '';
  ok(ut.includes('não permite concluir ausência') && ut.includes('parcial'), 'diz por quê');
  ok(!ut.includes('Nenhum dispositivo com vulnerabilidade'), 'não afirma ausência');
  const conclusive = devicePriorityListView(list({}, { candidateAssets: 0, absenceState: 'conclusive' }));
  eq(conclusive.kind, 'noCandidates', 'completa: ausência na aquisição completa');
  ok(('text' in conclusive ? conclusive.text : '').includes('aquisição completa mais recente'), 'com a base da conclusão');
  const failed = devicePriorityListView(list({}, {
    candidateAssets: 0, absenceState: 'conclusiveAttemptFailed', absenceNote: 'A tentativa mais recente de coleta falhou.',
  }));
  eq(failed.kind, 'noCandidates', 'zero anterior conhecido');
  ok(('text' in failed ? failed.text : '').includes('tentativa mais recente de coleta falhou'), 'com a ressalva da tentativa');
});

const declared: DevicePriorityCriticality = {
  storedValue: 4, state: 'declared', label: 'Criticidade 4 declarada em 12/09/2026 13:00 UTC por Gestora Demo',
  declaredValue: 4, declaredAt: '2026-09-12T13:00:00Z', declaredByName: 'Gestora Demo', note: null,
};

test('declaração: "nada foi alterado" só quando o servidor recusou; gravação confirmada nunca vira isso', () => {
  ok(declarationErrorText({ status: 403 }).includes('Nada foi alterado'), '403');
  ok(declarationErrorText({ status: 400, error: 'A criticidade declarada deve estar entre 1 e 4.' })
    .startsWith('A criticidade declarada deve estar entre 1 e 4.'), '400 com motivo do servidor');
  ok(!declarationErrorText({ status: 0 }).includes('Nada foi alterado'), 'sem resposta: não confirma nem nega');
  ok(!declarationErrorText({ status: 500 }).includes('Nada foi alterado'), 'erro do servidor: não confirma nem nega');
  const saved = declarationSavedNotRefreshedText(declared);
  ok(saved.includes('foi registrada') && saved.includes('não pôde ser atualizada'), 'gravou, releitura falhou');
  ok(!saved.includes('Nada foi alterado'), 'nunca nega a gravação');
});

test('inventário: a criticidade confirmada entra só na linha do ativo declarado', () => {
  const rows = [
    { id: 'a', name: 'nb-01', criticality: 1, criticalityConfirmed: false },
    { id: 'b', name: 'srv-02', criticality: 2, criticalityConfirmed: false },
  ];
  const next = applyDeclaredCriticality(rows, { assetId: 'a', criticality: declared });
  eq(next[0].criticality, 4, 'valor confirmado');
  eq(next[0].criticalityConfirmed, true, 'deixa de ser "não confirmada"');
  eq(next[1], rows[1], 'outra linha intacta');
  eq(rows[0].criticality, 1, 'sem mutar a lista anterior');
  const diverged = applyDeclaredCriticality(rows, { assetId: 'b', criticality: { ...declared, state: 'diverged', storedValue: 2 } });
  eq(diverged[1].criticalityConfirmed, false, 'divergente não é confirmada');
});

test('releitura em segundo plano: página que esvaziou volta à última válida, uma vez', () => {
  eq(pageAfterRefresh({ items: [{}] as DevicePriorityList['items'], total: 11, page: 2, pageSize: 10 }), null, 'página com itens');
  eq(pageAfterRefresh({ items: [], total: 10, page: 2, pageSize: 10 }), 1, 'item saiu da faixa: volta para a 1');
  eq(pageAfterRefresh({ items: [], total: 0, page: 3, pageSize: 10 }), null, 'fila vazia: estado próprio, sem correção');
  eq(pageAfterRefresh({ items: [], total: 5, page: 1, pageSize: 10 }), null, 'primeira página');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) {
  console.log(`${failures} falha(s)`);
  process.exit(1);
}
