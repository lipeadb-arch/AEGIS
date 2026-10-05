using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Posture;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Evolução mensal das fotografias publicadas: último publicado do mês representa o mês; mês sem
/// publicação fica sem ponto; KNIGHT e NIST em séries separadas; versões incompatíveis não são ligadas.
/// </summary>
public sealed class PostureMonthlyHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static PostureSnapshotSummaryDto Snap(string type, string family, DateTimeOffset at, double? score,
        string formula = "aegis-score-v1", string catalog = "nist-csf-2.0", string? source = null) =>
        new(Guid.NewGuid(), type, "1", formula, catalog, family, source, source is null ? null : "Microsoft Entra ID", at,
            score is null ? "NotEvaluated" : "Evaluated", score, 40, 10, 106, 5, 5, 0, 96, 0, 0, at, new string('a', 64));

    [Fact]
    public void UltimaPublicacaoDoMes_MesesSemPublicacaoSemPonto_SeriesSeparadas()
    {
        var aug1 = Snap("AegisScoreNist", "aegis-nist:NIST CSF 2.0", new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), 40);
        var aug2 = Snap("AegisScoreNist", "aegis-nist:NIST CSF 2.0", new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero), 45);
        var oct = Snap("AegisScoreNist", "aegis-nist:NIST CSF 2.0", new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 50);
        var knight = Snap("Knight", "knight:MicrosoftEntraId", new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), 70, "knight-score-v1", "ak-knight-v10", "MicrosoftEntraId");
        var old = Snap("AegisScoreNist", "aegis-nist:NIST CSF 2.0", new(2025, 1, 2, 0, 0, 0, TimeSpan.Zero), 10);

        var result = PostureMonthlyHistory.Build(new List<PostureSnapshotSummaryDto> { aug1, aug2, oct, knight, old }, Now, 12);

        result.Months.Should().HaveCount(12);
        result.Months.Last().Should().Be(new DateOnly(2026, 10, 1));
        result.Series.Should().HaveCount(2, "KNIGHT e NIST nunca são somados nem misturados");

        var nist = result.Series.First();
        nist.Type.Should().Be("AegisScoreNist");
        nist.Points.Select(p => p.Month).Should().Equal(new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 1));
        nist.Points[0].SnapshotId.Should().Be(aug2.Id, "o mês é representado pela ÚLTIMA publicação do mês");
        nist.Points[0].PublishedInMonth.Should().Be(2);
        nist.Points.Should().NotContain(p => p.Month == new DateOnly(2026, 9, 1), "setembro sem publicação fica sem ponto");
        nist.Points[1].ComparableWithPrevious.Should().BeTrue();
        nist.Points.Should().NotContain(p => p.SnapshotId == old.Id, "fora da janela");

        result.Series.Last().Label.Should().Be("KNIGHT · Microsoft Entra ID");
    }

    [Fact]
    public void VersaoIncompativel_RecomecaASerie_ComMotivo()
    {
        var sep = Snap("Knight", "knight:MicrosoftEntraId", new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero), 60, "knight-score-v1", "ak-knight-v9", "MicrosoftEntraId");
        var oct = Snap("Knight", "knight:MicrosoftEntraId", new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), 70, "knight-score-v1", "ak-knight-v10", "MicrosoftEntraId");

        var points = PostureMonthlyHistory.Build(new List<PostureSnapshotSummaryDto> { sep, oct }, Now, 6).Series.Single().Points;

        points[1].ComparableWithPrevious.Should().BeFalse("catálogos diferentes não formam uma série homogênea");
        points[1].BreakReasons.Should().Equal(PostureSnapshotComparer.ReasonDifferentCatalog);
    }
}
