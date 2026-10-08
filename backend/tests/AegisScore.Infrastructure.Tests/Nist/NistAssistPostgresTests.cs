using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture.Export;
using AegisScore.Application.Remediation;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Nist;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture.Export;
using AegisScore.Infrastructure.Remediation;
using AegisScore.Infrastructure.Tests.Documents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Nist;

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] A assistência de IA em PostgreSQL REAL (motor simulado — nenhuma chamada externa):
///   • a migration cria gerações, incorporações e resumo executivo com FK tenant-safe (o banco recusa a rodada ou a geração de
///     outro tenant) e um único resumo por rodada e escopo — duas aceitações simultâneas: uma vence, a outra é conflito;
///   • duas incorporações simultâneas da mesma sugestão sobre a mesma versão-base: uma grava, a outra é conflito, e a
///     procedência só existe para a que gravou;
///   • a jornada gerar → incorporar → aceitar resumo → publicar → exportar roda inteira no PostgreSQL, com a montagem do
///     relatório na leitura consistente e a reexportação idêntica depois de o presente mudar.
/// </summary>
public sealed class NistAssistPostgresTests
{
    private const string Migration = "NistAi01_ContextualAssistance";
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly RemediationActor Gestora = new(Guid.NewGuid(), "Gestora Demo");
    private static readonly RemediationActor Analista = new(Guid.NewGuid(), "Analista Demo");

    private readonly ITestOutputHelper _output;
    public NistAssistPostgresTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Migracao_FkTenantSafe_EUmResumoPorRodadaEEscopo_SobAceitacaoSimultanea()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var (tenantA, tenantB) = (Guid.NewGuid(), Guid.NewGuid());
        await SeedAsync(opt, tenantA, tenantB);

