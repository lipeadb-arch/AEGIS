using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests;

/// <summary>
/// Testes do switch fail-open de <see cref="DependencyInjection.AddAegisScoreInfrastructure"/> para o
/// ILLMClient: exercita o composition root REAL (ServiceCollection + IConfiguration in-memory), sem
/// rede nem banco. A resolução decide o motor apenas pela presença de AegisAi:ApiKey.
/// </summary>
public sealed class AegisAiDependencyInjectionTests
{
    [Fact]
    public void SemApiKey_ResolveStubLlmClient()
    {
        using var provider = BuildProvider(apiKey: null);

        var client = provider.GetRequiredService<ILLMClient>();

        client.Should().BeOfType<StubLlmClient>(
            "sem chave o container deve cair no stub determinístico — a demo nunca quebra por rede");
    }

    [Fact]
    public void ComApiKey_ResolveGeminiLlmClient()
    {
        using var provider = BuildProvider(apiKey: "chave-de-producao");

        var client = provider.GetRequiredService<ILLMClient>();

        client.Should().BeOfType<GeminiLlmClient>(
            "com a chave presente o container deve engatar o motor real Gemini via HttpClient tipado");
    }

    private static ServiceProvider BuildProvider(string? apiKey)
    {
        var settings = new Dictionary<string, string?>
        {
            // Connection string dummy: AddDbContext apenas REGISTRA (não abre conexão) — evita null no UseNpgsql.
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=aegis_test;Username=test;Password=test",
        };
        // Ausência da chave é modelada omitindo a entrada (config[...] devolve null naturalmente).
        if (apiKey is not null)
            settings["AegisAi:ApiKey"] = apiKey;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddAegisScoreInfrastructure(config);
        return services.BuildServiceProvider();
    }
}
