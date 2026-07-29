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
/// [AEGIS-AUD-010] O que o SQLite não prova: (1) que a migration torna <c>PasswordHash</c> NULLABLE no
/// PostgreSQL real (o SQLite não impõe NOT NULL do mesmo modo) e que os índices relevantes seguem intactos;
/// (2) que ela é ADITIVA e SEGURA — preserva contas, hashes, vínculos Entra, memberships e refresh sessions,
/// e o <c>Down</c> ABORTA com mensagem explícita quando há conta federated-only (sem inventar hash nem
/// apagar contas). Gated por <c>AEGIS_TEST_PG</c>; cada teste cria e destrói seu próprio database.
/// </summary>
public sealed class Aud010PasswordHashPostgresTests
{
    private const string PreAud010 = "20260728212552_Aud007FederatedIdentityBinding";
    private const string Aud010 = "20260729120030_Aud010NullablePasswordHash";
    // [AEGIS-AUD-011] Head atual do modelo EF: as operações via DbContext (que hoje citam PlatformRole)
    // exigem um schema que tenha a coluna. O comportamento do AUD-010 (PasswordHash nullable) persiste aqui.
    private const string Head = "20260729211634_Aud011SeparatePlatformTenantRoles";
    private const string Tid = "11111111-1111-1111-1111-111111111111";

    private readonly ITestOutputHelper _output;
    public Aud010PasswordHashPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Migration_MakesPasswordHashNullable_PreservesData_AndKeepsIndexes_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        // Schema ANTERIOR (AUD-007): PasswordHash é NOT NULL.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud010);

        (await ColumnIsNullableAsync(opt)).Should().BeFalse("antes do AUD-010 a coluna é NOT NULL");

        // Semeia no schema antigo (AUD-007): conta local (com hash) + vínculo Entra + membership + sessão.
        // A IdentityAccount vai por SQL cru — o schema é anterior ao eixo global PlatformRole do modelo EF.
        var tenant = Guid.NewGuid();
        var localAccountId = Guid.NewGuid();
        string localHash = "210000." + new string('a', 24) + "." + new string('b', 44);
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Alfa", Slug = "alfa-" + tenant.ToString("N")[..8], Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var seedConn = await OpenAsync(opt))
        {
            await using var cmd = seedConn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"ExternalTenantId\",\"ExternalObjectId\",\"CreatedAt\") " +
                $"VALUES ('{localAccountId}','ana@demo.example.com','{localHash}','{Tid}','aaaaaaaa-0000-0000-0000-000000000001', now())";
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var user = new User { TenantId = tenant, IdentityAccountId = localAccountId, DisplayName = "Ana", Role = TenantRole.Analyst };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.UserRefreshTokens.Add(new UserRefreshToken
            {
                TenantId = tenant, UserId = user.Id,
                TokenHash = new string('c', 64), ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            });
            await db.SaveChangesAsync();
        }

        // Sobe até o head atual (o AUD-010, aditivo, torna a coluna NULLABLE; segue nullable no head).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Head);

        (await ColumnIsNullableAsync(opt)).Should().BeTrue("o AUD-010 torna PasswordHash NULLABLE");

        await using var conn = await OpenAsync(opt);
        (await ScalarAsync(conn, "SELECT count(*) FROM \"IdentityAccounts\"")).Should().Be(1, "migration não apaga contas");
        (await TextAsync(conn, $"SELECT \"PasswordHash\" FROM \"IdentityAccounts\" WHERE \"Id\"='{localAccountId}'"))
            .Should().Be(localHash, "o hash existente é preservado byte a byte");
        (await TextAsync(conn, $"SELECT \"ExternalTenantId\" FROM \"IdentityAccounts\" WHERE \"Id\"='{localAccountId}'"))
            .Should().Be(Tid, "o vínculo Entra é preservado");
        (await ScalarAsync(conn, "SELECT count(*) FROM \"Users\"")).Should().Be(1, "memberships preservados");
        (await ScalarAsync(conn, "SELECT count(*) FROM \"UserRefreshTokens\"")).Should().Be(1, "refresh sessions preservados");

        // Agora uma conta FEDERATED-ONLY (PasswordHash NULL) pode existir — o que a coluna NOT NULL barrava.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.IdentityAccounts.Add(new IdentityAccount
            {
                Email = "fed@demo.example.com", PasswordHash = null,
                ExternalTenantId = Tid, ExternalObjectId = "aaaaaaaa-0000-0000-0000-000000000002",
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().NotThrowAsync("conta federated-only sem hash é válida após o AUD-010");
        }

        // O índice único PARCIAL de (tid,oid) segue sendo invariante: dois oids iguais colidem.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.IdentityAccounts.Add(new IdentityAccount
            {
                Email = "dup@demo.example.com", PasswordHash = null,
                ExternalTenantId = Tid, ExternalObjectId = "aaaaaaaa-0000-0000-0000-000000000002",
            });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("o índice único de vínculo (tid,oid) segue intacto");
        }

        // O índice único GLOBAL de e-mail segue barrando duplicata.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.IdentityAccounts.Add(new IdentityAccount { Email = "ana@demo.example.com", PasswordHash = null });
            var act = async () => await db.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>("o e-mail segue único GLOBAL");
        }
    }

    [Fact]
    public async Task Rollback_Aborts_WhenPasswordlessAccountExists_PreservingData_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud010);

        // Uma conta federated-only (PasswordHash NULL) torna o rollback INSEGURO. Vai por SQL cru — no
        // schema AUD-010 a coluna PlatformRole do modelo EF ainda não existe.
        await using (var seedConn = await OpenAsync(opt))
        {
            await using var cmd = seedConn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"CreatedAt\") " +
                $"VALUES ('{Guid.NewGuid()}','fed@demo.example.com', NULL, now())";
            await cmd.ExecuteNonQueryAsync();
        }

        // O Down ABORTA com mensagem explícita — nunca inventa hash nem apaga a conta.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var act = async () => await Migrator(db).MigrateAsync(PreAud010);
            (await act.Should().ThrowAsync<Exception>("o rollback é bloqueado enquanto houver conta passwordless"))
                .Which.Message.Should().Contain("federated-only", "a mensagem explica por que o rollback parou");
        }

        // Nada foi corrompido: a conta segue lá, ainda com PasswordHash NULL (não virou "" nem hash fictício).
        await using var conn = await OpenAsync(opt);
        (await ScalarAsync(conn, "SELECT count(*) FROM \"IdentityAccounts\" WHERE \"PasswordHash\" IS NULL"))
            .Should().Be(1, "a conta federated-only permanece intacta, sem credencial inventada");
        (await ColumnIsNullableAsync(opt)).Should().BeTrue("a coluna continua NULLABLE — o rollback não avançou");
    }

    // ---- helpers ----

    private static IMigrator Migrator(AegisScoreDbContext db) =>
        db.GetInfrastructure().GetRequiredService<IMigrator>();

    private static async Task<bool> ColumnIsNullableAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        await using var conn = await OpenAsync(opt);
        var value = await TextAsync(conn,
            "SELECT is_nullable FROM information_schema.columns " +
            "WHERE table_name='IdentityAccounts' AND column_name='PasswordHash'");
        return value == "YES";
    }

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

    private static async Task<string?> TextAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value ? null : (string?)result;
    }
}
