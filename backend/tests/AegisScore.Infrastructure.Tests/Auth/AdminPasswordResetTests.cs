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
/// Redefinição ADMINISTRATIVA de senha (<see cref="AuthService.AdminResetPasswordAsync"/>) — recuperação
/// legítima quando a pessoa perdeu o acesso e o onboarding preserva (corretamente) a credencial global.
/// Harness SQLite in-memory, no idioma do <c>PasswordChangeTests</c>. Provas centrais: só reescreve o hash da
/// IDENTIDADE (nunca membership), revoga as sessões em TODOS os tenants, recusa federated-only e auto-reset, e
/// é ATÔMICA — uma falha na revogação não deixa hash novo persistido.
/// </summary>
public sealed class AdminPasswordResetTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>O ator (PlatformAdmin) — uma identidade DISTINTA do alvo. O serviço só o usa para a guarda de
    /// auto-reset e a auditoria; a autoridade de plataforma é imposta na borda (policy do controller).</summary>
    private static readonly Guid AdminAccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private const string OldPassword = "senha antiga longa e boa";
    private const string NewPassword = "senha nova longa e melhor";

    private readonly SqliteConnection _connection;

    public AdminPasswordResetTests()
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

    // ---- 1/2/3/4: sucesso — hash novo vale, o antigo não, e as sessões caem em todos os tenants ----

    [Fact]
    public async Task Reset_TrocaHash_RevogaSessoesEmTodosOsTenants_ERetornaContagem()
    {
        var (targetId, tokenA, tokenB) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        await using var db = NewContext(TenantA);   // o admin opera de um tenant; a revogação cruza para o outro
        var result = await Auth(db).AdminResetPasswordAsync(AdminAccountId, targetId, NewPassword, default);

        result.Status.Should().Be(AdminPasswordResetStatus.Reset);
        result.AffectedEnvironments.Should().Be(2, "a identidade tem acesso a dois tenants — ambos revogados");

        await using var assert = NewContext(null);
        var acc = await assert.IdentityAccounts.SingleAsync(a => a.Id == targetId);

        // (2) o hash novo valida a senha nova e rejeita a anterior
        new Pbkdf2PasswordHasher().Verify(NewPassword, acc.PasswordHash!).Should().BeTrue("a nova senha passa a valer");
        new Pbkdf2PasswordHasher().Verify(OldPassword, acc.PasswordHash!).Should().BeFalse("a antiga não vale mais");

        // (3) a senha em claro NÃO é persistida (guarda-se hash, nunca o texto)
        acc.PasswordHash.Should().NotBe(NewPassword).And.NotContain(NewPassword);

        // (4) refresh tokens de TODOS os memberships/tenants foram revogados — ancorado no account_id
        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenA)).RevokedAt
            .Should().NotBeNull("sessão do tenant A revogada");
        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenB)).RevokedAt
            .Should().NotBeNull("sessão do tenant B revogada — cross-tenant, ancorada no account_id");
    }

    // ---- 3: o resultado tipado NUNCA carrega senha nem hash ----

    [Fact]
    public void AdminPasswordResetResult_NaoExpoeSenhaNemHash()
    {
        var props = typeof(AdminPasswordResetResult).GetProperties().Select(p => p.Name).ToList();
        props.Any(p => p.Contains("Password", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("o resultado nunca devolve a senha");
        props.Any(p => p.Contains("Hash", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("o resultado nunca devolve o hash");
    }

    // ---- 6: identidade inexistente → NotFound (404 genérico na borda), sem tocar nada ----

    [Fact]
    public async Task Reset_IdentidadeInexistente_RetornaNotFound()
    {
        await using var db = NewContext(TenantA);
        var result = await Auth(db).AdminResetPasswordAsync(AdminAccountId, Guid.NewGuid(), NewPassword, default);

        result.Status.Should().Be(AdminPasswordResetStatus.NotFound);
    }

    // ---- 7: senha fraca → WeakPassword e NADA muda (nem hash, nem sessões) ----

    [Fact]
    public async Task Reset_SenhaFraca_RejeitadaSemAlterarNada()
    {
        var (targetId, tokenA, _) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        await using var db = NewContext(TenantA);
        var result = await Auth(db).AdminResetPasswordAsync(AdminAccountId, targetId, "curta", default);

        result.Status.Should().Be(AdminPasswordResetStatus.WeakPassword);

        await using var assert = NewContext(null);
        new Pbkdf2PasswordHasher().Verify(OldPassword, (await assert.IdentityAccounts.SingleAsync(a => a.Id == targetId)).PasswordHash!)
            .Should().BeTrue("a senha permanece a antiga");
        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenA)).RevokedAt
            .Should().BeNull("nenhuma sessão foi revogada");
    }

    // ---- 8: federated-only (sem hash) → NoLocalCredential e CONTINUA sem hash (não fabrica credencial) ----

    [Fact]
    public async Task Reset_ContaFederatedOnly_RecusadaESegueSemHash()
    {
        Guid targetId;
        await using (var seed = NewContext(null))
        {
            var account = new IdentityAccount { Email = "fed@demo.example.com", PasswordHash = null };
            seed.IdentityAccounts.Add(account);
            await seed.SaveChangesAsync();
            targetId = account.Id;
        }

        await using var db = NewContext(TenantA);
        var result = await Auth(db).AdminResetPasswordAsync(AdminAccountId, targetId, NewPassword, default);

        result.Status.Should().Be(AdminPasswordResetStatus.NoLocalCredential);
        (await db.IdentityAccounts.SingleAsync(a => a.Id == targetId)).PasswordHash
            .Should().BeNull("não fabrica uma credencial local para uma conta do provedor corporativo");
    }

    // ---- 9: auto-redefinição administrativa → SelfResetForbidden (deve usar a troca normal) ----

    [Fact]
    public async Task Reset_DoProprioAdministrador_EhRecusada()
    {
        var (targetId, tokenA, _) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        await using var db = NewContext(TenantA);
        // ator == alvo: barrado ANTES de qualquer escrita.
        var result = await Auth(db).AdminResetPasswordAsync(targetId, targetId, NewPassword, default);

        result.Status.Should().Be(AdminPasswordResetStatus.SelfResetForbidden);

        await using var assert = NewContext(null);
        new Pbkdf2PasswordHasher().Verify(OldPassword, (await assert.IdentityAccounts.SingleAsync(a => a.Id == targetId)).PasswordHash!)
            .Should().BeTrue("nada muda numa auto-redefinição recusada");
        (await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.Id == tokenA)).RevokedAt
            .Should().BeNull("nenhuma sessão foi revogada");
    }

    // ---- 10: falha DURANTE a revogação → transação revertida, hash novo NÃO persiste (atomicidade) ----

    [Fact]
    public async Task Reset_FalhaNaRevogacao_NaoDeixaHashNovoPersistido()
    {
        var (targetId, _, _) = await SeedIdentityWithSessionsInTwoTenantsAsync();

        // Fault injection determinística: derruba a tabela de refresh tokens. A substituição do hash
        // (IdentityAccounts) ocorre ANTES da revogação (UserRefreshTokens) na MESMA transação — então a
        // revogação falha DEPOIS de o hash ser escrito. Se a atomicidade não valesse, o hash novo "vazaria".
        await using (var sabotage = NewContext(null))
            await sabotage.Database.ExecuteSqlRawAsync("DROP TABLE \"UserRefreshTokens\"");

        await using var db = NewContext(TenantA);
        var act = () => Auth(db).AdminResetPasswordAsync(AdminAccountId, targetId, NewPassword, default);

        await act.Should().ThrowAsync<Exception>("a revogação falha e a transação inteira é revertida");

        // Ler de um contexto NOVO (o banco, não o tracker): o hash tem de continuar sendo o ANTIGO.
        await using var assert = NewContext(null);
        var acc = await assert.IdentityAccounts.SingleAsync(a => a.Id == targetId);
        new Pbkdf2PasswordHasher().Verify(OldPassword, acc.PasswordHash!)
            .Should().BeTrue("a senha nova não pode persistir se a revogação falhou (nenhum estado parcial)");
        new Pbkdf2PasswordHasher().Verify(NewPassword, acc.PasswordHash!)
            .Should().BeFalse("o hash novo foi revertido junto com a transação");
    }

    // ---- Rate limiting da rota de mutação de credencial ----

    [Fact]
    public void ResetPasswordEndpoint_TemRateLimiting()
    {
        // Redefinir credencial é mutação sensível — nunca ilimitada, mesmo exigindo autoridade de plataforma.
        var method = typeof(PlatformIdentitiesController).GetMethod(nameof(PlatformIdentitiesController.ResetPassword))!;
        var attr = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        attr.Should().NotBeNull("a redefinição de senha não pode ser ilimitada");
        attr!.PolicyName.Should().Be("platform-password-reset");
    }

    // ---- Fixture (idioma do PasswordChangeTests) --------------------------------

    private async Task<(Guid targetId, Guid tokenA, Guid tokenB)> SeedIdentityWithSessionsInTwoTenantsAsync()
    {
        Guid targetId;
        await using (var seed = NewContext(null))
        {
            var account = new IdentityAccount
            {
                Email = "bruno@demo.example.com",
                PasswordHash = new Pbkdf2PasswordHasher().Hash(OldPassword),
            };
            seed.IdentityAccounts.Add(account);
            await seed.SaveChangesAsync();
            targetId = account.Id;
        }

        var tokenA = await SeedMembershipAndTokenAsync(TenantA, targetId, new string('a', 64));
        var tokenB = await SeedMembershipAndTokenAsync(TenantB, targetId, new string('b', 64));
        return (targetId, tokenA, tokenB);
    }

    private async Task<Guid> SeedMembershipAndTokenAsync(Guid tenantId, Guid accountId, string tokenHash)
    {
        await using var db = NewContext(tenantId);
        var membership = new User
        {
            TenantId = tenantId, IdentityAccountId = accountId,
            DisplayName = "Bruno", Role = TenantRole.Analyst, IsActive = true,
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
