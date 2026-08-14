using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe (infra AEGIS_TEST_PG já existente)
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// O que o SQLite não prova: a proteção do ÚLTIMO administrador SOB CONCORRÊNCIA REAL. Duas requisições
/// simultâneas removem/rebaixam os DOIS últimos administradores de um tenant — sem a trava de linha
/// (<c>FOR UPDATE</c>), o write-skew de READ COMMITTED deixaria as duas passarem e zeraria os administradores.
/// Com a trava, exatamente UMA vence e a outra recebe <see cref="MembershipAdminStatus.LastAdminProtected"/>,
/// e o tenant NUNCA fica sem administrador ativo. Gated por <c>AEGIS_TEST_PG</c>; cada teste cria/destrói um
/// database próprio (nunca toca o <c>aegis_dev</c>). Repetido algumas vezes — a garantia é do lock, não do timing.
/// </summary>
public sealed class LastAdminConcurrencyPostgresTests
{
    private readonly ITestOutputHelper _output;
    public LastAdminConcurrencyPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConcurrentDeactivation_OfLastTwoAdmins_NeverLeavesTenantWithoutAdmin_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await EnsureSchemaAsync(opt);

        for (var i = 0; i < 5; i++)
        {
            var (tenant, mA, mB) = await SeedFreshTenantWithTwoAdminsAsync(opt);

            var results = await Task.WhenAll(
                RunAsync(opt, tenant, svc => svc.SetMembershipStatusAsync(
                    new SetMembershipStatusCommand(mA, Guid.NewGuid(), Active: false))),
                RunAsync(opt, tenant, svc => svc.SetMembershipStatusAsync(
                    new SetMembershipStatusCommand(mB, Guid.NewGuid(), Active: false))));

            AssertExactlyOneSurvived(results);
            await AssertOneActiveAdminAsync(opt, tenant);
        }
    }

    [Fact]
    public async Task ConcurrentDemotion_OfLastTwoAdmins_NeverLeavesTenantWithoutAdmin_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await EnsureSchemaAsync(opt);

        for (var i = 0; i < 5; i++)
        {
            var (tenant, mA, mB) = await SeedFreshTenantWithTwoAdminsAsync(opt);

            var results = await Task.WhenAll(
                RunAsync(opt, tenant, svc => svc.UpdateMembershipAsync(
                    new UpdateMembershipCommand(mA, Guid.NewGuid(), null, TenantRole.Analyst))),
                RunAsync(opt, tenant, svc => svc.UpdateMembershipAsync(
                    new UpdateMembershipCommand(mB, Guid.NewGuid(), null, TenantRole.Analyst))));

            AssertExactlyOneSurvived(results);
            await AssertOneActiveAdminAsync(opt, tenant);
        }
    }

    // ---- helpers ----

    private static void AssertExactlyOneSurvived(MembershipAdminResult[] results)
    {
        results.Count(r => r.Status == MembershipAdminStatus.LastAdminProtected)
            .Should().Be(1, "exatamente uma das operações concorrentes é barrada");
        results.Count(r => r.Status == MembershipAdminStatus.Updated)
            .Should().Be(1, "e exatamente a outra passa");
    }

    private static async Task AssertOneActiveAdminAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        (await db.Users.CountAsync(u => u.Role == TenantRole.TenantAdmin && u.IsActive))
            .Should().Be(1, "o tenant NUNCA fica sem administrador ativo (write-skew fechado pelo FOR UPDATE)");
    }

    /// <summary>Uma "requisição": DbContext + serviço PRÓPRIOS (conexão própria) para concorrência real.</summary>
    private static Task<MembershipAdminResult> RunAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant,
        Func<IUserManagementService, Task<MembershipAdminResult>> op) =>
        Task.Run(async () =>
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            var svc = new UserManagementService(
                db, new SystemTenantContext(tenant), NullLogger<UserManagementService>.Instance);
            return await op(svc);
        });

    private static async Task<(Guid tenant, Guid mA, Guid mB)> SeedFreshTenantWithTwoAdminsAsync(
        DbContextOptions<AegisScoreDbContext> opt)
    {
        var tenant = Guid.NewGuid();
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
        db.Tenants.Add(new Tenant { Id = tenant, Name = "T", Slug = $"t-{tenant:N}", Status = TenantStatus.Active });

        var a = new IdentityAccount { Email = $"a-{Guid.NewGuid():N}@demo.example.com" };
        var b = new IdentityAccount { Email = $"b-{Guid.NewGuid():N}@demo.example.com" };
        db.IdentityAccounts.AddRange(a, b);

        var mA = new User { TenantId = tenant, Account = a, DisplayName = "A", Role = TenantRole.TenantAdmin, IsActive = true };
        var mB = new User { TenantId = tenant, Account = b, DisplayName = "B", Role = TenantRole.TenantAdmin, IsActive = true };
        db.Users.AddRange(mA, mB);
        await db.SaveChangesAsync();
        return (tenant, mA.Id, mB.Id);
    }

    private static async Task EnsureSchemaAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        await db.Database.EnsureCreatedAsync();
    }
}
