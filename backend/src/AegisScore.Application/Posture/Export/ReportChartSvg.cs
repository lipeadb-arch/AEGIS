using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Gráficos dos relatórios HTML em SVG desenhado NO SERVIDOR a partir de <see cref="ReportChart"/>:
/// o arquivo continua autocontido, abre por <c>file://</c> e lê-se sem JavaScript. Nada de atributo <c>style</c> (a CSP do
/// relatório não o admite): cores e espessuras vêm de classes do CSS fixo, cujo hash vai na CSP. Cada gráfico tem título,
/// descrição, legenda em texto, base declarada, valores escritos no desenho e uma tabela com os mesmos valores — cor nunca é o
/// único portador da informação, e os links do detalhe funcionam pelo teclado na tabela.
/// </summary>
public static class ReportChartSvg
{
    private static readonly HtmlEncoder Enc = HtmlEncoder.Create(UnicodeRanges.All);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double W = 440;

    /// <summary>Grade de gráficos (figuras) — usada pelos dois relatórios.</summary>
    public static string Grid(IEnumerable<ReportChart> charts, Func<string, string?> linkAttrs)
    {
        var sb = new StringBuilder("<div class=\"charts\">");
        foreach (var c in charts) sb.Append(Figure(c, linkAttrs));
        return sb.Append("</div>").ToString();
    }

    /// <summary>
    /// Uma figura completa. <paramref name="linkAttrs"/> converte o destino do detalhe em atributos de âncora
    /// (ex.: <c>href="#fn-GV"</c>, ou <c>href="#controles" data-filter="…"</c> no KNIGHT); nulo = sem link.
    /// </summary>
    public static string Figure(ReportChart c, Func<string, string?> linkAttrs)
    {
        var id = "ch-" + c.Id;
        var sb = new StringBuilder();
        sb.Append("<figure class=\"chart chart-").Append(c.Kind.ToString().ToLowerInvariant()).Append("\" id=\"").Append(id).Append("\">")
          .Append("<figcaption><span class=\"ch-t\" id=\"").Append(id).Append("-t\">").Append(E(c.Title)).Append("</span>")
          .Append("<span class=\"ch-d\" id=\"").Append(id).Append("-d\">").Append(E(c.Description)).Append("</span></figcaption>");
        if (c.EmptyMessage is { } empty)
        {
            sb.Append("<p class=\"ch-empty\">").Append(E(empty)).Append("</p>");
            if (c.Basis is { } b0) sb.Append("<p class=\"ch-basis\">").Append(E(b0)).Append("</p>");
            Notes(sb, c);
            return sb.Append("</figure>").ToString();
        }

        sb.Append(c.Kind switch
        {
            ReportChartKind.Columns => Columns(c, id, linkAttrs),
            ReportChartKind.HorizontalBars => Bars(c, id, linkAttrs, stacked: false),
            ReportChartKind.StackedBars => Bars(c, id, linkAttrs, stacked: true),
            ReportChartKind.Donut => Donut(c, id, linkAttrs),
            ReportChartKind.Line => Line(c, id),
            ReportChartKind.Radar => Radar(c, id),
            _ => "",
        });
        Legend(sb, c);
        if (c.Basis is { } basis) sb.Append("<p class=\"ch-basis\">").Append(E(basis)).Append("</p>");
        Notes(sb, c);
        Table(sb, c, linkAttrs);
        return sb.Append("</figure>").ToString();
    }

    // ---- Tipos -------------------------------------------------------------------------------------------------------

