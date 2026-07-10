using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Queries;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// Lê a série temporal do Aegis Score sobre o AegisScoreDbContext. O Global Query Filter (fail-closed)
/// já restringe TenantScoreSnapshots ao tenant ambiente resolvido do JWT — por isso a consulta não
/// recebe nem filtra TenantId explicitamente: delegar o recorte ao filtro É o próprio isolamento.
/// </summary>
public sealed class TenantScoreTrendQuery : ITenantScoreTrendQuery
{
    private const int MinDays = 1;
    private const int MaxDays = 365;   // teto defensivo: evita varredura ilimitada por input abusivo

    private readonly AegisScoreDbContext _db;

    public TenantScoreTrendQuery(AegisScoreDbContext db) => _db = db;

    public async Task<IReadOnlyList<TenantTrendDto>> GetTrendAsync(int days = 30, CancellationToken ct = default)
    {
        var window = Math.Clamp(days, MinDays, MaxDays);

        // Janela [hoje - (window-1), hoje]: 'window' dias corridos, incluindo hoje.
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(window - 1));

        // AsNoTracking: leitura pura. O índice único (TenantId, SnapshotDate) — tenant-leading —
        // serve este range ordenado por data como um seek + varredura ordenada.
        return await _db.TenantScoreSnapshots
            .AsNoTracking()
            .Where(s => s.SnapshotDate >= since)
            .OrderBy(s => s.SnapshotDate)
            .Select(s => new TenantTrendDto(s.SnapshotDate, s.TotalAchievedScore, s.TotalMaxScore))
            .ToListAsync(ct);
    }
}
