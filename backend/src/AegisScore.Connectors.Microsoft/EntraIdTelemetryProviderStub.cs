using AegisScore.Application.Telemetry.Providers;

namespace AegisScore.Connectors.Microsoft;

/// <summary>
/// Implementação STUB da porta <see cref="IEntraIdTelemetryProvider"/>. NÃO chama o Microsoft Graph ainda:
/// sintetiza um retrato de postura de identidade de ALTO RISCO, fiel aos Indicadores de Exposição de um
/// relatório real de assessment (Purple Knight / tenant vicunha.com.br), para exercitar a degradação de
/// score em <c>PR.AA-01</c> (privilégio sem MFA) e <c>GV.RR-01</c> (excesso de administradores) ponta a
/// ponta, ANTES de plugar a chamada OAuth real.
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
        // Cenário deliberadamente crítico — cada número corresponde a um Indicador de Exposição do relatório:
        //   • 15 contas privilegiadas   → PK "More than 10 Privileged Administrators exist"  → reprova GV.RR-01
        //   •  4 admins sem MFA          → PK "MFA not configured for privileged accounts"     → reprova PR.AA-01
        //   •  9 admins com mailbox      → PK "Privileged accounts with mailbox"               → superfície de phishing
        //   •  6 guests inativos > 30d   → PK "Guest accounts that were inactive for more than 30 days"
        //   • contas OT sem MFA técnico  → terminais fabris (urdideira, costura) que não suportam MFA nativo
        var posture = new EntraIdIdentityPosture(
            TenantId: tenantId,
            TenantDomain: string.IsNullOrWhiteSpace(tenantDomain) ? "vicunha.com.br" : tenantDomain,
            TotalPrivilegedAccounts: 15,
            PrivilegedAccountsWithoutMfa: 4,
            PrivilegedAccountsWithMailbox: 9,
            InactiveGuestAccountsOver30Days: 6,
            MfaExemptServiceAccounts: new[]
            {
                "svc-urdideira-ot@vicunha.com.br",
                "svc-costura-plc@vicunha.com.br",
                "svc-dashboard-fabril@vicunha.com.br",
            },
            CollectedAt: DateTimeOffset.UtcNow);

        return Task.FromResult(posture);
    }
}
