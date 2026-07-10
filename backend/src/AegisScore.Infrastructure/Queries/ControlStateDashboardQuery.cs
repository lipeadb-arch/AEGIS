using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// Lê a matriz de conformidade do tenant sobre o AegisScoreDbContext.
///
/// Zero Trust: NÃO há <c>.Where(x => x.TenantId == ...)</c>. O Global Query Filter (fail-closed) já
/// restringe TenantControlStates ao tenant ambiente resolvido do JWT — delegar o recorte ao filtro É o
/// próprio isolamento. Um <c>Where</c> explícito seria pior: daria a falsa impressão de que a segurança
/// depende desta linha e não do DbContext, e mascararia um filtro removido por acidente.
///
/// Performance: <c>AsNoTracking</c> (leitura pura, sem change tracker) e projeção direta no banco — o
/// SELECT carrega apenas as 7 colunas do DTO, nunca a entidade inteira nem o grafo da subcategoria.
/// </summary>
public sealed class ControlStateDashboardQuery : IControlStateDashboardQuery
{
    private readonly AegisScoreDbContext _db;

    public ControlStateDashboardQuery(AegisScoreDbContext db) => _db = db;

    public async Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default) =>
        await _db.TenantControlStates
            .AsNoTracking()
            .OrderBy(x => x.Subcategory!.Code)
            .Select(x => new TenantControlStateDto(
                x.SubcategoryId,
                x.Subcategory!.Code,
                x.CurrentScore,
                x.Subcategory!.MaxScorePoints,   // denominador do catálogo, via JOIN — jamais desnormalizado
                x.Status.ToString(),
                x.AiEvidence,
                x.LastEvaluatedAt,
                x.LastVerdictSource.ToString()))
            .ToListAsync(ct);
}
