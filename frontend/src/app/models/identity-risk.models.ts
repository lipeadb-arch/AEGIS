// [AEGIS-MVP-MICROSOFT-COVERAGE-03] Risco de identidade (Microsoft Entra ID Protection).
//
// Espelha o contrato agregado de /api/v1/telemetry/identity/entra-id (camelCase; enums viajam como NOME).
// Funções PURAS de apresentação — testáveis sem Angular (ver frontend/tests/identity-risk.models.spec.ts).
//
// INVARIANTES QUE ESTE MÓDULO TRAVA:
//  • ausência de dado NUNCA vira 0 na tela: `null` é "—" com um estado explicando o porquê;
//  • "permissão não concedida", "licença insuficiente", "coleta parcial", "indisponível" e "nunca coletado"
//    são estados DISTINTOS, jamais colapsados em "sem risco";
//  • nada aqui carrega nome, e-mail, ID, IP ou localização — o backend não envia, e a tela não inventa;
//  • ausência de detecções NÃO é prova de que os controles estejam eficazes.

/** Desfecho tipado de UMA capacidade de coleta (espelha KnightCapabilityOutcome). */
export type IdentityRiskOutcome =
  | 'Collected'
  | 'InsufficientPermission'
  | 'LimitedByLicense'
  | 'Unavailable'
  | 'NotAttempted'
  | 'Throttled'
  | 'AuthenticationFailure'
  | 'Error';

export interface IdentityRiskCapability {
  outcome: IdentityRiskOutcome;
  detail: string | null;
  /** Há agregados preservados desta capacidade (mesmo que a última tentativa tenha falhado). */
  hasData: boolean;
  /** A leitura terminou por completo — quando falso, os números são um PISO, não o total. */
  isComplete: boolean;
}

export interface IdentityRiskLevels {
  high: number;
  medium: number;
  low: number;
  none: number;
  /** Nível SUPRIMIDO pela fonte (tipicamente licença) — não é "sem risco". */
  hidden: number;
  unknown: number;
}

export interface IdentityRiskStates {
  atRisk: number;
  confirmedCompromised: number;
  remediated: number;
  dismissed: number;
  confirmedSafe: number;
  none: number;
  unknown: number;
  /** Em aberto (atRisk + confirmedCompromised). O bucket desconhecido NÃO entra aqui. */
  active: number;
  /** Tratado (remediated + dismissed + confirmedSafe). */
  resolved: number;
}

export interface IdentityRiskCategory {
  category: string;
  count: number;
}

export interface IdentityRiskyUsers {
  total: number;
  deleted: number;
  processing: number;
  /** Entradas de contas ainda existentes — base das distribuições. */
  live: number;
  active: number;
  highRiskActive: number;
  levels: IdentityRiskLevels;
  states: IdentityRiskStates;
  mostRecentRiskUpdateAt: string | null;
  isComplete: boolean;
}

export interface IdentityRiskDetections {
  windowDays: number;
  windowStart: string;
  windowEnd: string;
  totalInWindow: number;
  outsideWindow: number;
  undated: number;
  inRecentWindow: number;
  active: number;
  resolved: number;
  highRiskActive: number;
  /** Detecções cuja categoria a fonte suprimiu (tipo `generic`) — indício de detalhe limitado por licença. */
  premiumDetailWithheld: number;
  realtime: number;
  nearRealtime: number;
  offline: number;
  timingNotDefined: number;
  timingUnknown: number;
  levels: IdentityRiskLevels;
  states: IdentityRiskStates;
  topTypes: IdentityRiskCategory[];
  mostRecentDetectionAt: string | null;
  isComplete: boolean;
}

export interface IdentityRisk {
  riskyUsersCapability: IdentityRiskCapability;
  riskDetectionsCapability: IdentityRiskCapability;
  riskyUsers: IdentityRiskyUsers | null;
  detections: IdentityRiskDetections | null;
  evaluatedAt: string;
}

export interface IdentityAuthenticationPosture {
  totalUsers: number;
  mfaCapable: number;
  mfaRegistered: number;
  passwordlessCapable: number;
  capabilityUnknown: number;
  mfaCapableCoveragePercent: number | null;
  passwordlessCoveragePercent: number | null;
  methodsRegistered: IdentityRiskCategory[];
  isComplete: boolean;
}