    private static string Columns(ReportChart c, string id, Func<string, string?> link)
    {
        const double h = 230, x0 = 34, y0 = 18, y1 = h - 42;
        var x1 = W - 6;
        var sb = Open(c, id, h);
        Grid(sb, c, x0, x1, y0, y1);
        var n = c.Categories.Count;
        var group = (x1 - x0) / Math.Max(1, n);
        var k = c.Series.Count;
        var bw = Math.Min(34, group * 0.72 / Math.Max(1, k));
        for (var i = 0; i < n; i++)
        {
            var gx = x0 + group * i + (group - bw * k) / 2;
            var open = Anchor(c, i, link);
            sb.Append(open);
            for (var j = 0; j < k; j++)
            {
                var s = c.Series[j];
                var v = s.Values[i];
                var x = gx + bw * j;
                var tone = k == 1 && c.CategoryTones is { } tones ? tones[i] : s.Tone;
                if (v is { } val)
                {
                    var y = Y(val, c, y0, y1);
                    sb.Append("<rect class=\"f-").Append(tone).Append("\" x=\"").Append(N(x)).Append("\" y=\"").Append(N(y))
                      .Append("\" width=\"").Append(N(bw - 2)).Append("\" height=\"").Append(N(Math.Max(0.5, y1 - y))).Append("\"><title>")
                      .Append(E($"{c.Categories[i]} · {s.Label}: {ReportChart.Format(val, c.Decimals)}")).Append("</title></rect>");
                    sb.Append(Text(x + (bw - 2) / 2, y - 4, ReportChart.Format(val, c.Decimals), "ch-val", "middle"));
                }
                else
                {
                    sb.Append(Text(x + (bw - 2) / 2, y1 - 4, "—", "ch-sm", "middle"));
                }
            }
            if (open.Length > 0) sb.Append("</a>");
            sb.Append(Text(x0 + group * i + group / 2, y1 + 16, Clip(c.Categories[i], 14), "ch-txt", "middle"));
        }
        if (c.Series.Any(s => s.Values.Any(v => v is null)))
            sb.Append(Text(x1, h - 6, "— = " + c.EmptyLabel, "ch-sm", "end"));
        return sb.Append("</svg>").ToString();
    }

