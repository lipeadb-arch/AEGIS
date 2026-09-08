/**
 * [AEGIS-MVP-PRODUCT-03] Contratos da jornada de remediação de um achado do AEGIS KNIGHT — espelham
 * `/api/v1/remediation/action-plans` (camelCase, enums como NOME).
 *
 * Três coisas que estas funções puras existem para a tela nunca confundir:
 *
 *   1. ETAPA ≠ RESULTADO. "Concluída" é uma decisão de gestão sobre o trabalho; o que aconteceu com o achado
 *      está na validação. Uma ação pode estar concluída sem que a exposição tenha sido comprovadamente
 *      corrigida — e a tela precisa dizer as duas coisas, separadas.
 *   2. RELATO ≠ PROVA. "Marcar como executado" registra o que a pessoa diz ter feito.
 *   3. ATESTAÇÃO ≠ COMPROVAÇÃO. Uma validação humana com evidência referenciada é registro legítimo, mas
 *      nunca é apresentada como correção técnica comprovada.
 */

/** Etapa OPERACIONAL. `Vencido` é legado: atraso hoje vem do prazo (`isOverdue`), não de uma etapa. */
export type ActionPlanStatus = 'Aberto' | 'EmAndamento' | 'AguardandoValidacao' | 'Concluido' | 'Vencido';

/** Como a validação foi feita — muda radicalmente o peso da afirmação. */
export type ActionPlanValidationMethod = 'NewAssessment' | 'HumanEvidence';

/** O desfecho observado no achado. Nenhum deles diz "resolvido" sem qualificação. */
export type ActionPlanValidationOutcome =
  | 'ExposureCleared'
  | 'ReductionObserved'
  | 'NoChangeObserved'
  | 'EvidenceInsufficient'
  | 'HumanAttested';

export type ActionPlanEventKind =
  | 'Created'
  | 'StatusChanged'
  | 'ExecutionRecorded'
  | 'ValidationRecorded'
  | 'Edited';

export interface ActionPlanEvent {
  kind: ActionPlanEventKind;
  at: string;
  actorName: string;
  fromStatus: ActionPlanStatus | null;
  toStatus: ActionPlanStatus | null;
  note: string | null;
}

export interface ActionPlanValidation {
  method: ActionPlanValidationMethod;
  outcome: ActionPlanValidationOutcome;
  /** Avaliação usada como EVIDÊNCIA — distinta da avaliação de ORIGEM do plano. */
  validationRunId: string | null;
  evidenceReference: string | null;
  /** Instante da COLETA usada como evidência — nulo na atestação humana. */
  evidenceCollectedAt: string | null;
  /** A coleta antecede o relato de execução: a mudança observada não é atribuível a esta ação. */
  precedesReportedExecution: boolean;
  /** Esta validação fala pelo CICLO ATUAL — só ela pode sustentar o encerramento. */
  appliesToCurrentCycle: boolean;
  observedBefore: number | null;
  observedAfter: number | null;
  objectsNoLongerPresent: number | null;
  /** `true` quando a conclusão se apoia nos CONJUNTOS preservados, não apenas em totais. */
  comparedBySets: boolean;
  rationale: string;
  decidedAt: string;
  decidedByName: string;
}

/** Fonte concreta da coleta — o mesmo eixo que o KNIGHT usa. */
export type KnightOriginSource = 'Demo' | 'MicrosoftEntraId' | 'GoogleWorkspace';

/** Demonstração ou coleta real. */
export type KnightOriginMode = 'Demo' | 'Live';

