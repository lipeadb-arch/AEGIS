using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        // [AEGIS-MVP-VULN-01] Principais vulnerabilidades ativo×CVE efetivamente ABERTAS (no máx. 8), priorizadas por
        // FATOS DA FONTE + criticidade do ativo (a MESMA régua determinística da tela). Consulta LIMITADA no banco
        // (Take 8 + observações só dos 8) — nunca materializa todo o tenant. SÓ campos permitidos — NUNCA machineId,
        // ExternalId de binding, IP, aadDeviceId, segredo ou payload bruto.
        var topExposureRows = await _db.AssetThreatExposures.AsNoTracking()
            .Where(e => e.Threat!.Source == ThreatSource.Cve
                && (_db.AssetThreatObservations.Any(o => o.AssetThreatExposureId == e.Id && o.LifecycleState == ObservationLifecycle.Open)
                    || !_db.AssetThreatObservations.Any(o => o.AssetThreatExposureId == e.Id)))
            .OrderByDescending(e => e.Threat!.ExploitVerified == true)
            .ThenByDescending(e => e.Threat!.PublicExploit == true)
            .ThenByDescending(e => e.Threat!.CvssScore ?? -1)
            .ThenByDescending(e => e.Threat!.Epss ?? -1)
            .ThenByDescending(e => e.Asset!.Criticality)
            .ThenBy(e => e.Threat!.Code)
            .ThenBy(e => e.Asset!.Name)
            .ThenBy(e => e.Id)
            .Take(MaxVulnerabilities)
            .Select(e => new
            {
                e.Id,
                CveId = e.Threat!.Code, e.Threat!.Severity, e.Threat!.CvssScore,
                e.Threat!.PublicExploit, e.Threat!.ExploitVerified, e.Threat!.Epss,
                AssetName = e.Asset!.Name, AssetCriticality = e.Asset!.Criticality,
            })
            .ToListAsync(ct);

        var topIds = topExposureRows.Select(r => r.Id).ToList();
        var obsForTop = await _db.AssetThreatObservations.AsNoTracking()
            .Where(o => topIds.Contains(o.AssetThreatExposureId))
            .Select(o => new { o.AssetThreatExposureId, Provider = o.ConnectorConfig!.Provider, o.EvidenceJson })
            .ToListAsync(ct);
        var obsByExposure = obsForTop.GroupBy(o => o.AssetThreatExposureId).ToDictionary(g => g.Key, g => g.ToList());

        var topVulnerabilities = topExposureRows
            .Select(r =>
            {
                obsByExposure.TryGetValue(r.Id, out var os);
                os ??= new();
                var providers = os.Select(o => o.Provider.ToString())
                    .Distinct(System.StringComparer.Ordinal)
                    .OrderBy(p => p, System.StringComparer.Ordinal)
                    .ToList();
                var product = os.Select(o => FirstProduct(o.EvidenceJson))
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                return new AuditorVulnerability(
                    r.CveId, r.Severity, r.CvssScore, r.PublicExploit, r.ExploitVerified, r.Epss,
                    r.AssetName, r.AssetCriticality, product, "Open", providers);
            })
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

    /// <summary>Primeiro produto afetado (rótulo curto) do EvidenceJson da exposição, ou null. Defensivo a jsonb inválido.</summary>
    private static string? FirstProduct(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Products", out var products)
                || products.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var p in products.EnumerateArray())
            {
                var vendor = p.TryGetProperty("Vendor", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var name = p.TryGetProperty("Product", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var label = string.Join(" ", new[] { vendor, name }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(label)) return label.Length <= 120 ? label : label[..120];
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
