using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// Conta os controles não-conformes do tenant sobre o AegisScoreDbContext. O Global Query Filter
/// (fail-closed) já restringe TenantControlStates ao tenant ambiente; a contagem roda no banco
/// (COUNT(*) com WHERE), sem materializar linhas — leitura barata para o KPI do HUD.
/// </summary>
public sealed class PendingControlsQuery : IGetPendingControlsQuery
{
    private readonly AegisScoreDbContext _db;

    public PendingControlsQuery(AegisScoreDbContext db) => _db = db;

    public Task<int> CountAsync(CancellationToken ct = default) =>
        _db.TenantControlStates.CountAsync(x => x.Status == ControlStatus.NonCompliant, ct);
}
