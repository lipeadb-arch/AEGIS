using System.Text;
using AegisScore.Application.Abstractions;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Implementação FAKE de <see cref="IAiAssessmentService"/> para desenvolvimento/demonstração:
/// devolve respostas canned sem chamar nenhum LLM — logo, sem chave e sem consumo de tokens. Registrada
/// automaticamente pelo DI quando não há Ai:ApiKey, permitindo exercer o Auditor Virtual, a leitura
/// documental e a assistência NIST ponta a ponta.
/// </summary>
public sealed class StubAssessmentService : IAiAssessmentService
{
    /// <summary>
    /// Triagem determinística por tema — descoberta de CANDIDATOS, jamais prova. Roteia temas do texto
    /// para controles que TÊM regra, exercitando a esteira documental sem rede. Mudanças de integridade:
    /// <list type="bullet">
    /// <item>SEM fallback: texto sem tema reconhecido → ZERO candidatos (antes fabricava GV.PO-01/GV.RR-01);</item>
    /// <item>termo ISOLADO não é candidato: "política"/"diretriz" sozinhos não disparam GV.PO-01 — exige-se
    /// a combinação explícita ("política de segurança");</item>
    /// <item>o texto do candidato é NEUTRO ("tema X identificado"), nunca uma afirmação de fato ausente do
    /// documento (como o antigo "aprovada pela direção"). A prova — sustentação + trecho literal — vem só
    /// na segunda passada.</item>
    /// </list>
    /// </summary>
    public Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct)
    {
        var text = (request.DocumentText ?? "").ToLowerInvariant();
        var claims = new List<DocumentClaim>();

        void Detect(string code, string theme, params string[] terms)
        {
            if (terms.Any(t => text.Contains(t, StringComparison.Ordinal)))
                // Confiança da triagem é só um sinal de candidato (0.5), NÃO entra em score/cobertura —
                // a segunda passada recomputa a confiança probatória a partir do trecho literal.
                claims.Add(new DocumentClaim(code, $"Tema candidato: {theme} (a confirmar por trecho literal).", 0.5));
        }

        Detect("PR.AA-01", "autenticação/identidade privilegiada",
            "privilegiad", "multifator", "mfa", "autenticacao", "autenticação", "conditional access");
        Detect("RC.RP-01", "continuidade/recuperação",
            "continuidade", "recuperacao", "recuperação", "plano de recuperacao", "plano de recuperação", "backup");
        Detect("PR.DS-01", "proteção/criptografia de dados",
            "criptograf", "dados sensiveis", "dados sensíveis", "bitlocker");
        // Combinação EXPLÍCITA — "política"/"diretriz" isolados NÃO disparam (não são prova de nada).
        Detect("GV.PO-01", "política de segurança da informação",
            "politica de seguranca", "política de segurança", "psi ");
        Detect("GV.RR-01", "papéis e responsabilidades de segurança",
            "papeis e responsabilidades", "papéis e responsabilidades", "matriz raci",
            "comite de seguranca", "comitê de segurança");

        // SEM fallback: documento sem tema reconhecido termina Analisado com ZERO candidatos → zero prova.
        return Task.FromResult(new DocumentAnalysis(
            Summary: $"[Simulado] Leitura de '{request.FileName ?? "documento"}': {claims.Count} tema(s) " +
                     "candidato(s) — a sustentação depende de trecho literal na 2ª passada.",
            Claims: claims));
    }

    /// <summary>
    /// Segunda passada determinística: decide se o trecho SUSTENTA o controle e devolve o TRECHO LITERAL
    /// que o prova. Sustenta somente quando encontra uma FRASE do trecho com sinais de EXECUÇÃO (o controle
    /// roda: "responsável", "periodicidade", "registro"…) — e devolve essa frase VERBATIM como
    /// <c>EvidenceQuote</c> (nunca paráfrase; o worker ainda a valida como literalmente presente no texto).
    /// Intenção declarada ("deve/recomenda") ou menção temática solta NÃO sustentam. Não é NLP — é o
    /// suficiente para exercitar a esteira sem rede e para o documento sintético render ZERO prova.
    /// </summary>
    public Task<DocumentControlVerdict> EvaluateDocumentControlAsync(
        DocumentControlEvaluationRequest request, CancellationToken ct)
    {
        var excerpt = request.DocumentExcerpt ?? "";
        if (string.IsNullOrWhiteSpace(excerpt))
            return Task.FromResult(new DocumentControlVerdict(
                false, "", 0.0, $"[Simulado] Nenhum trecho do documento endereça {request.SubcategoryCode}."));

        // Casamento por RADICAL (pega flexões: "responsabilidade", "registradas", "revisada"). Detecção em
        // minúsculas, mas o TRECHO devolvido preserva o texto original (o EvidenceQuote tem de ser literal).
        string[] execution = ["responsab", "responsav", "accountab", "periodic", "trimestr", "mensal",
                              "anualmente", "registr", "evidenc", "auditor", "revis", "aprovad",
                              "sancao", "sanção", "disciplinar", "comite", "comitê", "matriz raci"];
        string[] intent = ["deve", "deverá", "devera", "recomenda", "pretende", "objetivo", "futuro"];

        // Escolhe a FRASE literal com mais sinais de execução — é ela que vira o trecho probatório.
        var bestSentence = "";
        var bestHits = 0;
        foreach (var sentence in SplitSentences(excerpt))
        {
            var lower = sentence.ToLowerInvariant();
            var hits = execution.Count(t => lower.Contains(t, StringComparison.Ordinal));
            if (hits > bestHits) { bestHits = hits; bestSentence = sentence.Trim(); }
        }

        if (bestHits == 0)
        {
            // Sem execução: intenção declarada ou tema solto — NÃO sustenta, sem trecho probatório.
            var lowerAll = excerpt.ToLowerInvariant();
            var intentOnly = intent.Any(t => lowerAll.Contains(t, StringComparison.Ordinal));
            return Task.FromResult(new DocumentControlVerdict(
                false, "", intentOnly ? 0.45 : 0.30,
                $"[Simulado] {request.SubcategoryCode}: o texto {(intentOnly ? "declara intenção" : "menciona o tema")} " +
                "sem nomear responsável, periodicidade nem registro de execução — sem valor probatório."));
        }

        // 0.75 passa do limiar de cobertura (0.7); menos sinais ficam em Parcial. Crédito de score é 50%
        // (MitigatedByThirdParty) independentemente da confiança — ela só decide Coberto × Parcial.
        var confidence = Math.Min(0.75, 0.45 + 0.10 * bestHits);
        return Task.FromResult(new DocumentControlVerdict(
            true, bestSentence, confidence,
            $"[Simulado] {request.SubcategoryCode}: o trecho cita {bestHits} elemento(s) de execução " +
            "(responsável/periodicidade/registro), sustentando cobertura documental parcial."));
    }

    /// <summary>Quebra o trecho em frases (por . ! ? e quebras de linha), preservando o texto literal de cada uma.</summary>
    private static IEnumerable<string> SplitSentences(string text) =>
        text.Split(new[] { '.', '!', '?', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    // [AEGIS-LANGUAGE-STATES-01] O texto canned afirmava maturidade e riscos ("política formalizada", "GV.SC
    // incipiente") sem ter lido dado nenhum do ambiente — o fallback não pode preencher lacunas com conclusão.

    /// <summary>
    /// [AEGIS-AUDITOR-CONTEXT-01] Auditor SIMULADO (sem LLM): a mesma identidade do motor real, montando a resposta SÓ com as fontes do
    /// contexto do tenant — cita as chaves, diz a natureza e as limitações, separa o que falta verificar e lembra que a decisão é do
    /// assessor. Não roteia para entrevista, não usa limiares fixos e não afirma nada que não esteja nas fontes. Sem contexto, diz que não
    /// há dados suficientes. Marcada como demonstração.
    /// </summary>
    public Task<AuditorReply> ChatAsync(AuditorChatRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = request.Context?.Assessments;
        var focus = context?.FocusLabel ?? request.Focus?.PageLabel ?? AuditorPages.Label("general");
        var sb = new StringBuilder();
        sb.Append("[Demonstração — motor simulado, sem análise por IA] Foco: ").Append(focus).Append(".\n");

        var sources = context?.Sources ?? Array.Empty<AuditorContextSource>();
        var picked = PickSources(sources, request.UserMessage);
        var cited = new List<string>();
        if (picked.Count == 0)
        {
            sb.Append("Não há dados suficientes nos registros do tenant para responder sobre o ambiente. ")
              .Append("Para avançar, registre ou colete as evidências pertinentes (avaliação do KNIGHT, documentos, inventário ou avaliação NIST) ");
            sb.Append("e volte a perguntar.");
        }
        else
        {
            var references = picked.Where(s => !AuditorSourceNature.SupportsEnvironmentClaim(s.Nature)).ToList();
            picked = picked.Except(references).ToList();
            foreach (var r in references)
            {
                cited.Add(r.Key);
                sb.Append("Referência do requisito (explica, não comprova o ambiente): ").Append(r.Title).Append(" [").Append(r.Key).Append("].\n");
            }
            sb.Append(picked.Count == 0 ? "Não há registros do tenant pertinentes a esta pergunta no contexto.\n" : "O que os registros do tenant mostram:\n");
            foreach (var s in picked)
            {
                cited.Add(s.Key);
                sb.Append("• ").Append(s.Title).Append(" — ").Append(Trim(s.Detail, 260))
                  .Append(" [").Append(s.Key).Append("] (").Append(AuditorSourceNature.Label(s.Nature)).Append(s.IsDemo ? "; demonstração" : "").Append(").\n");
            }
            var limits = picked.Where(s => s.Limitation is not null).Select(s => s.Limitation!).Distinct().Take(2).ToList();
            if (limits.Count > 0) sb.Append("Limites dessas fontes: ").Append(string.Join(" ", limits)).Append('\n');
            if (context!.Limitations.FirstOrDefault(l => l.Contains(" de ", StringComparison.Ordinal) && l.Contains("incluíd", StringComparison.Ordinal)) is { } partial)
                sb.Append("Lista parcial: ").Append(partial).Append('\n');
        }
        sb.Append("Para concluir, o assessor precisa examinar as evidências vinculadas, entrevistar o responsável pela prática e registrar o resultado ")
          .Append("de um teste. Política escrita não comprova execução, e um resultado do KNIGHT não demonstra sozinho o atendimento do requisito. ")
          .Append("A validação e qualquer gravação continuam com o assessor.");

        return Task.FromResult(new AuditorReply(sb.ToString(), AuditorScope.Global, AuditorIntent.Copilot, null, Simulated: true, CitedKeys: cited));
    }

    /// <summary>
    /// Fontes pertinentes à pergunta: as que citam o código de subcategoria ou de controle mencionado; senão as do foco (seleção NIST,
    /// correlação, controle KNIGHT em foco), depois as gerais. Nunca mais de seis.
    /// </summary>
    private static List<AuditorContextSource> PickSources(IReadOnlyList<AuditorContextSource> sources, string question)
    {
        if (sources.Count == 0) return new List<AuditorContextSource>();
        var mentioned = System.Text.RegularExpressions.Regex.Matches(question ?? "", @"\b[A-Z]{2}\.[A-Z]{2}-\d{2}\b|\bAK-[A-Z0-9-]+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
        var byMention = mentioned.Count == 0 ? new List<AuditorContextSource>()
            : sources.Where(s => mentioned.Any(m => (s.Title + " " + s.Detail).Contains(m, StringComparison.OrdinalIgnoreCase))).ToList();
        int Rank(AuditorContextSource s) => s.Key[0] switch
        {
            'N' => s.Nature == AuditorSourceNature.Reference ? 4 : 0,
            'R' => 1,
            'K' => s.Title.StartsWith("Controle em foco", StringComparison.Ordinal) ? 0 : 2,
            'D' => 3,
            'A' => 3,
            'P' => 5,
            _ => 6,
        };
        return byMention.Concat(sources.Where(s => s.Nature != AuditorSourceNature.Reference || byMention.Count == 0).OrderBy(Rank))
            .Distinct().Take(6).ToList();
    }

    private static string Trim(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Assistência NIST simulada: demonstração por regras fixas, marcada "[Simulado]".</summary>
    public Task<AegisScore.Application.Nist.NistAssistDraft> AssistNistAsync(
        AegisScore.Application.Nist.NistAssistPrompt request, CancellationToken ct)
        => Task.FromResult(StubNistAssist.Draft(request));
}
