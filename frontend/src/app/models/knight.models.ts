// Modelos da tela AEGIS KNIGHT — espelham o contrato de /api/v1/knight/assessments (camelCase). Enums viajam
// como NOME (nunca ordinal). Funções PURAS de apresentação (testáveis, sem Angular). O score aqui é o score
// KNIGHT (fórmula própria), ANULÁVEL e DISTINTO do AEGIS Score geral.

import { SeverityLevel } from './scoring.models';

export type { SeverityLevel };

export type KnightMode = 'Demo' | 'Live';
export type KnightRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed';

/** Veredito determinístico de um indicador. Falta de dado é NotEvaluated (reduz cobertura), nunca aprovação. */
export type KnightIndicatorStatus =
  | 'Passed'
  | 'Exposed'
  | 'Mitigated'
  | 'NotEvaluated'
  | 'Error'
  | 'NotApplicable';

export type KnightCategory =
  | 'PrivilegedAccess'
  | 'IdentityGovernance'
  | 'AccountHygiene'
  | 'GuestAccess'
  | 'ServiceAccounts';

/** Estado de conexão exibido no badge: separação inequívoca entre Demo, Não configurado e Conectado. */
export type KnightConnectionState = 'Demo' | 'NotConfigured' | 'Connected';

/** Fonte concreta de coleta (multicoletor). */
export type KnightSourceType = 'Demo' | 'MicrosoftEntraId' | 'GoogleWorkspace';

/** Estado da coleta/fonte de uma execução (ou de disponibilidade). */
export type KnightSourceState =
  | 'NotConfigured'
  | 'Configured'
  | 'Collecting'
  | 'Completed'
  | 'PartialCollection'
  | 'InsufficientPermission'
  | 'AuthenticationFailure'
  | 'Throttled'
  | 'Unavailable'
  | 'Error';

/** Desfecho por capacidade da fonte (o que foi coletado e o que faltou). Falhas distintas não colapsam. */
export type KnightCapabilityOutcome =
  | 'Collected'
  | 'InsufficientPermission'
  | 'Unavailable'
  | 'NotAttempted'
  | 'Throttled'
  | 'AuthenticationFailure'
  | 'Error';

export interface KnightCapability {
  capability: string;
  outcome: KnightCapabilityOutcome;
  detail: string | null;
}

/** Disponibilidade de uma fonte para o tenant (espelha KnightSourceDto). */
export interface KnightSource {
  source: KnightSourceType;
  label: string;
  configured: boolean;
  enabled: boolean;
}

/** Estado das fontes: Demo sempre disponível; reais conforme configuração (espelha KnightSourcesDto). */
export interface KnightSources {
  demoAvailable: boolean;
  realSources: KnightSource[];
}

export interface KnightIndicator {
  indicatorId: string; // "AK-ENTRA-001"
  title: string;
  category: KnightCategory;
  severity: SeverityLevel;
  status: KnightIndicatorStatus;
  evidence: string;
  affectedObjectCount: number;
  nistCodes: string[];
  mitreTechniques: string[];
  recommendation: string;
  collectedAt: string; // ISO 8601
  sourceType: KnightSourceType;
  notEvaluatedReason: string | null;
  /**
   * [AEGIS-MVP-PRODUCT-02] `true` quando ESTA avaliação preservou os objetos que sustentam o veredito — é o
   * que autoriza a aba "Afetados". `false` numa avaliação anterior à preservação, que continua válida e NÃO
   * é retropreenchida com a coleta de hoje.
   */
  hasAffectedDetail: boolean;
  /** `true` quando a lista preservada cobre todo o conjunto que produziu a contagem. */
  affectedDetailComplete: boolean;
  /** O que a coleta não conseguiu enumerar no detalhe, quando aplicável. */
  affectedDetailLimitation: string | null;
}

export interface KnightCounts {
  passed: number;
  exposed: number;
  mitigated: number;
  notEvaluated: number;
  error: number;
  notApplicable: number;
}

