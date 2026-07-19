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
    /// Triagem determinística por tema. Antes devolvia SEMPRE os mesmos dois códigos canned
    /// (GV.PO-01/GV.RR-01) — e nenhum dos dois tem regra no <c>aegis_assessment_rules.json</c>, então a
    /// segunda passada do RAG caía direto no fallback e a feature NUNCA era exercitada em DEV. Aqui os
    /// temas do texto roteiam para controles que TÊM regra, e a esteira documental roda inteira sem rede.
    /// </summary>
    public Task<DocumentAnalysis> AnalyzeDocumentAsync(DocumentAnalysisRequest request, CancellationToken ct)
    {
        var text = (request.DocumentText ?? "").ToLowerInvariant();
        var claims = new List<DocumentClaim>();

        void Detect(string code, string claim, double confidence, params string[] terms)
        {
            if (terms.Any(t => text.Contains(t, StringComparison.Ordinal)))
                claims.Add(new DocumentClaim(code, claim, confidence));
        }

        Detect("PR.AA-01", "Exigência de autenticação multifator e revisão de contas privilegiadas.", 0.68,
            "privilegiad", "multifator", "mfa", "autenticacao", "autenticação");
        Detect("RC.RP-01", "Menção a plano de continuidade / recuperação de negócios.", 0.55,
            "continuidade", "recuperacao", "recuperação", "backup");
        Detect("PR.DS-01", "Tratamento de proteção e criptografia de dados.", 0.60,
            "criptograf", "dados sensiveis", "dados sensíveis");
        Detect("GV.PO-01", "Menção a política de segurança da informação aprovada pela direção.", 0.72,
            "politica", "política", "diretriz");
        Detect("GV.RR-01", "Definição de papéis e responsabilidades de segurança.", 0.65,
            "responsavel", "responsável", "papeis", "papéis", "comite", "comitê");

        // Sem nenhum tema reconhecido, mantém o par histórico — a demo nunca fica sem claim algum.
        if (claims.Count == 0)
        {
            claims.Add(new DocumentClaim("GV.PO-01", "Menção a política de segurança da informação aprovada pela direção.", 0.72));
            claims.Add(new DocumentClaim("GV.RR-01", "Definição de papéis e responsabilidades de segurança.", 0.65));
        }

        return Task.FromResult(new DocumentAnalysis(
            Summary: $"[Simulado] Leitura de '{request.FileName ?? "documento"}': {claims.Count} controle(s) " +
                     "NIST CSF 2.0 endereçado(s) pelo texto.",
            Claims: claims));
    }

    /// <summary>
    /// Segunda passada determinística: pontua o trecho pela presença dos TERMOS DE EXECUÇÃO que separam
    /// política escrita de controle operante ("responsável", "periodicidade", "registro"…). Não é NLP —
    /// é o suficiente para o pipeline de duas passadas rodar sem rede e para os testes exercitarem a
    /// fronteira Coberto × Parcial com dados previsíveis.
    /// </summary>
    public Task<DocumentControlVerdict> EvaluateDocumentControlAsync(
        DocumentControlEvaluationRequest request, CancellationToken ct)
    {
        var excerpt = (request.DocumentExcerpt ?? "").ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(excerpt))
            return Task.FromResult(new DocumentControlVerdict(
                0.0, $"[Simulado] Nenhum trecho do documento endereça {request.SubcategoryCode}."));

        // Sinais de EXECUÇÃO (o controle roda) contra sinais de INTENÇÃO (o controle é desejado).
        //
        // Casamento por RADICAL, não por palavra inteira: uma PSI real escreve "responsabilidade",
        // "registradas", "revisada" — flexões que a lista de palavras exatas não pegava, fazendo o Stub
        // rebaixar para Parcial um documento com aprovação executiva, sanções e RACI. Errar para baixo é
        // melhor que inflar, mas continua sendo errar.
        string[] execution = ["responsab", "responsav", "accountab", "periodic", "trimestr", "mensal",
                              "anualmente", "registr", "evidenc", "auditor", "revis", "aprovad",
                              "sancao", "sanção", "disciplinar", "comite", "comitê", "matriz raci"];
        string[] intent = ["deve", "deverá", "devera", "recomenda", "pretende", "objetivo", "futuro"];

        var executionHits = execution.Count(t => excerpt.Contains(t, StringComparison.Ordinal));
        var intentOnly = intent.Any(t => excerpt.Contains(t, StringComparison.Ordinal)) && executionHits == 0;

        // 0.75 passa do limiar de cobertura (0.7); 0.45 fica em Parcial. A fronteira é o ponto do teste.
        var confidence = intentOnly ? 0.45 : Math.Min(0.75, 0.45 + 0.10 * executionHits);

        var rationale = intentOnly
            ? $"[Simulado] {request.SubcategoryCode}: o texto DECLARA a intenção, mas não nomeia responsável, " +
              "periodicidade nem registro de execução — evidência parcial."
            : $"[Simulado] {request.SubcategoryCode}: o trecho cita {executionHits} elemento(s) de execução " +
              "(responsável/periodicidade/registro), sustentando cobertura documental.";

        return Task.FromResult(new DocumentControlVerdict(confidence, rationale));
    }

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

    /// <summary>
    /// Copiloto GRC determinístico (sem LLM): devolve uma orientação canned coerente com o FOCO daquele
    /// escopo — o suficiente para exercitar o fluxo /auditor/chat ponta a ponta sem chave nem tokens.
    /// </summary>
    public Task<AuditorReply> ChatAsync(AuditorChatRequest request, CancellationToken ct)
    {
        var msg = request.UserMessage.ToLowerInvariant();

        // Roteamento de Intenção determinístico por palavra-chave: pedido de auditoria/diagnóstico/lacunas
        // → START_INTERVIEW (a resposta já é a 1ª pergunta); qualquer outra coisa → COPILOT.
        var wantsInterview =
            msg.Contains("auditar") || msg.Contains("auditoria") || msg.Contains("diagnóstic") ||
            msg.Contains("diagnostic") || msg.Contains("lacuna") || msg.Contains("entrevista") ||
            msg.Contains("gap") || msg.Contains("fechar");

        if (wantsInterview)
        {
            var (question, code) = FirstInterviewQuestion(request.Scope);
            return Task.FromResult(new AuditorReply(
                question, request.Scope, AuditorIntent.StartInterview, new AuditorInterviewSeed(code)));
        }

        var reply =
            $"[Copiloto GRC · simulado] No escopo {request.Scope}, o foco é {ScopeFocus(request.Scope)} " +
            $"Sua mensagem: \"{request.UserMessage}\". (Motor de IA simulado — configure Ai:ApiKey para respostas reais.)";
        return Task.FromResult(new AuditorReply(reply, request.Scope, AuditorIntent.Copilot));
    }

    /// <summary>Foco canned por escopo (usado na resposta COPILOT simulada).</summary>
    private static string ScopeFocus(AuditorScope scope) => scope switch
    {
        AuditorScope.Global => "a visão executiva do Secure Score: priorize as Funções com mais controles NonCompliant.",
        AuditorScope.Protect => "PR.AA/PR.DS: confirme MFA privilegiado (100%) e criptografia de endpoint (≥95%).",
        AuditorScope.Detect => "DE.AE/DE.CM: verifique cobertura de logs críticos (≥95%) e ativos críticos monitorados.",
        AuditorScope.Respond => "RS.MA/RS.MI: valide MTTA (≤30 min), MTTR (≤120 min) e isolamento automatizado.",
        AuditorScope.Recover => "RC.RP: confirme backups imutáveis, íntegros (Valid) e RTO atendido.",
        AuditorScope.Govern => "GV.SC/GV.RR: audite fornecedores com acesso à rede e a revisão periódica de administradores.",
        AuditorScope.Identify => "ID.AM: revise o inventário — EDR ativo e sistemas operacionais suportados.",
        _ => "a postura geral do Aegis Score.",
    };

    /// <summary>Primeira pergunta canned do fluxo NIST por escopo (+ a subcategoria investigada).</summary>
    private static (string Question, string? Code) FirstInterviewQuestion(AuditorScope scope) => scope switch
    {
        AuditorScope.Protect => ("Qual a cobertura atual de MFA para contas privilegiadas e o Conditional Access está aplicado (PR.AA)?", "PR.AA-01"),
        AuditorScope.Detect => ("Qual a cobertura de logs das fontes críticas e há ativos críticos fora do monitoramento (DE.CM)?", "DE.CM-01"),
        AuditorScope.Respond => ("Qual o MTTA médio dos incidentes e a cobertura de threat hunting (RS.MA)?", "RS.MA-01"),
        AuditorScope.Recover => ("Os backups são imutáveis, testados (integridade Valid) e o RTO é atendido (RC.RP)?", "RC.RP-01"),
        AuditorScope.Identify => ("O inventário de ativos está completo, com EDR ativo e sistemas suportados (ID.AM)?", "ID.AM-01"),
        AuditorScope.Govern => ("Como a organização audita os fornecedores de TI com acesso à rede corporativa (GV.SC)?", "GV.SC-01"),
        _ => ("Por qual Função NIST você quer começar o diagnóstico de lacunas?", null),
    };

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
        ["PR.AA-01"] = new AdvisoryDraft(
            "Impor MFA em todas as contas privilegiadas via Conditional Access",
            "Contas administrativas sem MFA são o vetor nº 1 de comprometimento de identidade: uma única " +
            "credencial privilegiada vazada concede movimento lateral e escalonamento imediatos, sem barreira adicional.",
            "1. No Entra ID, crie uma política de Conditional Access mirando o grupo de contas privilegiadas.\n" +
            "2. Exija 'Grant access → Require multifactor authentication'.\n" +
            "3. Publique em modo Report-only, valide os sign-ins e então mude para On (Enforce).\n" +
            "4. Bloqueie a autenticação legada (POP/IMAP/SMTP), que ignora o MFA.\n" +
            "5. Evidencie 100% de cobertura no relatório de métodos de autenticação."),
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
    private static AdvisoryDraft FallbackAdvisory(string code) => new(
        $"Fechar a lacuna do controle {code}",
        $"[Simulado] O controle {code} está não-conforme no ledger do tenant. A ausência de evidência técnica " +
        "deste controle mantém uma lacuna de postura que reduz o Secure Score e eleva a exposição associada.",
        $"[Simulado · configure Ai:ApiKey para texto real] Recomendação para {code}:\n" +
        "1. Identifique a evidência técnica exigida pela subcategoria NIST.\n" +
        "2. Implemente/ajuste o controle na plataforma correspondente.\n" +
        "3. Colete a telemetria que comprove a implementação efetiva.\n" +
        "4. Reavalie o controle no Aegis Score para elevar o score.");

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
