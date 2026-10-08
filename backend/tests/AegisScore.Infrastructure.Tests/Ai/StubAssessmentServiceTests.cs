using AegisScore.Application.Abstractions;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Testes do Auditor SIMULADO (<see cref="StubAssessmentService"/>, sem LLM): a mesma identidade em qualquer
/// página, sem roteamento para entrevista, sem limiares fixos; a resposta é montada SÓ com as fontes do contexto do tenant (citadas
/// pela chave, com natureza e limitação), é marcada como demonstração e, sem registros, diz que não há dados suficientes.
/// </summary>
public sealed class StubAssessmentServiceTests
{
    private readonly StubAssessmentService _sut = new();

    [Theory]
    [InlineData(AuditorScope.Protect, "quero auditar e fechar lacunas")]
    [InlineData(AuditorScope.Govern, "inicie a entrevista")]
    [InlineData(AuditorScope.Global, "diagnóstico")]
    public async Task ChatAsync_NuncaRoteiaParaEntrevista_NemUsaLimiaresFixos(AuditorScope scope, string mensagem)
    {
        var reply = await _sut.ChatAsync(new AuditorChatRequest(scope, Array.Empty<AuditorMessage>(), mensagem), CancellationToken.None);

        reply.Intent.Should().Be(AuditorIntent.Copilot);
        reply.Metadata.Should().BeNull();
        reply.Simulated.Should().BeTrue();
        reply.Message.Should().StartWith("[Demonstração — motor simulado");
        reply.Message.Should().NotContain("MTTA").And.NotContain("≥95%").And.NotContain("100%");
    }

    [Fact]
    public async Task ChatAsync_SemRegistrosDoTenant_DizQueNaoHaDadosSuficientes_ENaoCitaNada()
    {
        var reply = await _sut.ChatAsync(new AuditorChatRequest(AuditorScope.Global, Array.Empty<AuditorMessage>(), "como está o MFA?"), CancellationToken.None);

        reply.Message.Should().Contain("Não há dados suficientes nos registros do tenant");
        reply.CitedKeys.Should().BeEmpty("nada foi encontrado — nada é atribuído ao tenant");
    }

    [Fact]
    public async Task ChatAsync_ComContexto_CitaFontesComNaturezaELimitacao_ENaoInventa()
    {
        var context = new AuditorAssessmentContext(DateTimeOffset.UnixEpoch, "AEGIS NIST · GV.PO-01", new[]
        {
            new AuditorContextSource("N1", "NIST", AuditorSourceNature.Reference, "GV.PO-01 — resultado esperado", "Política estabelecida…", null, false, "Orientação geral.", null),
            new AuditorContextSource("N2", "NIST", AuditorSourceNature.Documentation, "Política de Segurança", "Trecho literal validado: «revisada anualmente»", "2026-09-01", false,
                "Trecho literal: comprova o que o texto estabelece, não a execução da prática.", null),
            new AuditorContextSource("K1", "KNIGHT", AuditorSourceNature.ObservedConfiguration, "AK-ENTRA-002 — MFA de administradores", "Resultado Reprovado", "2026-10-01", true,
                "Um controle técnico cobre um aspecto.", null),
        }, new[] { "AEGIS KNIGHT: 1 de 9 controles reprovados incluídos (ordem: severidade, depois objetos afetados)." });
        var request = new AuditorChatRequest(AuditorScope.Govern, Array.Empty<AuditorMessage>(), "o que falta em GV.PO-01?",
            Context() with { Assessments = context });

        var reply = await _sut.ChatAsync(request, CancellationToken.None);

        reply.Message.Should().Contain("Foco: AEGIS NIST · GV.PO-01");
        reply.Message.Should().Contain("[N2]").And.Contain("Documentação (trecho literal)");
        reply.Message.Should().Contain("[K1]").And.Contain("demonstração");
        reply.Message.Should().Contain("não a execução da prática");
        reply.Message.Should().Contain("Lista parcial: AEGIS KNIGHT: 1 de 9");
        reply.Message.Should().Contain("A validação e qualquer gravação continuam com o assessor");
        reply.CitedKeys.Should().Contain(new[] { "N2", "K1" });
        reply.CitedKeys.Should().OnlyContain(k => k == "N1" || k == "N2" || k == "K1", "só cita chaves que existem no contexto");
    }

    private static AuditorTenantContext Context() => new(
        "NotEvaluated", null, 0, 0, 0, 0, 0, null, Array.Empty<AuditorFunctionPosture>(), Array.Empty<AuditorControlGap>(),
        Array.Empty<AuditorDocumentEvidence>(), new AuditorConnectorContext(0, 0, 0, 0, 0, 0, null), Array.Empty<string>());
}
