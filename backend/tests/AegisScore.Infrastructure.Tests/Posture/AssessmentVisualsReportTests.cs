using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using FluentAssertions.Execution;
using UglyToad.PdfPig;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Relatórios visuais KNIGHT e NIST: o painel e a evolução mensal saem dos MESMOS fatos congelados
/// em HTML, PDF e CSV; os números reconciliam com os modelos autoritativos; ausência nunca vira zero; a fotografia sem histórico
/// congelado continua exportada como antes (sem painel); o hash cobre o histórico; e reexportar é determinístico.
/// </summary>
public sealed class AssessmentVisualsReportTests
{
    // ---- Reconciliação dos gráficos com os modelos autoritativos ------------------------------------------------------

    [Fact]
    public void Knight_GraficosReconciliamComOModeloDoRelatorio()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: true);
        var model = KnightReportModelBuilder.Build(s, integrityVerified: true);
        var history = FrozenPostureHistoryBuilder.Deserialize(s.HistoryJson)!;
        var charts = ReportChartBuilder.ForKnight(model, history);
        var k = model.Kpis;

        using var _ = new AssertionScope();
        var donut = charts.Executive.Single(c => c.Id == "knight-cobertura");
        donut.Series[0].Values.Should().Equal(k.Passed + k.Failed + k.Mitigated, k.NotEvaluated, k.Errors);
        donut.Series[0].Values.Sum().Should().Be(s.EligibleItems, "a rosca é a base aplicável — não aplicáveis ficam fora");
        (100.0 * donut.Series[0].Values[0]!.Value / s.EligibleItems).Should().BeApproximately(s.Coverage, 0.05);

        var sev = charts.Executive.Single(c => c.Id == "knight-severidade");
        sev.Series[0].Values.Sum().Should().Be(k.Findings);

        var services = charts.Executive.Single(c => c.Id == "knight-servicos");
        services.Categories.Should().Equal(model.ByService.Select(r => r.Label));
        Enumerable.Range(0, services.Categories.Count).Select(services.CategoryTotal).Sum().Should().Be(k.TotalControls,
            "cada controle aparece uma vez numa barra empilhada");

        var domains = charts.Detail.Single(c => c.Id == "knight-dominios");
        domains.Series[0].Values.Should().BeInDescendingOrder();
        domains.Series[0].Values.Sum().Should().Be(k.Failed);

        var line = charts.History!;
        line.Categories.Should().HaveCount(12);
        line.Series.Should().ContainSingle("a nota KNIGHT tem eixo próprio, nunca dividido com a maturidade 1–5");
        line.Series[0].Values.Count(v => v is null).Should().Be(1, "janeiro não teve publicação e fica sem ponto");
        line.Series[0].Values.Last().Should().Be(s.Score, "o último mês é a própria publicação");
    }

    [Fact]
    public void Nist_GraficosReconciliamComORelatorioEAusenciaNaoViraZero()
    {
        var s = AssessmentVisualsFixtures.Nist(withHistory: true);
        var r = NistReportCanonical.Deserialize(s.NistReportJson!);
        var charts = ReportChartBuilder.ForNist(r, FrozenPostureHistoryBuilder.Deserialize(s.HistoryJson));

        using var _ = new AssertionScope();
        var functions = charts.Executive.Single(c => c.Id == "nist-funcoes");
        functions.Categories.Should().Equal("GV", "ID", "PR", "DE", "RS", "RC");
        functions.Series.Single(x => x.Key == "atual").Values.Should().Equal(r.Functions.Select(f => f.Current));
        functions.Series.Single(x => x.Key == "atual").Values.Last().Should().BeNull("RC não tem avaliação — nunca zero");
        functions.Max.Should().Be(5);

        var radar = charts.Detail.Single(c => c.Kind == ReportChartKind.Radar);
        radar.Series.Should().HaveCount(2);
        ReportChartSvg.Figure(radar, _ => null).Should().NotContain("<polygon class=\"r-cur", "perfil incompleto não é fechado artificialmente");

        var states = charts.Executive.Single(c => c.Id == "nist-situacao");
        states.Series[0].Values.Sum().Should().Be(r.Summary.Subcategories);
        states.Series[0].Values[0].Should().Be(r.Summary.Evaluated);
        states.Series[0].Values[1].Should().Be(r.Summary.NotApplicable);

        var progress = charts.Detail.Single(c => c.Id == "nist-andamento");
        Enumerable.Range(0, 6).Select(progress.CategoryTotal).Should().Equal(r.Functions.Select(f => (double)f.Subcategories));

        var findings = charts.Executive.Single(c => c.Id == "nist-achados");
        findings.Series.Sum(x => x.Values.Sum()).Should().Be(r.Findings.Count);

        var line = charts.History!;
        line.Series.Select(x => x.Label).Should().Equal("Maturidade atual", "Alvo");
        line.Min.Should().Be(1);
        line.Max.Should().Be(5);
    }

    // ---- Formatos ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Html_TemPainelEHistorico_SemEstiloInline_ComCspPorHash_EOfflineSemRecursoExterno()
    {
        foreach (var (html, ids) in new[]
        {
            (Text(PostureSnapshotHtmlWriter.Write(AssessmentVisualsFixtures.Knight(true), true)), new[] { "ch-knight-cobertura", "ch-knight-severidade", "ch-knight-servicos", "ch-knight-dominios", "ch-knight-historico" }),
            (Text(NistReportHtmlWriter.Write(AssessmentVisualsFixtures.Nist(true), AssessmentVisualsFixtures.NistReport(), true)), new[] { "ch-nist-funcoes", "ch-nist-situacao", "ch-nist-achados", "ch-nist-radar", "ch-nist-andamento", "ch-nist-lacunas", "ch-nist-historico" }),
        })
        {
            using var _ = new AssertionScope();
            foreach (var id in ids) html.Should().Contain($"id=\"{id}\"");
            html.Should().Contain("<svg viewBox=").And.Contain("role=\"img\"").And.Contain("Valores do gráfico");
            html.Should().Contain("Evolução mensal").And.Contain("Sem publicação neste mês").And.Contain("esta publicação");
            Regex.IsMatch(html, "\\sstyle=").Should().BeFalse("a CSP não admite atributo style");
            html.Should().NotContain("unsafe-inline").And.NotContain("http://").And.NotMatchRegex("src=\"https?:");
            html.Should().Contain("default-src 'none'");
        }
    }

    [Fact]
    public void Csv_TemUmaLinhaPorValorDeGraficoEUmaPorMes_EReconcilia()
    {
        var k = AssessmentVisualsFixtures.Knight(true);
        var model = KnightReportModelBuilder.Build(k, true);
        var history = FrozenPostureHistoryBuilder.Deserialize(k.HistoryJson)!;
        var charts = ReportChartBuilder.ForKnight(model, history);
        var csv = CsvTestReader.Parse(Text(PostureSnapshotCsvWriter.Write(k)));

        using var _ = new AssertionScope();
        var header = csv[0];
        header.Should().HaveCount(63 + ReportCsvVisuals.HeadersId.Length);
        header[^ReportCsvVisuals.HeadersId.Length..].Should().Equal(ReportCsvVisuals.HeadersId);
        csv.Skip(1).Should().OnlyContain(r => r.Length == header.Length, "a tabela continua retangular");
        int Col(string name) => Array.IndexOf(header, name);

        // Cada tipo de linha tem RowKind e Origin próprios; painel e histórico não têm IndicatorId (não são controles).
        var chartRows = csv.Skip(1).Where(r => r[Col("RowKind")] == "Grafico").ToList();
        var historyRows = csv.Skip(1).Where(r => r[Col("RowKind")] == "Historico").ToList();
        int originCol = Col("Origin"), indicatorCol = Col("IndicatorId"), snapshotCol = Col("SnapshotId");
        var snapshotId = k.Id.ToString("D");
        chartRows.Should().OnlyContain(r => r[originCol] == "Painel" && r[indicatorCol] == "" && r[snapshotCol] == snapshotId);
        historyRows.Should().OnlyContain(r => r[originCol] == "Histórico" && r[indicatorCol] == "" && r[snapshotCol] == snapshotId);
        csv.Skip(1).Where(r => r[Col("Origin")] == "Automatizado").Select(r => r[Col("IndicatorId")]).Distinct()
            .Should().HaveCount(k.Indicators.Count, "as linhas de controle continuam reconciliando com o relatório");

        // Valores do painel = exatamente os desenhados (categoria × série), na ordem dos gráficos.
        var expectedCharts = ReportCsvVisuals.ChartRows(charts).Select(c => c.Select(x => x.Value ?? "").ToArray()).ToList();
        chartRows.Select(r => r[^ReportCsvVisuals.HeadersId.Length..]).Should().BeEquivalentTo(expectedCharts, o => o.WithStrictOrdering());
        var donut = charts.Executive.Single(c => c.Id == "knight-cobertura");
        chartRows.Where(r => r[Col("ChartId")] == "knight-cobertura").Select(r => double.Parse(r[Col("ChartValue")], CultureInfo.InvariantCulture))
            .Should().Equal(donut.Series[0].Values.Select(v => v!.Value));

        // Histórico = os 12 meses do período congelado; mês sem publicação dito, nunca zero; o último é esta publicação.
        historyRows.Select(r => r[Col("HistoryMonth")]).Should().Equal(history.Months.Select(m => m.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
        foreach (var r in historyRows)
        {
            var p = history.Points.SingleOrDefault(x => x.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture) == r[Col("HistoryMonth")]);
            if (p is null) { r[Col("HistoryValue")].Should().BeEmpty(); r[Col("HistoryNotes")].Should().Be("Sem publicação neste mês"); continue; }
            r[Col("HistoryValue")].Should().Be(p.Score?.ToString("0.#", CultureInfo.InvariantCulture) ?? "");
            r[Col("HistoryIsThisPublication")].Should().Be(p.IsThisPublication ? "true" : "false");
            r[Col("HistorySnapshotId")].Should().Be(p.IsThisPublication ? "" : p.SnapshotId.ToString("D"));
        }
        historyRows.Last()[Col("HistoryValue")].Should().Be(k.Score?.ToString("0.#", CultureInfo.InvariantCulture));

        var n = AssessmentVisualsFixtures.Nist(true);
        var nr = AssessmentVisualsFixtures.NistReport();
        var nh = FrozenPostureHistoryBuilder.Deserialize(n.HistoryJson)!;
        var ncsv = CsvTestReader.Parse(Text(NistReportCsvWriter.Write(n, nr)));
        ncsv[0].Should().HaveCount(NistReportCsvWriter.Headers.Length + ReportCsvVisuals.HeadersPt.Length);
        ncsv.Skip(1).Should().OnlyContain(r => r.Length == ncsv[0].Length, "a tabela continua retangular");
        var nChart = ncsv.Skip(1).Where(r => r[0] == "Gráfico").Select(r => r[^ReportCsvVisuals.HeadersPt.Length..]).ToList();
        nChart.Should().BeEquivalentTo(ReportCsvVisuals.ChartRows(ReportChartBuilder.ForNist(nr, nh)).Select(c => c.Select(x => x.Value ?? "").ToArray()),
            o => o.WithStrictOrdering());
        var nHist = ncsv.Skip(1).Where(r => r[0] == "Histórico").ToList();
        nHist.Select(r => r[Array.IndexOf(ncsv[0], "Mês")]).Should().Equal(nh.Months.Select(m => m.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
        nHist.Last()[Array.IndexOf(ncsv[0], "Valor no mês")].Should().Be(n.MaturityCurrent!.Value.ToString("0.##", CultureInfo.InvariantCulture),
            "o último mês é esta publicação, com a maturidade atual da fotografia");
    }

    [Fact]
    public void Pdf_TemGraficosEstaticosComTitulosValoresEHistorico()
    {
        var knight = PdfText(PostureSnapshotPdfWriter.Write(AssessmentVisualsFixtures.Knight(true)));
        var nist = PdfText(NistReportPdfWriter.Write(AssessmentVisualsFixtures.Nist(true), AssessmentVisualsFixtures.NistReport()));

        using var _ = new AssertionScope();
        knight.Should().Contain("Painel da avaliação").And.Contain("Cobertura da avaliação").And.Contain("Evolução mensal")
            .And.Contain("out/25").And.Contain("set/26").And.Contain("Sem publicação neste mês");
        nist.Should().Contain("Atual × alvo nas seis funções").And.Contain("Situação das subcategorias").And.Contain("Evolução mensal")
            .And.Contain("Maturidade atual").And.Contain("Alvo");
        nist.Should().NotContain("Perfil atual × alvo (radar)", "o radar fica só no HTML; as colunas mostram os mesmos números");
    }

    // ---- Compatibilidade, congelamento e integridade ------------------------------------------------------------------

    [Fact]
    public void FotografiaSemHistoricoCongelado_ExportaComoAntes_SemPainel()
    {
        var k = AssessmentVisualsFixtures.Knight(false);
        var n = AssessmentVisualsFixtures.Nist(false);
        var r = AssessmentVisualsFixtures.NistReport();

        using var _ = new AssertionScope();
        Text(PostureSnapshotHtmlWriter.Write(k, true)).Should().NotContain("id=\"aegis-visuals\"").And.NotContain("<svg");
        Text(NistReportHtmlWriter.Write(n, r, true)).Should().NotContain("<svg").And.Contain("class=\"bar-row\"");
        Text(PostureSnapshotCsvWriter.Write(k)).Split("\r\n")[0].Split(';').Should().HaveCount(63);
        Text(NistReportCsvWriter.Write(n, r)).Split("\r\n")[0].Split(';').Should().HaveCount(NistReportCsvWriter.Headers.Length);
        PdfText(PostureSnapshotPdfWriter.Write(k)).Should().NotContain("Painel da avaliação");
        PdfText(NistReportPdfWriter.Write(n, r)).Should().NotContain("Evolução mensal");
    }

    [Fact]
    public void Hash_CobreOHistorico_EFotografiaAntigaMantemOHash()
    {
        var legacy = AssessmentVisualsFixtures.Knight(false);
        var before = PostureSnapshotHasher.Compute(legacy);
        legacy.HistoryJson = "";
        PostureSnapshotHasher.Compute(legacy).Should().Be(before, "sem histórico o bloco não é escrito: o hash antigo não muda");

        var visual = AssessmentVisualsFixtures.Knight(true);
        PostureSnapshotHasher.Verify(visual).Should().BeTrue();
        visual.HistoryJson = visual.HistoryJson!.Replace("\"score\":63", "\"score\":93");
        PostureSnapshotHasher.Verify(visual).Should().BeFalse("alterar o histórico congelado é adulteração detectável");
    }

    [Fact]
    public void Reexportar_EDeterministico()
    {
        var k = AssessmentVisualsFixtures.Knight(true);
        var n = AssessmentVisualsFixtures.Nist(true);
        var r = AssessmentVisualsFixtures.NistReport();
        using var _ = new AssertionScope();
        Sha(PostureSnapshotHtmlWriter.Write(k, true)).Should().Be(Sha(PostureSnapshotHtmlWriter.Write(k, true)));
        Sha(PostureSnapshotCsvWriter.Write(k)).Should().Be(Sha(PostureSnapshotCsvWriter.Write(k)));
        Sha(NistReportHtmlWriter.Write(n, r, true)).Should().Be(Sha(NistReportHtmlWriter.Write(n, r, true)));
        Sha(NistReportCsvWriter.Write(n, r)).Should().Be(Sha(NistReportCsvWriter.Write(n, r)));
        PdfText(NistReportPdfWriter.Write(n, r)).Should().Be(PdfText(NistReportPdfWriter.Write(n, r)));
    }

    /// <summary>
    /// Grava os relatórios sintéticos para a verificação visual (navegador e leitor de PDF) quando
    /// <c>AEGIS_VISUALS_OUT</c> aponta um diretório. Sem a variável, só confere que todos são gerados.
    /// </summary>
    [Fact]
    public void Fixtures_GeramOsArquivosDeVerificacao()
    {
        var k = AssessmentVisualsFixtures.Knight(true);
        var n = AssessmentVisualsFixtures.Nist(true);
        var r = AssessmentVisualsFixtures.NistReport();
        var files = new (string Name, byte[] Bytes)[]
        {
            ("knight-visual.html", PostureSnapshotHtmlWriter.Write(k, true)),
            ("knight-visual.pdf", PostureSnapshotPdfWriter.Write(k)),
            ("knight-visual.csv", PostureSnapshotCsvWriter.Write(k)),
            ("nist-visual.html", NistReportHtmlWriter.Write(n, r, true)),
            ("nist-visual.pdf", NistReportPdfWriter.Write(n, r)),
            ("nist-visual.csv", NistReportCsvWriter.Write(n, r)),
        };
        files.Should().OnlyContain(f => f.Bytes.Length > 1000);
        var dir = Environment.GetEnvironmentVariable("AEGIS_VISUALS_OUT");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(dir, name), bytes);
    }

    private static string Text(byte[] b) => Encoding.UTF8.GetString(b).TrimStart('﻿');

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static string PdfText(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }
}
