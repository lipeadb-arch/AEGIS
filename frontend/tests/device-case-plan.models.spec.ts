/**
 * [AEGIS-JOURNEY-01] Testes de LÓGICA PURA da jornada "prioridade por dispositivo → plano → acompanhamento".
 *
 * O risco desta entrega não é layout — é a tela misturar unidades, adivinhar o plano de um caso por nome ou posição,
 * ou apresentar a saída da fila como correção. Estes testes fixam as fronteiras:
 *   (1) a chave do caso é dispositivo + CVE canônica — nome, posição e faixa não entram;
 *   (2) um plano nomeado no endereço só ocupa o painel do SEU caso;
 *   (3) cada origem leva à sua tela, com o plano no endereço;
 *   (4) a visão geral mantém dispositivos, casos e planos separados e diz quando o total é parcial;
 *   (5) sem fonte, sem leitura ou com falha, nada vira zero nem "nada a tratar";
 *   (6) nenhum estado da situação na fonte é apresentado como correção.
 *
 * Compilado por `tsc` (CommonJS) e executado por `node`, no mesmo padrão de remediation.models.spec.ts.
 */
import {
  ActionPlan,
  DEVICE_VERIFICATION_PENDING,
  DeviceCaseOrigin,
  activeDevicePlanFor,
  deviceOriginLead,
  devicePlansForCase,
  normalizeCve,
  originLabel,
  pinnedDevicePlanRejection,
  planLink,
  planSubject,
  seededDeviceProposal,
  seededDeviceTitle,
  sourceReadingTone,
} from '../src/app/models/remediation.models';
import {
  DashboardDevicePriority,
  devicePriorityCardView,
  devicePriorityItemParams,
  devicePriorityUnitLines,
} from '../src/app/models/dashboard-overview.models';
import { DevicePriorityDisposition } from '../src/app/models/device-priority.models';

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
function contains(haystack: string, needle: string, msg: string): void {
  if (!haystack.includes(needle)) throw new Error(`${msg}: "${needle}" não está em "${haystack}"`);
}
function notContains(haystack: string, needle: string, msg: string): void {
  if (haystack.toLowerCase().includes(needle.toLowerCase()))
    throw new Error(`${msg}: "${needle}" NÃO deveria estar em "${haystack}"`);
}

// ---- dados sintéticos -----------------------------------------------------------------------
const ASSET = 'a1000000-0000-0000-0000-000000000001';
const OTHER = 'b2000000-0000-0000-0000-000000000002';

function origin(over: Partial<DeviceCaseOrigin> = {}): DeviceCaseOrigin {
  return {
    schema: 'device-case-origin-v1', assetId: ASSET, assetName: 'pc-a.demo.example.com', assetNameIsPlaceholder: false,
    cveId: 'CVE-2024-3001', cveTitle: null, evaluatedAt: '2026-09-12T12:00:00Z', policyCode: 'AEGIS-PRIO-DEV',
    policyVersion: 1, caseBand: 'p1', caseBandLabel: 'Prioridade 1 · tratar primeiro', caseReason: '', wasDeterminingCase: true,
    deviceBand: 'p1', deviceBandLabel: 'Prioridade 1 · tratar primeiro', devicePositionReason: '', severityLabel: 'Crítica (CVSS 9.8)',
    cvssScore: 9.8, exploitLabel: 'Exploit público informado pela fonte', epss: null, source: 'Microsoft Defender',
    firstSeenAt: '2026-09-12T10:00:00Z', acquiredAt: '2026-09-12T11:00:00Z', acquisitionLabel: 'Aquisição atual',
    factors: [], caveats: [], informationLabel: '', absenceLabel: null,
    ...over,
  };
}

function plan(over: Partial<ActionPlan> = {}): ActionPlan {
  return {
    id: 'p-1', knightIndicatorId: null, originRunId: null, originAffectedCount: null, originSourceType: null, originMode: null,
    title: 'Tratar CVE-2024-3001', proposedAction: null, responsiblePerson: null, responsibleArea: null, dueDate: null,
    status: 'Aberto', isOverdue: false, isActive: true, nextStep: '', executionNotes: null, executionEvidenceRef: null,
    executedAt: null, completedAt: null, createdAt: '2026-09-12T12:00:00Z', cycleStartedAt: '2026-09-12T12:00:00Z',
    version: 1, latestValidation: null, applicableValidation: null, allowedTransitions: [], closureBlockedReason: null,
    validations: [], events: [], originKind: 'DeviceVulnerability', deviceOrigin: origin(),
    ...over,
  };
}

