using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] Relatório HTML AUTOCONTIDO de uma fotografia KNIGHT — escritor PURO (sem EF, rede
/// ou relógio). Um único arquivo: dados como ilha JSON, estilo e script inline, abas, gráficos, filtros e
/// detalhes funcionando OFFLINE, sem sessão e sem CDN.
///
/// Segurança do conteúdo (a evidência é DADO, nunca marcação):
///   • os dados vão num <c>&lt;script type="application/json"&gt;</c> com o encoder JavaScript padrão, que escapa
///     <c>&lt; &gt; &amp; ' "</c> — texto de evidência não consegue fechar a tag nem injetar marcação;
///   • o script só escreve com textContent/createElement; links só para HTTPS validado no servidor;
///   • CSP restritiva por HASH (<c>default-src 'none'</c>): nada externo carrega e nenhum script não previsto roda;
///   • o cabeçalho estático (visível sem JavaScript) é codificado com o encoder HTML (acentos preservados).
/// </summary>
public static class PostureSnapshotHtmlWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
    };

    public static byte[] Write(PostureSnapshot snapshot, bool integrityVerified)
    {
        if (snapshot.Type != PostureSnapshotType.Knight)
            throw new PostureExportNotSupportedException("O relatório HTML está disponível para fotografias do AEGIS KNIGHT.");

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified);
        var data = JsonSerializer.Serialize(model, Json);
        // [AEGIS-ASSESSMENT-VISUALS-01] Painel visual e histórico SÓ nas fotografias que congelaram o histórico na publicação.
        var history = FrozenPostureHistoryBuilder.Deserialize(snapshot.HistoryJson);
        var charts = history is null ? null : ReportChartBuilder.ForKnight(model, history);

        // [AEGIS-KNIGHT-COVERAGE-01] Quebras de linha NORMALIZADAS antes do hash: o navegador converte CRLF em LF ao
        // ler o documento e calcula o hash da CSP sobre o texto já normalizado. Num checkout com CRLF (Windows), o
        // hash do texto bruto não conferia e a CSP bloqueava estilo e script — o relatório abria sem funcionar.
        var css = NormalizeNewlines(charts is null ? KnightReportHtmlAssets.Css : KnightReportHtmlAssets.Css + "\n" + ReportChartSvg.Css);
        var js = NormalizeNewlines(KnightReportHtmlAssets.Js);
        var csp = "default-src 'none'; img-src data:; style-src '" + Sha256(css) + "'; script-src '" + Sha256(js)
            + "'; base-uri 'none'; form-action 'none'";
        // frame-ancestors é ignorado em <meta> (só vale como cabeçalho HTTP); o download já sai com cabeçalhos próprios.

        var h = model.Header;
        // Texto de dado: & < > " ' escapados, acentos preservados. A CSP é constante do código (sem dado externo).
        string E(string? s) => Html.Encode(s ?? "");
        var title = "AEGIS KNIGHT · Relatório de postura" + (string.IsNullOrWhiteSpace(h.ClientName) ? "" : " · " + h.ClientName);

        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!doctype html>\n<html lang=\"pt-BR\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(csp).Append("\">\n")
          .Append("<meta name=\"referrer\" content=\"no-referrer\">\n")
          .Append("<meta name=\"generator\" content=\"AEGIS KNIGHT\">\n")
          .Append("<title>").Append(E(title)).Append("</title>\n")
          .Append("<style>").Append(css).Append("</style>\n</head>\n<body>\n")
          .Append("<header class=\"top\"><div class=\"wrap\">")
          .Append("<div class=\"brand\">AEGIS KNIGHT · Análise da postura de segurança multicloud</div>")
          .Append("<h1>Relatório de postura de segurança</h1>")
          .Append("<p class=\"sub\">").Append(E(string.IsNullOrWhiteSpace(h.ClientName) ? "Cliente não registrado na fotografia" : h.ClientName))
          .Append(" · ").Append(E(h.SourceLabel)).Append(h.IsDemo ? " · DEMONSTRAÇÃO (dados sintéticos)" : "").Append("</p>")
          .Append("<div class=\"meta\"><span>Publicado em ").Append(E(Utc(h.CapturedAt))).Append("</span>")
          .Append("<span>Coleta: ").Append(E(h.DataRecency is { } r ? Utc(r) : "—")).Append("</span>")
          .Append("<span>Fotografia ").Append(E(h.SnapshotId.ToString("D"))).Append("</span>")
          .Append("<span>Integridade: ").Append(h.IntegrityVerified ? "hash verificado" : "não verificada").Append("</span></div>")
          .Append("</div></header>\n<main class=\"wrap\" id=\"app\">\n")
          .Append(Summary(model))
          .Append(charts is null ? "" : Visuals(charts, history!))
          .Append("<noscript><div class=\"panel\"><p>O resumo executivo, os riscos prioritários e as limitações acima não dependem de JavaScript. ")
          .Append("A lista completa de controles com filtros, a metodologia e o glossário exigem JavaScript habilitado no navegador. Nenhum recurso externo é carregado.</p></div></noscript>\n")
          .Append("</main>\n<footer class=\"wrap\">Gerado pelo AEGIS a partir da fotografia imutável ")
          .Append(E(h.SnapshotId.ToString("D"))).Append(" (SHA-256 ").Append(E(h.ContentHash)).Append("). Resultados determinísticos; a narrativa consultiva não altera nota, resultado ou severidade.</footer>\n")
          .Append("<script type=\"application/json\" id=\"aegis-data\">").Append(data).Append("</script>\n")
          .Append("<script>").Append(js).Append("</script>\n</body>\n</html>\n");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Resumo executivo, riscos e ações prioritárias e limitações, desenhados no servidor a partir do
    /// MESMO modelo da ilha de dados: legíveis sem JavaScript (e impressos), e o script só os move para a primeira aba. Todo texto vindo da
    /// fotografia é codificado; nenhum atributo de estilo (a CSP só autoriza o estilo por hash).
    /// </summary>
    internal static string Summary(KnightReportModel m)
    {
        static string E(string? s) => Html.Encode(s ?? "");
        string Pct(double? v) => v is { } x ? E(x.ToString("0.#", ReportChart.Pt)) + "%" : "—";
        var k = m.Kpis;
        var applicable = k.Evaluated + k.NotEvaluated + k.Errors;
        var fraction = $"{k.Evaluated} de {applicable} {(applicable == 1 ? "controle aplicável avaliado" : "controles aplicáveis avaliados")}";
        var problem = m.Header.CollectionState is { } cs && cs != nameof(KnightSourceState.Completed);
        var warning = k.Score is null
            ? (applicable == 0 ? "Sem nota: nenhum controle aplicável nesta avaliação."
                : "Sem nota: nenhum controle pôde ser avaliado. A ausência de resultado não indica conformidade nem ausência de risco.")
            : k.Evaluated < applicable
                ? $"Avaliação parcial: a nota considera {k.Evaluated} de {applicable} controles aplicáveis. Os {applicable - k.Evaluated} restantes não foram avaliados e não contam como aprovados."
                : problem ? "Coleta com limitação: todos os controles aplicáveis foram avaliados, mas alguma leitura de fonte não foi concluída." : null;

        var sb = new StringBuilder("<section id=\"aegis-summary\" class=\"grp\" aria-labelledby=\"sum-h\">");
        sb.Append("<h2 id=\"sum-h\" class=\"grp-h\">Resumo executivo</h2>");
        if (m.Sources is { } inc)
        {
            sb.Append("<p class=\"incl\"><strong>").Append(E(inc.Text)).Append("</strong>");
            if (inc.WithoutEvaluated.Count > 0) sb.Append(" · sem controle avaliado: ").Append(E(string.Join(", ", inc.WithoutEvaluated)));
            sb.Append("</p>");
        }
        sb.Append("<div class=\"grid g4\">");
        Kpi(sb, "Nota KNIGHT", k.Score is { } sc ? E(Math.Round(sc).ToString(CultureInfo.InvariantCulture)) + "<small>/100</small>" : "—",
            k.Score is null ? "Sem nota: nenhum controle avaliado." : $"Sobre os {k.Evaluated} controles avaliados (cobertura {Pct(k.Coverage)}).",
            "Pondera a severidade e o resultado de cada controle avaliado (fórmula " + E(m.Header.FormulaVersion) + "). Controles não avaliados ficam fora da nota: nunca contam como zero nem como aprovados.", k.Score is null || warning is not null);
        Kpi(sb, "Cobertura da avaliação", Pct(k.Coverage), E(fraction),
            $"Controles avaliados ÷ controles aplicáveis ({k.NotApplicable} não aplicável(is) ficam fora da base). Mostra quanto foi possível verificar, não se o ambiente está conforme.", k.Evaluated < applicable);
        Kpi(sb, "Controles reprovados", k.Failed.ToString(CultureInfo.InvariantCulture),
            $"{k.Mitigated} mitigado(s) com atenção · {k.NotEvaluated + k.Errors} sem avaliação.",
            "Reprovado: a configuração exposta foi comprovada. Mitigado: exposição com controle compensatório comprovado. Não avaliado e erro na avaliação não são aprovação.", false);
        Kpi(sb, "Aprovação", Pct(k.ApprovalPercent),
            k.Evaluated == 0 ? "Sem controle avaliado: não há aprovação a calcular." : $"{k.Passed} aprovado(s) de {k.Evaluated} controles avaliados.",
            "Aprovados ÷ controles avaliados. Não é a nota: a nota também pesa a severidade de cada controle.", false);
        sb.Append("</div>");
        if (warning is not null) sb.Append("<p class=\"note\" role=\"note\">").Append(E(warning)).Append("</p>");
        if (m.Header.CollectionSummary is { } summary) sb.Append("<p class=\"sub\">").Append(E(summary)).Append("</p>");

        sb.Append("<h3>Limitações que afetam a conclusão</h3>");
        var groups = m.LimitationGroups ?? Array.Empty<ReportLimitationGroup>();
        if (groups.Count == 0)
            sb.Append("<p class=\"sub\">").Append(m.LegacyLimitations.Count > 0
                ? "Esta fotografia registra as limitações no formato anterior; elas estão na aba de detalhes técnicos."
                : "Nenhuma limitação de coleta registrada: todas as capacidades desta fonte foram lidas.").Append("</p>");
        else
        {
            sb.Append("<ul class=\"lims\">");
            foreach (var g in groups)
            {
                sb.Append("<li><span class=\"pill ").Append(g.AffectedControls.Count > 0 ? "s-Mitigated" : "s-NotEvaluated").Append("\">")
                  .Append(E(g.CauseLabel)).Append("</span>");
                if (g.KindLabel is not null) sb.Append(" <span class=\"muted\">").Append(E(g.KindLabel)).Append("</span>");
                if (g.Services.Count > 0) sb.Append(" <span class=\"muted\">· ").Append(E(string.Join(", ", g.Services))).Append("</span>");
                sb.Append("<div>").Append(E(g.Capabilities.Count == 1 ? g.Capabilities[0] : $"{g.Capabilities.Count} capacidades: " + string.Join(", ", g.Capabilities)))
                  .Append(" · ").Append(g.AffectedControls.Count > 0 ? $"{g.AffectedControls.Count} controle(s) sem avaliação por isso" : "nenhum controle ficou sem avaliação por isso")
                  .Append("</div><div class=\"sub\">").Append(E(g.Guidance)).Append("</div></li>");
            }
            sb.Append("</ul><p class=\"sub\">Cada capacidade, com o detalhe técnico e os controles prejudicados, está na aba de detalhes técnicos.</p>");
        }

        sb.Append("<h3>Interpretação consultiva</h3>");
        if (m.Advisory is { } a)
        {
            sb.Append("<p class=\"note info\">").Append(a.FromAi ? "Gerada por IA" : "Resumo determinístico do AEGIS")
              .Append(", congelado nesta fotografia. Não altera nota, resultado, severidade nem mapeamento.</p>")
              .Append("<p>").Append(E(a.ExecutiveSummary)).Append("</p>");
            List(sb, "Riscos destacados", a.PriorityRisks);
            List(sb, "Ações sugeridas", a.RecommendedActions);
            List(sb, "Lacunas de coleta", a.CollectionGaps);
        }
        else sb.Append("<p class=\"sub\">Nenhuma interpretação registrada nesta fotografia. Os resultados acima são determinísticos e não dependem dela.</p>");
        sb.Append("</section>\n");

        sb.Append("<section id=\"aegis-risks\" class=\"grp\" aria-labelledby=\"risk-h\"><h2 id=\"risk-h\" class=\"grp-h\">Riscos e ações prioritárias</h2>")
          .Append("<p class=\"sub\">Até cinco controles reprovados, por severidade e, depois, pela quantidade afetada. Risco, impacto e orientação são os registrados para cada controle nesta fotografia.</p>");
        if (m.Priorities.Count == 0)
            sb.Append("<p class=\"empty\">Nenhum controle reprovado nesta avaliação.")
              .Append(k.NotEvaluated + k.Errors > 0 ? $" {k.NotEvaluated + k.Errors} controle(s) não foram avaliados: isso não indica ausência de risco." : "").Append("</p>");
        else
        {
            sb.Append("<ol class=\"prio\">");
            foreach (var p in m.Priorities)
            {
                sb.Append("<li><div class=\"prio-hd\"><span class=\"pill v-").Append(E(p.Severity)).Append("\">").Append(E(p.SeverityLabel)).Append("</span> ")
                  .Append("<a href=\"#tab-controls\" data-open=\"").Append(E(p.ControlId)).Append("\"><strong>").Append(E(p.Title)).Append("</strong></a></div>")
                  .Append("<div class=\"meta\">").Append(E(string.Join(" · ", new[] { p.Service, p.ControlId, p.AffectedComposition }.Where(x => !string.IsNullOrWhiteSpace(x)))))
                  .Append("</div><dl class=\"pdl\">");
                if (p.Risk is not null) sb.Append("<dt>Risco</dt><dd>").Append(E(p.Risk)).Append("</dd>");
                if (p.Impact is not null) sb.Append("<dt>Impacto</dt><dd>").Append(E(p.Impact)).Append("</dd>");
                sb.Append("<dt>O que fazer</dt><dd>").Append(E(p.Guidance ?? "Orientação não registrada nesta fotografia; consulte o detalhe do controle.")).Append("</dd></dl></li>");
            }
            sb.Append("</ol>");
            if (k.Failed > m.Priorities.Count)
                sb.Append("<p class=\"sub\"><a href=\"#tab-controls\" data-filter=\"status=Exposed\">Ver os ").Append(k.Failed).Append(" controles reprovados</a></p>");
        }
        return sb.Append("</section>\n").ToString();

        static void Kpi(StringBuilder sb, string label, string valueHtml, string note, string help, bool partial) =>
            sb.Append("<div class=\"kpi").Append(partial ? " partial" : "").Append("\"><div class=\"l\">").Append(E(label)).Append("</div><div class=\"v\">").Append(valueHtml)
              .Append("</div><div class=\"n\">").Append(note).Append("</div><details><summary>Base de cálculo</summary><span>").Append(help).Append("</span></details></div>");

        static void List(StringBuilder sb, string title, IReadOnlyList<string> items)
        {
            if (items.Count == 0) return;
            sb.Append("<h4>").Append(E(title)).Append("</h4><ol>");
            foreach (var x in items) sb.Append("<li>").Append(E(x)).Append("</li>");
            sb.Append("</ol>");
        }
    }

    /// <summary>
    /// [AEGIS-ASSESSMENT-VISUALS-01] Painel visual desenhado no servidor (lê-se sem JavaScript). O script do relatório o move
    /// para a visão geral, logo após os indicadores, e transforma os links dos gráficos em filtros da aba de controles.
    /// </summary>
    private static string Visuals(ReportCharts charts, FrozenPostureHistory history)
    {
        var sb = new StringBuilder("<section id=\"aegis-visuals\" class=\"visuals\" aria-labelledby=\"vis-h\">");
        sb.Append("<h2 id=\"vis-h\" class=\"vis-h\">Painel da avaliação</h2>")
          .Append(ReportChartSvg.Grid(charts.Executive.Concat(charts.Detail), FilterLink));
        if (charts.History is { } line) ReportHistoryHtml.Append(sb, history, line);
        return sb.Append("</section>\n").ToString();
    }

    /// <summary>"filtro:status=Exposed&amp;domain=Identity" → âncora para a aba de controles com o filtro em data-filter.</summary>
    private static string? FilterLink(string target)
    {
        if (!target.StartsWith("filtro:", StringComparison.Ordinal)) return null;
        var pairs = target["filtro:".Length..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .Select(p => p[0] + "=" + Uri.EscapeDataString(p[1]));
        return "href=\"#tab-controls\" data-filter=\"" + Html.Encode(string.Join("&", pairs)) + "\"";
    }

    internal static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Sha256(string content) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Utc(DateTimeOffset v) =>
        v.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>Formato não disponível para o tipo de fotografia pedido (o controller responde 400).</summary>
public sealed class PostureExportNotSupportedException : Exception
{
    public PostureExportNotSupportedException(string message) : base(message) { }
}
