/**
 * [AEGIS-CROSS-SOURCE-01] Testes de LÓGICA PURA da apresentação das situações entre fontes (frontend).
 *
 * O que está sendo provado é comportamento: o estado vem do backend e não é reinterpretado; ausência de contagem não
 * vira 0; prévia truncada nunca se apresenta como total; unidades (situações, ativos, CVEs) ficam explícitas; e os
 * estados de linguagem do produto (carregando, falha, sem fonte, sem leitura, população vazia, zero apurado, filtro
 * vazio) são distintos. Compiladas por `tsc`, executadas por `node`.
 */
import {
  CROSS_SOURCE_STATE_FILTERS,
  CrossSourceListView,
  CrossSourceRuleTally,
  CrossSourceSituationList,
  CrossSourceSituationSummary,
  acquisitionSpanText,
  acquisitionTone,
  associationTone,
  crossSourceEvaluationTotals,
  evidenceBasisLabel,
  crossSourceListView,
  crossSourceStateTone,
  crossSourceSummaryText,
  cveCountText,
  cvePageText,
  cvePreviewText,
  pageRangeText,
  ruleShortLabel,
  truncationNote,
} from '../src/app/models/cross-source.models';

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

function summary(over: Partial<CrossSourceSituationSummary>): CrossSourceSituationSummary {
  return {
    readingState: 'Available',
    readingNote: null,
    assetsWithBothSources: 4,
    assetsEvaluated: 4,
    evaluationTruncated: false,
    situationsIdentified: 0,
    assetsWithSituations: 0,
    byRule: [],
    ...over,
  };
}

function list(over: Partial<CrossSourceSituationList>, s: Partial<CrossSourceSituationSummary> = {}): CrossSourceSituationList {
  return {
    evaluatedAt: '2026-09-11T12:00:00Z',
    heading: 'Situações identificadas entre fontes',
    scope: '',
    policy: { maxEvidenceAgeDays: 7, maxSourceActivityAgeDays: 30, acquisitionGapCaveatHours: 24, description: '' },
    summary: summary(s),
    stateFilter: 'identified',
    ruleFilter: null,
    items: [],
    total: 0,
    page: 1,
    pageSize: 10,
    ...over,
  };
}

console.log('cross-source.models');

test('tom: identificada pede atenção; conflito/contradição são aviso; o resto é neutro — nunca "ok"', () => {
  eq(crossSourceStateTone('identified'), 'attention', 'situação identificada');
  eq(crossSourceStateTone('linkConflict'), 'warn', 'vínculo em conflito');
  eq(crossSourceStateTone('contradictoryEvidence'), 'warn', 'registros contraditórios');
  eq(crossSourceStateTone('notIdentified'), 'muted', 'nenhuma condição NÃO é "seguro"');
  eq(crossSourceStateTone('insufficientEvidence'), 'muted', 'insuficiência é neutra');
});

test('aquisição: só a mais recente e completa é neutra-positiva; anterior, parcial e não concluída são aviso', () => {
  eq(acquisitionTone('current'), 'ok', 'atual completa');
  eq(acquisitionTone('currentPartial'), 'warn', 'parcial');
  eq(acquisitionTone('currentUnconcluded'), 'warn', 'publicação não concluída');
  eq(acquisitionTone('previous'), 'warn', 'evidência anterior preservada');
  eq(acquisitionTone('notRecorded'), 'muted', 'sem marca: nada é afirmado');
});

test('contagem de CVEs: ausência é "—", nunca 0; singular/plural', () => {
  eq(cveCountText(null), '—', 'sem evidência usável');
  eq(cveCountText(undefined), '—', 'campo ausente');
  eq(cveCountText(0), '0 CVEs', 'zero apurado é número');
  eq(cveCountText(1), '1 CVE', 'singular');
  eq(cveCountText(12), '12 CVEs', 'plural');
});

test('prévia truncada diz quantas faltam e não se apresenta como total', () => {
  eq(cvePreviewText({ cvePreview: ['CVE-1', 'CVE-2', 'CVE-3'], cvePreviewTruncated: true, openCveCount: 10 }),
    'CVE-1, CVE-2, CVE-3 e mais 7', 'restante explícito');
  eq(cvePreviewText({ cvePreview: ['CVE-1'], cvePreviewTruncated: false, openCveCount: 1 }), 'CVE-1', 'prévia completa');
  eq(cvePreviewText({ cvePreview: [], cvePreviewTruncated: false, openCveCount: null }), '—', 'sem prévia');
});

test('página com unidade explícita e intervalo real', () => {
  eq(pageRangeText(23, 3, 10, 'situações'), '21–23 de 23 situações', 'última página parcial');
  eq(pageRangeText(0, 1, 10, 'situações'), '0 situações', 'vazio');
  eq(cvePageText({ total: 12, page: 2, pageSize: 10, items: [], excludedOutOfPolicy: 0, noLongerReported: 0 }),
    '11–12 de 12 CVEs distintas', 'CVEs distintas, não ocorrências');
  eq(cvePageText(null), 'Sem evidência de vulnerabilidade elegível.', 'sem página não vira zero');
});

