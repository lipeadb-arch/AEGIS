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
/// [AEGIS-AUD-009] Cobertura de serviço do fluxo de refresh — hoje insuficiente — provando a invariante
/// central: NENHUM token bruto (nem o sucessor) é persistido, só o hash SHA-256; o lookup, a rotação, o
/// logout, a troca de tenant e a revogação de cadeia operam EXCLUSIVAMENTE por hash. Bateria relacional
/// (SQLite em memória) para a lógica determinística; a concorrência real fica na bateria PostgreSQL.
/// </summary>
public sealed class RefreshTokenHashingTests : IDisposable
{
    private const string Senha = "uma frase longa e boa de verdade";
    private const string Email = "ana@demo.example.com";
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Sha256RefreshTokenHasher Hasher = new();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _accountId;

    public RefreshTokenHashingTests()
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

        SeedMembership(TenantA);
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) Login: devolve o bruto ao cliente, mas persiste SÓ o hash (itens 1-4) ---------------------

    [Fact]
    public async Task Login_DevolveBrutoAoCliente_MasPersisteApenasHash()
    {
        string raw;
        await using (var db = NewContext(null))
            raw = (await ServiceFor(db).LoginAsync(Email, Senha, default))!.RefreshToken;

        raw.Should().NotBeNullOrEmpty("o token bruto vai ao cliente (cookie HttpOnly)");

        var row = await SingleTokenRowAsync();
        row.TokenHash.Should().Be(Hasher.Hash(raw), "a coluna guarda o HASH do bruto");
        row.TokenHash.Should().NotBe(raw, "o bruto NUNCA é persistido");
        Regex.IsMatch(row.TokenHash, "^[0-9a-f]{64}$").Should().BeTrue("SHA-256 hex, 64 chars");
        row.ReplacedByTokenHash.Should().BeNull("recém-emitido não tem sucessor");
        RawAppearsInAnyColumn(row, raw).Should().BeFalse("o bruto não aparece em nenhuma coluna da linha");
    }

    // ---- 2) Refresh correto: localiza por hash, rotaciona, revoga o pai (itens 5,7,8,9,10) ------------

    [Fact]
    public async Task Refresh_LocalizaPorHash_Rotaciona_PaiGuardaSoHashSucessor_FilhoSoHash()
    {
        var raw0 = await LoginAsync();

        RefreshResult result;
        await using (var db = NewContext(null))
            result = await ServiceFor(db).RefreshAsync(raw0, default);

        result.Outcome.Should().Be(RefreshOutcome.Success, "refresh com token correto funciona por hash");
        var raw1 = result.Pair!.RefreshToken;
        raw1.Should().NotBe(raw0, "o vencedor recebe o sucessor BRUTO, novo");

        var pai = await TokenRowByHashAsync(Hasher.Hash(raw0));
        var filho = await TokenRowByHashAsync(Hasher.Hash(raw1));

        pai!.RevokedAt.Should().NotBeNull("a rotação revoga o pai");
        pai.ReplacedByTokenHash.Should().Be(Hasher.Hash(raw1), "o pai guarda SOMENTE o hash do sucessor");
        pai.ReplacedByTokenHash.Should().Be(filho!.TokenHash, "hash do sucessor no pai == TokenHash do filho");
        filho.TokenHash.Should().Be(Hasher.Hash(raw1), "o filho guarda SOMENTE o hash");
        filho.IsActive.Should().BeTrue("o filho nasce ativo");

        // Nenhuma coluna de nenhuma linha carrega o bruto (do pai ou do filho).
        foreach (var row in await AllTokenRowsAsync())
        {
            RawAppearsInAnyColumn(row, raw0).Should().BeFalse();
            RawAppearsInAnyColumn(row, raw1).Should().BeFalse();
        }
    }

    // ---- 3) Token desconhecido falha (item 6) --------------------------------------------------------

    [Fact]
    public async Task Refresh_TokenDesconhecido_Falha()
    {
        await using var db = NewContext(null);
        var result = await ServiceFor(db).RefreshAsync("token-que-nunca-existiu", default);
        result.Outcome.Should().Be(RefreshOutcome.InvalidOrBreach);
        result.Pair.Should().BeNull();
    }

    // ---- 4) Conflito benigno na janela: 409 retryable, NÃO revoga cadeia, NÃO limpa cookie (13,14) ---

    [Fact]
    public async Task Refresh_ConflitoBenignoNaJanela_RetornaConflict_SemRevogarCadeia()
    {
        var raw0 = await LoginAsync();
        string raw1;
        await using (var db = NewContext(null))
            raw1 = (await ServiceFor(db).RefreshAsync(raw0, default)).Pair!.RefreshToken;

        // Reapresenta o token JÁ rotacionado, dentro da janela de idempotência (revogado agora mesmo).
        RefreshResult conflito;
        await using (var db = NewContext(null))
            conflito = await ServiceFor(db).RefreshAsync(raw0, default);

        conflito.Outcome.Should().Be(RefreshOutcome.RotationConflict, "corrida benigna != breach");
        conflito.Pair.Should().BeNull("nunca devolve token bruto no conflito");

        // A cadeia permanece intacta: o sucessor continua ativo e utilizável.
        var filho = await TokenRowByHashAsync(Hasher.Hash(raw1));
        filho!.IsActive.Should().BeTrue("conflito benigno NÃO revoga a cadeia");

        // E o sucessor ainda rotaciona normalmente (a sessão válida não foi apagada em silêncio).
        await using (var db = NewContext(null))
            (await ServiceFor(db).RefreshAsync(raw1, default)).Outcome
                .Should().Be(RefreshOutcome.Success);
    }

    // ---- 5) Reuso FORA da janela = breach: revoga a cadeia (item 15) ---------------------------------

    [Fact]
    public async Task Refresh_ReusoForaDaJanela_RevogaCadeia()
    {
        var raw0 = await LoginAsync();
        string raw1;
        await using (var db = NewContext(null))
            raw1 = (await ServiceFor(db).RefreshAsync(raw0, default)).Pair!.RefreshToken;

        // Envelhece a revogação do pai para além da janela de idempotência (5 s).
        await BackdateRevokedAtAsync(Hasher.Hash(raw0), TimeSpan.FromSeconds(30));

        RefreshResult breach;
        await using (var db = NewContext(null))
            breach = await ServiceFor(db).RefreshAsync(raw0, default);

        breach.Outcome.Should().Be(RefreshOutcome.InvalidOrBreach, "reuso fora da janela é breach");
        var filho = await TokenRowByHashAsync(Hasher.Hash(raw1));
        filho!.IsRevoked.Should().BeTrue("o breach revoga a CADEIA (o sucessor também cai)");
    }

    // ---- 6) Expiração não dispara revogação de cadeia (Crítico 2) ------------------------------------

    [Fact]
    public async Task Refresh_TokenExpirado_Falha_SemRevogar()
    {
        var raw0 = await LoginAsync();
        await ExpireTokenAsync(Hasher.Hash(raw0));

        RefreshResult result;
        await using (var db = NewContext(null))
            result = await ServiceFor(db).RefreshAsync(raw0, default);

        result.Outcome.Should().Be(RefreshOutcome.InvalidOrBreach);
        var row = await TokenRowByHashAsync(Hasher.Hash(raw0));
        row!.RevokedAt.Should().BeNull("token expirado só falha; NÃO entra na cascata de revogação");
    }

    // ---- 7) Logout revoga pelo hash (item 16) --------------------------------------------------------

    [Fact]
    public async Task Logout_RevogaPorHash()
    {
        var raw0 = await LoginAsync();

        await using (var db = NewContext(null))
            await ServiceFor(db).LogoutAsync(raw0, default);

        var row = await TokenRowByHashAsync(Hasher.Hash(raw0));
        row!.RevokedAt.Should().NotBeNull("logout localiza e revoga pelo hash do cookie");
    }

    // ---- 8) Cadeia percorrida SOMENTE por hashes (item 18) -------------------------------------------

    [Fact]
    public async Task Breach_PercorreCadeiaInteiraPorHashes()
    {
        var raw0 = await LoginAsync();
        string raw1, raw2;
        await using (var db = NewContext(null))
            raw1 = (await ServiceFor(db).RefreshAsync(raw0, default)).Pair!.RefreshToken;
        await using (var db = NewContext(null))
            raw2 = (await ServiceFor(db).RefreshAsync(raw1, default)).Pair!.RefreshToken;

        // Reuso do RAIZ (t0) fora da janela → breach revoga t0→t1→t2, caminhando por ReplacedByTokenHash.
        await BackdateRevokedAtAsync(Hasher.Hash(raw0), TimeSpan.FromSeconds(30));
        await using (var db = NewContext(null))
            (await ServiceFor(db).RefreshAsync(raw0, default)).Outcome
                .Should().Be(RefreshOutcome.InvalidOrBreach);

        var neto = await TokenRowByHashAsync(Hasher.Hash(raw2));
        neto!.IsRevoked.Should().BeTrue(
            "a revogação de cadeia chegou até a ponta caminhando exclusivamente por hashes");
    }

    // ---- 9) Isolamento por tenant preservado (item 20) ----------------------------------------------

    [Fact]
    public async Task Refresh_PreservaIsolamentoPorTenant()
    {
        // Segunda pessoa, em OUTRO tenant.
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        const string emailB = "bob@demo.example.com";
        await using (var ctx = NewContext(null))
        {
            ctx.Tenants.Add(new Tenant { Id = tenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active });
            var accB = new IdentityAccount { Email = emailB, PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha) };
            ctx.IdentityAccounts.Add(accB);
            await ctx.SaveChangesAsync();
            await using var dbB = NewContext(tenantB);
            dbB.Users.Add(new User { TenantId = tenantB, IdentityAccountId = accB.Id, DisplayName = "Bob", Role = TenantRole.Analyst });
            await dbB.SaveChangesAsync();
        }

        var rawA = await LoginAsync();
        string rawB;
        await using (var db = NewContext(null))
            rawB = (await ServiceFor(db).LoginAsync(emailB, Senha, default))!.RefreshToken;

        // Rotaciona a sessão de A; a de B não pode ser tocada.
        await using (var db = NewContext(null))
            await ServiceFor(db).RefreshAsync(rawA, default);

        var tokenB = await TokenRowByHashAsync(Hasher.Hash(rawB));
        tokenB!.TenantId.Should().Be(tenantB);
        tokenB.IsActive.Should().BeTrue("rotação num tenant não afeta sessões de outro");

        // O query filter de tenant continua isolando as leituras por hash.
        await using var ctxA = NewContext(TenantA);
        (await ctxA.UserRefreshTokens.AnyAsync(t => t.TokenHash == Hasher.Hash(rawB)))
            .Should().BeFalse("sob o tenant A, o token de B é invisível");
    }

    // ---- 10) Guard AUD-008 continua protegendo a entidade renomeada (item 21) -----------------------

    [Fact]
    public async Task GuardAud008_BloqueiaEscritaDeRefreshTokenCrossTenant()
    {
        // Um contexto do tenant A tenta gravar um refresh token carimbado para OUTRO tenant → fail-closed.
        var outroTenant = Guid.NewGuid();
        await using var db = NewContext(TenantA);
        db.UserRefreshTokens.Add(new UserRefreshToken
        {
            TenantId = outroTenant,
            UserId = Guid.NewGuid(),
            TokenHash = Hasher.Hash("x"),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<TenantSecurityException>(
            "o guard cross-tenant do AUD-008 segue válido para UserRefreshToken após o rename");
    }

    // ---- Fixture -------------------------------------------------------------------------------------

    private void SeedMembership(Guid tenantId)
    {
        using var db = NewContext(tenantId);
        db.Users.Add(new User
        {
            TenantId = tenantId, IdentityAccountId = _accountId, DisplayName = "Ana", Role = TenantRole.Analyst,
        });
        db.SaveChanges();
    }

    private async Task<string> LoginAsync()
    {
        await using var db = NewContext(null);
        return (await ServiceFor(db).LoginAsync(Email, Senha, default))!.RefreshToken;
    }

    private async Task<UserRefreshToken> SingleTokenRowAsync()
    {
        await using var db = NewContext(null);
        return await db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync();
    }

    private async Task<UserRefreshToken?> TokenRowByHashAsync(string hash)
    {
        await using var db = NewContext(null);
        return await db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash);
    }

    private async Task<List<UserRefreshToken>> AllTokenRowsAsync()
    {
        await using var db = NewContext(null);
        return await db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }

    // Escritas sob o tenant DA LINHA (TenantA): o guard AUD-008 exige tenant resolvido para gravar uma
    // entidade tenant-owned — um contexto sem tenant seria (corretamente) recusado fail-closed.
    private async Task BackdateRevokedAtAsync(string hash, TimeSpan ago)
    {
        await using var db = NewContext(TenantA);
        var row = await db.UserRefreshTokens.FirstAsync(t => t.TokenHash == hash);
        row.RevokedAt = DateTimeOffset.UtcNow - ago;
        await db.SaveChangesAsync();
    }

    private async Task ExpireTokenAsync(string hash)
    {
        await using var db = NewContext(TenantA);
        var row = await db.UserRefreshTokens.FirstAsync(t => t.TokenHash == hash);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    /// <summary>Varre as colunas string da linha à procura do bruto — prova que ele não vazou para o banco.</summary>
    private static bool RawAppearsInAnyColumn(UserRefreshToken row, string raw) =>
        row.TokenHash == raw || row.ReplacedByTokenHash == raw;

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(_options, new SystemTenantContext(tenantId));

    private AuthService ServiceFor(AegisScoreDbContext db) =>
        new(db, _options, new StubTokenService(), new Pbkdf2PasswordHasher(), Hasher,
            Options.Create(new FederationOptions()), NullLogger<AuthService>.Instance);

    /// <summary>Emissor sem JWT real: cada refresh é um bruto ÚNICO de alta entropia (o serviço o hasheia).</summary>
    private sealed class StubTokenService : IJwtTokenService
    {
        public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User m, IdentityAccount a) =>
            ($"access.{m.TenantId}.{a.Email}", DateTimeOffset.UtcNow.AddMinutes(10));

        public (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken() =>
            (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(7));
    }
}
