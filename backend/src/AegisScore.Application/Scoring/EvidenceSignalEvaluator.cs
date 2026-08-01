using System;
using System.Globalization;
using AegisScore.Domain;

namespace AegisScore.Application.Scoring;

/// <summary>Veredito determinístico derivado de UMA evidência ingerida (status + motivo legível).</summary>
public sealed record EvidenceVerdict(ControlStatus Status, string Reason);

/// <summary>
/// [AEGIS-AUD-019] Autoridade determinística que projeta uma EVIDÊNCIA INGERIDA (<c>EvidenceSignal</c>) num
/// veredito de controle, a partir do <see cref="SignalMapping.ScoringHint"/> — NUNCA do nome do sinal e
/// NUNCA do LLM. Trabalha só sobre campos NORMALIZADOS (NumericValue/Severity/Unit), sem interpretar o
/// payload bruto protegido. Fail-closed: hint ausente/desconhecido/incompatível, ou dado insuficiente para o
/// hint conhecido, resulta em <c>null</c> — a evidência permanece persistida e o controle segue NotEvaluated
/// (nenhum estado é inventado).
/// </summary>
public static class EvidenceSignalEvaluator
{
    // ---- Famílias de hint versionáveis (v1) — nomes ESTÁVEIS: são contrato de dados (SignalMapping.ScoringHint) ----

    /// <summary>Valor percentual em que MAIOR é melhor, com thresholds explícitos (ex.: Secure Score).</summary>
    public const string PercentHigherIsBetter = "percent.higherIsBetter.v1";

    /// <summary>Presença do evento COMPROVA que o controle opera (detecção/bloqueio/mitigação) → Compliant.</summary>
    public const string EventControlProven = "event.controlProven.v1";

    /// <summary>Presença do evento representa uma FALHA do controle → NonCompliant.</summary>
    public const string EventControlFailure = "event.controlFailure.v1";

    // Thresholds do percentual (v1). Maior é melhor.
    private const double CompliantThreshold = 80;
    private const double MitigatedThreshold = 50;

    /// <summary>True se o hint é reconhecido pela fórmula v1 (usado pelo seeder e por diagnóstico).</summary>
    public static bool IsKnownHint(string? scoringHint) => Normalize(scoringHint) is
        PercentHigherIsBetter or EventControlProven or EventControlFailure;

    /// <summary>
    /// Projeta a evidência num veredito determinístico, ou <c>null</c> (fail-closed) quando não há base:
    /// hint ausente/desconhecido, ou dados insuficientes para um hint conhecido. O chamador mantém a
    /// evidência e NÃO inventa estado.
    /// </summary>
    public static EvidenceVerdict? Evaluate(string? scoringHint, double? numericValue, int? severity, string? unit) =>
        Normalize(scoringHint) switch
        {
            PercentHigherIsBetter => EvaluatePercent(numericValue),
            EventControlProven    => new EvidenceVerdict(
                ControlStatus.Compliant, "Evidência de telemetria comprova a operação efetiva do controle."),
            EventControlFailure   => new EvidenceVerdict(
                ControlStatus.NonCompliant, "Evidência de telemetria indica falha do controle."),
            _                     => null,   // hint ausente/desconhecido → sem veredito (evidência fica persistida)
        };

    private static EvidenceVerdict? EvaluatePercent(double? numericValue)
    {
        if (numericValue is not { } v || double.IsNaN(v))
            return null;   // hint conhecido, mas dado insuficiente → fail-closed (sem veredito)

        var pct = v.ToString("0.#", CultureInfo.InvariantCulture);
        if (v >= CompliantThreshold)
            return new EvidenceVerdict(ControlStatus.Compliant, $"Cobertura em {pct}% (≥ {CompliantThreshold:0}%).");
        if (v >= MitigatedThreshold)
            return new EvidenceVerdict(ControlStatus.MitigatedByThirdParty,
                $"Cobertura parcial em {pct}% (entre {MitigatedThreshold:0}% e {CompliantThreshold:0}%).");
        return new EvidenceVerdict(ControlStatus.NonCompliant, $"Cobertura em {pct}% (< {MitigatedThreshold:0}%).");
    }

    private static string? Normalize(string? hint) => string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
}
