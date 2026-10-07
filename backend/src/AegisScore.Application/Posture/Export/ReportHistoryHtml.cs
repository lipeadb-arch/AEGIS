using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Seção "Evolução mensal" dos relatórios HTML, só a partir do histórico CONGELADO: identidade da
/// série (instrumento, fonte ou composição, avaliação e escopo, período, fórmula/metodologia, catálogo e base da cobertura), o
/// gráfico de linha e a tabela mês a mês com publicação, período avaliado, base, variação (só entre pontos comparáveis) e notas.
/// </summary>
public static class ReportHistoryHtml
{
    private static readonly HtmlEncoder Enc = HtmlEncoder.Create(UnicodeRanges.All);
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static void Append(StringBuilder sb, FrozenPostureHistory h, ReportChart chart)
    {
        var maturity = h.Series.Type == "NistMaturity";
        sb.Append("<h3 id=\"evolucao\">Evolução mensal (").Append(E(FrozenPostureHistoryBuilder.MonthLabel(h.From))).Append(" a ")
          .Append(E(FrozenPostureHistoryBuilder.MonthLabel(h.Until))).Append(")</h3>")
          .Append("<div class=\"hist-id\"><dl class=\"kv\">")
          .Append(Dt("Série", h.Series.Label))
          .Append(Dt("Instrumento e escala", h.Series.Instrument))
          .Append(h.Series.Composition is { Count: > 0 } comp ? Dt("Composição de fontes", string.Join(", ", comp)) : "")
          .Append(Dt(maturity ? "Metodologia · catálogo" : "Fórmula · catálogo", $"{h.Series.FormulaVersion} · {h.Series.CatalogVersion}"))
          .Append(Dt("Base da cobertura", h.Series.CoverageBasis))
          .Append(Dt("Critério mensal", h.Criterion))
          .Append(Dt("Esta publicação", h.IncludesThisPublication
              ? $"é o ponto de {FrozenPostureHistoryBuilder.MonthLabel(h.PublicationMonth)}"
              : $"publicada em {FrozenPostureHistoryBuilder.MonthLabel(h.PublicationMonth)}, fora do período escolhido"))
          .Append(Dt("Histórico congelado", "na publicação; reexportar não consulta publicações posteriores"))
          .Append("</dl></div>");
        sb.Append(ReportChartSvg.Figure(chart, _ => null));

        sb.Append("<div class=\"tw\"><table class=\"hist-t\"><caption>Mês a mês (meses sem publicação ficam sem resultado)</caption><thead><tr>")
          .Append("<th scope=\"col\">Mês</th><th scope=\"col\">").Append(maturity ? "Atual" : "Nota").Append("</th>")
          .Append(maturity ? "<th scope=\"col\">Alvo</th>" : "")
          .Append("<th scope=\"col\">Cobertura e base</th><th scope=\"col\">")
          .Append(maturity ? "Rodada e período avaliado" : "Coleta").Append("</th><th scope=\"col\">Publicação</th>")
          .Append("<th scope=\"col\">Variação</th><th scope=\"col\">Observações</th></tr></thead><tbody>");
        var byMonth = h.Points.ToDictionary(p => p.Month);
        foreach (var m in h.Months)
        {
            sb.Append("<tr><th scope=\"row\">").Append(E(FrozenPostureHistoryBuilder.MonthLabel(m))).Append("</th>");
            if (!byMonth.TryGetValue(m, out var p))
            {
                sb.Append("<td colspan=\"").Append(maturity ? 7 : 6).Append("\" class=\"muted\">Sem publicação neste mês</td></tr>");
                continue;
            }
            sb.Append("<td>").Append(maturity ? Value(p.MaturityCurrent, 1, "sem nível") : Value(p.Score, 0, "sem nota")).Append("</td>");
            if (maturity) sb.Append("<td>").Append(Value(p.MaturityTarget, 1, "sem alvo")).Append("</td>");
            sb.Append("<td>").Append(E(p.Coverage.ToString("0.#", Pt))).Append("% · ").Append(p.EvaluatedItems).Append(" de ")
              .Append(p.EligibleItems).Append(maturity ? $" subcategorias (aplicáveis: {p.ApplicableItems})" : " controles aplicáveis").Append("</td>");
            sb.Append("<td>").Append(maturity
                ? E((p.CycleName ?? "—") + (p.PeriodStart is { } ps && p.PeriodEnd is { } pe ? $" ({ps:dd/MM/yyyy} a {pe:dd/MM/yyyy})" : ""))
                : E(p.DataRecency is { } dr ? Utc(dr) : "—"))
              .Append("</td>");
            sb.Append("<td>").Append(p.IsThisPublication ? "<strong>esta publicação</strong>" : E(Utc(p.CapturedAt)))
              .Append(p.PublishedInMonth > 1 ? $"<br><span class=\"muted\">{p.PublishedInMonth} publicações no mês; vale a última</span>" : "")
              .Append("</td>");
            sb.Append("<td>").Append(p.Delta is { } d
                ? E((d > 0 ? "+" : "") + d.ToString(maturity ? "0.0#" : "0.#", Pt) + " desde " + FrozenPostureHistoryBuilder.MonthLabel(p.DeltaFrom!.Value))
                : p.BreakReasons.Count > 0 ? "não comparável" : "—").Append("</td>");
            var notes = (p.Notes ?? Array.Empty<string>()).ToList();
            if (p.BreakReasons.Count > 0)
                notes.Insert(0, "Recomeça: " + string.Join(", ", p.BreakReasons.Select(ReportChartBuilder.BreakLabel)) + ".");
            sb.Append("<td>").Append(notes.Count == 0 ? "—" : string.Join("<br>", notes.Select(E))).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");
        if (h.RelatedSeries.Count > 0)
        {
            sb.Append("<ul class=\"ch-notes\">");
            foreach (var r in h.RelatedSeries) sb.Append("<li>").Append(E(r)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("<p class=\"muted\">A variação só é calculada entre pontos comparáveis da mesma série; ela não indica, sozinha, a causa (correção, mudança no ambiente ou de cobertura).</p>");
    }

    private static string Value(double? v, int decimals, string empty) =>
        v is { } x ? E(ReportChart.Format(x, decimals)) : "<span class=\"muted\">" + E(empty) + "</span>";

    private static string Dt(string k, string? v) => "<dt>" + E(k) + "</dt><dd>" + E(v ?? "—") + "</dd>";

    private static string Utc(DateTimeOffset v) =>
        v.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string E(string? s) => Enc.Encode(s ?? "");
}
