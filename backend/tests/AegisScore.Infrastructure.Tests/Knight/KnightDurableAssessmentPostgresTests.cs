using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-DURABLE-01] Durabilidade da avaliação KNIGHT em PostgreSQL 18 REAL (gate
/// <c>AEGIS_TEST_PG</c>, banco descartável do <see cref="PostgresProbe"/>).
///
/// Por que existe, além da suíte SQLite: o defeito do ambiente real aconteceu em PostgreSQL, sobre uma
/// TRANSAÇÃO real — a que fixa a aquisição do ADM citada pela execução. A correção passou a fechar a
/// avaliação DENTRO dessa mesma escrita, e é aqui que se prova que o COMMIT leva a linha já concluída, em
/// vez de deixá-la em <c>Running</c> à espera de uma segunda gravação que o cancelamento nunca fará.
/// Também é aqui que a nova leitura da "última avaliação" é exercida no provedor de produção, com as três
/// execuções abandonadas reproduzidas na mesma forma que têm no banco real.
///
/// LIMITES declarados: o pipeline HTTP e a autenticação JWT não são executados (a leitura sai pela ação do
/// controller com dependências reais); nenhuma chamada externa acontece — a fonte é o coletor de
/// DEMONSTRAÇÃO (sintético, <c>demo.example.com</c>) e o PostgreSQL é o descartável, nunca o corporativo.
/// Nenhuma execução real do ambiente de homologação é lida, reparada ou tocada por este teste.
/// </summary>
public sealed class KnightDurableAssessmentPostgresTests
{
    private readonly ITestOutputHelper _output;

    public KnightDurableAssessmentPostgresTests(ITestOutputHelper output) => _output = output;

    // ---- (1) A avaliação é durável ANTES da IA, em PostgreSQL real ---------------------------------

    /// <summary>
    /// Reprodução do caso do HOMOLOG-01 no provedor de produção: a IA cancela o chamador no meio da
    /// narrativa. A linha precisa estar <c>Completed</c>, com <c>CompletedAt</c>, narrativa determinística e
    /// os indicadores todos — e precisa ser LEGÍVEL pela porta por Id, com o estado real.
    /// </summary>
    [Fact]
    public async Task Cancelamento_DuranteANarrativa_ConcluiEPersisteEmPostgres()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrarESemearTenantAsync(opt, tenant, "kd");

        using var cts = new CancellationTokenSource();

        Guid runId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runId = (await ServiceFor(db, tenant, new IaQueCancelaOChamador(cts))
                .RunDemoAssessmentAsync(cts.Token)).Id;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var run = await db.KnightAssessmentRuns.AsNoTracking()
                .Include(r => r.Indicators).SingleAsync(r => r.Id == runId);

            run.Status.Should().Be(KnightRunStatus.Completed,
                "a escrita que commitou já levava a avaliação concluída");
            run.CompletedAt.Should().NotBeNull();
            run.AdvisoryJson.Should().NotBeNullOrWhiteSpace("a narrativa determinística entra junto");
            run.AdvisoryFromAi.Should().BeFalse();
            run.Score.Should().Be(23d);
            run.Indicators.Should().HaveCount(5);

            // Os objetos afetados foram congelados na MESMA escrita — nada ficou pela metade.
            (await db.KnightAffectedObjects.AsNoTracking().CountAsync(o => o.RunId == runId))
                .Should().BeGreaterThan(0);

            var controller = new KnightAssessmentsController(
                ServiceFor(db, tenant, new IaQueCancelaOChamador(cts)), new SystemTenantContext(tenant));
            var porId = await controller.GetById(runId, default);
            porId.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightAssessmentDto>().Which
                .Status.Should().Be(nameof(KnightRunStatus.Completed));
        }

        _output.WriteLine($"KNIGHT durável sob cancelamento da IA em PostgreSQL real (run {runId}).");
    }

    // ---- (2) A transação da citação do ADM continua íntegra ----------------------------------------

    /// <summary>
    /// O caminho do Entra ID grava a execução e FIXA a aquisição do ADM na mesma transação. Fechar a
    /// avaliação antes da IA não pode afrouxar isso: depois do commit a linha está concluída E aponta para
    /// a aquisição que a sustenta, mesmo com a narrativa cancelada.
    /// </summary>
    [Fact]
    public async Task CitacaoDaAquisicaoAdm_SobreviveAoCancelamentoDaNarrativa()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        await MigrarESemearTenantAsync(opt, tenant, "ka");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            db.Connectors.Add(new ConnectorConfig
            {
                TenantId = tenant,
                Provider = ConnectorProvider.Microsoft,
                Capability = ConnectorCapability.IdentityPosture,
                DisplayName = "Microsoft Entra ID · AEGIS KNIGHT",
                Enabled = true,
                EncryptedSettings = "{\"clientSecret\":\"segredo-sintetico\"}",
            });
            await db.SaveChangesAsync();
        }

        using var cts = new CancellationTokenSource();

        Guid runId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            runId = (await ServiceFor(db, tenant, new IaQueCancelaOChamador(cts), entra: true)
                .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId, cts.Token)).Id;

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var run = await db.KnightAssessmentRuns.AsNoTracking().SingleAsync(r => r.Id == runId);

            run.Status.Should().Be(KnightRunStatus.Completed);
            run.CompletedAt.Should().NotBeNull();
            run.IdentityAcquisitionId.Should().NotBeNull("a procedência da evidência foi commitada junto");

            (await db.IdentityAcquisitions.AsNoTracking()
                .AnyAsync(a => a.Id == run.IdentityAcquisitionId))
                .Should().BeTrue("a aquisição citada existe de fato no banco");
        }

        _output.WriteLine($"Citação do ADM preservada sob cancelamento da IA em PostgreSQL real (run {runId}).");
    }

    // ---- (3) Leitura da última avaliação, no provedor de produção ----------------------------------

    /// <summary>
    /// Reproduz o que o inventário encontrou no ambiente real: execuções concluídas e, DEPOIS delas,
    /// execuções abandonadas em <c>Running</c> sem <c>CompletedAt</c>. A leitura devolve o último resultado
    /// concluído e DECLARA a tentativa — sem reparar, fechar ou apagar nada.
    /// </summary>
    [Fact]
    public async Task UltimaAvaliacao_ComExecucoesAbandonadas_DevolveAConcluidaEDeclaraATentativa()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();
        var outro = Guid.NewGuid();
        await MigrarESemearTenantAsync(opt, tenant, "kl");
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            db.Tenants.Add(new Tenant { Id = outro, Name = "Outro", Slug = "ko-" + outro.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        Guid concluida;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            concluida = (await ServiceFor(db, tenant, new SemIa()).RunDemoAssessmentAsync()).Id;

        // As TRÊS abandonadas, na mesma forma das encontradas no banco real — e uma quarta, de OUTRO tenant.
        var abandonadas = new List<Guid>();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            foreach (var minutos in new[] { 5, 10, 15 })
                abandonadas.Add(await SemearAbandonadaAsync(db, tenant, DateTimeOffset.UtcNow.AddMinutes(minutos)));
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(outro)))
            await SemearAbandonadaAsync(db, outro, DateTimeOffset.UtcNow.AddMinutes(30));

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var controller = new KnightAssessmentsController(
                ServiceFor(db, tenant, new SemIa()), new SystemTenantContext(tenant));

            var body = (await controller.GetLatestState(default)).Result
                .Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightLatestDto>().Which;

            body.Assessment.Should().NotBeNull();
            body.Assessment!.Id.Should().Be(concluida, "o resultado exibido é o último CONCLUÍDO");
            body.Assessment.Status.Should().Be(nameof(KnightRunStatus.Completed));

            body.UnfinishedAttempt.Should().NotBeNull("a tentativa mais recente não pode sumir em silêncio");
            body.UnfinishedAttempt!.Id.Should().Be(abandonadas.Last(), "a mais recente das três");
            body.UnfinishedAttempt.Status.Should().Be(nameof(KnightRunStatus.Running));

            // Nada foi reparado: as três continuam exatamente como estavam.
            var aindaRunning = await db.KnightAssessmentRuns.AsNoTracking()
                .CountAsync(r => r.Status == KnightRunStatus.Running && r.CompletedAt == null);
            aindaRunning.Should().Be(3, "a leitura não fecha, não conserta e não apaga execução alguma");

            // E o acesso por Id continua alcançando uma delas, com o estado real.
            var porId = await controller.GetById(abandonadas.Last(), default);
            porId.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightAssessmentDto>().Which
                .Status.Should().Be(nameof(KnightRunStatus.Running));
        }

        _output.WriteLine("Leitura da última avaliação valida em PostgreSQL real, sem tocar nas abandonadas.");
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static async Task MigrarESemearTenantAsync(DbContextOptions<AegisScoreDbContext> opt, Guid tenant, string prefixo)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        await db.Database.MigrateAsync();
        db.Tenants.Add(new Tenant
        {
            Id = tenant,
            Name = "Cliente KNIGHT",
            Slug = $"{prefixo}-" + tenant.ToString("N"),
            Status = TenantStatus.Active,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Uma execução ABANDONADA, na forma exata das encontradas no ambiente real.</summary>
    private static async Task<Guid> SemearAbandonadaAsync(AegisScoreDbContext db, Guid tenant, DateTimeOffset iniciadaEm)
    {
        var run = new KnightAssessmentRun
        {
            TenantId = tenant,
            Mode = KnightAssessmentMode.Live,
            SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed,
            Source = "Microsoft Entra ID",
            Status = KnightRunStatus.Running,
            CatalogVersion = KnightCatalog.Version,
            ScoreFormulaVersion = "teste",
            StartedAt = iniciadaEm,
            Coverage = 0,
        };
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static IAegisKnightAssessmentService ServiceFor(
        AegisScoreDbContext db, Guid tenantId, ILLMClient ia, bool entra = false)
    {
        var coletores = entra
            ? new IKnightCollector[] { new DemoKnightCollector(), new ColetorEntraSintetico() }
            : new IKnightCollector[] { new DemoKnightCollector() };

        var registry = new KnightCollectorRegistry(coletores);
        var tenant = new SystemTenantContext(tenantId);
        var config = new ConfigSintetica();
        var aquisicoes = new IdentityAcquisitionStore(db, tenant, TimeProvider.System);
        var evidence = new IdentityEvidenceService(db, registry, config, aquisicoes, tenant);
        return new AegisKnightAssessmentService(
            db, registry, config, new KnightAdvisoryGenerator(ia), evidence, aquisicoes, tenant);
    }

    private sealed class ConfigSintetica : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(source switch
            {
                KnightSourceType.MicrosoftEntraId =>
                    new KnightEntraIdConfiguration("00000000-0000-0000-0000-00000000dead", "client", "segredo-sintetico"),
                KnightSourceType.Demo => new KnightDemoConfiguration(),
                _ => new KnightSourceNotConfigured(source),
            });

        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    /// <summary>Coletor sintético do Entra ID — nenhuma chamada ao Microsoft Graph.</summary>
    private sealed class ColetorEntraSintetico : IKnightCollector
    {
        public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

        public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default) =>
            Task.FromResult(new KnightCollectionResult(
                KnightSourceType.MicrosoftEntraId,
                KnightSourceState.Completed,
                "Microsoft Entra ID (sintético)",
                new KnightFactSet(new[]
                {
                    KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 3),
                    KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 1),
                    KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 0),
                }),
                new[] { new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected) },
                DateTimeOffset.UtcNow));
    }

    /// <summary>O chamador desiste EXATAMENTE durante a narrativa — sem espera, de forma determinística.</summary>
    private sealed class IaQueCancelaOChamador : ILLMClient
    {
        private readonly CancellationTokenSource _cts;
        public IaQueCancelaOChamador(CancellationTokenSource cts) => _cts = cts;

        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            _cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("");
        }
    }

    /// <summary>IA indisponível — nenhuma rede sai deste teste.</summary>
    private sealed class SemIa : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new InvalidOperationException("IA indisponível (teste).");
    }
}
