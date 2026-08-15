using AegisScore.Application.Abstractions;
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
/// [AEGIS-AUD-010 × AUD-007] Fluxo ponta a ponta das autoridades SEPARADAS, com os serviços REAIS
/// (<see cref="IdentityProvisioningService"/> + <see cref="UserManagementService"/> + <see cref="AuthService"/>)
/// sobre um mesmo banco:
///  1. PlatformAdmin provisiona uma identidade GLOBAL federated-only (sem senha local);
///  2. essa conta NÃO autentica pelo fluxo Local (sem hash), mesmo em modo Hybrid;
///  3. a troca federada é NEGADA enquanto não houver membership (sem auto-provisionamento no login Entra);
///  4. TenantAdmin concede acesso usando o IdentityAccountId;
///  5. o primeiro login Entra vincula tid/oid e o AEGIS emite a sessão local.
/// </summary>
public sealed class IdentitySeparationFlowTests : IDisposable
{
    private const string Email = "ana@demo.example.com";
    private const string EntraTid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";
    private static readonly Guid TenantA = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public IdentitySeparationFlowTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Alfa", Slug = "alfa", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task FluxoFederatedOnly_ProvisionaVinculaEAutentica_SemAutoProvisionamento()
    {
        // 1) PLATFORMADMIN provisiona a identidade global federated-only (Hybrid: senha opcional; aqui sem).
        Guid accountId;
        await using (var db = NewContext(null))
        {
            var prov = await IdentityService(db)
                .ProvisionAsync(new ProvisionIdentityCommand(Email));   // sem senha
            prov.Succeeded.Should().BeTrue();
            prov.Identity!.HasLocalCredential.Should().BeFalse();
            accountId = prov.Identity.Id;
        }

        // Conta existe, SEM credencial local e SEM membership.
        await using (var assert = NewContext(null))
        {
            (await assert.IdentityAccounts.SingleAsync(a => a.Id == accountId)).PasswordHash.Should().BeNull();
            (await assert.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        }

        // 2) O fluxo LOCAL não autentica uma conta sem hash — mesmo em Hybrid (login por senha habilitado).
        await using (var db = NewContext(null))
            (await AuthFor(db).LoginAsync(Email, "qualquer senha longa aqui", null, default)).Pair
                .Should().BeNull("conta sem PasswordHash nunca autentica pelo fluxo Local");

        // 3) A troca federada é NEGADA sem membership — o login Entra NÃO provisiona acesso.
        await using (var db = NewContext(null))
            (await AuthFor(db).ExchangeFederatedAsync(new FederatedIdentity(EntraTid, Oid, Email), null, default)).Pair
                .Should().BeNull("sem membership ativo não há sessão, e nada é criado no login Entra");

        // A tentativa negada não deixou efeito colateral (nenhum vínculo tid/oid gravado).
        await using (var assert = NewContext(null))
            (await assert.IdentityAccounts.SingleAsync(a => a.Id == accountId)).ExternalObjectId
                .Should().BeNull("login negado não vincula a identidade");

        // 4) TENANTADMIN concede acesso usando o IdentityAccountId (nunca e-mail nem senha).
        await using (var db = NewContext(TenantA))
        {
            var grant = await GrantService(db, TenantA)
                .GrantAccessAsync(new GrantTenantAccessCommand(accountId, "Ana", TenantRole.Analyst, Guid.NewGuid()));
            grant.Status.Should().Be(AccessGrantStatus.Granted);
        }

        // 5) Agora o primeiro login Entra VINCULA tid/oid e emite a sessão local.
        await using (var db = NewContext(null))
        {
            var pair = (await AuthFor(db).ExchangeFederatedAsync(new FederatedIdentity(EntraTid, Oid, Email), null, default)).Pair;
            pair.Should().NotBeNull("com membership ativo, a conta federated-only autentica pelo Entra");
        }

        await using (var assert = NewContext(null))
        {
            var acc = await assert.IdentityAccounts.SingleAsync(a => a.Id == accountId);
            acc.ExternalTenantId.Should().Be(EntraTid, "o vínculo tid/oid foi fechado no primeiro login");
            acc.ExternalObjectId.Should().Be(Oid);
            acc.PasswordHash.Should().BeNull("federar não cria credencial local");
        }

        // E o fluxo Local segue recusando: a conta continua federated-only.
        await using (var db = NewContext(null))
            (await AuthFor(db).LoginAsync(Email, "qualquer senha longa aqui", null, default)).Pair
                .Should().BeNull("continua sem credencial local após federar");
    }

    // ---- Fixture ----------------------------------------------------------------

    private static IOptions<FederationOptions> Hybrid() => Options.Create(new FederationOptions
    {
        Mode = FederationMode.Hybrid,
        TenantId = EntraTid,
        ApiClientId = "api-client-id",
        ApiScope = "api://api-client-id/access_as_user",
        SpaClientId = "spa-client-id",
    });

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(_options, new SystemTenantContext(tenantId));

    private IdentityProvisioningService IdentityService(AegisScoreDbContext db) =>
        new(db, new Pbkdf2PasswordHasher(), Hybrid(), NullLogger<IdentityProvisioningService>.Instance);

    private static UserManagementService GrantService(AegisScoreDbContext db, Guid tenantId) =>
        new(db, new SystemTenantContext(tenantId), NullLogger<UserManagementService>.Instance);

    private AuthService AuthFor(AegisScoreDbContext db) =>
        new(db, _options, new JwtTokenService(Options.Create(new JwtOptions
        {
            SigningKey = "aegis-test-signing-key-com-mais-de-32-bytes", Issuer = "aegis-score", Audience = "aegis-score",
        })), new Pbkdf2PasswordHasher(), new Sha256RefreshTokenHasher(), Hybrid(),
        NullLogger<AuthService>.Instance);
}
