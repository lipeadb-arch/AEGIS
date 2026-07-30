using System.IdentityModel.Tokens.Jwt;
using System.Linq;
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
/// [AEGIS-AUD-012] Seleção EXPLÍCITA de tenant no login: o serviço nunca escolhe o primeiro registro em
/// silêncio. Prova os invariantes de maior risco com JWT REAL (o ticket de seleção round-trip por
/// assinatura/audience própria/propósito): um único acesso auto-seleciona; vários exigem o último tenant
/// REVALIDADO ou a escolha explícita; o último tenant só vale se ainda houver membership ativo nele; e o
/// ticket de seleção NÃO é uma sessão (audience própria, sem tenant_id).
/// </summary>
public sealed class TenantSelectionTests : IDisposable
{
    private const string Senha = "uma frase longa e boa de verdade";
    private const string AnaEmail = "ana@demo.example.com";
    private const string SoloEmail = "solo@demo.example.com";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TenantSuspensa = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TenantSemAcesso = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly Sha256RefreshTokenHasher Hasher = new();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;
    private Guid _anaId;
    private Guid _soloId;

    public TenantSelectionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Bravo", Slug = "bravo", Status = TenantStatus.Active },
            new Tenant { Id = TenantSuspensa, Name = "Zulu", Slug = "zulu", Status = TenantStatus.Suspended },
            new Tenant { Id = TenantSemAcesso, Name = "Kilo", Slug = "kilo", Status = TenantStatus.Active });

        var ana = new IdentityAccount { Email = AnaEmail, PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha) };
        var solo = new IdentityAccount { Email = SoloEmail, PasswordHash = new Pbkdf2PasswordHasher().Hash(Senha) };
        ctx.IdentityAccounts.AddRange(ana, solo);
        ctx.SaveChanges();
        _anaId = ana.Id;
        _soloId = solo.Id;

        // Ana: DOIS acessos válidos (A, B) + um acesso a um tenant SUSPENSO (não deve contar/valer).
        SeedMembership(TenantA, _anaId, TenantRole.Analyst);
        SeedMembership(TenantB, _anaId, TenantRole.TenantAdmin);
        SeedMembership(TenantSuspensa, _anaId, TenantRole.Analyst);
        // Solo: um ÚNICO acesso.
        SeedMembership(TenantA, _soloId, TenantRole.Analyst);
    }

    public void Dispose() => _connection.Dispose();

    // ---- Resolução do ambiente no login ----------------------------------------------------------

    [Fact]
    public async Task Login_UmUnicoAcesso_SelecionaAutomaticamente()
    {
        await using var db = NewContext(null);
        var result = await Auth(db).LoginAsync(SoloEmail, Senha, null, default);

        result.Outcome.Should().Be(LoginOutcome.Authenticated, "um único acesso pode ser selecionado automaticamente");
        TenantClaim(result.Pair!.AccessToken).Should().Be(TenantA.ToString());
        result.SelectionTicket.Should().BeNull("sessão emitida não precisa de ticket");
    }

    [Fact]
    public async Task Login_VariosAcessos_SemUltimoTenant_ExigeSelecao()
    {
        await using var db = NewContext(null);
        var result = await Auth(db).LoginAsync(AnaEmail, Senha, lastTenantId: null, default);

        result.Outcome.Should().Be(LoginOutcome.SelectionRequired);
        result.Pair.Should().BeNull("nenhuma sessão antes da escolha");
        result.Tenants!.Select(t => t.Slug).Should().BeEquivalentTo(new[] { "alfa", "bravo" },
            "só ambientes ativos e não suspensos entram na seleção");
    }

    [Fact]
    public async Task Login_UltimoTenantValido_AutenticaDireto_SemSelecao()
    {
        await using var db = NewContext(null);
        var result = await Auth(db).LoginAsync(AnaEmail, Senha, lastTenantId: TenantB, default);

        result.Outcome.Should().Be(LoginOutcome.Authenticated, "o último tenant revalidou (membership ativo, tenant não suspenso)");
        TenantClaim(result.Pair!.AccessToken).Should().Be(TenantB.ToString(), "reabre exatamente o último ambiente");
    }

    [Fact]
    public async Task Login_UltimoTenantSuspenso_IgnoraDica_ExigeSelecao()
    {
        // Ana TEM membership no tenant suspenso, mas ele não revalida — a dica é descartada e não vira sessão.
        await using var db = NewContext(null);
        var result = await Auth(db).LoginAsync(AnaEmail, Senha, lastTenantId: TenantSuspensa, default);

        result.Outcome.Should().Be(LoginOutcome.SelectionRequired,
            "um último tenant suspenso não pode ser reutilizado — exige escolha explícita");
        result.Tenants!.Select(t => t.Slug).Should().NotContain("zulu");
    }

    [Fact]
    public async Task Login_UltimoTenantSemMembership_IgnoraDica_ExigeSelecao()
    {
        // Dica apontando para um tenant onde Ana NÃO tem acesso: nunca vira sessão (não é um oráculo de acesso).
        await using var db = NewContext(null);
        var result = await Auth(db).LoginAsync(AnaEmail, Senha, lastTenantId: TenantSemAcesso, default);

        result.Outcome.Should().Be(LoginOutcome.SelectionRequired);
    }

    // ---- Conclusão da seleção pelo ticket --------------------------------------------------------

    [Fact]
    public async Task SelectTenant_ComTicketDoLogin_EmiteSessaoParaOAlvoEscolhido()
    {
        string ticket;
        await using (var db = NewContext(null))
            ticket = (await Auth(db).LoginAsync(AnaEmail, Senha, null, default)).SelectionTicket!;

        await using (var db = NewContext(null))
        {
            var pair = await Auth(db).SelectTenantAsync(ticket, TenantB, default);
            pair.Should().NotBeNull();
            TenantClaim(pair!.AccessToken).Should().Be(TenantB.ToString());
            RoleClaim(pair.AccessToken).Should().Be(nameof(TenantRole.TenantAdmin), "o papel é o do ambiente escolhido");
        }
    }

    [Fact]
    public async Task SelectTenant_TicketInvalido_Recusa()
    {
        await using var db = NewContext(null);
        (await Auth(db).SelectTenantAsync("nao.e.um.ticket", TenantB, default))
            .Should().BeNull("ticket adulterado/inexistente falha fechado, sem tocar o banco");
    }

    [Fact]
    public async Task SelectTenant_AlvoSemAcessoAtivo_Recusa_MesmoComTicketValido()
    {
        string ticket;
        await using (var db = NewContext(null))
            ticket = (await Auth(db).LoginAsync(AnaEmail, Senha, null, default)).SelectionTicket!;

        // Ticket legítimo de Ana, mas o alvo é um tenant onde ela não tem acesso (ou suspenso): negado.
        await using (var db = NewContext(null))
        {
            (await Auth(db).SelectTenantAsync(ticket, TenantSemAcesso, default)).Should().BeNull();
            (await Auth(db).SelectTenantAsync(ticket, TenantSuspensa, default)).Should().BeNull("tenant suspenso não recebe sessão");
        }
    }

    [Fact]
    public async Task SelectionTicket_NaoEhUmaSessao_AudiencePropriaESemTenant()
    {
        string ticket;
        await using (var db = NewContext(null))
            ticket = (await Auth(db).LoginAsync(AnaEmail, Senha, null, default)).SelectionTicket!;

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(ticket);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("aegis-score:tenant-selection",
            "a audience própria faz o Bearer padrão rejeitar o ticket como access token");
        jwt.Claims.Should().Contain(c => c.Type == "purpose" && c.Value == "tenant-selection");
        jwt.Claims.Should().Contain(c => c.Type == "account_id" && c.Value == _anaId.ToString());
        jwt.Claims.Should().NotContain(c => c.Type == "tenant_id", "um ticket não carrega ambiente — não é sessão");
        jwt.Claims.Should().NotContain(c => c.Type == "role");
    }

    // ---- Fixture ---------------------------------------------------------------------------------

    private static string? TenantClaim(string accessToken) => Claim(accessToken, "tenant_id");
    private static string? RoleClaim(string accessToken) => Claim(accessToken, "role");

    private static string? Claim(string accessToken, string type) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .FirstOrDefault(c => c.Type == type)?.Value;

    private void SeedMembership(Guid tenantId, Guid accountId, TenantRole role)
    {
        using var db = NewContext(tenantId);
        db.Users.Add(new User { TenantId = tenantId, IdentityAccountId = accountId, DisplayName = "User", Role = role });
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
