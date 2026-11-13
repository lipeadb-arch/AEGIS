// ============================================================================
//  [AEGIS-KNIGHT-CIS-01] Visão CIS Benchmarks do AEGIS KNIGHT
// ============================================================================
// A projeção (resultado por recomendação, regras, recursos, mapeamento) é feita no BACKEND e é a mesma que a publicação congela
// para o HTML. Aqui ficam só os tipos e a lógica pura de leitura: filtros, contagens do recorte, segmentos da barra e textos.
// Nada aqui decide um resultado — a tela mostra o que a projeção decidiu, com o motivo.

export type CisStatus = 'Compliant' | 'NonCompliant' | 'NotEvaluated' | 'NotApplicable' | 'OutOfScope';

export interface CisCounts {
  compliant: number;
  nonCompliant: number;
  notEvaluated: number;
  notApplicable: number;
  outOfScope: number;
  considered: number;
  evaluated: number;
  total: number;
}

export interface CisLink { title: string; url: string; }

export interface CisMapping {
  catalogVersion: string;
  referenceRepository: string;
  referenceCommit: string;
  traceRepository: string;
  traceCommit: string;
  reliableCatalogVersions: string[];
  note: string;
}

export interface CisOriginSource {
  source: string;
  label: string;
  availability: string;
  availabilityLabel: string;
  included: boolean;
  runId: string | null;
  catalogVersion: string | null;
  capturedAt: string | null;
  sourceState: string | null;
  sourceStateLabel: string | null;
  mappingReliable: boolean;
  note: string | null;
}

export interface CisOrigin { label: string; dataRecency: string | null; sources: CisOriginSource[]; }

export interface CisProfileOption { key: string; label: string; recommendations: number; counts: CisCounts; }

export interface CisBenchmark {
  key: string;
  framework: string;
  version: string;
  label: string;
  url: string;
  ruleset: string;
  recommendations: number;
  catalogEntries: number;
  counts: CisCounts;
  manualCounts: CisCounts;
  mitigated: number;
  profiles: CisProfileOption[];
  profileNotRecorded: number;
  profileDivergent: number;
}

export interface CisRuleRef { indicatorId: string; match: string; matchLabel: string; note: string | null; proof: string; proofLabel: string; }

export interface CisVariant {
  key: string;
  variant: string | null;
  title: string;
  status: CisStatus;
  statusLabel: string;
  reason: string | null;
  disposition: string;
  dispositionLabel: string;
  dispositionNote: string | null;
  traceRule: string | null;
  rules: CisRuleRef[];
}

export interface CisManual {
  result: string;
  resultLabel: string;
  justification: string;
  responsibleName: string;
  evidenceReference: string | null;
  evidenceDocumentTitle: string | null;
  evidenceDocumentSha256: string | null;
  validUntil: string | null;
  expired: boolean;
  recordedByName: string;
  recordedAt: string;
  complement: boolean;
}

export interface CisRecommendation {
  id: string;
  benchmark: string;
  section: string;
  title: string;
  service: string;
  serviceLabel: string;
  platform: string;
  severity: string;
  severityLabel: string;
  profileState: string;
  profileStateLabel: string;
  profiles: string[];
  profileLabels: string[];
  status: CisStatus;
  statusLabel: string;
  origin: 'Automated' | 'Manual' | 'AutomatedAndManual' | 'None';
  originLabel: string;
  mitigated: boolean;
  reason: string | null;
  whatChecked: string;
  found: string;
  whyItMatters: string | null;
  howToFix: string | null;
  links: CisLink[];
  limitations: string[];
  variants: CisVariant[];
  ruleIds: string[];
  manual: CisManual | null;
}

export interface CisRule {
  indicatorId: string;
  title: string;
  source: string;
  sourceLabel: string;
  runId: string | null;
  catalogVersion: string | null;
  status: string;
  statusLabel: string;
  evidence: string;
  notEvaluatedReason: string | null;
  affectedCount: number;
  affectedComposition: string | null;
  evidenceCount: number;
  detailLimitation: string | null;
  expected: string | null;
  criterion: string | null;
  recommendation: string | null;
  links: CisLink[];
  recommendations: string[];
}

export interface CisOwnCheck { indicatorId: string; title: string; source: string; sourceLabel: string; status: string; statusLabel: string; note: string; }

export interface CisReconciliation {
  knightControls: number;
  knightApplicable: number;
  knightEvaluated: number;
  knightNotApplicable: number;
  rulesInComposition: number;
  rulesLinked: number;
  ownChecks: number;
  recommendations: number;
  considered: number;
  evaluated: number;
  outOfScope: number;
  withoutAutomatedRule: number;
  explanation: string[];
}