    private static string Bars(ReportChart c, string id, Func<string, string?> link, bool stacked)
    {
        const double row = 36, top = 6, x0 = 2, barH = 14;
        var x1 = W - 44;
        var n = c.Categories.Count;
        var h = top + row * n + 6;
        var sb = Open(c, id, h);
        var max = c.Max <= 0 ? 1 : c.Max;
        for (var i = 0; i < n; i++)
        {
            var y = top + row * i;
            var open = Anchor(c, i, link);
            sb.Append(open);
            sb.Append(Text(x0, y + 11, Clip(c.Categories[i], 56), "ch-txt", "start"));
            sb.Append("<rect class=\"ch-track\" x=\"").Append(N(x0)).Append("\" y=\"").Append(N(y + 16)).Append("\" width=\"")
              .Append(N(x1 - x0)).Append("\" height=\"").Append(N(barH)).Append("\"/>");
            var x = x0;
            double total = 0;
            foreach (var s in c.Series)
            {
                if (s.Values[i] is not { } v || v <= 0) continue;
                total += v;
                var w = (x1 - x0) * v / max;
                var tone = !stacked && c.Series.Count == 1 && c.CategoryTones is { } tones ? tones[i] : s.Tone;
                sb.Append("<rect class=\"f-").Append(tone).Append("\" x=\"").Append(N(x)).Append("\" y=\"").Append(N(y + 16))
                  .Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(N(barH)).Append("\"><title>")
                  .Append(E($"{c.Categories[i]} · {s.Label}: {ReportChart.Format(v, c.Decimals)}")).Append("</title></rect>");
                if (stacked && w >= 18)
                    sb.Append(Text(x + w / 2, y + 27, ReportChart.Format(v, c.Decimals), "ch-in " + "in-" + s.Tone, "middle"));
                if (stacked) x += w;
            }
            var label = stacked ? ReportChart.Format(total, 0) : ReportChart.Format(c.Series[0].Values[i], c.Decimals);
            var endX = stacked ? x0 + (x1 - x0) * total / max : x0 + (x1 - x0) * (c.Series[0].Values[i] ?? 0) / max;
            sb.Append(Text(Math.Min(endX + 5, x1 + 4), y + 27, label, "ch-val", "start"));
            if (open.Length > 0) sb.Append("</a>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static string Donut(ReportChart c, string id, Func<string, string?> link)
    {
        const double h = 196, cx = 96, cy = 98, r = 76, inner = 48;
        var values = c.Series[0].Values;
        var total = values.Sum(v => v ?? 0);
        var sb = Open(c, id, h);
        var angle = -90.0;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v || v <= 0 || total <= 0) continue;
            var sweep = 360.0 * v / total;
            var tone = c.CategoryTones?[i] ?? c.Series[0].Tone;
            var open = Anchor(c, i, link);
            sb.Append(open).Append("<path class=\"f-").Append(tone).Append("\" d=\"").Append(Arc(cx, cy, r, inner, angle, sweep))
              .Append("\"><title>").Append(E($"{c.Categories[i]}: {ReportChart.Format(v, 0)} de {ReportChart.Format(total, 0)} ({Pct(v, total)})")).Append("</title></path>");
            if (open.Length > 0) sb.Append("</a>");
            angle += sweep;
        }
        sb.Append(Text(cx, cy + 2, ReportChart.Format(total, 0), "ch-big", "middle"))
          .Append(Text(cx, cy + 18, c.Unit, "ch-sm", "middle"));
        // Rótulos à direita: contagem e percentual de cada parcela, sobre o total declarado.
        var ly = 22.0;
        for (var i = 0; i < c.Categories.Count; i++)
        {
            var v = values[i] ?? 0;
            var tone = c.CategoryTones?[i] ?? c.Series[0].Tone;
            sb.Append("<rect class=\"f-").Append(tone).Append("\" x=\"192\" y=\"").Append(N(ly - 9)).Append("\" width=\"10\" height=\"10\" rx=\"2\"/>")
              .Append(Text(208, ly, c.Categories[i], "ch-txt", "start"))
              .Append(Text(W - 4, ly, $"{ReportChart.Format(v, 0)} · {Pct(v, total)}", "ch-val", "end"));
            ly += 26;
        }
        return sb.Append("</svg>").ToString();
    }

    private static string Line(ReportChart c, string id)
    {
        const double h = 238, x0 = 34, y0 = 18, y1 = h - 38;
        var x1 = W - 10;
        var sb = Open(c, id, h);
        Grid(sb, c, x0, x1, y0, y1);
        var n = c.Categories.Count;
        double X(int i) => n <= 1 ? (x0 + x1) / 2 : x0 + 8 + (x1 - x0 - 16) * i / (n - 1);
        foreach (var s in c.Series)
        {
            for (var i = 1; i < n; i++)
            {
                if (s.Connect is null || !s.Connect[i] || s.Values[i] is not { } b || s.Values[i - 1] is not { } a) continue;
                sb.Append("<line class=\"l-").Append(s.Tone).Append(s.Dashed ? " dash" : "").Append("\" x1=\"").Append(N(X(i - 1))).Append("\" y1=\"")
                  .Append(N(Y(a, c, y0, y1))).Append("\" x2=\"").Append(N(X(i))).Append("\" y2=\"").Append(N(Y(b, c, y0, y1))).Append("\"/>");
            }
        }
        var first = true;
        foreach (var s in c.Series)
        {
            for (var i = 0; i < n; i++)
            {
                if (s.Values[i] is not { } v) continue;
                var y = Y(v, c, y0, y1);
                sb.Append("<circle class=\"f-").Append(s.Tone).Append("\" cx=\"").Append(N(X(i))).Append("\" cy=\"").Append(N(y)).Append("\" r=\"")
                  .Append(s.Dashed ? "3" : "4").Append("\"><title>").Append(E($"{c.Categories[i]} · {s.Label}: {ReportChart.Format(v, c.Decimals)}")).Append("</title></circle>");
                // Valor escrito só na série principal (a do alvo fica na tabela e na legenda) para não sobrepor rótulos.
                if (first) sb.Append(Text(X(i), y - 8, ReportChart.Format(v, c.Decimals), "ch-val", "middle"));
            }
            first = false;
        }
        // Em tela estreita (CSS), os meses alternados somem para os rótulos não se sobreporem; a tabela traz todos.
        for (var i = 0; i < n; i++)
            sb.Append(Text(X(i), y1 + 16, c.Categories[i], n > 6 && i % 2 == 1 ? "ch-sm ch-alt" : "ch-sm", "middle"));
        return sb.Append("</svg>").ToString();
    }

    private static string Radar(ReportChart c, string id)
    {
        const double h = 300, cy = 152, R = 104;
        var cx = W / 2;
        var n = c.Categories.Count;
        var sb = Open(c, id, h);
        (double X, double Y) P(int i, double v) =>
            (cx + R * (v / c.Max) * Math.Cos((-90 + 360.0 * i / n) * Math.PI / 180), cy + R * (v / c.Max) * Math.Sin((-90 + 360.0 * i / n) * Math.PI / 180));
        for (var ring = 1; ring <= (int)c.Max; ring++)
        {
            sb.Append("<polygon class=\"ch-ring\" points=\"").Append(string.Join(" ", Enumerable.Range(0, n).Select(i => { var p = P(i, ring); return N(p.X) + "," + N(p.Y); }))).Append("\"/>");
            var lp = P(0, ring);
            sb.Append(Text(lp.X + 4, lp.Y + 3, ring.ToString(Inv), "ch-sm", "start"));
        }
        for (var i = 0; i < n; i++)
        {
            var end = P(i, c.Max);
            sb.Append("<line class=\"ch-grid\" x1=\"").Append(N(cx)).Append("\" y1=\"").Append(N(cy)).Append("\" x2=\"").Append(N(end.X)).Append("\" y2=\"").Append(N(end.Y)).Append("\"/>");
            var lab = P(i, c.Max * 1.17);
            var anchor = Math.Abs(lab.X - cx) < 8 ? "middle" : lab.X > cx ? "start" : "end";
            sb.Append(Text(lab.X, lab.Y + 4, c.Categories[i], "ch-txt", anchor));
        }
        foreach (var s in c.Series)
        {
            var full = s.Values.All(v => v is not null);
            if (full)
                sb.Append("<polygon class=\"r-").Append(s.Tone).Append(s.Dashed ? " dash" : "").Append("\" points=\"")
                  .Append(string.Join(" ", s.Values.Select((v, i) => { var p = P(i, v!.Value); return N(p.X) + "," + N(p.Y); }))).Append("\"/>");
            else
                // Perfil incompleto: só os lados entre funções vizinhas COM valor — nunca fechado nem levado a zero.
                for (var i = 0; i < n; i++)
                {
                    var j = (i + 1) % n;
                    if (s.Values[i] is not { } a || s.Values[j] is not { } b) continue;
                    var pa = P(i, a); var pb = P(j, b);
                    sb.Append("<line class=\"l-").Append(s.Tone).Append(s.Dashed ? " dash" : "").Append("\" x1=\"").Append(N(pa.X)).Append("\" y1=\"").Append(N(pa.Y))
                      .Append("\" x2=\"").Append(N(pb.X)).Append("\" y2=\"").Append(N(pb.Y)).Append("\"/>");
                }
            for (var i = 0; i < n; i++)
            {
                if (s.Values[i] is not { } v) continue;
                var p = P(i, v);
                sb.Append("<circle class=\"f-").Append(s.Tone).Append("\" cx=\"").Append(N(p.X)).Append("\" cy=\"").Append(N(p.Y)).Append("\" r=\"3.5\"><title>")
                  .Append(E($"{c.Categories[i]} · {s.Label}: {ReportChart.Format(v, c.Decimals)}")).Append("</title></circle>");
            }
        }
        return sb.Append("</svg>").ToString();
    }

    // ---- Partes comuns ----------------------------------------------------------------------------------------------

    private static StringBuilder Open(ReportChart c, string id, double h)
    {
        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 ").Append(N(W)).Append(' ').Append(N(h)).Append("\" role=\"img\" aria-labelledby=\"")
          .Append(id).Append("-t ").Append(id).Append("-s\" focusable=\"false\"><desc id=\"").Append(id).Append("-s\">")
          .Append(E(Summary(c))).Append("</desc>");
        return sb;
    }

