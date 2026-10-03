using System.Linq;
using AegisScore.Application.Scoring;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Perfil atual × alvo com ausência explícita. O contraste com <see cref="MaturityScoringService.Aggregate"/>
/// é o motivo do teste: o cálculo antigo transformava "sem nota" em 0 e uma função com uma categoria avaliada e outra
/// vazia aparecia com metade da maturidade real.
/// </summary>
public sealed class MaturityProfileTests
{
    private readonly MaturityScoringService _svc = new();

    [Fact]
    public void CategoriaSemNota_NaoVirarZero_NemPuxaAFuncao()
    {
        var input = new[]
        {
            new SubcategoryProfileScore("PR.AA-01", 4, 5, false),
            new SubcategoryProfileScore("PR.AT-01", null, null, false),
        };

        var profile = _svc.AggregateProfile(input);
        profile.Categories.Single(c => c.RefCode == "PR.AT").Current.Should().BeNull();
        profile.Functions.Single().Current.Should().Be(4, "a categoria sem nota fica fora da média, não entra como zero");

        // O cálculo legado, com os mesmos dados, produz a conclusão enganosa que a jornada nova evita.
        var legacy = _svc.Aggregate(input.Select(i => new SubcategoryScore(i.SubcategoryCode, i.CurrentScore, i.TargetScore)));
        legacy.Functions.Single().CurrentScore.Should().Be(2, "legado: (4 + 0) / 2");
    }

    [Fact]
    public void Lacuna_SoSobrePares_NaoSeAplicaForaDasMedias_ContagensExplicam()
    {
        var profile = _svc.AggregateProfile(new[]
        {
            new SubcategoryProfileScore("GV.OC-01", 1, null, false),   // só atual
            new SubcategoryProfileScore("GV.OC-02", null, 5, false),   // só alvo
            new SubcategoryProfileScore("GV.OC-03", 3, 4, false),      // par
            new SubcategoryProfileScore("GV.OC-04", null, null, true), // não se aplica
        });

        var oc = profile.Categories.Single();
        oc.Current.Should().Be(2);
        oc.Target.Should().Be(4.5);
        oc.Gap.Should().Be(1, "lacuna média dos pares (4 − 3), não 4,5 − 2");
        (oc.Subcategories, oc.WithCurrent, oc.WithTarget, oc.WithGap, oc.NotApplicable).Should().Be((4, 2, 2, 1, 1));
    }

    [Fact]
    public void SemNenhumaNota_TudoIndeterminado()
    {
        var profile = _svc.AggregateProfile(new[] { new SubcategoryProfileScore("RC.RP-01", null, null, false) });
        profile.Overall.Current.Should().BeNull();
        profile.Overall.Target.Should().BeNull();
        profile.Overall.Gap.Should().BeNull();
        profile.Overall.Subcategories.Should().Be(1);
    }
}
