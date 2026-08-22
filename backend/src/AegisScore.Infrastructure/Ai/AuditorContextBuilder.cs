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
    private const int EvidenceQuoteMaxChars = 240;
    private const int ReasonMaxChars = 160;
    private const int RemediationMaxChars = 240;

    private readonly AegisScoreDbContext _db;
    private readonly IWorkspacePostureQuery _posture;

    public AuditorContextBuilder(AegisScoreDbContext db, IWorkspacePostureQuery posture)
    {
        _db = db;
        _posture = posture;
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

        // [AEGIS-MVP-POSTURE-02] Principais exposições de configuração ABERTAS (no máx. 8), ordenadas pelo rank da
        // fonte e depois pelo maior gap. SÓ os campos permitidos — nunca resposta bruta, actionUrl, segredo ou PII.
        // Ordenação em memória (conjunto pequeno por tenant): rank asc com nulos por último, depois maior gap.
        var exposureEntities = (await _db.PostureExposureFindings.AsNoTracking()
                .Where(f => f.LifecycleState == PostureExposureState.Open)
                .ToListAsync(ct))
            .OrderBy(f => f.SourceRank ?? int.MaxValue)
            .ThenByDescending(f => f.Gap)
            .Take(MaxExposures)
            .ToList();

        var topExposures = exposureEntities
            .Select(f => new AuditorPostureExposure(
                f.ExternalId, f.Title, f.Category, f.Service, f.Gap, f.SourceRank, f.Tier,
                Truncate(f.Remediation, RemediationMaxChars),
                f.Threats ?? new List<string>()))
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
            topExposures);
    }

    private static string Truncate(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
