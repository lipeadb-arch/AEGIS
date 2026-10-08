using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Nist;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Assistência NIST do motor SIMULADO: regras fixas sobre as fontes que o servidor montou, sem
/// provedor, sem tokens e SEM análise real — tudo marcado "[Simulado]". Nunca descreve o conteúdo de documento: só repete o
/// que a plataforma já registrou (trecho literal validado, resultado do KNIGHT, observação do analista) e diz o que não foi
/// examinado. Sugere nível apenas por uma regra fixa e declarada sobre procedimentos realizados, citando-os — o suficiente
/// para exercitar a jornada (gerar → conferir fontes → revisar → incorporar) com dados sintéticos.
/// </summary>
internal static class StubNistAssist
{
    private const string Tag = "[Simulado] ";

    public static NistAssistDraft Draft(NistAssistPrompt p) => p.Kind switch
    {
        "Finding" => Finding(p),
        "ExecutiveSummary" => Executive(p),
        _ => Subcategory(p),
    };

    private static NistAssistDraftItem I(string text, params string[] sources) => new(Tag + text, sources);

    private static NistAssistDraft Subcategory(NistAssistPrompt p)
    {
        var t = p.Target;
        var catalog = p.Sources.FirstOrDefault(s => s.Kind == "Catalog");
        var sections = new Dictionary<string, IReadOnlyList<NistAssistDraftItem>>(StringComparer.Ordinal);

        sections["outcome"] = new[]
        {
            I($"O resultado {t.SubcategoryCode} pede que a prática descrita no catálogo exista, seja conhecida e seja mantida no escopo \"{t.ScopeName}\".",
                catalog is null ? Array.Empty<string>() : new[] { catalog.Key }),
        };

        var positive = new List<NistAssistDraftItem>();
        var negative = new List<NistAssistDraftItem>();
        var unproven = new List<NistAssistDraftItem>();
        foreach (var s in p.Sources)
        {
            switch (s.Kind)
            {
                case "KnightIndicator" when s.Status is "Aprovado" or "Mitigado por controle compensatório":
                    positive.Add(I($"Controle técnico \"{s.Title}\" com resultado {s.Status}" + (s.IsDemo ? " (cenário de demonstração)" : "") +
                                   " — apoio técnico, não comprova sozinho a prática.", s.Key));
                    break;
                case "KnightIndicator" when s.Status == "Reprovado":
                    negative.Add(I($"Controle técnico \"{s.Title}\" reprovado" + (s.IsDemo ? " (cenário de demonstração)" : "") + ".", s.Key));
                    break;
                case "Document" when s.ContentExamined && s.Basis == NistAssistBasis.Fact:
                    positive.Add(I($"Há trecho literal validado do documento \"{s.Title}\" para esta subcategoria.", s.Key));
                    break;
                case "Document" when !s.ContentExamined:
                    unproven.Add(I($"O documento \"{s.Title}\" está vinculado, mas o conteúdo não foi examinado pela assistência (sem trecho ou análise disponível).", s.Key));
                    break;
                case "Procedure" when s.Status is "Satisfatório":
                    positive.Add(I($"Procedimento realizado com conclusão satisfatória: {Short(s.Title)}.", s.Key));
                    break;
                case "Procedure" when s.Status is "Insatisfatório" or "Parcialmente satisfatório":
                    negative.Add(I($"Procedimento realizado com conclusão {s.Status!.ToLowerInvariant()}: {Short(s.Title)}.", s.Key));
                    break;
                case "Procedure" when s.Status is "Planejado" or "Em execução":
                    unproven.Add(I($"Procedimento ainda sem resultado: {Short(s.Title)} — planejar não comprova a realização.", s.Key));
                    break;
            }
            if (s.Basis == NistAssistBasis.Unconfirmed)
                unproven.Add(I($"\"{s.Title}\" é conteúdo herdado ou importado que aguarda confirmação humana.", s.Key));
        }
        if (!p.Sources.Any(s => s.Kind == "Procedure" && s.Status is "Satisfatório" or "Insatisfatório" or "Parcialmente satisfatório" or "Inconclusivo"))
            unproven.Add(I("Nenhum procedimento de avaliação foi realizado nesta rodada para esta subcategoria."));

        var justification = positive.Concat(negative).Take(4)
            .Select(x => new NistAssistDraftItem(x.Text, x.Sources)).ToList();
        var evaluation = p.Sources.FirstOrDefault(s => s.Kind == "Evaluation");
        if (evaluation is not null)
            justification.Add(I("O registro do analista nesta rodada foi considerado como relato, não como fato verificado.", evaluation.Key));

        sections["justification"] = justification;
        sections["supporting"] = positive;
        sections["contradicting"] = negative;
        sections["unproven"] = unproven;
        sections["questions"] = new[]
        {
            I($"Quem é responsável pela prática de {t.SubcategoryCode} e como a responsabilidade foi formalizada?"),
            I("Com que periodicidade a prática é executada ou revisada, e onde fica o registro da última execução?"),
            I("Que exceções existem hoje e como foram aprovadas?"),
        };
        sections["recommendations"] = new[]
        {
            I("Formalizar responsável, periodicidade e registro de execução da prática no escopo avaliado."),
            I("Vincular à subcategoria a evidência que demonstra a execução (registro, ata, relatório ou resultado técnico)."),
        };
        sections["risks"] = new[]
        {
            I("Sem registro de execução, a prática pode existir só no papel e a avaliação fica sem base verificável (orientação geral)."),
        };
        sections["criteria"] = new[]
        {
            I("Responsável nomeado e periodicidade definida em documento vigente."),
            I("Registro datado da última execução vinculado como evidência nesta rodada."),
        };

        var procedures = new List<NistAssistDraftProcedure>
        {
            new("Examine", Tag + "Examinar o documento ou registro que formaliza a prática e conferir data de aprovação e responsável.", Array.Empty<string>()),
            new("Interview", Tag + "Entrevistar o responsável pela prática sobre execução, periodicidade e exceções.", Array.Empty<string>()),
            new("Test", Tag + "Selecionar uma amostra recente e verificar se a prática foi executada conforme o definido.", Array.Empty<string>()),
        };

        // Regra FIXA declarada (não é análise): só com procedimento REALIZADO e concluído, citando-o.
        NistAssistDraftLevel? level = null;
        var performed = p.Sources.Where(s => s.Kind == "Procedure" && s.Basis == NistAssistBasis.AnalystReport
                                             && s.Status is "Satisfatório" or "Parcialmente satisfatório" or "Insatisfatório").ToList();
        if (!t.NotApplicable && performed.Count > 0)
        {
            var value = performed.Any(s => s.Status == "Insatisfatório") ? 1 : performed.Any(s => s.Status == "Parcialmente satisfatório") ? 2 : 3;
            level = new NistAssistDraftLevel(value.ToString(System.Globalization.CultureInfo.InvariantCulture), performed.Select(s => s.Key).ToList(),
                Tag + "Regra fixa do motor simulado sobre a conclusão dos procedimentos realizados (insatisfatório → 1, parcial → 2, satisfatório → 3). Não é análise.");
        }
        return new NistAssistDraft(sections, level, procedures, Simulated: true);
    }

