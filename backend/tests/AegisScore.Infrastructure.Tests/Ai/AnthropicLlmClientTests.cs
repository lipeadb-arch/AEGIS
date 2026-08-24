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
/// Testes do <see cref="AnthropicLlmClient"/> — o transporte HTTP real do seam ILLMClient sobre a Anthropic
/// Messages API. Toda a rede é isolada por um <see cref="StubHttpMessageHandler"/> fake; NENHUM teste toca a
/// internet nem consome tokens da Anthropic. Cobre o mapeamento do payload, a extração de <c>content[].text</c>
/// (inclusive múltiplos blocos), a tradução dos desfechos operacionais e a blindagem de segurança da chave.
/// </summary>
public sealed class AnthropicLlmClientTests
{
    private const string Model = "claude-opus-4-8";

    // ---- Payload: model + max_tokens + system + messages[user] --------------------

    [Fact]
    public async Task ExecutePromptAsync_MontaPayloadDaMessagesApi_ComModeloSystemUserEMaxTokens()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AnthropicResponse("ok"));
        var client = CreateClient(handler, model: Model, maxTokens: 512);

        await client.ExecutePromptAsync("SYSTEM-PROMPT", "USER-PROMPT");

        var body = handler.CapturedBody;
        body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be(Model, "o modelo configurado vai no corpo");
        root.GetProperty("max_tokens").GetInt32().Should().Be(512, "o teto de tokens de saída é preservado");
        root.GetProperty("system").GetString().Should().Be("SYSTEM-PROMPT", "o systemPrompt vai no campo top-level system");

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("USER-PROMPT", "o userPrompt vira o conteúdo da mensagem user");
    }

    [Fact]
    public async Task ExecutePromptAsync_NaoEnviaParametrosDeAmostragem()
    {
        // Preserva o comportamento padrão do modelo, como o adaptador anterior: sem temperature/top_p/top_k.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AnthropicResponse("ok"));
        var client = CreateClient(handler);

        await client.ExecutePromptAsync("system", "user");

        var body = handler.CapturedBody;
        body.Should().NotBeNull();
        body!.Should().NotContain("temperature");
        body.Should().NotContain("top_p");
        body.Should().NotContain("top_k");
    }

    // ---- Headers: x-api-key + anthropic-version (chave NUNCA na URL) ---------------

    [Fact]
    public async Task ExecutePromptAsync_EnviaChaveEmXApiKey_EVersaoDaApi_ForaDaUrl()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AnthropicResponse("ok"));
        var client = CreateClient(handler, apiKey: "chave-secreta-123");

        await client.ExecutePromptAsync("system", "user");

        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.GetValues("x-api-key")
            .Should().ContainSingle().Which.Should().Be("chave-secreta-123");
        handler.CapturedRequest.Headers.GetValues("anthropic-version")
            .Should().ContainSingle().Which.Should().Be("2023-06-01");

        // Blindagem: a chave viaja em cabeçalho, NUNCA na URL (evita vazar em logs de acesso/proxies).
        handler.CapturedRequest.RequestUri!.AbsoluteUri.Should().NotContain("chave-secreta-123");
        handler.CapturedRequest.RequestUri.Query.Should().BeEmpty();
        handler.CapturedRequest.RequestUri.AbsoluteUri.Should().Be("https://api.anthropic.com/v1/messages");
        handler.CapturedRequest.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    // ---- Extração de content[].text -----------------------------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoRespostaValida_ExtraiTextoDoBlocoText()
    {
        const string vereditoEsperado =
            "{\"status\":\"Compliant\",\"aiEvidence\":\"MFA bloqueou o acesso não autorizado (rule 42).\"}";
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AnthropicResponse(vereditoEsperado));
        var client = CreateClient(handler);

        var resultado = await client.ExecutePromptAsync("system", "user");

        // Transporte puro: devolve o texto BRUTO, sem parsear (o parsing é do AegisAiEvaluatorService).
        resultado.Should().Be(vereditoEsperado);
    }

    [Fact]
    public async Task ExecutePromptAsync_QuandoMultiplosBlocosDeTexto_ConcatenaNaOrdem_IgnorandoNaoTexto()
    {
        // A resposta pode conter VÁRIOS blocos e blocos NÃO-textuais intercalados. O cliente concatena só os
        // type=="text", na ordem, e ignora os demais — nunca presume que content[0] é o único bloco.
        var corpo = JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "text", text = "Parte 1. " },
                new { type = "tool_use", id = "tu_1", name = "x", input = new { } },
                new { type = "text", text = "Parte 2." },
            },
            stop_reason = "end_turn",
        });
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, corpo);
        var client = CreateClient(handler);

        var resultado = await client.ExecutePromptAsync("system", "user");

        resultado.Should().Be("Parte 1. Parte 2.", "os blocos de texto são concatenados na ordem, sem o bloco tool_use");
    }

    // ---- Ausência de texto avaliável → AiUnavailableException ----------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoRespostaSemBlocoDeTexto_LancaAiUnavailable()
    {
        // Só um bloco não-textual (ex.: tool_use) e stop_reason — sem texto avaliável para o consumidor.
        var corpo = JsonSerializer.Serialize(new
        {
            content = new object[] { new { type = "tool_use", id = "tu_1", name = "x", input = new { } } },
            stop_reason = "tool_use",
        });
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, corpo);
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        // Falha controlada (não NullReference) e carrega a razão de parada para diagnóstico.
        await acao.Should().ThrowAsync<AiUnavailableException>().WithMessage("*tool_use*");
    }

    [Fact]
    public async Task ExecutePromptAsync_QuandoContentVazio_LancaAiUnavailable()
    {
        var corpo = JsonSerializer.Serialize(new { content = Array.Empty<object>(), stop_reason = "max_tokens" });
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, corpo);
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiUnavailableException>().WithMessage("*max_tokens*");
    }

    [Fact]
    public async Task ExecutePromptAsync_QuandoBlocoDeTextoVazio_LancaAiUnavailable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AnthropicResponse(""));
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiUnavailableException>("texto vazio não é conteúdo avaliável");
    }

    // ---- Cota esgotada (429) → AiQuotaExhaustedException ---------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoQuota429_LancaAiQuotaExhausted()
    {
        // 429 é a cota/rate limit esgotada: caso DISTINTO, com mensagem própria para a UI ("cota esgotada"),
        // e ainda subtipo de AiUnavailableException (→ 503 no middleware). Os retries transitórios já ocorreram.
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{\"type\":\"error\"}");
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiQuotaExhaustedException>();
    }

    // ---- Demais não-2xx → AiUnavailableException (401/403/5xx/400/404) -------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]        // chave inválida
    [InlineData(HttpStatusCode.Forbidden)]           // sem permissão para o modelo
    [InlineData(HttpStatusCode.NotFound)]            // modelo aposentado/inexistente
    [InlineData(HttpStatusCode.InternalServerError)] // 5xx do provedor
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ExecutePromptAsync_QuandoStatusDeErro_LancaAiUnavailable(HttpStatusCode status)
    {
        // Qualquer não-2xx é indisponibilidade do motor sob a ótica do consumidor: vira AiUnavailableException
        // (→ 503 no middleware), NÃO a HttpRequestException crua do EnsureSuccessStatusCode (→ 500 opaco). A
        // mensagem preserva o status para diagnóstico e NÃO inclui o corpo (que pode conter dados do prompt).
        var handler = new StubHttpMessageHandler(status, "{\"error\":\"boom\"}");
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        (await acao.Should().ThrowAsync<AiUnavailableException>())
            .Which.Message.Should().Contain(((int)status).ToString(), "o status HTTP fica na mensagem para diagnóstico");
    }

    [Fact]
    public async Task ExecutePromptAsync_Status401_NaoEhCotaExhausted()
    {
        // 401 é erro de configuração, não cota: precisa ser o AiUnavailableException BASE, não a subclasse de cota.
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "{}");
        var client = CreateClient(handler);

        var ex = (await client.Invoking(c => c.ExecutePromptAsync("system", "user"))
            .Should().ThrowAsync<AiUnavailableException>()).Which;
        ex.Should().NotBeOfType<AiQuotaExhaustedException>();
    }

    [Fact]
    public async Task ExecutePromptAsync_NaoRegistraCorpoDoProvedorNaExcecao()
    {
        // A resposta bruta pode conter eco do prompt/documento: a exceção NUNCA a inclui.
        var handler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"segredo-do-corpo-abc\"}}");
        var client = CreateClient(handler);

        var ex = (await client.Invoking(c => c.ExecutePromptAsync("system", "user"))
            .Should().ThrowAsync<AiUnavailableException>()).Which;
        ex.Message.Should().NotContain("segredo-do-corpo-abc");
    }

    // ---- Guard-clause: sem chave, sem rede ----------------------------------------

    [Fact]
    public async Task ExecutePromptAsync_SemApiKey_LancaAiUnavailableSemChamarRede()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, AnthropicResponse("ok"));
        var client = CreateClient(handler, apiKey: "");

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<AiUnavailableException>();
        handler.CapturedRequest.Should().BeNull("a guard-clause deve barrar antes de qualquer chamada HTTP");
    }

    // ---- Falha de transporte → AiUnavailableException -----------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoFalhaDeTransporte_TraduzParaAiUnavailable()
    {
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException("DNS/conexão/TLS")));
        var client = new AnthropicLlmClient(http, Options.Create(new AiOptions { ApiKey = "test-key" }));

        var ex = (await client.Invoking(c => c.ExecutePromptAsync("system", "user"))
            .Should().ThrowAsync<AiUnavailableException>()).Which;
        ex.Message.Should().Contain("Falha de transporte");
    }

    // ---- Timeout por tentativa (Polly) traduzido ----------------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoTimeoutRejected_TraduzParaAiUnavailable_NaoCota()
    {
        // O timeout por tentativa do Polly surge como TimeoutRejectedException. O client a traduz para
        // AiUnavailableException (categoria CONHECIDA que o frontend já traduz), NÃO para a subclasse de cota.
        var http = new HttpClient(new ThrowingHandler(new Polly.Timeout.TimeoutRejectedException("por tentativa")));
        var client = new AnthropicLlmClient(http, Options.Create(new AiOptions { ApiKey = "test-key" }));

        var ex = (await client.Invoking(c => c.ExecutePromptAsync("system", "user"))
            .Should().ThrowAsync<AiUnavailableException>()).Which;
        ex.GetType().Should().Be(typeof(AiUnavailableException), "timeout NÃO é cota (AiQuotaExhaustedException)");
        ex.Message.Should().Contain("Timeout ao aguardar resposta do motor de IA");
    }

    [Fact]
    public async Task ExecutePromptAsync_QuandoTimeoutDoHttpClient_TraduzParaAiUnavailable()
    {
        // Timeout NATIVO do HttpClient (TaskCanceledException SEM cancelamento do chamador) — transitório.
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout do HttpClient")));
        var client = new AnthropicLlmClient(http, Options.Create(new AiOptions { ApiKey = "test-key" }));

        await client.Invoking(c => c.ExecutePromptAsync("system", "user"))
            .Should().ThrowAsync<AiUnavailableException>();
    }

    // ---- Cancelamento do chamador continua cancelamento ---------------------------

    [Fact]
    public async Task ExecutePromptAsync_QuandoChamadorCancela_PropagaCancelamento_NaoIndisponibilidade()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("cancelado pelo chamador")));
        var client = new AnthropicLlmClient(http, Options.Create(new AiOptions { ApiKey = "test-key" }));

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

    private static AnthropicLlmClient CreateClient(
        StubHttpMessageHandler handler, string apiKey = "test-key", string model = Model, int maxTokens = 4096)
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new AiOptions { ApiKey = apiKey, Model = model, MaxOutputTokens = maxTokens });
        return new AnthropicLlmClient(http, options);
    }

    /// <summary>Monta um corpo /v1/messages válido com um único bloco de texto (já escapado corretamente).</summary>
    private static string AnthropicResponse(string text) => JsonSerializer.Serialize(new
    {
        id = "msg_123",
        type = "message",
        role = "assistant",
        model = Model,
        content = new[] { new { type = "text", text } },
        stop_reason = "end_turn",
    });

    /// <summary>
    /// <see cref="HttpMessageHandler"/> fake: responde um status + corpo fixos e captura a request para
    /// inspeção (URL/headers/corpo). Substitui um mock — mais limpo que interceptar o protected SendAsync.
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
            // Captura o corpo AQUI (antes de o client dispor a request) para inspeção do payload.
            CapturedBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
