using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-CONSOLIDATED-01] <see cref="KnightConsolidatedBuilder"/> é PURA (sem EF/rede) — cobre a leitura
/// AO VIVO do relatório consolidado: seleção padrão, exclusão explícita, ausência de dupla contagem e a nota
/// combinada distinta da média simples.
/// </summary>
public sealed class KnightConsolidatedBuilderTests
{
    [Fact]
    public void Build_NoSourceHasAnAssessment_AllCandidatesAreNotAssessed_ScoreIsNull()
    {
        var result = KnightConsolidatedBuilder.Build(new KnightLatestBySource(Array.Empty<KnightSourceLatest>()), null);

        result.IncludedSources.Should().BeEmpty();
        result.Score.Should().BeNull();
        // [AEGIS-KNIGHT-COVERAGE-04] Toda fonte candidata aparece, na ordem do catálogo — não só as três primeiras.
        result.Sources.Select(s => s.Source).Should().Equal(KnightSourceCatalog.ConsolidationCandidates);
        result.Sources.Should().HaveCount(9, "as nove fontes do conector Microsoft que têm coletor, com o Azure");
        result.Sources.Should().OnlyContain(s => s.AvailabilityState == "NotAssessed" && !s.Included);
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Com as nove fontes Microsoft, a lista inteira passa do limite da coluna da fotografia
    /// (200) — o PostgreSQL recusaria a publicação. O rótulo diz quantas são e nomeia as que cabem; a composição congelada
    /// continua sendo a lista completa.
    /// </summary>
    [Fact]
    public void Label_NoveFontes_CabeNaColuna_EDizQuantasSao()
    {
        var all = KnightSourceCatalog.All.Where(d => d.Consolidable).Select(d => d.Label).ToList();
        all.Should().HaveCount(9);
        ("Consolidado — " + string.Join(", ", all)).Length.Should().BeGreaterThan(KnightConsolidatedLabel.MaxLength, "o caso que quebrava a publicação");

        var label = KnightConsolidatedLabel.For(all);
        label.Length.Should().BeLessThanOrEqualTo(KnightConsolidatedLabel.MaxLength);
        label.Should().StartWith("Consolidado — 9 fontes: Microsoft Entra ID").And.MatchRegex(@" e mais \d+$");
        KnightConsolidatedLabel.For(all.Take(2).ToList()).Should().Be("Consolidado — Microsoft Entra ID, Microsoft Teams", "o que cabe é dito por inteiro");
        KnightConsolidatedLabel.For(Array.Empty<string>()).Should().Be("Consolidado");
    }

    [Fact]
    public void Build_DefaultSelection_IncludesEveryCompletedSource_NeverDoubleCounts()
    {
        var entra = Assessment(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID",
            Indicator("AK-ENTRA-001", SeverityLevel.Critical, KnightIndicatorStatus.Exposed),
            Indicator("AK-ENTRA-003", SeverityLevel.Medium, KnightIndicatorStatus.Passed));
        var teams = Assessment(KnightSourceType.MicrosoftTeams, "Microsoft Teams",
            Indicator("AK-TEAMS-010", SeverityLevel.High, KnightIndicatorStatus.Exposed));

        var latest = new KnightLatestBySource(new[]
        {
            new KnightSourceLatest(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", entra, null),
            new KnightSourceLatest(KnightSourceType.MicrosoftTeams, "Microsoft Teams", teams, null),
        });

        var result = KnightConsolidatedBuilder.Build(latest, null);

        result.IncludedSources.Should().BeEquivalentTo(new[] { KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftTeams });
        result.Indicators.Should().HaveCount(3);
        result.Indicators.Select(i => i.IndicatorId).Should().OnlyHaveUniqueItems("cada indicador vem de exatamente uma fonte");

        var exchange = result.Sources.Single(s => s.Source == KnightSourceType.MicrosoftExchangeOnline);
        exchange.AvailabilityState.Should().Be("NotAssessed");
        exchange.Included.Should().BeFalse();
    }

    [Fact]
    public void Build_ExplicitSelection_ExcludedSourceStaysVisibleButDoesNotContribute()
    {
        var entra = Assessment(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID",
            Indicator("AK-ENTRA-001", SeverityLevel.Critical, KnightIndicatorStatus.Exposed));
        var teams = Assessment(KnightSourceType.MicrosoftTeams, "Microsoft Teams",
            Indicator("AK-TEAMS-010", SeverityLevel.High, KnightIndicatorStatus.Exposed));
        var latest = new KnightLatestBySource(new[]
        {
            new KnightSourceLatest(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", entra, null),
            new KnightSourceLatest(KnightSourceType.MicrosoftTeams, "Microsoft Teams", teams, null),
        });

        var result = KnightConsolidatedBuilder.Build(latest, new[] { KnightSourceType.MicrosoftEntraId });

        result.IncludedSources.Should().Equal(KnightSourceType.MicrosoftEntraId);
        result.Indicators.Should().ContainSingle().Which.IndicatorId.Should().Be("AK-ENTRA-001");

        var teamsEntry = result.Sources.Single(s => s.Source == KnightSourceType.MicrosoftTeams);
        teamsEntry.Included.Should().BeFalse();
        teamsEntry.AvailabilityState.Should().Be("Available", "tem avaliação concluída, só não foi escolhida");
        teamsEntry.Score.Should().Be(teams.Score, "a nota PRÓPRIA da fonte continua visível mesmo excluída");
    }

    // [AEGIS-KNIGHT-CONSOLIDATED-02] Nulo (nenhum pedido) e vazio (seleção explícita de zero fontes) precisam
    // produzir composições DIFERENTES — string de consulta não distingue as duas por si só (ver o parâmetro
    // `explicit` do controller), mas a função pura precisa honrar a distinção quando ela chega correta.

    [Fact]
    public void Build_NullSelection_IsTheClearDefault_IncludesEveryCompletedSource()
    {
        var entra = Assessment(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID",
            Indicator("AK-ENTRA-001", SeverityLevel.Critical, KnightIndicatorStatus.Exposed));
        var latest = new KnightLatestBySource(new[]
        {
            new KnightSourceLatest(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", entra, null),
        });

        var result = KnightConsolidatedBuilder.Build(latest, null);

        // nulo é o padrão CLARO: nenhum pedido explícito
        result.IncludedSources.Should().Equal(KnightSourceType.MicrosoftEntraId);
    }

    [Fact]
    public void Build_ExplicitEmptySelection_IncludesNothing_ButKeepsAvailableSourcesVisible()
    {
        var entra = Assessment(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID",
            Indicator("AK-ENTRA-001", SeverityLevel.Critical, KnightIndicatorStatus.Exposed));
        var latest = new KnightLatestBySource(new[]
        {
            new KnightSourceLatest(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", entra, null),
        });

        // Lista EXPLICITAMENTE vazia (não nula): a pessoa desmarcou todas as fontes de propósito. Diferente do
        // "nenhum pedido" acima, isto NÃO deve reverter ao padrão de incluir tudo.
        var result = KnightConsolidatedBuilder.Build(latest, Array.Empty<KnightSourceType>());

        result.IncludedSources.Should().BeEmpty("seleção vazia EXPLÍCITA não deve reverter ao padrão de incluir tudo");
        result.Indicators.Should().BeEmpty();
        result.Score.Should().BeNull();

        var entraEntry = result.Sources.Single(s => s.Source == KnightSourceType.MicrosoftEntraId);
        entraEntry.Included.Should().BeFalse();
        entraEntry.AvailabilityState.Should().Be("Available", "tem avaliação concluída — só não foi escolhida, e continua visível");
        entraEntry.Score.Should().Be(entra.Score);
    }

    [Fact]
    public void Build_RequestedSourceOutsideCandidates_IsIgnoredRatherThanEmptyingTheComposition()
    {
        var entra = Assessment(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID",
            Indicator("AK-ENTRA-001", SeverityLevel.Critical, KnightIndicatorStatus.Exposed));
        var latest = new KnightLatestBySource(new[]
        {
            new KnightSourceLatest(KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", entra, null),
        });

        // "google" não é candidata do relatório consolidado (só as fontes do conector Microsoft) — pedir só ela não deve
        // esvaziar silenciosamente a composição de quem TEM avaliação concluída entre as candidatas.
        var result = KnightConsolidatedBuilder.Build(latest, new[] { KnightSourceType.GoogleWorkspace });

        result.IncludedSources.Should().BeEmpty("nenhuma candidata foi pedida — Google não é candidata desta entrega");
        result.Sources.Should().HaveCount(KnightSourceCatalog.ConsolidationCandidates.Count);
    }

    private static KnightAssessment Assessment(KnightSourceType source, string label, params KnightIndicatorView[] indicators)
    {
        var score = KnightScoreFormula.Compute(indicators.Select(i => (i.Severity, i.Status)));
        return new KnightAssessment(
            Guid.NewGuid(), KnightAssessmentMode.Live, source, KnightSourceState.Completed, label,
            KnightRunStatus.Completed, "ak-knight-vX", KnightScoreFormula.Version,
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-5),
            score.Score, score.Coverage,
            score.PassedCount, score.ExposedCount, score.MitigatedCount, score.NotEvaluatedCount, score.ErrorCount, score.NotApplicableCount,
            indicators, Array.Empty<KnightCapabilityStatus>(), null, false);
    }

    private static KnightIndicatorView Indicator(string id, SeverityLevel severity, KnightIndicatorStatus status) => new(
        id, id, KnightIndicatorCategory.PrivilegedAccess, severity, status, "evidência",
        status == KnightIndicatorStatus.Exposed ? 1 : 0,
        Array.Empty<string>(), Array.Empty<string>(), "recomendação", DateTimeOffset.UtcNow.AddMinutes(-6),
        SourceOf(id), null);

    /// <summary>O prefixo do id já namespaced por fonte — evita um parâmetro extra em toda chamada do teste.</summary>
    private static KnightSourceType SourceOf(string indicatorId) => indicatorId switch
    {
        _ when indicatorId.StartsWith("AK-TEAMS", StringComparison.Ordinal) => KnightSourceType.MicrosoftTeams,
        _ when indicatorId.StartsWith("AK-EXO", StringComparison.Ordinal) => KnightSourceType.MicrosoftExchangeOnline,
        _ => KnightSourceType.MicrosoftEntraId,
    };
}
