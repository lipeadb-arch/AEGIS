using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// Integridade probatória do motor SIMULADO (sem LLM): a triagem não fabrica claims e o julgamento só
/// sustenta um controle com TRECHO LITERAL. Blinda o defeito da homologação — um documento que apenas
/// "diz existir" não pode virar GV.PO-01 nem inventar "aprovada pela direção".
/// </summary>
public sealed class StubDocumentAnalysisTests
{
    private readonly StubAssessmentService _sut = new();

    // O documento sintético da homologação: só afirma existir, sem política/responsável/revisão/controle.
    private const string SyntheticDoc =
        "Este documento existe apenas para validar o armazenamento persistente do ambiente de homologação.";

    [Fact]
    public async Task Triagem_DocumentoSintetico_ProduzZeroClaims()
    {
        var analysis = await _sut.AnalyzeDocumentAsync(
            new DocumentAnalysisRequest(Guid.NewGuid(), SyntheticDoc, "sintetico.txt"), default);

        analysis.Claims.Should().BeEmpty("um documento que só diz existir não endereça controle algum");
    }

    [Fact]
    public async Task Triagem_TermoIsolado_NaoViraCandidato()
    {
        // "política" e "diretriz" isolados não são prova de nada — não devem sequer virar candidato.
        var analysis = await _sut.AnalyzeDocumentAsync(
            new DocumentAnalysisRequest(Guid.NewGuid(), "Esta diretriz trata da política de viagens.", "x.txt"), default);

        analysis.Claims.Should().BeEmpty("termo isolado não dispara candidato — exige-se combinação explícita");
    }

    [Fact]
    public async Task Julgamento_ApenasIntencao_NaoSustenta_SemTrecho()
    {
        var verdict = await _sut.EvaluateDocumentControlAsync(
            Request("A organização deve proteger seus acessos privilegiados."), default);

        verdict.Supported.Should().BeFalse("declarar intenção não é evidenciar execução");
        verdict.EvidenceQuote.Should().BeEmpty("sem sustentação não há trecho probatório");
    }

    [Fact]
    public async Task Julgamento_FraseDeExecucao_Sustenta_ComTrechoLITERAL()
    {
        const string excerpt =
            "A revisão de acessos privilegiados ocorre trimestralmente, com responsável nomeado e registro em ata de auditoria.";

        var verdict = await _sut.EvaluateDocumentControlAsync(Request(excerpt), default);

        verdict.Supported.Should().BeTrue("frase com responsável/periodicidade/registro sustenta o controle");
        verdict.EvidenceQuote.Should().NotBeNullOrWhiteSpace();
        // O trecho devolvido é LITERAL — sobrevive à validação contra o próprio texto.
        EvidenceQuoteValidator.IsLiterallyPresent(excerpt, verdict.EvidenceQuote)
            .Should().BeTrue("o EvidenceQuote do stub é verbatim, nunca paráfrase");
    }

    [Fact]
    public async Task Julgamento_TrechoVazio_NaoSustenta()
    {
        var verdict = await _sut.EvaluateDocumentControlAsync(Request("   "), default);
        verdict.Supported.Should().BeFalse();
        verdict.EvidenceQuote.Should().BeEmpty();
    }

    private static DocumentControlEvaluationRequest Request(string excerpt) => new(
        "PR.AA-01",
        "Identities and credentials are managed.",
        new[] { "Entra ID: authenticationMethods e sign-in logs" },
        "score = com_mfa / privilegiadas",
        excerpt,
        "politica.pdf");
}
