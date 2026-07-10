namespace AegisScore.Application.Queries;

/// <summary>
/// Um ponto da série temporal de postura do Aegis Score (uma foto diária). Alimenta o gráfico de
/// tendência no Angular, no modelo Microsoft Secure Score.
/// </summary>
public record TenantTrendDto(DateOnly SnapshotDate, int AchievedScore, int MaxScore)
{
    /// <summary>
    /// Percentual de postura no dia, calculado em runtime — AchievedScore / MaxScore × 100 — e
    /// arredondado a 1 casa decimal (convenção de formatação do projeto). Blindado contra divisão
    /// por zero: sem denominador, o percentual é 0 em vez de NaN/Infinity.
    /// </summary>
    public double Percentage => MaxScore == 0
        ? 0
        : Math.Round((double)AchievedScore / MaxScore * 100, 1);
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
