using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AegisScore.Application.Posture.Export;

namespace AegisScore.Application.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] CSV DE TRABALHO da jornada NIST — funções PURAS de escrita e leitura.
///
/// Escrita: uma linha por subcategoria do catálogo (código E título), valores vigentes e a VERSÃO de cada registro; UTF-8
/// com BOM, ';', toda célula de texto neutralizada contra fórmula (a mesma proteção das exportações).
///
/// Leitura: RFC 4180 (aspas, aspas duplas, quebras de linha dentro de aspas), delimitador ';' ou ',' detectado pelo
/// cabeçalho, BOM ignorado, e o apóstrofo de neutralização retirado quando protege um caractere de fórmula — o arquivo
/// exportado volta sem perder nem ganhar caracteres.
/// </summary>
public static class NistWorkingCsv
{
    public const string ColAssessment = "Avaliação";
    public const string ColCycle = "Rodada";
    public const string ColScope = "Escopo";
    public const string ColCode = "Código";
    public const string ColTitle = "Título";
    public const string ColFunction = "Função";
    public const string ColCategory = "Categoria";
    public const string ColState = "Situação";
    public const string ColCurrent = "Atual";
    public const string ColTarget = "Alvo";
    public const string ColNotApplicable = "Não se aplica";
    public const string ColRationale = "Justificativa";
    public const string ColCurrentComments = "Observações sobre a situação atual";
    public const string ColTargetComments = "Observações sobre o alvo";
    public const string ColGaps = "Lacunas observadas";
    public const string ColRisk = "Risco ou impacto";
    public const string ColGuidance = "Orientação de melhoria";
    public const string ColOwner = "Responsável (texto)";
    public const string ColVersion = "Versão";

    public static readonly string[] Headers =
    {
        ColAssessment, ColCycle, ColScope, ColCode, ColTitle, ColFunction, ColCategory, ColState, ColCurrent, ColTarget,
        ColNotApplicable, ColRationale, ColCurrentComments, ColTargetComments, ColGaps, ColRisk, ColGuidance, ColOwner, ColVersion,
    };

    /// <summary>Colunas sem as quais o arquivo não é identificável (código, rodada, escopo, versão).</summary>
    public static readonly string[] RequiredHeaders = { ColCycle, ColScope, ColCode, ColVersion };

    public const string UpdateRule =
        "Linha sem registro na rodada (versão vazia ou 0) cria um RASCUNHO importado. Linha com registro precisa trazer a versão " +
        "vigente: as células preenchidas substituem os valores; células VAZIAS mantêm o valor atual (a importação nunca apaga " +
        "campos nem registros). \"Não se aplica = Sim\" retira os níveis. Todo conteúdo importado fica \"aguardando confirmação " +
        "humana\" e fora das médias até ser confirmado na tela. Versão diferente da vigente é conflito e nada é gravado. Título, " +
        "função, categoria e situação servem para leitura e são ignorados na importação (o catálogo é a autoridade).";

    public sealed record Row(
        string Code, string Title, string FunctionCode, string CategoryCode, string StateLabel,
        int? Current, int? Target, bool NotApplicable, string? Rationale, string? CurrentComments, string? TargetComments,
        string? Gaps, string? RiskImpact, string? Guidance, string? Owner, int Version);

    public static byte[] Write(string assessmentName, string cycleName, string scopeName, IEnumerable<Row> rows)
    {
        var csv = new CsvBuilder();
        foreach (var h in Headers) csv.Text(h);
        csv.EndRow();
        foreach (var r in rows)
        {
            csv.Text(assessmentName).Text(cycleName).Text(scopeName).Text(r.Code).Text(r.Title).Text(r.FunctionCode).Text(r.CategoryCode)
               .Text(r.StateLabel)
               .SystemValue(r.Current?.ToString(CultureInfo.InvariantCulture)).SystemValue(r.Target?.ToString(CultureInfo.InvariantCulture))
               .Text(r.NotApplicable ? "Sim" : "Não")
               .Text(r.Rationale).Text(r.CurrentComments).Text(r.TargetComments).Text(r.Gaps).Text(r.RiskImpact).Text(r.Guidance).Text(r.Owner)
               .SystemValue(r.Version.ToString(CultureInfo.InvariantCulture))
               .EndRow();
        }
        return csv.ToUtf8WithBom();
    }

    /// <summary>Arquivo lido: cabeçalho normalizado e linhas (com o número da linha física de início).</summary>
    public sealed record ParsedFile(IReadOnlyList<string> Headers, IReadOnlyList<(int Line, IReadOnlyList<string> Cells)> Rows, IReadOnlyList<string> Errors);

    public static ParsedFile Parse(string text, int maxRows)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(text)) return new ParsedFile(Array.Empty<string>(), Array.Empty<(int, IReadOnlyList<string>)>(), new[] { "Arquivo vazio." });
        if (text[0] == '﻿') text = text[1..];

        var firstLineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];
        var delimiter = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';

        var records = new List<(int Line, List<string> Cells)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
                continue;
            }
            if (c == '"' && cell.Length == 0) { inQuotes = true; quoted = true; continue; }
            if (c == delimiter) { cells.Add(Unneutralize(cell.ToString(), quoted)); cell.Clear(); quoted = false; continue; }
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cells.Add(Unneutralize(cell.ToString(), quoted));
                cell.Clear();
                quoted = false;
                if (cells.Any(x => x.Length > 0)) records.Add((recordLine, cells));
                cells = new List<string>();
                line++;
                recordLine = line;
                continue;
            }
            cell.Append(c);
        }
        if (inQuotes) errors.Add("Aspas não fechadas no fim do arquivo.");
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(Unneutralize(cell.ToString(), quoted));
            if (cells.Any(x => x.Length > 0)) records.Add((recordLine, cells));
        }

        if (records.Count == 0) return new ParsedFile(Array.Empty<string>(), Array.Empty<(int, IReadOnlyList<string>)>(), new[] { "Arquivo sem cabeçalho." });
        var headers = records[0].Cells.Select(h => h.Trim()).ToList();
        var rows = records.Skip(1).Select(r => (r.Line, (IReadOnlyList<string>)r.Cells)).ToList();
        if (rows.Count > maxRows) errors.Add($"O arquivo tem {rows.Count} linhas; o limite é {maxRows}.");
        foreach (var required in RequiredHeaders)
            if (!headers.Any(h => Same(h, required)))
                errors.Add($"Coluna obrigatória ausente: \"{required}\".");
        var duplicated = headers.GroupBy(h => Normalize(h)).Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => g.First()).ToList();
        foreach (var d in duplicated) errors.Add($"Coluna repetida no cabeçalho: \"{d}\".");
        return new ParsedFile(headers, rows, errors);
    }

    /// <summary>Retira o apóstrofo que a exportação acrescenta para neutralizar fórmula — e só ele.</summary>
    private static string Unneutralize(string value, bool quoted)
    {
        if (value.Length >= 2 && value[0] == '\'')
        {
            var rest = value[1..];
            var i = 0;
            while (i < rest.Length && rest[i] == ' ') i++;
            if (rest[0] is '\t' or '\r' or '\n' || i < rest.Length && rest[i] is '=' or '+' or '-' or '@')
                return rest;
        }
        return quoted ? value : value.Trim();
    }

    public static bool Same(string a, string b) => Normalize(a) == Normalize(b);

    /// <summary>Comparação de cabeçalho tolerante a caixa, espaços e acentos ("Codigo" = "Código").</summary>
    public static string Normalize(string s)
    {
        var d = (s ?? "").Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && !char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
