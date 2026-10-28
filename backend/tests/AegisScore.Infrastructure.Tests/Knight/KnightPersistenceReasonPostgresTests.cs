using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api;
using AegisScore.Api.Workers;
using AegisScore.Application.Abstractions;
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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-PERSISTENCE-01] Falha real de produção (27/10/2026): a avaliação KNIGHT não gravava — PostgreSQL
/// SQLSTATE 22001, "value too long for type character varying(500)" no SaveChanges de RunCoreAsync — e o worker mantinha
/// a avaliação anterior. O motivo de "não avaliado" é montado a partir de diagnóstico COLETADO e não tem tamanho fixo; a
/// coluna tinha. O SQLite ignora o limite, por isso a prova é aqui, no schema MIGRADO do PostgreSQL real, pelo fluxo
/// inteiro: coletor → ADM → avaliação → gravação → releitura → fotografia → relatório HTML.
/// </summary>
public sealed class KnightPersistenceReasonPostgresTests
{
    private readonly ITestOutputHelper _output;
    public KnightPersistenceReasonPostgresTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// SMTP AUTH desabilitado na organização e Get-CASMailbox recusado: a leitura disponível sustenta os próprios
    /// controles, a recusada produz "não avaliado" com o motivo INTEGRAL, e a avaliação parcial é gravada, relida e
    /// publicada com o mesmo texto — sem corte, sem mudar status, nota ou denominadores.
    /// </summary>
    [Fact]
    public async Task SmtpAuthDesabilitado_GetCasMailboxRecusado_GravaReleEPublicaOMotivoIntegral()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();
        var tenant = await SeedAsync(opt);

