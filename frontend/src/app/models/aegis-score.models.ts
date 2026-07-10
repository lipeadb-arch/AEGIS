// Espelha AegisScore.Application.Queries.TenantTrendDto

/** Um ponto da série temporal de postura do Aegis Score — uma foto agregada diária. */
export interface TenantTrendDto {
  /** Dia lógico da foto. DateOnly no backend → string ISO "yyyy-MM-dd". */
  snapshotDate: string;
  /** Numerador consolidado do dia: SUM(CurrentScore). */
  achievedScore: number;
  /** Denominador consolidado do dia: SUM(MaxScorePoints). */
  maxScore: number;
  /** Percentual de postura já calculado no backend (achieved / max × 100, 1 casa). */
  percentage: number;
}

/** Espelha AegisScore.Application.Queries.CurrentScoreDto — Score Atual do tenant em tempo real. */
export interface CurrentScoreDto {
  /** SUM(CurrentScore) das subcategorias avaliadas. */
  achievedScore: number;
  /** SUM(MaxScorePoints) das subcategorias avaliadas. */
  maxScore: number;
  /** Nº de controles avaliados que compõem o cálculo. */
  evaluatedControls: number;
  /** Percentual já calculado no backend (achieved / max × 100, 1 casa). */
  percentage: number;
}
