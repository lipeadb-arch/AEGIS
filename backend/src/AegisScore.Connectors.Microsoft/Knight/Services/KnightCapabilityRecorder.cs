using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Domain;
using Microsoft.Extensions.Logging;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Registro de capacidades dos coletores de configuração
// ============================================================================
// As regras que os coletores do Entra ID, do Teams e do Exchange já seguiam, reunidas num lugar para os serviços
// novos não reimplementarem (e divergirem):
//   • uma capacidade só publica documentos quando CONCLUI — metade de uma leitura não vira configuração;
//   • a falha de uma capacidade não interrompe as outras, e cada uma guarda o PRÓPRIO desfecho e motivo;
//   • o motivo é MONTADO pelo AEGIS (categoria + endpoint), nunca o texto bruto da fonte;
//   • o estado da fonte deriva das capacidades: todas coletadas → concluída; alguma → parcial; nenhuma → o motivo
//     comum a todas, e indisponível quando os motivos divergem.

/// <summary>Acumula documentos e desfechos das capacidades de UMA coleta.</summary>
public sealed class KnightCapabilityRecorder
{
    private readonly ILogger? _log;
    private readonly string _serviceLabel;
    private readonly TimeProvider _time;

    public KnightCapabilityRecorder(string serviceLabel, ILogger? log = null, TimeProvider? time = null, string? correlationId = null)
    {
        _serviceLabel = serviceLabel;
        _log = log;
        _time = time ?? TimeProvider.System;
        CorrelationId = correlationId ?? KnightRunCorrelation.New();
    }

    /// <summary>[AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Correlação desta execução de coleta (diagnóstico e logs).</summary>
    public string CorrelationId { get; }

    public List<KnightConfigurationDocument> Documents { get; } = new();

    public List<KnightCapabilityStatus> Capabilities { get; } = new();

    /// <summary>Registra um desfecho sem executar leitura (ex.: capacidade que exige certificado e ele não existe).</summary>
    public void Record(KnightCapability capability, KnightCapabilityOutcome outcome, string? detail = null,
        KnightCapabilityDiagnostic? diagnostic = null) =>
        Capabilities.Add(new KnightCapabilityStatus(capability, outcome, detail,
            diagnostic is null ? null : diagnostic with { CorrelationId = diagnostic.CorrelationId ?? CorrelationId }));

    /// <summary>Registra uma falha classificada (desfecho, motivo e diagnóstico com a correlação desta coleta).</summary>
    public void Record(KnightCapability capability, KnightFailure failure)
    {
        Record(capability, failure.Outcome, failure.Reason, failure.Diagnostic);
        Log(capability, failure);
    }