export interface ActionPlan {
  id: string;
  knightIndicatorId: string | null;
  /** Avaliação que ORIGINOU a ação. */
  originRunId: string | null;
  originAffectedCount: number | null;
  /** Fonte da avaliação de origem — parte da IDENTIDADE do problema, junto com o indicador. */
  originSourceType: KnightOriginSource | null;
  /** Demo ou coleta real: uma ação de demonstração jamais responde por um achado real. */
  originMode: KnightOriginMode | null;
  title: string;
  proposedAction: string | null;
  responsiblePerson: string | null;
  responsibleArea: string | null;
  dueDate: string | null; // "yyyy-MM-dd"
  status: ActionPlanStatus;
  isOverdue: boolean;
  isActive: boolean;
  nextStep: string;
  executionNotes: string | null;
  executionEvidenceRef: string | null;
  executedAt: string | null;
  completedAt: string | null;
  createdAt: string;
  /** Início do ciclo vigente — repactuado quando uma ação encerrada é reaberta. */
  cycleStartedAt: string;
  /** Versão lida — devolvida na próxima escrita para detectar atualização conflitante. */
  version: number;
  latestValidation: ActionPlanValidation | null;
  /**
   * A validação que fala pelo ciclo ATUAL. Numa ação reaberta ela é DIFERENTE de `latestValidation`, e é
   * essa diferença que impede a tela de reciclar uma comprovação de um ciclo já encerrado.
   */
  applicableValidation: ActionPlanValidation | null;
  /**
   * Etapas alcançáveis daqui, decididas pelo SERVIDOR a partir do que o ciclo tem registrado. A tela oferece
   * exatamente estas: espelhar a regra no cliente faria os dois divergirem, e a pessoa veria um botão que a
   * gravação depois recusaria.
   */
  allowedTransitions: ActionPlanStatus[];
  /** Por que encerrar ainda não está disponível — nulo quando está. */
  closureBlockedReason: string | null;
  validations: ActionPlanValidation[];
  events: ActionPlanEvent[];
}

export interface CreateActionPlanRequest {
  runId: string;
  indicatorId: string;
  title: string;
  proposedAction: string | null;
  responsiblePerson: string | null;
  responsibleArea: string | null;
  dueDate: string | null;
}

export interface UpdateActionPlanRequest {
  expectedVersion: number;
  title?: string | null;
  proposedAction?: string | null;
  responsiblePerson?: string | null;
  responsibleArea?: string | null;
  dueDate?: string | null;
  status?: ActionPlanStatus | null;
}

export interface RecordExecutionRequest {
  expectedVersion: number;
  notes: string;
  evidenceReference: string | null;
}

export interface ValidateActionPlanRequest {
  expectedVersion: number;
  /** Com avaliação, o SERVIDOR decide o desfecho. Sem ela, exige-se `evidenceReference`. */
  validationRunId: string | null;
  evidenceReference: string | null;
  note: string | null;
}

// ---- Apresentação (funções PURAS, testáveis sem Angular) ---------------------------------------

const STATUS_LABEL: Record<ActionPlanStatus, string> = {
  Aberto: 'Aberta',
  EmAndamento: 'Em andamento',
  AguardandoValidacao: 'Aguardando validação',
  Concluido: 'Concluída',
  Vencido: 'Vencida (legado)',
};

export function actionStatusLabel(status: ActionPlanStatus): string {
  return STATUS_LABEL[status] ?? status;
}

const OUTCOME_LABEL: Record<ActionPlanValidationOutcome, string> = {
  ExposureCleared: 'Exposição encerrada na nova avaliação',
  ReductionObserved: 'Redução observada (achado ainda exposto)',
  NoChangeObserved: 'Sem melhora observada',
  EvidenceInsufficient: 'Evidência insuficiente para comprovar',
  HumanAttested: 'Atestação humana (não é comprovação técnica)',
};

export function outcomeLabel(outcome: ActionPlanValidationOutcome): string {
  return OUTCOME_LABEL[outcome] ?? outcome;
}

const METHOD_LABEL: Record<ActionPlanValidationMethod, string> = {
  NewAssessment: 'Comparação com nova avaliação',
  HumanEvidence: 'Atestação humana com evidência referenciada',
};

export function methodLabel(method: ActionPlanValidationMethod): string {
  return METHOD_LABEL[method] ?? method;
}

