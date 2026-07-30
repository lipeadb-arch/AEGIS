using System.Text.RegularExpressions;
using AegisScore.Application.Abstractions;
using AegisScore.Domain;
using AegisScore.Infrastructure.Auth;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe (infra AEGIS_TEST_PG já existente)
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Auth;

/// <summary>
/// [AEGIS-AUD-009] O que o SQLite não prova: (1) a rotação ATÔMICA sob concorrência REAL — só um vencedor,
/// sem fork de cadeia, perdedores em conflito benigno; (2) a MIGRATION real sobre o schema anterior —
/// backfill dos brutos legados em hash, lookup por hash, ausência de plaintext e um <c>Down</c> que
/// invalida sessões sem reintroduzir plaintext. Gated por <c>AEGIS_TEST_PG</c>; cada teste cria e destrói
/// seu próprio database (o <c>aegis_dev</c> nunca é tocado).
/// </summary>
public sealed class RefreshTokenPostgresTests
{
    private const string Senha = "uma frase longa e boa de verdade";
    private const string Email = "ana@demo.example.com";
    private static readonly Sha256RefreshTokenHasher Hasher = new();

    private const string PreAud009 = "20260724002301_Aud50_DurableOperationalQueues";
    private const string Aud009 = "20260727204000_Aud009HashRefreshTokens";
    // [AEGIS-AUD-011] Aplicar as migrations aditivas posteriores (vínculo Entra, PasswordHash nullable e
    // o eixo global PlatformRole) alinha o schema ao modelo EF ATUAL, para que o SELECT interno de
    // IdentityAccount (no RefreshAsync) rode sem referenciar colunas ainda inexistentes. Aponta para o head.
    private const string ModelHead = "20260729211634_Aud011SeparatePlatformTenantRoles";

    private readonly ITestOutputHelper _output;
    public RefreshTokenPostgresTests(ITestOutputHelper output) => _output = output;

    // ---- Concorrência real da rotação (itens 11, 12, 13, 14, 19) ------------------------------------

    [Fact]
    public async Task ConcurrentRefresh_OnlyOneWins_NoChainFork_LosersAreBenignConflict_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.EnsureCreatedAsync();

        var tenant = Guid.NewGuid();
        await SeedAccountAndMembershipAsync(opt, tenant);

        // Uma sessão inicial (login) → o bruto que várias requisições vão apresentar ao MESMO tempo.
        string raw0;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            raw0 = (await ServiceFor(db, opt).LoginAsync(Email, Senha, null, default)).Pair!.RefreshToken;

