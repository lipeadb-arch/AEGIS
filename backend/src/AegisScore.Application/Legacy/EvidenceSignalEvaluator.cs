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

    /// <summary>
    /// Valor percentual em que MAIOR é melhor, com um ÚNICO threshold de conformidade (ex.: Secure Score).
    /// Binário de propósito: cobertura ≥ 80% comprova o controle (Compliant), abaixo disso NÃO comprova
    /// (NonCompliant). NUNCA produz <c>MitigatedByThirdParty</c> — mitigação exige prova EXPLÍCITA de terceiro
    /// ou controle compensatório (ver <c>DeterministicControlEvaluator</c>), que um percentual parcial não dá.
    /// </summary>
    public const string PercentHigherIsBetter = "percent.higherIsBetter.v1";

    /// <summary>Presença do evento COMPROVA que o controle opera (detecção/bloqueio/mitigação) → Compliant.</summary>
    public const string EventControlProven = "event.controlProven.v1";

    /// <summary>Presença do evento representa uma FALHA do controle → NonCompliant.</summary>
    public const string EventControlFailure = "event.controlFailure.v1";

    // Threshold ÚNICO de conformidade do percentual (v1). Maior é melhor: ≥ 80% comprova, < 80% não comprova.
    private const double CompliantThreshold = 80;

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
            PercentHigherIsBetter => EvaluatePercent(numericValue, unit),
            EventControlProven    => new EvidenceVerdict(
                ControlStatus.Compliant, "Evidência de telemetria comprova a operação efetiva do controle."),
            EventControlFailure   => new EvidenceVerdict(
                ControlStatus.NonCompliant, "Evidência de telemetria indica falha do controle."),
            _                     => null,   // hint ausente/desconhecido → sem veredito (evidência fica persistida)
        };

    private static EvidenceVerdict? EvaluatePercent(double? numericValue, string? unit)
    {
        // Fail-closed: só pontua um percentual BEM-FORMADO — presente, finito, no domínio 0–100 e com unidade
        // compatível (quando informada). Qualquer violação → sem veredito (o controle segue NotEvaluated), nunca
        // um estado inventado.
        if (numericValue is not { } v || double.IsNaN(v) || double.IsInfinity(v))
            return null;
        if (v < 0 || v > 100)
            return null;                 // fora do domínio percentual → não é 0% nem 100%: dado incompatível
        if (!IsPercentUnit(unit))
            return null;                 // unidade incompatível (ex.: "count") → não interpretar como percentual

        // BINÁRIO: a cobertura comprova o controle (≥ 80%) ou não (< 80%). NUNCA MitigatedByThirdParty — uma
        // cobertura parcial de Secure Score não é evidência de terceiro/controle compensatório (essa mitigação
        // só vem de regras determinísticas explícitas, no DeterministicControlEvaluator).
        var pct = v.ToString("0.#", CultureInfo.InvariantCulture);
        return v >= CompliantThreshold
            ? new EvidenceVerdict(ControlStatus.Compliant, $"Cobertura em {pct}% (≥ {CompliantThreshold:0}%).")
            : new EvidenceVerdict(ControlStatus.NonCompliant, $"Cobertura em {pct}% (< {CompliantThreshold:0}%).");
    }

    /// <summary>
    /// Unidade compatível com um percentual: AUSENTE (o próprio hint já implica "%") ou um token percentual
    /// conhecido, comparado de forma case-insensitive. Unidade presente e desconhecida rejeita o veredito.
    /// </summary>
    private static bool IsPercentUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return true;   // sem unidade: o hint percentual já define o domínio
        var u = unit.Trim();
        return u == "%"
            || u.Equals("percent", StringComparison.OrdinalIgnoreCase)
            || u.Equals("percentage", StringComparison.OrdinalIgnoreCase)
            || u.Equals("pct", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? hint) => string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
}
