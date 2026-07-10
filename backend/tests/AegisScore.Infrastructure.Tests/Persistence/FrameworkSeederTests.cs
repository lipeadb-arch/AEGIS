using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Persistence;

/// <summary>
/// Testes do <see cref="FrameworkSeeder"/> sobre SQLite in-memory — um banco relacional REAL e efêmero,
/// exercitando EnsureCreated + SaveChanges de verdade (sem PostgreSQL, sem mocks de EF). Cobre o contrato
/// que sustenta o painel: idempotência, carga do grafo completo e o preenchimento nunca-zero dos pesos.
/// </summary>
public sealed class FrameworkSeederTests : IDisposable
{
    // Contagens esperadas para o fixture mínimo abaixo (2 funções / 2 categorias / 3 subcategorias).
    private const int ExpectedFunctions = 2;
    private const int ExpectedCategories = 2;
    private const int ExpectedSubcategories = 3;
    private const int ExpectedMaturityLevels = 2;

    private readonly SqliteConnection _connection;
    private readonly string _catalogPath;

    public FrameworkSeederTests()
    {
        // O banco in-memory do SQLite vive ENQUANTO a conexão estiver aberta. Mantemos uma conexão por
        // teste (o xUnit instancia a classe por [Fact], então cada teste recebe um banco limpo e isolado).
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using (var ctx = NewContext())
            ctx.Database.EnsureCreated();

        _catalogPath = WriteCatalogFixture();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (File.Exists(_catalogPath))
            File.Delete(_catalogPath);
    }

    // ---- Requisito 1: idempotência ------------------------------------------------

