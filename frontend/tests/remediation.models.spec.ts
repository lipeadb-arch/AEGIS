/**
 * [AEGIS-MVP-PRODUCT-03] Testes de LÓGICA PURA da jornada de remediação.
 *
 * O risco desta entrega não é layout — é a tela transformar trabalho relatado em correção comprovada. Estes
 * testes fixam exatamente as fronteiras que não podem escorregar:
 *   (1) etapa do plano ≠ resultado no achado — uma ação concluída sem validação diz isso, em voz alta;
 *   (2) redução observada NÃO é resolução, e sem os dois conjuntos preservados não se afirma QUAIS objetos
 *       foram corrigidos;
 *   (3) atestação humana nunca conta como comprovação técnica;
 *   (4) atraso acompanha a etapa, jamais a substitui;
 *   (5) "existe ação ativa?" tem uma resposta só, compartilhada pelo KNIGHT e pelas Prioridades.
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node`, no mesmo padrão de knight-affected.models.spec.ts.
 */
import {
  ActionPlan,
  ActionPlanStatus,
  ActionPlanValidation,
  actionResult,
  actionSituation,
  actionStatusLabel,
  activePlanFor,
  allowedTransitions,
  isTechnicallyProven,
  outcomeLabel,
  seededProposal,
  validationBasis,
} from '../src/app/models/remediation.models';

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
function contains(haystack: string, needle: string, msg: string): void {
  if (!haystack.includes(needle)) throw new Error(`${msg}: "${needle}" não está em "${haystack}"`);
}
function notContains(haystack: string, needle: string, msg: string): void {
  if (haystack.includes(needle)) throw new Error(`${msg}: "${needle}" NÃO deveria estar em "${haystack}"`);
}

const RUN_ORIGEM = '11111111-1111-1111-1111-111111111111';
const RUN_NOVA = '22222222-2222-2222-2222-222222222222';

function validation(over: Partial<ActionPlanValidation> = {}): ActionPlanValidation {
  return {
    method: 'NewAssessment',
    outcome: 'ReductionObserved',
    validationRunId: RUN_NOVA,
    evidenceReference: null,
    observedBefore: 12,
    observedAfter: 8,
    objectsNoLongerPresent: 4,
    comparedBySets: true,
    rationale: 'A quantidade afetada caiu de 12 para 8.',
    decidedAt: '2026-09-07T12:00:00Z',
    decidedByName: 'Analista',
    ...over,
  };
}

function plan(over: Partial<ActionPlan> = {}): ActionPlan {
  return {
    id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    knightIndicatorId: 'AK-ENTRA-001',
    originRunId: RUN_ORIGEM,
    originAffectedCount: 12,
    title: 'Registrar segundo fator nas contas administrativas',
    proposedAction: null,
    responsiblePerson: 'Equipe de Identidade',
    responsibleArea: 'TI',
    dueDate: '2026-10-01',
    status: 'EmAndamento',
    isOverdue: false,
    isActive: true,
    nextStep: 'Concluir a execução e registrar o que foi feito.',
    executionNotes: null,
    executionEvidenceRef: null,
    executedAt: null,
    completedAt: null,
    createdAt: '2026-09-01T10:00:00Z',
    version: 3,
    latestValidation: null,
    validations: [],
    events: [],
    ...over,
  };
}

console.log('\n[AEGIS-MVP-PRODUCT-03] remediation.models');

// ---- (1) Etapa do plano ≠ resultado no achado --------------------------------------------------

test('ação CONCLUÍDA sem validação declara que a correção não foi comprovada', () => {
  const p = plan({ status: 'Concluido', isActive: false, latestValidation: null });
  contains(actionResult(p), 'não foi comprovada', 'encerrar o trabalho não comprova o ambiente');
  eq(actionStatusLabel(p.status), 'Concluída', 'a etapa continua sendo dita como é');
});

test('ação em andamento sem validação diz "ainda não validado", nunca silêncio', () => {
  eq(actionResult(plan()), 'Ainda não validado.', 'silêncio se leria como "está tudo bem"');
});

// ---- (2) Redução observada ≠ resolução ---------------------------------------------------------

test('redução observada não é apresentada como resolvido, e traz as duas quantidades', () => {
  const p = plan({ latestValidation: validation() });
  const r = actionResult(p);
  contains(r, 'Redução observada', 'a leitura precisa dizer que o achado continua exposto');
  contains(r, 'ainda exposto', 'sem essa ressalva a linha se leria como conclusão');
  contains(r, '12 → 8', 'as duas quantidades sustentam a afirmação');
  notContains(r, 'Resolvid', 'redução não é resolução');
});

test('exposição encerrada é o único desfecho automático que fala em encerramento', () => {
  eq(
    outcomeLabel('ExposureCleared'),
    'Exposição encerrada na nova avaliação',
    'e mesmo ele nomeia a AVALIAÇÃO, não o mundo',
  );
});

test('sem os dois conjuntos preservados, a base é quantidade — não quais objetos foram corrigidos', () => {
  const base = validationBasis(validation({ comparedBySets: false, objectsNoLongerPresent: null }));
  contains(base, 'quantidade', 'a base precisa dizer o que sustenta a conclusão');
  contains(base, 'não é possível afirmar quais objetos', 'a limitação é dita, não escondida');
});

test('com os conjuntos preservados, a base cita a comparação dos conjuntos', () => {
  contains(validationBasis(validation()), 'conjuntos preservados', 'aí sim a afirmação mais forte se sustenta');
});

