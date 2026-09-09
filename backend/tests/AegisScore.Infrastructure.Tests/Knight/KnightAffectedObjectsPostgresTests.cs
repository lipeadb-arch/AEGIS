using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Queries;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Queries;
using AegisScore.Infrastructure.Tests.Documents;   // PostgresProbe
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-MVP-PRODUCT-02] Validação INTEGRADA dos objetos afetados em PostgreSQL 18 REAL (gate
/// <c>AEGIS_TEST_PG</c>, banco descartável do <see cref="PostgresProbe"/>).
///
/// Por que ela existe: a suíte SQLite prova a SEMÂNTICA (contagem × lista, histórico, parcialidade, busca),
/// mas não prova que a MIGRATION aplica, que a FK composta tenant-safe existe de fato no banco, nem que a
/// paginação/busca traduzem no provedor de produção. Aqui a migration roda de verdade, a persistência acontece
/// em PostgreSQL e a leitura sai pela ação do controller com as dependências reais.
///
/// LIMITE declarado: como no Dia 1, o alvo é a ação do controller com dependências reais — o repositório não
/// tem harness de integração HTTP (<c>WebApplicationFactory</c>), então o pipeline HTTP e a autenticação JWT
/// da rota NÃO são executados. Nenhuma chamada externa: a fonte é o coletor de DEMONSTRAÇÃO (sintético,
/// <c>demo.example.com</c>), e o PostgreSQL é o descartável do CI — nunca o corporativo.
/// </summary>
public sealed class KnightAffectedObjectsPostgresTests
{
    private readonly ITestOutputHelper _output;

    public KnightAffectedObjectsPostgresTests(ITestOutputHelper output) => _output = output;

    // ---- (1) Migration + persistência + leitura pelo controller ------------------------------------

    [Fact]
    public async Task MigrationAplica_EOsAfetadosPersistemELeemPeloController_ComContagemCoerente()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;   // AEGIS_TEST_PG não definido — pulado honestamente
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            // A migration deste pacote aplica sobre o histórico inteiro — é o que prova que as 3 colunas
            // aditivas e a tabela nova convivem com o schema existente.
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente KNIGHT", Slug = "kn-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        Guid runId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var assessment = await ServiceFor(db, tenant).RunDemoAssessmentAsync();
            runId = assessment.Id;

            var privileged = assessment.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-002");
            privileged.AffectedObjectCount.Should().Be(12);
            privileged.HasAffectedDetail.Should().BeTrue();
            privileged.AffectedDetailComplete.Should().BeTrue();

            // Persistiu de fato em PostgreSQL, com o vínculo denormalizado da execução.
            var rows = await db.KnightAffectedObjects.AsNoTracking()
                .Where(o => o.RunId == runId && o.IndicatorId == "AK-ENTRA-002").CountAsync();
            rows.Should().Be(12, "a lista persistida é o MESMO conjunto que produziu a contagem");
        }

        // ---- Leitura pela ação do controller, com as dependências reais -----------------------------
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var controller = new KnightAssessmentsController(ServiceFor(db, tenant), new SystemTenantContext(tenant));

            var action = await controller.GetAffected(runId, "AK-ENTRA-002", page: 1, pageSize: 5, search: null);
            var body = action.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightAffectedObjectsDto>().Which;

            body.State.Should().Be(nameof(KnightAffectedDetailState.Available));
            body.AffectedObjectCount.Should().Be(12);
            body.TotalPreserved.Should().Be(12);
            body.Items.Should().HaveCount(5, "a paginação acontece NO BANCO, não no navegador");

            // Tipo explícito e nome ausente são propriedades do CONJUNTO — a primeira página de 5 não é o
            // lugar de procurá-las (a ordenação é por nome, e os dois casos ficam fora dela).
            var completa = await controller.GetAffected(runId, "AK-ENTRA-002", 1, 100, null);
            var todos = completa.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightAffectedObjectsDto>().Which;
            todos.Items.Should().HaveCount(12);
            todos.Items.Should().Contain(i => i.Kind == nameof(KnightAffectedObjectKind.ServicePrincipal),
                "um membro de papel privilegiado pode ser uma APLICAÇÃO — o tipo viaja explícito");
            todos.Items.Should().Contain(i => i.DisplayName == null,
                "nome ausente na fonte permanece ausente — a tela mostra o identificador, não um rótulo inventado");

            // Busca no servidor, traduzida pelo Npgsql (ILIKE/LIKE) — não um filtro em memória.
            var busca = await controller.GetAffected(runId, "AK-ENTRA-002", 1, 50, "Ana");
            var achado = busca.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightAffectedObjectsDto>().Which;
            achado.MatchCount.Should().Be(1);
            achado.TotalPreserved.Should().Be(12, "o total do conjunto continua visível durante a busca");

            // Achado fora do escopo de detalhe não inventa lista.
            var fora = await controller.GetAffected(runId, "AK-ENTRA-005", 1, 50, null);
            fora.Result.Should().BeOfType<OkObjectResult>().Which.Value
                .Should().BeOfType<KnightAffectedObjectsDto>().Which
                .State.Should().Be(nameof(KnightAffectedDetailState.OutOfScope));

            // Achado inexistente → 404 (nunca uma lista vazia disfarçada de resposta).
            var inexistente = await controller.GetAffected(runId, "AK-NAO-EXISTE", 1, 50, null);
            inexistente.Result.Should().BeOfType<NotFoundResult>();
        }

        _output.WriteLine($"KNIGHT afetados persistidos e lidos em PostgreSQL real (run {runId}).");
    }

    // ---- (2) Isolamento por tenant, garantido pelo banco -------------------------------------------

    [Fact]
    public async Task DetalheDeOutroTenant_NaoAtravessa_ENemPelaFkNemPelaLeitura()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenantA, Name = "Cliente A", Slug = "a-" + tenantA.ToString("N"), Status = TenantStatus.Active });
            db.Tenants.Add(new Tenant { Id = tenantB, Name = "Cliente B", Slug = "b-" + tenantB.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        Guid runA;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
            runA = (await ServiceFor(db, tenantA).RunDemoAssessmentAsync()).Id;

        // O tenant B não enxerga a avaliação de A — indistinguível de inexistente.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
        {
            var controller = new KnightAssessmentsController(ServiceFor(db, tenantB), new SystemTenantContext(tenantB));
            var action = await controller.GetAffected(runA, "AK-ENTRA-002", 1, 50, null);
            action.Result.Should().BeOfType<NotFoundResult>();
        }

        // DUAS defesas, provadas separadamente. A primeira é da APLICAÇÃO: o guard de escrita do DbContext
        // recusa a gravação multi-tenant antes mesmo de chegar ao banco.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var indicatorA = await db.KnightIndicatorResults.IgnoreQueryFilters()
                .FirstAsync(i => i.RunId == runA && i.IndicatorId == "AK-ENTRA-002");

            db.KnightAffectedObjects.Add(new KnightAffectedObject
            {
                TenantId = tenantB,                    // tenant DIVERGENTE do indicador (que é do tenant A)
                RunId = runA,
                IndicatorResultId = indicatorA.Id,
                IndicatorId = "AK-ENTRA-002",
                ExternalId = "intruso-01",
                Kind = KnightAffectedObjectKind.User,
            });

            var gravar = async () => await db.SaveChangesAsync();
            await gravar.Should().ThrowAsync<TenantSecurityException>(
                "a escrita cross-tenant é recusada fail-closed antes de tocar o banco");
        }

        // A segunda é do BANCO. Um INSERT CRU não passa pelo guard da aplicação — é exatamente por isso que a
        // FK composta (IndicatorResultId, TenantId) existe: sem ela, um caminho que contornasse o DbContext
        // criaria uma linha de tenant divergente que o query filter apenas ESCONDERIA, sem impedir.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var indicatorA = await db.KnightIndicatorResults.IgnoreQueryFilters()
                .FirstAsync(i => i.RunId == runA && i.IndicatorId == "AK-ENTRA-002");

            var sql = @"INSERT INTO ""KnightAffectedObjects""
                        (""Id"", ""TenantId"", ""RunId"", ""IndicatorResultId"", ""IndicatorId"", ""ExternalId"",
                         ""Kind"", ""Roles"", ""CreatedAt"")
                        VALUES ({0}, {1}, {2}, {3}, 'AK-ENTRA-002', 'intruso-02', 0, '[]'::jsonb, now())";

            var inserirCru = async () => await db.Database.ExecuteSqlRawAsync(
                sql, Guid.NewGuid(), tenantB, runA, indicatorA.Id);

            // SQL cru sobe a exceção do provedor diretamente (não há SaveChanges para envolvê-la em
            // DbUpdateException) — o que importa é o CÓDIGO do erro: violação de chave estrangeira.
            (await inserirCru.Should().ThrowAsync<PostgresException>(
                "o próprio banco precisa recusar um afetado de tenant divergente"))
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }
    }

    // ---- (3) KNIGHT → Central de Prioridades: o mesmo resultado, sem recontagem --------------------

    [Fact]
    public async Task CentralDePrioridades_ApontaParaAMesmaAvaliacao_ComOsValoresDaAutoridadeKnight()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) return;
        var opt = pg.DbOptions();

        var tenant = Guid.NewGuid();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Cliente KNIGHT", Slug = "kp-" + tenant.ToString("N"), Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }

        KnightAssessment assessment;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            assessment = await ServiceFor(db, tenant).RunDemoAssessmentAsync();

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var tenantCtx = new SystemTenantContext(tenant);
            var priorities = new PriorityWorkspaceQuery(
                new WorkspacePostureQuery(db, tenantCtx),
                new PostureExposureQuery(db, tenantCtx, StaticExposureLanguageCatalog.Empty),
                new VulnerabilityQuery(db, tenantCtx),
                ServiceFor(db, tenant),
                TimeProvider.System);

            var workspace = await priorities.GetAsync();
            var fila = workspace.IdentityFindings;

            fila.RunId.Should().Be(assessment.Id,
                "a Central e a tela do KNIGHT apontam para a MESMA avaliação — divergir aqui seria duas verdades");
            fila.IsDemo.Should().BeTrue("origem demonstrativa é sempre identificada, nunca misturada com corporativa");
            fila.Score.Should().Be(assessment.Score, "o score KNIGHT vem VERBATIM da autoridade");
            fila.ExposedCount.Should().Be(assessment.ExposedCount);
            fila.NotEvaluatedCount.Should().Be(assessment.NotEvaluatedCount,
                "cobertura incompleta é informação que viaja, não silêncio");

            fila.Top.Should().NotBeEmpty();
            fila.Top.Should().OnlyContain(f => f.Status == nameof(KnightIndicatorStatus.Exposed));
            fila.Top.First().Severity.Should().Be(nameof(SeverityLevel.Critical),
                "a ordem de exibição é severidade — a régua é a do produto, não um score novo");

            foreach (var f in fila.Top)
            {
                var origem = assessment.Indicators.Single(i => i.IndicatorId == f.IndicatorId);
                f.AffectedObjectCount.Should().Be(origem.AffectedObjectCount, "nada é recontado na Central");
                f.Evidence.Should().Be(origem.Evidence);
                f.HasAffectedDetail.Should().Be(origem.HasAffectedDetail);
            }

            workspace.ReadModelVersion.Should().Be("priority-workspace-v3");
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private static IAegisKnightAssessmentService ServiceFor(AegisScoreDbContext db, Guid tenantId)
    {
        var registry = new KnightCollectorRegistry(new[] { new DemoKnightCollector() });
        var tenant = new SystemTenantContext(tenantId);
        var config = new DemoOnlyConfigProvider();
        var evidence = new IdentityEvidenceService(db, registry, config, new AegisScore.Infrastructure.Identity.IdentityAcquisitionStore(db, tenant), tenant);
        return new AegisKnightAssessmentService(db, registry, config, new NoAdvisoryGenerator(), evidence, tenant);
    }

    private sealed class DemoOnlyConfigProvider : IKnightSourceConfigurationProvider
    {
        public Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default) =>
            Task.FromResult<KnightSourceConfiguration>(new KnightDemoConfiguration());
        public Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnightSourceAvailability>>(Array.Empty<KnightSourceAvailability>());
    }

    private sealed class NoAdvisoryGenerator : IKnightAdvisoryGenerator
    {
        public Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default) =>
            Task.FromResult(new KnightAdvisoryResult(KnightAdvisoryFallback.Build(input), FromAi: false));
    }
}
