using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Sinal de telemetria normalizado na borda HTTP, pronto para o motor avaliar UM controle NIST. É o
/// equivalente de aplicação do <c>TelemetryIngestionRequest</c> (contrato da Api) — mantém a Application
/// sem depender da camada de apresentação.
/// </summary>
/// <param name="Source">Ferramenta de origem, ex.: "Microsoft Defender".</param>
/// <param name="EventName">Nome do evento/alerta, ex.: "Malware blocked".</param>
/// <param name="Severity">Severidade reportada pela ferramenta, ex.: "High".</param>
/// <param name="SubcategoryCode">Controle NIST CSF 2.0 que a evidência endereça, ex.: "PR.AA-01".</param>
/// <param name="RawData">Evidência técnica crua (log/JSON) — dado NÃO confiável.</param>
public record TelemetrySignal(
    string Source, string EventName, string Severity, string SubcategoryCode, string RawData);

/// <summary>
/// Porta de ingestão de telemetria: a superfície que finalmente dá um CHAMADOR ao motor de avaliação
/// (<see cref="IAegisAiEvaluatorService.EvaluateAsync"/>). Recebe um sinal de segurança já normalizado,
/// resolve o tenant do contexto (fail-closed), compõe o payload cru para o LLM e delega ao motor — que
/// grava o veredito no ledger com precedência de <c>Telemetry</c> (autoritativa, pode atingir 100%).
///
/// É o seam fino entre a borda HTTP e o motor: mantém o <c>TelemetryController</c> magro e torna a
/// composição do payload testável isoladamente. A implementação vive na Infrastructure (padrão do
/// projeto: portas na Application, impls na Infrastructure).
/// </summary>
public interface ITelemetryIngestionService
{
    /// <summary>
    /// Avalia o sinal contra o controle NIST indicado e persiste o veredito (fonte <c>Telemetry</c>).
    /// Fail-closed: sem tenant resolvido no contexto, lança antes de tocar o motor.
    /// </summary>
    Task<ComplianceVerdict> IngestAsync(TelemetrySignal signal, CancellationToken ct = default);
}
