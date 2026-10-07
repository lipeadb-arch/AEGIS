using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Colunas e linhas do CSV que reconciliam o painel visual e a evolução mensal com o HTML e o PDF:
/// uma linha por valor de gráfico (categoria × série, exatamente o desenhado) e uma linha por mês do período do histórico
/// congelado (mês sem publicação incluído, com o valor vazio). As colunas entram AO FINAL e só nas fotografias com histórico
/// congelado — o CSV das anteriores não muda. O radar e a linha mensal não geram linhas de gráfico: repetem, respectivamente,
/// as colunas atual × alvo e as linhas de histórico.
/// </summary>
public static class ReportCsvVisuals
{
    public static readonly string[] HeadersPt =
    {
        "Gráfico", "Título do gráfico", "Categoria do gráfico", "Série do gráfico", "Valor no gráfico", "Unidade", "Base do gráfico",
        "Mês", "Valor no mês", "Alvo no mês", "Cobertura no mês (%)", "Avaliados no mês", "Base aplicável no mês", "Fotografia do mês",
        "Publicada em", "Esta publicação", "Publicações no mês", "Comparável com o anterior", "Variação", "Variação desde",
        "Motivos de quebra", "Observações do mês", "Período avaliado", "Série histórica", "Período do histórico",
    };

    public static readonly string[] HeadersId =
    {
        "ChartId", "ChartTitle", "ChartCategory", "ChartSeries", "ChartValue", "ChartUnit", "ChartBasis",
        "HistoryMonth", "HistoryValue", "HistoryTarget", "HistoryCoverage", "HistoryEvaluated", "HistoryApplicable", "HistorySnapshotId",
        "HistoryPublishedAt", "HistoryIsThisPublication", "HistoryPublishedInMonth", "HistoryComparable", "HistoryDelta", "HistoryDeltaFrom",
        "HistoryBreakReasons", "HistoryNotes", "HistoryEvaluatedPeriod", "HistorySeries", "HistoryWindow",
    };

    /// <summary>Uma célula: valor e se é gerado pelo sistema (número/data, sem neutralizar) ou texto.</summary>
    public readonly record struct Cell(string? Value, bool System);

    /// <summary>Linhas de gráfico: (identificação curta da linha, células na ordem dos cabeçalhos acima).</summary>
    public static IEnumerable<IReadOnlyList<Cell>> ChartRows(ReportCharts charts)
    {
        foreach (var c in charts.Executive.Concat(charts.Detail).Where(c => c.Kind != ReportChartKind.Radar))
            for (var i = 0; i < c.Categories.Count; i++)
                foreach (var s in c.Series)
                    yield return Row(
                        T(c.Id), T(c.Title), T(c.Categories[i]), T(s.Label),
                        N(s.Values[i], c.Decimals), T(c.Unit), T(c.Basis));
    }

    /// <summary>Linhas do histórico: uma por mês do período, com a mesma identidade da série em todas.</summary>
    public static IEnumerable<IReadOnlyList<Cell>> HistoryRows(FrozenPostureHistory h)
    {
        var maturity = h.Series.Type == "NistMaturity";
        var byMonth = h.Points.ToDictionary(p => p.Month);
        var series = $"{h.Series.Label} · {h.Series.Instrument} · {h.Series.FormulaVersion} / {h.Series.CatalogVersion}"
            + (h.Series.Composition is { Count: > 0 } comp ? " · composição: " + string.Join(", ", comp) : "");
        var window = $"{h.From:yyyy-MM} a {h.Until:yyyy-MM}";
        foreach (var m in h.Months)
        {
            var cells = new List<Cell> { T(null), T(null), T(null), T(null), T(null), T(null), T(null) };
            cells.Add(S(m.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
            if (!byMonth.TryGetValue(m, out var p))
            {
                cells.AddRange(new[] { T(null), T(null), T(null), T(null), T(null), T(null), T(null), T(null), T(null), T(null), T(null), T(null), T(null),
                    T("Sem publicação neste mês"), T(null) });
            }
            else
            {
                cells.Add(N(maturity ? p.MaturityCurrent : p.Score, maturity ? 2 : 1));
                cells.Add(N(maturity ? p.MaturityTarget : null, 2));
                cells.Add(N(p.Coverage, 1));
                cells.Add(S(p.EvaluatedItems.ToString(CultureInfo.InvariantCulture)));
                cells.Add(S((p.ApplicableItems ?? p.EligibleItems).ToString(CultureInfo.InvariantCulture)));
                cells.Add(S(p.IsThisPublication ? null : p.SnapshotId.ToString("D")));
                cells.Add(S(p.IsThisPublication ? null : PostureSnapshotCsvWriter.Iso(p.CapturedAt)));
                cells.Add(S(p.IsThisPublication ? "true" : "false"));
                cells.Add(S(p.PublishedInMonth.ToString(CultureInfo.InvariantCulture)));
                cells.Add(S(p.ComparableWithPrevious ? "true" : "false"));
                cells.Add(N(p.Delta, 2));
                cells.Add(S(p.DeltaFrom?.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
                cells.Add(T(p.BreakReasons.Count == 0 ? null : string.Join(", ", p.BreakReasons.Select(ReportChartBuilder.BreakLabel))));
                cells.Add(T(p.Notes is { Count: > 0 } notes ? string.Join(" ", notes) : null));
                cells.Add(T(maturity
                    ? (p.CycleName ?? "") + (p.PeriodStart is { } ps && p.PeriodEnd is { } pe ? $" ({ps:yyyy-MM-dd} a {pe:yyyy-MM-dd})" : "")
                    : p.DataRecency is { } d ? "coleta " + PostureSnapshotCsvWriter.Iso(d) : null));
            }
            cells.Add(T(series));
            cells.Add(S(window));
            yield return cells;
        }
    }

    private static IReadOnlyList<Cell> Row(params Cell[] first)
    {
        var cells = first.ToList();
        while (cells.Count < HeadersPt.Length) cells.Add(T(null));
        return cells;
    }

    private static Cell T(string? v) => new(v, false);
    private static Cell S(string? v) => new(v, true);
    private static Cell N(double? v, int decimals) =>
        new(v?.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture), true);
}
