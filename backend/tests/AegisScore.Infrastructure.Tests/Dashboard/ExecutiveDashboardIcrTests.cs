using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Dashboard;

/// <summary>
/// Testes focados do ICR no dashboard executivo (<see cref="DashboardController.Executive"/>), sobre SQLite
/// in-memory, exercitando o controller de verdade. Provam a correção do "ICR sintético apresentado como
/// medição": sem NENHUM <see cref="IcrScore"/> persistido o contrato devolve <c>icr == null</c> (não
/// avaliado) — jamais o "45 · Moderado" que o antigo proxy de constantes fabricava —, enquanto ICRs REAIS
/// seguem produzindo a mesma média e banda de antes. A fórmula, os pesos e as faixas do ICR não mudaram.
/// </summary>
public sealed class ExecutiveDashboardIcrTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;

    public ExecutiveDashboardIcrTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantId);
        ctx.Database.EnsureCreated();
        // A raiz Tenant só alimenta o nome do cliente no cabeçalho — o ICR nunca depende dela.
        ctx.Tenants.Add(new Tenant { Id = TenantId, Name = "AEGIS Homolog", Slug = "aegis-homolog" });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Executive_SemNenhumIcrScore_DevolveIcrNulo()
    {
        await using var db = NewContext(TenantId);
        var dto = (await ControllerFor(db).Executive(CancellationToken.None)).Value;

        dto.Should().NotBeNull();
        dto!.Icr.Should().BeNull("sem NENHUM IcrScore medido o ICR é 'não avaliado', não um número");
        // O cliente e o instante de apuração continuam presentes mesmo sem ICR.
        dto.ClientName.Should().Be("AEGIS Homolog");
    }

    [Fact]
    public async Task Executive_SemIcrScore_NaoProduzO45Sintetico_MesmoComOsInsumosDoAntigoProxy()
    {
        // Reproduz o contexto EXATO que alimentava o proxy sintético (processos + perfil de pesos global),
        // porém SEM nenhum IcrScore. Com os pesos default e sem maturidade, o antigo fallback devolvia
        // exatamente "45 · Moderado"; agora não há caminho que fabrique número — o ICR é nulo.
        await using (var seed = NewContext(TenantId))
        {
            seed.Processes.Add(new BusinessProcess { Name = "Faturamento", ProcessValue = 4 });
            seed.IcrWeightProfiles.Add(new IcrWeightProfile { TenantId = null });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(TenantId);
        var dto = (await ControllerFor(db).Executive(CancellationToken.None)).Value;

        dto!.Icr.Should().BeNull("o proxy sintético foi removido — insumos de postura NÃO viram um ICR fabricado");
    }

    [Theory]
    [InlineData(new[] { 72.0 }, 72.0, "Alto")]
    [InlineData(new[] { 30.0, 50.0 }, 40.0, "Moderado")]
    [InlineData(new[] { 10.0, 20.0, 30.0 }, 20.0, "Controlado")]
    [InlineData(new[] { 85.0, 95.0 }, 90.0, "Critico")]
    public async Task Executive_ComIcrScoresReais_ProduzMediaEBandaCorretas(double[] scores, double expectedAvg, string expectedBand)
    {
        await using (var seed = NewContext(TenantId))
        {
            foreach (var s in scores)
                seed.IcrScores.Add(new IcrScore
                {
                    SubjectType = IcrSubjectType.Risk,
                    SubjectRef = Guid.NewGuid().ToString(),
                    Score = s,
                    Band = IcrBand.Moderado, // banda persistida é irrelevante: o dashboard recalcula pela média
                });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(TenantId);
        var dto = (await ControllerFor(db).Executive(CancellationToken.None)).Value;

        dto!.Icr.Should().NotBeNull("com IcrScores reais o dashboard apura e mostra o índice");
        dto.Icr!.Score.Should().Be(expectedAvg, "a média dos ICRs persistidos é preservada (comportamento inalterado)");
        dto.Icr.Band.Should().Be(expectedBand, "a banda vem do IcrScoringService sobre a média real (faixas 40/60/80)");
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>
    /// O controller instanciado direto: as dependências são serviços puros (<see cref="MaturityScoringService"/>,
    /// <see cref="IcrScoringService"/>) e o mesmo tenant ambiente do DbContext (fail-closed, isolado). A leitura
    /// composta da tela inicial entra como stub que FALHA se chamada — estes testes exercitam só o /executive, e
    /// um stub silencioso esconderia uma chamada acidental à outra superfície.
    /// </summary>
    private static DashboardController ControllerFor(AegisScoreDbContext db) =>
        new(db, new SystemTenantContext(TenantId), new MaturityScoringService(), new IcrScoringService(),
            new UnusedOverviewQuery());

    private sealed class UnusedOverviewQuery : IDashboardOverviewQuery
    {
        public Task<DashboardOverviewDto> GetAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("A tela inicial composta não participa destes testes do /executive.");
    }
}
