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
  | 'DeviceGovernance'
  // [AEGIS-KNIGHT-COVERAGE-02] Colaboração e comunicação (Microsoft Teams): com quem se fala, quem entra numa
  // reunião, o que sai da organização por um canal de conversa.
  | 'CollaborationSecurity'
  // [AEGIS-KNIGHT-COVERAGE-04] Proteção contra ameaças (Defender), proteção de dados (Purview, compartilhamento) e
  // infraestrutura de nuvem (Azure).
  | 'ThreatProtection'
  | 'DataProtection'
  | 'CloudInfrastructure';

/** Estado de conexão exibido no badge: separação inequívoca entre Demo, Não configurado e Conectado. */
export type KnightConnectionState = 'Demo' | 'NotConfigured' | 'Connected';

/** Fonte concreta de coleta (multicoletor). */
export type KnightSourceType =
  | 'Demo'
  | 'MicrosoftEntraId'
  | 'MicrosoftTeams'
  | 'MicrosoftExchangeOnline'
  // [AEGIS-KNIGHT-COVERAGE-04] Demais serviços do Microsoft 365 e o Azure — mesma credencial do conector Microsoft.
  | 'MicrosoftDefenderForOffice365'
  | 'MicrosoftPurview'
  | 'MicrosoftSharePoint'
  | 'MicrosoftIntune'
  | 'MicrosoftFabric'
  | 'MicrosoftAzure'
  | 'GoogleWorkspace'
  /**
   * [AEGIS-KNIGHT-CONSOLIDATED-01] NÃO é uma fonte de coleta: marca uma avaliação/relatório que COMPÕE, sem
   * somar, avaliações concluídas de várias fontes reais (as fontes Microsoft elegíveis do catálogo). Cada indicador
   * combinado preserva o próprio `sourceType` real.
   */
  | 'Consolidated';

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
  /** [AEGIS-KNIGHT-CLOSURE-01] Requisito do que o AEGIS de fato chama (o mesmo texto das exportações). Ausente em respostas antigas. */
  requirement?: string | null;
  /** [AEGIS-KNIGHT-CLOSURE-01] Versão preview/beta lida por esta capacidade, quando houver. */
  previewApi?: string | null;
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
  /**
   * [AEGIS-KNIGHT-CONSOLIDATED-01] Presente SÓ quando `sourceType === 'Consolidated'`: a composição das três
   * fontes candidatas (as nove fontes Microsoft elegíveis) — incluídas, disponíveis mas não escolhidas, ou nunca
   * avaliadas. Ausente em toda avaliação de fonte única.
   */
  sources?: KnightConsolidatedSource[];
}

/**
 * [AEGIS-KNIGHT-CONSOLIDATED-01] Composição de UMA fonte candidata no relatório consolidado (espelha
 * KnightConsolidatedSourceDto). Nota e cobertura são SEMPRE as da própria fonte — nunca somadas às demais.
 */
