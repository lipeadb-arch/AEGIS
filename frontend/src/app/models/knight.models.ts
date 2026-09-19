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
  | 'ServiceAccounts'
  // [AEGIS-KNIGHT-COVERAGE-01] Controles de configuração do locatário.
  | 'TenantConfiguration'
  | 'AuthenticationPolicy'
  | 'ApplicationGovernance'
  | 'DeviceGovernance';

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
  | 'Error'
  /** Permissão existe, mas a LICENÇA do tenant não habilita a capacidade — distinto de permissão ausente. */
  | 'LimitedByLicense';

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
  /** [AEGIS-KNIGHT-MULTICLOUD-01] Evidências de configuração preservadas (não contadas como afetados). */
  evidenceObjectCount?: number;
  /** [AEGIS-KNIGHT-MULTICLOUD-01] Perfil, eixos e contribuição para a nota. Ausente em respostas antigas. */
  presentation?: KnightControlPresentation | null;
  /**
   * [AEGIS-KNIGHT-COVERAGE-01] Composição NOMEADA dos afetados ("12 contas de usuário e 2 aplicações"), calculada no
   * servidor pela mesma definição do HTML, CSV e PDF. Nula quando nada foi afetado ou em respostas antigas.
   */
  affectedComposition?: string | null;
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

/**
 * [AEGIS-KNIGHT-DURABLE-01] Uma execução que NÃO concluiu — só o cabeçalho. Não tem score, contagens nem
 * narrativa porque não tem veredito fechado: o que interessa é que a tentativa existiu e em que estado ficou.
 */
export interface KnightUnfinishedRun {
  id: string;
  status: KnightRunStatus;
  sourceType: KnightSourceType;
  mode: KnightMode;
  startedAt: string; // ISO 8601
}

/**
 * [AEGIS-KNIGHT-DURABLE-01] Resposta de `GET /latest`: o último RESULTADO CONCLUÍDO e, à parte, a tentativa
 * mais recente que não concluiu. A tela mostra o resultado como resultado e a tentativa como tentativa —
 * nunca uma no lugar da outra.
 */
export interface KnightLatest {
  assessment: KnightAssessment | null;
  unfinishedAttempt: KnightUnfinishedRun | null;
}

/**
 * Badge: demo → DEMONSTRAÇÃO; real → CONECTADO. Sem assessment, o badge segue a FONTE: conector real configurado
 * ainda sem sincronização é CONECTADO (a tela diz que falta a primeira avaliação); sem fonte, NÃO CONFIGURADO.
 */
