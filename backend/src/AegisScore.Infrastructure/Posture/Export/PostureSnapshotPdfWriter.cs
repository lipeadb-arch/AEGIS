using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Posture.Export;

/// <summary>
/// [AEGIS-AUD-034] Renderiza o RELATÓRIO EXECUTIVO em PDF (pt-BR) a partir da fotografia IMUTÁVEL de postura, com a
/// edição CORE do PDFsharp/MigraDoc (sem GDI+/WPF/Office/navegador). O relatório é DETERMINÍSTICO — nenhum resumo ou
/// recomendação é gerado por IA; todo texto deriva apenas dos números congelados na fotografia. Cobre os dois
/// instrumentos (AEGIS Score/NIST e AEGIS KNIGHT) sem misturá-los. Score ausente é "Não avaliado", nunca 0%, e a
/// data autoritativa é <see cref="PostureSnapshot.CapturedAt"/> — jamais o instante da exportação.
/// </summary>
public static class PostureSnapshotPdfWriter
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    // Paleta sóbria para um relatório executivo.
    private static readonly Color Ink = new(24, 30, 46);
    private static readonly Color Muted = new(96, 106, 128);
    private static readonly Color HeaderShade = new(28, 36, 56);
    private static readonly Color Zebra = new(244, 246, 250);
    private static readonly Color Line = new(206, 213, 226);
    private static readonly Color Accent = new(11, 94, 155);

    /// <summary>Nome de família resolvido uma única vez (registra o resolvedor de fontes no processo).</summary>
    private static readonly string FontFamily = InitializeFonts();

    private static readonly string[] NistFunctionOrder = { "GV", "ID", "PR", "DE", "RS", "RC" };

    private static readonly Dictionary<string, string> NistFunctionName = new(StringComparer.Ordinal)
    {
        ["GV"] = "Governar", ["ID"] = "Identificar", ["PR"] = "Proteger",
        ["DE"] = "Detectar", ["RS"] = "Responder", ["RC"] = "Recuperar",
    };

    public static byte[] Write(PostureSnapshot s)
    {
        var doc = new Document();
        doc.Info.Title = "Relatório Executivo de Postura";
        doc.Info.Author = "AEGIS";
        doc.Info.Subject = s.Type == PostureSnapshotType.Knight ? "AEGIS KNIGHT" : "AEGIS Score / NIST CSF";

        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = FontFamily;
        normal.Font.Size = 8.5;
        normal.Font.Color = Ink;

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.7);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.7);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.9);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.9);

        AddFooter(section);
        AddHeaderBlock(section, s);
        AddMetadata(section, s);
        AddExecutiveSummary(section, s);

        if (s.Type == PostureSnapshotType.Knight)
            AddKnightBody(section, s);
        else
            AddAegisBody(section, s);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms);
        return ms.ToArray();
    }

    // ---- Cabeçalho + metadados -----------------------------------------------------------------------

    private static void AddHeaderBlock(Section section, PostureSnapshot s)
    {
        var eyebrow = section.AddParagraph("AEGIS · Postura de Segurança · Fotografia imutável");
        eyebrow.Format.Font.Size = 8;
        eyebrow.Format.Font.Color = Muted;
        eyebrow.Format.SpaceAfter = Unit.FromMillimeter(1);

        var title = section.AddParagraph("Relatório Executivo de Postura");
        title.Format.Font.Size = 19;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Ink;

        var instrument = s.Type == PostureSnapshotType.Knight ? "AEGIS KNIGHT" : "AEGIS Score / NIST CSF";
        if (s.Type == PostureSnapshotType.Knight && !string.IsNullOrWhiteSpace(s.SourceLabel))
            instrument += $" · Fonte: {s.SourceLabel}";
        var sub = section.AddParagraph(instrument);
        sub.Format.Font.Size = 10.5;
        sub.Format.Font.Color = Accent;
        sub.Format.Font.Bold = true;
        sub.Format.SpaceAfter = Unit.FromMillimeter(3);
    }

    private static void AddMetadata(Section section, PostureSnapshot s)
    {
        var table = section.AddTable();
        table.Borders.Color = Line;
        table.Borders.Width = 0.25;
        table.LeftPadding = Unit.FromMillimeter(1.6);
        table.RightPadding = Unit.FromMillimeter(1.6);
        table.TopPadding = Unit.FromMillimeter(0.8);
        table.BottomPadding = Unit.FromMillimeter(0.8);
        // Dois pares chave/valor por linha.
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(5.1));
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(5.1));

        var pairs = new List<(string K, string V)>
        {
            ("Instrumento", s.Type == PostureSnapshotType.Knight ? "AEGIS KNIGHT" : "AEGIS Score / NIST CSF"),
            ("Data da fotografia (UTC)", s.CapturedAt.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss 'UTC'", Pt)),
            ("Score", ScoreText(s.Score)),
            ("Cobertura", Percent(s.Coverage)),
            ("Itens avaliados", s.EvaluatedItems.ToString(Pt)),
            ("Itens elegíveis", s.EligibleItems.ToString(Pt)),
            ("Pontos obtidos", s.AchievedPoints.ToString(Pt)),
            ("Pontos possíveis", $"{s.PossiblePoints.ToString(Pt)} (elegível {s.EligiblePoints.ToString(Pt)})"),
            ("Versão da fórmula", s.FormulaVersion),
            ("Versão do catálogo", s.CatalogVersion),
            ("Versão do schema", s.SchemaVersion),
            ("Recência dos dados", s.DataRecency is { } dr ? dr.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss 'UTC'", Pt) : "—"),
        };
        if (s.Type == PostureSnapshotType.Knight && !string.IsNullOrWhiteSpace(s.SourceLabel))
            pairs.Insert(1, ("Fonte KNIGHT", s.SourceLabel!));

        for (var i = 0; i < pairs.Count; i += 2)
        {
            var row = table.AddRow();
            FillKv(row, 0, pairs[i]);
            if (i + 1 < pairs.Count) FillKv(row, 2, pairs[i + 1]);
        }

        // Contagens por estado — uma faixa dedicada.
        var counts = section.AddParagraph();
        counts.Format.SpaceBefore = Unit.FromMillimeter(2.2);
        counts.Format.Font.Size = 9;
        counts.AddFormattedText("Contagens por estado:  ", TextFormat.Bold);
        counts.AddText(CountsLine(s));

        // Snapshot ID e hash completos (largura total, monoespaçado visual por rótulo).
        AddIdAndHash(section, "Snapshot ID", s.Id.ToString("D"));
        AddIdAndHash(section, "Hash SHA-256", s.ContentHash);

        var note = section.AddParagraph(
            "Relatório derivado exclusivamente de uma fotografia imutável (append-only) da postura. Os números " +
            "refletem o instante da captura; \"não avaliado\" é sempre distinto de \"não conforme\" e de 0%.");
        note.Format.Font.Size = 7.5;
        note.Format.Font.Color = Muted;
        note.Format.Font.Italic = true;
        note.Format.SpaceBefore = Unit.FromMillimeter(1.8);
        note.Format.SpaceAfter = Unit.FromMillimeter(3);
    }

    private static void FillKv(Row row, int col, (string K, string V) kv)
    {
        row.Cells[col].Shading.Color = Zebra;
        var k = row.Cells[col].AddParagraph(kv.K);
        k.Format.Font.Size = 7.5;
        k.Format.Font.Color = Muted;
        var v = row.Cells[col + 1].AddParagraph(kv.V);
        v.Format.Font.Size = 9;
        v.Format.Font.Bold = true;
    }

    private static void AddIdAndHash(Section section, string label, string value)
    {
        var p = section.AddParagraph();
        p.Format.SpaceBefore = Unit.FromMillimeter(1.2);
        p.Format.Font.Size = 8.5;
        p.AddFormattedText(label + ":  ", TextFormat.Bold);
        p.AddText(value);   // Snapshot ID / hash de 64 chars — o layout quebra na largura da página
    }

    // ---- Resumo executivo determinístico -------------------------------------------------------------

    private static void AddExecutiveSummary(Section section, PostureSnapshot s)
    {
        Heading(section, "Resumo Executivo");
        var p = section.AddParagraph();
        p.Format.Font.Size = 9;
        p.Format.SpaceAfter = Unit.FromMillimeter(3);

        if (s.Type == PostureSnapshotType.Knight)
        {
            var source = string.IsNullOrWhiteSpace(s.SourceLabel) ? "não informada" : s.SourceLabel!;
            p.AddText(
                $"No instante da captura, o assessment do AEGIS KNIGHT (fonte {source}) apresentava " +
                $"{(s.Score is null ? "score não avaliado" : $"score {Num(s.Score.Value)}")} e cobertura de {Percent(s.Coverage)} " +
                $"sobre {s.EligibleItems} indicadores aplicáveis. Dos {s.EvaluatedItems} avaliados: " +
                $"{s.CompliantCount} conformes, {s.NonCompliantCount} expostos e {s.MitigatedCount} mitigados; " +
                $"{s.NotEvaluatedCount} não avaliados e {s.ErrorCount} com erro de coleta. " +
                "O veredito de cada indicador é determinístico (regras), sem decisão por IA.");
        }
        else
        {
            p.AddText(
                $"No instante da captura, a postura AEGIS Score/NIST apresentava " +
                $"{(s.Score is null ? "score não avaliado (nenhum controle avaliado)" : $"score de {Percent(s.Score.Value)}")} " +
                $"e cobertura de {Percent(s.Coverage)} sobre {s.EligibleItems} controles elegíveis do catálogo {s.CatalogVersion}. " +
                $"Dos {s.EvaluatedItems} controles avaliados: {s.CompliantCount} conformes, {s.NonCompliantCount} não conformes " +
                $"e {s.MitigatedCount} mitigados por terceiro; {s.NotEvaluatedCount} permanecem sem avaliação (sem score, não " +
                $"confundidos com não conformidade). Pontuação: {s.AchievedPoints} de {s.PossiblePoints} pontos avaliados " +
                $"({s.EligiblePoints} elegíveis). Vereditos determinísticos por telemetria e análise documental.");
        }
    }

    // ---- Corpo AEGIS Score / NIST --------------------------------------------------------------------

    private static void AddAegisBody(Section section, PostureSnapshot s)
    {
        Heading(section, "Visão por Função (NIST CSF)");
        var byFunction = s.Controls
            .GroupBy(c => c.FunctionCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var ft = section.AddTable();
        StyleTable(ft);
        ft.AddColumn(Unit.FromCentimeter(4.2)); // Função
        ft.AddColumn(Unit.FromCentimeter(2.6)); // Avaliados/Total
        ft.AddColumn(Unit.FromCentimeter(2.6)); // Pontos
        ft.AddColumn(Unit.FromCentimeter(2.2)); // Conformes
        ft.AddColumn(Unit.FromCentimeter(2.4)); // Não conformes
        ft.AddColumn(Unit.FromCentimeter(3.0)); // Mitig./Não aval.
        HeaderRow(ft, "Função", "Avaliados", "Pontos", "Conformes", "Não conf.", "Mitig. / N.aval.");

        foreach (var code in NistFunctionOrder)
        {
            if (!byFunction.TryGetValue(code, out var items)) continue;
            AddFunctionRow(ft, code, items);
        }
        // Funções fora da ordem canônica (defensivo), preservando estabilidade.
        foreach (var code in byFunction.Keys.Where(k => !NistFunctionOrder.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            AddFunctionRow(ft, code, byFunction[code]);

        Heading(section, "Controles avaliados e não avaliados");
        if (s.Controls.Count == 0)
        {
            var none = section.AddParagraph("Nenhum controle no catálogo ativo.");
            none.Format.Font.Color = Muted;
            return;
        }

        var table = section.AddTable();
        StyleTable(table);
        table.AddColumn(Unit.FromCentimeter(1.3)); // Função
        table.AddColumn(Unit.FromCentimeter(2.3)); // Subcategoria
        table.AddColumn(Unit.FromCentimeter(2.4)); // Estado
        table.AddColumn(Unit.FromCentimeter(1.9)); // Pontos
        table.AddColumn(Unit.FromCentimeter(2.0)); // Veredito
        table.AddColumn(Unit.FromCentimeter(2.6)); // Avaliado em
        table.AddColumn(Unit.FromCentimeter(4.5)); // Evidência
        HeaderRow(table, "Função", "Subcategoria", "Estado", "Pontos", "Veredito", "Avaliado em (UTC)", "Referências de evidência");

        var zebra = false;
        foreach (var c in s.Controls.OrderBy(c => c.SubcategoryCode, StringComparer.Ordinal))
        {
            var row = table.AddRow();
            if (zebra) ShadeRow(row);
            zebra = !zebra;

            Cell(row, 0, c.FunctionCode);
            Cell(row, 1, c.SubcategoryCode, bold: true);
            Cell(row, 2, c.Evaluated ? ControlStatusText(c.Status) : "Não avaliado", color: c.Evaluated ? (Color?)null : Muted);
            Cell(row, 3, $"{c.AchievedPoints} / {c.MaxPoints}", align: ParagraphAlignment.Right);
            Cell(row, 4, c.Evaluated ? VerdictText(c.VerdictSource) : "—");
            Cell(row, 5, c.EvaluatedAt is { } at ? at.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", Pt) : "—");

            var evCell = row.Cells[6];
            if (c.EvidenceRefs.Count == 0)
            {
                var e = evCell.AddParagraph("—");
                e.Format.Font.Color = Muted;
            }
            else
            {
                foreach (var e in c.EvidenceRefs
                    .OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Reference, StringComparer.Ordinal))
                {
                    var ep = evCell.AddParagraph($"[{EvidenceKind(e.Kind)}] {e.Source} / {e.Reference}");
                    ep.Format.Font.Size = 7;
                }
            }
        }
    }

    private static void AddFunctionRow(Table ft, string code, List<PostureSnapshotControl> items)
    {
        var evaluated = items.Count(i => i.Evaluated);
        var achieved = items.Sum(i => i.AchievedPoints);
        var max = items.Sum(i => i.MaxPoints);
        var compliant = items.Count(i => i.Status == ControlStatus.Compliant);
        var nonCompliant = items.Count(i => i.Status == ControlStatus.NonCompliant);
        var mitigated = items.Count(i => i.Status == ControlStatus.MitigatedByThirdParty);
        var notEval = items.Count(i => !i.Evaluated);

        var row = ft.AddRow();
        var name = NistFunctionName.TryGetValue(code, out var n) ? $"{code} · {n}" : code;
        Cell(row, 0, name, bold: true);
        Cell(row, 1, $"{evaluated} / {items.Count}", align: ParagraphAlignment.Center);
        Cell(row, 2, $"{achieved} / {max}", align: ParagraphAlignment.Center);
        Cell(row, 3, compliant.ToString(Pt), align: ParagraphAlignment.Center);
        Cell(row, 4, nonCompliant.ToString(Pt), align: ParagraphAlignment.Center);
        Cell(row, 5, $"{mitigated} / {notEval}", align: ParagraphAlignment.Center);
    }

    // ---- Corpo AEGIS KNIGHT --------------------------------------------------------------------------

    private static void AddKnightBody(Section section, PostureSnapshot s)
    {
        Heading(section, "Resumo por estado e severidade");
        var status = section.AddParagraph();
        status.Format.Font.Size = 9;
        status.AddFormattedText("Por estado:  ", TextFormat.Bold);
        status.AddText(CountsLine(s));

        var sev = section.AddParagraph();
        sev.Format.Font.Size = 9;
        sev.Format.SpaceAfter = Unit.FromMillimeter(2);
        sev.AddFormattedText("Por severidade:  ", TextFormat.Bold);
        sev.AddText(SeverityLine(s.Indicators));

        Heading(section, "Indicadores");
        if (s.Indicators.Count == 0)
        {
            var none = section.AddParagraph("Nenhum indicador na fotografia.");
            none.Format.Font.Color = Muted;
            return;
        }

        var table = section.AddTable();
        StyleTable(table);
        table.AddColumn(Unit.FromCentimeter(2.7)); // Indicador (id + categoria + coleta)
        table.AddColumn(Unit.FromCentimeter(3.4)); // Título
        table.AddColumn(Unit.FromCentimeter(1.7)); // Severidade
        table.AddColumn(Unit.FromCentimeter(1.7)); // Status
        table.AddColumn(Unit.FromCentimeter(1.1)); // Afetados
        table.AddColumn(Unit.FromCentimeter(3.1)); // Evidência
        table.AddColumn(Unit.FromCentimeter(3.3)); // NIST / MITRE
        HeaderRow(table, "Indicador", "Título", "Severidade", "Status", "Afet.", "Evidência", "NIST / MITRE");

        var zebra = false;
        foreach (var i in s.Indicators.OrderBy(x => x.IndicatorId, StringComparer.Ordinal))
        {
            var row = table.AddRow();
            if (zebra) ShadeRow(row);
            zebra = !zebra;

            var idCell = row.Cells[0];
            var idP = idCell.AddParagraph(i.IndicatorId);
            idP.Format.Font.Bold = true;
            idP.Format.Font.Size = 8;
            var catP = idCell.AddParagraph(KnightCategoryText(i.Category));
            catP.Format.Font.Size = 6.5;
            catP.Format.Font.Color = Muted;
            var colP = idCell.AddParagraph("coleta " + i.CollectedAt.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", Pt));
            colP.Format.Font.Size = 6.5;
            colP.Format.Font.Color = Muted;

            Cell(row, 1, i.Title);
            Cell(row, 2, SeverityText(i.Severity));
            Cell(row, 3, KnightStatusText(i.Status), color: i.Status == KnightIndicatorStatus.Exposed ? new Color(150, 30, 50) : (Color?)null);
            Cell(row, 4, i.AffectedObjectCount.ToString(Pt), align: ParagraphAlignment.Center);
            Cell(row, 5, string.IsNullOrWhiteSpace(i.Evidence) ? "—" : i.Evidence);

            var mapCell = row.Cells[6];
            var nist = mapCell.AddParagraph("NIST: " + (i.NistCodes.Count > 0 ? string.Join(", ", i.NistCodes) : "—"));
            nist.Format.Font.Size = 7;
            var mitre = mapCell.AddParagraph("MITRE: " + (i.MitreTechniques.Count > 0 ? string.Join(", ", i.MitreTechniques) : "—"));
            mitre.Format.Font.Size = 7;
        }
    }

    // ---- Utilidades de layout ------------------------------------------------------------------------

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

    private static void StyleTable(Table table)
    {
        table.Borders.Color = Line;
        table.Borders.Width = 0.25;
        table.LeftPadding = Unit.FromMillimeter(1.3);
        table.RightPadding = Unit.FromMillimeter(1.3);
        table.TopPadding = Unit.FromMillimeter(0.7);
        table.BottomPadding = Unit.FromMillimeter(0.7);
    }

    private static void HeaderRow(Table table, params string[] headers)
    {
        var row = table.AddRow();
        row.HeadingFormat = true;
        row.Shading.Color = HeaderShade;
        for (var i = 0; i < headers.Length; i++)
        {
            var p = row.Cells[i].AddParagraph(headers[i]);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 7.5;
            p.Format.Font.Color = Colors.White;
        }
    }

    private static void ShadeRow(Row row)
    {
        for (var i = 0; i < row.Cells.Count; i++) row.Cells[i].Shading.Color = Zebra;
    }

    private static void Cell(Row row, int col, string text, bool bold = false, ParagraphAlignment align = ParagraphAlignment.Left, Color? color = null)
    {
        var p = row.Cells[col].AddParagraph(text ?? "");
        p.Format.Font.Size = 7.8;
        p.Format.Font.Bold = bold;
        p.Format.Alignment = align;
        if (color is { } c) p.Format.Font.Color = c;
    }

    private static void AddFooter(Section section)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Muted;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText("AEGIS · Relatório executivo derivado de fotografia imutável de postura · página ");
        footer.AddPageField();
        footer.AddText(" de ");
        footer.AddNumPagesField();
    }

    // ---- Texto pt-BR determinístico ------------------------------------------------------------------

    private static string ScoreText(double? score) => score is null ? "Não avaliado" : Percent(score.Value);
    private static string Percent(double value) => Num(value) + "%";
    private static string Num(double value) => value.ToString("0.#", Pt);

    private static string CountsLine(PostureSnapshot s) =>
        $"Conformes {s.CompliantCount} · Não conformes {s.NonCompliantCount} · Mitigados {s.MitigatedCount} · " +
        $"Não avaliados {s.NotEvaluatedCount}" +
        (s.Type == PostureSnapshotType.Knight ? $" · Erros {s.ErrorCount} · Não aplicáveis {s.NotApplicableCount}" : "");

    private static string SeverityLine(IEnumerable<PostureSnapshotIndicator> indicators)
    {
        var g = indicators.GroupBy(i => i.Severity).ToDictionary(x => x.Key, x => x.Count());
        int C(SeverityLevel l) => g.TryGetValue(l, out var v) ? v : 0;
        return $"Crítica {C(SeverityLevel.Critical)} · Alta {C(SeverityLevel.High)} · Média {C(SeverityLevel.Medium)} · " +
               $"Baixa {C(SeverityLevel.Low)} · Informativa {C(SeverityLevel.Informational)}";
    }

    private static string ControlStatusText(ControlStatus? status) => status switch
    {
        ControlStatus.Compliant => "Conforme",
        ControlStatus.NonCompliant => "Não conforme",
        ControlStatus.MitigatedByThirdParty => "Mitigado por terceiro",
        _ => "Não avaliado",
    };

    private static string VerdictText(VerdictSource? source) => source switch
    {
        VerdictSource.Telemetry => "Telemetria",
        VerdictSource.Documentary => "Documental",
        _ => "—",
    };

    private static string EvidenceKind(string kind) => kind switch
    {
        "telemetry" => "telemetria",
        "document" => "documento",
        _ => kind,
    };

    private static string KnightStatusText(KnightIndicatorStatus status) => status switch
    {
        KnightIndicatorStatus.Passed => "Conforme",
        KnightIndicatorStatus.Exposed => "Exposto",
        KnightIndicatorStatus.Mitigated => "Mitigado",
        KnightIndicatorStatus.NotEvaluated => "Não avaliado",
        KnightIndicatorStatus.Error => "Erro",
        KnightIndicatorStatus.NotApplicable => "Não aplicável",
        _ => status.ToString(),
    };

    private static string KnightCategoryText(KnightIndicatorCategory category) => category switch
    {
        KnightIndicatorCategory.PrivilegedAccess => "Acesso privilegiado",
        KnightIndicatorCategory.IdentityGovernance => "Governança de identidade",
        KnightIndicatorCategory.AccountHygiene => "Higiene de contas",
        _ => category.ToString(),
    };

    private static string SeverityText(SeverityLevel level) => level switch
    {
        SeverityLevel.Critical => "Crítica",
        SeverityLevel.High => "Alta",
        SeverityLevel.Medium => "Média",
        SeverityLevel.Low => "Baixa",
        SeverityLevel.Informational => "Informativa",
        _ => level.ToString(),
    };

    private static string InitializeFonts()
    {
        var resolver = PdfReportFontResolver.TryCreate();
        if (resolver is not null)
        {
            GlobalFontSettings.FontResolver = resolver;
            return PdfReportFontResolver.FamilyName;
        }
        // Ambiente sem os arquivos de fonte usuais: recorre à leitura direta das fontes do Windows (sem GDI).
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        return "Arial";
    }
}