export interface KnightPriorityRisk {
  title: string;
  rationale: string;
  indicatorIds: string[];
}

export interface KnightRecommendedAction {
  order: number;
  action: string;
  indicatorIds: string[];
}

export interface KnightCorrelation {
  description: string;
  indicatorIds: string[];
}

/** Interpretação/priorização ASSISTIDA POR IA (ou fallback determinístico). Nunca contém veredito/score. */
export interface KnightAdvisory {
  executiveSummary: string;
  priorityRisks: KnightPriorityRisk[];
  recommendedActions: KnightRecommendedAction[];
  correlations: KnightCorrelation[];
  collectionGaps: string[];
}

export interface KnightAssessment {
  id: string;
  mode: KnightMode;
  isDemo: boolean;
  sourceType: KnightSourceType;
  sourceState: KnightSourceState;
  source: string; // rótulo legível da fonte
  status: KnightRunStatus;
  catalogVersion: string; // "ak-knight-v1"
  scoreFormulaVersion: string; // "knight-score-v1"
  startedAt: string; // ISO 8601
  completedAt: string | null;
  score: number | null; // score KNIGHT — null = sem avaliação (nunca 0 por ausência)
  coverage: number; // 0..100
  counts: KnightCounts;
  indicators: KnightIndicator[];
  capabilities: KnightCapability[];
  advisory: KnightAdvisory | null;
  advisoryFromAi: boolean; // true = IA; false = fallback determinístico
}

/** Badge: sem assessment → NÃO CONFIGURADO; demo → DEMONSTRAÇÃO; real → CONECTADO. */
export function connectionStateOf(a: KnightAssessment | null): KnightConnectionState {
  if (!a) return 'NotConfigured';
  return a.isDemo ? 'Demo' : 'Connected';
}

export function connectionBadgeLabel(state: KnightConnectionState): string {
  switch (state) {
    case 'Demo':
      return 'DEMONSTRAÇÃO';
    case 'Connected':
      return 'CONECTADO';
    case 'NotConfigured':
      return 'NÃO CONFIGURADO';
  }
}

const STATUS_LABEL: Record<KnightIndicatorStatus, string> = {
  Passed: 'Conforme',
  Exposed: 'Exposto',
  Mitigated: 'Mitigado',
  NotEvaluated: 'Não avaliado',
  Error: 'Erro',
  NotApplicable: 'Não aplicável',
};

export function statusLabel(status: KnightIndicatorStatus): string {
  return STATUS_LABEL[status];
}

const SEVERITY_LABEL: Record<SeverityLevel, string> = {
  Critical: 'Crítica',
  High: 'Alta',
  Medium: 'Média',
  Low: 'Baixa',
  Informational: 'Informativa',
};

export function severityLabel(severity: SeverityLevel): string {
  return SEVERITY_LABEL[severity];
}

const CATEGORY_LABEL: Record<KnightCategory, string> = {
  PrivilegedAccess: 'Acesso privilegiado',
  IdentityGovernance: 'Governança de identidade',
  AccountHygiene: 'Higiene de contas',
  GuestAccess: 'Acesso de convidados',
  ServiceAccounts: 'Contas de serviço',
};

export function categoryLabel(category: KnightCategory): string {
  return CATEGORY_LABEL[category];
}

/** Ordem de exibição: exposto primeiro (salta aos olhos), depois mitigado, conforme e o restante; empate por ID. */
const STATUS_RANK: Record<KnightIndicatorStatus, number> = {
  Exposed: 0,
  Mitigated: 1,
  Passed: 2,
  NotEvaluated: 3,
  Error: 4,
  NotApplicable: 5,
};

export function sortIndicatorsByRisk(indicators: KnightIndicator[]): KnightIndicator[] {
  return [...indicators].sort(
    (a, b) => STATUS_RANK[a.status] - STATUS_RANK[b.status] || a.indicatorId.localeCompare(b.indicatorId),
  );
}

/** Score para exibição: "—" quando null (sem avaliação), nunca "0". */
export function scoreDisplay(score: number | null): string {
  return score === null ? '—' : String(Math.round(score));
}

