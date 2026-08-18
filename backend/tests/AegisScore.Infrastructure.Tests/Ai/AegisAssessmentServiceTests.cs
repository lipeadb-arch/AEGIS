using System.Text.Json;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Testes do <see cref="AegisAssessmentService.ChatAsync"/> — o motor de alto nível PROVIDER-NEUTRAL. O
/// transporte é isolado por um <see cref="CapturingLlmClient"/> fake (sem rede, sem tokens): validamos que o
/// serviço (1) traduz a conclusão do LLM na <see cref="AuditorReply"/> certa com a intenção classificada e o
/// seed quando START_INTERVIEW; (2) é RESILIENTE (JSON malformado nunca quebra o chat); e (3) FUNDAMENTA o
/// prompt no contexto tenant-scoped (grounding) sem inventar dados.
/// </summary>
public sealed class AegisAssessmentServiceTests
{
    // ---- Roteamento: START_INTERVIEW ---------------------------------------------

    [Fact]
    public async Task ChatAsync_LlmClassificaStartInterview_MapeiaIntentESemeiaSubcategoria()
    {
        const string pergunta = "Qual a cobertura de MFA para contas privilegiadas hoje?";
        var llm = new CapturingLlmClient(RouterJson("START_INTERVIEW", pergunta, "PR.AA-01"));
        var sut = CreateService(llm);

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
        var llm = new CapturingLlmClient(RouterJson("COPILOT", "PR.AA trata autenticação; PR.DS proteção de dados.", null));
        var sut = CreateService(llm);

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
        var sut = CreateService(new CapturingLlmClient(textoLivre));

        var reply = await sut.ChatAsync(
            new AuditorChatRequest(AuditorScope.Global, Array.Empty<AuditorMessage>(), "e aí?"),
            CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.Copilot, "sem JSON válido cai no fallback resiliente");
        reply.Metadata.Should().BeNull();
        reply.Message.Should().Be(textoLivre, "a conclusão inteira vira a resposta — o chat nunca quebra por formatação");
    }

    // ---- Grounding: o contexto tenant-scoped viaja no prompt ----------------------

    [Fact]
    public async Task ChatAsync_QuandoHaContexto_InjetaDadosDoTenantNoPromptComoFonteUnica()
    {
        var llm = new CapturingLlmClient(RouterJson("COPILOT", "ok", null));
        var sut = CreateService(llm);
        var context = new AuditorTenantContext(
            ScoreState: "Evaluated", ScorePercentage: 62.5, CoveragePercentage: 80,
            CompliantControls: 10, NonCompliantControls: 3, MitigatedControls: 1, NotEvaluatedControls: 5,
            LatestEvidenceAt: null,
            Functions: Array.Empty<AuditorFunctionPosture>(),
            TopGaps: new[] { new AuditorControlGap("GV.SC-01", "NonCompliant", "sem auditoria de terceiros") },
            RecentEvidence: Array.Empty<AuditorDocumentEvidence>(),
            Connectors: new AuditorConnectorContext(2, 2, 1, 0, 1, 0, null),
            PendingRecommendations: Array.Empty<string>());

        await sut.ChatAsync(
            new AuditorChatRequest(AuditorScope.Global, Array.Empty<AuditorMessage>(), "resuma minha postura", context),
            CancellationToken.None);

        // O System Prompt obriga a fundamentação; o User Prompt carrega o contexto serializado do tenant.
        llm.LastSystemPrompt.Should().Contain("SOMENTE os dados do bloco CONTEXTO DO TENANT");
        llm.LastSystemPrompt.Should().Contain("não há dados suficientes");
        llm.LastUserPrompt.Should().Contain("BEGIN_CONTEXT");
        llm.LastUserPrompt.Should().Contain("GV.SC-01", "a lacuna do tenant precisa chegar ao modelo como fato");
    }

    // ---- Modo demonstrativo: contexto de laboratório sintético (SÓ GeminiFreeDemo) ----

    [Fact]
    public async Task EvaluateDocumentControl_NoModoDemo_InjetaContextoDeLaboratorio_MantendoCitacaoLiteral()
    {
        var llm = new CapturingLlmClient(VerdictJson);
        var sut = new AegisAssessmentService(llm, StaticAuditorPersonaProvider.Neutral, GateFor(AiMode.GeminiFreeDemo));

        await sut.EvaluateDocumentControlAsync(SampleControlRequest(), CancellationToken.None);

        llm.LastSystemPrompt.Should().Contain("AUTHORIZED SYNTHETIC LABORATORY",
            "no modo demonstrativo o julgamento sabe que o tenant é um laboratório fictício autorizado");
        llm.LastSystemPrompt.Should().Contain("VERBATIM",
            "a exigência de trecho LITERAL permanece mesmo no modo demonstrativo");
    }

    [Fact]
    public async Task EvaluateDocumentControl_ForaDoModoDemo_NaoInjetaContextoDeLaboratorio()
    {
        var llm = new CapturingLlmClient(VerdictJson);
        var sut = new AegisAssessmentService(llm, StaticAuditorPersonaProvider.Neutral, GateFor(AiMode.Simulated));

        await sut.EvaluateDocumentControlAsync(SampleControlRequest(), CancellationToken.None);

        llm.LastSystemPrompt.Should().NotContain("SYNTHETIC LABORATORY",
            "fora do GeminiFreeDemo a tolerância demonstrativa NUNCA é injetada — produção intacta");
        llm.LastSystemPrompt.Should().Contain("VERBATIM");
    }

    [Fact]
    public async Task AnalyzeDocument_SemGate_NaoInjetaContextoDeLaboratorio()
    {
        // Sem gate (default null) = não demonstrativo — cobre o caso normal/simulado/futuro não demonstrativo.
        var llm = new CapturingLlmClient("""{"summary":"ok","claims":[]}""");
        await CreateService(llm).AnalyzeDocumentAsync(
            new DocumentAnalysisRequest(System.Guid.NewGuid(), "texto", "p.docx"), CancellationToken.None);

        llm.LastSystemPrompt.Should().NotContain("SYNTHETIC LABORATORY");
    }

    // ---- helpers ------------------------------------------------------------------

    private const string VerdictJson = """{"supported":false,"evidenceQuote":"","confidence":0.0,"rationale":"x"}""";

    private static AiFreeTierGate GateFor(AiMode mode) =>
        new(Options.Create(new AiOptions { Mode = mode, ApiKey = "k" }));

    private static DocumentControlEvaluationRequest SampleControlRequest() => new(
        "PR.AA-05", "Identities and credentials are managed.", new[] { "Entra ID: MFA privilegiada" },
        "", "Revisão trimestral de acessos privilegiados no laboratório sintético.", "politica.docx");

    private static AegisAssessmentService CreateService(ILLMClient llm) =>
        new(llm, StaticAuditorPersonaProvider.Neutral);

    private static string RouterJson(string intent, string message, string? targetSubcategoryCode) =>
        JsonSerializer.Serialize(new { intent, message, targetSubcategoryCode });

    /// <summary>ILLMClient fake: devolve um texto fixo e captura os prompts enviados (system/user).</summary>
    private sealed class CapturingLlmClient : ILLMClient
    {
        private readonly string _reply;
        public string LastSystemPrompt { get; private set; } = "";
        public string LastUserPrompt { get; private set; } = "";

        public CapturingLlmClient(string reply) => _reply = reply;

        public Task<string> ExecutePromptAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(_reply);
        }
    }
}
