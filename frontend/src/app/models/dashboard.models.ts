// Espelha AegisScore.Api.Contracts.ExecutiveDashboardDto

export interface ExposureCards {
  criticalProcessesExposed: number;
  ineffectiveControls: number;
  overdueActionPlans: number;
  overallMaturity: number;
  targetMaturity: number;
}

export interface RadarPoint {
  function: string; // "GV"
  functionName: string; // "GOVERN (GV)"
  current: number;
  target: number;
}

export interface GapPoint {
  code: string;
  name: string;
  current: number;
  target: number;
  gap: number;
}

export interface HeatCell {
  probability: number; // 1..4
  impact: number; // 1..4
  count: number;
}

export interface RiskLevelCount {
  level: string; // Baixo | Medio | Alto | Critico
  count: number;
}

export interface Icr {
  score: number; // 0..100
  band: string; // Controlado | Moderado | Alto | Critico
}

export interface ExecutiveDashboard {
  clientName: string;
  generatedAt: string;
  exposure: ExposureCards;
  maturityByFunction: RadarPoint[];
  topGaps: GapPoint[];
  riskHeatmap: HeatCell[];
  riskByLevel: RiskLevelCount[];
  icr: Icr;
}

/**
 * Resumo do pior raio de explosão do tenant (espelha `BlastRadiusSummaryDto`). Vem de endpoint
 * PRÓPRIO — não do /executive —, para não entrar no caminho crítico do FCP.
 */
export interface BlastRadiusSummary {
  rootAssetName: string;
  score: number; // 0..100
  riskLevel: string; // Baixo | Medio | Alto | Critico
  impactedAssetCount: number;
  impactedProcessCount: number;
  maxDepth: number;
  assessedAt: string; // ISO 8601
}

/**
 * Balanço CAPEX × OPEX das lacunas de evidência — a pergunta orçamentária da diretoria.
 *
 * Lacuna de TELEMETRIA se fecha comprando/ligando ferramenta (capex); lacuna de DOCUMENTAÇÃO se fecha
 * com processo e gente (opex). Ver `buildGapBalance` em scoring.models.
 */
export interface GapBalance {
  telemetryCount: number;
  documentationCount: number;
  total: number;
  /** 0..100 — fatia de ferramenta; a de processo é o complemento. */
  telemetryPct: number;
  documentationPct: number;
  /** Os controles cegos mais pesados, já ordenados por peso NIST perdido. */
  topBlindSpots: BlindSpotRow[];
}

/** Uma linha do Top N de pontos cegos: o controle, sua natureza dominante e o que está em jogo. */
export interface BlindSpotRow {
  code: string; // "PR.AA-01"
  label: string; // nome amigável do glossário
  nature: 'Telemetry' | 'Documentation' | 'Both';
  sourceIdentifier: string; // "Entra ID" · "Auditoria Manual"
  /** Pontos NIST que este controle deixa de somar — o custo de mantê-lo cego. */
  pointsAtStake: number;
}
