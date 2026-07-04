namespace AegisScore.Application.Abstractions;

/// <summary>
/// Invariante multi-tenant violada em tempo de escrita: não há tenant ambiente resolvido,
/// ou uma entidade tentou ser gravada com um TenantId divergente do contexto.
/// Fail-closed — nunca deve ser tratada como erro de validação de cliente.
/// </summary>
public sealed class TenantSecurityException : Exception
{
    public TenantSecurityException(string message) : base(message) { }
}
