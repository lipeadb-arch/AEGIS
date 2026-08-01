using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// [AEGIS-AUD-001/002] Testes do <see cref="CurrentScoreQuery"/> sobre SQLite in-memory (a tradução LINQ→SQL
/// do JOIN elegível roda de verdade). Provam: NotEvaluated ≠ 0%, 0% real por NonCompliant, cobertura
/// separada do score, a PARIDADE real com a agregação compartilhada do snapshot (<see cref="AegisScoreAggregator"/>),
/// e que estado de framework INATIVO não entra no score nem infla a cobertura.
/// </summary>
public sealed class CurrentScoreQueryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string PrAa = "PR.AA-01";   // peso 20
    private const string PrDs = "PR.DS-01";   // peso 20
    private const string DeCm = "DE.CM-01";   // peso 15
    private const int EligibleMax = 55;       // 20 + 20 + 15
    private const int EligibleCount = 3;

    private readonly SqliteConnection _connection;

    public CurrentScoreQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetCurrentAsync_SemAvaliacoes_NaoRetorna0PorCento_MasNotEvaluated()
    {
        await using var db = NewContext(TenantA);
        var dto = await new CurrentScoreQuery(db).GetCurrentAsync();

        dto.EvaluatedControls.Should().Be(0);
        dto.Percentage.Should().BeNull("nenhum controle avaliado NÃO é 0% — é ausência de score");
        dto.EvaluationState.Should().Be(nameof(ScoreEvaluationState.NotEvaluated));
        dto.EligibleControls.Should().Be(EligibleCount, "o denominador de cobertura vem do catálogo ativo");
        dto.EligibleMaxScore.Should().Be(EligibleMax);
        dto.NotEvaluatedControls.Should().Be(EligibleCount);
        dto.FormulaVersion.Should().Be(AegisScoreFormulaV1.Version);
    }

    [Fact]
    public async Task GetCurrentAsync_UmControleNonCompliant_TemScoreRealDeZero_DistintoDeNotEvaluated()
    {
        await SeedStateAsync(PrAa, ControlStatus.NonCompliant, 0);

        await using var db = NewContext(TenantA);
        var dto = await new CurrentScoreQuery(db).GetCurrentAsync();

        dto.EvaluatedControls.Should().Be(1);
        dto.Percentage.Should().Be(0, "há avaliação: 0% é um score real (o controle está no denominador)");
        dto.EvaluationState.Should().Be(nameof(ScoreEvaluationState.Evaluated));
    }

    [Fact]
    public async Task GetCurrentAsync_SeparaScoreDeCobertura()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);   // 20/20
        await SeedStateAsync(DeCm, ControlStatus.NonCompliant, 0); // 0/15

        await using var db = NewContext(TenantA);
        var dto = await new CurrentScoreQuery(db).GetCurrentAsync();

        dto.AchievedScore.Should().Be(20);
        dto.EvaluatedMaxScore.Should().Be(35, "só os controles AVALIADOS entram no denominador do score");
        dto.Percentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(20.0 / 35 * 100));
        dto.CoveragePercentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(35.0 / EligibleMax * 100));
        dto.CoveragePercentage.Should().NotBe(dto.Percentage!.Value, "score e cobertura são eixos distintos");
    }

    [Fact]
    public async Task GetCurrentAsync_TemParidadeComAAgregacaoDoSnapshot()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedStateAsync(PrDs, ControlStatus.MitigatedByThirdParty, 10);

        await using var db = NewContext(TenantA);
        var dto = await new CurrentScoreQuery(db).GetCurrentAsync();

        // A MESMA autoridade de agregação que o AegisScoreSnapshotWorker usa para gravar a foto diária —
        // paridade REAL (mesma rotina), não uma reimplementação da soma no teste.
        var snap = await AegisScoreAggregator.AggregateAsync(db);

        dto.AchievedScore.Should().Be(snap.AchievedScore, "o numerador do score atual = o do snapshot");
        dto.EvaluatedMaxScore.Should().Be(snap.EvaluatedMaxScore, "o denominador avaliado = o do snapshot");
        dto.EvaluatedControls.Should().Be(snap.EvaluatedControls);

        // E o percentual lido da foto (TotalAchievedScore/TotalMaxScore = o que o worker grava) bate com o DTO.
        var trend = new TenantTrendDto(new DateOnly(2026, 7, 31), snap.AchievedScore, snap.EvaluatedMaxScore);
        dto.Percentage.Should().Be(trend.Percentage, "current score e snapshot derivam o percentual pela mesma autoridade");
    }

    [Fact]
    public async Task GetCurrentAsync_EstadoDeFrameworkInativo_NaoEntraNoScoreNemInflaCobertura()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);                       // 20/20, catálogo ATIVO
        await SeedInactiveFrameworkStateAsync("OLD.XX-01", weight: 40, ControlStatus.Compliant, 40);   // versão antiga

        await using var db = NewContext(TenantA);
        var dto = await new CurrentScoreQuery(db).GetCurrentAsync();

        dto.EvaluatedControls.Should().Be(1, "só o estado do framework ATIVO é avaliado");
        dto.AchievedScore.Should().Be(20, "o estado inativo (40 pts) não entra no numerador");
        dto.EvaluatedMaxScore.Should().Be(20, "o peso do estado inativo não entra no denominador avaliado");
        dto.EvaluatedMaxScore.Should().BeLessThanOrEqualTo(dto.EligibleMaxScore, "avaliado ≤ elegível");
        dto.EligibleControls.Should().Be(EligibleCount, "o universo elegível é só o catálogo ativo");
        dto.EligibleMaxScore.Should().Be(EligibleMax);
        dto.CoveragePercentage.Should().BeLessThanOrEqualTo(100, "cobertura nunca ultrapassa 100%");
        dto.CoveragePercentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(20.0 / EligibleMax * 100));

        // A MESMA exclusão vale para a foto diária (a autoridade é compartilhada).
        var snap = await AegisScoreAggregator.AggregateAsync(db);
        snap.EvaluatedControls.Should().Be(1);
        snap.AchievedScore.Should().Be(20);
        snap.EvaluatedMaxScore.Should().Be(20);
    }

    // ---- fixture --------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private async Task SeedStateAsync(string subCode, ControlStatus status, int score)
    {
        await using var db = NewContext(TenantA);
        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId,
            Status = status,
            CurrentScore = score,
            LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Semeia uma subcategoria numa versão ANTIGA (inativa) do framework e um estado do tenant para ela.</summary>
    private async Task SeedInactiveFrameworkStateAsync(string subCode, int weight, ControlStatus status, int score)
    {
        await using var db = NewContext(TenantA);
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0 (legado)", IsActive = false };   // Name é único
        var fn = new NistFunction { Code = "OLD", Name = "Legacy" };
        var cat = new NistCategory { Code = "OLD.XX", Name = "Legacy" };
        cat.Subcategories.Add(new NistSubcategory { Code = subCode, Description = "x", MaxScorePoints = weight });
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);
        db.FrameworkVersions.Add(fv);
        await db.SaveChangesAsync();

        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId,
            Status = status,
            CurrentScore = score,
            LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { Code = "PR", Name = "PROTECT" };
        var prAa = new NistCategory { Code = "PR.AA", Name = "Identity" };
        prAa.Subcategories.Add(new NistSubcategory { Code = PrAa, Description = "x", MaxScorePoints = 20 });
        var prDs = new NistCategory { Code = "PR.DS", Name = "Data" };
        prDs.Subcategories.Add(new NistSubcategory { Code = PrDs, Description = "x", MaxScorePoints = 20 });
        pr.Categories.Add(prAa);
        pr.Categories.Add(prDs);

        var de = new NistFunction { Code = "DE", Name = "DETECT" };
        var deCm = new NistCategory { Code = "DE.CM", Name = "Monitoring" };
        deCm.Subcategories.Add(new NistSubcategory { Code = DeCm, Description = "x", MaxScorePoints = 15 });
        de.Categories.Add(deCm);

        fv.Functions.Add(pr);
        fv.Functions.Add(de);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();
    }
}
