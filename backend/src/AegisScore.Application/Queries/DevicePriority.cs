using System;
using System.Globalization;

namespace AegisScore.Application.Queries;

// ============================================================================
//  [AEGIS-RISK-PRIORITIZATION-01] O que sobrevive da prioridade por CVE em dispositivos
// ============================================================================
// A prioridade por CVE saiu da operação com a abordagem antiga. Ficam a criticidade DECLARADA do ativo (inventário, usada
// pelo AEGIS NIST) e o contrato do fator congelado no registro de origem dos planos históricos de dispositivo, lido por
// desserialização — nenhuma prioridade nova é calculada.

/// <summary>Criticidade do ativo e a proveniência da declaração (nula = sem proveniência).</summary>
public sealed record DevicePriorityCriticalityFacts(
    int StoredValue, int? DeclaredValue, DateTimeOffset? DeclaredAt, string? DeclaredByName, string? Note);

public static class DevicePriorityCriticalityStates
{
    public const string Declared = "declared";
    public const string NotConfirmed = "notConfirmed";
    /// <summary>Há declaração, mas o valor cadastrado atual difere do declarado — a proveniência não o cobre.</summary>
    public const string Diverged = "diverged";
}

/// <summary>Linguagem da criticidade declarada e a forma canônica do identificador da CVE dos planos históricos.</summary>
public static class DevicePriorityNarrative
{
    /// <summary>Forma canônica do identificador da CVE (sem espaços, maiúsculas) — a mesma na chave do plano e na leitura.</summary>
    public static string NormalizeCve(string? cve) => (cve ?? "").Trim().ToUpperInvariant();

    public static string CriticalityLabel(DevicePriorityCriticalityFacts c, string state) => state switch
    {
        DevicePriorityCriticalityStates.Declared =>
            $"Criticidade {c.DeclaredValue} declarada em {Utc(c.DeclaredAt!.Value)}" +
            (string.IsNullOrWhiteSpace(c.DeclaredByName) ? "" : $" por {c.DeclaredByName}"),
        DevicePriorityCriticalityStates.Diverged =>
            $"Criticidade não confirmada — o valor cadastrado ({c.StoredValue}) difere do declarado ({c.DeclaredValue})",
        _ => $"Criticidade não confirmada — valor cadastrado {c.StoredValue} sem proveniência" +
             (c.StoredValue == 1 ? " (1 é o valor padrão do modelo para ativos criados pela coleta)" : ""),
    };

    public static DevicePriorityCriticalityDto CriticalityDto(DevicePriorityCriticalityFacts c)
    {
        var state = c.DeclaredAt is null || c.DeclaredValue is null ? DevicePriorityCriticalityStates.NotConfirmed
            : c.DeclaredValue != c.StoredValue ? DevicePriorityCriticalityStates.Diverged
            : DevicePriorityCriticalityStates.Declared;
        return new DevicePriorityCriticalityDto(c.StoredValue, state, CriticalityLabel(c, state), c.DeclaredValue, c.DeclaredAt,
            string.IsNullOrWhiteSpace(c.DeclaredByName) ? null : c.DeclaredByName, c.Note);
    }

    /// <summary>Data em UTC para textos (os campos de data dos contratos seguem ISO 8601).</summary>
    private static string Utc(DateTimeOffset d) =>
        d.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>UM fator com valor/estado, origem, data disponível e natureza (fato da fonte, declarado, inferido, desconhecido).</summary>
public sealed record DevicePriorityFactorDto(
    string Code,
    string Label,
    string Value,
    string Kind,
    string? Source,
    DateTimeOffset? AvailableAt,
    string? AvailableAtLabel,
    string Effect,
    string? Note);

public sealed record DevicePriorityCriticalityDto(
    int StoredValue, string State, string Label, int? DeclaredValue, DateTimeOffset? DeclaredAt, string? DeclaredByName, string? Note);
