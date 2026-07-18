using System.Text.Json;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Services;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Carrega a <see cref="AuditorPersona"/> de <c>AuditorPersonality.json</c> UMA vez, na construção
/// (singleton) — a personalidade é reference data de processo, não muda por requisição nem por tenant.
///
/// ⚠️ FAIL-SOFT de propósito, ao contrário do <c>FrameworkSeeder</c> (que aborta o boot). A distinção é
/// de GRC, não de gosto: sem o catálogo de regras a plataforma MENTE sobre a postura (falso positivo de
/// conformidade); sem a persona ela apenas escreve de forma mais seca — o veredito, o score e a evidência
/// são idênticos. Degradar a redação é aceitável; derrubar a API por um arquivo de tom não é. O log de
/// Warning deixa a degradação visível.
/// </summary>
public sealed class AuditorPersonaProvider : IAuditorPersonaProvider
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public AuditorPersona Persona { get; }

    public AuditorPersonaProvider(string personalityPath, ILogger<AuditorPersonaProvider> logger)
    {
        Persona = Load(personalityPath, logger);
    }

    private static AuditorPersona Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning(
                "Personalidade do Auditor não encontrada em '{Path}'. O motor segue avaliando com a persona " +
                "técnica neutra — vereditos inalterados, redação sem a camada consultiva.", path);
            return AuditorPersona.Neutral;
        }

        try
        {
            var file = JsonSerializer.Deserialize<PersonalityFileJson>(File.ReadAllText(path), Json);
            var cfg = file?.AuditorConfig;
            if (cfg is null)
            {
                logger.LogWarning(
                    "Personalidade do Auditor em '{Path}' não contém o objeto 'AuditorConfig'. Usando a persona neutra.",
                    path);
                return AuditorPersona.Neutral;
            }

            var persona = new AuditorPersona(
                cfg.Persona ?? "",
                cfg.Tone ?? Array.Empty<string>(),
                cfg.TranslationRules ?? Array.Empty<AuditorTranslationRule>(),
                cfg.ActionDirectives ?? Array.Empty<string>());

            logger.LogInformation(
                "Personalidade do Auditor carregada de '{Path}': {Tones} traços de tom, {Rules} traduções de " +
                "negócio, {Directives} diretivas de ação.",
                path, persona.Tone.Count, persona.TranslationRules.Count, persona.ActionDirectives.Count);

            return persona;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Personalidade do Auditor em '{Path}' está malformada. Usando a persona neutra.", path);
            return AuditorPersona.Neutral;
        }
    }

    /// <summary>Forma crua do arquivo — envelope 'AuditorConfig' como especificado na configuração.</summary>
    private record PersonalityFileJson(AuditorConfigJson? AuditorConfig);

    private record AuditorConfigJson(
        string? Persona,
        IReadOnlyList<string>? Tone,
        IReadOnlyList<AuditorTranslationRule>? TranslationRules,
        IReadOnlyList<string>? ActionDirectives);
}
