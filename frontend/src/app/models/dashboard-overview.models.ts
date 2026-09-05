// [AEGIS-MVP-PRODUCT-01] Espelha AegisScore.Application.Queries.DashboardOverviewDto — a leitura COMPOSTA
// da tela inicial (GET /api/v1/dashboard/overview).
//
// A regra que este contrato carrega: cada dimensão tem ESTADO PRÓPRIO. A tela não pode decidir "tem postura?"
// por uma dimensão só — era exatamente isso que fazia um ambiente com telemetria real, porém sem assessment
// de maturidade, aparecer como "Nenhuma postura medida". Aqui, cada painel só aparece com números se a SUA
// dimensão tiver evidência; e valor `null` significa "não houve leitura", nunca zero.
//
// O frontend NÃO recalcula score, cobertura, contagem, gap ou criticidade — tudo vem apurado do backend.

import { PostureExposureItem, PostureExposureSummary } from './posture-exposure.models';
import { VulnerabilityGroup, VulnerabilitySummary } from './vulnerability.models';
import { ConnectorHealthSummary, EvidenceCoverageSummary, WorkspaceOverall, WorkspacePosture } from './workspace.models';

/** Estado de UMA dimensão — decide se o painel mostra número, estado vazio, parcialidade ou "sem fonte". */
export type DashboardSignalState = 'NoSource' | 'NeverCollected' | 'Partial' | 'Available';

/** Uma métrica com a PROVENIÊNCIA junto. `value` é nulo em `NoSource`/`NeverCollected` — jamais 0 por ausência. */
export interface DashboardMetric {
  state: DashboardSignalState;
  value: number | null;
  sourceLabel: string;
  observedAt: string | null;
  note: string | null;
}

/** O que já foi OBSERVADO no ambiente. Métricas independentes: uma vazia não apaga as demais. */
export interface DashboardEnvironment {
  assets: DashboardMetric;
  configurationExposures: DashboardMetric;
  vulnerabilities: DashboardMetric;
  affectedAssets: DashboardMetric;
  identity: DashboardMetric;
}

/**
 * Risco de NEGÓCIO — maturidade CMMI, registro de riscos e ICR. Vem de avaliação assistida, não de telemetria;
 * por isso é um bloco separado, com estado próprio, que NÃO pode esconder o ambiente observado.
 */
export interface DashboardBusinessRisk {
  maturityState: DashboardSignalState;
  overallMaturity: number | null;
  targetMaturity: number | null;
  evaluatedSubcategories: number;
  icrState: DashboardSignalState;
  icrScore: number | null;
  icrBand: string | null;
  riskRegisterState: DashboardSignalState;
  risksEvaluated: number;
  criticalProcessesExposed: number | null;
  overdueActionPlans: number | null;
}

/** Uma capacidade de identidade que a fonte NÃO entregou — com o motivo real, nunca "sem dados". */
export interface DashboardIdentityGap {
  capability: string;
  outcome: string;
  detail: string | null;
}

/** Postura consultiva de identidade do último snapshot da Evidence Fabric (sem nova coleta). */
export interface DashboardIdentity {
  state: DashboardSignalState;
  collectionState: string;
  sourceLabel: string;
  collectedAt: string | null;
  isDegraded: boolean;
  capabilitiesCollected: string[];
  capabilitiesMissing: DashboardIdentityGap[];
  controlsAwaitingEvidence: number;
}

/** Uma fonte conectada, já com a idade da última leitura apurada pelo servidor. */
export interface DashboardSource {
  id: string;
  displayName: string;
  provider: string;
  capability: string;
  status: string;
  enabled: boolean;
  everSynced: boolean;
  lastSyncAt: string | null;
  staleDays: number | null;
}

/** Saúde das fontes: contadores VERBATIM da autoridade + a lista já ordenada por gravidade. */
export interface DashboardSources {
  configured: number;
  enabled: number;
  disabled: number;
  healthy: number;
  degraded: number;
  failed: number;
  neverSynced: number;
  attention: number;
  lastSyncAt: string | null;
  items: DashboardSource[];
}

export interface DashboardExposureQueue {
  summary: PostureExposureSummary;
  top: PostureExposureItem[];
}

export interface DashboardVulnerabilityQueue {
  summary: VulnerabilitySummary;
  top: VulnerabilityGroup[];
}

