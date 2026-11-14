using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AegisScore.Application.Knight.Reference;

namespace AegisScore.Application.Knight.Cis;

// ============================================================================
//  [AEGIS-KNIGHT-CIS-01] Rastreio das recomendações CIS (benchmark, edição, seção e perfil)
// ============================================================================
// O catálogo de referência identifica cada recomendação pela seção do benchmark. Este rastreio acrescenta, para a MESMA edição,
// o que a fonte de rastreio (os rulesets do Monkey365, no commit fixado do catálogo) registra: a regra que verifica a seção e o
// perfil (licença E3/E5 e nível). O texto oficial do CIS não é reproduzido: a apresentação é autoral, em português, e diz o recorte
// técnico avaliado. Perfil não registrado, divergente ou ambíguo na fonte é LIMITAÇÃO declarada — nunca inferido de outra edição.

/// <summary>Como a fonte de rastreio registra o perfil da recomendação nesta edição.</summary>
public enum KnightCisProfileState
{
    /// <summary>Perfil registrado para esta edição.</summary>
    Recorded = 0,

    /// <summary>A fonte de rastreio mapeia a seção nesta edição, mas não registra o perfil.</summary>
    NotRecorded = 1,

    /// <summary>O ruleset e a regra da fonte de rastreio registram perfis diferentes para a mesma seção.</summary>
    Divergent = 2,

    /// <summary>A regra é um modelo compartilhado entre seções e registra os dois níveis: não identifica o perfil desta seção.</summary>
    Ambiguous = 3,

    /// <summary>A seção não consta do ruleset da edição: não é uma recomendação rastreada (verificação própria do KNIGHT).</summary>
    NotTraced = 4,
}

/// <summary>Rastreio de UMA entrada do catálogo de referência.</summary>
public sealed record KnightCisTraceEntry(
    string Key, string? Rule, string? Ruleset, KnightCisProfileState ProfileState, IReadOnlyList<string> Profiles,
    IReadOnlyList<string>? RulesetProfiles, IReadOnlyList<string>? FindingProfiles);

/// <summary>Um benchmark CIS registrado no AEGIS, na edição fixada.</summary>
public sealed record KnightCisBenchmarkDescriptor(string Key, string Framework, string Version, string Label, string Url, string Ruleset);

public static class KnightCisTrace
{
    private const string ResourceName = "AegisScore.Knight.CisTrace.json";
    private static readonly Lazy<Loaded> Data = new(Load);

    private sealed record Loaded(string Repository, string Commit, IReadOnlyDictionary<string, string> Rulesets,
        IReadOnlyDictionary<string, KnightCisTraceEntry> ByKey);

    /// <summary>Repositório e commit da fonte de rastreio (o mesmo commit do catálogo de referência).</summary>
    public static string Repository => Data.Value.Repository;
    public static string Commit => Data.Value.Commit;

    /// <summary>
    /// Os quatro benchmarks nas edições registradas no AEGIS. A chave é o prefixo das chaves do catálogo de referência
    /// ("CIS-M365-7.0.0"), e a página é a página pública do benchmark no CIS (só o endereço, sem conteúdo licenciado).
    /// </summary>
    public static IReadOnlyList<KnightCisBenchmarkDescriptor> Benchmarks { get; } = new[]
    {
        new KnightCisBenchmarkDescriptor("CIS-M365-7.0.0", "CIS Microsoft 365 Foundations", "7.0.0", "Microsoft 365 Foundations 7.0.0",
            "https://www.cisecurity.org/benchmark/microsoft_365", "cis_m365_7.0.0.json"),
        new KnightCisBenchmarkDescriptor("CIS-AZ-6.0.0", "CIS Microsoft Azure Foundations", "6.0.0", "Azure Foundations 6.0.0",
            "https://www.cisecurity.org/benchmark/azure", "cis_azure_6.0.json"),
        new KnightCisBenchmarkDescriptor("CIS-AZC-2.0.0", "CIS Microsoft Azure Compute Services", "2.0.0", "Azure Compute Services 2.0.0",
            "https://www.cisecurity.org/benchmark/azure", "cis_azure_compute_services_2.0.json"),
        new KnightCisBenchmarkDescriptor("CIS-AZD-2.0.0", "CIS Microsoft Azure Database Services", "2.0.0", "Azure Database Services 2.0.0",
            "https://www.cisecurity.org/benchmark/azure", "cis_azure_database_services_2.0.json"),
    };

