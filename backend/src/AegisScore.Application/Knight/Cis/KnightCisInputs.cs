using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Cis;

/// <summary>
/// [AEGIS-KNIGHT-CIS-01] Monta a entrada da projeção CIS a partir das DUAS origens que a usam — a leitura ao vivo da composição do
/// escopo (tela) e as execuções pinadas da publicação (fotografia → HTML) —, com os mesmos campos. A projeção é uma só.
/// </summary>
public static class KnightCisInputs
{
    /// <summary>Leitura ao vivo: a composição do escopo salvo, como a aba "Geral" a mostra.</summary>
    public static KnightCisInput FromConsolidated(KnightConsolidatedAssessment c, IReadOnlyList<KnightManualResultView> manual)
    {
        var bySource = c.Indicators.GroupBy(i => i.SourceType).ToDictionary(g => g.Key, g => g.ToList());
        var sources = c.Sources.Select(e => new KnightCisSourceInput(
            e.Source, e.Label, e.AvailabilityState, e.Included, e.SourceRunId, e.CatalogVersion, e.CapturedAt, e.SourceState,
            e.Included && bySource.TryGetValue(e.Source, out var list) ? list.Select(FromView).ToList() : Array.Empty<KnightCisRuleInput>()))
            .ToList();
        return new KnightCisInput(
            KnightConsolidatedLabel.For(c.Sources.Where(s => s.Included).Select(s => s.Label).ToList()), c.DataRecency, sources, manual,
            new KnightCisKnightTotals(c.Indicators.Count, c.PassedCount + c.ExposedCount + c.MitigatedCount + c.NotEvaluatedCount + c.ErrorCount,
                c.PassedCount + c.ExposedCount + c.MitigatedCount, c.PassedCount, c.ExposedCount, c.MitigatedCount, c.NotEvaluatedCount,
                c.ErrorCount, c.NotApplicableCount));
    }

    public static KnightCisRuleInput FromView(KnightIndicatorView v) => new(
        v.IndicatorId, v.Title, v.Status, v.Evidence, v.NotEvaluatedReason, v.AffectedObjectCount, v.AffectedComposition,
        v.EvidenceObjectCount, v.AffectedDetailLimitation, Blank(v.Recommendation), Blank(v.Presentation?.ExpectedConfiguration),
        Blank(v.Presentation?.Criterion), Blank(v.Presentation?.Rationale), Blank(v.Presentation?.Impact), Docs(v.Presentation?.References));

    /// <summary>Publicação: uma execução gravada (com os objetos que sustentam cada veredito) vira as regras da fonte.</summary>
    public static IReadOnlyList<KnightCisRuleInput> FromRun(KnightAssessmentRun run) =>
        run.Indicators.OrderBy(i => i.IndicatorId, StringComparer.Ordinal).Select(i =>
        {
            var p = KnightControlPresentations.For(i.IndicatorId, i.Category, i.Severity, i.Status, i.SourceType, i.NistCodes, i.MitreTechniques, run.CatalogVersion);
            var affected = i.AffectedObjects.Where(o => o.Relation == KnightObjectRelation.Affected).ToList();
            return new KnightCisRuleInput(
                i.IndicatorId, i.Title, i.Status, i.Evidence, i.NotEvaluatedReason, i.AffectedObjectCount,
                i.AffectedObjectCount > 0 ? KnightObjectNouns.Composition(affected.Select(o => o.Kind), i.AffectedObjectCount) : null,
                i.AffectedObjects.Count(o => o.Relation == KnightObjectRelation.Evidence), i.AffectedDetailLimitation,
                Blank(i.Recommendation), Blank(p.ExpectedConfiguration), Blank(p.Criterion), Blank(p.Rationale), Blank(p.Impact), Docs(p.References));
        }).ToList();

    private static IReadOnlyList<KnightCisLink> Docs(IReadOnlyList<KnightControlReference>? refs) =>
        (refs ?? Array.Empty<KnightControlReference>())
            .Where(r => !KnightControlProfiles.IsBenchmark(r.Framework) && r.Framework != KnightControlProfiles.NistFramework
                        && r.Framework != KnightControlProfiles.MitreFramework
                        && Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            .Select(r => new KnightCisLink(r.Code, r.Url!))
            .ToList();

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>[AEGIS-KNIGHT-CIS-01] Texto JSON CONGELADO da visão CIS na fotografia (sob o hash). Mesma entrada → mesmos bytes.</summary>
public static class KnightCisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static string Serialize(KnightCisView view) => JsonSerializer.Serialize(view, Options);

    /// <summary>Lê a visão congelada; ausente (fotografia anterior a este recurso) ou ilegível devolve <c>null</c>.</summary>
    public static KnightCisView? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<KnightCisView>(json, Options); }
        catch (JsonException) { return null; }
    }
}
