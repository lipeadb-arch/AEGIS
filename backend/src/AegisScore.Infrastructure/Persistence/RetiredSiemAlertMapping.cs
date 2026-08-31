using System.Collections.Generic;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// [AEGIS-MVP-SCORE-GUARD-SIEM-01] Identidade CANÔNICA e ÚNICA do mapping de scoring APOSENTADO: o par
/// (<see cref="ConnectorCapability.Siem"/>, <c>siem.alert.highSeverity</c>), que antes concedia
/// <c>Compliant</c> aos controles <c>DE.AE-02</c>/<c>DE.CM-01</c> pela mera presença de um alerta de alta
/// severidade — semântica incorreta (um alerta não comprova monitoramento suficiente, cobertura, resposta,
/// contenção nem playbook).
///
/// Autoridade compartilhada por: (1) o seeder, que NÃO o inclui mais nos mappings padrão e o REMOVE de bancos
/// já semeados de forma idempotente; (2) o guard de prontidão, que recusa a base enquanto ele persistir ativo;
/// (3) o reparo de estados legados, que reprojeta/retrai apenas os controles historicamente afetados. Escopo
/// ESTREITO de propósito — nunca toca outros mappings, outras capabilities ou o Secure Score/EDR.
/// </summary>
internal static class RetiredSiemAlertMapping
{
    public const ConnectorCapability Capability = ConnectorCapability.Siem;
    public const string SignalKey = "siem.alert.highSeverity";

    /// <summary>Os ÚNICOS controles historicamente afetados por este mapping (os que ele mapeava) — o reparo se limita a eles.</summary>
    public static readonly IReadOnlyList<string> AffectedControls = new[] { "DE.AE-02", "DE.CM-01" };
}
