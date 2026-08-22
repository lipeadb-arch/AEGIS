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
}

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
