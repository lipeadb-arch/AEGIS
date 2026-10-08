using System.Text;
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

    // [AEGIS-LANGUAGE-STATES-01] O texto canned afirmava maturidade e riscos ("política formalizada", "GV.SC
    // incipiente") sem ter lido dado nenhum do ambiente — o fallback não pode preencher lacunas com conclusão.
    public Task<string> GenerateExecutiveReportAsync(ExecutiveReportRequest request, CancellationToken ct)
        => Task.FromResult(
            "# Plano Diretor de Segurança (Simulado)\n\n" +
            $"Cliente: **{request.ClientName}**.\n\n" +
            "> Conteúdo gerado pelo motor de IA **simulado** (StubAssessmentService), sem chamada a LLM e **sem " +
            "análise dos dados deste ambiente**.\n\n" +
            "## Maturidade atual\nNão há dados suficientes neste modo simulado.\n\n" +
            "## Principais riscos\nNão há dados suficientes neste modo simulado.\n");

    public Task<IReadOnlyList<NormalizedSignal>> NormalizeSignalsAsync(RawSignalBatch batch, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<NormalizedSignal>>(Array.Empty<NormalizedSignal>());

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

    /// <summary>
    /// Redige um advisory CANNED ancorado no código do controle (sem LLM). Um banco fixo cobre os
    /// controles do Protect com texto mastigado; para os demais, um fallback genérico compõe uma
    /// recomendação a partir do próprio código — o suficiente para exercer o fluxo consultivo ponta a
    /// ponta sem chave nem tokens.
    /// </summary>
    public Task<AdvisoryDraft> GenerateAdvisoryAsync(AdvisoryGenerationRequest request, CancellationToken ct)
    {
        var code = (request.SubcategoryCode ?? "").Trim().ToUpperInvariant();
        var draft = AdvisoryBank.TryGetValue(code, out var canned) ? canned : FallbackAdvisory(code);
        return Task.FromResult(draft);
    }

    /// <summary>Advisories canned por código de controle (foco no Protect — a Fase 1). Fallback: <see cref="FallbackAdvisory"/>.</summary>
    private static readonly Dictionary<string, AdvisoryDraft> AdvisoryBank = new()
    {
        // [AEGIS-LANGUAGE-STATES-01] PR.AA-01 é o ciclo de vida de identidades e credenciais de usuários, serviços
        // e dispositivos. O texto anterior reduzia o controle inteiro a "MFA para administradores" — um exemplo.
        ["PR.AA-01"] = new AdvisoryDraft(
            "Controlar o ciclo de vida de identidades e credenciais",
            "[Simulado] Identidades e credenciais sem ciclo de vida controlado — contas sem responsável, credenciais " +
            "de aplicações que ninguém revisa, acesso privilegiado sem segundo fator — permitem acesso indevido " +
            "difícil de perceber. O efeito concreto neste ambiente depende da avaliação dos dados coletados.",
            "1. Inventarie as identidades de usuários, serviços/aplicações e dispositivos, cada uma com responsável.\n" +
            "2. Defina e registre criação, alteração e remoção (entrada, mudança de função e saída).\n" +
            "3. Exija MFA, começando pelas contas privilegiadas (Conditional Access em Report-only, depois On).\n" +
            "4. Revise e rotacione credenciais de aplicações e contas de serviço; remova as sem uso comprovado.\n" +
            "5. Guarde as revisões e o relatório de métodos de autenticação como evidência para a reavaliação."),
        ["PR.DS-01"] = new AdvisoryDraft(
            "Cifrar dados em repouso nos endpoints e eliminar tráfego em claro",
            "Endpoints sem criptografia de disco e tráfego não cifrado expõem dados sensíveis a exfiltração " +
            "em caso de perda/roubo do dispositivo ou de interceptação de rede — uma falha direta de confidencialidade.",
            "1. Force BitLocker (Windows) / FileVault (macOS) via política de MDM em 100% da frota.\n" +
            "2. Publique o status de criptografia no inventário e bloqueie o acesso de dispositivos não cifrados.\n" +
            "3. Imponha TLS 1.2+ nos serviços internos; desative protocolos em claro (HTTP, FTP, Telnet).\n" +
            "4. Ative DLP para monitorar e barrar a saída de dados sensíveis não cifrados."),
        ["PR.PS-01"] = new AdvisoryDraft(
            "Aplicar baseline de hardening CIS e zerar o backlog de patches críticos",
            "Plataformas fora do baseline de configuração e com patches críticos pendentes ampliam a superfície " +
            "de ataque: cada CVE crítica não corrigida é uma porta conhecida para execução remota de código.",
            "1. Adote o CIS Benchmark da plataforma como baseline e meça a conformidade (meta ≥ 80%).\n" +
            "2. Priorize e aplique todos os patches de severidade crítica dentro do SLA.\n" +
            "3. Automatize a gestão de patches (WSUS/Intune/gerenciador equivalente) com janelas de manutenção.\n" +
            "4. Monitore o desvio de configuração e reconcilie continuamente contra o baseline."),
        ["PR.IR-01"] = new AdvisoryDraft(
            "Impor política de firewall default-deny e microssegmentar a rede",
            "Sem uma postura default-deny, a rede confia por omissão: um host comprometido alcança livremente " +
            "outros ativos, transformando um incidente pontual em movimento lateral irrestrito.",
            "1. Configure o firewall com regra final default-deny; libere apenas fluxos explicitamente necessários.\n" +
            "2. Microssegmente por zonas (identidade, dados, OT) para conter o raio de explosão.\n" +
            "3. Revise e remova regras permissivas legadas (any-any).\n" +
            "4. Registre e alerte sobre os deny para detectar varredura e movimento lateral."),
    };

    /// <summary>Recomendação genérica para um controle fora do banco canned — ancorada no próprio código.</summary>
    // [AEGIS-LANGUAGE-STATES-01] O fallback atribuía ao Microsoft Secure Score o resultado de um controle NIST
    // ("reduz o Secure Score") e afirmava exposição sem evidência. São escalas distintas.
    private static AdvisoryDraft FallbackAdvisory(string code) => new(
        $"Fechar a lacuna do controle {code}",
        $"[Simulado] O controle {code} está não conforme na avaliação AEGIS deste ambiente. Enquanto a evidência " +
        "exigida não for apresentada, a lacuna permanece aberta; o efeito concreto no ambiente depende de avaliação.",
        $"[Simulado · configure Ai:ApiKey para texto real] Recomendação para {code}:\n" +
        "1. Identifique a evidência técnica exigida pela subcategoria NIST.\n" +
        "2. Implemente/ajuste o controle na plataforma correspondente.\n" +
        "3. Colete a telemetria ou o documento que comprove a implementação efetiva.\n" +
        "4. Reavalie o controle no AEGIS com a nova evidência.");

    /// <summary>[AEGIS-NIST-AI-ASSIST-01] Assistência NIST simulada: demonstração por regras fixas, marcada "[Simulado]".</summary>
    public Task<AegisScore.Application.Nist.NistAssistDraft> AssistNistAsync(
        AegisScore.Application.Nist.NistAssistPrompt request, CancellationToken ct)
        => Task.FromResult(StubNistAssist.Draft(request));

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
