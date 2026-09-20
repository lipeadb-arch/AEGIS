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
    IReadOnlyList<KnightReferenceCoverageGroup> ByService);

public static class KnightReferenceCoverageSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static KnightReferenceCoverageSummary Summarize(KnightReferenceCoverage c) =>
        new(c.CatalogVersion, c.ReferenceCommit, c.Frameworks, c.Total, c.ByPlatform, c.ByService);

    /// <summary>Texto JSON estável (mesma entrada → mesmos bytes), assinado pelo hash da fotografia.</summary>
    public static string Serialize(KnightReferenceCoverage coverage) =>
        JsonSerializer.Serialize(Summarize(coverage), Options);

    /// <summary>Lê o resumo congelado; texto ausente ou ilegível devolve <c>null</c> (declarado no relatório).</summary>
    public static KnightReferenceCoverageSummary? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<KnightReferenceCoverageSummary>(json, Options); }
        catch (JsonException) { return null; }
    }
}
