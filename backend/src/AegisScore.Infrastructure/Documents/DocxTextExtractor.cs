using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// Extrator de texto de DOCX (políticas corporativas) via Open XML SDK — 100% gerenciado, sem GDI/Office/
/// binário nativo, portável no container Linux do Render. Plugado como mais um <see cref="IDocumentTextExtractor"/>;
/// o DocumentAnalysisWorker o seleciona pelo <see cref="CanHandle"/>.
///
/// Extrai parágrafos e células de tabela em ORDEM DE LEITURA (blocos de nível superior na ordem do corpo),
/// preservando quebras suficientes para o chunking. SEGURANÇA: abre somente leitura e lê apenas o texto do XML
/// (w:t) — NUNCA executa macro, link externo ou conteúdo incorporado (OLE/imagens). <c>.doc</c> (binário) e
/// <c>.docm</c> (macro-enabled) permanecem NÃO suportados.
/// </summary>
public sealed class DocxTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string? contentType, string? fileName)
    {
        // MIME oficial do .docx. O do .docm (…​wordprocessingml.document.macroEnabled.12) NÃO casa este teste.
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("wordprocessingml.document", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
            return true;
        return Path.GetExtension(fileName ?? string.Empty)
            .Equals(".docx", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ExtractAsync(Stream content, string? contentType, CancellationToken ct)
    {
        // O Open XML SDK é síncrono e lê de um stream posicionável. Materializamos em memória (documentos de
        // governança são pequenos) e rodamos o parse — CPU-bound — fora do thread do worker.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            using var word = WordprocessingDocument.Open(buffer, isEditable: false);
            var body = word.MainDocumentPart?.Document?.Body;
            if (body is null) return string.Empty;

            // Percorre os blocos de nível superior NA ORDEM do documento: parágrafos e tabelas.
            foreach (var element in body.ChildElements)
            {
                ct.ThrowIfCancellationRequested();
                switch (element)
                {
                    case Paragraph paragraph:
                        var line = ParagraphText(paragraph);
                        if (line.Length > 0) sb.AppendLine(line);
                        break;

                    case Table table:
                        foreach (var row in table.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>()
                                .Select(cell => string.Join(
                                    " ", cell.Elements<Paragraph>().Select(ParagraphText)).Trim())
                                .Where(cell => cell.Length > 0);
                            var rowText = string.Join(" | ", cells);
                            if (rowText.Length > 0) sb.AppendLine(rowText);
                        }
                        sb.AppendLine();   // separa a tabela do bloco seguinte (ajuda o chunking)
                        break;
                }
            }

            return sb.ToString();
        }, ct);
    }

    /// <summary>Texto de um parágrafo: concatena os runs de texto (w:t) na ordem, ignorando OLE/imagens.</summary>
    private static string ParagraphText(Paragraph paragraph) =>
        string.Concat(paragraph.Descendants<Text>().Select(t => t.Text)).Trim();
}
