using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-03] Suficiência de EVIDÊNCIA por indicador
// ============================================================================
// Uma avaliação KNIGHT pode terminar em PartialCollection porque UMA capacidade opcional falhou — por
// exemplo, as permissões de Identity Risk que ainda não foram concedidas. Tratar essa parcialidade GLOBAL
// como "nenhuma evidência serve" jogaria fora coletas perfeitamente completas de papéis privilegiados e de
// registro de MFA, e o produto diria "não dá para comprovar" quando dá.
//
// O inverso é pior: aceitar como prova de melhora uma coleta em que a capacidade NECESSÁRIA àquele
// indicador falhou. Ausência de dado vira "0 afetados" em nenhum lugar do KNIGHT — e não pode virar
// "corrigido" aqui.
//
// Por isso a suficiência é avaliada POR INDICADOR, contra as capacidades que aquele indicador realmente
// consome.

/// <summary>
/// Capacidades de coleta que sustentam CADA indicador com jornada de remediação nesta entrega. O escopo é
/// fechado de propósito: um indicador fora do mapa não tem requisito declarado e a suficiência recai apenas
/// sobre o veredito do próprio indicador (que já vira <c>NotEvaluated</c> quando o dado falta).
/// </summary>
public static class KnightIndicatorEvidence
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<KnightCapability>> Required =
        new Dictionary<string, IReadOnlyList<KnightCapability>>(StringComparer.Ordinal)
        {
            // Cruzamento entre membros de papéis privilegiados e o relatório de registro de métodos.
            ["AK-ENTRA-001"] = new[] { KnightCapability.PrivilegedRoleInventory, KnightCapability.MfaRegistration },
            // Só o inventário de papéis privilegiados produz o total comparado ao teto.
            ["AK-ENTRA-002"] = new[] { KnightCapability.PrivilegedRoleInventory },
            // Só as contas de convidado e sua atividade.
            ["AK-ENTRA-004"] = new[] { KnightCapability.GuestAccounts },
        };

    /// <summary>Capacidades exigidas pelo indicador (vazio quando ele não declara requisito nesta entrega).</summary>
    public static IReadOnlyList<KnightCapability> RequiredCapabilities(string indicatorId) =>
        Required.TryGetValue((indicatorId ?? "").Trim(), out var caps) ? caps : Array.Empty<KnightCapability>();

    /// <summary>Um desfecho de capacidade que NÃO entregou o dado — nenhum deles pode sustentar "melhorou".</summary>
    public static bool IsFailedOutcome(KnightCapabilityOutcome outcome) => outcome != KnightCapabilityOutcome.Collected;

    /// <summary>
    /// Capacidades NECESSÁRIAS ao indicador que a coleta declarou como NÃO coletadas. Uma capacidade que
    /// sequer aparece na lista não é tratada como falha aqui: fonte que não declara capacidades (o provedor
    /// de demonstração, por exemplo) tem a suficiência decidida pelo veredito do próprio indicador, que já
    /// se declara <c>NotEvaluated</c> quando o fato está ausente.
    /// </summary>
    public static IReadOnlyList<KnightCapabilityStatus> MissingFor(
        string indicatorId, IReadOnlyList<KnightCapabilityStatus> capabilities)
    {
        var required = RequiredCapabilities(indicatorId);
        if (required.Count == 0 || capabilities.Count == 0) return Array.Empty<KnightCapabilityStatus>();

        return capabilities
            .Where(c => required.Contains(c.Capability) && IsFailedOutcome(c.Outcome))
            .OrderBy(c => c.Capability)
            .ToList();
    }

    /// <summary>
    /// <c>true</c> quando o indicador recebeu um veredito de fato. <c>NotEvaluated</c> e <c>Error</c> são
    /// exatamente as situações em que o dado faltou — nunca comprovam melhora.
    /// </summary>
    public static bool IsConclusiveVerdict(KnightIndicatorStatus status) => status
        is KnightIndicatorStatus.Passed or KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated;
}
