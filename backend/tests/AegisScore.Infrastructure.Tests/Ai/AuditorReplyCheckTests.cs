using AegisScore.Application.Abstractions;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// [AEGIS-AUDITOR-CONTEXT-01] Conferência da resposta do Auditor (citações, links, orientação geral) e validação dos identificadores da tela
/// (foco). Escritores puros — sem banco, sem rede.
/// </summary>
public sealed class AuditorReplyCheckTests
{
    private static readonly AuditorAssessmentContext Context = new(DateTimeOffset.UnixEpoch, "AEGIS NIST", new[]
    {
        new AuditorContextSource("N1", "NIST", AuditorSourceNature.Reference, "GV.PO-01 — resultado esperado", null, null, false, null, null),
        new AuditorContextSource("K2", "KNIGHT", AuditorSourceNature.ObservedConfiguration, "AK-ENTRA-002", "Reprovado", "2026-10-01", false, null,
            new AuditorSourceLink("/knight", new Dictionary<string, string> { ["run"] = "r", ["finding"] = "AK-ENTRA-002" })),
    }, Array.Empty<string>());

    private static AuditorReply Reply(string message, params string[] cited) =>
        new(message, AuditorScope.Global, AuditorIntent.Copilot, null, false, cited);

    [Fact]
    public void CitacaoValida_VoltaComAFonte_NaOrdemDoTexto()
    {
        var r = AuditorReplyCheck.Check(Reply("O controle reprovado [K2] contraria o requisito [N1].", "N1"), Context);

        r.Sources.Select(s => s.Key).Should().Equal("K2", "N1");
        r.Notes.Should().BeEmpty();
    }

    [Fact]
    public void CitacaoInexistente_SaiDoTexto_EEhDita()
    {
        var r = AuditorReplyCheck.Check(Reply("Há MFA em todos [K9] e o controle [K2] reprova.", "Z1"), Context);

        r.Message.Should().NotContain("[K9]").And.Contain("[K2]");
        r.Sources.Select(s => s.Key).Should().Equal("K2");
        r.Notes.Should().Contain(n => n.Contains("2 citação(ões) a fontes que não existem"));
    }

    [Fact]
    public void LinkNoTexto_EhRemovido()
    {
        var r = AuditorReplyCheck.Check(Reply("Veja https://evil.example.com/x e www.outro.example [K2]."), Context);

        r.Message.Should().NotContain("evil.example").And.NotContain("www.outro").And.Contain("[link removido]");
        r.Notes.Should().Contain(n => n.Contains("link(s)"));
    }

    [Fact]
    public void SoReferenciaOuNadaCitado_ViraOrientacaoGeral()
    {
        AuditorReplyCheck.Check(Reply("O requisito pede política aprovada [N1]."), Context).Notes
            .Should().Contain(n => n.Contains("não citou registros do tenant"), "catálogo não é constatação sobre o ambiente");
        AuditorReplyCheck.Check(Reply("Resposta sem fonte."), Context).Notes.Should().Contain(n => n.Contains("orientação geral"));
        AuditorReplyCheck.Check(Reply("Sem contexto."), null).Notes.Should().Contain(n => n.Contains("Não há registros do tenant"));
    }

    [Fact]
    public void RespostaLonga_EhEncurtada()
    {
        var r = AuditorReplyCheck.Check(Reply(new string('a', AuditorReplyCheck.MaxMessage + 50) + " [K2]"), Context);
        r.Message.Length.Should().BeLessThanOrEqualTo(AuditorReplyCheck.MaxMessage + 1);
        r.Notes.Should().Contain(n => n.Contains("encurtada"));
    }

    [Theory]
    [InlineData(true, false, false, null)]       // só a avaliação
    [InlineData(true, true, false, null)]        // falta o escopo
    [InlineData(true, true, true, "gv.po-1")]   // código fora do formato
    public void Foco_SelecaoNistIncompletaOuInvalida_EhRecusada(bool a, bool c, bool s, string? code)
    {
        FluentActions.Invoking(() => AuditorFocus.From("nist", a ? Guid.NewGuid() : null, c ? Guid.NewGuid() : null, s ? Guid.NewGuid() : null, code, null, null))
            .Should().Throw<AuditorFocusInvalidException>();
    }

    [Fact]
    public void Foco_ControleKnightSemAvaliacao_EhRecusado_EPaginaDesconhecidaViraGeral()
    {
        FluentActions.Invoking(() => AuditorFocus.From("knight", null, null, null, null, null, "AK-ENTRA-001"))
            .Should().Throw<AuditorFocusInvalidException>();
        FluentActions.Invoking(() => AuditorFocus.From("knight", null, null, null, null, Guid.NewGuid(), "<script>"))
            .Should().Throw<AuditorFocusInvalidException>();
        var f = AuditorFocus.From("../../admin", null, null, null, null, null, null);
        f.Page.Should().Be("general");
        f.Nist.Should().BeNull();
        AuditorFocus.From("nist", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), " gv.po-01 ", null, null).Nist!.SubcategoryCode.Should().Be("GV.PO-01");
    }
}
