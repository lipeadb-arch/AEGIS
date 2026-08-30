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

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Constrói o <see cref="AuditorTenantContext"/> a partir da MESMA projeção de postura do Dashboard/Funções
/// (<see cref="IWorkspacePostureQuery"/>) + as lacunas (controles NonCompliant) e a evidência documental
/// CURTA (trecho literal já validado) do tenant ambiente. Somente leitura, isolado pelo Global Query Filter.
/// Nunca inclui documento completo nem log bruto — só agregados e trechos limitados.
/// </summary>
public sealed class AuditorContextBuilder : IAuditorContextBuilder
{
    private const int MaxGaps = 8;
    private const int MaxEvidence = 6;
    private const int MaxRecommendations = 6;
    private const int MaxExposures = 8;
    private const int MaxVulnerabilities = 8;
    private const int EvidenceQuoteMaxChars = 240;
    private const int ReasonMaxChars = 160;
    private const int RemediationMaxChars = 240;

    private readonly AegisScoreDbContext _db;
    private readonly IWorkspacePostureQuery _posture;
    private readonly IPostureExposureQuery _exposureQuery;
    private readonly IVulnerabilityQuery _vulnerabilityQuery;

    public AuditorContextBuilder(
        AegisScoreDbContext db,
        IWorkspacePostureQuery posture,
        IPostureExposureQuery exposureQuery,
        IVulnerabilityQuery vulnerabilityQuery)
    {
        _db = db;
        _posture = posture;
        _exposureQuery = exposureQuery;
        _vulnerabilityQuery = vulnerabilityQuery;
    }

    public async Task<AuditorTenantContext> BuildAsync(CancellationToken ct = default)
    {
        // Postura (score/cobertura/contagens + Funções + saúde dos conectores) pela autoridade única.
        var w = await _posture.GetAsync(ct);

        var functions = w.Functions
            .Select(f => new AuditorFunctionPosture(
                f.Code, f.Name, f.EvaluationState, f.Percentage, f.CoveragePercentage,
                f.NonCompliantControls, f.NotEvaluatedControls))
            .ToList();

        // Lacunas: controles NonCompliant do tenant (Global Query Filter), com a natureza da prova ausente.
        // Materializa a entidade (o jsonb de MissingRequirements passa pelo mesmo conversor do resto do app) e
        // ordena por recência CLIENT-SIDE: ORDER BY de DateTimeOffset não é portável a todo provedor (ex.:
        // SQLite). O conjunto NonCompliant por tenant é pequeno (limitado pelo catálogo), então é barato.
        var gapEntities = (await _db.TenantControlStates.AsNoTracking()
                .Include(s => s.Subcategory)
                .Where(s => s.Status == ControlStatus.NonCompliant)
                .ToListAsync(ct))
            .OrderByDescending(s => s.LastEvaluatedAt)
            .Take(MaxGaps)
            .ToList();

        var topGaps = gapEntities
            .Select(s => new AuditorControlGap(
                s.Subcategory?.Code ?? "",
                "NonCompliant",
                s.MissingRequirements.FirstOrDefault()?.Description ?? Truncate(s.AiEvidence, ReasonMaxChars)))
            .Where(g => !string.IsNullOrWhiteSpace(g.SubcategoryCode))
            .ToList();

        // Evidência documental: SÓ o trecho literal já validado (EvidenceQuote não-nulo), truncado — nunca o
        // documento inteiro. Ordenada por confiança para trazer a mais forte primeiro.
        var evidenceRows = await (
            from m in _db.DocumentControlMappings.AsNoTracking()
            join d in _db.GovernanceDocuments.AsNoTracking() on m.GovernanceDocumentId equals d.Id
            where m.EvidenceQuote != null && m.EvidenceQuote != ""
            orderby m.Confidence descending
            select new { d.Title, m.SubcategoryCode, m.Confidence, m.EvidenceQuote })
            .Take(MaxEvidence)
            .ToListAsync(ct);

        var recentEvidence = evidenceRows
            .Select(e => new AuditorDocumentEvidence(
                e.Title, e.SubcategoryCode, e.Confidence, Truncate(e.EvidenceQuote, EvidenceQuoteMaxChars)))
            .ToList();

        var connectors = new AuditorConnectorContext(
            w.Connectors.Configured, w.Connectors.Enabled, w.Connectors.Healthy,
            w.Connectors.Degraded, w.Connectors.Failed, w.Connectors.NeverSynced, w.Connectors.LastSyncAt);

        // [AEGIS-MVP-LANGUAGE-02] Exposições de configuração ABERTAS pela AUTORIDADE de leitura — já com linguagem
        // CLARA e texto de fonte SANITIZADO (nunca HTML/bruto atravessa para a IA). No máx. MaxExposures, ordenação
        // da fonte (rank asc, depois maior gap) preservada pela query.
        var exposurePage = await _exposureQuery.GetAsync(
            new PostureExposureFilter(State: PostureExposureStateFilter.Open, Page: 1, PageSize: MaxExposures), ct);
        var topExposures = exposurePage.Items
            .Select(e => new AuditorPostureExposure(
                e.ExternalId, e.DisplayTitle, e.WhyItMatters,
                e.FirstAction is null ? null : Truncate(e.FirstAction, RemediationMaxChars),
                e.Category, e.Service, e.Gap, e.SourceRank, e.Tier, e.Threats))
            .ToList();

        // [AEGIS-MVP-LANGUAGE-02] Vulnerabilidades AGRUPADAS por CVE/problema (JAMAIS o mesmo CVE repetido por ativo)
        // pela AUTORIDADE de leitura. Título CLARO determinístico + rótulo de exploit (DISPONIBILIDADE, não ataque no
        // ambiente) + ALCANCE (quantidade de ativos). No máx. MaxVulnerabilities. SÓ campos permitidos — NUNCA
        // machineId, ExternalId de binding, IP, aadDeviceId, segredo, payload bruto ou inventário completo.
        var vulnOverview = await _vulnerabilityQuery.GetOverviewAsync(
            new VulnerabilityFilter(State: VulnerabilityLifecycleFilter.Open, Page: 1, PageSize: MaxVulnerabilities), ct);
        // Textos claros VERBATIM da autoridade única (VulnerabilityNarrative via a query) — a IA não recalcula.
        var topVulnerabilities = vulnOverview.Groups
            .Select(g => new AuditorVulnerability(
                g.CveId, g.DisplayTitle, g.Severity, g.CvssScore, g.Epss,
                g.ExploitLabel, g.WhyItMatters, g.FirstAction,
                g.OpenAssetCount, g.AffectedAssetCount, g.MaxAssetCriticality, g.EffectiveLifecycle, g.Providers))
            .ToList();

        // Recomendações pendentes derivadas das lacunas (curtas, sem inventar): "código: o que falta".
        var recommendations = topGaps
            .Select(g => string.IsNullOrWhiteSpace(g.Reason) ? g.SubcategoryCode : $"{g.SubcategoryCode}: {g.Reason}")
            .Take(MaxRecommendations)
            .ToList();

        return new AuditorTenantContext(
            w.Overall.EvaluationState,
            w.Overall.Percentage,
            w.Overall.CoveragePercentage,
            w.Overall.CompliantControls,
            w.Overall.NonCompliantControls,
            w.Overall.MitigatedControls,
            w.Overall.NotEvaluatedControls,
            w.Overall.LatestEvidenceAt,
            functions,
            topGaps,
            recentEvidence,
            connectors,
            recommendations,
            topExposures,
            topVulnerabilities);
    }

    private static string Truncate(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
