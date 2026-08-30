using System.Text.RegularExpressions;

namespace AegisScore.Application.Services;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] Vocabulário DETERMINÍSTICO de exposições de configuração (backend, puro): tradução de
/// CATEGORIA (para a moldura SourceOnly) e de AMEAÇAS conhecidas (para a tela principal e o contexto da IA — mesma
/// autoridade, sem HTML/token cru). Termo desconhecido passa direto (nunca inventa tradução).
/// </summary>
public static class ExposureVocabulary
{
    private static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal)
    {
        ["device"] = "Dispositivos",
        ["apps"] = "Aplicativos",
        ["identity"] = "Identidades",
        ["data"] = "Dados",
    };

    // Ameaças conhecidas do Secure Score. Chave NORMALIZADA (minúsculas, sem espaços) → variantes com espaço/caixa casam.
    private static readonly Dictionary<string, string> Threats = new(StringComparer.Ordinal)
    {
        ["accountbreach"] = "Comprometimento de contas",
        ["datadeletion"] = "Exclusão de dados",
        ["dataexfiltration"] = "Exfiltração de dados",
        ["dataspillage"] = "Exposição acidental de dados",
        ["elevationofprivilege"] = "Elevação de privilégio",
        ["maliciousinsider"] = "Ameaça interna",
        ["passwordcracking"] = "Quebra de senhas",
        ["phishingorwhaling"] = "Phishing direcionado",
        ["spoofing"] = "Falsificação de identidade",
    };

    private static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", "").ToLowerInvariant();

    /// <summary>Categoria da fonte → pt-BR; desconhecida/nula passa direto.</summary>
    public static string? CategoryPt(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return category;
        return Categories.TryGetValue(Norm(category), out var pt) ? pt : category.Trim();
    }

    /// <summary>Ameaça conhecida → rótulo claro pt-BR; desconhecida passa direto (sanitizada).</summary>
    public static string ThreatPt(string threat)
    {
        if (string.IsNullOrWhiteSpace(threat)) return threat;
        return Threats.TryGetValue(Norm(threat), out var pt)
            ? pt
            : SourceTextSanitizer.ToPlainText(threat, 80) ?? threat.Trim();
    }

    /// <summary>Traduz a lista de ameaças (preservando ordem; sem duplicar), para a tela principal e a IA.</summary>
    public static IReadOnlyList<string> ThreatsPt(IEnumerable<string>? threats) =>
        (threats ?? Array.Empty<string>()).Select(ThreatPt).ToList();
}
