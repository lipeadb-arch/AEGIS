using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>Projeção enxuta de um indicador avaliado, enviada à IA consultiva (sem PII nem credencial).</summary>
public sealed record KnightAdvisoryIndicator(
    string IndicatorId,
    string Title,
    KnightIndicatorCategory Category,
    SeverityLevel Severity,
    KnightIndicatorStatus Status,
    string Evidence,
    int AffectedObjectCount,
    IReadOnlyList<string> NistCodes,
    IReadOnlyList<string> MitreTechniques);

/// <summary>
/// Entrada da ÚNICA chamada de IA por assessment: a FONTE, os resultados determinísticos agregados, o score,
/// a cobertura e as LIMITAÇÕES de coleta (capacidades sem permissão/indisponíveis). Sem credenciais, tokens
/// ou PII desnecessária — só o necessário para a IA priorizar, explicar e destacar lacunas de cobertura.
/// </summary>
public sealed record KnightAdvisoryInput(
    KnightSourceType Source,
    KnightAssessmentMode Mode,
    double? Score,
    double Coverage,
    IReadOnlyList<KnightAdvisoryIndicator> Indicators,
    IReadOnlyList<string> Limitations);

/// <summary>Um risco prioritário identificado pela camada consultiva, citando os indicadores que o embasam.</summary>
public sealed record KnightPriorityRisk(string Title, string Rationale, IReadOnlyList<string> IndicatorIds);

/// <summary>Uma ação recomendada, ordenada por prioridade, citando os indicadores que a motivam.</summary>
public sealed record KnightRecommendedAction(int Order, string Action, IReadOnlyList<string> IndicatorIds);

/// <summary>Uma correlação entre achados, citando os indicadores correlacionados.</summary>
public sealed record KnightCorrelation(string Description, IReadOnlyList<string> IndicatorIds);

/// <summary>
/// Saída ESTRUTURADA e CONSULTIVA da camada de IA (ou do fallback determinístico). NUNCA contém status,
/// severidade, score, cobertura ou mapeamento — a IA não decide nada disso; apenas interpreta e prioriza
/// sobre os vereditos determinísticos já fechados.
/// </summary>
public sealed record KnightAdvisory(
    string ExecutiveSummary,
    IReadOnlyList<KnightPriorityRisk> PriorityRisks,
    IReadOnlyList<KnightRecommendedAction> RecommendedActions,
    IReadOnlyList<KnightCorrelation> Correlations,
    IReadOnlyList<string> CollectionGaps);

/// <summary>Resultado da geração do resumo consultivo: a narrativa e a indicação explícita de sua procedência.</summary>
/// <param name="Advisory">A narrativa estruturada.</param>
/// <param name="FromAi">TRUE se veio do motor de IA; FALSE se é o fallback determinístico (IA indisponível/inválida).</param>
public sealed record KnightAdvisoryResult(KnightAdvisory Advisory, bool FromAi);

/// <summary>
/// Porta da camada consultiva de IA do KNIGHT. Contrato: UMA chamada por assessment (nunca por indicador).
/// A implementação JAMAIS altera vereditos, score ou cobertura, e SEMPRE retorna um resultado — recorrendo
/// ao fallback determinístico quando a IA está indisponível. A indisponibilidade da IA não reprova o assessment.
/// </summary>
public interface IKnightAdvisoryGenerator
{
    Task<KnightAdvisoryResult> GenerateAsync(KnightAdvisoryInput input, CancellationToken ct = default);
}

/// <summary>
/// Fallback determinístico do resumo consultivo — construído SÓ a partir dos vereditos determinísticos, sem
/// rede nem IA. Usado quando a IA está indisponível/inválida, garantindo que o assessment sempre tenha uma
/// narrativa. PURO e testável; cita os IndicatorIds em cada conclusão.
/// </summary>
public static class KnightAdvisoryFallback
{
    /// <summary>Vereditos que representam RISCO (entram na priorização): exposto primeiro, depois mitigado.</summary>
    private static int RiskRank(KnightIndicatorStatus status) => status switch
    {
        KnightIndicatorStatus.Exposed   => 0,
        KnightIndicatorStatus.Mitigated => 1,
        _                               => 2,   // Passed/NotEvaluated/Error/NotApplicable não são "risco a priorizar"
    };

