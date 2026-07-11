using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Testes da telemetria de ATIVOS (Identify / ID.AM) — o fluxo <see cref="TelemetryIngestionService.IngestAssetAsync"/>
/// → motor (<see cref="StubLlmClient"/> real) → <see cref="ControlStateWriter"/> sobre SQLite in-memory.
/// Provam a heurística de Identify pedida: sem EDR ou SO obsoleto REPROVA (NonCompliant); EDR ativo e zero
/// CVEs críticas APROVA (Compliant, 100%); o meio-termo recebe crédito parcial. Fonte sempre Telemetry.
/// </summary>
public sealed class AssetTelemetryTests : IDisposable
{
    private const int MaxPoints = 10;              // peso de ID.AM (tier médio) no catálogo NIST CSF 2.0
    private const string SubCode = "ID.AM-01";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public AssetTelemetryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task IngestAssetAsync_SemEdr_ClassificaNonCompliant_ComZeroPontos()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA);

        var verdict = await ingestion.IngestAssetAsync(new AssetTelemetrySignal(
            "AD Domain Controller 01", EdrCoverageStatus.Absent, OsLifecycleStatus.Supported,
            CriticalVulnerabilitiesCount: 0, IsCriticalAsset: true, SubCode));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "um ativo sem EDR está exposto");
        verdict.AwardedScore.Should().Be(0);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.Status.Should().Be(ControlStatus.NonCompliant);
        state.CurrentScore.Should().Be(0);
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task IngestAssetAsync_SistemaOperacionalObsoleto_ClassificaNonCompliant()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA);

        // EDR ativo, mas o SO em fim de vida (EOL) já reprova a postura do ativo.
        var verdict = await ingestion.IngestAssetAsync(new AssetTelemetrySignal(
            "Legacy File Server", EdrCoverageStatus.Active, OsLifecycleStatus.EndOfLife,
            CriticalVulnerabilitiesCount: 0, IsCriticalAsset: false, SubCode));

        verdict.Status.Should().Be(ControlStatus.NonCompliant, "SO em fim de vida é postura inaceitável");
        verdict.AwardedScore.Should().Be(0);
    }

    [Fact]
    public async Task IngestAssetAsync_EdrAtivoESemCvesCriticas_ClassificaCompliant_100()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA);

        var verdict = await ingestion.IngestAssetAsync(new AssetTelemetrySignal(
            "Microsoft 365 (Identidade)", EdrCoverageStatus.Active, OsLifecycleStatus.Supported,
            CriticalVulnerabilitiesCount: 0, IsCriticalAsset: true, SubCode));

        verdict.Status.Should().Be(ControlStatus.Compliant, "EDR ativo e zero CVEs críticas");
        verdict.AwardedScore.Should().Be(MaxPoints);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.CurrentScore.Should().Be(MaxPoints, "a telemetria de ativo íntegro leva ID.AM-01 a 100%");
        state.LastVerdictSource.Should().Be(VerdictSource.Telemetry);
    }

    [Fact]
    public async Task IngestAssetAsync_EdrAtivoComCvesCriticas_ClassificaCreditoParcial()
    {
        await using var db = NewContext(TenantA);
        var ingestion = IngestionFor(db, TenantA);

        // EDR presente e SO suportado, porém com CVEs críticas ativas: nem exposto nem íntegro → 50%.
        var verdict = await ingestion.IngestAssetAsync(new AssetTelemetrySignal(
            "Gateway VPN", EdrCoverageStatus.Active, OsLifecycleStatus.Supported,
            CriticalVulnerabilitiesCount: 3, IsCriticalAsset: true, SubCode));

        verdict.Status.Should().Be(ControlStatus.MitigatedByThirdParty, "EDR mitiga, mas CVEs críticas pendentes");
        verdict.AwardedScore.Should().Be(MaxPoints / 2);
    }

    // ---- infraestrutura do teste ----------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    /// <summary>Cadeia REAL de produção (ingestão de ativo → motor com Stub → writer) sob o tenant do contexto.</summary>
    private static ITelemetryIngestionService IngestionFor(AegisScoreDbContext db, Guid? tenantId)
    {
        var ctx = new SystemTenantContext(tenantId);
        var writer = new ControlStateWriter(db, ctx, NullLogger<ControlStateWriter>.Instance);
        var evaluator = new AegisAiEvaluatorService(db, new StubLlmClient(), ctx, writer);
        return new TelemetryIngestionService(evaluator, ctx);
    }

    /// <summary>Catálogo mínimo: o grafo até ID.AM-01 com peso conhecido.</summary>
    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var fn = new NistFunction { Code = "ID", Name = "IDENTIFY" };
        var cat = new NistCategory { Code = "ID.AM", Name = "Asset Management" };
        cat.Subcategories.Add(new NistSubcategory
        {
            Code = SubCode,
            Description = "Inventários de ativos são mantidos e atualizados.",
            MaxScorePoints = MaxPoints,
        });
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);

        ctx.FrameworkVersions.Add(fv);   // catálogo é dado de referência: não é ITenantOwned
        ctx.SaveChanges();
    }
}
