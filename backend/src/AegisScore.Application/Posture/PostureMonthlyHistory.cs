using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AegisScore.Application.Posture;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Um mês de uma série: a fotografia PUBLICADA que representa o mês e o que permite compará-la à
/// anterior. <paramref name="ComparableWithPrevious"/> só é verdadeiro quando o ponto anterior existe e as duas fotografias
/// são compatíveis pela MESMA regra da comparação (<see cref="PostureSnapshotComparer.CheckCompatibility"/>);
/// <paramref name="BreakReasons"/> diz por que a série recomeça (versão de fórmula, catálogo, esquema ou composição diferente).
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
    /// <summary>
    /// Base aplicável do ponto. NIST: subcategorias do catálogo − não se aplica (o universo das médias). [AEGIS-ASSESSMENT-VISUALS-01]
    /// KNIGHT: controles aplicáveis (avaliados + não avaliados + erro), o denominador da cobertura.
    /// </summary>
    int? ApplicableItems = null,
    string? CycleName = null,
    /// <summary>Avisos do ponto: cobertura, universo ou composição diferentes do ponto anterior, queda de valor.</summary>
    IReadOnlyList<string>? Notes = null,
    /// <summary>
    /// [AEGIS-ASSESSMENT-VISUALS-01] Variação na escala do instrumento em relação ao ponto ANTERIOR da série (que pode estar
    /// alguns meses antes — <see cref="DeltaFrom"/> diz qual). Nula quando não há ponto anterior comparável ou valor dos dois lados.
    /// </summary>
    double? Delta = null,
    DateOnly? DeltaFrom = null,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Instante dos dados (coleta KNIGHT; última revisão NIST) — distinto da publicação.</summary>
    DateTimeOffset? DataRecency = null,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Período avaliado (rodada NIST). Nulo no KNIGHT.</summary>
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Fontes incluídas de um consolidado KNIGHT.</summary>
    IReadOnlyList<string>? Composition = null,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Este ponto é a PRÓPRIA publicação do relatório que congelou o histórico.</summary>
    bool IsThisPublication = false);

/// <summary>Uma série = um instrumento e uma família semântica (NIST, ou KNIGHT de uma fonte/composição). Nunca somadas.</summary>
public sealed record PostureMonthlySeries(
    string Type,
    string SemanticFamily,
    string? SourceType,
    string Label,
    IReadOnlyList<PostureMonthlyPoint> Points,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Instrumento e escala, por extenso ("AEGIS KNIGHT · nota 0–100").</summary>
    string? Instrument = null,
    double? ScaleMin = null,
    double? ScaleMax = null,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] O que a cobertura mede nesta série (denominador explícito).</summary>
    string? CoverageBasis = null,
    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Avaliação e escopo NIST da série (nunca misturados).</summary>
    Guid? NistAssessmentId = null,
    Guid? NistScopeId = null);

public sealed record PostureMonthlyHistoryDto(
    string Criterion,
    IReadOnlyList<DateOnly> Months,
    IReadOnlyList<PostureMonthlySeries> Series);

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Evolução MENSAL a partir das fotografias publicadas — motor PURO (sem EF/relógio próprio).
///
/// Critério: cada mês é representado pela ÚLTIMA fotografia publicada naquele mês para a mesma família semântica
/// (instrumento + fonte ou composição; avaliação + escopo no NIST). Mês sem publicação não tem ponto — nada é interpolado
/// nem repetido. KNIGHT e NIST ficam em séries separadas e nenhuma série é somada a outra. Pontos consecutivos só são
/// "comparáveis" quando a regra única de compatibilidade das fotografias aceita o par; uma mudança de fórmula, catálogo,
/// esquema ou composição recomeça a série. [AEGIS-ASSESSMENT-VISUALS-01] O período é escolhido pelo chamador (fim e
/// quantidade de meses); o mês corrente não é inserido quando não pertence a ele.
/// </summary>
public static class PostureMonthlyHistory
{
    public const string Criterion =
        "Cada mês é representado pela última fotografia publicada no mês, por instrumento e fonte (ou avaliação e escopo). "
        + "Mês sem publicação fica sem resultado; versões de fórmula, catálogo, esquema ou composição diferentes não são ligadas.";

    public const int DefaultMonths = 12;
    public const int MaxMonths = 36;

