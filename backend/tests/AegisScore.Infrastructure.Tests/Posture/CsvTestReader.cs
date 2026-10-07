using System.Collections.Generic;
using System.Text;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Leitor CSV mínimo dos testes (';', aspas duplas escapadas, quebra de linha dentro de aspas):
/// lê o que os escritores geram sem desalinhar colunas quando um texto livre contém ';' — o que <c>Split(';')</c> não garante.
/// </summary>
internal static class CsvTestReader
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            if (c == '"') quoted = true;
            else if (c == ';') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
