using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-CONSOLIDATED-01] Relatório KNIGHT CONSOLIDADO
// ============================================================================
// Combina avaliações CONCLUÍDAS de várias fontes reais do MESMO tenant num único relatório, sem recoletar nada
// e sem inventar um "score combinado entre instrumentos" (esse continua proibido — ver PostureSnapshot.cs). O
// que muda aqui é mais estreito: a fórmula knight-score-v1 já é uma soma ponderada por severidade sobre uma
// LISTA de indicadores; nada na fórmula pressupõe que a lista venha de uma fonte só. Aplicá-la sobre a UNIÃO dos
// indicadores de Entra ID + Teams + Exchange Online produz uma nota legítima — bem diferente de tirar a média
// simples das três notas — desde que cada indicador apareça EXATAMENTE uma vez. Como os catálogos são
// namespaced por fonte (AK-ENTRA-*, AK-TEAMS-*, AK-EXO-*), não há colisão de IndicatorId entre fontes e,
// portanto, nenhum resultado é contado duas vezes.
//
// Uma fonte SEM avaliação concluída, ou disponível mas não escolhida pelo chamador, nunca vira aprovação
// silenciosa: ela aparece na composição com o próprio estado, sem contribuir para a nota nem para a cobertura
// combinadas.

/// <summary>
/// As três fontes candidatas do relatório consolidado (Microsoft 365 restante e Azure ficam de fora desta
/// entrega — ver AEGIS_STATE.md). A ORDEM é a de apresentação em toda superfície (API, HTML, CSV, PDF).
/// </summary>
public static class KnightConsolidatedCandidates
{
    // [AEGIS-KNIGHT-COVERAGE-04] A lista deixou de ser fixa em três: vem do catálogo único de fontes. Fotografias
    // antigas continuam com a composição que congelaram (três entradas) — nada é recalculado nelas.
    public static readonly IReadOnlyList<KnightSourceType> Sources = KnightSourceCatalog.ConsolidationCandidates;

    /// <summary>Rótulo ESTÁVEL de uma fonte candidata mesmo quando ela nunca produziu avaliação alguma (sem <see cref="KnightSourceLatest"/> para copiar).</summary>
    public static string StaticLabel(KnightSourceType source) => KnightSourceCatalog.Label(source);
}

/// <summary>
/// Resolve o nome de uma fonte KNIGHT vindo de fora (rota, query string, corpo) — autoridade ÚNICA reusada
/// pelos controllers que aceitam fonte por nome, para não haver duas listas de apelidos divergentes.
/// </summary>
public static class KnightSourceNames
{
    public static bool TryParse(string? value, out KnightSourceType source) => KnightSourceCatalog.TryParse(value, out source);
}

/// <summary>
/// Composição de UMA fonte candidata no relatório consolidado. <see cref="Included"/> distingue quem CONTRIBUI
/// para a nota/cobertura combinadas de quem só aparece para que a ausência seja visível. Os campos numéricos são
/// SEMPRE os da própria fonte (nunca recalculados aqui) — nulos quando não há avaliação concluída para ela.
/// </summary>
public sealed record KnightConsolidatedSourceEntry(
    KnightSourceType Source,
    string Label,
    bool Included,
    /// <summary>"Included" | "Available" (tem avaliação concluída, mas não foi escolhida) | "NotAssessed" (nunca concluiu uma avaliação).</summary>
    string AvailabilityState,
    Guid? SourceRunId,
    string? SourceState,
    string? CatalogVersion,
    /// <summary>Instante da avaliação usada (conclusão, ou início quando não há conclusão registrada) — não recalculado.</summary>
    DateTimeOffset? CapturedAt,
    double? Score,
    double? Coverage,
    int? PassedCount,
    int? ExposedCount,
    int? MitigatedCount,
    int? NotEvaluatedCount,
    int? ErrorCount,
    int? NotApplicableCount,
    IReadOnlyList<string> CollectionLimitations);

/// <summary>
/// Leitura AO VIVO (nunca persistida, nunca dispara coleta) do relatório KNIGHT consolidado: a última avaliação
/// concluída de cada fonte ESCOLHIDA, combinada pela MESMA fórmula knight-score-v1 sobre a união dos
/// indicadores, mais a composição das três fontes candidatas (escolhidas, disponíveis ou nunca avaliadas).
/// </summary>
public sealed record KnightConsolidatedAssessment(
    IReadOnlyList<KnightSourceType> IncludedSources,
    string FormulaVersion,
    double? Score,
    double Coverage,
    int PassedCount,
    int ExposedCount,
    int MitigatedCount,
    int NotEvaluatedCount,
    int ErrorCount,
    int NotApplicableCount,
    IReadOnlyList<KnightIndicatorView> Indicators,
    IReadOnlyList<KnightCapabilityStatus> Capabilities,
    IReadOnlyList<KnightConsolidatedSourceEntry> Sources,
    /// <summary>Mais ANTIGA das recências das fontes incluídas — o relatório é tão fresco quanto a fonte mais velha nele.</summary>
    DateTimeOffset? DataRecency);

