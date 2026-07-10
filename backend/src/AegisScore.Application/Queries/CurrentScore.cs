namespace AegisScore.Application.Queries;

/// <summary>
/// Score Atual do tenant calculado em TEMPO REAL a partir do <c>TenantControlState</c> — o KPI hero do
/// HUD. Mesma matemática da foto diária (SUM CurrentScore / SUM MaxScorePoints das subcategorias
/// avaliadas), mas sem esperar o snapshot da meia-noite: reflete avaliações recém-processadas (ex.: o
/// pilar Govern) no mesmo instante em que caem no banco.
/// </summary>
public record CurrentScoreDto(int AchievedScore, int MaxScore, int EvaluatedControls)
{
    /// <summary>
    /// Percentual de postura AGORA — AchievedScore / MaxScore × 100, arredondado a 1 casa (convenção de
    /// formatação do projeto). Blindado contra divisão por zero: sem controles avaliados, é 0 (não NaN).
    /// </summary>
    public double Percentage => MaxScore == 0
        ? 0
        : Math.Round((double)AchievedScore / MaxScore * 100, 1);
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
