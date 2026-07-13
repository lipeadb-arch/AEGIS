using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Testes do <see cref="StubAssessmentService"/> — o Copiloto GRC determinístico (sem LLM) sob o novo
/// contrato de ROTEAMENTO DE INTENÇÃO (Agentic Routing). Blindam a assinatura
/// <see cref="IAiAssessmentService.ChatAsync"/> → <see cref="AuditorReply"/>: garantem que a
/// <see cref="AuditorReply.Intent"/> e o <see cref="AuditorReply.Metadata"/> saem corretos por
/// palavra-chave e por escopo. Sem rede, sem tokens, sem banco — roteamento puro.
/// </summary>
public sealed class StubAssessmentServiceTests
{
    private readonly StubAssessmentService _sut = new();

    // ---- COPILOT: dúvida geral ----------------------------------------------------

    [Fact]
    public async Task ChatAsync_MensagemGeral_RoteiaComoCopilotSemMetadata()
    {
        var req = new AuditorChatRequest(AuditorScope.Protect, Array.Empty<AuditorMessage>(),
            "Qual a diferença entre PR.AA e PR.DS?");

        var reply = await _sut.ChatAsync(req, CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.Copilot, "sem palavra-chave de auditoria é uma consulta comum");
        reply.Metadata.Should().BeNull("COPILOT não semeia entrevista");
        reply.Scope.Should().Be(AuditorScope.Protect, "o escopo da tela ativa é ecoado de volta");
        reply.Message.Should().NotBeNullOrWhiteSpace();
    }

    // ---- START_INTERVIEW: pedido de auditoria -------------------------------------

    [Theory]
    [InlineData("Quero auditar meus controles")]
    [InlineData("Vamos fazer um diagnóstico de maturidade")]
    [InlineData("Me ajude a fechar as lacunas de conformidade")]
    [InlineData("Preciso mapear meus gaps de segurança")]
    [InlineData("Podemos iniciar a entrevista de governança?")]
    public async Task ChatAsync_PalavraChaveDeAuditoria_RoteiaComoStartInterview(string mensagem)
    {
        var req = new AuditorChatRequest(AuditorScope.Protect, Array.Empty<AuditorMessage>(), mensagem);

        var reply = await _sut.ChatAsync(req, CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.StartInterview,
            "verbos de auditoria/diagnóstico/lacuna disparam o modo entrevista");
        reply.Message.Should().NotBeNullOrWhiteSpace("a resposta JÁ É a 1ª pergunta do fluxo NIST");
    }

    [Fact]
    public async Task ChatAsync_StartInterview_EmiteSeedComSubcategoriaAlvoNoMetadata()
    {
        var req = new AuditorChatRequest(AuditorScope.Protect, Array.Empty<AuditorMessage>(),
            "Quero auditar o pilar de proteção");

        var reply = await _sut.ChatAsync(req, CancellationToken.None);

        reply.Metadata.Should().BeOfType<AuditorInterviewSeed>(
            "a UI precisa do código NIST para entrar no modo entrevista");
        reply.Metadata.As<AuditorInterviewSeed>().TargetSubcategoryCode.Should().Be("PR.AA-01");
    }

    // ---- Roteamento por escopo: a 1ª pergunta muda com a tela ativa ---------------

    [Theory]
    [InlineData(AuditorScope.Protect, "PR.AA-01")]
    [InlineData(AuditorScope.Detect, "DE.CM-01")]
    [InlineData(AuditorScope.Respond, "RS.MA-01")]
    [InlineData(AuditorScope.Recover, "RC.RP-01")]
    [InlineData(AuditorScope.Identify, "ID.AM-01")]
    [InlineData(AuditorScope.Govern, "GV.SC-01")]
    public async Task ChatAsync_StartInterviewPorEscopo_SemeiaSubcategoriaCanonicaDaFuncao(
        AuditorScope scope, string codigoEsperado)
    {
        var req = new AuditorChatRequest(scope, Array.Empty<AuditorMessage>(), "diagnóstico de lacunas");

        var reply = await _sut.ChatAsync(req, CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.StartInterview);
        reply.Scope.Should().Be(scope);
        reply.Metadata.As<AuditorInterviewSeed>().TargetSubcategoryCode.Should().Be(codigoEsperado);
    }

    [Fact]
    public async Task ChatAsync_StartInterviewNoEscopoGlobal_NaoFixaSubcategoria()
    {
        // GLOBAL não tem uma Função-alvo: a entrevista começa perguntando POR ONDE começar (seed sem código).
        var req = new AuditorChatRequest(AuditorScope.Global, Array.Empty<AuditorMessage>(),
            "quero fechar meus gaps");

        var reply = await _sut.ChatAsync(req, CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.StartInterview);
        reply.Metadata.As<AuditorInterviewSeed>().TargetSubcategoryCode.Should().BeNull();
    }
}
