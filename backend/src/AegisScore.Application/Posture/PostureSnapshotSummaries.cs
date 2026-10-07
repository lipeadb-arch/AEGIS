using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Application.Posture;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Resumo ÚNICO de uma fotografia — o mesmo para a lista, a evolução mensal e o histórico
/// congelado na publicação (antes vivia só no serviço de fotografias; a publicação NIST também precisa dele). Só lê o que a
/// fotografia congelou: composição (consolidado KNIGHT) e período avaliado (rodada NIST) saem dos JSONs dela.
/// </summary>
public static class PostureSnapshotSummaries
{
    public static PostureSnapshotSummaryDto Of(PostureSnapshot s)
    {
        var included = s.SourceType == KnightSourceType.Consolidated
            ? KnightConsolidatedCompositionJson.Deserialize(s.CompositionJson)?.Where(e => e.Included).ToList()
            : null;
        var (periodStart, periodEnd) = s.Type == PostureSnapshotType.NistMaturity ? PeriodOf(s.NistReportJson) : (null, null);
        return new PostureSnapshotSummaryDto(
            s.Id,
            s.Type.ToString(),
            s.SchemaVersion,
            s.FormulaVersion,
            s.CatalogVersion,
            s.SemanticFamily,
            s.SourceType?.ToString(),
            s.SourceLabel,
            s.CapturedAt,
            s.Score is null ? "NotEvaluated" : "Evaluated",
            s.Score,
            s.Coverage,
            s.EvaluatedItems,
            s.EligibleItems,
            s.CompliantCount,
            s.NonCompliantCount,
            s.MitigatedCount,
            s.NotEvaluatedCount,
            s.ErrorCount,
            s.NotApplicableCount,
            s.DataRecency,
            s.ContentHash,
            s.ClientName,
            s.SourceRunId,
            s.MaturityCurrent,
            s.MaturityTarget,
            s.MaturityGap,
            s.NistAssessmentId,
            s.NistCycleId,
            s.NistScopeId,
            s.NistCycleName,
            included?.Select(e => e.Label).ToList(),
            included is null ? null : CompositionKey(included),
            periodStart,
            periodEnd,
            !string.IsNullOrEmpty(s.HistoryJson));
    }

    /// <summary>"fonte=catálogo" das fontes incluídas, em ordem estável — a identidade da base de um consolidado.</summary>
    public static string CompositionKey(IEnumerable<KnightConsolidatedSourceEntry> included) =>
        string.Join("|", included
            .Select(e => $"{e.Source}={e.CatalogVersion ?? "?"}")
            .OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Período da rodada congelado no relatório NIST (sem desserializar o documento inteiro).</summary>
    private static (DateOnly?, DateOnly?) PeriodOf(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            if (!doc.RootElement.TryGetProperty("cycle", out var cycle)) return (null, null);
            return (Date(cycle, "periodStart"), Date(cycle, "periodEnd"));
        }
        catch (JsonException) { return (null, null); }

        static DateOnly? Date(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
    }
}