    private static NistAssistDraft Finding(NistAssistPrompt p)
    {
        var finding = p.Sources.FirstOrDefault(s => s.Kind == "Finding");
        var plan = p.Sources.FirstOrDefault(s => s.Kind == "Plan");
        var catalog = p.Sources.FirstOrDefault(s => s.Kind == "Catalog");
        var evidence = p.Sources.Where(s => s.Kind is "KnightIndicator" or "Document" or "Manual" or "Procedure" && s.ContentExamined).Select(s => s.Key).ToArray();
        var f = finding is null ? Array.Empty<string>() : new[] { finding.Key };
        var sections = new Dictionary<string, IReadOnlyList<NistAssistDraftItem>>(StringComparer.Ordinal);

        if (NistAssistSections.NormalizeFocus(p.Focus) == NistAssistSections.FocusTreatment)
        {
            sections["treatment"] = new[]
            {
                I($"Tratar o achado \"{p.Target.FindingTitle}\" formalizando a prática que falta e registrando a sua execução no escopo.", f),
            };
            sections["steps"] = new[]
            {
                I("Confirmar com o responsável a condição observada e o alcance no escopo."),
                I("Definir a correção, o responsável e o prazo no plano de tratamento."),
                I("Executar a correção e guardar o registro datado da execução."),
                I("Pedir a validação humana com a evidência referenciada; a reavaliação da subcategoria é um ato separado."),
            };
            sections["criteria"] = new[]
            {
                I("Registro datado da correção, com responsável identificado."),
                I("Evidência anexada ao plano que permita a outra pessoa conferir a correção."),
            };
            sections["confirmations"] = new[]
            {
                I("Confirmar com a equipe técnica nomes de configuração, versões e licenças antes de executar qualquer etapa técnica."),
            };
            if (plan is not null)
                sections["treatment"] = sections["treatment"].Append(I("Já existe plano para este achado: a proposta só ajusta o texto se a pessoa decidir.", plan.Key)).ToList();
        }
        else
        {
            sections["explanation"] = new[]
            {
                I($"O achado registra: {p.Target.FindingTitle}.", f),
            }.Concat(evidence.Take(2).Select(k => I("A evidência citada no registro sustenta a condição observada.", k))).ToList();
            sections["relevance"] = new[]
            {
                I($"A lacuna afeta o resultado esperado de {p.Target.SubcategoryCode}.", catalog is null ? Array.Empty<string>() : new[] { catalog.Key }),
            };
            sections["impacts"] = new[]
            {
                I("Impacto possível no limite da condição registrada; não há incidente demonstrado nas fontes.", f),
            };
            sections["verifications"] = new[]
            {
                I("Verificar se a condição se repete nos demais ativos ou unidades do escopo."),
                I("Obter registro datado que confirme quando a condição começou."),
            };
        }
        return new NistAssistDraft(sections, null, Array.Empty<NistAssistDraftProcedure>(), Simulated: true);
    }

