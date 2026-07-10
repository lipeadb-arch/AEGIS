namespace AegisScore.Application.Queries;

/// <summary>
/// Contagem de controles pendentes (NIST NonCompliant) do tenant — o KPI "Controles Pendentes" do
/// HUD Tático. Contrato na camada Application (EF-free); a implementação sobre o AegisScoreDbContext
/// vive na Infrastructure. O tenant NÃO é parâmetro: o isolamento é fail-closed via ITenantContext +
/// Global Query Filter.
/// </summary>
public interface IGetPendingControlsQuery
{
    /// <summary>Nº de TenantControlState com Status == NonCompliant no tenant ambiente.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
