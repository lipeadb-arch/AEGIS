using AegisScore.DbMigrator;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.DbMigrator;

/// <summary>
/// [Homologação] Testes focados do bootstrap do PRIMEIRO administrador (<see cref="AdminBootstrapper"/> +
/// <see cref="BootstrapOptions"/>). Provam a semântica de segurança: fail-closed, idempotente para a
/// instalação que ele criou, e restrito ao PRIMEIRO administrador — nunca um segundo PlatformAdmin por
/// bootstrap. Harness SQLite in-memory (banco relacional real: índices únicos e stamping fail-closed do
/// DbContext valem de verdade), o mesmo padrão do <c>IdentityProvisioningServiceTests</c>.
/// </summary>
public sealed class AdminBootstrapperTests : IDisposable
{
    private const string AdminEmail = "admin@demo.example.com";
    private const string StrongPassword = "uma frase longa e boa";   // 21 chars, sem regra de composição
    private const string TenantName = "Cliente Homolog";
    private const string ExpectedSlug = "cliente-homolog";           // derivado do nome

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AegisScoreDbContext> _options;

    public AdminBootstrapperTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AegisScoreDbContext(_options, new SystemTenantContext(null));
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // 1) Desabilitado: o migrator nem chama o bootstrapper (a decisão é no Enabled) — nada é tocado.
    [Fact]
    public void Desabilitado_QuandoEnabledAusente_NaoAtiva()
    {
        var opt = Load(new() { ["Bootstrap:AdminEmail"] = AdminEmail });   // sem Bootstrap:Enabled
        opt.Enabled.Should().BeFalse("sem Enabled=true o migrator pula o bootstrap, sem tocar o banco");
        opt.Error.Should().BeNull();
    }

    // 2) Configuração inválida (senha fraca): recusa antes de qualquer escrita.
    [Fact]
    public async Task ConfiguracaoInvalida_FalhaSemPersistir()
    {
        var opt = Load(new()
        {
            ["Bootstrap:Enabled"] = "true",
            ["Bootstrap:AdminEmail"] = AdminEmail,
            ["Bootstrap:AdminPassword"] = "curta",   // < 12 caracteres
            ["Bootstrap:TenantName"] = TenantName,
        });
        opt.Enabled.Should().BeTrue();
        opt.Error.Should().NotBeNull("senha fraca é recusada na validação de configuração");

        var code = await AdminBootstrapper.RunAsync(_options, opt, NullLogger.Instance);

        code.Should().Be(MigratorExitCode.BootstrapFailure);
        await AssertBancoVazioAsync();
    }

    // 3) Banco vazio: cria a pessoa (PlatformAdmin) + tenant + membership (TenantAdmin ativo).
    [Fact]
    public async Task BancoVazio_CriaPlatformAdmin_Tenant_ETenantAdmin()
    {
        var code = await AdminBootstrapper.RunAsync(_options, Valid(), NullLogger.Instance);
        code.Should().Be(MigratorExitCode.Success);

        await using var db = Read();
        var acc = await db.IdentityAccounts.SingleAsync();
        acc.Email.Should().Be(AdminEmail);
        acc.PlatformRole.Should().Be(PlatformRole.PlatformAdmin);
        acc.PasswordHash.Should().NotBeNullOrEmpty("a senha vira hash PBKDF2 — nunca claro/nulo");
        acc.PasswordHash!.Should().NotContain(StrongPassword);

        var tenant = await db.Tenants.SingleAsync();
        tenant.Slug.Should().Be(ExpectedSlug);
        tenant.Status.Should().Be(TenantStatus.Active);

        var membership = await db.Users.IgnoreQueryFilters().SingleAsync();
        membership.TenantId.Should().Be(tenant.Id);
        membership.IdentityAccountId.Should().Be(acc.Id);
        membership.Role.Should().Be(TenantRole.TenantAdmin);
        membership.IsActive.Should().BeTrue();
    }

    // 4) Segunda execução idêntica: no-op com sucesso (idempotente para a instalação que ele criou).
    [Fact]
    public async Task SegundaExecucaoIdentica_EhNoOp()
    {
        (await AdminBootstrapper.RunAsync(_options, Valid(), NullLogger.Instance)).Should().Be(MigratorExitCode.Success);
        (await AdminBootstrapper.RunAsync(_options, Valid(), NullLogger.Instance)).Should().Be(MigratorExitCode.Success);

        await using var db = Read();
        (await db.IdentityAccounts.CountAsync()).Should().Be(1, "não duplica a identidade");
        (await db.Tenants.CountAsync()).Should().Be(1, "não duplica o tenant");
        (await db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(1, "não duplica o membership");
    }

    // 5) Já existe OUTRA identidade e a conta configurada não existe: não é mais o primeiro admin → recusa.
    [Fact]
    public async Task OutraIdentidadePreexistente_ImpedeNovoBootstrap()
    {
        await using (var seed = Read())
        {
            seed.IdentityAccounts.Add(new IdentityAccount
            {
                Email = "outra.pessoa@demo.example.com",
                PasswordHash = "x",
                PlatformRole = PlatformRole.None,
            });
            await seed.SaveChangesAsync();
        }

        var code = await AdminBootstrapper.RunAsync(_options, Valid(), NullLogger.Instance);
        code.Should().Be(MigratorExitCode.BootstrapFailure, "com outra identidade já não é o PRIMEIRO administrador");

        await using var db = Read();
        (await db.IdentityAccounts.CountAsync()).Should().Be(1, "não cria a conta configurada");
        (await db.Tenants.AnyAsync()).Should().BeFalse("não cria tenant");
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse("não cria membership");
    }

    // 6) Conta configurada existe em estado PARCIAL (sem PlatformAdmin, sem membership): recusa sem promover.
    [Fact]
    public async Task ContaConfiguradaEmEstadoParcial_FalhaEPermaneceIntacta()
    {
        Guid preId;
        await using (var seed = Read())
        {
            var seeded = new IdentityAccount { Email = AdminEmail, PasswordHash = "x", PlatformRole = PlatformRole.None };
            seed.IdentityAccounts.Add(seeded);
            await seed.SaveChangesAsync();
            preId = seeded.Id;
        }

        var code = await AdminBootstrapper.RunAsync(_options, Valid(), NullLogger.Instance);
        code.Should().Be(MigratorExitCode.BootstrapFailure);

        await using var db = Read();
        var acc = await db.IdentityAccounts.SingleAsync();
        acc.Id.Should().Be(preId);
        acc.PlatformRole.Should().Be(PlatformRole.None, "nunca promove silenciosamente a conta preexistente");
        acc.PasswordHash.Should().Be("x", "a credencial preexistente não é tocada");
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        (await db.Tenants.AnyAsync()).Should().BeFalse();
    }

    // ---- Helpers ----------------------------------------------------------------

    private AegisScoreDbContext Read() => new(_options, new SystemTenantContext(null));

    private async Task AssertBancoVazioAsync()
    {
        await using var db = Read();
        (await db.IdentityAccounts.AnyAsync()).Should().BeFalse();
        (await db.Tenants.AnyAsync()).Should().BeFalse();
        (await db.Users.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    private static BootstrapOptions Valid() => Load(new()
    {
        ["Bootstrap:Enabled"] = "true",
        ["Bootstrap:AdminEmail"] = AdminEmail,
        ["Bootstrap:AdminPassword"] = StrongPassword,
        ["Bootstrap:TenantName"] = TenantName,
    });

    private static BootstrapOptions Load(Dictionary<string, string?> values) =>
        BootstrapOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
