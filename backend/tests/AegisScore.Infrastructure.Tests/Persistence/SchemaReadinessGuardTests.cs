using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// [AEGIS-AUD-052] Guard de prontidão da API.
///
/// A API deixou de aplicar migrations e de semear: quem prepara o banco é o AegisScore.DbMigrator.
/// Estes testes fixam o contrato do que a API RECUSA — e provam que ela apenas constata, sem emitir
/// DDL nem reparar dados. Um serviço que sobe sobre catálogo ausente ou duplicado reporta postura de
/// segurança falsa, que é pior do que não subir.
///
/// Harness dos demais testes: SQLite in-memory com a conexão mantida aberta.
/// </summary>
public sealed class SchemaReadinessGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SchemaReadinessGuardTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AegisScoreDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(null));

    /// <summary>Catálogo mínimo válido: 1 FrameworkVersion + 1 função/categoria/subcategoria + 1 regra.</summary>
    private static void SeedValidCatalog(AegisScoreDbContext db, string subcategoryCode = "PR.AA-01")
    {
        var fv = new FrameworkVersion { Name = SchemaReadinessGuard.CatalogName, IsActive = true };
        var fn = new NistFunction { FrameworkVersionId = fv.Id, Code = "PR", Name = "Protect" };
        var cat = new NistCategory { FunctionId = fn.Id, Code = "PR.AA", Name = "Identity" };
        var sub = new NistSubcategory
        {
            CategoryId = cat.Id,
            Code = subcategoryCode,
            Description = "teste",
            MaxScorePoints = 20,
        };
        cat.Subcategories.Add(sub);
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);
        db.FrameworkVersions.Add(fv);
        db.SaveChanges();

        db.AssessmentRules.Add(new AegisAssessmentRule
        {
            SubcategoryId = sub.Id,
            SubcategoryCode = subcategoryCode,
            CalculationLogic = "teste",
        });
        db.SaveChanges();
    }

    // ---- Estado íntegro ---------------------------------------------------------

    [Fact]
    public async Task EstadoIntegro_EhAprovado()
    {
        await using var db = NewContext();
        SeedValidCatalog(db);

        var result = await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        result.IsReady.Should().BeTrue(result.Describe());
        result.Problems.Should().BeEmpty();
    }

    // ---- Catálogo ---------------------------------------------------------------

    [Fact]
    public async Task CatalogoAusente_EhReprovado()
    {
        await using var db = NewContext();

        var result = await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("ausente");
    }

    [Fact]
    public async Task CatalogoDuplicado_EhReprovado_ENaoEhReparado()
    {
        await using var db = NewContext();
        SeedValidCatalog(db);

        // O índice único desta entrega IMPEDE a duplicata em bancos novos — e o fato de esta linha
        // precisar removê-lo é a prova de que ele funciona. O que se testa aqui é o guard como defesa
        // em profundidade: um banco LEGADO, duplicado por duas réplicas antes da correção, precisa ser
        // recusado pela API em vez de silenciosamente aceito.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_FrameworkVersions_Name\";");

        db.FrameworkVersions.Add(new FrameworkVersion { Name = SchemaReadinessGuard.CatalogName });
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("DUPLICADO");

        (await db.FrameworkVersions.CountAsync()).Should().Be(2,
            "o guard CONSTATA — reparar dados nunca é responsabilidade da API");
    }

    [Fact]
    public async Task CodigosDeSubcategoriaDuplicados_SaoReprovados()
    {
        await using var db = NewContext();
        SeedValidCatalog(db);

        // Mesmo código sob OUTRA categoria: os índices únicos do catálogo são compostos com o Id do
        // pai, então o banco aceita — e o motor de avaliação passaria a ter mapeamento ambíguo.
        var fv = await db.FrameworkVersions.FirstAsync();
        var fn = new NistFunction { FrameworkVersionId = fv.Id, Code = "DE", Name = "Detect" };
        var cat = new NistCategory { FunctionId = fn.Id, Code = "DE.CM", Name = "Monitoring" };
        cat.Subcategories.Add(new NistSubcategory
        {
            CategoryId = cat.Id,
            Code = "PR.AA-01",   // colide de propósito
            Description = "colisao",
            MaxScorePoints = 15,
        });
        fn.Categories.Add(cat);
        db.Functions.Add(fn);
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("duplicado");
    }

    // ---- Regras -----------------------------------------------------------------

    [Fact]
    public async Task RegrasAusentes_SaoReprovadas()
    {
        await using var db = NewContext();
        SeedValidCatalog(db);
        db.AssessmentRules.RemoveRange(db.AssessmentRules);
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("regra");
    }

    [Fact]
    public async Task RegraOrfa_EhReprovada()
    {
        await using var db = NewContext();
        SeedValidCatalog(db);

        // A FK AssessmentRule.SubcategoryId → Subcategory.Id já impede a órfã em bancos íntegros;
        // desligá-la aqui simula o estado legado (ou um banco manipulado à mão) que o guard precisa
        // detectar. Sem essa checagem, uma regra órfã produziria veredito sobre controle inexistente.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

        db.AssessmentRules.Add(new AegisAssessmentRule
        {
            SubcategoryId = Guid.NewGuid(),   // não existe no catálogo
            SubcategoryCode = "XX.XX-99",
            CalculationLogic = "orfa",
        });
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("inexistentes");
    }

    // ---- Migrations pendentes ---------------------------------------------------

    [Fact]
    public async Task MigrationsPendentes_SaoReprovadas_ECurtoCircuitamAVerificacao()
    {
        // EnsureCreated materializa o schema a partir do MODELO, sem tabela de histórico: para o EF,
        // todas as migrations continuam pendentes — que é justamente o estado de um banco não
        // preparado pelo migrator.
        await using var db = NewContext();
        SeedValidCatalog(db);

        var result = await SchemaReadinessGuard.CheckMigrationsAsync(db, keyRing: null);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("AegisScoreDbContext");
        result.Describe().Should().Contain("pendente");
    }

    [Fact]
    public async Task CheckAsync_ComMigrationsPendentes_NaoAvaliaOCatalogo()
    {
        await using var db = NewContext();
        // Catálogo AUSENTE de propósito: se a verificação de dados rodasse, o problema apareceria.
        var result = await SchemaReadinessGuard.CheckAsync(db, keyRing: null);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("pendente");
        result.Describe().Should().NotContain("ausente",
            "migration pendente explica tudo — listar o catálogo faltando confundiria o operador");
    }

    // ---- Contrato de não-mutação -------------------------------------------------

    [Fact]
    public async Task Guard_NaoEmiteDDL_NemSemeia()
    {
        await using var db = NewContext();
        SeedValidCatalog(db);

        var versoesAntes = await db.FrameworkVersions.CountAsync();
        var subsAntes = await db.Subcategories.CountAsync();
        var regrasAntes = await db.AssessmentRules.CountAsync();

        await SchemaReadinessGuard.CheckAsync(db, keyRing: null);
        await SchemaReadinessGuard.CheckCatalogIntegrityAsync(db);

        (await db.FrameworkVersions.CountAsync()).Should().Be(versoesAntes);
        (await db.Subcategories.CountAsync()).Should().Be(subsAntes);
        (await db.AssessmentRules.CountAsync()).Should().Be(regrasAntes,
            "a API nunca semeia: quem faz isso é o AegisScore.DbMigrator");
    }

    [Fact]
    public async Task EnsureReadyAsync_LancaQuandoNaoPronto()
    {
        await using var db = NewContext();

        var act = () => SchemaReadinessGuard.EnsureReadyAsync(db, keyRing: null);

        (await act.Should().ThrowAsync<SchemaNotReadyException>())
            .Which.Message.Should().Contain("AegisScore.DbMigrator",
                "a mensagem precisa dizer ao operador qual é a ação, não apenas que falhou");
    }

    // ---- Validação COMPLETA do pacote (CheckActivePackageAsync) — sobre o artefato REAL semeado ---------

    [Fact]
    public async Task PacoteCsfCompleto_Semeado_EhAprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeTrue(result.Describe());
    }

    [Fact]
    public async Task PacoteSemVersaoAtiva_EhReprovado()
    {
        await using var db = NewContext();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("ATIVA");
    }

    [Fact]
    public async Task PacoteComRegraFaltando_EhReprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        db.AssessmentRules.Remove(await db.AssessmentRules.FirstAsync());
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("Rubricas");
    }

    [Fact]
    public async Task PacoteComHintDesconhecido_EhReprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        var mapping = await db.SignalMappings.FirstAsync();
        mapping.ScoringHint = "hint.inexistente.v9";
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("Hint");
    }

    [Fact]
    public async Task PacoteSemProveniencia_EhReprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        db.ReferenceDatasetProvenances.RemoveRange(db.ReferenceDatasetProvenances);
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("Proveniência");
    }

    [Fact]
    public async Task PacoteComSegundaFrameworkAtivaDeOutroNome_EhReprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);

        // O índice parcial IMPEDE duas ativas em bancos novos — esta linha o remove para simular um banco
        // legado/manipulado (defesa em profundidade, mesmo idioma do teste de catálogo duplicado).
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"UX_FrameworkVersion_SingleActive\";");
        db.FrameworkVersions.Add(new FrameworkVersion { Name = "Outro Framework 1.0", IsActive = true });
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("ATIVA");
    }

    [Fact]
    public async Task PacoteComDescricaoOficialAdulteradaNoBanco_ReprovaPorHash()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        var sub = await db.Subcategories.FirstAsync();
        sub.Description = "descrição adulterada no banco";
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("Hash do catálogo");
    }

    [Fact]
    public async Task PacoteComPesoAdulteradoNoBanco_ReprovaPorHashDaMetodologia()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        var sub = await db.Subcategories.FirstAsync();
        sub.MaxScorePoints += 3;
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("Hash da metodologia");
    }

    [Fact]
    public async Task PacoteComRubricaAdulteradaNoBanco_ReprovaPorHashDasRegras()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        var rule = await db.AssessmentRules.FirstAsync();
        rule.CalculationLogic = "rubrica adulterada no banco";
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("Hash das regras");
    }

    [Fact]
    public async Task PacoteComEvidenceTypeIncoerente_EhReprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        // Adultera uma regra DOCUMENTAL para Telemetry — o tipo persistido deixa de bater com os requisitos.
        var doc = await db.AssessmentRules.FirstAsync(r => r.EvidenceType == RuleEvidenceType.Documentation);
        doc.EvidenceType = RuleEvidenceType.Telemetry;
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("incoerente");
    }

    [Fact]
    public async Task PacoteComClassificacaoAdulterada_EhReprovado()
    {
        await using var db = NewContext();
        await SeedRealPackageAsync(db);
        var meth = await db.ReferenceDatasetProvenances
            .FirstAsync(p => p.Kind == ReferenceDatasetKind.AegisMethodology && p.IsCurrent);
        meth.Classification = DatasetClassification.Official;   // metodologia é Derived
        await db.SaveChangesAsync();

        var result = await SchemaReadinessGuard.CheckActivePackageAsync(db);

        result.IsReady.Should().BeFalse();
        result.Describe().Should().Contain("classificação");
    }

    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");

    private static async Task SeedRealPackageAsync(AegisScoreDbContext db)
    {
        await FrameworkSeeder.SeedAsync(db,
            Path.Combine(DataDir, "nist_csf_2_0_catalog.json"),
            Path.Combine(DataDir, "aegis_methodology.json"));
        await FrameworkSeeder.SeedAssessmentRulesAsync(db,
            Path.Combine(DataDir, "aegis_assessment_rules.json"),
            Path.Combine(DataDir, "aegis_methodology.json"));
        await FrameworkSeeder.SeedSignalMappingsAsync(db);
    }
}