        // 8 "requisições" concorrentes com o MESMO cookie. Cada uma com seu próprio DbContext (não é
        // thread-safe). Só uma pode vencer o UPDATE ... WHERE RevokedAt IS NULL.
        var contexts = new List<AegisScoreDbContext>();
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
                lock (contexts) contexts.Add(db);
                return await ServiceFor(db, opt).RefreshAsync(raw0, default);
            }));
            var results = await Task.WhenAll(tasks);

            results.Count(r => r.Outcome == RefreshOutcome.Success)
                .Should().Be(1, "apenas um refresh concorrente vence a rotação atômica");
            results.Where(r => r.Outcome != RefreshOutcome.Success)
                .Should().OnlyContain(r => r.Outcome == RefreshOutcome.RotationConflict,
                    "os perdedores caem no conflito benigno da janela — nunca em breach");

            var winner = results.Single(r => r.Outcome == RefreshOutcome.Success);

            await using var verify = new AegisScoreDbContext(opt, new SystemTenantContext(null));
            var rows = await verify.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking().ToListAsync();

            rows.Should().HaveCount(2, "pai + UM único sucessor — sem fork de cadeia");
            rows.Count(r => r.RevokedAt == null).Should().Be(1, "só o sucessor fica ativo");
            rows.Single(r => r.TokenHash == Hasher.Hash(raw0)).RevokedAt
                .Should().NotBeNull("o pai foi revogado pela rotação");
            rows.Single(r => r.RevokedAt == null).TokenHash
                .Should().Be(Hasher.Hash(winner.Pair!.RefreshToken),
                    "o único sucessor ativo é exatamente o bruto entregue ao vencedor");

            // Nenhum bruto (nem raw0, nem o sucessor) aparece em qualquer coluna persistida.
            await AssertNoPlaintextAsync(opt, raw0, winner.Pair!.RefreshToken);
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    // ---- Migration real: transforma linha legada, sem plaintext; Down não reintroduz plaintext (22,23,19)

    [Fact]
    public async Task Migration_HashesLegacyRow_PreservesSession_NoPlaintext_DownInvalidatesOnly_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();

        // 1) Schema ANTERIOR ao AUD-009 (colunas brutas Token / ReplacedByToken).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud009);

        var tenant = Guid.NewGuid();
        var userId = await SeedAccountAndMembershipAsync(opt, tenant);

        // 2) Uma sessão LEGADA real: pai rotacionado→filho, ambos com o bruto persistido (como era antes).
        var parentRaw = "legacy-parent-" + Guid.NewGuid().ToString("N");
        var childRaw = "legacy-child-" + Guid.NewGuid().ToString("N");
        await InsertLegacyTokenAsync(opt, tenant, userId, parentRaw,
            revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1), replacedByRaw: childRaw);
        await InsertLegacyTokenAsync(opt, tenant, userId, childRaw, revokedAt: null, replacedByRaw: null);

        // 3) Aplica o AUD-009 — o backfill hasheia os brutos existentes.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(Aud009);

        // 4) Linha legada agora é HASH; a cadeia pai→filho continua íntegra por hash.
        await using (var verify = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var pai = await verify.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(t => t.TokenHash == Hasher.Hash(parentRaw));
            var filho = await verify.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(t => t.TokenHash == Hasher.Hash(childRaw));

            pai.ReplacedByTokenHash.Should().Be(Hasher.Hash(childRaw), "o sucessor do pai virou hash");
            pai.ReplacedByTokenHash.Should().Be(filho.TokenHash, "cadeia preservada: pai.ReplacedBy == filho.TokenHash");
            Regex.IsMatch(filho.TokenHash, "^[0-9a-f]{64}$").Should().BeTrue();
        }

        await AssertNoPlaintextAsync(opt, parentRaw, childRaw);

        // 4b) Alinha o schema ao modelo EF atual (migrations aditivas posteriores) — sem isto o SELECT de
        //     IdentityAccount dentro do RefreshAsync referenciaria colunas ainda inexistentes no schema Aud009.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(ModelHead);

        // 5) A sessão legada CONTINUA utilizável: o filho (ativo) rotaciona por hash, sem novo login.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            (await ServiceFor(db, opt).RefreshAsync(childRaw, default)).Outcome
                .Should().Be(RefreshOutcome.Success, "migration preservou a sessão existente");

        // 6) Down: NÃO restaura plaintext. Invalida sessões (remove linhas) e volta aos nomes antigos.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await Migrator(db).MigrateAsync(PreAud009);

        await using (var conn = await OpenAsync(opt))
        {
            // Colunas antigas voltam (a query só compila se existirem) e a tabela está VAZIA — zero plaintext.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM \"UserRefreshTokens\" WHERE \"Token\" IS NOT NULL OR \"ReplacedByToken\" IS NOT NULL";
            var remaining = (long)(await cmd.ExecuteScalarAsync())!;
            remaining.Should().Be(0, "o Down apagou as linhas de refresh (sem reintroduzir plaintext)");
        }

        // Usuário/conta permanecem — o Down invalida SOMENTE sessões.
        await using (var conn = await OpenAsync(opt))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM \"Users\"";
            ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1, "Down não apaga memberships");
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static IMigrator Migrator(AegisScoreDbContext db) =>
        db.GetInfrastructure().GetRequiredService<IMigrator>();

    private static async Task<NpgsqlConnection> OpenAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        var conn = new NpgsqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Semeia Tenant + IdentityAccount (globais) e um membership ativo (tenant-owned). Retorna o UserId.</summary>
    private static async Task<Guid> SeedAccountAndMembershipAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant)
    {
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenant))
            {
                db.Tenants.Add(new Tenant { Id = tenant, Name = "Alfa", Slug = "alfa-" + tenant.ToString("N")[..8], Status = TenantStatus.Active });
                await db.SaveChangesAsync();
            }
        }

        // IdentityAccount por SQL cru (só colunas-base): este teste roda contra schemas ANTERIORES ao
        // AUD-007, que não têm as colunas de vínculo Entra presentes no modelo EF atual — um INSERT via EF
        // referenciaria colunas ainda inexistentes.
        var accountId = Guid.NewGuid();
        await using (var conn = await OpenAsync(opt))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO \"IdentityAccounts\" (\"Id\",\"Email\",\"PasswordHash\",\"CreatedAt\") " +
                              "VALUES (@id,@email,@pw,@created)";
            cmd.Parameters.AddWithValue("id", accountId);
            cmd.Parameters.AddWithValue("email", Email);
            cmd.Parameters.AddWithValue("pw", new Pbkdf2PasswordHasher().Hash(Senha));
            cmd.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var user = new User { TenantId = tenant, IdentityAccountId = accountId, DisplayName = "Ana", Role = TenantRole.Analyst };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        }
    }

    /// <summary>Insere uma linha no SCHEMA ANTIGO (colunas brutas), como existiria antes do AUD-009.</summary>
    private static async Task InsertLegacyTokenAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid userId, string rawToken,
        DateTimeOffset? revokedAt, string? replacedByRaw)
    {
        await using var conn = await OpenAsync(opt);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "UserRefreshTokens"
                ("Id","TenantId","UserId","Token","ExpiresAt","RevokedAt","ReplacedByToken","CreatedAt","UpdatedAt")
            VALUES (@id,@tenant,@user,@token,@exp,@revoked,@replaced,@created,NULL)
            """;
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("token", rawToken);
        cmd.Parameters.AddWithValue("exp", DateTimeOffset.UtcNow.AddDays(7));
        cmd.Parameters.Add(new NpgsqlParameter("revoked", NpgsqlDbType.TimestampTz)
        { Value = (object?)revokedAt ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("replaced", NpgsqlDbType.Text)
        { Value = (object?)replacedByRaw ?? DBNull.Value });
        cmd.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Prova que NENHUMA coluna persistida contém qualquer um dos brutos informados.</summary>
    private static async Task AssertNoPlaintextAsync(
        DbContextOptions<AegisScoreDbContext> opt, params string[] raws)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        foreach (var raw in raws)
        {
            var found = await db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(t => t.TokenHash == raw || t.ReplacedByTokenHash == raw);
            found.Should().BeFalse($"o bruto não pode estar em nenhuma coluna");
        }
        // Todo TokenHash é SHA-256 hex de 64 chars.
        var hashes = await db.UserRefreshTokens.IgnoreQueryFilters().AsNoTracking()
            .Select(t => t.TokenHash).ToListAsync();
        hashes.Should().OnlyContain(h => Regex.IsMatch(h, "^[0-9a-f]{64}$"));
    }

    private static AuthService ServiceFor(AegisScoreDbContext db, DbContextOptions<AegisScoreDbContext> opt) =>
        new(db, opt, new StubTokenService(), new Pbkdf2PasswordHasher(), Hasher,
            Options.Create(new FederationOptions()), NullLogger<AuthService>.Instance);

    private sealed class StubTokenService : IJwtTokenService
    {
        public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User m, IdentityAccount a) =>
            ($"access.{m.TenantId}", DateTimeOffset.UtcNow.AddMinutes(10));

        public (string Token, DateTimeOffset ExpiresAt) CreateRefreshToken() =>
            (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(7));

        // [AEGIS-AUD-012] Ticket de seleção com round-trip simples (sem JWT real).
        public (string Token, DateTimeOffset ExpiresAt) CreateTenantSelectionTicket(Guid accountId) =>
            ($"selticket.{accountId}", DateTimeOffset.UtcNow.AddMinutes(5));

        public bool TryReadTenantSelectionTicket(string ticket, out Guid accountId)
        {
            accountId = Guid.Empty;
            const string prefix = "selticket.";
            return ticket?.StartsWith(prefix) == true && Guid.TryParse(ticket[prefix.Length..], out accountId);
        }
    }
}
