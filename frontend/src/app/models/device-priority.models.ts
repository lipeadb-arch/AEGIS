/**
 * [AEGIS-RISK-PRIORITIZATION-01] Prioridade de tratamento de vulnerabilidades em dispositivos — contratos e apresentação.
 * Espelha `AegisScore.Application/Queries/DevicePriority.cs`.
 *
 * O backend é a AUTORIDADE da faixa, da ordem, dos motivos e do texto (DevicePriorityEvaluator + DevicePriorityNarrative).
 * Aqui só há apresentação: nenhuma faixa é recalculada no cliente, contagem ausente não vira 0, e nenhum rótulo chama a
 * prioridade de risco calculado, probabilidade, incidente ou exposição à internet.
 */

import { CrossSourceNote, CrossSourcePolicy, pageRangeText } from './cross-source.models';

export type DevicePriorityBand = 'p1' | 'p2' | 'p3' | 'p4' | 'insufficient';

export type DevicePriorityStatus =
  | 'prioritized'
  | 'insufficient'
  | 'allDisposed'
  | 'noOpenCases'
  | 'absenceNotVerified'
  | 'noSourceRecord';

/** Completude para concluir ausência — distinta da contagem de casos e de candidatos. */
export type DevicePriorityAbsenceState = 'conclusive' | 'conclusiveAttemptFailed' | 'notVerifiable' | 'notApplicable';

export type DevicePriorityFactorKind = 'sourceFact' | 'declared' | 'inferred' | 'unknown';

export type DevicePriorityEffect = 'basis' | 'determinant' | 'aggravating' | 'none' | 'notUsed';

export type DevicePriorityReadingState = 'NoSource' | 'NeverCollected' | 'Available';

export interface DevicePriorityFactor {
  code: string;
  label: string;
  value: string;
  kind: DevicePriorityFactorKind;
  source: string | null;
  availableAt: string | null;
  availableAtLabel: string | null;
  effect: DevicePriorityEffect;
  note: string | null;
}

export interface DevicePriorityDecisionRow {
  severity: string;
  exploitVerified: string;
  exploitPublic: string;
  exploitNotInformed: string;
}

export interface DevicePriorityPolicy {
  code: string;
  version: number;
  name: string;
  rationale: string;
  table: DevicePriorityDecisionRow[];
  aggravation: string[];
  tieBreaks: string[];
  notUsed: string[];
  temporal: CrossSourcePolicy;
}

export interface DevicePriorityCriticality {
  storedValue: number;
  state: 'declared' | 'notConfirmed' | 'diverged';
  label: string;
  declaredValue: number | null;
  declaredAt: string | null;
  declaredByName: string | null;
  note: string | null;
}

export interface DevicePriorityCase {
  cveId: string;
  title: string | null;
  band: DevicePriorityBand;
  bandLabel: string;
  reason: string;
  severityLabel: string;
  severity: string | null;
  cvssScore: number | null;
  exploitLabel: string;
  attackVectorLabel: string | null;
  epss: number | null;
  knownExploitedMarkedWithoutOrigin: boolean;
  source: string;
  firstSeenAt: string;
  acquiredAt: string;
  acquisitionState: string;
  acquisitionLabel: string;
}

export interface DevicePriorityCasePage {
  /** Total de casos (unidade: casos ativo × CVE) — não o tamanho da página. */
  total: number;
  page: number;
  pageSize: number;
  items: DevicePriorityCase[];
}

export interface DevicePriorityCount {
  band: DevicePriorityBand;
  label: string;
  count: number;
}

export interface DevicePriorityDisposition {
  status: 'mitigated' | 'accepted' | 'falsePositive';
  label: string;
  count: number;
}

export interface DevicePrioritySituation {
  ruleCode: string;
  ruleVersion: number;
  title: string;
  state: string;
  stateLabel: string;
  hasCaveats: boolean;
}

