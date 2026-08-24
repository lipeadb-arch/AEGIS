using System.Net;
using System.Text;
using System.Text.Json;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Roteadores tenant-scoped (a fronteira de dados na prática): provam a SELEÇÃO Anthropic × Stub por tenant e a
/// invariante crítica de segurança — um tenant fora da allowlist NUNCA chega a fazer uma chamada HTTP externa.
/// Sem rede real: o motor externo é um <see cref="AnthropicLlmClient"/> sobre um handler-espião.
/// </summary>
public sealed class TenantScopedAiRouterTests
{
    private const string Sandbox = "sandbox-lab";

    // ---- ILLMClient router --------------------------------------------------------

    [Fact]
    public async Task LlmRouter_TenantAllowlisted_UsaAnthropic()
    {
        var handler = new SpyHandler(HttpStatusCode.OK, AnthropicResponse("resposta-real"));
        var router = LlmRouter(Sandbox, handler, allow: Sandbox);

        var result = await router.ExecutePromptAsync("system", "user");

        handler.Called.Should().BeTrue("o tenant da allowlist chega ao motor externo");
        result.Should().Be("resposta-real");
    }

    [Fact]
    public async Task LlmRouter_TenantForaDaAllowlist_UsaStub_NUNCAChamaHttp()
    {
        var handler = new SpyHandler(HttpStatusCode.OK, AnthropicResponse("nao-deveria-vir"));
        var router = LlmRouter("tenant-corporativo", handler, allow: Sandbox);

        // Cabeçalho SUBCATEGORY para o stub determinístico ancorar; o resultado é local, sem rede.
        var result = await router.ExecutePromptAsync("system", "SUBCATEGORY: PR.AA-01\ntelemetria");

        handler.Called.Should().BeFalse("tenant fora da allowlist NUNCA faz chamada HTTP externa");
        result.Should().NotBeNullOrEmpty("o stub responde localmente");
    }

    [Fact]
    public async Task LlmRouter_SemChave_UsaStub_MesmoNaAllowlist()
    {
        var handler = new SpyHandler(HttpStatusCode.OK, AnthropicResponse("x"));
        var router = LlmRouter(Sandbox, handler, allow: Sandbox, apiKey: "");

        await router.ExecutePromptAsync("system", "SUBCATEGORY: PR.AA-01");

        handler.Called.Should().BeFalse("sem chave o provedor não está apto — sempre stub");
    }

    // ---- IAiAssessmentService router ---------------------------------------------

    [Fact]
    public async Task AssessmentRouter_TenantForaDaAllowlist_UsaStub()
    {
        var handler = new SpyHandler(HttpStatusCode.OK, AnthropicResponse("{}"));
        var router = AssessmentRouter("tenant-corporativo", handler, allow: Sandbox);

        var analysis = await router.AnalyzeDocumentAsync(
            new DocumentAnalysisRequest(System.Guid.NewGuid(), "texto sintético", "politica.pdf"), default);

        handler.Called.Should().BeFalse("o motor de alto nível também respeita o gate");
        analysis.Summary.Should().Contain("Simulado", "o stub canned responde localmente");
    }

    [Fact]
    public async Task AssessmentRouter_TenantAllowlisted_UsaAnthropic()
    {
        var handler = new SpyHandler(HttpStatusCode.OK, AnthropicResponse("""{"summary":"real","claims":[]}"""));
        var router = AssessmentRouter(Sandbox, handler, allow: Sandbox);

        var analysis = await router.AnalyzeDocumentAsync(
            new DocumentAnalysisRequest(System.Guid.NewGuid(), "texto sintético", "politica.pdf"), default);

        handler.Called.Should().BeTrue();
        analysis.Summary.Should().Be("real");
    }

    // ---- helpers ------------------------------------------------------------------

    private static TenantScopedLlmRouter LlmRouter(string slug, SpyHandler handler, string allow, string apiKey = "chave") =>
        new(Anthropic(handler, apiKey), new StubLlmClient(), GateFor(apiKey, allow), new FakeResolver(slug));

    private static TenantScopedAssessmentRouter AssessmentRouter(string slug, SpyHandler handler, string allow, string apiKey = "chave") =>
        new(
            new AegisAssessmentService(Anthropic(handler, apiKey), StaticAuditorPersonaProvider.Neutral),
            new StubAssessmentService(),
            GateFor(apiKey, allow),
            new FakeResolver(slug));

    private static AiFreeTierGate GateFor(string apiKey, string allow) =>
        new(Options.Create(new AiOptions
        {
            Mode = AiMode.ExternalDemo,
            ApiKey = apiKey,
            FreeTier = new AiFreeTierOptions { AllowedTenantSlugs = { allow } },
        }));

    private static AnthropicLlmClient Anthropic(SpyHandler handler, string apiKey) =>
        new(new HttpClient(handler), Options.Create(new AiOptions { ApiKey = apiKey }));

    /// <summary>Monta um corpo /v1/messages válido com o texto informado num único bloco content[].text.</summary>
    private static string AnthropicResponse(string text) => JsonSerializer.Serialize(new
    {
        content = new[] { new { type = "text", text } },
        stop_reason = "end_turn",
    });

    /// <summary>Resolver fake: devolve um slug fixo (o tenant "vigente" do teste).</summary>
    private sealed class FakeResolver : IAiTenantResolver
    {
        private readonly string? _slug;
        public FakeResolver(string? slug) => _slug = slug;
        public void OverrideTenant(System.Guid tenantId) { }
        public Task<string?> GetCurrentSlugAsync(CancellationToken ct = default) => Task.FromResult(_slug);
    }

    /// <summary>Handler-espião: registra se foi chamado e devolve um corpo fixo.</summary>
    private sealed class SpyHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public bool Called { get; private set; }

        public SpyHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