function summary(over: Partial<DashboardDevicePriority> = {}): DashboardDevicePriority {
  return {
    state: 'Available', note: null, evaluatedAt: '2026-09-12T12:00:00Z', policyCode: 'AEGIS-PRIO-DEV', policyVersion: 1,
    candidateAssets: 2, assetsEvaluated: 2, evaluationTruncated: false, completeThroughBand: null, truncationNote: null,
    assetsByBand: [
      { band: 'p1', label: '', count: 2 }, { band: 'p2', label: '', count: 0 }, { band: 'p3', label: '', count: 0 },
      { band: 'p4', label: '', count: 0 }, { band: 'insufficient', label: '', count: 0 },
    ],
    casesByBand: [
      { band: 'p1', label: '', count: 2 }, { band: 'p2', label: '', count: 0 }, { band: 'p3', label: '', count: 1 },
      { band: 'p4', label: '', count: 0 }, { band: 'insufficient', label: '', count: 0 },
    ],
    casesPartial: false, absenceState: 'conclusive', absenceNote: null, dispositions: [], top: [],
    plans: { active: 1, awaitingValidation: 0, overdue: 0, completed: 0 },
    ...over,
  };
}

console.log('device-case-plan.models');

test('a chave do caso é dispositivo + CVE canônica, sem nome nem posição', () => {
  eq(normalizeCve('  cve-2024-3001 '), 'CVE-2024-3001', 'CVE normalizada');
  const knight = plan({ id: 'k-1', originKind: 'KnightFinding', deviceOrigin: null, knightIndicatorId: 'AK-ENTRA-001' });
  const closed = plan({ id: 'p-0', isActive: false, status: 'Concluido', createdAt: '2026-09-01T12:00:00Z' });
  const active = plan({ id: 'p-1' });
  const otherCve = plan({ id: 'p-2', deviceOrigin: origin({ cveId: 'CVE-2024-3002' }) });
  const renamed = plan({ id: 'p-3', deviceOrigin: origin({ assetId: OTHER, assetName: 'pc-a.demo.example.com' }) });
  const all = [knight, closed, active, otherCve, renamed];
  const doCaso = devicePlansForCase(all, ASSET.toUpperCase(), 'cve-2024-3001');
  eq(doCaso.map((p) => p.id).join(','), 'p-1,p-0', 'só os planos do caso, do mais recente ao mais antigo');
  eq(activeDevicePlanFor(all, ASSET, 'CVE-2024-3001')?.id, 'p-1', 'o ativo do caso');
  eq(activeDevicePlanFor(all, OTHER, 'CVE-2024-3002'), null, 'mesmo nome de dispositivo não identifica o caso');
});

test('plano do endereço só ocupa o painel do SEU caso', () => {
  eq(pinnedDevicePlanRejection(plan(), ASSET, 'cve-2024-3001'), null, 'mesmo caso');
  contains(pinnedDevicePlanRejection(plan(), OTHER, 'CVE-2024-3001') ?? '', 'outro dispositivo', 'outro dispositivo');
  contains(pinnedDevicePlanRejection(plan(), ASSET, 'CVE-2024-9999') ?? '', 'CVE-2024-3001', 'outra CVE');
  contains(
    pinnedDevicePlanRejection(plan({ originKind: 'KnightFinding', deviceOrigin: null }), ASSET, 'CVE-2024-3001') ?? '',
    'não nasceu de um caso', 'outra origem');
});

test('cada origem leva à sua tela, com o plano no endereço', () => {
  const l = planLink(plan())!;
  eq(l.commands[0], '/priorities', 'caso de dispositivo abre a Central');
  eq(l.queryParams['device'], ASSET, 'dispositivo no endereço');
  eq(l.queryParams['cve'], 'CVE-2024-3001', 'CVE no endereço');
  eq(l.queryParams['plan'], 'p-1', 'plano no endereço');
  const k = planLink(plan({ originKind: 'KnightFinding', deviceOrigin: null, knightIndicatorId: 'AK-ENTRA-001', originRunId: 'r-1' }))!;
  eq(k.commands[0], '/identity', 'achado do KNIGHT abre o KNIGHT');
  eq(k.queryParams['run'], 'r-1', 'avaliação de origem preservada');
  eq(planLink(plan({ originKind: 'RiskTreatment', deviceOrigin: null })), null, 'tratamento de risco não tem destino aqui');
  eq(planSubject(plan()), 'CVE-2024-3001', 'identificador do caso');
  contains(originLabel(plan()), 'Vulnerabilidade em dispositivo · pc-a.demo.example.com · CVE-2024-3001', 'origem legível');
  notContains(originLabel(plan()), 'DeviceVulnerability', 'sem nome de enum');
});