    private static NistAssistDraft Executive(NistAssistPrompt p)
    {
        string? M(string label) => p.Metrics.FirstOrDefault(m => m.Label == label)?.Key;
        string[] Keys(params string?[] keys) => keys.Where(k => k is not null).Select(k => k!).ToArray();
        var sections = new Dictionary<string, IReadOnlyList<NistAssistDraftItem>>(StringComparer.Ordinal)
        {
            ["situation"] = new[] { I("A situação atual e o alvo estão nos indicadores do AEGIS desta rodada; a média considera só subcategorias confirmadas por revisão humana.",
                Keys(M("Atual (média)"), M("Alvo (média)"), M("Lacuna média"))) },
            ["coverage"] = new[] { I("A cobertura mostra quanto do catálogo foi avaliado ou declarado não aplicável; o restante não entra nas médias.",
                Keys(M("Cobertura"), M("Avaliadas"), M("Aguardando confirmação"))) },
            ["gaps"] = p.Sources.Where(s => s.Kind == "Gap").Take(3).Select(s => I($"Lacuna confirmada em {s.Title}.", s.Key)).ToList(),
            ["risks"] = p.Sources.Where(s => s.Kind == "Finding").Take(3).Select(s => I($"Risco registrado como achado: {s.Title}.", s.Key)).ToList(),
            ["treatment"] = new[] { I("O andamento do tratamento segue os planos registrados para os achados.", Keys(M("Planos ativos"), M("Planos atrasados"))) },
            ["limitations"] = p.Sources.Where(s => s.Kind == "Limitation").Take(3).Select(s => I(s.Content ?? s.Title, s.Key)).ToList(),
            ["priorities"] = p.Priorities.Select(x => I(x.Text, x.Key)).ToList(),
            ["nextSteps"] = new[] { I("Confirmar as subcategorias pendentes e acompanhar os planos atrasados na próxima reunião de gestão.") },
        };
        return new NistAssistDraft(sections, null, Array.Empty<NistAssistDraftProcedure>(), Simulated: true);
    }

    private static string Short(string s) => s.Length <= 140 ? s : s[..140] + "…";
}
