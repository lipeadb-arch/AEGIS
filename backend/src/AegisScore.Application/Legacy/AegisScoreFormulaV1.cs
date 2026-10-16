using System;
using AegisScore.Domain;

namespace AegisScore.Application.Scoring;

/// <summary>
/// [AEGIS-AUD-001] Autoridade ÚNICA e reproduzível do Aegis Score — a fórmula oficial v1
/// (<see cref="Version"/> = <c>aegis-score-v1</c>). Toda leitura (score atual, snapshot diário, tendência)
/// e toda escrita de pontos (<c>ControlStateWriter</c>) passam por aqui, de modo que numerador, denominador,
/// arredondamento e a semântica de NotEvaluated NUNCA tenham duas implementações capazes de divergir.
///
/// Semântica por controle:
/// <list type="bullet">
/// <item><c>Compliant</c> → 100% do peso (<c>MaxScorePoints</c>);</item>
/// <item><c>MitigatedByThirdParty</c> → 50% do peso;</item>
/// <item><c>NonCompliant</c> → 0, mas o PESO permanece no denominador AVALIADO;</item>
/// <item><c>NotEvaluated</c> → fora do numerador E do denominador (não é 0%, é "sem score").</item>
/// </list>
///
/// Score = pontos obtidos / peso dos controles AVALIADOS (nulo quando nada foi avaliado).
/// Cobertura = peso avaliado / peso ELEGÍVEL do catálogo ativo.
/// </summary>
public static class AegisScoreFormulaV1
{
    /// <summary>Versão oficial da fórmula — rastreável no contrato de leitura (e no snapshot, se necessário).</summary>
    public const string Version = "aegis-score-v1";

    private const double MitigatedCreditFactor = 0.5;

    /// <summary>
    /// Traduz o status de UM controle em pontos, limitado por <paramref name="maxScorePoints"/>. Arredondamento
    /// bancário (ToEven) PRESERVADO do escritor original: peso 5 mitigado = 2 (2.5→2), peso 15 = 8 (7.5→8).
    /// NotEvaluated não chega aqui — ele nem tem <c>TenantControlState</c> (representado só no read model).
    /// </summary>
    public static int PointsFor(ControlStatus status, int maxScorePoints) => status switch
    {
        ControlStatus.Compliant             => maxScorePoints,
        ControlStatus.MitigatedByThirdParty => (int)Math.Round(maxScorePoints * MitigatedCreditFactor),
        _                                   => 0,   // NonCompliant → não pontua; o peso fica no denominador
    };

    /// <summary>Arredondamento ÚNICO dos percentuais do produto (1 casa, ToEven), no mesmo lugar para todos.</summary>
    public static double RoundPercentage(double value) => Math.Round(value, 1);

    /// <summary>
    /// Consolida os agregados no <see cref="AegisScoreResult"/> oficial — score anulável (NotEvaluated quando
    /// nada foi avaliado) e cobertura sempre definida.
    /// </summary>
    /// <param name="achievedScore">SUM(CurrentScore) dos controles avaliados (numerador).</param>
    /// <param name="evaluatedMaxScore">SUM(MaxScorePoints) dos controles AVALIADOS (denominador do score).</param>
    /// <param name="evaluatedControls">Nº de controles avaliados (têm <c>TenantControlState</c>).</param>
    /// <param name="eligibleMaxScore">SUM(MaxScorePoints) de TODAS as subcategorias do catálogo ativo.</param>
    /// <param name="eligibleControls">Nº de subcategorias do catálogo ativo (denominador de cobertura).</param>
    public static AegisScoreResult Compute(
        int achievedScore, int evaluatedMaxScore, int evaluatedControls,
        int eligibleMaxScore, int eligibleControls) =>
        new(Version, achievedScore, evaluatedMaxScore, evaluatedControls, eligibleMaxScore, eligibleControls);
}

/// <summary>Estado de avaliação do score do tenant — o eixo que separa "sem score" de "0%".</summary>
public enum ScoreEvaluationState
{
    /// <summary>Nenhum controle avaliado: NÃO existe percentual (não é 0%).</summary>
    NotEvaluated = 0,

    /// <summary>Há controles avaliados: o percentual existe (podendo ser 0% real, se todos NonCompliant).</summary>
    Evaluated = 1,
}

/// <summary>
/// [AEGIS-AUD-001/002] Resultado oficial do Aegis Score, consolidado pela fórmula v1. Os agregados crus são
/// expostos para rastreabilidade; os derivados (percentual/cobertura/estado) são calculados AQUI, nunca no
/// frontend.
/// </summary>
public sealed record AegisScoreResult(
    string FormulaVersion,
    int AchievedScore,
    int EvaluatedMaxScore,
    int EvaluatedControls,
    int EligibleMaxScore,
    int EligibleControls)
{
    /// <summary>Controles do catálogo ativo ainda SEM avaliação (elegíveis − avaliados; nunca negativo).</summary>
    public int NotEvaluatedControls => Math.Max(0, EligibleControls - EvaluatedControls);

    /// <summary>Há base para um score? (ao menos um controle avaliado com peso).</summary>
    public ScoreEvaluationState EvaluationState =>
        EvaluatedControls > 0 && EvaluatedMaxScore > 0
            ? ScoreEvaluationState.Evaluated
            : ScoreEvaluationState.NotEvaluated;

    /// <summary>
    /// Percentual de postura — ANULÁVEL de propósito: <c>null</c> quando nada foi avaliado (NotEvaluated),
    /// nunca 0%. Havendo avaliação, é AchievedScore/EvaluatedMaxScore×100 (podendo ser 0% real).
    /// </summary>
    public double? Percentage => EvaluationState == ScoreEvaluationState.Evaluated
        ? AegisScoreFormulaV1.RoundPercentage((double)AchievedScore / EvaluatedMaxScore * 100)
        : null;

    /// <summary>Cobertura de avaliação: peso avaliado / peso elegível do catálogo ativo (0 quando não há catálogo).</summary>
    public double CoveragePercentage => EligibleMaxScore > 0
        ? AegisScoreFormulaV1.RoundPercentage((double)EvaluatedMaxScore / EligibleMaxScore * 100)
        : 0;
}