const EVENT_LABEL: Record<ActionPlanEventKind, string> = {
  Created: 'Ação criada',
  StatusChanged: 'Etapa alterada',
  ExecutionRecorded: 'Execução relatada',
  ValidationRecorded: 'Validação registrada',
  Edited: 'Campos editados',
};

export function eventLabel(kind: ActionPlanEventKind): string {
  return EVENT_LABEL[kind] ?? kind;
}

/**
 * `true` SOMENTE quando a melhora foi COMPROVADA por uma nova coleta compatível. Atestação humana, aceite de
 * risco e encerramento administrativo ficam de fora — é esta função que impede a tela de exibir um selo de
 * "comprovado" sobre um comentário.
 */
export function isTechnicallyProven(v: ActionPlanValidation | null): boolean {
  return (
    !!v &&
    v.method === 'NewAssessment' &&
    (v.outcome === 'ExposureCleared' || v.outcome === 'ReductionObserved') &&
    // A comprovação precisa falar por ESTE ciclo: numa ação reaberta, a coleta que fechou o ciclo anterior
    // continua verdadeira e continua no histórico, mas não comprova o trabalho que está em curso agora.
    v.appliesToCurrentCycle &&
    // E precisa ser POSTERIOR ao trabalho relatado: uma coleta anterior pode ter observado a melhora, mas
    // atribuí-la a esta ação seria inventar a causa a partir da coincidência no tempo.
    !v.precedesReportedExecution
  );
}

/**
 * A SITUAÇÃO da ação em uma linha, com o atraso dito JUNTO da etapa — nunca no lugar dela. Saber que está
 * atrasada sem saber onde o trabalho parou não ajuda ninguém a destravá-lo.
 */
export function actionSituation(p: ActionPlan): string {
  const base = actionStatusLabel(p.status);
  return p.isOverdue ? `${base} · em atraso` : base;
}

/**
 * O que a tela pode afirmar sobre o RESULTADO no achado — separado da etapa do plano. Sem validação, a
 * resposta é explicitamente "ainda não comprovado", jamais silêncio (que se lê como "está tudo bem").
 */
export function actionResult(p: ActionPlan): string {
  // A validação do CICLO ATUAL é a que responde pelo trabalho em curso. Cair na mais recente sem dizer que
  // ela é de outro ciclo faria uma ação reaberta exibir a comprovação que encerrou o ciclo ANTERIOR como se
  // fosse resultado do esforço de agora.
  const v = p.applicableValidation ?? p.latestValidation;
  if (!v) {
    return p.status === 'Concluido'
      ? 'Encerrada sem validação registrada — a correção não foi comprovada pelo AEGIS.'
      : 'Ainda não validado.';
  }
  const quantidade =
    v.observedBefore !== null && v.observedAfter !== null
      ? ` (${v.observedBefore} → ${v.observedAfter} afetado(s))`
      : '';
  // A ressalva de CAUSALIDADE viaja junto do resultado: uma coleta anterior ao trabalho relatado pode ter
  // observado a melhora, mas não a produziu — e apresentar as duas coisas juntas seria inventar a causa.
  const causal = v.precedesReportedExecution
    ? ' — coleta anterior ao relato de execução: não atribuível a esta ação'
    : '';
  const ciclo = v.appliesToCurrentCycle
    ? ''
    : ' — validação de um ciclo anterior desta ação: não responde pelo trabalho em curso';
  return `${outcomeLabel(v.outcome)}${quantidade}${causal}${ciclo}`;
}

/**
 * O ALCANCE de uma validação registrada. O histórico preserva tudo o que foi decidido — inclusive o que já
 * não autoriza nada — e cada linha precisa dizer por si mesma até onde vale, senão a lista inteira se lê
 * como um conjunto de comprovações vigentes.
 */
export function validationScope(v: ActionPlanValidation): string {
  if (!v.appliesToCurrentCycle) {
    return 'Ciclo anterior desta ação — permanece no histórico, mas não sustenta o encerramento do ciclo atual.';
  }
  if (v.precedesReportedExecution) {
    return 'A coleta usada é ANTERIOR ao relato de execução — o que ela observou continua valendo, mas não é ' +
      'atribuível a este trabalho.';
  }
  return 'Vale para o ciclo atual desta ação.';
}

