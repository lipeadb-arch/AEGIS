using System.Net;
using System.Text;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Documents;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Ai;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Ai;

/// <summary>
/// RAG documental de duas passadas: seleção de trecho dirigida ao controle (chunking) e injeção da
/// PERSONA no System Prompt do motor documental. O que estes testes protegem é o que não aparece no
/// resultado: um prompt que perdeu a persona continua devolvendo JSON válido, e um chunker que devolve
/// o documento inteiro continua "funcionando" — só custa 10× mais tokens e julga pior.
/// </summary>
public class DocumentRagTests
{
    // ---- Frente 1: a persona chega ao prompt do motor DOCUMENTAL --------------------

    [Fact]
    public async Task PersonaEhInjetadaNoSystemPromptDaTriagem()
    {
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"{\"summary\":\"ok\",\"claims\":[]}"}]}""");
        var service = ServiceWith(handler, PersonaFixture);

        await service.AnalyzeDocumentAsync(new DocumentAnalysisRequest(Guid.NewGuid(), "texto", "politica.pdf"), default);

        handler.LastSystemPrompt.Should().Contain("Consultor Estratégico");
        handler.LastSystemPrompt.Should().Contain("RS.MA → Resposta a Incidentes");
        handler.LastSystemPrompt.Should().Contain("NEVER changes the status",
            "a salvaguarda tem de viajar junto: persona governa tom, jamais veredito");
    }

    [Fact]
    public async Task PersonaEhInjetadaNoSystemPromptDoJulgamentoDirigido()
    {
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"{\"confidence\":0.8,\"rationale\":\"ok\"}"}]}""");
        var service = ServiceWith(handler, PersonaFixture);

        await service.EvaluateDocumentControlAsync(SampleRequest("trecho"), default);

        handler.LastSystemPrompt.Should().Contain("Consultor Estratégico");
        handler.LastSystemPrompt.Should().Contain("ACTION DIRECTIVES");
    }

    [Fact]
    public async Task PersonaNEUTRA_NaoContaminaOPrompt()
    {
        // Sem persona o prompt tem de ficar EXATAMENTE como era — nada de cabeçalho vazio gastando token.
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"{\"confidence\":0.5,\"rationale\":\"x\"}"}]}""");
        var service = ServiceWith(handler, StaticAuditorPersonaProvider.Neutral);

        await service.EvaluateDocumentControlAsync(SampleRequest("trecho"), default);

        handler.LastSystemPrompt.Should().NotContain("AUDITOR PERSONA");
    }

    [Fact]
    public async Task JulgamentoDirigido_LevaControleCriteriosETrecho_MasNaoODocumentoInteiro()
    {
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"{\"confidence\":0.9,\"rationale\":\"ok\"}"}]}""");
        var service = ServiceWith(handler, StaticAuditorPersonaProvider.Neutral);

        await service.EvaluateDocumentControlAsync(SampleRequest("O acesso privilegiado é revisado."), default);

        handler.LastUserPrompt.Should().Contain("PR.AA-01");
        handler.LastUserPrompt.Should().Contain("Entra ID: authenticationMethods");   // critério da regra
        handler.LastUserPrompt.Should().Contain("O acesso privilegiado é revisado.");
        handler.LastUserPrompt.Should().Contain("untrusted data",
            "o trecho do cliente é DADO, nunca instrução — a fronteira anti-injeção é obrigatória");
    }

    [Fact]
    public async Task ConfiancaForaDaFaixa_EhLimitada_NaoPropagaLixo()
    {
        // Um modelo que devolve 3.7 quebraria o limiar de cobertura silenciosamente.
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"{\"confidence\":3.7,\"rationale\":\"x\"}"}]}""");
        var service = ServiceWith(handler, StaticAuditorPersonaProvider.Neutral);

        var verdict = await service.EvaluateDocumentControlAsync(SampleRequest("trecho"), default);

        verdict.Confidence.Should().Be(1.0);
    }

    // ---- Frente 2: chunking dirigido ao controle ------------------------------------

    [Fact]
    public void Chunker_EscolheOParagrafoQueEnderecaOControle_EDescartaORuido()
    {
        var doc = string.Join("\n\n",
            "Este documento descreve a política de viagens corporativas e reembolso de despesas da empresa.",
            "O acesso privilegiado exige autenticação multifator, com revisão trimestral e responsável nomeado.",
            "O refeitório funciona das onze às quatorze horas, com cardápio publicado semanalmente no mural.");

        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            doc, new[] { "PR.AA-01", "autenticação multifator acesso privilegiado revisão" }, charBudget: 140);

        excerpt.Should().Contain("acesso privilegiado");
        excerpt.Should().NotContain("refeitório");
        excerpt.Should().NotContain("viagens corporativas");
    }

    [Fact]
    public void Chunker_PreservaAOrdemDODOCUMENTO_NaoAOrdemDePlacar()
    {
        // A política é escrita do geral para o específico: ler fora de ordem distorce a intenção.
        var doc = string.Join("\n\n",
            "A revisão de acesso privilegiado ocorre trimestralmente sob responsabilidade do time de segurança.",
            "Texto de enchimento sobre estacionamento, crachás de visitante e horários de portaria do prédio.",
            "O acesso privilegiado exige autenticação multifator para todos os administradores de domínio.");

        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            doc, new[] { "acesso privilegiado autenticação multifator revisão" }, charBudget: 250);

        excerpt.IndexOf("trimestralmente", StringComparison.Ordinal)
            .Should().BeLessThan(excerpt.IndexOf("administradores de domínio", StringComparison.Ordinal));
    }

    [Fact]
    public void Chunker_TermosGENERICOS_NaoArrastamSecoesAlheias()
    {
        // Regressão de um falso positivo observado ao vivo: as regras do 800-53 usam palavras que
        // aparecem em TODO parágrafo de uma política ("registro", "responsável", "revisão"). Sem peso
        // por raridade, elas puxavam a seção de acessos para dentro do trecho de continuidade — e o
        // motor creditava cobertura ao controle errado, que é falso positivo de conformidade.
        var doc = string.Join("\n\n",
            "Seção de acessos: a revisão de contas privilegiadas ocorre sob responsabilidade do gestor, com registro em ata.",
            "Seção de ativos: o inventário é revisado sob responsabilidade do gestor, com registro em ata de conferência.",
            "Seção de fornecedores: contratos são revisados sob responsabilidade do gestor, com registro em ata de reunião.",
            "Seção de continuidade: o plano de recuperação de desastres prevê restauração dos sistemas críticos.");

        // Faixa PRIMÁRIA: código + outcome (define o assunto). Faixa de APOIO: a prosa do 800-53, que
        // na regra real vem recheada destes genéricos.
        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            doc,
            primaryTerms: new[] { "RC.RP-01", "continuidade recuperação de desastres restauração" },
            supportingTerms: new[] { "registro responsável revisão execução cobertura ata gestor" },
            charBudget: 130);

        excerpt.Should().Contain("continuidade");
        excerpt.Should().NotContain("privilegiadas", "termo genérico não pode arrastar a seção de acessos");
        excerpt.Should().NotContain("fornecedores");
    }

    [Fact]
    public void Chunker_OrcamentoSOBRANDO_NaoConvidaParagrafoIrrelevante()
    {
        // Regressão do caso real: quando o trecho pertinente é curto e o teto é largo, um parágrafo que
        // casa UM termo solto entrava na carona e o motor julgava o controle contra evidência alheia.
        // Orçamento é TETO, não cota a preencher.
        // O documento PRECISA passar do orçamento, senão o early-return o devolve inteiro e o
        // ranqueamento nem roda. Aqui o ruído garante isso; os dois parágrafos de interesse somam ~200
        // caracteres e caberiam FOLGADOS no teto — só a relevância pode excluir o segundo.
        var ruido = string.Join("\n\n", Enumerable.Repeat(
            "Parágrafo administrativo sobre estacionamento, crachás de visitante, cardápio do refeitório e "
            + "manutenção preventiva da frota, sem qualquer relação com controles de segurança.", 12));

        var doc = string.Join("\n\n",
            ruido,
            "O plano de recuperação de desastres prevê restauração dos sistemas críticos em até quatro horas.",
            "A revisão de contas privilegiadas ocorre trimestralmente com registro em ata pelo comitê responsável.");

        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            doc,
            primaryTerms: new[] { "RC.RP-01", "plano de recuperação restauração sistemas críticos" },
            supportingTerms: new[] { "registro responsável revisão" },
            charBudget: 1_000);   // cabem os dois — só a relevância pode excluir o segundo

        excerpt.Should().Contain("recuperação");
        excerpt.Should().NotContain("privilegiadas");
    }

    [Fact]
    public void Chunker_SemSinalLexico_DevolveOComecoTruncado_EmVezDeVazio()
    {
        var doc = new string('a', 500) + "\n\n" + new string('b', 500);

        var excerpt = DocumentChunker.SelectRelevantExcerpt(doc, new[] { "termo-que-nao-existe" }, charBudget: 100);

        excerpt.Should().HaveLength(100, "sem casamento, ler o começo é melhor que entregar nada ao modelo");
    }

    [Fact]
    public void Chunker_DocumentoMenorQueOOrcamento_VaiInteiro()
    {
        const string doc = "Política curta sobre gestão de acessos privilegiados na organização.";

        DocumentChunker.SelectRelevantExcerpt(doc, new[] { "acessos" }, charBudget: 5_000)
            .Should().Be(doc);
    }

    [Fact]
    public void Chunker_IgnoraAcentuacao_AoCasarTermos()
    {
        // O PDF do cliente e o catálogo 800-53 divergem em acentuação o tempo todo.
        var doc = string.Join("\n\n",
            "Conteudo irrelevante sobre a frota de veiculos e manutencao preventiva agendada pela oficina.",
            "A revisao periodica de acessos privilegiados e registrada em ata pelo comite de seguranca.");

        var excerpt = DocumentChunker.SelectRelevantExcerpt(
            doc, new[] { "revisão periódica de acessos privilegiados" }, charBudget: 120);

        excerpt.Should().Contain("revisao periodica");
    }

    // ---- Stub determinístico: intenção × execução -----------------------------------

    [Fact]
    public async Task Stub_TextoDeINTENCAO_FicaAbaixoDoLimiarDeCobertura()
    {
        var verdict = await new StubAssessmentService()
            .EvaluateDocumentControlAsync(SampleRequest("A organização deve proteger seus acessos."), default);

        verdict.Confidence.Should().BeLessThan(0.7, "declarar intenção não é evidenciar execução");
    }

    [Fact]
    public async Task Stub_TextoDeEXECUCAO_AlcancaOLimiarDeCobertura()
    {
        var verdict = await new StubAssessmentService().EvaluateDocumentControlAsync(
            SampleRequest("Revisão trimestral de acessos, com responsável nomeado e registro em ata de auditoria."),
            default);

        verdict.Confidence.Should().BeGreaterThanOrEqualTo(0.7);
    }

    // ---- fixtures -------------------------------------------------------------------

    private static AuditorPersona PersonaFixtureValue => new(
        "Consultor Estratégico de Cibersegurança sênior",
        new[] { "Proativo", "Didático" },
        new[] { new AuditorTranslationRule("RS.MA", "Resposta a Incidentes (MTTA/MTTR)") },
        new[] { "Proponha a correção antes do pedido." });

    private static IAuditorPersonaProvider PersonaFixture => new StaticAuditorPersonaProvider(PersonaFixtureValue);

    private static DocumentControlEvaluationRequest SampleRequest(string excerpt) => new(
        "PR.AA-01",
        "Identities and credentials are managed.",
        new[] { "Entra ID: authenticationMethods e sign-in logs" },
        "score = com_mfa / privilegiadas",
        excerpt,
        "politica.pdf");

    private static ClaudeAssessmentService ServiceWith(CapturingHandler handler, IAuditorPersonaProvider persona) =>
        new(new HttpClient(handler), Options.Create(new AiOptions { ApiKey = "test-key" }), persona);

    /// <summary>Handler que devolve um corpo fixo e GUARDA o que foi enviado — o objeto do teste.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public string LastSystemPrompt { get; private set; } = "";
        public string LastUserPrompt { get; private set; } = "";

        public CapturingHandler(string body) => _body = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var sent = await request.Content!.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(sent);
            LastSystemPrompt = doc.RootElement.GetProperty("system").GetString() ?? "";
            LastUserPrompt = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? "";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
