/**
 * [AEGIS-MVP-MICROSOFT-COVERAGE-01] Contratos da aba "Software exposto" (inventário/exposição de software).
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