    /// <summary>Executa UMA capacidade; publica os documentos somente se ela concluir.</summary>
    public async Task RunAsync(
        KnightCapability capability, Func<Action<KnightConfigurationDocument>, Task> collect, CancellationToken ct)
    {
        var pending = new List<KnightConfigurationDocument>();
        var started = _time.GetTimestamp();
        try
        {
            await collect(pending.Add);
            Documents.AddRange(pending);
            Capabilities.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Collected));
        }
        catch (Exception ex) when (KnightFailureClassifier.IsCallerCancellation(ex, ct))
        {
            throw;
        }
        catch (Exception ex)
        {
            // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] UMA classificação para tudo: HTTP, tempo do pipeline, circuito aberto,
            // transporte, contrato e defeito interno — sem "erro inesperado" atribuído ao provedor.
            Record(capability, KnightFailureClassifier.Classify(ex, _serviceLabel, "read", ElapsedMs(started), CorrelationId));
            if (ex is not EntraGraphException) _log?.LogDebug(ex, "Exceção original da capacidade {Capability}.", capability);
        }
    }

    /// <summary>Milissegundos desde um carimbo do relógio desta coleta.</summary>
    public long ElapsedMs(long started) => (long)_time.GetElapsedTime(started).TotalMilliseconds;

    private void Log(KnightCapability capability, KnightFailure f) =>
        _log?.LogWarning(
            "Falha classificada em {Source}/{Capability}: Outcome={Outcome}, Tipo={Kind}, Estagio={Stage}, DuracaoMs={DurationMs}, "
            + "HttpStatus={HttpStatus}, Codigo={Code}, Endpoint={Endpoint}, Correlacao={CorrelationId}, RequisicaoServico={RequestId}",
            _serviceLabel, capability, f.Outcome, f.Diagnostic.Kind, f.Diagnostic.Stage, f.Diagnostic.DurationMs,
            f.Diagnostic.HttpStatus, f.Diagnostic.Code ?? "n/a", f.Diagnostic.Endpoint ?? "n/a", CorrelationId,
            f.Diagnostic.ServiceRequestId ?? "n/a");

    /// <summary>Desfecho e motivo CONTROLADO de uma falha HTTP sanitizada.</summary>
    public static (KnightCapabilityOutcome Outcome, string Reason) Classify(EntraGraphException ex, string serviceLabel)
    {
        var f = KnightFailureClassifier.FromHttp(ex, serviceLabel, "read");
        return (f.Outcome, f.Reason);
    }

    /// <summary>Resultado da coleta a partir do que foi registrado.</summary>
    public KnightCollectionResult Result(KnightSourceType source, string label, DateTimeOffset collectedAt, string? detail = null)
    {
        var state = DeriveState(Capabilities);
        return new KnightCollectionResult(source, state, label, KnightFactSet.Empty, Capabilities.ToList(), collectedAt,
            detail ?? (state == KnightSourceState.Completed ? null : "Coleta parcial: ver o desfecho de cada leitura."),
            TenantConfiguration: new KnightTenantConfiguration(Documents, Capabilities));
    }

    /// <summary>Falha ANTES de qualquer leitura (token, adaptador): todas as capacidades ficam com o mesmo motivo.</summary>
    public static KnightCollectionResult Failure(
        KnightSourceType source, string label, IEnumerable<KnightCapability> capabilities,
        KnightSourceState state, KnightCapabilityOutcome outcome, string reason, DateTimeOffset at,
        KnightCapabilityDiagnostic? diagnostic = null)
    {
        var caps = capabilities.Select(c => new KnightCapabilityStatus(c, outcome, reason, diagnostic)).ToList();
        return new KnightCollectionResult(source, state, label, KnightFactSet.Empty, caps, at, reason,
            TenantConfiguration: new KnightTenantConfiguration(Array.Empty<KnightConfigurationDocument>(), caps));
    }

    public static KnightSourceState StateFor(EntraGraphErrorKind kind) => kind switch
    {
        EntraGraphErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
        EntraGraphErrorKind.Throttled => KnightSourceState.Throttled,
        EntraGraphErrorKind.InsufficientPermission => KnightSourceState.InsufficientPermission,
        EntraGraphErrorKind.Contract or EntraGraphErrorKind.Incomplete => KnightSourceState.Error,
        _ => KnightSourceState.Unavailable,
    };

    /// <summary>[AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Estado da fonte a partir do desfecho de uma falha classificada.</summary>
    public static KnightSourceState StateFor(KnightCapabilityOutcome outcome) => outcome switch
    {
        KnightCapabilityOutcome.AuthenticationFailure => KnightSourceState.AuthenticationFailure,
        KnightCapabilityOutcome.Throttled => KnightSourceState.Throttled,
        KnightCapabilityOutcome.InsufficientPermission => KnightSourceState.InsufficientPermission,
        KnightCapabilityOutcome.Error => KnightSourceState.Error,
        _ => KnightSourceState.Unavailable,
    };

    public static KnightCapabilityOutcome OutcomeFor(KnightSourceState state) => state switch
    {
        KnightSourceState.AuthenticationFailure => KnightCapabilityOutcome.AuthenticationFailure,
        KnightSourceState.Throttled => KnightCapabilityOutcome.Throttled,
        KnightSourceState.InsufficientPermission => KnightCapabilityOutcome.InsufficientPermission,
        KnightSourceState.Error => KnightCapabilityOutcome.Error,
        _ => KnightCapabilityOutcome.Unavailable,
    };

    /// <summary>A MESMA derivação dos coletores do Teams e do Exchange (ver <c>TeamsKnightCollector.DeriveState</c>).</summary>
    public static KnightSourceState DeriveState(IReadOnlyList<KnightCapabilityStatus> all)
    {
        if (all.Count == 0) return KnightSourceState.Unavailable;
        var collected = all.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        if (collected == all.Count) return KnightSourceState.Completed;
        if (collected > 0) return KnightSourceState.PartialCollection;

        var failed = all.Where(c => c.Outcome != KnightCapabilityOutcome.NotAttempted).ToList();
        if (failed.Count == all.Count)
        {
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission)) return KnightSourceState.InsufficientPermission;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.Throttled)) return KnightSourceState.Throttled;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure)) return KnightSourceState.AuthenticationFailure;
            if (failed.All(c => c.Outcome == KnightCapabilityOutcome.Error)) return KnightSourceState.Error;
        }
        return KnightSourceState.Unavailable;
    }
}
