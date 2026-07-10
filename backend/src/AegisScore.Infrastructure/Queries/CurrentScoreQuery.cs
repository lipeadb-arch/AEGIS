using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// Consolida o Score Atual do tenant em tempo real sobre o AegisScoreDbContext. Replica EXATAMENTE a
/// fórmula do <c>AegisScoreSnapshotWorker</c> — SUM(CurrentScore) / SUM(Subcategory.MaxScorePoints)
/// sobre os <c>TenantControlState</c> avaliados — para o KPI instantâneo e a foto diária jamais
/// divergirem no mesmo dia. O Global Query Filter (fail-closed) já restringe ao tenant ambiente
/// resolvido do JWT: a consulta não recebe nem filtra TenantId explicitamente.
/// </summary>
public sealed class CurrentScoreQuery : ICurrentScoreQuery
{
    private readonly AegisScoreDbContext _db;

    public CurrentScoreQuery(AegisScoreDbContext db) => _db = db;

    public async Task<CurrentScoreDto> GetCurrentAsync(CancellationToken ct = default)
    {
        // Agregações no banco (o cast p/ int? cobre o SUM de zero linhas — NULL no SQL — num tenant
        // ainda sem avaliações, sem estourar). Idêntico ao worker, garantindo o mesmo número no HUD.
        var states = _db.TenantControlStates;
        var achieved = await states.SumAsync(x => (int?)x.CurrentScore, ct) ?? 0;
        var max = await states.SumAsync(x => (int?)x.Subcategory!.MaxScorePoints, ct) ?? 0;
        var evaluated = await states.CountAsync(ct);

        return new CurrentScoreDto(achieved, max, evaluated);
    }
}
