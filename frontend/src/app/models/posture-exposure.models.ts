/**
 * [AEGIS-MVP-POSTURE-02] Contratos da tela de Exposições de CONFIGURAÇÃO (postura) — modelo do Microsoft
 * Secure Score. Espelham os DTOs do backend (`AegisScore.Application/Queries/PostureExposures.cs`).
 *
 * ⚠️ NÃO são vulnerabilidades/CVEs de ativos: são "recomendações de postura" / "exposições de configuração".
 * Nenhum TenantId trafega — o tenant é resolvido no servidor pela claim do JWT.
 */

/** Uma exposição de configuração projetada para a tela (sem segredo, sem actionUrl, sem PII). */
export interface PostureExposureItem {
  id: string;
  externalId: string;
  title: string;
  category: string | null;
  service: string | null;
  actionType: string | null;
  currentScore: number;
  maxScore: number;
  gap: number;
  sourceRank: number | null;
  tier: string | null;
  implementationCost: string | null;
  userImpact: string | null;
  remediation: string | null;
  remediationImpact: string | null;
  threats: string[];
  sourceState: string | null;
  lifecycleState: 'Open' | 'Resolved' | string;
  firstSeenAt: string;
  lastSeenAt: string;
  resolvedAt: string | null;

  // ---- [AEGIS-MVP-LANGUAGE-02] Camada CLARA (autoral) + texto de FONTE sanitizado (secundário) ----
  /** Título claro (autoral) OU, sem catálogo, o título de fonte sanitizado (SourceOnly). Nunca vazio. */
  displayTitle: string;
  /** O que significa (autoral). Nulo em SourceOnly. */
  plainSummary: string | null;
  /** Por que importa (autoral). Nulo em SourceOnly. */
  whyItMatters: string | null;
  /** Primeira ação (autoral) OU a remediação de fonte sanitizada (fallback). */
  firstAction: string | null;
  /** Título original da fonte, sanitizado — referência técnica secundária. */
  sourceTitle: string | null;
  /** Remediação original da fonte, sanitizada — referência técnica secundária (não bloco bruto). */
  sourceRemediation: string | null;
  /** Impacto da remediação original da fonte, sanitizado. */
  sourceRemediationImpact: string | null;
  /** "Localized" (há redação autoral) ou "SourceOnly" (fallback de fonte). */
  languageCoverage: 'Localized' | 'SourceOnly' | string;
}

// ---- [AEGIS-MVP-LANGUAGE-02] Vocabulário VISÍVEL traduzido deterministicamente (puro, testável) ----
// Termos técnicos da fonte → pt-BR. O valor original permanece disponível na área técnica quando útil.

const CATEGORY_PT: Record<string, string> = {
  device: 'Dispositivos',
  apps: 'Aplicativos',
  identity: 'Identidades',
  data: 'Dados',
};
const TIER_PT: Record<string, string> = {
  core: 'Essencial',
  'defense in depth': 'Defesa em profundidade',
  advanced: 'Avançado',
};
const IMPACT_PT: Record<string, string> = {
  low: 'Baixo',
  moderate: 'Moderado',
  high: 'Alto',
};
const ACTION_TYPE_PT: Record<string, string> = {
  config: 'Configuração',
  review: 'Revisão',
  behavior: 'Comportamento',
};

function translate(map: Record<string, string>, value: string | null): string | null {
  if (value === null || value.trim() === '') return value;
  return map[value.trim().toLowerCase()] ?? value; // desconhecido passa direto (nunca inventa)
}

export const categoryPt = (v: string | null): string | null => translate(CATEGORY_PT, v);
export const tierPt = (v: string | null): string | null => translate(TIER_PT, v);
/** Nível de custo/impacto (Low/Moderate/High → Baixo/Moderado/Alto). */
export const impactPt = (v: string | null): string | null => translate(IMPACT_PT, v);
export const actionTypePt = (v: string | null): string | null => translate(ACTION_TYPE_PT, v);

/** Alcance por ativo NÃO é informado pelo Secure Score — mostre isto honestamente, nunca invente contagem. */
export const EXPOSURE_REACH_UNKNOWN = 'Alcance por ativo não informado pela fonte';

/** Contagem de exposições ABERTAS por categoria (distribuição do resumo). */
export interface PostureExposureCategoryCount {
  category: string;
  open: number;
}

/** Resumo da postura de exposição do tenant. */
export interface PostureExposureSummary {
  sourceLabel: string;
  totalOpen: number;
  totalResolved: number;
  openByCategory: PostureExposureCategoryCount[];
  /** null = "Ainda não coletado" (NUNCA 0). */
  lastCollectedAt: string | null;
  /** Secure Score geral mais recente coletado; null quando ainda não há coleta. */
  latestSecureScorePercent: number | null;
  latestSecureScoreAt: string | null;
}

/** Página de exposições + resumo. `total` é a contagem FILTRADA (para paginação). */
export interface PostureExposureList {
  summary: PostureExposureSummary;
  items: PostureExposureItem[];
  total: number;
  page: number;
  pageSize: number;
}

/** Filtro de estado do ciclo de vida AEGIS (não confundir com o `sourceState`, metadado da fonte). */
export type PostureExposureStateFilter = 'open' | 'resolved' | 'all';

/** Parâmetros de consulta da listagem. */
export interface PostureExposureQueryParams {
  state?: PostureExposureStateFilter;
  category?: string;
  service?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}