const SOURCE_TYPE_LABEL: Record<KnightSourceType, string> = {
  Demo: 'Demonstração',
  MicrosoftEntraId: 'Microsoft Entra ID',
  GoogleWorkspace: 'Google Workspace',
};

export function sourceTypeLabel(source: KnightSourceType): string {
  return SOURCE_TYPE_LABEL[source];
}

const SOURCE_STATE_LABEL: Record<KnightSourceState, string> = {
  NotConfigured: 'Não configurado',
  Configured: 'Configurado',
  Collecting: 'Coletando',
  Completed: 'Coleta concluída',
  PartialCollection: 'Coleta parcial',
  InsufficientPermission: 'Permissão insuficiente',
  AuthenticationFailure: 'Falha de autenticação',
  Throttled: 'Throttling',
  Unavailable: 'Indisponível',
  Error: 'Erro',
};

export function sourceStateLabel(state: KnightSourceState): string {
  return SOURCE_STATE_LABEL[state];
}

/** Um estado de coleta que NÃO é a conclusão íntegra — a UI o destaca (permissão/parcial/falha). */
export function isProblemState(state: KnightSourceState): boolean {
  return state !== 'Completed';
}

const CAPABILITY_OUTCOME_LABEL: Record<KnightCapabilityOutcome, string> = {
  Collected: 'Coletado',
  InsufficientPermission: 'Permissão insuficiente',
  Unavailable: 'Indisponível',
  NotAttempted: 'Não tentado',
  Throttled: 'Throttling',
  AuthenticationFailure: 'Falha de autenticação',
  Error: 'Erro',
};

export function capabilityOutcomeLabel(outcome: KnightCapabilityOutcome): string {
  return CAPABILITY_OUTCOME_LABEL[outcome];
}

/**
 * [AEGIS-MVP-PRODUCT-01] Nome LEGÍVEL de uma capacidade de coleta. O cliente não deve ler o identificador
 * técnico do coletor ("MfaRegistration") numa tela de negócio. Autoridade ÚNICA: a Visão geral reexporta
 * esta função em vez de manter um segundo dicionário que sairia do ar com o primeiro.
 */
const CAPABILITY_LABEL: Record<string, string> = {
  PrivilegedRoleInventory: 'Contas com privilégio administrativo',
  MfaRegistration: 'Registro de múltiplo fator',
  GuestAccounts: 'Contas de convidado',
  ConditionalAccessPolicies: 'Políticas de acesso condicional',
  ApplicationInventory: 'Credenciais de aplicações',
  ServiceAccountExemptions: 'Exceções de contas de serviço',
  SecurityBaseline: 'Configuração de segurança padrão',
  BreakGlassDesignation: 'Contas de emergência',
  IdentityRiskDetections: 'Detecções de risco de identidade',
  RiskyUsers: 'Usuários sinalizados como de risco',
  AuthenticationMethods: 'Métodos de autenticação registrados',
};

/** Capacidade desconhecida degrada para o próprio identificador — nunca some da tela. */
export function capabilityLabel(capability: string): string {
  return CAPABILITY_LABEL[capability] ?? capability;
}

/** Capacidades com problema (não coletadas) — o que a UI mostra como limitação de cobertura. */
export function problemCapabilities(caps: KnightCapability[]): KnightCapability[] {
  return caps.filter((c) => c.outcome !== 'Collected');
}

// ============================================================================
//  [AEGIS-MVP-PRODUCT-02] Objetos AFETADOS de um achado
// ============================================================================
// A tela precisa levar o analista de "78 objetos privilegiados" a "quais são, e por que cada um entrou".
// Três coisas que estas funções existem para não deixar a UI quebrar:
//   1. lista ≠ acusação — o conjunto é material de REVISÃO, não uma ordem de remover pessoas;
//   2. lista ≠ censo de pessoas — um membro de papel privilegiado pode ser aplicação ou grupo;
//   3. nome ausente não se inventa — sem nome, mostra-se o identificador e declara-se a limitação.

