using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// [AEGIS-AUD-021/027/032] Testes da projeção ÚNICA do workspace (<see cref="WorkspacePostureQuery"/>) sobre
/// SQLite in-memory. Provam: fail-closed sem tenant, NotEvaluated ≠ 0%, consistência score/cobertura/contagens,
/// paridade Função↔total, exclusão de framework inativo, isolamento por tenant e saúde de conectores (nunca
/// sincronizado ≠ saudável).
/// </summary>
public sealed class WorkspacePostureQueryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string PrAa = "PR.AA-01";   // peso 20 (Função PR)
    private const string PrDs = "PR.DS-01";   // peso 20 (Função PR)
    private const string DeCm = "DE.CM-01";   // peso 15 (Função DE)
    private const int EligibleMax = 55;
    private const int EligibleCount = 3;

    private readonly SqliteConnection _connection;

    public WorkspacePostureQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
        // ConnectorConfig tem FK para Tenant — os estados não, mas os conectores exigem a linha do tenant.
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SemTenantAmbiente_ProjecaoVazia_FailClosed()
    {
        await using var db = NewContext(null);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(null)).GetAsync();

        w.Overall.EvaluationState.Should().Be(nameof(ScoreEvaluationState.NotEvaluated));
        w.Overall.Percentage.Should().BeNull();
        w.Overall.EligibleControls.Should().Be(0, "sem tenant, nada é projetado — nem o catálogo");
        w.Functions.Should().BeEmpty();
        w.Connectors.Total.Should().Be(0);
    }

    [Fact]
    public async Task SemAvaliacoes_TudoNotEvaluated_NaoZero_ComElegivelDoCatalogo()
    {
        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Overall.Percentage.Should().BeNull("nada avaliado NÃO é 0% — é ausência de score");
        w.Overall.EvaluationState.Should().Be(nameof(ScoreEvaluationState.NotEvaluated));
        w.Overall.EligibleControls.Should().Be(EligibleCount, "o universo elegível vem do catálogo ativo");
        w.Overall.EligibleMaxScore.Should().Be(EligibleMax);
        w.Overall.EvaluatedControls.Should().Be(0);
        w.Overall.CoveragePercentage.Should().Be(0);
        w.Overall.NotEvaluatedControls.Should().Be(EligibleCount);
        w.Functions.Should().HaveCount(2, "PR e DE");
        w.Functions.Should().OnlyContain(f => f.Percentage == null && f.EvaluatedControls == 0);
    }

    [Fact]
    public async Task ScoreCoberturaContagens_Consistentes_EAvaliadoNaoExcedeElegivel()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);            // 20/20
        await SeedStateAsync(DeCm, ControlStatus.NonCompliant, 0);          // 0/15 (avaliado, 0% real)

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        var o = w.Overall;
        o.EvaluatedControls.Should().Be(2);
        o.CompliantControls.Should().Be(1);
        o.NonCompliantControls.Should().Be(1);
        o.MitigatedControls.Should().Be(0);
        o.NotEvaluatedControls.Should().Be(1, "PR.DS-01 ficou sem avaliação");
        o.AchievedScore.Should().Be(20);
        o.EvaluatedMaxScore.Should().Be(35);
        o.Percentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(20.0 / 35 * 100));
        o.CoveragePercentage.Should().Be(AegisScoreFormulaV1.RoundPercentage(35.0 / EligibleMax * 100));
        o.EvaluatedControls.Should().BeLessThanOrEqualTo(o.EligibleControls);
        o.EvaluatedMaxScore.Should().BeLessThanOrEqualTo(o.EligibleMaxScore);
        o.CoveragePercentage.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task SomaDasFuncoes_BateComOTotal()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedStateAsync(PrDs, ControlStatus.MitigatedByThirdParty, 10);
        await SeedStateAsync(DeCm, ControlStatus.NonCompliant, 0);

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Functions.Sum(f => f.EligibleControls).Should().Be(w.Overall.EligibleControls);
        w.Functions.Sum(f => f.EvaluatedControls).Should().Be(w.Overall.EvaluatedControls);
        w.Functions.Sum(f => f.CompliantControls).Should().Be(w.Overall.CompliantControls);
        w.Functions.Sum(f => f.NonCompliantControls).Should().Be(w.Overall.NonCompliantControls);
        w.Functions.Sum(f => f.MitigatedControls).Should().Be(w.Overall.MitigatedControls);
        w.Functions.Sum(f => f.NotEvaluatedControls).Should().Be(w.Overall.NotEvaluatedControls);

        var pr = w.Functions.Single(f => f.Code == "PR");
        pr.EvaluatedControls.Should().Be(2);
        pr.CompliantControls.Should().Be(1);
        pr.MitigatedControls.Should().Be(1);
    }

    [Fact]
    public async Task EstadoDeFrameworkInativo_NaoEntraNoScoreNemInflaCobertura()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedInactiveFrameworkStateAsync("OLD.XX-01", weight: 40, ControlStatus.Compliant, 40);

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Overall.EvaluatedControls.Should().Be(1, "só o estado do framework ATIVO conta");
        w.Overall.AchievedScore.Should().Be(20);
        w.Overall.EligibleControls.Should().Be(EligibleCount);
        w.Overall.CoveragePercentage.Should().BeLessThanOrEqualTo(100);
        w.Functions.Should().NotContain(f => f.Code == "OLD");
    }

    [Fact]
    public async Task IsolamentoPorTenant_BNaoVeAPosturaNemConectoresDeA()
    {
        await SeedStateAsync(PrAa, ControlStatus.Compliant, 20);
        await SeedConnectorAsync(TenantA, ConnectorCapability.Siem, ConnectorStatus.Healthy, DateTimeOffset.UtcNow);

        await using var db = NewContext(TenantB);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantB)).GetAsync();

        w.Overall.EvaluatedControls.Should().Be(0, "o tenant B não vê os estados de A");
        w.Overall.Percentage.Should().BeNull();
        w.Connectors.Total.Should().Be(0, "o tenant B não vê os conectores de A");
    }

    [Fact]
    public async Task ConectorNuncaSincronizado_NaoEhContadoComoSaudavel()
    {
        await SeedConnectorAsync(TenantA, ConnectorCapability.Siem, ConnectorStatus.Unknown, lastSyncAt: null);   // nunca sincronizou
        await SeedConnectorAsync(TenantA, ConnectorCapability.Edr, ConnectorStatus.Healthy, DateTimeOffset.UtcNow);

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        w.Connectors.Total.Should().Be(2);
        w.Connectors.Healthy.Should().Be(1, "só o que sincronizou com status Healthy conta");
        w.Connectors.NeverSynced.Should().Be(1, "o nunca sincronizado é NeverSynced, jamais saudável");
        w.Connectors.Items.Should().Contain(i => !i.EverSynced && i.Status == nameof(ConnectorStatus.Unknown));
        w.Connectors.LastSyncAt.Should().NotBeNull("ao menos um conector sincronizou");
    }

    [Fact]
    public async Task Severidades_DerivadasDoStatus_ContamOsAvaliados()
    {
        await SeedStateAsync(PrAa, ControlStatus.NonCompliant, 0);          // Critical
        await SeedStateAsync(PrDs, ControlStatus.MitigatedByThirdParty, 10); // Medium
        await SeedStateAsync(DeCm, ControlStatus.Compliant, 15);           // Low

        await using var db = NewContext(TenantA);
        var w = await new WorkspacePostureQuery(db, new SystemTenantContext(TenantA)).GetAsync();

        int SevOf(string s) => w.Overall.Severities.Single(x => x.Severity == s).Count;
        SevOf(nameof(SeverityLevel.Critical)).Should().Be(1);
        SevOf(nameof(SeverityLevel.Medium)).Should().Be(1);
        SevOf(nameof(SeverityLevel.Low)).Should().Be(1);
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
            SubcategoryId = subId, Status = status, CurrentScore = score, LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedConnectorAsync(
        Guid tenantId, ConnectorCapability capability, ConnectorStatus status, DateTimeOffset? lastSyncAt)
    {
        await using var db = NewContext(tenantId);
        db.Connectors.Add(new ConnectorConfig
        {
            TenantId = tenantId, Provider = ConnectorProvider.Generic, Capability = capability,
            DisplayName = $"Generic {capability} {Guid.NewGuid():N}", AuthType = ConnectorAuthType.ApiKey, Enabled = true,
            LastStatus = status, LastSyncAt = lastSyncAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedInactiveFrameworkStateAsync(string subCode, int weight, ControlStatus status, int score)
    {
        await using var db = NewContext(TenantA);
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0 (legado)", IsActive = false };
        var fn = new NistFunction { Code = "OLD", Name = "Legacy", Order = 99 };
        var cat = new NistCategory { Code = "OLD.XX", Name = "Legacy" };
        cat.Subcategories.Add(new NistSubcategory { Code = subCode, Description = "x", MaxScorePoints = weight });
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);
        db.FrameworkVersions.Add(fv);
        await db.SaveChangesAsync();

        var subId = await db.Subcategories.Where(s => s.Code == subCode).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId, Status = status, CurrentScore = score, LastVerdictSource = VerdictSource.Telemetry,
        });
        await db.SaveChangesAsync();
    }

    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { Code = "PR", Name = "PROTECT", Order = 2 };
        var prAa = new NistCategory { Code = "PR.AA", Name = "Identity" };
        prAa.Subcategories.Add(new NistSubcategory { Code = PrAa, Description = "x", MaxScorePoints = 20 });
        var prDs = new NistCategory { Code = "PR.DS", Name = "Data" };
        prDs.Subcategories.Add(new NistSubcategory { Code = PrDs, Description = "x", MaxScorePoints = 20 });
        pr.Categories.Add(prAa);
        pr.Categories.Add(prDs);

        var de = new NistFunction { Code = "DE", Name = "DETECT", Order = 3 };
        var deCm = new NistCategory { Code = "DE.CM", Name = "Monitoring" };
        deCm.Subcategories.Add(new NistSubcategory { Code = DeCm, Description = "x", MaxScorePoints = 15 });
        de.Categories.Add(deCm);

        fv.Functions.Add(pr);
        fv.Functions.Add(de);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();
    }
}
