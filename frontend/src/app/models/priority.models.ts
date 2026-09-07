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
import { VulnerabilityGroup, VulnerabilitySummary } from './vulnerability.models';
import {
  KnightCategory,
  KnightIndicatorStatus,
  KnightSourceState,
  KnightSourceType,
  SeverityLevel,
} from './knight.models';

/** Fila de exposições de configuração: resumo tenant-scoped + os principais itens ABERTOS (≤5, ordem da fonte). */
export interface PriorityExposureQueue {
  summary: PostureExposureSummary;
  top: PostureExposureItem[];
}

/**
 * [AEGIS-MVP-LANGUAGE-02] Fila de vulnerabilidades por GRUPO/CVE (não mais a mesma ocorrência ativo×CVE repetida):
 * resumo multicloud + os principais GRUPOS ABERTOS (≤5, ordem determinística). O ALCANCE de cada item é a
 * quantidade de ativos afetados (`affectedAssetCount`).
 */
export interface PriorityVulnerabilityQueue {
  summary: VulnerabilitySummary;
  top: VulnerabilityGroup[];
}

/**
 * [AEGIS-MVP-PRODUCT-02] UM achado de identidade na Central — VERBATIM da autoridade KNIGHT. Severidade,
 * veredito, evidência, quantidade afetada e data vêm da avaliação persistida: a Central não recalcula nada e
 * não reordena por critério próprio.
 */
export interface PriorityKnightFinding {
  indicatorId: string;
  title: string;
  category: KnightCategory;
  severity: SeverityLevel;
  status: KnightIndicatorStatus;
  evidence: string;
  affectedObjectCount: number;
  /** `true` quando a avaliação preservou os objetos — é o que autoriza o link "ver afetados". */
  hasAffectedDetail: boolean;
  collectedAt: string;
}

/**
 * [AEGIS-MVP-PRODUCT-02] FILA de achados de identidade do AEGIS KNIGHT — a TERCEIRA fila, e não uma quarta
 * dimensão somada às outras. O score KNIGHT continua com fórmula própria e aparece aqui apenas identificado
 * como o que é. `runId` é a MESMA avaliação que a tela do KNIGHT mostra — os dois lugares não podem divergir;
 * `null` quando o tenant ainda não tem avaliação alguma (e a Central diz isso, em vez de mostrar zeros).
 */
export interface PriorityKnightQueue {
  runId: string | null;
  isDemo: boolean;
  sourceLabel: string | null;
  sourceType: KnightSourceType | null;
  sourceState: KnightSourceState | null;
  collectedAt: string | null;
  score: number | null;
  coverage: number;
  exposedCount: number;
  notEvaluatedCount: number;
  top: PriorityKnightFinding[];
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
  /** [AEGIS-MVP-PRODUCT-02] Fila de achados de identidade do AEGIS KNIGHT (leitura da avaliação persistida). */
  identityFindings: PriorityKnightQueue;
}
