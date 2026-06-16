using Stars.Domain;

namespace Stars.Application.Scoring;

/// <summary>A single subcategory's current/target score (1–5), e.g. from a SubcategoryEvaluation.</summary>
public record SubcategoryScore(string SubcategoryCode, double? CurrentScore, double? TargetScore);

/// <summary>An aggregated score at a given granularity.</summary>
public record AggregateScore(
    SnapshotLevel Level,
    string RefCode,
    double CurrentScore,
    double TargetScore,
    double Gap,
    int Count);

/// <summary>The full maturity rollup for an assessment.</summary>
public record MaturityResult(
    AggregateScore Overall,
    IReadOnlyList<AggregateScore> Functions,
    IReadOnlyList<AggregateScore> Categories);

/// <summary>
/// Aggregates subcategory maturity into Category → Function → Overall scores.
/// Mirrors the workbook "Pivots": each level is the average of the level below
/// (category = mean of its subcategories, function = mean of its categories,
/// overall = mean of its functions). Pure logic — no EF/DB dependency, fully testable.
/// </summary>
public class MaturityScoringService
{
    /// <summary>Category code from a subcategory code: "GV.OC-01" → "GV.OC".</summary>
    public static string CategoryOf(string subcategoryCode) =>
        subcategoryCode.Contains('-') ? subcategoryCode[..subcategoryCode.IndexOf('-')] : subcategoryCode;

    /// <summary>Function code from any code: "GV.OC-01" / "GV.OC" → "GV".</summary>
    public static string FunctionOf(string code) =>
        code.Contains('.') ? code[..code.IndexOf('.')] : code;

    public MaturityResult Aggregate(IEnumerable<SubcategoryScore> scores)
    {
        var subs = scores.ToList();

        var categories = subs
            .GroupBy(s => CategoryOf(s.SubcategoryCode))
            .Select(g => FromSubcategories(SnapshotLevel.Category, g.Key, g))
            .OrderBy(c => c.RefCode)
            .ToList();

        var functions = categories
            .GroupBy(c => FunctionOf(c.RefCode))
            .Select(g => FromAggregates(SnapshotLevel.Function, g.Key, g))
            .OrderBy(f => f.RefCode)
            .ToList();

        var overall = functions.Count == 0
            ? new AggregateScore(SnapshotLevel.Overall, "ALL", 0, 0, 0, 0)
            : FromAggregates(SnapshotLevel.Overall, "ALL", functions);

        return new MaturityResult(overall, functions, categories);
    }

    /// <summary>Flatten a result into MaturitySnapshot rows for persistence.</summary>
    public IEnumerable<MaturitySnapshot> ToSnapshots(Guid assessmentId, MaturityResult result)
    {
        MaturitySnapshot Map(AggregateScore a) => new()
        {
            AssessmentId = assessmentId,
            Level = a.Level,
            RefCode = a.RefCode,
            CurrentScore = a.CurrentScore,
            TargetScore = a.TargetScore,
            Gap = a.Gap
        };

        yield return Map(result.Overall);
        foreach (var f in result.Functions) yield return Map(f);
        foreach (var c in result.Categories) yield return Map(c);
    }

    private static AggregateScore FromSubcategories(SnapshotLevel level, string code, IEnumerable<SubcategoryScore> items)
    {
        var list = items.ToList();
        var cur = Avg(list.Select(i => i.CurrentScore));
        var tgt = Avg(list.Select(i => i.TargetScore));
        return new AggregateScore(level, code, cur, tgt, Round(tgt - cur), list.Count);
    }

    private static AggregateScore FromAggregates(SnapshotLevel level, string code, IEnumerable<AggregateScore> items)
    {
        var list = items.ToList();
        var cur = Round(list.Average(i => i.CurrentScore));
        var tgt = Round(list.Average(i => i.TargetScore));
        return new AggregateScore(level, code, cur, tgt, Round(tgt - cur), list.Count);
    }

    private static double Avg(IEnumerable<double?> xs)
    {
        var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? 0 : Round(v.Average());
    }

    private static double Round(double x) => Math.Round(x, 2);
}
