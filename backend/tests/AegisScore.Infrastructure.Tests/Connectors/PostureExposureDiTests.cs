using System;
using AegisScore.Application.Abstractions;
using AegisScore.Connectors.Microsoft;
using AegisScore.Connectors.Microsoft.Sentinel;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Connectors;

/// <summary>
/// [AEGIS-MVP-POSTURE-02] Lifetime da resolução de conectores: o <see cref="MicrosoftSecureScoreConnector"/>
/// injeta um typed HttpClient (<c>IEntraGraphClient</c>, transient) — se ele ou o <see cref="ConnectorRegistry"/>
/// fossem SINGLETON, o cliente ficaria CAPTURADO no root provider. Este teste prova que ambos são scoped: o
/// registry não resolve do root sob validação de escopo, e escopos distintos entregam INSTÂNCIAS distintas do
/// conector. Também prova que o registry resolve Microsoft/SecureScore.
/// </summary>
public sealed class PostureExposureDiTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Registrações REAIS do pacote Microsoft (incluindo o MicrosoftSecureScoreConnector como IEvidenceConnector)
        // — assim uma regressão que volte o conector a singleton é detectada aqui.
        services.AddMicrosoftConnectors();
        // O registry como na produção (Infrastructure DI): SCOPED.
        services.AddScoped<IConnectorRegistry, ConnectorRegistry>();
        // Dependência do conector fora do pacote Microsoft (na produção vem da Infrastructure).
        services.AddSingleton<IConnectorSecretProtector, FakeProtectorForDi>();

        // ValidateScopes: uma dependência cativa (scoped/transient capturado por singleton) é rejeitada.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Registry_IsScoped_NotResolvableFromRoot()
    {
        using var provider = BuildProvider();
        // Registry scoped não pode ser resolvido do ROOT sob ValidateScopes — prova que não é singleton cativo.
        var act = () => provider.GetRequiredService<IConnectorRegistry>();
        act.Should().Throw<InvalidOperationException>("o registry é scoped — não é capturável no root provider");
    }

    [Fact]
    public void Registry_ResolvesMicrosoftSecureScore_AndConnectorIsScopedNotCaptured()
    {
        using var provider = BuildProvider();

        IEvidenceConnector conn1, conn2;
        using (var scope1 = provider.CreateScope())
        {
            var registry1 = scope1.ServiceProvider.GetRequiredService<IConnectorRegistry>();
            var resolved = registry1.Resolve(ConnectorProvider.Microsoft, ConnectorCapability.SecureScore);
            resolved.Should().NotBeNull("o registry resolve o adaptador Microsoft/SecureScore");
            resolved.Should().BeOfType<MicrosoftSecureScoreConnector>();
            conn1 = resolved!;
        }

        using (var scope2 = provider.CreateScope())
        {
            conn2 = scope2.ServiceProvider.GetRequiredService<IConnectorRegistry>()
                .Resolve(ConnectorProvider.Microsoft, ConnectorCapability.SecureScore)!;
        }

        // Escopos distintos → instâncias distintas: o conector é scoped, não um singleton com o typed client capturado.
        conn1.Should().NotBeSameAs(conn2, "o conector é resolvido por escopo (não capturado no root)");
    }

    [Fact]
    public void Registry_ResolvesMicrosoftSentinelSiem()
    {
        // [AEGIS-MVP-MICROSOFT-SENTINEL] O registry resolve MicrosoftSentinel/Siem para o adaptador REAL (não mais
        // "adaptador não implementado"). Distinto de Generic/Siem (push) — o par provider+capability é a chave.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IConnectorRegistry>()
            .Resolve(ConnectorProvider.MicrosoftSentinel, ConnectorCapability.Siem);

        resolved.Should().BeOfType<MicrosoftSentinelConnector>("o registry resolve o adaptador MicrosoftSentinel/Siem");
    }

    private sealed class FakeProtectorForDi : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
