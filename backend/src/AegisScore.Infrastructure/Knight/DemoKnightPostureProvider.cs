using System;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Provedor de DEMONSTRAÇÃO do AEGIS KNIGHT: sintetiza um retrato de postura de identidade 100% FICTÍCIO
/// (domínio example.com), determinístico e sem rede. NÃO consulta Microsoft Graph, AD local nem Okta — a
/// modalidade é <see cref="KnightAssessmentMode.Demo"/> e a UI a rotula como DEMONSTRAÇÃO.
///
/// O cenário é calibrado para exercitar uma MISTURA de vereditos — Passed, Exposed e (somente com uma
/// evidência compensatória EXPLÍCITA no snapshot) Mitigated:
///   • 2 de 12 contas privilegiadas sem MFA        → AK-ENTRA-001 Exposed (Critical)
///   • 12 contas privilegiadas (teto 10)           → AK-ENTRA-002 Exposed (High)
///   • 0 contas privilegiadas com mailbox          → AK-ENTRA-003 Passed (Medium)
///   • 3 convidados inativos além da janela         → AK-ENTRA-004 Exposed (Medium)
///   • 2 contas de serviço sem MFA, com controle
///     compensatório COMPROVADO no snapshot         → AK-ENTRA-005 Mitigated (High)
///
/// Nenhum toggle de UI altera este veredito: a mitigação de AK-ENTRA-005 nasce de um controle compensatório
/// tecnicamente comprovado PRESENTE no snapshot, não de um botão de cenário.
/// </summary>
public sealed class DemoKnightPostureProvider : IKnightPostureProvider
{
    private const string DemoDomain = "demo.example.com";

    public KnightAssessmentMode Mode => KnightAssessmentMode.Demo;

    public Task<KnightPostureSnapshot> CollectAsync(Guid tenantId, CancellationToken ct = default)
    {
        var snapshot = new KnightPostureSnapshot(
            Mode: KnightAssessmentMode.Demo,
            Source: "Provedor de Demonstração AEGIS KNIGHT",
            TenantDomain: DemoDomain,
            CollectedAt: DateTimeOffset.UtcNow,
            TotalPrivilegedAccounts: 12,
            PrivilegedAccountsWithoutMfa: 2,
            PrivilegedAccountsWithMailbox: 0,
            InactiveGuestAccountsOverWindow: 3,
            InactiveGuestWindowDays: KnightCatalog.InactiveGuestWindowDays,
            MfaExemptServiceAccounts: new[]
            {
                $"svc-integracao-01@{DemoDomain}",
                $"svc-backup-02@{DemoDomain}",
            },
            CompensatingControls: new[]
            {
                // Evidência compensatória EXPLÍCITA e comprovada — a única base admissível para Mitigated.
                new KnightCompensatingControl(
                    ControlKey: "service-account-network-isolation",
                    Description: "Contas de serviço confinadas em segmento de rede dedicado sem rota de saída, "
                        + "comprovado por exportação das regras de firewall do ambiente de demonstração.",
                    CoversCategory: KnightIndicatorCategory.ServiceAccounts,
                    TechnicallyProven: true),
            });

        return Task.FromResult(snapshot);
    }
}
