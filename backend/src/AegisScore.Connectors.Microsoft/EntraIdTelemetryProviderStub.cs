using AegisScore.Application.Telemetry.Providers;

namespace AegisScore.Connectors.Microsoft;

/// <summary>
/// Implementação STUB da porta <see cref="IEntraIdTelemetryProvider"/>. NÃO chama o Microsoft Graph ainda:
/// sintetiza um retrato de postura de identidade de ALTO RISCO — um cenário de demonstração inteiramente
/// fictício, no estilo dos indicadores de exposição de frameworks de assessment de identidade — para
/// exercitar a degradação de score em <c>PR.AA-01</c> (privilégio sem MFA) e <c>GV.RR-01</c> (excesso de
/// administradores) ponta a ponta, ANTES de plugar a chamada OAuth real.
///
/// Implementação real (próxima fase): autentica via OAuth client credentials (segredos cifrados em
/// <c>ConnectorConfig.EncryptedSettings</c>) e agrega as consultas do Microsoft Graph — directoryRoles /
/// roleManagement (contas privilegiadas), reports/authenticationMethods (cobertura de MFA), users com
/// mailbox e userType=Guest + signInActivity (convidados inativos). Mesmo idioma do <c>SharePointProvider</c>.
/// </summary>
public sealed class EntraIdTelemetryProviderStub : IEntraIdTelemetryProvider
{
    public Task<EntraIdIdentityPosture> FetchIdentityPostureAsync(
        Guid tenantId, string tenantDomain, CancellationToken ct = default)
    {
        // Cenário sintético deliberadamente crítico — cada número existe para exercitar uma regra do motor:
        //   • 12 contas privilegiadas   → excede o limite de menor privilégio (>10)  → reprova GV.RR-01
        //   •  3 admins sem MFA          → privilégio sem MFA efetivo                 → reprova PR.AA-01
        //   •  5 admins com mailbox      → superfície de phishing sobre o admin
        //   •  4 guests inativos > 30d   → acesso de terceiros esquecido
        //   • contas OT sem MFA técnico  → terminais industriais (PLC/HMI) que não suportam MFA nativo
        var posture = new EntraIdIdentityPosture(
            TenantId: tenantId,
            TenantDomain: string.IsNullOrWhiteSpace(tenantDomain) ? "demo.example.com" : tenantDomain,
            TotalPrivilegedAccounts: 12,
            PrivilegedAccountsWithoutMfa: 3,
            PrivilegedAccountsWithMailbox: 5,
            InactiveGuestAccountsOver30Days: 4,
            MfaExemptServiceAccounts: new[]
            {
                "svc-ot-plc-01@example.com",
                "svc-ot-hmi-02@example.com",
            },
            CollectedAt: DateTimeOffset.UtcNow);

        return Task.FromResult(posture);
    }
}
