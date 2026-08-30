namespace AegisScore.Application.Services;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] Redação em LINGUAGEM CLARA de UMA exposição de configuração — camada de apresentação
/// AUTORAL do AEGIS, provider-neutral e em pt-BR. NÃO é tradução oficial da Microsoft: o texto original da fonte
/// (dinâmico) permanece disponível, sanitizado, como referência técnica secundária.
/// </summary>
/// <param name="DisplayTitle">Título claro e específico da exposição.</param>
/// <param name="PlainSummary">O que a exposição significa, em uma frase.</param>
/// <param name="WhyItMatters">Por que ela importa.</param>
/// <param name="FirstAction">Primeira ação prática.</param>
public sealed record ExposureLanguage(string DisplayTitle, string PlainSummary, string WhyItMatters, string FirstAction);

/// <summary>
/// Porta de leitura da camada de linguagem de exposições. Implementada na Infraestrutura por um provedor SINGLETON
/// que lê o JSON UMA vez (na primeira resolução) e o VALIDA fail-closed no ARQUIVO (schema, chave duplicada, campo
/// obrigatório vazio abortam).
///
/// ⚠️ FAIL-SOFT no LOOKUP, ao contrário do catálogo de controles: o catálogo do FORNECEDOR é DINÂMICO, então a
/// ausência de entrada para um título/externalId NÃO derruba a tela — devolve <c>null</c>, e o consumidor cai em
/// <c>SourceOnly</c> (texto de fonte sanitizado). Nunca inventa tradução silenciosa.
/// </summary>
public interface IExposureLanguageCatalog
{
    /// <summary>Quantidade de entradas carregadas.</summary>
    int Count { get; }

    /// <summary>
    /// Redação clara para a exposição, casando por <paramref name="externalId"/> (precedência, quando houver
    /// entrada por id) e depois pelo <paramref name="sourceTitle"/> NORMALIZADO; <c>null</c> se não houver entrada
    /// (o consumidor cai em SourceOnly). Sem fallback silencioso para tradução inventada.
    /// </summary>
    ExposureLanguage? Match(string? externalId, string? sourceTitle);
}

/// <summary>Catálogo IMUTÁVEL a partir de dicionários (título normalizado → redação; externalId → redação) — testes.</summary>
public sealed class StaticExposureLanguageCatalog : IExposureLanguageCatalog
{
    private readonly IReadOnlyDictionary<string, ExposureLanguage> _byTitle;
    private readonly IReadOnlyDictionary<string, ExposureLanguage> _byExternalId;

    public StaticExposureLanguageCatalog(
        IReadOnlyDictionary<string, ExposureLanguage> byTitle,
        IReadOnlyDictionary<string, ExposureLanguage>? byExternalId = null)
    {
        _byTitle = byTitle ?? throw new ArgumentNullException(nameof(byTitle));
        _byExternalId = byExternalId ?? new Dictionary<string, ExposureLanguage>(StringComparer.Ordinal);
    }

    public static StaticExposureLanguageCatalog Empty { get; } =
        new(new Dictionary<string, ExposureLanguage>(StringComparer.Ordinal));

    public int Count => _byTitle.Count + _byExternalId.Count;

    public ExposureLanguage? Match(string? externalId, string? sourceTitle)
    {
        if (!string.IsNullOrWhiteSpace(externalId) && _byExternalId.TryGetValue(externalId.Trim(), out var byId))
            return byId;
        var key = NormalizeTitle(sourceTitle);
        return key is not null && _byTitle.TryGetValue(key, out var byTitle) ? byTitle : null;
    }

    /// <summary>Chave estável do título: minúsculas, espaços colapsados, aparado. Autoridade ÚNICA de normalização.</summary>
    public static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @"\s+", " ");
        return collapsed.ToLowerInvariant();
    }
}
