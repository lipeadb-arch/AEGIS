using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] O que só o PostgreSQL real prova, sobre o schema MIGRADO (não EnsureCreated):
///   • os objetos de configuração do ADM e os objetos/evidências da avaliação gravam e releem com os tipos reais
///     (jsonb, FKs compostas tenant-safe);
///   • a fotografia v2 re-deriva o hash depois da ida-e-volta ao banco (precisão de timestamptz, jsonb);
///   • os objetos congelados herdam o gatilho append-only das fotografias;
///   • o índice único PARCIAL impede dois pedidos de sincronização ativos para o mesmo conector.
/// </summary>
public sealed class KnightMulticloudPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightMulticloudPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ColetaAvaliacaoEFotografiaV2_SobreSchemaMigrado_ComImutabilidadeEIndiceParcial()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = Guid.NewGuid();
        Guid conector;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente PG", Slug = $"t-{tenant:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var c = new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Entra", Enabled = true, EncryptedSettings = "cifrado",
            };
            db.Connectors.Add(c);
            await db.SaveChangesAsync();
            conector = c.Id;
        }

        Guid runId, snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var assessment = await KnightMulticloudReportTests.ServiceFor(db, tenant).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
            runId = assessment.Id;
            assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-008").AffectedObjectCount.Should().Be(1);
            var published = await new PostureSnapshotService(db, new SystemTenantContext(tenant),
                new AegisScore.Infrastructure.Connectors.NistSignalMapper(db)).PublishAsync(PostureSnapshotType.Knight, null, runId);
            snapshotId = published.Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            var configs = await db.IdentityConfigurationObservations.AsNoTracking()
                .Where(c => c.AcquisitionId == run.IdentityAcquisitionId).ToListAsync();
            configs.Should().HaveCount(4);
            configs.Should().Contain(c => c.Kind == ConfigurationObjectKind.ConditionalAccessPolicy && c.ExternalId == "p-admins"
                && c.SchemaVersion == "aegis-config-entra-ca-policy-v1" && c.ConfigurationJson.Contains("includeRoles"));

            (await db.KnightAffectedObjects.CountAsync(o => o.RunId == runId && o.Relation == KnightObjectRelation.Evidence))
                .Should().BeGreaterThan(0);

            // Hash re-derivável depois da ida-e-volta ao PostgreSQL, e exportação a partir de um contexto novo.
            var exported = await new PostureSnapshotExporter(db).ExportAsync(snapshotId, PostureExportFormat.Html);
            Encoding.UTF8.GetString(exported!.Content).Should().Contain("aegis-data");
        }

        // Os objetos congelados são append-only no próprio banco.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var update = async () => await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"PostureSnapshotObjects\" SET \"Detail\" = 'adulterado' WHERE \"SnapshotId\" = {snapshotId}");
            (await update.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("append-only");
        }

        // Um pedido ATIVO por conector é invariante do banco (índice único parcial), e a fila adquire no PostgreSQL.
        await using (var db1 = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        await using (var db2 = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var r1 = new KnightSyncRequests(db1, new SystemTenantContext(tenant), TimeProvider.System);
            var r2 = new KnightSyncRequests(db2, new SystemTenantContext(tenant), TimeProvider.System);
            var both = await Task.WhenAll(
                r1.EnqueueAsync(conector, KnightSourceType.MicrosoftEntraId, null),
                r2.EnqueueAsync(conector, KnightSourceType.MicrosoftEntraId, null));
            both.Select(b => b.Request.Id).Distinct().Should().ContainSingle("dois cliques simultâneos convergem para UM pedido");
            (await db1.KnightSyncRequests.CountAsync()).Should().Be(1);
        }
    }
}
