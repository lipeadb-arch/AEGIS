using Stars.Domain;

namespace Stars.Application.Scoring;

/// <summary>
/// Normalized (0..1) risk-contributing factors for one subject (vuln/asset/risk/process).
/// All are "higher = worse" EXCEPT ControlEffectiveness (1 = fully effective), which the
/// service inverts internally — better controls lower the index.
/// </summary>
public record IcrInput(
    double TechnicalSeverity,
    double AssetCriticality,
    double BusinessImpact,
    double RecentExploitation,
    double RegulatoryExposure,
    double ControlEffectiveness,
    double OverdueActionPlan);

public record IcrComputation(double Score, IcrBand Band, IReadOnlyDictionary<string, double> Contributions);

/// <summary>
/// Índice de Criticidade de Risco Cibernético (ICR) — the "executive, continuous" score
/// from the risklab/Perinity thesis. Weighted sum of seven factors → 0–100.
/// Default weights sum to 1.0; they live in IcrWeightProfile and are tunable per tenant.
/// Bands: 0–39 Controlado · 40–59 Moderado · 60–79 Alto · 80–100 Crítico.
/// </summary>
public class IcrScoringService
{
    public IcrComputation Compute(IcrInput f, IcrWeightProfile w)
    {
        var contributions = new Dictionary<string, double>
        {
            ["technicalSeverity"]      = Clamp(f.TechnicalSeverity)      * w.TechnicalSeverity,
            ["assetCriticality"]       = Clamp(f.AssetCriticality)       * w.AssetCriticality,
            ["businessImpact"]         = Clamp(f.BusinessImpact)         * w.BusinessImpact,
            ["recentExploitation"]     = Clamp(f.RecentExploitation)     * w.RecentExploitation,
            ["regulatoryExposure"]     = Clamp(f.RegulatoryExposure)     * w.RegulatoryExposure,
            ["controlIneffectiveness"] = (1 - Clamp(f.ControlEffectiveness)) * w.ControlEffectiveness,
            ["overdueActionPlan"]      = Clamp(f.OverdueActionPlan)      * w.OverdueActionPlan,
        };

        var score = Math.Round(contributions.Values.Sum() * 100.0, 1);
        return new IcrComputation(score, BandOf(score), contributions);
    }

    public IcrBand BandOf(double score) => score switch
    {
        < 40 => IcrBand.Controlado,
        < 60 => IcrBand.Moderado,
        < 80 => IcrBand.Alto,
        _    => IcrBand.Critico
    };

    private static double Clamp(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
}
