using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Posture;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Um mês de uma série: a fotografia PUBLICADA que representa o mês e o que permite compará-la à
/// anterior. <paramref name="ComparableWithPrevious"/> só é verdadeiro quando o ponto anterior existe e as duas fotografias
/// são compatíveis pela MESMA regra da comparação (<see cref="PostureSnapshotComparer.CheckCompatibility"/>);
/// <paramref name="BreakReasons"/> diz por que a série recomeça (versão de fórmula, catálogo ou esquema diferente).
/// </summary>
public sealed record PostureMonthlyPoint(
    DateOnly Month,
    Guid SnapshotId,
    DateTimeOffset CapturedAt,
    string EvaluationState,
    double? Score,
    double Coverage,
    int EvaluatedItems,
    int EligibleItems,
    string FormulaVersion,
    string CatalogVersion,
    string SchemaVersion,
    string? SourceLabel,
    Guid? SourceRunId,
    int PublishedInMonth,
    bool ComparableWithPrevious,
    IReadOnlyList<string> BreakReasons,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Maturidade 1–5 (só na série de maturidade NIST; nunca o score 0–100).</summary>
    double? MaturityCurrent = null,
    double? MaturityTarget = null,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Subcategorias aplicáveis (catálogo − não se aplica): o universo das médias.</summary>
    int? ApplicableItems = null,
    string? CycleName = null,
    /// <summary>[AEGIS-NIST-JOURNEY-02] Avisos do ponto: cobertura ou universo diferentes do ponto anterior, queda de nota.</summary>
    IReadOnlyList<string>? Notes = null);

/// <summary>Uma série = um instrumento e uma família semântica (NIST, ou KNIGHT de uma fonte/composição). Nunca somadas.</summary>
public sealed record PostureMonthlySeries(
    string Type,
    string SemanticFamily,
    string? SourceType,
    string Label,
    IReadOnlyList<PostureMonthlyPoint> Points);

public sealed record PostureMonthlyHistoryDto(
    string Criterion,
    IReadOnlyList<DateOnly> Months,
    IReadOnlyList<PostureMonthlySeries> Series);

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Evolução MENSAL a partir das fotografias publicadas — motor PURO (sem EF/relógio próprio).
///
/// Critério: cada mês é representado pela ÚLTIMA fotografia publicada naquele mês para a mesma família semântica
/// (instrumento + fonte). Mês sem publicação não tem ponto — nada é interpolado nem repetido. KNIGHT e NIST ficam em
/// séries separadas e nenhuma série é somada a outra. Pontos consecutivos só são "comparáveis" quando a regra única de
/// compatibilidade das fotografias aceita o par; uma mudança de fórmula, catálogo ou esquema recomeça a série.
/// </summary>
public static class PostureMonthlyHistory
{
    public const string Criterion =
        "Cada mês é representado pela última fotografia publicada no mês, por instrumento e fonte. "
        + "Mês sem publicação fica sem resultado; versões de fórmula, catálogo ou esquema diferentes não são ligadas.";