    /// <summary>Alternativa textual: os valores de cada categoria e série, na mesma ordem do desenho.</summary>
    public static string Summary(ReportChart c)
    {
        if (c.Kind == ReportChartKind.Donut)
        {
            var total = c.Series[0].Values.Sum(v => v ?? 0);
            return $"Total {ReportChart.Format(total, 0)} {c.Unit}: " + string.Join("; ",
                c.Categories.Select((cat, i) => $"{cat} {ReportChart.Format(c.Series[0].Values[i] ?? 0, 0)} ({Pct(c.Series[0].Values[i] ?? 0, total)})")) + ".";
        }
        return string.Join("; ", c.Categories.Select((cat, i) =>
            cat + ": " + string.Join(", ", c.Series.Select(s => (c.Series.Count > 1 ? s.Label + " " : "") +
                (s.Values[i] is { } v ? ReportChart.Format(v, c.Decimals) : c.EmptyLabel))))) + $". Escala: {c.Unit}.";
    }

    private static void Grid(StringBuilder sb, ReportChart c, double x0, double x1, double y0, double y1)
    {
        foreach (var t in Ticks(c))
        {
            var y = Y(t, c, y0, y1);
            sb.Append("<line class=\"ch-grid\" x1=\"").Append(N(x0)).Append("\" y1=\"").Append(N(y)).Append("\" x2=\"").Append(N(x1)).Append("\" y2=\"").Append(N(y)).Append("\"/>")
              .Append(Text(x0 - 5, y + 3.5, ReportChart.Format(t, t % 1 == 0 ? 0 : 1), "ch-sm", "end"));
        }
    }

