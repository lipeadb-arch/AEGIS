using System;
using System.Linq;
using System.Threading.Tasks;
using AegisScore.Application.Identity.Adm;
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
/// [AEGIS-KNIGHT-COVERAGE-02] O que só o PostgreSQL real prova para a configuração do Microsoft Teams, sobre o
/// schema MIGRADO:
///   • os documentos tipados gravam em jsonb (que reordena chaves e normaliza espaços) e ainda são relidos pelo
///     contrato — inclusive a lista de políticas, em que a identidade de cada uma é o que as distingue;
///   • a avaliação a partir do ADM dá o MESMO veredito do SQLite;
///   • uma aquisição do Teams e uma do Entra ID convivem no mesmo tenant sem se sobrescrever;
///   • o índice único parcial de sincronização passa a ser por (conector, FONTE): um pedido do Teams em
///     andamento não bloqueia um do Entra ID no mesmo conector — e um segundo pedido da MESMA fonte, sim.
/// </summary>
public sealed class KnightTeamsConfigurationPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightTeamsConfigurationPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConfiguracaoDoTeams_JsonbReal_MesmoVeredito_EConviveComAAquisicaoDoEntra()
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
                DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
            });
            await db.SaveChangesAsync();
        }

        Guid teamsRunId, snapshotId, entraRunId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var entra = await KnightMulticloudReportTests.ServiceFor(db, tenant).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
            entraRunId = entra.Id;

            var svc = KnightTeamsConfigurationFlowTests.ServiceFor(db, tenant,
                new TeamsCollectionScenario(TeamsCollectionScenario.Variant.CustomPolicyFails));
            var run = await svc.RunAssessmentAsync(KnightSourceType.MicrosoftTeams);
            teamsRunId = run.Id;

            // O veredito do PostgreSQL é o MESMO da bateria em SQLite: a política padrão atende, a personalizada não.
            var lobby = run.Indicators.Single(i => i.IndicatorId == "AK-TEAMS-010");
            lobby.Status.Should().Be(KnightIndicatorStatus.Exposed);
            lobby.AffectedObjectCount.Should().Be(1);

            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenant),
                new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
                .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftTeams, teamsRunId)).Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            // Duas aquisições, de duas FONTES, no mesmo tenant: nenhuma apagou a outra.
            var aquisicoes = await db.IdentityAcquisitions.AsNoTracking().ToListAsync();
            aquisicoes.Select(a => a.Provider).Should().BeEquivalentTo(new[]
            {
                KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftTeams,
            });
            (await db.KnightAssessmentRuns.AsNoTracking().CountAsync()).Should().Be(2);
            (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == entraRunId)).SourceType
                .Should().Be(KnightSourceType.MicrosoftEntraId);

            var teamsAcquisition = (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == teamsRunId)).IdentityAcquisitionId;
            var docs = await db.IdentityConfigurationObservations.AsNoTracking()
                .Where(c => c.AcquisitionId == teamsAcquisition).ToListAsync();

            var doTeams = KnightConfigurationKinds.All
                .Where(spec => KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftTeams).Contains(spec.Capability))
                .Select(spec => spec.Kind);
            docs.Select(d => d.Kind).Distinct().Should().Contain(doTeams);

            // Relidos do jsonb pelo contrato, sem documento "ilegível" — e as DUAS políticas de reunião
            // continuam distinguíveis pela identidade, que é o que sustenta a afirmação de alcance.
            var config = KnightTenantConfiguration.FromObserved(
                docs.Select(d => new IdentityObservedConfiguration(d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)),
                KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftTeams)
                    .Select(c => new KnightCapabilityStatus(c, KnightCapabilityOutcome.Collected)));

            var politicas = config.Read<TeamsMeetingPolicyConfiguration>();
            politicas.Collected.Should().BeTrue();
            politicas.Items.Select(p => p.Identity).Should().BeEquivalentTo(new[] { "Global", "Tag:Convidados" });
            politicas.Items.Single(p => p.Identity == "Tag:Convidados").AutoAdmittedUsers.Should().Be("Everyone");
            config.Read<TeamsFederationConfiguration>().Single!.AllowedDomains.Should().ContainSingle(d => d == "parceiro.example.com");
            config.Read<TeamsPolicyAssignment>().Items.Should().ContainSingle(a => a.GroupId == "11111111-2222-3333-4444-555555555555");

            // A avaliação relida do banco reproduz o mesmo veredito e o mesmo alcance declarado.
            var contexto = new KnightEvaluationContext(KnightFactSet.Empty,
                KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftTeams)
                    .Select(c => new KnightCapabilityStatus(c, KnightCapabilityOutcome.Collected)).ToList(),
                null, config, Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);
            var outcome = TeamsConfigurationControls.Definitions.Single(d => d.Id == "AK-TEAMS-010").Evaluate!(contexto);
            outcome.Status.Should().Be(KnightIndicatorStatus.Exposed);
            outcome.Affected.Single().Detail.Should().Contain("11111111-2222-3333-4444-555555555555");

            var snapshot = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            snapshot.SourceType.Should().Be(KnightSourceType.MicrosoftTeams);
            snapshot.Indicators.Should().OnlyContain(i => i.IndicatorId.StartsWith("AK-TEAMS-"));
            PostureSnapshotHasher.Verify(snapshot).Should().BeTrue("a fotografia do Teams sobrevive à ida e volta ao PostgreSQL");
        }
    }

    /// <summary>
    /// O índice único parcial que garante "no máximo um pedido ATIVO" passou a incluir a FONTE. Só o PostgreSQL
    /// real aplica o filtro parcial: é ele que decide se as duas fontes convivem ou se uma bloqueia a outra.
    /// </summary>
    [Fact]
    public async Task PedidoDeSincronizacao_AtivoPorFonte_NaoBloqueiaAOutraFonteDoMesmoConector()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = Guid.NewGuid();
        Guid connectorId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente PG", Slug = $"t-{tenant:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var connector = new ConnectorConfig
            {
                TenantId = tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Microsoft 365", Enabled = true, EncryptedSettings = "cifrado",
            };
            db.Connectors.Add(connector);
            await db.SaveChangesAsync();
            connectorId = connector.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var requests = new KnightSyncRequests(db, new SystemTenantContext(tenant), TimeProvider.System);

            var entra = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftEntraId, null);
            entra.AlreadyActive.Should().BeFalse();

            var teams = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftTeams, null);
            teams.AlreadyActive.Should().BeFalse("são fontes diferentes: uma coleta do Teams não espera a do Entra ID");
            teams.Request.Id.Should().NotBe(entra.Request.Id);

            // Da MESMA fonte, o segundo clique continua devolvendo o MESMO pedido — sem nova coleta.
            var denovo = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftTeams, null);
            denovo.AlreadyActive.Should().BeTrue();
            denovo.Request.Id.Should().Be(teams.Request.Id);

            // A tela acompanha a linha da sua fonte, não a última de qualquer uma.
            (await requests.GetLatestAsync(connectorId, KnightSourceType.MicrosoftEntraId))!.Id.Should().Be(entra.Request.Id);
            (await requests.GetLatestAsync(connectorId, KnightSourceType.MicrosoftTeams))!.Id.Should().Be(teams.Request.Id);
        }
    }
}
