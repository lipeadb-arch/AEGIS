/**
 * [AEGIS-CROSS-SOURCE-01] Situações identificadas entre fontes — contratos e apresentação.
 * Espelha `AegisScore.Application/Queries/CrossSourceCorrelation.cs`.
 *
 * O backend é a AUTORIDADE da regra, do estado e do texto (CrossSourceCorrelationEvaluator + CrossSourceNarrative).
 * Aqui só há apresentação: nenhum estado é inferido no cliente, nenhuma contagem ausente vira 0, e nenhum rótulo
 * chama o resultado de incidente, risco calculado ou exposição.
 */

export type CrossSourceState =
  | 'identified'
  | 'notIdentified'
  | 'insufficientEvidence'
  | 'linkConflict'
  | 'contradictoryEvidence'
  | 'notEvaluated';

export type CrossSourceReadingState = 'NoSource' | 'NeverCollected' | 'Available';

export interface CrossSourceNote {
  code: string;
  text: string;
}

export interface CrossSourcePolicy {
  maxEvidenceAgeDays: number;
  maxSourceActivityAgeDays: number;
  acquisitionGapCaveatHours: number;
  description: string;
}

export interface CrossSourceEvidenceRecord {
  source: string;
  connectorName: string;
  role: 'vulnerabilities' | 'deviceManagement';
  roleLabel: string;
  eligible: boolean;
  eligibility: string;
  eligibilityLabel: string;
  exclusionReason: string | null;
  isActive: boolean;
  firstObservedAt: string;
  /** Marca da aquisição do AEGIS que observou o registro — a data PRÓPRIA desta evidência. */
  acquiredAt: string;
  /** Última atividade informada pela fonte — outro conceito, nunca misturado com a aquisição. */
  sourceActivityAt: string | null;
  noLongerObservedSince: string | null;
  linkedAt: string | null;
  acquisitionState: string;
  acquisitionLabel: string;
  resolutionLabel: string;
  complianceLabel: string | null;
  encryptionLabel: string | null;
  latestAttemptFailed: boolean;
}

export interface CrossSourceRuleResult {
  ruleCode: string;
  ruleVersion: number;
  title: string;
  state: CrossSourceState;
  stateLabel: string;
  hasCaveats: boolean;
  /** O que foi identificado? */
  summary: string;
  /** Quais dados sustentam isso? */
  supportingData: string;
  /** O que deve ser verificado ou tratado? */
  whatToVerify: string;
  limitation: string;
  criteria: string[];
  caveats: CrossSourceNote[];
  reasons: string[];
  openCveCount: number | null;
}

export interface CrossSourceCveSource {
  source: string;
  connectorName: string;
  firstSeenAt: string;
  acquiredAt: string;
  acquisitionState: string;
  acquisitionLabel: string;
}

export interface CrossSourceCve {
  cveId: string;
  title: string | null;
  severity: string | null;
  cvssScore: number | null;
  sources: CrossSourceCveSource[];
}

export interface CrossSourceCvePage {
  /** Total de CVEs distintas (unidade: CVEs) — não o tamanho da página. */
  total: number;
  page: number;
  pageSize: number;
  items: CrossSourceCve[];
  excludedOutOfPolicy: number;
  noLongerReported: number;
}

export interface AssetCrossSource {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  /** Momento do CÁLCULO — distinto das datas das evidências. */
  evaluatedAt: string;
  heading: string;
  scope: string;
  associationReason: string;
  policy: CrossSourcePolicy;
  rules: CrossSourceRuleResult[];
  evidence: CrossSourceEvidenceRecord[];
  cves: CrossSourceCvePage | null;
}

export interface CrossSourceRuleTally {
  ruleCode: string;
  ruleVersion: number;
  title: string;
  identified: number;
  identifiedWithCaveats: number;
  notIdentified: number;
  insufficientEvidence: number;
  linkConflict: number;
  contradictoryEvidence: number;
}

