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

export type CrossSourceAssociationState = 'proven' | 'notProven' | 'conflict' | 'sourceMissing';

/** Conclusão da AVALIAÇÃO sobre a associação dos registros das duas fontes neste ativo (texto do backend). */
export interface CrossSourceAssociation {
  state: CrossSourceAssociationState;
  label: string;
  text: string;
}

/** O que representam as datas de um resultado: sustentam a conclusão, só estão disponíveis, ou nenhuma participa. */
export type CrossSourceEvidenceBasis = 'supporting' | 'available' | 'none';

/** Aquisições de UMA fonte que participam: intervalo e quantidade — nunca só a mais recente. */
export interface CrossSourceAcquisitionSpan {
  first: string;
  last: string;
  acquisitions: number;
}

export interface AssetCrossSource {
  assetId: string;
  assetName: string;
  nameIsPlaceholder: boolean;
  /** Momento do CÁLCULO — distinto das datas das evidências. */
  evaluatedAt: string;
  heading: string;
  scope: string;
  /** O REQUISITO da associação (critério) — igual para todo ativo, nunca uma afirmação sobre ele. */
  associationCriterion: string;
  /** A CONCLUSÃO da avaliação sobre a associação neste ativo. */
  association: CrossSourceAssociation;
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
  notIdentifiedWithCaveats: number;
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
  evidenceBasis: CrossSourceEvidenceBasis;
  vulnerabilityAcquisitions: CrossSourceAcquisitionSpan | null;
  deviceManagementAcquisitions: CrossSourceAcquisitionSpan | null;
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
  // A regra é uma conjunção: uma condição pode estar presente e a outra não. O rótulo descreve a COMBINAÇÃO.
  { value: 'notIdentified', label: 'Combinação não identificada' },
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
  /** Todas as avaliações concluíram que a combinação não se formou. */
  | { kind: 'zeroConclusive'; text: string; limited: boolean }
  /** Nenhuma avaliação verificou a combinação (insuficiente, conflito, contradição): o zero NÃO é conclusivo. */
  | { kind: 'zeroInconclusive'; text: string; limited: boolean }
  /** Parte concluída sem a combinação, parte inconclusiva — as duas são ditas. */
  | { kind: 'zeroMixed'; text: string; limited: boolean }
  | { kind: 'filterEmpty'; text: string }
  | { kind: 'items' };

/** Totais de AVALIAÇÕES (unidade: ativo × regra), somados das contagens por regra do backend — nada é inferido. */
export interface CrossSourceEvaluationTotals {
  evaluations: number;
  identified: number;
  notIdentified: number;
  insufficientEvidence: number;
  linkConflict: number;
  contradictoryEvidence: number;
  /** Avaliações sem conclusão (insuficiente, conflito, contradição): não verificaram a combinação. */
  inconclusive: number;
}

export function crossSourceEvaluationTotals(s: CrossSourceSituationSummary): CrossSourceEvaluationTotals {
  const sum = (f: (t: CrossSourceRuleTally) => number) => s.byRule.reduce((acc, t) => acc + f(t), 0);
  const identified = sum((t) => t.identified);
  const notIdentified = sum((t) => t.notIdentified);
  const insufficientEvidence = sum((t) => t.insufficientEvidence);
  const linkConflict = sum((t) => t.linkConflict);
  const contradictoryEvidence = sum((t) => t.contradictoryEvidence);
  const inconclusive = insufficientEvidence + linkConflict + contradictoryEvidence;
  return {
    evaluations: identified + notIdentified + inconclusive,
    identified, notIdentified, insufficientEvidence, linkConflict, contradictoryEvidence, inconclusive,
  };
}

function countText(n: number, one: string, many: string): string {
  return n === 1 ? `1 ${one}` : `${n} ${many}`;
}

function joinPt(parts: string[]): string {
  return parts.length <= 1 ? parts.join('') : `${parts.slice(0, -1).join(', ')} e ${parts[parts.length - 1]}`;
}

/** "2 com evidência insuficiente e 1 com vínculo em conflito" — só as categorias presentes. */
export function inconclusiveBreakdown(t: CrossSourceEvaluationTotals): string {
  const parts: string[] = [];
  if (t.insufficientEvidence) parts.push(`${t.insufficientEvidence} com evidência insuficiente`);
  if (t.linkConflict) parts.push(`${t.linkConflict} com vínculo em conflito`);
  if (t.contradictoryEvidence) parts.push(`${t.contradictoryEvidence} com registros contraditórios`);
  return joinPt(parts);
}

/**
 * O que a lista da Central pode afirmar — com os estados de linguagem do produto: carregando, falha, sem fonte, sem
 * leitura, população vazia, filtro sem correspondência e, sem situação identificada, TRÊS zeros distintos pelos totais
 * por estado: conclusivo, inconclusivo e misto — cada um declarando o recorte quando o teto de avaliação foi atingido.
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
  const t = crossSourceEvaluationTotals(s);
  if (t.identified > 0) return { kind: 'filterEmpty', text: 'Nenhuma situação nesta página.' };

  const limited = s.evaluationTruncated;
  const cut = limited
    ? ` Recorte limitado: avaliados ${s.assetsEvaluated} de ${s.assetsWithBothSources} ativos com registros das duas ` +
      'fontes (teto por leitura); os demais não foram avaliados.'
    : '';
  const evaluations = `${countText(t.evaluations, 'avaliação', 'avaliações')} (ativo × regra)`;
  if (t.evaluations === 0 || t.notIdentified === 0)
    return {
      kind: 'zeroInconclusive',
      limited,
      text:
        (t.evaluations === 0
          ? 'Nenhuma avaliação (ativo × regra) foi concluída nesta leitura.'
          : `Nenhuma combinação pôde ser verificada: das ${evaluations}, ${inconclusiveBreakdown(t)}.`) +
        ' Não foi demonstrada a ausência das situações — o zero desta leitura não é conclusivo.' + cut,
    };
  if (t.inconclusive > 0)
    return {
      kind: 'zeroMixed',
      limited,
      text:
        'Nenhuma situação identificada nas avaliações concluídas: em ' +
        `${countText(t.notIdentified, 'avaliação', 'avaliações')} a combinação não se formou; ` +
        (t.inconclusive === 1 ? 'outra não pôde ser verificada' : `outras ${t.inconclusive} não puderam ser verificadas`) +
        ` (${inconclusiveBreakdown(t)}). Isso não comprova que os dispositivos estejam seguros.` + cut,
    };
  return {
    kind: 'zeroConclusive',
    limited,
    text:
      `Nenhuma situação identificada: nas ${evaluations} de ` +
      `${countText(s.assetsEvaluated, 'ativo avaliado', 'ativos avaliados')}, a combinação das duas condições não se ` +
      'formou nas evidências elegíveis. Isso não comprova que os dispositivos estejam seguros — cada condição isolada ' +
      'continua nas telas de cada fonte.' + cut,
  };
}

/** Resumo com UNIDADES explícitas: situações (ativo × regra), ativos e avaliações ainda inconclusivas. */
export function crossSourceSummaryText(s: CrossSourceSituationSummary): string {
  const situations = s.situationsIdentified === 1 ? '1 situação identificada' : `${s.situationsIdentified} situações identificadas`;
  const assets = s.assetsWithSituations === 1 ? '1 ativo' : `${s.assetsWithSituations} ativos`;
  const pop = s.assetsWithBothSources === 1 ? '1 ativo' : `${s.assetsWithBothSources} ativos`;
  const t = crossSourceEvaluationTotals(s);
  const pending = t.inconclusive > 0
    ? ` · ${countText(t.inconclusive, 'avaliação inconclusiva', 'avaliações inconclusivas')} (ativo × regra)`
    : '';
  const cut = s.evaluationTruncated ? ' · recorte parcial (teto de avaliação)' : '';
  return `${situations} em ${assets} · ${pop} com registros das duas fontes${pending}${cut}`;
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

/** Associação comprovada é neutra-positiva (identidade, não segurança); conflito é aviso; o resto é neutro. */
export function associationTone(state: CrossSourceAssociationState | string): CrossSourceTone {
  return state === 'proven' ? 'ok' : state === 'conflict' ? 'warn' : 'muted';
}

/** Rótulo do que as datas de um item representam. */
export function evidenceBasisLabel(basis: CrossSourceEvidenceBasis | string): string {
  switch (basis) {
    case 'supporting':
      return 'Aquisições que sustentam a conclusão';
    case 'available':
      return 'Aquisições disponíveis — sem conclusão combinada';
    default:
      return 'Nenhuma evidência elegível participa';
  }
}

/**
 * Aquisições de UMA fonte: uma data quando há uma aquisição; intervalo e quantidade quando há mais — nunca reduzido à
 * mais recente. Sem aquisição: a fonte não sustenta a conclusão (base "supporting") ou não há evidência ("—").
 */
export function acquisitionSpanText(
  span: CrossSourceAcquisitionSpan | null | undefined,
  fmt: (iso: string) => string,
  basis?: CrossSourceEvidenceBasis | string,
): string {
  if (!span) return basis === 'supporting' ? 'não sustenta esta conclusão' : '—';
  if (span.acquisitions <= 1) return fmt(span.last);
  return `${fmt(span.first)} → ${fmt(span.last)} (${span.acquisitions} aquisições)`;
}
