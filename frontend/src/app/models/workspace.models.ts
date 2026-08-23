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
  enabled: boolean;
}

/**
 * Saúde OPERACIONAL dos conectores: `healthy`/`degraded`/`failed`/`neverSynced` contam SOMENTE os
 * habilitados (particionam `enabled`). O desabilitado fica fora do denominador operacional (`disabled`),
 * e nunca-sincronizado jamais é saudável. O Dashboard mostra `healthy/enabled`, não `healthy/configured`.
 */
export interface ConnectorHealthSummary {
  configured: number;
  enabled: number;
  disabled: number;
  healthy: number;
  degraded: number;
  failed: number;
  neverSynced: number;
  lastSyncAt: string | null;
  items: ConnectorHealthItem[];
}

/** Resumo curto e honesto do que NÃO está saudável (só as partes não-zero), ou "todos operacionais". */
export function connectorBreakdown(c: ConnectorHealthSummary): string {
  const parts: string[] = [];
  if (c.degraded > 0) parts.push(`${c.degraded} degradado${c.degraded > 1 ? 's' : ''}`);
  if (c.failed > 0) parts.push(`${c.failed} com falha`);
  if (c.neverSynced > 0) parts.push(`${c.neverSynced} nunca sincronizado${c.neverSynced > 1 ? 's' : ''}`);
  if (c.disabled > 0) parts.push(`${c.disabled} desabilitado${c.disabled > 1 ? 's' : ''}`);
  if (parts.length === 0) return c.enabled > 0 ? 'todos operacionais' : 'nenhum conector habilitado';
  return parts.join(' · ');
}

/**
 * [AEGIS-MVP-ENV-01] Uma fatia de cobertura por NATUREZA da prova esperada. É só COBERTURA (peso avaliado ÷
 * elegível), NUNCA score: 100% coberto pode conter controles não conformes, e 0% significa "não avaliado",
 * jamais reprovação. Sem score por bucket — a entrega trata de cobertura, não de quatro scores concorrentes.
 */
export interface EvidenceCoverageSlice {
  eligibleControls: number;
  evaluatedControls: number;
  eligibleMaxScore: number;
  evaluatedMaxScore: number;
  coveragePercentage: number;
}

/**
 * Cobertura recortada pela NATUREZA da medição esperada. Os quatro buckets PARTICIONAM o mesmo universo
 * elegível de `overall` (framework ativo); a autoridade de classificação é o `EvidenceType` tipado da regra —
 * a ausência de regra é o estado próprio `notAutomated` (nunca reclassificado como documentação).
 */
export interface EvidenceCoverageSummary {
  telemetry: EvidenceCoverageSlice; // Ambiente e telemetria
  documentation: EvidenceCoverageSlice; // Governança e evidência dirigida
  both: EvidenceCoverageSlice; // Evidência híbrida
  notAutomated: EvidenceCoverageSlice; // Avaliação orientada
}

export interface WorkspacePosture {
  overall: WorkspaceOverall;
  functions: FunctionPosture[];
  connectors: ConnectorHealthSummary;
  evidenceCoverage: EvidenceCoverageSummary;
}

/** Natureza da prova esperada — chave estável dos quatro buckets de cobertura. */
export type EvidenceNature = 'telemetry' | 'documentation' | 'both' | 'notAutomated';

/** Um bucket já com rótulo e texto de ajuda para a UI (a natureza da medição, não a existência de um PDF). */
export interface EvidenceNatureView {
  key: EvidenceNature;
  label: string;
  help: string;
  slice: EvidenceCoverageSlice;
}

/**
 * Rótulos e ajuda por natureza — a classificação descreve a NATUREZA ESPERADA da medição, não a existência de
 * um documento. `documentation` é governança/evidência dirigida (entrevista, evidência literal OU documento),
 * jamais "faça upload de todas as políticas"; `notAutomated` é a fronteira atual da automação, nunca falha do usuário.
 */
export function evidenceNatures(ec: EvidenceCoverageSummary): EvidenceNatureView[] {
  return [
    {
      key: 'telemetry',
      label: 'Ambiente e telemetria',
      help: 'Controles cuja medição esperada vem do ambiente — cloud, identidade, ativos, EDR. Coletáveis por conector.',
      slice: ec.telemetry,
    },
    {
      key: 'documentation',
      label: 'Governança e evidência dirigida',
      help: 'Controles organizacionais, comprovados por evidência dirigida, entrevista ou documento — não necessariamente um PDF.',
      slice: ec.documentation,
    },
    {
      key: 'both',
      label: 'Evidência híbrida',
      help: 'Exigem as duas naturezas: sinal do ambiente E evidência de governança.',
      slice: ec.both,
    },
    {
      key: 'notAutomated',
      label: 'Avaliação orientada',
      help: 'Controles ainda sem regra de automação. Não é falha sua — é a fronteira atual da automação.',
      slice: ec.notAutomated,
    },
  ];
}

/** Etapa da jornada environment-first, derivada EXCLUSIVAMENTE da projeção do workspace (ver `environmentStage`). */
export type EnvironmentStage = 'no-connector' | 'never-synced' | 'synced-no-tech-coverage' | 'measured';

/**
 * Deriva a etapa environment-first por FUNÇÃO PURA sobre a projeção — sem estado próprio nem chamada extra:
 *  • A `no-connector`         — nenhum conector habilitado;
 *  • B `never-synced`         — habilitado(s), porém nenhum jamais sincronizou;
 *  • C `synced-no-tech-coverage` — houve sincronização, mas ainda sem cobertura técnica (telemetria avaliada = 0);
 *  • D `measured`             — já existe cobertura técnica (telemetria avaliada > 0).
 */
export function environmentStage(w: WorkspacePosture): EnvironmentStage {
  const c = w.connectors;
  if (c.enabled === 0) return 'no-connector';
  const everSynced = c.items.some((i) => i.enabled && i.everSynced);
  if (!everSynced) return 'never-synced';
  if (w.evidenceCoverage.telemetry.evaluatedControls === 0) return 'synced-no-tech-coverage';
  return 'measured';
}

/** Rótulo de postura para a UI: "Não avaliado" quando não há score; caso contrário o percentual com 1 casa. */
export function postureLabel(percentage: number | null): string {
  return percentage === null ? 'Não avaliado' : `${percentage.toFixed(1)}%`;
}

/** Função NIST específica na projeção, ou `undefined` se ausente (catálogo sem aquela Função ativa). */
export function functionOf(w: WorkspacePosture | null, code: string): FunctionPosture | undefined {
  return w?.functions.find((f) => f.code === code);
}
