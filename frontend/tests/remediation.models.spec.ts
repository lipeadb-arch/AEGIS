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
 *   (5) "existe ação ativa?" tem uma resposta só — e ela inclui a PROCEDÊNCIA, porque o mesmo identificador
 *       de achado na demonstração e numa coleta real são dois problemas distintos;
 *   (6) uma comprovação de um ciclo anterior, ou apoiada numa coleta anterior ao trabalho relatado, não
 *       responde pelo ciclo atual — fica no histórico, identificada, sem autorizar nada.
 *
 * O GRAFO de transições não é mais espelhado aqui: ele vem pronto do servidor em `allowedTransitions`, e
 * reimplementá-lo no cliente é justamente o que faria a tela oferecer um botão que a gravação recusaria.
 *
 * Não há runner Angular (karma/jest) neste projeto — apenas `ng build`. Compilado por `tsc` (CommonJS) e
 * executado por `node`, no mesmo padrão de knight-affected.models.spec.ts.
 */
import {
  ActionPlan,
  ActionPlanValidation,
  PinnedPlanState,
  actionResult,
  actionSituation,
  actionStatusLabel,
  activePlanFor,
  canManageActionPlans,
  isTechnicallyProven,
  originLabel,
  outcomeLabel,
  panelAllowsCreation,
  pinnedPlanRejection,
  planForPanel,
  seededProposal,
  validationBasis,
  validationScope,
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
    evidenceCollectedAt: '2026-09-07T11:00:00Z',
    precedesReportedExecution: false,
    appliesToCurrentCycle: true,
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
    originSourceType: 'MicrosoftEntraId',
    originMode: 'Live',
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
    cycleStartedAt: '2026-09-01T10:00:00Z',
    version: 3,
    latestValidation: null,
    applicableValidation: null,
    allowedTransitions: ['Aberto', 'AguardandoValidacao'],
    closureBlockedReason: null,
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
  const v = validation();
  const p = plan({ latestValidation: v, applicableValidation: v });
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
  const humana = validation({
    method: 'HumanEvidence',
    outcome: 'HumanAttested',
    comparedBySets: false,
    evidenceCollectedAt: null,
  });
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

test('a etapa legada "Vencido" é identificada como legado na apresentação', () => {
  contains(actionStatusLabel('Vencido'), 'legado', 'quem vê precisa saber que aquilo não é do fluxo novo');
});

// ---- (5) Comprovação de OUTRO ciclo não responde pelo ciclo atual -------------------------------

test('validação de um ciclo ANTERIOR não é comprovação do trabalho em curso', () => {
  const velha = validation({ outcome: 'ExposureCleared', appliesToCurrentCycle: false });
  ok(
    !isTechnicallyProven(velha),
    'a ação foi reaberta porque o problema voltou — a coleta que fechou o ciclo passado não fala por este',
  );
  contains(validationScope(velha), 'Ciclo anterior', 'a linha do histórico precisa dizer isso por si mesma');
  contains(validationScope(velha), 'não sustenta o encerramento', 'e dizer que não autoriza nada');
});

test('o resultado exibido é o do CICLO ATUAL, e uma validação vencida é qualificada como tal', () => {
  const velha = validation({ outcome: 'ExposureCleared', appliesToCurrentCycle: false });
  const reaberta = plan({
    status: 'EmAndamento',
    cycleStartedAt: '2026-09-05T09:00:00Z',
    latestValidation: velha,
    applicableValidation: null,
    validations: [velha],
  });
  const r = actionResult(reaberta);
  contains(r, 'ciclo anterior', 'sem essa ressalva a tela reciclaria a prova de um ciclo encerrado');
  contains(r, 'não responde pelo trabalho em curso', 'é exatamente o que a pessoa precisa saber');
});

test('a validação APLICÁVEL tem precedência sobre a mais recente', () => {
  const aplicavel = validation({ outcome: 'NoChangeObserved', observedBefore: 12, observedAfter: 12 });
  const velha = validation({ outcome: 'ExposureCleared', appliesToCurrentCycle: false });
  const p = plan({ latestValidation: velha, applicableValidation: aplicavel, validations: [velha, aplicavel] });
  contains(actionResult(p), 'Sem melhora observada', 'o ciclo atual é que responde pela ação');
  notContains(actionResult(p), 'Exposição encerrada', 'a prova do ciclo passado não pode reaparecer como atual');
});

// ---- (6) Correlação no tempo não é causalidade ---------------------------------------------------

test('coleta ANTERIOR ao relato de execução não é atribuída a esta ação', () => {
  const antes = validation({ outcome: 'ExposureCleared', precedesReportedExecution: true });
  ok(!isTechnicallyProven(antes), 'a melhora é real, mas não foi este trabalho que a produziu');
  const p = plan({ latestValidation: antes, applicableValidation: antes });
  contains(actionResult(p), 'coleta anterior ao relato de execução', 'a ressalva viaja junto do resultado');
  contains(actionResult(p), 'não atribuível a esta ação', 'a causa não é inventada a partir da coincidência');
  contains(validationScope(antes), 'continua valendo', 'o que foi observado não é apagado');
  contains(validationScope(antes), 'não é atribuível', 'só a atribuição é recusada');
});

test('uma validação do ciclo atual, posterior à execução, é dita como vigente', () => {
  contains(validationScope(validation()), 'Vale para o ciclo atual', 'o caso normal também precisa ser dito');
});

// ---- (7) A PROCEDÊNCIA faz parte da identidade do problema ---------------------------------------

test('demonstração e coleta real com o MESMO indicador são ações distintas', () => {
  const demo = plan({
    id: 'demo',
    knightIndicatorId: 'AK-ENTRA-002',
    originSourceType: 'Demo',
    originMode: 'Demo',
  });
  const real = plan({
    id: 'real',
    knightIndicatorId: 'AK-ENTRA-002',
    originSourceType: 'MicrosoftEntraId',
    originMode: 'Live',
  });
  const fila = [demo, real];

  eq(
    activePlanFor(fila, 'AK-ENTRA-002', 'MicrosoftEntraId', 'Live')?.id,
    'real',
    'o achado real é respondido pela ação real',
  );
  eq(
    activePlanFor(fila, 'AK-ENTRA-002', 'Demo', 'Demo')?.id,
    'demo',
    'e a de treinamento continua respondendo pelo cenário de demonstração',
  );
  eq(
    activePlanFor([demo], 'AK-ENTRA-002', 'MicrosoftEntraId', 'Live'),
    null,
    'uma ação de demonstração NÃO pode bloquear a criação da ação real',
  );
});

test('a ação ATIVA do achado é a que decide entre criar e abrir', () => {
  const ativa = plan({ id: 'ativa', knightIndicatorId: 'AK-ENTRA-002', isActive: true });
  const encerrada = plan({ id: 'velha', knightIndicatorId: 'AK-ENTRA-002', isActive: false, status: 'Concluido' });
  eq(
    activePlanFor([encerrada, ativa], 'AK-ENTRA-002', 'MicrosoftEntraId', 'Live')?.id,
    'ativa',
    'a encerrada não bloqueia nem responde',
  );
  eq(
    activePlanFor([encerrada], 'AK-ENTRA-002', 'MicrosoftEntraId', 'Live'),
    null,
    'ação encerrada LIBERA a origem para um novo ciclo',
  );
  eq(
    activePlanFor([ativa], 'AK-ENTRA-004', 'MicrosoftEntraId', 'Live'),
    null,
    'a ação de um achado não responde por outro',
  );
});

test('a procedência é dita em palavras, não deduzida de um identificador', () => {
  contains(
    originLabel(plan({ originSourceType: 'Demo', originMode: 'Demo' })),
    'demonstração',
    'quem olha precisa saber que aquilo é treino',
  );
  contains(
    originLabel(plan({ originSourceType: 'MicrosoftEntraId', originMode: 'Live' })),
    'coleta real',
    'e que aquilo é trabalho sobre o cliente',
  );
  contains(
    originLabel(plan({ originSourceType: null, originMode: null })),
    'não registrada',
    'ausência de procedência é dita, não preenchida com um palpite',
  );
});

// ---- (8) Controles de escrita só para os papéis autorizados -------------------------------------

test('só Manager e TenantAdmin recebem os controles de escrita', () => {
  ok(canManageActionPlans('Manager'), 'Manager altera planos de ação');
  ok(canManageActionPlans('TenantAdmin'), 'TenantAdmin também');
  ok(!canManageActionPlans('Analyst'), 'Analyst lê a evidência, mas não movimenta a ação');
  ok(!canManageActionPlans('Viewer'), 'Viewer, muito menos');
  ok(!canManageActionPlans(null), 'sem papel resolvido, nenhum controle de escrita é oferecido');
});

// ---- (9) Link de PLANO: a ação indicada não é substituída por outra ----------------------------
//
// O defeito que estes testes fecham não aparece em nenhum print: entre pedir uma ação pelo endereço e
// recebê-la, o painel exibia a ação ATIVA do achado — e a exibia de novo quando a pedida não podia ser
// aberta. Quem seguiu o link para conferir um encerramento via outro trabalho, com a mesma aparência de
// resposta. Um aviso ao lado não desfaz isso: o que impede a substituição é o painel ficar VAZIO.

test('enquanto a ação indicada carrega, nenhuma outra ocupa o painel', () => {
  const ativa = plan({ id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' });
  const estado: PinnedPlanState = { kind: 'carregando', id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' };
  eq(planForPanel(estado, ativa), null, 'a ação ativa não preenche a espera pela ação pedida');
  ok(!panelAllowsCreation(estado), 'e o vazio da espera não é convite para criar uma segunda ação');
});

test('ação indicada e indisponível deixa o painel vazio, nunca substituído', () => {
  const ativa = plan({ id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' });
  const estado: PinnedPlanState = {
    kind: 'indisponivel',
    id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
    reason: 'não pôde ser aberta',
  };
  eq(planForPanel(estado, ativa), null, 'existir outra ação ativa não torna o erro invisível');
  ok(!panelAllowsCreation(estado), 'e muito menos vira uma tela de criação');
});

test('sem ação no endereço, a ação ativa ocupa o painel e a criação é oferecida', () => {
  const ativa = plan();
  eq(planForPanel({ kind: 'livre' }, ativa), ativa, 'navegação comum continua abrindo a ação em curso');
  eq(planForPanel({ kind: 'livre' }, null), null, 'sem ação ativa, o achado ainda pode receber uma');
  ok(panelAllowsCreation({ kind: 'livre' }), 'é aqui — e só aqui — que criar faz sentido');
});

test('a ação carregada ocupa o painel mesmo estando encerrada', () => {
  const encerrada = plan({ id: 'cccccccc-cccc-cccc-cccc-cccccccccccc', status: 'Concluido', isActive: false });
  const ativa = plan({ id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb' });
  eq(
    planForPanel({ kind: 'carregada', id: encerrada.id, plan: encerrada }, ativa),
    encerrada,
    'quem veio conferir um encerramento vê o encerramento, não o ciclo que começou depois',
  );
});

test('uma ação de OUTRO achado é recusada, não exibida sob o cabeçalho errado', () => {
  const outra = plan({ knightIndicatorId: 'AK-ENTRA-004' });
  const motivo = pinnedPlanRejection(outra, 'AK-ENTRA-001', 'MicrosoftEntraId', 'Live');
  ok(!!motivo, 'a ação de um problema não aparece ao lado do veredito de outro');
  contains(motivo!, 'AK-ENTRA-004', 'e o motivo diz a qual achado ela pertence');
});

test('uma ação de OUTRA procedência é recusada mesmo com o indicador igual', () => {
  const demo = plan({ originSourceType: 'Demo', originMode: 'Demo' });
  const motivo = pinnedPlanRejection(demo, 'AK-ENTRA-001', 'MicrosoftEntraId', 'Live');
  ok(!!motivo, 'ação de demonstração não responde por um achado de coleta real');
  contains(motivo!, 'demonstração', 'e o motivo diz de onde ela veio');
  eq(
    pinnedPlanRejection(demo, 'AK-ENTRA-001', 'Demo', 'Demo'),
    null,
    'sob a procedência dela, a mesma ação é legítima',
  );
});

test('ação sem procedência registrada não é adotada por uma coleta real', () => {
  const legada = plan({ originSourceType: null, originMode: null });
  ok(
    !!pinnedPlanRejection(legada, 'AK-ENTRA-001', 'MicrosoftEntraId', 'Live'),
    'ausência de origem não autoriza assumir a origem da avaliação aberta',
  );
});

// ---- (10) Proposta semeada: orienta confirmar antes de desativar --------------------------------

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
