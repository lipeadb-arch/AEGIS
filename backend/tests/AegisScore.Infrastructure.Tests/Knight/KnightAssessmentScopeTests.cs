using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AegisScore.Api.Contracts;
using AegisScore.Api.Controllers;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Knight;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-SCOPE-01] Escopo salvo da avaliação KNIGHT por tenant. O caso de referência tem 201 controles avaliados e 255
/// aplicáveis, dos quais 12 são do Microsoft Fabric sem avaliação. Com o Fabric fora do escopo, o consolidado mostra 201/243
/// (82,7%) e 42 sem avaliação — na leitura ao vivo, na publicação e no relatório —, e a composição diz "Fora do escopo". O
/// conector, as avaliações do Fabric e a escolha de outro tenant não mudam.
/// </summary>
public sealed class KnightAssessmentScopeTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaa5555-5555-5555-5555-555555555555");
    private static readonly Guid TenantB = Guid.Parse("bbbb6666-6666-6666-6666-666666666666");

    private readonly SqliteConnection _connection;

    public KnightAssessmentScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Leitura ao vivo (builder puro) -------------------------------------------------------------------------

    [Fact]
    public void CasoDeReferencia_SemEscopo_201de255_ComFabric()
    {
        var r = KnightConsolidatedBuilder.Build(ReferenceCase(), null);

        Evaluated(r).Should().Be(201);
        Applicable(r).Should().Be(255);
        r.NotEvaluatedCount.Should().Be(54);
        r.Coverage.Should().Be(78.8);
        r.Sources.Single(s => s.Source == KnightSourceType.MicrosoftFabric).AvailabilityState.Should().Be("Included");
    }

    [Fact]
    public void CasoDeReferencia_FabricForaDoEscopo_201de243_827_E42SemAvaliacao()
    {
        var r = KnightConsolidatedBuilder.Build(ReferenceCase(), null, new[] { KnightSourceType.MicrosoftFabric });

        Evaluated(r).Should().Be(201);
        Applicable(r).Should().Be(243);
        r.Coverage.Should().Be(82.7);
        (r.NotEvaluatedCount + r.ErrorCount).Should().Be(42);
        r.Indicators.Should().NotContain(i => i.SourceType == KnightSourceType.MicrosoftFabric, "os controles e achados do Fabric saem dos totais");
        r.Capabilities.Should().NotContain(c => c.Capability == KnightCapability.FabricTenantSettings, "as limitações do Fabric também saem");
        r.IncludedSources.Should().Equal(KnightSourceType.MicrosoftEntraId, KnightSourceType.MicrosoftTeams,
            KnightSourceType.MicrosoftExchangeOnline, KnightSourceType.MicrosoftAzure);

        var fabric = r.Sources.Single(s => s.Source == KnightSourceType.MicrosoftFabric);
        fabric.Included.Should().BeFalse();
        fabric.AvailabilityState.Should().Be(KnightConsolidatedAvailability.OutOfScope);
        fabric.Score.Should().BeNull();
        fabric.NotEvaluatedCount.Should().BeNull();
        fabric.SourceRunId.Should().BeNull("uma fonte fora do escopo não é pinada nem publicada");
        fabric.CollectionLimitations.Should().BeEmpty();
        KnightConsolidatedAvailability.Label(fabric.AvailabilityState, fabric.Included).Should().Be("Fora do escopo");
        KnightConsolidatedCollection.StateOf(r.Sources).Should().Be(KnightSourceState.Completed,
            "a coleta parcial do Fabric não torna parcial uma avaliação da qual ele não faz parte");
    }

    [Fact]
    public void EscopoVenceASelecao_FabricPedidoContinuaFora()
    {
        var all = KnightConsolidatedCandidates.Sources;
        var r = KnightConsolidatedBuilder.Build(ReferenceCase(), all, new[] { KnightSourceType.MicrosoftFabric });

        r.IncludedSources.Should().NotContain(KnightSourceType.MicrosoftFabric);
        Applicable(r).Should().Be(243);
    }

    // ---- Persistência por tenant ---------------------------------------------------------------------------------

    [Fact]
    public async Task Escopo_SalvoPorTenant_NaoAfetaOutroTenant_ESobreviveARecarga()
    {
        await using (var dbA = NewContext(TenantA))
        {
            var saved = await ScopeFor(dbA, TenantA).SaveAsync(InScopeWithoutFabric(), "Pessoa Gestora");
            saved.OutOfScope.Should().Equal(KnightSourceType.MicrosoftFabric);
            saved.UpdatedByName.Should().Be("Pessoa Gestora");
            saved.UpdatedAt.Should().NotBeNull();
        }

        await using (var dbA2 = NewContext(TenantA))
            (await ScopeFor(dbA2, TenantA).GetAsync()).OutOfScope.Should().Equal(new[] { KnightSourceType.MicrosoftFabric }, "a escolha vale ao recarregar");

        await using (var dbB = NewContext(TenantB))
        {
            var b = await ScopeFor(dbB, TenantB).GetAsync();
            b.OutOfScope.Should().BeEmpty("a escolha de um tenant não afeta outro");
            b.UpdatedAt.Should().BeNull();
        }

        // Regravar devolve o Fabric ao escopo, no MESMO registro (um por tenant).
        await using (var dbA3 = NewContext(TenantA))
        {
            (await ScopeFor(dbA3, TenantA).SaveAsync(KnightConsolidatedCandidates.Sources.ToList(), null)).OutOfScope.Should().BeEmpty();
            (await dbA3.KnightAssessmentScopes.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public void Formato_IgnoraNomeDesconhecido_EOrdenaPeloCatalogo()
    {
        KnightAssessmentScopeFormat.Parse("MicrosoftAzure,Inexistente,MicrosoftFabric,MicrosoftFabric")
            .Should().Equal(KnightSourceType.MicrosoftFabric, KnightSourceType.MicrosoftAzure);
        KnightAssessmentScopeFormat.Serialize(new[] { KnightSourceType.MicrosoftAzure, KnightSourceType.MicrosoftFabric })
            .Should().Be("MicrosoftFabric,MicrosoftAzure");
        KnightAssessmentScopeFormat.Parse(null).Should().BeEmpty();
    }

    // ---- API ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Api_SalvaPorSlug_RecusaFonteDesconhecidaOuNaoElegivel()
    {
        await using var db = NewContext(TenantA);
        var controller = new KnightAssessmentsController(null!, new SystemTenantContext(TenantA), scope: ScopeFor(db, TenantA));

        (await controller.SaveScope(new SaveKnightAssessmentScopeRequest(new[] { "entra", "inexistente" }), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.SaveScope(new SaveKnightAssessmentScopeRequest(new[] { "google" }), default))
            .Result.Should().BeOfType<BadRequestObjectResult>("o Google Workspace não compõe o consolidado");
        (await controller.SaveScope(new SaveKnightAssessmentScopeRequest(null), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();

        var ok = (await controller.SaveScope(new SaveKnightAssessmentScopeRequest(
            new[] { "entra", "teams", "exchange", "defender-office365", "purview", "sharepoint", "intune", "azure" }), default))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<KnightAssessmentScopeDto>().Subject;
        ok.OutOfScope.Should().Equal("fabric");
        ok.InScope.Should().NotContain("fabric").And.HaveCount(8);

        var read = (await controller.GetScope(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<KnightAssessmentScopeDto>().Subject;
        read.OutOfScope.Should().Equal("fabric");
    }

    // ---- Publicação, prévia e relatório --------------------------------------------------------------------------

    [Fact]
    public async Task Publicacao_FabricForaDoEscopo_ComposicaoDizForaDoEscopo_TotaisSemFabric_OutroTenantInalterado()
    {
        await using (var dbA = NewContext(TenantA))
        {
            await SeedRunAsync(dbA, KnightSourceType.MicrosoftEntraId, "AK-ENTRA", passed: 3, exposed: 1, notEvaluated: 1);
            await SeedRunAsync(dbA, KnightSourceType.MicrosoftFabric, "AK-FAB", passed: 0, exposed: 0, notEvaluated: 12);
            await ScopeFor(dbA, TenantA).SaveAsync(InScopeWithoutFabric(), "Pessoa Gestora");

            var detail = await SnapshotsFor(dbA, TenantA).PublishConsolidatedKnightAsync(null);
            detail.Indicators.Should().HaveCount(5).And.OnlyContain(i => i.SourceType == "MicrosoftEntraId");
            var fabric = detail.Composition!.Single(c => c.Source == KnightSourceType.MicrosoftFabric);
            fabric.AvailabilityState.Should().Be("OutOfScope");
            fabric.Included.Should().BeFalse();
            fabric.SourceRunId.Should().BeNull();
            detail.Summary.NotEvaluatedCount.Should().Be(1, "os 12 do Fabric não entram");
        }

        await using (var dbB = NewContext(TenantB))
        {
            await SeedRunAsync(dbB, KnightSourceType.MicrosoftEntraId, "AK-ENTRA", passed: 3, exposed: 1, notEvaluated: 1);
            await SeedRunAsync(dbB, KnightSourceType.MicrosoftFabric, "AK-FAB", passed: 0, exposed: 0, notEvaluated: 12);
            var detail = await SnapshotsFor(dbB, TenantB).PublishConsolidatedKnightAsync(null);
            detail.Composition!.Single(c => c.Source == KnightSourceType.MicrosoftFabric).Included.Should().BeTrue(
                "o tenant B não retirou o Fabric do escopo");
            detail.Summary.NotEvaluatedCount.Should().Be(13);
        }
    }

    [Fact]
    public async Task Publicacao_FontePinadaForaDoEscopo_ERecusada_PreviaTambem()
    {
        await using var db = NewContext(TenantA);
        var entra = await SeedRunAsync(db, KnightSourceType.MicrosoftEntraId, "AK-ENTRA", passed: 1, exposed: 1, notEvaluated: 0);
        var fabric = await SeedRunAsync(db, KnightSourceType.MicrosoftFabric, "AK-FAB", passed: 0, exposed: 0, notEvaluated: 12);
        await ScopeFor(db, TenantA).SaveAsync(InScopeWithoutFabric(), null);

        var selection = new[]
        {
            new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftEntraId, entra),
            new KnightConsolidatedSourceSelection(KnightSourceType.MicrosoftFabric, fabric),
        };
        await FluentActions.Invoking(() => SnapshotsFor(db, TenantA).PublishConsolidatedKnightAsync(selection))
            .Should().ThrowAsync<PostureSnapshotNotAvailableException>().WithMessage("*fora do escopo*");
        await FluentActions.Invoking(() => SnapshotsFor(db, TenantA).PreviewConsolidatedHistoryAsync(selection, HistoryWindow.Default))
            .Should().ThrowAsync<PostureSnapshotNotAvailableException>().WithMessage("*fora do escopo*");

        var ok = await SnapshotsFor(db, TenantA).PublishConsolidatedKnightAsync(new[] { selection[0] });
        ok.Composition!.Single(c => c.Source == KnightSourceType.MicrosoftFabric).AvailabilityState.Should().Be("OutOfScope");
    }

    [Fact]
    public async Task RelatorioHtml_AbreEmGeral_ComAbasDasFontesIncluidas_DadosDaPropriaFotografia()
    {
        await using var db = NewContext(TenantA);
        await SeedRunAsync(db, KnightSourceType.MicrosoftEntraId, "AK-ENTRA", passed: 3, exposed: 1, notEvaluated: 1);
        await SeedRunAsync(db, KnightSourceType.MicrosoftTeams, "AK-TEAMS", passed: 2, exposed: 2, notEvaluated: 0);
        await SeedRunAsync(db, KnightSourceType.MicrosoftFabric, "AK-FAB", passed: 0, exposed: 0, notEvaluated: 12);
        await ScopeFor(db, TenantA).SaveAsync(InScopeWithoutFabric(), null);
        var detail = await SnapshotsFor(db, TenantA).PublishConsolidatedKnightAsync(null);

        var snapshot = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects)
            .SingleAsync(s => s.Id == detail.Summary.Id);
        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.SourceTabs.Should().NotBeNull();
        model.SourceTabs!.Select(t => t.Key).Should().Equal("MicrosoftEntraId", "MicrosoftTeams");
        var teams = model.SourceTabs![1];
        (teams.Evaluated, teams.Applicable, teams.Failed, teams.Coverage).Should().Be((4, 4, 2, 100.0));
        model.Controls.Should().OnlyContain(c => c.Source == "MicrosoftEntraId" || c.Source == "MicrosoftTeams");
        model.Notes.Should().Contain(n => n.Contains("fora do escopo salvo", StringComparison.Ordinal));

        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("\"sourceTabs\":[").And.Contain("\"key\":\"MicrosoftTeams\"").And.Contain("\"availabilityState\":\"OutOfScope\"");
        html.Should().Contain("[['geral','Geral']]", "a navegação abre em Geral e lista as fontes incluídas a partir da ilha de dados")
            .And.Contain("'Fora do escopo'")
            .And.Contain(".tabs[hidden]{display:none}", "na aba de uma fonte, as abas de seções de Geral somem de fato")
            .And.Contain("querySelectorAll('.tabs button[data-tab]')", "trocar de seção não desmarca a aba de fonte");
        html.Should().NotContain("src=\"http", "nada externo carrega — o arquivo funciona offline");

        // A CSP continua autorizando exatamente o script embutido (hash sobre o texto normalizado).
        var script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var hash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        html.Should().Contain("script-src '" + hash + "'");
    }

    [Fact]
    public void RelatorioDeFonteUnica_NaoTemAbasDeFonte()
    {
        KnightReportModelBuilder.SourceTabs(null).Should().BeEmpty();
    }

    // ---- Apoio ----------------------------------------------------------------------------------------------------

    private static int Evaluated(KnightConsolidatedAssessment r) => r.PassedCount + r.ExposedCount + r.MitigatedCount;
    private static int Applicable(KnightConsolidatedAssessment r) => Evaluated(r) + r.NotEvaluatedCount + r.ErrorCount;

    private static IReadOnlyCollection<KnightSourceType> InScopeWithoutFabric() =>
        KnightConsolidatedCandidates.Sources.Where(s => s != KnightSourceType.MicrosoftFabric).ToList();

    /// <summary>
    /// 201 avaliados e 255 aplicáveis: Entra 100/110, Teams 35/41, Exchange 46/56, Azure 20/36 (201/243) e Fabric 0/12, este com a
    /// leitura das configurações do locatário recusada.
    /// </summary>
    private static KnightLatestBySource ReferenceCase() => new(new[]
    {
        Latest(KnightSourceType.MicrosoftEntraId, "AK-ENTRA", passed: 80, exposed: 20, mitigated: 0, notEvaluated: 10),
        Latest(KnightSourceType.MicrosoftTeams, "AK-TEAMS", passed: 30, exposed: 5, mitigated: 0, notEvaluated: 6),
        Latest(KnightSourceType.MicrosoftExchangeOnline, "AK-EXO", passed: 40, exposed: 0, mitigated: 6, notEvaluated: 10),
        Latest(KnightSourceType.MicrosoftAzure, "AK-AZ", passed: 20, exposed: 0, mitigated: 0, notEvaluated: 16),
        Latest(KnightSourceType.MicrosoftFabric, "AK-FAB", passed: 0, exposed: 0, mitigated: 0, notEvaluated: 12,
            new KnightCapabilityStatus(KnightCapability.FabricTenantSettings, KnightCapabilityOutcome.InsufficientPermission, "403")),
    });

    private static KnightSourceLatest Latest(KnightSourceType source, string prefix, int passed, int exposed, int mitigated, int notEvaluated,
        params KnightCapabilityStatus[] capabilities)
    {
        var indicators = new List<KnightIndicatorView>();
        void Add(int n, KnightIndicatorStatus status)
        {
            for (var i = 0; i < n; i++)
                indicators.Add(new KnightIndicatorView(
                    $"{prefix}-{status}-{i:000}", "controle", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium, status, "evidência",
                    0, Array.Empty<string>(), Array.Empty<string>(), "recomendação", DateTimeOffset.UtcNow.AddMinutes(-6), source, null));
        }
        Add(passed, KnightIndicatorStatus.Passed);
        Add(exposed, KnightIndicatorStatus.Exposed);
        Add(mitigated, KnightIndicatorStatus.Mitigated);
        Add(notEvaluated, KnightIndicatorStatus.NotEvaluated);
        var score = KnightScoreFormula.Compute(indicators.Select(i => (i.Severity, i.Status)));
        var label = KnightSourceCatalog.Label(source);
        return new KnightSourceLatest(source, label, new KnightAssessment(
            Guid.NewGuid(), KnightAssessmentMode.Live, source, capabilities.Length > 0 ? KnightSourceState.PartialCollection : KnightSourceState.Completed,
            label, KnightRunStatus.Completed, "ak-knight-v13", KnightScoreFormula.Version,
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-5),
            score.Score, score.Coverage, score.PassedCount, score.ExposedCount, score.MitigatedCount, score.NotEvaluatedCount,
            score.ErrorCount, score.NotApplicableCount, indicators, capabilities, null, false), null);
    }

    private static async Task<Guid> SeedRunAsync(AegisScoreDbContext db, KnightSourceType source, string prefix, int passed, int exposed, int notEvaluated)
    {
        var statuses = Enumerable.Repeat(KnightIndicatorStatus.Passed, passed)
            .Concat(Enumerable.Repeat(KnightIndicatorStatus.Exposed, exposed))
            .Concat(Enumerable.Repeat(KnightIndicatorStatus.NotEvaluated, notEvaluated)).ToList();
        var score = KnightScoreFormula.Compute(statuses.Select(s => (SeverityLevel.High, s)));
        var run = new KnightAssessmentRun
        {
            Mode = KnightAssessmentMode.Live, SourceType = source, SourceState = KnightSourceState.Completed,
            Source = KnightSourceCatalog.Label(source), Status = KnightRunStatus.Completed,
            CatalogVersion = "ak-knight-v13", ScoreFormulaVersion = KnightScoreFormula.Version,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-25),
            Score = score.Score, Coverage = score.Coverage, PassedCount = score.PassedCount, ExposedCount = score.ExposedCount,
            NotEvaluatedCount = score.NotEvaluatedCount,
        };
        for (var i = 0; i < statuses.Count; i++)
            run.Indicators.Add(new KnightIndicatorResult
            {
                IndicatorId = $"{prefix}-{i:000}", Title = $"Controle {i}", Category = KnightIndicatorCategory.PrivilegedAccess,
                Severity = SeverityLevel.High, Status = statuses[i], Evidence = "evidência", SourceType = source,
                CollectedAt = DateTimeOffset.UtcNow.AddMinutes(-26),
            });
        db.KnightAssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private AegisScoreDbContext NewContext(Guid tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private static KnightAssessmentScopeService ScopeFor(AegisScoreDbContext db, Guid tenantId) =>
        new(db, new SystemTenantContext(tenantId), TimeProvider.System);

    private static IPostureSnapshotService SnapshotsFor(AegisScoreDbContext db, Guid tenantId) =>
        new PostureSnapshotService(db, new SystemTenantContext(tenantId));
}
