using System;
using System.Collections.Generic;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// Fórmula PRÓPRIA e transparente de score e cobertura do AEGIS KNIGHT — autoridade única
/// (<see cref="Version"/> = <c>knight-score-v1</c>). É DISTINTA da fórmula global do AEGIS Score
/// (<c>aegis-score-v1</c>) e NÃO deve ser confundida com ela.
///
/// Pesos por severidade: Critical=10, High=7, Medium=4, Low=2, Informational=0.
/// Fatores por veredito: Passed=1, Mitigated=0.5, Exposed=0.
///
/// Score = round(100 × Σ(peso × fator) / Σ(pesos dos indicadores efetivamente avaliados)).
/// Regras:
///  - Passed/Exposed/Mitigated são os indicadores AVALIADOS (entram no score);
///  - NotEvaluated e Error NÃO entram no denominador do score, mas REDUZEM a cobertura;
///  - NotApplicable não entra nem no score nem na cobertura aplicável;
///  - sem nenhum indicador avaliado (ou peso avaliado zero), o score é <c>null</c> — nunca 0;
///  - Cobertura = avaliados / aplicáveis.
/// </summary>
public static class KnightScoreFormula
{
    /// <summary>Versão oficial da fórmula do KNIGHT — rastreável no contrato de leitura e na execução persistida.</summary>
    public const string Version = "knight-score-v1";

    private const double MitigatedFactor = 0.5;

    /// <summary>Peso de risco por severidade (0 = sem peso, não influencia o score).</summary>
    public static int WeightFor(SeverityLevel severity) => severity switch
    {
        SeverityLevel.Critical      => 10,
        SeverityLevel.High          => 7,
        SeverityLevel.Medium        => 4,
        SeverityLevel.Low           => 2,
        SeverityLevel.Informational => 0,
        _                           => 0,
    };

    /// <summary>
    /// Fator de crédito por veredito: Passed=1, Mitigated=0.5, Exposed=0. <c>null</c> para os vereditos que
    /// NÃO são avaliados (NotEvaluated/Error/NotApplicable) — o chamador os exclui do numerador/denominador.
    /// </summary>
    public static double? FactorFor(KnightIndicatorStatus status) => status switch
    {
        KnightIndicatorStatus.Passed    => 1.0,
        KnightIndicatorStatus.Mitigated => MitigatedFactor,
        KnightIndicatorStatus.Exposed   => 0.0,
        _                               => null,
    };

    /// <summary>Arredonda o score para inteiro, de forma explicável (0,5 sobe) — determinístico e reproduzível.</summary>
    public static double RoundScore(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Arredonda a cobertura a 1 casa (mesmo idioma de arredondamento de percentual do produto).</summary>
    public static double RoundCoverage(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Consolida os vereditos numa <see cref="KnightScoreResult"/>. Recebe apenas os pares
    /// (severidade, veredito) — a fórmula é PURA (sem EF, sem rede, sem IA) e testável.
    /// </summary>
    public static KnightScoreResult Compute(IEnumerable<(SeverityLevel Severity, KnightIndicatorStatus Status)> indicators)
    {
        double achievedWeighted = 0;
        double evaluatedWeight = 0;
        int evaluated = 0;
        int applicable = 0;
        int passed = 0, exposed = 0, mitigated = 0, notEvaluated = 0, error = 0, notApplicable = 0;

        foreach (var (severity, status) in indicators)
        {
            switch (status)
            {
                case KnightIndicatorStatus.Passed: passed++; break;
                case KnightIndicatorStatus.Exposed: exposed++; break;
                case KnightIndicatorStatus.Mitigated: mitigated++; break;
                case KnightIndicatorStatus.NotEvaluated: notEvaluated++; break;
                case KnightIndicatorStatus.Error: error++; break;
                case KnightIndicatorStatus.NotApplicable: notApplicable++; break;
            }

            // NotApplicable fica FORA da cobertura aplicável (o indicador não conta para este ambiente).
            if (status == KnightIndicatorStatus.NotApplicable)
                continue;

            applicable++;

            var factor = FactorFor(status);
            if (factor is null)
                continue;   // NotEvaluated/Error: aplicável, porém NÃO avaliado — reduz a cobertura, fora do score

            evaluated++;
            var weight = WeightFor(severity);
            evaluatedWeight += weight;
            achievedWeighted += weight * factor.Value;
        }

        // Score anulável: sem indicador avaliado (ou sem peso avaliado) NÃO existe percentual — nunca 0.
        double? score = evaluated > 0 && evaluatedWeight > 0
            ? RoundScore(100 * achievedWeighted / evaluatedWeight)
            : null;

        double coverage = applicable > 0
            ? RoundCoverage(100.0 * evaluated / applicable)
            : 0;

        return new KnightScoreResult(
            Version, score, coverage,
            evaluated, applicable,
            passed, exposed, mitigated, notEvaluated, error, notApplicable);
    }
}

/// <summary>Resultado consolidado da fórmula do KNIGHT — score anulável, cobertura e contagens por veredito.</summary>
/// <param name="FormulaVersion">Versão da fórmula aplicada (<c>knight-score-v1</c>).</param>
/// <param name="Score">Postura KNIGHT (0..100) ou <c>null</c> quando nada foi avaliado — nunca 0 por ausência.</param>
/// <param name="Coverage">Cobertura (0..100): avaliados / aplicáveis.</param>
public sealed record KnightScoreResult(
    string FormulaVersion,
    double? Score,
    double Coverage,
    int EvaluatedCount,
    int ApplicableCount,
    int PassedCount,
    int ExposedCount,
    int MitigatedCount,
    int NotEvaluatedCount,
    int ErrorCount,
    int NotApplicableCount);
