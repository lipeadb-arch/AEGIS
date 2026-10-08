/**
 * [AEGIS-NIST-AI-ASSIST-01] Regras PURAS da assistência de IA na jornada NIST:
 *   (1) aplicar uma sugestão só muda o RASCUNHO, campo a campo; nível só se válido na escala;
 *   (2) a gravação leva só os campos aplicados que continuam com conteúdo; sem campo, sem referência;
 *   (3) procedência legível (real × simulada, edição, revisão declarada) — nunca dita como aprovação;
 *   (4) fontes abrem âncoras da própria tela; nenhum link externo vem da resposta;
 *   (5) atualidade pela impressão digital; resumo executivo na ordem do AEGIS; seleção NIST lida da URL.
 *
 * Compilado por `tsc` (CommonJS) e executado por `node`, como os demais specs de lógica.
 */
import '@angular/compiler';
import {
  EXECUTIVE_SECTIONS,
  NistAssistSource,
  NistAssistView,
  NistExecutiveSummary,
  applyAssistToDraft,
  assistErrorText,
  assistIsCurrent,
  assistanceRef,
  assistedFieldsText,
  assistedFieldsToSend,
  executiveDraftFrom,
  executiveProvenance,
  sourceAnchorId,
} from '../src/app/models/nist-assist.models';
import { draftFrom, NistAssistedField } from '../src/app/models/nist.models';
import { nistSelectionFromUrl } from '../src/app/services/agent-state.service';

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

function view(applicable: Record<string, string>, over: Partial<NistAssistView> = {}): NistAssistView {
  return {
    id: 'g1', kind: 'Subcategory', focus: null, assessmentId: 'a', cycleId: 'c', scopeId: 's', subcategoryCode: 'GV.PO-01', findingId: null,
    mode: 'Simulated', availability: 'Simulated', modeLabel: 'Demonstração', generatedAt: '2026-10-06T12:00:00Z', requestedByName: 'Gestora Demo',
    contextFingerprint: 'fp-1', contextSummary: '', current: true, staleOnArrival: false, reused: false, sources: [], sections: [], level: null,
    levelNote: null, procedures: [], applicable, validationNotes: [], disclaimer: '', ...over,
  };
}
function source(target: string, id: string | null, code: string | null = null): NistAssistSource {
  return {
    key: 'S1', kind: 'X', kindLabel: 'X', basis: 'Fact', basisLabel: 'Fato sustentado', title: 't', detail: null, date: null, status: null,
    isDemo: false, contentExamined: true, limitation: null, link: { target, id, code },
  };
}
function field(over: Partial<NistAssistedField> = {}): NistAssistedField {
  return {
    field: 'rationale', label: 'justificativa', assistanceId: 'g1', mode: 'Simulated', generatedAt: '2026-10-06T12:00:00Z', requestedByName: 'Gestora Demo',
    incorporatedByName: 'Gestora Demo', incorporatedAt: '2026-10-06T12:30:00Z', edited: false, staleAcknowledged: false, ...over,
  };
}

console.log('nist-assist.models');

test('(1) aplicar muda só o campo escolhido do rascunho; nível só se válido; campo sem texto não aplica', () => {
  const base = { ...draftFrom(null), rationale: 'minha justificativa', notApplicable: true };
  const v = view({ rationale: '• texto da IA', currentLevel: '3', gaps: '• lacuna' });
  const r = applyAssistToDraft(base, v, 'rationale')!;
  eq(r.rationale, '• texto da IA', 'justificativa aplicada');
  eq(base.rationale, 'minha justificativa', 'o rascunho original não é mutado');
  const lv = applyAssistToDraft(base, v, 'currentLevel')!;
  eq(lv.currentLevel, 3, 'nível aplicado');
  eq(lv.notApplicable, false, 'aplicar nível tira o "não se aplica"');
  eq(applyAssistToDraft(base, view({ currentLevel: '3.5' }), 'currentLevel'), null, 'nível fora da escala não aplica');
  eq(applyAssistToDraft(base, view({ currentLevel: '7' }), 'currentLevel'), null, 'nível 7 não aplica');
  eq(applyAssistToDraft(base, v, 'riskImpact'), null, 'campo sem texto aplicável não aplica');
  eq(applyAssistToDraft(base, v, 'ownerName'), null, 'campo fora da assistência não aplica');
});

test('(2) a gravação leva só os campos aplicados que continuam com conteúdo; sem campos, sem referência', () => {
  const d = { ...draftFrom(null), rationale: 'texto', gaps: '   ', currentLevel: 2 };
  eq(assistedFieldsToSend(d, ['gaps', 'rationale', 'rationale', 'currentLevel']).join(','), 'currentLevel,rationale', 'esvaziado sai; sem repetição');
  eq(assistedFieldsToSend({ ...d, notApplicable: true }, ['currentLevel']).length, 0, 'nível de "não se aplica" não vai');
  eq(assistanceRef('g1', []), null, 'sem campos, sem referência');
  const ref = assistanceRef('g1', ['rationale'], true)!;
  ok(ref.assistanceId === 'g1' && ref.acknowledgeStale && ref.fields[0] === 'rationale', 'referência com a revisão declarada');
});

