using System.Collections.Generic;
using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests;

/// <summary>
/// Composition root REAL (ServiceCollection + IConfiguration in-memory), sem rede nem banco. Sob o provedor
/// ÚNICO, as interfaces neutras (ILLMClient, IAiAssessmentService) sempre resolvem os ROTEADORES tenant-scoped
/// — a decisão externo × stub é do gate em runtime, não do registro. O gate reflete a configuração <c>Ai</c>.
/// </summary>
public sealed class AegisAiDependencyInjectionTests
{
    [Fact]
    public void ContainerLigaSempreOsRoteadoresNeutros()
    {
        using var provider = BuildProvider(mode: "Simulated", apiKey: null);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<ILLMClient>().Should().BeOfType<TenantScopedLlmRouter>(
            "o transporte neutro passa SEMPRE pelo roteador — a fronteira de dados do Free Tier");
        sp.GetRequiredService<IAiAssessmentService>().Should().BeOfType<TenantScopedAssessmentRouter>(
            "o motor de alto nível também é mediado pelo gate");
    }

    [Fact]
    public void Gate_SimuladoSemChave_NaoConfigurado()
    {
        using var provider = BuildProvider(mode: "Simulated", apiKey: null);

        var gate = provider.GetRequiredService<IAiFreeTierGate>();

        gate.ProviderConfigured.Should().BeFalse("sem chave/modo demonstrativo o provedor externo não sobe");
        gate.IsExternalAllowedForSlug("qualquer").Should().BeFalse();
    }

    [Fact]
    public void Gate_ExternalDemoComChave_ConfiguradoELiberaSomenteAllowlist()
    {
        using var provider = BuildProvider(mode: "ExternalDemo", apiKey: "chave", allowedSlug: "sandbox");

        var gate = provider.GetRequiredService<IAiFreeTierGate>();

        gate.ProviderConfigured.Should().BeTrue();
        gate.IsExternalAllowedForSlug("sandbox").Should().BeTrue("o slug configurado da allowlist libera");
        gate.IsExternalAllowedForSlug("tenant-corporativo").Should().BeFalse("fora da allowlist nunca libera");
    }

    [Fact]
    public void HttpClientDoAnthropic_TemTimeoutNativoDesabilitado_PollyEhAutoridadeUnica()
    {
        using var provider = BuildProvider(mode: "ExternalDemo", apiKey: "chave");

        var client = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(nameof(AnthropicLlmClient));

        client.Timeout.Should().Be(System.Threading.Timeout.InfiniteTimeSpan,
            "o timeout nativo (100s) do HttpClient é desabilitado para o Polly (120s) ser a única autoridade");
    }

    private static ServiceProvider BuildProvider(string mode, string? apiKey, string? allowedSlug = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // Connection string dummy: AddDbContext apenas REGISTRA (não abre conexão) — evita null no UseNpgsql.
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=aegis_test;Username=test;Password=test",
            ["Ai:Mode"] = mode,
        };
        if (apiKey is not null) settings["Ai:ApiKey"] = apiKey;
        if (allowedSlug is not null) settings["Ai:FreeTier:AllowedTenantSlugs:0"] = allowedSlug;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddAegisScoreInfrastructure(config);
        // O host (Program.cs/DbMigrator) é quem registra o ITenantContext — o mesmo padrão de que dependem
        // ControlStateWriter, WorkspacePostureQuery e o resolver do gate. O container mínimo do teste o provê.
        services.AddScoped<ITenantContext>(_ => new SystemTenantContext(null));
        return services.BuildServiceProvider();
    }
}
