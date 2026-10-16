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
/// que lê o JSON UMA vez — na PRIMEIRA resolução (a fábrica do singleton é lazy) — e o VALIDA fail-closed
/// (arquivo ausente, inválido, código duplicado ou campo vazio ABORTAM; nunca há fallback silencioso para o
/// nome genérico da categoria).
///
/// Dois acessos, de propósito: <see cref="Get"/> devolve <c>null</c> quando não há entrada (para usos que
/// toleram ausência); <see cref="GetRequired"/> é FAIL-CLOSED em runtime — o dashboard o usa para garantir que
/// nenhuma subcategoria ATIVA caia silenciosamente para a apresentação incompleta.
/// </summary>
public interface IControlLanguageCatalog
{
    /// <summary>Quantidade de códigos com redação carregada.</summary>
    int Count { get; }

    /// <summary>Conjunto dos códigos cobertos (ex.: "PR.AA-01") — usado por validações/testes.</summary>
    IReadOnlyCollection<string> Codes { get; }

    /// <summary>Redação clara do código, ou <c>null</c> se não houver entrada (sem fallback silencioso).</summary>
    ControlLanguage? Get(string code);

    /// <summary>
    /// Redação OBRIGATÓRIA do código: lança <see cref="InvalidOperationException"/> SANITIZADA (só o código no
    /// erro — sem caminho nem conteúdo do arquivo) quando a subcategoria ativa não tem entrada. É o guard
    /// fail-closed de runtime — a ausência de redação para um código ATIVO nunca é silenciosa.
    /// </summary>
    ControlLanguage GetRequired(string code) =>
        Get(code) ?? throw new InvalidOperationException(
            $"Sem redação em linguagem clara para a subcategoria ativa '{code}'.");
}