test('(3) procedência legível: simulada × real, edição e revisão declarada; nunca "aprovação"', () => {
  const sim = assistedFieldsText([field(), field({ field: 'currentLevel', label: 'nível', edited: true })])!;
  ok(sim.includes('SIMULADA (demonstração)'), 'simulada dita');
  ok(sim.includes('nível (editada)'), 'edição dita');
  ok(sim.includes('Não é aprovação nem revisão'), 'não é aprovação');
  const real = assistedFieldsText([field({ mode: 'Real', staleAcknowledged: true })])!;
  ok(real.includes('sugestão da IA') && real.includes('desatualizada, revisada pela pessoa'), 'real e revisão declarada');
  eq(assistedFieldsText([]), null, 'sem campos, sem texto');
});

test('(4) fontes abrem âncoras da tela; destino desconhecido não vira link', () => {
  eq(sourceAnchorId(source('Evidence', 'e1')), 'ev-e1', 'evidência');
  eq(sourceAnchorId(source('Procedure', 'p1')), 'proc-p1', 'procedimento');
  eq(sourceAnchorId(source('Finding', 'f1')), 'achado-f1', 'achado');
  eq(sourceAnchorId(source('Evaluation', 'x')), 'sc-eval', 'avaliação');
  eq(sourceAnchorId(source('Outcome', null)), 'sc-outcome', 'resultado esperado');
  eq(sourceAnchorId(source('External', 'https://x')), null, 'nada externo');
  eq(sourceAnchorId({ ...source('Evidence', 'e1'), link: null }), null, 'sem vínculo');
});

test('(5) atualidade pela impressão digital; nascida desatualizada nunca é atual', () => {
  ok(assistIsCurrent(view({}), 'fp-1'), 'mesma impressão digital');
  ok(!assistIsCurrent(view({}), 'fp-2'), 'contexto mudou');
  ok(!assistIsCurrent(view({}, { staleOnArrival: true }), 'fp-1'), 'nasceu desatualizada');
});

test('(6) resumo executivo: rascunho na ordem do AEGIS, a partir da sugestão ou do aceito; procedência sem confundir revisão', () => {
  const d = executiveDraftFrom({ priorities: '1. Tratar.', situation: 'Atual 1,5.' });
  eq(d.length, EXECUTIVE_SECTIONS.length, 'todas as seções');
  eq(d[0].key, 'situation', 'ordem do AEGIS');
  eq(d.find((x) => x.key === 'priorities')!.text, '1. Tratar.', 'texto da sugestão');
  eq(d.find((x) => x.key === 'gaps')!.text, '', 'seção ausente vem vazia');
  eq(executiveDraftFrom([{ key: 'nextSteps', title: 'Próximos passos', text: 'Reunião.' }]).find((x) => x.key === 'nextSteps')!.text, 'Reunião.', 'do aceito');
  const s: NistExecutiveSummary = {
    id: 'x', sections: [], origin: 'Assisted', mode: 'Simulated', assistanceId: 'g', generatedAt: null, requestedByName: null,
    acceptedByName: 'Gestora Demo', acceptedAt: '2026-10-06T12:00:00Z', edited: true, staleAcknowledged: false, current: true,
    reviewedByName: 'Revisora Demo', reviewedAt: '2026-10-06T13:00:00Z', reviewCurrent: false, reviewNote: null, version: 2,
  };
  const p = executiveProvenance(s);
  ok(p.includes('SIMULADO') && p.includes('com edição'), 'origem e edição');
  ok(p.includes('sem revisão humana posterior'), 'revisão que não vale para o conteúdo atual não é dita como revisão');
  ok(executiveProvenance({ ...s, origin: 'Manual', mode: null }).startsWith('Redigido pela pessoa'), 'manual');
});

test('(7) erro por motivo; a jornada manual segue', () => {
  ok(assistErrorText('Disabled', 'x').includes('desativada'), 'desativada');
  ok(assistErrorText('Timeout', 'x').includes('tempo limite'), 'tempo');
  ok(assistErrorText('InvalidResponse', 'x').includes('descartada'), 'inválida');
  eq(assistErrorText('Unavailable', 'mensagem do servidor'), 'mensagem do servidor', 'indisponível: mensagem do servidor');
});

test('(8) seleção NIST lida da URL para o Auditor: só com avaliação, rodada e escopo; código só em subcategoria', () => {
  const q = '?avaliacao=a1&rodada=c1&escopo=s1';
  const sub = nistSelectionFromUrl(`/nist/gv/gv.po-01${q}#achado-1`)!;
  ok(sub.assessmentId === 'a1' && sub.cycleId === 'c1' && sub.scopeId === 's1', 'seleção');
  eq(sub.code, 'GV.PO-01', 'código da subcategoria');
  eq(nistSelectionFromUrl(`/nist/gv${q}`)!.code, null, 'função sem código');
  eq(nistSelectionFromUrl(`/nist/gv/documentos${q}`)!.code, null, 'página que não é subcategoria');
  eq(nistSelectionFromUrl('/nist/gv?avaliacao=a1'), null, 'seleção incompleta');
  eq(nistSelectionFromUrl(`/knight${q}`), null, 'fora do NIST');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) process.exit(1);
