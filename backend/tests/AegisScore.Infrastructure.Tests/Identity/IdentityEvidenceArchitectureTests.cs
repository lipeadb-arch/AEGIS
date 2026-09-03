using System;
using System.Linq;
using AegisScore.Connectors.Microsoft;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Identity;

/// <summary>
/// [AEGIS-MVP-EVIDENCE-FABRIC-01] Teste de ARQUITETURA que impede o retorno de telemetria de identidade
/// SIMULADA ao runtime de produção: o antigo <c>EntraIdTelemetryProviderStub</c> (números fictícios →
/// PR.AA-01/GV.RR-01) foi aposentado e NÃO pode voltar ao DI de produção nem existir como tipo no pacote de
/// conectores Microsoft. A postura de identidade vem SOMENTE da Evidence Fabric (aquisição real).
/// </summary>
public sealed class IdentityEvidenceArchitectureTests
{
    [Fact]
    public void ProductionMicrosoftConnectors_DoNotRegisterAnyTelemetryProviderStub()
    {
        var services = new ServiceCollection();
        services.AddMicrosoftConnectors();

        var offending = services.Any(d =>
            d.ServiceType.Name.Contains("EntraIdTelemetryProvider")
            || (d.ImplementationType?.Name.Contains("TelemetryProviderStub") ?? false));

        offending.Should().BeFalse("nenhuma rota de produção pode receber telemetria de identidade simulada");
    }

    [Fact]
    public void ConnectorsMicrosoftAssembly_ContainsNoTelemetryProviderStubType()
    {
        var assembly = typeof(DependencyInjection).Assembly;

        assembly.GetTypes()
            .Where(t => t.Name.Contains("TelemetryProviderStub") || t.Name == "EntraIdTelemetryProviderStub")
            .Should().BeEmpty("o stub de telemetria de identidade foi removido e não deve reaparecer");
    }
}
