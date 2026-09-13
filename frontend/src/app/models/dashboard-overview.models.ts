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
import {
  DevicePriorityBand,
  DevicePriorityCount,
  DevicePriorityDisposition,
  bandCountsText,
  disposedCasesText,
} from './device-priority.models';
import { VulnerabilityGroup, VulnerabilitySummary } from './vulnerability.models';
import { ConnectorHealthSummary, EvidenceCoverageSummary, WorkspaceOverall, WorkspacePosture } from './workspace.models';

/** Estado de UMA dimensão — decide se o painel mostra número, estado vazio, parcialidade ou "sem fonte". */
export type DashboardSignalState = 'NoSource' | 'NeverCollected' | 'Partial' | 'Available' | 'Undetermined';

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

/** [AEGIS-JOURNEY-01] UM dispositivo do resumo — aponta o caso determinante e o plano ativo desse caso, se houver. */
export interface DashboardDevicePriorityItem {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  position: number;
  band: DevicePriorityBand;
  bandLabel: string;
  cveId: string | null;
  caseBandLabel: string | null;
  activePlanId: string | null;
}

/** [AEGIS-JOURNEY-01] Planos de casos de dispositivo do tenant (unidade: planos) — contagem completa. */
export interface DashboardDevicePlans {
  active: number;
  awaitingValidation: number;
  overdue: number;
  completed: number;
}

/**
 * [AEGIS-JOURNEY-01] Resumo da prioridade de tratamento em dispositivos — a MESMA leitura da Central. Três unidades que
 * a tela nunca soma: dispositivos (`assetsByBand`), casos dispositivo × CVE (`casesByBand`) e planos (`plans`).
 * Sem fonte, sem leitura ou com falha, as contagens chegam NULAS ou vazias — nunca zero inventado.
 */
export interface DashboardDevicePriority {
  state: 'NoSource' | 'NeverCollected' | 'Available' | 'Unavailable';
  note: string | null;
  evaluatedAt: string | null;
  policyCode: string | null;
  policyVersion: number | null;
  /** Dispositivos com caso em aberto SEM disposição — a população da fila. Zero não é ausência de vulnerabilidade. */
  candidateAssets: number | null;
  assetsEvaluated: number | null;
  evaluationTruncated: boolean;
  completeThroughBand: DevicePriorityBand | null;
  truncationNote: string | null;
  assetsByBand: DevicePriorityCount[];
  casesByBand: DevicePriorityCount[];
  /** Os casos cobrem só os dispositivos avaliados (teto por leitura). */
  casesPartial: boolean;
  absenceState: string | null;
  absenceNote: string | null;
  /**
   * Casos dispositivo × CVE ainda em aberto na fonte com disposição humana registrada — fora da fila, não corrigidos.
   * Opcional para respostas anteriores a este campo (sem ele, a tela não afirma ausência).
   */
  dispositions?: DevicePriorityDisposition[] | null;
  top: DashboardDevicePriorityItem[];
  plans: DashboardDevicePlans;
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
  /** [AEGIS-JOURNEY-01] Prioridade de tratamento em dispositivos. Opcional para respostas de versões anteriores. */
  devicePriority?: DashboardDevicePriority;
}

/** [AEGIS-JOURNEY-01] O que o cartão de prioridade da visão geral pode afirmar — cada estado com texto próprio. */
export type DevicePriorityCardView =
  | { kind: 'missing'; text: string }
  | { kind: 'unavailable'; text: string }
  | { kind: 'noSource'; text: string }
  | { kind: 'neverCollected'; text: string }
  | { kind: 'noCandidates'; text: string }
  | { kind: 'noCandidatesUnverified'; text: string }
  | { kind: 'onlyDispositions'; text: string }
  | { kind: 'data' };

/**
 * Estado do cartão. Fila vazia (zero candidatos) NÃO é ausência de vulnerabilidade: os casos em aberto com disposição
 * humana ficam fora da fila e continuam em aberto na fonte. "A fonte não reporta vulnerabilidade em aberto" só é dito
 * quando não há candidato NEM caso disposto e a completude da coleta sustenta a ausência; falha e ausência de fonte têm
 * textos próprios e nunca aparecem como "nada a tratar".
 */
