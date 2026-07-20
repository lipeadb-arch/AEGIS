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
/// Testes do <see cref="UserManagementService"/>. Harness dos demais (SQLite in-memory), então o índice
/// único <c>(TenantId, Email)</c>, o Global Query Filter e o stamping fail-closed são exercitados de
/// verdade — que é onde mora a garantia de isolamento deste serviço.
/// </summary>
public sealed class UserManagementServiceTests : IDisposable
{
    private const string Senha = "uma frase longa e boa";   // 21 chars, sem regra de composição
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

    // ---- Criação ---------------------------------------------------------------

    [Fact]
    public async Task CreateUserAsync_NormalizaEmail_EDerivaHashPbkdf2()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateUserAsync(
            new CreateUserCommand("  Ana.Silva@Demo.Aegis  ", "  Ana Silva  ", Senha, UserRole.Analyst));

        result.Succeeded.Should().BeTrue();
        result.Status.Should().Be(UserProvisioningStatus.Created);

        var saved = await db.Users.SingleAsync();
        saved.DisplayName.Should().Be("Ana Silva");
        saved.TenantId.Should().Be(TenantA, "carimbado pelo SaveChanges, não pelo chamador");
        saved.IsActive.Should().BeTrue();

        // A credencial mora na PESSOA, não no vínculo.
        var account = await db.IdentityAccounts.SingleAsync();
        saved.IdentityAccountId.Should().Be(account.Id);
        account.Email.Should().Be("ana.silva@demo.aegis", "o login normaliza igual — senão o AuthService não acha");
        account.PasswordHash.Should().NotContain(Senha, "a senha em claro nunca é persistida");
        account.PasswordHash.Split('.').Should().HaveCount(3, "formato self-describing iterações.salt.hash");
        new Pbkdf2PasswordHasher().Verify(Senha, account.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task CreateUserAsync_EmailDuplicadoNoMesmoTenant_EhConflito()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);

        await svc.CreateUserAsync(Cmd("ana@demo.aegis"));
        var second = await svc.CreateUserAsync(Cmd("  ANA@Demo.Aegis "));   // mesma identidade após normalizar

        second.Status.Should().Be(UserProvisioningStatus.EmailAlreadyInUse);
        (await db.Users.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("a@b")]                    // domínio sem ponto
    [InlineData("com espaco@demo.aegis")]
    [InlineData("@demo.aegis")]
    public async Task CreateUserAsync_EmailMalformado_EhRejeitado(string email)
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateUserAsync(Cmd(email));

