using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using AegisScore.Application.Nist;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] Relatório HTML AUTOCONTIDO da fotografia de maturidade NIST — escritor PURO (sem EF, rede ou
/// relógio), derivado só do relatório congelado. Renderizado no servidor: o conteúdo inteiro está no HTML (lê-se sem
/// JavaScript, imprime-se inteiro); o script só organiza em abas, filtra a visão técnica e abre o detalhe ao seguir um link.
///
/// Segurança: todo texto passa pelo encoder HTML; links só para http(s); CSP restritiva por HASH (<c>default-src 'none'</c>),
/// nada externo carrega e nenhum script não previsto roda. Funciona offline, aberto do disco.
/// </summary>
public static class NistReportHtmlWriter
{
    private static readonly HtmlEncoder Enc = HtmlEncoder.Create(UnicodeRanges.All);
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static byte[] Write(PostureSnapshot snapshot, NistMaturityReport r, bool integrityVerified)
    {
        // [AEGIS-ASSESSMENT-VISUALS-01] Painel visual e histórico SÓ quando a fotografia congelou o histórico na publicação. As
        // anteriores saem exatamente como antes (mesmos bytes, mesmo CSS e mesma CSP) — nunca enriquecidas depois.
        var history = FrozenPostureHistoryBuilder.Deserialize(snapshot.HistoryJson);
        var charts = history is null ? null : ReportChartBuilder.ForNist(r, history);
        var css = PostureSnapshotHtmlWriter.NormalizeNewlines(charts is null ? Css : Css + "\n" + ReportChartSvg.Css);
        var js = PostureSnapshotHtmlWriter.NormalizeNewlines(Js);
        var csp = "default-src 'none'; img-src data:; style-src '" + Sha256(css) + "'; script-src '" + Sha256(js)
            + "'; base-uri 'none'; form-action 'none'";

        var s = r.Summary;
        var sb = new StringBuilder(128 * 1024);
        var title = "AEGIS NIST · Maturidade · " + r.Assessment.Name + " · " + r.Cycle.Name;
        sb.Append("<!doctype html>\n<html lang=\"pt-BR\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(csp).Append("\">\n")
          .Append("<meta name=\"referrer\" content=\"no-referrer\">\n<meta name=\"generator\" content=\"AEGIS NIST\">\n")
          .Append("<title>").Append(E(title)).Append("</title>\n<style>").Append(css).Append("</style>\n</head>\n<body>\n");

        // ---- Cabeçalho ----
        sb.Append("<header class=\"top\"><div class=\"wrap\">")
          .Append("<p class=\"brand\">AEGIS NIST · Maturidade pelo NIST CSF 2.0 (metodologia autoral do AEGIS)</p>")
          .Append("<h1>Relatório de maturidade</h1>")
          .Append("<p class=\"sub\">").Append(E(r.Client.Name ?? "Cliente não registrado na fotografia")).Append(" · ")
          .Append(E(r.Assessment.Name)).Append("</p>")
          .Append("<dl class=\"meta\">")
          .Append(Dt("Escopo", r.Scope.Name))
          .Append(Dt("Rodada", $"{r.Cycle.Name} — {r.Cycle.PeriodKindLabel}, {D(r.Cycle.PeriodStart)} a {D(r.Cycle.PeriodEnd)}"))
          .Append(Dt("Publicado em", r.Publication is { } p ? Utc(p.PublishedAt) + (p.PublishedByName is null ? "" : " por " + p.PublishedByName) : "—"))
          .Append(Dt("Integridade", integrityVerified ? "hash verificado na exportação" : "não verificada"))
          .Append("</dl></div></header>\n");

        sb.Append("<nav class=\"tabs wrap\" aria-label=\"Seções do relatório\">")
          .Append("<a class=\"tab\" href=\"#executiva\" data-tab=\"executiva\">Visão executiva</a>")
          .Append("<a class=\"tab\" href=\"#tecnica\" data-tab=\"tecnica\">Visão técnica</a>")
          .Append("<a class=\"tab\" href=\"#metodologia\" data-tab=\"metodologia\">Metodologia e integridade</a></nav>\n");
        sb.Append("<main class=\"wrap\">\n");

        // =========================== Visão executiva ===========================
        sb.Append("<section id=\"executiva\" class=\"view\" aria-labelledby=\"h-exec\"><h2 id=\"h-exec\">Visão executiva</h2>");
        sb.Append("<div class=\"cards\">")
          .Append(Card("Atual (média)", NistReportCanonical.Level(s.Current), s.WithCurrent == 0 ? "sem avaliação confirmada" : $"sobre {s.WithCurrent} subcategoria(s)"))
          .Append(Card("Alvo (média)", NistReportCanonical.Level(s.Target), s.WithTarget == 0 ? "sem alvo confirmado" : $"sobre {s.WithTarget} subcategoria(s)"))
          .Append(Card("Lacuna média", Gap(s.Gap), s.WithGap == 0 ? "indeterminada" : $"sobre {s.WithGap} com atual e alvo"))
          .Append(Card("Cobertura", s.Coverage.ToString("0.#", Pt) + "%", $"{s.Evaluated} avaliadas + {s.NotApplicable} não aplicáveis de {s.Subcategories}"))
          .Append(Card("Achados abertos", s.FindingsOpen.ToString(Pt), $"{s.FindingsRiskAccepted} com risco aceito · {s.FindingsClosed} encerrados"))
          .Append(Card("Planos", s.PlansActive.ToString(Pt) + " ativos", $"{s.PlansCompleted} concluídos · {s.PlansOverdue} atrasados"))
          .Append("</div>");
        sb.Append("<p class=\"note\">").Append(E(NistMethodologyText.InstrumentsNote)).Append("</p>");

        if (charts is not null)
        {
            // [AEGIS-ASSESSMENT-VISUALS-01] Painel: atual × alvo, situação das subcategorias e achados — clique leva ao detalhe.
            sb.Append("<h3 id=\"painel\">Painel da avaliação</h3>").Append(ReportChartSvg.Grid(charts.Executive, Anchor));
        }
        else
        {
            // Atual × alvo por função (barras sobre a escala 1–5; ausência dita, nunca desenhada como zero).
            sb.Append("<h3>Atual × alvo nas seis funções</h3><div class=\"bars\" role=\"list\">");
            foreach (var f in r.Functions)
            {
                sb.Append("<a class=\"bar-row\" role=\"listitem\" href=\"#fn-").Append(E(f.Code)).Append("\">")
                  .Append("<span class=\"bar-label\"><b>").Append(E(f.Code)).Append("</b> ").Append(E(f.Name)).Append("</span>")
                  .Append("<span class=\"bar-track\" aria-hidden=\"true\">");
                // Largura por data-w: a CSP não admite estilo inline; o script aplica a largura (sem script, o texto ao lado basta).
                if (f.Current is { } c) sb.Append("<span class=\"bar cur\" data-w=\"").Append(Pct(c)).Append("\"></span>");
                if (f.Target is { } t) sb.Append("<span class=\"bar tgt\" data-w=\"").Append(Pct(t)).Append("\"></span>");
                sb.Append("</span><span class=\"bar-val\">")
                  .Append(f.Current is null && f.Target is null
                      ? "sem avaliação confirmada"
                      : $"atual {NistReportCanonical.Level(f.Current)} · alvo {NistReportCanonical.Level(f.Target)} · {f.Evaluated}/{f.Subcategories} avaliadas")
                  .Append("</span></a>");
            }
            sb.Append("</div><p class=\"legend\"><span class=\"sw cur\"></span> atual <span class=\"sw tgt\"></span> alvo — escala 1 a 5 da metodologia do AEGIS.</p>");
        }

        sb.Append("<h3>Funções</h3><div class=\"tw\"><table><thead><tr><th scope=\"col\">Função</th><th scope=\"col\">Atual</th><th scope=\"col\">Alvo</th>")
          .Append("<th scope=\"col\">Lacuna</th><th scope=\"col\">Avaliadas</th><th scope=\"col\">Não se aplicam</th><th scope=\"col\">Aguardando confirmação</th><th scope=\"col\">Sem avaliação</th></tr></thead><tbody>");
        foreach (var f in r.Functions)
            sb.Append("<tr><th scope=\"row\"><a href=\"#fn-").Append(E(f.Code)).Append("\">").Append(E(f.Code + " — " + f.Name)).Append("</a></th>")
              .Append(Td(NistReportCanonical.Level(f.Current))).Append(Td(NistReportCanonical.Level(f.Target))).Append(Td(Gap(f.Gap)))
              .Append(Td($"{f.Evaluated} de {f.Subcategories}")).Append(Td(f.NotApplicable.ToString(Pt))).Append(Td(f.PendingConfirmation.ToString(Pt)))
              .Append(Td((f.NotEvaluated + f.InProgress).ToString(Pt))).Append("</tr>");
        sb.Append("</tbody></table></div>");

        sb.Append("<h3>Lacunas prioritárias</h3>");
        if (r.PriorityGaps.Count == 0) sb.Append("<p class=\"muted\">Nenhuma lacuna confirmada (atual e alvo registrados por revisão humana).</p>");
        else
        {
            sb.Append("<div class=\"tw\"><table><thead><tr><th scope=\"col\">Resultado</th><th scope=\"col\">Atual</th><th scope=\"col\">Alvo</th><th scope=\"col\">Lacuna</th>")
              .Append("<th scope=\"col\">Risco fundamentado</th><th scope=\"col\">Achados abertos</th><th scope=\"col\">Tratamento</th></tr></thead><tbody>");
            foreach (var g in r.PriorityGaps)
                sb.Append("<tr><th scope=\"row\">").Append(SubLink(g.Code, g.Title)).Append("</th>")
                  .Append(Td(g.CurrentLevel.ToString(Pt))).Append(Td(g.TargetLevel.ToString(Pt))).Append(Td("+" + g.Gap.ToString(Pt)))
                  .Append(Td(g.RiskImpact ?? "—")).Append(Td(g.OpenFindings == 0 ? "—" : $"{g.OpenFindings} (maior: {g.HighestSeverityLabel})"))
                  .Append(Td(g.TreatmentLabel)).Append("</tr>");
            sb.Append("</tbody></table></div><p class=\"muted\">Lacuna é distância até a meta escolhida. Sem achado registrado, ela é meta de melhoria — não falha nem risco crítico por si.</p>");
        }

        sb.Append("<h3>Riscos fundamentados e tratamento</h3>");
        if (r.Findings.Count == 0) sb.Append("<p class=\"muted\">Nenhum achado registrado nesta rodada e escopo.</p>");
        else
        {
            sb.Append("<div class=\"tw\"><table><thead><tr><th scope=\"col\">Achado</th><th scope=\"col\">Severidade</th><th scope=\"col\">Prioridade</th>")
              .Append("<th scope=\"col\">Situação</th><th scope=\"col\">Plano</th><th scope=\"col\">Responsável</th><th scope=\"col\">Prazo</th><th scope=\"col\">Tratamento</th></tr></thead><tbody>");
            foreach (var f in r.Findings)
                sb.Append("<tr><th scope=\"row\"><a href=\"#achado-").Append(f.Id.ToString("N")).Append("\">").Append(E(f.Title)).Append("</a><span class=\"code\">")
                  .Append(E(f.SubcategoryCode)).Append("</span></th>")
                  .Append(Td(f.SeverityLabel)).Append(Td(f.PriorityLabel)).Append(Td(f.StatusLabel))
                  .Append(Td(f.Plan?.StatusLabel ?? "Sem plano")).Append(Td(f.Plan is null ? "—" : Person(f.Plan.Responsible)))
                  .Append(Td(f.Plan?.DueDate is { } dd ? D(dd) : "—")).Append(Td(f.TreatmentLabel)).Append("</tr>");
            sb.Append("</tbody></table></div><p class=\"muted\">Concluir um plano não altera maturidade, score nem conformidade: a reavaliação da subcategoria é um ato separado.</p>");
        }

        if (charts is not null)
        {
            sb.Append("<h3 id=\"detalhe-visual\">Perfil, andamento e lacunas</h3>").Append(ReportChartSvg.Grid(charts.Detail, Anchor));
            ReportHistoryHtml.Append(sb, history!, charts.History!);
        }

        sb.Append("<h3>Limitações desta fotografia</h3>");
        if (r.Limitations.Count == 0) sb.Append("<p class=\"muted\">Nenhuma limitação registrada.</p>");
        else
        {
            sb.Append("<ul class=\"lim\">");
            foreach (var l in r.Limitations) sb.Append("<li>").Append(E(l)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("</section>\n");

        // =========================== Visão técnica ===========================
        sb.Append("<section id=\"tecnica\" class=\"view\" aria-labelledby=\"h-tec\"><h2 id=\"h-tec\">Visão técnica</h2>")
          .Append("<div class=\"filters\" hidden><label>Função <select id=\"f-fn\"><option value=\"\">Todas</option>");
        foreach (var f in r.Functions) sb.Append("<option value=\"").Append(E(f.Code)).Append("\">").Append(E(f.Code + " — " + f.Name)).Append("</option>");
        sb.Append("</select></label><label>Situação <select id=\"f-state\"><option value=\"\">Todas</option>");
        foreach (var st in new[] { NistSubcategoryStates.Evaluated, NistSubcategoryStates.InProgress, NistSubcategoryStates.PendingConfirmation, NistSubcategoryStates.NotEvaluated, NistSubcategoryStates.NotApplicable })
            sb.Append("<option value=\"").Append(st).Append("\">").Append(E(NistLabels.State(st))).Append("</option>");
        sb.Append("</select></label><label>Buscar <input id=\"f-q\" type=\"search\" placeholder=\"código ou título\"></label>")
          .Append("<p id=\"f-count\" class=\"muted\" aria-live=\"polite\"></p></div>");

        foreach (var f in r.Functions)
        {
            sb.Append("<div class=\"fn\" data-fn=\"").Append(E(f.Code)).Append("\"><h3 id=\"fn-").Append(E(f.Code)).Append("\">").Append(E(f.Code + " — " + f.Name))
              .Append("</h3><p class=\"muted\">Atual ").Append(NistReportCanonical.Level(f.Current)).Append(" · alvo ").Append(NistReportCanonical.Level(f.Target))
              .Append(" · ").Append(f.Evaluated).Append(" de ").Append(f.Subcategories).Append(" avaliadas</p>");
            foreach (var c in r.Categories.Where(c => c.FunctionCode == f.Code))
            {
                sb.Append("<h4>").Append(E(c.Code + " — " + c.Name)).Append(" <span class=\"muted\">atual ").Append(NistReportCanonical.Level(c.Current))
                  .Append(" · alvo ").Append(NistReportCanonical.Level(c.Target)).Append("</span></h4>");
                foreach (var x in r.Subcategories.Where(x => x.CategoryCode == c.Code))
                    AppendSubcategory(sb, x, r);
            }
            sb.Append("</div>");
        }

        sb.Append("<h3 id=\"achados\">Achados e planos</h3>");
        if (r.Findings.Count == 0) sb.Append("<p class=\"muted\">Nenhum achado registrado.</p>");
        foreach (var f in r.Findings) AppendFinding(sb, f, r);
        sb.Append("</section>\n");

        // =========================== Metodologia ===========================
        sb.Append("<section id=\"metodologia\" class=\"view\" aria-labelledby=\"h-met\"><h2 id=\"h-met\">Metodologia e integridade</h2>")
          .Append("<p>").Append(E(r.Methodology.Statement)).Append("</p>")
          .Append("<p>").Append(E(r.Methodology.AggregationRule)).Append("</p>")
          .Append("<p>").Append(E(r.Methodology.CoverageRule)).Append("</p>")
          .Append("<p>").Append(E(r.Methodology.InstrumentsNote)).Append("</p>")
          .Append("<div class=\"tw\"><table><caption>Escala ").Append(E(r.Methodology.Version)).Append("</caption><thead><tr><th scope=\"col\">Nível</th><th scope=\"col\">Nome</th><th scope=\"col\">Descrição</th></tr></thead><tbody>");
        foreach (var l in r.Methodology.Scale)
            sb.Append("<tr><th scope=\"row\">").Append(l.Level).Append("</th>").Append(Td(l.Name)).Append(Td(l.Description)).Append("</tr>");
        sb.Append("</tbody></table></div><dl class=\"kv\">")
          .Append(Dt("Catálogo", $"{r.Catalog.FrameworkName} — {r.Catalog.Functions} funções, {r.Catalog.Categories} categorias, {r.Catalog.Subcategories} subcategorias"))
          .Append(Dt("Aproveitamento da rodada", r.Cycle.SeedModeLabel + (r.Cycle.SeedFromCycleName is null ? "" : $" — \"{r.Cycle.SeedFromCycleName}\"")))
          .Append(Dt("Fotografia", snapshot.Id.ToString("D")))
          .Append(Dt("Hash SHA-256 do conteúdo", snapshot.ContentHash))
          .Append(Dt("Impressão digital revisada", r.Publication?.ContentFingerprint ?? "—"))
          .Append(Dt("Esquema", $"{snapshot.SchemaVersion} · {r.Schema}"))
          .Append("</dl><p class=\"muted\">Título e explicação de cada subcategoria em linguagem clara: redação do AEGIS, não tradução oficial do NIST. O texto oficial (inglês) acompanha cada subcategoria.</p></section>\n");

        sb.Append("</main>\n<footer class=\"wrap\">Gerado pelo AEGIS a partir da fotografia imutável ").Append(E(snapshot.Id.ToString("D")))
          .Append(" (SHA-256 ").Append(E(snapshot.ContentHash)).Append("). Reexportar uma fotografia não consulta o estado atual da avaliação.</footer>\n")
          .Append("<script>").Append(js).Append("</script>\n</body>\n</html>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendSubcategory(StringBuilder sb, NistReportSubcategory x, NistMaturityReport r)
    {
        sb.Append("<details class=\"sub\" id=\"sub-").Append(E(x.Code)).Append("\" data-fn=\"").Append(E(x.FunctionCode))
          .Append("\" data-state=\"").Append(E(x.State)).Append("\" data-search=\"").Append(E((x.Code + " " + x.Title).ToLowerInvariant())).Append("\">")
          .Append("<summary><span class=\"code\">").Append(E(x.Code)).Append("</span> <span class=\"st st-").Append(E(x.State)).Append("\">")
          .Append(E(x.StateLabel)).Append("</span> ").Append(E(x.Title)).Append("<span class=\"lv\">")
          .Append(x.NotApplicable && x.State == NistSubcategoryStates.NotApplicable ? "não se aplica"
              : $"atual {Lv(x.CurrentLevel)} · alvo {Lv(x.TargetLevel)} · lacuna {Gap(x.Gap)}")
          .Append("</span></summary><div class=\"body\">")
          .Append("<p lang=\"en\" class=\"muted\"><b>NIST CSF 2.0:</b> ").Append(E(x.OfficialOutcome)).Append("</p><dl class=\"kv\">")
          .Append(Dt("Responsável pela prática", Person(x.Owner)))
          .Append(Dt("Avaliador / revisor", $"{x.AssessorName ?? "—"} / {x.ReviewerName ?? "—"}"))
          .Append(Dt("Avaliação humana", x.RecordedByName is null ? "sem confirmação humana" : $"{x.RecordedByName} em {Utc(x.RecordedAt)} (versão {x.Version})"))
          .Append(Dt("Revisão", x.ReviewStateLabel + (x.ReviewDecisionByName is null ? "" : $" — {x.ReviewDecisionByName} em {Utc(x.ReviewDecisionAt)}") + (x.ReviewDecisionNote is null ? "" : $": {x.ReviewDecisionNote}")))
          .Append(x.ContentOrigin == "Analyst" && x.OriginNote is null ? "" : Dt("Origem do conteúdo", NistLabels.ContentOrigin(x.ContentOrigin) + (x.OriginNote is null ? "" : " — " + x.OriginNote)))
          .Append(Opt("Justificativa", x.Rationale)).Append(Opt("Observações (atual)", x.CurrentComments)).Append(Opt("Observações (alvo)", x.TargetComments))
          .Append(Opt("Lacunas observadas", x.Gaps)).Append(Opt("Risco ou impacto", x.RiskImpact)).Append(Opt("Orientação de melhoria", x.ImprovementGuidance))
          .Append("</dl>");

        sb.Append("<h5>Procedimentos de avaliação</h5>");
        if (x.Procedures.Count == 0) sb.Append("<p class=\"muted\">Nenhum procedimento registrado.</p>");
        else
        {
            sb.Append("<ul class=\"items\">");
            foreach (var p in x.Procedures)
            {
                sb.Append("<li><b>").Append(E(p.MethodLabel)).Append("</b> — ").Append(E(p.Procedure)).Append("<br><span class=\"muted\">")
                  .Append(E(p.StatusLabel)).Append(p.OutcomeLabel is null ? "" : " · conclusão: " + E(p.OutcomeLabel))
                  .Append(p.PerformedOn is { } on ? " · realizado em " + D(on) : "")
                  .Append(p.RecordedByName is null ? "" : " · registrado por " + E(p.RecordedByName)).Append("</span>");
                if (p.Observation is not null) sb.Append("<br>").Append(E(p.Observation));
                if (p.EvidenceIds.Count > 0)
                    sb.Append("<br><span class=\"muted\">Evidências: ").Append(E(string.Join(" | ", p.EvidenceIds.Select(id => x.Evidence.FirstOrDefault(e => e.Id == id)?.Title ?? "evidência não vigente")))).Append("</span>");
                sb.Append("</li>");
            }
            sb.Append("</ul><p class=\"muted\">Método planejado não comprova a realização: só o procedimento realizado tem conclusão.</p>");
        }

        sb.Append("<h5>Evidências</h5>");
        if (x.Evidence.Count == 0) sb.Append("<p class=\"muted\">Nenhuma evidência vinculada.</p>");
        else
        {
            sb.Append("<ul class=\"items\">");
            foreach (var e in x.Evidence)
            {
                sb.Append("<li><details><summary>").Append(E(e.OriginKindLabel)).Append(" — ").Append(E(e.Title)).Append("</summary><dl class=\"kv\">")
                  .Append(Opt("Origem", e.OriginLabel)).Append(Dt("Data na origem", Utc(e.CollectedAt))).Append(Opt("Escopo da coleta", e.OriginScope))
                  .Append(Dt("Vinculada", Utc(e.LinkedAt) + (e.RecordedByName is null ? "" : " por " + e.RecordedByName))).Append(Opt("O que demonstra", e.Notes));
                if (SafeUrl(e.Uri) is { } url) sb.Append("<dt>Link</dt><dd><a rel=\"noopener noreferrer\" href=\"").Append(E(url)).Append("\">").Append(E(url)).Append("</a></dd>");
                sb.Append("</dl></details></li>");
            }
            sb.Append("</ul>");
        }

        if (x.FindingIds.Count > 0)
        {
            sb.Append("<h5>Achados</h5><ul class=\"items\">");
            foreach (var id in x.FindingIds)
                if (r.Findings.FirstOrDefault(f => f.Id == id) is { } f)
                    sb.Append("<li><a href=\"#achado-").Append(id.ToString("N")).Append("\">").Append(E(f.Title)).Append("</a> — ").Append(E(f.SeverityLabel))
                      .Append(" · ").Append(E(f.StatusLabel)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("</div></details>");
    }

    private static void AppendFinding(StringBuilder sb, NistReportFinding f, NistMaturityReport r)
    {
        var sub = r.Subcategories.FirstOrDefault(x => x.Code == f.SubcategoryCode);
        sb.Append("<details class=\"finding\" id=\"achado-").Append(f.Id.ToString("N")).Append("\"><summary><span class=\"sev sev-").Append(E(f.Severity)).Append("\">")
          .Append(E(f.SeverityLabel)).Append("</span> ").Append(E(f.Title)).Append(" <span class=\"code\">").Append(E(f.SubcategoryCode)).Append("</span></summary><div class=\"body\"><dl class=\"kv\">")
          .Append("<dt>Subcategoria</dt><dd>").Append(SubLink(f.SubcategoryCode, f.SubcategoryTitle)).Append("</dd>")
          .Append(Dt("Condição observada", f.Condition)).Append(Dt("Risco", f.Risk)).Append(Dt("Impacto", f.Impact))
          .Append(Dt("Severidade", f.SeverityLabel + " — " + f.SeverityRationale)).Append(Dt("Prioridade", f.PriorityLabel + " — " + f.PriorityRationale))
          .Append(Dt("Recomendação", f.Recommendation)).Append(Dt("Situação do achado", f.StatusLabel + (f.StatusNote is null ? "" : " — " + f.StatusNote)))
          .Append(Dt("Origem", f.OriginCurrentLevel is null && f.OriginTargetLevel is null ? "lacuna documentada" : $"atual {Lv(f.OriginCurrentLevel)} · alvo {Lv(f.OriginTargetLevel)} no registro"))
          .Append(Dt("Registrado", Utc(f.CreatedAt) + (f.CreatedByName is null ? "" : " por " + f.CreatedByName)));
        if (f.EvidenceIds.Count > 0 && sub is not null)
            sb.Append(Dt("Evidências", string.Join(" | ", f.EvidenceIds.Select(id => sub.Evidence.FirstOrDefault(e => e.Id == id)?.Title ?? "evidência não vigente"))));
        sb.Append("</dl>");
        if (f.Plan is { } p)
        {
            sb.Append("<h5>Plano de tratamento</h5><dl class=\"kv\">")
              .Append(Dt("Plano", p.Title)).Append(Opt("Ação proposta", p.ProposedAction))
              .Append(Dt("Responsável", Person(p.Responsible) + (p.ResponsibleArea is null ? "" : " · " + p.ResponsibleArea)))
              .Append(Dt("Prazo", p.DueDate is { } dd ? D(dd) + (p.IsOverdue ? " (atrasado)" : "") : "—"))
              .Append(Dt("Etapa", p.StatusLabel + (p.WasReopened ? " — reaberto" : ""))).Append(Dt("Próxima providência", p.NextStep))
              .Append(Opt("Execução relatada", p.ExecutionNotes is null ? null : p.ExecutionNotes + (p.ExecutedAt is { } ex ? $" ({Utc(ex)})" : "")))
              .Append(Opt("Validação", p.ValidationOutcomeLabel is null ? null
                  : $"{p.ValidationMethodLabel} — {p.ValidationOutcomeLabel}" + (p.ValidationEvidenceReference is null ? "" : $"; evidência: {p.ValidationEvidenceReference}")
                    + (p.ValidatedByName is null ? "" : $"; por {p.ValidatedByName}") + (p.ValidationAppliesToCurrentCycle == false ? " (ciclo anterior do plano)" : "")))
              .Append("</dl>");
        }
        else sb.Append("<p class=\"muted\">Sem plano de tratamento registrado.</p>");
        sb.Append("</div></details>");
    }

    // ---- Apoio ----

    private static string E(string? s) => Enc.Encode(s ?? "");

    /// <summary>[AEGIS-ASSESSMENT-VISUALS-01] Destino do detalhe de um gráfico: âncora interna do próprio relatório.</summary>
    private static string? Anchor(string target) => target.StartsWith('#') ? "href=\"" + E(target) + "\"" : null;
    private static string D(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static string Utc(DateTimeOffset? v) => v is { } x ? x.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC" : "—";
    private static string Lv(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "—";
    private static string Gap(double? g) => g is { } x ? (x > 0 ? "+" : "") + x.ToString("0.0", Pt) : "indeterminada";
    private static string Gap(int? g) => g is { } x ? (x > 0 ? "+" : "") + x.ToString(CultureInfo.InvariantCulture) : "indeterminada";
    private static string Pct(double level) => Math.Round(Math.Clamp(level / 5.0, 0, 1) * 100, 1).ToString("0.#", CultureInfo.InvariantCulture);
    private static string Td(string? v) => "<td>" + E(v) + "</td>";
    private static string Dt(string k, string? v) => "<dt>" + E(k) + "</dt><dd>" + E(v ?? "—") + "</dd>";
    private static string Opt(string k, string? v) => string.IsNullOrWhiteSpace(v) ? "" : Dt(k, v);
    private static string Card(string k, string v, string hint) =>
        "<div class=\"card\"><div class=\"k\">" + E(k) + "</div><div class=\"v\">" + E(v) + "</div><div class=\"h\">" + E(hint) + "</div></div>";
    private static string SubLink(string code, string title) =>
        "<a href=\"#sub-" + E(code) + "\">" + E(title) + "</a><span class=\"code\">" + E(code) + "</span>";
    private static string Person(NistReportPerson p) => p.Name is null ? "—"
        : p.Kind switch { "User" => p.Name + " (usuário)", "External" => p.Name + " (externo" + (p.Contact is null ? ")" : ", " + p.Contact + ")"), _ => p.Name };

    private static string? SafeUrl(string? uri) =>
        uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.ToString() : null;

    private static string Sha256(string content) =>
        "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // Estilo e script FIXOS (sem dado): o hash de cada um vai na CSP. Atributo style seria bloqueado pela CSP — a largura das
    // barras vem de data-w e é aplicada pelo script (manipulação via CSSOM, permitida).
    internal const string Css = """
        :root{--ink:#18202e;--muted:#5f6a80;--line:#d6dce8;--bg:#f6f8fb;--card:#fff;--acc:#0b5e9b;--cur:#0b5e9b;--tgt:#b8c7dc;--warn:#9a5b00;--bad:#a12622;--ok:#1f7a3f}
        *{box-sizing:border-box}html{-webkit-text-size-adjust:100%}
        body{margin:0;font:15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,Arial,sans-serif;color:var(--ink);background:var(--bg)}
        .wrap{max-width:1120px;margin:0 auto;padding:0 16px}
        .top{background:#1c2438;color:#fff;padding:20px 0 16px}.top h1{margin:4px 0 6px;font-size:26px}
        .brand{margin:0;font-size:13px;opacity:.8}.sub{margin:0 0 8px;font-size:16px}
        .meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,240px),1fr));gap:4px 16px;margin:0;font-size:13px}
        .meta dt{opacity:.75}.meta dd{margin:0 0 4px}
        .tabs{display:flex;flex-wrap:wrap;gap:8px;padding-top:12px;padding-bottom:4px}
        .tab{padding:8px 14px;border:1px solid var(--line);border-radius:8px;background:#fff;color:var(--ink);text-decoration:none;font-weight:600}
        .tab[aria-current=true]{background:var(--acc);color:#fff;border-color:var(--acc)}
        .tab:focus-visible,a:focus-visible,summary:focus-visible,select:focus-visible,input:focus-visible{outline:3px solid #f0a500;outline-offset:2px}
        h2{font-size:22px;margin:20px 0 12px}h3{font-size:18px;margin:22px 0 10px}h4{font-size:15px;margin:16px 0 6px}h5{font-size:14px;margin:12px 0 4px}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,160px),1fr));gap:12px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px}
        .card .k{font-size:12px;color:var(--muted);text-transform:none}.card .v{font-size:24px;font-weight:700}.card .h{font-size:12px;color:var(--muted)}
        .note{background:#eef4fb;border-left:4px solid var(--acc);padding:8px 12px;border-radius:6px}
        .muted{color:var(--muted);font-size:13px}
        .bars{display:flex;flex-direction:column;gap:6px}
        .bar-row{display:grid;grid-template-columns:minmax(0,200px) minmax(0,1fr) minmax(0,260px);gap:8px;align-items:center;text-decoration:none;color:inherit;padding:4px;border-radius:6px}
        .bar-row:hover{background:#eef2f8}
        .bar-track{position:relative;height:18px;background:#e9edf3;border-radius:4px;overflow:hidden}
        .bar{position:absolute;left:0;top:0;bottom:0;border-radius:4px}.bar.tgt{background:var(--tgt);top:9px}.bar.cur{background:var(--cur);bottom:9px}
        .bar-val{font-size:12px;color:var(--muted)}
        .legend{font-size:12px;color:var(--muted)}.sw{display:inline-block;width:12px;height:12px;border-radius:2px;vertical-align:middle}.sw.cur{background:var(--cur)}.sw.tgt{background:var(--tgt)}
        .tw{overflow-x:auto;-webkit-overflow-scrolling:touch;border:1px solid var(--line);border-radius:8px;background:#fff}
        table{border-collapse:collapse;width:100%;min-width:560px;font-size:13px}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
        thead th{background:#eef1f6}caption{text-align:left;padding:6px 8px;font-weight:600}
        .code{font-family:ui-monospace,Consolas,monospace;font-size:12px;border:1px solid var(--line);border-radius:4px;padding:0 4px;margin-left:6px;white-space:nowrap}
        details.sub,details.finding{background:#fff;border:1px solid var(--line);border-radius:8px;margin:6px 0}
        details>summary{cursor:pointer;padding:8px 10px;list-style-position:inside}
        details .body{padding:4px 12px 12px;border-top:1px solid var(--line)}
        .lv{display:block;font-size:12px;color:var(--muted);margin-left:22px}
        .st{font-size:11px;padding:1px 6px;border-radius:10px;border:1px solid var(--line);white-space:nowrap}
        .st-Evaluated{background:#e5f2ff}.st-PendingConfirmation{background:#fff3d6}.st-InProgress{background:#fff8e5}.st-NotApplicable{background:#eef0f3}
        .sev{font-size:11px;padding:1px 6px;border-radius:10px;color:#fff;background:var(--muted)}.sev-Critical{background:var(--bad)}.sev-High{background:#c2410c}.sev-Medium{background:var(--warn)}.sev-Low{background:#4b5563}
        .kv{display:grid;grid-template-columns:minmax(0,220px) minmax(0,1fr);gap:4px 12px;margin:8px 0}.kv dt{color:var(--muted);font-size:13px}.kv dd{margin:0;overflow-wrap:anywhere}
        .items{padding-left:18px}.items li{margin:4px 0;overflow-wrap:anywhere}.lim li{margin:4px 0}
        .filters{display:flex;flex-wrap:wrap;gap:12px;align-items:end;background:#fff;border:1px solid var(--line);border-radius:8px;padding:10px;margin-bottom:10px}
        .filters label{display:flex;flex-direction:column;font-size:12px;color:var(--muted);gap:2px}
        .filters select,.filters input{font:inherit;padding:6px 8px;border:1px solid var(--line);border-radius:6px;min-width:0;max-width:100%}
        footer{color:var(--muted);font-size:12px;padding:24px 16px 40px;overflow-wrap:anywhere}
        .js .view[hidden]{display:none}
        @media (max-width:640px){.bar-row{grid-template-columns:1fr}.kv{grid-template-columns:1fr}.top h1{font-size:22px}.card .v{font-size:20px}}
        @media print{.tabs,.filters{display:none}.view[hidden]{display:block!important}details{break-inside:avoid}details:not([open])>.body{display:block}body{background:#fff}}
        """;

    internal const string Js = """
        (function(){
          var doc=document;doc.documentElement.className+=' js';
          var views=Array.prototype.slice.call(doc.querySelectorAll('.view'));
          var tabs=Array.prototype.slice.call(doc.querySelectorAll('.tab'));
          function show(id){views.forEach(function(v){v.hidden=v.id!==id;});tabs.forEach(function(t){t.setAttribute('aria-current',t.getAttribute('data-tab')===id?'true':'false');});}
          function reveal(hash){
            if(!hash||hash.length<2){show('executiva');return;}
            var el=doc.getElementById(decodeURIComponent(hash.slice(1)));if(!el){show('executiva');return;}
            var view=el.closest?el.closest('.view'):null;show(view?view.id:'executiva');
            var d=el;while(d){if(d.tagName==='DETAILS')d.open=true;d=d.parentElement;}
            if(el.scrollIntoView)el.scrollIntoView();
          }
          tabs.forEach(function(t){t.addEventListener('click',function(e){e.preventDefault();var id=t.getAttribute('data-tab');show(id);if(history.replaceState)history.replaceState(null,'','#'+id);var v=doc.getElementById(id);if(v){var h=v.querySelector('h2');if(h){h.setAttribute('tabindex','-1');h.focus();}}});});
          window.addEventListener('hashchange',function(){reveal(location.hash);});
          Array.prototype.forEach.call(doc.querySelectorAll('.bar[data-w]'),function(b){b.style.width=b.getAttribute('data-w')+'%';});
          var filters=doc.querySelector('.filters');if(filters)filters.hidden=false;
          var fFn=doc.getElementById('f-fn'),fSt=doc.getElementById('f-state'),fQ=doc.getElementById('f-q'),out=doc.getElementById('f-count');
          var subs=Array.prototype.slice.call(doc.querySelectorAll('details.sub'));
          function apply(){var fn=fFn.value,st=fSt.value,q=(fQ.value||'').toLowerCase().trim(),n=0;
            subs.forEach(function(s){var ok=(!fn||s.getAttribute('data-fn')===fn)&&(!st||s.getAttribute('data-state')===st)&&(!q||s.getAttribute('data-search').indexOf(q)>=0);s.hidden=!ok;if(ok)n++;});
            Array.prototype.forEach.call(doc.querySelectorAll('.fn'),function(f){f.hidden=!!fn&&f.getAttribute('data-fn')!==fn;});
            out.textContent=n+' de '+subs.length+' subcategoria(s)';}
          if(fFn){fFn.addEventListener('change',apply);fSt.addEventListener('change',apply);fQ.addEventListener('input',apply);apply();}
          reveal(location.hash);
        })();
        """;
}
