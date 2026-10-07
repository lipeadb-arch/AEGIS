using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Nist;

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Uma seção da resposta da assistência: título e dica na tela, descrição ao motor e a regra de
/// sustentação. <paramref name="RequiresSources"/>: afirmação sobre o ambiente — item sem fonte válida do contexto é
/// descartado. <paramref name="SourcePrefixes"/>: que chaves contam (S = fonte, M = indicador, P = prioridade do AEGIS).
/// <paramref name="SourceKinds"/>: quando só certos tipos de fonte sustentam a seção (riscos REGISTRADOS = achados).
/// </summary>
public sealed record NistAssistSectionSpec(
    string Key,
    string Title,
    string Hint,
    string PromptDescription,
    bool RequiresSources,
    IReadOnlyList<string> SourcePrefixes,
    IReadOnlyList<string>? SourceKinds = null);

public static class NistAssistSections
{
    public const string FocusExplain = "Explain";
    public const string FocusTreatment = "Treatment";

    private static readonly string[] S = { "S" };

    private static readonly NistAssistSectionSpec[] Subcategory =
    {
        new("outcome", "O que o resultado esperado exige", "orientação geral, a partir do catálogo",
            "Short plain explanation of what the expected outcome of this subcategory requires. General guidance; may cite the catalog source.", false, S),
        new("justification", "Rascunho de justificativa", "sobre o ambiente — cada trecho cita a fonte",
            "Draft justification for the assessor: what the cited sources show about the current state. Every item MUST cite source keys.", true, S),
        new("supporting", "Evidências favoráveis", "o que as fontes demonstram",
            "What the sources DEMONSTRATE in favor of the outcome. Every item MUST cite source keys.", true, S),
        new("contradicting", "Evidências contraditórias ou desfavoráveis", "o que as fontes contradizem",
            "What the sources CONTRADICT or show as missing/failed. Every item MUST cite source keys.", true, S),
        new("unproven", "O que permanece sem comprovação", "lacunas de comprovação no contexto",
            "What remains WITHOUT proof in this context (claims not backed by examined content, documents not examined, procedures not performed).", false, S),
        new("questions", "Perguntas de entrevista", "orientação geral",
            "Interview questions the assessor can ask to close the proof gaps (one question per item).", false, S),
        new("recommendations", "Recomendações de melhoria", "orientação geral",
            "Improvement recommendations pertinent to the gaps observed. General guidance unless an item cites sources.", false, S),
        new("risks", "Riscos e impactos possíveis", "com fundamento citado ou como orientação geral",
            "POSSIBLE risks and impacts. Cite sources when grounded in the environment; otherwise state them as general guidance. Never claim incidents or damage.", false, S),
        new("criteria", "Critérios verificáveis para avaliar a melhoria", "orientação geral",
            "Verifiable criteria to evaluate whether the improvement was achieved (observable, with the evidence to collect).", false, S),
    };

    private static readonly NistAssistSectionSpec[] FindingExplain =
    {
        new("explanation", "O problema e a condição observada", "sobre o ambiente — cita o achado e as evidências",
            "Explain the registered problem and the observed condition. Every item MUST cite source keys.", true, S),
        new("relevance", "Por que importa", "orientação geral",
            "Why this problem matters for the expected outcome (general guidance, may cite the catalog).", false, S),
        new("impacts", "Possíveis impactos", "com fundamento citado ou como orientação geral",
            "POSSIBLE impacts, limited to what the condition allows to state. Never claim incidents or damage.", false, S),
        new("verifications", "Evidências ou verificações adicionais", "orientação geral",
            "Additional evidence to collect or checks to perform before or during treatment.", false, S),
    };

    private static readonly NistAssistSectionSpec[] FindingTreatment =
    {
        new("treatment", "Proposta de ação de tratamento", "proposta — não é execução nem aprovação",
            "A treatment action proposal for this finding (what to do). It is a proposal only.", false, S),
        new("steps", "Etapas de execução", "orientação geral",
            "Ordered execution steps (one step per item). No invented commands, permissions or admin links.", false, S),
        new("criteria", "Critérios e evidências para validar a correção", "orientação geral",
            "Criteria and evidence needed for a PERSON to validate the correction.", false, S),
        new("confirmations", "Depende de confirmação técnica", "confirmar antes de executar",
            "Specific instructions that depend on technical confirmation in the environment (versions, licenses, configuration names).", false, S),
    };

    private static readonly NistAssistSectionSpec[] Executive =
    {
        new("situation", "Situação atual e alvo", "explica os indicadores do AEGIS",
            "Current and target situation, EXPLAINING the AEGIS indicators. Every item MUST cite metric keys (M…). Do not compute new numbers.", true, new[] { "M" }),
        new("coverage", "Cobertura e base dos resultados", "explica os indicadores do AEGIS",
            "Coverage and the basis of the results (what was evaluated, pending confirmation, not applicable). Cite metric keys (M…).", true, new[] { "M" }),
        new("gaps", "Principais lacunas", "lacunas confirmadas registradas",
            "Main confirmed gaps. Cite the gap sources (S…) or priority keys (P…). A gap is distance to the target, not a failure by itself.", true, new[] { "S", "P" }),
        new("risks", "Riscos registrados", "somente achados registrados",
            "Risks ACTUALLY REGISTERED as findings. Cite the finding sources (S…). Do not add risks that are not registered.", true, S, new[] { "Finding" }),
        new("treatment", "Andamento do tratamento", "explica os indicadores do AEGIS",
            "Treatment progress (plans, overdue, validation). Cite metric keys (M…) or finding sources (S…).", true, new[] { "M", "S" }),
        new("limitations", "Limitações e o que ainda não está comprovado", "base declarada pelo AEGIS",
            "Limitations and what is still not proven. May cite limitation sources (S…) or metrics (M…).", false, new[] { "S", "M" }),
        new("priorities", "Ações prioritárias (ordem do AEGIS)", "a ordem é do critério do AEGIS, não da IA",
            "Priority actions, ONE item per priority key, in the given P order. Every item MUST cite exactly its P key.", true, new[] { "P" }),
        new("nextSteps", "Próximos passos", "orientação geral",
            "Next steps for the management team (general guidance).", false, S),
    };

    public static IReadOnlyList<NistAssistSectionSpec> For(string kind, string? focus) => kind switch
    {
        "Subcategory" => Subcategory,
        "Finding" => string.Equals(focus, FocusTreatment, StringComparison.OrdinalIgnoreCase) ? FindingTreatment : FindingExplain,
        "ExecutiveSummary" => Executive,
        _ => Array.Empty<NistAssistSectionSpec>(),
    };

    /// <summary>Foco do achado normalizado (Explain por padrão).</summary>
    public static string NormalizeFocus(string? focus) =>
        string.Equals((focus ?? "").Trim(), FocusTreatment, StringComparison.OrdinalIgnoreCase) ? FocusTreatment : FocusExplain;

    /// <summary>Seções do resumo executivo que a pessoa aceita (chave → título).</summary>
    public static IReadOnlyList<(string Key, string Title)> ExecutiveKeys => Executive.Select(x => (x.Key, x.Title)).ToList();

    public static string MethodLabel(string method) => NistLabels.Method(method);
}