    private static bool IsRisk(KnightIndicatorStatus status) =>
        status is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated;

    public static KnightAdvisory Build(KnightAdvisoryInput input)
    {
        var indicators = input.Indicators;

        // Ordem de prioridade: risco (exposto > mitigado), depois maior peso de severidade, depois ID estável.
        var ranked = indicators
            .Where(i => IsRisk(i.Status))
            .OrderBy(i => RiskRank(i.Status))
            .ThenByDescending(i => KnightScoreFormula.WeightFor(i.Severity))
            .ThenBy(i => i.IndicatorId, StringComparer.Ordinal)
            .ToList();

        var exposed = indicators.Count(i => i.Status == KnightIndicatorStatus.Exposed);
        var mitigated = indicators.Count(i => i.Status == KnightIndicatorStatus.Mitigated);
        var passed = indicators.Count(i => i.Status == KnightIndicatorStatus.Passed);

        var scoreText = input.Score is { } sc
            ? sc.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : "indisponível (nenhum indicador avaliado)";
        var summary =
            $"Interpretação determinística (fallback, sem IA). Postura KNIGHT: score {scoreText}, "
            + $"cobertura {input.Coverage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%. "
            + $"{exposed} exposição(ões), {mitigated} mitigada(s) e {passed} conforme(s) "
            + $"entre {indicators.Count} indicador(es). Priorize os itens expostos de maior severidade.";

        var priorityRisks = ranked
            .Take(3)
            .Select(i => new KnightPriorityRisk(
                i.Title,
                i.Status == KnightIndicatorStatus.Mitigated
                    ? $"Risco mitigado por controle compensatório: {i.Evidence}"
                    : i.Evidence,
                new[] { i.IndicatorId }))
            .ToList();

        var actions = ranked
            .Select((i, idx) => new KnightRecommendedAction(
                idx + 1,
                DefinitionFor(i.IndicatorId)?.Recommendation ?? "Revisar a exposição identificada.",
                new[] { i.IndicatorId }))
            .ToList();

        // Correlações: indicadores expostos que compartilham ao menos um código NIST são tratados em conjunto.
        var correlations = BuildCorrelations(indicators.Where(i => i.Status == KnightIndicatorStatus.Exposed).ToList());

        // Lacunas de coleta: indicadores não avaliados/erro (com o motivo) + limitações de capacidade da fonte.
        var gaps = new List<string>();
        foreach (var i in indicators.Where(i =>
                     i.Status is KnightIndicatorStatus.NotEvaluated or KnightIndicatorStatus.Error))
            gaps.Add($"{i.IndicatorId}: {i.Evidence}");
        foreach (var lim in input.Limitations)
            gaps.Add(lim);
        if (input.Source == KnightSourceType.Demo)
            gaps.Add("Modo demonstração: superfície sintética (example.com); integração real (Microsoft Graph/AD/Okta) ainda não conectada.");

        return new KnightAdvisory(summary, priorityRisks, actions, correlations, gaps);
    }

    private static IReadOnlyList<KnightCorrelation> BuildCorrelations(IReadOnlyList<KnightAdvisoryIndicator> exposed)
    {
        var correlations = new List<KnightCorrelation>();
        for (var a = 0; a < exposed.Count; a++)
        {
            for (var b = a + 1; b < exposed.Count; b++)
            {
                var shared = exposed[a].NistCodes.Intersect(exposed[b].NistCodes, StringComparer.Ordinal).ToList();
                if (shared.Count == 0) continue;
                correlations.Add(new KnightCorrelation(
                    $"Exposições relacionadas pelo(s) controle(s) NIST {string.Join(", ", shared)} — tratar em conjunto amplia o efeito da correção.",
                    new[] { exposed[a].IndicatorId, exposed[b].IndicatorId }));
            }
        }
        return correlations;
    }

    private static KnightIndicatorDefinition? DefinitionFor(string indicatorId) =>
        KnightCatalog.Indicators.FirstOrDefault(d => d.Id == indicatorId);
}
