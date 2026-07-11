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
}
