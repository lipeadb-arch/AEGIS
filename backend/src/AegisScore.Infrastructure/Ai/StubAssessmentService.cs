using System.Text.RegularExpressions;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Implementação FAKE de <see cref="IAiAssessmentService"/> para desenvolvimento/demonstração:
/// devolve respostas canned (roteiro fixo baseado no NIST CSF 2.0) sem chamar nenhum LLM — logo,
/// sem chave e sem consumo de tokens. Registrada automaticamente pelo DI quando não há Ai:ApiKey,
/// permitindo exercer o fluxo do Auditor Virtual ponta a ponta (o POST interviews volta 200 OK).
///
/// <para>O <see cref="ConductInterviewTurnAsync"/> conduz uma entrevista determinística: uma
/// pergunta por subcategoria-alvo (extraída do enquadramento), encerrando após <c>MaxTurns</c>.</para>
/// </summary>
public sealed class StubAssessmentService : IAiAssessmentService
{
    /// <summary>Teto de perguntas por sessão — mantém a demo curta e finita.</summary>
    private const int MaxTurns = 4;

    /// <summary>Casa códigos NIST no formato "GV.OC-01" dentro do histórico textual.</summary>
    private static readonly Regex NistCode = new(@"[A-Z]{2}\.[A-Z]{2}-\d{2}", RegexOptions.Compiled);

    /// <summary>Perguntas canned por categoria (prefixo "Fn.CT"). Fallback: pergunta genérica.</summary>
    private static readonly Dictionary<string, string> Bank = new()
    {
        ["GV.OC"] = "Como a organização define e comunica sua missão, partes interessadas e os requisitos legais/regulatórios que orientam a gestão de risco de cibersegurança?",
        ["GV.RM"] = "Como os objetivos e o apetite a risco de cibersegurança são estabelecidos, acordados e comunicados às partes interessadas?",
        ["GV.RR"] = "Como estão definidos, comunicados e mantidos os papéis, responsabilidades e autoridades de cibersegurança na organização?",
        ["GV.PO"] = "Como a política de segurança da informação é estabelecida, aprovada, comunicada e revisada periodicamente?",
        ["GV.OV"] = "Como a liderança supervisiona os resultados da estratégia de gestão de risco e ajusta o rumo quando necessário?",
        ["GV.SC"] = "Como a organização identifica e gerencia os riscos de cibersegurança na cadeia de suprimentos e com terceiros?",
        ["ID.AM"] = "Como a empresa gerencia o inventário de ativos físicos e de software ativos na rede?",
        ["ID.RA"] = "Como as vulnerabilidades dos ativos são identificadas, avaliadas e priorizadas para tratamento?",
    };

    /// <summary>Roteiro padrão quando o enquadramento não traz códigos explícitos.</summary>
    private static readonly List<string> DefaultScript = new() { "GV.OC-01", "GV.RR-01", "GV.PO-01" };

    public Task<InterviewTurn> ConductInterviewTurnAsync(InterviewContext context, CancellationToken ct)
    {
        var plan = ExtractTargets(context.History).Take(MaxTurns).ToList();
        // Nº de perguntas já feitas = ocorrências de "Assistant:" no histórico (0 no Start).
        var asked = context.History.Count(h => h.StartsWith("Assistant:", StringComparison.OrdinalIgnoreCase));

        if (asked >= plan.Count)
            return Task.FromResult(new InterviewTurn("", null, true)); // roteiro coberto → encerra

        var code = plan[asked];
        return Task.FromResult(new InterviewTurn(QuestionFor(code), code, false));
    }

    public Task<MaturitySuggestion> SuggestMaturityAsync(MaturitySuggestionRequest request, CancellationToken ct)
        => Task.FromResult(new MaturitySuggestion(
            CurrentLevel: 3, // "Parcial" no ledger → exercita a geração de IdentifiedRisk no fluxo
            Confidence: 0.6,
            Rationale: $"[Simulado] Avaliação canned de {request.SubcategoryCode}: evidência parcial " +
                       "declarada na entrevista, sem documento formal correlato. Nível 3 (Gerenciado) provisório.",
            EvidenceRefs: Array.Empty<Guid>()));

    public Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct)
        => Task.FromResult(new DocumentAnalysis(
            Summary: $"[Simulado] Leitura canned de '{request.FileName ?? "documento"}': o texto declara " +
                     "controles de governança compatíveis com o NIST CSF 2.0.",
            Claims: new List<DocumentClaim>
            {
                new("GV.PO-01", "Menção a política de segurança da informação aprovada pela direção.", 0.72),
                new("GV.RR-01", "Definição de papéis e responsabilidades de segurança.", 0.65),
            }));

    public Task<IReadOnlyList<ActionPlanSuggestion>> GenerateActionPlanAsync(ActionPlanRequest request, CancellationToken ct)
    {
        IReadOnlyList<ActionPlanSuggestion> plans = request.Gaps
            .Select(g => new ActionPlanSuggestion(
                g.SubcategoryCode,
                $"[Simulado] Endereçar a lacuna de {g.SubcategoryCode}.",
                "Formalizar o controle, atribuir responsável e evidenciar a execução.",
                g.Gap >= 2 ? "Alta" : "Média"))
            .ToList();
        return Task.FromResult(plans);
    }

    public Task<string> GenerateExecutiveReportAsync(ExecutiveReportRequest request, CancellationToken ct)
        => Task.FromResult(
            "# Plano Diretor de Segurança (Simulado)\n\n" +
            $"Cliente: **{request.ClientName}**.\n\n" +
            "> Conteúdo gerado pelo motor de IA **simulado** (StubAssessmentService), sem chamada a LLM.\n\n" +
            "## Maturidade atual\nControles de governança parcialmente estabelecidos; política formalizada, " +
            "porém supervisão (GV.OV) e gestão de terceiros (GV.SC) ainda incipientes.\n\n" +
            "## Principais riscos\nLacunas em oversight e na cadeia de suprimentos.\n");

    public Task<IReadOnlyList<NormalizedSignal>> NormalizeSignalsAsync(RawSignalBatch batch, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NormalizedSignal>>(Array.Empty<NormalizedSignal>());

    // ---- helpers ----

    /// <summary>Extrai (na ordem, sem repetir) os códigos NIST do histórico; se não houver, usa o roteiro padrão.</summary>
    private static List<string> ExtractTargets(IReadOnlyList<string> history)
    {
        var codes = new List<string>();
        foreach (var line in history)
            foreach (Match m in NistCode.Matches(line))
                if (!codes.Contains(m.Value))
                    codes.Add(m.Value);
        return codes.Count > 0 ? codes : DefaultScript;
    }

    private static string QuestionFor(string code)
    {
        var key = code.Length >= 5 ? code[..5] : code; // "GV.OC-01" → "GV.OC"
        return Bank.TryGetValue(key, out var q)
            ? q
            : $"Descreva como a organização implementa e evidencia os controles da subcategoria {code} do NIST CSF 2.0.";
    }
}