        KnightAssessment run;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            try
            {
                run = await KnightExchangeConfigurationFlowTests
                    .ServiceFor(db, tenant, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.CasMailboxesDenied))
                    .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline);
            }
            catch (Exception ex)
            {
                // Diagnóstico da reprodução: entidade, propriedade, indicador e TAMANHO — nunca o texto.
                _output.WriteLine($"FALHA NA GRAVAÇÃO: {ex.GetType().Name} → {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
                foreach (var linha in Oversized(db)) _output.WriteLine("  " + linha);
                throw;
            }
        }

        run.SourceState.Should().Be(KnightSourceState.PartialCollection, "uma leitura foi recusada; as demais concluíram");
        var smtp = run.Indicators.Single(i => i.IndicatorId == "AK-EXO-016");
        smtp.Status.Should().Be(KnightIndicatorStatus.NotEvaluated, "sem as substituições por caixa, o valor da organização não decide");
        var motivo = smtp.NotEvaluatedReason!;
        _output.WriteLine($"AK-EXO-016: motivo com {motivo.Length} caracteres");
        motivo.Length.Should().BeGreaterThan(500, "é exatamente a combinação que excedia a coluna antiga");
        motivo.Should().Contain("SUBSTITUIÇÕES").And.EndWith("não o ambiente.", "o motivo é preservado inteiro, sem corte");

        // A leitura recusada só derruba o que dependia dela: o que a organização sustenta continua avaliado.
        run.Indicators.Where(i => i.IndicatorId.StartsWith("AK-EXO-")).Should().HaveCount(17);
        run.Indicators.Should().Contain(i => i.IndicatorId.StartsWith("AK-EXO-") && i.Status == KnightIndicatorStatus.Passed);
        run.Score.Should().NotBeNull();
        run.NotEvaluatedCount.Should().Be(run.Indicators.Count(i => i.Status == KnightIndicatorStatus.NotEvaluated));
        run.PassedCount.Should().Be(run.Indicators.Count(i => i.Status == KnightIndicatorStatus.Passed));

        Guid snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            // Releitura do banco, em outro contexto: o MESMO texto, caractere a caractere.
            var gravado = await db.KnightIndicatorResults.AsNoTracking()
                .SingleAsync(i => i.RunId == run.Id && i.IndicatorId == "AK-EXO-016");
            gravado.NotEvaluatedReason.Should().Be(motivo);
            gravado.Evidence.Should().Be("Não avaliado: " + motivo);

            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenant))
                .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, run.Id)).Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var fotografia = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            var congelado = fotografia.Indicators.Single(i => i.IndicatorId == "AK-EXO-016");
            congelado.NotEvaluatedReason.Should().Be(motivo, "a fotografia congela o motivo inteiro");
            congelado.Evidence.Should().Be("Não avaliado: " + motivo);
            PostureSnapshotHasher.Verify(fotografia).Should().BeTrue("o conteúdo congelado sobrevive à ida e volta");

            // O relatório HTML leva o motivo inteiro nos dados que a página renderiza como texto.
            var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshotId, PostureExportFormat.Html))!.Content);
            StringValues(DataIsland(html), "notEvaluatedReason").Should().Contain(motivo);
        }
    }

    /// <summary>
    /// Os limites antigos da FOTOGRAFIA — motivo em 1000 e evidência em 2000 — também caíram. O caso de 653 caracteres só
    /// passava da coluna de 500; aqui o motivo tem mais de 1000 e a evidência mais de 2000 caracteres. O texto é sintético
    /// (lista de políticas em demo.example.com, como a que um motivo montado de listas variáveis carrega) e é gravado sobre
    /// o resultado de uma avaliação real no PostgreSQL migrado; depois: releitura, publicação da fotografia (hash válido),
    /// releitura da fotografia e relatório HTML — o MESMO texto, caractere a caractere, em todos.
    /// </summary>
    [Fact]
    public async Task MotivoAcimaDe1000_EvidenciaAcimaDe2000_GravaPublicaReleEExportaIntegrais()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.MigrateAsync();
        var tenant = await SeedAsync(opt);

        var politicas = new StringBuilder("as substituições por caixa não foram lidas; políticas envolvidas: ");
        for (var i = 1; politicas.Length < 2100; i++)
            politicas.Append($"Política sintética {i:D3} (mailbox{i:D3}@demo.example.com); ");
        var motivo = politicas.Append("fim do diagnóstico sintético.").ToString();
        var evidencia = "Não avaliado: " + motivo;
        motivo.Length.Should().BeGreaterThan(1000, "passa do limite antigo do motivo na fotografia");
        evidencia.Length.Should().BeGreaterThan(2000, "passa do limite antigo da evidência na fotografia");
        _output.WriteLine($"motivo: {motivo.Length} caracteres; evidência: {evidencia.Length} caracteres");

        Guid runId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            runId = (await KnightExchangeConfigurationFlowTests
                .ServiceFor(db, tenant, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.CasMailboxesDenied))
                .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline)).Id;
        }

        // Gravação no PostgreSQL: o resultado "não avaliado" recebe o motivo e a evidência longos. Status, nota e
        // denominadores da execução não são tocados.
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var resultado = await db.KnightIndicatorResults.SingleAsync(i => i.RunId == runId && i.IndicatorId == "AK-EXO-016");
            resultado.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
            resultado.NotEvaluatedReason = motivo;
            resultado.Evidence = evidencia;
            await db.SaveChangesAsync();
        }

        Guid snapshotId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var gravado = await db.KnightIndicatorResults.AsNoTracking()
                .SingleAsync(i => i.RunId == runId && i.IndicatorId == "AK-EXO-016");
            gravado.NotEvaluatedReason.Should().Be(motivo, "a releitura devolve o motivo inteiro");
            gravado.Evidence.Should().Be(evidencia, "a releitura devolve a evidência inteira");

            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenant))
                .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, runId)).Summary.Id;
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var fotografia = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            var congelado = fotografia.Indicators.Single(i => i.IndicatorId == "AK-EXO-016");
            congelado.NotEvaluatedReason.Should().Be(motivo, "a fotografia congela o motivo acima de 1000 caracteres inteiro");
            congelado.Evidence.Should().Be(evidencia, "a fotografia congela a evidência acima de 2000 caracteres inteira");
            PostureSnapshotHasher.Verify(fotografia).Should().BeTrue("o hash da fotografia confere com o conteúdo relido");

            var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshotId, PostureExportFormat.Html))!.Content);
            var dados = DataIsland(html);
            StringValues(dados, "notEvaluatedReason").Should().Contain(motivo, "o relatório HTML leva o motivo inteiro");
            StringValues(dados, "evidence").Should().Contain(evidencia, "o relatório HTML leva a evidência inteira");
        }
    }

    /// <summary>
    /// Falha de gravação NUNCA vira sucesso, nem "falha de coleta". O banco é deixado propositalmente ATRÁS do modelo — a
    /// coluna volta a character varying(500), como no ambiente antes desta migration — e a sincronização passa pelo worker
    /// real: o pedido termina "PersistenceError" com etapa, pedido, SQLSTATE, entidade, propriedade e tamanhos (sem o texto
    /// coletado), nenhuma linha da avaliação fica gravada e a avaliação anterior continua sendo a vigente.
    /// </summary>
    [Fact]
    public async Task SchemaAtrasDoModelo_FalhaDeGravacao_ViraPersistenceErrorComDiagnosticoSeguro_EPreservaAAnterior()
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

        // 1) Uma avaliação anterior VÁLIDA, concluída pelo mesmo worker.
        var anterior = await SyncAsync(opt, tenant, connectorId, ExchangeCollectionScenario.Variant.Compliant);
        anterior.Status.Should().Be(KnightSyncStatus.Completed);
        var anteriorRunId = anterior.RunId!.Value;

        // 2) O schema volta ao limite antigo (o ambiente antes desta migration).
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"KnightIndicatorResults\" ALTER COLUMN \"NotEvaluatedReason\" TYPE character varying(500)");
        int runsAntes, resultadosAntes;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            runsAntes = await db.KnightAssessmentRuns.CountAsync();
            resultadosAntes = await db.KnightIndicatorResults.CountAsync();
        }

        // 3) A sincronização que produz o motivo longo.
        var falha = await SyncAsync(opt, tenant, connectorId, ExchangeCollectionScenario.Variant.CasMailboxesDenied);
        _output.WriteLine($"Pedido: {falha.Status} / {falha.FailureCategory}");
        _output.WriteLine($"Mensagem: {falha.Message}");

        falha.Status.Should().Be(KnightSyncStatus.Failed, "falha de gravação nunca aparece como sucesso");
        falha.FailureCategory.Should().Be("PersistenceError", "não é falha de coleta");
        falha.RunId.Should().BeNull("nenhuma avaliação deste pedido foi gravada");
        falha.LastCompletedRunId.Should().Be(anteriorRunId, "a avaliação anterior continua sendo a vigente");
        falha.Message.Should().Contain("a avaliação anterior continua valendo")
            .And.Contain("pedido " + falha.Id)
            .And.Contain("etapa: gravação da avaliação")
            .And.Contain("SQLSTATE 22001")
            .And.MatchRegex(@"KnightIndicatorResult\.NotEvaluatedReason \(AK-EXO-016\) com \d{3,} caracteres, limite 500");
        falha.Message!.Length.Should().BeLessThanOrEqualTo(1000);
        falha.Message.Should().NotContain("SUBSTITUIÇÕES", "o diagnóstico leva nomes e tamanhos, nunca o texto coletado")
            .And.NotContain("Evidence", "só a coluna que de fato excedeu é apontada")
            .And.NotContain("Json");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            (await db.KnightAssessmentRuns.CountAsync()).Should().Be(runsAntes, "a gravação é atômica: nada da tentativa ficou");
            (await db.KnightIndicatorResults.CountAsync()).Should().Be(resultadosAntes);
            (await db.KnightAssessmentRuns.AsNoTracking()
                    .Where(r => r.SourceType == KnightSourceType.MicrosoftExchangeOnline)
                    .OrderByDescending(r => r.StartedAt).FirstAsync()).Id
                .Should().Be(anteriorRunId);
        }
    }

    /// <summary>
    /// A migration preserva o que já estava gravado: execução e fotografia criadas no schema ANTERIOR atravessam a conversão
    /// de varchar para text sem mudar um caractere, a fotografia continua íntegra pelo hash e as colunas passam a text.
    /// </summary>
    [Fact]
    public async Task Migration_PreservaRegistrosAntigos_EConverteAsColunasParaText()
    {
        await using var pg = await PostgresProbe.TryCreateAsync();
        if (pg is null) { _output.WriteLine("PULADO: AEGIS_TEST_PG não definido."); return; }
        var opt = pg.DbOptions();
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            var todas = db.Database.GetMigrations().ToList();
            var desta = todas.Single(m => m.EndsWith("_KnightPersistence01_DiagnosticTextColumns", StringComparison.Ordinal));
            await migrator.MigrateAsync(todas[todas.IndexOf(desta) - 1]);
            (await ColumnTypesAsync(db)).Should().Contain("KnightIndicatorResults.NotEvaluatedReason=character varying(500)");
        }
        var tenant = await SeedAsync(opt);

        Guid runId, snapshotId;
        List<(string Id, string? Reason, string Evidence)> antes;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            // A leitura das CAIXAS recusada cabe nos limites antigos: é o registro que já existe em produção.
            runId = (await KnightExchangeConfigurationFlowTests
                .ServiceFor(db, tenant, new ExchangeCollectionScenario(ExchangeCollectionScenario.Variant.MailboxesDenied))
                .RunAssessmentAsync(KnightSourceType.MicrosoftExchangeOnline)).Id;
            snapshotId = (await new PostureSnapshotService(db, new SystemTenantContext(tenant))
                .PublishAsync(PostureSnapshotType.Knight, KnightSourceType.MicrosoftExchangeOnline, runId)).Summary.Id;
            antes = (await db.KnightIndicatorResults.AsNoTracking().Where(i => i.RunId == runId).ToListAsync())
                .Select(i => (i.IndicatorId, i.NotEvaluatedReason, i.Evidence)).OrderBy(x => x.IndicatorId).ToList();
        }
        antes.Should().Contain(x => x.Reason != null, "o registro antigo tem motivos de não avaliado");

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(null)))
        {
            await db.Database.MigrateAsync();
            var tipos = await ColumnTypesAsync(db);
            _output.WriteLine(string.Join(Environment.NewLine, tipos));
            tipos.Should().Contain(new[]
            {
                "KnightIndicatorResults.NotEvaluatedReason=text",
                "PostureSnapshotIndicators.NotEvaluatedReason=text",
                "PostureSnapshotIndicators.Evidence=text",
                "PostureSnapshotIndicators.AffectedDetailLimitation=text",
            });
        }

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
        {
            var depois = (await db.KnightIndicatorResults.AsNoTracking().Where(i => i.RunId == runId).ToListAsync())
                .Select(i => (i.IndicatorId, i.NotEvaluatedReason, i.Evidence)).OrderBy(x => x.IndicatorId).ToList();
            depois.Should().Equal(antes, "a conversão não reescreve nem corta o que já estava gravado");

            var fotografia = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
                .SingleAsync(s => s.Id == snapshotId);
            PostureSnapshotHasher.Verify(fotografia).Should().BeTrue("a fotografia anterior continua íntegra depois da migration");
        }
    }

    private static async Task<List<string>> ColumnTypesAsync(AegisScoreDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT table_name, column_name, data_type, character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND ((table_name = 'KnightIndicatorResults' AND column_name = 'NotEvaluatedReason') "
                + "OR (table_name = 'PostureSnapshotIndicators' AND column_name IN ('NotEvaluatedReason', 'Evidence', 'AffectedDetailLimitation')))";
            var tipos = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tipos.Add($"{reader.GetString(0)}.{reader.GetString(1)}="
                    + (reader.IsDBNull(3) ? reader.GetString(2) : $"{reader.GetString(2)}({reader.GetValue(3)})"));
            return tipos;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Uma sincronização do Exchange pelo WORKER real, com o serviço real de avaliação sob o tenant do pedido.</summary>
    private static async Task<KnightSyncRequestView> SyncAsync(
        DbContextOptions<AegisScoreDbContext> opt, Guid tenant, Guid connectorId, ExchangeCollectionScenario.Variant variant)
    {
        Guid requestId;
        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            requestId = (await new KnightSyncRequests(db, new SystemTenantContext(tenant), TimeProvider.System)
                .EnqueueAsync(connectorId, KnightSourceType.MicrosoftExchangeOnline, null)).Request.Id;

        var services = new ServiceCollection();
        services.AddSingleton(opt);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<TenantScopeOverride>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddScoped(sp => new AegisScoreDbContext(opt, new SystemTenantContext(sp.GetRequiredService<ITenantContext>().TenantId)));
        services.AddScoped(sp => KnightExchangeConfigurationFlowTests.ServiceFor(
            sp.GetRequiredService<AegisScoreDbContext>(), sp.GetRequiredService<ITenantContext>().TenantId!.Value,
            new ExchangeCollectionScenario(variant)));
        var provider = services.BuildServiceProvider();
        var options = Options.Create(new KnightSyncOptions { LeaseSeconds = 120, HeartbeatSeconds = 5, PollSeconds = 1, MaxAttempts = 2 });
        var queue = new DurableKnightSyncQueue(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, options);
        var worker = new KnightSyncWorker(provider.GetRequiredService<IServiceScopeFactory>(), queue, options,
            NullLogger<KnightSyncWorker>.Instance);

        var lease = await queue.TryClaimNextAsync();
        lease.Should().NotBeNull();
        lease!.RequestId.Should().Be(requestId);
        await worker.ProcessAsync(lease, CancellationToken.None);

        await using (var db = new AegisScoreDbContext(opt, new SystemTenantContext(tenant)))
            return (await new KnightSyncRequests(db, new SystemTenantContext(tenant), TimeProvider.System)
                .GetAsync(connectorId, requestId))!;
    }

    /// <summary>Propriedades de texto rastreadas que excedem o limite do MODELO (ou o da coluna) — nome e tamanho, nunca valor.</summary>
    internal static IEnumerable<string> Oversized(DbContext db, int? columnLimit = null)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
            foreach (var p in entry.Properties.Where(p => p.Metadata.ClrType == typeof(string)))
            {
                var limit = p.Metadata.GetMaxLength() ?? columnLimit;
                if (p.CurrentValue is string s && limit is { } max && s.Length > max)
                    yield return $"{entry.Metadata.ClrType.Name}.{p.Metadata.Name} ({(entry.Properties.FirstOrDefault(x => x.Metadata.Name == "IndicatorId")?.CurrentValue ?? "?")}): {s.Length} > {max}";
            }
    }

    internal static JsonElement DataIsland(string html)
    {
        const string open = "<script type=\"application/json\" id=\"aegis-data\">";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return JsonDocument.Parse(html[start..end]).RootElement.Clone();
    }

    internal static IEnumerable<string> StringValues(JsonElement e, string property)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == property && p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                    foreach (var v in StringValues(p.Value, property)) yield return v;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    foreach (var v in StringValues(item, property)) yield return v;
                break;
        }
    }

    internal static async Task<Guid> SeedAsync(DbContextOptions<AegisScoreDbContext> opt)
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