test('resumo com unidades: situações (ativo × regra) × ativos', () => {
  eq(crossSourceSummaryText(summary({ situationsIdentified: 3, assetsWithSituations: 2, assetsWithBothSources: 5 })),
    '3 situações identificadas em 2 ativos · 5 ativos com registros das duas fontes', 'unidades distintas');
  eq(crossSourceSummaryText(summary({ situationsIdentified: 1, assetsWithSituations: 1, assetsWithBothSources: 1 })),
    '1 situação identificada em 1 ativo · 1 ativo com registros das duas fontes', 'singular');
});

test('teto atingido: totais declarados parciais; sem teto, nada', () => {
  eq(truncationNote(summary({})), null, 'sem truncamento');
  const note = truncationNote(summary({ evaluationTruncated: true, assetsEvaluated: 5000, assetsWithBothSources: 7200 }));
  eq(note!.includes('5000 de 7200'), true, 'avaliados × população');
  eq(note!.includes('parciais'), true, 'declara parcialidade');
});

test('estados da lista: carregando, falha, sem fonte, sem leitura, população vazia, zero apurado, filtro vazio', () => {
  eq(crossSourceListView(null, true, null).kind, 'loading', 'carregando');
  eq(crossSourceListView(null, false, 'API inacessível').kind, 'error', 'falha não vira vazio');
  eq(crossSourceListView(list({}, { readingState: 'NoSource', readingNote: 'x' })).kind, 'noSource', 'sem fonte');
  eq(crossSourceListView(list({}, { readingState: 'NeverCollected' })).kind, 'neverCollected', 'sem leitura');
  eq(crossSourceListView(list({}, { assetsWithBothSources: 0, assetsEvaluated: 0 })).kind, 'noPopulation', 'nenhum ativo avaliável');
  const zero = crossSourceListView(list({}, { byRule: [tally('XS-DEF-INT-NONCOMPLIANT', { notIdentified: 4 })] }));
  eq(zero.kind, 'zeroConclusive', 'zero apurado');
  eq((zero as { text: string }).text.includes('não comprova'), true, 'zero não é "seguro"');
  eq(crossSourceListView(list({ stateFilter: 'linkConflict' })).kind, 'filterEmpty', 'filtro de estado sem correspondência');
  eq(crossSourceListView(list({ ruleFilter: 'XS-DEF-INT-UNENCRYPTED' })).kind, 'filterEmpty', 'filtro de regra sem correspondência');
});

function crossSourceListViewArgs(l: CrossSourceSituationList) {
  return crossSourceListView(l, false, null);
}

test('com itens, a lista é exibida', () => {
  const l = list({
    total: 1,
    items: [{
      assetId: 'a', assetName: 'pc-01', nameIsPlaceholder: false, ruleCode: 'XS-DEF-INT-NONCOMPLIANT', ruleVersion: 1,
      title: 't', state: 'identified', stateLabel: 'Situação identificada', hasCaveats: false, summary: 's',
      openCveCount: 2, cvePreview: ['CVE-1', 'CVE-2'], cvePreviewTruncated: false, caveats: [],
      evidenceBasis: 'supporting', vulnerabilityAcquisitions: null, deviceManagementAcquisitions: null,
    }],
  }, { situationsIdentified: 1, assetsWithSituations: 1 });
  eq(crossSourceListViewArgs(l).kind, 'items', 'lista');
});

function tally(ruleCode: string, over: Partial<CrossSourceRuleTally>): CrossSourceRuleTally {
  return {
    ruleCode, ruleVersion: 1, title: ruleCode, identified: 0, identifiedWithCaveats: 0, notIdentified: 0,
    notIdentifiedWithCaveats: 0, insufficientEvidence: 0, linkConflict: 0, contradictoryEvidence: 0, ...over,
  };
}

const RA = 'XS-DEF-INT-NONCOMPLIANT';
const RB = 'XS-DEF-INT-UNENCRYPTED';
const textOf = (v: CrossSourceListView) => ('text' in v ? v.text : '');

test('lista vazia inteiramente inconclusiva: o zero não é conclusivo e diz por quê', () => {
  const v = crossSourceListView(list({}, { byRule: [tally(RA, { insufficientEvidence: 4 }), tally(RB, { insufficientEvidence: 4 })] }));
  eq(v.kind, 'zeroInconclusive', 'inconclusiva');
  eq(textOf(v).includes('não coexistem'), false, 'não verificado ≠ "não coexistem"');
  eq(textOf(v).includes('das 8 avaliações (ativo × regra), 8 com evidência insuficiente'), true, 'motivo com a contagem');
  eq(textOf(v).includes('Não foi demonstrada a ausência'), true, 'não afirma ausência');
});

test('lista vazia inteiramente em conflito: nenhuma combinação foi verificada', () => {
  const v = crossSourceListView(list({}, { byRule: [tally(RA, { linkConflict: 4 }), tally(RB, { linkConflict: 4 })] }));
  eq(v.kind, 'zeroInconclusive', 'conflito');
  eq(textOf(v).includes('8 com vínculo em conflito'), true, 'conflitos contados');
});

