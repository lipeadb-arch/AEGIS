using AegisScore.Application.Abstractions;
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
/// [AEGIS-AUD-010] Testes da CONCESSÃO DE ACESSO A TENANT (<see cref="UserManagementService"/>) — a
/// autoridade tenant-scoped, agora SEPARADA do provisionamento global de identidade
/// (<see cref="IdentityProvisioningServiceTests"/>). Harness dos demais (SQLite in-memory), então o índice
/// único <c>(TenantId, IdentityAccountId)</c>, o Global Query Filter e o stamping fail-closed são
/// exercitados de verdade — que é onde mora a garantia de isolamento deste serviço. Prova central: esta
/// superfície NÃO cria identidade, NÃO toca credencial nem vínculo Entra, e nunca escreve fora do ambiente.
/// </summary>
public sealed class UserManagementServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;

    public UserManagementServiceTests()
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

    // ---- A identidade global deve PREEXISTIR (sem auto-provisionamento) ----------

    [Fact]
    public async Task GrantAccess_IdentidadeInexistente_EhNaoEncontrada_SemCriarNada()
    {
        await using var db = NewContext(TenantA);
        // Um IdentityAccountId que não existe: resposta genérica, e NADA é criado (nem conta, nem membership).
        var result = await ServiceFor(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(Guid.NewGuid(), "Ana", UserRole.Analyst));

        result.Status.Should().Be(AccessGrantStatus.IdentityNotFound);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse("a concessão nunca provisiona identidade global");
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse("nenhum membership é criado");
    }

    [Fact]
    public async Task GrantAccess_IdentidadePreexistente_CriaMembershipNoTenantAmbiente()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "  Ana Silva  ", UserRole.Manager));

        result.Status.Should().Be(AccessGrantStatus.Granted);
        result.User!.Email.Should().Be("ana@demo.example.com", "o e-mail vem da identidade global resolvida");

        var saved = await db.Users.SingleAsync();
        saved.IdentityAccountId.Should().Be(accountId);
        saved.TenantId.Should().Be(TenantA, "carimbado pelo SaveChanges, não pelo chamador");
        saved.DisplayName.Should().Be("Ana Silva", "aparado, sem inventar nome a partir do e-mail");
        saved.Role.Should().Be(UserRole.Manager);
        saved.IsActive.Should().BeTrue();
    }

    // ---- Não toca credencial global nem vínculo Entra ---------------------------

    [Fact]
    public async Task GrantAccess_NaoAlteraPasswordHashNemVinculoEntra()
    {
        var accountId = await SeedIdentityAsync(
            "ana@demo.example.com", withPassword: true, tid: "t-1", oid: "o-1");
        string? hashAntes;
        await using (var read = NewContext(null))
        {
            hashAntes = (await read.IdentityAccounts.SingleAsync(a => a.Id == accountId)).PasswordHash;
        }

        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA).GrantAccessAsync(
                new GrantTenantAccessCommand(accountId, "Ana", UserRole.TenantAdmin));

        await using var assert = NewContext(null);
        var acc = await assert.IdentityAccounts.SingleAsync(a => a.Id == accountId);
        acc.PasswordHash.Should().Be(hashAntes, "conceder acesso NÃO reseta a credencial global");
        acc.ExternalTenantId.Should().Be("t-1", "o vínculo Entra é imutável por esta superfície");
        acc.ExternalObjectId.Should().Be("o-1");
    }

    // ---- Só escreve no tenant AMBIENTE ------------------------------------------

    [Fact]
    public async Task GrantAccess_CriaMembershipApenasNoTenantAmbiente_NaoTocaOutroTenant()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);

        // A MESMA pessoa já tem um acesso INATIVO no Tenant B (semeado à parte).
        await using (var seedB = NewContext(TenantB))
        {
            seedB.Users.Add(new User
            {
                TenantId = TenantB, IdentityAccountId = accountId,
                DisplayName = "Ana (B)", Role = UserRole.Analyst, IsActive = false,
            });
            await seedB.SaveChangesAsync();
        }

        // Um TenantAdmin de A concede acesso: cria membership em A e NÃO toca o de B.
        await using (var db = NewContext(TenantA))
        {
            var result = await ServiceFor(db, TenantA).GrantAccessAsync(
                new GrantTenantAccessCommand(accountId, "Ana (A)", UserRole.Manager));
            result.Status.Should().Be(AccessGrantStatus.Granted);
        }

        await using var assert = NewContext(null);
        var acessos = await assert.Users.IgnoreQueryFilters()
            .Where(u => u.IdentityAccountId == accountId).OrderBy(u => u.TenantId).ToListAsync();
        acessos.Should().HaveCount(2);

        var emA = acessos.Single(u => u.TenantId == TenantA);
        emA.Role.Should().Be(UserRole.Manager);
        emA.IsActive.Should().BeTrue();

        var emB = acessos.Single(u => u.TenantId == TenantB);
        emB.Role.Should().Be(UserRole.Analyst, "o acesso do outro tenant permanece intacto");
        emB.IsActive.Should().BeFalse("um TenantAdmin de A nunca escreve no tenant B");
    }

    // ---- Idempotência: atualização e reativação ---------------------------------

    [Fact]
    public async Task GrantAccess_MembershipExistente_AtualizaPapelENome_Idempotente()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        await svc.GrantAccessAsync(new GrantTenantAccessCommand(accountId, "Ana", UserRole.Analyst));
        var result = await svc.GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "Ana Silva", UserRole.TenantAdmin));

        result.Status.Should().Be(AccessGrantStatus.AccessUpdated);
        var saved = await db.Users.SingleAsync();
        saved.Role.Should().Be(UserRole.TenantAdmin);
        saved.DisplayName.Should().Be("Ana Silva");
        (await db.Users.CountAsync()).Should().Be(1, "reconcede o MESMO membership, não empilha");
    }

    [Fact]
    public async Task GrantAccess_MembershipInativo_EhReativado()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        await svc.GrantAccessAsync(new GrantTenantAccessCommand(accountId, "Ana", UserRole.Analyst));

        var user = await db.Users.SingleAsync();
        user.IsActive = false;
        await db.SaveChangesAsync();

        var result = await svc.GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "Ana", UserRole.Analyst));

        result.Status.Should().Be(AccessGrantStatus.AccessUpdated);
        (await db.Users.SingleAsync()).IsActive.Should().BeTrue();
    }

    // ---- Escalonamento de privilégio barrado ------------------------------------

    [Fact]
    public async Task GrantAccess_PlatformAdmin_NaoEhAtribuivel()
    {
        var accountId = await SeedIdentityAsync("root@demo.example.com", withPassword: true);
        await using var db = NewContext(TenantA);

        var result = await ServiceFor(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "Root", UserRole.PlatformAdmin));

        result.Status.Should().Be(AccessGrantStatus.RoleNotAssignable);
        (await db.Users.AnyAsync()).Should().BeFalse("nenhum PlatformAdmin nasce por esta superfície");
    }

    [Fact]
    public async Task GrantAccess_MembershipExistente_NaoPromoveParaPlatformAdmin()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        await svc.GrantAccessAsync(new GrantTenantAccessCommand(accountId, "Ana", UserRole.Analyst));

        // O caminho de ATUALIZAÇÃO não pode ser a porta dos fundos do escalonamento.
        var result = await svc.GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "Ana", UserRole.PlatformAdmin));

        result.Status.Should().Be(AccessGrantStatus.RoleNotAssignable);
        (await db.Users.SingleAsync()).Role.Should().Be(UserRole.Analyst, "o papel vigente fica intacto");
    }

    // ---- Validação e fail-closed ------------------------------------------------

    [Fact]
    public async Task GrantAccess_NomeDeExibicaoVazio_EhRejeitado()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await using var db = NewContext(TenantA);

        var result = await ServiceFor(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "   ", UserRole.Analyst));

        result.Status.Should().Be(AccessGrantStatus.InvalidDisplayName);
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task GrantAccess_SemTenantNoContexto_FalhaFechado()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await using var db = NewContext(null);
        var act = () => ServiceFor(db, null).GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "Ana", UserRole.Analyst));

        await act.Should().ThrowAsync<TenantSecurityException>();
    }

    // ---- Invariante de banco: um membership por (tenant, pessoa) -----------------

    [Fact]
    public async Task IndiceUnico_RejeitaSegundoMembershipDaMesmaPessoaAoMesmoTenant()
    {
        var accountId = await SeedIdentityAsync("ana@demo.example.com", withPassword: true);
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA).GrantAccessAsync(
            new GrantTenantAccessCommand(accountId, "Ana", UserRole.Analyst));

        // Insert cru, contornando o serviço: é o índice (TenantId, IdentityAccountId) que precisa
        // barrar a corrida, não o if do C#.
        db.Users.Add(new User
        {
            TenantId = TenantA, IdentityAccountId = accountId,
            DisplayName = "clone", Role = UserRole.Analyst,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // ---- Fixture ----------------------------------------------------------------

    /// <summary>
    /// Semeia uma <see cref="IdentityAccount"/> global (a "pessoa") e devolve o Id. É o pré-requisito de
    /// toda concessão — nesta arquitetura a criação da identidade é autoridade SEPARADA (PlatformAdmin).
    /// </summary>
    private async Task<Guid> SeedIdentityAsync(
        string email, bool withPassword, string? tid = null, string? oid = null)
    {
        await using var db = NewContext(null);
        var account = new IdentityAccount
        {
            Email = email,
            PasswordHash = withPassword ? new Pbkdf2PasswordHasher().Hash("uma frase longa e boa") : null,
            ExternalTenantId = tid,
            ExternalObjectId = oid,
        };
        db.IdentityAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IUserManagementService ServiceFor(AegisScoreDbContext db, Guid? tenantId) =>
        new UserManagementService(
            db, new SystemTenantContext(tenantId), NullLogger<UserManagementService>.Instance);
}