    public static PostureMonthlyHistoryDto Build(IReadOnlyList<PostureSnapshotSummaryDto> snapshots, DateTimeOffset now, int months)
    {
        months = Math.Clamp(months, 1, 36);
        var nowUtc = now.UtcDateTime;
        var last = new DateOnly(nowUtc.Year, nowUtc.Month, 1);
        var axis = Enumerable.Range(0, months).Select(i => last.AddMonths(i - months + 1)).ToList();
        var first = axis[0];

        var series = snapshots
            .GroupBy(s => (s.Type, s.SemanticFamily))
            .Select(g =>
            {
                var byMonth = g
                    .GroupBy(s => MonthOf(s.CapturedAt))
                    .Where(m => m.Key >= first && m.Key <= last)
                    .OrderBy(m => m.Key)
                    .Select(m => (Month: m.Key, Count: m.Count(),
                        Rep: m.OrderByDescending(s => s.CapturedAt.UtcTicks).ThenByDescending(s => s.Id).First()))
                    .ToList();

                var points = new List<PostureMonthlyPoint>();
                PostureSnapshotSummaryDto? previous = null;
                foreach (var (month, count, rep) in byMonth)
                {
                    var reasons = previous is null
                        ? Array.Empty<string>()
                        : PostureSnapshotComparer.CheckCompatibility(Side(previous), Side(rep)).ToArray();
                    var maturity = rep.Type == MaturityType;
                    points.Add(new PostureMonthlyPoint(
                        month, rep.Id, rep.CapturedAt, rep.EvaluationState, rep.Score, rep.Coverage, rep.EvaluatedItems,
                        rep.EligibleItems, rep.FormulaVersion, rep.CatalogVersion, rep.SchemaVersion, rep.SourceLabel,
                        rep.SourceRunId, count, previous is not null && reasons.Length == 0, reasons,
                        maturity ? rep.MaturityCurrent : null,
                        maturity ? rep.MaturityTarget : null,
                        maturity ? rep.EligibleItems - rep.NotApplicableCount : null,
                        maturity ? rep.NistCycleName : null,
                        maturity ? MaturityNotes(previous, rep) : null));
                    previous = rep;
                }

                var latest = g.OrderByDescending(s => s.CapturedAt.UtcTicks).First();
                return new PostureMonthlySeries(g.Key.Type, g.Key.SemanticFamily, latest.SourceType, LabelOf(latest), points);
            })
            .Where(s => s.Points.Count > 0)
            .OrderBy(s => s.Type == "AegisScoreNist" ? 0 : s.Type == MaturityType ? 1 : 2)
            .ThenBy(s => s.Label, StringComparer.CurrentCulture)
            .ToList();

        return new PostureMonthlyHistoryDto(Criterion, axis, series);
    }

    private static DateOnly MonthOf(DateTimeOffset at)
    {
        var u = at.UtcDateTime;
        return new DateOnly(u.Year, u.Month, 1);
    }

    private static string LabelOf(PostureSnapshotSummaryDto s) => s.Type switch
    {
        "AegisScoreNist" => $"NIST · postura do ambiente ({s.FormulaVersion})",
        MaturityType => $"NIST · maturidade 1–5 — {s.SourceLabel ?? "avaliação"}",
        _ => $"KNIGHT · {s.SourceLabel ?? s.SourceType ?? "fonte"}",
    };

    private const string MaturityType = "NistMaturity";

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-02] O que o leitor precisa saber antes de comparar dois meses de maturidade: a base mudou
    /// (cobertura, universo aplicável) e queda de média não é, sozinha, piora da segurança.
    /// </summary>
    private static IReadOnlyList<string> MaturityNotes(PostureSnapshotSummaryDto? previous, PostureSnapshotSummaryDto current)
    {
        var notes = new List<string>();
        if (previous is null) return notes;
        if (Math.Abs(previous.Coverage - current.Coverage) > 0.05)
            notes.Add($"Cobertura mudou de {previous.Coverage:0.#}% para {current.Coverage:0.#}%: médias sobre conjuntos diferentes.");
        var prevUniverse = previous.EligibleItems - previous.NotApplicableCount;
        var currUniverse = current.EligibleItems - current.NotApplicableCount;
        if (prevUniverse != currUniverse)
            notes.Add($"Universo aplicável mudou de {prevUniverse} para {currUniverse} subcategorias.");
        if (previous.MaturityCurrent is { } a && current.MaturityCurrent is { } b && b < a)
            notes.Add("A média atual caiu; isso não indica, sozinho, piora da segurança (rigor, evidência nova, cobertura ou universo).");
        return notes;
    }

    private static PostureComparisonSide Side(PostureSnapshotSummaryDto s) => new(
        s.CapturedAt, s.Type, s.SemanticFamily, s.FormulaVersion, s.CatalogVersion, s.SchemaVersion, s.Score, s.Coverage,
        s.CompliantCount, s.NonCompliantCount, s.MitigatedCount, s.NotEvaluatedCount, s.ErrorCount, s.NotApplicableCount,
        Array.Empty<PostureComparableItem>());
}
