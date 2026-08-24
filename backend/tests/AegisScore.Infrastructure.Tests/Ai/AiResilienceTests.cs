using System.Net;
using System.Text;
using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Resiliência dos motores de IA (Polly v8 via Microsoft.Extensions.Http.Resilience), exercitada pelo
/// composition root REAL: o pipeline é registrado no <see cref="DependencyInjection"/>, então testá-lo
/// construindo o HttpClient à mão provaria nada — é justamente o registro que precisa estar certo.
///
/// O handler de teste conta as tentativas que CHEGAM na rede. É a única forma de distinguir "o retry
/// funcionou" de "o client engoliu o erro": ambos terminam com a mesma exceção para o chamador.
/// </summary>
public sealed class AiResilienceTests
{
    private const string ValidAnthropicBody =
        """{"content":[{"type":"text","text":"veredito"}],"stop_reason":"end_turn"}""";

    [Fact]
    public async Task Erro429_EhRetentado_ESucedeNaTentativaSeguinte()
    {
        // Rate limit é o caso central: a cota estoura em rajada e volta sozinha em segundos.
        var handler = new SequenceHandler(
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.OK, ValidAnthropicBody));

        var client = ResolveLlmClient(handler);
        var result = await client.ExecutePromptAsync("system", "user");

        result.Should().Be("veredito");
        handler.Attempts.Should().Be(2, "o 429 tem de ser retentado, não propagado ao avaliador");
    }

    [Fact]
    public async Task Erro5xx_EhRetentado()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, ValidAnthropicBody));

        var client = ResolveLlmClient(handler);
        var result = await client.ExecutePromptAsync("system", "user");

        result.Should().Be("veredito");
        handler.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task Erro401_NAO_EhRetentado_FalhaImediatamente()
    {
        // Chave inválida não melhora com insistência: repetir só queima latência e polui o log de cota.
        var handler = new SequenceHandler(
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.OK, ValidAnthropicBody));

        var client = ResolveLlmClient(handler);

        var act = async () => await client.ExecutePromptAsync("system", "user");

        await act.Should().ThrowAsync<AiUnavailableException>();
        handler.Attempts.Should().Be(1, "erro de configuração falha na hora, sem retry");
    }

    [Fact]
    public async Task Erro404_NAO_EhRetentado()
    {
        // Modelo aposentado/inexistente — contrato errado, não falha transitória.
        var handler = new SequenceHandler(
            (HttpStatusCode.NotFound, "{}"),
            (HttpStatusCode.OK, ValidAnthropicBody));

        var client = ResolveLlmClient(handler);

        var act = async () => await client.ExecutePromptAsync("system", "user");

        await act.Should().ThrowAsync<AiUnavailableException>();
        handler.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task RetriesEsgotados_DegradamParaAiUnavailable_NaoParaErroCru()
    {
        // Depois de esgotar as tentativas, o avaliador precisa ver AiUnavailableException (→ 503 e
        // fallback), nunca uma HttpRequestException crua que viraria 500.
        var handler = new SequenceHandler(
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.TooManyRequests, "{}"));

        var client = ResolveLlmClient(handler);

        var act = async () => await client.ExecutePromptAsync("system", "user");

        await act.Should().ThrowAsync<AiUnavailableException>();
        handler.Attempts.Should().Be(4, "1 tentativa original + 3 retries configurados");
    }

    // ---- Timeout injetável (única autoridade) — handler lento + teto curto ----------

    [Fact]
    public async Task TimeoutInjetavelCurto_ComHandlerLento_ProduzAiUnavailable()
    {
        // Autoridade ÚNICA e INJETÁVEL do timeout do pipeline: teto de teste de 50ms + handler que demora 5s
        // → o Polly dispara o timeout, o AnthropicLlmClient traduz para AiUnavailableException — em milissegundos
        // (o teste NÃO espera 120s). O HttpClient tem o timeout nativo desabilitado (Polly é a única autoridade).
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new AiOptions { ApiKey = "chave-de-teste" }));
        services.AddHttpClient<AnthropicLlmClient>(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .AddAiResilience(TimeSpan.FromMilliseconds(50))
            .ConfigurePrimaryHttpMessageHandler(() => new SlowHandler(TimeSpan.FromSeconds(5)));
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<AnthropicLlmClient>();
        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiUnavailableException>();
    }

    // ---- harness -------------------------------------------------------------------

    /// <summary>
    /// Resolve o <see cref="AnthropicLlmClient"/> do container REAL, com a chave presente (para o guard passar)
    /// e o handler de teste no lugar do transporte. O pipeline de resiliência vem do registro de produção —
    /// reaproveita-se o MESMO named client "AnthropicLlmClient" (que já tem AddAiResilience), trocando só o
    /// primary handler pelo roteiro de teste. Testa-se o transporte real, isolado do roteador do gate.
    /// </summary>
    private static ILLMClient ResolveLlmClient(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AegisScore"] = "Host=localhost;Database=aegis_test;Username=t;Password=t",
            ["Ai:Mode"] = "ExternalDemo",
            ["Ai:ApiKey"] = "chave-de-teste",
        }).Build();

        var services = new ServiceCollection();
        services.AddAegisScoreInfrastructure(config);
        services.AddHttpClient<AnthropicLlmClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<AnthropicLlmClient>();
    }

    /// <summary>Devolve as respostas na ordem dada e conta quantas tentativas chegaram até aqui.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _responses;
        private int _index;

        public int Attempts => _index;

        public SequenceHandler(params (HttpStatusCode, string)[] responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, body) = _index < _responses.Length
                ? _responses[_index]
                : _responses[^1];   // além do roteiro, repete a última — evita IndexOutOfRange mascarar o teste
            _index++;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Handler LENTO: espera o delay (cancelável pelo timeout do Polly) antes de responder 200.</summary>
    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public SlowHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidAnthropicBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
