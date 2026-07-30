using System.IdentityModel.Tokens.Jwt;
using System.Text.RegularExpressions;
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
/// [AEGIS-AUD-007] Troca federada (Entra → sessão local) e o efeito dos modos Local/Federated/Hybrid.
/// A identidade externa é sempre um <see cref="FederatedIdentity"/> JÁ VALIDADO (a verificação
/// criptográfica é do esquema JWT Bearer, exercitada por build/integração) — aqui provamos o VÍNCULO por
/// tid+oid, a ausência de auto-provisionamento e a emissão do par local por hash.
/// </summary>
public sealed class FederatedAuthTests : IDisposable
{
    private const string Senha = "uma frase longa e boa de verdade";
    private const string Email = "ana@demo.example.com";
    private const string EntraTid = "11111111-1111-1111-1111-111111111111";
    private const string OidA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string OidB = "bbbbbbbb-0000-0000-0000-000000000002";
    private static readonly Guid TenantA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Sha256RefreshTokenHasher Hasher = new();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _accountId;
    private Guid _membershipId;

    public FederatedAuthTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        var account = new IdentityAccount { Email = Email, PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha) };
        ctx.IdentityAccounts.Add(account);
        ctx.SaveChanges();
        _accountId = account.Id;
        _membershipId = SeedMembership(TenantA, _accountId, active: true);
    }

    public void Dispose() => _connection.Dispose();

    // ---- Modos Local / Federated / Hybrid (login por senha) -----------------------------------------

    [Fact]
    public async Task Login_ModoLocal_Funciona()
    {
        await using var db = NewContext(null);
        (await ServiceFor(db, Fed(FederationMode.Local)).LoginAsync(Email, Senha, null, default)).Pair
            .Should().NotBeNull("Local é o padrão de dev/demonstração");
    }

    [Fact]
    public async Task Login_ModoFederated_BloqueiaSenha_MesmoComCredencialValida()
    {
        await using var db = NewContext(null);
        (await ServiceFor(db, Fed(FederationMode.Federated)).LoginAsync(Email, Senha, null, default)).Pair
            .Should().BeNull("em Federated o login por senha fica desabilitado");
    }

    [Fact]
    public async Task Login_ModoHybrid_AindaAceitaSenha()
    {
        await using var db = NewContext(null);
        (await ServiceFor(db, Fed(FederationMode.Hybrid)).LoginAsync(Email, Senha, null, default)).Pair
            .Should().NotBeNull("Hybrid permite os dois caminhos");
    }

    [Fact]
    public async Task Exchange_FederacaoDesligada_Nega()
    {
        await using var db = NewContext(null);
        (await ServiceFor(db, Fed(FederationMode.Local))
            .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, Email), null, default)).Pair
            .Should().BeNull("sem federação não há troca");
    }

    // ---- Validação do token externo (tid/oid) -------------------------------------------------------

    [Theory]
    [InlineData(null, OidA)]
    [InlineData("", OidA)]
    [InlineData(EntraTid, null)]
    [InlineData(EntraTid, "")]
    public async Task Exchange_SemTidOuOid_Recusado(string? tid, string? oid)
    {
        await using var db = NewContext(null);
        (await ServiceFor(db, Fed(FederationMode.Federated))
            .ExchangeFederatedAsync(new FederatedIdentity(tid, oid, Email), null, default)).Pair
            .Should().BeNull();
    }

    [Fact]
    public async Task Exchange_TidForaDoPermitido_Recusado()
    {
        await using var db = NewContext(null);
        var outroTid = "99999999-9999-9999-9999-999999999999";
        (await ServiceFor(db, Fed(FederationMode.Federated))
            .ExchangeFederatedAsync(new FederatedIdentity(outroTid, OidA, Email), null, default)).Pair
            .Should().BeNull("o tid do token precisa casar com o tenant Entra configurado");
    }

    // ---- Primeiro vínculo e ausência de auto-provisionamento ----------------------------------------

    [Fact]
    public async Task Exchange_PrimeiroLogin_VinculaContaPreexistentePorEmail()
    {
        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, "  Ana@Demo.Example.com "), null, default)).Pair
                .Should().NotBeNull("e-mail correspondente + membership ativo → primeiro vínculo");

        var acc = await AccountAsync(_accountId);
        acc.ExternalTenantId.Should().Be(EntraTid);
        acc.ExternalObjectId.Should().Be(OidA, "a conta preexistente foi vinculada à identidade externa");
    }

    [Fact]
    public async Task Exchange_SemContaCorrespondente_Nega_SemProvisionar()
    {
        var antes = await CountAccountsAsync();
        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, "ninguem@demo.example.com"), null, default)).Pair
                .Should().BeNull();

        (await CountAccountsAsync()).Should().Be(antes, "nenhuma conta é criada automaticamente");
    }

    [Fact]
    public async Task Exchange_MembershipInativo_NaoRecebeSessao_NemVincula()
    {
        // Conta com membership INATIVO apenas.
        const string emailBob = "bob@demo.example.com";
        Guid bobId;
        await using (var ctx = NewContext(null))
        {
            var bob = new IdentityAccount { Email = emailBob, PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha) };
            ctx.IdentityAccounts.Add(bob);
            await ctx.SaveChangesAsync();
            bobId = bob.Id;
        }
        SeedMembership(TenantA, bobId, active: false);

        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidB, emailBob), null, default)).Pair
                .Should().BeNull("membership inativo não recebe sessão");

        var bobAcc = await AccountAsync(bobId);
        bobAcc.ExternalObjectId.Should().BeNull("login negado não deixa efeito colateral: a conta não é vinculada");
    }

    // ---- Identidade já vinculada: por tid+oid, imune a troca de e-mail e a captura ------------------

    [Fact]
    public async Task Exchange_JaVinculada_AutenticaPorTidOid_MesmoComEmailAlterado()
    {
        await using (var db = NewContext(null))
            await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, Email), null, default);

        // O e-mail muda no Entra (e na conta) — o vínculo por tid+oid não pode quebrar.
        await using (var ctx = NewContext(null))
        {
            var acc = await ctx.IdentityAccounts.FirstAsync(a => a.Id == _accountId);
            acc.Email = "ana.nova@demo.example.com";
            await ctx.SaveChangesAsync();
        }

        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, "email-que-nao-casa@demo.example.com"), null, default)).Pair
                .Should().NotBeNull("uma vez vinculada, a pessoa é localizada por tid+oid, não por e-mail");
    }

    [Fact]
    public async Task Exchange_OutroOidComMesmoEmail_NaoCapturaContaJaVinculada()
    {
        // Ana vincula com OidA.
        await using (var db = NewContext(null))
            await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, Email), null, default);

        // Alguém com o MESMO e-mail mas outro oid tenta entrar → não captura a conta já vinculada.
        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidB, Email), null, default)).Pair
                .Should().BeNull("conta já ligada a outro oid não é capturável pelo e-mail");

        (await AccountAsync(_accountId)).ExternalObjectId.Should().Be(OidA, "o vínculo original permanece");
    }

    // ---- Par local resultante: claims internas corretas + refresh só por hash -----------------------

    [Fact]
    public async Task Exchange_EmiteJwtLocalComAccountTenantEPapelInternos()
    {
        TokenPair? pair;
        await using (var db = NewContext(null))
            pair = (await ServiceFor(db, Fed(FederationMode.Federated), RealJwt())
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, Email), null, default)).Pair;

        pair.Should().NotBeNull();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(pair!.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == "sub" && c.Value == _membershipId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "tenant_id" && c.Value == TenantA.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "account_id" && c.Value == _accountId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == nameof(TenantRole.Analyst));
    }

    [Fact]
    public async Task Exchange_RefreshLocalArmazenadoSomentePorHash()
    {
        TokenPair? pair;
        await using (var db = NewContext(null))
            pair = (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid, OidA, Email), null, default)).Pair;

        await using var assert = NewContext(null);
        var row = await assert.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        row.TokenHash.Should().Be(Hasher.Hash(pair!.RefreshToken));
        row.TokenHash.Should().NotBe(pair.RefreshToken, "o bruto nunca é persistido, nem na troca federada");
        Regex.IsMatch(row.TokenHash, "^[0-9a-f]{64}$").Should().BeTrue();
    }

    // ---- Canonicalização de tid/oid (PR #14) --------------------------------------------------------

    [Theory]
    [InlineData("não-é-guid", OidA)]
    [InlineData(EntraTid, "12345")]
    public async Task Exchange_TidOuOidMalformado_Recusado_SemConsultaNemEscrita(string tid, string oid)
    {
        var antes = await CountAccountsAsync();
        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(tid, oid, Email), null, default)).Pair
                .Should().BeNull("identificador malformado falha antes de qualquer consulta ou escrita");

        (await CountAccountsAsync()).Should().Be(antes);
        (await AccountAsync(_accountId)).ExternalObjectId.Should().BeNull("nenhuma linha foi alterada");
    }

    [Fact]
    public async Task Exchange_CanonicalizaGuids_AoPersistir()
    {
        // tid/oid chegam em MAIÚSCULAS; o vínculo persistido deve ser o "D" canônico (minúsculo).
        await using (var db = NewContext(null))
            (await ServiceFor(db, Fed(FederationMode.Federated))
                .ExchangeFederatedAsync(new FederatedIdentity(EntraTid.ToUpperInvariant(), OidA.ToUpperInvariant(), Email), null, default)).Pair
                .Should().NotBeNull();

        var acc = await AccountAsync(_accountId);
        acc.ExternalTenantId.Should().Be(EntraTid, "persistido canônico minúsculo");
        acc.ExternalObjectId.Should().Be(OidA);
    }

    // ---- Fixture ------------------------------------------------------------------------------------

    private Guid SeedMembership(Guid tenantId, Guid accountId, bool active)
    {
        using var db = NewContext(tenantId);
        var user = new User
        {
            TenantId = tenantId, IdentityAccountId = accountId, DisplayName = "Ana",
            Role = TenantRole.Analyst, IsActive = active,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private async Task<IdentityAccount> AccountAsync(Guid id)
    {
        await using var db = NewContext(null);
        return await db.IdentityAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<int> CountAccountsAsync()
    {
        await using var db = NewContext(null);
        return await db.IdentityAccounts.CountAsync();
    }

    private static IOptions<FederationOptions> Fed(FederationMode mode) => Options.Create(new FederationOptions
    {
        Mode = mode,
        TenantId = EntraTid,
        ApiClientId = "api-client-id",
        ApiScope = "api://api-client-id/access_as_user",
        SpaClientId = "spa-client-id",
    });

    private static JwtTokenService RealJwt() => new(Options.Create(new JwtOptions
    {
        SigningKey = "aegis-test-signing-key-com-mais-de-32-bytes", Issuer = "aegis-score", Audience = "aegis-score",
    }));

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(_options, new SystemTenantContext(tenantId));

    private AuthService ServiceFor(AegisScoreDbContext db, IOptions<FederationOptions> fed, IJwtTokenService? tokens = null) =>
        new(db, _options, tokens ?? new StubTokenService(), new Pbkdf2PasswordHasher(), Hasher, fed,
            NullLogger<AuthService>.Instance);

    private sealed class StubTokenService : IJwtTokenService
    {
        public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User m, IdentityAccount a) =>
            ($"access.{m.TenantId}.{a.Email}", DateTimeOffset.UtcNow.AddMinutes(10));

        public (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken() =>
            (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(7));

        // [AEGIS-AUD-012] Ticket de seleção com round-trip simples (sem JWT real).
        public (string Token, DateTimeOffset ExpiresAt) CreateTenantSelectionTicket(Guid accountId) =>
            ($"selticket.{accountId}", DateTimeOffset.UtcNow.AddMinutes(5));

        public bool TryReadTenantSelectionTicket(string ticket, out Guid accountId)
        {
            accountId = Guid.Empty;
            const string prefix = "selticket.";
            return ticket?.StartsWith(prefix) == true && Guid.TryParse(ticket[prefix.Length..], out accountId);
        }
    }
}
