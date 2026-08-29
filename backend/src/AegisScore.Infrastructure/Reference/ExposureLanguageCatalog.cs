using System.Text.Json;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Services;

namespace AegisScore.Infrastructure.Reference;

/// <summary>
/// [AEGIS-MVP-LANGUAGE-02] Carrega a camada de LINGUAGEM CLARA de EXPOSIÇÕES de <c>aegis_exposure_language.pt-BR.json</c>
/// UMA vez, na construção (singleton lazy). O ARQUIVO é validado FAIL-CLOSED (JSON inválido, chave duplicada ou
/// campo obrigatório vazio ABORTAM) — mas o LOOKUP é FAIL-SOFT: como o catálogo do fornecedor é dinâmico, a ausência
/// de entrada para um título devolve <c>null</c> e o consumidor cai em <c>SourceOnly</c> (texto de fonte sanitizado),
/// nunca uma tradução inventada.
/// </summary>
public sealed class ExposureLanguageCatalog : IExposureLanguageCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IReadOnlyDictionary<string, ExposureLanguage> _byTitle;
    private readonly IReadOnlyDictionary<string, ExposureLanguage> _byExternalId;

    public ExposureLanguageCatalog(string path, ILogger<ExposureLanguageCatalog> logger)
    {
        (_byTitle, _byExternalId) = Load(path, logger);
    }

    public int Count => _byTitle.Count;

    public ExposureLanguage? Match(string? externalId, string? sourceTitle)
    {
        if (!string.IsNullOrWhiteSpace(externalId) && _byExternalId.TryGetValue(externalId.Trim(), out var byId))
            return byId;
        var key = StaticExposureLanguageCatalog.NormalizeTitle(sourceTitle);
        return key is not null && _byTitle.TryGetValue(key, out var byTitle) ? byTitle : null;
    }

    private static (IReadOnlyDictionary<string, ExposureLanguage> ByTitle, IReadOnlyDictionary<string, ExposureLanguage> ById) Load(
        string path, ILogger logger)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Catálogo de linguagem de exposições não encontrado em '{path}'. Verifique se o Data/ do projeto da " +
                "API foi copiado para o output/imagem.", path);

        ExposureFileJson? file;
        try
        {
            file = JsonSerializer.Deserialize<ExposureFileJson>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Catálogo de linguagem de exposições em '{path}' está malformado (JSON inválido).", ex);
        }

        var entries = file?.Exposures;
        if (entries is null || entries.Count == 0)
            throw new InvalidOperationException(
                $"Catálogo de linguagem de exposições em '{path}' não contém nenhuma entrada (lista 'exposures' vazia/ausente).");

        var byTitle = new Dictionary<string, ExposureLanguage>(StringComparer.Ordinal);
        var byExternalId = new Dictionary<string, ExposureLanguage>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.DisplayTitle) || string.IsNullOrWhiteSpace(e.PlainSummary) ||
                string.IsNullOrWhiteSpace(e.WhyItMatters) || string.IsNullOrWhiteSpace(e.FirstAction))
                throw new InvalidOperationException(
                    $"Catálogo de linguagem de exposições em '{path}': entrada com campo obrigatório vazio " +
                    "(displayTitle/plainSummary/whyItMatters/firstAction).");
            if (string.IsNullOrWhiteSpace(e.MatchTitle) && string.IsNullOrWhiteSpace(e.ExternalId))
                throw new InvalidOperationException(
                    $"Catálogo de linguagem de exposições em '{path}': entrada sem 'matchTitle' nem 'externalId' (sem chave de correspondência).");

            var lang = new ExposureLanguage(e.DisplayTitle, e.PlainSummary, e.WhyItMatters, e.FirstAction);

            if (!string.IsNullOrWhiteSpace(e.MatchTitle))
            {
                var key = StaticExposureLanguageCatalog.NormalizeTitle(e.MatchTitle)!;
                if (!byTitle.TryAdd(key, lang))
                    throw new InvalidOperationException(
                        $"Catálogo de linguagem de exposições em '{path}' com matchTitle DUPLICADO: '{e.MatchTitle}'.");
            }
            if (!string.IsNullOrWhiteSpace(e.ExternalId) && !byExternalId.TryAdd(e.ExternalId.Trim(), lang))
                throw new InvalidOperationException(
                    $"Catálogo de linguagem de exposições em '{path}' com externalId DUPLICADO: '{e.ExternalId}'.");
        }

        logger.LogInformation(
            "Catálogo de linguagem de exposições carregado de '{Path}': {ByTitle} por título, {ById} por externalId.",
            path, byTitle.Count, byExternalId.Count);

        return (byTitle, byExternalId);
    }

    private sealed record ExposureFileJson(IReadOnlyList<ExposureEntryJson>? Exposures);

    private sealed record ExposureEntryJson(
        string? MatchTitle, string? ExternalId,
        string DisplayTitle, string PlainSummary, string WhyItMatters, string FirstAction);
}
