using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisScore.Application.Knight.Reference;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Resumo CONGELADO da cobertura de implementação numa fotografia: benchmarks, total,
/// plataformas e serviços. Não carrega a lista controle a controle (disponível na tela, pelo catálogo corrente): o
/// relatório publicado precisa dos números, e os números não podem mudar quando o catálogo evoluir.
/// </summary>
public sealed record KnightReferenceCoverageSummary(
    string CatalogVersion,
    string ReferenceCommit,
    IReadOnlyList<KnightReferenceFramework> Frameworks,
    KnightReferenceCoverageGroup Total,
    IReadOnlyList<KnightReferenceCoverageGroup> ByPlatform,
    IReadOnlyList<KnightReferenceCoverageGroup> ByService,
    // [AEGIS-KNIGHT-CLOSURE-01] Resultados manuais VIGENTES na publicação, congelados à parte da cobertura automatizada.
    IReadOnlyList<KnightManualResultEntry>? ManualResults = null);

/// <summary>
/// [AEGIS-KNIGHT-CLOSURE-01] Um resultado manual congelado na fotografia: o controle de referência, o resultado declarado,
/// a justificativa, o responsável, a evidência e quem registrou. Nunca é somado à avaliação automatizada.
/// </summary>
public sealed record KnightManualResultEntry(
    string ReferenceKey,
    string ReferenceLabel,
    string Title,
    string Disposition,
    string DispositionLabel,
    string Result,
    string ResultLabel,
    string Justification,
    string ResponsibleName,
    string? EvidenceReference,
    string? EvidenceDocumentTitle,
    string? EvidenceDocumentSha256,
    string? ValidUntil,
    bool Expired,
    string RecordedByName,
    string RecordedAt);

public static class KnightReferenceCoverageSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static KnightReferenceCoverageSummary Summarize(KnightReferenceCoverage c,
        IReadOnlyList<KnightManualResultEntry>? manual = null) =>
        new(c.CatalogVersion, c.ReferenceCommit, c.Frameworks, c.Total, c.ByPlatform, c.ByService,
            manual is { Count: > 0 } ? manual : null);

    /// <summary>Texto JSON estável (mesma entrada → mesmos bytes), assinado pelo hash da fotografia.</summary>
    public static string Serialize(KnightReferenceCoverage coverage, IReadOnlyList<KnightManualResultEntry>? manual = null) =>
        JsonSerializer.Serialize(Summarize(coverage, manual), Options);

    /// <summary>[AEGIS-KNIGHT-CLOSURE-01] Os resultados vigentes como entradas congeladas, na ordem estável das chaves.</summary>
    public static IReadOnlyList<KnightManualResultEntry> Entries(KnightReferenceCoverage coverage, IEnumerable<KnightManualResultView> current)
    {
        var byKey = coverage.Controls.ToDictionary(c => c.Control.Key, StringComparer.Ordinal);
        return current
            .Where(v => v.Result != AegisScore.Domain.KnightManualResult.Withdrawn)
            .OrderBy(v => v.ReferenceKey, StringComparer.Ordinal)
            .Select(v =>
            {
                byKey.TryGetValue(v.ReferenceKey, out var st);
                return new KnightManualResultEntry(
                    v.ReferenceKey, st is null ? v.ReferenceKey : KnightReferenceCatalog.Label(st.Control), st?.Control.Title ?? v.ReferenceKey,
                    st?.Disposition.ToString() ?? v.ReferenceDisposition,
                    st is null ? v.ReferenceDisposition : KnightReferenceCatalog.DispositionLabel(st.Disposition),
                    v.Result.ToString(), v.ResultLabel, v.Justification, v.ResponsibleName, v.EvidenceReference, v.EvidenceDocumentTitle,
                    v.EvidenceDocumentSha256, v.ValidUntil?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), v.Expired,
                    v.RecordedByName, v.RecordedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            })
            .ToList();
    }

    /// <summary>Lê o resumo congelado; texto ausente ou ilegível devolve <c>null</c> (declarado no relatório).</summary>
    public static KnightReferenceCoverageSummary? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<KnightReferenceCoverageSummary>(json, Options); }
        catch (JsonException) { return null; }
    }
}