export interface AssetDevicePriority {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  /** Momento do CÁLCULO — distinto das datas das evidências. */
  evaluatedAt: string;
  heading: string;
  scope: string;
  policy: DevicePriorityPolicy;
  status: DevicePriorityStatus;
  band: DevicePriorityBand;
  bandLabel: string;
  positionReason: string;
  determiningCase: DevicePriorityCase | null;
  informationLabel: string;
  factors: DevicePriorityFactor[];
  couldChange: string[];
  limitations: string[];
  caveats: CrossSourceNote[];
  nextAction: string;
  criticality: DevicePriorityCriticality;
  situations: DevicePrioritySituation[];
  casesByBand: DevicePriorityCount[];
  insufficientCases: number;
  dispositions: DevicePriorityDisposition[];
  noLongerReported: number;
  excludedOutOfPolicy: number;
  cases: DevicePriorityCasePage | null;
  absenceState: DevicePriorityAbsenceState;
  /** Completude da fonte neste dispositivo, com a data da aquisição que a sustenta (texto do backend). */
  absenceLabel: string | null;
  /** Casos em aberto armazenados nas aquisições elegíveis — o fato, sem conclusão de ausência. */
  storedOpenCases: number;
}

export interface DevicePriorityItem {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  /** Posição (1-based) na ordem filtrada — contínua entre páginas. */
  position: number;
  status: DevicePriorityStatus;
  band: DevicePriorityBand;
  bandLabel: string;
  positionReason: string;
  determiningCase: DevicePriorityCase | null;
  prioritizableCases: number;
  casesByBand: DevicePriorityCount[];
  insufficientCases: number;
  dispositionCases: number;
  aggravators: string[];
  deviceContextLabel: string;
  criticalityLabel: string;
  informationLabel: string;
  caveats: CrossSourceNote[];
  nextAction: string;
  /** Outros dispositivos avaliados com exatamente os mesmos fatores de ordenação (empate real). */
  tiedAssets: number;
}

export interface DevicePrioritySummary {
  readingState: DevicePriorityReadingState;
  readingNote: string | null;
  candidateAssets: number;
  assetsEvaluated: number;
  evaluationTruncated: boolean;
  completeThroughBand: DevicePriorityBand | null;
  truncationNote: string | null;
  assetsByBand: DevicePriorityCount[];
  casesByBand: DevicePriorityCount[];
  dispositions: DevicePriorityDisposition[];
  outOfScopeSources: number;
  outOfScopeNote: string | null;
  /** Suficiência da coleta para concluir ausência fora da fila — nunca deduzida de candidateAssets. */
  absenceState: DevicePriorityAbsenceState;
  absenceNote: string | null;
}

/** Declaração de criticidade CONFIRMADA pelo servidor — o que o componente avisa às superfícies que o contêm. */
export interface DevicePriorityCriticalityChange {
  assetId: string;
  criticality: DevicePriorityCriticality;
}

export interface DevicePriorityList {
  evaluatedAt: string;
  heading: string;
  scope: string;
  policy: DevicePriorityPolicy;
  summary: DevicePrioritySummary;
  bandFilter: DevicePriorityBand | null;
  items: DevicePriorityItem[];
  /** Total FILTRADO (unidade: dispositivos). */
  total: number;
  page: number;
  pageSize: number;
}

export interface DevicePriorityFilter {
  band: DevicePriorityBand | null;
  page: number;
  pageSize: number;
}

// ---- Apresentação (pura) --------------------------------------------------------------------------------------------

export const DEVICE_PRIORITY_HEADING = 'Prioridade de tratamento';

export type DevicePriorityTone = 'attention' | 'warn' | 'info' | 'muted';

/** Prioridade 1 pede atenção; 2 é aviso; 3 informativo; 4 e não priorizável são neutros (nunca "ok" = seguro). */
export function bandTone(band: DevicePriorityBand | string): DevicePriorityTone {
  switch (band) {
    case 'p1':
      return 'attention';
    case 'p2':
      return 'warn';
    case 'p3':
      return 'info';
    default:
      return 'muted';
  }
}

/** Rótulo curto para colunas estreitas. */
export function bandShort(band: DevicePriorityBand | string): string {
  switch (band) {
    case 'p1':
      return 'P1';
    case 'p2':
      return 'P2';
    case 'p3':
      return 'P3';
    case 'p4':
      return 'P4';
    default:
      return 'Não priorizável';
  }
}

