using AegisScore.Application.Documents;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Testes da autoridade FINAL da prova documental (<see cref="EvidenceQuoteValidator"/>): um trecho só
/// conta se estiver LITERALMENTE presente no texto (após normalização mínima de Unicode/whitespace). É o
/// que descarta trecho inventado (alucinação) fail-closed, seja de modelo real ou do stub.
/// </summary>
public sealed class EvidenceQuoteValidatorTests
{
    private const string Source =
        "Política de Segurança da Informação.\n\n" +
        "A revisão de acessos privilegiados é realizada trimestralmente, com responsável nomeado e " +
        "registro em ata de auditoria.\n\nO comitê de segurança aprova a política anualmente.";

    [Fact]
    public void TrechoLiteralmentePresente_EhAceito()
    {
        const string quote = "A revisão de acessos privilegiados é realizada trimestralmente, com responsável nomeado e registro em ata de auditoria.";
        EvidenceQuoteValidator.IsLiterallyPresent(Source, quote).Should().BeTrue();
    }

    [Fact]
    public void DiferencasDeWhitespaceEQuebraDeLinha_NaoImpedem()
    {
        // O mesmo conteúdo com espaços/quebras diferentes (como a extração de PDF costuma variar).
        const string quote = "A revisão de acessos privilegiados   é realizada trimestralmente,\ncom responsável nomeado e registro em ata de auditoria.";
        EvidenceQuoteValidator.IsLiterallyPresent(Source, quote).Should().BeTrue("normalização de whitespace é permitida");
    }

    [Fact]
    public void TrechoINVENTADO_EhRejeitado()
    {
        // Paráfrase plausível, mas ausente do texto — o defeito mais caro do produto.
        const string quote = "A política de segurança foi aprovada pela direção e cobre todos os sistemas críticos.";
        EvidenceQuoteValidator.IsLiterallyPresent(Source, quote).Should().BeFalse("trecho ausente do texto é descartado fail-closed");
    }

    [Fact]
    public void PalavraGenericaIsolada_NaoProva()
    {
        EvidenceQuoteValidator.IsLiterallyPresent(Source, "política").Should().BeFalse("um termo isolado não é passagem probatória");
        EvidenceQuoteValidator.IsLiterallyPresent(Source, "Política de Segurança").Should().BeFalse("título curto também não prova implementação");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TrechoVazioOuNulo_EhRejeitado(string? quote)
    {
        EvidenceQuoteValidator.IsLiterallyPresent(Source, quote).Should().BeFalse();
    }

    [Fact]
    public void NormalizacaoUnicode_CasaFormasCanonicamenteEquivalentes()
    {
        // "revisão" com 'ã' composto (NFC) × decomposto (a + ~). NFC deve reconciliar sem trocar letras.
        var composed = "A revisão de acessos privilegiados é realizada trimestralmente, com responsável nomeado.";
        var decomposed = composed.Normalize(System.Text.NormalizationForm.FormD);
        var src = "Cabeçalho.\n\n" + composed + "\n\nRodapé.";
        EvidenceQuoteValidator.IsLiterallyPresent(src, decomposed).Should().BeTrue("NFC casa formas equivalentes");
    }
}