/** Estado do conector de identidade (espelha IdentityEvidenceConnectorState). */
export type IdentityConnectorState = 'NotConfigured' | 'Disabled' | 'MissingCredential' | 'Configured';

/** Estado da coleta armazenada (espelha IdentityEvidenceCollectionState). */
export type IdentityCollectionState =
  | 'NoConnector'
  | 'Disabled'
  | 'MissingCredential'
  | 'NeverCollected'
  | 'Complete'
  | 'Partial';

export interface IdentityCapabilityEntry {
  capability: string;
  outcome: IdentityRiskOutcome;
  detail: string | null;
}

/** Projeção consultiva completa devolvida pela Evidence Fabric de identidade. */
export interface IdentityEvidenceProjection {
  connectorState: IdentityConnectorState;
  collectionState: IdentityCollectionState;
  lastAttemptState: string;
  isDegraded: boolean;
  source: string;
  schemaVersion: string | null;
  collectedAt: string | null;
  lastAttemptAt: string | null;
  lastAttemptDetail: string | null;
  capabilities: IdentityCapabilityEntry[];
  controls: { code: string; title: string; state: string; explanation: string }[];
  identityRisk: IdentityRisk | null;
  authenticationPosture: IdentityAuthenticationPosture | null;
}

// ---- Linguagem operacional ---------------------------------------------------------------------
// Nomes crus de enum (confirmedCompromised, atRisk…) NÃO aparecem na visão inicial. E em nenhum lugar o
// AEGIS afirma ter confirmado um comprometimento — quem marcou foi a Microsoft.

const OUTCOME_LABEL: Record<IdentityRiskOutcome, string> = {
  Collected: 'Coletado',
  InsufficientPermission: 'Permissão ainda não concedida',
  LimitedByLicense: 'Licença insuficiente',
  Unavailable: 'Indisponível no momento',
  NotAttempted: 'Ainda não coletado',
  Throttled: 'Limite de consultas atingido',
  AuthenticationFailure: 'Falha de autenticação',
  Error: 'Erro na coleta',
};

export function outcomeLabel(outcome: IdentityRiskOutcome): string {
  return OUTCOME_LABEL[outcome] ?? 'Ainda não coletado';
}

/** Explicação objetiva do estado da capacidade + a ação que o operador pode tomar. */
export function outcomeGuidance(outcome: IdentityRiskOutcome, permission: string): string {
  switch (outcome) {
    case 'Collected':
      return 'Dados lidos diretamente do Microsoft Entra ID Protection.';
    case 'InsufficientPermission':
      return `Conceda ${permission} à aplicação e refaça a coleta. Enquanto isso, esta dimensão fica sem dados — o que não significa ausência de risco.`;
    case 'LimitedByLicense':
      return 'O tenant não tem a licença Microsoft Entra ID necessária para esta dimensão. Os dados existem no produto, mas não são expostos a este plano.';
    case 'Throttled':
      return 'A Microsoft limitou temporariamente o volume de consultas. Tente novamente mais tarde — os dados anteriores foram preservados.';
    case 'AuthenticationFailure':
      return 'A aplicação não conseguiu autenticar junto à Microsoft. Revise a credencial do conector.';
    case 'NotAttempted':
      return 'Esta dimensão ainda não foi coletada neste cliente.';
    default:
      return 'A Microsoft não respondeu a esta consulta. A última fotografia válida, se houver, continua sendo exibida.';
  }
}

