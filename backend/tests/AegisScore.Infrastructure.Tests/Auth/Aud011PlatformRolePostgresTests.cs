using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AegisScore.Api.Auth;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe (infra AEGIS_TEST_PG já existente)
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-011] O que o SQLite não prova: (1) o BACKFILL do papel global legado (Users.Role=3 →
/// IdentityAccount.PlatformRole=1 + membership vira TenantAdmin) num PostgreSQL real, preservando dados;
/// (2) as CHECK constraints dos dois eixos rejeitando valores fora da faixa; (3) o rollback FAIL-CLOSED
/// que ABORTA enquanto houver papel global atribuído. Seeding do estado legado é por SQL cru — o modelo
/// atual (TenantRole, sem o valor 3) nem consegue expressá-lo. Gated por <c>AEGIS_TEST_PG</c>.
/// </summary>
public sealed class Aud011PlatformRolePostgresTests
{
    private const string PreAud011 = "20260729120030_Aud010NullablePasswordHash";
    private const string Aud011 = "20260729211634_Aud011SeparatePlatformTenantRoles";

    private readonly ITestOutputHelper _output;
    public Aud011PlatformRolePostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Migration_BackfillsLegacyPlatformAdmin_PreservesData_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        // Schema ANTERIOR (AUD-010): sem PlatformRole, sem constraints — permite semear o valor legado 3.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud011);

        var tenant = Guid.NewGuid();
        var account = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using (var conn = await OpenAsync(opt))
        {
            await ExecAsync(conn, $"INSERT INTO \"Tenants\" (\"Id\",\"Name\",\"Slug\",\"Status\",\"CreatedAt\") VALUES ('{tenant}','Alfa','alfa-{tenant:N}',1, now());");
            await ExecAsync(conn, $"INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"CreatedAt\") VALUES ('{account}','ana@demo.example.com','210000.aaa.bbb', now());");
            // Membership LEGADO com Role=3 (o antigo PlatformAdmin embutido no membership).
            await ExecAsync(conn, $"INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"IdentityAccountId\",\"DisplayName\",\"Role\",\"IsActive\",\"CreatedAt\") VALUES ('{user}','{tenant}','{account}','Ana',3,true, now());");
            await ExecAsync(conn, $"INSERT INTO \"UserRefreshTokens\" (\"Id\",\"TenantId\",\"UserId\",\"TokenHash\",\"ExpiresAt\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','{tenant}','{user}','{new string('c', 64)}', now() + interval '7 days', now());");
        }

        // Sobe o AUD-011: backfill + constraints.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud011);

        await using var check = await OpenAsync(opt);
        (await ScalarAsync(check, $"SELECT \"PlatformRole\" FROM \"IdentityAccounts\" WHERE \"Id\"='{account}'"))
            .Should().Be(1, "o papel global legado SUBIU para a identidade (PlatformRole=PlatformAdmin)");
        (await ScalarAsync(check, $"SELECT \"Role\" FROM \"Users\" WHERE \"Id\"='{user}'"))
            .Should().Be(2, "o membership legado virou TenantAdmin, preservando o acesso operacional");
        (await ScalarAsync(check, "SELECT count(*) FROM \"IdentityAccounts\"")).Should().Be(1, "identidades preservadas");
        (await ScalarAsync(check, "SELECT count(*) FROM \"Users\"")).Should().Be(1, "memberships preservados");
        (await ScalarAsync(check, "SELECT count(*) FROM \"UserRefreshTokens\"")).Should().Be(1, "sessões preservadas");
        (await ScalarAsync(check, "SELECT count(*) FROM \"Users\" WHERE \"Role\"=3")).Should().Be(0, "nenhum Role=3 remanescente");
    }

    [Fact]
    public async Task Constraints_RejectOutOfRangeRoleAndPlatformRole_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud011);

        var tenant = Guid.NewGuid();
        var account = Guid.NewGuid();
        await using var conn = await OpenAsync(opt);
        await ExecAsync(conn, $"INSERT INTO \"Tenants\" (\"Id\",\"Name\",\"Slug\",\"Status\",\"CreatedAt\") VALUES ('{tenant}','Alfa','alfa-{tenant:N}',1, now());");
        await ExecAsync(conn, $"INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"PlatformRole\",\"CreatedAt\") VALUES ('{account}','ana@demo.example.com','x',0, now());");

        // Users.Role=3 (o antigo PlatformAdmin) e Role=5 (desconhecido) são rejeitados pela constraint.
        var role3 = async () => await ExecAsync(conn, $"INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"IdentityAccountId\",\"DisplayName\",\"Role\",\"IsActive\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','{tenant}','{account}','X',3,true, now());");
        (await role3.Should().ThrowAsync<PostgresException>("CK_Users_Role só admite 0/1/2"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        var role5 = async () => await ExecAsync(conn, $"INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"IdentityAccountId\",\"DisplayName\",\"Role\",\"IsActive\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','{tenant}','{account}','X',5,true, now());");
        await role5.Should().ThrowAsync<PostgresException>();

        // PlatformRole=2 (fora de 0/1) é rejeitado pela constraint do eixo global.
        var pr2 = async () => await ExecAsync(conn, $"INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"PlatformRole\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','dois@demo.example.com','x',2, now());");
        (await pr2.Should().ThrowAsync<PostgresException>("CK_IdentityAccounts_PlatformRole só admite 0/1"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Rollback_AbortsWhileGlobalRoleAssigned_ThenSucceedsAfterDowngrade_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud011);

        // Uma identidade com autoridade global (PlatformRole=PlatformAdmin).
        var account = Guid.NewGuid();
        await using (var conn = await OpenAsync(opt))
            await ExecAsync(conn, $"INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"PlatformRole\",\"CreatedAt\") VALUES ('{account}','ana@demo.example.com','x',1, now());");

        // O Down ABORTA — não há como representar o papel global no modelo antigo sem espalhar/descartar.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var act = async () => await Migrator(db).MigrateAsync(PreAud011);
            (await act.Should().ThrowAsync<Exception>("rollback bloqueado enquanto houver papel global"))
                .Which.Message.Should().Contain("PlatformRole");
        }

        // Nada foi corrompido: a coluna e o valor global seguem lá.
        await using (var conn = await OpenAsync(opt))
        {
            (await ScalarAsync(conn, $"SELECT \"PlatformRole\" FROM \"IdentityAccounts\" WHERE \"Id\"='{account}'")).Should().Be(1);
            // Rebaixa deliberadamente o papel global e então o rollback prossegue com segurança.
            await ExecAsync(conn, $"UPDATE \"IdentityAccounts\" SET \"PlatformRole\"=0 WHERE \"Id\"='{account}';");
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud011);

        await using var final = await OpenAsync(opt);
        (await ScalarAsync(final, "SELECT count(*) FROM \"IdentityAccounts\"")).Should().Be(1, "rollback não apaga identidades");
        (await ScalarAsync(final,
            "SELECT count(*) FROM information_schema.columns WHERE table_name='IdentityAccounts' AND column_name='PlatformRole'"))
            .Should().Be(0, "após rebaixar, o Down remove a coluna do eixo global");
    }

    // ---- Regra corrigida do backfill: só promove privilégio USÁVEL (Role=3 ATIVO em tenant não suspenso) ----

    [Fact]
    public async Task Backfill_ActiveRole3_OnboardingTenant_Promotes_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var (account, _) = await SeedAndMigrateLegacyRole3Async(pg.DbOptions(), tenantStatus: 0, isActive: true);

        await using var conn = await OpenAsync(pg.DbOptions());
        (await ScalarAsync(conn, $"SELECT \"PlatformRole\" FROM \"IdentityAccounts\" WHERE \"Id\"='{account}'"))
            .Should().Be(1, "Role=3 ATIVO em tenant Onboarding (não suspenso) promove a autoridade global");
        (await ScalarAsync(conn, "SELECT count(*) FROM \"Users\" WHERE \"Role\"=3")).Should().Be(0, "Role=3 normalizado");
    }

    [Fact]
    public async Task Backfill_InactiveRole3_ActiveTenant_DoesNotPromote_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var (account, _) = await SeedAndMigrateLegacyRole3Async(pg.DbOptions(), tenantStatus: 1, isActive: false);

        await using var conn = await OpenAsync(pg.DbOptions());
        (await ScalarAsync(conn, $"SELECT \"PlatformRole\" FROM \"IdentityAccounts\" WHERE \"Id\"='{account}'"))
            .Should().Be(0, "Role=3 INATIVO não reativa autoridade global");
        (await ScalarAsync(conn, $"SELECT \"Role\" FROM \"Users\" WHERE \"IdentityAccountId\"='{account}'"))
            .Should().Be(2, "mesmo sem promover, o membership legado é normalizado para TenantAdmin");
    }

    [Fact]
    public async Task Backfill_ActiveRole3_SuspendedTenant_DoesNotPromote_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var (account, _) = await SeedAndMigrateLegacyRole3Async(pg.DbOptions(), tenantStatus: 2, isActive: true);

        await using var conn = await OpenAsync(pg.DbOptions());
        (await ScalarAsync(conn, $"SELECT \"PlatformRole\" FROM \"IdentityAccounts\" WHERE \"Id\"='{account}'"))
            .Should().Be(0, "Role=3 em tenant SUSPENSO não vira autoridade global ativa");
        (await ScalarAsync(conn, $"SELECT \"Role\" FROM \"Users\" WHERE \"IdentityAccountId\"='{account}'"))
            .Should().Be(2, "o membership legado é normalizado para TenantAdmin de qualquer forma");
    }

    [Fact]
    public async Task Backfill_InactiveLegacyPlusActiveAnalyst_DoesNotPromote_NoClaim_PolicyDenied_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        const string senha = "uma frase longa e boa de verdade";
        var pwHash = new Pbkdf2PasswordHasher().Hash(senha);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud011);

        // Uma pessoa com: (a) Role=3 INATIVO num tenant Active e (b) Analyst ATIVO noutro tenant Active.
        var tenantLegacy = Guid.NewGuid();
        var tenantActive = Guid.NewGuid();
        var account = Guid.NewGuid();
        await using (var conn = await OpenAsync(opt))
        {
            await ExecAsync(conn, $"INSERT INTO \"Tenants\" (\"Id\",\"Name\",\"Slug\",\"Status\",\"CreatedAt\") VALUES ('{tenantLegacy}','L','l-{tenantLegacy:N}',1, now());");
            await ExecAsync(conn, $"INSERT INTO \"Tenants\" (\"Id\",\"Name\",\"Slug\",\"Status\",\"CreatedAt\") VALUES ('{tenantActive}','A','a-{tenantActive:N}',1, now());");
            await ExecAsync(conn, $"INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"CreatedAt\") VALUES ('{account}','ana@demo.example.com','{pwHash}', now());");
            await ExecAsync(conn, $"INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"IdentityAccountId\",\"DisplayName\",\"Role\",\"IsActive\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','{tenantLegacy}','{account}','Ana',3,false, now());");
            await ExecAsync(conn, $"INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"IdentityAccountId\",\"DisplayName\",\"Role\",\"IsActive\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','{tenantActive}','{account}','Ana',0,true, now());");
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud011);

        // (1) NÃO promove: o único Role=3 estava inativo; o membership ativo é Analyst (não Role=3).
        await using (var conn = await OpenAsync(opt))
            (await ScalarAsync(conn, $"SELECT \"PlatformRole\" FROM \"IdentityAccounts\" WHERE \"Id\"='{account}'"))
                .Should().Be(0, "um privilégio legado inativo não é reativado por causa de outro membership ativo");

        // (2) O JWT emitido no login NÃO carrega platform_role (login cai no membership Analyst ativo).
        string accessToken;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var pair = await Auth(db, opt).LoginAsync("ana@demo.example.com", senha, default);
            pair.Should().NotBeNull();
            accessToken = pair!.AccessToken;
        }
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        jwt.Claims.Any(c => c.Type == "platform_role").Should().BeFalse("sem autoridade global, sem claim global");

        // (3) A policy REAL de plataforma nega o principal desse token.
        (await AuthorizePlatformAsync(jwt)).Succeeded
            .Should().BeFalse("sem platform_role, a policy PlatformAdmin é negada");
    }

    // ---- helpers ----

    /// <summary>Migra a PreAud011, semeia tenant (status dado) + conta + UM membership Role=3 (isActive dado)
    /// por SQL cru, e sobe o AUD-011. Devolve (accountId, tenantId).</summary>
    private static async Task<(Guid account, Guid tenant)> SeedAndMigrateLegacyRole3Async(
        DbContextOptions<AegisScoreDbContext> opt, int tenantStatus, bool isActive)
    {
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud011);

        var tenant = Guid.NewGuid();
        var account = Guid.NewGuid();
        await using (var conn = await OpenAsync(opt))
        {
            await ExecAsync(conn, $"INSERT INTO \"Tenants\" (\"Id\",\"Name\",\"Slug\",\"Status\",\"CreatedAt\") VALUES ('{tenant}','T','t-{tenant:N}',{tenantStatus}, now());");
            await ExecAsync(conn, $"INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"CreatedAt\") VALUES ('{account}','ana@demo.example.com','x', now());");
            await ExecAsync(conn, $"INSERT INTO \"Users\" (\"Id\",\"TenantId\",\"IdentityAccountId\",\"DisplayName\",\"Role\",\"IsActive\",\"CreatedAt\") VALUES ('{Guid.NewGuid()}','{tenant}','{account}','Ana',3,{(isActive ? "true" : "false")}, now());");
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud011);

        return (account, tenant);
    }

    private static AuthService Auth(AegisScoreDbContext db, DbContextOptions<AegisScoreDbContext> opt) => new(
        db, opt,
        new JwtTokenService(Options.Create(new JwtOptions
        {
            SigningKey = "aegis-test-signing-key-com-mais-de-32-bytes", Issuer = "aegis-score", Audience = "aegis-score",
        })),
        new Pbkdf2PasswordHasher(), new Sha256RefreshTokenHasher(), Options.Create(new FederationOptions()),
        NullLogger<AuthService>.Instance);

    /// <summary>Avalia a policy REAL de plataforma contra o principal do JWT (roleType="role", como o Bearer).</summary>
    private static async Task<AuthorizationResult> AuthorizePlatformAsync(JwtSecurityToken jwt)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "Test", nameType: "name", roleType: "role"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(PlatformAuthorization.AddPlatformPolicy);
        await using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();
        return await authz.AuthorizeAsync(principal, PlatformAuthorization.PolicyName);
    }

    private static IMigrator Migrator(AegisScoreDbContext db) =>
        db.GetInfrastructure().GetRequiredService<IMigrator>();

    private static async Task<NpgsqlConnection> OpenAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        var conn = new NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(v);
    }
}
