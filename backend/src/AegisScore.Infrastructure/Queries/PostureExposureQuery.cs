using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Application.Services;
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
    private readonly IExposureLanguageCatalog _language;

    public PostureExposureQuery(AegisScoreDbContext db, ITenantContext tenant, IExposureLanguageCatalog language)
    {
        _db = db;
        _tenant = tenant;
        _language = language;
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

        // Âncora do resumo: o conector Microsoft/SecureScore do tenant (query filter fail-closed). "Última coleta"
        // vem do LastSyncAt dele — não do LastSeenAt dos findings, que é incorreto quando a coleta foi bem-sucedida
        // mas sem exposições, quando tudo foi resolvido, ou quando uma nova coleta não trouxe novo gap.
        var connector = await _db.Connectors.AsNoTracking()
            .Where(c => c.Provider == ConnectorProvider.Microsoft && c.Capability == ConnectorCapability.SecureScore)
            .Select(c => new { c.Id, c.LastSyncAt })
            .FirstOrDefaultAsync(ct);

        var score = connector is null
            ? ((double?)null, (DateTimeOffset?)null)
            : await LatestSecureScoreAsync(connector.Id, ct);
        var summary = BuildSummary(all, connector?.LastSyncAt, score);

        // Filtro da LISTA por ESTADO/CATEGORIA/SERVIÇO (o resumo reflete o tenant inteiro). A BUSCA por texto NÃO
        // entra aqui: [AEGIS-MVP-LANGUAGE-02] ela precisa enxergar a LINGUAGEM CLARA (DisplayTitle/PlainSummary),
        // que só nasce no ToDto — então projeta-se PRIMEIRO e filtra-se DEPOIS.
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

        // Ordena sobre Row (rank asc, nulos por ÚLTIMO; depois maior gap; depois título/Id estável) e PROJETA na
        // sequência para a linguagem clara. O conjunto por tenant é pequeno (bounded), então projetar tudo é barato.
        var projected = q
            .OrderBy(r => r.SourceRank ?? int.MaxValue)
            .ThenByDescending(r => r.Gap)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .Select(ToDto)
            .ToList();

        // [AEGIS-MVP-LANGUAGE-02] BUSCA sobre a linguagem já CLARA/sanitizada: título e resumo em pt-BR, mais o título
        // ORIGINAL de fonte, o ExternalId e o serviço — assim o cliente encontra por "senha", "MFA" etc. e não só pelo
        // texto em inglês da fonte. Roda ANTES da paginação (varre o tenant inteiro), depois de projetar/enriquecer.
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var needle = filter.Search!.Trim();
            projected = projected.Where(d =>
                (d.DisplayTitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.PlainSummary?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.SourceTitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.ExternalId?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Service?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        var total = projected.Count;
        var pageItems = projected
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new PostureExposureListDto(summary, pageItems, total, page, pageSize);
    }

    private async Task<(double? Percent, DateTimeOffset? At)> LatestSecureScoreAsync(Guid connectorId, CancellationToken ct)
    {
        // O sinal overall é ancorado ao CONECTOR Microsoft/SecureScore (ConnectorConfigId), não só ao texto
        // "secureScore.overall": SignalKey é definido no escopo da capability, então um sinal de mesma chave de
        // OUTRA capability/conector não pode contaminar o resumo. Materializa (bounded) e escolhe o mais recente
        // em memória — evita ORDER BY de DateTimeOffset no provedor (SQLite não o traduz de forma consistente).
        var overall = await _db.Signals.AsNoTracking()
            .Where(s => s.ConnectorConfigId == connectorId && s.SignalKey == OverallSignalKey && s.NumericValue != null)
            .Select(s => new { s.NumericValue, s.CollectedAt })
            .ToListAsync(ct);
        if (overall.Count == 0) return (null, null);
        var latest = overall.OrderByDescending(x => x.CollectedAt).First();
        return (latest.NumericValue, latest.CollectedAt);
    }

    private static PostureExposureSummaryDto BuildSummary(
        IReadOnlyList<Row> all, DateTimeOffset? lastCollectedAt, (double? Percent, DateTimeOffset? At) score)
    {
        var open = all.Where(r => r.LifecycleState == PostureExposureState.Open).ToList();
        var resolved = all.Count(r => r.LifecycleState == PostureExposureState.Resolved);

        var byCategory = open
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Outros" : r.Category!, StringComparer.Ordinal)
            .Select(g => new PostureExposureCategoryCountDto(g.Key, g.Count()))
            .OrderByDescending(c => c.Open)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();

        // Última coleta = LastSyncAt do conector Microsoft/SecureScore (null = ainda não coletado, nunca 0). Uma
        // coleta bem-sucedida SEM exposições, ou com tudo resolvido, ainda conta como coletada.
        return new PostureExposureSummaryDto(
            SourceLabel, open.Count, resolved, byCategory, lastCollectedAt, score.Percent, score.At);
    }

    /// <summary>
    /// [AEGIS-MVP-LANGUAGE-02] Projeta a exposição com a camada CLARA e a fonte SANITIZADA. O texto de fonte
    /// (título/remediação/impacto) é convertido em texto simples pela autoridade única (<see cref="SourceTextSanitizer"/>)
    /// — conteúdo bruto de conector JAMAIS cruza a fronteira. Sem entrada no catálogo, cai em <c>SourceOnly</c>:
    /// título claro = título de fonte sanitizado; primeira ação = remediação de fonte sanitizada (fallback honesto).
    /// </summary>
    private PostureExposureItemDto ToDto(Row r)
    {
        var sourceTitle = SourceTextSanitizer.ToPlainText(r.Title, 200);
        var sourceRemediation = SourceTextSanitizer.ToPlainText(r.Remediation, SourceTextSanitizer.DefaultMaxLength);
        var sourceRemediationImpact = SourceTextSanitizer.ToPlainText(r.RemediationImpact, SourceTextSanitizer.DefaultMaxLength);
        var threats = ExposureVocabulary.ThreatsPt(r.Threats);   // AMEAÇAS traduzidas — tela principal + contexto da IA

        var lang = _language.Match(r.ExternalId, sourceTitle ?? r.Title);
        var localized = lang is not null;

        string displayTitle, firstAction;
        string? plainSummary, whyItMatters;
        if (localized)
        {
            displayTitle = lang!.DisplayTitle;
            plainSummary = lang.PlainSummary;
            whyItMatters = lang.WhyItMatters;
            firstAction = lang.FirstAction;
        }
        else
        {
            // [AEGIS-MVP-LANGUAGE-02] SourceOnly: MOLDURA genérica em pt-BR — NUNCA finge tradução oficial. O título e a
            // remediação ORIGINAIS sanitizados permanecem nos detalhes como referência da fonte.
            var categoryPt = ExposureVocabulary.CategoryPt(r.Category);
            var serviceLabel = string.IsNullOrWhiteSpace(r.Service) ? "um serviço" : r.Service!.Trim();
            displayTitle = $"Revisar configuração de {categoryPt ?? "segurança"} em {serviceLabel}";
            plainSummary = "A fonte identificou uma configuração que reduz a postura de segurança deste serviço.";
            whyItMatters = threats.Count > 0
                ? $"Relacionada a: {string.Join(", ", threats)}."
                : "Pode facilitar ataques se não for revisada e corrigida.";
            firstAction = "Revise a configuração indicada pela fonte, valide o impacto em um grupo controlado e então aplique a correção.";
        }

        return new PostureExposureItemDto(
            r.Id, r.ExternalId,
            sourceTitle ?? "",                                                  // Title (compat) — SANITIZADO, nunca bruto
            r.Category, r.Service, r.ActionType,
            r.CurrentScore, r.MaxScore, r.Gap, r.SourceRank, r.Tier,
            r.ImplementationCost, r.UserImpact,
            sourceRemediation, sourceRemediationImpact,                         // Remediation/RemediationImpact (compat) — sanitizados
            threats, r.SourceState,
            r.LifecycleState == PostureExposureState.Open ? "Open" : "Resolved",
            r.FirstSeenAt, r.LastSeenAt, r.ResolvedAt)
        {
            DisplayTitle = displayTitle,
            PlainSummary = plainSummary,
            WhyItMatters = whyItMatters,
            FirstAction = firstAction,
            SourceTitle = sourceTitle,
            SourceRemediation = sourceRemediation,
            SourceRemediationImpact = sourceRemediationImpact,
            LanguageCoverage = (localized ? ExposureLanguageCoverage.Localized : ExposureLanguageCoverage.SourceOnly).ToString(),
        };
    }

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
