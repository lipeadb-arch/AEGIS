using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Application.Nist;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] CSV ESTRUTURADO da fotografia de maturidade NIST — escritor PURO, derivado só do relatório
/// congelado. Uma tabela única (UTF-8 com BOM, ';'), com a coluna <c>Registro</c> dizendo o tipo de cada linha: Resumo,
/// Função, Subcategoria, Procedimento, Evidência e Achado. Toda linha leva fotografia, hash, avaliação, rodada e escopo; toda
/// linha de subcategoria é identificável por código E título. Texto passa pelo mesmo neutralizador de fórmula das demais
/// exportações. Contagens reconciliam com o HTML e o PDF: uma linha "Subcategoria" por subcategoria do catálogo, uma
/// "Achado" por achado, uma "Evidência" por vínculo vigente e uma "Procedimento" por procedimento.
/// </summary>
public static class NistReportCsvWriter
{
    public static readonly string[] Headers =
    {
        "Registro", "Fotografia", "Hash do conteúdo", "Cliente", "Avaliação", "Rodada", "Período (início)", "Período (fim)", "Escopo",
        "Metodologia", "Catálogo", "Código", "Título", "Função", "Categoria", "Situação", "Atual", "Alvo", "Lacuna", "Não se aplica",
        "Revisão", "Responsável", "Avaliador", "Revisor", "Registrado por", "Registrado em", "Origem do conteúdo", "Justificativa",
        "Observações (atual)", "Observações (alvo)", "Lacunas", "Risco ou impacto", "Orientação de melhoria",
        "Método", "Procedimento", "Andamento", "Conclusão", "Observação", "Data de realização",
        "Evidência", "Origem da evidência", "Data na origem", "Escopo da coleta", "Vinculada por",
        "Achado", "Condição observada", "Risco", "Impacto", "Severidade", "Justificativa da severidade", "Prioridade",
        "Justificativa da prioridade", "Recomendação", "Situação do achado", "Plano", "Etapa do plano", "Responsável do plano",
        "Prazo do plano", "Validação do plano", "Tratamento", "Média atual", "Média alvo", "Lacuna média", "Cobertura (%)",
        "Avaliadas", "Não se aplicam", "Aguardando confirmação", "Sem avaliação",
    };

