using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace AegisScore.Infrastructure.Posture.Export;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] PDF da fotografia de MATURIDADE NIST (pt-BR, A4), derivado só do relatório congelado, com a
/// mesma pilha do relatório de postura (PDFsharp/MigraDoc core, fontes do resolvedor existente). Ordem: identificação →
/// visão executiva (atual × alvo, cobertura, seis funções, lacunas prioritárias, achados e tratamento, limitações) → visão
/// técnica (categorias e subcategorias com métodos, observações e evidências) → metodologia e integridade. Determinístico:
/// nenhum texto é gerado na exportação; a data autoritativa é a da publicação.
/// </summary>
public static class NistReportPdfWriter
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Color Ink = new(24, 30, 46);
    private static readonly Color Muted = new(96, 106, 128);
    private static readonly Color HeaderShade = new(28, 36, 56);
    private static readonly Color Zebra = new(244, 246, 250);
    private static readonly Color Line = new(206, 213, 226);
    private static readonly Color Accent = new(11, 94, 155);

    public static byte[] Write(PostureSnapshot snapshot, NistMaturityReport r)
    {
        var doc = new Document();
        doc.Info.Title = "Relatório de maturidade NIST";
        doc.Info.Author = "AEGIS";
        doc.Info.Subject = "AEGIS NIST — maturidade (metodologia autoral do AEGIS)";
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = PostureSnapshotPdfWriter.ReportFontFamily;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.7);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.7);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.9);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.9);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Muted;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText($"AEGIS NIST · Fotografia imutável {snapshot.Id.ToString("D")[..8]} · página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();

        // ---- Identificação ----
        var eyebrow = section.AddParagraph("AEGIS NIST · Maturidade pelo NIST CSF 2.0 · Fotografia imutável");
        eyebrow.Format.Font.Size = 8;
        eyebrow.Format.Font.Color = Muted;
        var title = section.AddParagraph("Relatório de maturidade");
        title.Format.Font.Size = 19;
        title.Format.Font.Bold = true;
        var sub = section.AddParagraph($"{r.Client.Name ?? "Cliente não registrado"} · {r.Assessment.Name}");
        sub.Format.Font.Size = 10.5;
        sub.Format.Font.Bold = true;
        sub.Format.Font.Color = Accent;
        sub.Format.SpaceAfter = Unit.FromMillimeter(2);

        var meta = Kv(section);
        KvRow(meta, "Escopo", r.Scope.Name + (r.Scope.Description is null ? "" : " — " + r.Scope.Description));
        KvRow(meta, "Rodada", $"{r.Cycle.Name} — {r.Cycle.PeriodKindLabel}, {D(r.Cycle.PeriodStart)} a {D(r.Cycle.PeriodEnd)} ({NistLabels.CycleStatus(r.Cycle.Status).ToLowerInvariant()} na publicação)");
        KvRow(meta, "Publicação", r.Publication is { } pub ? Stamp(pub.PublishedAt) + (pub.PublishedByName is null ? "" : " por " + pub.PublishedByName) : "—");
        KvRow(meta, "Catálogo e metodologia", $"{r.Catalog.FrameworkName} · {r.Methodology.Version} (escala 1–5 autoral do AEGIS)");
        KvRow(meta, "Fotografia / hash", $"{snapshot.Id:D} · SHA-256 {snapshot.ContentHash}");

        // ---- Visão executiva ----
        var s = r.Summary;
        Heading(section, "Visão executiva");
        Body(section, $"Atual (média): {NistReportCanonical.Level(s.Current)} · Alvo (média): {NistReportCanonical.Level(s.Target)} · " +
                      $"Lacuna média: {Gap(s.Gap)} · Cobertura: {s.Coverage.ToString("0.#", Pt)}% ({s.Evaluated} avaliadas + {s.NotApplicable} não aplicáveis de {s.Subcategories}).", bold: true);
        Body(section, $"Médias sobre {s.WithCurrent} subcategoria(s) com atual e {s.WithTarget} com alvo confirmados; {s.PendingConfirmation} aguardando confirmação humana; " +
                      $"{s.NotEvaluated + s.InProgress} sem situação atual. Revisões aprovadas: {s.ReviewApproved}. Procedimentos realizados: {s.ProceduresPerformed} " +
                      $"(planejados sem resultado: {s.ProceduresPlanned}). Achados abertos: {s.FindingsOpen}; planos ativos: {s.PlansActive}, atrasados: {s.PlansOverdue}.");
        Body(section, NistMethodologyText.InstrumentsNote, muted: true);

        var fn = Table(section, 4.6, 1.5, 1.5, 1.5, 2.2, 1.6, 2.0, 2.0);
        Header(fn, "Função", "Atual", "Alvo", "Lacuna", "Avaliadas", "Não se aplicam", "Aguardando confirmação", "Sem avaliação");
        var zebra = false;
        foreach (var f in r.Functions)
        {
            var row = fn.AddRow();
            if (zebra) Shade(row);
            zebra = !zebra;
            Cell(row, 0, $"{f.Code} — {f.Name}", bold: true);
            Cell(row, 1, NistReportCanonical.Level(f.Current), align: ParagraphAlignment.Center);
            Cell(row, 2, NistReportCanonical.Level(f.Target), align: ParagraphAlignment.Center);
            Cell(row, 3, Gap(f.Gap), align: ParagraphAlignment.Center);
            Cell(row, 4, $"{f.Evaluated} de {f.Subcategories}", align: ParagraphAlignment.Center);
            Cell(row, 5, f.NotApplicable.ToString(Pt), align: ParagraphAlignment.Center);
            Cell(row, 6, f.PendingConfirmation.ToString(Pt), align: ParagraphAlignment.Center);
            Cell(row, 7, (f.NotEvaluated + f.InProgress).ToString(Pt), align: ParagraphAlignment.Center);
        }

        // [AEGIS-ASSESSMENT-VISUALS-01] Painel e evolução mensal SÓ nas fotografias que congelaram o histórico (as anteriores
        // saem como antes). Os gráficos são os MESMOS dados do HTML; o histórico vem congelado, nunca das publicações de hoje.
        var history = FrozenPostureHistoryBuilder.Deserialize(snapshot.HistoryJson);
        var visuals = history is null ? null : new ReportChartPdf(PostureSnapshotPdfWriter.ReportFontFamily);
        if (history is not null && visuals is not null)
        {
            var charts = ReportChartBuilder.ForNist(r, history);
            Heading(section, "Painel da avaliação");
            foreach (var c in charts.Executive.Concat(charts.Detail)) visuals.Add(section, c);
            Heading(section, $"Evolução mensal ({FrozenPostureHistoryBuilder.MonthLabel(history.From)} a {FrozenPostureHistoryBuilder.MonthLabel(history.Until)})");
            Body(section, $"{history.Series.Label} · {history.Series.Instrument} · {history.Series.FormulaVersion} / {history.Series.CatalogVersion}. {history.Series.CoverageBasis} {history.Criterion}", muted: true);
            visuals.Add(section, charts.History!);
            ReportChartPdf.HistoryTable(section, history, (row, col, text, bold) => Cell(row, col, text, bold: bold));
            foreach (var related in history.RelatedSeries) Body(section, related, muted: true);
            Body(section, "A variação só é calculada entre pontos comparáveis da mesma série; ela não indica, sozinha, a causa.", muted: true);
        }

        Heading(section, "Lacunas prioritárias");
        if (r.PriorityGaps.Count == 0) Body(section, "Nenhuma lacuna confirmada (atual e alvo registrados por revisão humana).", muted: true);
        else
        {
            var gt = Table(section, 5.4, 1.1, 1.1, 1.2, 4.2, 4.0);
            Header(gt, "Resultado", "Atual", "Alvo", "Lacuna", "Risco fundamentado", "Tratamento");
            zebra = false;
            foreach (var g in r.PriorityGaps)
            {
                var row = gt.AddRow();
                if (zebra) Shade(row);
                zebra = !zebra;
                Cell(row, 0, $"{g.Code} — {g.Title}");
                Cell(row, 1, g.CurrentLevel.ToString(Pt), align: ParagraphAlignment.Center);
                Cell(row, 2, g.TargetLevel.ToString(Pt), align: ParagraphAlignment.Center);
                Cell(row, 3, "+" + g.Gap.ToString(Pt), align: ParagraphAlignment.Center);
                Cell(row, 4, g.RiskImpact ?? "—");
                Cell(row, 5, g.OpenFindings == 0 ? g.TreatmentLabel : $"{g.OpenFindings} achado(s), maior {g.HighestSeverityLabel} — {g.TreatmentLabel}");
            }
            Body(section, "Lacuna é a distância até a meta escolhida; sem achado registrado, é meta de melhoria — não falha nem risco crítico por si.", muted: true);
        }

        Heading(section, "Achados e tratamento");
        if (r.Findings.Count == 0) Body(section, "Nenhum achado registrado nesta rodada e escopo.", muted: true);
        else
        {
            var ft = Table(section, 5.0, 1.6, 1.6, 1.9, 3.1, 1.9, 2.0);
            Header(ft, "Achado", "Severidade", "Prioridade", "Situação", "Plano · responsável", "Prazo", "Tratamento");
            zebra = false;
            foreach (var f in r.Findings)
            {
                var row = ft.AddRow();
                if (zebra) Shade(row);
                zebra = !zebra;
                Cell(row, 0, $"{f.Title} ({f.SubcategoryCode})", bold: true);
                Cell(row, 1, f.SeverityLabel);
                Cell(row, 2, f.PriorityLabel);
                Cell(row, 3, f.StatusLabel);
                Cell(row, 4, f.Plan is null ? "Sem plano" : $"{f.Plan.Title} · {Person(f.Plan.Responsible)}");
                Cell(row, 5, f.Plan?.DueDate is { } dd ? D(dd) + (f.Plan.IsOverdue ? " (atrasado)" : "") : "—");
                Cell(row, 6, f.TreatmentLabel);
            }
            Body(section, "Concluir um plano não altera maturidade, score nem conformidade: a reavaliação da subcategoria é um ato separado.", muted: true);
        }

        Heading(section, "Limitações desta fotografia");
        if (r.Limitations.Count == 0) Body(section, "Nenhuma limitação registrada.", muted: true);
        foreach (var l in r.Limitations) Body(section, "• " + l);

        // ---- Visão técnica ----
        section.AddPageBreak();
        Heading(section, "Visão técnica — categorias e subcategorias");
        Body(section, "Cada subcategoria traz situação, níveis confirmados, responsáveis, revisão, procedimentos (planejado × resultado) e evidências com procedência.", muted: true);
        foreach (var f in r.Functions)
        {
            var h = section.AddParagraph($"{f.Code} — {f.Name}  ·  atual {NistReportCanonical.Level(f.Current)} · alvo {NistReportCanonical.Level(f.Target)}");
            h.Format.Font.Size = 10.5;
            h.Format.Font.Bold = true;
            h.Format.SpaceBefore = Unit.FromMillimeter(3);
            h.Format.KeepWithNext = true;
            foreach (var c in r.Categories.Where(c => c.FunctionCode == f.Code))
            {
                var ch = section.AddParagraph($"{c.Code} — {c.Name}  (atual {NistReportCanonical.Level(c.Current)} · alvo {NistReportCanonical.Level(c.Target)})");
                ch.Format.Font.Size = 9;
                ch.Format.Font.Bold = true;
                ch.Format.Font.Color = Accent;
                ch.Format.SpaceBefore = Unit.FromMillimeter(1.6);
                ch.Format.KeepWithNext = true;
                var st = Table(section, 2.0, 6.2, 3.2, 1.1, 1.1, 1.2, 2.3);
                Header(st, "Código", "Resultado", "Situação", "Atual", "Alvo", "Lacuna", "Revisão");
                zebra = false;
                foreach (var x in r.Subcategories.Where(x => x.CategoryCode == c.Code))
                {
                    var row = st.AddRow();
                    if (zebra) Shade(row);
                    zebra = !zebra;
                    Cell(row, 0, x.Code, bold: true);
                    Cell(row, 1, x.Title);
                    Cell(row, 2, x.StateLabel);
                    Cell(row, 3, Lv(x.CurrentLevel), align: ParagraphAlignment.Center);
                    Cell(row, 4, Lv(x.TargetLevel), align: ParagraphAlignment.Center);
                    Cell(row, 5, x.Gap is { } gp ? (gp > 0 ? "+" : "") + gp.ToString(Pt) : "—", align: ParagraphAlignment.Center);
                    Cell(row, 6, x.ReviewStateLabel);
                }
                foreach (var x in r.Subcategories.Where(x => x.CategoryCode == c.Code && HasDetail(x)))
                    SubcategoryDetail(section, x);
            }
        }

        if (r.Findings.Count > 0)
        {
            Heading(section, "Achados — detalhe");
            foreach (var f in r.Findings)
            {
                var t = Kv(section);
                KvRow(t, "Achado", $"{f.Title} ({f.SubcategoryCode} — {f.SubcategoryTitle})", bold: true);
                KvRow(t, "Condição observada", f.Condition);
                KvRow(t, "Risco / impacto", $"{f.Risk} / {f.Impact}");
                KvRow(t, "Severidade", $"{f.SeverityLabel} — {f.SeverityRationale}");
                KvRow(t, "Prioridade", $"{f.PriorityLabel} — {f.PriorityRationale}");
                KvRow(t, "Recomendação", f.Recommendation);
                KvRow(t, "Situação", f.StatusLabel + (f.StatusNote is null ? "" : " — " + f.StatusNote));
                if (f.Plan is { } p)
                {
                    KvRow(t, "Plano", $"{p.Title} · {p.StatusLabel}{(p.WasReopened ? " (reaberto)" : "")} · {Person(p.Responsible)} · prazo {(p.DueDate is { } dd ? D(dd) : "—")}");
                    KvRow(t, "Próxima providência", p.NextStep);
                    if (p.ExecutionNotes is not null) KvRow(t, "Execução relatada", p.ExecutionNotes);
                    if (p.ValidationOutcomeLabel is not null)
                        KvRow(t, "Validação", $"{p.ValidationMethodLabel} — {p.ValidationOutcomeLabel}" + (p.ValidationEvidenceReference is null ? "" : $" (evidência: {p.ValidationEvidenceReference})"));
                }
                section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1.5);
            }
        }

        // ---- Metodologia e integridade ----
        Heading(section, "Metodologia e integridade");
        Body(section, r.Methodology.Statement);
        Body(section, r.Methodology.AggregationRule);
        Body(section, r.Methodology.CoverageRule);
        var sc = Table(section, 1.4, 3.2, 12.6);
        Header(sc, "Nível", "Nome", "Descrição");
        foreach (var l in r.Methodology.Scale)
        {
            var row = sc.AddRow();
            Cell(row, 0, l.Level.ToString(Pt), align: ParagraphAlignment.Center);
            Cell(row, 1, l.Name, bold: true);
            Cell(row, 2, l.Description);
        }
        Body(section, $"Impressão digital do conteúdo revisado: {r.Publication?.ContentFingerprint ?? "—"}. Hash SHA-256 da fotografia: {snapshot.ContentHash}. " +
                      "Reexportar uma fotografia não consulta o estado atual da avaliação.", muted: true);
        Body(section, "Título e explicação das subcategorias em linguagem clara: redação do AEGIS, não tradução oficial do NIST.", muted: true);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        visuals?.Draw(renderer);
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms);
        return ms.ToArray();
    }

    private static bool HasDetail(NistReportSubcategory x) =>
        x.State != NistSubcategoryStates.NotEvaluated || x.Procedures.Count > 0 || x.Evidence.Count > 0;

    private static void SubcategoryDetail(Section section, NistReportSubcategory x)
    {
        var t = Kv(section);
        KvRow(t, x.Code, x.Title, bold: true);
        KvRow(t, "Situação", $"{x.StateLabel} · atual {Lv(x.CurrentLevel)} · alvo {Lv(x.TargetLevel)}" + (x.NotApplicable ? " · não se aplica" : ""));
        KvRow(t, "Responsáveis", $"prática: {Person(x.Owner)} · avaliador: {x.AssessorName ?? "—"} · revisor: {x.ReviewerName ?? "—"}");
        KvRow(t, "Avaliação humana / revisão", (x.RecordedByName is null ? "sem confirmação humana" : $"{x.RecordedByName} em {Stamp(x.RecordedAt)}") + " · " + x.ReviewStateLabel
            + (x.ReviewDecisionByName is null ? "" : $" ({x.ReviewDecisionByName})"));
        if (x.ContentOrigin != "Analyst" || x.OriginNote is not null)
            KvRow(t, "Origem do conteúdo", NistLabels.ContentOrigin(x.ContentOrigin) + (x.OriginNote is null ? "" : " — " + x.OriginNote));
        Opt(t, "Justificativa", x.Rationale);
        Opt(t, "Observações (atual)", x.CurrentComments);
        Opt(t, "Lacunas observadas", x.Gaps);
        Opt(t, "Risco ou impacto", x.RiskImpact);
        Opt(t, "Orientação de melhoria", x.ImprovementGuidance);
        foreach (var p in x.Procedures)
            KvRow(t, $"Método: {p.MethodLabel}", $"{p.Procedure} — {p.StatusLabel}" + (p.OutcomeLabel is null ? "" : $", conclusão {p.OutcomeLabel}")
                + (p.PerformedOn is { } on ? $" em {D(on)}" : "") + (p.Observation is null ? "" : $". Observado: {p.Observation}"));
        foreach (var e in x.Evidence)
            KvRow(t, $"Evidência: {e.OriginKindLabel}", $"{e.Title} · data na origem {Stamp(e.CollectedAt)}" + (e.OriginScope is null ? "" : $" · {e.OriginScope}")
                + (e.Notes is null ? "" : $" · {e.Notes}") + (e.Uri is null ? "" : $" · {e.Uri}"));
        section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1);
    }

    // ---- Apoio ----

    private static Table Table(Section section, params double[] widthsCm)
    {
        var t = section.AddTable();
        t.Borders.Color = Line;
        t.Borders.Width = 0.25;
        t.LeftPadding = Unit.FromMillimeter(1.2);
        t.RightPadding = Unit.FromMillimeter(1.2);
        t.TopPadding = Unit.FromMillimeter(0.6);
        t.BottomPadding = Unit.FromMillimeter(0.6);
        foreach (var w in widthsCm) t.AddColumn(Unit.FromCentimeter(w));
        return t;
    }

    private static void Header(Table t, params string[] headers)
    {
        var row = t.AddRow();
        row.HeadingFormat = true;
        row.Shading.Color = HeaderShade;
        for (var i = 0; i < headers.Length; i++)
        {
            var p = row.Cells[i].AddParagraph(headers[i]);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 7.3;
            p.Format.Font.Color = Colors.White;
        }
    }

    private static Table Kv(Section section)
    {
        var t = Table(section, 4.2, 13.0);
        t.Format.SpaceAfter = Unit.FromMillimeter(1);
        return t;
    }

    private static void KvRow(Table t, string k, string v, bool bold = false)
    {
        var row = t.AddRow();
        row.KeepWith = 0;
        Cell(row, 0, k, bold: true, color: Muted);
        Cell(row, 1, v, bold: bold);
    }

    private static void Opt(Table t, string k, string? v)
    {
        if (!string.IsNullOrWhiteSpace(v)) KvRow(t, k, v);
    }

    private static void Shade(Row row)
    {
        for (var i = 0; i < row.Cells.Count; i++) row.Cells[i].Shading.Color = Zebra;
    }

    private static void Cell(Row row, int col, string text, bool bold = false, ParagraphAlignment align = ParagraphAlignment.Left, Color? color = null)
    {
        var p = row.Cells[col].AddParagraph(text ?? "");
        p.Format.Font.Size = 7.6;
        p.Format.Font.Bold = bold;
        p.Format.Alignment = align;
        if (color is { } c) p.Format.Font.Color = c;
    }

    private static void Heading(Section section, string text)
    {
        var h = section.AddParagraph(text);
        h.Format.Font.Size = 11.5;
        h.Format.Font.Bold = true;
        h.Format.Font.Color = Accent;
        h.Format.SpaceBefore = Unit.FromMillimeter(3);
        h.Format.SpaceAfter = Unit.FromMillimeter(1.6);
        h.Format.KeepWithNext = true;
    }

    private static void Body(Section section, string text, bool bold = false, bool muted = false)
    {
        var p = section.AddParagraph(text);
        p.Format.Font.Size = 8.6;
        p.Format.Font.Bold = bold;
        if (muted) p.Format.Font.Color = Muted;
        p.Format.SpaceAfter = Unit.FromMillimeter(1.4);
    }

    private static string D(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset? at) => at is { } v ? v.ToUniversalTime().ToString("dd/MM/yyyy HH:mm 'UTC'", Pt) : "—";
    private static string Lv(int? v) => v?.ToString(Pt) ?? "—";
    private static string Gap(double? g) => g is { } x ? (x > 0 ? "+" : "") + x.ToString("0.0", Pt) : "indeterminada";
    private static string Person(NistReportPerson p) => p.Name is null ? "—"
        : p.Kind switch { "User" => p.Name + " (usuário)", "External" => p.Name + " (externo" + (p.Contact is null ? ")" : ", " + p.Contact + ")"), _ => p.Name };
}
