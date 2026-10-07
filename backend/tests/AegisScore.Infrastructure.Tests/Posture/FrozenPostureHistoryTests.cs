using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Histórico mensal: período escolhido (o mês corrente não é inserido à força), variação só entre
/// pontos comparáveis, quebra por composição, série congelada na publicação com a mesma série da prévia (409 quando mudou),
/// isolamento por tenant e fotografias antigas que não mudam quando outra é publicada depois.
/// </summary>
public sealed class FrozenPostureHistoryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaa7070-7070-7070-7070-7070707070a1");
    private static readonly Guid TenantB = Guid.Parse("bbbb7070-7070-7070-7070-7070707070b1");
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public FrozenPostureHistoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Regra mensal --------------------------------------------------------------------------------------------------

    private static PostureSnapshotSummaryDto Snap(DateTimeOffset at, double? score, double coverage = 80, int eligible = 20,
        string catalog = "ak-knight-v10", string family = "knight:MicrosoftEntraId", string? compositionKey = null, string type = "Knight") =>
        new(Guid.NewGuid(), type, PostureSnapshotSchema.KnightReportVersion, "knight-score-v1", catalog, family, "MicrosoftEntraId",
            "Microsoft Entra ID", at, score is null ? "NotEvaluated" : "Evaluated", score, coverage, (int)(eligible * coverage / 100), eligible,
            5, 3, 0, 2, 0, 1, at, new string('a', 64), CompositionKey: compositionKey);

    private static DateTimeOffset M(int y, int m, int d = 15) => new(y, m, d, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PeriodoEscolhido_NaoInsereOMesCorrente_EVariacaoSoEntreComparaveis()
    {
        var list = new List<PostureSnapshotSummaryDto>
        {
            Snap(M(2026, 3), 50), Snap(M(2026, 4), 55), Snap(M(2026, 6), 52, coverage: 90),
            Snap(M(2026, 7), 60, catalog: "ak-knight-v11"), Snap(M(2026, 10), 70, catalog: "ak-knight-v11"),
        };
        var h = PostureMonthlyHistory.Build(list, Now, 6, until: new DateOnly(2026, 8, 1));

        using var _ = new AssertionScope();
        h.Months.Should().Equal(Enumerable.Range(3, 6).Select(m => new DateOnly(2026, m, 1)), "o período termina em agosto: outubro não entra");
        var p = h.Series.Single().Points;
        p.Select(x => x.Month.Month).Should().Equal(3, 4, 6, 7);
        p[1].Delta.Should().Be(5);
        p[1].DeltaFrom.Should().Be(new DateOnly(2026, 3, 1));
        p[2].Delta.Should().Be(-3, "maio sem publicação: a variação é contra abril e diz isso");
        p[2].DeltaFrom.Should().Be(new DateOnly(2026, 4, 1));
        p[2].Notes.Should().Contain(n => n.Contains("Cobertura mudou")).And.Contain(n => n.Contains("A nota caiu"));
        p[3].ComparableWithPrevious.Should().BeFalse();
        p[3].Delta.Should().BeNull("catálogo diferente: nenhuma variação numérica");
        p[3].BreakReasons.Should().Equal(PostureSnapshotComparer.ReasonDifferentCatalog);
        h.Series.Single().ScaleMax.Should().Be(100);
    }

    [Fact]
    public void ConsolidadoComCatalogoDeUmaFonteTrocado_RecomecaASerie()
    {
        const string family = "knight:consolidated:MicrosoftEntraId+MicrosoftTeams";
        var list = new List<PostureSnapshotSummaryDto>
        {
            Snap(M(2026, 8), 60, catalog: "ak-knight-consolidated", family: family, compositionKey: "MicrosoftEntraId=ak-knight-v9|MicrosoftTeams=ak-knight-v10"),
            Snap(M(2026, 9), 64, catalog: "ak-knight-consolidated", family: family, compositionKey: "MicrosoftEntraId=ak-knight-v10|MicrosoftTeams=ak-knight-v10"),
        };
        var p = PostureMonthlyHistory.Build(list, Now, 12).Series.Single().Points;
        p[1].ComparableWithPrevious.Should().BeFalse();
        p[1].BreakReasons.Should().Equal(PostureSnapshotComparer.ReasonDifferentComposition);
        ReportChartBuilder.BreakLabel(p[1].BreakReasons[0]).Should().Contain("composição");
    }

    // ---- Histórico congelado (função pura) -----------------------------------------------------------------------------

    [Fact]
    public void Congelado_IncluiEstaPublicacaoSoNoPeriodo_IgnoraPublicacoesPosteriores_EDizOutraComposicao()
    {
        var current = Snap(M(2026, 9, 30), 66);
        var priors = new List<PostureSnapshotSummaryDto>
        {
            Snap(M(2026, 8), 61), Snap(M(2026, 7), 58), Snap(M(2026, 10, 2), 99),   // a de outubro é POSTERIOR: nunca entra
            Snap(M(2026, 9), 40, type: "NistMaturity"),                            // outro instrumento: nunca entra
        };

        var h = FrozenPostureHistoryBuilder.Build(current, priors, HistoryWindow.Default);
        using (new AssertionScope())
        {
            h.Until.Should().Be(new DateOnly(2026, 9, 1), "o padrão termina no mês da publicação");
            h.Months.Should().HaveCount(12);
            h.IncludesThisPublication.Should().BeTrue();
            h.Points.Single(p => p.IsThisPublication).Score.Should().Be(66);
            h.Points.Should().NotContain(p => p.Score == 99 || p.Score == 40);
            h.Series.Instrument.Should().Contain("KNIGHT");
        }

        var earlier = FrozenPostureHistoryBuilder.Build(current, priors, new HistoryWindow(new DateOnly(2026, 8, 1), 6));
        earlier.IncludesThisPublication.Should().BeFalse("o período escolhido termina antes do mês da publicação");
        earlier.Points.Should().NotContain(p => p.IsThisPublication);
        earlier.Points.Select(p => p.Month.Month).Should().Equal(7, 8);

        var cons = Snap(M(2026, 9, 30), 66, family: "knight:consolidated:A+B", catalog: "ak-knight-consolidated", compositionKey: "A=v1|B=v1");
        var other = Snap(M(2026, 8), 70, family: "knight:consolidated:A", catalog: "ak-knight-consolidated", compositionKey: "A=v1");
        FrozenPostureHistoryBuilder.Build(cons, new[] { other }, HistoryWindow.Default).RelatedSeries
            .Should().ContainSingle().Which.Should().Contain("Outra composição").And.Contain("ago/26");
    }

    [Fact]
    public void ImpressaoDigital_MudaQuandoASerieApresentadaMuda_EARecusaAconteceAntesDeGravar()
    {
        var current = Snap(M(2026, 9, 30), 66);
        var priors = new List<PostureSnapshotSummaryDto> { Snap(M(2026, 8), 61) };
        var basis = FrozenPostureHistoryBuilder.Build(current, priors, HistoryWindow.Default).BasisFingerprint;

        using var _ = new AssertionScope();
        FrozenPostureHistoryBuilder.Build(current with { Id = Guid.NewGuid(), CapturedAt = current.CapturedAt.AddMinutes(5) }, priors, HistoryWindow.Default)
            .BasisFingerprint.Should().Be(basis, "o id e o instante da nova fotografia não fazem parte do que a prévia mostrou");
        FrozenPostureHistoryBuilder.Build(current, priors.Append(Snap(M(2026, 9, 2), 63)).ToList(), HistoryWindow.Default)
            .BasisFingerprint.Should().NotBe(basis, "outra publicação entrou na série");
        FrozenPostureHistoryBuilder.Build(current, priors, new HistoryWindow(null, 6)).BasisFingerprint.Should().NotBe(basis, "outro período");
        FrozenPostureHistoryBuilder.Build(current with { CapturedAt = M(2026, 10, 1) }, priors, HistoryWindow.Default)
            .BasisFingerprint.Should().NotBe(basis, "o mês virou: o ponto desta publicação mudaria de mês");
        FrozenPostureHistoryBuilder.Build(current, priors.Append(Snap(M(2024, 1), 10)).ToList(), HistoryWindow.Default)
            .BasisFingerprint.Should().Be(basis, "publicação fora do período não aparece na série");

        var h = FrozenPostureHistoryBuilder.Build(current, priors, HistoryWindow.Default);
        FluentActions.Invoking(() => FrozenPostureHistoryBuilder.EnsureMatches(h, basis)).Should().NotThrow();
        FluentActions.Invoking(() => FrozenPostureHistoryBuilder.EnsureMatches(h, new string('0', 64))).Should().Throw<HistoryChangedException>();
    }

    // ---- Publicação KNIGHT (SQLite) -----------------------------------------------------------------------------------

    [Fact]
    public async Task Publicacao_CongelaASerieDaPrevia_RecusaSerieMudada_EIsolaTenant()
    {
        await using (var db = NewContext(TenantB))
        {
            await SeedRunAsync(db, 90);
            await ServiceFor(db, TenantB).PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftEntraId);
        }

        await using var dbA = NewContext(TenantA);
        var svc = ServiceFor(dbA, TenantA);
        var run1 = await SeedRunAsync(dbA, 40);
        var preview = await svc.PreviewKnightHistoryAsync(null, run1, HistoryWindow.Default);
        preview.Points.Should().ContainSingle(p => p.IsThisPublication).Which.Score.Should().Be(40);
        preview.Points.Should().HaveCount(1, "a fotografia do tenant B nunca entra na série do tenant A");

        var first = await svc.PublishAsync(PostureSnapshotType.Knight, null, run1, default, HistoryWindow.Default, preview.BasisFingerprint);
        first.History.Should().NotBeNull();
        first.History!.BasisFingerprint.Should().Be(preview.BasisFingerprint);
        first.History.Points.Single().SnapshotId.Should().Be(first.Summary.Id, "o ponto desta publicação leva o id dela, sem depender do histórico");
        first.Summary.HasFrozenHistory.Should().BeTrue();

        // Outra prévia, uma publicação no meio e a confirmação da prévia antiga: recusada, nada gravado.
        var run2 = await SeedRunAsync(dbA, 55);
        var stale = await svc.PreviewKnightHistoryAsync(null, run2, HistoryWindow.Default);
        await svc.PublishAsync(PostureSnapshotType.Knight, null, run1);
        var count = await dbA.PostureSnapshots.CountAsync();
        var act = () => svc.PublishAsync(PostureSnapshotType.Knight, null, run2, default, HistoryWindow.Default, stale.BasisFingerprint);
        (await act.Should().ThrowAsync<HistoryChangedException>()).Which.Message.Should().Contain("histórico mudou");
        (await dbA.PostureSnapshots.CountAsync()).Should().Be(count);

        var fresh = await svc.PreviewKnightHistoryAsync(null, run2, HistoryWindow.Default);
        var second = await svc.PublishAsync(PostureSnapshotType.Knight, null, run2, default, HistoryWindow.Default, fresh.BasisFingerprint);
        second.History!.Points.Single(p => p.IsThisPublication).PublishedInMonth.Should().Be(3, "três publicações no mês; vale a última");

        // A primeira fotografia continua com a série que congelou — reexportar não vê a publicação posterior.
        dbA.ChangeTracker.Clear();
        var stored = await dbA.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
            .SingleAsync(s => s.Id == first.Summary.Id);
        PostureSnapshotHasher.Verify(stored).Should().BeTrue();
        FrozenPostureHistoryBuilder.Deserialize(stored.HistoryJson)!.Points.Should().ContainSingle().Which.Score.Should().Be(40);
        var csv = System.Text.Encoding.UTF8.GetString((await new PostureSnapshotExporter(dbA).ExportAsync(first.Summary.Id, PostureExportFormat.Csv))!.Content);
        csv.Should().NotContain(";55;", "a nota de uma publicação posterior não aparece no relatório antigo");
    }

    [Fact]
    public async Task Previa_SemExecucao_Recusa_EPeriodoForaDoMesAtualNaoTrazEstaPublicacao()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        var missing = () => svc.PreviewKnightHistoryAsync(null, Guid.NewGuid(), HistoryWindow.Default);
        await missing.Should().ThrowAsync<PostureSnapshotNotAvailableException>();

        var run = await SeedRunAsync(db, 70);
        var lastMonth = PostureMonthlyHistory.MonthOf(DateTimeOffset.UtcNow).AddMonths(-1);
        var h = await svc.PreviewKnightHistoryAsync(null, run, new HistoryWindow(lastMonth, 12));
        h.IncludesThisPublication.Should().BeFalse();
        h.Points.Should().BeEmpty();
        h.Until.Should().Be(lastMonth);
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private static IPostureSnapshotService ServiceFor(AegisScoreDbContext db, Guid? tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new NistSignalMapper(db));

    private static async Task<Guid> SeedRunAsync(AegisScoreDbContext db, double score)
    {
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = KnightSourceType.MicrosoftEntraId, SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID", Status = KnightRunStatus.Completed, CatalogVersion = "ak-knight-v10", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Score = score, Coverage = 100, PassedCount = 1, ExposedCount = 1,
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-001", Title = "Contas privilegiadas sem MFA", Category = KnightIndicatorCategory.PrivilegedAccess,
            Severity = SeverityLevel.Critical, Status = KnightIndicatorStatus.Exposed, Evidence = "1 conta.", AffectedObjectCount = 1,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
        });
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-003", Title = "Contas de emergência", Category = KnightIndicatorCategory.PrivilegedAccess,
            Severity = SeverityLevel.Medium, Status = KnightIndicatorStatus.Passed, Evidence = "Ok.", AffectedObjectCount = 0,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }
}
