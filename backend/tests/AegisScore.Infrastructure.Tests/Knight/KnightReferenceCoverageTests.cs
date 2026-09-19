using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Cobertura de IMPLEMENTAÇÃO do catálogo de referência: integral e parcial separadas,
/// nada escondido, nada declarado como implementado sem regra no fluxo ativo e coleta produzida por conector real.
/// </summary>
public sealed class KnightReferenceCoverageTests
{
    [Fact]
    public void CatalogoDeReferencia_FixadoNumCommit_ComChavesUnicas()
    {
        KnightReferenceCatalog.ReferenceCommit.Should().MatchRegex("^[0-9a-f]{40}$");
        KnightReferenceCatalog.Controls.Should().HaveCount(457);
        KnightReferenceCatalog.Controls.Select(c => c.Key).Should().OnlyHaveUniqueItems();
        KnightReferenceCatalog.Controls.Should().OnlyContain(c => c.Service != KnightService.Unspecified && !string.IsNullOrWhiteSpace(c.Title));
    }

    [Fact]
    public void Cobertura_SeparaIntegralDeParcial_ENaoSomaAsDuasComoCompleta()
    {
        var t = KnightReferenceCatalog.Coverage().Total;
        (t.Implemented + t.Partial + t.Pending + t.ManualOnly + t.RequiresAccess + t.ApiLimitation).Should().Be(t.Total);
        t.FullPercent.Should().Be(Math.Round(100.0 * t.Implemented / t.Total, 1, MidpointRounding.AwayFromZero));
        t.PartialPercent.Should().Be(Math.Round(100.0 * t.Partial / t.Total, 1, MidpointRounding.AwayFromZero));
        t.AnyAutomatedPercent.Should().BeGreaterThan(t.FullPercent, "há controles parciais, e eles não entram na cobertura integral");
    }

    [Fact]
    public void EntraId_TemCadaControleDeReferenciaClassificado_ENadaAlemDisso()
    {
        var entra = KnightReferenceCatalog.Coverage().ByPlatform.Single(p => p.Key == nameof(KnightPlatform.EntraId));
        entra.Total.Should().Be(83);
        // Números TRAVADOS: mudar a classificação exige revisar este teste — e explicar por quê no PR.
        entra.Implemented.Should().Be(56);
        entra.Partial.Should().Be(7);
        entra.ApiLimitation.Should().Be(18);
        entra.ManualOnly.Should().Be(2);
        entra.Pending.Should().Be(0);
        entra.RequiresAccess.Should().Be(0);
    }