/** Rótulos amigáveis dos tipos de detecção mais comuns; um tipo novo aparece pelo próprio nome. */
const DETECTION_TYPE_LABEL: Record<string, string> = {
  adminconfirmedusercompromised: 'Comprometimento confirmado por administrador',
  anomaloustoken: 'Token anômalo',
  anomaloususeractivity: 'Atividade anômala do usuário',
  anonymizedipaddress: 'Acesso por IP anonimizado',
  generic: 'Detecção sem detalhe (requer plano superior)',
  impossibletravel: 'Deslocamento impossível',
  investigationsthreatintelligence: 'Inteligência de ameaças',
  leakedcredentials: 'Credenciais vazadas',
  maliciousipaddress: 'IP malicioso',
  malwareinfectedipaddress: 'IP com malware',
  mcassuspiciousinboxmanipulationrules: 'Regras suspeitas de caixa de entrada',
  newcountry: 'Acesso de país inédito',
  other: 'Outros tipos',
  passwordspray: 'Ataque de password spray',
  riskyipaddress: 'IP de risco',
  suspiciousapitraffic: 'Tráfego de API suspeito',
  suspiciousbrowser: 'Navegador suspeito',
  suspiciousinboxforwarding: 'Encaminhamento suspeito de e-mail',
  suspiciousipaddress: 'IP suspeito',
  suspicioussendingpatterns: 'Padrão de envio suspeito',
  tokenissueranomaly: 'Anomalia no emissor do token',
  unfamiliarfeatures: 'Comportamento fora do padrão',
  unknown: 'Tipo não informado',
  unlikelytravel: 'Deslocamento improvável',
};

export function detectionTypeLabel(category: string): string {
  return DETECTION_TYPE_LABEL[category.toLowerCase()] ?? category;
}

/** Rótulos amigáveis dos métodos de autenticação registrados. */
const METHOD_LABEL: Record<string, string> = {
  email: 'E-mail',
  fido2securitykey: 'Chave de segurança FIDO2',
  microsoftauthenticatorpush: 'Microsoft Authenticator (push)',
  mobilephone: 'Telefone celular',
  other: 'Outros métodos',
  passkeydevicebound: 'Passkey vinculada ao dispositivo',
  softwareonetimepasscode: 'Código único por aplicativo',
  temporaryaccesspass: 'Passe de acesso temporário',
  unknown: 'Método não informado',
  windowshelloforbusiness: 'Windows Hello for Business',
};

export function methodLabel(category: string): string {
  return METHOD_LABEL[category.toLowerCase()] ?? category;
}

// ---- Regras de apresentação --------------------------------------------------------------------

/**
 * Um número só pode ser mostrado quando a capacidade PRODUZIU dados. Sem dados devolvemos `null`, que a UI
 * renderiza como "—". Nunca 0: zero é uma afirmação forte ("não há risco") que só a coleta completa sustenta.
 */
export function riskCount(capability: IdentityRiskCapability | null, value: number | null | undefined): number | null {
  if (!capability || !capability.hasData) return null;
  return typeof value === 'number' ? value : null;
}

/** Formata a contagem para exibição: "—" quando indisponível; "≥ n" quando a leitura ficou incompleta. */
export function countDisplay(capability: IdentityRiskCapability | null, value: number | null | undefined): string {
  const n = riskCount(capability, value);
  if (n === null) return '—';
  return capability && !capability.isComplete ? `≥ ${n}` : String(n);
}

/** Uma capacidade em qualquer estado que não seja coleta íntegra — a UI destaca e explica. */
export function isLimited(capability: IdentityRiskCapability | null): boolean {
  if (!capability) return true;
  return capability.outcome !== 'Collected' || !capability.isComplete;
}

/** Estados globais que a seção de risco pode assumir — cada um com sua mensagem própria. */
export type IdentityRiskSectionState =
  | 'NoConnector'
  | 'NeverCollected'
  | 'PreservedAfterFailure'
  | 'Partial'
  | 'Complete';

/**
 * Estado da SEÇÃO a partir da projeção. Distingue explicitamente "nunca coletado" de "última fotografia
 * preservada após falha" e de "coleta parcial" — três realidades operacionais diferentes.
 */
export function sectionState(p: IdentityEvidenceProjection | null): IdentityRiskSectionState {
  if (!p || p.connectorState === 'NotConfigured') return 'NoConnector';
  if (!p.identityRisk) return 'NeverCollected';

  const caps = [p.identityRisk.riskyUsersCapability, p.identityRisk.riskDetectionsCapability];
  const anyData = caps.some((c) => c.hasData);
  if (!anyData) return 'NeverCollected';

  // Degradação com evidência preservada tem precedência: o operador precisa saber que está vendo o passado.
  if (p.isDegraded) return 'PreservedAfterFailure';
  return caps.every((c) => c.outcome === 'Collected' && c.isComplete) ? 'Complete' : 'Partial';
}

