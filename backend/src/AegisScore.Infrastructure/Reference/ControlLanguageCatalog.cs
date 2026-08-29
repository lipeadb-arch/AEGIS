using System.Text.Json;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Services;

namespace AegisScore.Infrastructure.Reference;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-01] Carrega a camada de LINGUAGEM CLARA (autoral, provider-neutral, pt-BR) de
/// <c>aegis_control_language.pt-BR.json</c> UMA vez, na construção (singleton) — é reference data de
/// apresentação, não muda por requisição nem por tenant.
///
/// ⚠️ FAIL-CLOSED de propósito, ao contrário do <see cref="AegisScore.Infrastructure.Ai.AuditorPersonaProvider"/>
/// (que degrada o TOM em silêncio). Aqui a distinção é o produto: o objetivo desta camada é que TODO controle
/// apareça em linguagem clara sem depender da IA; degradar para o nome genérico da categoria reintroduziria
/// exatamente o defeito que ela existe para corrigir. Por isso a ausência do arquivo, JSON inválido, código
/// duplicado ou campo vazio ABORTAM o carregamento com mensagem clara — a quebra é DETECTADA, nunca mascarada.
/// A completude perante o catálogo ativo (uma entrada por subcategoria) é conferida por teste dedicado.
/// </summary>
public sealed class ControlLanguageCatalog : IControlLanguageCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IReadOnlyDictionary<string, ControlLanguage> _byCode;

    public ControlLanguageCatalog(string path, ILogger<ControlLanguageCatalog> logger)
    {
        _byCode = Load(path, logger);
    }

    public int Count => _byCode.Count;

    public IReadOnlyCollection<string> Codes => (IReadOnlyCollection<string>)_byCode.Keys;

    public ControlLanguage? Get(string code) =>
        code is not null && _byCode.TryGetValue(code, out var lang) ? lang : null;

    private static IReadOnlyDictionary<string, ControlLanguage> Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Catálogo de linguagem clara não encontrado em '{path}'. Ele é obrigatório: sem ele os " +
                "controles apareceriam com o nome genérico da categoria — o defeito que esta camada corrige. " +
                "Verifique se o Data/ do projeto da API foi copiado para o output/imagem.", path);

        ControlLanguageFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize<ControlLanguageFileJson>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Catálogo de linguagem clara em '{path}' está malformado (JSON inválido).", ex);
        }

        var controls = file?.Controls;
        if (controls is null || controls.Count == 0)
            throw new InvalidOperationException(
                $"Catálogo de linguagem clara em '{path}' não contém nenhum controle (lista 'controls' vazia/ausente).");

        var byCode = new Dictionary<string, ControlLanguage>(controls.Count, StringComparer.Ordinal);
        foreach (var c in controls)
        {
            if (string.IsNullOrWhiteSpace(c.Code))
                throw new InvalidOperationException($"Catálogo de linguagem clara em '{path}' contém entrada com 'code' vazio.");
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Summary) ||
                string.IsNullOrWhiteSpace(c.Impact) || string.IsNullOrWhiteSpace(c.InitialAction))
                throw new InvalidOperationException(
                    $"Catálogo de linguagem clara em '{path}': o código '{c.Code}' tem campo obrigatório vazio " +
                    "(title/summary/impact/initialAction). Sem fallback silencioso — o artefato precisa ser completo.");
            if (!byCode.TryAdd(c.Code, new ControlLanguage(c.Title, c.Summary, c.Impact, c.InitialAction)))
                throw new InvalidOperationException(
                    $"Catálogo de linguagem clara em '{path}' com código DUPLICADO: '{c.Code}'.");
        }

        logger.LogInformation(
            "Catálogo de linguagem clara carregado de '{Path}': {Count} controle(s) em linguagem clara.",
            path, byCode.Count);

        return byCode;
    }

    /// <summary>Forma crua do arquivo: proveniência (ignorada aqui, é metadado) + a lista de controles.</summary>
    private sealed record ControlLanguageFileJson(IReadOnlyList<ControlLanguageJson>? Controls);

    private sealed record ControlLanguageJson(string Code, string Title, string Summary, string Impact, string InitialAction);
}