    private const string MaturityType = "NistMaturity";

    /// <summary>
    /// <paramref name="until"/> é o ÚLTIMO mês do período (qualquer dia dele); nulo = mês de <paramref name="now"/>. Um fim no
    /// futuro é trazido para o mês corrente — não há publicação depois de agora.
    /// </summary>
    public static PostureMonthlyHistoryDto Build(
        IReadOnlyList<PostureSnapshotSummaryDto> snapshots, DateTimeOffset now, int months, DateOnly? until = null)
    {
        var axis = Axis(now, months, until);
        var series = snapshots
            .GroupBy(s => (s.Type, s.SemanticFamily))
            .Select(g => SeriesOf(g.ToList(), axis))
            .Where(s => s.Points.Count > 0)
            .OrderBy(s => s.Type == "AegisScoreNist" ? 0 : s.Type == MaturityType ? 1 : 2)
            .ThenBy(s => s.Label, StringComparer.CurrentCulture)
            .ToList();

        return new PostureMonthlyHistoryDto(Criterion, axis, series);
    }

    /// <summary>Eixo de meses (primeiro dia de cada mês), do mais antigo ao fim do período inclusive.</summary>
    public static IReadOnlyList<DateOnly> Axis(DateTimeOffset now, int months, DateOnly? until)
    {
        months = Math.Clamp(months <= 0 ? DefaultMonths : months, 1, MaxMonths);
        var current = MonthOf(now);
        var last = until is { } u ? new DateOnly(u.Year, u.Month, 1) : current;
        if (last > current) last = current;
        return Enumerable.Range(0, months).Select(i => last.AddMonths(i - months + 1)).ToList();
    }

    /// <summary>Uma série (mesmo tipo e família) projetada no eixo — pontos só nos meses com publicação.</summary>
    public static PostureMonthlySeries SeriesOf(IReadOnlyList<PostureSnapshotSummaryDto> family, IReadOnlyList<DateOnly> axis)
    {
        var first = axis[0];
        var last = axis[^1];
        var byMonth = family
            .GroupBy(s => MonthOf(s.CapturedAt))
            .Where(m => m.Key >= first && m.Key <= last)
            .OrderBy(m => m.Key)
            .Select(m => (Month: m.Key, Count: m.Count(),
                Rep: m.OrderByDescending(s => s.CapturedAt.UtcTicks).ThenByDescending(s => s.Id).First()))
            .ToList();

        var points = new List<PostureMonthlyPoint>();
        PostureSnapshotSummaryDto? previous = null;
        DateOnly? previousMonth = null;
        foreach (var (month, count, rep) in byMonth)
        {
            var reasons = previous is null
                ? Array.Empty<string>()
                : PostureSnapshotComparer.CheckCompatibility(Side(previous), Side(rep)).ToArray();
            var maturity = rep.Type == MaturityType;
            var comparable = previous is not null && reasons.Length == 0;
            double? delta = null;
            if (comparable && ValueOf(previous!) is { } a && ValueOf(rep) is { } b)
                delta = Math.Round(b - a, maturity ? 2 : 1, MidpointRounding.AwayFromZero);

            points.Add(new PostureMonthlyPoint(
                month, rep.Id, rep.CapturedAt, rep.EvaluationState, rep.Score, rep.Coverage, rep.EvaluatedItems,
                rep.EligibleItems, rep.FormulaVersion, rep.CatalogVersion, rep.SchemaVersion, rep.SourceLabel,
                rep.SourceRunId, count, comparable, reasons,
                maturity ? rep.MaturityCurrent : null,
                maturity ? rep.MaturityTarget : null,
                Applicable(rep),
                maturity ? rep.NistCycleName : null,
                Notes(previous, rep, comparable),
                delta,
                delta is null ? null : previousMonth,
                rep.DataRecency,
                rep.PeriodStart,
                rep.PeriodEnd,
                rep.CompositionLabels));
            previous = rep;
            previousMonth = month;
        }

        var latest = family.OrderByDescending(s => s.CapturedAt.UtcTicks).ThenByDescending(s => s.Id).First();
        var isMaturity = latest.Type == MaturityType;
        return new PostureMonthlySeries(latest.Type, latest.SemanticFamily, latest.SourceType, LabelOf(latest), points,
            InstrumentOf(latest.Type), isMaturity ? 1 : 0, isMaturity ? 5 : 100, CoverageBasisOf(latest.Type),
            isMaturity ? latest.NistAssessmentId : null, isMaturity ? latest.NistScopeId : null);
    }