export function connectionStateOf(a: KnightAssessment | null, realSourceConfigured = false): KnightConnectionState {
  if (!a) return realSourceConfigured ? 'Connected' : 'NotConfigured';
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

// [AEGIS-KNIGHT-MULTICLOUD-01] Vocabulário do assessment: controle APROVADO ou REPROVADO — o mesmo do relatório
// HTML/CSV. "Mitigado" é exposição com controle compensatório COMPROVADO (atenção), nunca um rótulo visual.
const STATUS_LABEL: Record<KnightIndicatorStatus, string> = {
  Passed: 'Aprovado',
  Exposed: 'Reprovado',
  Mitigated: 'Mitigado (atenção)',
  NotEvaluated: 'Não avaliado',
  Error: 'Erro na avaliação',
  NotApplicable: 'Não aplicável',
};

export function statusLabel(status: KnightIndicatorStatus): string {
  return STATUS_LABEL[status];
}

const SEVERITY_LABEL: Record<SeverityLevel, string> = {
  Critical: 'Crítico',
  High: 'Alto',
  Medium: 'Médio',
  Low: 'Baixo',
  Informational: 'Informativo',
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
  TenantConfiguration: 'Configuração do locatário',
  AuthenticationPolicy: 'Política de autenticação',
  ApplicationGovernance: 'Governança de aplicações',
  DeviceGovernance: 'Governança de dispositivos',
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
  LimitedByLicense: 'Licença insuficiente',
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
  IdentityRiskyUsers: 'Usuários sinalizados como de risco',
  RiskyUsers: 'Usuários sinalizados como de risco',
  ApplicationPermissions: 'Permissões de aplicativo concedidas',
  ApplicationConsents: 'Consentimentos delegados (todos os usuários)',
  DirectoryUsers: 'Diretório de usuários',
  DirectoryGroups: 'Grupos e membros externos',
  DriveSharingAudit: 'Auditoria de compartilhamento no Drive',
  OAuthTokenAudit: 'Auditoria de autorizações OAuth',
  AuthenticationMethods: 'Métodos de autenticação registrados',
  // [AEGIS-KNIGHT-COVERAGE-01] Configuração do locatário (mesmos nomes do relatório exportado).
  AuthorizationPolicy: 'Política de autorização do diretório',
  AdminConsentPolicy: 'Fluxo de consentimento do administrador',
  AppManagementPolicy: 'Política de gerenciamento de aplicações',
  AuthenticationMethodsPolicy: 'Política de métodos de autenticação',
  DirectorySettings: 'Configurações de diretório (senhas e grupos)',
  Domains: 'Domínios',
  DirectorySynchronization: 'Sincronização híbrida',
  DeviceRegistrationPolicy: 'Política de registro de dispositivos',
  GroupVisibility: 'Visibilidade de grupos do Microsoft 365',
  PrivilegedAccountDetails: 'Origem e licenças das contas privilegiadas',
  PrivilegedIdentityManagement: 'Privileged Identity Management (PIM)',
  AccessReviews: 'Revisões de acesso',
  NamedLocations: 'Locais nomeados',
  ServicePrincipalSettings: 'Aplicações de serviço do Microsoft 365',
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
export type KnightAffectedObjectKind =
  | 'User'
  | 'Guest'
  | 'ServicePrincipal'
  | 'Group'
  | 'Device'
  | 'Unknown'
  | 'Policy'
  | 'DirectoryRole'
  | 'TenantSetting'
  | 'Domain';

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
  /** [AEGIS-KNIGHT-MULTICLOUD-01] Afetado (contado) ou evidência de configuração que sustentou o veredito. */
  relation?: 'Affected' | 'Evidence';
  /** [AEGIS-KNIGHT-MULTICLOUD-01] Configuração observada (políticas, papéis), quando houver. */
  observedConfiguration?: string | null;
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

/** [AEGIS-KNIGHT-COVERAGE-01] Mesmos rótulos do relatório exportado (definição única no servidor: KnightObjectNouns). */
const AFFECTED_KIND_LABEL: Record<KnightAffectedObjectKind, string> = {
  User: 'Conta de usuário',
  Guest: 'Conta de convidado',
  ServicePrincipal: 'Aplicação',
  Group: 'Grupo',
  Device: 'Dispositivo',
  Unknown: 'Item de tipo não identificado',
  Policy: 'Política',
  DirectoryRole: 'Papel de diretório',
  TenantSetting: 'Configuração do locatário',
  Domain: 'Domínio',
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
  /** [AEGIS-KNIGHT-COVERAGE-01] Composição nomeada dos afetados, quando o servidor a informou. */
  affectedComposition?: string | null;
}

/** Títulos claros para os achados com detalhe preservado; os demais mantêm o título do catálogo. */
const FINDING_TITLE: Record<string, string> = {
  'AK-ENTRA-001': 'Contas privilegiadas sem método de MFA registrado no diretório',
  // [AEGIS-KNIGHT-COVERAGE-01] A lista mistura contas, convidados, aplicações e grupos: "objetos" não dizia o quê.
  'AK-ENTRA-002': 'Identidades com papel administrativo para revisão de acesso',
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
        ? `${f.affectedComposition ?? `${n} identidade(s)`} com papel privilegiado — acima do teto de menor privilégio ` +
          'parametrizado no AEGIS. É o conjunto sujeito a revisão, não uma lista de acessos desnecessários.'
        : 'As identidades com papel privilegiado estão dentro do teto de menor privilégio parametrizado no ' +
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

/* ============================================================================================
 * [AEGIS-KNIGHT-MULTICLOUD-01] Assessment de postura — perfil, eixos, visão geral e filtros
 *
 * Tudo aqui é derivado da avaliação que a API devolveu: nenhuma contagem é estimada, nenhum gráfico tem série
 * inventada, e as unidades não se misturam — controles, ocorrências (objeto × controle) e objetos únicos são
 * números diferentes. Funções PURAS (testadas em tests/knight-assessment.models.spec.ts).
 * ============================================================================================ */

export interface KnightControlReference {
  framework: string;
  version: string | null;
  code: string;
  url: string | null;
}

export interface KnightControlPresentation {
  domain: string;
  domainLabel: string;
  service: string;
  provider: string;
  description: string | null;
  rationale: string | null;
  expectedConfiguration: string | null;
  doesNotProve: string | null;
  criterion: string | null;
  references: KnightControlReference[];
  requiredCapabilities: string[];
  weight: number;
  factor: number | null;
  achievedPoints: number | null;
  possiblePoints: number | null;
  /** [AEGIS-KNIGHT-COVERAGE-01] Impacto potencial (texto determinístico do catálogo), distinto do risco (rationale). */
  impact?: string | null;
  /** [AEGIS-KNIGHT-COVERAGE-01] Plataforma: Microsoft Entra ID, Microsoft 365, Microsoft Azure, Google Workspace. */
  platform?: string | null;
  serviceKey?: string | null;
}

/* ---- [AEGIS-KNIGHT-COVERAGE-01] Cobertura do catálogo de referência (propriedade do produto) ---------------- */

export type KnightReferenceDisposition = 'Implemented' | 'Partial' | 'Pending' | 'ManualOnly' | 'RequiresAccess' | 'ApiLimitation';

export interface KnightReferenceCoverageGroup {
  key: string;
  label: string;
  total: number;
  implemented: number;
  partial: number;
  pending: number;
  manualOnly: number;
  requiresAccess: number;
  apiLimitation: number;
  fullPercent: number;
  partialPercent: number;
  anyAutomatedPercent: number;
}

export interface KnightReferenceControlStatus {
  key: string;
  framework: string;
  version: string;
  section: string | null;
  variant: string | null;
  service: string;
  serviceLabel: string;
  platform: string;
  severity: string;
  title: string;
  disposition: KnightReferenceDisposition;
  dispositionLabel: string;
  indicatorIds: string[];
  note: string | null;
}

export interface KnightReferenceCoverage {
  catalogVersion: string;
  referenceCommit: string;
  frameworks: string[];
  total: KnightReferenceCoverageGroup;
  byPlatform: KnightReferenceCoverageGroup[];
  byService: KnightReferenceCoverageGroup[];
  controls: KnightReferenceControlStatus[];
}

/** As TRÊS medidas que a tela nunca mistura: catálogo (produto), avaliação (coleta neste ambiente) e aprovação. */
export interface KnightThreeMeasures {
  catalogFull: number | null;
  catalogPartial: number | null;
  catalogAnyAutomated: number | null;
  assessmentCoverage: number;
  approval: number | null;
}

export function threeMeasures(a: KnightAssessment, c: KnightReferenceCoverage | null, platform: string | null = null): KnightThreeMeasures {
  const g = c ? (platform ? c.byPlatform.find((p) => p.label === platform) ?? null : c.total) : null;
  return {
    catalogFull: g ? g.fullPercent : null,
    catalogPartial: g ? g.partialPercent : null,
    catalogAnyAutomated: g ? g.anyAutomatedPercent : null,
    assessmentCoverage: a.coverage,
    approval: overviewKpis(a).approvalPercent,
  };
}

export interface KnightAffectedSummaryItem {
  externalId: string;
  kind: KnightAffectedObjectKind;
  displayName: string | null;
  userPrincipalName: string | null;
  controlCount: number;
  indicatorIds: string[];
}

/** Ocorrências × objetos únicos × controles expostos de uma avaliação. */
export interface KnightAffectedSummary {
  runId: string;
  exposedControls: number;
  occurrences: number;
  uniqueObjects: number;
  complete: boolean;
  incompleteIndicatorIds: string[];
  top: KnightAffectedSummaryItem[];
}

/**
 * Linha curta dos controles com achados: ocorrências (cada aparição de um objeto num controle) e objetos
 * distintos. Com a lista de algum controle incompleta, o total de objetos é um mínimo — dito em palavras.
 */
export function knightUnitsLine(s: KnightAffectedSummary | null): string {
  if (!s) return 'Controles reprovados ou mitigados.';
  const occ = `${s.occurrences} ocorrência(s)`;
  if (s.complete) return `${occ} em ${s.uniqueObjects} item(ns) distinto(s) (contas, aplicações, papéis, políticas…).`;
  const n = s.incompleteIndicatorIds.length;
  return `${occ} em pelo menos ${s.uniqueObjects} item(ns) distinto(s) — a lista de ${n} controle(s) está incompleta.`;
}

export const NIST_FRAMEWORK = 'NIST CSF';
export const MITRE_FRAMEWORK = 'MITRE ATT&CK';
const SEVERITY_ORDER: SeverityLevel[] = ['Critical', 'High', 'Medium', 'Low', 'Informational'];

/** Um controle reprovado ou mitigado é um FINDING — o que o relatório conta por severidade. */
export function isFinding(i: KnightIndicator): boolean {
  return i.status === 'Exposed' || i.status === 'Mitigated';
}

export function isEvaluated(i: KnightIndicator): boolean {
  return i.status === 'Passed' || i.status === 'Exposed' || i.status === 'Mitigated';
}

/** Domínio, serviço, provedor e plataforma do controle; resposta antiga (sem perfil) cai na fonte, nunca em "Microsoft". */
export function axesOf(i: KnightIndicator): { domain: string; domainLabel: string; service: string; provider: string; platform: string } {
  const p = i.presentation;
  if (p) return { domain: p.domain, domainLabel: p.domainLabel, service: p.service, provider: p.provider, platform: p.platform ?? p.service };
  const service = sourceTypeLabel(i.sourceType);
  const provider = i.sourceType === 'MicrosoftEntraId' ? 'Microsoft' : i.sourceType === 'GoogleWorkspace' ? 'Google' : 'Demonstração';
  return { domain: 'Identity', domainLabel: 'Identidade', service, provider, platform: service };
}

/** [AEGIS-KNIGHT-COVERAGE-01] Benchmark de configuração (referência fixada no catálogo de referência). */
export function isBenchmark(framework: string): boolean {
  return framework.startsWith('CIS ');
}

/** Frameworks do controle (um controle mapeado a dois frameworks continua sendo UM controle). */
export function frameworksOf(i: KnightIndicator): string[] {
  const out = new Set<string>();
  for (const r of i.presentation?.references ?? []) {
    if (r.framework === NIST_FRAMEWORK || r.framework === MITRE_FRAMEWORK || isBenchmark(r.framework))
      out.add(r.version ? `${r.framework} ${r.version}` : r.framework);
  }
  if (!i.presentation) {
    if (i.nistCodes.length) out.add(`${NIST_FRAMEWORK} 2.0`);
    if (i.mitreTechniques.length) out.add(MITRE_FRAMEWORK);
  }
  return [...out];
}

export interface KnightOverviewKpis {
  total: number;
  evaluated: number;
  passed: number;
  failed: number;
  mitigated: number;
  notEvaluated: number;
  errors: number;
  notApplicable: number;
  /** Aprovados ÷ avaliados — NÃO é a nota. `null` sem controle avaliado. */
  approvalPercent: number | null;
  findings: number;
  findingsBySeverity: { key: SeverityLevel; label: string; count: number }[];
}

export function overviewKpis(a: KnightAssessment): KnightOverviewKpis {
  const ind = a.indicators;
  const passed = ind.filter((i) => i.status === 'Passed').length;
  const failed = ind.filter((i) => i.status === 'Exposed').length;
  const mitigated = ind.filter((i) => i.status === 'Mitigated').length;
  const evaluated = passed + failed + mitigated;
  const findings = ind.filter(isFinding);
  return {
    total: ind.length,
    evaluated,
    passed,
    failed,
    mitigated,
    notEvaluated: ind.filter((i) => i.status === 'NotEvaluated').length,
    errors: ind.filter((i) => i.status === 'Error').length,
    notApplicable: ind.filter((i) => i.status === 'NotApplicable').length,
    approvalPercent: evaluated > 0 ? Math.round((1000 * passed) / evaluated) / 10 : null,
    findings: findings.length,
    findingsBySeverity: SEVERITY_ORDER.map((key) => ({
      key,
      label: severityLabel(key),
      count: findings.filter((f) => f.severity === key).length,
    })),
  };
}

export interface KnightDistributionRow {
  key: string;
  label: string;
  counts: Record<KnightIndicatorStatus, number>;
  total: number;
}

/** Distribuição de resultados por eixo (domínio ou serviço). Cada controle entra UMA vez. */
export function distributionBy(a: KnightAssessment, axis: 'domain' | 'service' | 'platform'): KnightDistributionRow[] {
  const rows = new Map<string, KnightDistributionRow>();
  for (const i of a.indicators) {
    const ax = axesOf(i);
    const key = axis === 'domain' ? ax.domain : axis === 'platform' ? ax.platform : ax.service;
    const label = axis === 'domain' ? ax.domainLabel : axis === 'platform' ? ax.platform : ax.service;
    const row = rows.get(key) ?? {
      key,
      label,
      counts: { Passed: 0, Exposed: 0, Mitigated: 0, NotEvaluated: 0, Error: 0, NotApplicable: 0 },
      total: 0,
    };
    row.counts[i.status]++;
    row.total++;
    rows.set(key, row);
  }
  return [...rows.values()].sort(
    (x, y) => y.counts.Exposed - x.counts.Exposed || y.total - x.total || x.label.localeCompare(y.label),
  );
}

const WEIGHT: Record<SeverityLevel, number> = { Critical: 10, High: 7, Medium: 4, Low: 2, Informational: 0 };

/** Até cinco controles REPROVADOS: severidade, depois objetos afetados — critérios verificáveis, nada inventado. */
export function priorityControls(a: KnightAssessment, max = 5): KnightIndicator[] {
  return a.indicators
    .filter((i) => i.status === 'Exposed')
    .sort(
      (x, y) =>
        WEIGHT[y.severity] - WEIGHT[x.severity] ||
        y.affectedObjectCount - x.affectedObjectCount ||
        x.indicatorId.localeCompare(y.indicatorId),
    )
    .slice(0, max);
}

/** Filtros combináveis da aba de controles. `status: 'findings'` = reprovado OU mitigado. */
export interface KnightControlFilters {
  q: string;
  status: '' | 'findings' | KnightIndicatorStatus;
  severity: '' | SeverityLevel;
  platform: string;
  service: string;
  domain: string;
  framework: string;
}

export const EMPTY_FILTERS: KnightControlFilters = { q: '', status: '', severity: '', platform: '', service: '', domain: '', framework: '' };

export function matchesFilters(i: KnightIndicator, f: KnightControlFilters): boolean {
  if (f.status === 'findings' ? !isFinding(i) : f.status && i.status !== f.status) return false;
  if (f.severity && i.severity !== f.severity) return false;
  const ax = axesOf(i);
  if (f.platform && ax.platform !== f.platform) return false;
  if (f.service && ax.service !== f.service) return false;
  if (f.domain && ax.domain !== f.domain) return false;
  if (f.framework && !frameworksOf(i).includes(f.framework)) return false;
  const q = f.q.trim().toLowerCase();
  if (q) {
    const hay = [i.indicatorId, findingTitle(i), i.title, i.evidence, ax.service, ax.domainLabel, i.presentation?.description ?? '']
      .join(' ')
      .toLowerCase();
    if (!hay.includes(q)) return false;
  }
  return true;
}

/** O recorte aplicado, em palavras — a tela diz o que está mostrando, sempre. */
export function describeFilters(f: KnightControlFilters, a: KnightAssessment | null): string {
  const parts: string[] = [];
  if (f.status) parts.push(`resultado: ${f.status === 'findings' ? 'findings' : statusLabel(f.status)}`);
  if (f.severity) parts.push(`severidade: ${severityLabel(f.severity)}`);
  if (f.platform) parts.push(`plataforma: ${f.platform}`);
  if (f.service) parts.push(`serviço: ${f.service}`);
  if (f.domain) {
    const label = a?.indicators.map(axesOf).find((x) => x.domain === f.domain)?.domainLabel ?? f.domain;
    parts.push(`domínio: ${label}`);
  }
  if (f.framework) parts.push(`framework: ${f.framework}`);
  if (f.q.trim()) parts.push(`pesquisa: “${f.q.trim()}”`);
  return parts.length ? parts.join(' · ') : 'sem filtros (avaliação completa)';
}

export function filterOptions(a: KnightAssessment): {
  platforms: string[];
  services: string[];
  domains: { key: string; label: string }[];
  frameworks: string[];
} {
  const platforms = new Set<string>();
  const services = new Set<string>();
  const domains = new Map<string, string>();
  const frameworks = new Set<string>();
  for (const i of a.indicators) {
    const ax = axesOf(i);
    platforms.add(ax.platform);
    services.add(ax.service);
    domains.set(ax.domain, ax.domainLabel);
    frameworksOf(i).forEach((x) => frameworks.add(x));
  }
  return {
    platforms: [...platforms].sort(),
    services: [...services].sort(),
    domains: [...domains.entries()].map(([key, label]) => ({ key, label })).sort((x, y) => x.label.localeCompare(y.label)),
    frameworks: [...frameworks].sort(),
  };
}

/** Uma limitação de coleta com o que ela prejudicou e o que fazer. */
export interface KnightLimitationView {
  capability: string;
  label: string;
  cause: string;
  detail: string | null;
  affectedControls: string[];
  guidance: string;
}

const CAUSE: Record<string, [string, string]> = {
  InsufficientPermission: [
    'Permissão ausente',
    'Conceder a permissão indicada ao aplicativo do conector (consentimento de administrador) e sincronizar novamente em Integrações.',
  ],
  LimitedByLicense: [
    'Licença insuficiente',
    'A capacidade depende de licença do provedor. Sem ela, os controles afetados continuam não avaliados — nunca aprovados.',
  ],
  Throttled: ['Limite de taxa do provedor', 'Sincronizar novamente mais tarde.'],
  AuthenticationFailure: ['Falha de autenticação', 'Verificar a credencial do conector e reconectar em Integrações.'],
  Unavailable: ['Serviço indisponível', 'Sincronizar novamente; persistindo, verificar a conectividade.'],
  NotAttempted: ['Não executada', 'A capacidade não foi executada nesta coleta.'],
  Error: ['Erro de coleta', 'Sincronizar novamente; persistindo, acionar o suporte com o horário da tentativa.'],
};

/** Capacidades não coletadas → causa, controles prejudicados (não avaliados que dependem dela) e orientação. */
export function limitationViews(a: KnightAssessment): KnightLimitationView[] {
  return problemCapabilities(a.capabilities).map((c) => {
    const [cause, guidance] = CAUSE[c.outcome] ?? ['Não coletado', 'Sincronizar novamente em Integrações.'];
    return {
      capability: c.capability,
      label: capabilityLabel(c.capability),
      cause,
      detail: c.detail,
      affectedControls: a.indicators
        .filter((i) => (i.status === 'NotEvaluated' || i.status === 'Error') && (i.presentation?.requiredCapabilities ?? []).includes(c.capability))
        .map((i) => i.indicatorId),
      guidance,
    };
  });
}

/** Contribuição do controle para a nota, em palavras (peso × fator); fora da nota quando não avaliado. */
export function contributionText(i: KnightIndicator): string {
  const p = i.presentation;
  if (!p) return 'Contribuição não disponível nesta resposta.';
  if (p.factor === null) return 'Fora da nota (não avaliado, erro ou não aplicável): reduz a cobertura, não a nota.';
  const fmt = (n: number | null) => String(n ?? 0).replace('.', ',');
  return `Peso ${p.weight} × fator ${fmt(p.factor)} = ${fmt(p.achievedPoints)} de ${p.possiblePoints} ponto(s).`;
}
