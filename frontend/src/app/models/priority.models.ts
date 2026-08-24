/**
 * [AEGIS-MVP-PRIORITIES-01] Contratos da Central de Prioridades — read model COMPOSTO.
 * Espelha `AegisScore.Application/Queries/PriorityWorkspace.cs`.
 *
 * É PURA COMPOSIÇÃO: reutiliza os tipos já existentes de postura, exposições de configuração e
 * vulnerabilidades. NÃO há um "score geral de risco" combinando NIST × Secure Score × CVSS × EPSS ×
 * criticidade — as dimensões são semanticamente distintas e ficam em DUAS FILAS separadas. Provider-neutral:
 * cada fila carrega e mostra a própria fonte real. Nenhum TenantId trafega — o tenant é resolvido no servidor.
 */

import { WorkspaceOverall } from './workspace.models';
import { PostureExposureItem, PostureExposureSummary } from './posture-exposure.models';
import { VulnerabilityItem, VulnerabilitySummary } from './vulnerability.models';

/** Fila de exposições de configuração: resumo tenant-scoped + os principais itens ABERTOS (≤5, ordem da fonte). */
export interface PriorityExposureQueue {
  summary: PostureExposureSummary;
  top: PostureExposureItem[];
}

/** Fila de vulnerabilidades ativo×CVE: resumo multicloud + as principais exposições ABERTAS (≤5, ordem determinística). */
export interface PriorityVulnerabilityQueue {
  summary: VulnerabilitySummary;
  top: VulnerabilityItem[];
}

/**
 * Read model composto da Central de Prioridades. Reúne, SEM combinar num único índice, três dimensões
 * distintas: postura NIST atual, fila de exposições de configuração e fila de vulnerabilidades em ativos.
 * Ativos afetados e frescor derivam dos resumos já existentes de cada fila — nada é recalculado no cliente.
 */
export interface PriorityWorkspace {
  /** Versão semântica DESTE contrato composto (não é um score). */
  readModelVersion: string;
  /** Instante de geração da leitura (ISO 8601). */
  generatedAt: string;
  /** Postura consolidada atual do tenant (mesma autoridade do Dashboard/Funções). */
  posture: WorkspaceOverall;
  /** Fila de exposições de configuração (resumo + top abertos). */
  configurationExposures: PriorityExposureQueue;
  /** Fila de vulnerabilidades em ativos (resumo + top abertos). */
  vulnerabilities: PriorityVulnerabilityQueue;
}
