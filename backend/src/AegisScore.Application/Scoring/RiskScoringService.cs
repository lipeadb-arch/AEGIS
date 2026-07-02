using System.Text.Json;
using AegisScore.Domain;

namespace AegisScore.Application.Scoring;

/// <summary>
/// Risk band cut-offs. A score &lt;= BaixoMax is Baixo, &lt;= MedioMax is Medio,
/// &lt;= AltoMax is Alto, otherwise Critico. Defaults derived from the
/// "Sistema de Gestão de Riscos" workbook; overridable per tenant via RiskAppetite.
/// </summary>
public record RiskBands(int BaixoMax, int MedioMax, int AltoMax)
{
    public static readonly RiskBands Default = new(BaixoMax: 4, MedioMax: 7, AltoMax: 9);
}

/// <summary>
/// Scores a risk from three 1–4 factors. Formula reverse-engineered and validated against
/// the client's own register: RiskLevel = Probability + Impact + ProcessValue (range 3–12).
/// (e.g. SEC0001 = 1+3+1 = 5 → Médio; SEC0002 = 4+4+3 = 11 → Crítico.)
/// The 2D heat-map uses 2·Probability + Impact, matching the "Matriz de Risco" tab.
/// </summary>
public class RiskScoringService
{
    public int Score(int probability, int impact, int processValue)
        => probability + impact + processValue;

    public RiskLevel Classify(int score, RiskBands? bands = null)
    {
        var b = bands ?? RiskBands.Default;
        if (score <= b.BaixoMax) return RiskLevel.Baixo;
        if (score <= b.MedioMax) return RiskLevel.Medio;
        if (score <= b.AltoMax) return RiskLevel.Alto;
        return RiskLevel.Critico;
    }

    public (int Score, RiskLevel Level) Evaluate(int probability, int impact, int processValue, RiskBands? bands = null)
    {
        var s = Score(probability, impact, processValue);
        return (s, Classify(s, bands));
    }

    /// <summary>Heat-map coordinate value (likelihood-weighted), per the matrix tab.</summary>
    public int HeatmapValue(int probability, int impact) => (2 * probability) + impact;

    /// <summary>
    /// Reads per-tenant bands from RiskAppetite.ThresholdsJson, e.g.
    /// {"baixoMax":4,"medioMax":7,"altoMax":9}. Falls back to defaults if absent/invalid.
    /// </summary>
    public RiskBands ParseBands(string? thresholdsJson)
    {
        if (string.IsNullOrWhiteSpace(thresholdsJson)) return RiskBands.Default;
        try
        {
            using var doc = JsonDocument.Parse(thresholdsJson);
            var root = doc.RootElement;
            int Get(string name, int fallback) =>
                root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? v : fallback;

            return new RiskBands(
                Get("baixoMax", RiskBands.Default.BaixoMax),
                Get("medioMax", RiskBands.Default.MedioMax),
                Get("altoMax", RiskBands.Default.AltoMax));
        }
        catch (JsonException)
        {
            return RiskBands.Default;
        }
    }
}
