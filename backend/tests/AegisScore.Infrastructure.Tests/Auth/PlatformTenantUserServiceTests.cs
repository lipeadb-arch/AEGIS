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
/// Onboarding de usuário no tenant (<see cref="PlatformTenantUserService"/>): a operação que cria a
/// identidade global (quando nova) E concede acesso, atomicamente. Harness SQLite in-memory (índice único de
/// e-mail + query filter + stamping reais). Prova central: identidade nova nasce COM o acesso (sem estado
/// parcial); identidade existente é APENAS reconhecida (senha, papel global e vínculo Entra intactos).
/// </summary>
public sealed class PlatformTenantUserServiceTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public PlatformTenantUserServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        ctx.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente A", Slug = "cliente-a", Status = TenantStatus.Active });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Cenário 3: identidade + acesso, sem estado parcial ---------------------

    [Fact]
    public async Task Onboard_IdentidadeNova_CriaContaEAcesso_NaMesmaTransacao()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Local).OnboardAsync(
            OnboardCmd("gestor@demo.example.com", "  Gestor  ", TenantRole.Manager, "uma frase longa e boa"));

        result.Status.Should().Be(TenantUserOnboardingStatus.IdentityCreatedAndGranted);
        result.IdentityExisted.Should().BeFalse();
        result.User!.HasLocalCredential.Should().BeTrue("modo Local com senha → credencial local");

        (await db.IdentityAccounts.CountAsync()).Should().Be(1, "a identidade nasceu");
        var membership = await db.Users.SingleAsync();
        membership.TenantId.Should().Be(TenantA, "carimbado no tenant ambiente");
        membership.Role.Should().Be(TenantRole.Manager);
        membership.DisplayName.Should().Be("Gestor", "aparado");
        membership.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Onboard_PapelIndefinido_NaoDeixaEstadoParcial()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Local).OnboardAsync(
            OnboardCmd("x@demo.example.com", "X", (TenantRole)999, "uma frase longa e boa"));

        result.Status.Should().Be(TenantUserOnboardingStatus.RoleNotAssignable);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse("recusa ANTES de escrever — sem identidade órfã");
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Onboard_ModoLocalSemSenha_ExigeSenha()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Local).OnboardAsync(
            OnboardCmd("x@demo.example.com", "X", TenantRole.Analyst, initialPassword: null));

        result.Status.Should().Be(TenantUserOnboardingStatus.PasswordRequired);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Onboard_ModoFederadoComSenha_RecusaSenha()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Federated).OnboardAsync(
            OnboardCmd("x@demo.example.com", "X", TenantRole.Analyst, "uma frase longa e boa"));

        result.Status.Should().Be(TenantUserOnboardingStatus.PasswordNotAllowed);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Onboard_ModoFederado_CriaContaFederatedOnly()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Federated).OnboardAsync(
            OnboardCmd("x@demo.example.com", "X", TenantRole.Analyst, initialPassword: null));

        result.Status.Should().Be(TenantUserOnboardingStatus.IdentityCreatedAndGranted);
        result.User!.HasLocalCredential.Should().BeFalse("federated-only: sem senha local");
        (await db.IdentityAccounts.SingleAsync()).PasswordHash.Should().BeNull("nunca '' nem hash fictício");
    }

    [Fact]
    public async Task Onboard_SenhaFraca_EhRejeitada()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Local).OnboardAsync(
            OnboardCmd("x@demo.example.com", "X", TenantRole.Analyst, "curta"));

        result.Status.Should().Be(TenantUserOnboardingStatus.WeakPassword);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse();
    }

    // ---- Cenário 4: identidade EXISTENTE preservada por completo -----------------

    [Fact]
    public async Task Onboard_IdentidadeExistente_PreservaSenhaPapelGlobalEVinculoEntra_ESenhaInformadaEhIgnorada()
    {
        // Uma identidade global JÁ existente: com senha, PlatformAdmin e vínculo Entra.
        var originalHash = new Pbkdf2PasswordHasher().Hash("senha original longa e boa");
        Guid accountId;
        await using (var seed = NewContext(null))
        {
            var account = new IdentityAccount
            {
                Email = "chefe@demo.example.com",
                PasswordHash = originalHash,
                PlatformRole = PlatformRole.PlatformAdmin,
                ExternalTenantId = "11111111-1111-1111-1111-1111111111aa",
                ExternalObjectId = "22222222-2222-2222-2222-2222222222bb",
            };
            seed.IdentityAccounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Hybrid).OnboardAsync(
            // Envia uma senha DIFERENTE de propósito: ela NÃO pode redefinir a credencial existente.
            OnboardCmd("Chefe@Demo.Example.com", "Chefe no A", TenantRole.Analyst, "outra senha longa e boa"));

        result.Status.Should().Be(TenantUserOnboardingStatus.ExistingIdentityGranted);
        result.IdentityExisted.Should().BeTrue("a UI precisa comunicar que a pessoa já existia");

        var acc = await db.IdentityAccounts.SingleAsync(a => a.Id == accountId);
        acc.PasswordHash.Should().Be(originalHash, "a senha informada NÃO redefine uma credencial existente");
        acc.PlatformRole.Should().Be(PlatformRole.PlatformAdmin, "o papel global é preservado");
        acc.ExternalTenantId.Should().Be("11111111-1111-1111-1111-1111111111aa", "o vínculo Entra é preservado");
        acc.ExternalObjectId.Should().Be("22222222-2222-2222-2222-2222222222bb");

        var membership = await db.Users.SingleAsync();
        membership.IdentityAccountId.Should().Be(accountId, "o acesso vincula a MESMA pessoa (por Id, não e-mail)");
        membership.Role.Should().Be(TenantRole.Analyst);
        (await db.IdentityAccounts.CountAsync()).Should().Be(1, "nenhuma identidade duplicada foi criada");
    }

    [Fact]
    public async Task Onboard_IdentidadeExistenteComAcessoAqui_AtualizaEReativa()
    {
        Guid accountId;
        await using (var seed = NewContext(null))
        {
            var account = new IdentityAccount
            {
                Email = "ana@demo.example.com",
                PasswordHash = new Pbkdf2PasswordHasher().Hash("uma frase longa e boa"),
            };
            seed.IdentityAccounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }
        await using (var seed = NewContext(TenantA))
        {
            seed.Users.Add(new User
            {
                TenantId = TenantA, IdentityAccountId = accountId,
                DisplayName = "Ana", Role = TenantRole.Analyst, IsActive = false,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA, FederationMode.Local).OnboardAsync(
            OnboardCmd("ana@demo.example.com", "Ana Silva", TenantRole.Manager, initialPassword: null));

        result.Status.Should().Be(TenantUserOnboardingStatus.ExistingIdentityAccessUpdated);
        var membership = await db.Users.SingleAsync();
        membership.Role.Should().Be(TenantRole.Manager);
        membership.IsActive.Should().BeTrue("reativado");
        (await db.Users.CountAsync()).Should().Be(1, "reconcede o MESMO membership, não empilha");
    }

    // ---- Fixture ----------------------------------------------------------------

    /// <summary>Ator-padrão (operador externo ≠ alvo) para os onboardings destes testes; as guardas por
    /// ator (auto-rebaixamento / último admin no caminho de identidade existente) têm testes dedicados.</summary>
    private static readonly Guid Operator = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static OnboardTenantUserCommand OnboardCmd(
        string email, string displayName, TenantRole role, string? initialPassword, Guid? actor = null) =>
        new(email, displayName, role, initialPassword, actor ?? Operator);

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IPlatformTenantUserService ServiceFor(
        AegisScoreDbContext db, Guid? tenantId, FederationMode mode)
    {
        var tenant = new SystemTenantContext(tenantId);
        var users = new UserManagementService(db, tenant, NullLogger<UserManagementService>.Instance);
        var federation = Options.Create(new FederationOptions { Mode = mode });
        return new PlatformTenantUserService(
            db, tenant, users, new Pbkdf2PasswordHasher(), federation,
            NullLogger<PlatformTenantUserService>.Instance);
    }
}