test('registro de origem é identificado como tal; sugestões não prometem correção', () => {
  contains(deviceOriginLead(origin()), 'Registro de origem', 'rótulo de origem');
  contains(deviceOriginLead(origin()), 'AEGIS-PRIO-DEV v1', 'política e versão');
  eq(seededDeviceTitle(' cve-2024-3001', 'pc-a'), 'Tratar CVE-2024-3001 em pc-a', 'título sugerido');
  contains(seededDeviceProposal('CVE-2024-3001', null), 'não a comprova', 'a proposta diz o limite do plano');
  contains(DEVICE_VERIFICATION_PENDING, 'ainda não está disponível', 'verificação técnica pendente dita');
  contains(DEVICE_VERIFICATION_PENDING, 'plano concluído não significa dispositivo corrigido', 'concluído ≠ corrigido');
});

test('nenhum estado da situação na fonte tem tom de "resolvido"', () => {
  eq(sourceReadingTone('open'), 'warn', 'ainda em aberto na fonte');
  eq(sourceReadingTone('notReported'), 'info', 'não reportada é informação, não correção');
  for (const s of ['absenceNotVerifiable', 'notAttributable', 'outsideEligibleAcquisitions', 'assetNotFound'])
    eq(sourceReadingTone(s), 'muted', `${s} é neutro`);
});

test('visão geral: dispositivos, casos e planos em linhas próprias, sem soma entre unidades', () => {
  const lines = devicePriorityUnitLines(summary());
  contains(lines.devices, '2 dispositivos na fila (vulnerabilidade em aberto sem disposição registrada) · P1 2', 'unidade: dispositivos');
  eq(lines.disposed, null, 'sem casos dispostos, sem linha "fora da fila"');
  contains(lines.cases, '3 casos dispositivo × CVE priorizáveis · P1 2 · P3 1', 'unidade: casos');
  contains(lines.plans, '1 plano ativo', 'unidade: planos');
  contains(lines.plans, 'concluído ≠ dispositivo corrigido', 'concluído ≠ corrigido');
  notContains(lines.devices, '5', 'nenhuma soma de dispositivos com casos');
  const partial = devicePriorityUnitLines(summary({ casesPartial: true, evaluationTruncated: true, assetsEvaluated: 5000, candidateAssets: 6200 }));
  contains(partial.cases, 'parcial: só dos 5000 dispositivos avaliados', 'total parcial dito');
  contains(devicePriorityUnitLines(summary({ candidateAssets: 1, plans: { active: 0, awaitingValidation: 0, overdue: 0, completed: 1 } })).plans,
    '0 planos ativos', 'zero planos é contagem real de planos');
});

test('sem fonte, sem leitura ou com falha: nada vira zero nem "nada a tratar"', () => {
  eq(devicePriorityCardView(undefined).kind, 'missing', 'resposta sem o bloco');
  const falha = devicePriorityCardView(summary({ state: 'Unavailable', note: 'falhou', candidateAssets: null }));
  eq(falha.kind, 'unavailable', 'falha própria');
  eq(devicePriorityCardView(summary({ state: 'NoSource', candidateAssets: null, note: 'sem fonte' })).kind, 'noSource', 'sem fonte');
  eq(devicePriorityCardView(summary({ state: 'NeverCollected', candidateAssets: null })).kind, 'neverCollected', 'sem leitura');
  const naoVerificavel = devicePriorityCardView(summary({ candidateAssets: 0, absenceState: 'notVerifiable', absenceNote: 'parcial' }));
  eq(naoVerificavel.kind, 'noCandidatesUnverified', 'zero com coleta parcial não é ausência');
  contains('text' in naoVerificavel ? naoVerificavel.text : '', 'não permite concluir ausência', 'dito');
  const zero = devicePriorityCardView(summary({ candidateAssets: 0 }));
  contains('text' in zero ? zero.text : '', 'não comprova que os dispositivos estejam seguros', 'zero conclusivo com limite');
  eq(devicePriorityCardView(summary()).kind, 'data', 'com dados');
});