/** Filtros da Central (o padrão é "todas as faixas"; não priorizáveis ficam num filtro próprio). */
export const DEVICE_PRIORITY_BAND_FILTERS: ReadonlyArray<{ value: DevicePriorityBand | null; label: string }> = [
  { value: null, label: 'Todas as faixas' },
  { value: 'p1', label: 'Prioridade 1' },
  { value: 'p2', label: 'Prioridade 2' },
  { value: 'p3', label: 'Prioridade 3' },
  { value: 'p4', label: 'Prioridade 4' },
  { value: 'insufficient', label: 'Informação insuficiente' },
];

export function factorKindLabel(kind: DevicePriorityFactorKind | string): string {
  switch (kind) {
    case 'sourceFact':
      return 'Fato da fonte';
    case 'declared':
      return 'Informação declarada';
    case 'inferred':
      return 'Inferência identificada';
    default:
      return 'Desconhecido';
  }
}

export function factorEffectLabel(effect: DevicePriorityEffect | string): string {
  switch (effect) {
    case 'basis':
      return 'Base da avaliação';
    case 'determinant':
      return 'Determinou a faixa';
    case 'aggravating':
      return 'Antecipou uma faixa';
    case 'none':
      return 'Considerado — não altera a faixa';
    default:
      return 'Não usado nesta versão';
  }
}

/** Contagens por faixa, só as presentes: "P1 2 · P3 1". Nenhuma: "—" (nunca "0 casos" inventado). */
export function bandCountsText(counts: DevicePriorityCount[] | null | undefined): string {
  const present = (counts ?? []).filter((c) => c.count > 0);
  if (!present.length) return '—';
  return present.map((c) => `${bandShort(c.band)} ${c.count}`).join(' · ');
}

/** Disposições humanas presentes ("2 com risco aceito · 1 falso positivo"); nenhuma = null. */
export function dispositionText(dispositions: DevicePriorityDisposition[] | null | undefined): string | null {
  const present = (dispositions ?? []).filter((d) => d.count > 0);
  if (!present.length) return null;
  return present.map((d) => `${d.count} · ${d.label.toLowerCase()}`).join(' · ');
}

/** Empate real: diz que a ordem entre eles é só de estabilidade — nunca finge diferença de risco. */
export function tieText(tiedAssets: number): string | null {
  if (tiedAssets <= 0) return null;
  return (
    `Empate real com ${tiedAssets === 1 ? '1 outro dispositivo' : `${tiedAssets} outros dispositivos`} pelos mesmos ` +
    'fatores — a ordem entre eles é alfabética, só para estabilidade.'
  );
}

/** Contagem com unidade explícita ("1 caso" / "n casos"). */
export function casesText(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  return n === 1 ? '1 caso' : `${n} casos`;
}

/** "3 dispositivos com vulnerabilidade em aberto · P1 1 · P2 2 · 1 sem informação suficiente · recorte parcial". */
export function devicePrioritySummaryText(s: DevicePrioritySummary): string {
  const pop = s.candidateAssets === 1 ? '1 dispositivo' : `${s.candidateAssets} dispositivos`;
  const bands = s.assetsByBand.filter((b) => b.band !== 'insufficient' && b.count > 0);
  const insufficient = s.assetsByBand.find((b) => b.band === 'insufficient')?.count ?? 0;
  const parts = [`${pop} com vulnerabilidade em aberto`];
  if (bands.length) parts.push(bands.map((b) => `${bandShort(b.band)} ${b.count}`).join(' · '));
  if (insufficient > 0) parts.push(`${insufficient} sem informação suficiente para priorizar`);
  if (s.evaluationTruncated) parts.push(`recorte parcial: ${s.assetsEvaluated} avaliados`);
  return parts.join(' · ');
}

export type DevicePriorityListView =
  | { kind: 'loading' }
  | { kind: 'error'; text: string }
  | { kind: 'noSource'; text: string }
  | { kind: 'neverCollected'; text: string }
  | { kind: 'noCandidates'; text: string }
  | { kind: 'noCandidatesUnverified'; text: string }
  | { kind: 'onlyInsufficient'; text: string }
  | { kind: 'filterEmpty'; text: string }
  | { kind: 'items' };

