using System.Collections.Generic;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Modo operacional da IA do AEGIS. É a autoridade de configuração que decide se um motor externo pode
/// ser chamado — jamais um nome de provedor espalhado pela aplicação.
/// <list type="bullet">
/// <item><see cref="Disabled"/>: IA externa desligada; todo consumidor usa o motor determinístico/stub.</item>
/// <item><see cref="Simulated"/>: motor simulado (stub canned) — a demo funciona sem chave nem rede.</item>
/// <item><see cref="GeminiFreeDemo"/>: IA demonstrativa via provedor gratuito, restrita aos tenants da
/// allowlist (dados sintéticos). Fora da allowlist, o comportamento é idêntico a <see cref="Simulated"/>.</item>
/// </list>
/// </summary>
public enum AiMode
{
    Disabled = 0,
    Simulated = 1,
    GeminiFreeDemo = 2,
}

/// <summary>
/// Configuração ÚNICA e genérica da IA do AEGIS (seção <c>Ai</c>). Consolida o antigo par
/// <c>Ai</c>/<c>AegisAi</c> num só contrato, portável entre provedores: o domínio, os controllers, os
/// workers e o frontend dependem só das interfaces neutras do AEGIS — nunca destes nomes. Trocar de
/// provedor exige apenas um novo adaptador de <see cref="Application.Abstractions.ILLMClient"/>, o registro
/// na DI, esta configuração e os testes de contrato.
///
/// A <see cref="ApiKey"/> NUNCA é versionada: vem de 'dotnet user-secrets' (dev) ou variável de ambiente /
/// secret store (prod). Nenhum log jamais registra a chave ou fragmento dela.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Rótulo genérico do provedor (ex.: "Gemini"). Informativo/telemetria — a resolução do
    /// adaptador é feita pela DI, não por este texto.</summary>
    public string Provider { get; set; } = "Gemini";

    /// <summary>Chave da API generativa. Preencher via user-secrets/variável de ambiente, nunca no JSON.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Modelo pinado (estável). Default <c>gemini-3.5-flash</c>: GA, com Free Tier, baixa latência e alta
    /// vazão para saída estruturada — sem depender do alias volátil <c>-latest</c>. Sobrescrevível por
    /// <c>Ai:Model</c>.
    /// </summary>
    public string Model { get; set; } = "gemini-3.5-flash";

    /// <summary>Raiz REST do provedor. O modelo e ':generateContent' são anexados em runtime pelo adaptador.</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";

    /// <summary>Modo operacional (ver <see cref="AiMode"/>). Default <see cref="AiMode.Simulated"/>:
    /// o serviço SEMPRE inicia sem chave, em modo simulado.</summary>
    public AiMode Mode { get; set; } = AiMode.Simulated;

    /// <summary>Teto de tokens de SAÍDA por chamada (maxOutputTokens do generateContent). Protege a cota.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>Temperatura de amostragem. Baixa por padrão: auditoria pede consistência, não criatividade.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>Caminho do <c>AuditorPersonality.json</c> (persona do Copiloto). Migrado de <c>AegisAi</c>.</summary>
    public string? PersonalityPath { get; set; }

    /// <summary>Controles do Free Tier (limites e allowlist de tenants sintéticos).</summary>
    public AiFreeTierOptions FreeTier { get; set; } = new();
}

/// <summary>
/// Controles do Free Tier demonstrativo. A <see cref="AllowedTenantSlugs"/> é a FRONTEIRA DE DADOS:
/// somente tenants nela (laboratório sandbox, com identidades/documentos/telemetria SINTÉTICOS) podem ter
/// seus dados enviados ao provedor gratuito. Nenhum slug/domínio/id é hardcoded — tudo vem da configuração.
/// </summary>
public sealed class AiFreeTierOptions
{
    /// <summary>Slugs de tenant autorizados a usar a IA externa gratuita (dados sintéticos). Vazio = nenhum.</summary>
    public List<string> AllowedTenantSlugs { get; set; } = new();

    /// <summary>Teto de caracteres do documento enviados por análise (triagem). Protege a cota/latência.</summary>
    public int MaxDocumentChars { get; set; } = 24_000;

    /// <summary>Teto de chamadas ao motor por análise documental (triagem + julgamentos dirigidos).</summary>
    public int MaxCallsPerAnalysis { get; set; } = 8;

    /// <summary>Teto de perguntas ao Auditor por usuário/minuto (rate limit do endpoint de chat).</summary>
    public int MaxQuestionsPerMinute { get; set; } = 10;
}
