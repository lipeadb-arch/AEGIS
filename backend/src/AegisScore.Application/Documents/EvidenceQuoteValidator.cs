using System.Globalization;
using System.Text;

namespace AegisScore.Application.Documents;

/// <summary>
/// Autoridade FINAL da prova documental: valida, EM CÓDIGO, que um trecho probatório existe LITERALMENTE
/// no texto extraído do documento. É o que separa evidência real de alucinação — nem a resposta do modelo
/// (real ou stub) nem um racional bem escrito bastam: se o trecho citado não está no texto, ele é
/// descartado fail-closed e não gera mapping, cobertura nem crédito no score.
///
/// <para><b>Normalização permitida:</b> forma Unicode canônica (NFC) e colapso de espaços/quebras de
/// linha em um único espaço, com trim — porque a extração de PDF/DOCX introduz variações de whitespace e
/// de composição Unicode que não mudam o CONTEÚDO. <b>O que NÃO se faz:</b> remover acentos, trocar
/// palavras, casar por radical ou aceitar similaridade semântica. Isso seria "substituir palavras", e o
/// requisito é presença LITERAL — comparação Ordinal após a normalização mínima.</para>
///
/// <para><b>Piso de materialidade:</b> um trecho probatório é uma passagem, não uma palavra solta. Um
/// "quote" curto demais (um título, um termo genérico como "política") não prova implementação e é
/// rejeitado mesmo que esteja presente no texto — a presença literal é necessária, não suficiente.</para>
/// </summary>
public static class EvidenceQuoteValidator
{
    /// <summary>Comprimento mínimo do trecho normalizado — abaixo disso é palavra/título, não passagem probatória.</summary>
    private const int MinQuoteLength = 24;

    /// <summary>Mínimo de palavras — reforça o piso: um trecho probatório tem várias palavras, não uma isolada.</summary>
    private const int MinQuoteWords = 4;

    /// <summary>
    /// True se <paramref name="quote"/> é uma passagem materialmente relevante e LITERALMENTE presente em
    /// <paramref name="sourceText"/> (após normalização Unicode + whitespace). Fail-closed: nulo/curto/ausente → false.
    /// </summary>
    public static bool IsLiterallyPresent(string? sourceText, string? quote)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(quote))
            return false;

        var normalizedQuote = Normalize(quote);
        if (normalizedQuote.Length < MinQuoteLength)
            return false;
        if (CountWords(normalizedQuote) < MinQuoteWords)
            return false;

        var normalizedSource = Normalize(sourceText);
        return normalizedSource.Contains(normalizedQuote, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalização MÍNIMA e reversível em significado: NFC + colapso de qualquer sequência de whitespace
    /// (espaço, tab, quebra de linha) em um único espaço + trim. Preserva letras, acentos e pontuação.
    /// </summary>
    public static string Normalize(string text)
    {
        var nfc = text.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(nfc.Length);
        var pendingSpace = false;
        foreach (var ch in nfc)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;   // não abre com espaço; agrupa runs
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static int CountWords(string normalized)
    {
        if (normalized.Length == 0) return 0;
        var words = 1;
        foreach (var ch in normalized)
            if (ch == ' ') words++;
        return words;
    }
}
