using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Documents;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Extrator DOCX (Open XML SDK): lê parágrafos e tabela de um DOCX pequeno criado EM MEMÓRIA; recusa
/// `.doc`/`.docm`/formatos aleatórios; e confirma que PDF/TXT/DOCX têm extrator (a autoridade dos formatos
/// aceitos no upload) enquanto os demais não. Sem rede, sem arquivo em disco.
/// </summary>
public sealed class DocxTextExtractorTests
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task Extract_LeParagrafoETabela_EmOrdemLegivel()
    {
        using var ms = new MemoryStream(BuildDocx());

        var text = await new DocxTextExtractor().ExtractAsync(ms, DocxMime, CancellationToken.None);

        text.Should().Contain("Revisão trimestral de acessos privilegiados", "o parágrafo é lido");
        text.Should().Contain("Responsável", "o cabeçalho da tabela é lido");
        text.Should().Contain("Equipe de Segurança", "as células da tabela são lidas");
        text.Should().Contain("Trimestral");
    }

    [Theory]
    [InlineData("politica.doc")]
    [InlineData("politica.docm")]
    [InlineData("planilha.xlsx")]
    [InlineData("qualquer.bin")]
    public void CanHandle_FormatosNaoSuportados_False(string fileName)
    {
        new DocxTextExtractor().CanHandle(null, fileName).Should().BeFalse();
    }

    [Fact]
    public void CanHandle_MimeDeMacroEnabled_False()
    {
        new DocxTextExtractor()
            .CanHandle("application/vnd.ms-word.document.macroEnabled.12", "x.docm")
            .Should().BeFalse(".docm (macro-enabled) NÃO é suportado");
    }

    [Fact]
    public void FormatosAceitos_SaoExatamente_PDF_TXT_DOCX()
    {
        // Espelha a validação de upload: um formato é aceito se ALGUM extrator o trata.
        IDocumentTextExtractor[] all = { new PlainTextExtractor(), new PdfTextExtractor(), new DocxTextExtractor() };
        bool Supported(string? ct, string fn) => all.Any(e => e.CanHandle(ct, fn));

        Supported("application/pdf", "p.pdf").Should().BeTrue();
        Supported("text/plain", "p.txt").Should().BeTrue();
        Supported(DocxMime, "p.docx").Should().BeTrue();

        Supported(null, "p.doc").Should().BeFalse("formato não suportado é recusado no upload");
        Supported(null, "p.docm").Should().BeFalse();
        Supported(null, "p.xlsx").Should().BeFalse();
    }

    private static byte[] BuildDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            body.AppendChild(new Paragraph(new Run(new Text(
                "Revisão trimestral de acessos privilegiados, com responsável nomeado e registro em ata."))));

            var table = new Table();
            table.Append(Row("Controle", "Responsável", "Frequência"));
            table.Append(Row("PR.AA-05", "Equipe de Segurança", "Trimestral"));
            body.AppendChild(table);

            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static TableRow Row(params string[] cells)
    {
        var row = new TableRow();
        foreach (var c in cells)
            row.Append(new TableCell(new Paragraph(new Run(new Text(c)))));
        return row;
    }
}