        result.Status.Should().Be(UserProvisioningStatus.InvalidEmail);
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("curta123")]                 // 8 < 12
    [InlineData("            ")]             // 12 chars, só espaço
    public async Task CreateUserAsync_SenhaForaDaPolitica_EhRejeitada(string senha)
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateUserAsync(
            new CreateUserCommand("ana@demo.aegis", "Ana", senha, UserRole.Analyst));

        result.Status.Should().Be(UserProvisioningStatus.WeakPassword);
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CreateUserAsync_SenhaLongaSemComposicao_EhAceita()
    {
        // NIST SP 800-63B: comprimento manda, regra de composição não. Uma frase sem maiúscula,
        // dígito ou símbolo DEVE passar.
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateUserAsync(
            new CreateUserCommand("ana@demo.aegis", "Ana", "cavalo bateria grampo correto", UserRole.Analyst));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task CreateUserAsync_NomeDeExibicaoVazio_EhRejeitado()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateUserAsync(
            new CreateUserCommand("ana@demo.aegis", "   ", Senha, UserRole.Analyst));

        result.Status.Should().Be(UserProvisioningStatus.InvalidDisplayName);
    }

    // ---- Escalonamento de privilégio -------------------------------------------

    [Fact]
    public async Task CreateUserAsync_PlatformAdmin_NaoEhAtribuivel()
    {
        // O vetor: um TenantAdmin emitindo PlatformAdmin viraria admin da PLATAFORMA (cria tenants, §20).
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).CreateUserAsync(
            new CreateUserCommand("root@demo.aegis", "Root", Senha, UserRole.PlatformAdmin));

        result.Status.Should().Be(UserProvisioningStatus.RoleNotAssignable);
        (await db.Users.AnyAsync()).Should().BeFalse("nenhum PlatformAdmin nasce por esta superfície");
    }

    [Fact]
    public async Task AssignUserToTenantAsync_NaoPromoveParaPlatformAdmin()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        await svc.CreateUserAsync(Cmd("ana@demo.aegis"));

        // O caminho de ATUALIZAÇÃO não pode ser a porta dos fundos do escalonamento.
        var result = await svc.AssignUserToTenantAsync(
            new AssignUserToTenantCommand(TenantA, "ana@demo.aegis", UserRole.PlatformAdmin));

        result.Status.Should().Be(UserProvisioningStatus.RoleNotAssignable);
        (await db.Users.SingleAsync()).Role.Should().Be(UserRole.Analyst, "o papel vigente fica intacto");
    }

    // ---- Concessão de acesso ---------------------------------------------------

    [Fact]
    public async Task AssignUserToTenantAsync_IdentidadeAusente_CriaComSenhaInicial()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).AssignUserToTenantAsync(
            new AssignUserToTenantCommand(TenantA, "ana.silva@demo.aegis", UserRole.Manager, Senha));

        result.Status.Should().Be(UserProvisioningStatus.Created);
        var saved = await db.Users.SingleAsync();
        saved.Role.Should().Be(UserRole.Manager);
        saved.DisplayName.Should().Be("ana.silva", "provisório derivado do local do e-mail");
    }

    [Fact]
    public async Task AssignUserToTenantAsync_IdentidadeAusenteSemSenha_ExigeSenha()
    {
        await using var db = NewContext(TenantA);
        var result = await ServiceFor(db, TenantA).AssignUserToTenantAsync(
            new AssignUserToTenantCommand(TenantA, "ana@demo.aegis", UserRole.Analyst));

        result.Status.Should().Be(UserProvisioningStatus.PasswordRequired,
            "não há credencial a herdar de outro tenant — a leitura cross-tenant não existe");
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AssignUserToTenantAsync_IdentidadeExistente_AtualizaPapelSemTocarASenha()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        await svc.CreateUserAsync(Cmd("ana@demo.aegis"));
        var hashOriginal = (await db.IdentityAccounts.SingleAsync()).PasswordHash;

        var result = await svc.AssignUserToTenantAsync(
            new AssignUserToTenantCommand(TenantA, "ana@demo.aegis", UserRole.TenantAdmin, "outra senha longa"));

        result.Status.Should().Be(UserProvisioningStatus.AccessUpdated);
        (await db.Users.SingleAsync()).Role.Should().Be(UserRole.TenantAdmin);
        (await db.IdentityAccounts.SingleAsync()).PasswordHash
            .Should().Be(hashOriginal, "conceder permissão NÃO é resetar credencial");
    }

    [Fact]
    public async Task AssignUserToTenantAsync_IdentidadeInativa_EhReativada()
    {
        await using var db = NewContext(TenantA);
        var svc = ServiceFor(db, TenantA);
        await svc.CreateUserAsync(Cmd("ana@demo.aegis"));

        var user = await db.Users.SingleAsync();
        user.IsActive = false;
        await db.SaveChangesAsync();

        var result = await svc.AssignUserToTenantAsync(
            new AssignUserToTenantCommand(TenantA, "ana@demo.aegis", UserRole.Analyst));

        result.Status.Should().Be(UserProvisioningStatus.AccessUpdated);
        (await db.Users.SingleAsync()).IsActive.Should().BeTrue();
    }

    // ---- Isolamento: o coração deste serviço -----------------------------------

    [Fact]
    public async Task MesmoEmailEmTenantsDistintos_EhUmaPessoaComDoisAcessos()
    {
        const string email = "ana@demo.aegis";

        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA).CreateUserAsync(
                new CreateUserCommand(email, "Ana (A)", Senha, UserRole.Analyst));

        await using (var db = NewContext(TenantB))
        {
            var result = await ServiceFor(db, TenantB).CreateUserAsync(
                new CreateUserCommand(email, "Ana (B)", "senha completamente outra", UserRole.TenantAdmin));
            result.Succeeded.Should().BeTrue("a mesma pessoa pode ter acesso a vários clientes");
        }

        await using var assert = NewContext(null);

        // UMA pessoa, UMA credencial — a premissa que sustenta o seletor de ambientes.
        var contas = await assert.IdentityAccounts.Where(a => a.Email == email).ToListAsync();
        contas.Should().HaveCount(1, "o e-mail é único GLOBAL desde a normalização");

        // DOIS acessos, com papéis próprios por cliente.
        var acessos = await assert.Users.IgnoreQueryFilters()
            .Where(u => u.IdentityAccountId == contas[0].Id).ToListAsync();
        acessos.Should().HaveCount(2);
        acessos.Select(u => u.TenantId).Should().BeEquivalentTo(new[] { TenantA, TenantB });
        acessos.Select(u => u.Role).Should().BeEquivalentTo(new[] { UserRole.Analyst, UserRole.TenantAdmin });
    }

    [Fact]
    public async Task CreateUserAsync_NaoPermiteTrocarASenhaDeContaExistente()
    {
        // ⚠️ O VETOR CENTRAL que a normalização fecha. Mallory administra o tenant B e tenta "criar"
        // a pessoa que já existe no tenant A, escolhendo uma senha que ela conhece. Se a senha vingasse,
        // Mallory faria login como a vítima e usaria o seletor para entrar no ambiente A.
        await using (var db = NewContext(TenantA))
            await ServiceFor(db, TenantA).CreateUserAsync(
                new CreateUserCommand("ana@demo.aegis", "Ana", Senha, UserRole.Analyst));

        await using (var db = NewContext(TenantB))
            await ServiceFor(db, TenantB).CreateUserAsync(
                new CreateUserCommand("ana@demo.aegis", "Ana", "senha da mallory 123", UserRole.TenantAdmin));

        await using var assert = NewContext(null);
        var conta = await assert.IdentityAccounts.SingleAsync(a => a.Email == "ana@demo.aegis");

        var hasher = new Pbkdf2PasswordHasher();
        hasher.Verify(Senha, conta.PasswordHash).Should().BeTrue("a credencial da vítima permanece");
        hasher.Verify("senha da mallory 123", conta.PasswordHash).Should()
            .BeFalse("um admin de outro cliente NÃO redefine a senha de ninguém");
    }

    [Fact]
    public async Task AssignUserToTenantAsync_TenantDivergenteDoContexto_EhRecusado()
    {
        await using var db = NewContext(TenantA);

        // Um admin de A tentando conceder acesso em B: a asserção barra antes de qualquer escrita.
        var act = () => ServiceFor(db, TenantA).AssignUserToTenantAsync(
            new AssignUserToTenantCommand(TenantB, "ana@demo.aegis", UserRole.Analyst, Senha));

        await act.Should().ThrowAsync<TenantSecurityException>();
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CreateUserAsync_SemTenantNoContexto_FalhaFechado()
    {
        await using var db = NewContext(null);
        var act = () => ServiceFor(db, null).CreateUserAsync(Cmd("ana@demo.aegis"));

        await act.Should().ThrowAsync<TenantSecurityException>();
    }

    [Fact]
    public async Task IndiceUnico_RejeitaSegundoAcessoDaMesmaPessoaAoMesmoTenant()
    {
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA).CreateUserAsync(Cmd("ana@demo.aegis"));
        var conta = await db.IdentityAccounts.SingleAsync();

        // Insert cru, contornando o serviço: é o índice (TenantId, IdentityAccountId) que precisa
        // barrar, não o if do C#.
        db.Users.Add(new User
        {
            TenantId = TenantA, IdentityAccountId = conta.Id,
            DisplayName = "clone", Role = UserRole.Analyst,
        });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task IndiceUnico_RejeitaSegundaContaComMesmoEmailGlobal()
    {
        await using var db = NewContext(TenantA);
        await ServiceFor(db, TenantA).CreateUserAsync(Cmd("ana@demo.aegis"));

        db.IdentityAccounts.Add(new IdentityAccount { Email = "ana@demo.aegis", PasswordHash = "x" });

        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("o e-mail é único GLOBAL");
    }

    // ---- Fixture ----------------------------------------------------------------

    private static CreateUserCommand Cmd(string email) =>
        new(email, "Ana Silva", Senha, UserRole.Analyst);

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IUserManagementService ServiceFor(AegisScoreDbContext db, Guid? tenantId) =>
        new UserManagementService(
            db, new SystemTenantContext(tenantId), new Pbkdf2PasswordHasher(),
            NullLogger<UserManagementService>.Instance);
}
