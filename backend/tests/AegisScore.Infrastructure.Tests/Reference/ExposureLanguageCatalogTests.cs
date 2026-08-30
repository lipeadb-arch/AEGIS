using AegisScore.Infrastructure.Reference;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Reference;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] Catálogo de linguagem de EXPOSIÇÕES. ARQUIVO validado FAIL-CLOSED (schema, chave
/// duplicada, campo vazio); LOOKUP FAIL-SOFT (o catálogo do fornecedor é dinâmico → ausência vira SourceOnly,
/// nunca tradução inventada). O caso do arquivo REAL cobre as recomendações da homologação.
/// </summary>
public class ExposureLanguageCatalogTests
{
    private static string RealPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "aegis_exposure_language.pt-BR.json");

    private static ExposureLanguageCatalog Load() => new(RealPath, NullLogger<ExposureLanguageCatalog>.Instance);

    [Fact]
    public void RealFile_IsAvailableInOutput_AndLoads()
    {
        System.IO.File.Exists(RealPath).Should().BeTrue("o catálogo de exposições precisa ser copiado para o output (Data/)");
        Load().Count.Should().BeGreaterThanOrEqualTo(11, "as recomendações iniciais da homologação");
    }

    [Fact]
    public void MatchesKnownTitle_ByNormalization_Localized()
    {
        var cat = Load();
        // Casa mesmo com espaços/caixa diferentes (normalização do título).
        var lang = cat.Match(null, "  block adobe reader FROM creating   child processes ");
        lang.Should().NotBeNull();
        lang!.DisplayTitle.Should().Be("Impedir que o Adobe Reader abra outros programas");
        lang.PlainSummary.Should().NotBeNullOrWhiteSpace();
        lang.WhyItMatters.Should().NotBeNullOrWhiteSpace();
        lang.FirstAction.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UnknownTitle_ReturnsNull_ForSourceOnlyFallback() =>
        Load().Match("ext-desconhecido", "Some brand-new Microsoft recommendation not in the catalog")
            .Should().BeNull("catálogo do fornecedor é dinâmico — ausência cai em SourceOnly, sem tradução inventada");

    [Fact]
    public void MissingFile_Throws_FailClosed()
    {
        var act = () => new ExposureLanguageCatalog(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "nao-existe.json"),
            NullLogger<ExposureLanguageCatalog>.Instance);
        act.Should().Throw<System.IO.FileNotFoundException>();
    }

    [Fact]
    public void MalformedFile_Throws() =>
        WithTempFile("{ isto não é json ", path =>
            ((System.Action)(() => new ExposureLanguageCatalog(path, NullLogger<ExposureLanguageCatalog>.Instance)))
                .Should().Throw<System.InvalidOperationException>());

    [Fact]
    public void DuplicateMatchTitle_Throws() =>
        WithTempFile(
            """
            { "exposures": [
              { "matchTitle": "Block X", "displayTitle": "A", "plainSummary": "s", "whyItMatters": "w", "firstAction": "f" },
              { "matchTitle": "block x", "displayTitle": "B", "plainSummary": "s", "whyItMatters": "w", "firstAction": "f" }
            ] }
            """,
            path => ((System.Action)(() => new ExposureLanguageCatalog(path, NullLogger<ExposureLanguageCatalog>.Instance)))
                .Should().Throw<System.InvalidOperationException>().WithMessage("*DUPLICADO*"));

    [Fact]
    public void EmptyField_Throws() =>
        WithTempFile(
            """
            { "exposures": [
              { "matchTitle": "Block Y", "displayTitle": "  ", "plainSummary": "s", "whyItMatters": "w", "firstAction": "f" }
            ] }
            """,
            path => ((System.Action)(() => new ExposureLanguageCatalog(path, NullLogger<ExposureLanguageCatalog>.Instance)))
                .Should().Throw<System.InvalidOperationException>());

    private static void WithTempFile(string content, System.Action<string> body)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aegis-exposure-{System.Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, content);
        try { body(path); }
        finally { System.IO.File.Delete(path); }
    }
}
