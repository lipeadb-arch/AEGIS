using System.Text;
using AegisScore.Application.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// Extrator de texto de PDFs via UglyToad.PdfPig (OSS permissiva, 100% gerenciado — sem binário nativo,
/// portável Linux/Windows). Plugado como mais um <see cref="IDocumentTextExtractor"/>; o
/// DocumentAnalysisWorker o seleciona pelo <see cref="CanHandle"/>. Concatena as palavras por página
/// (GetWords preserva o espaçamento entre tokens — melhor sinal para o LLM que o page.Text cru).
/// </summary>
public sealed class PdfTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string? contentType, string? fileName)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return true;
        return Path.GetExtension(fileName ?? string.Empty)
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> ExtractAsync(Stream content, string? contentType, CancellationToken ct)
    {
        // PdfPig é síncrono e lê de um byte[]. Materializamos o stream em memória (documentos de
        // governança são pequenos) e rodamos o parse — CPU-bound — fora do thread do worker.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            using var pdf = PdfDocument.Open(bytes);
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sb.AppendLine(string.Join(' ', page.GetWords().Select(w => w.Text)));
            }
            return sb.ToString();
        }, ct);
    }
}