test('população mista: parte concluída sem a combinação e parte inconclusiva — as duas são ditas', () => {
  const v = crossSourceListView(list({}, {
    byRule: [tally(RA, { notIdentified: 2, insufficientEvidence: 1, linkConflict: 1 }), tally(RB, { notIdentified: 3, linkConflict: 1 })],
  }));
  eq(v.kind, 'zeroMixed', 'mista');
  eq(textOf(v).includes('em 5 avaliações a combinação não se formou'), true, 'parte concluída');
  eq(textOf(v).includes('outras 3 não puderam ser verificadas (1 com evidência insuficiente e 2 com vínculo em conflito)'), true,
    'parte inconclusiva');
  eq(textOf(v).includes('não comprova'), true, 'não é "seguro"');
});

test('conclusivamente negativa: a combinação não se formou em nenhuma avaliação — sem afirmar segurança', () => {
  const v = crossSourceListView(list({}, { byRule: [tally(RA, { notIdentified: 4 }), tally(RB, { notIdentified: 4 })] }));
  eq(v.kind, 'zeroConclusive', 'conclusiva');
  eq(textOf(v).includes('nas 8 avaliações (ativo × regra) de 4 ativos avaliados'), true, 'unidades explícitas');
  eq(textOf(v).includes('não comprova'), true, 'não é "seguro"');
  eq((v as { limited: boolean }).limited, false, 'sem recorte');
});

test('recorte pelo teto: nem o zero conclusivo vale para o ambiente inteiro', () => {
  const v = crossSourceListView(list({}, {
    evaluationTruncated: true, assetsEvaluated: 4, assetsWithBothSources: 9,
    byRule: [tally(RA, { notIdentified: 4 }), tally(RB, { notIdentified: 4 })],
  }));
  eq((v as { limited: boolean }).limited, true, 'recorte declarado');
  eq(textOf(v).includes('Recorte limitado: avaliados 4 de 9'), true, 'avaliados × população');
});

test('resumo declara as avaliações inconclusivas e o recorte parcial', () => {
  const s = summary({
    situationsIdentified: 1, assetsWithSituations: 1, assetsWithBothSources: 4,
    byRule: [tally(RA, { identified: 1, insufficientEvidence: 3 }), tally(RB, { linkConflict: 4 })],
  });
  eq(crossSourceSummaryText(s),
    '1 situação identificada em 1 ativo · 4 ativos com registros das duas fontes · 7 avaliações inconclusivas (ativo × regra)',
    'pendências explícitas');
  eq(crossSourceSummaryText({ ...s, evaluationTruncated: true }).endsWith(' · recorte parcial (teto de avaliação)'), true, 'recorte');
  eq(crossSourceEvaluationTotals(s).evaluations, 8, 'avaliações = ativos × regras');
});

test('rótulo do filtro descreve a combinação, sem negar os fatos isolados', () => {
  eq(CROSS_SOURCE_STATE_FILTERS.find((f) => f.value === 'notIdentified')!.label, 'Combinação não identificada', 'rótulo');
});

test('associação: o tom segue a conclusão da avaliação, nunca o critério', () => {
  eq(associationTone('proven'), 'ok', 'comprovada');
  eq(associationTone('conflict'), 'warn', 'conflito');
  eq(associationTone('notProven'), 'muted', 'não comprovada');
  eq(associationTone('sourceMissing'), 'muted', 'fonte ausente');
});

test('aquisições: intervalo e quantidade, nunca só a mais recente; base explícita', () => {
  const fmt = (s: string) => s.slice(0, 10);
  eq(acquisitionSpanText({ first: '2026-09-09T10:00:00Z', last: '2026-09-12T10:00:00Z', acquisitions: 2 }, fmt),
    '2026-09-09 → 2026-09-12 (2 aquisições)', 'intervalo');
  eq(acquisitionSpanText({ first: '2026-09-12T10:00:00Z', last: '2026-09-12T10:00:00Z', acquisitions: 1 }, fmt), '2026-09-12', 'uma');
  eq(acquisitionSpanText(null, fmt, 'supporting'), 'não sustenta esta conclusão', 'fonte fora da conclusão');
  eq(acquisitionSpanText(null, fmt, 'none'), '—', 'sem evidência');
  eq(evidenceBasisLabel('supporting'), 'Aquisições que sustentam a conclusão', 'base: sustentação');
  eq(evidenceBasisLabel('available').includes('sem conclusão combinada'), true, 'base: disponível');
});

test('rótulos curtos só para as regras conhecidas; código desconhecido passa como veio', () => {
  eq(ruleShortLabel('XS-DEF-INT-NONCOMPLIANT'), 'Vulnerabilidades + não conforme', 'regra A');
  eq(ruleShortLabel('XS-DEF-INT-UNENCRYPTED'), 'Vulnerabilidades + sem criptografia', 'regra B');
  eq(ruleShortLabel('XS-FUTURA'), 'XS-FUTURA', 'não inventa nome');
});

console.log(`\n${count - failures}/${count} testes de lógica do frontend (cross-source) aprovados.`);
if (failures > 0) throw new Error(`${failures} teste(s) de lógica do frontend (cross-source) falharam`);