export interface CrossSourceSituationSummary {
  readingState: CrossSourceReadingState;
  readingNote: string | null;
  assetsWithBothSources: number;
  assetsEvaluated: number;
  evaluationTruncated: boolean;
  situationsIdentified: number;
  assetsWithSituations: number;
  byRule: CrossSourceRuleTally[];
}

export interface CrossSourceSituationItem {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  ruleCode: string;
  ruleVersion: number;
  title: string;
  state: CrossSourceState;
  stateLabel: string;
  hasCaveats: boolean;
  summary: string;
  openCveCount: number | null;
  cvePreview: string[];
  cvePreviewTruncated: boolean;
  caveats: CrossSourceNote[];
  vulnerabilitiesAcquiredAt: string | null;
  deviceManagementAcquiredAt: string | null;
}

export interface CrossSourceSituationList {
  evaluatedAt: string;
  heading: string;
  scope: string;
  policy: CrossSourcePolicy;
  summary: CrossSourceSituationSummary;
  stateFilter: CrossSourceState;
  ruleFilter: string | null;
  items: CrossSourceSituationItem[];
  /** Total FILTRADO (unidade: situações ativo × regra). */
  total: number;
  page: number;
  pageSize: number;
}

export interface CrossSourceFilter {
  state: CrossSourceState;
  rule: string | null;
  page: number;
  pageSize: number;
}

// ---- Apresentação (pura) --------------------------------------------------------------------------------------------

export const CROSS_SOURCE_HEADING = 'Situações identificadas entre fontes';

export type CrossSourceTone = 'attention' | 'warn' | 'muted' | 'ok';

/** Situação identificada pede atenção; vínculo/registro contraditório é aviso; o resto é neutro (nunca "ok" = seguro). */
export function crossSourceStateTone(state: CrossSourceState | string): CrossSourceTone {
  switch (state) {
    case 'identified':
      return 'attention';
    case 'linkConflict':
    case 'contradictoryEvidence':
      return 'warn';
    default:
      return 'muted';
  }
}

/** Aquisição atual completa é neutra-positiva; parcial, não concluída, anterior ou sem registro recebem aviso. */
export function acquisitionTone(state: string): CrossSourceTone {
  return state === 'current' ? 'ok' : state === 'notRecorded' || state === 'currentNotRecorded' ? 'muted' : 'warn';
}

/** Filtros de estado da Central (o padrão é "identificadas"). */
export const CROSS_SOURCE_STATE_FILTERS: ReadonlyArray<{ value: CrossSourceState; label: string }> = [
  { value: 'identified', label: 'Identificadas' },
  { value: 'linkConflict', label: 'Vínculo em conflito' },
  { value: 'contradictoryEvidence', label: 'Registros contraditórios' },
  { value: 'insufficientEvidence', label: 'Evidência insuficiente' },
  { value: 'notIdentified', label: 'Nenhuma condição' },
];

/** "1 CVE" / "n CVEs"; sem contagem (sem evidência usável) = "—", nunca 0. */
export function cveCountText(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  return n === 1 ? '1 CVE' : `${n} CVEs`;
}

/** Prévia limitada que NUNCA se apresenta como total: diz quantas faltam. */
export function cvePreviewText(item: Pick<CrossSourceSituationItem, 'cvePreview' | 'cvePreviewTruncated' | 'openCveCount'>): string {
  if (!item.cvePreview.length) return '—';
  const shown = item.cvePreview.join(', ');
  if (!item.cvePreviewTruncated) return shown;
  const rest = (item.openCveCount ?? item.cvePreview.length) - item.cvePreview.length;
  return rest > 0 ? `${shown} e mais ${rest}` : `${shown}…`;
}

/** Total paginado com unidade explícita. */
export function pageRangeText(total: number, page: number, pageSize: number, unit: string): string {
  if (total <= 0) return `0 ${unit}`;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return `${from}–${to} de ${total} ${unit}`;
}

