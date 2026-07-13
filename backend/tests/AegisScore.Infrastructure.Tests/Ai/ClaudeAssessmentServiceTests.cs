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
/// Testes do <see cref="ClaudeAssessmentService.ChatAsync"/> sob o novo contrato de ROTEAMENTO DE
/// INTENÇÃO. A rede é isolada por um <see cref="StubHttpMessageHandler"/> fake que devolve o envelope
/// da Anthropic Messages API; validamos que o serviço traduz a conclusão do LLM na
/// <see cref="AuditorReply"/> certa — com a <see cref="AuditorReply.Intent"/> classificada e o
/// <see cref="AuditorInterviewSeed"/> quando for START_INTERVIEW — E que o parser é RESILIENTE
/// (JSON malformado nunca quebra o chat). Nenhum teste toca a internet nem consome tokens.
/// </summary>
public sealed class ClaudeAssessmentServiceTests
{
    // ---- Roteamento: START_INTERVIEW ---------------------------------------------

    [Fact]
    public async Task ChatAsync_LlmClassificaStartInterview_MapeiaIntentESemeiaSubcategoria()
    {
        const string pergunta = "Qual a cobertura de MFA para contas privilegiadas hoje?";
        var conclusao = RouterJson("START_INTERVIEW", pergunta, "PR.AA-01");
        var sut = CreateService(AnthropicResponse(conclusao));

        var reply = await sut.ChatAsync(
            new AuditorChatRequest(AuditorScope.Protect, Array.Empty<AuditorMessage>(), "quero auditar"),
            CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.StartInterview);
        reply.Message.Should().Be(pergunta, "em START_INTERVIEW a message JÁ É a 1ª pergunta do fluxo NIST");
        reply.Scope.Should().Be(AuditorScope.Protect, "o escopo da tela ativa é ecoado de volta");
        reply.Metadata.As<AuditorInterviewSeed>().TargetSubcategoryCode.Should().Be("PR.AA-01");
    }

    // ---- Roteamento: COPILOT ------------------------------------------------------

    [Fact]
    public async Task ChatAsync_LlmClassificaCopilot_MapeiaIntentSemMetadata()
    {
        var conclusao = RouterJson("COPILOT", "PR.AA trata autenticação; PR.DS trata proteção de dados.", null);
        var sut = CreateService(AnthropicResponse(conclusao));

        var reply = await sut.ChatAsync(
            new AuditorChatRequest(AuditorScope.Protect, Array.Empty<AuditorMessage>(), "diferença PR.AA x PR.DS"),
            CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.Copilot);
        reply.Metadata.Should().BeNull("COPILOT não carrega seed de entrevista");
        reply.Message.Should().Contain("PR.DS");
    }

    // ---- Resiliência: JSON malformado nunca quebra o chat -------------------------

    [Fact]
    public async Task ChatAsync_QuandoLlmNaoDevolveJson_TrataConclusaoInteiraComoCopilot()
    {
        const string textoLivre = "Claro! Recomendo começar exigindo MFA em todas as contas privilegiadas.";
        var sut = CreateService(AnthropicResponse(textoLivre));

        var reply = await sut.ChatAsync(
            new AuditorChatRequest(AuditorScope.Global, Array.Empty<AuditorMessage>(), "e aí?"),
            CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.Copilot, "sem JSON válido cai no fallback resiliente");
        reply.Metadata.Should().BeNull();
        reply.Message.Should().Be(textoLivre, "a conclusão inteira vira a resposta — o chat nunca quebra por formatação");
    }

    // ---- Hardening: a chave viaja no header, nunca na URL -------------------------

    [Fact]
    public async Task ChatAsync_EnviaChaveNoHeaderAnthropic_ForaDaQueryString()
    {
        var handler = new StubHttpMessageHandler(AnthropicResponse(RouterJson("COPILOT", "ok", null)));
        var sut = CreateService(handler, apiKey: "sk-ant-secreta");

        await sut.ChatAsync(
            new AuditorChatRequest(AuditorScope.Global, Array.Empty<AuditorMessage>(), "oi"),
            CancellationToken.None);

        handler.CapturedRequest.Should().NotBeNull();
        handler.CapturedRequest!.Headers.GetValues("x-api-key")
            .Should().ContainSingle().Which.Should().Be("sk-ant-secreta");
        handler.CapturedRequest.RequestUri!.Query.Should().NotContain("key=", "a chave nunca entra na query string");
    }

    // ---- helpers ------------------------------------------------------------------

    private static ClaudeAssessmentService CreateService(StubHttpMessageHandler handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new AiOptions { ApiKey = apiKey });
        return new ClaudeAssessmentService(http, options);
    }

    private static ClaudeAssessmentService CreateService(string anthropicBody, string apiKey = "test-key")
        => CreateService(new StubHttpMessageHandler(anthropicBody), apiKey);

    /// <summary>JSON do roteador que o System Prompt exige do LLM ({intent, message, targetSubcategoryCode}).</summary>
    private static string RouterJson(string intent, string message, string? targetSubcategoryCode) =>
        JsonSerializer.Serialize(new { intent, message, targetSubcategoryCode });

    /// <summary>Envelope da Anthropic Messages API: content[] com blocos {type:"text", text}.</summary>
    private static string AnthropicResponse(string text) => JsonSerializer.Serialize(new
    {
        content = new[] { new { type = "text", text } }
    });

    /// <summary>
    /// <see cref="HttpMessageHandler"/> fake: devolve um corpo 200 fixo e captura a request para inspeção
    /// (URL/headers). Mesmo idioma do handler de <c>GeminiLlmClientTests</c> — test double à mão, sem
    /// framework de mock.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubHttpMessageHandler(string json) => _json = json;

        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }
}
