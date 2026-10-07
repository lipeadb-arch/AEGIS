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
// indicadores das fontes INCLUÍDAS (qualquer das elegíveis do catálogo) produz uma nota legítima — bem diferente de tirar
// a média simples das notas por fonte — desde que cada indicador apareça EXATAMENTE uma vez. Como os catálogos são
// namespaced por fonte (AK-ENTRA-*, AK-TEAMS-*, AK-EXO-*), não há colisão de IndicatorId entre fontes e,
// portanto, nenhum resultado é contado duas vezes.
//
// Uma fonte SEM avaliação concluída, ou disponível mas não escolhida pelo chamador, nunca vira aprovação
// silenciosa: ela aparece na composição com o próprio estado, sem contribuir para a nota nem para a cobertura
// combinadas.

/// <summary>
/// As fontes candidatas do relatório consolidado — as nove fontes Microsoft elegíveis do catálogo único de fontes. A ORDEM
/// é a de apresentação em toda superfície (API, HTML, CSV, PDF). Toda fonte selecionada e com avaliação concluída entra no
/// cálculo; nenhuma aparece só decorativamente na composição.
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
/// indicadores, mais a composição de todas as fontes candidatas (escolhidas, disponíveis ou nunca avaliadas).
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
/// [AEGIS-KNIGHT-COVERAGE-04] Estado da COLETA de um relatório consolidado, separado do término da execução. A
/// composição termina sempre (ela só lê avaliações já concluídas), mas a coleta só é completa quando TODAS as fontes
/// incluídas foram coletadas integralmente — uma fonte parcial torna o conjunto parcial, e quais fontes são parciais
/// fica dito, com as limitações de cada uma preservadas na composição.
/// </summary>
public static class KnightConsolidatedCollection
{
    /// <summary>Completed só com todas as fontes incluídas íntegras; qualquer outra situação é coleta parcial.</summary>
    public static KnightSourceState StateOf(IEnumerable<KnightConsolidatedSourceEntry> entries)
    {
        var included = entries.Where(e => e.Included).ToList();
        if (included.Count == 0) return KnightSourceState.PartialCollection;
        return included.All(IsComplete) ? KnightSourceState.Completed : KnightSourceState.PartialCollection;
    }

    /// <summary>Fontes incluídas cuja coleta não foi integral (estado diferente de concluída, ou com limitação registrada).</summary>
    public static IReadOnlyList<KnightConsolidatedSourceEntry> Incomplete(IEnumerable<KnightConsolidatedSourceEntry> entries) =>
        entries.Where(e => e.Included && !IsComplete(e)).ToList();

    /// <summary>Frase única usada na tela, no HTML e no PDF: término da execução × completude da coleta.</summary>
    public static string Describe(IReadOnlyList<KnightConsolidatedSourceEntry> entries)
    {
        var included = entries.Count(e => e.Included);
        var incomplete = Incomplete(entries);
        if (incomplete.Count == 0)
            return $"Coleta completa nas {included} fontes incluídas.";
        return $"Coleta parcial em {incomplete.Count} de {included} fontes incluídas ("
            + string.Join(", ", incomplete.Select(e => e.Label))
            + "). A composição terminou; os controles sem dado dessas fontes ficam não avaliados e as limitações de cada uma estão na composição.";
    }

    private static bool IsComplete(KnightConsolidatedSourceEntry e) =>
        string.Equals(e.SourceState, nameof(KnightSourceState.Completed), StringComparison.Ordinal)
        && e.CollectionLimitations.Count == 0;

    /// <summary>Rótulo em português do estado de coleta de uma fonte (o mesmo da tela).</summary>
    public static string Label(string? state) => state switch
    {
        nameof(KnightSourceState.Completed) => "Coleta concluída",
        nameof(KnightSourceState.PartialCollection) => "Coleta parcial",
        nameof(KnightSourceState.InsufficientPermission) => "Permissão insuficiente",
        nameof(KnightSourceState.AuthenticationFailure) => "Falha de autenticação",
        nameof(KnightSourceState.Throttled) => "Limite de requisições",
        nameof(KnightSourceState.Unavailable) => "Indisponível",
        nameof(KnightSourceState.Error) => "Erro",
        nameof(KnightSourceState.Collecting) => "Coletando",
        nameof(KnightSourceState.Configured) => "Configurado",
        nameof(KnightSourceState.NotConfigured) => "Não configurado",
        null or "" => "—",
        _ => state,
    };
}

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Rótulo do consolidado. Com nove fontes, a lista inteira passa do limite da coluna da
/// fotografia (200): o rótulo diz quantas fontes são e nomeia as que cabem — a lista COMPLETA continua na composição
/// congelada, que é a autoridade sobre o que entrou.
/// </summary>
public static class KnightConsolidatedLabel
{
    public const int MaxLength = 200;

    public static string For(IReadOnlyList<string> includedLabels, int max = MaxLength)
    {
        if (includedLabels.Count == 0) return "Consolidado";
        var full = "Consolidado — " + string.Join(", ", includedLabels);
        if (full.Length <= max) return full;
        for (var shown = includedLabels.Count - 1; shown >= 1; shown--)
        {
            var text = $"Consolidado — {includedLabels.Count} fontes: {string.Join(", ", includedLabels.Take(shown))} e mais {includedLabels.Count - shown}";
            if (text.Length <= max) return text;
        }
        return $"Consolidado — {includedLabels.Count} fontes";
    }
}

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
