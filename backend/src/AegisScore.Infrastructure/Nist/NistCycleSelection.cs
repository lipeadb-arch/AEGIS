using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] A rodada MAIS RECENTE de cada avaliação (pelo início do período, depois pela criação). Usada só
/// pelas leituras agregadas que existiam antes das rodadas (dashboards legados): sem ela, uma avaliação com duas rodadas
/// teria cada subcategoria contada duas vezes nas médias.
/// </summary>
public static class NistCycleSelection
{
    public static async Task<List<Guid>> LatestCycleIdsAsync(AegisScoreDbContext db, CancellationToken ct)
    {
        var cycles = await db.NistCycles.AsNoTracking()
            .Select(c => new { c.Id, c.AssessmentId, c.PeriodStart, c.CreatedAt }).ToListAsync(ct);
        return cycles.GroupBy(c => c.AssessmentId)
            .Select(g => g.OrderByDescending(c => c.PeriodStart).ThenByDescending(c => c.CreatedAt.UtcTicks).ThenBy(c => c.Id).First().Id)
            .ToList();
    }
}
