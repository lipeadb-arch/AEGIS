using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// [AEGIS-AUD-001/002] Testes da autoridade ÚNICA da fórmula v1 — a semântica oficial de pontos, score
/// ANULÁVEL (NotEvaluated ≠ 0%), cobertura e arredondamento, num só lugar.
/// </summary>
public sealed class AegisScoreFormulaTests
{
    [Theory]
    [InlineData(ControlStatus.Compliant, 20, 20)]              // 100%
    [InlineData(ControlStatus.MitigatedByThirdParty, 20, 10)]  // 50% exato
    [InlineData(ControlStatus.MitigatedByThirdParty, 15, 8)]   // 7.5 → 8 (bancário/ToEven)
    [InlineData(ControlStatus.MitigatedByThirdParty, 5, 2)]    // 2.5 → 2 (bancário/ToEven)
    [InlineData(ControlStatus.NonCompliant, 20, 0)]            // 0, mas o peso fica no denominador
    public void PointsFor_TraduzStatusEmPontos_PreservandoArredondamentoBancario(
        ControlStatus status, int weight, int expected)
        => AegisScoreFormulaV1.PointsFor(status, weight).Should().Be(expected);

    [Fact]
    public void FormulaVersion_EhEstavel()
        => AegisScoreFormulaV1.Version.Should().Be("aegis-score-v1");

    [Fact]
    public void Compute_SemControlesAvaliados_NaoTemScore_EhNotEvaluated_NaoZero()
    {
        var r = AegisScoreFormulaV1.Compute(
            achievedScore: 0, evaluatedMaxScore: 0, evaluatedControls: 0,
            eligibleMaxScore: 100, eligibleControls: 6);

        r.EvaluationState.Should().Be(ScoreEvaluationState.NotEvaluated);
        r.Percentage.Should().BeNull("sem controles avaliados NÃO é 0% — é ausência de score");
        r.NotEvaluatedControls.Should().Be(6);
    }

    [Fact]
    public void Compute_TodosNonCompliant_TemScoreRealDeZero()
    {
        // 3 controles avaliados (peso 40), 0 pontos → 0% REAL — distinto de NotEvaluated.
        var r = AegisScoreFormulaV1.Compute(0, evaluatedMaxScore: 40, evaluatedControls: 3, eligibleMaxScore: 100, eligibleControls: 8);

        r.EvaluationState.Should().Be(ScoreEvaluationState.Evaluated);
        r.Percentage.Should().Be(0, "há avaliação: 0% é um score real, não NotEvaluated");
    }

    [Fact]
    public void Compute_Compliant_AtingeCem() =>
        AegisScoreFormulaV1.Compute(40, 40, 2, 100, 8).Percentage.Should().Be(100);

    [Fact]
    public void Compute_MisturaComMitigated_CalculaPercentualPonderado()
    {
        // Compliant(20) + Mitigated(10 de 20) = 30 obtidos / 40 avaliados = 75%.
        var r = AegisScoreFormulaV1.Compute(achievedScore: 30, evaluatedMaxScore: 40, evaluatedControls: 2, eligibleMaxScore: 100, eligibleControls: 8);
        r.Percentage.Should().Be(75);
    }

    [Fact]
    public void Compute_CoberturaParcial_SeparaScoreDeCobertura()
    {
        // 40 de 100 pesos avaliados → cobertura 40%; entre os avaliados, 30/40 = 75%.
        var r = AegisScoreFormulaV1.Compute(30, 40, 2, 100, 8);

        r.CoveragePercentage.Should().Be(40, "cobertura = peso avaliado / peso elegível");
        r.Percentage.Should().Be(75, "score = pontos obtidos / peso AVALIADO");
        r.Percentage.Should().NotBe(r.CoveragePercentage, "score e cobertura são eixos distintos");
    }

    [Fact]
    public void Compute_ParidadeComTrendDto_MesmaAutoridade()
    {
        // O DTO de tendência (snapshot) e o resultado do score usam a MESMA fórmula → mesmo percentual.
        var r = AegisScoreFormulaV1.Compute(achievedScore: 30, evaluatedMaxScore: 40, evaluatedControls: 2, eligibleMaxScore: 100, eligibleControls: 8);
        var trend = new TenantTrendDto(new DateOnly(2026, 7, 31), AchievedScore: 30, MaxScore: 40);

        trend.Percentage.Should().Be(r.Percentage, "current score e snapshot derivam o percentual pela mesma autoridade");
    }
}
