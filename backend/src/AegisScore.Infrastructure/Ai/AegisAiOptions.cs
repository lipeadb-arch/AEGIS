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

    /// <summary>Modelo Gemini. Flash pela alta velocidade de inferência exigida na avaliação de postura.</summary>
    public string Model { get; set; } = "gemini-1.5-flash";

    /// <summary>Raiz REST; o modelo e ':generateContent' são anexados em runtime pelo client.</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";
}
