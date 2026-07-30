using System.IdentityModel.Tokens.Jwt;
using AegisScore.Application.Abstractions;
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
/// [AEGIS-AUD-011] Todos os caminhos de emissão de sessão carregam corretamente os DOIS eixos, com JWT
/// REAL: login, troca de tenant e refresh. Provas centrais: a troca de tenant altera SÓ o papel tenant
/// (<c>role</c>) e preserva a autoridade global (<c>platform_role</c>); o refresh consulta o estado GLOBAL
/// ATUAL da identidade ao reemitir. Nada aqui muda a exigência de membership ativo nem o fluxo de switch.
/// </summary>
public sealed class PlatformRoleSessionTests : IDisposable
{
    private const string Senha = "uma frase longa e boa de verdade";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Sha256RefreshTokenHasher Hasher = new();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _adminAccountId;   // Analyst em A, TenantAdmin em B, PlatformAdmin GLOBAL

    public PlatformRoleSessionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
        var account = new IdentityAccount
        {
            Email = "ana@demo.example.com",
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha),
            PlatformRole = PlatformRole.PlatformAdmin,
        };
        ctx.IdentityAccounts.Add(account);
        ctx.SaveChanges();
        _adminAccountId = account.Id;

        SeedMembership(TenantA, _adminAccountId, TenantRole.Analyst);
        SeedMembership(TenantB, _adminAccountId, TenantRole.TenantAdmin);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Login_CarregaPlatformRoleQuandoAIdentidadeEhAdminGlobal()
    {
        await using var db = NewContext(null);
        // [AEGIS-AUD-012] Ana tem dois acessos: passa a DICA do último tenant (A) para o login autenticar
        // direto (em vez de exigir seleção), e prova que o eixo global viaja na claim própria mesmo assim.
        var pair = (await Auth(db).LoginAsync("ana@demo.example.com", Senha, TenantA, default)).Pair;

        pair.Should().NotBeNull();
        PlatformRoleClaim(pair!.AccessToken).Should().Be("PlatformAdmin",
            "a autoridade global viaja na claim própria, em qualquer tenant inicial");
    }

    [Fact]
    public async Task Login_SemPapelGlobal_NaoCarregaClaimGlobal()
    {
        // Uma segunda pessoa, só Analyst em A, sem papel global.
        Guid plainId;
        await using (var ctx = NewContext(null))
        {
            var acc = new IdentityAccount { Email = "bob@demo.example.com", PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha) };
            ctx.IdentityAccounts.Add(acc);
            await ctx.SaveChangesAsync();
            plainId = acc.Id;
        }
        SeedMembership(TenantA, plainId, TenantRole.Analyst);

        await using var db = NewContext(null);
        // Bob tem UM único acesso → login autentica direto (seleção automática), sem precisar de dica.
        var pair = (await Auth(db).LoginAsync("bob@demo.example.com", Senha, null, default)).Pair;
        HasPlatformClaim(pair!.AccessToken).Should().BeFalse("sem papel global, sem claim global");
    }

    [Fact]
    public async Task TenantSwitch_TrocaSoOPapelTenant_PreservaAAutoridadeGlobal()
    {
        // Assume o tenant A e troca para B; role muda (Analyst → TenantAdmin), platform_role permanece.
        TokenPair? afterSwitch;
        await using (var db = NewContext(TenantA))
            afterSwitch = await Auth(db).SwitchTenantAsync(_adminAccountId, TenantB, null, default);

        afterSwitch.Should().NotBeNull();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(afterSwitch!.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "TenantAdmin",
            "o papel tenant passa a ser o do ambiente de destino");
        jwt.Claims.Should().Contain(c => c.Type == "platform_role" && c.Value == "PlatformAdmin",
            "a autoridade global é invariável à troca de tenant");
    }

    [Fact]
    public async Task Refresh_MantemOsDoisEixos_EConsultaOEstadoGlobalAtual()
    {
        // Login → refresh: ambos os eixos presentes.
        string refresh1;
        await using (var db = NewContext(null))
            // Dica do último tenant (A) para autenticar direto: Ana tem dois acessos (AUD-012).
            refresh1 = (await Auth(db).LoginAsync("ana@demo.example.com", Senha, TenantA, default)).Pair!.RefreshToken;

        TokenPair pair2;
        await using (var db = NewContext(null))
            pair2 = (await Auth(db).RefreshAsync(refresh1, default)).Pair!;
        PlatformRoleClaim(pair2.AccessToken).Should().Be("PlatformAdmin", "o refresh preserva o eixo global");

        // A autoridade global é REVOGADA no banco entre as emissões...
        await using (var ctx = NewContext(null))
        {
            var acc = await ctx.IdentityAccounts.SingleAsync(a => a.Id == _adminAccountId);
            acc.PlatformRole = PlatformRole.None;
            await ctx.SaveChangesAsync();
        }

        // ...e o próximo refresh reflete o estado ATUAL (sem claim global) — não um valor cristalizado.
        TokenPair pair3;
        await using (var db = NewContext(null))
            pair3 = (await Auth(db).RefreshAsync(pair2.RefreshToken, default)).Pair!;
        HasPlatformClaim(pair3.AccessToken).Should().BeFalse(
            "o refresh consulta o estado global atual da identidade ao reemitir");
    }

    // ---- Fixture ----------------------------------------------------------------

    private static string? PlatformRoleClaim(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .FirstOrDefault(c => c.Type == "platform_role")?.Value;

    private static bool HasPlatformClaim(string accessToken) => PlatformRoleClaim(accessToken) is not null;

    private void SeedMembership(Guid tenantId, Guid accountId, TenantRole role)
    {
        using var db = NewContext(tenantId);
        db.Users.Add(new User { TenantId = tenantId, IdentityAccountId = accountId, DisplayName = "Ana", Role = role });
        db.SaveChanges();
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(_options, new SystemTenantContext(tenantId));

    private AuthService Auth(AegisScoreDbContext db) => new(
        db, _options,
        new JwtTokenService(Options.Create(new JwtOptions
        {
            SigningKey = "aegis-test-signing-key-com-mais-de-32-bytes", Issuer = "aegis-score", Audience = "aegis-score",
        })),
        new Pbkdf2PasswordHasher(), Hasher, Options.Create(new FederationOptions()),
        NullLogger<AuthService>.Instance);
}
