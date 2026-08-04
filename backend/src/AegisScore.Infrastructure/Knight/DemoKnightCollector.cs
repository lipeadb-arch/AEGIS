using System;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Knight;

/// <summary>
/// Coletor de DEMONSTRAÇÃO do AEGIS KNIGHT: produz fatos normalizados 100% SINTÉTICOS (domínio example.com),
/// determinístico e sem rede — nunca consulta Microsoft Graph, AD local nem Okta. Percorre o MESMO pipeline
/// multicoletor das fontes reais; a única diferença é a origem dos fatos. O cenário é calibrado para o
/// resultado demonstrável preservado da Entrega 1 (5 indicadores compartilhados → score 23, cobertura 100%):
///   • 2 de 12 privilegiadas sem MFA        → AK-ENTRA-001 Exposed (Critical)
///   • 12 privilegiadas (teto 10)           → AK-ENTRA-002 Exposed (High)
///   • 0 privilegiadas com mailbox          → AK-ENTRA-003 Passed (Medium)
///   • 3 convidados inativos                → AK-ENTRA-004 Exposed (Medium)
///   • 2 contas de serviço isentas de MFA,
///     com controle compensatório COMPROVADO → AK-ENTRA-005 Mitigated (High)
/// </summary>
public sealed class DemoKnightCollector : IKnightCollector
{
    private const string DemoDomain = "demo.example.com";

    public KnightSourceType Source => KnightSourceType.Demo;

    public Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        var facts = new KnightFactSet(new[]
        {
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, 12),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, 2),
            KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithMailbox, 0),
            KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, 3),
            KnightObservation.OfCount(KnightSignalKey.ServiceAccountsMfaExempt, 2),
            // Evidência compensatória EXPLÍCITA e comprovada — a única base admissível para Mitigated.
            KnightObservation.OfFlag(KnightSignalKey.ServiceAccountMfaExemptionProven, true),
        });

        var capabilities = new[]
        {
            new KnightCapabilityStatus(KnightCapability.PrivilegedRoleInventory, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.GuestAccounts, KnightCapabilityOutcome.Collected),
            new KnightCapabilityStatus(KnightCapability.ServiceAccountExemptions, KnightCapabilityOutcome.Collected),
        };

        var result = new KnightCollectionResult(
            KnightSourceType.Demo,
            KnightSourceState.Completed,
            "Provedor de Demonstração AEGIS KNIGHT",
            facts,
            capabilities,
            DateTimeOffset.UtcNow,
            $"Cenário sintético (domínio {DemoDomain}); nunca consultou Microsoft Graph, AD local ou Okta.");

        return Task.FromResult(result);
    }
}
