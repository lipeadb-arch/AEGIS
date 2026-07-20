using AegisScore.Application.Abstractions;
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
/// Testes do SSO simulado (§22): login sem tenant, listagem de ambientes e troca de contexto.
///
/// ⚠️ Existem porque a suíte DEIXOU PASSAR um bug real: a versão original de
/// <c>GetAccessibleTenantsAsync</c> projetava direto num record dentro de um <c>.Join()</c>, o EF não
/// traduzia a expressão e a rota devolvia 500 — descoberto só no smoke test ao vivo. Um teste que
/// EXECUTA a consulta contra um banco relacional pega isso na hora, porque a falha de tradução acontece
/// no provider, não no C#.
/// </summary>
public sealed class TenantSwitchingTests : IDisposable
{
    private const string Senha = "uma frase longa e boa";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TenantSuspenso = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _accountId;

    public TenantSwitchingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active },
            new Tenant { Id = TenantSuspenso, Name = "Zulu", Slug = "zulu", Status = TenantStatus.Suspended });

        // Uma PESSOA com acesso a três ambientes — um deles suspenso.
        var account = new IdentityAccount
        {
            Email = "ana@demo.aegis",
            PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha),
        };
        ctx.IdentityAccounts.Add(account);
        ctx.SaveChanges();   // Tenant e IdentityAccount NÃO são ITenantOwned: gravam sem tenant ambiente
        _accountId = account.Id;

        // ⚠️ Cada membership é ITenantOwned, então precisa de um contexto ligado AO SEU tenant — o
        // StampTenant fail-closed recusa gravá-los sob um contexto sem tenant (ou sob outro). É a
        // mesma razão pela qual o AuthService abre um contexto por destino em IssuePairAsync.
        SeedMembership(TenantA, UserRole.Analyst);
        SeedMembership(TenantB, UserRole.TenantAdmin);
        SeedMembership(TenantSuspenso, UserRole.Analyst);
    }

    private void SeedMembership(Guid tenantId, UserRole role)
    {
        using var db = NewContext(tenantId);
        db.Users.Add(new User
        {
            TenantId = tenantId, IdentityAccountId = _accountId, DisplayName = "Ana", Role = role,
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task LoginAsync_SemTenantAmbiente_AutenticaPelaContaGlobal()
    {
        // O contexto vai SEM tenant, como no login real (o analista só informou e-mail e senha).
        await using var db = NewContext(null);
        var pair = await ServiceFor(db).LoginAsync("  Ana@Demo.Aegis  ", Senha, default);

        pair.Should().NotBeNull("o e-mail normaliza e a credencial é da conta global");
        pair!.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoginAsync_SenhaErrada_Recusa()
    {
        await using var db = NewContext(null);
        (await ServiceFor(db).LoginAsync("ana@demo.aegis", "senha errada demais", default))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetAccessibleTenantsAsync_ListaAmbientesAtivos_OmitindoSuspenso()
    {
        await using var db = NewContext(null);
        var lista = await ServiceFor(db).GetAccessibleTenantsAsync(_accountId, default);

        lista.Select(t => t.Slug).Should().BeEquivalentTo(new[] { "alfa", "bravo" },
            "tenant suspenso não entra no seletor");
        lista.Select(t => t.Name).Should().BeInAscendingOrder();
        lista.Single(t => t.Slug == "bravo").Role.Should().Be(UserRole.TenantAdmin,
            "o papel é o DAQUELE cliente");
    }

    [Fact]
    public async Task GetAccessibleTenantsAsync_ContaDesconhecida_DevolveVazio()
    {
        await using var db = NewContext(null);
        (await ServiceFor(db).GetAccessibleTenantsAsync(Guid.NewGuid(), default))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchTenantAsync_ComMembershipAtivo_EmiteParaOAlvo()
    {
        await using var db = NewContext(TenantA);
        var pair = await ServiceFor(db).SwitchTenantAsync(_accountId, TenantB, null, default);

        pair.Should().NotBeNull();

        await using var assert = NewContext(TenantB);
        var token = await assert.UserRefreshTokens.SingleAsync(t => t.Token == pair!.RefreshToken);
        token.TenantId.Should().Be(TenantB, "o refresh novo pertence ao ambiente de DESTINO");
    }

    [Fact]
    public async Task SwitchTenantAsync_SemMembership_Recusa()
    {
        var estranho = Guid.Parse("44444444-4444-4444-4444-444444444444");
        await using var db = NewContext(TenantA);

        (await ServiceFor(db).SwitchTenantAsync(_accountId, estranho, null, default))
            .Should().BeNull("sem acesso ativo no alvo não há troca");
    }

    [Fact]
    public async Task SwitchTenantAsync_ParaTenantSuspenso_Recusa()
    {
        await using var db = NewContext(TenantA);
        (await ServiceFor(db).SwitchTenantAsync(_accountId, TenantSuspenso, null, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task SwitchTenantAsync_RevogaORefreshDoAmbienteAnterior()
    {
        // Sessão em A...
        string anterior;
        await using (var db = NewContext(null))
            anterior = (await ServiceFor(db).LoginAsync("ana@demo.aegis", Senha, default))!.RefreshToken;

        // ...e troca para B levando o refresh corrente.
        await using (var db = NewContext(TenantA))
            (await ServiceFor(db).SwitchTenantAsync(_accountId, TenantB, anterior, default))
                .Should().NotBeNull();

        await using var assert = NewContext(null);
        var antigo = await assert.UserRefreshTokens.IgnoreQueryFilters()
            .SingleAsync(t => t.Token == anterior);
        antigo.RevokedAt.Should().NotBeNull(
            "duas sessões vivas de tenants distintos deixariam um replay reabrir o ambiente abandonado");
    }

    // ---- Fixture ----------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(_options, new SystemTenantContext(tenantId));

    private AuthService ServiceFor(AegisScoreDbContext db) =>
        new(db, _options, new StubTokenService(), new Pbkdf2PasswordHasher(),
            NullLogger<AuthService>.Instance);

    /// <summary>Emissor de tokens sem JWT real: estes testes exercitam as CONSULTAS, não a assinatura.</summary>
    private sealed class StubTokenService : IJwtTokenService
    {
        public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User m, IdentityAccount a) =>
            ($"access.{m.TenantId}.{a.Email}", DateTimeOffset.UtcNow.AddMinutes(10));

        public (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken() =>
            (Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(7));
    }
}
