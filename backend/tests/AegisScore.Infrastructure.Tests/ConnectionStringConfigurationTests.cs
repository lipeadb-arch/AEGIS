using AegisScore.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests;

/// <summary>
/// [AEGIS-AUD-057] A connection string do banco deixou de ser versionada em appsettings.json — passa a
/// vir de user-secrets ou variável de ambiente. Estes testes travam o fail-fast: sem
/// ConnectionStrings:AegisScore, a composição da API precisa abortar com mensagem clara, sem
/// NullReferenceException, sem tentar conectar e sem ecoar o valor configurado.
/// </summary>
public sealed class ConnectionStringConfigurationTests
{
    private static IServiceCollection ComposeWith(params (string Key, string? Value)[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddAegisScoreInfrastructure(config);
        return services;
    }

    [Fact]
    public void ConnectionStringAusente_FalhaRapido()
    {
        // Nenhuma entrada de ConnectionStrings — config[...] devolve null.
        var act = () => ComposeWith(("AegisAi:ApiKey", ""));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:AegisScore*",
                "sem connection string, UseNpgsql(null) adiaria a falha para a primeira conexão");
    }

    [Fact]
    public void ConnectionStringVazia_FalhaRapido()
    {
        var act = () => ComposeWith(("ConnectionStrings:AegisScore", ""));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ConnectionStringEmBranco_FalhaRapido()
    {
        var act = () => ComposeWith(("ConnectionStrings:AegisScore", "   "));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MensagemDeFalha_NaoMencionaCredenciais()
    {
        var act = () => ComposeWith(("ConnectionStrings:AegisScore", ""));

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().NotContain("Password", "a mensagem de erro nunca deve conter credenciais");
        ex.Message.Should().Contain("user-secrets", "deve dizer ao operador ONDE configurar");
    }

    [Fact]
    public void ConnectionStringPresente_ComponeSemLancar()
    {
        // AddDbContext apenas REGISTRA o contexto; não abre conexão. Uma string válida basta para compor.
        var act = () => ComposeWith(
            ("ConnectionStrings:AegisScore", "Host=localhost;Database=aegis_test;Username=t;Password=t"));

        act.Should().NotThrow();
    }
}
