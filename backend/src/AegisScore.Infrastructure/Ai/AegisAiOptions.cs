namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Configuração do motor de IA de transporte do Aegis Score (<see cref="GeminiLlmClient"/>). Mapeada da
/// seção "AegisAi" do appsettings via padrão Options. A <see cref="ApiKey"/> NUNCA é versionada: vem de
/// 'dotnet user-secrets' (dev) ou variável de ambiente / secret store (prod). Espelha a convenção de
/// <c>AiOptions</c> (motor de alto nível), mantida em seção própria porque o provedor/modelo divergem.
/// </summary>
public sealed class AegisAiOptions
{
    public const string SectionName = "AegisAi";

    /// <summary>Chave da Generative Language API (Google AI Studio). Preencher via user-secrets, não no JSON.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Modelo Gemini. Flash pela alta velocidade de inferência exigida na avaliação de postura.
    ///
    /// ⚠️ ALIAS <c>-latest</c> DE PROPÓSITO, não um pin de versão: sondagem em 2026-07-18 mostrou
    /// <c>gemini-2.0-flash</c> e <c>gemini-2.0-flash-lite</c> em 429 RESOURCE_EXHAUSTED (cota free
    /// esgotada, persistente desde 2026-07-13) e <c>gemini-2.5-flash</c> em 404 ("no longer available to
    /// new users"). Um modelo pinado envelhece para 404/429 e derruba a avaliação com 503; o alias
    /// acompanha o Flash vigente. Sobrescrevível por <c>AegisAi:Model</c> quando um pin for necessário.
    /// </summary>
    public string Model { get; set; } = "gemini-flash-latest";

    /// <summary>Raiz REST; o modelo e ':generateContent' são anexados em runtime pelo client.</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
}
