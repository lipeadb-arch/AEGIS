using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Autoridade ÚNICA de leitura das exposições de configuração do tenant ambiente, sobre
/// o AegisScoreDbContext. Somente leitura, isolada pelo Global Query Filter (fail-closed): sem tenant, devolve
/// vazio. Ordenação padrão = rank da fonte (menor primeiro; nulos por último) e depois maior gap — a ordem em
/// que a TI do cliente deveria atacar. O conjunto por tenant é pequeno e limitado (o catálogo do Secure Score
/// tem poucas centenas de controles), então a ordenação/filtragem/paginação em memória é barata e PORTÁVEL
/// (evita divergência de NULLS FIRST/LAST e de ORDER BY de DateTimeOffset entre PostgreSQL e SQLite).
/// </summary>
public sealed class PostureExposureQuery : IPostureExposureQuery
{
    /// <summary>Sinal do Secure Score geral — o "Secure Score mais recente" do resumo (mesmo signalKey do coletor).</summary>
    private const string OverallSignalKey = "secureScore.overall";

    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const string SourceLabel = "Microsoft Secure Score";

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;

    public PostureExposureQuery(AegisScoreDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<PostureExposureListDto> GetAsync(PostureExposureFilter filter, CancellationToken ct = default)
    {
        var page = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize <= 0 ? DefaultPageSize : Math.Min(filter.PageSize, MaxPageSize);

        // Fail-closed: sem tenant ambiente, nada é projetado (o catálogo é global e não pode vazar sozinho).
        if (_tenant.TenantId is null)
            return Empty(page, pageSize);

        // Conjunto COMPLETO do tenant (Global Query Filter fail-closed) — bounded; agregado em memória.
        var all = await _db.PostureExposureFindings.AsNoTracking()
            .Select(f => new Row(
                f.Id, f.ExternalId, f.Title, f.Category, f.Service, f.ActionType,
                f.CurrentScore, f.MaxScore, f.Gap, f.SourceRank, f.Tier,
                f.ImplementationCost, f.UserImpact, f.Remediation, f.RemediationImpact,
                f.Threats, f.SourceState, f.LifecycleState, f.FirstSeenAt, f.LastSeenAt, f.ResolvedAt))
            .ToListAsync(ct);

        var summary = BuildSummary(all, await LatestSecureScoreAsync(ct));

        // Filtro da LISTA (o resumo reflete o tenant inteiro).
        IEnumerable<Row> q = all;
        q = filter.State switch
        {
            PostureExposureStateFilter.Open => q.Where(r => r.LifecycleState == PostureExposureState.Open),
            PostureExposureStateFilter.Resolved => q.Where(r => r.LifecycleState == PostureExposureState.Resolved),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(filter.Category))
            q = q.Where(r => string.Equals(r.Category, filter.Category!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Service))
            q = q.Where(r => string.Equals(r.Service, filter.Service!.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var needle = filter.Search!.Trim();
            q = q.Where(r =>
                (r.Title?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.ExternalId?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Service?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = q.ToList();
        var total = filtered.Count;

        // Ordenação padrão: rank asc (nulos por ÚLTIMO), depois maior gap, depois título/Id (estável).
        var ordered = filtered
            .OrderBy(r => r.SourceRank ?? int.MaxValue)
            .ThenByDescending(r => r.Gap)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToDto)
            .ToList();

        return new PostureExposureListDto(summary, ordered, total, page, pageSize);
    }

    private async Task<(double? Percent, DateTimeOffset? At)> LatestSecureScoreAsync(CancellationToken ct)
    {
        // Materializa os sinais overall (bounded por tenant) e escolhe o mais recente em memória — evita ORDER BY
        // de DateTimeOffset no provedor (SQLite não o traduz de forma consistente).
        var overall = await _db.Signals.AsNoTracking()
            .Where(s => s.SignalKey == OverallSignalKey && s.NumericValue != null)
            .Select(s => new { s.NumericValue, s.CollectedAt })
            .ToListAsync(ct);
        if (overall.Count == 0) return (null, null);
        var latest = overall.OrderByDescending(x => x.CollectedAt).First();
        return (latest.NumericValue, latest.CollectedAt);
    }

    private static PostureExposureSummaryDto BuildSummary(
        IReadOnlyList<Row> all, (double? Percent, DateTimeOffset? At) score)
    {
        var open = all.Where(r => r.LifecycleState == PostureExposureState.Open).ToList();
        var resolved = all.Count(r => r.LifecycleState == PostureExposureState.Resolved);

        var byCategory = open
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Outros" : r.Category!, StringComparer.Ordinal)
            .Select(g => new PostureExposureCategoryCountDto(g.Key, g.Count()))
            .OrderByDescending(c => c.Open)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();

        // Última coleta = observação mais recente entre TODAS as exposições (null = ainda não coletado, nunca 0).
        DateTimeOffset? lastCollected = all.Count == 0 ? null : all.Max(r => r.LastSeenAt);

        return new PostureExposureSummaryDto(
            SourceLabel, open.Count, resolved, byCategory, lastCollected, score.Percent, score.At);
    }

    private static PostureExposureItemDto ToDto(Row r) => new(
        r.Id, r.ExternalId, r.Title, r.Category, r.Service, r.ActionType,
        r.CurrentScore, r.MaxScore, r.Gap, r.SourceRank, r.Tier,
        r.ImplementationCost, r.UserImpact, r.Remediation, r.RemediationImpact,
        r.Threats ?? new List<string>(), r.SourceState,
        r.LifecycleState == PostureExposureState.Open ? "Open" : "Resolved",
        r.FirstSeenAt, r.LastSeenAt, r.ResolvedAt);

    private static PostureExposureListDto Empty(int page, int pageSize) => new(
        new PostureExposureSummaryDto(SourceLabel, 0, 0, Array.Empty<PostureExposureCategoryCountDto>(), null, null, null),
        Array.Empty<PostureExposureItemDto>(), 0, page, pageSize);

    /// <summary>Projeção de leitura de uma exposição (evita materializar a entidade rastreada).</summary>
    private sealed record Row(
        Guid Id, string ExternalId, string Title, string? Category, string? Service, string? ActionType,
        double CurrentScore, double MaxScore, double Gap, int? SourceRank, string? Tier,
        string? ImplementationCost, string? UserImpact, string? Remediation, string? RemediationImpact,
        List<string>? Threats, string? SourceState, PostureExposureState LifecycleState,
        DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset? ResolvedAt);
}
