using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Relatório HTML do KNIGHT reorganizado: resumo executivo, riscos e ações prioritárias e
/// limitações desenhados no servidor (legíveis sem JavaScript), fontes incluídas × avaliadas pelas contagens congeladas, prioridades
/// com o que a fotografia sustenta, limitações equivalentes agrupadas sem perder a capacidade, origem correta da interpretação e
/// avisos de tendência do histórico. Tudo sai da fotografia — nenhum dado atual, nenhum recálculo de nota.
/// </summary>
public sealed class KnightResultsReportTests
{
    private static string Html(PostureSnapshot s) => Encoding.UTF8.GetString(PostureSnapshotHtmlWriter.Write(s, true));

    /// <summary>O trecho desenhado no servidor: tudo antes da ilha de dados e do script.</summary>
    private static string Static(string html) => html[..html.IndexOf("<script type=\"application/json\"", StringComparison.Ordinal)];

    private static void Compose(PostureSnapshot s, Func<KnightConsolidatedSourceEntry, KnightConsolidatedSourceEntry> change) =>
        s.CompositionJson = KnightConsolidatedCompositionJson.Serialize(
            KnightConsolidatedCompositionJson.Deserialize(s.CompositionJson)!.Select(change).ToList());

    [Fact]
    public void FontesIncluidasXFontesComControlesAvaliados_PelasContagensCongeladas()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        KnightReportModelBuilder.Build(s, true).Sources!.Text.Should().Be("4 fontes incluídas · 4 com controles avaliados");

        // Teams incluído, mas sem nenhum veredito: entra na composição e não conta como fonte avaliada.
        Compose(s, e => e.Source == KnightSourceType.MicrosoftTeams ? e with { PassedCount = 0, ExposedCount = 0, MitigatedCount = 0, NotEvaluatedCount = 5 } : e);
        var inc = KnightReportModelBuilder.Build(s, true).Sources!;