    /// <summary>Benchmark de uma chave do catálogo de referência ("CIS-AZ-6.0.0:7.3#dns" → CIS-AZ-6.0.0).</summary>
    public static KnightCisBenchmarkDescriptor? BenchmarkOf(string referenceKey)
    {
        var colon = referenceKey.IndexOf(':');
        if (colon <= 0) return null;
        var prefix = referenceKey[..colon];
        return Benchmarks.FirstOrDefault(b => string.Equals(b.Key, prefix, StringComparison.Ordinal));
    }

    public static KnightCisTraceEntry? For(string referenceKey) =>
        Data.Value.ByKey.TryGetValue(referenceKey, out var e) ? e : null;

    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)Data.Value.ByKey.Keys;

    /// <summary>
    /// Rótulo em português de um perfil registrado, preservando o significado: a LICENÇA (E3/E5, só no Microsoft 365) e o
    /// NÍVEL (1 ou 2) são coisas diferentes e aparecem as duas. "E3 Level 1" → "E3 · Nível 1"; "Level 2" → "Nível 2".
    /// </summary>
    public static string ProfileLabel(string profile)
    {
        var p = profile.Trim();
        var lic = p.StartsWith("E3 ", StringComparison.Ordinal) ? "E3" : p.StartsWith("E5 ", StringComparison.Ordinal) ? "E5" : null;
        var rest = lic is null ? p : p[3..];
        var level = rest switch { "Level 1" => "Nível 1", "Level 2" => "Nível 2", _ => rest };
        return lic is null ? level : $"{lic} · {level}";
    }

    public static string ProfileStateLabel(KnightCisProfileState s) => s switch
    {
        KnightCisProfileState.Recorded => "Perfil registrado pela fonte de rastreio para esta edição",
        KnightCisProfileState.NotRecorded => "Perfil não registrado pela fonte de rastreio para esta edição",
        KnightCisProfileState.Divergent => "Perfil divergente na fonte de rastreio (o ruleset e a regra registram perfis diferentes)",
        KnightCisProfileState.Ambiguous => "Perfil ambíguo na fonte de rastreio (regra compartilhada registra os dois níveis)",
        KnightCisProfileState.NotTraced => "Seção fora do ruleset da edição",
        _ => s.ToString(),
    };

    // ---- Carga ---------------------------------------------------------------------------------------

    private sealed class FileDto
    {
        [JsonPropertyName("traceRepository")] public string? Repository { get; set; }
        [JsonPropertyName("traceCommit")] public string? Commit { get; set; }
        [JsonPropertyName("rulesets")] public Dictionary<string, string>? Rulesets { get; set; }
        [JsonPropertyName("entries")] public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("rule")] public string? Rule { get; set; }
        [JsonPropertyName("ruleset")] public string? Ruleset { get; set; }
        [JsonPropertyName("profileState")] public string? ProfileState { get; set; }
        [JsonPropertyName("profiles")] public List<string>? Profiles { get; set; }
        [JsonPropertyName("rulesetProfiles")] public List<string>? RulesetProfiles { get; set; }
        [JsonPropertyName("findingProfiles")] public List<string>? FindingProfiles { get; set; }
    }

    private static Loaded Load()
    {
        using var stream = typeof(KnightCisTrace).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Recurso {ResourceName} ausente do assembly.");
        var file = JsonSerializer.Deserialize<FileDto>(stream) ?? throw new InvalidOperationException("Rastreio CIS ilegível.");
        var byKey = new Dictionary<string, KnightCisTraceEntry>(StringComparer.Ordinal);
        foreach (var e in file.Entries ?? new List<EntryDto>())
        {
            if (string.IsNullOrWhiteSpace(e.Key) || byKey.ContainsKey(e.Key))
                throw new InvalidOperationException($"Chave de rastreio CIS vazia ou duplicada: '{e.Key}'.");
            if (KnightReferenceCatalog.Find(e.Key) is null)
                throw new InvalidOperationException($"O rastreio CIS cita {e.Key}, que não existe no catálogo de referência.");
            if (!Enum.TryParse<KnightCisProfileState>(e.ProfileState, out var state))
                throw new InvalidOperationException($"Estado de perfil desconhecido em {e.Key}: '{e.ProfileState}'.");
            byKey[e.Key] = new KnightCisTraceEntry(e.Key, e.Rule, e.Ruleset, state,
                (IReadOnlyList<string>?)e.Profiles ?? Array.Empty<string>(), e.RulesetProfiles, e.FindingProfiles);
        }
        return new Loaded(file.Repository ?? "", file.Commit ?? "",
            file.Rulesets ?? new Dictionary<string, string>(), byKey);
    }
}