// ---- (3) Atestação humana nunca é comprovação técnica -------------------------------------------

test('atestação humana NÃO conta como melhora tecnicamente comprovada', () => {
  const humana = validation({ method: 'HumanEvidence', outcome: 'HumanAttested', comparedBySets: false });
  ok(!isTechnicallyProven(humana), 'um "feito" com anexo não é verificação do ambiente');
  contains(outcomeLabel('HumanAttested'), 'não é comprovação técnica', 'o rótulo carrega o limite');
  contains(validationBasis(humana), 'não verificou o ambiente', 'a base explicita o que faltou');
});

test('evidência insuficiente também não conta como melhora comprovada', () => {
  ok(
    !isTechnicallyProven(validation({ outcome: 'EvidenceInsufficient' })),
    'ausência de prova não pode virar prova',
  );
  ok(!isTechnicallyProven(validation({ outcome: 'NoChangeObserved' })), 'sem melhora não é melhora');
  ok(!isTechnicallyProven(null), 'ausência de validação não comprova nada');
});

test('só nova avaliação com melhora observada conta como comprovação técnica', () => {
  ok(isTechnicallyProven(validation({ outcome: 'ExposureCleared' })), 'exposição encerrada é comprovação');
  ok(isTechnicallyProven(validation({ outcome: 'ReductionObserved' })), 'redução observada também é');
});

// ---- (4) Atraso acompanha a etapa, não a substitui ----------------------------------------------

test('atraso é dito JUNTO com a etapa operacional', () => {
  const atrasada = actionSituation(plan({ status: 'EmAndamento', isOverdue: true }));
  contains(atrasada, 'Em andamento', 'apagar a etapa esconderia onde o trabalho parou');
  contains(atrasada, 'em atraso', 'e o atraso continua visível');
});

test('sem atraso, a situação é só a etapa', () => {
  eq(actionSituation(plan({ status: 'Aberto' })), 'Aberta', 'nenhum adorno quando não há atraso');
});

test('a etapa legada "Vencido" é identificada como legado e nunca é destino de transição', () => {
  contains(actionStatusLabel('Vencido'), 'legado', 'quem vê precisa saber que aquilo não é do fluxo novo');
  const destinos: ActionPlanStatus[] = ([] as ActionPlanStatus[]).concat(
    allowedTransitions('Aberto'),
    allowedTransitions('EmAndamento'),
    allowedTransitions('AguardandoValidacao'),
    allowedTransitions('Concluido'),
    allowedTransitions('Vencido'),
  );
  ok(!destinos.includes('Vencido'), 'atraso vem do prazo — nunca vira etapa');
});

// ---- (5) Transições do fluxo mínimo -------------------------------------------------------------

test('não se conclui direto de "Aberto": nada foi sequer relatado', () => {
  ok(!allowedTransitions('Aberto').includes('Concluido'), 'concluir sem execução nem validação é o atalho errado');
});

test('concluir só a partir de "Aguardando validação"', () => {
  ok(allowedTransitions('AguardandoValidacao').includes('Concluido'), 'é o passo seguinte à execução relatada');
  ok(!allowedTransitions('EmAndamento').includes('Concluido'), 'pular a comprovação não é uma transição do fluxo');
});

test('uma ação encerrada pode ser reaberta: o problema pode voltar', () => {
  ok(allowedTransitions('Concluido').includes('EmAndamento'), 'reabrir é melhor do que duplicar');
});

// ---- (6) Uma resposta só para "existe ação ativa?" ----------------------------------------------

test('a ação ATIVA do achado é a que decide entre criar e abrir', () => {
  const ativa = plan({ id: 'ativa', knightIndicatorId: 'AK-ENTRA-002', isActive: true });
  const encerrada = plan({ id: 'velha', knightIndicatorId: 'AK-ENTRA-002', isActive: false, status: 'Concluido' });
  eq(activePlanFor([encerrada, ativa], 'AK-ENTRA-002')?.id, 'ativa', 'a encerrada não bloqueia nem responde');
  eq(activePlanFor([encerrada], 'AK-ENTRA-002'), null, 'ação encerrada LIBERA a origem para um novo ciclo');
  eq(activePlanFor([ativa], 'AK-ENTRA-004'), null, 'a ação de um achado não responde por outro');
});

// ---- (7) Proposta semeada: orienta confirmar antes de desativar ---------------------------------

test('a proposta de convidados manda CONFIRMAR a necessidade antes de desativar', () => {
  const texto = seededProposal('AK-ENTRA-004', 3);
  contains(texto, 'Confirmar com a área responsável', 'a primeira providência é confirmar, não desativar');
  contains(texto, 'não comprova desuso', 'ausência de registro não é inatividade comprovada');
  const posConfirmar = texto.indexOf('Confirmar');
  const posDesativar = texto.indexOf('Desativar');
  ok(posConfirmar >= 0 && posConfirmar < posDesativar, 'confirmar precisa vir ANTES de desativar no texto');
});

test('a proposta de MFA lembra que registro não comprova imposição', () => {
  contains(
    seededProposal('AK-ENTRA-001', 2),
    'não comprova a imposição',
    'registrar método e exigir método são coisas diferentes',
  );
});

test('achado sem redação própria não recebe proposta inventada', () => {
  eq(seededProposal('AK-ENTRA-999', 5), '', 'melhor um campo vazio para o humano do que uma frase fabricada');
});

console.log(`\n${count - failures}/${count} testes passaram (remediation.models).`);
if (failures > 0) throw new Error(`${failures} teste(s) da remediação falharam`);
