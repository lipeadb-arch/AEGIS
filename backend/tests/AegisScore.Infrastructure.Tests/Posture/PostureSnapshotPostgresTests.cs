using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // reutiliza PostgresProbe (infra AEGIS_TEST_PG já existente)
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-AUD-036] A imutabilidade da fotografia é de severidade ALTA: a foto publicada precisa permanecer
/// verdadeira mesmo diante de um UPDATE/DELETE fora do serviço. Por isso é validada também contra um PostgreSQL
/// DESCARTÁVEL real, com as MIGRATIONS aplicadas (o gatilho append-only vive na migration, não no modelo — o
/// EnsureCreated dos testes SQLite não o cria). O gatilho tem que provar, no banco, que a linha não muda nem
/// some. Gated por <c>AEGIS_TEST_PG</c>; reutiliza a <see cref="PostgresProbe"/> (banco descartável por teste,
/// <c>aegis_dev</c> nunca é tocado). Sem a variável, o teste registra a ausência e retorna.
/// </summary>
public sealed class PostureSnapshotPostgresTests
{
    private readonly ITestOutputHelper _output;

    public PostureSnapshotPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PublishedSnapshot_IsAppendOnly_UpdateAndDeleteBlocked_OnRealPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }

        var opt = pg.DbOptions();

        // Migra (NÃO EnsureCreated): é a migration que instala o gatilho append-only.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = Guid.NewGuid();

        // Publica uma fotografia mínima (pai + 1 controle) via o pipeline de escrita (stamping fail-closed).
        Guid snapshotId, controlId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var snapshot = new PostureSnapshot
            {
                Type = PostureSnapshotType.AegisScoreNist,
                SchemaVersion = "posture-snapshot-v1",
                FormulaVersion = "aegis-score-v1",
                CatalogVersion = "NIST CSF 2.0",
                SemanticFamily = "aegis-nist:NIST CSF 2.0",
                CapturedAt = DateTimeOffset.UtcNow,
                Score = 80,
                AchievedPoints = 16,
                PossiblePoints = 20,
                EligiblePoints = 20,
                Coverage = 100,
                EvaluatedItems = 1,
                EligibleItems = 1,
                CompliantCount = 1,
                ContentHash = "hash-original",
            };
            snapshot.Controls.Add(new PostureSnapshotControl
            {
                SubcategoryCode = "PR.AA-01", FunctionCode = "PR", Evaluated = true, Status = ControlStatus.Compliant,
                AchievedPoints = 16, MaxPoints = 20, VerdictSource = VerdictSource.Telemetry,
                EvaluatedAt = DateTimeOffset.UtcNow,
            });
            db.PostureSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
            snapshotId = snapshot.Id;
            controlId = snapshot.Controls.First().Id;
        }

        // UPDATE e DELETE crus (fora do serviço) são RECUSADOS pelo gatilho — no pai e no filho.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var updateParent = async () => await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PostureSnapshots\" SET \"ContentHash\" = 'adulterado' WHERE \"Id\" = {0}", snapshotId);
            (await updateParent.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");

            var deleteParent = async () => await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"PostureSnapshots\" WHERE \"Id\" = {0}", snapshotId);
            await deleteParent.Should().ThrowAsync<PostgresException>();

            var updateChild = async () => await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"PostureSnapshotControls\" SET \"Status\" = 1 WHERE \"Id\" = {0}", controlId);
            await updateChild.Should().ThrowAsync<PostgresException>();

            var deleteChild = async () => await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"PostureSnapshotControls\" WHERE \"Id\" = {0}", controlId);
            await deleteChild.Should().ThrowAsync<PostgresException>();
        }

        // A fotografia permanece intacta (linha, dono e conteúdo preservados) no PostgreSQL real.
        await using (var verify = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var row = await verify.PostureSnapshots.IgnoreQueryFilters()
                .Include(s => s.Controls)
                .FirstOrDefaultAsync(s => s.Id == snapshotId);
            row.Should().NotBeNull();
            row!.TenantId.Should().Be(tenant);
            row.ContentHash.Should().Be("hash-original");
            row.Controls.Should().ContainSingle(c => c.Id == controlId);
        }
    }
}