export interface KnightCisView {
  schemaVersion: string;
  mapping: CisMapping;
  origin: CisOrigin;
  benchmarks: CisBenchmark[];
  recommendations: CisRecommendation[];
  rules: CisRule[];
  ownChecks: CisOwnCheck[];
  reconciliation: CisReconciliation;
  limitations: string[];
  disclaimer: string;
}

/** Ordem de leitura: o que exige ação primeiro; "Fora do escopo" por último, fora dos totais. */
export const CIS_STATUS_ORDER: CisStatus[] = ['NonCompliant', 'NotEvaluated', 'Compliant', 'NotApplicable', 'OutOfScope'];

const CIS_STATUS_LABEL: Record<CisStatus, string> = {
  Compliant: 'Conforme',
  NonCompliant: 'Não conforme',
  NotEvaluated: 'Não avaliado',
  NotApplicable: 'Não aplicável',
  OutOfScope: 'Fora do escopo',
};

export function cisStatusLabel(s: CisStatus | string): string {
  return CIS_STATUS_LABEL[s as CisStatus] ?? s;
}

/** Filtros da página. `profile` "-" = perfil não registrado pela fonte de rastreio. */
export interface CisFilters { q: string; benchmark: string; service: string; status: '' | CisStatus; profile: string; }

export const EMPTY_CIS_FILTERS: CisFilters = { q: '', benchmark: '', service: '', status: '', profile: '' };

/** Busca por seção ("5.2.2", com ou sem o prefixo do benchmark) ou por palavras do título, sem distinguir acento nem caixa. */
export function matchesCis(r: CisRecommendation, f: CisFilters): boolean {
  if (f.benchmark && r.benchmark !== f.benchmark) return false;
  if (f.service && r.service !== f.service) return false;
  if (f.status && r.status !== f.status) return false;
  if (f.profile === '-') { if (r.profiles.length) return false; }
  else if (f.profile && !r.profiles.includes(f.profile)) return false;
  const q = fold(f.q);
  if (q) {
    const hay = fold(`${r.section} ${r.id} ${r.title}`);
    if (!q.split(/\s+/).every((t) => hay.includes(t))) return false;
  }
  return true;
}

export function filterCis(view: KnightCisView, f: CisFilters): CisRecommendation[] {
  return view.recommendations.filter((r) => matchesCis(r, f));
}

/** Contagens de um recorte, com os mesmos denominadores da projeção (Fora do escopo fica fora de "consideradas"). */
export function cisCounts(recs: CisRecommendation[]): CisCounts {
  const n = (s: CisStatus) => recs.filter((r) => r.status === s).length;
  const c = { compliant: n('Compliant'), nonCompliant: n('NonCompliant'), notEvaluated: n('NotEvaluated'), notApplicable: n('NotApplicable'), outOfScope: n('OutOfScope') };
  const considered = c.compliant + c.nonCompliant + c.notEvaluated + c.notApplicable;
  return { ...c, considered, evaluated: c.compliant + c.nonCompliant, total: considered + c.outOfScope };
}

/** "222 com veredito de 423 consideradas (47 conformes, …, 12 fora do escopo)" — os números do recorte com o denominador. */
export function cisCountsText(c: CisCounts): string {
  const parts = [`${c.compliant} conformes`, `${c.nonCompliant} não conformes`, `${c.notEvaluated} não avaliadas`, `${c.notApplicable} não aplicáveis`];
  if (c.outOfScope) parts.push(`${c.outOfScope} fora do escopo`);
  return `${c.evaluated} com veredito de ${c.considered} consideradas (${parts.join(', ')})`;
}

export function countOf(c: CisCounts, s: CisStatus): number {
  switch (s) {
    case 'Compliant': return c.compliant;
    case 'NonCompliant': return c.nonCompliant;
    case 'NotEvaluated': return c.notEvaluated;
    case 'NotApplicable': return c.notApplicable;
    default: return c.outOfScope;
  }
}

export interface CisSegment { status: CisStatus; label: string; count: number; percent: number; }

/** Segmentos da barra (só os não vazios), na ordem de leitura; as larguras somam 100 sobre o TOTAL do benchmark. */
export function cisSegments(c: CisCounts): CisSegment[] {
  const total = c.total;
  return CIS_STATUS_ORDER
    .map((s) => ({ status: s, label: cisStatusLabel(s), count: countOf(c, s), percent: total ? (100 * countOf(c, s)) / total : 0 }))
    .filter((x) => x.count > 0);
}

/** Texto acessível da barra: cada estado com o número (a cor nunca é a única informação). */
export function cisBarLabel(name: string, c: CisCounts): string {
  return `${name}: ` + CIS_STATUS_ORDER.map((s) => `${cisStatusLabel(s)} ${countOf(c, s)}`).join(', ');
}

