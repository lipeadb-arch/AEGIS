using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

/// <summary>
/// [AEGIS-AUD-034] Escritor CSV PURO (sem EF/rede/relógio) da fotografia imutável de postura. Produz um arquivo
/// UTF-8 com BOM, delimitador <c>;</c>, números em formato INVARIANTE, timestamps em ISO 8601 (UTC) e uma linha por
/// controle NIST (AEGIS Score) ou por indicador (KNIGHT). O schema é ESTÁVEL e previsível: colunas fixas, itens
/// ordenados de forma determinística (código/id ordinal).
///
/// Segurança: TODA célula textual é protegida contra CSV/Formula Injection — um texto que comece (após espaços/
/// controles ignorados pelo Excel) por <c>= + - @</c>, ou por TAB/CR/LF, é neutralizado com prefixo <c>'</c> antes
/// de qualquer aspa. Números e timestamps gerados pelo SISTEMA não passam pelo neutralizador (não são texto externo)
/// — nunca começam por caractere perigoso. Aspas, delimitador, CR/LF e Unicode são escapados corretamente.
/// </summary>
public static class PostureSnapshotCsvWriter
{
    public static byte[] Write(PostureSnapshot snapshot)
    {
        var csv = new CsvBuilder();
        if (snapshot.Type == PostureSnapshotType.Knight)
            WriteKnight(csv, snapshot);
        else
            WriteAegis(csv, snapshot);
        return csv.ToUtf8WithBom();
    }

    // ---- AEGIS Score / NIST — uma linha por controle (do catálogo ATIVO completo) --------------------

    private static void WriteAegis(CsvBuilder csv, PostureSnapshot s)
    {
        csv.Text("SnapshotId").Text("ContentHash").Text("Type").Timestamp("CapturedAt")
           .Text("SchemaVersion").Text("FormulaVersion").Text("CatalogVersion")
           .Text("EvaluationState").Text("Score").Text("Coverage")
           .Text("FunctionCode").Text("SubcategoryCode").Text("Evaluated").Text("Status")
           .Text("AchievedPoints").Text("MaxPoints").Text("VerdictSource").Text("EvaluatedAt")
           .Text("EvidenceRefs").EndRow();

        var state = EvaluationState(s.Score);
        foreach (var c in s.Controls.OrderBy(c => c.SubcategoryCode, StringComparer.Ordinal))
        {
            csv.Text(s.Id.ToString("D")).Text(s.ContentHash).Text(s.Type.ToString()).TimestampValue(s.CapturedAt)
               .Text(s.SchemaVersion).Text(s.FormulaVersion).Text(s.CatalogVersion)
               .Text(state).Number(s.Score).Number(s.Coverage)
               .Text(c.FunctionCode).Text(c.SubcategoryCode).Bool(c.Evaluated)
               .Text(c.Evaluated ? c.Status?.ToString() ?? "" : "NotEvaluated")
               .Number(c.AchievedPoints).Number(c.MaxPoints)
               .Text(c.VerdictSource?.ToString()).TimestampValue(c.EvaluatedAt)
               .Text(FormatEvidence(c.EvidenceRefs)).EndRow();
        }
    }

    // ---- AEGIS KNIGHT — uma linha por indicador ------------------------------------------------------

