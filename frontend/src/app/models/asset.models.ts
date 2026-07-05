/** Modelo do inventário contínuo de ativos (pilar Identify / ID.AM do NIST CSF 2.0). */

/** Verticais de ativo do NIST CSF 2.0 (serializadas pela API como o nome do enum). */
export type AssetCategory =
  | 'Hardware'
  | 'Software'
  | 'Data'
  | 'People'
  | 'Facilities'
  | 'SupplyChain';

/** Nível de risco calculado pelo motor de IA (nulo enquanto não avaliado). */
export type RiskLevel = 'Baixo' | 'Medio' | 'Alto' | 'Critico';

/** Espelha o AssetDto do backend (Contracts/Dtos.cs). */
export interface AssetDto {
  id: string;
  name: string;
  category: AssetCategory;
  subType: string | null;
  description: string | null;
  criticality: number;
  ownerName: string | null;
  externalRef: string | null;
  businessProcessId: string | null;
  discoverySource: string;
  lastSeenAt: string | null;
  isActive: boolean;
  riskScore: number | null;
  riskLevel: RiskLevel | null;
  riskScoredAt: string | null;
  createdAt: string;
}

/** Envelope de paginação genérico (espelha PagedResult<T>). */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** Filtros combinados da grid tática (todos por AND; categorias por OR entre si). */
export interface AssetQuery {
  category?: AssetCategory[];
  riskLevel?: RiskLevel | null;
  criticality?: number | null;
  isActive?: boolean | null;
  search?: string;
  page: number;
  pageSize: number;
}

/** Rótulos PT das verticais NIST para os chips de filtro e a coluna Categoria. */
export const ASSET_CATEGORIES: ReadonlyArray<{ value: AssetCategory; label: string }> = [
  { value: 'Hardware', label: 'Hardware' },
  { value: 'Software', label: 'Software' },
  { value: 'Data', label: 'Dados' },
  { value: 'People', label: 'Pessoas' },
  { value: 'Facilities', label: 'Instalações' },
  { value: 'SupplyChain', label: 'Cadeia de Supr.' },
];

const CATEGORY_LABELS = new Map<string, string>(ASSET_CATEGORIES.map((c) => [c.value, c.label]));

/** Rótulo PT de uma categoria (fallback: o próprio valor). */
export function categoryLabel(value: string): string {
  return CATEGORY_LABELS.get(value) ?? value;
}

export const RISK_LEVELS: ReadonlyArray<RiskLevel> = ['Baixo', 'Medio', 'Alto', 'Critico'];