    [Fact]
    public void Disposicoes_DeclaradasSoParaChavesExistentes_ENuncaParaAlgoImplementado()
    {
        var linked = KnightCatalog.Indicators.SelectMany(d => d.References).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, declared) in KnightReferenceDispositions.All)
        {
            KnightReferenceCatalog.Find(key).Should().NotBeNull(key);
            linked.Should().NotContain(key, $"{key} não pode ser declarado {declared.Disposition} e citado por um controle ao mesmo tempo");
            declared.Note.Should().NotBeNullOrWhiteSpace();
            declared.Disposition.Should().BeOneOf(KnightReferenceDisposition.ApiLimitation, KnightReferenceDisposition.ManualOnly, KnightReferenceDisposition.RequiresAccess);
        }
    }

    [Fact]
    public void LimitacaoDeApi_DizQualVersaoOuCanalFalta_ESemApiPrivada()
    {
        foreach (var (key, d) in KnightReferenceDispositions.All.Where(x => x.Value.Disposition == KnightReferenceDisposition.ApiLimitation))
        {
            d.Note.Should().MatchRegex("beta|versão estável", key);
            d.Note.Should().Contain("Verificação manual", key);
            d.Note.Should().NotContainAny("main.iam.ad.ext.azure.com", "api.interfaces.records.teams");
        }
    }

    [Fact]
    public void ReferenciaDeclaradaEmRegraSemColeta_NaoContaComoImplementada()
    {
        var semColeta = new KnightIndicatorDefinition("AK-TESTE-001", "1", "t", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Low,
            new HashSet<KnightSourceType> { KnightSourceType.MicrosoftEntraId }, Array.Empty<string>(), Array.Empty<string>(), "r", "e",
            _ => new KnightIndicatorOutcome(KnightIndicatorStatus.NotEvaluated, "x", 0, "x"))
        {
            References = new[] { new KnightReferenceLink("CIS-M365-7.0.0:5.1.2.5") },
        };
        var status = KnightReferenceCatalog.Coverage(new[] { semColeta }).Controls.Single(c => c.Control.Key == "CIS-M365-7.0.0:5.1.2.5");
        status.Disposition.Should().Be(KnightReferenceDisposition.Pending);
        status.Note.Should().Contain("fora do fluxo ativo");
    }

    [Fact]
    public void ControleCitandoReferenciaInexistente_EhDefeitoDeCatalogo()
    {
        var def = KnightCatalog.Indicators.First(d => d.Id == "AK-ENTRA-016") with
        {
            References = new[] { new KnightReferenceLink("CIS-M365-7.0.0:99.99") },
        };
        FluentActions.Invoking(() => KnightReferenceCatalog.Coverage(new[] { def })).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TodoControleAtivo_TemPerfilCompleto_ComRiscoEImpactoProprios()
    {
        var active = KnightCatalog.Indicators.Where(d => d.Sources.Any(s => s != KnightSourceType.Demo)).ToList();
        foreach (var d in active)
        {
            var p = KnightControlProfiles.For(d.Id);
            p.Should().NotBeNull(d.Id);
            p!.Impact.Should().NotBeNullOrWhiteSpace(d.Id);
            p.Rationale.Should().NotBeNullOrWhiteSpace(d.Id);
            p.RequiredCapabilities.Should().NotBeEmpty(d.Id);
            p.Documentation.All(r => r.Url == null || r.Url.StartsWith("https://learn.microsoft.com/")).Should().BeTrue(d.Id);
        }
        active.Select(d => KnightControlProfiles.For(d.Id)!.Impact).Should().OnlyHaveUniqueItems("texto genérico repetido não explica o controle");
        active.Select(d => KnightControlProfiles.For(d.Id)!.Rationale).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ControlesDeConfiguracao_SoConsomemCapacidadesDoColetorReal()
    {
        foreach (var d in EntraConfigurationControls.Definitions)
        {
            KnightCollectorCapabilities.IsActive(d).Should().BeTrue(d.Id);
            d.Service.Should().Be(KnightService.EntraId);
            d.References.Should().NotBeEmpty(d.Id);
        }
    }

    [Fact]
    public void ResumoCongelado_RelidoIgualAoPublicado()
    {
        var json = KnightReferenceCoverageSnapshot.Serialize(KnightReferenceCatalog.Coverage());
        var back = KnightReferenceCoverageSnapshot.Deserialize(json)!;
        back.Total.Implemented.Should().Be(KnightReferenceCatalog.Coverage().Total.Implemented);
        KnightReferenceCoverageSnapshot.Serialize(KnightReferenceCatalog.Coverage()).Should().Be(json, "mesma entrada, mesmos bytes");
        KnightReferenceCoverageSnapshot.Deserialize("{ilegível").Should().BeNull();
    }

    [Fact]
    public void HashDaFotografia_ProtegeImpactoECobertura_SemMudarAsAnteriores()
    {
        var s = new PostureSnapshot
        {
            TenantId = Guid.NewGuid(), Type = PostureSnapshotType.Knight, SchemaVersion = PostureSnapshotSchema.KnightReportVersion,
            FormulaVersion = "knight-score-v1", CatalogVersion = "ak-knight-v3", SemanticFamily = "knight:MicrosoftEntraId",
            SourceType = KnightSourceType.MicrosoftEntraId, CapturedAt = DateTimeOffset.UnixEpoch, Coverage = 100,
        };
        s.Indicators.Add(new PostureSnapshotIndicator { IndicatorId = "AK-ENTRA-001", Title = "t", Evidence = "e", SourceType = KnightSourceType.MicrosoftEntraId });
        var legacy = PostureSnapshotHasher.Compute(s);

        // Fotografia anterior (sem os campos novos): o hash não muda por existir a extensão.
        PostureSnapshotHasher.Compute(s).Should().Be(legacy);

        s.Indicators.Single().Impact = "impacto";
        s.ReferenceCoverageJson = "{}";
        s.ContentHash = PostureSnapshotHasher.Compute(s);
        s.ContentHash.Should().NotBe(legacy);
        PostureSnapshotHasher.Verify(s).Should().BeTrue();

        s.Indicators.Single().Impact = "outro impacto";
        PostureSnapshotHasher.Verify(s).Should().BeFalse("o impacto publicado é conteúdo assinado");
        s.Indicators.Single().Impact = "impacto";
        s.ReferenceCoverageJson = "{\"x\":1}";
        PostureSnapshotHasher.Verify(s).Should().BeFalse("a cobertura publicada é conteúdo assinado");
    }

    [Fact]
    public void ComposicaoNomeada_NaoTransformaAplicacaoEmUsuario()
    {
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.User, KnightAffectedObjectKind.User, KnightAffectedObjectKind.ServicePrincipal, KnightAffectedObjectKind.Group })
            .Should().Be("4 identidades: 2 contas de usuário, 1 aplicação e 1 grupo");
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.Domain }).Should().Be("1 domínio");
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.User, KnightAffectedObjectKind.User }, 75)
            .Should().Be("75 contas de usuário (lista preservada: 2 contas de usuário)");
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.DirectoryRole, KnightAffectedObjectKind.Policy })
            .Should().Be("2 itens: 1 política e 1 papel de diretório");
    }
}