    private static void WriteKnight(CsvBuilder csv, PostureSnapshot s)
    {
        csv.Text("SnapshotId").Text("ContentHash").Text("Type").Timestamp("CapturedAt")
           .Text("SchemaVersion").Text("FormulaVersion").Text("CatalogVersion")
           .Text("EvaluationState").Text("Score").Text("Coverage")
           .Text("SourceType").Text("SourceLabel")
           .Text("IndicatorId").Text("Title").Text("Category").Text("Severity").Text("Status")
           .Text("AffectedObjectCount").Timestamp("CollectedAt")
           .Text("NistCodes").Text("MitreTechniques").Text("Evidence").EndRow();

        var state = EvaluationState(s.Score);
        foreach (var i in s.Indicators.OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
        {
            csv.Text(s.Id.ToString("D")).Text(s.ContentHash).Text(s.Type.ToString()).TimestampValue(s.CapturedAt)
               .Text(s.SchemaVersion).Text(s.FormulaVersion).Text(s.CatalogVersion)
               .Text(state).Number(s.Score).Number(s.Coverage)
               .Text(s.SourceType?.ToString()).Text(s.SourceLabel)
               .Text(i.IndicatorId).Text(i.Title).Text(i.Category.ToString()).Text(i.Severity.ToString()).Text(i.Status.ToString())
               .Number(i.AffectedObjectCount).TimestampValue(i.CollectedAt)
               .Text(string.Join(" ", i.NistCodes)).Text(string.Join(" ", i.MitreTechniques)).Text(i.Evidence)
               .EndRow();
        }
    }

    private static string EvaluationState(double? score) => score is null ? "NotEvaluated" : "Evaluated";

    /// <summary>Referências de evidência sanitizadas numa única célula (uma por linha), legível e sem conteúdo bruto.</summary>
    private static string FormatEvidence(IEnumerable<PostureEvidenceRef> refs)
    {
        var lines = refs
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Reference, StringComparer.Ordinal)
            .Select(e =>
            {
                var at = e.CollectedAt is { } dt ? " @ " + Iso(dt) : "";
                return $"[{e.Kind}] {e.Source} / {e.Reference}{at}";
            })
            .ToList();
        return string.Join("\n", lines);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Montador de CSV com escaping correto (aspas, delimitador, CR/LF) e neutralização de Formula Injection nas
    /// células TEXTUAIS. Cada célula do sistema (número/timestamp/booleano) usa um caminho próprio que nunca é
    /// confundido com texto externo sanitizado.
    /// </summary>
    private sealed class CsvBuilder
    {
        public const char Delimiter = ';';
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly StringBuilder _sb = new(8192);
        private bool _firstInRow = true;

        /// <summary>Célula TEXTUAL (possivelmente externa): neutraliza fórmula e escapa aspas/delimitador/CR-LF.</summary>
        public CsvBuilder Text(string? value)
        {
            Separate();
            _sb.Append(Encode(Neutralize(value ?? "")));
            return this;
        }

        /// <summary>Célula NUMÉRICA do sistema, formato invariante. Vazia quando o score anulável é nulo (nunca "0").</summary>
        public CsvBuilder Number(double? value)
        {
            Separate();
            if (value is { } v) _sb.Append(Encode(v.ToString(Inv)));
            return this;
        }

        public CsvBuilder Number(int value)
        {
            Separate();
            _sb.Append(Encode(value.ToString(Inv)));
            return this;
        }

        public CsvBuilder Bool(bool value)
        {
            Separate();
            _sb.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>Célula de cabeçalho para uma coluna de timestamp (o rótulo é textual e neutralizado).</summary>
        public CsvBuilder Timestamp(string header) => Text(header);

        /// <summary>Valor de timestamp do sistema em ISO 8601 (UTC). Vazio quando nulo. Nunca começa por caractere perigoso.</summary>
        public CsvBuilder TimestampValue(DateTimeOffset? value)
        {
            Separate();
            if (value is { } v) _sb.Append(Encode(Iso(v)));
            return this;
        }

        public CsvBuilder EndRow()
        {
            _sb.Append("\r\n");
            _firstInRow = true;
            return this;
        }

        public byte[] ToUtf8WithBom()
        {
            var body = Encoding.UTF8.GetBytes(_sb.ToString());
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var buffer = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, buffer, 0, bom.Length);
            Buffer.BlockCopy(body, 0, buffer, bom.Length, body.Length);
            return buffer;
        }

        private void Separate()
        {
            if (!_firstInRow) _sb.Append(Delimiter);
            _firstInRow = false;
        }

        /// <summary>Envolve em aspas (dobrando aspas internas) quando a célula contém aspa, delimitador ou quebra de linha.</summary>
        private static string Encode(string cell)
        {
            if (cell.IndexOf('"') < 0 && cell.IndexOf(Delimiter) < 0 && cell.IndexOf('\r') < 0 && cell.IndexOf('\n') < 0)
                return cell;
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// Neutraliza CSV/Formula Injection: um texto que comece por TAB/CR/LF, ou cujo primeiro caractere NÃO branco
        /// seja <c>= + - @</c>, ganha o prefixo <c>'</c> — o Excel/Sheets passa a tratar a célula como texto, não
        /// fórmula. Espaços à esquerda (que o Excel ignora antes de avaliar) e controles à esquerda são cobertos.
        /// </summary>
        private static string Neutralize(string v)
        {
            if (v.Length == 0) return v;
            if (v[0] == '\t' || v[0] == '\r' || v[0] == '\n') return "'" + v;
            var i = 0;
            while (i < v.Length && v[i] == ' ') i++;   // o Excel ignora espaços à esquerda antes de uma fórmula
            if (i >= v.Length) return v;
            var c = v[i];
            return c is '=' or '+' or '-' or '@' ? "'" + v : v;
        }
    }
}
