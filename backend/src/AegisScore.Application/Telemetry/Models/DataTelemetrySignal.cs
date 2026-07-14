namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Sinal TIPADO da categoria PR.DS (Data Security) — o análogo de dados do <c>IdentityTelemetrySignal</c>.
/// Carrega as métricas que decidem a postura de proteção de dados e produz, via <see cref="ToMetricLines"/>,
/// os rótulos canônicos que o motor (StubLlmClient / prompt real) lê. São os MESMOS rótulos que o
/// <c>TelemetryController</c> compõe hoje na rota <c>/telemetry/protect/data</c> — o signal apenas centraliza
/// esse contrato de fio na Application (paridade com o padrão de identidade), mantendo intercambiáveis a
/// composição inline do controller e este record tipado.
/// </summary>
/// <param name="EndpointEncryptionCoverage">% de endpoints com criptografia em repouso (mínimo 95%).</param>
/// <param name="UnencryptedTrafficDetected">Tráfego em claro detectado na rede — dado em trânsito exposto.</param>
public record DataTelemetrySignal(
    double EndpointEncryptionCoverage,
    bool UnencryptedTrafficDetected)
{
    /// <summary>Rótulos canônicos lidos pela regra PR.DS-01 do <c>StubLlmClient</c> (e pelo prompt do motor real).</summary>
    public IReadOnlyList<string> ToMetricLines() => new[]
    {
        $"Endpoint Encryption Coverage: {EndpointEncryptionCoverage}%",
        $"Unencrypted Traffic Detected: {(UnencryptedTrafficDetected ? "true" : "false")}",
    };
}
