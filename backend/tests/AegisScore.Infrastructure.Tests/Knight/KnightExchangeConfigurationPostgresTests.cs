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
/// [AEGIS-KNIGHT-COVERAGE-03] O que só o PostgreSQL real prova para a configuração do Exchange Online, sobre o
/// schema MIGRADO:
///   • os quinze documentos tipados gravam em jsonb (que reordena chaves, normaliza espaços e não guarda a
///     ordem original) e ainda voltam pelo contrato — inclusive os inventários, em que a HERANÇA é um valor
///     AUSENTE e precisa continuar ausente depois da ida e volta;
///   • a avaliação relida do banco dá o MESMO veredito da bateria em SQLite;
///   • as três aquisições Microsoft — Entra ID, Teams e Exchange Online — convivem no mesmo tenant sem que
///     nenhuma apague a outra;
///   • os textos que a avaliação GRAVA cabem nas colunas que os guardam. O SQLite ignora HasMaxLength; o
///     PostgreSQL recusa, e é aqui que o motivo montado a partir de dado coletado é de fato verificado;
///   • o índice único parcial de sincronização é por (conector, FONTE): um pedido do Exchange não bloqueia o
///     do Teams nem o do Entra ID no mesmo conector.
/// </summary>
public sealed class KnightExchangeConfigurationPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightExchangeConfigurationPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConfiguracaoDoExchange_JsonbReal_MesmoVeredito_EConviveComAsOutrasFontesMicrosoft()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = await SeedAsync(opt);

        Guid exchangeRunId, entraRunId, snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var entra = await KnightMulticloudReportTests.ServiceFor(db, tenant).RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
            entraRunId = entra.Id;

            // A SUBSTITUIÇÃO POR CAIXA é o cenário escolhido de propósito: é o que exige que o valor AUSENTE
            // (herança) continue ausente depois do jsonb. Se a ida e volta o transformasse em "false", a
            // contagem publicada inverteria de sentido sem nenhum erro aparente.
            var run = await KnightExchangeConfigurationFlowTests
                .ServiceFor(db, tenant, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.SmtpAuthOverride))
                .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline);
            exchangeRunId = run.Id;

            var smtp = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-016");
            smtp.Status.Should().Be(KnightIndicatorStatus.Exposed, "o mesmo veredito da bateria em SQLite");
            smtp.AffectedObjectCount.Should().Be(1);

            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenant),
                    new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
                .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, exchangeRunId)).Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            // Duas aquisições de duas FONTES no mesmo tenant: nenhuma apagou a outra.
            var aquisicoes = await db.IdentityAcquisitions.AsNoTracking().ToListAsync();
            aquisicoes.Select(a => a.Provider).Should().BeEquivalentTo(new[]
            {
                KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftExchangeOnline,
            });
            aquisicoes.Single(a => a.Provider == KnightSourceType.MicrosoftExchangeOnline).SourceLabel
                .Should().Be("Exchange Online");
            (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == entraRunId)).SourceType
                .Should().Be(KnightSourceType.MicrosoftEntraId);

            var acquisitionId = (await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == exchangeRunId)).IdentityAcquisitionId;
            acquisitionId.Should().NotBeNull();
            var docs = await db.IdentityConfigurationObservations.AsNoTracking()
                .Where(c => c.AcquisitionId == acquisitionId).ToListAsync();

            var capacidades = KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftExchangeOnline)
                .Select(c => new KnightCapabilityStatus(c, KnightCapabilityOutcome.Collected)).ToList();
            var config0 = KnightTenantConfiguration.FromObserved(
                docs.Select(d => new IdentityObservedConfiguration(d.Kind, d.ExternalId, d.DisplayName, d.SchemaVersion, d.ConfigurationJson)),
                capacidades);

            var doExchange = KnightConfigurationKinds.All
                .Where(spec => KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftExchangeOnline).Contains(spec.Capability))
                .Select(spec => spec.Kind)
                .Distinct()
                .ToList();

            // Todo documento gravado pertence ao Exchange e traz contrato versionado — nenhum invade outra fonte.
            docs.Select(d => d.Kind).Distinct().Should().BeSubsetOf(doExchange);
            docs.Should().OnlyContain(d => d.SchemaVersion.StartsWith("aegis-config-exo-"));

            // A ÚNICA ausência é a regra de transporte, e ela é significativa: neste ambiente não existe regra
            // alguma, e uma coleção vazia não gera documento. Quem atesta que a leitura ACONTECEU é a
            // CAPACIDADE, não a existência do documento — confundir as duas transformaria "não há regra" em
            // "não foi lido" (ou o contrário, que é pior).
            docs.Select(d => d.Kind).Distinct().Should()
                .Contain(doExchange.Where(k => k != ConfigurationObjectKind.ExchangeTransportRule));
            docs.Should().NotContain(d => d.Kind == ConfigurationObjectKind.ExchangeTransportRule);
            config0.Read<ExchangeTransportRuleConfiguration>().Collected.Should().BeTrue(
                "a leitura Get-TransportRule concluiu; o que ela devolveu foi uma lista vazia");
            config0.Read<ExchangeTransportRuleConfiguration>().Items.Should().BeEmpty();

            var config = config0;

            // HERANÇA depois do jsonb: o ausente continua ausente, e a contagem por origem não se mistura.
            var inv = config.Read<ExchangeSmtpAuthOverrideInventory>().Single!;
            inv.InheritingOrganizationCount.Should().Be(1, "a caixa que não declarou valor HERDA — não é 'desabilitada'");
            inv.ExplicitlyDisabledCount.Should().Be(0);
            inv.ExplicitlyEnabled.Should().ContainSingle(m => m.SmtpClientAuthenticationDisabled == false);

            // As LISTAS de política sobrevivem à ida e volta distinguíveis pela identidade, que é o que sustenta
            // qualquer afirmação de alcance.
            var compartilhamento = config.Read<ExchangeSharingPolicyConfiguration>();
            compartilhamento.Collected.Should().BeTrue();
            compartilhamento.Items.Select(p => p.Identity)
                .Should().BeEquivalentTo(new[] { "Default Sharing Policy", "Parceiros (desativada)" });
            compartilhamento.Items.Single(p => p.IsDefault == true).Enabled.Should().BeTrue();
            config.Read<ExchangeOrganizationConfiguration>().Single!.CustomerLockBoxEnabled.Should().BeTrue();

            // E a avaliação RELIDA do banco reproduz o mesmo veredito, com o mesmo objeto afetado nomeado.
            var contexto = new KnightEvaluationContext(
                KnightFactSet.Empty, capacidades, null, config,
                Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);
            var outcome = ExchangeConfigurationControls.Definitions.Single(d => d.Id == "AK-EXO-016").Evaluate!(contexto);
            outcome.Status.Should().Be(KnightIndicatorStatus.Exposed);
            outcome.Affected.Should().ContainSingle(a => a.UserPrincipalName == "ana.silva@example.com");

            var snapshot = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            snapshot.SourceType.Should().Be(KnightSourceType.MicrosoftExchangeOnline);
            snapshot.Indicators.Should().OnlyContain(i => i.IndicatorId.StartsWith("AK-EXO-"));
            PostureSnapshotHasher.Verify(snapshot).Should().BeTrue("a fotografia do Exchange sobrevive à ida e volta ao PostgreSQL");
        }
    }

    /// <summary>
    /// Os textos que a avaliação GRAVA — evidência e motivo de não avaliado — são montados a partir de dado
    /// COLETADO: nomes de política, endereços, comandos recusados, listas que crescem com o ambiente. O SQLite
    /// ignora o limite da coluna e aceita qualquer tamanho; o PostgreSQL recusa. Este teste percorre TODAS as
    /// variantes do ambiente sintético — inclusive as que produzem os textos mais longos (leitura recusada,
    /// mecanismo faltando, truncamento e alcance de políticas) — e grava cada uma de verdade, na execução E na
    /// fotografia publicada, que tem limites próprios e mais apertados para a evidência.
    /// </summary>
    [Fact]
    public async Task TextosDaAvaliacaoDoExchange_CabemNasColunasReais_EmTodasAsVariantes()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = await SeedAsync(opt);

        foreach (var variante in Enum.GetValues<ExchangeCollectionScenario.Variant>())
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            var run = await KnightExchangeConfigurationFlowTests
                .ServiceFor(db, tenant, new ExchangeCollectionScenario(variante))
                .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline);

            // A GRAVAÇÃO é a prova: se algum texto não coubesse, o PostgreSQL teria recusado antes daqui.
            var published = await new PostureSnapshotService(db, new SystemTenantContext(tenant),
                    new AegisScore.Infrastructure.Connectors.NistSignalMapper(db))
                .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, run.Id);

            var persistido = await db.KnightAssessmentRuns.AsNoTracking()
                .Include(r => r.Indicators)
                .SingleAsync(r => r.Id == run.Id);
            persistido.Indicators.Count(i => i.IndicatorId.StartsWith("AK-EXO-")).Should().Be(17, variante.ToString());

            var fotografia = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Indicators)
                .SingleAsync(s => s.Id == published.Summary.Id);
            fotografia.Indicators.Should().HaveCount(persistido.Indicators.Count, variante.ToString());

            // A releitura confirma que nada foi cortado em silêncio no caminho de ida.
            foreach (var indicador in fotografia.Indicators)
            {
                (indicador.Evidence?.Length ?? 0).Should().BeLessThanOrEqualTo(2000, $"{variante}/{indicador.IndicatorId}");
                (indicador.NotEvaluatedReason?.Length ?? 0).Should().BeLessThanOrEqualTo(1000, $"{variante}/{indicador.IndicatorId}");
            }
            foreach (var indicador in persistido.Indicators.Where(i => i.IndicatorId.StartsWith("AK-EXO-")))
                (indicador.NotEvaluatedReason?.Length ?? 0).Should().BeLessThanOrEqualTo(500,
                    $"{variante}/{indicador.IndicatorId}: o motivo é montado a partir de dado coletado");

            var maior = persistido.Indicators.Where(i => i.IndicatorId.StartsWith("AK-EXO-"))
                .OrderByDescending(i => (i.Evidence ?? "").Length + (i.NotEvaluatedReason ?? "").Length).First();
            _output.WriteLine($"{variante}: estado {persistido.SourceState}; texto mais longo em {maior.IndicatorId} "
                + $"({(maior.Evidence ?? "").Length} + {(maior.NotEvaluatedReason ?? "").Length} caracteres)");
        }
    }

    /// <summary>
    /// O índice único parcial "no máximo um pedido ATIVO" é por (conector, FONTE). Só o PostgreSQL real aplica
    /// o filtro parcial — e agora são TRÊS fontes no mesmo conector Microsoft.
    /// </summary>
    [Fact]
    public async Task PedidoDeSincronizacaoDoExchange_NaoBloqueiaTeamsNemEntra_NoMesmoConector()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();

        var tenant = await SeedAsync(opt);
        Guid connectorId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            connectorId = (await db.Connectors.AsNoTracking().SingleAsync()).Id;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var requests = new KnightSyncRequests(db, new SystemTenantContext(tenant), TimeProvider.System);

            var entra = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftEntraId, null);
            var teams = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftTeams, null);
            var exchange = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftExchangeOnline, null);

            exchange.AlreadyActive.Should().BeFalse("são três fontes distintas do mesmo registro de aplicação");
            exchange.Request.Id.Should().NotBe(entra.Request.Id).And.NotBe(teams.Request.Id);

            // Da MESMA fonte, o segundo clique devolve o MESMO pedido — sem nova coleta.
            var denovo = await requests.EnqueueAsync(connectorId, KnightSourceType.MicrosoftExchangeOnline, null);
            denovo.AlreadyActive.Should().BeTrue();
            denovo.Request.Id.Should().Be(exchange.Request.Id);

            (await requests.GetLatestAsync(connectorId, KnightSourceType.MicrosoftExchangeOnline))!.Id
                .Should().Be(exchange.Request.Id);
            (await requests.GetLatestAsync(connectorId, KnightSourceType.MicrosoftTeams))!.Id.Should().Be(teams.Request.Id);
            (await requests.GetLatestAsync(connectorId, KnightSourceType.MicrosoftEntraId))!.Id.Should().Be(entra.Request.Id);
        }
    }

    private static async Task<Guid> SeedAsync(DbContextOptions<AegisScoreDbContext> opt)
    {
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
        return tenant;
    }
}
