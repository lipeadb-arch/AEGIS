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

    public KnightCapabilityRecorder(string serviceLabel, ILogger? log = null)
    {
        _serviceLabel = serviceLabel;
        _log = log;
    }

    public List<KnightConfigurationDocument> Documents { get; } = new();

    public List<KnightCapabilityStatus> Capabilities { get; } = new();

    /// <summary>Registra um desfecho sem executar leitura (ex.: capacidade que exige certificado e ele não existe).</summary>
    public void Record(KnightCapability capability, KnightCapabilityOutcome outcome, string? detail = null) =>
        Capabilities.Add(new KnightCapabilityStatus(capability, outcome, detail));

    /// <summary>Executa UMA capacidade; publica os documentos somente se ela concluir.</summary>
    public async Task RunAsync(
        KnightCapability capability, Func<Action<KnightConfigurationDocument>, Task> collect, CancellationToken ct)
    {
        var pending = new List<KnightConfigurationDocument>();
        try
        {
            await collect(pending.Add);
            Documents.AddRange(pending);
            Capabilities.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Collected));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var (outcome, reason) = Classify(ex, _serviceLabel);
            _log?.LogWarning(
                "Falha sanitizada em {Capability}: Outcome={Outcome}, HttpStatus={HttpStatus}, Code={Code}, Endpoint={Endpoint}",
                capability, outcome, ex.HttpStatusCode, ex.GraphErrorCode ?? "n/a", ex.EndpointPath ?? "n/a");
            Capabilities.Add(new KnightCapabilityStatus(capability, outcome, reason));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            _log?.LogWarning("Falha de transporte em {Capability}: {Type}.", capability, ex.GetType().Name);
            Capabilities.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Unavailable,
                $"{_serviceLabel}: falha de rede ou tempo esgotado nesta leitura."));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Falha inesperada ao coletar {Capability}.", capability);
            Capabilities.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Error,
                $"{_serviceLabel}: erro inesperado nesta leitura."));
        }
    }

    /// <summary>Desfecho e motivo CONTROLADO de uma falha HTTP sanitizada.</summary>
    public static (KnightCapabilityOutcome Outcome, string Reason) Classify(EntraGraphException ex, string serviceLabel)
    {
        var (outcome, baseReason) = ex.Kind switch
        {
            _ when MentionsLicense(ex.GraphErrorCode) =>
                (KnightCapabilityOutcome.LimitedByLicense, "O locatário não tem a licença que este recurso exige."),
            EntraGraphErrorKind.InsufficientPermission =>
                (KnightCapabilityOutcome.InsufficientPermission, "Autorização recusada para esta leitura."),
            EntraGraphErrorKind.Throttled =>
                (KnightCapabilityOutcome.Throttled, "Limite de taxa atingido nesta leitura."),
            EntraGraphErrorKind.AuthFailure =>
                (KnightCapabilityOutcome.AuthenticationFailure, "Falha de autenticação nesta leitura."),
            _ => (KnightCapabilityOutcome.Unavailable, "Serviço indisponível para esta leitura."),
        };

        var parts = new List<string>();
        if (ex.HttpStatusCode is { } status) parts.Add($"HTTP {status}");
        if (!string.IsNullOrWhiteSpace(ex.GraphErrorCode)) parts.Add($"código: {ex.GraphErrorCode}");
        if (!string.IsNullOrWhiteSpace(ex.EndpointPath)) parts.Add($"endpoint: {ex.EndpointPath}");
        var reason = $"{serviceLabel}: {baseReason}" + (parts.Count == 0 ? "" : " " + string.Join(" · ", parts));
        return (outcome, reason.Length > 460 ? reason[..459] + "…" : reason);
    }

    private static bool MentionsLicense(string? code) =>
        code is not null && (code.Contains("licen", StringComparison.OrdinalIgnoreCase)
            || code.Contains("NotLicensed", StringComparison.OrdinalIgnoreCase)
            || code.Contains("PremiumLicense", StringComparison.OrdinalIgnoreCase));

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
        KnightSourceState state, KnightCapabilityOutcome outcome, string reason, DateTimeOffset at)
    {
        var caps = capabilities.Select(c => new KnightCapabilityStatus(c, outcome, reason)).ToList();
        return new KnightCollectionResult(source, state, label, KnightFactSet.Empty, caps, at, reason,
            TenantConfiguration: new KnightTenantConfiguration(Array.Empty<KnightConfigurationDocument>(), caps));
    }

    public static KnightSourceState StateFor(EntraGraphErrorKind kind) => kind switch
    {
        EntraGraphErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
        EntraGraphErrorKind.Throttled => KnightSourceState.Throttled,
        EntraGraphErrorKind.InsufficientPermission => KnightSourceState.InsufficientPermission,
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
