using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// Prova que a CONCESSÃO de acesso (<c>POST /api/v1/users/access</c>, via <see cref="UserManagementService.GrantAccessAsync"/>)
/// e o ONBOARDING de uma identidade EXISTENTE (<see cref="PlatformTenantUserService"/>) NÃO contornam as
/// proteções administrativas: ambos editam/reativam um membership existente e por isso passam pela MESMA
/// autoridade guardada (auto-rebaixamento, último admin, isolamento tenant-scoped). Regressão do bypass que
/// existia enquanto <c>ApplyUpdateAsync</c> não conhecia o ator nem aplicava as guardas.
/// </summary>
public sealed class GrantAccessGuardBypassTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public GrantAccessGuardBypassTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "A", Slug = "a", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "B", Slug = "b", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ---- /users/access (GrantAccessAsync) não contorna as guardas ----------------

    [Fact]
    public async Task GrantAccess_AutoRebaixamentoDeAdmin_EhBarrado()
    {
        var admin = await SeedIdentityAsync("admin@demo.example.com");
        await SeedMembershipAsync(TenantA, admin, TenantRole.TenantAdmin, active: true);
        // Outro admin para isolar do guard de ÚLTIMO admin — aqui provamos SÓ o auto-rebaixamento.
        await SeedMembershipAsync(TenantA, await SeedIdentityAsync("outro@demo.example.com"), TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await Users(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(admin, "Admin", TenantRole.Analyst, ActorAccountId: admin));   // ator == alvo

        result.Status.Should().Be(AccessGrantStatus.SelfDemotionForbidden,
            "conceder acesso a si com papel menor não pode virar auto-rebaixamento");
        (await db.Users.SingleAsync(u => u.IdentityAccountId == admin)).Role.Should().Be(TenantRole.TenantAdmin);
    }

    [Fact]
    public async Task GrantAccess_RebaixarUltimoAdmin_EhBarrado()
    {
        var admin = await SeedIdentityAsync("admin@demo.example.com");
        var membership = await SeedMembershipAsync(TenantA, admin, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await Users(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(admin, "Admin", TenantRole.Analyst, ActorAccountId: Guid.NewGuid()));

        result.Status.Should().Be(AccessGrantStatus.LastAdminProtected,
            "a concessão não pode rebaixar o último administrador ativo");
        (await db.Users.SingleAsync(u => u.Id == membership)).Role.Should().Be(TenantRole.TenantAdmin);
    }

    [Fact]
    public async Task GrantAccess_NaoAfetaMembershipDeOutroTenant()
    {
        var person = await SeedIdentityAsync("p@demo.example.com");
        var membershipB = await SeedMembershipAsync(TenantB, person, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await Users(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(person, "P em A", TenantRole.Analyst, ActorAccountId: Guid.NewGuid()));

        result.Status.Should().Be(AccessGrantStatus.Granted, "cria um NOVO membership no tenant A");
        await using var assert = NewContext(null);
        (await assert.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == membershipB)).Role
            .Should().Be(TenantRole.TenantAdmin, "o acesso admin no tenant B fica intacto");
        (await assert.Users.IgnoreQueryFilters().CountAsync(u => u.IdentityAccountId == person)).Should().Be(2);
    }

    // ---- Onboarding de identidade EXISTENTE não contorna as guardas --------------

    [Fact]
    public async Task Onboard_IdentidadeExistente_AutoRebaixamento_EhBarrado()
    {
        var admin = await SeedIdentityAsync("admin@demo.example.com");
        await SeedMembershipAsync(TenantA, admin, TenantRole.TenantAdmin, active: true);
        await SeedMembershipAsync(TenantA, await SeedIdentityAsync("outro@demo.example.com"), TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await Onboarding(db, TenantA).OnboardAsync(
            new OnboardTenantUserCommand("admin@demo.example.com", "Admin", TenantRole.Analyst, null, ActorAccountId: admin));

        result.Status.Should().Be(TenantUserOnboardingStatus.SelfDemotionForbidden);
        (await db.Users.SingleAsync(u => u.IdentityAccountId == admin)).Role.Should().Be(TenantRole.TenantAdmin);
    }

    [Fact]
    public async Task Onboard_IdentidadeExistente_RebaixarUltimoAdmin_EhBarrado()
    {
        var admin = await SeedIdentityAsync("admin@demo.example.com");
        var membership = await SeedMembershipAsync(TenantA, admin, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await Onboarding(db, TenantA).OnboardAsync(
            new OnboardTenantUserCommand("admin@demo.example.com", "Admin", TenantRole.Analyst, null, ActorAccountId: Guid.NewGuid()));

        result.Status.Should().Be(TenantUserOnboardingStatus.LastAdminProtected);
        (await db.Users.SingleAsync(u => u.Id == membership)).Role.Should().Be(TenantRole.TenantAdmin);
    }

    [Fact]
    public async Task Onboard_IdentidadeExistente_NaoAfetaOutroTenant()
    {
        var person = await SeedIdentityAsync("p@demo.example.com");
        var membershipB = await SeedMembershipAsync(TenantB, person, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await Onboarding(db, TenantA).OnboardAsync(
            new OnboardTenantUserCommand("p@demo.example.com", "P em A", TenantRole.Analyst, null, ActorAccountId: Guid.NewGuid()));

        result.Status.Should().Be(TenantUserOnboardingStatus.ExistingIdentityGranted);
        await using var assert = NewContext(null);
        (await assert.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == membershipB)).Role
            .Should().Be(TenantRole.TenantAdmin, "o acesso admin no tenant B fica intacto");
    }

    // ---- Fixture ----------------------------------------------------------------

    private async Task<Guid> SeedIdentityAsync(string email)
    {
        await using var db = NewContext(null);
        var account = new IdentityAccount
        {
            Email = email, PasswordHash = new Pbkdf2PasswordHasher().Hash("uma frase longa e boa"),
        };
        db.IdentityAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private async Task<Guid> SeedMembershipAsync(Guid tenantId, Guid accountId, TenantRole role, bool active)
    {
        await using var db = NewContext(tenantId);
        var membership = new User
        {
            TenantId = tenantId, IdentityAccountId = accountId, DisplayName = "Seed", Role = role, IsActive = active,
        };
        db.Users.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IUserManagementService Users(AegisScoreDbContext db, Guid? tenantId) =>
        new UserManagementService(db, new SystemTenantContext(tenantId), NullLogger<UserManagementService>.Instance);

    private static IPlatformTenantUserService Onboarding(AegisScoreDbContext db, Guid? tenantId)
    {
        var tenant = new SystemTenantContext(tenantId);
        var users = new UserManagementService(db, tenant, NullLogger<UserManagementService>.Instance);
        return new PlatformTenantUserService(
            db, tenant, users, new Pbkdf2PasswordHasher(),
            Options.Create(new FederationOptions { Mode = FederationMode.Local }),
            NullLogger<PlatformTenantUserService>.Instance);
    }
}
