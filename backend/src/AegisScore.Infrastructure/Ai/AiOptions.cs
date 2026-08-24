using System.Collections.Generic;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Modo operacional da IA do AEGIS. É a autoridade de configuração que decide se um motor externo pode
/// ser chamado — jamais um nome de provedor espalhado pela aplicação.
/// </summary>
public enum AiMode
{
    Disabled = 0,
    Simulated = 1,

    /// <summary>
    /// Uso demonstrativo via provedor externo, restrito à allowlist e destinado apenas a dados sintéticos.
    /// O valor 2 é preservado por compatibilidade com a configuração anterior.
    /// </summary>
    ExternalDemo = 2,

    /// <summary>
    /// Uso corporativo autorizado via provedor externo. Continua exigindo chave + allowlist de tenant;
    /// este modo não remove a fronteira tenant-scoped nem altera as regras de minimização/classificação.
    /// </summary>
    ExternalEnterprise = 3,
}

/// <summary>
/// Configuração única e provider-neutral da IA do AEGIS (seção <c>Ai</c>).
/// A <see cref="ApiKey"/> nunca é versionada: vem de user-secrets, variável de ambiente ou secret store.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public string Provider { get; set; } = "Anthropic";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-opus-4-8";
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1/messages";
    public AiMode Mode { get; set; } = AiMode.Simulated;
    public int MaxOutputTokens { get; set; } = 4096;
    public string? PersonalityPath { get; set; }

    /// <summary>
    /// Configuração histórica do gate/limites. O nome <c>FreeTier</c> é preservado para compatibilidade de
    /// configuração; a allowlist também é autoridade no modo <see cref="AiMode.ExternalEnterprise"/>.
    /// </summary>
    public AiFreeTierOptions FreeTier { get; set; } = new();
}

/// <summary>
/// Limites e allowlist tenant-scoped do provedor externo. Apesar do nome legado, estes controles também
/// se aplicam ao modo corporativo: vazio significa que nenhum tenant pode enviar dados ao provedor externo.
/// </summary>
public sealed class AiFreeTierOptions
{
    public List<string> AllowedTenantSlugs { get; set; } = new();
    public int MaxDocumentChars { get; set; } = 24_000;
    public int MaxCallsPerAnalysis { get; set; } = 8;
    public int MaxQuestionsPerMinute { get; set; } = 10;
}
