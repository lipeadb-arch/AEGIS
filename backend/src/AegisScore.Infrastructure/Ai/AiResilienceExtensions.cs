using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Política de resiliência dos HttpClients dos motores de IA (Gemini e Claude), sob a fachada oficial
/// <c>Microsoft.Extensions.Http.Resilience</c> (Polly v8).
///
/// Vive num handler do pipeline HTTP, e NÃO dentro dos clients, por uma razão concreta: tanto o
/// <see cref="GeminiLlmClient"/> quanto o <see cref="ClaudeAssessmentService"/> traduzem qualquer
/// resposta não-2xx em falha de aplicação (<c>AiUnavailableException</c> / <c>EnsureSuccessStatusCode</c>)
/// no instante em que a veem. Um retry acima deles nunca enxergaria o 429 — só a exceção já mastigada.
/// No handler, a repetição acontece ANTES: o client recebe apenas o desfecho final da tentativa.
/// </summary>
internal static class AiResilienceExtensions
{
    /// <summary>
    /// Anexa retry exponencial + circuit breaker ao client. Os limiares são deliberadamente
    /// conservadores: o Aegis degrada com elegância (triagem cega no worker documental, 503 no avaliador
    /// de telemetria), então insistir demais custa latência e cota sem melhorar a decisão de auditoria.
    /// </summary>
    public static IHttpClientBuilder AddAiResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("aegis-ai", (pipeline, context) =>
        {
            // 1) RETRY com backoff exponencial + jitter.
            //    Jitter não é enfeite: sem ele, uma rajada de documentos que estoure a cota junta bate
            //    de volta na API toda no MESMO instante, e o 429 se perpetua em ondas sincronizadas.
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,   // ~2s, 4s, 8s
                UseJitter = true,
                // O default da fachada já cobre 5xx, 408 e 429, além de falhas de transporte. Explicitado
                // aqui porque é a regra de negócio da tarefa: só retentamos o que é TRANSITÓRIO. 401/403
                // (chave inválida) e 404 (modelo aposentado) não melhoram com insistência — falham na hora.
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException
                    || args.Outcome.Result is { } r && IsTransient(r.StatusCode)),
            });

            // 2) CIRCUIT BREAKER: quando o motor está fora do ar, parar de bater nele.
            //    Sem o disjuntor, cada documento da fila pagaria os 3 retries (≈14s) antes de cair no
            //    fallback — uma fila de 50 documentos levaria ~12 minutos para drenar contra uma API que
            //    já se sabe indisponível. Aberto o circuito, a falha é imediata e o worker degrada na hora.
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,                             // metade das chamadas falhando
                MinimumThroughput = 8,                          // ...com amostra mínima, para não abrir por azar
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(20),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException
                    || args.Outcome.Result is { } r && IsTransient(r.StatusCode)),
            });

            // 3) TIMEOUT por tentativa. Um LLM que pendura a conexão é pior que um que recusa: sem teto,
            //    o worker fica preso e o shutdown gracioso não acontece. Fica DEPOIS do retry no pipeline,
            //    portanto vale para cada tentativa individual, não para o conjunto.
            pipeline.AddTimeout(TimeSpan.FromSeconds(60));
        });

        return builder;
    }

    /// <summary>
    /// Falhas TRANSITÓRIAS — as que uma segunda tentativa pode resolver. 429 é o caso central (cota/rate
    /// limit) e 5xx o outro; 408 é timeout declarado pelo servidor. O resto é erro de configuração ou de
    /// contrato, e repetir só queima cota.
    /// </summary>
    private static bool IsTransient(System.Net.HttpStatusCode status) =>
        status == System.Net.HttpStatusCode.TooManyRequests
        || status == System.Net.HttpStatusCode.RequestTimeout
        || (int)status >= 500;
}