/// <summary>
/// Um par fonte→execução PINADO explicitamente pelo chamador de uma publicação consolidada — a execução EXATA
/// que a tela mostrava como incluída no instante da publicação. O serviço nunca recalcula "a mais recente" para
/// uma fonte presente aqui, e revalida tenant (Global Query Filter), <see cref="KnightSourceType"/> e conclusão
/// antes de aceitar.
/// </summary>
public sealed record KnightConsolidatedSourceSelection(KnightSourceType Source, Guid RunId);

/// <summary>
/// Combina <see cref="KnightAssessment"/>s já lidos (por <c>GetLatestBySourceAsync</c>) num
/// <see cref="KnightConsolidatedAssessment"/> — função PURA, sem EF/rede, para ser testável sem banco.
/// </summary>
public static class KnightConsolidatedBuilder
{
    /// <summary>
    /// <paramref name="requestedSources"/> NULO é o padrão CLARO: todas as candidatas com avaliação concluída.
    /// Uma lista PRESENTE, mesmo vazia, é a escolha EXPLÍCITA do chamador — inclusive "nenhuma fonte", que NÃO
    /// deve reverter ao padrão silenciosamente (a pessoa desmarcou tudo de propósito). Uma fonte pedida que não
    /// é candidata (ex.: "google", "demo") é ignorada — pedir outra coisa não deve produzir composição vazia
    /// por engano quando há candidata elegível.
    /// </summary>
    public static KnightConsolidatedAssessment Build(KnightLatestBySource latest, IReadOnlyCollection<KnightSourceType>? requestedSources)
    {
        var candidates = KnightConsolidatedCandidates.Sources;

        var wanted = new HashSet<KnightSourceType>(
            requestedSources is null ? candidates : requestedSources.Where(candidates.Contains));

        var bySource = latest.Sources.ToDictionary(s => s.Source);

        var entries = new List<KnightConsolidatedSourceEntry>();
        var includedSources = new List<KnightSourceType>();
        var includedIndicators = new List<KnightIndicatorView>();
        var includedCapabilities = new List<KnightCapabilityStatus>();
        DateTimeOffset? recency = null;

        foreach (var source in candidates)
        {
            bySource.TryGetValue(source, out var slot);
            var assessment = slot?.Assessment;
            var label = slot?.Label ?? KnightConsolidatedCandidates.StaticLabel(source);

            if (assessment is null)
            {
                entries.Add(new KnightConsolidatedSourceEntry(
                    source, label, false, "NotAssessed",
                    null, null, null, null, null, null, null, null, null, null, null, null,
                    Array.Empty<string>()));
                continue;
            }

            var included = wanted.Contains(source);
            var at = assessment.CompletedAt ?? assessment.StartedAt;
            entries.Add(new KnightConsolidatedSourceEntry(
                source, label, included, included ? "Included" : "Available",
                assessment.Id, assessment.SourceState.ToString(), assessment.CatalogVersion, at,
                assessment.Score, assessment.Coverage,
                assessment.PassedCount, assessment.ExposedCount, assessment.MitigatedCount,
                assessment.NotEvaluatedCount, assessment.ErrorCount, assessment.NotApplicableCount,
                CollectionLimitationsOf(assessment)));

            if (!included) continue;

            includedSources.Add(source);
            includedIndicators.AddRange(assessment.Indicators);
            includedCapabilities.AddRange(assessment.Capabilities);
            recency = recency is null || at < recency ? at : recency;
        }

        var score = KnightScoreFormula.Compute(includedIndicators.Select(i => (i.Severity, i.Status)));

        return new KnightConsolidatedAssessment(
            includedSources, KnightScoreFormula.Version, score.Score, score.Coverage,
            score.PassedCount, score.ExposedCount, score.MitigatedCount,
            score.NotEvaluatedCount, score.ErrorCount, score.NotApplicableCount,
            includedIndicators, includedCapabilities, entries, recency);
    }

    /// <summary>Mesma leitura usada no relatório por fonte: só capacidades que NÃO foram coletadas.</summary>
    private static List<string> CollectionLimitationsOf(KnightAssessment a) =>
        a.Capabilities
            .Where(c => KnightIndicatorEvidence.IsFailedOutcome(c.Outcome))
            .OrderBy(c => c.Capability)
            .Select(c => string.IsNullOrWhiteSpace(c.Detail)
                ? $"{c.Capability}: {c.Outcome}"
                : $"{c.Capability}: {c.Outcome} — {c.Detail}")
            .ToList();
}