export type CrossSourceListView =
  | { kind: 'loading' }
  | { kind: 'error'; text: string }
  | { kind: 'noSource'; text: string }
  | { kind: 'neverCollected'; text: string }
  | { kind: 'noPopulation'; text: string }
  | { kind: 'zero'; text: string }
  | { kind: 'filterEmpty'; text: string }
  | { kind: 'items' };

/**
 * O que a lista da Central pode afirmar — com os estados de linguagem do produto: carregando, falha, sem fonte, sem
 * leitura, população vazia (zero apurado de ativos avaliáveis), zero apurado de situações e filtro sem correspondência.
 */
export function crossSourceListView(
  list: CrossSourceSituationList | null,
  loading = false,
  error: string | null = null,
): CrossSourceListView {
  if (loading) return { kind: 'loading' };
  if (error) return { kind: 'error', text: error };
  if (!list) return { kind: 'loading' };
  const s = list.summary;
  if (s.readingState === 'NoSource')
    return { kind: 'noSource', text: s.readingNote ?? 'As regras exigem as duas fontes configuradas.' };
  if (s.readingState === 'NeverCollected')
    return { kind: 'neverCollected', text: s.readingNote ?? 'Ao menos uma fonte ainda não publicou leitura por dispositivo.' };
  if (s.assetsWithBothSources === 0)
    return {
      kind: 'noPopulation',
      text: 'Nenhum ativo tem registros das duas fontes — não há dispositivo em que as regras possam ser avaliadas.',
    };
  if (list.items.length > 0) return { kind: 'items' };
  const filtered = list.stateFilter !== 'identified' || !!list.ruleFilter;
  if (filtered) return { kind: 'filterEmpty', text: 'Nenhuma situação corresponde ao filtro selecionado.' };
  return {
    kind: 'zero',
    text:
      `Nenhuma situação identificada entre fontes nos ${s.assetsEvaluated} ativo(s) avaliados. ` +
      'Isso não comprova que os dispositivos estejam seguros — só que as duas condições não coexistem nas evidências elegíveis.',
  };
}

/** Resumo com UNIDADES explícitas: situações (ativo × regra) e ativos. */
export function crossSourceSummaryText(s: CrossSourceSituationSummary): string {
  const situations = s.situationsIdentified === 1 ? '1 situação identificada' : `${s.situationsIdentified} situações identificadas`;
  const assets = s.assetsWithSituations === 1 ? '1 ativo' : `${s.assetsWithSituations} ativos`;
  const pop = s.assetsWithBothSources === 1 ? '1 ativo' : `${s.assetsWithBothSources} ativos`;
  return `${situations} em ${assets} · ${pop} com registros das duas fontes`;
}

/** Teto atingido: os totais são parciais e isso é dito. */
export function truncationNote(s: CrossSourceSituationSummary): string | null {
  if (!s.evaluationTruncated) return null;
  return (
    `Avaliados ${s.assetsEvaluated} de ${s.assetsWithBothSources} ativos com registros das duas fontes (teto por leitura): ` +
    'os totais desta leitura são parciais.'
  );
}

/** Nome curto da regra para colunas estreitas. */
export function ruleShortLabel(code: string): string {
  switch (code) {
    case 'XS-DEF-INT-NONCOMPLIANT':
      return 'Vulnerabilidades + não conforme';
    case 'XS-DEF-INT-UNENCRYPTED':
      return 'Vulnerabilidades + sem criptografia';
    default:
      return code;
  }
}

/** Texto da página de CVEs com unidade e sem fingir que a página é o total. */
export function cvePageText(p: CrossSourceCvePage | null | undefined): string {
  if (!p) return 'Sem evidência de vulnerabilidade elegível.';
  if (p.total === 0) return 'Nenhuma CVE em aberto nas evidências elegíveis.';
  return pageRangeText(p.total, p.page, p.pageSize, p.total === 1 ? 'CVE distinta' : 'CVEs distintas');
}
