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
  observedBefore: number | null;
  observedAfter: number | null;
  objectsNoLongerPresent: number | null;
  /** `true` quando a conclusão se apoia nos CONJUNTOS preservados, não apenas em totais. */
  comparedBySets: boolean;
  rationale: string;
  decidedAt: string;
  decidedByName: string;
}

export interface ActionPlan {
  id: string;
  knightIndicatorId: string | null;
  /** Avaliação que ORIGINOU a ação. */
  originRunId: string | null;
  originAffectedCount: number | null;
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
  /** Versão lida — devolvida na próxima escrita para detectar atualização conflitante. */
  version: number;
  latestValidation: ActionPlanValidation | null;
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
    (v.outcome === 'ExposureCleared' || v.outcome === 'ReductionObserved')
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
  const v = p.latestValidation;
  if (!v) {
    return p.status === 'Concluido'
      ? 'Encerrada sem validação registrada — a correção não foi comprovada pelo AEGIS.'
      : 'Ainda não validado.';
  }
  const quantidade =
    v.observedBefore !== null && v.observedAfter !== null
      ? ` (${v.observedBefore} → ${v.observedAfter} afetado(s))`
      : '';
  return `${outcomeLabel(v.outcome)}${quantidade}`;
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

/** A ação ATIVA de um achado, se houver — é ela que decide entre "Criar plano" e "Abrir plano". */
export function activePlanFor(plans: ActionPlan[], indicatorId: string): ActionPlan | null {
  return plans.find((p) => p.isActive && p.knightIndicatorId === indicatorId) ?? null;
}

/** Etapas para as quais a ação pode avançar/voltar a partir da atual (espelha a regra do servidor). */
export function allowedTransitions(from: ActionPlanStatus): ActionPlanStatus[] {
  switch (from) {
    case 'Aberto':
      return ['EmAndamento', 'AguardandoValidacao'];
    case 'EmAndamento':
      return ['Aberto', 'AguardandoValidacao'];
    case 'AguardandoValidacao':
      return ['EmAndamento', 'Concluido'];
    case 'Concluido':
      return ['EmAndamento'];
    case 'Vencido':
      return ['Aberto', 'EmAndamento'];
  }
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
