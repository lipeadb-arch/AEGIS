using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Posture;

/// <summary>Um item (controle/indicador) reduzido ao necessário para comparar duas fotografias.</summary>
/// <param name="Code">Chave estável do item (código NIST ou IndicatorId).</param>
/// <param name="Title">Rótulo legível (título do indicador ou o próprio código).</param>
/// <param name="StatusLabel">Rótulo do status (para o texto "anterior → atual").</param>
/// <param name="IsEvaluated">O item foi efetivamente avaliado nesta fotografia (entra no score)?</param>
/// <param name="QualityRank">Rank de qualidade entre AVALIADOS (maior = melhor). Ignorado quando não avaliado.</param>
public sealed record PostureComparableItem(string Code, string Title, string StatusLabel, bool IsEvaluated, int QualityRank);

/// <summary>Um lado da comparação — os campos de compatibilidade + os números + os itens comparáveis.</summary>
public sealed record PostureComparisonSide(
    DateTimeOffset CapturedAt,
    string Type,
    string SemanticFamily,
    string FormulaVersion,
    string CatalogVersion,
    string SchemaVersion,
    double? Score,
    double Coverage,
    int CompliantCount,
    int NonCompliantCount,
    int MitigatedCount,
    int NotEvaluatedCount,
    int ErrorCount,
    int NotApplicableCount,
    IReadOnlyList<PostureComparableItem> Items);

/// <summary>
/// [AEGIS-AUD-037] Motor PURO de comparação de fotografias — sem EF/rede/relógio, testável isoladamente.
/// Duas responsabilidades: (1) decidir COMPATIBILIDADE (mesmo tipo, família, fórmula, catálogo e schema) — a
/// comparação só é semanticamente válida entre iguais; (2) montar o DELTA entre a fotografia anterior e a atual,
/// distinguindo melhora/piora de itens que passaram ou deixaram de ser avaliados. NUNCA produz um delta enganoso
/// entre fotografias incompatíveis: o chamador consulta <see cref="CheckCompatibility"/> antes de <see cref="BuildDelta"/>.
/// </summary>
public static class PostureSnapshotComparer
{
    // Códigos ESTÁVEIS de incompatibilidade (a UI mapeia para o texto). Nunca calcular delta se houver algum.
    public const string ReasonDifferentType = "DifferentType";
    public const string ReasonDifferentFamily = "DifferentSemanticFamily";
    public const string ReasonDifferentFormula = "DifferentFormulaVersion";
    public const string ReasonDifferentCatalog = "DifferentCatalogVersion";
    public const string ReasonDifferentSchema = "DifferentSchemaVersion";

    /// <summary>Retorna os motivos de incompatibilidade (vazio ⇒ compatível). Ordinal — versões são identificadores.</summary>
    public static IReadOnlyList<string> CheckCompatibility(PostureComparisonSide a, PostureComparisonSide b)
    {
        var reasons = new List<string>();
        if (!Eq(a.Type, b.Type)) reasons.Add(ReasonDifferentType);
        if (!Eq(a.SemanticFamily, b.SemanticFamily)) reasons.Add(ReasonDifferentFamily);
        if (!Eq(a.FormulaVersion, b.FormulaVersion)) reasons.Add(ReasonDifferentFormula);
        if (!Eq(a.CatalogVersion, b.CatalogVersion)) reasons.Add(ReasonDifferentCatalog);
        if (!Eq(a.SchemaVersion, b.SchemaVersion)) reasons.Add(ReasonDifferentSchema);
        return reasons;
    }

    /// <summary>
    /// Monta o delta assumindo compatibilidade. <paramref name="previous"/> e <paramref name="current"/> devem
    /// vir ordenados por instante (anterior → atual). O score é anulável: transições de/para "não avaliado" são
    /// reportadas como ESTADO, nunca como um número enganoso.
    /// </summary>
    public static PostureComparisonDeltaDto BuildDelta(PostureComparisonSide previous, PostureComparisonSide current)
    {
        var (scoreDelta, scoreState) = ScoreDelta(previous.Score, current.Score);

        var counts = new PostureCountDeltaDto(
            current.CompliantCount - previous.CompliantCount,
            current.NonCompliantCount - previous.NonCompliantCount,
            current.MitigatedCount - previous.MitigatedCount,
            current.NotEvaluatedCount - previous.NotEvaluatedCount,
            current.ErrorCount - previous.ErrorCount,
            current.NotApplicableCount - previous.NotApplicableCount);

        var improved = new List<PostureItemChangeDto>();
        var worsened = new List<PostureItemChangeDto>();
        var nowEvaluated = new List<PostureItemChangeDto>();
        var noLongerEvaluated = new List<PostureItemChangeDto>();

        var prev = previous.Items.ToDictionary(i => i.Code, StringComparer.Ordinal);
        var curr = current.Items.ToDictionary(i => i.Code, StringComparer.Ordinal);
        var codes = prev.Keys.Union(curr.Keys, StringComparer.Ordinal);

        foreach (var code in codes)
        {
            prev.TryGetValue(code, out var p);
            curr.TryGetValue(code, out var c);

            if (p is null && c is null) continue;

            if (p is null)
            {
                // Código novo na fotografia atual: só é "passou a ser avaliado" se de fato avaliado agora.
                if (c!.IsEvaluated)
                    nowEvaluated.Add(new PostureItemChangeDto(code, c.Title, "—", c.StatusLabel));
                continue;
            }

            if (c is null)
            {
                // Código sumiu do universo atual: só conta se era avaliado antes.
                if (p.IsEvaluated)
                    noLongerEvaluated.Add(new PostureItemChangeDto(code, p.Title, p.StatusLabel, "—"));
                continue;
            }

            if (p.IsEvaluated && c.IsEvaluated)
            {
                if (c.QualityRank > p.QualityRank)
                    improved.Add(new PostureItemChangeDto(code, c.Title, p.StatusLabel, c.StatusLabel));
                else if (c.QualityRank < p.QualityRank)
                    worsened.Add(new PostureItemChangeDto(code, c.Title, p.StatusLabel, c.StatusLabel));
                // rank igual → sem mudança material
            }
            else if (!p.IsEvaluated && c.IsEvaluated)
            {
                nowEvaluated.Add(new PostureItemChangeDto(code, c.Title, p.StatusLabel, c.StatusLabel));
            }
            else if (p.IsEvaluated && !c.IsEvaluated)
            {
                noLongerEvaluated.Add(new PostureItemChangeDto(code, c.Title, p.StatusLabel, c.StatusLabel));
            }
            // ambos não avaliados → sem mudança
        }

        return new PostureComparisonDeltaDto(
            scoreDelta,
            scoreState.ToString(),
            Math.Round(current.Coverage - previous.Coverage, 1, MidpointRounding.AwayFromZero),
            counts,
            Sort(improved), Sort(worsened), Sort(nowEvaluated), Sort(noLongerEvaluated));
    }

    private static (double?, ScoreDeltaState) ScoreDelta(double? previous, double? current) =>
        (previous, current) switch
        {
            ({ } p, { } c) => (Math.Round(c - p, 1, MidpointRounding.AwayFromZero), ScoreDeltaState.Numeric),
            (null, { }) => (null, ScoreDeltaState.BecameEvaluated),
            ({ }, null) => (null, ScoreDeltaState.BecameUnevaluated),
            (null, null) => (null, ScoreDeltaState.BothUnevaluated),
        };

    private static IReadOnlyList<PostureItemChangeDto> Sort(List<PostureItemChangeDto> items) =>
        items.OrderBy(i => i.Code, StringComparer.Ordinal).ToList();

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
}
