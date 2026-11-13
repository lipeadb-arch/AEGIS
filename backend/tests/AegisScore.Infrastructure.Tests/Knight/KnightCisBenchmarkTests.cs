using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Api.Controllers;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Cis;
using AegisScore.Application.Knight.Reference;
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
/// [AEGIS-KNIGHT-CIS-01] Visão CIS Benchmarks: resultado por recomendação a partir da evidência, a mesma projeção na tela e no HTML.
/// Todos os dados são SINTÉTICOS. O caso de referência reproduz a composição do exemplo: 201 de 243 controles avaliados no KNIGHT
/// (51 aprovados, 150 reprovados, 42 não avaliados e 119 não aplicáveis), o Fabric fora do escopo e o Teams com 0 de 17 avaliados.
/// </summary>
public sealed class KnightCisBenchmarkTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("c15c15c1-5c15-4c15-8c15-c15c15c15c15");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 14, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;

    public KnightCisBenchmarkTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Catálogo, rastreio e política --------------------------------------------------------------------------

    [Fact]
    public void Catalogo_457Entradas_435RecomendacoesNosQuatroBenchmarks_MaisUmaVerificacaoPropria()
    {
        KnightReferenceCatalog.Controls.Should().HaveCount(457);
        KnightCisTrace.Keys.Should().BeEquivalentTo(KnightReferenceCatalog.Controls.Select(c => c.Key), "toda entrada do catálogo tem rastreio");
        KnightCisTrace.Benchmarks.Select(b => b.Label).Should().Equal(
            "Microsoft 365 Foundations 7.0.0", "Azure Foundations 6.0.0", "Azure Compute Services 2.0.0", "Azure Database Services 2.0.0");

        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        view.Recommendations.Should().HaveCount(435);
        view.Benchmarks.Select(b => (b.Key, b.Recommendations, b.CatalogEntries)).Should().Equal(
            ("CIS-M365-7.0.0", 161, 165), ("CIS-AZ-6.0.0", 127, 132), ("CIS-AZC-2.0.0", 98, 101), ("CIS-AZD-2.0.0", 49, 59));
        view.Recommendations.Select(r => r.Id).Should().OnlyHaveUniqueItems("cada recomendação conta uma vez");
        view.Limitations.Should().Contain(l => l.Contains("CIS-AZC-2.0.0:s/n#plano-endpoint-privado"), "a entrada sem seção é verificação própria do KNIGHT");
    }

    [Fact]
    public void Politica_TodoVinculoParcialAtivo_TemDecisaoEscrita_ETodaRecomendacaoComVariantes_TemCombinacao()
    {
        var active = KnightCatalog.Indicators.Where(KnightCollectorCapabilities.IsActive)
            .SelectMany(d => d.References.Where(l => l.Match == KnightReferenceMatch.Partial).Select(l => (l.Key, d.Id)))
            .ToList();
        active.Should().NotBeEmpty();
        KnightCisEvidencePolicy.DeclaredPartialLinks.Should().Contain(active, "um vínculo parcial novo exige decisão sobre o que ele prova");

        var multi = KnightReferenceCatalog.Controls.GroupBy(c => KnightCisProjection.RecommendationId(c.Key))
            .Where(g => g.Count() > 1 && !g.Key.Contains("s/n")).Select(g => g.Key).ToList();
        multi.Should().HaveCount(15);
        KnightCisEvidencePolicy.DeclaredGroups.Should().BeEquivalentTo(multi);
    }

    [Fact]
    public void Rastreio_PerfilPreservaLicencaENivel_ELimitacaoQuandoAFonteNaoRegistra()
    {
        KnightCisTrace.ProfileLabel("E3 Level 1").Should().Be("E3 · Nível 1");
        KnightCisTrace.ProfileLabel("E5 Level 2").Should().Be("E5 · Nível 2");
        KnightCisTrace.ProfileLabel("Level 2").Should().Be("Nível 2");

        KnightCisTrace.For("CIS-AZ-6.0.0:2.1.3")!.Profiles.Should().Equal("Level 2");
        KnightCisTrace.For("CIS-AZD-2.0.0:3.1")!.ProfileState.Should().Be(KnightCisProfileState.Divergent);
        KnightCisTrace.For("CIS-AZD-2.0.0:6.5")!.ProfileState.Should().Be(KnightCisProfileState.Ambiguous, "regra compartilhada com os dois níveis");
        KnightCisTrace.For("CIS-AZC-2.0.0:s/n#plano-endpoint-privado")!.ProfileState.Should().Be(KnightCisProfileState.NotTraced);

        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        var m365 = view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0");
        m365.Profiles.Select(p => p.Key).Should().Contain(new[] { "E3 Level 1", "E5 Level 1" });
        m365.ProfileNotRecorded.Should().BeGreaterThan(0, "a fonte de rastreio não registra perfil para parte da edição 7.0.0");
        var divergent = view.Recommendations.Single(r => r.Id == "CIS-AZD-2.0.0:3.1");
        divergent.Profiles.Should().BeEmpty();
        divergent.Limitations.Should().Contain(l => l.Contains("divergente"));
        foreach (var b in view.Benchmarks)
            foreach (var p in b.Profiles)
                p.Recommendations.Should().Be(view.Recommendations.Count(r => r.Benchmark == b.Key && r.Profiles.Contains(p.Key)), "a recomendação conta uma vez no perfil");
    }

    // ---- Caso de referência ------------------------------------------------------------------------------------

    [Fact]
    public void CasoDeReferencia_KnightReconcilia201de243_51_150_42()
    {
        var c = KnightConsolidatedBuilder.Build(ReferenceComposition(), null, new[] { KnightSourceType.MicrosoftFabric });
        (c.PassedCount + c.ExposedCount + c.MitigatedCount).Should().Be(201);
        (c.PassedCount + c.ExposedCount + c.MitigatedCount + c.NotEvaluatedCount + c.ErrorCount).Should().Be(243);
        (c.PassedCount, c.ExposedCount, c.NotEvaluatedCount, c.NotApplicableCount).Should().Be((51, 150, 42, 119));
        c.Sources.Single(s => s.Source == KnightSourceType.MicrosoftTeams).PassedCount.Should().Be(0);
        c.Sources.Single(s => s.Source == KnightSourceType.MicrosoftTeams).NotEvaluatedCount.Should().Be(17);
        c.Sources.Single(s => s.Source == KnightSourceType.MicrosoftFabric).AvailabilityState.Should().Be("OutOfScope");
    }

    [Fact]
    public void CasoDeReferencia_TotaisCisReconciliamComAsProprisLinhas_EExplicamADiferencaParaOKnight()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));

        foreach (var b in view.Benchmarks)
        {
            var rows = view.Recommendations.Where(r => r.Benchmark == b.Key).ToList();
            b.Counts.Total.Should().Be(rows.Count, b.Key);
            b.Counts.Should().BeEquivalentTo(KnightCisProjection.CountsOf(rows), b.Key);
            (b.Counts.Considered + b.Counts.OutOfScope).Should().Be(rows.Count);
            b.ManualCounts.Total.Should().Be(rows.Count(r => r.Origin == "Manual"));
        }

        var r = view.Reconciliation;
        (r.KnightEvaluated, r.KnightApplicable, r.KnightNotApplicable, r.KnightControls).Should().Be((201, 243, 119, 362));
        r.RulesInComposition.Should().Be(362);
        (r.RulesLinked + r.OwnChecks).Should().Be(362, "toda regra incluída sustenta recomendação CIS ou é verificação própria");
        view.Rules.Should().HaveCount(r.RulesLinked);
        view.OwnChecks.Should().HaveCount(r.OwnChecks).And.OnlyContain(o => o.Note.Contains("Verificação própria do KNIGHT"));
        view.OwnChecks.Should().Contain(o => o.IndicatorId.StartsWith("AK-ENTRA-"), "as verificações próprias de Entra ID ficam identificáveis");
        r.Recommendations.Should().Be(435);
        r.Considered.Should().Be(435 - r.OutOfScope);
        r.Explanation.Should().Contain(x => x.Contains("O KNIGHT conta regras"))
            .And.Contain(x => x.Contains("Fora do escopo") && x.Contains("Microsoft Fabric (Power BI)"))
            .And.Contain(x => x.StartsWith($"Totais CIS: {r.Considered} recomendações consideradas"));
        r.Evaluated.Should().NotBe(201, "os totais CIS contam recomendações, não as 201 regras avaliadas — e a explicação diz por quê");
    }

    [Fact]
    public void CasoDeReferencia_FabricForaDoEscopo_ForaDosTotais_TeamsSemAvaliacaoComMotivo()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));

        var fabric = view.Recommendations.Where(r => r.Service == nameof(KnightService.Fabric)).ToList();
        fabric.Should().NotBeEmpty().And.OnlyContain(r => r.Status == KnightCisStatus.OutOfScope);
        fabric.Should().OnlyContain(r => r.Reason!.Contains("fora do escopo salvo"));
        view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0").Counts.OutOfScope.Should().Be(fabric.Count);
        view.Origin.Sources.Single(s => s.Source == "MicrosoftFabric").AvailabilityLabel.Should().Be("Fora do escopo");

        var teams = view.Recommendations.Where(r => r.Service == nameof(KnightService.Teams) && r.RuleIds.Any(id => id.StartsWith("AK-TEAMS-"))).ToList();
        teams.Should().NotBeEmpty();
        teams.Should().OnlyContain(r => r.Status == KnightCisStatus.NotEvaluated);
        teams.Where(r => r.Id != "CIS-M365-7.0.0:8.6.1").Should().OnlyContain(r => r.Reason!.Contains("Autorização recusada pela Microsoft (sintético)"),
            "o motivo específico da regra vai junto");
        view.Recommendations.Should().NotContain(r => r.Status == KnightCisStatus.Compliant && r.RuleIds.Any(id => id.StartsWith("AK-TEAMS-")));
    }

    [Fact]
    public void CasoDeReferencia_VinculoParcial_Composta_VariantesEConflito()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        KnightCisRecommendation R(string id) => view.Recommendations.Single(r => r.Id == id);

        // Parcial que prova a violação: abaixo de 90% com MFA já prova que nem todos são capazes de MFA.
        R("CIS-M365-7.0.0:5.2.3.4").Status.Should().Be(KnightCisStatus.NonCompliant);
        R("CIS-M365-7.0.0:5.2.3.4").Variants.Single().Rules.Single(x => x.IndicatorId == "AK-ENTRA-006").Proof.Should().Be("ProvesViolation");
        // Parcial aprovada que não prova o critério: security defaults não é a política que a recomendação pede.
        var mfaAll = R("CIS-M365-7.0.0:5.2.2.2");
        mfaAll.Status.Should().Be(KnightCisStatus.NotEvaluated);
        mfaAll.Reason.Should().Contain("AK-ENTRA-014 (aprovado) avalia só parte do critério");
        mfaAll.Limitations.Should().Contain(l => l.StartsWith("Equivalência parcial — AK-ENTRA-014"));

        // Composta: as duas partes aprovadas → conforme; uma parte sem avaliação → não avaliada, dizendo qual.
        R("CIS-M365-7.0.0:5.2.3.5").Status.Should().Be(KnightCisStatus.Compliant);
        R("CIS-M365-7.0.0:5.2.3.5").RuleIds.Should().BeEquivalentTo("AK-ENTRA-029", "AK-ENTRA-031");
        var idle = R("CIS-M365-7.0.0:1.3.2");
        idle.Status.Should().Be(KnightCisStatus.NotEvaluated);
        idle.Reason.Should().StartWith("Critério composto: nem todas as partes estão comprovadas.").And.Contain("AK-ENTRA-075");
        R("CIS-M365-7.0.0:8.6.1").Status.Should().Be(KnightCisStatus.NotEvaluated, "a parte do Teams não foi avaliada");

        // Variantes: uma recomendação, várias entradas do catálogo.
        var udp = R("CIS-AZ-6.0.0:7.3");
        udp.Variants.Should().HaveCount(5);
        view.Recommendations.Count(r => r.Id.StartsWith("CIS-AZ-6.0.0:7.3")).Should().Be(1);
        var conflict = R("CIS-AZC-2.0.0:2.4.11");
        conflict.Status.Should().Be(KnightCisStatus.NotEvaluated);
        conflict.Reason.Should().Contain("dois critérios diferentes");
    }

    [Fact]
    public void CasoDeReferencia_SemRegraAutomatizada_ManualOuMotivoDaDisposicao()
    {
        var manual = new[]
        {
            Manual("CIS-M365-7.0.0:5.2.4.1", KnightManualResult.Compliant, new DateOnly(2027, 6, 30), expired: false),
            Manual("CIS-M365-7.0.0:5.1.2.6", KnightManualResult.NonCompliant, new DateOnly(2026, 10, 1), expired: true),
        };
        var view = KnightCisProjection.Build(Input(ReferenceComposition(), manual));
        KnightCisRecommendation R(string id) => view.Recommendations.Single(r => r.Id == id);

        var sspr = R("CIS-M365-7.0.0:5.2.4.1");
        sspr.Status.Should().Be(KnightCisStatus.Compliant);
        sspr.Origin.Should().Be("Manual");
        sspr.OriginLabel.Should().Be("Verificação manual");
        sspr.Manual!.ResponsibleName.Should().Be("Pessoa Responsável (sintético)");
        sspr.Manual.ValidUntil.Should().Be("2027-06-30");
        sspr.Found.Should().Contain("Verificação manual registrada como conforme");

        var linkedIn = R("CIS-M365-7.0.0:5.1.2.6");
        linkedIn.Status.Should().Be(KnightCisStatus.NotEvaluated, "verificação manual vencida não vale");
        linkedIn.Reason.Should().Contain("venceu em 2026-10-01");
        linkedIn.Manual!.Expired.Should().BeTrue();

        var bg = R("CIS-M365-7.0.0:1.1.2");
        bg.Status.Should().Be(KnightCisStatus.NotEvaluated);
        bg.Reason.Should().StartWith("Verificação manual:");
        bg.HowToFix.Should().Contain("registre o resultado manual");

        view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0").ManualCounts.Compliant.Should().Be(1);
    }

    [Fact]
    public void Mitigacao_NaoEhConformidade()
    {
        var comp = ReferenceComposition(new Dictionary<string, KnightIndicatorStatus> { ["AK-ENTRA-007"] = KnightIndicatorStatus.Mitigated });
        var r = KnightCisProjection.Build(Input(comp)).Recommendations.Single(x => x.Id == "CIS-M365-7.0.0:5.2.2.3");
        r.Status.Should().Be(KnightCisStatus.NonCompliant);
        r.Mitigated.Should().BeTrue();
        r.StatusLabel.Should().Be("Não conforme");
    }

    [Fact]
    public void Composta_UmaParteReprovada_ProvaAViolacao()
    {
        var comp = ReferenceComposition(new Dictionary<string, KnightIndicatorStatus> { ["AK-ENTRA-031"] = KnightIndicatorStatus.Exposed });
        var r = KnightCisProjection.Build(Input(comp)).Recommendations.Single(x => x.Id == "CIS-M365-7.0.0:5.2.3.5");
        r.Status.Should().Be(KnightCisStatus.NonCompliant);
        r.Found.Should().Contain("AK-ENTRA-031 reprovou");
    }

    [Fact]
    public void AvaliacaoHistorica_SemMapeamentoConfirmado_FicaNaoAvaliadaComALimitacao()
    {
        var comp = ReferenceComposition(catalogOf: s => s == KnightSourceType.MicrosoftAzure ? "ak-knight-v12" : KnightCatalog.Version);
        var view = KnightCisProjection.Build(Input(comp));

        var azure = view.Recommendations.Where(r => r.RuleIds.Count > 0 && r.RuleIds.All(id => id.StartsWith("AK-AZ-"))
            && r.Variants.Any(v => v.Disposition is "Implemented" or "Partial")).ToList();
        azure.Should().NotBeEmpty();
        azure.Where(r => r.Status != KnightCisStatus.NotEvaluated).Select(r => r.Id).Should().BeEmpty();
        azure.Where(r => r.Id != "CIS-AZC-2.0.0:2.4.11" && !r.Reason!.Contains("catálogo ak-knight-v12")).Select(r => r.Id + " → " + r.Reason)
            .Should().BeEmpty("o motivo diz a versão executada (o conflito de identificação continua com o motivo dele)");
        view.Origin.Sources.Single(s => s.Source == "MicrosoftAzure").MappingReliable.Should().BeFalse();
        view.Limitations.Should().Contain(l => l.StartsWith("Avaliação histórica sem mapeamento confirmado") && l.Contains("Microsoft Azure"));
        view.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:5.2.3.4").Status.Should().Be(KnightCisStatus.NonCompliant, "as demais fontes seguem");
    }

    [Fact]
    public void Projecao_EhDeterministica()
    {
        var composition = ReferenceComposition();
        var a = KnightCisJson.Serialize(KnightCisProjection.Build(Input(composition)));
        var b = KnightCisJson.Serialize(KnightCisProjection.Build(Input(composition)));
        (a == b).Should().BeTrue("a mesma composição produz os mesmos bytes");
        KnightCisJson.Deserialize(a)!.Recommendations.Should().HaveCount(435);
    }

    // ---- API, publicação e HTML ---------------------------------------------------------------------------------

    [Fact]
    public async Task Api_ProjetaAComposicaoDoEscopoSalvo()
    {
        await using var db = NewContext(TenantA);
        await new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System)
            .SaveAsync(KnightConsolidatedCandidates.Sources.Where(s => s != KnightSourceType.MicrosoftFabric).ToList(), "Pessoa Gestora");
        var controller = new KnightAssessmentsController(new FixedLatest(ReferenceComposition()), new SystemTenantContext(TenantA),
            new KnightManualResultService(db, new SystemTenantContext(TenantA), TimeProvider.System),
            scope: new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System));

        var view = (await controller.GetCisBenchmarks(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<KnightCisView>().Subject;
        view.Reconciliation.KnightEvaluated.Should().Be(201);
        view.Reconciliation.KnightApplicable.Should().Be(243);
        view.Origin.Sources.Single(s => s.Source == "MicrosoftFabric").Availability.Should().Be("OutOfScope");
        view.Recommendations.Where(r => r.Service == nameof(KnightService.Fabric)).Should().OnlyContain(r => r.Status == KnightCisStatus.OutOfScope);
    }

    [Fact]
    public async Task Publicacao_CongelaAVisaoCis_SobOHash_EOHtmlAbreOfflineComAAba()
    {
        await using var db = NewContext(TenantA);
        await SeedAsync(db, ReferenceComposition());
        db.KnightManualAssessments.Add(new KnightManualAssessment
        {
            ReferenceKey = "CIS-M365-7.0.0:5.2.4.1", Result = KnightManualResult.Compliant, Justification = "Conferido no portal (sintético).",
            ResponsibleName = "Pessoa Responsável (sintético)", ValidUntil = new DateOnly(2099, 1, 1), ReferenceDisposition = "ApiLimitation",
            CatalogVersion = KnightCatalog.Version, RecordedByName = "Pessoa Gestora (sintético)", RecordedAt = Now,
        });
        await db.SaveChangesAsync();
        await new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System)
            .SaveAsync(KnightConsolidatedCandidates.Sources.Where(s => s != KnightSourceType.MicrosoftFabric).ToList(), "Pessoa Gestora");

        var detail = await new PostureSnapshotService(db, new SystemTenantContext(TenantA)).PublishConsolidatedKnightAsync(null);
        detail.Summary.EvaluatedItems.Should().Be(201);
        detail.Summary.EligibleItems.Should().Be(243);

        var snapshot = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
            .AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        snapshot.CisBenchmarkJson.Should().NotBeNullOrEmpty();
        PostureSnapshotHasher.Compute(snapshot).Should().Be(snapshot.ContentHash, "a visão CIS entra no hash e re-deriva");
        var frozen = KnightCisJson.Deserialize(snapshot.CisBenchmarkJson)!;
        frozen.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:5.2.4.1").Origin.Should().Be("Manual");
        frozen.Origin.Sources.Where(s => s.Included).Should().OnlyContain(s => s.RunId != null && s.CatalogVersion == KnightCatalog.Version);

        // Tela e HTML: a MESMA projeção sobre a mesma composição.
        var live = KnightCisProjection.Build(KnightCisInputs.FromConsolidated(
            KnightConsolidatedBuilder.Build(ReferenceComposition(), null, new[] { KnightSourceType.MicrosoftFabric }),
            await new KnightManualResultService(db, new SystemTenantContext(TenantA), TimeProvider.System).CurrentAsync()));
        frozen.Benchmarks.Select(b => b.Counts).Should().BeEquivalentTo(live.Benchmarks.Select(b => b.Counts));
        frozen.Recommendations.Select(r => (r.Id, r.Status)).Should().Equal(live.Recommendations.Select(r => (r.Id, r.Status)));

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.CisState.Should().Be("Frozen");
        model.Notes.Should().NotContain(n => n.Contains("produzido pelo catálogo ak-knight-consolidated"),
            "ak-knight-consolidated identifica a composição, não a versão da regra");
        model.Notes.Should().Contain(n => n.Contains($"usam o mesmo catálogo, {KnightCatalog.Version}"));

        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("['cis','CIS Benchmarks']").And.Contain("\"cisState\":\"Frozen\"").And.Contain("\"schemaVersion\":\"knight-cis-v1\"");
        html.Should().Contain("Resumo da avaliação").And.Contain("'Resultados por fonte'").And.Contain("function limGroups(");
        html.Should().NotContain("src=\"http").And.NotContain("href=\"http://", "o arquivo funciona offline, sem nada externo");
        var script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        html.Should().Contain("script-src 'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'");
    }

    [Fact]
    public async Task FotografiaAnterior_SemVisaoCis_AbreComOQueRegistrou_EOHashContinuaValido()
    {
        await using var db = NewContext(TenantA);
        await SeedAsync(db, ReferenceComposition());
        var detail = await new PostureSnapshotService(db, new SystemTenantContext(TenantA)).PublishConsolidatedKnightAsync(null);

        // Simula a fotografia publicada antes deste recurso: sem a visão CIS e com o hash daquele formato (a fotografia é append-only
        // pelo EF, então o ajuste do cenário é direto no banco).
        var old = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
            .AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        var oldHash = PostureSnapshotHasher.Compute(WithoutCis(old));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PostureSnapshots SET CisBenchmarkJson = NULL, ContentHash = {oldHash} WHERE Id = {detail.Summary.Id}");
        db.ChangeTracker.Clear();

        var snapshot = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
            .AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        PostureSnapshotHasher.Compute(snapshot).Should().Be(snapshot.ContentHash);
        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.Cis.Should().BeNull();
        model.CisState.Should().Be("NotRecorded");
        var export = await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html);
        Encoding.UTF8.GetString(export!.Content).Should().Contain("\"cisState\":\"NotRecorded\"").And.Contain("anterior à visão CIS Benchmarks");
    }

    // ---- Composição sintética ------------------------------------------------------------------------------------

    /// <summary>Contagens por fonte do caso de referência (aprovados, reprovados, não avaliados, não aplicáveis).</summary>
    private static readonly (KnightSourceType Source, int P, int E, int Ne, int Na)[] Reference =
    {
        (KnightSourceType.MicrosoftEntraId, 14, 53, 9, 0),
        (KnightSourceType.MicrosoftTeams, 0, 0, 17, 0),
        (KnightSourceType.MicrosoftExchangeOnline, 4, 12, 1, 0),
        (KnightSourceType.MicrosoftDefenderForOffice365, 5, 13, 0, 1),
        (KnightSourceType.MicrosoftPurview, 1, 0, 4, 0),
        (KnightSourceType.MicrosoftSharePoint, 1, 4, 8, 0),
        (KnightSourceType.MicrosoftIntune, 1, 1, 0, 0),
        (KnightSourceType.MicrosoftFabric, 0, 0, 12, 0),
        (KnightSourceType.MicrosoftAzure, 25, 67, 3, 118),
    };

    /// <summary>Resultados fixados para exercitar vínculo parcial, composição e motivo de não avaliado.</summary>
    private static readonly IReadOnlyDictionary<string, KnightIndicatorStatus> Fixed = new Dictionary<string, KnightIndicatorStatus>
    {
        ["AK-ENTRA-006"] = KnightIndicatorStatus.Exposed,      // parcial que prova a violação (5.2.3.4)
        ["AK-ENTRA-014"] = KnightIndicatorStatus.Passed,       // parcial aprovada que não prova (5.2.2.2)
        ["AK-ENTRA-029"] = KnightIndicatorStatus.Passed,       // composta 5.2.3.5, parte 1
        ["AK-ENTRA-031"] = KnightIndicatorStatus.Passed,       // composta 5.2.3.5, parte 2
        ["AK-ENTRA-069"] = KnightIndicatorStatus.Passed,       // composta 1.3.2, parte 1
        ["AK-ENTRA-075"] = KnightIndicatorStatus.NotEvaluated, // composta 1.3.2, parte 2 sem avaliação
        ["AK-MDO-019"] = KnightIndicatorStatus.Passed,         // composta 8.6.1 (a parte do Teams não foi avaliada)
        ["AK-ENTRA-007"] = KnightIndicatorStatus.Passed,
    };

    private static KnightLatestBySource ReferenceComposition(IReadOnlyDictionary<string, KnightIndicatorStatus>? overrides = null,
        Func<KnightSourceType, string>? catalogOf = null)
    {
        var pins = new Dictionary<string, KnightIndicatorStatus>(Fixed);
        foreach (var (k, v) in overrides ?? new Dictionary<string, KnightIndicatorStatus>()) pins[k] = v;
        var list = new List<KnightSourceLatest>();
        foreach (var (source, p, e, ne, na) in Reference)
        {
            var defs = KnightCatalog.ForSource(source).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
            defs.Should().HaveCount(p + e + ne + na, $"o catálogo de {source} tem o tamanho do caso de referência");
            var left = new Dictionary<KnightIndicatorStatus, int>
            {
                [KnightIndicatorStatus.Passed] = p, [KnightIndicatorStatus.Exposed] = e,
                [KnightIndicatorStatus.NotEvaluated] = ne, [KnightIndicatorStatus.NotApplicable] = na,
            };
            var status = new Dictionary<string, KnightIndicatorStatus>(StringComparer.Ordinal);
            foreach (var d in defs.Where(d => pins.ContainsKey(d.Id)))
            {
                var s = pins[d.Id];
                var counted = s == KnightIndicatorStatus.Mitigated ? KnightIndicatorStatus.Exposed : s;
                left[counted]--;
                status[d.Id] = s;
            }
            var order = new[] { KnightIndicatorStatus.Exposed, KnightIndicatorStatus.NotEvaluated, KnightIndicatorStatus.Passed, KnightIndicatorStatus.NotApplicable };
            foreach (var d in defs.Where(d => !status.ContainsKey(d.Id)))
            {
                var s = order.First(x => left[x] > 0);
                left[s]--;
                status[d.Id] = s;
            }
            left.Values.Should().OnlyContain(n => n == 0);

            var label = KnightSourceCatalog.Label(source);
            var catalog = catalogOf?.Invoke(source) ?? KnightCatalog.Version;
            var views = defs.Select(d => View(d, status[d.Id], source, catalog)).ToList();
            var score = KnightScoreFormula.Compute(views.Select(v => (v.Severity, v.Status)));
            var state = ne > 0 && p + e == 0 ? KnightSourceState.InsufficientPermission : ne > 0 ? KnightSourceState.PartialCollection : KnightSourceState.Completed;
            list.Add(new KnightSourceLatest(source, label, new KnightAssessment(
                Guid.NewGuid(), KnightAssessmentMode.Live, source, state, label, KnightRunStatus.Completed, catalog, KnightScoreFormula.Version,
                Now.AddMinutes(-40), Now.AddMinutes(-30), score.Score, score.Coverage, score.PassedCount, score.ExposedCount, score.MitigatedCount,
                score.NotEvaluatedCount, score.ErrorCount, score.NotApplicableCount, views, Array.Empty<KnightCapabilityStatus>(), null, false), null));
        }
        return new KnightLatestBySource(list);
    }

    private static KnightIndicatorView View(KnightIndicatorDefinition d, KnightIndicatorStatus status, KnightSourceType source, string catalog)
    {
        var reason = status == KnightIndicatorStatus.NotEvaluated
            ? source == KnightSourceType.MicrosoftTeams ? "Autorização recusada pela Microsoft (sintético)" : "Leitura incompleta nesta coleta (sintético)"
            : null;
        var affected = status is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated ? 3 : 0;
        return new KnightIndicatorView(d.Id, d.Title, d.Category, d.Severity, status,
            status switch
            {
                KnightIndicatorStatus.Passed => "Configuração conforme o critério (sintético).",
                KnightIndicatorStatus.Exposed => "3 itens fora do critério (sintético).",
                KnightIndicatorStatus.Mitigated => "Exposição com controle compensatório comprovado (sintético).",
                KnightIndicatorStatus.NotApplicable => "Nenhum recurso deste tipo no escopo (sintético).",
                _ => "Não avaliado: " + reason,
            },
            affected, d.NistCodes, d.MitreTechniques, d.Recommendation, Now.AddMinutes(-35), source, reason,
            HasAffectedDetail: true, AffectedDetailComplete: true, Presentation:
            KnightControlPresentations.For(d.Id, d.Category, d.Severity, status, source, d.NistCodes, d.MitreTechniques, catalog),
            AffectedComposition: affected > 0 ? "3 contas de usuário" : null);
    }

    private static KnightCisInput Input(KnightLatestBySource latest, IReadOnlyList<KnightManualResultView>? manual = null) =>
        KnightCisInputs.FromConsolidated(KnightConsolidatedBuilder.Build(latest, null, new[] { KnightSourceType.MicrosoftFabric }),
            manual ?? Array.Empty<KnightManualResultView>());

    private static KnightManualResultView Manual(string key, KnightManualResult result, DateOnly validUntil, bool expired) => new(
        Guid.NewGuid(), key, result, KnightManualResults.ResultLabel(result), "Conferido no portal do serviço (sintético).",
        "Pessoa Responsável (sintético)", "CHG-0000 (sintético)", null, null, null, validUntil, expired, "ApiLimitation",
        KnightCatalog.Version, "Pessoa Gestora (sintético)", Now.AddDays(-10));

    private static async Task SeedAsync(AegisScoreDbContext db, KnightLatestBySource latest)
    {
        foreach (var s in latest.Sources)
        {
            var a = s.Assessment!;
            var run = new KnightAssessmentRun
            {
                TenantId = TenantA,
                Mode = KnightAssessmentMode.Live, SourceType = s.Source, SourceState = a.SourceState, Source = a.Source,
                Status = KnightRunStatus.Completed, CatalogVersion = a.CatalogVersion, ScoreFormulaVersion = a.ScoreFormulaVersion,
                StartedAt = a.StartedAt, CompletedAt = a.CompletedAt, Score = a.Score, Coverage = a.Coverage, PassedCount = a.PassedCount,
                ExposedCount = a.ExposedCount, MitigatedCount = a.MitigatedCount, NotEvaluatedCount = a.NotEvaluatedCount,
                ErrorCount = a.ErrorCount, NotApplicableCount = a.NotApplicableCount,
            };
            foreach (var i in a.Indicators)
            {
                var r = new KnightIndicatorResult
                {
                    RunId = run.Id,
                    IndicatorId = i.IndicatorId, Title = i.Title, Category = i.Category, Severity = i.Severity, Status = i.Status,
                    Evidence = i.Evidence, AffectedObjectCount = i.AffectedObjectCount, NistCodes = i.NistCodes.ToList(),
                    MitreTechniques = i.MitreTechniques.ToList(), Recommendation = i.Recommendation, SourceType = i.SourceType,
                    NotEvaluatedReason = i.NotEvaluatedReason, CollectedAt = i.CollectedAt, HasAffectedDetail = true, AffectedDetailComplete = true,
                };
                for (var n = 0; n < i.AffectedObjectCount; n++)
                    r.AffectedObjects.Add(new KnightAffectedObject
                    {
                        RunId = run.Id, IndicatorResultId = r.Id,
                        IndicatorId = i.IndicatorId, ExternalId = $"{i.IndicatorId.ToLowerInvariant()}-obj-{n}", Kind = KnightAffectedObjectKind.User,
                        DisplayName = $"Conta sintética {n}", UserPrincipalName = $"conta{n}@demo.example.com", Relation = KnightObjectRelation.Affected,
                    });
                run.Indicators.Add(r);
            }
            db.KnightAssessmentRuns.Add(run);
        }
        await db.SaveChangesAsync();
    }

    private static PostureSnapshot WithoutCis(PostureSnapshot s)
    {
        s.CisBenchmarkJson = null;
        return s;
    }

    private AegisScoreDbContext NewContext(Guid tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    /// <summary>Serviço de leitura com a composição fixa (a API só lê "a última de cada fonte").</summary>
    private sealed class FixedLatest : IAegisKnightAssessmentService
    {
        private readonly KnightLatestBySource _latest;
        public FixedLatest(KnightLatestBySource latest) => _latest = latest;
        public Task<KnightLatestBySource> GetLatestBySourceAsync(CancellationToken ct = default) => Task.FromResult(_latest);
        public Task<KnightAssessment> RunDemoAssessmentAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAssessment> RunAssessmentAsync(KnightSourceType source, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightSyncRunResult> RunForSyncRequestAsync(KnightSourceType source, KnightSyncBinding binding, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightLatestAssessment> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAssessment?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightSourcesStatus> GetSourcesStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAffectedObjectsPage?> GetAffectedObjectsAsync(Guid runId, string indicatorId, int page, int pageSize, string? search,
            CancellationToken ct = default, KnightObjectRelation relation = KnightObjectRelation.Affected) => throw new NotSupportedException();
        public Task<KnightAffectedSummary?> GetAffectedSummaryAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnightAffectedSummary?> GetAffectedSummaryAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

