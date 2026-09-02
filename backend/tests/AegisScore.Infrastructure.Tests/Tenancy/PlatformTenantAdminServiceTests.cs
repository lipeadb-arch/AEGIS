using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Tenancy;

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Testes do <see cref="PlatformTenantAdminService"/> — o ciclo de vida
/// administrativo dos tenants (PlatformAdmin). Mesmo harness relacional dos demais (SQLite in-memory), então o
/// índice único de <c>Tenant.Slug</c>, os Global Query Filters e as FKs reais são exercitados de verdade. A
/// AUTORIZAÇÃO (só PlatformAdmin) é do controller (verificada por
/// <c>AdminLifecycleAuthorizationTests</c>); aqui exercitamos a REGRA.
/// </summary>
public sealed class PlatformTenantAdminServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TenantC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    /// <summary>Ator PlatformAdmin com acesso a TenantA e TenantB (dois ambientes ativos).</summary>
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public PlatformTenantAdminServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();

        // Tenants e a identidade são GLOBAIS (não ITenantOwned): entram por qualquer contexto.
        ctx.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Cliente A", Slug = "cliente-a", Status = TenantStatus.Active },
            new Tenant { Id = TenantB, Name = "Cliente B", Slug = "cliente-b", Status = TenantStatus.Active },
            new Tenant { Id = TenantC, Name = "Cliente C", Slug = "cliente-c", Status = TenantStatus.Onboarding });
        ctx.IdentityAccounts.Add(new IdentityAccount
        {
            Id = ActorId, Email = "plataforma@demo.example.com", PlatformRole = PlatformRole.PlatformAdmin,
        });
        ctx.SaveChanges();

        // Cada membership (ITenantOwned) é carimbado pelo StampTenant do SEU tenant — por isso cada um entra
        // por um contexto ligado ao próprio tenant (mesmo padrão do provisionamento real). O ator fica com
        // acessos ATIVOS em A e B (dois ambientes utilizáveis).
        using (var a = NewContext(TenantA))
        {
            a.Users.Add(new User { IdentityAccountId = ActorId, DisplayName = "Admin", Role = TenantRole.TenantAdmin });
            a.SaveChanges();
        }
        using (var b = NewContext(TenantB))
        {
            b.Users.Add(new User { IdentityAccountId = ActorId, DisplayName = "Admin", Role = TenantRole.TenantAdmin });
            b.SaveChanges();
        }
    }

    public void Dispose() => _connection.Dispose();

    // ---- Leitura ----------------------------------------------------------------

    [Fact]
    public async Task ListTenantsAsync_TrazTodosOsTenants_InclusiveSuspensos()
    {
        await using var db = NewContext(TenantA);
        var svc = Service(db);

        await svc.SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));

        var list = await Service(NewContext(TenantA)).ListTenantsAsync();
        list.Should().HaveCount(3, "o catálogo da plataforma enxerga TODOS os clientes");
        list.Should().Contain(t => t.Id == TenantB && t.Status == TenantStatus.Suspended,
            "inclusive os suspensos — é o catálogo administrativo");
    }

    // ---- Renomear (nome de exibição; slug IMUTÁVEL) -----------------------------

    [Fact]
    public async Task RenameTenantAsync_AlteraNome_NaoAlteraSlug()
    {
        await using var db = NewContext(TenantA);
        var result = await Service(db).RenameTenantAsync(new RenameTenantCommand(TenantA, "  Cliente A (novo)  "));

        result.Succeeded.Should().BeTrue();
        result.Tenant!.Name.Should().Be("Cliente A (novo)", "o nome é aparado e aplicado");
        result.Tenant.Slug.Should().Be("cliente-a", "o slug é imutável neste pacote");

        var saved = await db.Tenants.SingleAsync(t => t.Id == TenantA);
        saved.Slug.Should().Be("cliente-a", "o slug persistido não muda");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameTenantAsync_NomeInvalido_EhRejeitado(string nome)
    {
        await using var db = NewContext(TenantA);
        var result = await Service(db).RenameTenantAsync(new RenameTenantCommand(TenantA, nome));

        result.Status.Should().Be(TenantAdminMutationStatus.InvalidName);
        (await db.Tenants.SingleAsync(t => t.Id == TenantA)).Name.Should().Be("Cliente A", "nada alterado");
    }

    [Fact]
    public async Task RenameTenantAsync_TenantInexistente_EhNotFound()
    {
        await using var db = NewContext(TenantA);
        var result = await Service(db).RenameTenantAsync(new RenameTenantCommand(Guid.NewGuid(), "X"));
        result.Status.Should().Be(TenantAdminMutationStatus.NotFound);
    }

    // ---- Suspender / reativar ---------------------------------------------------

    [Fact]
    public async Task SuspendAsync_DefineSuspenso_QuandoOAtorTemOutroAmbienteAtivo()
    {
        await using var db = NewContext(TenantA);
        // O ator tem A e B ativos → suspender B é permitido (ainda resta A).
        var result = await Service(db).SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));

        result.Succeeded.Should().BeTrue();
        result.Tenant!.Status.Should().Be(TenantStatus.Suspended);
        (await db.Tenants.SingleAsync(t => t.Id == TenantB)).Status.Should().Be(TenantStatus.Suspended);
    }

    [Fact]
    public async Task SuspendAsync_RevogaRefreshTokensAtivosDoAmbiente()
    {
        Guid userBId;
        await using (var seed = NewContext(TenantB))
        {
            // Um usuário qualquer de TenantB com um refresh token ativo.
            var acc = new IdentityAccount { Email = "user-b@demo.example.com" };
            seed.IdentityAccounts.Add(acc);
            var user = new User { TenantId = TenantB, IdentityAccountId = acc.Id, DisplayName = "User B", Role = TenantRole.Analyst };
            seed.Users.Add(user);
            seed.UserRefreshTokens.Add(new UserRefreshToken
            {
                TenantId = TenantB, UserId = user.Id, TokenHash = new string('a', 64),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await seed.SaveChangesAsync();
            userBId = user.Id;
        }

        await using (var db = NewContext(TenantA))
            await Service(db).SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));

        await using var assert = NewContext(TenantB);
        var token = await assert.UserRefreshTokens.IgnoreQueryFilters().SingleAsync(t => t.UserId == userBId);
        token.RevokedAt.Should().NotBeNull("suspender revoga as sessões ativas do ambiente");
    }

    [Fact]
    public async Task SuspendAsync_TenantSuspenso_IndisponivelNosFluxosNormais()
    {
        await using (var db = NewContext(TenantA))
            await Service(db).SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));

        // Mesmo predicado que o login/seletor usa (AuthService.ValidMembershipsAsync): ativo E não suspenso.
        await using var assert = NewContext(TenantA);
        var accessible = await (
            from u in assert.Users.IgnoreQueryFilters()
            join t in assert.Tenants on u.TenantId equals t.Id
            where u.IdentityAccountId == ActorId && u.IsActive && t.Status != TenantStatus.Suspended
            select t.Id).ToListAsync();

        accessible.Should().NotContain(TenantB, "um tenant suspenso deixa de ser ambiente utilizável");
        accessible.Should().Contain(TenantA, "os demais ambientes do ator seguem disponíveis");
    }

    [Fact]
    public async Task SuspendAsync_EhIdempotente()
    {
        await using var db = NewContext(TenantA);
        var svc = Service(db);
        await svc.SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));
        var again = await svc.SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));

        again.Succeeded.Should().BeTrue("suspender o já-suspenso é sucesso sem efeito");
        again.Tenant!.Status.Should().Be(TenantStatus.Suspended);
    }

    [Fact]
    public async Task SuspendAsync_UltimoAmbienteDoAtor_EhBarrado()
    {
        // Ator com acesso ativo SÓ em TenantA: suspender A o trancaria para fora da plataforma.
        await using var db = NewContext(TenantA);
        await db.Users.IgnoreQueryFilters().Where(u => u.IdentityAccountId == ActorId && u.TenantId == TenantB)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, _ => false));

        var result = await Service(NewContext(TenantA))
            .SetTenantStatusAsync(new SetTenantStatusCommand(TenantA, Suspend: true, ActorId));

        result.Status.Should().Be(TenantAdminMutationStatus.SelfLockoutForbidden,
            "a plataforma não pode ficar sem forma administrativa válida de recuperação pelo ator");
        (await NewContext(TenantA).Tenants.SingleAsync(t => t.Id == TenantA)).Status
            .Should().Be(TenantStatus.Active, "nada foi alterado quando a suspensão é barrada");
    }

    [Fact]
    public async Task ReactivateAsync_LevaSuspensoDeVoltaParaActive_EhIdempotente()
    {
        await using var db = NewContext(TenantA);
        var svc = Service(db);
        await svc.SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: true, ActorId));

        var reactivated = await svc.SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: false, ActorId));
        reactivated.Tenant!.Status.Should().Be(TenantStatus.Active);

        // Idempotente: reativar de novo não muda nada.
        (await svc.SetTenantStatusAsync(new SetTenantStatusCommand(TenantB, Suspend: false, ActorId)))
            .Tenant!.Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task ReactivateAsync_TenantEmOnboarding_PermaneceOnboarding()
    {
        await using var db = NewContext(TenantA);
        // Reativar um tenant NÃO suspenso é no-op: não inventa transição Onboarding→Active.
        var result = await Service(db).SetTenantStatusAsync(new SetTenantStatusCommand(TenantC, Suspend: false, ActorId));
        result.Tenant!.Status.Should().Be(TenantStatus.Onboarding);
    }

    // ---- Fixture ----------------------------------------------------------------

    private DbContextOptions<AegisScoreDbContext> Options =>
        new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;

    private AegisScoreDbContext NewContext(Guid? tenantId) => new(Options, new SystemTenantContext(tenantId));

    private PlatformTenantAdminService Service(AegisScoreDbContext db) =>
        new(db, NullLogger<PlatformTenantAdminService>.Instance);
}