        using var _ = new AssertionScope();
        inc.Text.Should().Be("4 fontes incluídas · 3 com controles avaliados");
        inc.WithoutEvaluated.Should().Equal("Microsoft Teams");
        Static(Html(s)).Should().Contain("4 fontes incluídas · 3 com controles avaliados").And.Contain("sem controle avaliado: Microsoft Teams");
        KnightReportModelBuilder.Inclusion(null, "Microsoft Entra ID", 0).Text.Should().Be("1 fonte incluída · nenhuma com controles avaliados");
    }

    [Fact]
    public void Prioridades_TrazemRiscoImpactoAfetadosEOrientacaoCongelados_SemInventar()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        var entra = s.Indicators.Single(i => i.IndicatorId == "AK-ENTRA-001");
        entra.Rationale = "Conta privilegiada sem MFA pode ser tomada com uma senha vazada.";
        entra.Impact = "Controle administrativo do locatário.";

        var model = KnightReportModelBuilder.Build(s, true);
        var top = model.Priorities.First();

        using var _ = new AssertionScope();
        top.ControlId.Should().Be("AK-ENTRA-001", "crítico e com mais afetados vem primeiro");
        top.Risk.Should().Be(entra.Rationale);
        top.Impact.Should().Be(entra.Impact);
        top.Guidance.Should().Be("Ajustar a configuração (texto sintético).");
        top.AffectedComposition.Should().NotBeNullOrWhiteSpace();
        model.Priorities.Skip(1).Should().OnlyContain(p => p.Risk == null && p.Impact == null, "sem texto congelado, nada é preenchido");
        model.Priorities.Should().OnlyContain(p => model.Controls.Single(c => c.Id == p.ControlId).Status == "Exposed");

        var html = Static(Html(s));
        html.Should().Contain("Riscos e ações prioritárias").And.Contain(entra.Rationale).And.Contain("data-open=\"AK-ENTRA-001\"");
        html.IndexOf("id=\"aegis-summary\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("id=\"aegis-risks\"", StringComparison.Ordinal),
            "o resumo executivo vem antes dos riscos");
    }

    [Fact]
    public void Limitacoes_EquivalentesAgrupadas_DiferentesSeparadas_ComDetalheTecnicoSeguro()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        var process = new KnightCapabilityDiagnostic("read", KnightFailureKind.ProcessExit, 4100, null, "137", null, "c-1");
        s.CapabilitiesJson = JsonSerializer.Serialize(new List<KnightCapabilityStatus>
        {
            new(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Error, "Processo encerrado.", process),
            new(KnightCapability.DirectoryUsers, KnightCapabilityOutcome.Error, "Processo encerrado.", process),
            new(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Error, null, new KnightCapabilityDiagnostic("read", KnightFailureKind.Timeout, 30000)),
            new(KnightCapability.MfaRegistration, KnightCapabilityOutcome.Error, null, new KnightCapabilityDiagnostic("read", KnightFailureKind.CircuitOpen, 1)),
            new(KnightCapability.ApplicationInventory, KnightCapabilityOutcome.InsufficientPermission),
            new(KnightCapability.ApplicationConsents, KnightCapabilityOutcome.LimitedByLicense),
        }, KnightCapabilitiesJson.Options);

        var model = KnightReportModelBuilder.Build(s, true);

        using var _ = new AssertionScope();
        model.Limitations.Should().HaveCount(6, "cada capacidade continua consultável");
        model.LimitationGroups.Should().HaveCount(5, "o mesmo processo encerrado agrupa; tempo esgotado ≠ circuito aberto; permissão ≠ licença");
        model.LimitationGroups!.Single(g => g.Capabilities.Count == 2).KindLabel.Should().Be("o processo do adaptador terminou com código de erro");
        model.Limitations.Single(l => l.Capability == nameof(KnightCapability.GuestAccounts)).Technical.Should()
            .Contain("Correlação da execução: c-1").And.Contain("código 137");
        var html = Static(Html(s));
        html.Should().Contain("Limitações que afetam a conclusão").And.Contain("tempo esgotado").And.Contain("circuito do cliente HTTP aberto");
    }

    [Fact]
    public void SemRegistroDeCapacidades_ComControlesNaoAvaliados_NaoAfirmaColetaCompleta()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        s.CapabilitiesJson = null;
        var model = KnightReportModelBuilder.Build(s, true);
        var pending = model.Kpis.NotEvaluated + model.Kpis.Errors;
        var html = Html(s);

        using var _ = new AssertionScope();
        pending.Should().BePositive("o cenário tem controles não avaliados");
        model.Limitations.Should().BeEmpty();
        model.NoLimitationText.Should().Be($"Não há limitações de coleta registradas nesta fotografia. Isso não comprova coleta completa: {pending} controles aplicáveis ficaram sem avaliação.");
        Static(html).Should().Contain(model.NoLimitationText!, "caminho desenhado no servidor");
        html.Should().NotContain("foram lidas", "nem o servidor nem o script afirmam leitura completa sem registro")
            .And.Contain("D.noLimitationText", "o caminho montado por JavaScript usa o mesmo texto da ilha de dados");

        // Só com registro explícito, e TODO coletado, afirma-se a leitura completa.
        s.CapabilitiesJson = JsonSerializer.Serialize(new List<KnightCapabilityStatus>
        {
            new(KnightCapability.DirectoryUsers, KnightCapabilityOutcome.Collected),
            new(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Collected),
        }, KnightCapabilitiesJson.Options);
        KnightReportModelBuilder.Build(s, true).NoLimitationText.Should().Be("Todas as 2 capacidades de coleta registradas nesta fotografia foram lidas.");
    }

    [Fact]
    public void Hierarquia_ResumoRiscosResultadosELimitacoesNoFim_ComAvisoCompactoPertoDaNota()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: true);
        s.CapabilitiesJson = JsonSerializer.Serialize(new List<KnightCapabilityStatus>
        {
            new(KnightCapability.DirectoryUsers, KnightCapabilityOutcome.Error, null, new KnightCapabilityDiagnostic("read", KnightFailureKind.Timeout, 30000)),
        }, KnightCapabilitiesJson.Options);
        var html = Html(s);
        var stat = Static(html);
        int At(string id) => stat.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);

        using var _ = new AssertionScope();
        new[] { At("aegis-summary"), At("aegis-risks"), At("aegis-visuals"), At("aegis-limits"), stat.IndexOf("<noscript>", StringComparison.Ordinal) }
            .Should().BeInAscendingOrder("resumo → riscos → resultados → limitações e interpretação, também sem JavaScript").And.NotContain(-1);
        var summary = stat[At("aegis-summary")..At("aegis-risks")];
        summary.Should().Contain("1 limitação de coleta registrada").And.Contain("href=\"#aegis-limits\"")
            .And.NotContain("Limitações que afetam a conclusão", "o detalhamento não fica entre a nota e os riscos")
            .And.NotContain("Interpretação consultiva");
        stat[At("aegis-limits")..].Should().Contain("Limitações que afetam a conclusão").And.Contain("Interpretação consultiva");
        html.Should().Contain("getElementById('aegis-limits')", "com JavaScript o bloco vai para o fim da primeira aba, depois dos resultados");
    }

    [Fact]
    public void CoresDosEstados_MesmaCorrespondenciaDoKnight_NosDoisTemas()
    {
        var html = Html(AssessmentVisualsFixtures.Knight(withHistory: true));

        using var _ = new AssertionScope();
        // Aprovado = ciano e reprovado = vermelho-rosado, como na aplicação; mitigado âmbar, erro violeta, não avaliado e não aplicável cinzas distintos.
        html.Should().Contain("--ok:#006b7d;").And.Contain("--fail:#c2185b;").And.Contain("--ok:#26e0ff;").And.Contain("--fail:#ff5c8a;");
        html.Should().NotContain("--ok:#1b7f3b").And.NotContain("--ok:#57c27a").And.NotContain("--fail:#b3261e").And.NotContain("--fail:#ff7a70");
        html.Should().Contain(".seg.NotApplicable{background:var(--na)}").And.Contain("--na:#b7c0cf;").And.Contain("--na:#4a5468;");
    }

    [Fact]
    public void ResumoExecutivo_LegivelSemJavaScript_ComNotaAoLadoDaCoberturaEAvisoDeParcial()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        var k = KnightReportModelBuilder.Build(s, true).Kpis;
        var applicable = k.Evaluated + k.NotEvaluated + k.Errors;
        var html = Html(s);
        var stat = Static(html);

        using var _ = new AssertionScope();
        stat.Should().Contain("Resumo executivo").And.Contain("Nota KNIGHT").And.Contain($"{k.Evaluated} de {applicable} controles aplicáveis avaliados");
        stat.Should().Contain($"Avaliação parcial: a nota considera {k.Evaluated} de {applicable} controles aplicáveis");
        stat.Should().NotContain(" style=", "a CSP só autoriza estilo por hash: nada de atributo de estilo");
        html.Should().Contain("default-src 'none'").And.NotContain("<script src").And.NotContain("<link");
        stat.IndexOf("id=\"aegis-summary\"", StringComparison.Ordinal).Should().BeLessThan(stat.IndexOf("<noscript>", StringComparison.Ordinal));
    }

    [Fact]
    public void SemControleAvaliado_SemNota_NuncaZeroSimulado()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        foreach (var i in s.Indicators) i.Status = KnightIndicatorStatus.NotEvaluated;
        s.Score = null;
        s.Coverage = 0;
        s.CompliantCount = s.NonCompliantCount = s.MitigatedCount = s.ErrorCount = s.NotApplicableCount = 0;
        s.NotEvaluatedCount = s.Indicators.Count;

        var stat = Static(Html(s));
        stat.Should().Contain("Sem nota: nenhum controle pôde ser avaliado").And.Contain("não indica conformidade")
            .And.NotContain("0<small>/100</small>").And.Contain("Nenhum controle reprovado nesta avaliação.")
            .And.Contain("não indica ausência de risco");
    }

    [Fact]
    public void Interpretacao_OrigemDoConteudoQueExiste_SemAnunciarFallbackVazio()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: false);
        Static(Html(s)).Should().Contain("Nenhuma interpretação registrada nesta fotografia").And.NotContain("IA indisponível");

        s.AdvisoryJson = JsonSerializer.Serialize(new { executiveSummary = "Resumo determinístico sintético.", priorityRisks = Array.Empty<object>(),
            recommendedActions = Array.Empty<object>(), correlations = Array.Empty<object>(), collectionGaps = new[] { "Teams não coletado." } });
        s.AdvisoryFromAi = false;
        Static(Html(s)).Should().Contain("Resumo determinístico do AEGIS").And.Contain("Resumo determinístico sintético.").And.Contain("Teams não coletado.");
    }

    [Fact]
    public void Historico_UmPontoNaoETendencia_MesAusenteNaoEContinuidade()
    {
        var full = AssessmentVisualsFixtures.Knight(withHistory: true);
        Html(full).Should().Contain("1 mês(es) sem publicação entre o primeiro e o último ponto", "janeiro não teve publicação");

        var single = AssessmentVisualsFixtures.Knight(withHistory: true, new HistoryWindow(new DateOnly(2026, 9, 1), 1));
        Html(single).Should().Contain("Um único mês com publicação: ainda não há tendência.");
    }

    [Fact]
    public void ReexportarEDeterministico_EAFotografiaNaoMuda()
    {
        var s = AssessmentVisualsFixtures.Knight(withHistory: true);
        var hash = s.ContentHash;
        Html(s).Should().Be(Html(s));
        PostureSnapshotHasher.Compute(s).Should().Be(hash, "o relatório não altera a fotografia");
    }
}
