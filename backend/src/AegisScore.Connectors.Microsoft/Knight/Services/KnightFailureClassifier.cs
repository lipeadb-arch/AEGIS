using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using AegisScore.Application.Knight;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

// ============================================================================
//  [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Classificação ÚNICA das falhas de leitura dos coletores Microsoft
// ============================================================================
// Antes, cada coletor reconhecia uma parte das exceções: o pipeline de resiliência (AddStandardResilienceHandler) lança
// TimeoutRejectedException quando estoura o tempo da tentativa ou o total e BrokenCircuitException quando o circuito está
// aberto — e nenhum dos dois era reconhecido. Os dois caíam no "erro inesperado", atribuído ao provedor, e um circuito
// aberto por uma leitura derrubava em sequência as leituras seguintes do mesmo cliente com a mesma mensagem genérica.
//
// Regras desta classificação:
//   • cancelamento pedido pelo CHAMADOR não passa por aqui — quem chama propaga (IsCallerCancellation);
//   • tempo esgotado, circuito aberto e falha de transporte são INDISPONIBILIDADE, cada uma com o próprio tipo técnico;
//   • 401, 403, 429 e falha do serviço preservam a distinção que o status HTTP dá; licença só quando o código a comprova;
//   • resposta fora do contrato é CONTRATO, não indisponibilidade;
//   • qualquer outra exceção é defeito INTERNO do AEGIS — nunca atribuído à Microsoft, à rede ou à permissão.
// O motivo é montado aqui (vocabulário fechado + status/código/endpoint já sanitizados); nunca o texto da exceção.

/// <summary>Uma falha de leitura classificada: desfecho, motivo controlado e diagnóstico técnico seguro.</summary>
public sealed record KnightFailure(KnightCapabilityOutcome Outcome, string Reason, KnightCapabilityDiagnostic Diagnostic);

