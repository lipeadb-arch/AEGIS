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
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ExecutePromptAsync_QuandoStatusDeErro_EnsureSuccessEstoura(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(status, "{\"error\":\"boom\"}");
        var client = CreateClient(handler);

        var acao = () => client.ExecutePromptAsync("system", "user");

        await acao.Should().ThrowAsync<HttpRequestException>();
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

    // ---- helpers ------------------------------------------------------------------

    private static GeminiLlmClient CreateClient(StubHttpMessageHandler handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new AegisAiOptions { ApiKey = apiKey });
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
