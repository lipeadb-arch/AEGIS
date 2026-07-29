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
/// [AEGIS-AUD-010] Testes do PROVISIONAMENTO GLOBAL DE IDENTIDADE (<see cref="IdentityProvisioningService"/>)
/// — a autoridade de PLATAFORMA, distinta da concessão de acesso a tenant. Provam: cria SÓ a
/// <see cref="IdentityAccount"/> (nunca membership), unicidade global do e-mail, a política de senha por
/// modo (Local/Federated/Hybrid) e que uma conta federated-only nasce SEM credencial (PasswordHash null),
/// jamais com string vazia ou hash fictício. Harness SQLite in-memory — o índice único de e-mail é real.
/// </summary>
public sealed class IdentityProvisioningServiceTests : IDisposable
{
    private const string Senha = "uma frase longa e boa";   // 21 chars, sem regra de composição
    private readonly SqliteConnection _connection;

    public IdentityProvisioningServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Política de senha por modo ---------------------------------------------

    [Fact]
    public async Task Local_SemSenha_EhRecusado()
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Local)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com"));

        result.Status.Should().Be(IdentityProvisioningStatus.PasswordRequired);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse("recusa de política não deixa rastro");
    }

    [Fact]
    public async Task Federated_SemSenha_EhAceito_ContaFederatedOnly()
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Federated)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com"));

        result.Succeeded.Should().BeTrue();
        result.Identity!.HasLocalCredential.Should().BeFalse("federated-only não tem credencial local");

        var acc = await db.IdentityAccounts.SingleAsync();
        acc.PasswordHash.Should().BeNull("ausência de senha é null — nunca string vazia nem hash fictício");
    }

    [Fact]
    public async Task Federated_ComSenha_EhRecusado_SemArmazenarCredencialInutil()
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Federated)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com", Senha));

        result.Status.Should().Be(IdentityProvisioningStatus.PasswordNotAllowed);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse("uma credencial que nunca seria usada não é guardada");
    }

    [Fact]
    public async Task Hybrid_SemSenha_EhAceito()
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Hybrid)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com"));

        result.Succeeded.Should().BeTrue();
        (await db.IdentityAccounts.SingleAsync()).PasswordHash.Should().BeNull();
    }

    [Fact]
    public async Task Hybrid_ComSenha_EhAceito_ComCredencialLocal()
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Hybrid)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com", Senha));

        result.Succeeded.Should().BeTrue();
        result.Identity!.HasLocalCredential.Should().BeTrue();
        (await db.IdentityAccounts.SingleAsync()).PasswordHash.Should().NotBeNull();
    }

    [Fact]
    public async Task Local_ComSenha_UsaPbkdf2_ENaoVazaHash()
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Local)
            .ProvisionAsync(new ProvisionIdentityCommand("  Ana.Silva@Demo.Example.com  ", Senha));

        result.Succeeded.Should().BeTrue();

        var acc = await db.IdentityAccounts.SingleAsync();
        acc.Email.Should().Be("ana.silva@demo.example.com", "e-mail normalizado igual ao login");
        acc.PasswordHash.Should().NotContain(Senha, "a senha em claro nunca é persistida");
        acc.PasswordHash!.Split('.').Should().HaveCount(3, "formato self-describing iterações.salt.hash (PBKDF2)");
        new Pbkdf2PasswordHasher().Verify(Senha, acc.PasswordHash).Should().BeTrue();

        // A projeção de saída NÃO carrega o hash — só o Id/e-mail/flag.
        result.Identity!.GetType().GetProperty("PasswordHash").Should().BeNull("IdentitySummary não expõe hash");
    }

    // ---- Só cria a identidade global (nunca membership) --------------------------

    [Fact]
    public async Task Provisionamento_NaoCriaMembership()
    {
        await using var db = NewContext();
        await ServiceFor(db, FederationMode.Local)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com", Senha));

        (await db.IdentityAccounts.CountAsync()).Should().Be(1);
        (await db.Users.IgnoreQueryFilters().AnyAsync())
            .Should().BeFalse("provisionamento global não concede acesso a nenhum tenant");
    }

    // ---- Unicidade global e corrida ---------------------------------------------

    [Fact]
    public async Task EmailDuplicadoGlobal_EhConflito()
    {
        await using var db = NewContext();
        var svc = ServiceFor(db, FederationMode.Local);
        await svc.ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com", Senha));
        var second = await svc.ProvisionAsync(new ProvisionIdentityCommand("  ANA@Demo.Example.com ", Senha));

        second.Status.Should().Be(IdentityProvisioningStatus.EmailAlreadyInUse);
        (await db.IdentityAccounts.CountAsync()).Should().Be(1, "o e-mail é único GLOBAL após normalizar");
    }

    [Fact]
    public async Task CorridaDeEmailGlobal_EhInvarianteDeBanco()
    {
        await using var db = NewContext();
        await ServiceFor(db, FederationMode.Local)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com", Senha));

        // Insert cru de segunda conta com o MESMO e-mail: é o índice único que barra, não o if do C#.
        db.IdentityAccounts.Add(new IdentityAccount { Email = "ana@demo.example.com", PasswordHash = "x" });
        var act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("o e-mail é único GLOBAL");
    }

    // ---- Validação de e-mail e senha --------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("a@b")]                       // domínio sem ponto
    [InlineData("com espaco@demo.example.com")]
    public async Task EmailMalformado_EhRejeitado(string email)
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Local)
            .ProvisionAsync(new ProvisionIdentityCommand(email, Senha));

        result.Status.Should().Be(IdentityProvisioningStatus.InvalidEmail);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("curta123")]        // 8 < 12
    [InlineData("            ")]     // 12 chars, só espaço
    public async Task SenhaForaDaPolitica_EhRejeitada(string senha)
    {
        await using var db = NewContext();
        var result = await ServiceFor(db, FederationMode.Local)
            .ProvisionAsync(new ProvisionIdentityCommand("ana@demo.example.com", senha));

        result.Status.Should().Be(IdentityProvisioningStatus.WeakPassword);
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse();
    }

    // ---- Fixture ----------------------------------------------------------------

    private AegisScoreDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(null));

    private static IdentityProvisioningService ServiceFor(AegisScoreDbContext db, FederationMode mode) =>
        new(db, new Pbkdf2PasswordHasher(),
            Options.Create(new FederationOptions { Mode = mode }),
            NullLogger<IdentityProvisioningService>.Instance);
}
