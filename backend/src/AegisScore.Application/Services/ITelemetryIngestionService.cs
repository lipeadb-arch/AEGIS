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
/// Sinal de telemetria de UM ativo (Identify / ID.AM) normalizado na borda. Carrega os metadados táticos
/// que a postura de gestão de ativos exige — cobertura de EDR, ciclo de vida do SO, CVEs críticas ativas
/// e se o ativo é vital — mais o controle NIST alvo (default resolvido para <c>ID.AM-01</c>).
/// </summary>
public record AssetTelemetrySignal(
    string AssetName, EdrCoverageStatus EdrCoverage, OsLifecycleStatus OsLifecycle,
    int CriticalVulnerabilitiesCount, bool IsCriticalAsset, string SubcategoryCode);

/// <summary>
/// Sinal de telemetria de UMA categoria de qualquer pilar do NIST (Protect, Detect, Respond, Recover),
/// normalizado na borda. O controller de cada categoria (Identity/PR.AA, Monitoring/DE.CM, Analysis/RS.MA,
/// Execution/RC.RP…) achata seu contrato específico neste formato comum — o controle NIST alvo, o pilar e a
/// categoria (que compõem o cabeçalho legível) e as linhas de métrica já formatadas ("Privileged MFA
/// Coverage: 50%", "Immutable Backups Enabled: false") que o motor avalia. Um único seam DRY para todos os
/// pilares — antes eram <c>ProtectTelemetrySignal</c> e <c>DetectTelemetrySignal</c>, idênticos.
/// </summary>
public record CategoryTelemetrySignal(
    string SubcategoryCode, string Pillar, string Category, IReadOnlyList<string> Metrics);

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

    /// <summary>
    /// Ingere a telemetria de um ativo (Identify / ID.AM): compõe os metadados táticos num payload que o
    /// motor avalia contra o controle de gestão de ativos, persistindo o veredito com fonte <c>Telemetry</c>.
    /// Mesmo caminho autoritativo do <see cref="IngestAsync"/> — um ativo exposto (sem EDR, SO obsoleto)
    /// pode rebaixar o controle; um ativo íntegro pode levá-lo a 100%. Fail-closed no tenant.
    /// </summary>
    Task<ComplianceVerdict> IngestAssetAsync(AssetTelemetrySignal signal, CancellationToken ct = default);

    /// <summary>
    /// Ingere a telemetria de UMA categoria de um pilar NIST (Protect/Detect/Respond/Recover): compõe as
    /// métricas num payload que o motor avalia contra o controle indicado, persistindo o veredito com fonte
    /// <c>Telemetry</c>. Caminho autoritativo ÚNICO para todos os pilares — qualquer falha de auditoria
    /// (privilégio sem MFA, ativo crítico sem monitoração, backup corrompido…) reprova o controle.
    /// Fail-closed no tenant.
    /// </summary>
    Task<ComplianceVerdict> IngestCategoryAsync(CategoryTelemetrySignal signal, CancellationToken ct = default);
}
