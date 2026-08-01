using AegisScore.Application.Scoring;

namespace AegisScore.Application.Queries;

/// <summary>
/// [AEGIS-AUD-001/002] Score Atual do tenant em TEMPO REAL a partir do <c>TenantControlState</c> — o KPI
/// hero do HUD, consolidado pela autoridade ÚNICA <see cref="AegisScoreFormulaV1"/>. Expõe os agregados
/// crus (numerador, pesos avaliado/elegível, contagens) e os derivados oficiais (percentual ANULÁVEL,
/// cobertura, estado de avaliação); o frontend NÃO recalcula a fórmula.
/// </summary>
/// <param name="AchievedScore">SUM(CurrentScore) dos controles avaliados (numerador).</param>
/// <param name="EvaluatedMaxScore">SUM(MaxScorePoints) dos controles AVALIADOS (denominador do score).</param>
/// <param name="EvaluatedControls">Nº de controles avaliados (têm <c>TenantControlState</c>).</param>
/// <param name="EligibleMaxScore">SUM(MaxScorePoints) do catálogo ativo (denominador de cobertura).</param>
/// <param name="EligibleControls">Nº de subcategorias do catálogo ativo.</param>
public record CurrentScoreDto(
    int AchievedScore, int EvaluatedMaxScore, int EvaluatedControls, int EligibleMaxScore, int EligibleControls)
{
    private AegisScoreResult Result => AegisScoreFormulaV1.Compute(
        AchievedScore, EvaluatedMaxScore, EvaluatedControls, EligibleMaxScore, EligibleControls);

    /// <summary>Versão oficial da fórmula aplicada (rastreabilidade histórica).</summary>
    public string FormulaVersion => Result.FormulaVersion;

    /// <summary>Controles do catálogo ainda SEM avaliação (elegíveis − avaliados).</summary>
    public int NotEvaluatedControls => Result.NotEvaluatedControls;

    /// <summary>Estado de avaliação como texto ("NotEvaluated"/"Evaluated") — separa "sem score" de "0%".</summary>
    public string EvaluationState => Result.EvaluationState.ToString();

    /// <summary>
    /// Percentual de postura — ANULÁVEL de propósito: <c>null</c> quando nada foi avaliado (NotEvaluated),
    /// NUNCA 0%. Com avaliação, é o score real (podendo ser 0% quando todos os avaliados são NonCompliant).
    /// </summary>
    public double? Percentage => Result.Percentage;

    /// <summary>Cobertura: peso avaliado / peso elegível do catálogo ativo — distinta do score.</summary>
    public double CoveragePercentage => Result.CoveragePercentage;
}

/// <summary>
/// Consulta de leitura do Score Atual (instantâneo) do Aegis Score. O CONTRATO vive na Application (que
/// não conhece EF Core); a implementação que toca o AegisScoreDbContext mora na Infrastructure — mesmo
/// padrão porta/adaptador de <see cref="ITenantScoreTrendQuery"/> e <see cref="IGetPendingControlsQuery"/>.
///
/// O tenant NÃO é parâmetro: o isolamento é fail-closed via ITenantContext + Global Query Filter, de
/// modo que a consulta enxerga exclusivamente o tenant ambiente resolvido do token JWT.
/// </summary>
public interface ICurrentScoreQuery
{
    Task<CurrentScoreDto> GetCurrentAsync(CancellationToken ct = default);
}