    public static byte[] Write(PostureSnapshot snapshot, NistMaturityReport r)
    {
        var csv = new CsvBuilder();
        foreach (var h in Headers) csv.Text(h);
        csv.EndRow();

        var row = new Row(csv, snapshot, r);
        var s = r.Summary;
        row.Start("Resumo", null, "Rodada inteira", null, null)
           .Sys("Média atual", Num(s.Current)).Sys("Média alvo", Num(s.Target)).Sys("Lacuna média", Num(s.Gap))
           .Sys("Cobertura (%)", s.Coverage.ToString("0.#", CultureInfo.InvariantCulture))
           .Sys("Avaliadas", Int(s.Evaluated)).Sys("Não se aplicam", Int(s.NotApplicable))
           .Sys("Aguardando confirmação", Int(s.PendingConfirmation)).Sys("Sem avaliação", Int(s.NotEvaluated + s.InProgress))
           // A metodologia CONGELADA na fotografia (autoral do AEGIS, não exigência do NIST) acompanha os números.
           .Set("Observação", r.Methodology.Statement)
           .End();

        // [AEGIS-NIST-AI-ASSIST-01] Resumo executivo aceito: uma linha por seção, com a procedência (só quando existe).
        if (r.Interpretation is { } it)
            foreach (var sec in it.Sections)
                row.Start("Interpretação", null, sec.Title, null, null)
                   .Set("Observação", sec.Text).Set("Origem do conteúdo", it.Describe())
                   .End();

        foreach (var f in r.Functions)
            row.Start("Função", f.Code, f.Name, f.Code, null)
               .Sys("Média atual", Num(f.Current)).Sys("Média alvo", Num(f.Target)).Sys("Lacuna média", Num(f.Gap))
               .Sys("Avaliadas", Int(f.Evaluated)).Sys("Não se aplicam", Int(f.NotApplicable))
               .Sys("Aguardando confirmação", Int(f.PendingConfirmation)).Sys("Sem avaliação", Int(f.NotEvaluated + f.InProgress))
               .End();

        foreach (var x in r.Subcategories)
        {
            row.Start("Subcategoria", x.Code, x.Title, x.FunctionCode, x.CategoryCode)
               .Set("Situação", x.StateLabel).Sys("Atual", Int(x.CurrentLevel)).Sys("Alvo", Int(x.TargetLevel)).Sys("Lacuna", Int(x.Gap))
               .Set("Não se aplica", x.NotApplicable ? "Sim" : "Não").Set("Revisão", x.ReviewStateLabel)
               .Set("Responsável", Person(x.Owner)).Set("Avaliador", x.AssessorName).Set("Revisor", x.ReviewerName)
               .Set("Registrado por", x.RecordedByName).Sys("Registrado em", Iso(x.RecordedAt))
               .Set("Origem do conteúdo", NistLabels.ContentOrigin(x.ContentOrigin) + (x.OriginNote is null ? "" : " — " + x.OriginNote)
                    + (x.Assistance is { } xa ? " · Conteúdo assistido por IA: " + xa.Describe() : ""))
               .Set("Justificativa", x.Rationale).Set("Observações (atual)", x.CurrentComments).Set("Observações (alvo)", x.TargetComments)
               .Set("Lacunas", x.Gaps).Set("Risco ou impacto", x.RiskImpact).Set("Orientação de melhoria", x.ImprovementGuidance)
               .End();
            foreach (var p in x.Procedures)
                row.Start("Procedimento", x.Code, x.Title, x.FunctionCode, x.CategoryCode)
                   .Set("Método", p.MethodLabel).Set("Procedimento", p.Procedure).Set("Andamento", p.StatusLabel)
                   .Set("Conclusão", p.OutcomeLabel).Set("Observação", p.Observation)
                   .Sys("Data de realização", p.PerformedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                   .Set("Registrado por", p.RecordedByName).Sys("Registrado em", Iso(p.RecordedAt))
                   .Set("Evidência", Titles(x, p.EvidenceIds))
                   .End();
            foreach (var e in x.Evidence)
                row.Start("Evidência", x.Code, x.Title, x.FunctionCode, x.CategoryCode)
                   .Set("Evidência", e.Title + (e.Uri is null ? "" : " <" + e.Uri + ">"))
                   .Set("Origem da evidência", e.OriginKindLabel + (e.OriginLabel is null ? "" : " — " + e.OriginLabel))
                   .Sys("Data na origem", Iso(e.CollectedAt)).Set("Escopo da coleta", e.OriginScope)
                   .Set("Vinculada por", e.RecordedByName).Set("Observação", e.Notes)
                   .End();
        }

        foreach (var f in r.Findings)
        {
            var sub = r.Subcategories.FirstOrDefault(x => x.Code == f.SubcategoryCode);
            row.Start("Achado", f.SubcategoryCode, f.SubcategoryTitle, sub?.FunctionCode, sub?.CategoryCode)
               .Set("Achado", f.Title).Set("Condição observada", f.Condition).Set("Risco", f.Risk).Set("Impacto", f.Impact)
               .Set("Severidade", f.SeverityLabel).Set("Justificativa da severidade", f.SeverityRationale)
               .Set("Prioridade", f.PriorityLabel).Set("Justificativa da prioridade", f.PriorityRationale)
               .Set("Recomendação", f.Recommendation)
               .Set("Situação do achado", f.StatusLabel + (f.StatusNote is null ? "" : " — " + f.StatusNote))
               .Set("Plano", f.Plan?.Title).Set("Etapa do plano", f.Plan?.StatusLabel)
               .Set("Responsável do plano", f.Plan is null ? null : Person(f.Plan.Responsible))
               .Sys("Prazo do plano", f.Plan?.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
               .Set("Validação do plano", f.Plan?.ValidationOutcomeLabel is null ? null
                   : $"{f.Plan.ValidationMethodLabel} — {f.Plan.ValidationOutcomeLabel}" + (f.Plan.ValidationEvidenceReference is null ? "" : $" ({f.Plan.ValidationEvidenceReference})"))
               .Set("Tratamento", f.TreatmentLabel)
               .Set("Evidência", sub is null ? null : Titles(sub, f.EvidenceIds))
               .Set("Registrado por", f.CreatedByName).Sys("Registrado em", Iso(f.CreatedAt))
               .Set("Origem do conteúdo", f.Assistance is { } fa ? "Conteúdo assistido por IA: " + fa.Describe() : null)
               .End();
        }

        return csv.ToUtf8WithBom();
    }

    private static string Titles(NistReportSubcategory sub, IEnumerable<Guid> ids) =>
        string.Join(" | ", ids.Select(id => sub.Evidence.FirstOrDefault(e => e.Id == id)?.Title ?? $"evidência {id:D} (não vigente na fotografia)"));

    private static string? Person(NistReportPerson p) => p.Name is null ? null
        : p.Kind switch
        {
            "User" => p.Name + " (usuário)",
            "External" => p.Name + " (externo" + (p.Contact is null ? ")" : ", " + p.Contact + ")"),
            _ => p.Name,
        };

    private static string? Num(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture);
    private static string? Int(int? v) => v?.ToString(CultureInfo.InvariantCulture);
    private static string? Iso(DateTimeOffset? v) => v is { } x ? PostureSnapshotCsvWriter.Iso(x) : null;

    /// <summary>Uma linha da tabela, preenchida por NOME de coluna (as demais ficam vazias) — a ordem é a de <see cref="Headers"/>.</summary>
    private sealed class Row
    {
        private readonly CsvBuilder _csv;
        private readonly PostureSnapshot _s;
        private readonly NistMaturityReport _r;
        private readonly Dictionary<string, (string? Value, bool System)> _cells = new(StringComparer.Ordinal);

        public Row(CsvBuilder csv, PostureSnapshot s, NistMaturityReport r)
        {
            _csv = csv;
            _s = s;
            _r = r;
        }

        public Row Start(string kind, string? code, string? title, string? fn, string? category)
        {
            _cells.Clear();
            Set("Registro", kind).Set("Fotografia", _s.Id.ToString("D")).Set("Hash do conteúdo", _s.ContentHash)
                .Set("Cliente", _r.Client.Name).Set("Avaliação", _r.Assessment.Name).Set("Rodada", _r.Cycle.Name)
                .Sys("Período (início)", _r.Cycle.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Sys("Período (fim)", _r.Cycle.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Set("Escopo", _r.Scope.Name).Set("Metodologia", _r.Methodology.Version).Set("Catálogo", _r.Catalog.FrameworkName)
                .Set("Código", code).Set("Título", title).Set("Função", fn).Set("Categoria", category);
            return this;
        }

        /// <summary>Texto (de pessoa ou de origem): sempre neutralizado contra fórmula.</summary>
        public Row Set(string column, string? value) => Put(column, value, system: false);

        /// <summary>Número ou data GERADOS pelo sistema (formato invariante): escapados, sem neutralizar o sinal.</summary>
        public Row Sys(string column, string? value) => Put(column, value, system: true);

        private Row Put(string column, string? value, bool system)
        {
            if (Array.IndexOf(Headers, column) < 0) throw new ArgumentException($"Coluna desconhecida: {column}");
            _cells[column] = (value, system);
            return this;
        }

        public void End()
        {
            foreach (var h in Headers)
            {
                if (!_cells.TryGetValue(h, out var c)) _csv.Text(null);
                else if (c.System) _csv.SystemValue(c.Value);
                else _csv.Text(c.Value);
            }
            _csv.EndRow();
        }
    }
}
