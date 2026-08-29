using System.Text.Json;
using AegisScore.Infrastructure.Reference;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Reference;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-01] Camada de LINGUAGEM CLARA (autoral, provider-neutral, pt-BR). O risco que estes
/// testes cobrem: os textos vivem num JSON versionado, então uma subcategoria sem entrada, um campo vazio ou
/// um código duplicado NÃO quebram o build — quebrariam a promessa de que todo controle é compreensível sem
/// IA, e deixariam a tela cair para o nome genérico da categoria. O caso do arquivo REAL é a rede de segurança:
/// cobre a completude perante o catálogo ativo e a separação da redação autoral em relação ao conteúdo oficial.
/// </summary>
public class ControlLanguageCatalogTests
{
    private static string RealLanguagePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "aegis_control_language.pt-BR.json");

    private static string RealCatalogPath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "nist_csf_2_0_catalog.json");

    private static ControlLanguageCatalog Load() =>
        new(RealLanguagePath, NullLogger<ControlLanguageCatalog>.Instance);

    [Fact]
    public void RealLanguageFile_IsAvailableInTheBuildOutput_UsedByTheApi()
    {
        // O arquivo é copiado para o output pelo MESMO mecanismo (Content/PreserveNewest) que o leva à
        // imagem/binário da API. Existir aqui prova que o empacotamento funciona — a API o encontrará.
        File.Exists(RealLanguagePath).Should().BeTrue(
            "o catálogo de linguagem de produção precisa ser copiado para o output (Data/)");
    }

    [Fact]
    public void RealLanguageFile_CoversEveryActiveSubcategory_Exactly()
    {
        var catalogCodes = ReadCatalogCodes().Keys.ToHashSet(StringComparer.Ordinal);
        var language = Load();

        language.Count.Should().Be(catalogCodes.Count, "uma entrada por subcategoria ativa, nem mais nem menos");
        language.Codes.ToHashSet(StringComparer.Ordinal).Should().BeEquivalentTo(catalogCodes,
            "a cobertura é EXATA: sem faltas (cairia no nome da categoria) e sem código órfão");
    }

    [Fact]
    public void RealLanguageFile_HasNoEmptyField_AndOneEntryPerCode()
    {
        var language = Load();
        foreach (var code in ReadCatalogCodes().Keys)
        {
            var lang = language.Get(code);
            lang.Should().NotBeNull($"{code} precisa de redação em linguagem clara");
            lang!.Title.Should().NotBeNullOrWhiteSpace($"{code}.title");
            lang.Summary.Should().NotBeNullOrWhiteSpace($"{code}.summary");
            lang.Impact.Should().NotBeNullOrWhiteSpace($"{code}.impact");
            lang.InitialAction.Should().NotBeNullOrWhiteSpace($"{code}.initialAction");
        }
    }

    [Fact]
    public void RealLanguageFile_TitlesAreSpecific_DistinctWithinEachCategory()
    {
        var language = Load();
        var catalog = ReadCatalogCodes();

        // Dois controles da MESMA categoria não podem repetir o mesmo título (o defeito era todos os PR.AA-*
        // aparecerem como "Identidade e Acesso"). Agrupa por categoria (prefixo antes do último '-').
        var byCategory = catalog.Keys
            .GroupBy(CategoryOf, StringComparer.Ordinal);
        foreach (var group in byCategory)
        {
            var titles = group.Select(code => language.Get(code)!.Title).ToList();
            titles.Should().OnlyHaveUniqueItems(
                $"os controles da categoria {group.Key} precisam de títulos específicos distintos");
        }
    }

    [Fact]
    public void RealLanguageFile_AuthoredWording_IsSeparateFromOfficialContent()
    {
        var catalog = ReadCatalogCodes();   // code -> descrição OFICIAL (NIST, em inglês)
        var language = Load();

        // A redação autoral (pt-BR) NÃO é a descrição oficial reescrita por cima: o título de cada controle
        // difere do texto oficial da subcategoria — o oficial permanece intocado como referência secundária.
        foreach (var (code, officialDescription) in catalog)
        {
            language.Get(code)!.Title.Should().NotBe(officialDescription,
                $"o título claro de {code} é autoral, não a descrição oficial do NIST");
        }

        // E a proveniência do artefato se declara DERIVADA e em pt-BR — nunca conteúdo oficial do NIST.
        using var doc = JsonDocument.Parse(File.ReadAllText(RealLanguagePath));
        var prov = doc.RootElement.GetProperty("provenance");
        prov.GetProperty("classification").GetString().Should().Be("derived");
        prov.GetProperty("language").GetString().Should().Be("pt-BR");
    }

    [Fact]
    public void MissingFile_Throws_FailClosed()
    {
        // FAIL-CLOSED (≠ persona, que degrada em silêncio): a ausência é DETECTADA, não mascarada com o nome
        // genérico da categoria — que é justamente o defeito que esta camada existe para corrigir.
        var act = () => new ControlLanguageCatalog(
            Path.Combine(AppContext.BaseDirectory, "Data", "nao-existe.json"),
            NullLogger<ControlLanguageCatalog>.Instance);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void MalformedFile_Throws() =>
        WithTempFile("{ isto não é json válido ", path =>
        {
            var act = () => new ControlLanguageCatalog(path, NullLogger<ControlLanguageCatalog>.Instance);
            act.Should().Throw<InvalidOperationException>();
        });

    [Fact]
    public void DuplicateCode_Throws() =>
        WithTempFile(
            """
            { "controls": [
              { "code": "PR.AA-01", "title": "A", "summary": "s", "impact": "i", "initialAction": "a" },
              { "code": "PR.AA-01", "title": "B", "summary": "s", "impact": "i", "initialAction": "a" }
            ] }
            """,
            path =>
            {
                var act = () => new ControlLanguageCatalog(path, NullLogger<ControlLanguageCatalog>.Instance);
                act.Should().Throw<InvalidOperationException>().WithMessage("*DUPLICADO*");
            });

    [Fact]
    public void EmptyField_Throws() =>
        WithTempFile(
            """
            { "controls": [
              { "code": "PR.AA-01", "title": "  ", "summary": "s", "impact": "i", "initialAction": "a" }
            ] }
            """,
            path =>
            {
                var act = () => new ControlLanguageCatalog(path, NullLogger<ControlLanguageCatalog>.Instance);
                act.Should().Throw<InvalidOperationException>();
            });

    // ---- helpers --------------------------------------------------------------------

    private static string CategoryOf(string code)
    {
        var dash = code.LastIndexOf('-');
        return dash > 0 ? code[..dash] : code;
    }

    /// <summary>Lê o catálogo NIST REAL: código da subcategoria → descrição oficial (referência de teste).</summary>
    private static IReadOnlyDictionary<string, string> ReadCatalogCodes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RealCatalogPath));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fn in doc.RootElement.GetProperty("functions").EnumerateArray())
            foreach (var cat in fn.GetProperty("categories").EnumerateArray())
                foreach (var sub in cat.GetProperty("subcategories").EnumerateArray())
                    map[sub.GetProperty("code").GetString()!] = sub.GetProperty("description").GetString() ?? "";
        return map;
    }

    private static void WithTempFile(string content, Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aegis-language-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        try { body(path); }
        finally { File.Delete(path); }
    }
}
