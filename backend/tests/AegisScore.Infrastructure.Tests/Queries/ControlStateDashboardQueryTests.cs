using AegisScore.Application.Queries;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Scoring;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Queries;

/// <summary>
/// Testes da <see cref="ControlStateDashboardQuery"/> sobre SQLite in-memory. Compilar não prova que uma
/// query EF roda: a tradução LINQ → SQL falha em RUNTIME. Estes testes executam a projeção de verdade —
/// incluindo o JOIN com o catálogo e a conversão dos enums em string — e travam o isolamento fail-closed.
/// </summary>
public sealed class ControlStateDashboardQueryTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private Guid _prAaId;
    private Guid _gvOcId;

    public ControlStateDashboardQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetDashboardAsync_ProjetaOEstadoComCodigoEPesoDoCatalogo()
    {
        await SeedStateAsync(TenantA, _prAaId, ControlStatus.Compliant, 20, VerdictSource.Telemetry, "telemetria: MFA ativo");

        await using var db = NewContext(TenantA);
        var rows = await QueryFor(db).GetDashboardAsync();

        var row = rows.Should().ContainSingle().Subject;
        row.SubcategoryId.Should().Be(_prAaId);
        row.SubcategoryCode.Should().Be("PR.AA-01", "o código vem do JOIN com o catálogo");
        row.ScorePoints.Should().Be(20);
        row.MaxScorePoints.Should().Be(20, "o denominador vem do catálogo, nunca do estado do tenant");
        row.ControlStatus.Should().Be("Compliant", "enums cruzam a fronteira como string");
        row.LastVerdictSource.Should().Be("Telemetry");
        row.AiEvidence.Should().Be("telemetria: MFA ativo");
    }

    [Fact]
    public async Task GetDashboardAsync_OrdenaPeloCodigoNist()
    {
        await SeedStateAsync(TenantA, _prAaId, ControlStatus.Compliant, 20, VerdictSource.Telemetry, "a");
        await SeedStateAsync(TenantA, _gvOcId, ControlStatus.MitigatedByThirdParty, 2, VerdictSource.Documentary, "b");

        await using var db = NewContext(TenantA);
        var rows = await QueryFor(db).GetDashboardAsync();

        rows.Select(r => r.SubcategoryCode).Should().ContainInOrder("GV.OC-01", "PR.AA-01");
        rows.Single(r => r.SubcategoryCode == "GV.OC-01").LastVerdictSource.Should().Be("Documentary");
    }

    [Fact]
    public async Task GetDashboardAsync_NaoEnxergaOEstadoDeOutroTenant()
    {
        await SeedStateAsync(TenantA, _prAaId, ControlStatus.Compliant, 20, VerdictSource.Telemetry, "de A");
        await SeedStateAsync(TenantB, _gvOcId, ControlStatus.NonCompliant, 0, VerdictSource.Telemetry, "de B");

        await using var dbA = NewContext(TenantA);
        var rowsA = await QueryFor(dbA).GetDashboardAsync();

        // Sem nenhum .Where(TenantId) na query: o Global Query Filter é quem isola.
        rowsA.Should().ContainSingle().Which.SubcategoryCode.Should().Be("PR.AA-01");
    }

    [Fact]
    public async Task GetDashboardAsync_SemTenantResolvido_RetornaVazio()
    {
        await SeedStateAsync(TenantA, _prAaId, ControlStatus.Compliant, 20, VerdictSource.Telemetry, "de A");

        // Fail-CLOSED: sem tenant ambiente o filtro não vaza nada — o oposto de expor a base inteira.
        await using var db = NewContext(null);
        var rows = await QueryFor(db).GetDashboardAsync();

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDashboardAsync_DesserializaOChecklistTecnicoPersistido()
    {
        // O ChecksJson gravado no ledger (pelo ControlStateWriter) é devolvido como lista tipada ao HUD.
        const string checksJson = """[{"Name":"Endpoint Encrypted","Passed":false,"Details":"90% (mínimo 95%)."}]""";
        await SeedStateAsync(TenantA, _prAaId, ControlStatus.NonCompliant, 0, VerdictSource.Telemetry, "reprovado", checksJson);

        await using var db = NewContext(TenantA);
        var row = (await QueryFor(db).GetDashboardAsync()).Should().ContainSingle().Subject;

        row.Checks.Should().ContainSingle();
        row.Checks[0].Name.Should().Be("Endpoint Encrypted");
        row.Checks[0].Passed.Should().BeFalse("o checklist técnico atravessa persistência → leitura íntegro");
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>Grava um estado sob o tenant informado (TenantId é carimbado pelo StampTenant).</summary>
    private async Task SeedStateAsync(
        Guid tenantId, Guid subcategoryId, ControlStatus status, int score, VerdictSource source, string evidence,
        string? checksJson = null)
    {
        await using var db = NewContext(tenantId);
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subcategoryId,
            Status = status,
            CurrentScore = score,
            LastVerdictSource = source,
            AiEvidence = evidence,
            ChecksJson = checksJson,
        });
        await db.SaveChangesAsync();
    }

    private void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };

        var pr = new NistFunction { Code = "PR", Name = "PROTECT" };
        var prAa = new NistCategory { Code = "PR.AA", Name = "Identity" };
        var prAaSub = new NistSubcategory { Code = "PR.AA-01", Description = "Identities managed", MaxScorePoints = 20 };
        prAa.Subcategories.Add(prAaSub);
        pr.Categories.Add(prAa);

        var gv = new NistFunction { Code = "GV", Name = "GOVERN" };
        var gvOc = new NistCategory { Code = "GV.OC", Name = "Org Context" };
        var gvOcSub = new NistSubcategory { Code = "GV.OC-01", Description = "Mission understood", MaxScorePoints = 5 };
        gvOc.Subcategories.Add(gvOcSub);
        gv.Categories.Add(gvOc);

        fv.Functions.Add(pr);
        fv.Functions.Add(gv);
        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();

        _prAaId = prAaSub.Id;
        _gvOcId = gvOcSub.Id;
    }

    /// <summary>
    /// A consulta com a auditoria de frescor DESLIGADA (0 horas) — estes casos exercitam a projeção do
    /// dashboard, não o TTL. O relógio real serve porque, sem janela, nenhuma data é comparada.
    /// O TTL tem cobertura própria em <c>SignalFreshnessTests</c>.
    /// </summary>
    private static ControlStateDashboardQuery QueryFor(AegisScoreDbContext db) =>
        new(db, Options.Create(new ScoringOptions { DefaultSignalFreshnessHours = 0 }), TimeProvider.System);
}