        Guid a, c, s, cycleB;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(m => m.EndsWith(Migration, StringComparison.Ordinal));
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
        {
            var created = await Services(db, tenantA).Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação", null, null, null, "Matriz", null), Gestora);
            (a, c, s) = (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantB)))
            cycleB = (await Services(db, tenantB).Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação B", null, null, null, "Única", null), Gestora)).Cycles!.Single().Id;

        Guid generationA;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
            generationA = (await Services(db, tenantA).Assist.AssistSubcategoryAsync(a, c, s, "GV.OC-01", new NistAssistRequest(), Gestora)).Id;

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        // Geração do tenant A apontando para a rodada do tenant B: o banco recusa (FK composta).
        await using (var cross = new NpgsqlCommand(
            """
            INSERT INTO "NistAiAssistances" ("Id","TenantId","AssessmentId","CycleId","AssessmentScopeId","Kind","ContextFingerprint","ContextSummary",
                                             "SourcesJson","OutputJson","Mode","Availability","MethodologyVersion","StaleOnArrival","GeneratedAt","CreatedAt")
            VALUES (@id, @tenant, @a, @cycle, @s, 0, 'x', 'x', '[]', '{}', 0, 'Simulated', 'v', false, now(), now())
            """, conn))
        {
            cross.Parameters.AddWithValue("id", Guid.NewGuid());
            cross.Parameters.AddWithValue("tenant", tenantA);
            cross.Parameters.AddWithValue("a", a);
            cross.Parameters.AddWithValue("cycle", cycleB);
            cross.Parameters.AddWithValue("s", s);
            var act = () => cross.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503", "rodada de outro tenant viola a FK composta");
        }
        // Incorporação do tenant B apontando para a geração do tenant A: também recusada.
        await using (var cross = new NpgsqlCommand(
            """
            INSERT INTO "NistAiIncorporations" ("Id","TenantId","AssessmentId","CycleId","AssessmentScopeId","AssistanceId","TargetKind","TargetId",
                                                "FieldsJson","StaleAcknowledged","IncorporatedAt","CreatedAt")
            VALUES (@id, @tenant, @a, @c, @s, @g, 0, @t, '[]', false, now(), now())
            """, conn))
        {
            cross.Parameters.AddWithValue("id", Guid.NewGuid());
            cross.Parameters.AddWithValue("tenant", tenantB);
            cross.Parameters.AddWithValue("a", a);
            cross.Parameters.AddWithValue("c", c);
            cross.Parameters.AddWithValue("s", s);
            cross.Parameters.AddWithValue("g", generationA);
            cross.Parameters.AddWithValue("t", Guid.NewGuid());
            var act = () => cross.ExecuteNonQueryAsync();
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503", "geração de outro tenant viola a FK composta");
        }

        // Duas pessoas aceitam o primeiro resumo da rodada ao mesmo tempo: uma vence, a outra recebe conflito.
        using var start = new SemaphoreSlim(0, 2);
        async Task<string> Accept(string text)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA));
            await start.WaitAsync();
            try
            {
                await Services(db, tenantA).Assist.SaveExecutiveSummaryAsync(a, c, s,
                    new SaveNistExecutiveSummaryCommand(new[] { new NistExecutiveSectionInput("nextSteps", text) }, null, 0), Gestora);
                return "ok";
            }
            catch (NistAssessmentConflictException)
            {
                return "conflict";
            }
        }
        var both = Task.WhenAll(Accept("Texto da primeira pessoa."), Accept("Texto da segunda pessoa."));
        start.Release(2);
        (await both).Should().BeEquivalentTo(new[] { "ok", "conflict" });
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenantA)))
            (await db.NistExecutiveSummaries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IncorporacoesSimultaneas_DaMesmaSugestao_UmaGravaEAOutraEhConflito()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        await SeedAsync(opt, tenant);

        Guid a, c, s;
        NistAssistView view;
        int version;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var x = Services(db, tenant);
            var created = await x.Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação", null, null, null, "Matriz", null), Gestora);
            (a, c, s) = (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
            version = (await x.Nist.SaveEvaluationAsync(a, c, s, "GV.PO-01", new SaveNistEvaluationCommand(2, 4, false, null, null, "Política existente.", null,
                null, null, null, 0), Gestora)).Evaluation!.Version;
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            view = await Services(db, tenant).Assist.AssistSubcategoryAsync(a, c, s, "GV.PO-01", new NistAssistRequest(), Gestora);
        view.Applicable.Should().ContainKey("improvementGuidance");

        using var start = new SemaphoreSlim(0, 2);
        async Task<string> Incorporate(RemediationActor actor, string suffix)
        {
            await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant));
            await start.WaitAsync();
            try
            {
                await Services(db, tenant).Nist.SaveEvaluationAsync(a, c, s, "GV.PO-01", new SaveNistEvaluationCommand(2, 4, false, null, null,
                    "Política existente.", null, null, view.Applicable["improvementGuidance"] + suffix, null, version,
                    Assistance: new NistAssistanceReference(view.Id, new[] { "improvementGuidance" })), actor);
                return "ok";
            }
            catch (NistAssessmentConflictException)
            {
                return "conflict";
            }
        }
        var both = Task.WhenAll(Incorporate(Gestora, ""), Incorporate(Analista, " (versão da analista)"));
        start.Release(2);
        (await both).Should().BeEquivalentTo(new[] { "ok", "conflict" });

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.NistAiIncorporations.CountAsync()).Should().Be(1, "só a gravação vencedora tem procedência");
            var e = (await Services(db, tenant).Nist.GetSubcategoryAsync(a, c, s, "GV.PO-01")).Evaluation!;
            e.Version.Should().Be(version + 1);
            e.AssistedFields!.Single().Field.Should().Be("improvementGuidance");
        }
    }

    [Fact]
    public async Task JornadaAssistida_EmPostgres_GeraIncorporaAceitaPublicaEReexportaIgual()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        var tenant = Guid.NewGuid();
        await SeedAsync(opt, tenant);

        Guid a, c, s, snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var x = Services(db, tenant);
            var created = await x.Nist.CreateAsync(new CreateNistAssessmentCommand("Avaliação", null, null, null, "Matriz", null), Gestora);
            (a, c, s) = (created.Id, created.Cycles!.Single().Id, created.Scopes.Single().Id);
            var saved = await x.Nist.SaveEvaluationAsync(a, c, s, "RC.RP-01", new SaveNistEvaluationCommand(2, 4, false, null, null, "Backup diário.",
                "Restauração nunca testada.", null, null, null, 0), Gestora);
            var p = await x.Work.AddProcedureAsync(a, c, s, "RC.RP-01", new AddNistProcedureCommand("Test", "Restaurar uma amostra do backup."), Gestora);
            await x.Work.UpdateProcedureAsync(a, c, s, "RC.RP-01", p.Id, new UpdateNistProcedureCommand(null, "Performed", "PartiallySatisfactory",
                "A restauração levou mais que o previsto.", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), null, p.Version), Gestora);
            var view = await x.Assist.AssistSubcategoryAsync(a, c, s, "RC.RP-01", new NistAssistRequest(), Gestora);
            view.Mode.Should().Be("Simulated");
            view.Level!.Level.Should().Be(2, "regra fixa e declarada do motor simulado sobre o procedimento parcialmente satisfatório");
            await x.Nist.SaveEvaluationAsync(a, c, s, "RC.RP-01", new SaveNistEvaluationCommand(2, 4, false, null, null, view.Applicable["rationale"],
                "Restauração nunca testada.", null, null, null, saved.Evaluation!.Version,
                Assistance: new NistAssistanceReference(view.Id, new[] { "rationale" })), Gestora);
            var exec = await x.Assist.AssistExecutiveAsync(a, c, s, new NistAssistRequest(), Gestora);
            await x.Assist.SaveExecutiveSummaryAsync(a, c, s, new SaveNistExecutiveSummaryCommand(
                exec.Applicable.Select(kv => new NistExecutiveSectionInput(kv.Key, kv.Value)).ToList(), exec.Id, 0), Gestora);
            var preview = await x.Publication.PreviewAsync(a, c, s);
            snapshotId = (await x.Publication.PublishAsync(a, c, s, new PublishNistCommand(preview.ContentFingerprint), Gestora)).SnapshotId;
        }

        byte[] html, csv;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var exporter = new PostureSnapshotExporter(db);
            html = (await exporter.ExportAsync(snapshotId, PostureExportFormat.Html))!.Content;
            csv = (await exporter.ExportAsync(snapshotId, PostureExportFormat.Csv))!.Content;
            System.Text.Encoding.UTF8.GetString(html).Should().Contain("Interpretação executiva").And.Contain("Conteúdo assistido por IA");
            await Services(db, tenant).Assist.WithdrawExecutiveSummaryAsync(a, c, s, 1, Gestora);
        }
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var exporter = new PostureSnapshotExporter(db);
            (await exporter.ExportAsync(snapshotId, PostureExportFormat.Html))!.Content.Should().Equal(html, "reexportar não lê o presente");
            (await exporter.ExportAsync(snapshotId, PostureExportFormat.Csv))!.Content.Should().Equal(csv);
            (await Services(db, tenant).Nist.AuditAsync(a, c, s, null)).Should()
                .Contain(t => t.Subject == "ExecutiveSummary" && t.Action == "Withdrawn")
                .And.Contain(t => t.Subject == "Assistance" && t.Action == "Incorporated");
        }
    }

    private static async Task SeedAsync(DbContextOptions<AegisScoreDbContext> opt, params Guid[] tenants)
    {
        await using var db = new AegisScoreDbContext(opt, new SystemTenantContext(null));
        await db.Database.MigrateAsync();
        await FrameworkSeeder.SeedAsync(db, Path.Combine(DataDir, "nist_csf_2_0_catalog.json"), Path.Combine(DataDir, "aegis_methodology.json"));
        foreach (var t in tenants)
            db.Tenants.Add(new Tenant { Id = t, Name = "Cliente Sintético", Slug = $"t-{t:N}", Status = TenantStatus.Active });
        await db.SaveChangesAsync();
    }

    private static (NistAssessmentService Nist, NistWorkService Work, NistPublicationService Publication, NistAssistService Assist) Services(AegisScoreDbContext db, Guid tenant)
    {
        var ctx = new SystemTenantContext(tenant);
        var remediation = new RemediationService(db, ctx, TimeProvider.System,
            new AegisScore.Infrastructure.Queries.DevicePriorityQuery(db, TimeProvider.System,
                Microsoft.Extensions.Options.Options.Create(new AegisScore.Application.Queries.CrossSourceCorrelationOptions())));
        var publication = new NistPublicationService(db, ctx, TimeProvider.System);
        var gate = new SimulatedGate();
        var resolver = new NoSlug();
        var router = new TenantScopedAssessmentRouter(
            new AegisAssessmentService(new NoProvider(), StaticAuditorPersonaProvider.Neutral, gate), new StubAssessmentService(), gate, resolver);
        return (new NistAssessmentService(db, ctx, TimeProvider.System), new NistWorkService(db, ctx, TimeProvider.System, remediation), publication,
            new NistAssistService(db, ctx, TimeProvider.System, router, gate, resolver, publication, new NistAssistInFlight()));
    }

    private sealed class SimulatedGate : IAiFreeTierGate
    {
        public AiMode Mode => AiMode.Simulated;
        public bool ProviderConfigured => false;
        public bool IsExternalAllowedForSlug(string? tenantSlug) => false;
    }

    private sealed class NoSlug : IAiTenantResolver
    {
        public void OverrideTenant(Guid tenantId) { }
        public Task<string?> GetCurrentSlugAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class NoProvider : ILLMClient
    {
        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new InvalidOperationException("Nenhum provedor externo é chamado nestes testes.");
    }
}
