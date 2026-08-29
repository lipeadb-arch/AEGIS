namespace AegisScore.Application.Services;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-01] Redação em LINGUAGEM CLARA de UMA subcategoria do catálogo NIST — a camada de
/// apresentação AUTORAL do AEGIS, provider-neutral e em português do Brasil, que torna o controle
/// compreensível SEM depender da IA.
///
/// ⚠️ SEPARAÇÃO DE RESPONSABILIDADES: NÃO é tradução oficial do NIST nem substitui a
/// <see cref="AegisScore.Domain.NistSubcategory.Description"/> oficial (que segue como referência técnica
/// secundária). É conteúdo DERIVADO, carregado de <c>aegis_control_language.pt-BR.json</c>, jamais escrito
/// por cima do <c>nist_csf_2_0_catalog.json</c>. Nenhum campo depende de Microsoft, Google, AWS ou de
/// qualquer fornecedor.
/// </summary>
/// <param name="Title">Título direto e específico do controle (nunca o nome genérico da categoria).</param>
/// <param name="Summary">O que o controle garante, em uma frase.</param>
/// <param name="Impact">Por que a sua ausência importa, em uma frase.</param>
/// <param name="InitialAction">Primeira ação prática e curta para avançar no controle.</param>
public sealed record ControlLanguage(string Title, string Summary, string Impact, string InitialAction);

/// <summary>
/// Porta de leitura da camada de linguagem clara. Implementada na Infraestrutura por um provedor SINGLETON
/// que lê o JSON UMA vez no startup e o VALIDA fail-closed (arquivo ausente, inválido, código duplicado ou
/// campo vazio ABORTAM — nunca há fallback silencioso para o nome genérico da categoria). A ausência de
/// entrada para um código específico é devolvida como <c>null</c> por <see cref="Get"/>, deixando ao
/// consumidor a decisão explícita (o dashboard nunca inventa o rótulo de categoria como título).
/// </summary>
public interface IControlLanguageCatalog
{
    /// <summary>Quantidade de códigos com redação carregada.</summary>
    int Count { get; }

    /// <summary>Conjunto dos códigos cobertos (ex.: "PR.AA-01") — usado por validações/testes.</summary>
    IReadOnlyCollection<string> Codes { get; }

    /// <summary>Redação clara do código, ou <c>null</c> se não houver entrada (sem fallback silencioso).</summary>
    ControlLanguage? Get(string code);
}

/// <summary>
/// Catálogo de linguagem IMUTÁVEL construído a partir de um dicionário — usado nos testes e como semente
/// determinística, onde ler o arquivo de produção seria ruído. Espelha o idioma de
/// <see cref="StaticAuditorPersonaProvider"/>.
/// </summary>
public sealed class StaticControlLanguageCatalog : IControlLanguageCatalog
{
    private readonly IReadOnlyDictionary<string, ControlLanguage> _byCode;

    public StaticControlLanguageCatalog(IReadOnlyDictionary<string, ControlLanguage> byCode)
    {
        _byCode = byCode ?? throw new ArgumentNullException(nameof(byCode));
    }

    /// <summary>Catálogo vazio — o dashboard degrada campo a campo (título nulo → o frontend mostra o código).</summary>
    public static StaticControlLanguageCatalog Empty { get; } =
        new(new Dictionary<string, ControlLanguage>(StringComparer.Ordinal));

    public int Count => _byCode.Count;

    public IReadOnlyCollection<string> Codes => (IReadOnlyCollection<string>)_byCode.Keys;

    public ControlLanguage? Get(string code) =>
        code is not null && _byCode.TryGetValue(code, out var lang) ? lang : null;
}
