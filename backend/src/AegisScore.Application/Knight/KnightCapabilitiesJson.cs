using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisScore.Application.Knight;

/// <summary>
/// [AEGIS-MVP-PRODUCT-03] Leitura do estado por capacidade gravado em <c>KnightAssessmentRun.CapabilitiesJson</c>.
///
/// Extraída para ser AUTORIDADE ÚNICA: o serviço do KNIGHT e a validação de remediação precisam ler
/// exatamente o mesmo JSON com as mesmas opções. Duas desserializações independentes divergiriam no primeiro
/// ajuste de nomenclatura — e a divergência apareceria como "capacidade ausente", que é justamente o que
/// bloqueia uma comprovação de correção legítima.
///
/// JSON ilegível NÃO vira lista vazia otimista em quem decide suficiência: devolve vazio, e quem depende de
/// uma capacidade obrigatória trata a ausência pelo veredito do próprio indicador (que já se declara
/// <c>NotEvaluated</c> quando o fato faltou).
/// </summary>
public static class KnightCapabilitiesJson
{
    /// <summary>As MESMAS opções usadas na serialização da execução — enums por nome, camelCase.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Desserializa o estado por capacidade; ausência ou JSON inválido devolvem lista vazia.</summary>
    public static IReadOnlyList<KnightCapabilityStatus> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KnightCapabilityStatus>();
        try
        {
            return JsonSerializer.Deserialize<List<KnightCapabilityStatus>>(json, Options)
                ?? (IReadOnlyList<KnightCapabilityStatus>)Array.Empty<KnightCapabilityStatus>();
        }
        catch (JsonException)
        {
            return Array.Empty<KnightCapabilityStatus>();
        }
    }
}