    [Fact]
    public async Task SeedAsync_ExecutadoDuasVezes_NaoDuplicaOCatalogo()
    {
        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, _catalogPath);
        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, _catalogPath);   // 2ª passada: o guard deve tornar no-op

        await using var assert = NewContext();
        (await assert.FrameworkVersions.CountAsync()).Should().Be(1);
        (await assert.Functions.CountAsync()).Should().Be(ExpectedFunctions);
        (await assert.Categories.CountAsync()).Should().Be(ExpectedCategories);
        (await assert.Subcategories.CountAsync()).Should().Be(ExpectedSubcategories);
        (await assert.MaturityLevels.CountAsync()).Should().Be(ExpectedMaturityLevels);
    }

    // ---- Requisito 2: carga do grafo completo ------------------------------------

    [Fact]
    public async Task SeedAsync_CarregaGrafoCompleto_DoFrameworkAteAsSubcategorias()
    {
        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, _catalogPath);

        await using var assert = NewContext();
        var fv = await assert.FrameworkVersions
            .Include(f => f.MaturityLevels)
            .Include(f => f.Functions).ThenInclude(fn => fn.Categories).ThenInclude(c => c.Subcategories)
            .SingleAsync();

        fv.Name.Should().Be("NIST CSF 2.0");
        fv.Source.Should().Be("unit-test-fixture");
        fv.IsActive.Should().BeTrue();
        fv.MaturityLevels.Should().HaveCount(ExpectedMaturityLevels);

        fv.Functions.Should().HaveCount(ExpectedFunctions);
        var protect = fv.Functions.Single(f => f.Code == "PR");
        protect.Categories.Should().ContainSingle(c => c.Code == "PR.AA");

        var aa = protect.Categories.Single(c => c.Code == "PR.AA");
        aa.Subcategories.Select(s => s.Code).Should().BeEquivalentTo("PR.AA-01", "PR.AA-02");
    }

    // ---- Requisito 3: backfill de scoring (MaxScorePoints) -----------------------

    [Fact]
    public async Task SeedAsync_PreencheMaxScorePoints_RespeitandoCatalogoEDerivandoOsAusentes()
    {
        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, _catalogPath);

        await using var assert = NewContext();
        var subs = await assert.Subcategories.ToListAsync();

        // Regra de ouro do Aegis Score: nenhum peso pode ser <= 0 (o denominador do score nunca zera).
        subs.Should().OnlyContain(s => s.MaxScorePoints > 0);

        subs.Single(s => s.Code == "PR.AA-01").MaxScorePoints.Should().Be(17, "peso explícito do catálogo é respeitado");
        subs.Single(s => s.Code == "PR.AA-02").MaxScorePoints.Should().Be(20, "ausente → tier de identidade/acesso (PR.AA)");
        subs.Single(s => s.Code == "GV.OC-01").MaxScorePoints.Should().Be(5, "ausente → tier de governança (GV.OC)");
    }

    [Fact]
    public async Task SeedAsync_ComSubcategoriasDePesoZeroJaPersistidas_BackfillCorrigeSemReSemear()
    {
        // Arrange: base legada — "NIST CSF 2.0" já semeado ANTES da coluna MaxScorePoints existir (peso 0).
        await using (var seed = NewContext())
        {
            var fv = new FrameworkVersion { Name = "NIST CSF 2.0", Source = "legacy", IsActive = true };

            var pr = new NistFunction { Code = "PR", Name = "PROTECT" };
            var praa = new NistCategory { Code = "PR.AA", Name = "Identity" };
            praa.Subcategories.Add(new NistSubcategory { Code = "PR.AA-01", Description = "x", MaxScorePoints = 0 });
            pr.Categories.Add(praa);

            var gv = new NistFunction { Code = "GV", Name = "GOVERN" };
            var gvoc = new NistCategory { Code = "GV.OC", Name = "Org Context" };
            gvoc.Subcategories.Add(new NistSubcategory { Code = "GV.OC-01", Description = "y", MaxScorePoints = 0 });
            gv.Categories.Add(gvoc);

            fv.Functions.Add(pr);
            fv.Functions.Add(gv);
            seed.FrameworkVersions.Add(fv);
            await seed.SaveChangesAsync();
        }

        // Act: o backfill roda SEMPRE (antes do guard); o guard então barra um novo seed (NIST já existe).
        await using (var ctx = NewContext())
            await FrameworkSeeder.SeedAsync(ctx, _catalogPath);

        // Assert: pesos legados corrigidos via DefaultWeight, e nenhuma duplicação de catálogo.
        await using var assert = NewContext();
        (await assert.FrameworkVersions.CountAsync()).Should().Be(1, "o guard de idempotência barra o re-seed");
        (await assert.Subcategories.SingleAsync(s => s.Code == "PR.AA-01")).MaxScorePoints.Should().Be(20);
        (await assert.Subcategories.SingleAsync(s => s.Code == "GV.OC-01")).MaxScorePoints.Should().Be(5);
    }

    // ---- infraestrutura do teste --------------------------------------------------

    /// <summary>
    /// Novo DbContext sobre a MESMA conexão (o EF trata conexões externas como não-próprias e não as
    /// fecha no dispose). Contextos distintos para semear e assertar garantem que a asserção lê o BANCO,
    /// não o cache do ChangeTracker.
    /// </summary>
    private AegisScoreDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AegisScoreDbContext>()
            .UseSqlite(_connection)
            .Options;
        // Catálogo é dado de referência (não ITenantOwned) — nenhum tenant ambiente é necessário,
        // exatamente como no seeding de startup. TenantId nulo espelha esse contexto.
        return new AegisScoreDbContext(options, new NullTenantContext());
    }

    private static string WriteCatalogFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nist_catalog_test_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, CatalogJson);
        return path;
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
    }

    /// <summary>
    /// Fixture mínimo e determinístico: 2 funções, 2 categorias, 3 subcategorias. Cobre os três casos de
    /// peso — explícito no catálogo (PR.AA-01=17), ausente em tier alto (PR.AA-02 → 20) e em tier baixo
    /// (GV.OC-01 → 5). Isolado do catálogo real de 106 itens para o teste não depender do arquivo de produção.
    /// </summary>
    private const string CatalogJson = """
        {
          "framework": "NIST CSF 2.0",
          "source": "unit-test-fixture",
          "maturityScale": [
            { "level": "1", "name": "Performed", "score": 1 },
            { "level": "2", "name": "Documented", "score": 2 }
          ],
          "functions": [
            {
              "code": "PR",
              "name": "PROTECT (PR)",
              "definition": "Safeguards to manage the organization's cybersecurity risks.",
              "categories": [
                {
                  "code": "PR.AA",
                  "name": "Identity Management, Authentication, and Access Control (PR.AA)",
                  "definition": "Access is limited to authorized users, services and hardware.",
                  "subcategories": [
                    { "code": "PR.AA-01", "description": "Identities and credentials are managed.", "maxScorePoints": 17 },
                    { "code": "PR.AA-02", "description": "Identities are proofed and bound to credentials." }
                  ]
                }
              ]
            },
            {
              "code": "GV",
              "name": "GOVERN (GV)",
              "definition": "The organization's cybersecurity risk strategy and policy are established.",
              "categories": [
                {
                  "code": "GV.OC",
                  "name": "Organizational Context (GV.OC)",
                  "definition": "The circumstances surrounding risk management decisions are understood.",
                  "subcategories": [
                    { "code": "GV.OC-01", "description": "The organizational mission is understood." }
                  ]
                }
              ]
            }
          ]
        }
        """;
}