    /// <summary>Marcas do eixo: inteiros na escala 1–5; cinco divisões nas contagens e na nota 0–100.</summary>
    public static IReadOnlyList<double> Ticks(ReportChart c)
    {
        if (c.Max - c.Min <= 5 && c.Max % 1 == 0) return Enumerable.Range((int)c.Min, (int)(c.Max - c.Min) + 1).Select(x => (double)x).ToList();
        var step = (c.Max - c.Min) / 4;
        return Enumerable.Range(0, 5).Select(i => Math.Round(c.Min + step * i, 1)).ToList();
    }

    private static double Y(double v, ReportChart c, double y0, double y1) =>
        y1 - (Math.Clamp(v, c.Min, c.Max) - c.Min) / Math.Max(1e-9, c.Max - c.Min) * (y1 - y0);

    private static string Anchor(ReportChart c, int i, Func<string, string?> link) =>
        c.CategoryLinks is { } links && i < links.Count && links[i] is { } target && link(target) is { } attrs
            ? "<a " + attrs + " aria-label=\"" + E(c.Categories[i] + ": abrir o detalhe") + "\">"
            : "";

    private static void Legend(StringBuilder sb, ReportChart c)
    {
        if (c.Kind == ReportChartKind.Donut) return;   // a rosca escreve os rótulos ao lado
        if (c.Series.Count == 1 && c.CategoryTones is not null) return;   // categorias já nomeadas no eixo
        sb.Append("<ul class=\"ch-legend\">");
        foreach (var s in c.Series)
            sb.Append("<li><i class=\"sw t-").Append(s.Tone).Append(s.Dashed ? " dash" : "").Append("\"></i>").Append(E(s.Label)).Append(s.Dashed ? " (tracejado)" : "").Append("</li>");
        sb.Append("</ul>");
    }

    private static void Notes(StringBuilder sb, ReportChart c)
    {
        if (c.Notes is not { Count: > 0 } notes) return;
        sb.Append("<ul class=\"ch-notes\">");
        foreach (var n in notes) sb.Append("<li>").Append(E(n)).Append("</li>");
        sb.Append("</ul>");
    }

