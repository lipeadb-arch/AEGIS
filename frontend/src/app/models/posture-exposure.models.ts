/**
 * [AEGIS-MVP-POSTURE-02] Contratos da tela de RECOMENDAÇÕES DE POSTURA — fonte: Microsoft Secure Score.
 * Espelham os DTOs do backend (`AegisScore.Application/Queries/PostureExposures.cs`). Os nomes técnicos
 * (`PostureExposure*`, rota `/exposures`) são contrato e ficam; a APRESENTAÇÃO é "Recomendações de postura".
 *
 * ⚠️ NÃO são vulnerabilidades/CVEs de ativos, e a diferença de pontos da fonte não comprova sozinha configuração
 * insegura nem exposição de ativo. Nenhum TenantId trafega — o tenant é resolvido no servidor pela claim do JWT.
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

// ---- [AEGIS-LANGUAGE-STATES-01] Vocabulário e estados de informação das RECOMENDAÇÕES DE POSTURA ----
// A superfície é baseada nas recomendações do Microsoft Secure Score. O que a fonte comprova é a DIFERENÇA DE
// PONTOS de cada recomendação — não, por si só, configuração insegura, exposição de ativo ou vulnerabilidade.

/** Nome consistente da superfície (navegação, títulos, filas e cartões). */
export const POSTURE_RECOMMENDATIONS_LABEL = 'Recomendações de postura';

/** O que "sem pendência na fonte" significa — e o que não significa. */
export const RECOMMENDATION_NO_LONGER_PENDING_HINT =
  'A fonte deixou de apontar diferença de pontos para esta recomendação numa coleta completa. Isso não é ' +
  'validação independente da correção.';

/** Estado do ciclo de vida AEGIS de uma recomendação, na semântica real do coletor. */
export function recommendationLifecyclePt(state: string): string {
  return state === 'Resolved' ? 'Sem pendência na fonte' : 'Pendente';
}

const SOURCE_STATE_PT: Record<string, string> = {
  ignored: 'Ignorada na fonte',
  thirdparty: 'Atendida por terceiro (declarado na fonte)',
  reviewed: 'Revisada na fonte',
  'risk accepted': 'Risco aceito na fonte',
  riskaccepted: 'Risco aceito na fonte',
  planned: 'Planejada na fonte',
};

/**
 * Estado declarado na FONTE (metadado do Secure Score, não do AEGIS). `Default` não vira selo; valor
 * desconhecido passa como está, identificado como da fonte — nunca é traduzido por suposição.
 */
export function sourceStatePt(state: string | null): string | null {
  if (!state || state.trim() === '' || state.trim().toLowerCase() === 'default') return null;
  return SOURCE_STATE_PT[state.trim().toLowerCase()] ?? `Estado na fonte: ${state.trim()}`;
}

/** Situação de LEITURA das recomendações — decide cartões, vazio e aviso. */
export type RecommendationReadingState =
  | 'NotConfigured'
  | 'NeverCollected'
  | 'FailedBeforeFirstCollection'
  | 'Available';

export interface RecommendationReading {
  state: RecommendationReadingState;
  /** Existe leitura com números? Só então contagens podem aparecer (inclusive 0). */
  hasData: boolean;
  /** A tentativa MAIS RECENTE falhou — com dados, eles são a última leitura disponível. */
  lastAttemptFailed: boolean;
  /**
   * A coleta mais recente concluiu com restrições (`Degraded`). Na semântica do executor isso NÃO significa
   * recomendações parciais — a completude delas é independente —, só que a coleta registrou restrições.
   */
  lastAttemptDegraded: boolean;
  /** Frase para o estado vazio ou o aviso que acompanha os números; `null` quando nada a ressalvar. */
  notice: string | null;
}

/**
 * Deriva o que a tela pode afirmar a partir do resumo. `null`/ausência NUNCA vira zero: sem leitura, as
 * contagens ficam "—" e o motivo é dito (sem integração, sem coleta, primeira tentativa falhou). Com leitura e
 * tentativa recente falha, os dados anteriores continuam visíveis COM o aviso.
 */
export function recommendationReading(s: PostureExposureSummary | null | undefined): RecommendationReading {
  if (!s) return { state: 'NeverCollected', hasData: false, lastAttemptFailed: false, lastAttemptDegraded: false, notice: null };
  const failed = s.lastAttemptStatus === 'Failed';
  const degraded = s.lastAttemptStatus === 'Degraded';
  const hasData = s.lastCollectedAt != null || s.totalOpen > 0 || s.totalResolved > 0;
  // Contrato anterior (sem sourceConfigured): trata como configurado — nunca afirma "sem integração" sem prova.
  const configured = s.sourceConfigured ?? true;

  if (hasData) {
    // Mesma precedência do backend (CollectionReadings.PostureRecommendations): falha > restrições > data
    // desconhecida. Nenhuma delas esconde os números.
    return {
      state: 'Available',
      hasData: true,
      lastAttemptFailed: failed,
      lastAttemptDegraded: degraded,
      notice: failed
        ? `A tentativa mais recente de coleta falhou. Os números abaixo são a última leitura disponível` +
          (s.lastCollectedAt ? ` (${formatStamp(s.lastCollectedAt)}).` : '.')
        : degraded
          ? 'A coleta mais recente terminou com restrições registradas pela integração. Os números abaixo são os ' +
            'que ela entregou; confira o detalhe em Integrações.'
          : s.lastCollectedAt
            ? null
            : 'Há recomendações registradas, mas a data da última coleta não é conhecida.',
    };
  }
  if (!configured) {
    return {
      state: 'NotConfigured',
      hasData: false,
      lastAttemptFailed: false,
      lastAttemptDegraded: false,
      notice: 'Nenhuma integração com o Microsoft Secure Score está configurada neste ambiente.',
    };
  }
  if (failed) {
    return {
      state: 'FailedBeforeFirstCollection',
      hasData: false,
      lastAttemptFailed: true,
      lastAttemptDegraded: false,
      notice: 'A integração está configurada, mas a tentativa mais recente de coleta falhou antes de qualquer leitura.',
    };
  }
  return {
    state: 'NeverCollected',
    hasData: false,
    lastAttemptFailed: false,
    lastAttemptDegraded: false,
    notice: 'A integração está configurada, mas nenhuma coleta foi concluída ainda.',
  };
}

function formatStamp(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString('pt-BR');
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
  /** Índice geral do Microsoft Secure Score (índice DA FONTE, não o AEGIS Score); null sem coleta. */
  latestSecureScorePercent: number | null;
  latestSecureScoreAt: string | null;
  /** [AEGIS-LANGUAGE-STATES-01] Existe integração Microsoft Secure Score configurada no ambiente? */
  sourceConfigured?: boolean;
  /** Desfecho da tentativa MAIS RECENTE (Healthy/Degraded/Failed/Syncing/Unknown); null sem integração. */
  lastAttemptStatus?: string | null;
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
