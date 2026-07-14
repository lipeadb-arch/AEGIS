namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Sinal TIPADO da categoria PR.PS (Platform Security). Carrega as métricas de hardening que decidem a
/// postura de plataforma e produz, via <see cref="ToMetricLines"/>, os rótulos canônicos que a regra
/// PR.PS-01 do <c>StubLlmClient</c> lê — os MESMOS que o <c>TelemetryController</c> compõe na rota
/// <c>/telemetry/protect/platform</c>. Centraliza o contrato de fio na Application (padrão de identidade).
/// </summary>
/// <param name="CisBenchmarkComplianceRate">% de conformidade com o benchmark CIS de hardening (mínimo 80%).</param>
/// <param name="MissingCriticalPatchesCount">Nº de patches críticos pendentes — qualquer valor &gt; 0 reprova.</param>
public record PlatformTelemetrySignal(
    double CisBenchmarkComplianceRate,
    int MissingCriticalPatchesCount)
{
    /// <summary>Rótulos canônicos lidos pela regra PR.PS-01 do <c>StubLlmClient</c> (e pelo prompt do motor real).</summary>
    public IReadOnlyList<string> ToMetricLines() => new[]
    {
        $"CIS Benchmark Compliance Rate: {CisBenchmarkComplianceRate}%",
        $"Missing Critical Patches: {MissingCriticalPatchesCount}",
    };
}
