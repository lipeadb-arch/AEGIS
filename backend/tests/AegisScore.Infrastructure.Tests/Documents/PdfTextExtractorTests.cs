using AegisScore.Infrastructure.Documents;
using FluentAssertions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Testes do <see cref="PdfTextExtractor"/>. O round-trip gera um PDF em memória com o próprio PdfPig
/// (que também escreve PDFs), extrai o texto de volta e valida o conteúdo — prova ponta a ponta sem
/// depender de arquivo fixture. Os testes de CanHandle travam o roteamento que estava quebrado
/// ("Sem extrator de texto para 'application/pdf'").
/// </summary>
public sealed class PdfTextExtractorTests
{
    private readonly PdfTextExtractor _extractor = new();

    // ---- CanHandle: roteamento pelo worker ---------------------------------------

    [Theory]
    [InlineData("application/pdf", null)]
    [InlineData("application/pdf", "qualquer")]
    [InlineData(null, "Politica_Seguranca_Aegis_Tech.pdf")]
    [InlineData(null, "RELATORIO.PDF")]   // extensão case-insensitive
    public void CanHandle_ParaPdf_RetornaTrue(string? contentType, string? fileName)
    {
        _extractor.CanHandle(contentType, fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("text/plain", "notas.txt")]
    [InlineData("application/json", "dados.json")]
    [InlineData(null, "planilha.xlsx")]
    [InlineData(null, null)]
    public void CanHandle_ParaNaoPdf_RetornaFalse(string? contentType, string? fileName)
    {
        _extractor.CanHandle(contentType, fileName).Should().BeFalse();
    }

    // ---- Round-trip: gera PDF → extrai → valida ----------------------------------

    [Fact]
    public async Task ExtractAsync_ExtraiOTextoDeUmPdfGeradoEmMemoria()
    {
        // ASCII puro de propósito: as fontes Standard-14 (Helvetica) não garantem glifos acentuados.
        const string linha1 = "Politica de Seguranca da Aegis Tech";
        const string linha2 = "Controle de acesso com MFA obrigatorio";
        using var pdf = new MemoryStream(BuildPdf(linha1, linha2));

        var texto = Normalize(await _extractor.ExtractAsync(pdf, "application/pdf", CancellationToken.None));

        texto.Should().Contain(linha1);
        texto.Should().Contain(linha2);
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>Colapsa o espaçamento/quebras do layout do PDF em espaços simples (comparação estável).</summary>
    private static string Normalize(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Gera um PDF de uma página com o PdfPig — uma linha de texto por chamada AddText.</summary>
    private static byte[] BuildPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        var y = 700;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(25, y), font);
            y -= 24;
        }
        return builder.Build();
    }
}
