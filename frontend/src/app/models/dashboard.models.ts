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
