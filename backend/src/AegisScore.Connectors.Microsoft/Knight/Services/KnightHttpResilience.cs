using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Limites de tempo e de repetição dos clientes HTTP do KNIGHT (Graph, tokens, Resource
/// Manager, Fabric, Key Vault). Configuração do AMBIENTE (<c>Knight:Http</c>), nunca do locatário.
///
/// <para><b>Por que não os padrões do <c>AddStandardResilienceHandler</c>.</b> Os padrões (10 s por tentativa, 30 s no total,
/// circuito aberto com 10% de falhas em 100 chamadas, contando 429) foram pensados para chamadas interativas entre serviços. Numa
/// coleta em lote eles produziam três defeitos concretos: (1) um 429 com <c>Retry-After</c> acima de ~25 s estourava os 30 s e
/// virava <c>TimeoutRejectedException</c>; (2) a leitura de MFA por usuário (uma chamada por conta) limitada pelo serviço contava
/// cada 429 como falha do circuito e o abria para TODAS as leituras seguintes do mesmo cliente; (3) o tempo nativo do
/// <c>HttpClient</c> (100 s) envolvia o pipeline inteiro e podia cortar as novas tentativas no meio.</para>
///
/// <para><b>O que mudou, e só isso:</b> 30 s por tentativa e 120 s no total (cabem duas esperas de <c>Retry-After</c> sem
/// repetição infinita — continuam 3 novas tentativas); o circuito ignora o 429 (o limite de taxa é atendido pelo
/// <c>Retry-After</c>, não por recusar todas as leituras) e só abre com metade das chamadas falhando numa amostra mínima; o
/// <c>Polly</c> é a única autoridade de tempo (o tempo nativo do <c>HttpClient</c> é desligado).</para>
/// </summary>
public sealed class KnightHttpOptions
{
    public const string Section = "Knight:Http";

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Precisa ser ao menos o dobro do tempo por tentativa (exigência do validador da fachada).</summary>
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(60);

    public int CircuitMinimumThroughput { get; set; } = 20;

    public double CircuitFailureRatio { get; set; } = 0.5;

    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

public static class KnightHttpResilience
{
    /// <summary>Anexa o pipeline do KNIGHT ao cliente (substitui o uso direto dos padrões da fachada).</summary>
    public static IHttpClientBuilder AddKnightResilience(this IHttpClientBuilder builder, KnightHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // O Polly é a ÚNICA autoridade de tempo: o tempo nativo do HttpClient envolve o pipeline inteiro (inclusive as
        // novas tentativas) e cortaria a coleta com uma TaskCanceledException sem relação com a tentativa em curso.
        builder.ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        builder.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = options.AttemptTimeout;
            o.TotalRequestTimeout.Timeout = options.TotalRequestTimeout;
            o.Retry.MaxRetryAttempts = options.MaxRetryAttempts;
            o.Retry.Delay = options.RetryBaseDelay;
            o.CircuitBreaker.SamplingDuration = options.CircuitSamplingDuration;
            o.CircuitBreaker.MinimumThroughput = options.CircuitMinimumThroughput;
            o.CircuitBreaker.FailureRatio = options.CircuitFailureRatio;
            o.CircuitBreaker.BreakDuration = options.CircuitBreakDuration;
            o.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(CountsForCircuit(args.Outcome));
        });
        return builder;
    }

    /// <summary>
    /// O que conta como falha do CIRCUITO: as falhas transitórias da fachada (5xx, 408, transporte, tempo esgotado), MENOS o
    /// 429. Limite de taxa diz "espere", não "o serviço caiu" — e é tratado pelo <c>Retry-After</c> da repetição.
    /// </summary>
    internal static bool CountsForCircuit(Outcome<HttpResponseMessage> outcome) =>
        outcome.Result is not { StatusCode: HttpStatusCode.TooManyRequests }
        && HttpClientResiliencePredicates.IsTransient(outcome);
}
