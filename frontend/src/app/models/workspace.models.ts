// Espelha AegisScore.Application.Queries.WorkspacePostureDto — a projeção ÚNICA do workspace
// (GET /api/v1/scoring/workspace). Postura geral e por Função NIST pela fórmula aegis-score-v1 +
// saúde/recência dos conectores. O frontend NÃO recalcula score, cobertura nem contagens.

export type EvaluationState = 'Evaluated' | 'NotEvaluated';

export interface SeverityCount {
  severity: string; // "Critical" | "Medium" | "Low" | ...
  count: number;
}

/** Postura consolidada do tenant (todas as Funções). `percentage` NULO = NotEvaluated (nunca 0% por ausência). */
export interface WorkspaceOverall {
  formulaVersion: string;
  evaluationState: EvaluationState;
  percentage: number | null;
  coveragePercentage: number;
  achievedScore: number;
  evaluatedMaxScore: number;
  eligibleMaxScore: number;
  eligibleControls: number;
  evaluatedControls: number;
  compliantControls: number;
  nonCompliantControls: number;
  mitigatedControls: number;
  notEvaluatedControls: number;
  severities: SeverityCount[];
  latestEvidenceAt: string | null; // ISO 8601 (recência da evidência)
}

/** Resumo de UMA Função NIST — MESMO contrato de postura da visão geral (compartilhado entre as seis telas). */
export interface FunctionPosture {
  code: string; // "PR"
  name: string; // "PROTECT"
  evaluationState: EvaluationState;
  percentage: number | null;
  coveragePercentage: number;
  eligibleControls: number;
  evaluatedControls: number;
  compliantControls: number;
  nonCompliantControls: number;
  mitigatedControls: number;
  notEvaluatedControls: number;
}

export interface ConnectorHealthItem {
  id: string;
  displayName: string;
  provider: string;
  capability: string;
  status: string; // "Healthy" | "Degraded" | "Failed" | "Unknown"
  lastSyncAt: string | null;
  everSynced: boolean;
}

export interface ConnectorHealthSummary {
  total: number;
  healthy: number;
  degraded: number;
  failed: number;
  neverSynced: number;
  lastSyncAt: string | null;
  items: ConnectorHealthItem[];
}

export interface WorkspacePosture {
  overall: WorkspaceOverall;
  functions: FunctionPosture[];
  connectors: ConnectorHealthSummary;
}

/** Rótulo de postura para a UI: "Não avaliado" quando não há score; caso contrário o percentual com 1 casa. */
export function postureLabel(percentage: number | null): string {
  return percentage === null ? 'Não avaliado' : `${percentage.toFixed(1)}%`;
}

/** Função NIST específica na projeção, ou `undefined` se ausente (catálogo sem aquela Função ativa). */
export function functionOf(w: WorkspacePosture | null, code: string): FunctionPosture | undefined {
  return w?.functions.find((f) => f.code === code);
}