/**
 * O que a fila pode afirmar: carregando, falha, sem fonte, sem leitura, nenhum dispositivo com vulnerabilidade em aberto,
 * só casos sem informação suficiente, filtro sem correspondência — cada um com texto próprio. Zero nunca é "seguro".
 */
export function devicePriorityListView(
  list: DevicePriorityList | null,
  loading = false,
  error: string | null = null,
): DevicePriorityListView {
  if (loading) return { kind: 'loading' };
  if (error) return { kind: 'error', text: error };
  if (!list) return { kind: 'loading' };
  const s = list.summary;
  if (s.readingState === 'NoSource')
    return { kind: 'noSource', text: s.readingNote ?? 'Nenhuma fonte de vulnerabilidades está configurada.' };
  if (s.readingState === 'NeverCollected')
    return { kind: 'neverCollected', text: s.readingNote ?? 'A fonte ainda não publicou uma leitura por dispositivo.' };
  if (s.candidateAssets === 0) {
    // Zero candidatos é uma CONTAGEM; só a completude da coleta permite chamá-la de ausência.
    if (s.absenceState === 'notVerifiable')
      return {
        kind: 'noCandidatesUnverified',
        text:
          'Nenhum caso de vulnerabilidade em aberto está publicado nesta leitura, mas isso não permite concluir ausência. ' +
          (s.absenceNote ?? ''),
      };
    return {
      kind: 'noCandidates',
      text:
        'Nenhum dispositivo com vulnerabilidade em aberto e disposição ativa na aquisição completa mais recente da fonte. ' +
        'Isso não comprova que os dispositivos estejam seguros.' +
        (s.absenceState === 'conclusiveAttemptFailed' && s.absenceNote ? ' ' + s.absenceNote : ''),
    };
  }
  if (list.items.length > 0) return { kind: 'items' };
  const prioritized = s.assetsByBand.filter((b) => b.band !== 'insufficient').reduce((a, b) => a + b.count, 0);
  if (!list.bandFilter && prioritized === 0)
    return {
      kind: 'onlyInsufficient',
      text:
        'Nenhum dispositivo tem caso priorizável: os dispositivos com vulnerabilidade em aberto não têm fundamento mínimo ' +
        '(veja o filtro "Informação insuficiente" para os motivos).',
    };
  return { kind: 'filterEmpty', text: 'Nenhum dispositivo nesta faixa ou nesta página.' };
}

/** Faixa de páginas com unidade explícita. */
export function devicePriorityRangeText(l: Pick<DevicePriorityList, 'total' | 'page' | 'pageSize'>): string {
  return pageRangeText(l.total, l.page, l.pageSize, l.total === 1 ? 'dispositivo' : 'dispositivos');
}

/** Página de casos com unidade. */
export function casePageText(p: DevicePriorityCasePage | null | undefined): string {
  if (!p) return 'Sem evidência de vulnerabilidade elegível.';
  if (p.total === 0) return 'Nenhum caso em aberto publicado nesta leitura.';
  return pageRangeText(p.total, p.page, p.pageSize, p.total === 1 ? 'caso' : 'casos');
}

/** EPSS como fato da fonte, com a semântica: nunca "chance de ser atacado". */
export function epssText(epss: number | null | undefined): string {
  if (epss === null || epss === undefined) return 'EPSS não informado';
  const pct = Math.round(epss * 1000) / 10;
  return `EPSS ${pct}% (probabilidade global, 30 dias)`;
}

/** Gate de APRESENTAÇÃO da declaração de criticidade — o servidor continua sendo a autoridade (Manager, TenantAdmin). */
export function canDeclareCriticality(role: string | null): boolean {
  return role === 'Manager' || role === 'TenantAdmin';
}

/**
 * Falha da PRÓPRIA declaração: só afirma "nada foi alterado" quando o servidor recusou (400/403/404). Sem resposta ou com
 * erro do servidor, a gravação não é confirmada nem negada.
 */
