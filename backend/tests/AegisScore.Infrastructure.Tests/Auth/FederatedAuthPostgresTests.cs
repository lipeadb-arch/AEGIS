using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe (infra AEGIS_TEST_PG já existente)
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-007] O que o SQLite não prova: (1) a UNICIDADE do vínculo (tid,oid) como invariante de banco
/// sob o índice único parcial real; (2) que a migration é ADITIVA e o rollback NÃO remove contas,
/// memberships ou sessões — só as colunas do vínculo. Gated por <c>AEGIS_TEST_PG</c>; cada teste cria e
/// destrói seu próprio database (o <c>aegis_dev</c> nunca é tocado).
/// </summary>
public sealed class FederatedAuthPostgresTests
{
    private const string PreAud007 = "20260727204000_Aud009HashRefreshTokens";
    private const string Aud007 = "20260728212552_Aud007FederatedIdentityBinding";
    private const string Tid = "11111111-1111-1111-1111-111111111111";

    private readonly ITestOutputHelper _output;
    public FederatedAuthPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task BindingUniqueness_TwoAccountsSameTidOid_SecondRejected_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.EnsureCreatedAsync();

        const string oid = "aaaaaaaa-0000-0000-0000-000000000001";

        // Conta 1 vincula-se a (tid, oid).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.IdentityAccounts.Add(new IdentityAccount
            {
                Email = "um@demo.example.com", PasswordHash = "x",
                ExternalTenantId = Tid, ExternalObjectId = oid,
            });
            await db.SaveChangesAsync();
        }

        // Conta 2 tenta o MESMO (tid, oid) → o índice único parcial rejeita no PostgreSQL real.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.IdentityAccounts.Add(new IdentityAccount
            {
                Email = "dois@demo.example.com", PasswordHash = "x",
                ExternalTenantId = Tid, ExternalObjectId = oid,
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>(
                "a mesma identidade externa não pode ser ligada a duas contas");
        }

        // Duas contas SEM vínculo (oid NULL) convivem — o índice é parcial.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.IdentityAccounts.Add(new IdentityAccount { Email = "tres@demo.example.com", PasswordHash = "x" });
            db.IdentityAccounts.Add(new IdentityAccount { Email = "quatro@demo.example.com", PasswordHash = "x" });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync("linhas não vinculadas (oid NULL) não colidem");
        }
    }

    [Fact]
    public async Task Migration_Additive_And_Rollback_PreservesAccountsMembershipsSessions_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        // Schema anterior ao AUD-007 (IdentityAccounts sem as colunas de vínculo).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud007);

        // Sobe o AUD-007 (aditivo) e semeia conta + membership + sessão (refresh) no schema completo.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud007);

        var tenant = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Alfa", Slug = "alfa-" + tenant.ToString("N")[..8], Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        // [AEGIS-AUD-011] IdentityAccount por SQL cru: este teste roda no schema AUD-007, anterior ao eixo
        // global PlatformRole do modelo EF atual — um INSERT via EF referenciaria a coluna ainda inexistente.
        await using (var seedConn = await OpenAsync(opt))
        {
            await using var cmd = seedConn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"ExternalTenantId\",\"ExternalObjectId\",\"CreatedAt\") " +
                $"VALUES ('{accountId}','ana@demo.example.com','x','{Tid}','aaaaaaaa-0000-0000-0000-000000000001', now())";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var user = new User { TenantId = tenant, IdentityAccountId = accountId, DisplayName = "Ana", Role = TenantRole.Analyst };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.UserRefreshTokens.Add(new UserRefreshToken
            {
                TenantId = tenant, UserId = user.Id,
                TokenHash = new string('a', 64), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await db.SaveChangesAsync();
        }

        // Rollback do AUD-007 → só as colunas de vínculo somem; conta, membership e sessão permanecem.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud007);

        await using var conn = await OpenAsync(opt);
        (await ScalarAsync(conn, "SELECT count(*) FROM \"IdentityAccounts\"")).Should().Be(1, "rollback não apaga contas");
        (await ScalarAsync(conn, "SELECT count(*) FROM \"Users\"")).Should().Be(1, "rollback não apaga memberships");
        (await ScalarAsync(conn, "SELECT count(*) FROM \"UserRefreshTokens\"")).Should().Be(1, "rollback não apaga sessões");
        // As colunas de vínculo foram removidas (o Down é aditivo-reverso).
        (await ScalarAsync(conn,
            "SELECT count(*) FROM information_schema.columns WHERE table_name='IdentityAccounts' " +
            "AND column_name IN ('ExternalTenantId','ExternalObjectId')"))
            .Should().Be(0, "o Down remove apenas as colunas de vínculo");
    }

    // ---- helpers ----

    private static IMigrator Migrator(AegisScoreDbContext db) =>
        db.GetInfrastructure().GetRequiredService<IMigrator>();

    private static async Task<NpgsqlConnection> OpenAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        var conn = new NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
