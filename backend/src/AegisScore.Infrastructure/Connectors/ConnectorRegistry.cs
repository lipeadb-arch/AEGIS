using AegisScore.Application.Abstractions;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Connectors;

/// <summary>
/// Resolves evidence connectors registered in DI. Adding a new tool/stack is just registering
/// another <see cref="IEvidenceConnector"/> — the registry (and the rest of the core) is untouched.
/// </summary>
public class ConnectorRegistry : IConnectorRegistry
{
    private readonly IReadOnlyList<IEvidenceConnector> _connectors;

    public ConnectorRegistry(IEnumerable<IEvidenceConnector> connectors)
        => _connectors = connectors.ToList();

    public IReadOnlyList<IEvidenceConnector> All => _connectors;

    public IEvidenceConnector? Resolve(ConnectorProvider provider, ConnectorCapability capability) =>
        _connectors.FirstOrDefault(c => c.Provider == provider && c.Capability == capability);
}
