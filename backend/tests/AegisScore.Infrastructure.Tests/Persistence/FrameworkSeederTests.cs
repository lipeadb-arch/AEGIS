using System.Text.Json;
using System.Text.Json.Nodes;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-MVP-POSTURE-01] Testes do <see cref="FrameworkSeeder"/> sobre SQLite in-memory (banco relacional
/// REAL e efêmero) exercitando os ARTEFATOS DE PRODUÇÃO (catálogo 6/22/106, metodologia e 99 regras) — os
/// cenários de mudança/recusa mutam cópias temporárias. Cobre: separação catálogo/metodologia, evidência
/// tipada, proveniência com histórico, atualização determinística (idêntico/metodologia/regras/oficial não
/// estrutural) e recusa fail-closed (estrutural, pesos incompletos/extras, regra órfã/duplicada).
/// </summary>
public sealed class FrameworkSeederTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _temps = new();

    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static string CatalogPath => Path.Combine(DataDir, "nist_csf_2_0_catalog.json");
    private static string MethodologyPath => Path.Combine(DataDir, "aegis_methodology.json");
    private static string RulesPath => Path.Combine(DataDir, "aegis_assessment_rules.json");

    public FrameworkSeederTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var t in _temps)
            if (File.Exists(t)) File.Delete(t);
    }

    // ---- Seed inicial: pacote completo, pesos da metodologia, proveniência ------------------------------

    [Fact]
    public async Task SeedInicial_CarregaPacoteCompleto_ComPesosDaMetodologia_ProvenienciaEEvidenciaTipada()
    {
        await SeedAllAsync();

        await using var db = NewContext();
        (await db.FrameworkVersions.CountAsync()).Should().Be(1);
        (await db.Functions.CountAsync()).Should().Be(6);
        (await db.Categories.CountAsync()).Should().Be(22);
        (await db.Subcategories.CountAsync()).Should().Be(106);
        (await db.MaturityLevels.CountAsync()).Should().Be(5);
        (await db.AssessmentRules.CountAsync()).Should().Be(99);

        // Pesos vêm da METODOLOGIA, não do catálogo (que já não os traz).
        var methodologyWeights = ReadMethodologyWeights();
        var subs = await db.Subcategories.ToListAsync();
        subs.Should().OnlyContain(s => s.MaxScorePoints > 0);
        foreach (var s in subs)
            s.MaxScorePoints.Should().Be(methodologyWeights[s.Code], $"peso de {s.Code} vem da metodologia");

        // Evidência TIPADA e persistida — distribuição real: 58 telemetria / 41 documental / 0 híbrida.
        (await db.AssessmentRules.CountAsync(r => r.EvidenceType == RuleEvidenceType.Telemetry)).Should().Be(58);
        (await db.AssessmentRules.CountAsync(r => r.EvidenceType == RuleEvidenceType.Documentation)).Should().Be(41);
        (await db.AssessmentRules.CountAsync(r => r.EvidenceType == RuleEvidenceType.Both)).Should().Be(0);

        // Proveniência: 3 conjuntos, revisão 1 vigente, classificação correta, hash SHA-256.
        var prov = await db.ReferenceDatasetProvenances.ToListAsync();
        prov.Should().HaveCount(3);
        prov.Should().OnlyContain(p => p.IsCurrent && p.Revision == 1);
        prov.Single(p => p.Kind == ReferenceDatasetKind.NistCatalog).Classification.Should().Be(DatasetClassification.Official);
        prov.Single(p => p.Kind == ReferenceDatasetKind.AegisMethodology).Classification.Should().Be(DatasetClassification.Derived);
        prov.Single(p => p.Kind == ReferenceDatasetKind.AegisAssessmentRules).Classification.Should().Be(DatasetClassification.Derived);
        prov.Should().OnlyContain(p => p.ContentHash.Length == 64);
        prov.Single(p => p.Kind == ReferenceDatasetKind.AegisMethodology).MethodologyVersion.Should().NotBeNullOrWhiteSpace();
        // Metadados antes ignorados agora persistidos (não descartados em silêncio).
        prov.Single(p => p.Kind == ReferenceDatasetKind.NistCatalog).OfficialReference.Should().NotBeNullOrWhiteSpace();
        prov.Single(p => p.Kind == ReferenceDatasetKind.NistCatalog).Notes.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SeteAusenciasDeclaradas_SaoExatamenteAsSubcategoriasSemRegra()
    {
        await SeedAllAsync();

        await using var db = NewContext();
        var ruleCodes = (await db.AssessmentRules.Select(r => r.SubcategoryCode).ToListAsync()).ToHashSet();
        var withoutRule = (await db.Subcategories.Select(s => s.Code).ToListAsync())
            .Where(c => !ruleCodes.Contains(c)).OrderBy(x => x).ToList();

        withoutRule.Should().BeEquivalentTo(SchemaReadinessGuard.NonAutomatedSubcategoryCodes);
    }

    // ---- Segunda execução idêntica: no-op --------------------------------------------------------------

    [Fact]
    public async Task SegundaExecucaoIdentica_NaoDuplica_NaoCriaNovaRevisao()
    {
        await SeedAllAsync();
        await SeedAllAsync();   // 2ª passada idêntica

        await using var db = NewContext();
        (await db.FrameworkVersions.CountAsync()).Should().Be(1);
        (await db.Subcategories.CountAsync()).Should().Be(106);
        (await db.AssessmentRules.CountAsync()).Should().Be(99);
        // Conteúdo idêntico → nenhuma revisão nova (proveniência continua com 3 linhas, todas revisão 1).
        (await db.ReferenceDatasetProvenances.CountAsync()).Should().Be(3);
        (await db.ReferenceDatasetProvenances.CountAsync(p => p.Revision == 1 && p.IsCurrent)).Should().Be(3);
    }

    // ---- Atualização determinística de metodologia: reconcilia + histórico -----------------------------

    [Fact]
    public async Task AtualizacaoDeMetodologia_ReconciliaPeso_ENovaRevisao_PreservandoHashAntigo()
    {
        await SeedAllAsync();

        // Muta um peso na metodologia (mantém as 106 chaves, positivo).
        var weights = ReadMethodologyWeights();
        var target = weights.Keys.First();
        var novoPeso = weights[target] + 7;
        var methodologyTemp = MutateJson(MethodologyPath, root =>
            root["subcategoryWeights"]![target] = novoPeso);

        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, CatalogPath, methodologyTemp);

        await using var db = NewContext();
        (await db.Subcategories.SingleAsync(s => s.Code == target)).MaxScorePoints.Should().Be(novoPeso);

        // Histórico: metodologia agora tem 2 revisões — a antiga PRESERVADA (não vigente, com hash distinto).
        var meth = await db.ReferenceDatasetProvenances
            .Where(p => p.Kind == ReferenceDatasetKind.AegisMethodology).OrderBy(p => p.Revision).ToListAsync();
        meth.Should().HaveCount(2);
        meth[0].Revision.Should().Be(1);
        meth[0].IsCurrent.Should().BeFalse();
        meth[0].SupersededAt.Should().NotBeNull();
        meth[1].Revision.Should().Be(2);
        meth[1].IsCurrent.Should().BeTrue();
        meth[0].ContentHash.Should().NotBe(meth[1].ContentHash, "o hash antigo permanece rastreável");
    }

    // ---- Atualização de regras: reconcilia + nova revisão ---------------------------------------------

    [Fact]
    public async Task AtualizacaoDeRegras_ReconciliaConteudo_ENovaRevisao()
    {
        await SeedAllAsync();

        var rulesTemp = MutateJson(RulesPath, root =>
            root!.AsArray()[0]!["calculation_logic"] = "RUBRICA CONSULTIVA ATUALIZADA (teste).");
        var firstCode = JsonNode.Parse(File.ReadAllText(RulesPath))!.AsArray()[0]!["subcategory_id"]!.GetValue<string>();

        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAssessmentRulesAsync(ctx, rulesTemp, MethodologyPath);

        await using var db = NewContext();
        (await db.AssessmentRules.SingleAsync(r => r.SubcategoryCode == firstCode))
            .CalculationLogic.Should().Be("RUBRICA CONSULTIVA ATUALIZADA (teste).");
        var rules = await db.ReferenceDatasetProvenances
            .Where(p => p.Kind == ReferenceDatasetKind.AegisAssessmentRules).ToListAsync();
        rules.Should().HaveCount(2);
        rules.Count(p => p.IsCurrent).Should().Be(1);
    }

    // ---- Atualização OFICIAL não estrutural: atualiza campos + nova revisão do catálogo ----------------

    [Fact]
    public async Task AtualizacaoOficialNaoEstrutural_AtualizaTexto_ENovaRevisaoDoCatalogo()
    {
        await SeedAllAsync();

        // Muda a DESCRIÇÃO de uma subcategoria (mesma topologia de códigos).
        var (funcIdx, catIdx, subCode) = FirstSubcategoryLocation();
        var catalogTemp = MutateJson(CatalogPath, root =>
            root!["functions"]![funcIdx]!["categories"]![catIdx]!["subcategories"]![0]!["description"] = "DESCRIÇÃO OFICIAL ATUALIZADA (teste).");

        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, catalogTemp, MethodologyPath);

        await using var db = NewContext();
        (await db.FrameworkVersions.CountAsync()).Should().Be(1, "atualização não estrutural NÃO cria nova versão");
        (await db.Subcategories.SingleAsync(s => s.Code == subCode)).Description.Should().Be("DESCRIÇÃO OFICIAL ATUALIZADA (teste).");
        var cat = await db.ReferenceDatasetProvenances
            .Where(p => p.Kind == ReferenceDatasetKind.NistCatalog).ToListAsync();
        cat.Should().HaveCount(2, "texto oficial mudou → nova revisão do catálogo (histórico preservado)");
        cat.Count(p => p.IsCurrent).Should().Be(1);
    }

    // ---- Recusa de alteração ESTRUTURAL ---------------------------------------------------------------

    [Fact]
    public async Task AlteracaoEstrutural_DoConjuntoDeCodigos_EhRecusada()
    {
        await SeedAllAsync();

        // Renomeia um código de subcategoria PERSISTIDO — a topologia do banco passa a divergir do artefato.
        await using (var mut = NewContext())
        {
            var sub = await mut.Subcategories.OrderBy(s => s.Code).FirstAsync();
            sub.Code = "ZZ.ZZ-99";
            await mut.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, CatalogPath, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ESTRUTURAL*");
    }

    // ---- Recusa fail-closed: metodologia incompleta / extra --------------------------------------------

    [Fact]
    public async Task MetodologiaComPesoFaltando_EhRecusadaAntesDePersistir()
    {
        var target = ReadMethodologyWeights().Keys.First();
        var methodologyTemp = MutateJson(MethodologyPath, root =>
            ((JsonObject)root!["subcategoryWeights"]!).Remove(target));

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, CatalogPath, methodologyTemp);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*sem peso*");
        (await ctx.FrameworkVersions.CountAsync()).Should().Be(0, "recusa ANTES de persistir");
    }

    [Fact]
    public async Task MetodologiaComPesoExtra_ForaDoCatalogo_EhRecusada()
    {
        var methodologyTemp = MutateJson(MethodologyPath, root =>
            root!["subcategoryWeights"]!["ZZ.ZZ-99"] = 10);

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, CatalogPath, methodologyTemp);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*fora do catálogo*");
    }

    // ---- Recusa fail-closed: regra órfã / duplicada ---------------------------------------------------

    [Fact]
    public async Task RegraOrfa_EhRecusada()
    {
        await SeedAllAsync();
        var rulesTemp = MutateJson(RulesPath, root =>
            root!.AsArray()[0]!["subcategory_id"] = "ZZ.ZZ-99");

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAssessmentRulesAsync(ctx, rulesTemp, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*inexistentes*");
    }

    [Fact]
    public async Task RegraDuplicada_EhRecusada()
    {
        await SeedAllAsync();
        var rulesTemp = MutateJson(RulesPath, root =>
        {
            var arr = root!.AsArray();
            var dup = JsonNode.Parse(arr[1]!.ToJsonString())!;
            dup["subcategory_id"] = arr[0]!["subcategory_id"]!.GetValue<string>();
            arr.Add(dup);
        });

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAssessmentRulesAsync(ctx, rulesTemp, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*duplicada*");
    }

    [Fact]
    public async Task ResiduoDeRegra_NoBancoForaDoArtefato_EhRecusado()
    {
        await SeedAllAsync();

        // Injeta uma regra que NÃO está no artefato (código válido, mas sem rubrica declarada: uma das 7).
        await using (var mut = NewContext())
        {
            var subId = await mut.Subcategories.Where(s => s.Code == "GV.OC-01").Select(s => s.Id).FirstAsync();
            mut.AssessmentRules.Add(new AegisAssessmentRule { SubcategoryId = subId, SubcategoryCode = "GV.OC-01" });
            await mut.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAssessmentRulesAsync(ctx, RulesPath, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*não pertencem mais*");
    }

    // ---- Recusa: mudança de HIERARQUIA (topologia) sem mudar código -----------------------------------

    [Fact]
    public async Task AlteracaoDeHierarquia_SubcategoriaMovidaDeCategoria_SemMudarCodigo_EhRecusadaComoEstrutural()
    {
        await SeedAllAsync();

        // Move uma subcategoria para OUTRA categoria (mesmo conjunto de códigos, hierarquia diferente).
        await using (var mut = NewContext())
        {
            var sub = await mut.Subcategories.OrderBy(s => s.Code).FirstAsync();
            var otherCategoryId = await mut.Categories.Where(c => c.Id != sub.CategoryId).Select(c => c.Id).FirstAsync();
            sub.CategoryId = otherCategoryId;
            await mut.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, CatalogPath, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ESTRUTURAL*");
    }

    // ---- Recusa: códigos de função/categoria duplicados no artefato ------------------------------------

    [Fact]
    public async Task CatalogoComCodigoDeFuncaoDuplicado_EhRecusado()
    {
        var catalogTemp = MutateJson(CatalogPath, root =>
            root!["functions"]![1]!["code"] = root!["functions"]![0]!["code"]!.GetValue<string>());

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, catalogTemp, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*função*");
    }

    [Fact]
    public async Task CatalogoComCodigoDeCategoriaDuplicado_EhRecusado()
    {
        var catalogTemp = MutateJson(CatalogPath, root =>
            root!["functions"]![0]!["categories"]![1]!["code"] =
                root!["functions"]![0]!["categories"]![0]!["code"]!.GetValue<string>());

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, catalogTemp, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*categoria*");
    }

    [Fact]
    public async Task ClassificacaoDeProveniencia_Desconhecida_EhRecusada()
    {
        var catalogTemp = MutateJson(CatalogPath, root =>
            root!["provenance"]!["classification"] = "inventada");

        await using var ctx = NewContext();
        var act = () => FrameworkSeeder.SeedAsync(ctx, catalogTemp, MethodologyPath);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*desconhecida*");
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------------

    private async Task SeedAllAsync()
    {
        await using var ctx = NewContext();
        await FrameworkSeeder.SeedAsync(ctx, CatalogPath, MethodologyPath);
        await FrameworkSeeder.SeedAssessmentRulesAsync(ctx, RulesPath, MethodologyPath);
        await FrameworkSeeder.SeedSignalMappingsAsync(ctx);
    }

    private AegisScoreDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new NullTenantContext());

    private static Dictionary<string, int> ReadMethodologyWeights()
    {
        var root = JsonNode.Parse(File.ReadAllText(MethodologyPath))!;
        return root["subcategoryWeights"]!.AsObject()
            .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>());
    }

    private static (int funcIdx, int catIdx, string subCode) FirstSubcategoryLocation()
    {
        var root = JsonNode.Parse(File.ReadAllText(CatalogPath))!;
        var sub = root["functions"]![0]!["categories"]![0]!["subcategories"]![0]!;
        return (0, 0, sub["code"]!.GetValue<string>());
    }

    /// <summary>Lê o artefato, aplica a mutação e grava um arquivo TEMPORÁRIO (o de produção nunca é tocado).</summary>
    private string MutateJson(string sourcePath, Action<JsonNode> mutate)
    {
        var root = JsonNode.Parse(File.ReadAllText(sourcePath))!;
        mutate(root);
        var temp = Path.Combine(Path.GetTempPath(), $"aegis_seed_test_{Guid.NewGuid():N}.json");
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _temps.Add(temp);
        return temp;
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
    }
}
