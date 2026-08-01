using AegisScore.Application.Scoring;

namespace AegisScore.Application.Queries;

/// <summary>
/// Um ponto da série temporal de postura do Aegis Score (uma foto diária). Alimenta o gráfico de
/// tendência no Angular, no modelo Microsoft Secure Score.
/// </summary>
public record TenantTrendDto(DateOnly SnapshotDate, int AchievedScore, int MaxScore)
{
    /// <summary>
    /// [AEGIS-AUD-001/002] Percentual de postura no dia pela MESMA autoridade da fórmula
    /// (<see cref="AegisScoreFormulaV1"/>). ANULÁVEL de propósito: uma foto 0/0 (nenhum controle avaliado
    /// naquele dia) é semanticamente NotEvaluated — <c>null</c>, não 0%.
    /// </summary>
    public double? Percentage => MaxScore <= 0
        ? null
        : AegisScoreFormulaV1.RoundPercentage((double)AchievedScore / MaxScore * 100);
}

/// <summary>
/// Consulta de leitura da série temporal do Aegis Score. O CONTRATO vive na camada Application (que
/// não conhece EF Core); a implementação que toca o AegisScoreDbContext mora na Infrastructure — o
/// mesmo padrão porta/adaptador de <c>IAuthService</c>, <c>IAegisAiEvaluatorService</c> etc.
///
/// O tenant NÃO é parâmetro: o isolamento é fail-closed via ITenantContext + Global Query Filter, de
/// modo que a consulta enxerga exclusivamente o tenant ambiente resolvido do token JWT.
/// </summary>
public interface ITenantScoreTrendQuery
{
    /// <summary>
    /// Retorna os snapshots dos últimos <paramref name="days"/> dias (default 30), em ordem
    /// cronológica crescente — o eixo X do gráfico de tendência.
    /// </summary>
    Task<IReadOnlyList<TenantTrendDto>> GetTrendAsync(int days = 30, CancellationToken ct = default);
}
