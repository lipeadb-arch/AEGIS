using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>Integrações da plataforma Aegis Score: KNIGHT e biblioteca documental do NIST.</summary>
public static class ProductConnectorScope
{
    public static bool Supports(ConnectorProvider provider, ConnectorCapability capability) =>
        (provider is ConnectorProvider.Microsoft or ConnectorProvider.Google)
        && (capability is ConnectorCapability.IdentityPosture or ConnectorCapability.PolicyDocuments);
}