export interface DashboardOverview {
  readModelVersion: string;
  generatedAt: string;
  clientName: string;
  posture: WorkspaceOverall;
  evidenceCoverage: EvidenceCoverageSummary;
  environment: DashboardEnvironment;
  businessRisk: DashboardBusinessRisk;
  configurationExposures: DashboardExposureQueue;
  vulnerabilities: DashboardVulnerabilityQueue;
  identity: DashboardIdentity;
  sources: DashboardSources;
}

/**
 * A dimensão tem número para mostrar? Só `Available` e `Partial` têm — e mesmo em `Partial` o número é uma
 * leitura INCOMPLETA, que a tela precisa rotular como tal. Função pura para o template não repetir a regra.
 */
export function hasReading(m: DashboardMetric | DashboardIdentity): boolean {
  return m.state === 'Available' || m.state === 'Partial';
}

/** Rótulo curto do estado, para a etiqueta ao lado do número (ou no lugar dele). */
export function stateLabel(state: DashboardSignalState): string {
  switch (state) {
    case 'Available':
      return 'Leitura atual';
    case 'Partial':
      return 'Leitura parcial';
    case 'NeverCollected':
      return 'Ainda não coletado';
    default:
      return 'Sem fonte conectada';
  }
}

/** Motivo, em linguagem operacional, de uma capacidade de identidade não ter sido entregue pela fonte. */
export function identityOutcomeLabel(outcome: string): string {
  switch (outcome) {
    case 'InsufficientPermission':
      return 'Permissão ainda não concedida';
    case 'InsufficientLicense':
      return 'Licença do ambiente não habilita';
    case 'Throttled':
      return 'Fonte limitou o volume de consulta';
    case 'AuthenticationFailure':
      return 'Falha de autenticação na fonte';
    case 'Unavailable':
      return 'Fonte indisponível no momento';
    case 'NotAttempted':
      return 'Não consultado nesta coleta';
    default:
      return 'Não entregue pela fonte';
  }
}

/** Nome legível de uma capacidade de identidade — o cliente não deve ler identificador técnico. */
export function identityCapabilityLabel(capability: string): string {
  switch (capability) {
    case 'PrivilegedRoleInventory':
      return 'Contas com privilégio administrativo';
    case 'MfaRegistration':
      return 'Registro de múltiplo fator';
    case 'GuestAccounts':
      return 'Contas de convidado';
    case 'ConditionalAccessPolicies':
      return 'Políticas de acesso condicional';
    case 'ApplicationInventory':
      return 'Credenciais de aplicações';
    case 'ServiceAccountExemptions':
      return 'Exceções de contas de serviço';
    case 'SecurityBaseline':
      return 'Configuração de segurança padrão';
    case 'BreakGlassDesignation':
      return 'Contas de emergência';
    case 'IdentityRiskDetections':
      return 'Detecções de risco de identidade';
    case 'RiskyUsers':
      return 'Usuários sinalizados como de risco';
    case 'AuthenticationMethods':
      return 'Métodos de autenticação registrados';
    default:
      return capability;
  }
}

/**
 * Reconstrói a projeção do workspace a partir da leitura composta — para os componentes que já consomem
 * `WorkspacePosture` (como o bloco environment-first) funcionarem SEM uma segunda requisição ao
 * `/scoring/workspace`. Os campos vêm todos da mesma resposta: `overall`, `evidenceCoverage` e a saúde das
 * fontes (que é a MESMA projeção de conectores, apenas reordenada e com a idade da leitura).
 *
 * `functions` fica vazio de propósito: a leitura composta não transporta a postura por Função NIST, e os
 * consumidores desta adaptação não a usam. Inventar entradas zeradas aqui seria exatamente o defeito que
 * este pacote corrige — ausência de dado não pode virar zero.
 */
export function workspaceFromOverview(o: DashboardOverview): WorkspacePosture {
  const connectors: ConnectorHealthSummary = {
    configured: o.sources.configured,
    enabled: o.sources.enabled,
    disabled: o.sources.disabled,
    healthy: o.sources.healthy,
    degraded: o.sources.degraded,
    failed: o.sources.failed,
    neverSynced: o.sources.neverSynced,
    lastSyncAt: o.sources.lastSyncAt,
    items: o.sources.items.map((s) => ({
      id: s.id,
      displayName: s.displayName,
      provider: s.provider,
      capability: s.capability,
      status: s.status,
      lastSyncAt: s.lastSyncAt,
      everSynced: s.everSynced,
      enabled: s.enabled,
    })),
  };

  return {
    overall: o.posture,
    functions: [],
    connectors,
    evidenceCoverage: o.evidenceCoverage,
  };
}
