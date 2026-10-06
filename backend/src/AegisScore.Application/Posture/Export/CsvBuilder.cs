using System;
using System.Globalization;
using System.Text;

namespace AegisScore.Application.Posture.Export;

// [AEGIS-NIST-JOURNEY-02] Extraído de PostureSnapshotCsvWriter, sem mudança de comportamento, para que o CSV da maturidade NIST
// use EXATAMENTE a mesma proteção contra fórmula e o mesmo escaping das demais exportações.

/// <summary>
/// Montador de CSV com escaping correto (aspas, delimitador, CR/LF) e neutralização de Formula Injection nas
/// células TEXTUAIS. Cada célula do sistema (número/timestamp/booleano) usa um caminho próprio que nunca é
/// confundido com texto externo sanitizado.
/// </summary>
internal sealed class CsvBuilder
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

    /// <summary>
    /// [AEGIS-NIST-JOURNEY-02] Valor GERADO PELO SISTEMA já formatado (número invariante, data ISO): escapado, mas não
    /// neutralizado — um "-1" de lacuna é número, não fórmula. Nunca usar com texto vindo de pessoa ou de origem externa.
    /// </summary>
    public CsvBuilder SystemValue(string? value)
    {
        Separate();
        if (value is not null) _sb.Append(Encode(value));
        return this;
    }

    /// <summary>Célula de cabeçalho para uma coluna de timestamp (o rótulo é textual e neutralizado).</summary>
    public CsvBuilder Timestamp(string header) => Text(header);

    /// <summary>Valor de timestamp do sistema em ISO 8601 (UTC). Vazio quando nulo. Nunca começa por caractere perigoso.</summary>
    public CsvBuilder TimestampValue(DateTimeOffset? value)
    {
        Separate();
        if (value is { } v) _sb.Append(Encode(PostureSnapshotCsvWriter.Iso(v)));
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
