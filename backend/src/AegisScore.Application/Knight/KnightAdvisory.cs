using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture.Export;
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
    IReadOnlyList<string> Limitations,
    // [AEGIS-KNIGHT-MEMORY-BUDGET] Desfecho e diagnóstico ESTRUTURADOS de cada capacidade, para o resumo determinístico agrupar as lacunas
    // pela causa, sem comparar textos. Fica fora do que é enviado à IA, que continua recebendo as limitações em texto.
    [property: JsonIgnore] IReadOnlyList<KnightCapabilityStatus>? Capabilities = null);

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
        var unknown = indicators.Where(i => i.Status is KnightIndicatorStatus.NotEvaluated or KnightIndicatorStatus.Error).ToList();
        var groups = GapGroups(input, unknown);

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
            .Select(i => (Text: DefinitionFor(i.IndicatorId)?.Recommendation ?? "Revisar a exposição identificada.", Ids: (IReadOnlyList<string>)new[] { i.IndicatorId }))
            .Concat(groups.Where(g => g.Guidance is not null).Select(g => (
                Text: $"Restabelecer a coleta de {SourceName(input.Source)} ({g.CauseLabel}): {g.Guidance} Ver as lacunas de coleta.",
                Ids: (IReadOnlyList<string>)Array.Empty<string>())))
            .Select((a, idx) => new KnightRecommendedAction(idx + 1, a.Text, a.Ids))
            .ToList();

        // Correlações: indicadores expostos que compartilham ao menos um código NIST são tratados em conjunto.
        var correlations = BuildCorrelations(indicators.Where(i => i.Status == KnightIndicatorStatus.Exposed).ToList());

        var gaps = groups.Select(g => g.Text(input.Source)).ToList();
        if (input.Capabilities is null)
            gaps.AddRange(input.Limitations);   // quem não entrega o estruturado (fontes antigas de teste) segue com as limitações em texto
        if (input.Source == KnightSourceType.Demo)
            gaps.Add("Modo demonstração: superfície sintética (example.com); integração real (Microsoft Graph/AD/Okta) ainda não conectada.");

        return new KnightAdvisory(Summary(input, exposed, mitigated, passed, unknown.Count, groups), priorityRisks, actions, correlations, gaps);
    }

    /// <summary>
    /// [AEGIS-LANGUAGE-STATES-01] Mesmo vocabulário das telas: o score é o do KNIGHT (escala própria), a cobertura é a fração de indicadores
    /// avaliados, e "exposto" é o veredito da regra. [AEGIS-KNIGHT-MEMORY-BUDGET] O que foi comprovado fica separado do que ficou desconhecido;
    /// sem nenhum controle avaliado não há o que priorizar entre achados, e a prioridade é restabelecer a coleta.
    /// </summary>
    private static string Summary(KnightAdvisoryInput input, int exposed, int mitigated, int passed, int unknown, IReadOnlyList<GapGroup> groups)
    {
        const string Origin = "Interpretação determinística (sem IA). ";
        var evaluated = exposed + mitigated + passed;
        if (evaluated == 0)
        {
            if (unknown == 0) return Origin + "Nenhum controle aplicável foi avaliado nesta execução; não há conclusão de postura a apresentar.";
            var main = groups.FirstOrDefault(g => g.Guidance is not null);
            return Origin + $"A avaliação não conseguiu concluir a postura: nenhum dos {unknown} controle(s) aplicável(is) pôde ser avaliado. "
                + "Isso não comprova risco nem conformidade, e o score KNIGHT fica indisponível. "
                + (main is null
                    ? "A leitura oficial destes controles não está disponível neste ambiente; o detalhe está nas lacunas de coleta."
                    : $"A prioridade operacional é restabelecer a coleta — causa principal: {main.CauseLabel}.");
        }

        var scoreText = input.Score is { } sc ? sc.ToString("0", CultureInfo.InvariantCulture) : "indisponível";
        var summary = Origin + $"Score KNIGHT (escala própria): {scoreText}; cobertura de {input.Coverage.ToString("0.#", CultureInfo.InvariantCulture)}% dos indicadores aplicáveis. "
            + $"Comprovado pela coleta: {exposed} exposto(s), {mitigated} mitigado(s) e {passed} conforme(s), em {evaluated} controle(s) avaliado(s). ";
        if (unknown > 0)
            summary += $"Sem conclusão: {unknown} controle(s) sem dados suficientes, que não contam como risco nem como conformidade. ";
        summary += exposed > 0 ? "Priorize os expostos de maior severidade" : "Nenhuma exposição foi comprovada entre os controles avaliados";
        return summary + (unknown > 0 ? "; a parte sem conclusão depende de restabelecer a coleta." : ".");
    }

    /// <summary>
    /// [AEGIS-KNIGHT-MEMORY-BUDGET] Lacunas agrupadas pela causa estruturada (tipo técnico, quando existe; senão o desfecho): uma linha com a
    /// fonte, as capacidades afetadas e os controles relacionados, em vez do mesmo parágrafo por controle e de novo por capacidade.
    /// </summary>
    private static List<GapGroup> GapGroups(KnightAdvisoryInput input, IReadOnlyList<KnightAdvisoryIndicator> unknown)
    {
        var failed = (input.Capabilities ?? Array.Empty<KnightCapabilityStatus>()).Where(c => c.Outcome != KnightCapabilityOutcome.Collected).ToList();
        var remaining = unknown.Select(i => i.IndicatorId).ToHashSet(StringComparer.Ordinal);
        var groups = failed
            .GroupBy(c => (Kind: CauseKind(c), c.Outcome))
            .Select(g =>
            {
                var capabilities = g.Select(c => c.Capability).ToHashSet();
                var controls = unknown
                    .Where(i => RequiredOf(i.IndicatorId).Any(capabilities.Contains))
                    .Select(i => i.IndicatorId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
                remaining.ExceptWith(controls);
                return new GapGroup(
                    g.Key.Kind is { } kind ? KnightReportModelBuilder.FailureKindLabel(kind.ToString())! : KnightReportModelBuilder.CauseLabel(g.Key.Outcome),
                    GuidanceFor(g.Key.Kind, g.Key.Outcome, capabilities),
                    g.Select(c => KnightCapabilityLabels.Label(c.Capability)).Distinct(StringComparer.Ordinal).ToList(),
                    controls);
            })
            .OrderByDescending(g => g.Controls.Count).ThenByDescending(g => g.Capabilities.Count)
            .ThenBy(g => g.CauseLabel, StringComparer.Ordinal)
            .ToList();

        if (remaining.Count > 0)
            groups.Add(new GapGroup(
                "sem dados suficientes para a regra (sem falha de coleta associada)", null, Array.Empty<string>(),
                remaining.OrderBy(id => id, StringComparer.Ordinal).ToList()));
        return groups;
    }

    private static IReadOnlyList<KnightCapability> RequiredOf(string indicatorId)
    {
        var required = KnightControlProfiles.RequiredCapabilitiesOf(indicatorId);
        return required.Count > 0 ? required : KnightIndicatorEvidence.RequiredCapabilities(indicatorId);
    }

    /// <summary>O tipo técnico é a causa quando diz mais que o desfecho; status HTTP, sessão e comando dependem do desfecho que produziram.</summary>
    private static KnightFailureKind? CauseKind(KnightCapabilityStatus c) =>
        c.Diagnostic?.Kind is { } kind and not (KnightFailureKind.HttpStatus or KnightFailureKind.Connection or KnightFailureKind.Command) ? kind : null;

    /// <summary>O que fazer para restabelecer a coleta. Nulo quando repetir a coleta não resolve (limitação conhecida, leitura não solicitada).</summary>
    private static string? GuidanceFor(KnightFailureKind? kind, KnightCapabilityOutcome outcome, IReadOnlySet<KnightCapability> capabilities)
    {
        if (outcome == KnightCapabilityOutcome.PlatformUnsupported || kind == KnightFailureKind.NoScope) return null;
        if (outcome == KnightCapabilityOutcome.NotAttempted && capabilities.All(KnightCollectorCapabilities.NotRequested.Contains)) return null;
        return kind switch
        {
            KnightFailureKind.MemoryLimit => "a interrupção foi do próprio AEGIS, por memória no contêiner; rever a capacidade do ambiente antes de repetir a sincronização.",
            KnightFailureKind.RuntimeUnavailable => "conferir o runtime e o módulo do adaptador nesta implantação e sincronizar novamente.",
            KnightFailureKind.Timeout or KnightFailureKind.CircuitOpen or KnightFailureKind.Transport => "sincronizar novamente; persistindo, verificar a conectividade.",
            KnightFailureKind.Contract or KnightFailureKind.Internal or KnightFailureKind.ProcessExit or KnightFailureKind.ProcessNoResult
                or KnightFailureKind.OutputLimit or KnightFailureKind.Incomplete => "sincronizar novamente; persistindo, acionar o suporte com a correlação do detalhe técnico.",
            _ => outcome switch
            {
                KnightCapabilityOutcome.InsufficientPermission => "conferir o diagnóstico de acesso antes de alterar permissões; a recusa não comprova, sozinha, qual requisito falta.",
                KnightCapabilityOutcome.AuthenticationFailure => "verificar a credencial do conector em Integrações.",
                KnightCapabilityOutcome.LimitedByLicense => "conferir a licença que a capacidade exige.",
                KnightCapabilityOutcome.Throttled => "sincronizar novamente mais tarde.",
                _ => "sincronizar novamente; persistindo, acionar o suporte com a correlação do detalhe técnico.",
            },
        };
    }

    private static string SourceName(KnightSourceType source) => KnightSourceCatalog.Label(source);

    private sealed record GapGroup(string CauseLabel, string? Guidance, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Controls)
    {
        public string Text(KnightSourceType source) =>
            $"{SourceName(source)} — {CauseLabel}"
            + (Capabilities.Count == 0 ? "" : $": {Capabilities.Count} capacidade(s) afetada(s) ({string.Join(", ", Capabilities)})")
            + (Controls.Count == 0 ? "; nenhum controle aplicável depende delas." : $"; {Controls.Count} controle(s) sem conclusão: {string.Join(", ", Controls)}.");
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
                    $"Achados relacionados pelo(s) controle(s) NIST {string.Join(", ", shared)} — podem ser revisados em conjunto.",
                    new[] { exposed[a].IndicatorId, exposed[b].IndicatorId }));
            }
        }
        return correlations;
    }

    private static KnightIndicatorDefinition? DefinitionFor(string indicatorId) =>
        KnightCatalog.Indicators.FirstOrDefault(d => d.Id == indicatorId);
}
