namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Sinal TIPADO da categoria PR.IR (Technology Infrastructure Resilience). Carrega a métrica de perímetro
/// que decide a postura de rede e produz, via <see cref="ToMetricLines"/>, o rótulo canônico que a regra
/// PR.IR-01 do <c>StubLlmClient</c> lê — o MESMO que o <c>TelemetryController</c> compõe na rota
/// <c>/telemetry/protect/network</c>. Centraliza o contrato de fio na Application (padrão de identidade).
/// </summary>
/// <param name="DefaultDenyFirewallEnforced">Firewall com política default-deny (perímetro restritivo); ausente reprova.</param>
public record NetworkTelemetrySignal(
    bool DefaultDenyFirewallEnforced)
{
    /// <summary>Rótulo canônico lido pela regra PR.IR-01 do <c>StubLlmClient</c> (e pelo prompt do motor real).</summary>
    public IReadOnlyList<string> ToMetricLines() => new[]
    {
        $"Default Deny Firewall Enforced: {(DefaultDenyFirewallEnforced ? "true" : "false")}",
    };
}
