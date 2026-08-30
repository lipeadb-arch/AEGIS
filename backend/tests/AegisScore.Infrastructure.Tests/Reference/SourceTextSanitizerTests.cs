using AegisScore.Application.Services;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Reference;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] O texto de fonte (Microsoft Graph, scanners) é CONTEÚDO NÃO CONFIÁVEL: pode conter
/// HTML, links, scripts ou entidades. Estes testes travam a autoridade única de sanitização — nada de tag, href,
/// script ou entidade crua cruza para a API/IA; só texto puro, normalizado e com teto de tamanho.
/// </summary>
public class SourceTextSanitizerTests
{
    [Fact]
    public void StripsBoldTags_KeepingText() =>
        SourceTextSanitizer.ToPlainText("Bloqueie o <b>Adobe Reader</b> agora")
            .Should().Be("Bloqueie o Adobe Reader agora");

    [Fact]
    public void StripsAnchor_DropsHrefAndUrl()
    {
        var clean = SourceTextSanitizer.ToPlainText("Veja <a href=\"https://evil.example/x?y=1\">a doc</a> aqui");
        clean.Should().Contain("a doc").And.Contain("Veja");   // texto do link preservado
        clean!.Should().NotContain("href").And.NotContain("http").And.NotContain("evil.example").And.NotContain("<");
    }

    [Fact]
    public void DecodesHtmlEntities() =>
        // &amp;/&#39; decodificam para texto simples (& e '); NÃO viram marcação ativa.
        SourceTextSanitizer.ToPlainText("Office &amp; macros &#39;seguras&#39;")
            .Should().Be("Office & macros 'seguras'");

    [Fact]
    public void RemovesScriptBlockWithContent()
    {
        var clean = SourceTextSanitizer.ToPlainText("Antes<script>alert('x');document.cookie</script>Depois");
        clean.Should().Be("Antes Depois");
        clean!.Should().NotContain("alert").And.NotContain("cookie").And.NotContain("script");
    }

    [Fact]
    public void NeutralizesEscapedScript_NoActiveMarkup()
    {
        // Um "<script>" ESCAPADO como entidade não pode virar tag ativa após o decode.
        var clean = SourceTextSanitizer.ToPlainText("&lt;script&gt;alert(1)&lt;/script&gt; fim");
        clean!.Should().NotContain("<script").And.NotContain("</script");
    }

    [Fact]
    public void HandlesMalformedHtml_NoDanglingMarkup()
    {
        var clean = SourceTextSanitizer.ToPlainText("texto <div class=\"x\" sem fechar");
        clean.Should().Be("texto");
        clean!.Should().NotContain("<").And.NotContain("class");
    }

    [Fact]
    public void EmptyOrWhitespace_ReturnsNull()
    {
        SourceTextSanitizer.ToPlainText(null).Should().BeNull();
        SourceTextSanitizer.ToPlainText("   ").Should().BeNull();
        SourceTextSanitizer.ToPlainText("<br/> <span></span>").Should().BeNull();
    }

    [Fact]
    public void PlainText_PassesThrough_Normalized() =>
        SourceTextSanitizer.ToPlainText("  bloqueie   scripts   ofuscados ")
            .Should().Be("bloqueie scripts ofuscados");

    [Fact]
    public void EnforcesLengthCap_WithEllipsis()
    {
        var clean = SourceTextSanitizer.ToPlainText(new string('a', 50), maxLength: 10);
        clean.Should().HaveLength(11);      // 10 + reticências
        clean.Should().EndWith("…");
    }
}