const SECTION_MESSAGE: Record<IdentityRiskSectionState, string> = {
  NoConnector: 'Conecte o Microsoft Entra ID para acompanhar o risco de identidade deste cliente.',
  NeverCollected:
    'Nenhuma coleta de risco de identidade foi executada ainda. Sem coleta não há como afirmar que existe — ou que não existe — risco.',
  PreservedAfterFailure:
    'Esta é a última fotografia válida. A coleta mais recente falhou e os dados anteriores foram preservados, não apagados.',
  Partial:
    'Coleta parcial: parte das dimensões não pôde ser lida. Os números mostrados são um piso, não o total.',
  Complete: 'Sinais de identidade detectados nos últimos 30 dias, lidos do Microsoft Entra ID Protection.',
};

export function sectionMessage(state: IdentityRiskSectionState): string {
  return SECTION_MESSAGE[state];
}

/**
 * Ressalva OBRIGATÓRIA da seção. Risco é uma fotografia dinâmica e a ausência de eventos não é evidência de
 * eficácia — a tela nunca deixa o leitor concluir o contrário.
 */
export const NO_DETECTION_CAVEAT =
  'A ausência de detecções não comprova que os controles estejam eficazes.';

/** Nenhum destes agregados entra no AEGIS Score nem no AEGIS KNIGHT Score. */
export const CONSULTATIVE_CAVEAT =
  'Indicadores operacionais e consultivos: não alteram o AEGIS Score nem o AEGIS KNIGHT Score.';

/** Ordena tipos de detecção por volume (desempate estável pelo rótulo) e limita a quantidade exibida. */
export function topDetectionTypes(detections: IdentityRiskDetections | null, limit = 5): IdentityRiskCategory[] {
  if (!detections) return [];
  return [...detections.topTypes]
    .sort((a, b) => b.count - a.count || a.category.localeCompare(b.category))
    .slice(0, limit);
}

/** Fatias não vazias da distribuição por nível, do mais grave ao menos grave, com rótulo em pt-BR. */
export function levelSlices(levels: IdentityRiskLevels | null): { key: string; label: string; count: number }[] {
  if (!levels) return [];
  const all = [
    { key: 'high', label: 'Alto', count: levels.high },
    { key: 'medium', label: 'Médio', count: levels.medium },
    { key: 'low', label: 'Baixo', count: levels.low },
    { key: 'none', label: 'Sem nível atribuído', count: levels.none },
    { key: 'hidden', label: 'Nível não revelado pelo plano', count: levels.hidden },
    { key: 'unknown', label: 'Nível desconhecido', count: levels.unknown },
  ];
  return all.filter((s) => s.count > 0);
}

/**
 * Fatias da distribuição por estado, agrupadas em EM ABERTO × RESOLVIDO × DESCONHECIDO. O bucket desconhecido
 * aparece sempre que houver contagem — nunca é somado a "resolvido" nem descartado.
 */
export function stateSlices(states: IdentityRiskStates | null): { key: string; label: string; count: number }[] {
  if (!states) return [];
  const all = [
    { key: 'atRisk', label: 'Exigem investigação', count: states.atRisk },
    { key: 'confirmedCompromised', label: 'Marcadas como potencialmente comprometidas', count: states.confirmedCompromised },
    { key: 'remediated', label: 'Corrigidas', count: states.remediated },
    { key: 'dismissed', label: 'Descartadas pela equipe', count: states.dismissed },
    { key: 'confirmedSafe', label: 'Confirmadas seguras', count: states.confirmedSafe },
    { key: 'none', label: 'Sem estado de risco', count: states.none },
    { key: 'unknown', label: 'Estado desconhecido', count: states.unknown },
  ];
  return all.filter((s) => s.count > 0);
}

/** Freshness legível da coleta: null quando nunca houve fotografia válida. */
export function freshnessOf(p: IdentityEvidenceProjection | null): string | null {
  return p?.collectedAt ?? null;
}