/** Natureza do objeto afetado. Nunca presumir pessoa: aplicação e grupo têm ações diferentes. */
export type KnightAffectedObjectKind = 'User' | 'Guest' | 'ServicePrincipal' | 'Group' | 'Device' | 'Unknown';

/**
 * Estado do DETALHE de um achado numa avaliação:
 *  • `OutOfScope`   — este achado não preserva objetos nesta entrega;
 *  • `NotPreserved` — a avaliação é ANTERIOR à preservação (histórico válido, jamais retropreenchido);
 *  • `Available`    — detalhe preservado e completo (inclusive o conjunto VAZIO de um achado conforme);
 *  • `Partial`      — preservado, porém declaradamente incompleto.
 */
export type KnightAffectedDetailState = 'OutOfScope' | 'NotPreserved' | 'Available' | 'Partial';

export interface KnightAffectedObject {
  externalId: string;
  kind: KnightAffectedObjectKind;
  displayName: string | null;
  userPrincipalName: string | null;
  roles: string[];
  detail: string | null;
}

/** Página de afetados de UM achado de UMA avaliação (paginada e pesquisada no servidor). */
export interface KnightAffectedObjects {
  runId: string;
  indicatorId: string;
  state: KnightAffectedDetailState;
  /** Contagem do veredito — a MESMA unidade e regra de dedupe da lista. */
  affectedObjectCount: number;
  totalPreserved: number;
  /** Objetos que satisfazem a busca (igual a `totalPreserved` sem busca). */
  matchCount: number;
  page: number;
  pageSize: number;
  items: KnightAffectedObject[];
  limitation: string | null;
  collectedAt: string | null;
}

const AFFECTED_KIND_LABEL: Record<KnightAffectedObjectKind, string> = {
  User: 'Usuário',
  Guest: 'Convidado',
  ServicePrincipal: 'Aplicação',
  Group: 'Grupo',
  Device: 'Dispositivo',
  Unknown: 'Tipo não identificado',
};

export function affectedKindLabel(kind: KnightAffectedObjectKind): string {
  return AFFECTED_KIND_LABEL[kind] ?? 'Tipo não identificado';
}

/** `true` quando a fonte não devolveu nome nem UPN — a tela mostra o identificador e explica por quê. */
export function isUnnamed(o: KnightAffectedObject): boolean {
  return !o.displayName && !o.userPrincipalName;
}

/** Rótulo do objeto: nome, senão UPN, senão o identificador da fonte. NUNCA um nome inventado. */
export function affectedLabel(o: KnightAffectedObject): string {
  return o.displayName || o.userPrincipalName || o.externalId;
}

/** Total de páginas do conjunto atualmente listado (busca aplicada), mínimo 1. */
export function totalPages(p: KnightAffectedObjects): number {
  return Math.max(1, Math.ceil(p.matchCount / Math.max(1, p.pageSize)));
}

/**
 * A frase que a aba "Afetados" mostra quando NÃO há uma tabela para exibir — ou o alerta que acompanha uma
 * tabela incompleta. Retorna `null` quando a lista está completa e não há nada a ressalvar.
 */
export function affectedNotice(p: KnightAffectedObjects): string | null {
  switch (p.state) {
    case 'OutOfScope':
      return 'Este achado ainda não preserva a lista de objetos afetados. O número ao lado vem da regra ' +
        'determinística; o detalhe nominal chega em uma próxima entrega.';
    case 'NotPreserved':
      return 'Esta avaliação é anterior à preservação de detalhe. O resultado continua válido, mas os objetos ' +
        'daquela coleta não foram guardados — e a lista de hoje não serve de prova para um resultado de ontem. ' +
        'Execute uma nova avaliação para obter o detalhe.';
    case 'Partial':
      return p.limitation ??
        'A coleta não conseguiu enumerar todos os objetos deste achado — a lista abaixo é parcial.';
    default:
      return p.limitation;
  }
}

