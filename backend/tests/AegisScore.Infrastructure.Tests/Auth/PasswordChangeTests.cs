using System.Reflection;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// Troca da PRÓPRIA senha (<see cref="AuthService.ChangeOwnPasswordAsync"/>). Harness SQLite in-memory. Prova
/// central: a troca revoga as sessões da identidade em TODOS os tenants (ancorada no account_id), preserva o
/// teto de 10 min do access token já emitido (não o toca) e recusa uma conta federated-only.
/// </summary>
public sealed class PasswordChangeTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Current = "senha atual longa e boa";

    private readonly SqliteConnection _connection;

    public PasswordChangeTests()
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

    // ---- Cenário 10: troca da própria senha + revogação cross-tenant -------------

    [Fact]
    public async Task ChangePassword_TrocaHashERevogaSessoesEmTodosOsTenants()
    {
        var (accountId, tokenA, tokenB) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        await using var db = NewContext(TenantA);   // a pessoa está no tenant A; a revogação cruza para o B
        var result = await Auth(db).ChangeOwnPasswordAsync(accountId, Current, "nova senha longa e boa", default);

        result.Status.Should().Be(PasswordChangeStatus.Changed);

        await using var assert = NewContext(null);
        var acc = await assert.IdentityAccounts.SingleAsync(a => a.Id == accountId);
        new Pbkdf2PasswordHasher().Verify("nova senha longa e boa", acc.PasswordHash!)
            .Should().BeTrue("a nova senha passa a valer");
        new Pbkdf2PasswordHasher().Verify(Current, acc.PasswordHash!).Should().BeFalse("a antiga não vale mais");

        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenA)).RevokedAt
            .Should().NotBeNull("sessão do tenant A revogada");
        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenB)).RevokedAt
            .Should().NotBeNull("sessão do tenant B revogada — cross-tenant, ancorada no account_id");
    }

    [Fact]
    public async Task ChangePassword_SenhaAtualIncorreta_NaoAlteraNada()
    {
        var (accountId, tokenA, _) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        await using var db = NewContext(TenantA);
        var result = await Auth(db).ChangeOwnPasswordAsync(accountId, "errada errada errada", "nova senha longa e boa", default);

        result.Status.Should().Be(PasswordChangeStatus.InvalidCurrentPassword);

        await using var assert = NewContext(null);
        new Pbkdf2PasswordHasher().Verify(Current, (await assert.IdentityAccounts.SingleAsync(a => a.Id == accountId)).PasswordHash!)
            .Should().BeTrue("a senha permanece a atual");
        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenA)).RevokedAt
            .Should().BeNull("nenhuma sessão foi revogada");
    }

    [Fact]
    public async Task ChangePassword_NovaSenhaFraca_EhRejeitada()
    {
        var (accountId, _, _) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        await using var db = NewContext(TenantA);
        var result = await Auth(db).ChangeOwnPasswordAsync(accountId, Current, "curta", default);

        result.Status.Should().Be(PasswordChangeStatus.WeakPassword);
        new Pbkdf2PasswordHasher().Verify(Current, (await db.IdentityAccounts.SingleAsync(a => a.Id == accountId)).PasswordHash!)
            .Should().BeTrue("a senha permanece a atual");
    }

    [Fact]
    public async Task ChangePassword_ContaFederatedOnly_SemSenhaLocal_EhRecusada()
    {
        Guid accountId;
        await using (var seed = NewContext(null))
        {
            var account = new IdentityAccount { Email = "fed@demo.example.com", PasswordHash = null };
            seed.IdentityAccounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        await using var db = NewContext(TenantA);
        var result = await Auth(db).ChangeOwnPasswordAsync(accountId, "qualquer coisa", "nova senha longa e boa", default);

        result.Status.Should().Be(PasswordChangeStatus.NoLocalCredential);
        (await db.IdentityAccounts.SingleAsync(a => a.Id == accountId)).PasswordHash
            .Should().BeNull("não inventa uma credencial local para uma conta do provedor");
    }

    // ---- Rate limiting do endpoint autenticado de troca de senha -----------------

    [Fact]
    public void ChangePasswordEndpoint_TemRateLimiting()
    {
        // A troca verifica a senha ATUAL — é alvo de brute force mesmo autenticada. O endpoint precisa de
        // uma policy de limitação (wiring verificado por reflexão; a policy é definida no Program.cs).
        var method = typeof(AuthController).GetMethod(nameof(AuthController.ChangePassword))!;
        var attr = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        attr.Should().NotBeNull("tentativas de troca de senha não podem ser ilimitadas");
        attr!.PolicyName.Should().Be("auth-password");
    }

    // ---- Fixture ----------------------------------------------------------------

    private async Task<(Guid accountId, Guid tokenA, Guid tokenB)> SeedIdentityWithSessionsInTwoTenantsAsync()
    {
        Guid accountId;
        await using (var seed = NewContext(null))
        {
            var account = new IdentityAccount
            {
                Email = "ana@demo.example.com",
                PasswordHash = new Pbkdf2PasswordHasher().Hash(Current),
            };
            seed.IdentityAccounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        var tokenA = await SeedMembershipAndTokenAsync(TenantA, accountId, new string('a', 64));
        var tokenB = await SeedMembershipAndTokenAsync(TenantB, accountId, new string('b', 64));
        return (accountId, tokenA, tokenB);
    }

    private async Task<Guid> SeedMembershipAndTokenAsync(Guid tenantId, Guid accountId, string tokenHash)
    {
        await using var db = NewContext(tenantId);
        var membership = new User
        {
            TenantId = tenantId, IdentityAccountId = accountId,
            DisplayName = "Ana", Role = TenantRole.TenantAdmin, IsActive = true,
        };
        db.Users.Add(membership);
        await db.SaveChangesAsync();

        var token = new UserRefreshToken
        {
            TenantId = tenantId, UserId = membership.Id,
            TokenHash = tokenHash, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        db.UserRefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    private DbContextOptions<AegisScoreDbContext> DbOpts =>
        new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(DbOpts, new SystemTenantContext(tenantId));

    private AuthService Auth(AegisScoreDbContext db) => new(
        db, DbOpts,
        new JwtTokenService(Options.Create(new JwtOptions
        {
            SigningKey = "aegis-test-signing-key-com-mais-de-32-bytes", Issuer = "aegis-score", Audience = "aegis-score",
        })),
        new Pbkdf2PasswordHasher(), new Sha256RefreshTokenHasher(),
        Options.Create(new FederationOptions()),
        NullLogger<AuthService>.Instance);
}
