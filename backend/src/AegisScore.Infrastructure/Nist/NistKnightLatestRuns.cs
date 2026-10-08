using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Nist;

internal sealed record LatestKnightRun(
    Guid Id, KnightSourceType SourceType, KnightAssessmentMode Mode, string Source, KnightSourceState SourceState, string CatalogVersion,
    DateTimeOffset? CompletedAt, DateTimeOffset StartedAt, Guid? IdentityAcquisitionId)
{
    public bool IsDemo => Mode == KnightAssessmentMode.Demo;
    public bool Partial => SourceState != KnightSourceState.Completed;
}

internal sealed record LatestKnightIndicator(
    Guid RunId, string IndicatorId, string Title, KnightIndicatorStatus Status, SeverityLevel Severity, IReadOnlyList<string> NistCodes,
    DateTimeOffset CollectedAt)
{
    public bool MapsTo(string code) => NistCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] A leitura técnica que o NIST oferece como evidência: a execução CONCLUÍDA mais recente de cada fonte e
/// modo do KNIGHT (o consolidado nunca é evidência) e os controles dela. Uma regra só, usada pelas evidências disponíveis da
/// subcategoria e pela correlação da rodada — as duas telas não podem discordar sobre o que está disponível.
/// </summary>
internal static class NistKnightLatestRuns
{
    internal static readonly KnightIndicatorStatus[] Evaluated =
        { KnightIndicatorStatus.Passed, KnightIndicatorStatus.Exposed, KnightIndicatorStatus.Mitigated };

    internal static async Task<(IReadOnlyList<LatestKnightRun> Runs, IReadOnlyList<LatestKnightIndicator> Indicators)> LoadAsync(
        AegisScoreDbContext db, CancellationToken ct)
    {
        var runs = await db.KnightAssessmentRuns.AsNoTracking()
            .Where(r => r.Status == KnightRunStatus.Completed && r.SourceType != KnightSourceType.Consolidated)
            .Select(r => new LatestKnightRun(r.Id, r.SourceType, r.Mode, r.Source, r.SourceState, r.CatalogVersion, r.CompletedAt, r.StartedAt,
                r.IdentityAcquisitionId))
            .ToListAsync(ct);
        // Ordenado em memória: o SQLite não ordena DateTimeOffset.
        var latest = runs
            .GroupBy(r => (r.SourceType, r.Mode))
            .Select(g => g.OrderByDescending(r => (r.CompletedAt ?? r.StartedAt).UtcTicks).ThenBy(r => r.Id).First())
            .OrderBy(r => r.SourceType).ThenBy(r => r.Mode)
            .ToList();
        if (latest.Count == 0) return (latest, Array.Empty<LatestKnightIndicator>());

        var runIds = latest.Select(r => r.Id).ToList();
        var indicators = await db.KnightIndicatorResults.AsNoTracking()
            .Where(i => runIds.Contains(i.RunId))
            .Select(i => new { i.RunId, i.IndicatorId, i.Title, i.Status, i.Severity, i.NistCodes, i.CollectedAt })
            .ToListAsync(ct);
        return (latest, indicators
            .Select(i => new LatestKnightIndicator(i.RunId, i.IndicatorId, i.Title, i.Status, i.Severity, i.NistCodes.ToList(), i.CollectedAt))
            .OrderBy(i => i.IndicatorId, StringComparer.Ordinal).ThenBy(i => i.RunId)
            .ToList());
    }
}