    private static void Table(StringBuilder sb, ReportChart c, Func<string, string?> link)
    {
        var withTotal = c.Kind == ReportChartKind.StackedBars;
        var donut = c.Kind == ReportChartKind.Donut;
        sb.Append("<details class=\"ch-data\"><summary>Valores do gráfico (").Append(E(c.Unit)).Append(")</summary><div class=\"tw\"><table><thead><tr><th scope=\"col\">")
          .Append(c.Kind == ReportChartKind.Line ? "Mês" : "Categoria").Append("</th>");
        foreach (var s in c.Series) sb.Append("<th scope=\"col\">").Append(E(s.Label)).Append("</th>");
        if (withTotal) sb.Append("<th scope=\"col\">Total</th>");
        if (donut) sb.Append("<th scope=\"col\">Percentual</th>");
        sb.Append("</tr></thead><tbody>");
        var donutTotal = donut ? c.Series[0].Values.Sum(v => v ?? 0) : 0;
        for (var i = 0; i < c.Categories.Count; i++)
        {
            sb.Append("<tr><th scope=\"row\">");
            if (c.CategoryLinks is { } links && i < links.Count && links[i] is { } target && link(target) is { } attrs)
                sb.Append("<a ").Append(attrs).Append('>').Append(E(c.Categories[i])).Append("</a>");
            else sb.Append(E(c.Categories[i]));
            sb.Append("</th>");
            foreach (var s in c.Series)
                sb.Append("<td>").Append(s.Values[i] is { } v ? ReportChart.Format(v, c.Decimals) : E(c.EmptyLabel)).Append("</td>");
            if (withTotal) sb.Append("<td>").Append(ReportChart.Format(c.CategoryTotal(i), 0)).Append("</td>");
            if (donut) sb.Append("<td>").Append(Pct(c.Series[0].Values[i] ?? 0, donutTotal)).Append("</td>");
            sb.Append("</tr>");
        }
        if (donut) sb.Append("<tr><th scope=\"row\">Total (base)</th><td>").Append(ReportChart.Format(donutTotal, 0)).Append("</td><td>100%</td></tr>");
        sb.Append("</tbody></table></div></details>");
    }

    private static string Arc(double cx, double cy, double r, double inner, double start, double sweep)
    {
        if (sweep >= 359.999)
            // Círculo cheio: dois semicírculos (um arco de 360° não se desenha).
            return Arc(cx, cy, r, inner, start, 180) + " " + Arc(cx, cy, r, inner, start + 180, 180);
        double Rad(double a) => a * Math.PI / 180;
        var end = start + sweep;
        var large = sweep > 180 ? 1 : 0;
        (double, double) Pt(double rr, double a) => (cx + rr * Math.Cos(Rad(a)), cy + rr * Math.Sin(Rad(a)));
        var (ax, ay) = Pt(r, start);
        var (bx, by) = Pt(r, end);
        var (cx2, cy2) = Pt(inner, end);
        var (dx, dy) = Pt(inner, start);
        return $"M{N(ax)} {N(ay)}A{N(r)} {N(r)} 0 {large} 1 {N(bx)} {N(by)}L{N(cx2)} {N(cy2)}A{N(inner)} {N(inner)} 0 {large} 0 {N(dx)} {N(dy)}Z";
    }

    private static string Text(double x, double y, string text, string cls, string anchor) =>
        "<text class=\"" + cls + "\" x=\"" + N(x) + "\" y=\"" + N(y) + "\" text-anchor=\"" + anchor + "\">" + E(text) + "</text>";

    public static string Pct(double v, double total) =>
        total <= 0 ? "—" : (100 * v / total).ToString("0.#", ReportChart.Pt) + "%";

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private static string N(double v) => Math.Round(v, 1).ToString("0.#", Inv);

    private static string E(string? s) => Enc.Encode(s ?? "");

