using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Implementação da porta de ingestão de telemetria. Orquestração fina, sem I/O próprio: resolve o
/// tenant ambiente, compõe o payload cru a partir do sinal e delega ao <see cref="IAegisAiEvaluatorService"/>,
/// que faz a inferência e o upsert do ledger com fonte <c>Telemetry</c>.
///
/// Secure-by-design: o guard de tenant aqui converte o <c>Guid?</c> do contexto num <c>Guid</c> exigido
/// pelo motor e falha fechado quando não há tenant — o motor e o writer repetem a checagem (defesa em
/// profundidade), mas barrar na borda evita montar payload e queimar uma inferência num contexto inconsistente.
/// </summary>
public sealed class TelemetryIngestionService : ITelemetryIngestionService
{
    private readonly IAegisAiEvaluatorService _evaluator;
    private readonly ITenantContext _tenant;

    public TelemetryIngestionService(IAegisAiEvaluatorService evaluator, ITenantContext tenant)
    {
        _evaluator = evaluator;
        _tenant = tenant;
    }

    public async Task<ComplianceVerdict> IngestAsync(TelemetrySignal signal, CancellationToken ct = default)
    {
        // Fail-closed: sem tenant resolvido no contexto (claim do JWT / X-Tenant), nada avança.
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Ingestão de telemetria sem tenant resolvido no contexto (fail-closed).");

        var payload = ComposePayload(signal);
        return await _evaluator.EvaluateAsync(tenantId, signal.SubcategoryCode, payload, ct);
    }

    public async Task<ComplianceVerdict> IngestAssetAsync(AssetTelemetrySignal signal, CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Ingestão de telemetria de ativo sem tenant resolvido no contexto (fail-closed).");

        var payload = ComposeAssetPayload(signal);
        return await _evaluator.EvaluateAsync(tenantId, signal.SubcategoryCode, payload, ct);
    }

    public async Task<ComplianceVerdict> IngestCategoryAsync(CategoryTelemetrySignal signal, CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException(
                "Ingestão de telemetria de categoria sem tenant resolvido no contexto (fail-closed).");

        var payload = ComposeCategoryPayload(signal);
        return await _evaluator.EvaluateAsync(tenantId, signal.SubcategoryCode, payload, ct);
    }

    /// <summary>
    /// Compõe o payload de UMA categoria (qualquer pilar): um cabeçalho "Pilar / Categoria (control CÓDIGO)"
    /// e as linhas de métrica já formatadas pelo controller. Os rótulos são o contrato lido pela heurística
    /// por categoria do StubLlmClient (e pelo prompt do motor real). Aqui só concatenamos — nenhuma regra
    /// nesta camada. Único compositor para Protect/Detect/Respond/Recover.
    /// </summary>
    private static string ComposeCategoryPayload(CategoryTelemetrySignal s) =>
        $"{s.Pillar} / {s.Category} (control {s.SubcategoryCode}) telemetry:\n{string.Join("\n", s.Metrics)}";

    /// <summary>
    /// Compõe um envelope legível a partir dos campos do webhook. O motor injeta isto no User Prompt
    /// dentro de uma fronteira de dados explícita e o trata como conteúdo NÃO confiável (anti-injeção);
    /// aqui só concatenamos — nenhuma interpretação de conteúdo acontece nesta camada.
    /// </summary>
    private static string ComposePayload(TelemetrySignal s) =>
        $"""
        Source: {s.Source}
        Event: {s.EventName}
        Severity: {s.Severity}
        RawData: {s.RawData}
        """;

    /// <summary>
    /// Compõe o payload de telemetria de ATIVO num texto estruturado e determinístico. Os rótulos
    /// ("EDR Coverage: Absent", "OS Lifecycle: EndOfLife", "Critical Vulnerabilities: N") são o contrato
    /// que a heurística de Identify do StubLlmClient — e o prompt do motor real — leem para decidir a postura.
    /// </summary>
    private static string ComposeAssetPayload(AssetTelemetrySignal s) =>
        $"""
        Asset Management telemetry for '{s.AssetName}':
        EDR Coverage: {s.EdrCoverage}
        OS Lifecycle: {s.OsLifecycle}
        Critical Vulnerabilities: {s.CriticalVulnerabilitiesCount}
        Critical Asset: {(s.IsCriticalAsset ? "true" : "false")}
        """;
}