export function declarationErrorText(err: { status?: number; error?: unknown } | null | undefined): string {
  if (err?.status === 403) return 'Seu papel não permite declarar a criticidade (Manager ou TenantAdmin). Nada foi alterado.';
  if (err?.status === 400 || err?.status === 404)
    return (typeof err.error === 'string' && err.error ? err.error + ' ' : '') + 'Nada foi alterado.';
  return 'Não foi possível confirmar se a declaração foi registrada. Recarregue a prioridade para verificar antes de repetir.';
}

/** Gravação confirmada, releitura falhou: diz exatamente isso — nunca "nada foi alterado". */
export function declarationSavedNotRefreshedText(c: DevicePriorityCriticality): string {
  return (
    `A declaração foi registrada (${c.label}), mas a apresentação não pôde ser atualizada agora. ` +
    'Use "Tentar novamente" para recarregar a prioridade.'
  );
}

/**
 * Linhas do inventário com a criticidade CONFIRMADA pelo servidor aplicada ao ativo declarado (as demais, intactas).
 * Confirmada = declarada com proveniência e igual ao valor cadastrado.
 */
export function applyDeclaredCriticality<T extends { id: string; criticality: number; criticalityConfirmed?: boolean }>(
  rows: readonly T[],
  change: DevicePriorityCriticalityChange,
): T[] {
  return rows.map((r) =>
    r.id === change.assetId
      ? { ...r, criticality: change.criticality.storedValue, criticalityConfirmed: change.criticality.state === 'declared' }
      : r,
  );
}

/**
 * Depois de uma releitura em segundo plano: se a página pedida ficou vazia porque a ordem/faixa mudou, a última página
 * válida (uma única correção, nunca um laço). Nula quando a página continua válida.
 */
export function pageAfterRefresh(l: Pick<DevicePriorityList, 'items' | 'total' | 'page' | 'pageSize'>): number | null {
  if (l.items.length > 0 || l.total === 0 || l.page <= 1) return null;
  const last = Math.max(1, Math.ceil(l.total / l.pageSize));
  return last < l.page ? last : null;
}

/**
 * Nota do detalhe aberto na Central quando, depois da releitura, o dispositivo não está mais na lista exibida: com o filtro
 * de faixa vazio, ele SAIU do filtro; com outros itens, pode ter mudado de faixa ou de posição. Nula quando a linha continua
 * visível ou quando não há detalhe aberto. O detalhe continua aberto nos dois casos — a existência dele não depende da linha.
 */
export function detailOutsideListNote(
  l: Pick<DevicePriorityList, 'items' | 'total' | 'bandFilter'> | null,
  expandedId: string | null,
): string | null {
  if (!expandedId || !l || l.items.some((i) => i.assetId === expandedId)) return null;
  const filter = l.bandFilter ? DEVICE_PRIORITY_BAND_FILTERS.find((f) => f.value === l.bandFilter)?.label : null;
  if (filter && l.total === 0)
    return (
      `Após a atualização, este dispositivo saiu do filtro "${filter}", que ficou sem dispositivos. A prioridade atualizada ` +
      'está no detalhe abaixo, que continua aberto.'
    );
  if (filter)
    return (
      `Após a atualização, este dispositivo não está mais nesta página do filtro "${filter}" (mudou de faixa ou de posição). ` +
      'A prioridade atualizada está no detalhe abaixo, que continua aberto.'
    );
  return 'Após a atualização, este dispositivo não está mais nesta página da fila (nova posição ou faixa); o detalhe continua aberto.';
}

export type InventoryPageStep = { kind: 'apply'; page: number } | { kind: 'reread'; page: number };

/**
 * Inventário relido depois de uma declaração: se a página pedida deixou de existir (o ativo saiu do filtro), relê a última
 * página válida UMA vez, com os mesmos filtros; sem resultados, normaliza para a página 1 e aplica o vazio verdadeiro. Uma
 * segunda resposta ainda fora do total é aplicada como veio — nunca um laço de releituras.
 */
export function inventoryPageStep(requestedPage: number, totalPages: number, alreadyCorrected: boolean): InventoryPageStep {
  if (totalPages <= 0) return { kind: 'apply', page: 1 };
  if (requestedPage > totalPages && !alreadyCorrected) return { kind: 'reread', page: totalPages };
  return { kind: 'apply', page: requestedPage };
}
