using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// Administração tenant-scoped de acessos (listagem, edição, desativação/reativação) do
/// <see cref="UserManagementService"/>. Harness dos demais (SQLite in-memory), então o Global Query Filter,
/// o índice único e a revogação por <c>ExecuteUpdate</c> são exercitados de verdade. A CORREÇÃO SOB
/// CONCORRÊNCIA REAL do último administrador é validada à parte, em PostgreSQL descartável
/// (<see cref="LastAdminConcurrencyPostgresTests"/>); aqui provamos a lógica das guardas em desfechos únicos.
/// </summary>
public sealed class MembershipAdministrationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public MembershipAdministrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Cliente A", Slug = "cliente-a", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Cliente B", Slug = "cliente-b", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Cenário 1: listagem ISOLADA pelo tenant --------------------------------

    [Fact]
    public async Task ListUsers_SoDevolveMembershipsDoTenantAmbiente()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var bob = await SeedIdentityAsync("bob@demo.example.com", withPassword: false);
        await SeedMembershipAsync(TenantA, ana, TenantRole.TenantAdmin, active: true);
        await SeedMembershipAsync(TenantA, bob, TenantRole.Analyst, active: false);
        // A MESMA Ana também acessa o tenant B — nunca pode aparecer na listagem de A.
        await SeedMembershipAsync(TenantB, ana, TenantRole.Manager, active: true);

        await using var db = NewContext(TenantA);
        var list = await ServiceFor(db, TenantA).ListUsersAsync();

        list.Should().HaveCount(2, "só os acessos do tenant ambiente, nunca de outro cliente");
        list.Select(u => u.Email).Should().BeEquivalentTo("ana@demo.example.com", "bob@demo.example.com");
        list.Single(u => u.Email == "bob@demo.example.com").HasLocalCredential
            .Should().BeFalse("bob é federated-only (sem senha local)");
        list.Single(u => u.Email == "ana@demo.example.com").HasLocalCredential.Should().BeTrue();
    }

    [Fact]
    public async Task ListUsers_SemTenantNoContexto_FalhaFechado()
    {
        await using var db = NewContext(null);
        var act = () => ServiceFor(db, null).ListUsersAsync();
        await act.Should().ThrowAsync<AegisScore.Application.Abstractions.TenantSecurityException>();
    }

    // ---- Cenário 5: edição/desativação recusa usuário CROSS-TENANT (mesmo 404) ---

    [Fact]
    public async Task Update_MembershipDeOutroTenant_EhNaoEncontrado()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var membershipB = await SeedMembershipAsync(TenantB, ana, TenantRole.Analyst, active: true);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).UpdateMembershipAsync(
            new UpdateMembershipCommand(membershipB, Guid.NewGuid(), "Hack", TenantRole.TenantAdmin));

        result.Status.Should().Be(MembershipAdminStatus.NotFound, "o membership de B é invisível ao tenant A");
        await using var assert = NewContext(TenantB);
        (await assert.Users.SingleAsync(u => u.Id == membershipB)).Role
            .Should().Be(TenantRole.Analyst, "nada foi alterado no tenant B");
    }

    [Fact]
    public async Task Deactivate_MembershipDeOutroTenant_EhNaoEncontrado()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var membershipB = await SeedMembershipAsync(TenantB, ana, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).SetMembershipStatusAsync(
            new SetMembershipStatusCommand(membershipB, Guid.NewGuid(), Active: false));

        result.Status.Should().Be(MembershipAdminStatus.NotFound);
        await using var assert = NewContext(TenantB);
        (await assert.Users.SingleAsync(u => u.Id == membershipB)).IsActive.Should().BeTrue();
    }

    // ---- Cenário 6: auto-lockout e último administrador (desfecho único) ----------

    [Fact]
    public async Task Deactivate_ASiMesmo_EhBarrado()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var membership = await SeedMembershipAsync(TenantA, ana, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).SetMembershipStatusAsync(
            new SetMembershipStatusCommand(membership, ana, Active: false));   // ator == alvo

        result.Status.Should().Be(MembershipAdminStatus.SelfDeactivationForbidden);
        (await db.Users.SingleAsync(u => u.Id == membership)).IsActive.Should().BeTrue("não se tranca para fora");
    }

    [Fact]
    public async Task Update_AutoRebaixamentoDeAdministrador_EhBarrado()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var membership = await SeedMembershipAsync(TenantA, ana, TenantRole.TenantAdmin, active: true);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).UpdateMembershipAsync(
            new UpdateMembershipCommand(membership, ana, null, TenantRole.Manager));   // ator rebaixa a si

        result.Status.Should().Be(MembershipAdminStatus.SelfDemotionForbidden);
        (await db.Users.SingleAsync(u => u.Id == membership)).Role
            .Should().Be(TenantRole.TenantAdmin, "o próprio papel de administrador é preservado");
    }

    [Fact]
    public async Task Deactivate_UltimoAdministradorAtivo_EhBarrado()
    {
        var admin = await SeedIdentityAsync("admin@demo.example.com", withPassword: true);
        var membership = await SeedMembershipAsync(TenantA, admin, TenantRole.TenantAdmin, active: true);
        // Um analista (não conta como administrador) existe no tenant.
        var analyst = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await SeedMembershipAsync(TenantA, analyst, TenantRole.Analyst, active: true);

        await using var db = NewContext(TenantA);
        // Ator distinto do alvo (defesa em profundidade): mesmo assim, remover o ÚLTIMO admin é barrado.
        var result = await ServiceFor(db, TenantA).SetMembershipStatusAsync(
            new SetMembershipStatusCommand(membership, analyst, Active: false));

        result.Status.Should().Be(MembershipAdminStatus.LastAdminProtected);
        (await db.Users.SingleAsync(u => u.Id == membership)).IsActive.Should().BeTrue("o tenant não fica sem admin");
    }

    [Fact]
    public async Task Demote_UltimoAdministradorAtivo_EhBarrado_MasComOutroAdmin_Passa()
    {
        var a = await SeedIdentityAsync("a@demo.example.com", withPassword: true);
        var b = await SeedIdentityAsync("b@demo.example.com", withPassword: true);
        var mA = await SeedMembershipAsync(TenantA, a, TenantRole.TenantAdmin, active: true);
        var mB = await SeedMembershipAsync(TenantA, b, TenantRole.TenantAdmin, active: true);
        var actor = Guid.NewGuid();   // um operador externo qualquer (≠ alvos)

        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        // Rebaixar A com B ainda admin: permitido.
        (await svc.UpdateMembershipAsync(new UpdateMembershipCommand(mA, actor, null, TenantRole.Manager)))
            .Status.Should().Be(MembershipAdminStatus.Updated);
        // Agora B é o último admin ativo: rebaixá-lo é barrado.
        (await svc.UpdateMembershipAsync(new UpdateMembershipCommand(mB, actor, null, TenantRole.Analyst)))
            .Status.Should().Be(MembershipAdminStatus.LastAdminProtected);

        (await db.Users.SingleAsync(u => u.Id == mB)).Role.Should().Be(TenantRole.TenantAdmin);
    }

    [Fact]
    public async Task Reactivate_EhIdempotente_NaoRestauraSessoes()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var membership = await SeedMembershipAsync(TenantA, ana, TenantRole.Analyst, active: false);
        var revokedToken = await SeedRefreshTokenAsync(TenantA, membership, revoked: true);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).SetMembershipStatusAsync(
            new SetMembershipStatusCommand(membership, Guid.NewGuid(), Active: true));

        result.Status.Should().Be(MembershipAdminStatus.Updated);
        (await db.Users.SingleAsync(u => u.Id == membership)).IsActive.Should().BeTrue();
        (await db.UserRefreshTokens.SingleAsync(t => t.Id == revokedToken)).RevokedAt
            .Should().NotBeNull("reativar NÃO ressuscita sessões antigas");
    }

    // ---- Cenário 8: revogação de refresh tokens em desativação/rebaixamento -------

    [Fact]
    public async Task Deactivate_RevogaRefreshTokensAtivosDoMembership()
    {
        var a = await SeedIdentityAsync("a@demo.example.com", withPassword: true);
        var b = await SeedIdentityAsync("b@demo.example.com", withPassword: true);
        var target = await SeedMembershipAsync(TenantA, a, TenantRole.Manager, active: true);
        await SeedMembershipAsync(TenantA, b, TenantRole.TenantAdmin, active: true);   // outro admin: não é o último
        var token = await SeedRefreshTokenAsync(TenantA, target, revoked: false);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).SetMembershipStatusAsync(
            new SetMembershipStatusCommand(target, Guid.NewGuid(), Active: false));

        result.Status.Should().Be(MembershipAdminStatus.Updated);
        (await db.UserRefreshTokens.SingleAsync(t => t.Id == token)).RevokedAt
            .Should().NotBeNull("desativar derruba as sessões do membership");
    }

    [Fact]
    public async Task Demote_ReduzPrivilegio_RevogaRefreshTokens()
    {
        var admin1 = await SeedIdentityAsync("admin1@demo.example.com", withPassword: true);
        var admin2 = await SeedIdentityAsync("admin2@demo.example.com", withPassword: true);
        var target = await SeedMembershipAsync(TenantA, admin1, TenantRole.TenantAdmin, active: true);
        await SeedMembershipAsync(TenantA, admin2, TenantRole.TenantAdmin, active: true);   // não é o último
        var token = await SeedRefreshTokenAsync(TenantA, target, revoked: false);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).UpdateMembershipAsync(
            new UpdateMembershipCommand(target, Guid.NewGuid(), null, TenantRole.Analyst));

        result.Status.Should().Be(MembershipAdminStatus.Updated);
        (await db.UserRefreshTokens.SingleAsync(t => t.Id == token)).RevokedAt
            .Should().NotBeNull("reduzir privilégio derruba as sessões (o papel antigo não sobrevive no token)");
    }

    [Fact]
    public async Task Update_SoRenomeia_NaoRevogaSessoes()
    {
        var ana = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        var membership = await SeedMembershipAsync(TenantA, ana, TenantRole.Analyst, active: true);
        var token = await SeedRefreshTokenAsync(TenantA, membership, revoked: false);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).UpdateMembershipAsync(
            new UpdateMembershipCommand(membership, Guid.NewGuid(), "Ana Silva", null));

        result.Status.Should().Be(MembershipAdminStatus.Updated);
        (await db.Users.SingleAsync(u => u.Id == membership)).DisplayName.Should().Be("Ana Silva");
        (await db.UserRefreshTokens.SingleAsync(t => t.Id == token)).RevokedAt
            .Should().BeNull("trocar só o nome não é redução de privilégio — a sessão permanece");
    }

    // ---- Fixture ----------------------------------------------------------------

    private async Task<Guid> SeedIdentityAsync(string email, bool withPassword)
    {
        await using var db = NewContext(null);
        var account = new IdentityAccount
        {
            Email = email,
            PasswordHash = withPassword ? new Pbkdf2PasswordHasher().Hash("uma frase longa e boa") : null,
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
            TenantId = tenantId, IdentityAccountId = accountId,
            DisplayName = "Seed", Role = role, IsActive = active,
        };
        db.Users.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    private async Task<Guid> SeedRefreshTokenAsync(Guid tenantId, Guid membershipId, bool revoked)
    {
        await using var db = NewContext(tenantId);
        var token = new UserRefreshToken
        {
            TenantId = tenantId, UserId = membershipId,
            TokenHash = new string('a', 64),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            RevokedAt = revoked ? DateTimeOffset.UtcNow.AddMinutes(-5) : null,
        };
        db.UserRefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IUserManagementService ServiceFor(AegisScoreDbContext db, Guid? tenantId) =>
        new UserManagementService(
            db, new SystemTenantContext(tenantId), NullLogger<UserManagementService>.Instance);
}
