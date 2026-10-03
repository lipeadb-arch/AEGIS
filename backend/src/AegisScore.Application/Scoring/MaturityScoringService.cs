using AegisScore.Domain;

namespace AegisScore.Application.Scoring;

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
/// [AEGIS-NIST-JOURNEY-01] Uma subcategoria no perfil atual × alvo: níveis ANULÁVEIS (ausência ≠ 0) e "não se aplica".
/// </summary>
public record SubcategoryProfileScore(string SubcategoryCode, int? CurrentScore, int? TargetScore, bool NotApplicable);

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Agregado do perfil: médias anuláveis e as contagens que dizem sobre o que cada número foi
/// calculado (<paramref name="Subcategories"/> no catálogo do nível; com atual; com alvo; com lacuna determinável).
/// </summary>
public record ProfileScore(
    SnapshotLevel Level,
    string RefCode,
    double? Current,
    double? Target,
    double? Gap,
    int Subcategories,
    int WithCurrent,
    int WithTarget,
    int WithGap,
    int NotApplicable);

/// <summary>[AEGIS-NIST-JOURNEY-01] Perfil atual × alvo completo (geral, funções e categorias).</summary>
public record MaturityProfile(
    ProfileScore Overall,
    IReadOnlyList<ProfileScore> Functions,
    IReadOnlyList<ProfileScore> Categories);

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

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-01] Perfil atual × alvo com AUSÊNCIA explícita — a mesma hierarquia de <see cref="Aggregate"/>
    /// (categoria = média das subcategorias, função = média das categorias, geral = média das funções), sem a armadilha
    /// do cálculo antigo, em que uma categoria sem nenhuma nota virava 0 e puxava a função para baixo.
    ///
    /// Regras:
    /// <list type="bullet">
    /// <item>atual e alvo são médias SEPARADAS dos níveis existentes; sem nenhum nível, o valor é <c>null</c> (nunca 0);</item>
    /// <item>a lacuna é a média das lacunas das subcategorias que têm OS DOIS níveis — nunca a diferença entre duas médias
    /// calculadas sobre conjuntos diferentes; sem nenhum par, é <c>null</c> (indeterminada);</item>
    /// <item>subcategorias "não se aplica" ficam fora de todas as médias e são contadas à parte;</item>
    /// <item>as contagens dizem sobre quantas subcategorias cada número foi calculado.</item>
    /// </list>
    /// </summary>
    public MaturityProfile AggregateProfile(IEnumerable<SubcategoryProfileScore> scores)
    {
        var subs = scores.ToList();

        var categories = subs
            .GroupBy(s => CategoryOf(s.SubcategoryCode))
            .Select(g => ProfileFromSubcategories(SnapshotLevel.Category, g.Key, g.ToList()))
            .OrderBy(c => c.RefCode, StringComparer.Ordinal)
            .ToList();

        var functions = categories
            .GroupBy(c => FunctionOf(c.RefCode))
            .Select(g => ProfileFromAggregates(SnapshotLevel.Function, g.Key, g.ToList()))
            .OrderBy(f => f.RefCode, StringComparer.Ordinal)
            .ToList();

        var overall = ProfileFromAggregates(SnapshotLevel.Overall, "ALL", functions);
        return new MaturityProfile(overall, functions, categories);
    }

    private static ProfileScore ProfileFromSubcategories(SnapshotLevel level, string code, IReadOnlyList<SubcategoryProfileScore> items)
    {
        var applicable = items.Where(i => !i.NotApplicable).ToList();
        var current = applicable.Where(i => i.CurrentScore.HasValue).Select(i => i.CurrentScore!.Value).ToList();
        var target = applicable.Where(i => i.TargetScore.HasValue).Select(i => i.TargetScore!.Value).ToList();
        var gaps = applicable.Where(i => i.CurrentScore.HasValue && i.TargetScore.HasValue)
            .Select(i => i.TargetScore!.Value - i.CurrentScore!.Value).ToList();

        return new ProfileScore(level, code,
            current.Count == 0 ? null : Round(current.Average()),
            target.Count == 0 ? null : Round(target.Average()),
            gaps.Count == 0 ? null : Round(gaps.Average()),
            items.Count, current.Count, target.Count, gaps.Count, items.Count - applicable.Count);
    }

    private static ProfileScore ProfileFromAggregates(SnapshotLevel level, string code, IReadOnlyList<ProfileScore> items)
    {
        static double? MeanOf(IEnumerable<double?> xs)
        {
            var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            return v.Count == 0 ? null : Round(v.Average());
        }

        return new ProfileScore(level, code,
            MeanOf(items.Select(i => i.Current)),
            MeanOf(items.Select(i => i.Target)),
            MeanOf(items.Select(i => i.Gap)),
            items.Sum(i => i.Subcategories), items.Sum(i => i.WithCurrent), items.Sum(i => i.WithTarget),
            items.Sum(i => i.WithGap), items.Sum(i => i.NotApplicable));
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