public static class KnightFailureClassifier
{
    /// <summary>O cancelamento é do chamador (propaga) — e não um tempo esgotado interno, que é falha da leitura.</summary>
    public static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    /// <summary>
    /// Classifica uma exceção de leitura. <paramref name="serviceLabel"/> prefixa o motivo ("Microsoft Graph: …");
    /// <paramref name="stage"/> e <paramref name="durationMs"/> entram só no diagnóstico.
    /// </summary>
    public static KnightFailure Classify(
        Exception ex, string serviceLabel, string stage, long? durationMs = null, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            EntraGraphException g => FromHttp(g, serviceLabel, stage, durationMs, correlationId),

            // O tempo da tentativa ou o total do pipeline de resiliência estourou.
            TimeoutRejectedException => Failure(KnightCapabilityOutcome.Unavailable, KnightFailureKind.Timeout,
                $"{serviceLabel}: tempo esgotado nesta leitura (limite por tentativa ou total do cliente HTTP, já com as novas tentativas).",
                stage, durationMs, correlationId, code: "TimeoutRejected"),

            // Circuito aberto (inclui o isolado): a leitura nem foi enviada.
            BrokenCircuitException => Failure(KnightCapabilityOutcome.Unavailable, KnightFailureKind.CircuitOpen,
                $"{serviceLabel}: leitura não enviada — o circuito do cliente HTTP estava aberto por falhas recentes do serviço. "
                + "Sincronizar novamente depois de alguns minutos.",
                stage, durationMs, correlationId, code: "CircuitOpen"),

            RateLimiterRejectedException => Failure(KnightCapabilityOutcome.Throttled, KnightFailureKind.Timeout,
                $"{serviceLabel}: limite de taxa local do cliente HTTP atingido nesta leitura.", stage, durationMs, correlationId,
                code: "RateLimiterRejected"),

            HttpRequestException h => Failure(KnightCapabilityOutcome.Unavailable, KnightFailureKind.Transport,
                $"{serviceLabel}: falha de rede ou de transporte nesta leitura, antes de haver resposta do serviço.",
                stage, durationMs, correlationId, code: h.HttpRequestError.ToString(), http: h.StatusCode is { } sc ? (int)sc : null),

            // Cancelamento que NÃO veio do chamador: tempo limite do HttpClient ou de um orçamento interno.
            OperationCanceledException or TimeoutException => Failure(KnightCapabilityOutcome.Unavailable, KnightFailureKind.Timeout,
                $"{serviceLabel}: tempo esgotado nesta leitura.", stage, durationMs, correlationId, code: ex.GetType().Name),

            IOException => Failure(KnightCapabilityOutcome.Unavailable, KnightFailureKind.Transport,
                $"{serviceLabel}: a conexão foi interrompida durante a leitura.", stage, durationMs, correlationId, code: ex.GetType().Name),

            JsonException => Failure(KnightCapabilityOutcome.Error, KnightFailureKind.Contract,
                $"{serviceLabel}: a resposta não pôde ser interpretada pelo contrato desta leitura.", stage, durationMs, correlationId,
                code: "JsonException"),

            _ => Failure(KnightCapabilityOutcome.Error, KnightFailureKind.Internal,
                $"{serviceLabel}: falha interna do AEGIS ao processar esta leitura ({SafeTypeName(ex)}). Não é atribuída ao provedor, "
                + "à rede nem à permissão.",
                stage, durationMs, correlationId, code: SafeTypeName(ex)),
        };
    }

    /// <summary>Falha HTTP já sanitizada pelo transporte: status, código curto, endpoint e id da requisição.</summary>
    public static KnightFailure FromHttp(
        EntraGraphException ex, string serviceLabel, string stage, long? durationMs = null, string? correlationId = null)
    {
        // [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Recusa no ENDPOINT DE TOKEN: a etapa é a emissão do token, não a leitura.
        var tokenStage = ex.EndpointPath == "/oauth2/v2.0/token";
        var (outcome, kind, head) = ex.Kind switch
        {
            _ when tokenStage && ex.Kind is EntraGraphErrorKind.AuthFailure or EntraGraphErrorKind.InsufficientPermission =>
                (ex.Kind == EntraGraphErrorKind.AuthFailure ? KnightCapabilityOutcome.AuthenticationFailure : KnightCapabilityOutcome.InsufficientPermission,
                 KnightFailureKind.HttpStatus,
                 $"O Microsoft Entra ID recusou a emissão do token{(ex.TokenResource is { } r ? " de " + r : "")}; nenhuma leitura foi enviada."),
            _ when MentionsLicense(ex.GraphErrorCode) =>
                (KnightCapabilityOutcome.LimitedByLicense, KnightFailureKind.HttpStatus, "O locatário não tem a licença que este recurso exige."),
            EntraGraphErrorKind.InsufficientPermission =>
                (KnightCapabilityOutcome.InsufficientPermission, KnightFailureKind.HttpStatus, "Autorização recusada para esta leitura."),
            EntraGraphErrorKind.Throttled =>
                (KnightCapabilityOutcome.Throttled, KnightFailureKind.HttpStatus, "Limite de taxa atingido nesta leitura."),
            EntraGraphErrorKind.AuthFailure =>
                (KnightCapabilityOutcome.AuthenticationFailure, KnightFailureKind.HttpStatus, "Falha de autenticação nesta leitura."),
            EntraGraphErrorKind.Contract =>
                (KnightCapabilityOutcome.Error, KnightFailureKind.Contract, string.IsNullOrWhiteSpace(ex.ContractDetail)
                    ? "A resposta não segue o contrato documentado desta leitura."
                    : $"A resposta não segue o contrato documentado desta leitura: {ex.ContractDetail}."),
            EntraGraphErrorKind.Incomplete =>
                (KnightCapabilityOutcome.Error, KnightFailureKind.Incomplete, "Leitura interrompida no teto de paginação do AEGIS com páginas restantes — o que foi lido não é o total."),
            _ when ex.HttpStatusCode is null =>
                (KnightCapabilityOutcome.Unavailable, KnightFailureKind.Transport, "Serviço indisponível para esta leitura."),
            _ => (KnightCapabilityOutcome.Unavailable, KnightFailureKind.HttpStatus, "Serviço indisponível para esta leitura."),
        };

        var parts = new List<string>();
        if (ex.HttpStatusCode is { } status) parts.Add($"HTTP {status}");
        if (!string.IsNullOrWhiteSpace(ex.GraphErrorCode)) parts.Add($"código: {ex.GraphErrorCode}");
        if (ex.OAuthErrorCodes.Count > 0) parts.Add(string.Join(", ", ex.OAuthErrorCodes));
        if (!string.IsNullOrWhiteSpace(ex.EndpointPath)) parts.Add($"endpoint: {ex.EndpointPath}");
        // Identificadores do serviço: só GUIDs, para o suporte da Microsoft localizar a requisição.
        if (ex.ServiceRequestId is { } rid) parts.Add($"requisição: {rid}");
        if (ex.ServiceCorrelationId is { } cid) parts.Add($"correlação: {cid}");
        var reason = $"{serviceLabel}: {head}" + (parts.Count == 0 ? "" : " " + string.Join(" · ", parts));
        if (reason.Length > 460) reason = reason[..459] + "…";

        var code = ex.OAuthErrorCodes.Count > 0 ? string.Join("/", new[] { ex.GraphErrorCode }.Concat(ex.OAuthErrorCodes).Where(c => c is not null)) : ex.GraphErrorCode;
        return new KnightFailure(outcome, reason, new KnightCapabilityDiagnostic(
            stage, kind, durationMs, ex.HttpStatusCode, code, ex.EndpointPath, correlationId, ex.ServiceRequestId, ex.ServiceCorrelationId));
    }

    /// <summary>Licença só quando o CÓDIGO do erro a comprova (ex.: AadPremiumLicenseRequired).</summary>
    public static bool MentionsLicense(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && (code.Contains("licen", StringComparison.OrdinalIgnoreCase)
            || code.Contains("NotLicensed", StringComparison.OrdinalIgnoreCase)
            || code.Contains("PremiumLicense", StringComparison.OrdinalIgnoreCase));

    /// <summary>Diagnóstico de uma leitura concluída com limitação (parcial, teto, orçamento) — não é falha.</summary>
    public static KnightCapabilityDiagnostic Incomplete(string stage, long? durationMs, string? correlationId, string? code = null) =>
        new(stage, KnightFailureKind.Incomplete, durationMs, Code: code, CorrelationId: correlationId);

    /// <summary>Nome do tipo da exceção, restrito a letras, dígitos e ponto (vai ao diagnóstico, nunca a mensagem).</summary>
    public static string SafeTypeName(Exception ex)
    {
        var name = ex.GetType().Name;
        var chars = new List<char>(name.Length);
        foreach (var c in name)
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_') chars.Add(c);
        var safe = new string(chars.ToArray());
        return safe.Length == 0 ? "Exception" : safe.Length > 80 ? safe[..80] : safe;
    }

    private static KnightFailure Failure(
        KnightCapabilityOutcome outcome, KnightFailureKind kind, string reason, string stage, long? durationMs, string? correlationId,
        string? code = null, int? http = null) =>
        new(outcome, reason, new KnightCapabilityDiagnostic(stage, kind, durationMs, http, code, null, correlationId));
}

/// <summary>Identificador curto de correlação de UMA execução de coleta (vai ao diagnóstico e aos logs).</summary>
public static class KnightRunCorrelation
{
    public static string New() => Guid.NewGuid().ToString("N")[..16];
}
