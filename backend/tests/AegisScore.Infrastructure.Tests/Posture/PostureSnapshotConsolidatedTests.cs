using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-KNIGHT-CONSOLIDATED-01] Relatório KNIGHT consolidado (Entra ID + Teams + Exchange Online): a
/// composição congela avaliações e aquisições utilizadas, cada fonte mantém a própria nota/cobertura (nunca
/// somadas), uma fonte sem avaliação ou não escolhida aparece sem virar aprovação, e nenhum resultado é contado
/// duas vezes. A leitura AO VIVO (<see cref="KnightConsolidatedBuilder"/>) é coberta à parte, sem banco.
/// </summary>
public sealed class PostureSnapshotConsolidatedTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("cccc3333-3333-3333-3333-333333333333");
    private static readonly Guid TenantB = Guid.Parse("dddd4444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;

    public PostureSnapshotConsolidatedTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- 1) Padrão claro: sem pedido explícito, inclui todas as fontes com avaliação concluída --------

    [Fact]
    public async Task Publish_DefaultSelection_IncludesEverySourceWithACompletedRun_ExcludedOnesStayVisible()
    {
        await using var db = NewContext(TenantA);
        await SeedEntraRunAsync(db);   // 1 Exposed (Critical), 1 Passed (Medium)
        await SeedTeamsRunAsync(db);   // 1 Exposed (High)

        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);

        detail.Summary.SourceType.Should().Be("Consolidated");
        detail.Summary.FormulaVersion.Should().Be("knight-score-v1");
        detail.Indicators.Should().HaveCount(3, "os dois indicadores do Entra ID + o do Teams — nenhum contado duas vezes");

        // [AEGIS-KNIGHT-COVERAGE-04] Toda fonte candidata é listada — incluída, disponível ou nunca avaliada —, na ordem do catálogo.
        detail.Composition.Select(c => c.Source).Should().Equal(KnightSourceCatalog.ConsolidationCandidates,
            "as fontes do conector Microsoft são sempre listadas, mesmo as que nunca foram avaliadas");
        var entra = detail.Composition!.Single(s => s.Source == KnightSourceType.MicrosoftEntraId);
        var teams = detail.Composition!.Single(s => s.Source == KnightSourceType.MicrosoftTeams);
        var exchange = detail.Composition!.Single(s => s.Source == KnightSourceType.MicrosoftExchangeOnline);

        entra.Included.Should().BeTrue();
        entra.AvailabilityState.Should().Be("Included");
        teams.Included.Should().BeTrue();
        exchange.Included.Should().BeFalse();
        exchange.AvailabilityState.Should().Be("NotAssessed", "nunca houve uma avaliação concluída de Exchange Online");
        exchange.Score.Should().BeNull();
        exchange.Label.Should().Be("Exchange Online");
    }

    // ---- 2) Fonte com avaliação concluída, mas NÃO escolhida: aparece sem virar aprovação --------------

    [Fact]
    public async Task Publish_ExplicitSelection_ExcludesUnrequestedSource_ButKeepsItVisibleAsAvailable()
    {
        await using var db = NewContext(TenantA);
        var entraRunId = await SeedEntraRunAsync(db);
        await SeedTeamsRunAsync(db);

        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(
            new[] { new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftEntraId, entraRunId) });

        detail.Indicators.Should().HaveCount(2, "só os indicadores do Entra ID entram — o Teams não foi escolhido");
        detail.Indicators.Should().OnlyContain(i => i.SourceType == "MicrosoftEntraId");

        var teams = detail.Composition!.Single(s => s.Source == KnightSourceType.MicrosoftTeams);
        teams.Included.Should().BeFalse();
        teams.AvailabilityState.Should().Be("Available", "tem avaliação concluída, só não foi escolhida");
        teams.Score.Should().NotBeNull("a nota PRÓPRIA da fonte continua visível mesmo excluída — não vira aprovação nem desaparece");
    }

    // ---- 3) A nota combinada NÃO é a média simples das notas por fonte ---------------------------------

    [Fact]
    public async Task Publish_CombinedScore_IsNotTheSimpleAverageOfPerSourceScores()
    {
        await using var db = NewContext(TenantA);
        await SeedEntraRunAsync(db);   // Critical Exposed (peso 10, fator 0) + Medium Passed (peso 4, fator 1) => run.Score = round(100*4/14) = 29
        await SeedTeamsRunAsync(db);   // High Exposed (peso 7, fator 0) => run.Score = 0

        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);

        // Combinada = knight-score-v1 sobre a UNIÃO dos indicadores: pesos 10(Exposed)+4(Passed)+7(Exposed),
        // achieved = 4*1 = 4, evaluated weight = 10+4+7 = 21 => round(100*4/21) = 19.
        detail.Summary.Score.Should().Be(19);

        var naiveAverage = (29 + 0) / 2.0;
        detail.Summary.Score.Should().NotBe(naiveAverage, "a fórmula pondera por severidade sobre o conjunto combinado, não tira média das notas por fonte");
    }

    // ---- 4) Nenhuma fonte pedida tem avaliação concluída: recusa explícita -----------------------------

    [Fact]
    public async Task Publish_NoSourceHasACompletedRun_ThrowsNotAvailable()
    {
        await using var db = NewContext(TenantA);
        var act = () => ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);
        await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>();
    }

    // ---- 5) Isolamento por tenant: a composição de um tenant não vê o outro ----------------------------

    [Fact]
    public async Task Publish_TenantIsolation_OtherTenantsRunsNeverContribute()
    {
        await using var dbA = NewContext(TenantA);
        await SeedEntraRunAsync(dbA);

        await using var dbB = NewContext(TenantB);
        var act = () => ServiceFor(dbB, TenantB).PublishConsolidatedKnightAsync(null);
        await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>("a avaliação do tenant A é invisível para o B");
    }

    // ---- 4b) [AEGIS-KNIGHT-CONSOLIDATED-02] Seleção EXPLICITAMENTE vazia: distinta de "nenhuma tem avaliação" -

    [Fact]
    public async Task Publish_ExplicitEmptySelection_ThrowsDistinctMessage_EvenThoughCompletedRunsExist()
    {
        await using var db = NewContext(TenantA);
        await SeedEntraRunAsync(db);
        await SeedTeamsRunAsync(db);

        // Lista PRESENTE e vazia: a pessoa desmarcou todas as fontes de propósito — bem diferente de "ninguém
        // nunca avaliou nada" (que é o caso coberto acima, com `null`). Não deve incluir nada em silêncio.
        var act = () => ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(Array.Empty<KnightConsolidatedSourceSelection>());

        (await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>()).Which.Message
            .Should().Contain("Nenhuma fonte foi selecionada", "a mensagem não deve sugerir sincronizar — a pessoa já tem avaliações concluídas, só não marcou nenhuma");

        (await db.PostureSnapshots.CountAsync()).Should().Be(0, "seleção vazia bloqueada não deve publicar nada");
    }

    // ---- 4c) [AEGIS-KNIGHT-CONSOLIDATED-02] A publicação pina a execução EXIBIDA, não "a mais recente" -------

    [Fact]
    public async Task Publish_Selection_PinsTheExactRunIndicated_NotTheLatestOfTheSameSource()
    {
        await using var db = NewContext(TenantA);
        var older = await SeedEntraRunAtAsync(db, "AK-ENTRA-901", DateTimeOffset.UtcNow.AddHours(-2));
        var newer = await SeedEntraRunAtAsync(db, "AK-ENTRA-902", DateTimeOffset.UtcNow.AddHours(-1));

        // A tela mostrava a execução MAIS ANTIGA (older) como incluída — por exemplo, porque a leitura ao vivo
        // aconteceu antes de uma nova coleta terminar. Publicar precisa congelar exatamente o que foi exibido.
        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(
            new[] { new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftEntraId, older) });

        detail.Indicators.Should().ContainSingle().Which.IndicatorId.Should().Be(
            "AK-ENTRA-901", "a execução PINADA é a exibida — nunca a mais recente da mesma fonte");

        var entraEntry = detail.Composition!.Single(s => s.Source == KnightSourceType.MicrosoftEntraId);
        entraEntry.SourceRunId.Should().Be(older);
        _ = newer; // só existe para provar que uma execução mais nova NÃO venceu a pinada
    }

    // ---- 4d) [AEGIS-KNIGHT-CONSOLIDATED-02] Execução pinada inválida: recusa, nunca substitui pela mais recente

    [Fact]
    public async Task Publish_Selection_WithNonExistentRunId_ThrowsAndDoesNotSubstituteTheLatest()
    {
        await using var db = NewContext(TenantA);
        await SeedEntraRunAsync(db); // existe uma execução válida da MESMA fonte — não pode virar o substituto silencioso

        var act = () => ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(
            new[] { new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftEntraId, Guid.NewGuid()) });

        (await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>()).Which.Message
            .Should().Contain("não está mais disponível");
        (await db.PostureSnapshots.CountAsync()).Should().Be(0, "uma execução pinada inválida nunca publica com a mais recente no lugar");
    }

    [Fact]
    public async Task Publish_Selection_WithRunFromAnotherTenant_ThrowsAndDoesNotSubstituteTheLatest()
    {
        await using var dbA = NewContext(TenantA);
        await SeedEntraRunAsync(dbA); // execução válida do PRÓPRIO tenant — também não pode virar substituto

        await using var dbB = NewContext(TenantB);
        var runOfB = await SeedEntraRunAsync(dbB);

        // TenantA tenta publicar pinando a execução do TenantB — o Global Query Filter a torna invisível.
        var act = () => ServiceFor(dbA, TenantA).PublishConsolidatedKnightAsync(
            new[] { new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftEntraId, runOfB) });

        await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>("a execução de outro tenant é indistinguível de inexistente");
        (await dbA.PostureSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Publish_Selection_WithRunFromADifferentSource_ThrowsAndDoesNotSubstituteTheLatest()
    {
        await using var db = NewContext(TenantA);
        var entraRunId = await SeedEntraRunAsync(db);

        // A execução existe, está concluída e é do próprio tenant — mas é do Entra ID, não do Teams como o
        // item da seleção declara. A revalidação de FONTE precisa recusar mesmo assim.
        var act = () => ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(
            new[] { new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftTeams, entraRunId) });

        await act.Should().ThrowAsync<PostureSnapshotNotAvailableException>();
        (await db.PostureSnapshots.CountAsync()).Should().Be(0);
    }

    // ---- 6) Composição CONGELADA: o hash re-deriva, e o conteúdo não muda com nova coleta -------------

    [Fact]
    public async Task Publish_Composition_IsFrozenAndHashReDerives()
    {
        Guid id;
        await using (var db = NewContext(TenantA))
        {
            await SeedEntraRunAsync(db);
            var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);
            id = detail.Summary.Id;
        }

        await using (var db = NewContext(TenantA))
        {
            var frozen = await db.PostureSnapshots.AsNoTracking()
                .Include(s => s.Indicators).Include(s => s.Objects).SingleAsync(s => s.Id == id);
            PostureSnapshotHasher.Verify(frozen).Should().BeTrue("o hash cobre CompositionJson e re-deriva após a persistência");
            frozen.CompositionJson.Should().NotBeNullOrWhiteSpace();
            frozen.SemanticFamily.Should().Be("knight:consolidated:MicrosoftEntraId");
        }
    }

    // ---- 7) HTML/CSV/PDF da mesma fotografia reconciliam a composição e as contagens -------------------

    [Fact]
    public async Task Export_Html_Csv_Pdf_OfTheSameConsolidatedSnapshot_Reconcile()
    {
        await using var db = NewContext(TenantA);
        await SeedEntraRunAsync(db);
        await SeedTeamsRunAsync(db);
        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);

        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Indicators).Include(s => s.Objects).SingleAsync(s => s.Id == detail.Summary.Id);
        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.Composition.Should().HaveCount(KnightSourceCatalog.ConsolidationCandidates.Count);
        model.Composition.Count(c => c.AvailabilityState == "NotAssessed").Should().Be(KnightSourceCatalog.ConsolidationCandidates.Count - 2,
            "as fontes sem avaliação aparecem como não avaliadas, sem nota — nunca como zero");
        model.Header.SourceType.Should().Be("Consolidated");
        model.Header.IsDemo.Should().BeFalse("uma fotografia consolidada real não é rotulada como demonstração");

        var exporter = new PostureSnapshotExporter(db);

        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("Composição do relatório consolidado");
        html.Should().Contain("default-src 'none'");

        var csvText = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Csv))!.Content).TrimStart('﻿');
        var csvLines = csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var header = csvLines[0].Split(';');
        var sourceCol = Array.IndexOf(header, "SourceType");
        var idCol = Array.IndexOf(header, "IndicatorId");
        var rows = csvLines.Skip(1).Select(l => l.Split(';')).ToList();
        rows.Select(r => r[idCol]).Distinct().Should().HaveCount(snapshot.Indicators.Count, "controles = IndicatorId distintos, igual ao HTML");
        rows.Select(r => r[sourceCol]).Distinct().Should().BeEquivalentTo(new[] { "MicrosoftEntraId", "MicrosoftTeams" },
            "cada linha carrega a fonte REAL do indicador, não 'Consolidated'");

        var pdfBytes = (await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Pdf))!.Content;
        pdfBytes.Should().NotBeEmpty();
        pdfBytes.Take(4).ToArray().Should().Equal(new byte[] { 0x25, 0x50, 0x44, 0x46 }, "assinatura %PDF");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Término da composição × completude da coleta: com uma fonte incluída em coleta parcial,
    /// a fotografia publicada diz "coleta parcial" (não "coleta concluída"), nomeia a fonte e preserva as limitações
    /// DELA na composição congelada — no modelo do relatório, no HTML e no PDF.
    /// </summary>
    [Fact]
    public async Task Publish_ComUmaFonteParcial_FotografiaDizColetaParcial_ENomeiaAFonteEAsLimitacoes()
    {
        await using var db = NewContext(TenantA);
        await SeedEntraRunAsync(db);
        var teamsRun = await SeedTeamsRunAsync(db);
        var run = await db.KnightAssessmentRuns.SingleAsync(r => r.Id == teamsRun);
        run.SourceState = KnightSourceState.PartialCollection;
        run.CapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new KnightCapabilityStatus(KnightCapability.TeamsMeetingPolicies, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.TeamsFederationConfiguration, KnightCapabilityOutcome.InsufficientPermission, "leitura recusada (sintético)"),
        }, KnightCapabilitiesJson.Options);
        await db.SaveChangesAsync();

        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);
        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Indicators).Include(s => s.Objects).SingleAsync(s => s.Id == detail.Summary.Id);

        var teams = detail.Composition!.Single(s => s.Source == KnightSourceType.MicrosoftTeams);
        teams.SourceState.Should().Be("PartialCollection");
        teams.CollectionLimitations.Should().ContainSingle().Which.Should().Contain("TeamsFederationConfiguration");
        KnightConsolidatedCollection.StateOf(detail.Composition!).Should().Be(KnightSourceState.PartialCollection);

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.Header.CollectionState.Should().Be("PartialCollection");
        model.Header.CollectionStateLabel.Should().Be("Coleta parcial");
        model.Header.CollectionSummary.Should().StartWith("Coleta parcial em 1 de 2 fontes incluídas (Microsoft Teams)");
        model.Composition!.Single(c => c.Source == KnightSourceType.MicrosoftTeams).CollectionLimitations.Should().Equal(
            "Federação e acesso externo do Teams: Autorização recusada — leitura recusada (sintético)");

        var exporter = new PostureSnapshotExporter(db);
        var html = Encoding.UTF8.GetString((await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("\"collectionState\":\"PartialCollection\"").And.Contain("Estado da coleta")
            .And.NotContain("TeamsFederationConfiguration: InsufficientPermission", "a limitação sai com os rótulos em português");
        (await exporter.ExportAsync(snapshot.Id, PostureExportFormat.Pdf))!.Content.Should().NotBeEmpty();
    }

    [Fact]
    public void Collection_TodasIntegras_EhCompleta_UmaComLimitacao_EhParcial()
    {
        static KnightConsolidatedSourceEntry E(string label, string state, params string[] limits) => new(
            KnightSourceType.MicrosoftEntraId, label, true, "Included", Guid.NewGuid(), state, "v", DateTimeOffset.UtcNow,
            50, 100, 1, 1, 0, 0, 0, 0, limits);
        var ok = new[] { E("A", "Completed"), E("B", "Completed") };
        KnightConsolidatedCollection.StateOf(ok).Should().Be(KnightSourceState.Completed);
        KnightConsolidatedCollection.Describe(ok).Should().Be("Coleta completa nas 2 fontes incluídas.");

        var withLimit = new[] { E("A", "Completed"), E("B", "Completed", "X: InsufficientPermission") };
        KnightConsolidatedCollection.StateOf(withLimit).Should().Be(KnightSourceState.PartialCollection,
            "estado 'concluída' com limitação registrada não é coleta completa");
        KnightConsolidatedCollection.Describe(withLimit).Should().Contain("(B)");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] A ação aberta para um achado — seja na visão por fonte, seja no consolidado — nasce da
    /// execução REAL da fonte, e o relatório consolidado a congela pelo indicador e pela procedência dessa fonte: a
    /// mesma ação, uma vez, sem a composição precisar de uma execução própria.
    /// </summary>
    [Fact]
    public async Task Publish_Consolidated_FreezesTheSourceRunActionPlan_OnceAndBoundToItsSource()
    {
        await using var db = NewContext(TenantA);
        var entraRun = await SeedEntraRunAsync(db);
        await SeedTeamsRunAsync(db);

        var remediation = new AegisScore.Infrastructure.Remediation.RemediationService(db, new SystemTenantContext(TenantA), TimeProvider.System,
            new AegisScore.Infrastructure.Queries.DevicePriorityQuery(db, TimeProvider.System,
                Microsoft.Extensions.Options.Options.Create(new AegisScore.Application.Queries.CrossSourceCorrelationOptions())));
        var plano = await remediation.CreateForFindingAsync(
            new AegisScore.Application.Remediation.CreateFindingActionPlanCommand(entraRun, "AK-ENTRA-001", "Registrar segundo fator",
                "Ação proposta (sintética).", "Equipe de Identidade", "TI", null),
            new AegisScore.Application.Remediation.RemediationActor(Guid.NewGuid(), "Analista Demo"));

        var detail = await ServiceFor(db, TenantA).PublishConsolidatedKnightAsync(null);

        var snapshot = await db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls).Include(s => s.Indicators).Include(s => s.ActionItems).Include(s => s.Objects)
            .SingleAsync(s => s.Id == detail.Summary.Id);
        snapshot.ActionItems.Should().ContainSingle("a ação do achado do Entra ID entra uma vez no consolidado")
            .Which.ActionPlanId.Should().Be(plano!.Id);
        snapshot.ActionItems.Single().IndicatorId.Should().Be("AK-ENTRA-001");
        PostureSnapshotHasher.Verify(snapshot).Should().BeTrue("a ação congelada faz parte do conteúdo assinado");
    }

    // ---- infraestrutura do teste -------------------------------------------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private static IPostureSnapshotService ServiceFor(AegisScoreDbContext db, Guid? tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId), new NistSignalMapper(db));

    /// <summary>Entra ID: 1 Exposed (Critical) + 1 Passed (Medium) — run.Score = round(100*4/14) = 29.</summary>
    private static async Task<Guid> SeedEntraRunAsync(AegisScoreDbContext db)
    {
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed, Source = "Microsoft Entra ID", Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v4", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-25),
            Score = 29, Coverage = 100, PassedCount = 1, ExposedCount = 1,
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-001", Title = "Contas privilegiadas sem MFA",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Critical,
            Status = KnightIndicatorStatus.Exposed, Evidence = "2 de 5 sem MFA.", AffectedObjectCount = 2,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-26),
        });
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-ENTRA-003", Title = "Contas privilegiadas com mailbox",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Medium,
            Status = KnightIndicatorStatus.Passed, Evidence = "Nenhuma.", AffectedObjectCount = 0,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-26),
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>
    /// Uma execução ISOLADA do Entra ID, com um único indicador identificável e instante CONTROLADO — usada
    /// para provar que a publicação pina a execução EXATA indicada, e não "a mais recente da fonte".
    /// </summary>
    private static async Task<Guid> SeedEntraRunAtAsync(AegisScoreDbContext db, string indicatorId, DateTimeOffset startedAt)
    {
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = KnightSourceType.MicrosoftEntraId,
            SourceState = KnightSourceState.Completed, Source = "Microsoft Entra ID", Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v4", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = startedAt, CompletedAt = startedAt.AddMinutes(5),
            Score = 0, Coverage = 100, ExposedCount = 1,
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = indicatorId, Title = "Indicador de teste",
            Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.Critical,
            Status = KnightIndicatorStatus.Exposed, Evidence = "evidência", AffectedObjectCount = 1,
            SourceType = KnightSourceType.MicrosoftEntraId, CollectedAt = startedAt.AddMinutes(1),
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>Teams: 1 Exposed (High) — run.Score = 0.</summary>
    private static async Task<Guid> SeedTeamsRunAsync(AegisScoreDbContext db)
    {
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = KnightSourceType.MicrosoftTeams,
            SourceState = KnightSourceState.Completed, Source = "Microsoft Teams", Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v5", ScoreFormulaVersion = "knight-score-v1",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Score = 0, Coverage = 100, ExposedCount = 1,
        };
        run.Indicators.Add(new KnightIndicatorResult
        {
            IndicatorId = "AK-TEAMS-010", Title = "Convidados ignoram o lobby",
            Category = KnightIndicatorCategory.GuestAccess, Severity = SeverityLevel.High,
            Status = KnightIndicatorStatus.Exposed, Evidence = "Política Global permite.", AffectedObjectCount = 1,
            SourceType = KnightSourceType.MicrosoftTeams, CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-6),
        });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }
}