export function devicePriorityCardView(dp: DashboardDevicePriority | null | undefined): DevicePriorityCardView {
  if (!dp)
    return {
      kind: 'missing',
      text: 'A prioridade de tratamento não veio nesta leitura. Abra a Central de Prioridades para consultá-la.',
    };
  if (dp.state === 'Unavailable')
    return {
      kind: 'unavailable',
      text: dp.note ?? 'Não foi possível calcular a prioridade de tratamento agora — nada é exibido, para a falha não parecer ausência.',
    };
  if (dp.state === 'NoSource')
    return { kind: 'noSource', text: dp.note ?? 'Nenhuma fonte de vulnerabilidades por dispositivo está configurada.' };
  if (dp.state === 'NeverCollected')
    return { kind: 'neverCollected', text: dp.note ?? 'A fonte ainda não publicou uma leitura por dispositivo.' };
  if ((dp.candidateAssets ?? 0) === 0) {
    const disposed = disposedCasesText(dp.dispositions);
    if (disposed)
      return {
        kind: 'onlyDispositions',
        text:
          `Nenhum dispositivo na fila de prioridade — e isso não é ausência de vulnerabilidade: ${disposed}. ` +
          (dp.absenceState === 'notVerifiable'
            ? 'Além disso, a coleta não permite concluir a ausência de outros casos. ' + (dp.absenceNote ?? '')
            : ''),
      };
    if (dp.absenceState === 'notVerifiable')
      return {
        kind: 'noCandidatesUnverified',
        text:
          'Nenhum caso de vulnerabilidade em aberto está publicado nesta leitura, mas a coleta não permite concluir ausência. ' +
          (dp.absenceNote ?? ''),
      };
    // Sem o campo das disposições (resposta anterior), a tela não pode afirmar que não há caso disposto.
    if (!dp.dispositions)
      return {
        kind: 'noCandidates',
        text:
          'Nenhum dispositivo na fila de prioridade nesta leitura. Casos com disposição registrada ficam fora da fila e não ' +
          'são contados aqui — consulte a Central de Prioridades.',
      };
    return {
      kind: 'noCandidates',
      text:
        'A fonte não reporta vulnerabilidade em aberto em nenhum dispositivo na aquisição completa mais recente — nem na ' +
        'fila, nem com disposição registrada. Isso não comprova que os dispositivos estejam seguros.' +
        (dp.absenceState === 'conclusiveAttemptFailed' && dp.absenceNote ? ' ' + dp.absenceNote : ''),
    };
  }
  return { kind: 'data' };
}

/** Soma só as faixas priorizáveis (P1…P4) de uma contagem por faixa — a mesma unidade da própria contagem. */
function prioritized(counts: DevicePriorityCount[]): number {
  return counts.filter((c) => c.band !== 'insufficient').reduce((s, c) => s + c.count, 0);
}

/**
 * As três linhas do cartão, cada uma com a SUA unidade: dispositivos, casos dispositivo × CVE e planos. Nenhuma soma
 * atravessa unidades. A linha de casos diz quando o total é parcial (teto por leitura).
 */
export function devicePriorityUnitLines(dp: DashboardDevicePriority): {
  devices: string;
  cases: string;
  /** Casos fora da fila por disposição humana (ainda em aberto na fonte); nulo quando não há nenhum. */
  disposed: string | null;
  plans: string;
} {
  const n = dp.candidateAssets ?? 0;
  const devicesInsufficient = dp.assetsByBand.find((b) => b.band === 'insufficient')?.count ?? 0;
  const devices =
    `${n === 1 ? '1 dispositivo' : `${n} dispositivos`} na fila (vulnerabilidade em aberto sem disposição registrada)` +
    (bandCountsText(dp.assetsByBand.filter((b) => b.band !== 'insufficient')) !== '—'
      ? ` · ${bandCountsText(dp.assetsByBand.filter((b) => b.band !== 'insufficient'))}`
      : '') +
    (devicesInsufficient > 0 ? ` · ${devicesInsufficient} sem informação suficiente` : '');

  const c = prioritized(dp.casesByBand);
  const casesInsufficient = dp.casesByBand.find((b) => b.band === 'insufficient')?.count ?? 0;
  const cases =
    `${c === 1 ? '1 caso' : `${c} casos`} dispositivo × CVE priorizáve${c === 1 ? 'l' : 'is'}` +
    (c > 0 ? ` · ${bandCountsText(dp.casesByBand.filter((b) => b.band !== 'insufficient'))}` : '') +
    (casesInsufficient > 0 ? ` · ${casesInsufficient} sem severidade informada` : '') +
    (dp.casesPartial ? ` — parcial: só dos ${dp.assetsEvaluated ?? 0} dispositivos avaliados nesta leitura` : '');

  const p = dp.plans;
  const plans =
    `${p.active === 1 ? '1 plano ativo' : `${p.active} planos ativos`}` +
    ` · ${p.awaitingValidation} aguardando validação · ${p.overdue} em atraso · ` +
    `${p.completed === 1 ? '1 concluído' : `${p.completed} concluídos`} (concluído ≠ dispositivo corrigido)`;

  return { devices, cases, disposed: disposedCasesText(dp.dispositions), plans };
}