    /// <summary>
    /// Estilo dos gráficos (fixo, sem dado). Usa as variáveis de cor de cada relatório quando existem (o KNIGHT tem modo
    /// escuro) e cai em tons fixos legíveis no fundo claro. Acrescentado ao CSS do relatório SÓ nas fotografias com painel
    /// visual — as anteriores mantêm o CSS (e o hash da CSP) de antes.
    /// </summary>
    public const string Css = """
.charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,360px),1fr));gap:12px;margin:12px 0}
.chart{background:var(--panel,var(--card,#fff));border:1px solid var(--line,#d6dce8);border-radius:10px;padding:12px;margin:0;min-width:0}
.chart figcaption .ch-t{display:block;font-weight:700;font-size:14px}.chart figcaption .ch-d{display:block;font-size:12px;color:var(--muted,#5f6a80);margin-top:2px}
.chart svg{display:block;width:100%;height:auto;max-width:560px;margin:8px auto 0;overflow:visible}
.chart svg text{font-family:inherit}.ch-txt{fill:var(--ink,#18202e);font-size:11.5px}.ch-sm{fill:var(--muted,#5f6a80);font-size:10px}
.ch-val{fill:var(--ink,#18202e);font-size:10.5px;font-weight:700}.ch-big{fill:var(--ink,#18202e);font-size:22px;font-weight:700}.ch-in{font-size:10px;font-weight:700;fill:#fff}
.in-na,.in-ne,.in-cur-light,.in-warn{fill:#1b2333}
.ch-grid{stroke:var(--line,#d6dce8);stroke-width:1}.ch-ring{fill:none;stroke:var(--line,#d6dce8);stroke-width:1}.ch-track{fill:var(--ne-bg,#eef1f6)}
.f-acc{fill:var(--accent,var(--acc,#0a6e94))}.f-ok{fill:var(--ok,#1b7f3b)}.f-bad{fill:var(--fail,var(--bad,#b3261e))}.f-warn{fill:var(--warn,#a86400)}
.f-ne{fill:var(--ne,#7b8699)}.f-err{fill:var(--err,#6b3fa0)}.f-na{fill:#b7c0cf}.f-cur{fill:var(--cur,#0b5e9b)}.f-tgt{fill:#d97706}.f-cur-light{fill:#7fb2e5}
.f-sev-Critical{fill:var(--crit,#8e1b12)}.f-sev-High{fill:var(--high,#c2410c)}.f-sev-Medium{fill:var(--med,#a16207)}.f-sev-Low{fill:var(--low,#1d5fb8)}.f-sev-Informational{fill:var(--info,#56627a)}
.l-acc,.l-cur,.l-tgt{fill:none;stroke-width:2.5;stroke-linecap:round}.l-acc{stroke:var(--accent,var(--acc,#0a6e94))}.l-cur{stroke:var(--cur,#0b5e9b)}.l-tgt{stroke:#d97706}
.r-cur{fill:rgba(11,94,155,.16);stroke:var(--cur,#0b5e9b);stroke-width:2}.r-tgt{fill:none;stroke:#d97706;stroke-width:2}
.dash{stroke-dasharray:6 4}
.ch-legend{list-style:none;display:flex;flex-wrap:wrap;gap:4px 14px;padding:0;margin:8px 0 0;font-size:12px}
.sw{display:inline-block;width:11px;height:11px;border-radius:2px;margin-right:6px;vertical-align:-1px}
.sw.t-acc{background:var(--accent,var(--acc,#0a6e94))}.sw.t-ok{background:var(--ok,#1b7f3b)}.sw.t-bad{background:var(--fail,var(--bad,#b3261e))}.sw.t-warn{background:var(--warn,#a86400)}
.sw.t-ne{background:var(--ne,#7b8699)}.sw.t-err{background:var(--err,#6b3fa0)}.sw.t-na{background:#b7c0cf}.sw.t-cur{background:var(--cur,#0b5e9b)}.sw.t-tgt{background:#d97706}.sw.t-cur-light{background:#7fb2e5}
.sw.dash{background:none;border-top:3px dashed #d97706;height:0;width:16px;border-radius:0;vertical-align:3px}
.ch-basis{font-size:12px;color:var(--muted,#5f6a80);margin:6px 0 0}.ch-notes{font-size:12px;margin:6px 0 0;padding-left:18px}
.ch-empty{font-style:italic;color:var(--muted,#5f6a80);margin:10px 0}
.ch-data summary{cursor:pointer;font-size:12px;margin-top:8px}.ch-data table{min-width:0}
.chart a:focus-visible{outline:3px solid #f0a500;outline-offset:1px}
.hist-t th{white-space:nowrap}.hist-t td{min-width:90px}.hist-t td:last-child{min-width:220px}.chart-line svg{max-width:680px}.muted{color:var(--muted,#5f6a80)}.vis-h{margin:18px 0 0}
@media (max-width:560px){.ch-txt{font-size:13.5px}.ch-sm{font-size:12.5px}.ch-val{font-size:13px}.ch-in{font-size:12px}.ch-alt{display:none}}
@media (prefers-color-scheme:dark){.f-na{fill:#4a5468}.sw.t-na{background:#4a5468}.in-na{fill:#fff}}
@media print{.charts{grid-template-columns:1fr 1fr}.chart{break-inside:avoid}.ch-data{display:none}}
""";
}
