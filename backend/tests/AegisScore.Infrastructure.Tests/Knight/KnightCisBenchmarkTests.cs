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
            b.ManualCounts.Total.Should().Be(rows.Count(r => r.Knight!.Origin == "Manual"));
            b.KnightCounts.Should().BeEquivalentTo(KnightCisProjection.KnightCountsOf(rows), b.Key);
            var withVerdict = rows.Where(x => x.Status is KnightCisStatus.Compliant or KnightCisStatus.NonCompliant or KnightCisStatus.NotApplicable).ToList();
            withVerdict.Where(x => !((x.Id.StartsWith("CIS-M365-7.0.0:6.") || x.Id.StartsWith("CIS-M365-7.0.0:7."))
                    && x.Criterion!.State == KnightCisCriterionState.Confirmed)).Should().BeEmpty(
                "só as seções 6, 7 e 8 do M365 7.0.0 têm critério confirmado, e o Teams não foi coletado no caso de referência");
            (b.Counts.Compliant, b.Counts.NonCompliant, b.Counts.NotApplicable).Should().Be(b.Key == "CIS-M365-7.0.0" ? (4, 1 + 2, 0) : (0, 0, 0), b.Key);
            b.CriterionConfirmed.Should().Be(b.Key == "CIS-M365-7.0.0" ? 40 : 0, b.Key);
            (b.KnightCounts!.Compliant + b.KnightCounts.NonCompliant).Should().BeGreaterThan(0, "a leitura do KNIGHT continua visível");
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
            .And.Contain(x => x.StartsWith($"Totais CIS: {r.Considered} recomendações consideradas"))
            .And.Contain(x => x.StartsWith("O resultado do KNIGHT chega a uma conclusão técnica em"));
        view.Mapping.OfficialSources.Should().Equal(KnightCisCriteria.M365V700Pdf);
        view.Mapping.CriteriaConfirmed.Should().Be(40);
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
        teams.Where(r => r.Id != "CIS-M365-7.0.0:8.6.1").Should().OnlyContain(r => r.Knight!.Reason!.Contains("Autorização recusada pela Microsoft (sintético)"),
            "o motivo específico da regra vai junto");
        view.Recommendations.Should().NotContain(r => r.Status == KnightCisStatus.Compliant && r.RuleIds.Any(id => id.StartsWith("AK-TEAMS-")));
    }

    [Fact]
    public void CasoDeReferencia_VinculoParcial_Composta_VariantesEConflito()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        KnightCisReading R(string id) => view.Recommendations.Single(r => r.Id == id).Knight!;
        KnightCisRecommendation Rec(string id) => view.Recommendations.Single(r => r.Id == id);

        // Parcial que prova a violação: abaixo de 90% com MFA já prova que nem todos são capazes de MFA.
        R("CIS-M365-7.0.0:5.2.3.4").Status.Should().Be(KnightCisStatus.NonCompliant);
        Rec("CIS-M365-7.0.0:5.2.3.4").Variants.Single().Rules.Single(x => x.IndicatorId == "AK-ENTRA-006").Proof.Should().Be("ProvesViolation");
        // Parcial aprovada que não prova o critério: security defaults não é a política que a recomendação pede.
        var mfaAll = R("CIS-M365-7.0.0:5.2.2.2");
        mfaAll.Status.Should().Be(KnightCisStatus.NotEvaluated);
        mfaAll.Reason.Should().Contain("AK-ENTRA-014 (aprovado) avalia só parte do critério");
        Rec("CIS-M365-7.0.0:5.2.2.2").Limitations.Should().Contain(l => l.StartsWith("Equivalência parcial — AK-ENTRA-014"));

        // Composta: as duas partes aprovadas → conforme; uma parte sem avaliação → não avaliada, dizendo qual.
        R("CIS-M365-7.0.0:5.2.3.5").Status.Should().Be(KnightCisStatus.Compliant);
        Rec("CIS-M365-7.0.0:5.2.3.5").RuleIds.Should().BeEquivalentTo("AK-ENTRA-029", "AK-ENTRA-031");
        var idle = R("CIS-M365-7.0.0:1.3.2");
        idle.Status.Should().Be(KnightCisStatus.NotEvaluated);
        idle.Reason.Should().StartWith("Critério composto: nem todas as partes estão comprovadas.").And.Contain("AK-ENTRA-075");
        R("CIS-M365-7.0.0:8.6.1").Status.Should().Be(KnightCisStatus.NotEvaluated, "a parte do Teams não foi avaliada");

        // Variantes: uma recomendação, várias entradas do catálogo.
        var udp = Rec("CIS-AZ-6.0.0:7.3");
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
        sspr.Knight!.Status.Should().Be(KnightCisStatus.Compliant);
        sspr.Knight.Origin.Should().Be("Manual");
        sspr.Knight.OriginLabel.Should().Be("Verificação manual");
        sspr.Status.Should().Be(KnightCisStatus.NotEvaluated, "a verificação manual é evidência; o critério da edição continua sem confirmação");
        sspr.Reason.Should().StartWith(KnightCisCriteria.NotConfirmedReason);
        sspr.Manual!.ResponsibleName.Should().Be("Pessoa Responsável (sintético)");
        sspr.Manual.ValidUntil.Should().Be("2027-06-30");
        sspr.Found.Should().Contain("Verificação manual registrada como conforme");

        var linkedIn = R("CIS-M365-7.0.0:5.1.2.6");
        linkedIn.Knight!.Status.Should().Be(KnightCisStatus.NotEvaluated, "verificação manual vencida não vale");
        linkedIn.Knight.Reason.Should().Contain("venceu em 2026-10-01");
        linkedIn.Manual!.Expired.Should().BeTrue();

        var bg = R("CIS-M365-7.0.0:1.1.2");
        bg.Knight!.Status.Should().Be(KnightCisStatus.NotEvaluated);
        bg.Knight.Reason.Should().StartWith("Verificação manual:");
        bg.HowToFix.Should().Contain("registre o resultado manual");

        view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0").ManualCounts.Compliant.Should().Be(1);
    }

    [Fact]
    public void Mitigacao_NaoEhConformidade()
    {
        var comp = ReferenceComposition(new Dictionary<string, KnightIndicatorStatus> { ["AK-ENTRA-007"] = KnightIndicatorStatus.Mitigated });
        var r = KnightCisProjection.Build(Input(comp)).Recommendations.Single(x => x.Id == "CIS-M365-7.0.0:5.2.2.3");
        r.Knight!.Status.Should().Be(KnightCisStatus.NonCompliant);
        r.Knight.Mitigated.Should().BeTrue();
        r.Knight.StatusLabel.Should().Be("Reprovado pelo KNIGHT", "mitigação não é conformidade");
        r.Status.Should().Be(KnightCisStatus.NotEvaluated);
        r.Mitigated.Should().BeFalse();
    }

    [Fact]
    public void Composta_UmaParteReprovada_ProvaAViolacao()
    {
        var comp = ReferenceComposition(new Dictionary<string, KnightIndicatorStatus> { ["AK-ENTRA-031"] = KnightIndicatorStatus.Exposed });
        var r = KnightCisProjection.Build(Input(comp)).Recommendations.Single(x => x.Id == "CIS-M365-7.0.0:5.2.3.5");
        r.Knight!.Status.Should().Be(KnightCisStatus.NonCompliant);
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
        azure.Where(r => r.Knight!.Status != KnightCisStatus.NotEvaluated).Select(r => r.Id).Should().BeEmpty();
        azure.Where(r => r.Id != "CIS-AZC-2.0.0:2.4.11" && !r.Knight!.Reason!.Contains("catálogo ak-knight-v12")).Select(r => r.Id + " → " + r.Reason)
            .Should().BeEmpty("o motivo diz a versão executada (o conflito de identificação continua com o motivo dele)");
        view.Origin.Sources.Single(s => s.Source == "MicrosoftAzure").MappingReliable.Should().BeFalse();
        view.Limitations.Should().Contain(l => l.StartsWith("Avaliação histórica sem mapeamento confirmado") && l.Contains("Microsoft Azure"));
        view.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:5.2.3.4").Knight!.Status.Should().Be(KnightCisStatus.NonCompliant, "as demais fontes seguem");
    }

    [Fact]
    public void VersoesDoCatalogo_RevisadasFixadas_VersaoFuturaNaoEhAprovadaAutomaticamente()
    {
        KnightCisEvidencePolicy.MappingCatalogVersions.Should().Equal("ak-knight-v13");
        KnightCisEvidencePolicy.MappingReliableFor("ak-knight-v14").Should().BeFalse();
        var view = KnightCisProjection.Build(Input(ReferenceComposition(catalogOf: s => s == KnightSourceType.MicrosoftAzure ? "ak-knight-v14" : KnightCatalog.Version)));
        view.Origin.Sources.Single(s => s.Source == "MicrosoftAzure").MappingReliable.Should().BeFalse();
        view.Recommendations.Single(r => r.Id == "CIS-AZ-6.0.0:2.1.2").Knight!.Reason.Should().Contain("ak-knight-v14").And.Contain("revisados para ak-knight-v13");
    }

    // ---- Critério da edição: resultado do KNIGHT ≠ resultado CIS ---------------------------------------------------

    private static KnightCisRecommendation FirstExact(KnightCisView view, string knightStatus) => view.Recommendations
        .Where(r => r.Criterion!.State == KnightCisCriterionState.NotConfirmed)
        .Where(r => r.Variants.Count == 1 && r.Variants[0].Rules.Count == 1 && r.Variants[0].Rules[0].Match == "Exact")
        .First(r => r.Knight!.Status == knightStatus && r.Knight.Origin == "Automated");

    private static KnightCisCriterionConfirmation Confirm(KnightCisRecommendation r, IReadOnlyList<string>? rules = null, string catalog = "ak-knight-v13") =>
        new(r.Id, "Edição oficial sintética (teste)", "seção " + r.Section + " (sintético)", "2026-11-13", "Pessoa Revisora (sintético)",
            new[] { catalog }, rules ?? r.RuleIds, "Conferência sintética para teste.");

    [Fact]
    public void VinculoIntegral_SemConfirmacaoOficial_FicaNaoAvaliado_ComALeituraDoKnightAoLado()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        var r = FirstExact(view, KnightCisStatus.Compliant);

        r.Status.Should().Be(KnightCisStatus.NotEvaluated, "classificação integral, perfil e catálogo v13 não confirmam o critério oficial");
        r.Reason.Should().StartWith(KnightCisCriteria.NotConfirmedReason + ":").And.Contain("seção " + r.Section);
        r.Origin.Should().Be("None");
        r.Knight!.StatusLabel.Should().Be("Aprovado pelo KNIGHT");
        r.Found.Should().Contain(r.RuleIds[0] + " aprovou: Configuração conforme o critério (sintético).", "a regra, o resultado e a evidência ficam visíveis");
        r.Criterion!.State.Should().Be(KnightCisCriterionState.NotConfirmed);
        r.Criterion.StateLabel.Should().Be(KnightCisCriteria.NotConfirmedReason);
        r.Criterion.Edition.Should().Be(view.Benchmarks.Single(b => b.Key == r.Benchmark).Framework + " " + view.Benchmarks.Single(b => b.Key == r.Benchmark).Version);
        r.Criterion.Section.Should().Be(r.Section);
        r.Criterion.Trace.Should().Contain("Monkey365").And.Contain("rastreio, não texto oficial");
        r.Criterion.OfficialSource.Should().BeNull();
        r.MappingOrigin.Should().Contain(r.RuleIds[0] + " (integral)").And.Contain("avaliação executada com ak-knight-v13").And.Contain("revisados para ak-knight-v13");

        view.Recommendations.Where(x => x.Criterion!.State == KnightCisCriterionState.NotConfirmed)
            .Should().NotContain(x => x.Status == KnightCisStatus.Compliant || x.Status == KnightCisStatus.NonCompliant || x.Status == KnightCisStatus.NotApplicable);
        view.Recommendations.Where(x => x.Status == KnightCisStatus.NotEvaluated && x.Criterion!.State == KnightCisCriterionState.NotConfirmed)
            .Should().OnlyContain(x => x.Reason!.StartsWith(KnightCisCriteria.NotConfirmedReason));
        view.Limitations[0].Should().StartWith("Critérios conferidos em: " + KnightCisCriteria.M365V700Pdf);
    }

    [Fact]
    public void VinculoConfirmado_ComEvidenciaSuficiente_RecebeResultado_ComAProcedencia()
    {
        var input = Input(ReferenceComposition());
        var baseline = KnightCisProjection.Build(input);
        var ok = FirstExact(baseline, KnightCisStatus.Compliant);
        var bad = FirstExact(baseline, KnightCisStatus.NonCompliant);
        var partialOnly = baseline.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:5.2.2.2"); // parcial aprovada que não prova
        var wrongRule = baseline.Recommendations.First(r => r.Id != ok.Id && r.Id != bad.Id && r.Knight!.Status == KnightCisStatus.Compliant && r.Knight.Origin == "Automated");
        var wrongVersion = baseline.Recommendations.First(r => r.Id != ok.Id && r.Id != bad.Id && r.Id != wrongRule.Id && r.Knight!.Status == KnightCisStatus.NonCompliant && r.Knight.Origin == "Automated");

        var view = KnightCisProjection.Build(input, KnightReferenceCatalog.Coverage(), KnightCatalog.Indicators, new[]
        {
            Confirm(ok), Confirm(bad), Confirm(partialOnly), Confirm(wrongRule, rules: Array.Empty<string>()), Confirm(wrongVersion, catalog: "ak-knight-v12"),
        });
        KnightCisRecommendation R(string id) => view.Recommendations.Single(r => r.Id == id);

        R(ok.Id).Status.Should().Be(KnightCisStatus.Compliant);
        R(ok.Id).Origin.Should().Be("Automated");
        R(ok.Id).Criterion!.State.Should().Be(KnightCisCriterionState.Confirmed);
        R(ok.Id).Criterion!.OfficialSource.Should().Be("Edição oficial sintética (teste)");
        R(ok.Id).Criterion!.ReviewedBy.Should().Be("Pessoa Revisora (sintético)");
        R(ok.Id).Criterion!.ReviewedCatalogVersions.Should().Equal("ak-knight-v13");
        R(bad.Id).Status.Should().Be(KnightCisStatus.NonCompliant);

        R(partialOnly.Id).Status.Should().Be(KnightCisStatus.NotEvaluated, "critério confirmado não basta quando a evidência não sustenta o resultado");
        R(partialOnly.Id).Reason.Should().Contain("avalia só parte do critério");
        R(wrongRule.Id).Status.Should().Be(KnightCisStatus.NotEvaluated);
        R(wrongRule.Id).Reason.Should().Contain("não foi revisado contra ele");
        R(wrongVersion.Id).Status.Should().Be(KnightCisStatus.NotEvaluated);
        R(wrongVersion.Id).Reason.Should().Contain("para o catálogo ak-knight-v12").And.Contain("a avaliação usou ak-knight-v13");

        // Totais do resumo: só os dois vereditos sustentados contam; a leitura do KNIGHT não muda.
        var counts = view.Recommendations.GroupBy(r => r.Benchmark).ToDictionary(g => g.Key, g => KnightCisProjection.CountsOf(g));
        foreach (var b in view.Benchmarks)
        {
            b.Counts.Should().BeEquivalentTo(counts[b.Key]);
            b.KnightCounts.Should().BeEquivalentTo(baseline.Benchmarks.Single(x => x.Key == b.Key).KnightCounts);
        }
        view.Benchmarks.Sum(b => b.Counts.Compliant + b.Counts.NonCompliant + b.Counts.NotApplicable).Should().Be(2);
        view.Benchmarks.Sum(b => b.CriterionConfirmed ?? 0).Should().Be(5);
        view.Mapping.CriteriaConfirmed.Should().Be(5);
    }

    [Fact]
    public void Databricks_ChaveDoClienteEmUmaSoSuperficie_NaoAprovaNemReprova_EMostraALeituraDoKnight()
    {
        foreach (var (status, evidence) in new[]
                 {
                     (KnightIndicatorStatus.Passed, "serviços gerenciados: chave da Microsoft; DBFS: Microsoft.Keyvault (sintético)"),
                     (KnightIndicatorStatus.Exposed, "serviços gerenciados: chave da Microsoft; DBFS: chave da Microsoft (sintético)"),
                 })
        {
            var input = Input(ReferenceComposition());
            input = input with
            {
                Sources = input.Sources.Select(s => s with
                {
                    Rules = s.Rules.Select(r => r.IndicatorId == "AK-AZ-DBR-003" ? r with { Status = status, Evidence = evidence, NotEvaluatedReason = null } : r).ToList(),
                }).ToList(),
            };
            var probe = KnightCisProjection.Build(input).Recommendations.Single(r => r.Id == "CIS-AZ-6.0.0:2.1.8");
            // Mesmo com o critério confirmado e o vínculo revisado, a evidência não sustenta resultado.
            var view = KnightCisProjection.Build(input, KnightReferenceCatalog.Coverage(), KnightCatalog.Indicators, new[] { Confirm(probe) });
            var r = view.Recommendations.Single(x => x.Id == "CIS-AZ-6.0.0:2.1.8");

            r.Knight!.Status.Should().Be(KnightCisStatus.NotEvaluated, status.ToString());
            r.Status.Should().Be(KnightCisStatus.NotEvaluated, status.ToString());
            r.Variants.Single().Rules.Single().Proof.Should().Be("Inconclusive");
            r.Variants.Single().Rules.Single().MatchLabel.Should().Be("Parcial");
            r.Found.Should().Contain("AK-AZ-DBR-003 " + (status == KnightIndicatorStatus.Passed ? "aprovou" : "reprovou") + ": " + evidence.TrimEnd('.'));
            r.Reason.Should().Contain("dados críticos");
            r.MappingOrigin.Should().Contain("AK-AZ-DBR-003 (parcial)");
            view.Rules.Single(x => x.IndicatorId == "AK-AZ-DBR-003").Evidence.Should().Be(evidence, "a regra de coleta e o resultado do KNIGHT ficam como estão");
        }
        KnightCatalog.Indicators.Single(d => d.Id == "AK-AZ-DBR-003").References.Single().Match.Should().Be(KnightReferenceMatch.Partial);
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

    // ---- Primeiro lote conferido: CIS M365 7.0.0, seção 8 (Teams), no PDF oficial --------------------------------------

    /// <summary>Teams COLETADO (fora da contagem do caso de referência), com um desfecho dirigido por seção do lote.</summary>
    private static readonly IReadOnlyDictionary<string, KnightIndicatorStatus> TeamsLot = new Dictionary<string, KnightIndicatorStatus>
    {
        ["AK-TEAMS-001"] = KnightIndicatorStatus.Exposed,       // 8.1.1: reprovação não sustentada (provedor pode ser autorizado)
        ["AK-TEAMS-002"] = KnightIndicatorStatus.Passed,        // 8.1.2: Conforme
        ["AK-TEAMS-003"] = KnightIndicatorStatus.Passed,        // 8.2.1: Conforme (estado de aprovação opcional)
        ["AK-TEAMS-005"] = KnightIndicatorStatus.NotApplicable, // 8.2.3: não aplicável do KNIGHT não sustentado
        ["AK-TEAMS-006"] = KnightIndicatorStatus.Exposed,       // 8.2.4: Não conforme
        ["AK-TEAMS-008"] = KnightIndicatorStatus.Passed,        // 8.5.1: Conforme
        ["AK-TEAMS-011"] = KnightIndicatorStatus.Exposed,       // 8.5.4: reprovação não sustentada (política Global × todas)
    };

    [Fact]
    public void LoteTeams_M365_700_RegistradoComFonteOficial_PaginaEVinculosRevisados()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        var registered = KnightCisCriteria.Registered.Where(c => c.RecommendationId.StartsWith("CIS-M365-7.0.0:8.")).ToList();
        registered.Should().HaveCount(15);
        registered.Select(c => c.RecommendationId).Should().OnlyHaveUniqueItems()
            .And.OnlyContain(id => id.StartsWith("CIS-M365-7.0.0:8."))
            .And.NotContain(new[] { "CIS-M365-7.0.0:8.4.1", "CIS-M365-7.0.0:8.6.1" }, "8.4.1 não tem regra e 8.6.1 depende do AK-MDO-019, não conferido");
        foreach (var c in registered)
        {
            var r = view.Recommendations.Single(x => x.Id == c.RecommendationId);
            c.OfficialSource.Should().Be(KnightCisCriteria.M365V700Pdf);
            c.Location.Should().StartWith("seção " + r.Section + ", p. ");
            c.ConsultedOn.Should().Be("2026-11-14");
            c.ReviewedBy.Should().Contain("sem segunda revisão humana");
            c.ReviewedCatalogVersions.Should().Equal("ak-knight-v13");
            c.ReviewedRules.Should().Equal(r.RuleIds, $"{c.RecommendationId}: o vínculo revisado é o vínculo do catálogo");
            c.Note.Should().StartWith("Edição: ");
            if (c.SustainedResults is not null) c.UnsustainedReason.Should().NotBeNullOrWhiteSpace();
            r.Criterion!.State.Should().Be(KnightCisCriterionState.Confirmed);
            r.Criterion.Location.Should().Be(c.Location);
        }
        registered.Where(c => c.SustainedResults is null).Select(c => c.RecommendationId)
            .Should().BeEquivalentTo(new[] { "CIS-M365-7.0.0:8.1.2", "CIS-M365-7.0.0:8.2.4" }, "só nelas a regra e a edição leem o mesmo valor, da mesma configuração");
        KnightCisCriteria.OfficialSourcesConsulted.Should().Equal(KnightCisCriteria.M365V700Pdf);
    }

    [Fact]
    public void LoteTeams_Conforme_NaoConforme_ENaoAvaliado_ComEvidenciaETotais()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition(exact: TeamsLot)));
        KnightCisRecommendation R(string section) => view.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:" + section);

        var channel = R("8.1.2");
        channel.Status.Should().Be(KnightCisStatus.Compliant);
        channel.Origin.Should().Be("Automated");
        channel.Found.Should().Contain("AK-TEAMS-002 aprovou");
        channel.Criterion!.OfficialSource.Should().Be(KnightCisCriteria.M365V700Pdf);
        channel.Criterion.Location.Should().Be("seção 8.1.2, p. 504–507 (Descrição, Auditoria e Valor padrão)");

        var trial = R("8.2.4");
        trial.Status.Should().Be(KnightCisStatus.NonCompliant);
        trial.Found.Should().Contain("AK-TEAMS-006");
        R("8.2.1").Status.Should().Be(KnightCisStatus.Compliant);
        R("8.5.1").Status.Should().Be(KnightCisStatus.Compliant);

        var lobby = R("8.5.4");
        lobby.Knight!.Status.Should().Be(KnightCisStatus.NonCompliant);
        lobby.Status.Should().Be(KnightCisStatus.NotEvaluated);
        lobby.Reason.Should().StartWith("Reprovado pelo KNIGHT não vale como resultado CIS: ")
            .And.Contain("Critério da edição conferido em " + KnightCisCriteria.M365V700Pdf).And.Contain("só sustenta Conforme.")
            .And.Contain("política de reunião padrão da organização (Global)");
        lobby.Found.Should().Contain("AK-TEAMS-011", "a regra, o resultado e a evidência continuam visíveis");

        R("8.1.1").Status.Should().Be(KnightCisStatus.NotEvaluated);
        R("8.1.1").Reason.Should().Contain("provedores AUTORIZADOS");
        R("8.2.3").Knight!.Status.Should().Be(KnightCisStatus.NotApplicable);
        R("8.2.3").Status.Should().Be(KnightCisStatus.NotEvaluated);
        R("8.2.3").Reason.Should().Contain("Não aplicável pelo KNIGHT não vale como resultado CIS");

        var notCollected = R("8.5.6");
        notCollected.Status.Should().Be(KnightCisStatus.NotEvaluated);
        notCollected.Criterion!.State.Should().Be(KnightCisCriterionState.Confirmed);
        notCollected.Reason.Should().Contain("Autorização recusada pela Microsoft (sintético)", "sem coleta, o motivo é o da regra, não o do critério");

        R("8.6.1").Criterion!.State.Should().Be(KnightCisCriterionState.NotConfirmed);
        R("8.6.1").Reason.Should().StartWith(KnightCisCriteria.NotConfirmedReason);

        var m365 = view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0");
        var teamsRows = view.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:8.")).ToList();
        (teamsRows.Count(r => r.Status == KnightCisStatus.Compliant), teamsRows.Count(r => r.Status == KnightCisStatus.NonCompliant),
            teamsRows.Count(r => r.Status == KnightCisStatus.NotApplicable)).Should().Be((3, 1, 0));
        (m365.Counts.Compliant, m365.Counts.NonCompliant).Should().Be((3 + 4, 1 + 1 + 2), "seção 8 do lote mais as seções 6 e 7 do caso de referência");
        m365.Counts.Should().BeEquivalentTo(KnightCisProjection.CountsOf(view.Recommendations.Where(r => r.Benchmark == m365.Key).ToList()));
        m365.CriterionConfirmed.Should().Be(40);
        view.Benchmarks.Where(b => b.Key != m365.Key).Should().OnlyContain(b => b.Counts.Compliant + b.Counts.NonCompliant + b.Counts.NotApplicable == 0);
        view.Reconciliation.Explanation.Should().Contain(x => x.Contains(" 11 têm o critério da edição confirmado"));
    }

    [Fact]
    public async Task LoteTeams_TelaEHtmlOffline_MesmoResultadoEvidenciaETotais()
    {
        await using var db = NewContext(TenantA);
        var composition = ReferenceComposition(exact: TeamsLot);
        await SeedAsync(db, composition);
        await new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System)
            .SaveAsync(KnightConsolidatedCandidates.Sources.Where(s => s != KnightSourceType.MicrosoftFabric).ToList(), "Pessoa Gestora");
        var controller = new KnightAssessmentsController(new FixedLatest(composition), new SystemTenantContext(TenantA),
            new KnightManualResultService(db, new SystemTenantContext(TenantA), TimeProvider.System),
            scope: new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System));
        var live = (KnightCisView)((OkObjectResult)(await controller.GetCisBenchmarks(default)).Result!).Value!;

        var detail = await new PostureSnapshotService(db, new SystemTenantContext(TenantA)).PublishConsolidatedKnightAsync(null);
        var snapshot = await db.PostureSnapshots.AsNoTracking().AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        var frozen = KnightCisJson.Deserialize(snapshot.CisBenchmarkJson)!;

        frozen.Benchmarks.Select(b => (b.Key, b.Counts, b.CriterionConfirmed)).Should().BeEquivalentTo(live.Benchmarks.Select(b => (b.Key, b.Counts, b.CriterionConfirmed)));
        frozen.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:8."))
            .Select(r => (r.Id, r.Status, r.Reason, r.Found, r.Knight!.Status, r.Criterion!.State, r.Criterion.OfficialSource, r.Criterion.Location))
            .Should().Equal(live.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:8."))
                .Select(r => (r.Id, r.Status, r.Reason, r.Found, r.Knight!.Status, r.Criterion!.State, r.Criterion.OfficialSource, r.Criterion.Location)));
        frozen.Recommendations.Count(r => r.Id.StartsWith("CIS-M365-7.0.0:8.") && r.Status is KnightCisStatus.Compliant).Should().Be(3);
        frozen.Recommendations.Count(r => r.Id.StartsWith("CIS-M365-7.0.0:8.") && r.Status is KnightCisStatus.NonCompliant).Should().Be(1);

        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("CIS_Microsoft_365_Foundations_Benchmark_v7.0.0.pdf").And.Contain("8.2.4, p. 521");
        html.Should().NotContain("src=\"http").And.NotContain("href=\"http://", "o arquivo funciona offline, sem nada externo");
    }

    // ---- Segundo lote conferido: CIS M365 7.0.0, seção 6 (Exchange Online), no mesmo PDF oficial ------------------------

    /// <summary>Desfechos dirigidos do Exchange por cima do caso de referência (001–012 reprovadas, 013 sem conclusão, 014–017 aprovadas).</summary>
    private static readonly IReadOnlyDictionary<string, KnightIndicatorStatus> ExchangeLot = new Dictionary<string, KnightIndicatorStatus>
    {
        ["AK-EXO-005"] = KnightIndicatorStatus.Passed,  // 6.1.1: Conforme
        ["AK-EXO-006"] = KnightIndicatorStatus.Passed,  // 6.1.2: inconclusivo (listas de ações menores que as da edição)
        ["AK-EXO-013"] = KnightIndicatorStatus.Exposed, // 6.5.1: Não conforme
        ["AK-EXO-016"] = KnightIndicatorStatus.Exposed, // 6.5.4: reprovação não sustentada (substituição por caixa)
        ["AK-EXO-017"] = KnightIndicatorStatus.Exposed, // 6.5.5: Não conforme
    };

    /// <summary>Os dois lotes juntos, para a paridade tela × HTML e as capturas.</summary>
    private static IReadOnlyDictionary<string, KnightIndicatorStatus> BothLots =>
        TeamsLot.Concat(ExchangeLot).ToDictionary(x => x.Key, x => x.Value);

    private static readonly string[] ExchangeSections =
        { "6.1.1", "6.1.2", "6.1.3", "6.2.1", "6.2.2", "6.2.3", "6.3.1", "6.3.2", "6.5.1", "6.5.2", "6.5.3", "6.5.4", "6.5.5" };

    [Fact]
    public void LoteExchange_M365_700_TodaASecao6Conferida_ComDesfechosSustentadosPorSecao()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        var lot = KnightCisCriteria.Registered.Where(c => c.RecommendationId.StartsWith("CIS-M365-7.0.0:6.")).ToList();
        lot.Select(c => c.RecommendationId).Should().BeEquivalentTo(ExchangeSections.Select(s => "CIS-M365-7.0.0:" + s));
        view.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:6.")).Select(r => r.Section)
            .Should().BeEquivalentTo(ExchangeSections, "o lote cobre todas as recomendações da seção 6");
        foreach (var c in lot)
        {
            var r = view.Recommendations.Single(x => x.Id == c.RecommendationId);
            c.OfficialSource.Should().Be(KnightCisCriteria.M365V700Pdf);
            c.Location.Should().StartWith("seção " + r.Section + ", p. 4");
            c.ConsultedOn.Should().Be("2026-11-14");
            c.ReviewedCatalogVersions.Should().Equal("ak-knight-v13");
            c.ReviewedRules.Should().Equal(r.RuleIds, $"{c.RecommendationId}: o vínculo revisado é o vínculo do catálogo");
            r.RuleIds.Should().OnlyContain(id => id.StartsWith("AK-EXO-"));
            if (c.SustainedResults is not null) c.UnsustainedReason.Should().NotBeNullOrWhiteSpace();
        }
        string[] Sections(Func<KnightCisCriterionConfirmation, bool> p) => lot.Where(p).Select(c => c.RecommendationId[^5..]).ToArray();
        Sections(c => c.SustainedResults is null).Should().BeEquivalentTo(new[] { "6.1.1", "6.5.1", "6.5.5" });
        Sections(c => c.SustainedResults is { Count: 0 }).Should().BeEquivalentTo(new[] { "6.1.2", "6.2.1", "6.2.2" });
        Sections(c => c.SustainedResults is { Count: 1 } s && s[0] == KnightCisStatus.Compliant)
            .Should().BeEquivalentTo(new[] { "6.1.3", "6.2.3", "6.3.1", "6.3.2", "6.5.2", "6.5.3", "6.5.4" });
        KnightCisCriteria.M365V700Pdf.Should().Contain("edição de 20/05/2026, data impressa na capa");
    }

    [Fact]
    public void LoteExchange_Conforme_NaoConforme_EInconclusivo_ComEvidenciaETotais()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition(exact: ExchangeLot)));
        KnightCisRecommendation R(string section) => view.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:" + section);

        R("6.1.1").Status.Should().Be(KnightCisStatus.Compliant);
        R("6.1.1").Found.Should().Contain("AK-EXO-005 aprovou");
        R("6.1.1").Criterion!.Location.Should().Be("seção 6.1.1, p. 417–418 (Descrição, Auditoria e Valor padrão)");
        R("6.5.1").Status.Should().Be(KnightCisStatus.NonCompliant);
        R("6.5.5").Status.Should().Be(KnightCisStatus.NonCompliant);
        R("6.5.5").Found.Should().Contain("AK-EXO-017");
        R("6.5.2").Status.Should().Be(KnightCisStatus.Compliant);
        R("6.5.3").Status.Should().Be(KnightCisStatus.Compliant, "aprovação exige a política padrão conforme");

        var audit = R("6.1.2");
        audit.Knight!.Status.Should().Be(KnightCisStatus.Compliant);
        audit.Status.Should().Be(KnightCisStatus.NotEvaluated);
        audit.Reason.Should().StartWith("Aprovado pelo KNIGHT não vale como resultado CIS: ")
            .And.Contain("FolderBind").And.Contain("MailboxLogin")
            .And.Contain("nenhum desfecho da leitura do KNIGHT vale como resultado CIS");
        audit.Found.Should().Contain("AK-EXO-006 aprovou", "a leitura técnica continua visível");

        var smtp = R("6.5.4");
        smtp.Knight!.Status.Should().Be(KnightCisStatus.NonCompliant);
        smtp.Status.Should().Be(KnightCisStatus.NotEvaluated);
        smtp.Reason.Should().StartWith("Reprovado pelo KNIGHT não vale como resultado CIS: ").And.Contain("substituição").And.Contain("só sustenta Conforme.");

        foreach (var s in new[] { "6.1.3", "6.2.3", "6.3.1", "6.3.2" })
        {
            R(s).Knight!.Status.Should().Be(KnightCisStatus.NonCompliant, s);
            R(s).Status.Should().Be(KnightCisStatus.NotEvaluated, s);
        }
        R("6.2.2").Knight!.Status.Should().Be(KnightCisStatus.NonCompliant);
        R("6.2.2").Reason.Should().Contain("regras desabilitadas").And.Contain("nenhum desfecho");
        R("6.2.1").Knight!.Status.Should().Be(KnightCisStatus.NonCompliant);
        R("6.2.1").Reason.Should().Contain("caixas de correio");

        var rows = view.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:6.")).ToList();
        (rows.Count(r => r.Status == KnightCisStatus.Compliant), rows.Count(r => r.Status == KnightCisStatus.NonCompliant),
            rows.Count(r => r.Status == KnightCisStatus.NotEvaluated)).Should().Be((3, 2, 8));
        var m365 = view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0");
        (m365.Counts.Compliant, m365.Counts.NonCompliant).Should().Be((3, 2 + 2), "seção 6 do lote mais a seção 7 do caso de referência; o Teams não foi coletado");
        m365.Counts.Should().BeEquivalentTo(KnightCisProjection.CountsOf(view.Recommendations.Where(r => r.Benchmark == m365.Key).ToList()));
    }

    [Fact]
    public async Task LoteExchange_TelaEHtmlOffline_MesmoResultadoEvidenciaETotais()
    {
        await using var db = NewContext(TenantA);
        var composition = ReferenceComposition(exact: BothLots);
        await SeedAsync(db, composition);
        await new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System)
            .SaveAsync(KnightConsolidatedCandidates.Sources.Where(s => s != KnightSourceType.MicrosoftFabric).ToList(), "Pessoa Gestora");
        var controller = new KnightAssessmentsController(new FixedLatest(composition), new SystemTenantContext(TenantA),
            new KnightManualResultService(db, new SystemTenantContext(TenantA), TimeProvider.System),
            scope: new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System));
        var live = (KnightCisView)((OkObjectResult)(await controller.GetCisBenchmarks(default)).Result!).Value!;

        var detail = await new PostureSnapshotService(db, new SystemTenantContext(TenantA)).PublishConsolidatedKnightAsync(null);
        var snapshot = await db.PostureSnapshots.AsNoTracking().AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        var frozen = KnightCisJson.Deserialize(snapshot.CisBenchmarkJson)!;

        frozen.Benchmarks.Select(b => (b.Key, b.Counts, b.CriterionConfirmed)).Should().BeEquivalentTo(live.Benchmarks.Select(b => (b.Key, b.Counts, b.CriterionConfirmed)));
        frozen.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:6."))
            .Select(r => (r.Id, r.Status, r.Reason, r.Found, r.Knight!.Status, r.Criterion!.State, r.Criterion.Location, r.Criterion.Note))
            .Should().Equal(live.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:6."))
                .Select(r => (r.Id, r.Status, r.Reason, r.Found, r.Knight!.Status, r.Criterion!.State, r.Criterion.Location, r.Criterion.Note)));
        var m365 = frozen.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0");
        (m365.Counts.Compliant, m365.Counts.NonCompliant, m365.CriterionConfirmed).Should().Be((3 + 3, 1 + 2 + 2, 40), "seção 8 (3/1) + seção 6 (3/2) + seção 7 do caso de referência (0/2)");

        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("6.1.2, p. 419").And.Contain("6.5.4, p. 456").And.Contain("de 20/05/2026, data impressa na capa");
        html.Should().NotContain("src=\"http").And.NotContain("href=\"http://", "o arquivo funciona offline, sem nada externo");
    }

    // ---- Terceiro lote conferido: CIS M365 7.0.0, seção 7 (SharePoint e OneDrive), no mesmo PDF oficial ------------------

    /// <summary>
    /// Desfechos dirigidos do SharePoint por cima do caso de referência (001–004 reprovadas, 005–012 sem conclusão, 013 aprovada). Os
    /// motivos de "não avaliado" das regras que dependem da API de administração continuam os do caso de referência.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, KnightIndicatorStatus> SharePointLot = new Dictionary<string, KnightIndicatorStatus>
    {
        ["AK-SPO-001"] = KnightIndicatorStatus.Passed,        // 7.2.1: Conforme (Graph)
        ["AK-SPO-003"] = KnightIndicatorStatus.Exposed,       // 7.2.3: Não conforme (Graph)
        ["AK-SPO-005"] = KnightIndicatorStatus.NotApplicable, // 7.2.5: não aplicável do KNIGHT não sustentado
        ["AK-SPO-006"] = KnightIndicatorStatus.NotApplicable, // 7.2.6: a edição declara Conforme com o compartilhamento desligado
        ["AK-SPO-008"] = KnightIndicatorStatus.NotApplicable, // 7.2.8: idem
        ["AK-SPO-013"] = KnightIndicatorStatus.Passed,        // 7.3.2: seção ausente da edição
    };

    private static IReadOnlyDictionary<string, KnightIndicatorStatus> ThreeLots =>
        TeamsLot.Concat(ExchangeLot).Concat(SharePointLot).ToDictionary(x => x.Key, x => x.Value);

    private static readonly string[] SharePointSections =
        { "7.2.1", "7.2.2", "7.2.3", "7.2.4", "7.2.5", "7.2.6", "7.2.7", "7.2.8", "7.2.9", "7.2.10", "7.2.11", "7.3.1", "7.3.2" };

    [Fact]
    public void LoteSharePoint_M365_700_TrezeChavesConferidas_DozeSecoesEUmaAusenteDaEdicao()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition()));
        var lot = KnightCisCriteria.Registered.Where(c => c.RecommendationId.StartsWith("CIS-M365-7.0.0:7.")).ToList();
        lot.Select(c => c.RecommendationId).Should().BeEquivalentTo(SharePointSections.Select(s => "CIS-M365-7.0.0:" + s));
        view.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:7.")).Select(r => r.Section)
            .Should().BeEquivalentTo(SharePointSections, "as 13 chaves do catálogo na seção 7");
        foreach (var c in lot)
        {
            var r = view.Recommendations.Single(x => x.Id == c.RecommendationId);
            c.OfficialSource.Should().Be(KnightCisCriteria.M365V700Pdf);
            c.ConsultedOn.Should().Be("2026-11-14");
            c.ReviewedCatalogVersions.Should().Equal("ak-knight-v13");
            c.ReviewedRules.Should().Equal(r.RuleIds, $"{c.RecommendationId}: o vínculo revisado é o vínculo do catálogo");
            r.RuleIds.Should().OnlyContain(id => id.StartsWith("AK-SPO-"));
            if (c.AbsentFromEdition is null) c.Location.Should().StartWith("seção " + r.Section + ", p. 4");
            if (c.SustainedResults is not null && c.SustainedResults.Count < 3) c.UnsustainedReason.Should().NotBeNullOrWhiteSpace();
            if (c.ResultMap is not null) c.ResultMapReason.Should().Contain("conforme");
        }
        string[] Sections(Func<KnightCisCriterionConfirmation, bool> p) => lot.Where(p).Select(c => c.RecommendationId["CIS-M365-7.0.0:".Length..]).ToArray();
        Sections(c => c.CollectionGap is not null).Should().BeEquivalentTo(
            new[] { "7.2.2", "7.2.4", "7.2.7", "7.2.8", "7.2.9", "7.2.10", "7.2.11", "7.3.1" }, "a coleta só lê o Microsoft Graph");
        lot.Where(c => c.CollectionGap is not null).Should().OnlyContain(c => c.CollectionGap!.Contains("API de administração do SharePoint"));
        Sections(c => c.ResultMap is not null).Should().BeEquivalentTo(new[] { "7.2.6", "7.2.8" });
        Sections(c => c.AbsentFromEdition is not null).Should().Equal("7.3.2");
        lot.Single(c => c.AbsentFromEdition is not null).Location.Should().Contain("Change History").And.Contain("Ticket #26841");

        var removed = view.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:7.3.2");
        removed.Criterion!.State.Should().Be(KnightCisCriterionState.AbsentFromEdition);
        removed.Criterion.StateLabel.Should().Be("Seção ausente da edição oficial");
        removed.Status.Should().Be(KnightCisStatus.NotEvaluated);
        removed.Reason.Should().StartWith("A seção 7.3.2 não consta de ").And.Contain("removida nesta edição");
        view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0").CriterionConfirmed.Should().Be(40, "8 (15) + 6 (13) + 7 (12); a 7.3.2 não conta");
    }

    [Fact]
    public void LoteSharePoint_GraphSustentaResultado_ApiAdministrativaNaoLida_MotivoEspecifico_EEdicaoConformeComCompartilhamentoDesligado()
    {
        var view = KnightCisProjection.Build(Input(ReferenceComposition(exact: SharePointLot)));
        KnightCisRecommendation R(string section) => view.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:" + section);

        R("7.2.1").Status.Should().Be(KnightCisStatus.Compliant);
        R("7.2.1").Found.Should().Contain("AK-SPO-001 aprovou");
        R("7.2.3").Status.Should().Be(KnightCisStatus.NonCompliant);
        R("7.2.3").Criterion!.Location.Should().Be("seção 7.2.3, p. 468–470 (Descrição, Auditoria e Valor padrão)");
        // Vínculos parciais: pelo Graph só se conclui com o compartilhamento desligado (7.2.2) ou o SharePoint restrito (7.2.4); a
        // reprovação vinda do caso de referência não prova, e o motivo diz qual configuração da edição a coleta não alcança.
        foreach (var (s, property) in new[] { ("7.2.2", "EnableAzureADB2BIntegration"), ("7.2.4", "OneDriveSharingCapability") })
        {
            R(s).Knight!.Status.Should().Be(KnightCisStatus.NotEvaluated, s);
            R(s).Status.Should().Be(KnightCisStatus.NotEvaluated, s);
            R(s).Reason.Should().StartWith($"A edição audita {property}", s).And.Contain("Leitura do KNIGHT: ");
        }

        foreach (var s in new[] { "7.2.6", "7.2.8" })
        {
            R(s).Knight!.Status.Should().Be(KnightCisStatus.NotApplicable, s);
            R(s).Status.Should().Be(KnightCisStatus.Compliant, s + ": a edição declara conforme com o compartilhamento desligado");
            R(s).Reason.Should().StartWith("A edição declara esta recomendação conforme", s);
            R(s).Origin.Should().Be("Automated");
        }
        R("7.2.5").Knight!.Status.Should().Be(KnightCisStatus.NotApplicable);
        R("7.2.5").Status.Should().Be(KnightCisStatus.NotEvaluated);
        R("7.2.5").Reason.Should().StartWith("Não aplicável pelo KNIGHT não vale como resultado CIS: ").And.Contain("PreventExternalUsersFromResharing");

        // Sem a API de administração: não avaliado, com a configuração exigida pela edição e o motivo da regra ao lado.
        foreach (var (s, property) in new[] { ("7.2.7", "DefaultSharingLinkType"), ("7.2.9", "ExternalUserExpirationRequired"),
                     ("7.2.10", "EmailAttestationRequired"), ("7.2.11", "DefaultLinkPermission"), ("7.3.1", "DisallowInfectedFileDownload") })
        {
            var r = R(s);
            r.Knight!.Status.Should().Be(KnightCisStatus.NotEvaluated, s);
            r.Status.Should().Be(KnightCisStatus.NotEvaluated, s);
            r.Criterion!.State.Should().Be(KnightCisCriterionState.Confirmed, s);
            r.Reason.Should().StartWith($"A edição audita {property}", s).And.Contain("API de administração do SharePoint")
                .And.Contain("Leitura do KNIGHT: ", "o motivo da regra continua visível");
        }

        R("7.3.2").Knight!.Status.Should().Be(KnightCisStatus.Compliant, "a leitura técnica do KNIGHT é preservada");
        R("7.3.2").Status.Should().Be(KnightCisStatus.NotEvaluated);
        R("7.3.2").Found.Should().Contain("AK-SPO-013 aprovou");

        var rows = view.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:7.")).ToList();
        (rows.Count(r => r.Status == KnightCisStatus.Compliant), rows.Count(r => r.Status == KnightCisStatus.NonCompliant),
            rows.Count(r => r.Status == KnightCisStatus.NotEvaluated)).Should().Be((3, 1, 9));
        var m365 = view.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0");
        m365.Counts.Should().BeEquivalentTo(KnightCisProjection.CountsOf(view.Recommendations.Where(r => r.Benchmark == m365.Key).ToList()));
        (m365.Counts.Compliant, m365.Counts.NonCompliant).Should().Be((3 + 4, 1 + 1), "seção 7 do lote mais a seção 6 do caso de referência");
    }

    [Fact]
    public async Task LoteSharePoint_TelaEHtmlOffline_MesmoResultadoEvidenciaETotais()
    {
        await using var db = NewContext(TenantA);
        var composition = ReferenceComposition(exact: ThreeLots);
        await SeedAsync(db, composition);
        await new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System)
            .SaveAsync(KnightConsolidatedCandidates.Sources.Where(s => s != KnightSourceType.MicrosoftFabric).ToList(), "Pessoa Gestora");
        var controller = new KnightAssessmentsController(new FixedLatest(composition), new SystemTenantContext(TenantA),
            new KnightManualResultService(db, new SystemTenantContext(TenantA), TimeProvider.System),
            scope: new KnightAssessmentScopeService(db, new SystemTenantContext(TenantA), TimeProvider.System));
        var live = (KnightCisView)((OkObjectResult)(await controller.GetCisBenchmarks(default)).Result!).Value!;

        var detail = await new PostureSnapshotService(db, new SystemTenantContext(TenantA)).PublishConsolidatedKnightAsync(null);
        var snapshot = await db.PostureSnapshots.AsNoTracking().AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        var frozen = KnightCisJson.Deserialize(snapshot.CisBenchmarkJson)!;

        frozen.Benchmarks.Select(b => (b.Key, b.Counts, b.CriterionConfirmed)).Should().BeEquivalentTo(live.Benchmarks.Select(b => (b.Key, b.Counts, b.CriterionConfirmed)));
        frozen.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:7."))
            .Select(r => (r.Id, r.Status, r.Reason, r.Found, r.Knight!.Status, r.Criterion!.State, r.Criterion.StateLabel, r.Criterion.Location))
            .Should().Equal(live.Recommendations.Where(r => r.Id.StartsWith("CIS-M365-7.0.0:7."))
                .Select(r => (r.Id, r.Status, r.Reason, r.Found, r.Knight!.Status, r.Criterion!.State, r.Criterion.StateLabel, r.Criterion.Location)));
        var m365 = frozen.Benchmarks.Single(b => b.Key == "CIS-M365-7.0.0");
        (m365.Counts.Compliant, m365.Counts.NonCompliant, m365.CriterionConfirmed).Should().Be((3 + 3 + 3, 1 + 2 + 1, 40),
            "seção 8 (3/1) + seção 6 (3/2) + seção 7 (3/1)");

        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("7.2.6, p. 476").And.Contain("Ticket #26841").And.Contain("AbsentFromEdition").And.Contain("Cr.state!=='NotConfirmed'");
        html.Should().NotContain("src=\"http").And.NotContain("href=\"http://", "o arquivo funciona offline, sem nada externo");
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
        frozen.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:5.2.4.1").Knight!.Origin.Should().Be("Manual");
        frozen.Recommendations.Single(r => r.Id == "CIS-M365-7.0.0:5.2.4.1").Manual!.ValidUntil.Should().Be("2099-01-01");
        frozen.Origin.Sources.Where(s => s.Included).Should().OnlyContain(s => s.RunId != null && s.CatalogVersion == KnightCatalog.Version);

        // Tela e HTML: a MESMA projeção sobre a mesma composição.
        var live = KnightCisProjection.Build(KnightCisInputs.FromConsolidated(
            KnightConsolidatedBuilder.Build(ReferenceComposition(), null, new[] { KnightSourceType.MicrosoftFabric }),
            await new KnightManualResultService(db, new SystemTenantContext(TenantA), TimeProvider.System).CurrentAsync()));
        frozen.Benchmarks.Select(b => b.Counts).Should().BeEquivalentTo(live.Benchmarks.Select(b => b.Counts));
        frozen.Recommendations.Select(r => (r.Id, r.Status)).Should().Equal(live.Recommendations.Select(r => (r.Id, r.Status)));
        frozen.Benchmarks.Select(b => b.KnightCounts).Should().BeEquivalentTo(live.Benchmarks.Select(b => b.KnightCounts));
        frozen.Recommendations.Select(r => (r.Id, r.Reason, r.Knight!.Status, r.Knight.Reason, r.Criterion!.State, r.Criterion.Trace, r.MappingOrigin))
            .Should().Equal(live.Recommendations.Select(r => (r.Id, r.Reason, r.Knight!.Status, r.Knight.Reason, r.Criterion!.State, r.Criterion.Trace, r.MappingOrigin)),
                "tela e HTML mostram o mesmo resultado, o mesmo motivo e a mesma origem do mapeamento");

        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.CisState.Should().Be("Frozen");
        model.Notes.Should().NotContain(n => n.Contains("produzido pelo catálogo ak-knight-consolidated"),
            "ak-knight-consolidated identifica a composição, não a versão da regra");
        model.Notes.Should().Contain(n => n.Contains($"usam o mesmo catálogo, {KnightCatalog.Version}"));

        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("['cis','CIS Benchmarks']").And.Contain("\"cisState\":\"Frozen\"").And.Contain("\"schemaVersion\":\"knight-cis-v2\"");
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

    [Fact]
    public async Task FotografiaComVisaoCisV1_AbreComoRegistrada_SemCamposNovos_EOHashContinuaValido()
    {
        await using var db = NewContext(TenantA);
        await SeedAsync(db, ReferenceComposition());
        var detail = await new PostureSnapshotService(db, new SystemTenantContext(TenantA)).PublishConsolidatedKnightAsync(null);

        // A visão no formato v1 (antes da separação entre leitura do KNIGHT e critério), com o hash daquele conteúdo.
        var node = System.Text.Json.Nodes.JsonNode.Parse((await db.PostureSnapshots.AsNoTracking().SingleAsync(s => s.Id == detail.Summary.Id)).CisBenchmarkJson!)!;
        node["schemaVersion"] = "knight-cis-v1";
        foreach (var rec in node["recommendations"]!.AsArray()) { var o = rec!.AsObject(); o.Remove("knight"); o.Remove("criterion"); o.Remove("mappingOrigin"); }
        foreach (var b in node["benchmarks"]!.AsArray()) { var o = b!.AsObject(); o.Remove("knightCounts"); o.Remove("criterionConfirmed"); }
        var v1 = node.ToJsonString();
        var old = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
            .AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        old.CisBenchmarkJson = v1;
        var hash = PostureSnapshotHasher.Compute(old);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PostureSnapshots SET CisBenchmarkJson = {v1}, ContentHash = {hash} WHERE Id = {detail.Summary.Id}");
        db.ChangeTracker.Clear();

        var snapshot = await db.PostureSnapshots.AsNoTracking().Include(s => s.Indicators).Include(s => s.Objects).Include(s => s.ActionItems)
            .AsSplitQuery().SingleAsync(s => s.Id == detail.Summary.Id);
        PostureSnapshotHasher.Compute(snapshot).Should().Be(snapshot.ContentHash, "o texto congelado não é reescrito");
        var model = KnightReportModelBuilder.Build(snapshot, integrityVerified: true);
        model.CisState.Should().Be("Frozen");
        model.Cis!.SchemaVersion.Should().Be("knight-cis-v1");
        model.Cis.Recommendations.Should().HaveCount(435).And.OnlyContain(r => r.Knight == null && r.Criterion == null);
        var html = Encoding.UTF8.GetString((await new PostureSnapshotExporter(db).ExportAsync(snapshot.Id, PostureExportFormat.Html))!.Content);
        html.Should().Contain("\"schemaVersion\":\"knight-cis-v1\"").And.Contain("Visão registrada antes da confirmação do critério da edição");
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
        Func<KnightSourceType, string>? catalogOf = null, IReadOnlyDictionary<string, KnightIndicatorStatus>? exact = null)
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
            // Desfechos dirigidos FORA da contagem do caso de referência (ex.: o Teams coletado no lote conferido).
            foreach (var d in defs.Where(d => exact?.ContainsKey(d.Id) == true)) status[d.Id] = exact![d.Id];
            var (fp, fe, fne) = (p, e, ne);
            if (exact is not null && defs.Any(d => exact.ContainsKey(d.Id)))
            {
                fp = status.Values.Count(s => s == KnightIndicatorStatus.Passed);
                fe = status.Values.Count(s => s is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated);
                fne = status.Values.Count(s => s == KnightIndicatorStatus.NotEvaluated);
            }

            var label = KnightSourceCatalog.Label(source);
            var catalog = catalogOf?.Invoke(source) ?? KnightCatalog.Version;
            var views = defs.Select(d => View(d, status[d.Id], source, catalog)).ToList();
            var score = KnightScoreFormula.Compute(views.Select(v => (v.Severity, v.Status)));
            var state = fne > 0 && fp + fe == 0 ? KnightSourceState.InsufficientPermission : fne > 0 ? KnightSourceState.PartialCollection : KnightSourceState.Completed;
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

