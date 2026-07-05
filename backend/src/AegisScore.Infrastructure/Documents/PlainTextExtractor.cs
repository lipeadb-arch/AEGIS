using System.Text;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Documents;

/// <summary>
/// Extrator de texto simples (text/plain, markdown, csv, json). PDFs/DOCX exigem um extrator
/// dedicado (ex.: PdfPig) plugado como outro IDocumentTextExtractor — o worker escolhe pelo CanHandle.
/// </summary>
public sealed class PlainTextExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string? contentType, string? fileName)
    {
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext is ".txt" or ".md" or ".csv" or ".json";
    }

    public async Task<string> ExtractAsync(Stream content, string? contentType, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }
}
