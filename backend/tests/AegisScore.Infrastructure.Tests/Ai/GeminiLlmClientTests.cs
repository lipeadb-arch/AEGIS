using System.Net;
using System.Text;
using System.Text.Json;
using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Testes do <see cref="GeminiLlmClient"/> — o transporte HTTP real do seam ILLMClient. Toda a rede é
/// isolada por um <see cref="StubHttpMessageHandler"/> fake; nenhum teste toca a internet nem consome
/// tokens. Cobre os três caminhos do contrato + a blindagem de segurança da chave.
/// </summary>
public sealed class GeminiLlmClientTests
{
    // ---- Cenário 1: caminho feliz -------------------------------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoRespostaValida_ExtraiTextoDoPrimeiroCandidato()
    {
        // O Gemini devolve o veredito do avaliador como texto em candidates[0].content.parts[0].text.
        const string vereditoEsperado =
            "{\"status\":\"Compliant\",\"aiEvidence\":\"MFA bloqueou o acesso não autorizado (rule 42).\"}";
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GeminiResponse(vereditoEsperado));
        var client = CreateClient(handler);

        var resultado = await client.ExecutePromptAsync("system", "user");

        // Transporte puro: devolve o texto BRUTO, sem parsear (o parsing é do AegisAiEvaluatorService).
        resultado.Should().Be(vereditoEsperado);
    }

    [Fact]
    public async Task ExecutePromptAsync_EnviaChaveNoHeader_ForaDaQueryString()
    {
        // Blindagem A (hardening): a API key viaja em x-goog-api-key, nunca na URL (evita vazar em logs).
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GeminiResponse("{}"));
        var client = CreateClient(handler, apiKey: "chave-secreta-123");

        await client.ExecutePromptAsync("system", "user");

        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.GetValues("x-goog-api-key")
            .Should().ContainSingle().Which.Should().Be("chave-secreta-123");
        handler.CapturedRequest.RequestUri!.Query.Should().NotContain("key=");
        handler.CapturedRequest.RequestUri.AbsoluteUri.Should().EndWith(":generateContent");
    }

    // ---- Cenário 2: safety block --------------------------------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoBloqueadoPorSafetyFilter_LancaAiUnavailable()
    {
        // Comportamento real do Gemini: 200 OK, SEM candidatos, com promptFeedback.blockReason.
        var corpo = JsonSerializer.Serialize(new { promptFeedback = new { blockReason = "SAFETY" } });
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, corpo);
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        // Falha controlada (não NullReference) e carrega o motivo do bloqueio para diagnóstico.
        await acao.Should().ThrowAsync<AiUnavailableException>().WithMessage("*SAFETY*");
    }

    // ---- Cenário 3: erro HTTP -----------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]              // modelo aposentado/inexistente (ex.: o antigo gemini-1.5-flash)
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExecutePromptAsync_QuandoStatusDeErro_LancaAiUnavailable(HttpStatusCode status)
    {
        // Qualquer não-2xx do Gemini é indisponibilidade do motor sob a ótica do avaliador: vira
        // AiUnavailableException (→ 503 no middleware), NÃO a HttpRequestException crua do
        // EnsureSuccessStatusCode (que degradaria para um 500 opaco). A mensagem preserva o status.
        var handler = new StubHttpMessageHandler(status, "{\"error\":\"boom\"}");
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        (await acao.Should().ThrowAsync<AiUnavailableException>())
            .Which.Message.Should().Contain(((int)status).ToString(), "o status HTTP fica na mensagem para diagnóstico");
    }

    // ---- Guard-clause: sem chave, sem rede ---------------------------------------

    [Fact]
    public async Task ExecutePromptAsync_SemApiKey_LancaAiUnavailableSemChamarRede()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GeminiResponse("{}"));
        var client = CreateClient(handler, apiKey: "");

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiUnavailableException>();
        handler.CapturedRequest.Should().BeNull("a guard-clause deve barrar antes de qualquer chamada HTTP");
    }

    // ---- Cenário 4: cota gratuita esgotada (429) ---------------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoQuota429_LancaAiQuotaExhausted()
    {
        // 429 RESOURCE_EXHAUSTED é a cota gratuita esgotada: caso DISTINTO, com mensagem própria para a UI
        // ("cota esgotada"), e ainda subtipo de AiUnavailableException (→ 503 no middleware). NÃO há resposta
        // simulada: a telemetria preserva o veredito determinístico e o Auditor/documento informam a cota.
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{\"error\":\"RESOURCE_EXHAUSTED\"}");
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiQuotaExhaustedException>();
    }

    // ---- Cenário 5: generationConfig compatível com Gemini 3.x --------------------

    [Fact]
    public async Task ExecutePromptAsync_GenerationConfig_SoMaxOutputTokens_SemParametrosDeAmostragem()
    {
        // Gemini 3.x recomenda os valores padrão de amostragem: enviamos SÓ maxOutputTokens (teto de cota),
        // sem temperature/topP/topK e sem thinking_budget (preserva o raciocínio padrão, sem inflar a cota).
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, GeminiResponse("ok"));
        var client = CreateClient(handler);

        await client.ExecutePromptAsync("system", "user");

        var body = handler.CapturedBody;
        body.Should().NotBeNull();
        body!.Should().Contain("maxOutputTokens", "o teto de tokens de saída é preservado");
        body.Should().NotContain("temperature");
        body.Should().NotContain("topP");
        body.Should().NotContain("top_p");
        body.Should().NotContain("topK");
        body.Should().NotContain("top_k");
        body.Should().NotContain("thinking", "não se envia thinking_budget");
    }

    // ---- Cenário 6: timeout por tentativa (Polly) traduzido -----------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoTimeoutRejected_TraduzParaAiUnavailable()
    {
        // O timeout por tentativa do Polly surge como TimeoutRejectedException (era a categoria crua
        // `TimeoutRejectedException` persistida no worker). O client a traduz para AiUnavailableException
        // (categoria CONHECIDA que o frontend já traduz), NÃO para a subclasse de cota.
        var http = new HttpClient(new ThrowingHandler(new Polly.Timeout.TimeoutRejectedException("por tentativa")));
        var client = new GeminiLlmClient(http, Options.Create(new AiOptions { ApiKey = "test-key" }));

        var acao = () => client.ExecutePromptAsync("system", "user");

        var ex = (await acao.Should().ThrowAsync<AiUnavailableException>()).Which;
        ex.GetType().Should().Be(typeof(AiUnavailableException), "timeout NÃO é cota (AiQuotaExhaustedException)");
        ex.Message.Should().Contain("Timeout ao aguardar resposta do motor de IA");
    }

    // ---- Cenário 7: cancelamento do chamador continua cancelamento -----------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoChamadorCancela_PropagaCancelamento_NaoIndisponibilidade()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("cancelado pelo chamador")));
        var client = new GeminiLlmClient(http, Options.Create(new AiOptions { ApiKey = "test-key" }));

        var acao = () => client.ExecutePromptAsync("system", "user", cts.Token);

        // Cancelamento do chamador é cancelamento — NUNCA vira AiUnavailableException (guarda !ct.IsCancellationRequested).
        await acao.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>Handler que sempre LANÇA a exceção informada — para exercitar os catches do client.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly System.Exception _ex;
        public ThrowingHandler(System.Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _ex;
    }

    private static GeminiLlmClient CreateClient(StubHttpMessageHandler handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new AiOptions { ApiKey = apiKey });
        return new GeminiLlmClient(http, options);
    }

    /// <summary>Monta um corpo generateContent válido com o texto informado, já escapado corretamente.</summary>
    private static string GeminiResponse(string text) => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { parts = new[] { new { text } } } } }
    });

    /// <summary>
    /// <see cref="HttpMessageHandler"/> fake: responde um status + corpo fixos e captura a request para
    /// inspeção (URL/headers). Substitui um mock — mais limpo que interceptar o protected SendAsync.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;

        public StubHttpMessageHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            // Captura o corpo AQUI (antes de o client dispor a request) para inspeção do generationConfig.
            CapturedBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