/** `true` quando existe tabela para mostrar (mesmo vazia por busca sem resultado). */
export function hasAffectedTable(p: KnightAffectedObjects): boolean {
  return p.state === 'Available' || p.state === 'Partial';
}

/**
 * Leitura HONESTA de um achado: o que o número significa, o que ele NÃO significa e qual é o critério da
 * regra. Corrige, sem fabricar conclusão, as quatro confusões que a revisão apontou — quantidade de
 * privilegiados não é quantidade de acessos desnecessários; o teto é parâmetro do AEGIS e não exigência do
 * NIST; registro de MFA não comprova imposição; atividade desconhecida não comprova inatividade.
 *
 * Devolve `null` para um achado sem leitura específica — a tela então mostra só a evidência do backend, sem
 * inventar interpretação.
 */
export interface FindingReading {
  /** O que o achado afirma, no limite do que a coleta provou. */
  means: string;
  /** O que ele explicitamente NÃO afirma. */
  doesNotMean: string;
  /** O critério da regra que produziu o veredito. */
  criterion: string;
}

const FINDING_READING: Record<string, FindingReading> = {
  'AK-ENTRA-002': {
    means:
      'Estes são os objetos que hoje têm algum papel privilegiado no diretório — pessoas, aplicações e ' +
      'grupos, juntos. É o conjunto que merece revisão de acesso.',
    doesNotMean:
      'Não significa que todos esses acessos sejam desnecessários, nem que alguém deva ser removido: o AEGIS ' +
      'não sabe quem precisa de qual papel. A decisão é da revisão humana.',
    criterion:
      'A regra compara o total de objetos privilegiados com um teto de menor privilégio. Esse teto é um ' +
      'parâmetro do AEGIS — o NIST recomenda menor privilégio, mas não fixa um número.',
  },
  'AK-ENTRA-001': {
    means:
      'Estas contas privilegiadas não aparecem com nenhum método capaz de MFA no relatório de registro do ' +
      'diretório.',
    doesNotMean:
      'Não significa que o acesso delas esteja necessariamente sem segundo fator: registro e capacidade de ' +
      'MFA não comprovam a imposição efetiva por política. A verificação da política é um passo à parte.',
    criterion:
      'Cruzamento entre os membros de papéis privilegiados e o relatório agregado de registro de métodos de ' +
      'autenticação. Conta ausente do relatório NÃO é contada como sem MFA.',
  },
  'AK-ENTRA-004': {
    // [AEGIS-MVP-PRODUCT-03] A frase "acesso de terceiro que ninguém está usando" afirmava DESUSO a partir da
    // ausência de registro — exatamente o que a regra não observa. O que a coleta viu é a falta de sinal de
    // acesso na janela; o resto é conclusão que só a área responsável pode dar.
    means:
      'Estes convidados não registraram acesso dentro da janela da regra. Um acesso de terceiro cuja ' +
      'necessidade ninguém confirmou permanece válido até que alguém o revise.',
    doesNotMean:
      'Atividade desconhecida não é inatividade comprovada: parte destes convidados pode simplesmente não ' +
      'ter registro de acesso disponível. O detalhe de cada linha diz qual é o caso.',
    criterion:
      'Convidados sem sinal de acesso dentro da janela de dias definida pela regra do AEGIS, considerando a ' +
      'data de criação quando não há acesso registrado.',
  },
};

export function findingReading(indicatorId: string): FindingReading | null {
  return FINDING_READING[indicatorId] ?? null;
}

/* ============================================================================================
 * [AEGIS-MVP-PRODUCT-02] Camada de APRESENTAÇÃO dos achados — compartilhada por KNIGHT e Prioridades.
 *
 * Motivo: o texto literal do catálogo diz "sem MFA efetivo", mas a fonte observa REGISTRO/capacidade de
 * método — a ressalva escondida na aba de evidência não conserta um título que afirma mais forte do que a
 * coleta prova. As funções abaixo são o ÚNICO lugar onde esse texto é escrito, para que a lista do KNIGHT e
 * a Central de Prioridades nunca digam coisas diferentes sobre o mesmo achado.
 *
 * O que elas NÃO fazem: não recalculam veredito, não reordenam, não tocam fórmula nem score, e não
 * reescrevem o texto gravado na avaliação — este continua visível, literal e identificado como tal, na aba
 * de evidência. São derivadas apenas de campos estruturados (`status`, `affectedObjectCount`), jamais de
 * parsing do texto histórico.
 * ============================================================================================ */

