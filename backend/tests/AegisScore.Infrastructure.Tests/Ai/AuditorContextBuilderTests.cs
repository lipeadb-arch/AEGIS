using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Contexto tenant-scoped do Auditor sobre SQLite in-memory. Prova que a fundamentação usa SOMENTE o tenant
/// ambiente: a lacuna e o score do tenant A aparecem para A e NÃO vazam para B (isolamento pelo Global Query
/// Filter). É a garantia de que o Auditor nunca funda uma resposta em dados de outro cliente.
/// </summary>
public sealed class AuditorContextBuilderTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string PrAa = "PR.AA-01";

    private readonly SqliteConnection _connection;

    public AuditorContextBuilderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();

        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var pr = new NistFunction { Code = "PR", Name = "PROTECT", Order = 2 };
        var cat = new NistCategory { Code = "PR.AA", Name = "Identity" };
        cat.Subcategories.Add(new NistSubcategory { Code = PrAa, Description = "Identities are managed", MaxScorePoints = 20 });
        pr.Categories.Add(cat);
        fv.Functions.Add(pr);
        ctx.FrameworkVersions.Add(fv);
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Contexto_TrazLacunaEScoreDoTenantAmbiente()
    {
        await SeedNonCompliantAsync(TenantA, "MFA ausente em contas privilegiadas");

        await using var db = NewContext(TenantA);
        var ctx = await BuilderFor(db, TenantA).BuildAsync();

        ctx.NonCompliantControls.Should().Be(1);
        ctx.TopGaps.Should().ContainSingle().Which.SubcategoryCode.Should().Be(PrAa);
        ctx.TopGaps[0].Reason.Should().Contain("MFA", "a razão da lacuna vem do estado real do controle");
    }

    [Fact]
    public async Task Contexto_NaoVazaParaOutroTenant()
    {
        await SeedNonCompliantAsync(TenantA, "lacuna só do tenant A");

        await using var db = NewContext(TenantB);
        var ctx = await BuilderFor(db, TenantB).BuildAsync();

        ctx.TopGaps.Should().BeEmpty("o contexto é isolado pelo Global Query Filter — nada do tenant A vaza para B");
        ctx.NonCompliantControls.Should().Be(0);
    }

    private async Task SeedNonCompliantAsync(Guid tenantId, string reason)
    {
        await using var db = NewContext(tenantId);
        var subId = await db.Subcategories.Where(s => s.Code == PrAa).Select(s => s.Id).SingleAsync();
        db.TenantControlStates.Add(new TenantControlState
        {
            SubcategoryId = subId,
            Status = ControlStatus.NonCompliant,
            CurrentScore = 0,
            LastVerdictSource = VerdictSource.Telemetry,
            AiEvidence = reason,
        });
        await db.SaveChangesAsync();
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static AuditorContextBuilder BuilderFor(AegisScoreDbContext db, Guid tenantId) =>
        new(db, new WorkspacePostureQuery(db, new SystemTenantContext(tenantId)));
}
