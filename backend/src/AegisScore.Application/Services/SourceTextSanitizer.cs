using System.Net;
using System.Text.RegularExpressions;

namespace AegisScore.Application.Services;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] Autoridade ÚNICA de conversão de TEXTO DE FONTE (conteúdo NÃO confiável vindo de
/// conectores — Microsoft Graph, scanners, etc.) em TEXTO SIMPLES seguro para a fronteira pública e o contexto
/// da IA. O conteúdo bruto NUNCA deve atravessar a API/IA: pode conter HTML, links, scripts ou entidades.
///
/// Determinística e sem estado. NÃO produz HTML, <c>href</c>, protocolo injetado nem script — só texto puro:
/// remove blocos <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> COM conteúdo, remove as demais tags, decodifica
/// entidades, normaliza espaços e aplica teto de tamanho. É a mesma autoridade usada pela API de exposições,
/// pela Central de Prioridades e pelo contexto do Auditor Virtual — nunca uma segunda cópia por superfície.
/// (No frontend, o texto resultante é sempre INTERPOLADO como dado — jamais via <c>[innerHTML]</c>.)
/// </summary>
public static class SourceTextSanitizer
{
    /// <summary>Teto padrão de tamanho do texto simples (caracteres) — evita blocos enormes na tela/IA.</summary>
    public const int DefaultMaxLength = 600;

    // Remove <script>…</script> e <style>…</style> INCLUINDO o conteúdo (o conteúdo de script nunca é "texto").
    private static readonly Regex ScriptOrStyle = new(
        @"<(script|style)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Remove qualquer tag remanescente (aberta, fechada, comentário, tag malformada até o próximo '>').
    private static readonly Regex AnyTag = new(@"<[^>]*>", RegexOptions.Singleline | RegexOptions.Compiled);

    // Um '<' solto sem '>' (HTML malformado) — remove do ponto do '<' até o fim, para não vazar marcação parcial.
    private static readonly Regex DanglingLt = new(@"<[^>]*$", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Converte texto de fonte em texto simples, ou <c>null</c> quando a entrada é nula/vazia ou fica vazia após a
    /// limpeza. NUNCA lança. O resultado não contém tags, scripts, <c>href</c> nem entidades cruas.
    /// </summary>
    public static string? ToPlainText(string? raw, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = ScriptOrStyle.Replace(raw, " ");
        text = AnyTag.Replace(text, " ");
        text = DanglingLt.Replace(text, " ");

        // Decodifica entidades (&amp; &lt; &#39; …) DEPOIS de remover tags — assim um "&lt;script&gt;" escapado
        // não vira uma tag ativa. Uma segunda passada de remoção cobre marcação que só existia escapada.
        text = WebUtility.HtmlDecode(text);
        text = ScriptOrStyle.Replace(text, " ");
        text = AnyTag.Replace(text, " ");
        text = DanglingLt.Replace(text, " ");

        text = Whitespace.Replace(text, " ").Trim();
        if (text.Length == 0)
            return null;

        var cap = maxLength < 1 ? 1 : maxLength;
        return text.Length <= cap ? text : text[..cap].TrimEnd() + "…";
    }
}
