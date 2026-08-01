using AegisScore.Application.Scoring;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// [AEGIS-AUD-019] Testes do hint determinístico <c>percent.higherIsBetter.v1</c>: BINÁRIO (≥ 80% Compliant,
/// &lt; 80% NonCompliant), fail-closed no domínio (0–100) e na unidade, e que NUNCA produz
/// <see cref="ControlStatus.MitigatedByThirdParty"/> — mitigação fica RESERVADA a prova explícita de
/// terceiro/controle compensatório (crédito de 50% preservado na <see cref="AegisScoreFormulaV1"/>).
/// </summary>
public sealed class EvidenceSignalEvaluatorTests
{
    private const string PercentHint = EvidenceSignalEvaluator.PercentHigherIsBetter;

    [Theory]
    [InlineData(100, ControlStatus.Compliant)]
    [InlineData(90, ControlStatus.Compliant)]
    [InlineData(80, ControlStatus.Compliant)]       // limite de conformidade (inclusive)
    [InlineData(79.9, ControlStatus.NonCompliant)]  // logo abaixo do limite
    [InlineData(60, ControlStatus.NonCompliant)]    // cobertura PARCIAL não é mitigação
    [InlineData(30, ControlStatus.NonCompliant)]
    [InlineData(0, ControlStatus.NonCompliant)]
    public void Percent_EhBinario_SemMitigacao(double value, ControlStatus expected)
    {
        var verdict = EvidenceSignalEvaluator.Evaluate(PercentHint, value, severity: null, unit: "percent");

        verdict.Should().NotBeNull();
        verdict!.Status.Should().Be(expected);
        verdict.Status.Should().NotBe(ControlStatus.MitigatedByThirdParty,
            "um percentual intermediário não prova terceiro/controle compensatório");
    }

    [Theory]
    [InlineData(null)]                        // valor ausente
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1.0)]                        // fora do domínio percentual
    [InlineData(101.0)]
    public void Percent_ValorInvalidoOuForaDoDominio_FailClosed(double? value) =>
        EvidenceSignalEvaluator.Evaluate(PercentHint, value, severity: null, unit: "percent")
            .Should().BeNull("dado malformado ou fora de 0–100 não inventa estado");

    [Theory]
    [InlineData("count")]
    [InlineData("ms")]
    [InlineData("bytes")]
    public void Percent_UnidadeIncompativel_FailClosed(string unit) =>
        EvidenceSignalEvaluator.Evaluate(PercentHint, 90, severity: null, unit: unit)
            .Should().BeNull("unidade não percentual não é interpretada como cobertura");

    [Theory]
    [InlineData("percent")]
    [InlineData("PERCENT")]   // case-insensitive
    [InlineData("%")]
    [InlineData("pct")]
    [InlineData(null)]        // ausente: o próprio hint já implica percentual
    [InlineData("")]
    public void Percent_UnidadeCompativelOuAusente_Avalia(string? unit) =>
        EvidenceSignalEvaluator.Evaluate(PercentHint, 90, severity: null, unit: unit)!
            .Status.Should().Be(ControlStatus.Compliant);

    [Fact]
    public void Mitigacao_ContinuaValendo50PorCento_ParaRegraCompensatoriaLegitima() =>
        // O percentual não produz mais MitigatedByThirdParty, mas a FÓRMULA preserva o crédito de 50% para
        // esse status quando ele vem de uma regra compensatória explícita (DeterministicControlEvaluator).
        AegisScoreFormulaV1.PointsFor(ControlStatus.MitigatedByThirdParty, 20).Should().Be(10);
}