export interface KnightConsolidatedSource {
  source: KnightSourceType;
  slug: string;
  label: string;
  included: boolean;
  /** "Included" | "Available" (avaliação concluída, não escolhida) | "NotAssessed" (nunca concluiu uma avaliação). */
  availabilityState: 'Included' | 'Available' | 'NotAssessed';
  sourceRunId: string | null;
  sourceState: KnightSourceState | null;
  catalogVersion: string | null;
  capturedAt: string | null; // ISO 8601
  score: number | null;
  coverage: number | null;
  counts: KnightCounts | null;
  collectionLimitations: string[];
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
 * [AEGIS-KNIGHT-COVERAGE-02] A última avaliação de UMA fonte (espelha KnightSourceLatestDto). Com mais de uma
 * fonte avaliada, "a última avaliação" deixou de ter resposta única: mostrar só a sincronização mais recente
 * esconderia a avaliação da outra fonte. Cada bloco traz a própria nota, cobertura e data — nunca somadas.
 */
export interface KnightSourceLatest {
  source: KnightSourceType;
  slug: string;
  label: string;
  assessment: KnightAssessment | null;
  unfinishedAttempt: KnightUnfinishedRun | null;
}

/** Resposta de `GET /latest-by-source`: um bloco por fonte que já produziu alguma avaliação. */
export interface KnightLatestBySource {
  sources: KnightSourceLatest[];
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
  CollaborationSecurity: 'Colaboração e comunicação',
  ThreatProtection: 'Proteção contra ameaças',
  DataProtection: 'Proteção de dados',
  CloudInfrastructure: 'Infraestrutura de nuvem',
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

/**
 * [AEGIS-KNIGHT-COVERAGE-04] Catálogo ÚNICO das fontes do KNIGHT na tela — espelho de `KnightSourceCatalog` no
 * servidor (mesma ordem, rótulo e apelido de rota). Rótulos, apelidos, candidatas do consolidado e fontes de cada
 * conector derivam daqui: uma fonte nova entra em um lugar só, e nenhuma lista paralela fica para trás.
 */
export interface KnightSourceDescriptor {
  source: Exclude<KnightSourceType, 'Consolidated'>;
  label: string;
  slug: string;
  provider: 'Microsoft' | 'Google' | 'Demonstração';
  /** Alimentada pelo conector Microsoft (mesma aplicação registrada). */
  microsoftConnector: boolean;
  /** Pode compor o relatório consolidado. */
  consolidable: boolean;
  /** O que a coleta desta fonte exige além da credencial comum (null quando nada além). */
  requirement: string | null;
}

export const KNIGHT_SOURCES: KnightSourceDescriptor[] = [
  { source: 'MicrosoftEntraId', label: 'Microsoft Entra ID', slug: 'entra', provider: 'Microsoft', microsoftConnector: true, consolidable: true, requirement: null },
  {
    source: 'MicrosoftTeams', label: 'Microsoft Teams', slug: 'teams', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'Exige, além da credencial acima, o papel Leitor do Teams (ou Leitor Global) atribuído a esta aplicação no Microsoft Entra ID.',
  },
  {
    source: 'MicrosoftExchangeOnline', label: 'Exchange Online', slug: 'exchange', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'Exige Exchange.ManageAsApp (API Office 365 Exchange Online) e o papel Leitor Global atribuído à aplicação.',
  },
  {
    source: 'MicrosoftDefenderForOffice365', label: 'Microsoft Defender para Office 365', slug: 'defender-office365', provider: 'Microsoft',
    microsoftConnector: true, consolidable: true,
    requirement: 'Usa a mesma sessão do Exchange Online. SPF e DMARC são lidos no DNS público. Parte das políticas exige licença do Defender para Office 365.',
  },
  {
    source: 'MicrosoftPurview', label: 'Microsoft Purview', slug: 'purview', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'DLP e rótulos exigem Exchange.ManageAsApp na API Microsoft Exchange Online Protection (sessão do Security & Compliance).',
  },
  {
    source: 'MicrosoftSharePoint', label: 'SharePoint e OneDrive', slug: 'sharepoint', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'SharePointTenantSettings.Read.All; as configurações administrativas exigem certificado e Sites.FullControl.All (API SharePoint).',
  },
  {
    source: 'MicrosoftIntune', label: 'Microsoft Intune', slug: 'intune', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'DeviceManagementConfiguration.Read.All e DeviceManagementServiceConfig.Read.All.',
  },
  {
    source: 'MicrosoftFabric', label: 'Microsoft Fabric (Power BI)', slug: 'fabric', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'Um administrador do Fabric precisa permitir que entidades de serviço usem as APIs de administração somente leitura, para um grupo que contenha a aplicação.',
  },
  {
    source: 'MicrosoftAzure', label: 'Microsoft Azure', slug: 'azure', provider: 'Microsoft', microsoftConnector: true, consolidable: true,
    requirement: 'Exige o papel Leitor do Azure RBAC nas assinaturas do escopo (ou no grupo de gerenciamento). Certificados dos cofres exigem papel no plano de dados (Leitor do Key Vault).',
  },
  { source: 'GoogleWorkspace', label: 'Google Workspace', slug: 'google', provider: 'Google', microsoftConnector: false, consolidable: false, requirement: null },
  { source: 'Demo', label: 'Demonstração', slug: 'demo', provider: 'Demonstração', microsoftConnector: false, consolidable: false, requirement: null },
];

/**
 * [AEGIS-KNIGHT-COVERAGE-04] Execução que produziu um achado. Numa avaliação de fonte única, é ela mesma. No relatório
 * consolidado (composição sem identificador próprio), é a execução da FONTE do indicador que entrou na composição —
 * é por ela que se leem afetados e evidências e é ela a origem de um plano de ação, o mesmo da visão por fonte.
 */
export function findingRunIdOf(
  assessment: { id: string; sourceType: KnightSourceType; sources?: { source: KnightSourceType; included: boolean; sourceRunId: string | null }[] },
  indicatorSource: KnightSourceType,
): string {
  if (assessment.sourceType !== 'Consolidated') return assessment.id;
  return assessment.sources?.find((s) => s.source === indicatorSource && s.included && !!s.sourceRunId)?.sourceRunId ?? assessment.id;
}

export function describeSource(source: KnightSourceType): KnightSourceDescriptor | undefined {
  return KNIGHT_SOURCES.find((d) => d.source === source);
}

/** Candidatas do relatório consolidado, na ordem de apresentação. */
export const CONSOLIDATION_CANDIDATES: KnightSourceType[] = KNIGHT_SOURCES.filter((d) => d.consolidable).map((d) => d.source);

/** Apelido de rota da fonte ("Consolidated" nunca vai na URL de uma fonte). */
export function sourceSlug(source: KnightSourceType): string {
  return source === 'Consolidated' ? 'consolidated' : describeSource(source)?.slug ?? source.toLowerCase();
}

export function sourceTypeLabel(source: KnightSourceType): string {
  return source === 'Consolidated' ? 'Consolidado' : describeSource(source)?.label ?? source;
}

const SOURCE_STATE_LABEL: Record<KnightSourceState, string> = {
  NotConfigured: 'Não configurado',
  Configured: 'Configurado',
  Collecting: 'Coletando',
  Completed: 'Coleta concluída',
  PartialCollection: 'Coleta parcial',
  InsufficientPermission: 'Permissão insuficiente',
  AuthenticationFailure: 'Falha de autenticação',
  Throttled: 'Limite de requisições',
  Unavailable: 'Indisponível',
  Error: 'Erro',
};

export function sourceStateLabel(state: KnightSourceState): string {
  return SOURCE_STATE_LABEL[state];
}

/**
 * [AEGIS-KNIGHT-COVERAGE-04] Completude da COLETA de um consolidado, separada do término da execução: a composição
 * sempre termina, mas só é coleta completa quando todas as fontes incluídas foram coletadas sem limitação. Mesma
 * regra do backend (KnightConsolidatedCollection), para tela, HTML e PDF dizerem a mesma coisa.
 */
export function consolidatedCollection(sources: KnightConsolidatedSource[]): { complete: boolean; incomplete: string[]; text: string } {
  const included = sources.filter((s) => s.included);
  const incomplete = included
    .filter((s) => s.sourceState !== 'Completed' || (s.collectionLimitations?.length ?? 0) > 0)
    .map((s) => s.label);
  const text = incomplete.length === 0
    ? `Coleta completa nas ${included.length} fontes incluídas.`
    : `Coleta parcial em ${incomplete.length} de ${included.length} fontes incluídas (${incomplete.join(', ')}). ` +
      'A composição terminou; os controles sem dado dessas fontes ficam não avaliados e as limitações de cada uma estão na composição.';
  return { complete: incomplete.length === 0 && included.length > 0, incomplete, text };
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
  Throttled: 'Limite de requisições',
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
  // [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams — os mesmos nomes do relatório exportado.
  TeamsClientConfiguration: 'Configuração do cliente do Teams',
  TeamsFederationConfiguration: 'Federação e acesso externo do Teams',
  TeamsMeetingPolicies: 'Políticas de reunião do Teams',
  TeamsMessagingPolicies: 'Políticas de mensagens do Teams',
  TeamsAppPermissionPolicies: 'Políticas de permissão de aplicativos do Teams',
  TeamsPolicyAssignments: 'Atribuições de política do Teams a grupos',
  // [AEGIS-KNIGHT-COVERAGE-03] Exchange Online — idem.
  ExchangeOrganizationConfig: 'Configuração da organização do Exchange',
  ExchangeTransportConfig: 'Configuração de transporte do Exchange',
  ExchangeSharingPolicies: 'Políticas de compartilhamento',
  ExchangeOwaMailboxPolicies: 'Políticas do Outlook na web',
  ExchangeTransportRules: 'Regras de transporte (fluxo de emails)',
  ExchangeRoleAssignmentPolicies: 'Políticas de atribuição de função ao usuário final',
  ExchangeExternalSenderIdentification: 'Identificação de remetentes externos',
  ExchangeOutboundSpamFilterPolicies: 'Políticas de filtro de spam de saída',
  ExchangeMailboxes: 'Caixas de correio',
  ExchangeMailboxSignIn: 'Estado de entrada das contas',
  ExchangeCasMailboxes: 'Acesso de cliente por caixa de correio',
  ExchangeAuditBypassAssociations: 'Desvios de auditoria de caixa de correio',
  // [AEGIS-KNIGHT-COVERAGE-04] Defender para Office 365, Purview, SharePoint/OneDrive, Intune e Fabric — idem.
  DefenderAtpPolicy: 'Anexos Seguros para SharePoint, OneDrive e Teams',
  DefenderSafeLinks: 'Políticas e regras de Links Seguros',
  DefenderSafeAttachments: 'Políticas e regras de Anexos Seguros',
  DefenderMalwareFilter: 'Políticas e regras antimalware',
  DefenderInboundSpam: 'Políticas e regras antispam de entrada',
  DefenderConnectionFilter: 'Filtro de conexão',
  DefenderOutboundSpam: 'Políticas e regras antispam de saída',
  DefenderAntiPhish: 'Políticas e regras antiphishing',
  DefenderDkim: 'Assinatura DKIM por domínio',
  DefenderAcceptedDomains: 'Domínios aceitos',
  DefenderDnsRecords: 'Registros SPF e DMARC (consulta DNS)',
  DefenderTeamsProtection: 'Proteção do Teams (ZAP)',
  DefenderPriorityAccounts: 'Contas prioritárias',
  DefenderPresetPolicies: 'Políticas de segurança predefinidas',
  PurviewAuditConfig: 'Configuração do log de auditoria unificado',
  PurviewDlpPolicies: 'Políticas de prevenção contra perda de dados (DLP)',
  PurviewLabelPolicies: 'Políticas de rótulos de confidencialidade',
  SharePointTenantSettings: 'Configurações do SharePoint (Microsoft Graph)',
  SharePointAdminTenant: 'Configurações administrativas do SharePoint e OneDrive',
  IntuneServiceSettings: 'Configurações de conformidade do Intune',
  IntuneEnrollmentRestrictions: 'Restrições de registro de dispositivos',
  FabricTenantSettings: 'Configurações do locatário do Fabric',
  AzureSubscriptions: 'Assinaturas do Azure no escopo',
  AzureAuthorization: 'Atribuições de papel, papéis personalizados e bloqueios',
  AzurePolicy: 'Atribuições de política do Azure',
  AzureDefenderForCloud: 'Planos e configurações do Defender para Nuvem',
  AzureMonitor: 'Alertas do log de atividades e Application Insights',
  AzureNetworking: 'Rede do Azure (NSGs, redes virtuais, gateways, logs de fluxo)',
  AzureStorage: 'Contas de armazenamento e serviços de blob e arquivos',
  AzureKeyVault: 'Cofres de chaves, chaves e segredos (metadados)',
  AzureKeyVaultCertificates: 'Políticas de certificado dos cofres (plano de dados)',
  AzureCompute: 'Máquinas virtuais, discos, contêineres e Batch',
  AzureAppService: 'App Service, Functions, slots e ambientes',
  AzureDatabases: 'Bancos de dados (SQL, PostgreSQL, MySQL, Cosmos DB, Redis, Data Factory)',
  AzureDatabricks: 'Workspaces do Azure Databricks',
  AzureTenantDiagnostics: 'Configurações de diagnóstico do Microsoft Entra ID',
  // [AEGIS-KNIGHT-CLOSURE-01] Leituras em versão preview, acesso adicional e complementos — idem.
  AzureResourceDiagnostics: 'Configurações de diagnóstico dos recursos do Azure (versão preview)',
  AzureActivityLogExport: 'Exportação do log de atividades das assinaturas (versão preview)',
  AzureSecurityContacts: 'Contatos e notificações do Defender para Nuvem (versão preview)',
  AzureAppSettingsKeyVaultReferences: 'Referências ao Key Vault nas configurações do App Service',
  AzureDatabricksWorkspaceApi: 'API dos workspaces do Azure Databricks',
  PerUserMfaStates: 'MFA por usuário (legado) (versão beta do Microsoft Graph)',
  AuthenticationMethodsPolicyPreview: 'Métodos de autenticação: campos da versão beta do Microsoft Graph',
  M365AppsAndServicesSettings: 'Aplicativos e serviços próprios dos usuários (versão beta do Microsoft Graph)',
  M365FormsSettings: 'Configurações do Microsoft Forms (versão beta do Microsoft Graph)',
  ActivityBasedTimeoutPolicy: 'Tempo limite de sessão ociosa do Microsoft 365',
  DefenderReportSubmissionPolicy: 'Destino das mensagens denunciadas pelos usuários',
};

/** Capacidade desconhecida degrada para o próprio identificador — nunca some da tela. */
export function capabilityLabel(capability: string): string {
  return CAPABILITY_LABEL[capability] ?? capability;
}

/**
 * [AEGIS-KNIGHT-COVERAGE-04] Limitação congelada na composição ("Capacidade: Desfecho — detalhe", identificadores) em
 * texto legível — os mesmos rótulos da tabela de limitações; o detalhe fica como foi registrado.
 */
export function readableLimitation(raw: string): string {
  const m = /^([A-Za-z0-9]+): ([A-Za-z]+)( — .*)?$/s.exec(raw);
  if (!m || !(m[2] in CAPABILITY_OUTCOME_LABEL)) return raw;
  return `${capabilityLabel(m[1])}: ${capabilityOutcomeLabel(m[2] as KnightCapabilityOutcome)}${m[3] ?? ''}`;
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
  | 'Domain'
  | 'CloudResource';

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
  CloudResource: 'Recurso de nuvem',
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
  /** [AEGIS-KNIGHT-CLOSURE-01] Versões preview/beta das leituras que sustentam o controle (vazio = só versões estáveis). */
  previewApis?: string[];
}

/* ---- [AEGIS-KNIGHT-COVERAGE-01] Cobertura do catálogo de referência (propriedade do produto) ---------------- */

/**
 * ApiLimitation = nenhum método publicado (nem estável nem preview); PreviewOnly = a leitura existe só em versão beta/preview e
 * ainda não foi implementada (no catálogo atual, nenhuma: as leituras preview são usadas e identificadas em cada controle).
 */
export type KnightReferenceDisposition = 'Implemented' | 'Partial' | 'Pending' | 'ManualOnly' | 'RequiresAccess' | 'ApiLimitation' | 'PreviewOnly';

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
  /** Leitura existente só em versão beta/preview ainda não implementada. Ausente em respostas antigas. */
  previewOnly?: number;
  /** [AEGIS-KNIGHT-CLOSURE-01] Das avaliadas (integral + parcial), quantas dependem de leitura em versão preview. */
  previewBacked?: number;
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
  /** [AEGIS-KNIGHT-CLOSURE-01] Versões preview das leituras que sustentam a avaliação (vazio = só estáveis). */
  previewApis?: string[];
  /** [AEGIS-KNIGHT-CLOSURE-01] Sem avaliação automatizada: aceita resultado de verificação manual. */
  manualEligible?: boolean;
  /** [AEGIS-KNIGHT-CLOSURE-01] Resultado manual VIGENTE deste cliente — à parte da avaliação automatizada. */
  manualResult?: KnightManualResult | null;
}

/* ---- [AEGIS-KNIGHT-CLOSURE-01] Resultado de verificação manual (atestação) ------------------------------------- */

export type KnightManualResultValue = 'Compliant' | 'NonCompliant' | 'NotApplicable' | 'Withdrawn';

/** Um resultado manual registrado (espelha KnightManualResultDto). Nunca entra na nota nem na cobertura automatizada. */
export interface KnightManualResult {
  id: string;
  referenceKey: string;
  result: KnightManualResultValue;
  resultLabel: string;
  justification: string;
  responsibleName: string;
  evidenceReference: string | null;
  evidenceDocumentId: string | null;
  evidenceDocumentTitle: string | null;
  evidenceDocumentSha256: string | null;
  validUntil: string | null;
  expired: boolean;
  referenceDisposition: string;
  catalogVersion: string;
  recordedByName: string;
  recordedAt: string;
}

/** Pedido de registro: o autor vem do token, nunca do corpo. */
export interface RecordKnightManualResultRequest {
  referenceKey: string;
  result: KnightManualResultValue;
  justification: string;
  responsibleName: string;
  evidenceReference: string | null;
  evidenceDocumentId: string | null;
  validUntil: string | null;
}

export const MANUAL_RESULT_OPTIONS: { value: KnightManualResultValue; label: string }[] = [
  { value: 'Compliant', label: 'Conforme' },
  { value: 'NonCompliant', label: 'Não conforme' },
  { value: 'NotApplicable', label: 'Não se aplica' },
];

/** Mínimo de caracteres da justificativa (o servidor é a autoridade; a tela só antecipa o aviso). */
export const MANUAL_JUSTIFICATION_MIN = 10;

/**
 * Validação de borda do formulário de resultado manual — o que falta, em palavras. Lista vazia = pode enviar. Retirar
 * dispensa evidência (mas não justificativa nem responsável).
 */
export function manualResultProblems(r: RecordKnightManualResultRequest, today: string): string[] {
  const out: string[] = [];
  if ((r.justification ?? '').trim().length < MANUAL_JUSTIFICATION_MIN)
    out.push(`Justificativa com ao menos ${MANUAL_JUSTIFICATION_MIN} caracteres: o que foi verificado e como.`);
  if (!(r.responsibleName ?? '').trim()) out.push('Responsável pelo resultado.');
  if (r.result !== 'Withdrawn' && !(r.evidenceReference ?? '').trim() && !r.evidenceDocumentId)
    out.push('Evidência: uma referência (chamado, registro) ou um documento da Central de Evidências.');
  if (r.validUntil && r.validUntil < today) out.push('A validade não pode estar no passado.');
  return out;
}

/** Texto curto do resultado manual vigente para a lista (com "vencido" quando a validade passou). */
export function manualResultBadge(m: KnightManualResult | null | undefined): string | null {
  if (!m) return null;
  return m.expired ? `${m.resultLabel} — vencido` : m.resultLabel;
}

/** Recorte da aba de cobertura: sem avaliação automatizada (aceita manual), avaliadas, com preview, com resultado manual, todas. */
export type KnightReferenceView = 'manual' | 'automated' | 'preview' | 'withManual' | 'all';

export interface KnightReferenceFilter {
  view: KnightReferenceView;
  platform: string;
  q: string;
}

/** "CIS Microsoft 365 7.0.0 · 5.1.2.4 (E5)" — benchmark, versão, seção e variante, como o relatório cita. */
export function referenceLabel(c: KnightReferenceControlStatus): string {
  const head = [c.framework, c.version].filter((x) => !!x).join(' ');
  const section = c.section ? ` · ${c.section}` : '';
  return `${head}${section}${c.variant ? ` (${c.variant})` : ''}`;
}

/** Os controles de referência do recorte pedido, na ordem estável da chave. */
export function filterReferenceControls(controls: KnightReferenceControlStatus[], f: KnightReferenceFilter): KnightReferenceControlStatus[] {
  const q = f.q.trim().toLowerCase();
  return controls
    .filter((c) => !f.platform || c.platform === f.platform)
    .filter((c) => {
      switch (f.view) {
        case 'manual': return !!c.manualEligible;
        case 'automated': return c.disposition === 'Implemented' || c.disposition === 'Partial';
        case 'preview': return (c.previewApis ?? []).length > 0;
        case 'withManual': return !!c.manualResult;
        default: return true;
      }
    })
    .filter((c) => !q || `${referenceLabel(c)} ${c.title} ${c.serviceLabel} ${c.indicatorIds.join(' ')} ${c.note ?? ''}`.toLowerCase().includes(q));
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
  /** [AEGIS-KNIGHT-PRESENTATION-01] Tipo específico calculado no servidor (ex.: "Conta de armazenamento"). */
  kindLabel?: string | null;
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
  const provider = describeSource(i.sourceType)?.provider ?? 'Demonstração';
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
  /** [AEGIS-KNIGHT-CLOSURE-01] Requisito do que o AEGIS chama (do servidor) e versão preview lida, quando houver. */
  requirement: string | null;
  previewApi: string | null;
}

const CAUSE: Record<string, [string, string]> = {
  InsufficientPermission: [
    // [AEGIS-KNIGHT-COVERAGE-03] O rótulo diz o que foi OBSERVADO, não o que falta. Uma recusa de autorização
    // é compatível com consentimento ausente, papel sem alcance, domínio errado e método de autenticação não
    // aceito — e no Exchange Online a permissão sozinha nunca autoriza comando algum. "Permissão ausente"
    // elegia uma dessas causas e mandava o operador consertar o que talvez já estivesse certo.
    'Autorização recusada',
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
    const [cause, generic] = CAUSE[c.outcome] ?? ['Não coletado', 'Sincronizar novamente em Integrações.'];
    // [AEGIS-KNIGHT-COVERAGE-04] No Azure a autorização é uma atribuição de papel no escopo recusado, não consentimento.
    // [AEGIS-KNIGHT-CLOSURE-01] Databricks desligado: a ação é habilitar em Integrações (e o acesso dentro do workspace).
    // Versão preview fora do contrato: não há o que conceder — a leitura fica limitada até a Microsoft estabilizá-la.
    const guidance = c.capability === 'AzureDatabricksWorkspaceApi' && c.outcome === 'NotAttempted'
      ? 'Habilitar a leitura da API dos workspaces em Configurações → Integrações (AEGIS KNIGHT) e adicionar a aplicação a cada workspace como administradora; depois sincronizar novamente.'
      : c.capability === 'AzureDatabricksWorkspaceApi' && (c.outcome === 'InsufficientPermission' || c.outcome === 'AuthenticationFailure')
        ? 'Adicionar a aplicação ao workspace do Databricks como entidade de serviço administradora (acesso concedido dentro do Databricks) e sincronizar novamente.'
        : c.outcome === 'Error' && c.previewApi
          ? 'A resposta da versão preview saiu do contrato documentado: os controles que dependem dela ficam não avaliados até a leitura ser revista. Nada a conceder.'
          : c.outcome === 'InsufficientPermission' && c.capability.startsWith('Azure')
            ? 'Atribuir à aplicação o papel indicado no escopo recusado (Azure RBAC na assinatura, ou no cofre) e sincronizar novamente em Integrações.'
            : generic;
    return {
      capability: c.capability,
      label: capabilityLabel(c.capability),
      cause,
      detail: c.detail,
      affectedControls: a.indicators
        .filter((i) => (i.status === 'NotEvaluated' || i.status === 'Error') && (i.presentation?.requiredCapabilities ?? []).includes(c.capability))
        .map((i) => i.indicatorId),
      guidance,
      requirement: c.requirement ?? null,
      previewApi: c.previewApi ?? null,
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

// ---- [AEGIS-KNIGHT-PRESENTATION-01] Glossário e controles citados ---------------------------------------

/** Um termo técnico do glossário ÚNICO do servidor (o mesmo do HTML e do PDF). */
export interface KnightGlossaryTerm {
  term: string;
  meaning: string;
  explanation: string;
}

export interface KnightGlossary {
  terms: KnightGlossaryTerm[];
  identifierExplanation: string;
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Siglas do glossário que aparecem nos textos — a MESMA regra do servidor (KnightGlossary.UsedIn): palavra inteira,
 * sensível a maiúsculas, com plural em "s"; hífen conta como parte da palavra ("AK-AZ-IAM-004" não é "IAM").
 */
export function glossaryTermsIn(texts: (string | null | undefined)[], terms: KnightGlossaryTerm[]): KnightGlossaryTerm[] {
  const all = texts.filter((t): t is string => !!t && !!t.trim()).join('\n');
  if (!all) return [];
  return terms.filter((t) => new RegExp(`(?<![\\p{L}\\p{N}_-])${escapeRegExp(t.term)}s?(?![\\p{L}\\p{N}_-])`, 'u').test(all));
}

/** Os textos de UM controle em que as siglas são procuradas (os mesmos campos que o servidor usa). */
export function indicatorTexts(i: KnightIndicator): (string | null | undefined)[] {
  const p = i.presentation;
  const ax = axesOf(i);
  return [
    findingTitle(i), i.title, p?.description, p?.rationale, p?.impact, p?.expectedConfiguration, p?.doesNotProve, p?.criterion,
    i.recommendation, i.notEvaluatedReason, i.evidence, ax.service, ax.domainLabel, ...frameworksOf(i),
  ];
}

/** Controle citado fora do próprio detalhe: código e título DESTA avaliação; `null` quando ela não tem o controle. */
export interface KnightControlRef {
  id: string;
  title: string | null;
}

export function controlRefs(ids: string[], a: KnightAssessment): KnightControlRef[] {
  const byId = new Map(a.indicators.map((i) => [i.indicatorId, i]));
  return ids.map((id) => {
    const i = byId.get(id);
    return { id, title: i ? findingTitle(i) || i.title || null : null };
  });
}
