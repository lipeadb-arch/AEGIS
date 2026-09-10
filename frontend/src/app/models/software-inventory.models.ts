/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-01] Contratos da aba "Inventário de software" (antes rotulada "Software exposto").
 * Espelham os DTOs do backend (`AegisScore.Application/Queries/SoftwareInventory.cs`).
 *
 * A UNIDADE é o PRODUTO consolidado (vendor + nome, SEM versão); os ativos relacionados (com a versão observada)
 * carregam sob demanda ao expandir. Nenhum campo técnico do fornecedor (id do Defender, machineId) trafega — só
 * provider/displayName. Nenhum TenantId trafega: o tenant é resolvido no servidor pela claim do JWT. Software
 * Inventory é evidência OPERACIONAL/de exposição — nunca concede nem remove pontos do AEGIS Score.
 */

/** Um ativo com este produto instalado (prévia ou expansão paginada). */
export interface SoftwareInstalledAssetPreview {
  assetId: string;
  assetName: string;
  criticality: number;
  subType: string | null;
  /** Versão observada NAQUELE ativo (nulo = fonte não informou). */
  version: string | null;
  effectiveState: 'Open' | 'Resolved' | string;
}

/** Uma FONTE (conector) configurada no tenant, com o estado/freshness ESPECÍFICO da dimensão de software. */
export interface SoftwareInventorySource {
  connectorConfigId: string;
  provider: string;
  displayName: string;
  /** Estado dos DADOS armazenados: Available/Partial/NeverCollected. */
  collectionState: string;
  /** Desfecho da tentativa MAIS RECENTE — pode ser de falha mesmo com dados completos preservados. */
  lastAttemptState: string;
  lastAttemptAt: string;
  lastCollectionAt: string | null;
  lastAttemptDetail: string | null;
}

/** Resumo tenant-scoped (KPIs). */
export interface SoftwareInventorySummary {
  totalProducts: number;
  productsWithWeaknesses: number;
  productsWithPublicExploit: number;
  productsWithActiveAlert: number;
  exposedInstallations: number;
  sources: SoftwareInventorySource[];
  /** null = nenhuma fonte com Software.Read.All coletou ainda — distinto de "coletado sem achados". */
  lastCollectedAt: string | null;
  neverCollected: boolean;
}

/** Um produto de software (grão vendor+nome) projetado para a lista priorizada. */
export interface SoftwareProductListItem {
  id: string;
  vendor: string;
  name: string;
  installedDeviceCount: number;
  openInstallationCount: number;
  weaknessesCount: number;
  publicExploit: boolean;
  activeAlert: boolean;
  impactScore: number | null;
  /** Texto determinístico (nunca gerado por IA). */
  firstAction: string;
  sources: string[];
  assetPreview: SoftwareInstalledAssetPreview[];
  assetPreviewTruncated: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
  effectiveState: 'Open' | 'Resolved' | string;
}

/** Página de produtos + resumo. `total` é a contagem FILTRADA (para paginação). */
export interface SoftwareInventoryList {
  summary: SoftwareInventorySummary;
  items: SoftwareProductListItem[];
  total: number;
  page: number;
  pageSize: number;
}

/** Página de ativos relacionados a UM produto (expansão sob demanda). */
export interface SoftwareProductAssets {
  items: SoftwareInstalledAssetPreview[];
  total: number;
  page: number;
  pageSize: number;
}

export type SoftwareObservationStateFilter = 'all' | 'open' | 'resolved';

/** Parâmetros de consulta da listagem. */
export interface SoftwareInventoryQueryParams {
  search?: string;
  vendor?: string;
  publicExploit?: boolean;
  activeAlert?: boolean;
  withWeaknesses?: boolean;
  minImpact?: number;
  maxImpact?: number;
  state?: SoftwareObservationStateFilter;
  assetId?: string;
  page?: number;
  pageSize?: number;
}

// ---- [AEGIS-LANGUAGE-STATES-01] Estados de informação e ciclo de vida do inventário de software ----
// Produto instalado ou observado NÃO é, por si, "software exposto": fraquezas, exploit público e alerta são
// informações da fonte que acompanham o produto, cada uma com o próprio significado.

/** Ciclo de vida do produto/instalação: "Resolved" do coletor = não mais observado pela fonte. */
export function softwareLifecyclePt(state: string): string {
  return state === 'Resolved' ? 'Não mais observado' : 'Observado';
}

export interface SoftwareReading {
  /** Existe leitura com números? Só então contagens podem aparecer (inclusive 0). */
  hasData: boolean;
  /** Estado vazio explicado, ou a ressalva (parcial / última tentativa sem sucesso) que acompanha os números. */
  notice: string | null;
}

const SUCCESSFUL_ATTEMPT = new Set(['Available', 'Partial']);

/**
 * O que a aba pode afirmar sobre a leitura de software. Coleta PARCIAL é piso (não o ambiente inteiro) e uma
 * tentativa recente sem sucesso não esconde os dados preservados — ambos viram aviso junto dos números.
 */
export function softwareReading(s: SoftwareInventorySummary | null | undefined): SoftwareReading {
  if (!s) return { hasData: false, notice: null };

  if (s.neverCollected) {
    if (s.sources.length === 0)
      return { hasData: false, notice: 'Nenhuma fonte Microsoft Defender está configurada neste ambiente.' };
    const blocked = s.sources.find((x) => x.lastAttemptState && !SUCCESSFUL_ATTEMPT.has(x.lastAttemptState)
      && x.lastAttemptState !== 'NeverCollected');
    return {
      hasData: false,
      notice: blocked
        ? `A fonte está configurada, mas a tentativa mais recente não trouxe software: ${softwareCollectionStatePt(blocked.lastAttemptState).toLowerCase()}.`
        : 'A fonte está configurada, mas nenhuma coleta de software foi concluída ainda.',
    };
  }

  const notes: string[] = [];
  if (s.sources.some((x) => x.collectionState === 'Partial'))
    notes.push('Coleta parcial: as contagens são um piso do que a fonte entregou, não o inventário inteiro.');
  const stale = s.sources.filter((x) => x.lastCollectionAt && x.lastAttemptState && !SUCCESSFUL_ATTEMPT.has(x.lastAttemptState));
  if (stale.length > 0)
    notes.push('A tentativa mais recente de coleta não teve sucesso; os números são a última leitura disponível.');
  const pending = s.sources.filter((x) => !x.lastCollectionAt).length;
  if (pending > 0)
    notes.push(`${pending} fonte(s) ainda sem coleta de software — os números cobrem apenas as fontes já coletadas.`);
  return { hasData: true, notice: notes.length ? notes.join(' ') : null };
}

/** Estados de coleta da dimensão de software — pt-BR (mesmo vocabulário do backend, sem zero sintético). */
export function softwareCollectionStatePt(state: string | null | undefined): string {
  switch ((state ?? '').trim()) {
    case 'Available':
      return 'Disponível';
    case 'Partial':
      return 'Parcial';
    case 'InsufficientPermission':
      return 'Permissão insuficiente';
    case 'Unsupported':
      return 'Licença/capacidade insuficiente';
    case 'Unavailable':
      return 'Indisponível';
    default:
      return 'Nunca coletado';
  }
}
