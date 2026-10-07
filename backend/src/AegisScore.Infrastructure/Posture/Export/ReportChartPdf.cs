using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace AegisScore.Infrastructure.Posture.Export;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Versões ESTÁTICAS dos gráficos no PDF, a partir dos MESMOS <see cref="ReportChart"/> do HTML.
/// O MigraDoc não desenha gráficos com o controle necessário (mês sem ponto, linha só entre vizinhos comparáveis), então o
/// texto (título, descrição, base, notas) entra no fluxo do documento e o desenho é VETORIAL: um espaço reservado de altura
/// fixa (uma tabela de uma célula, marcada com o gráfico) e, depois da paginação, o desenho com PDFsharp na posição exata em
/// que o espaço caiu. Escala, legenda e valores ficam escritos no desenho. O radar não entra no PDF: as colunas atual × alvo
/// já mostram os mesmos números.
/// </summary>
internal sealed class ReportChartPdf
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Color Muted = new(96, 106, 128);
    private const double WidthCm = 16.6;

    private readonly string _font;
    private readonly List<(Table Placeholder, ReportChart Chart)> _charts = new();

    public ReportChartPdf(string fontFamily) => _font = fontFamily;

    /// <summary>Acrescenta o gráfico ao fluxo: título, descrição, espaço reservado ao desenho, base e notas.</summary>
    public void Add(Section section, ReportChart chart)
    {
        if (chart.Kind == ReportChartKind.Radar) return;
        var title = section.AddParagraph(chart.Title);
        title.Format.Font.Size = 9.5;
        title.Format.Font.Bold = true;
        title.Format.SpaceBefore = Unit.FromMillimeter(3);
        title.Format.KeepWithNext = true;
        var desc = section.AddParagraph(chart.Description);
        desc.Format.Font.Size = 7.6;
        desc.Format.Font.Color = Muted;
        desc.Format.KeepWithNext = true;

        if (chart.EmptyMessage is { } empty)
        {
            var p = section.AddParagraph(empty);
            p.Format.Font.Size = 8;
            p.Format.Font.Italic = true;
        }
        else
        {
            var table = section.AddTable();
            table.Borders.Visible = false;
            table.AddColumn(Unit.FromCentimeter(WidthCm));
            var row = table.AddRow();
            row.HeightRule = RowHeightRule.Exactly;
            row.Height = Unit.FromPoint(HeightOf(chart));
            row.Cells[0].AddParagraph("");
            table.Tag = chart;
            _charts.Add((table, chart));
        }

        foreach (var text in new[] { chart.Basis }.Concat(chart.Notes ?? Array.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var p = section.AddParagraph(text);
            p.Format.Font.Size = 7.4;
            p.Format.Font.Color = Muted;
        }
    }

    /// <summary>Desenha cada gráfico na área que o espaço reservado ocupou depois da paginação.</summary>
    public void Draw(PdfDocumentRenderer renderer)
    {
        if (_charts.Count == 0) return;
        var pages = renderer.PdfDocument.PageCount;
        for (var page = 1; page <= pages; page++)
        {
            var infos = renderer.DocumentRenderer.GetRenderInfoFromPage(page);
            XGraphics? gfx = null;
            try
            {
                foreach (var info in infos)
                {
                    if (info.DocumentObject is not Table { Tag: ReportChart chart }) continue;
                    var area = info.LayoutInfo.ContentArea;
                    gfx ??= XGraphics.FromPdfPage(renderer.PdfDocument.Pages[page - 1], XGraphicsPdfPageOptions.Append);
                    var rect = new XRect(area.X.Point, area.Y.Point, area.Width.Point, area.Height.Point);
                    new Painter(gfx, _font, rect).Draw(chart);
                }
            }
            finally
            {
                gfx?.Dispose();
            }
        }
    }

    private static double HeightOf(ReportChart c) => c.Kind switch
    {
        ReportChartKind.Columns => 175,
        ReportChartKind.Line => 185,
        ReportChartKind.Donut => 132,
        ReportChartKind.HorizontalBars or ReportChartKind.StackedBars => 22 + 24 * c.Categories.Count,
        _ => 150,
    };

    // ---- Desenho -------------------------------------------------------------------------------------------------------

    private sealed class Painter
    {
        private readonly XGraphics _g;
        private readonly XRect _r;
        private readonly XFont _small, _text, _bold, _big;

        public Painter(XGraphics g, string font, XRect r)
        {
            _g = g;
            _r = r;
            _small = new XFont(font, 6.6, XFontStyleEx.Regular);
            _text = new XFont(font, 7.4, XFontStyleEx.Regular);
            _bold = new XFont(font, 7.0, XFontStyleEx.Bold);
            _big = new XFont(font, 14, XFontStyleEx.Bold);
        }

        public void Draw(ReportChart c)
        {
            switch (c.Kind)
            {
                case ReportChartKind.Columns: Columns(c); break;
                case ReportChartKind.HorizontalBars: Bars(c, stacked: false); break;
                case ReportChartKind.StackedBars: Bars(c, stacked: true); break;
                case ReportChartKind.Donut: Donut(c); break;
                case ReportChartKind.Line: Line(c); break;
            }
        }

        // Legenda no topo do desenho (cor + rótulo escrito).
        private double Legend(ReportChart c)
        {
            if (c.Series.Count == 1 && c.CategoryTones is not null) return _r.Top + 2;
            var x = _r.Left;
            var y = _r.Top + 2;
            foreach (var s in c.Series)
            {
                var label = s.Label + (s.Dashed ? " (tracejado)" : "");
                var w = _g.MeasureString(label, _small).Width + 18;
                if (x + w > _r.Right) { x = _r.Left; y += 10; }
                if (s.Dashed) _g.DrawLine(new XPen(Color(s.Tone), 1.6) { DashStyle = XDashStyle.Dash }, x, y + 4, x + 10, y + 4);
                else _g.DrawRectangle(new XSolidBrush(Color(s.Tone)), x, y, 7, 7);
                _g.DrawString(label, _small, Ink, new XPoint(x + 10, y + 6.2));
                x += w;
            }
            return y + 12;
        }

        private void Columns(ReportChart c)
        {
            var top = Legend(c) + 8;
            var x0 = _r.Left + 26;
            var x1 = _r.Right - 4;
            var y1 = _r.Bottom - 18;
            Grid(c, x0, x1, top, y1);
            var n = c.Categories.Count;
            var group = (x1 - x0) / Math.Max(1, n);
            var k = c.Series.Count;
            var bw = Math.Min(26, group * 0.7 / Math.Max(1, k));
            for (var i = 0; i < n; i++)
            {
                var gx = x0 + group * i + (group - bw * k) / 2;
                for (var j = 0; j < k; j++)
                {
                    var s = c.Series[j];
                    var x = gx + bw * j;
                    var tone = k == 1 && c.CategoryTones is { } tones ? tones[i] : s.Tone;
                    if (s.Values[i] is { } v)
                    {
                        var y = Y(v, c, top, y1);
                        _g.DrawRectangle(new XSolidBrush(Color(tone)), x, y, bw - 1.5, Math.Max(0.4, y1 - y));
                        Center(ReportChart.Format(v, c.Decimals), _bold, x + (bw - 1.5) / 2, y - 2.5);
                    }
                    else Center("—", _small, x + (bw - 1.5) / 2, y1 - 2.5);
                }
                Center(ReportChartSvg.Clip(c.Categories[i], 16), _text, x0 + group * i + group / 2, y1 + 10);
            }
            if (c.Series.Any(s => s.Values.Any(v => v is null)))
                _g.DrawString("— = " + c.EmptyLabel, _small, MutedBrush, new XRect(x0, y1 + 11, x1 - x0, 8), XStringFormats.TopRight);
        }

        private void Bars(ReportChart c, bool stacked)
        {
            var top = (c.Series.Count > 1 ? Legend(c) : _r.Top + 2) + 2;
            var x0 = _r.Left;
            var x1 = _r.Right - 30;
            var max = c.Max <= 0 ? 1 : c.Max;
            for (var i = 0; i < c.Categories.Count; i++)
            {
                var y = top + 24 * i;
                _g.DrawString(ReportChartSvg.Clip(c.Categories[i], 95), _text, Ink, new XPoint(x0, y + 7));
                _g.DrawRectangle(new XSolidBrush(XColor.FromArgb(238, 241, 246)), x0, y + 10, x1 - x0, 9);
                var x = x0;
                double total = 0;
                foreach (var s in c.Series)
                {
                    if (s.Values[i] is not { } v || v <= 0) continue;
                    total += v;
                    var w = (x1 - x0) * v / max;
                    var tone = !stacked && c.Series.Count == 1 && c.CategoryTones is { } tones ? tones[i] : s.Tone;
                    _g.DrawRectangle(new XSolidBrush(Color(tone)), x, y + 10, w, 9);
                    if (stacked && w >= 14)
                        Center(ReportChart.Format(v, c.Decimals), _bold, x + w / 2, y + 17.2, Light(s.Tone) ? Ink : XBrushes.White);
                    if (stacked) x += w;
                }
                var value = stacked ? total : c.Series[0].Values[i] ?? 0;
                var end = x0 + (x1 - x0) * value / max;
                _g.DrawString(ReportChart.Format(value, stacked ? 0 : c.Decimals), _bold, Ink, new XPoint(Math.Min(end + 3, x1 + 3), y + 17.2));
            }
        }

        private void Donut(ReportChart c)
        {
            var values = c.Series[0].Values;
            var total = values.Sum(v => v ?? 0);
            const double size = 112;
            var box = new XRect(_r.Left + 6, _r.Top + 8, size, size);
            var angle = -90.0;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } v || v <= 0 || total <= 0) continue;
                var sweep = 360.0 * v / total;
                _g.DrawPie(new XSolidBrush(Color(c.CategoryTones?[i] ?? c.Series[0].Tone)), box, angle, sweep);
                angle += sweep;
            }
            var hole = 64.0;
            _g.DrawEllipse(XBrushes.White, box.Left + (size - hole) / 2, box.Top + (size - hole) / 2, hole, hole);
            Center(ReportChart.Format(total, 0), _big, box.Left + size / 2, box.Top + size / 2 + 4);
            Center(c.Unit, _small, box.Left + size / 2, box.Top + size / 2 + 13);
            var lx = box.Right + 22;
            var ly = _r.Top + 14;
            for (var i = 0; i < c.Categories.Count; i++)
            {
                var v = values[i] ?? 0;
                _g.DrawRectangle(new XSolidBrush(Color(c.CategoryTones?[i] ?? c.Series[0].Tone)), lx, ly - 6.5, 7.5, 7.5);
                _g.DrawString(c.Categories[i], _text, Ink, new XPoint(lx + 11, ly));
                _g.DrawString($"{ReportChart.Format(v, 0)} · {ReportChartSvg.Pct(v, total)}", _bold, Ink, new XPoint(lx + 150, ly));
                ly += 17;
            }
        }

        private void Line(ReportChart c)
        {
            var top = Legend(c) + 8;
            var x0 = _r.Left + 26;
            var x1 = _r.Right - 8;
            var y1 = _r.Bottom - 16;
            Grid(c, x0, x1, top, y1);
            var n = c.Categories.Count;
            double X(int i) => n <= 1 ? (x0 + x1) / 2 : x0 + 6 + (x1 - x0 - 12) * i / (n - 1);
            foreach (var s in c.Series)
            {
                var pen = new XPen(Color(s.Tone), 1.8);
                if (s.Dashed) pen.DashStyle = XDashStyle.Dash;
                for (var i = 1; i < n; i++)
                    if (s.Connect is { } con && con[i] && s.Values[i] is { } b && s.Values[i - 1] is { } a)
                        _g.DrawLine(pen, X(i - 1), Y(a, c, top, y1), X(i), Y(b, c, top, y1));
            }
            var first = true;
            foreach (var s in c.Series)
            {
                for (var i = 0; i < n; i++)
                {
                    if (s.Values[i] is not { } v) continue;
                    var y = Y(v, c, top, y1);
                    var r = s.Dashed ? 2.2 : 2.8;
                    _g.DrawEllipse(new XSolidBrush(Color(s.Tone)), X(i) - r, y - r, 2 * r, 2 * r);
                    if (first) Center(ReportChart.Format(v, c.Decimals), _bold, X(i), y - 4.5);
                }
                first = false;
            }
            for (var i = 0; i < n; i++)
                Center(c.Categories[i], _small, X(i), y1 + 9);
        }

        private void Grid(ReportChart c, double x0, double x1, double top, double y1)
        {
            var pen = new XPen(XColor.FromArgb(214, 220, 232), 0.5);
            foreach (var t in ReportChartSvg.Ticks(c))
            {
                var y = Y(t, c, top, y1);
                _g.DrawLine(pen, x0, y, x1, y);
                _g.DrawString(ReportChart.Format(t, t % 1 == 0 ? 0 : 1), _small, MutedBrush, new XRect(x0 - 26, y - 4, 22, 8), XStringFormats.TopRight);
            }
        }

        private static double Y(double v, ReportChart c, double top, double y1) =>
            y1 - (Math.Clamp(v, c.Min, c.Max) - c.Min) / Math.Max(1e-9, c.Max - c.Min) * (y1 - top);

        private void Center(string text, XFont font, double x, double baseline, XBrush? brush = null)
        {
            var w = _g.MeasureString(text, font).Width;
            _g.DrawString(text, font, brush ?? Ink, new XPoint(x - w / 2, baseline));
        }

        private static readonly XBrush Ink = new XSolidBrush(XColor.FromArgb(24, 32, 46));
        private static readonly XBrush MutedBrush = new XSolidBrush(XColor.FromArgb(95, 106, 128));

        private static bool Light(string tone) => tone is "na" or "ne" or "cur-light" or "warn";

        /// <summary>Mesma paleta do HTML (tema claro), por classe de tom.</summary>
        private static XColor Color(string tone) => tone switch
        {
            "acc" => XColor.FromArgb(10, 110, 148),
            "ok" => XColor.FromArgb(27, 127, 59),
            "bad" => XColor.FromArgb(179, 38, 30),
            "warn" => XColor.FromArgb(168, 100, 0),
            "ne" => XColor.FromArgb(123, 134, 153),
            "err" => XColor.FromArgb(107, 63, 160),
            "na" => XColor.FromArgb(183, 192, 207),
            "cur" => XColor.FromArgb(11, 94, 155),
            "tgt" => XColor.FromArgb(217, 119, 6),
            "cur-light" => XColor.FromArgb(127, 178, 229),
            "sev-Critical" => XColor.FromArgb(142, 27, 18),
            "sev-High" => XColor.FromArgb(194, 65, 12),
            "sev-Medium" => XColor.FromArgb(161, 98, 7),
            "sev-Low" => XColor.FromArgb(29, 95, 184),
            _ => XColor.FromArgb(86, 98, 122),
        };
    }

    /// <summary>Tabela mês a mês do histórico congelado, para o PDF (os mesmos fatos da tabela do HTML).</summary>
    public static void HistoryTable(Section section, FrozenPostureHistory h, Action<Row, int, string, bool> cell)
    {
        var maturity = h.Series.Type == "NistMaturity";
        var t = section.AddTable();
        t.Borders.Color = new Color(206, 213, 226);
        t.Borders.Width = 0.25;
        t.LeftPadding = Unit.FromMillimeter(1.1);
        t.RightPadding = Unit.FromMillimeter(1.1);
        foreach (var w in maturity ? new[] { 1.4, 1.2, 1.2, 3.2, 3.6, 2.6, 3.4 } : new[] { 1.4, 1.3, 3.6, 3.6, 2.8, 3.9 })
            t.AddColumn(Unit.FromCentimeter(w));
        var head = t.AddRow();
        head.HeadingFormat = true;
        head.Shading.Color = new Color(28, 36, 56);
        var titles = maturity
            ? new[] { "Mês", "Atual", "Alvo", "Cobertura e base", "Rodada e período", "Publicação", "Variação e observações" }
            : new[] { "Mês", "Nota", "Cobertura e base", "Coleta / composição", "Publicação", "Variação e observações" };
        for (var i = 0; i < titles.Length; i++)
        {
            var p = head.Cells[i].AddParagraph(titles[i]);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 7;
            p.Format.Font.Color = Colors.White;
        }
        var byMonth = h.Points.ToDictionary(p => p.Month);
        foreach (var m in h.Months)
        {
            var row = t.AddRow();
            cell(row, 0, FrozenPostureHistoryBuilder.MonthLabel(m), true);
            if (!byMonth.TryGetValue(m, out var p))
            {
                row.Cells[1].MergeRight = titles.Length - 2;
                cell(row, 1, "Sem publicação neste mês", false);
                continue;
            }
            var col = 1;
            cell(row, col++, maturity ? ReportChart.Format(p.MaturityCurrent, 1) : ReportChart.Format(p.Score, 0), false);
            if (maturity) cell(row, col++, ReportChart.Format(p.MaturityTarget, 1), false);
            cell(row, col++, $"{p.Coverage.ToString("0.#", Pt)}% · {p.EvaluatedItems} de {p.EligibleItems}" + (maturity ? $" (aplicáveis {p.ApplicableItems})" : " aplicáveis"), false);
            cell(row, col++, maturity
                ? (p.CycleName ?? "—") + (p.PeriodStart is { } ps && p.PeriodEnd is { } pe ? $" ({ps:dd/MM/yyyy}–{pe:dd/MM/yyyy})" : "")
                : (p.DataRecency is { } d ? d.ToUniversalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "—")
                  + (p.Composition is { Count: > 0 } comp ? " · " + string.Join(", ", comp) : ""), false);
            cell(row, col++, p.IsThisPublication ? "esta publicação" : p.CapturedAt.ToUniversalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                + (p.PublishedInMonth > 1 ? $" ({p.PublishedInMonth} no mês; vale a última)" : ""), false);
            var notes = new List<string>();
            if (p.Delta is { } dv) notes.Add((dv > 0 ? "+" : "") + dv.ToString(maturity ? "0.0#" : "0.#", Pt) + " desde " + FrozenPostureHistoryBuilder.MonthLabel(p.DeltaFrom!.Value));
            if (p.BreakReasons.Count > 0) notes.Add("Recomeça: " + string.Join(", ", p.BreakReasons.Select(ReportChartBuilder.BreakLabel)));
            notes.AddRange(p.Notes ?? Array.Empty<string>());
            cell(row, col, notes.Count == 0 ? "—" : string.Join(" ", notes), false);
        }
    }
}