    public static DateOnly MonthOf(DateTimeOffset at)
    {
        var u = at.UtcDateTime;
        return new DateOnly(u.Year, u.Month, 1);
    }

    /// <summary>Valor desenhado: maturidade atual (1–5) na série NIST; score (0–100) nas demais.</summary>
    public static double? ValueOf(PostureSnapshotSummaryDto s) => s.Type == MaturityType ? s.MaturityCurrent : s.Score;

    public static string LabelOf(PostureSnapshotSummaryDto s) => s.Type switch
    {
        "AegisScoreNist" => $"NIST · postura do ambiente ({s.FormulaVersion})",
        MaturityType => $"NIST · maturidade 1–5 — {s.SourceLabel ?? "avaliação"}",
        _ => $"KNIGHT · {s.SourceLabel ?? s.SourceType ?? "fonte"}",
    };

    public static string InstrumentOf(string type) => type switch
    {
        "AegisScoreNist" => "AEGIS Score (postura do ambiente) · 0–100",
        MaturityType => "AEGIS NIST · maturidade 1–5 (metodologia do AEGIS)",
        _ => "AEGIS KNIGHT · nota 0–100",
    };

    public static string CoverageBasisOf(string type) => type switch
    {
        MaturityType => "Cobertura = (subcategorias avaliadas + não aplicáveis) ÷ subcategorias do catálogo no escopo.",
        "AegisScoreNist" => "Cobertura = peso dos controles avaliados ÷ peso dos controles elegíveis do catálogo.",
        _ => "Cobertura = controles avaliados ÷ controles aplicáveis (não aplicáveis ficam fora da base).",
    };

    private static int? Applicable(PostureSnapshotSummaryDto s) =>
        s.Type == MaturityType ? s.EligibleItems - s.NotApplicableCount : s.Type == "Knight" ? s.EligibleItems : null;

    /// <summary>
    /// O que o leitor precisa saber antes de comparar dois meses: a base mudou (cobertura, universo aplicável) e queda de valor
    /// não é, sozinha, piora da segurança. Nenhuma causa é atribuída — só o que os dois registros mostram.
    /// </summary>
    private static IReadOnlyList<string> Notes(PostureSnapshotSummaryDto? previous, PostureSnapshotSummaryDto current, bool comparable)
    {
        var notes = new List<string>();
        if (previous is null) return notes;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        if (!comparable)
        {
            notes.Add("Não comparável com o ponto anterior: a série recomeça aqui.");
            return notes;
        }
        if (Math.Abs(previous.Coverage - current.Coverage) > 0.05)
            notes.Add($"Cobertura mudou de {previous.Coverage.ToString("0.#", pt)}% para {current.Coverage.ToString("0.#", pt)}%: valores sobre conjuntos diferentes.");
        var prevUniverse = Applicable(previous);
        var currUniverse = Applicable(current);
        if (prevUniverse is not null && currUniverse is not null && prevUniverse != currUniverse)
            notes.Add(current.Type == MaturityType
                ? $"Universo aplicável mudou de {prevUniverse} para {currUniverse} subcategorias."
                : $"Base aplicável mudou de {prevUniverse} para {currUniverse} controles.");
        if (ValueOf(previous) is { } a && ValueOf(current) is { } b && b < a)
            notes.Add(current.Type == MaturityType
                ? "A média atual caiu; isso não indica, sozinho, piora da segurança (rigor, evidência nova, cobertura ou universo)."
                : "A nota caiu; o registro não indica a causa. Compare as duas fotografias para ver os controles que mudaram.");
        return notes;
    }

    private static PostureComparisonSide Side(PostureSnapshotSummaryDto s) => new(
        s.CapturedAt, s.Type, s.SemanticFamily, s.FormulaVersion, s.CatalogVersion, s.SchemaVersion, s.Score, s.Coverage,
        s.CompliantCount, s.NonCompliantCount, s.MitigatedCount, s.NotEvaluatedCount, s.ErrorCount, s.NotApplicableCount,
        Array.Empty<PostureComparableItem>(), s.CompositionKey);
}
