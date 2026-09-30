using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisScore.Application.Knight;

/// <summary>
/// [AEGIS-KNIGHT-CONSOLIDATED-01] Leitura/escrita de <c>PostureSnapshot.CompositionJson</c> — autoridade ÚNICA
/// para não haver duas serializações divergentes entre quem grava (publicação) e quem lê (relatório, hash).
/// A ORDEM das entradas é sempre a de <see cref="KnightConsolidatedCandidates.Sources"/>: a representação é
/// determinística, o que importa para o hash de integridade da fotografia.
/// </summary>
public static class KnightConsolidatedCompositionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IReadOnlyList<KnightConsolidatedSourceEntry> entries) =>
        JsonSerializer.Serialize(entries, Options);

    /// <summary>Ausência ou JSON ilegível devolvem <c>null</c> — distinto de uma composição vazia, que não existe.</summary>
    public static IReadOnlyList<KnightConsolidatedSourceEntry>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<KnightConsolidatedSourceEntry>>(json, Options);
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
