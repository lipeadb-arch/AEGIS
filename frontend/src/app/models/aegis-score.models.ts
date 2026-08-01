// Espelha AegisScore.Application.Queries.TenantTrendDto

/** Um ponto da série temporal de postura do Aegis Score — uma foto agregada diária. */
export interface TenantTrendDto {
  /** Dia lógico da foto. DateOnly no backend → string ISO "yyyy-MM-dd". */
  snapshotDate: string;
  /** Numerador consolidado do dia: SUM(CurrentScore). */
  achievedScore: number;
  /** Denominador consolidado do dia: SUM(MaxScorePoints avaliados). */
  maxScore: number;
  /**
   * Percentual pela fórmula oficial (aegis-score-v1). ANULÁVEL: uma foto 0/0 (nada avaliado no dia) é
   * semanticamente NotEvaluated — `null`, não 0%.
   */
  percentage: number | null;
}

/**
 * Espelha AegisScore.Application.Queries.CurrentScoreDto — Score Atual do tenant pela fórmula oficial
 * (aegis-score-v1). O frontend NÃO recalcula a fórmula: exibe os campos derivados prontos.
 */
export interface CurrentScoreDto {
  /** Numerador: SUM(CurrentScore) dos controles avaliados. */
  achievedScore: number;
  /** Denominador do SCORE: SUM(MaxScorePoints) dos controles AVALIADOS. */
  evaluatedMaxScore: number;
  /** Denominador da COBERTURA: SUM(MaxScorePoints) do catálogo ativo. */
  eligibleMaxScore: number;
  /** Nº de controles avaliados (compõem o score). */
  evaluatedControls: number;
  /** Nº de subcategorias do catálogo ativo. */
  eligibleControls: number;
  /** Nº de controles ainda sem avaliação (elegíveis − avaliados). */
  notEvaluatedControls: number;
  /** Versão oficial da fórmula aplicada ("aegis-score-v1"). */
  formulaVersion: string;
  /** "NotEvaluated" (sem score) ou "Evaluated" (o percentual existe, podendo ser 0% real). */
  evaluationState: string;
  /** Percentual do score — NULL quando nada foi avaliado (NotEvaluated); NUNCA 0% nesse caso. */
  percentage: number | null;
  /** Cobertura de avaliação (%) — peso avaliado / peso elegível. Distinta do score. */
  coveragePercentage: number;
}