test('[revisão] fila vazia não é ausência: casos em aberto com disposição humana aparecem, com unidade e sem virar correção', () => {
  const disp: DevicePriorityDisposition[] = [
    { status: 'mitigated', label: 'Mitigação informada', count: 1 },
    { status: 'accepted', label: 'Risco aceito', count: 1 },
    { status: 'falsePositive', label: 'Falso positivo', count: 1 },
  ];
  // Coleta completa, todos os casos ainda em aberto na fonte, todos com disposição: zero candidatos.
  const only = devicePriorityCardView(summary({ candidateAssets: 0, assetsByBand: [], casesByBand: [], dispositions: disp }));
  eq(only.kind, 'onlyDispositions', 'estado próprio, não "sem candidatos"');
  const t = 'text' in only ? only.text : '';
  contains(t, 'Nenhum dispositivo na fila de prioridade', 'descreve a fila');
  contains(t, 'não é ausência de vulnerabilidade', 'e o que ela não é');
  contains(t, '3 casos dispositivo × CVE seguem em aberto na fonte com disposição humana registrada', 'conjunto contado, com unidade');
  contains(t, 'Mitigação informada: 1 · Risco aceito: 1 · Falso positivo: 1', 'cada disposição');
  contains(t, 'disposição não é correção', 'disposição não é correção');
  notContains(t, 'A fonte não reporta', 'nunca a frase de ausência');
  notContains(t, 'Nenhum dispositivo com vulnerabilidade', 'nem a frase antiga');

  // Mesmo estado com coleta parcial: a parcialidade é dita junto.
  const parcial = devicePriorityCardView(summary({
    candidateAssets: 0, absenceState: 'notVerifiable', absenceNote: 'A aquisição mais recente foi parcial.', dispositions: disp,
  }));
  eq(parcial.kind, 'onlyDispositions', 'parcial com dispostos');
  contains('text' in parcial ? parcial.text : '', 'não permite concluir a ausência de outros casos', 'parcialidade dita');

  // Cenário misto: fila com dispositivos + casos dispostos fora dela — linhas próprias, sem soma.
  const misto = devicePriorityUnitLines(summary({ dispositions: [{ status: 'accepted', label: 'Risco aceito', count: 2 }] }));
  contains(misto.devices, '2 dispositivos na fila', 'a população é a da fila');
  contains(misto.disposed ?? '', '2 casos dispositivo × CVE seguem em aberto na fonte', 'fora da fila, com unidade');
  eq(devicePriorityCardView(summary({ dispositions: [{ status: 'accepted', label: 'Risco aceito', count: 2 }] })).kind, 'data', 'misto tem dados');

  // Ausência real: nenhum candidato E nenhum caso disposto, coleta completa.
  const zero = devicePriorityCardView(summary({ candidateAssets: 0, assetsByBand: [], casesByBand: [], dispositions: [] }));
  eq(zero.kind, 'noCandidates', 'ausência real');
  contains('text' in zero ? zero.text : '', 'nem na fila, nem com disposição registrada', 'descreve os dois conjuntos');
  // Resposta anterior sem o campo: a tela não afirma ausência que não pode sustentar.
  const antigo = devicePriorityCardView({ ...summary({ candidateAssets: 0 }), dispositions: undefined });
  notContains('text' in antigo ? antigo.text : '', 'A fonte não reporta', 'sem o campo, sem afirmação de ausência');
  contains('text' in antigo ? antigo.text : '', 'Casos com disposição registrada ficam fora da fila', 'e diz o que falta');
  // Falha continua falha, sem números.
  eq(devicePriorityCardView(summary({ state: 'Unavailable', candidateAssets: null, dispositions: [] })).kind, 'unavailable', 'falha');
});

test('o item da visão geral abre a Central com dispositivo, caso e plano', () => {
  const p = devicePriorityItemParams({
    assetId: ASSET, assetName: 'pc-a', nameIsPlaceholder: false, position: 1, band: 'p1', bandLabel: '', cveId: 'CVE-2024-3001',
    caseBandLabel: '', activePlanId: 'p-1',
  });
  eq(p['device'], ASSET, 'dispositivo');
  eq(p['cve'], 'CVE-2024-3001', 'CVE determinante');
  eq(p['plan'], 'p-1', 'plano ativo');
  eq(p['tab'], 'achados', 'aba de achados');
});

console.log(`\n${count - failures}/${count} ok`);
if (failures > 0) {
  console.log(`${failures} falha(s)`);
  process.exit(1);
}
