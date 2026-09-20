using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] O que só o PostgreSQL real prova para a configuração do locatário, sobre o schema
/// MIGRADO: os documentos tipados gravam em jsonb (que reordena chaves e normaliza espaços) e ainda são relidos pelo
/// contrato; a avaliação a partir do ADM dá o mesmo veredito do SQLite; e a fotografia com impacto, plataforma e
/// cobertura congelados re-deriva o hash depois da ida e volta ao banco.
/// </summary>
public sealed class KnightEntraConfigurationPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightEntraConfigurationPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConfiguracaoDoLocatario_JsonbReal_MesmoVeredito_EFotografiaVerificavel()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = Guid.NewGuid();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente PG", Slug = $"t-{tenant:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Entra", Enabled = true, EncryptedSettings = "cifrado",
            });
            await db.SaveChangesAsync();
        }

        Guid runId, snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var run = await KnightMulticloudReportTests.ServiceFor(db, tenant,
                    new EntraConfigurationScenario(EntraConfigurationScenario.Variant.NonCompliant).Handler())
                .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
            runId = run.Id;
            var ids = EntraConfigurationControls.Definitions.Select(d => d.Id).ToHashSet();
            run.Indicators.Where(i => ids.Contains(i.IndicatorId)).Should().OnlyContain(i => i.Status == KnightIndicatorStatus.Exposed);
            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenant),
                new AegisScore.Infrastructure.Connectors.NistSignalMapper(db)).PublishAsync(PostureSnapshotType.Knight, null, runId)).Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var acquisition = (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId)).IdentityAcquisitionId;
            var docs = await db.IdentityConfigurationObservations.AsNoTracking().Where(c => c.AcquisitionId == acquisition).ToListAsync();
            // O cenário não conforme não tem revisão de acesso: a capacidade foi COLETADA com lista vazia (nenhum documento).
            // Só os tipos que o coletor do ENTRA produz: o catálogo de tipos também abriga os do Microsoft Teams,
            // que pertencem a outra fonte e a outra aquisição.
            var doEntra = KnightConfigurationKinds.All
                .Where(spec => KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftEntraId).Contains(spec.Capability))
                .Select(spec => spec.Kind)
                .Where(k => k != ConfigurationObjectKind.AccessReviewDefinition);
            docs.Select(d => d.Kind).Distinct().Should().Contain(doEntra);

            // Relidos do jsonb pelo contrato, sem documento "ilegível".
            var config = KnightTenantConfiguration.FromObserved(
                docs.Select(d => new AegisScore.Application.Identity.Adm.IdentityObservedConfiguration(d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)),
                KnightConfigurationKinds.All.Select(s => new KnightCapabilityStatus(s.Capability, KnightCapabilityOutcome.Collected)).DistinctBy(c => c.Capability));

            config.Read<EntraAuthorizationPolicyConfiguration>().Collected.Should().BeTrue();
            config.Read<EntraPrivilegedRoleGovernance>().Items.Should().HaveCount(EntraConfigurationControls.PrivilegedRoleTemplates.Count);

            var snapshot = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            PostureSnapshotHasher.Verify(snapshot).Should().BeTrue("impacto, plataforma e cobertura congelados sobrevivem à ida e volta ao PostgreSQL");
            snapshot.ReferenceCoverageJson.Should().NotBeNullOrWhiteSpace();
        }
    }
}
