/**
 * [AEGIS-RISK-PRIORITIZATION-01] Contratos que sobrevivem à aposentadoria da prioridade por CVE: a criticidade declarada do
 * ativo (inventário), o caso congelado como origem dos planos históricos de dispositivo e o passo de paginação do
 * inventário. Nenhuma prioridade nova é calculada ou exibida.
 */

export type DevicePriorityBand = 'p1' | 'p2' | 'p3' | 'p4' | 'insufficient';

export type DevicePriorityFactorKind = 'sourceFact' | 'declared' | 'inferred' | 'unknown';

export type DevicePriorityEffect = 'basis' | 'determinant' | 'aggravating' | 'none' | 'notUsed';

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