/** "22 de 88 com veredito (25%)" — o denominador sempre junto do número. */
export function ratio(n: number, d: number, noun: string): string {
  if (!d) return `nenhuma ${noun}`;
  return `${n} de ${d} ${noun} (${pct((100 * n) / d)})`;
}

export function pct(v: number): string {
  return `${(Math.round(v * 10) / 10).toString().replace('.', ',')}%`;
}

/** Perfis oferecidos no filtro: os do benchmark escolhido (ou de todos), na ordem da projeção, sem repetir. */
export function profileOptions(view: KnightCisView, benchmark: string): CisProfileOption[] {
  const seen = new Set<string>();
  const out: CisProfileOption[] = [];
  for (const b of view.benchmarks) {
    if (benchmark && b.key !== benchmark) continue;
    for (const p of b.profiles) if (!seen.has(p.key)) { seen.add(p.key); out.push(p); }
  }
  return out;
}

export function serviceOptions(view: KnightCisView): { key: string; label: string }[] {
  const seen = new Map<string, string>();
  for (const r of view.recommendations) if (!seen.has(r.service)) seen.set(r.service, r.serviceLabel);
  return [...seen].map(([key, label]) => ({ key, label })).sort((a, b) => a.label.localeCompare(b.label, 'pt-BR'));
}

export function benchmarkLabel(view: KnightCisView, key: string): string {
  return view.benchmarks.find((b) => b.key === key)?.label ?? key;
}

/** O recorte aplicado em palavras (nunca um filtro silencioso). */
export function describeCisFilters(view: KnightCisView, f: CisFilters): string {
  const p: string[] = [];
  if (f.benchmark) p.push(`benchmark: ${benchmarkLabel(view, f.benchmark)}`);
  if (f.service) p.push(`serviço: ${view.recommendations.find((r) => r.service === f.service)?.serviceLabel ?? f.service}`);
  if (f.status) p.push(`resultado: ${cisStatusLabel(f.status)}`);
  if (f.profile === '-') p.push('perfil não registrado na fonte de rastreio');
  else if (f.profile) p.push(`perfil: ${profileOptions(view, '').find((x) => x.key === f.profile)?.label ?? f.profile}`);
  if (f.q.trim()) p.push(`pesquisa: “${f.q.trim()}”`);
  return p.length ? p.join(' · ') : 'todas as recomendações';
}

/** Linhas "regra × variante" do detalhe, com o resultado da regra e o que ele provou contra o critério oficial. */
export function ruleRows(view: KnightCisView, r: CisRecommendation): { ref: CisRuleRef; variant: CisVariant; rule: CisRule | null }[] {
  const byId = new Map(view.rules.map((x) => [x.indicatorId, x] as const));
  return r.variants.flatMap((v) => v.rules.map((ref) => ({ ref, variant: v, rule: byId.get(ref.indicatorId) ?? null })));
}

/** O que a seção "Como corrigir" diz quando a projeção não traz orientação. */
export function fixFallback(r: CisRecommendation): string {
  switch (r.status) {
    case 'Compliant': return 'Nada a corrigir: a evidência comprova o critério.';
    case 'NotApplicable': return 'Nada a corrigir: o critério não se aplica a este ambiente.';
    case 'OutOfScope': return 'A fonte desta recomendação foi retirada do escopo da avaliação; ela não entra nos totais.';
    default: return 'Sem orientação adicional registrada.';
  }
}

function fold(s: string): string {
  return (s ?? '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().trim();
}

// ---- Limitações por fonte, agrupadas por causa (visão Geral) -------------------------------------------------------

export interface SourceLimitationGroup { cause: string; items: { capability: string; detail: string | null }[]; }

/**
 * Agrupa as limitações LEGÍVEIS de uma fonte ("Capacidade: Causa — detalhe") por causa: "Autorização recusada · 5 leituras".
 * O detalhe de cada leitura (códigos, endpoints, frases longas) fica para a área recolhida.
 */
export function groupSourceLimitations(readable: string[]): SourceLimitationGroup[] {
  const groups = new Map<string, SourceLimitationGroup>();
  for (const x of readable) {
    const i = x.indexOf(': ');
    const capability = i > 0 ? x.slice(0, i) : x;
    const rest = i > 0 ? x.slice(i + 2) : '';
    const j = rest.indexOf(' — ');
    const cause = (j >= 0 ? rest.slice(0, j) : rest) || 'Limitação registrada';
    const g = groups.get(cause) ?? { cause, items: [] };
    g.items.push({ capability, detail: j >= 0 ? rest.slice(j + 3) : null });
    groups.set(cause, g);
  }
  return [...groups.values()];
}