/** Parâmetros do endereço da Central que abrem o detalhe do dispositivo com o caso (e o plano ativo) selecionados. */
export function devicePriorityItemParams(i: DashboardDevicePriorityItem): Record<string, string | null> {
  return { tab: 'achados', device: i.assetId, cve: i.cveId, plan: i.activePlanId };
}

/**
 * A dimensão tem número para mostrar? Só `Available` e `Partial` têm — e mesmo em `Partial` o número é uma
 * leitura INCOMPLETA, que a tela precisa rotular como tal. Função pura para o template não repetir a regra.
 */
export function hasReading(m: DashboardMetric | DashboardIdentity): boolean {
  return m.state === 'Available' || m.state === 'Partial';
}

/**
 * Rótulo curto do ESTADO — deliberadamente sem afirmar recência. `Available` significa "existe leitura",
 * não "a leitura é de agora": um snapshot de três semanas atrás também chega como `Available`. Quem precisa
 * falar de frescor usa `metricFreshness`, que só afirma data quando existe data.
 */
export function stateLabel(state: DashboardSignalState): string {
  switch (state) {
    case 'Available':
      return 'Leitura disponível';
    case 'Partial':
      return 'Leitura parcial';
    case 'NeverCollected':
      return 'Ainda não coletado';
    case 'Undetermined':
      return 'Coleta não comprovada';
    default:
      return 'Sem fonte conectada';
  }
}

/**
 * Mesmo critério de desatualização que o servidor aplica à saúde das fontes
 * (`DashboardOverviewDto.StaleAfterDays`). Duplicado aqui como CONSTANTE nomeada para que a etiqueta do
 * cartão e a lista de fontes envelheçam juntas — não é um limiar novo.
 */
export const STALE_AFTER_DAYS = 7;

/** O que a tela pode AFIRMAR sobre a idade de uma leitura. */
export interface MetricFreshness {
  /** Etiqueta exibida no lugar do antigo rótulo de estado. */
  label: string;
  /** A leitura passou do limiar de desatualização — só pode ser `true` quando há data. */
  stale: boolean;
  /** Existe instante de observação comprovado nesta métrica. */
  dated: boolean;
}

function formatDay(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}/${p(d.getMonth() + 1)}/${d.getFullYear()}`;
}

/**
 * Recência HONESTA de um cartão. O defeito corrigido: `Available` era rotulado "Leitura atual" — mas
 * disponibilidade não comprova frescor, e um dado antigo passava por atual.
 *
 * A idade é medida contra o `generatedAt` da PRÓPRIA leitura composta (relógio do servidor), não contra o
 * relógio do navegador, para que a etiqueta não mude de sentido em uma máquina com data errada. Sem
 * `observedAt` a função NÃO inventa data nem afirma atualidade: diz que a leitura existe e que a data de
 * coleta não é conhecida. Nada aqui altera valores, score ou estado de conector.
 */
export function metricFreshness(
  m: Pick<DashboardMetric, 'state' | 'observedAt'>,
  generatedAt: string,
): MetricFreshness {
  if (m.state !== 'Available' && m.state !== 'Partial')
    return { label: stateLabel(m.state), stale: false, dated: false };

  const prefix = m.state === 'Partial' ? 'Leitura parcial' : 'Leitura';

  if (!m.observedAt)
    return { label: `${prefix} · sem data de coleta`, stale: false, dated: false };

  const observed = new Date(m.observedAt).getTime();
  const reference = new Date(generatedAt).getTime();
  const days = Math.floor((reference - observed) / 86_400_000);
  const stale = Number.isFinite(days) && days >= STALE_AFTER_DAYS;

  return {
    label: stale
      ? `${prefix} de ${formatDay(m.observedAt)} · desatualizada (${days} dias)`
      : `${prefix} de ${formatDay(m.observedAt)}`,
    stale,
    dated: true,
  };
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

/**
 * Nome legível de uma capacidade de identidade. Reexportado do modelo do AEGIS KNIGHT — as duas telas leem
 * as MESMAS capacidades, e dois dicionários paralelos sairiam do ar um do outro na primeira capacidade nova.
 */
export { capabilityLabel as identityCapabilityLabel } from './knight.models';

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