/**
 * A base da conclusão. Só a comparação dos CONJUNTOS preservados sustenta "estes objetos foram corrigidos";
 * fora disso, o que se observou foi variação de quantidade — e a tela diz exatamente isso.
 */
export function validationBasis(v: ActionPlanValidation): string {
  if (v.method === 'HumanEvidence') {
    return 'Registro humano com evidência referenciada. O AEGIS não verificou o ambiente para este registro.';
  }
  return v.comparedBySets
    ? 'Comparação dos conjuntos preservados nas duas coletas.'
    : 'Comparação de quantidade — os conjuntos não estavam preservados nos dois lados, então não é possível ' +
        'afirmar quais objetos foram corrigidos.';
}

/**
 * A ação ATIVA de um achado, se houver — é ela que decide entre "Criar plano" e "Abrir plano".
 *
 * A PROCEDÊNCIA entra na busca porque o indicador sozinho não identifica o problema: "AK-ENTRA-001 no
 * cenário de demonstração" e "AK-ENTRA-001 na coleta real do diretório" são dois problemas distintos.
 * Ignorá-la faria uma ação de treinamento responder por um achado real — e bloquear a criação da ação real.
 */
export function activePlanFor(
  plans: ActionPlan[],
  indicatorId: string,
  sourceType: KnightOriginSource | null,
  mode: KnightOriginMode | null,
): ActionPlan | null {
  return (
    plans.find(
      (p) =>
        p.isActive &&
        p.knightIndicatorId === indicatorId &&
        p.originSourceType === sourceType &&
        p.originMode === mode,
    ) ?? null
  );
}

/**
 * [AEGIS-MVP-PRODUCT-03] O ESTADO do painel de ação quando o endereço identifica um plano (`?plan=<id>`).
 *
 * Ele existe porque `ActionPlan | null` não consegue dizer a verdade neste ponto: `null` já significa "não
 * há ação para este achado — crie uma". Enquanto a ação indicada está sendo lida, ou quando ela não pôde ser
 * aberta, o painel NÃO está livre para propor a criação de outra nem para exibir a ação ativa: quem seguiu o
 * link veio conferir AQUELA ação, e substituí-la por outra com a mesma aparência de resposta é o defeito que
 * este tipo existe para tornar impossível.
 *
 *   • `livre`        — o endereço não nomeia ação alguma; a ação ATIVA do achado pode ocupar o painel;
 *   • `carregando`   — o endereço nomeia uma ação e ela ainda está sendo lida; nada ocupa o painel;
 *   • `carregada`    — a ação nomeada foi lida E confere com o contexto aberto; é ela, e só ela;
 *   • `indisponivel` — a ação nomeada não pôde ser aberta (inexistente, de outro achado ou de outra
 *     procedência); o painel diz isso e oferece uma saída EXPLÍCITA, sem trocar o contexto sozinho.
 */
export type PinnedPlanState =
  | { kind: 'livre' }
  | { kind: 'carregando'; id: string }
  | { kind: 'carregada'; id: string; plan: ActionPlan }
  | { kind: 'indisponivel'; id: string; reason: string };

/**
 * Por que a ação lida pelo endereço NÃO pode ocupar o painel aberto — `null` quando pode.
 *
 * O tenant já é garantido pelo servidor; o que ele não decide é se aquela ação pertence ao que está na tela.
 * Duas recusas, pelo mesmo motivo de fundo — a ação apareceria sob um cabeçalho que não é o dela:
 *
 *   • outro ACHADO: a ação de um problema ao lado do veredito de outro;
 *   • outra PROCEDÊNCIA: uma ação nascida do cenário de demonstração exibida sob uma coleta real (ou o
 *     inverso). O indicador coincide; o problema, não.
 */