/** O mínimo que uma superfície precisa expor para ser apresentada — satisfeito por KnightIndicator e por PriorityKnightFinding. */
export interface FindingLike {
  indicatorId: string;
  title: string;
  status: KnightIndicatorStatus;
  affectedObjectCount: number;
  evidence: string;
}

/** Títulos claros para os achados com detalhe preservado; os demais mantêm o título do catálogo. */
const FINDING_TITLE: Record<string, string> = {
  'AK-ENTRA-001': 'Contas privilegiadas sem método de MFA registrado no diretório',
  'AK-ENTRA-002': 'Objetos privilegiados sujeitos a revisão de acesso',
  'AK-ENTRA-004': 'Convidados sinalizados por atividade desconhecida',
};

/** Título do achado na lista e na Central. Um achado sem título revisado mantém o do catálogo, sem invenção. */
export function findingTitle(f: FindingLike): string {
  return FINDING_TITLE[f.indicatorId] ?? f.title;
}

/**
 * A SITUAÇÃO em uma linha, no limite do que a coleta provou. Quando não há redação revisada para o achado,
 * devolve a evidência gravada — nunca uma frase inventada.
 */
export function findingSituation(f: FindingLike): string {
  const n = f.affectedObjectCount;
  const exposto = f.status === 'Exposed' || f.status === 'Mitigated';

  switch (f.indicatorId) {
    case 'AK-ENTRA-001':
      return exposto
        ? `${n} conta(s) privilegiada(s) sem nenhum método capaz de MFA no relatório de registro do ` +
          'diretório. Registro não comprova imposição por política.'
        : 'Nenhuma conta privilegiada aparece sem método capaz de MFA no relatório de registro. Isso não ' +
          'comprova imposição por política.';
    case 'AK-ENTRA-002':
      return exposto
        ? `${n} objeto(s) com papel privilegiado — acima do teto de menor privilégio parametrizado no ` +
          'AEGIS. É o conjunto sujeito a revisão, não uma lista de acessos desnecessários.'
        : 'Os objetos com papel privilegiado estão dentro do teto de menor privilégio parametrizado no ' +
          'AEGIS. O teto é parâmetro do AEGIS, não um número exigido pelo NIST.';
    case 'AK-ENTRA-004':
      return exposto
        ? `${n} convidado(s) sem sinal de acesso dentro da janela da regra. Atividade desconhecida não ` +
          'comprova desuso — o detalhe de cada linha diz qual é o caso.'
        : 'Nenhum convidado ficou sem sinal de acesso dentro da janela da regra.';
    default:
      return f.evidence;
  }
}

/* ============================================================================================
 * [AEGIS-MVP-PRODUCT-02] Guarda de CONTEXTO das leituras de afetados.
 *
 * O detalhe carrega por avaliação × indicador × página × busca. Uma resposta lenta de um contexto anterior
 * não pode preencher o contexto atual: seria apresentar objetos de um achado (ou de uma busca) como resposta
 * de outro. A chave abaixo identifica o pedido; a comparação decide quem pode escrever no estado.
 * ============================================================================================ */

/** Identidade de UM pedido de afetados. Busca vazia e ausente são o mesmo pedido. */
export function affectedRequestKey(
  runId: string,
  indicatorId: string,
  page: number,
  search: string | null,
): string {
  return [runId, indicatorId, String(page), (search ?? '').trim()].join('|');
}

/** `true` somente quando a resposta pertence ao pedido que está aberto agora. */
export function isCurrentAffectedResponse(current: string, responded: string): boolean {
  return current === responded;
}