export function pinnedPlanRejection(
  plan: ActionPlan,
  indicatorId: string | null,
  sourceType: KnightOriginSource | null,
  mode: KnightOriginMode | null,
): string | null {
  if (plan.knightIndicatorId !== indicatorId) {
    return (
      `A ação indicada no endereço pertence ao achado ${plan.knightIndicatorId ?? 'não identificado'}, ` +
      'não ao achado aberto.'
    );
  }
  if (plan.originSourceType !== sourceType || plan.originMode !== mode) {
    return (
      `A ação indicada no endereço nasceu de outra procedência (${originLabel(plan)}) e não responde pelo ` +
      'achado desta avaliação.'
    );
  }
  return null;
}

/**
 * QUEM ocupa o painel de ação, dado o estado do endereço e a ação ativa do achado.
 *
 * É a autoridade única sobre a substituição — e ela responde `null` nos dois estados intermediários de
 * propósito: enquanto a ação indicada carrega, e quando ela não pôde ser aberta, NADA a substitui. A ação
 * ativa só ocupa o painel quando o endereço não nomeia ação alguma; a partir daí, quem troca de contexto é
 * a pessoa, explicitamente.
 */
export function planForPanel(state: PinnedPlanState, active: ActionPlan | null): ActionPlan | null {
  if (state.kind === 'carregada') return state.plan;
  if (state.kind === 'livre') return active;
  return null;
}

/**
 * O painel pode oferecer a CRIAÇÃO de uma ação? Só na navegação livre. Um endereço que aponta para uma ação
 * existente não é convite para abrir um segundo ciclo — e, enquanto ela não abre, o vazio no painel não
 * significa "não há ação para este achado".
 */
export function panelAllowsCreation(state: PinnedPlanState): boolean {
  return state.kind === 'livre';
}

/** Procedência da ação em uma linha — o que distingue um treino de trabalho real sobre o cliente. */
export function originLabel(p: ActionPlan): string {
  if (!p.originSourceType) return 'Origem não registrada';
  const fonte =
    p.originSourceType === 'MicrosoftEntraId'
      ? 'Microsoft Entra ID'
      : p.originSourceType === 'GoogleWorkspace'
        ? 'Google Workspace'
        : 'Provedor de demonstração';
  return p.originMode === 'Demo' ? `${fonte} · cenário de demonstração` : `${fonte} · coleta real`;
}

/**
 * A pessoa pode ALTERAR planos de ação? Espelha `[Authorize(Roles = "Manager,TenantAdmin")]` das mutações.
 * É gate de APRESENTAÇÃO apenas: esconder um botão não protege nada, e o servidor continua sendo a
 * autoridade — mas oferecer um controle que responderá 403 é enganar quem clica.
 */
export function canManageActionPlans(role: string | null): boolean {
  return role === 'Manager' || role === 'TenantAdmin';
}

/**
 * Texto inicial da ação proposta, semeado com o CONTEXTO do achado para o humano editar. É uma sugestão
 * determinística — não uma decisão: quem escreve a ação é a pessoa responsável.
 */
export function seededProposal(indicatorId: string, affectedCount: number): string {
  switch (indicatorId) {
    case 'AK-ENTRA-001':
      return (
        `Registrar um método resistente a phishing para as ${affectedCount} conta(s) administrativa(s) ` +
        'listadas e, em seguida, confirmar na política de acesso condicional que o segundo fator é exigido ' +
        'de fato (o registro sozinho não comprova a imposição).'
      );
    case 'AK-ENTRA-002':
      return (
        `Conduzir revisão de acesso dos ${affectedCount} objeto(s) com papel administrativo, separando ` +
        'pessoas, aplicações e grupos, e remover apenas os papéis que a área responsável confirmar como ' +
        'desnecessários.'
      );
    case 'AK-ENTRA-004':
      return (
        `Confirmar com a área responsável, para cada um dos ${affectedCount} convidado(s) listado(s), se o ` +
        'acesso ainda é necessário. Desativar somente os acessos confirmados como dispensáveis — ausência de ' +
        'registro de acesso não comprova desuso.'
      );
    default:
      return '';
  }
}
