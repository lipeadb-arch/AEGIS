using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Application.Remediation;

// ============================================================================
//  [AEGIS-MVP-PRODUCT-03] Decidir se uma nova avaliação COMPROVA a correção
// ============================================================================
// Este é o ponto do pacote em que é mais fácil mentir com dados verdadeiros. Duas avaliações do mesmo tenant
// mostrando 12 e depois 8 objetos afetados NÃO provam, sozinhas, que quatro objetos foram corrigidos: podem
// ser fontes diferentes, regras diferentes, uma coleta parcial, ou simplesmente quatro objetos que deixaram
// o diretório por outro motivo enquanto outros entraram.
//
// A função abaixo é PURA e conservadora. Ela recusa a comparação antes de aceitá-la, e quando aceita,
// distingue REDUÇÃO OBSERVADA de EXPOSIÇÃO ENCERRADA — e só afirma "estes objetos saíram" quando os DOIS
// conjuntos foram preservados e declarados completos.

/// <summary>Os fatos de UMA avaliação relevantes para julgar um indicador — nada além do necessário.</summary>
/// <param name="RunId">Identificador da avaliação.</param>
/// <param name="SourceType">Fonte concreta da coleta — comparar Demo com coleta real é inválido.</param>
/// <param name="Mode">Demo ou Live, o mesmo eixo visto pelo outro lado.</param>
/// <param name="CatalogVersion">Versão do catálogo de regras que produziu o veredito.</param>
/// <param name="CollectedAt">Instante da coleta — define a ordem temporal.</param>
/// <param name="IndicatorFound"><c>false</c> quando o indicador nem consta desta avaliação.</param>
/// <param name="Status">Veredito do indicador nesta avaliação.</param>
/// <param name="AffectedCount">Quantidade afetada apurada nesta avaliação.</param>
/// <param name="DetailComplete"><c>true</c> quando a lista preservada cobre TODO o conjunto contado.</param>
/// <param name="AffectedExternalIds">Identificadores preservados; vazio quando não há detalhe.</param>
/// <param name="Capabilities">Estado por capacidade — usado para a suficiência POR INDICADOR.</param>
public sealed record KnightRunEvidence(
    Guid RunId,
    KnightSourceType SourceType,
    KnightAssessmentMode Mode,
    string CatalogVersion,
    DateTimeOffset CollectedAt,
    bool IndicatorFound,
    KnightIndicatorStatus Status,
    int AffectedCount,
    bool DetailComplete,
    IReadOnlyCollection<string> AffectedExternalIds,
    IReadOnlyList<KnightCapabilityStatus> Capabilities);

/// <summary>Veredito da comparação: o desfecho, o que sustenta a conclusão e o que foi observado dos dois lados.</summary>
public sealed record KnightValidationVerdict(
    ActionPlanValidationOutcome Outcome,
    string Rationale,
    int? ObservedBefore,
    int? ObservedAfter,
    int? ObjectsNoLongerPresent,
    bool ComparedBySets);

/// <summary>Comparação DETERMINÍSTICA entre a avaliação de origem e a apresentada como evidência.</summary>
public static class KnightValidationEvaluator
{
    /// <summary>
    /// Julga se <paramref name="evidence"/> comprova melhora no indicador em relação a <paramref name="origin"/>.
    /// Toda recusa produz <see cref="ActionPlanValidationOutcome.EvidenceInsufficient"/> com o motivo explícito —
    /// nunca um desfecho neutro que a tela pudesse ler como "não piorou, então melhorou".
    /// </summary>
    public static KnightValidationVerdict Evaluate(
        string indicatorId, KnightRunEvidence origin, KnightRunEvidence evidence)
    {
        // (0) A mesma avaliação não pode comprovar a si mesma.
        if (origin.RunId == evidence.RunId)
            return Insufficient(
                "A avaliação apresentada é a MESMA que originou o achado. Uma coleta não comprova a correção " +
                "de um problema que ela própria revelou.");

        // (1) Ordem temporal. Uma coleta anterior (ou simultânea) descreve o mundo antes do trabalho.
        if (evidence.CollectedAt <= origin.CollectedAt)
            return Insufficient(
                "A avaliação apresentada é anterior (ou simultânea) à que originou o achado. Ela descreve o " +
                "estado de antes do trabalho e não pode comprová-lo.");

        // (2) Fontes comparáveis. Demonstração é cenário sintético; compará-la com coleta real (ou o contrário)
        //     produziria um número que parece prova e não é.
        if (origin.SourceType != evidence.SourceType || origin.Mode != evidence.Mode)
            return Insufficient(
                "As duas avaliações vêm de fontes diferentes (ou uma é demonstrativa e a outra é coleta real). " +
                "Números de origens distintas não se subtraem.");

        // (3) Regras comparáveis. Catálogos diferentes podem contar coisas diferentes com o mesmo nome.
        if (!string.Equals(origin.CatalogVersion, evidence.CatalogVersion, StringComparison.Ordinal))
            return Insufficient(
                $"As avaliações usaram versões diferentes das regras ({origin.CatalogVersion} × " +
                $"{evidence.CatalogVersion}). A variação pode vir da mudança de critério, não do ambiente.");

        // (4) O indicador precisa existir nos dois lados.
        if (!origin.IndicatorFound || !evidence.IndicatorFound)
            return Insufficient(
                "O achado não consta em uma das avaliações comparadas — não há o que comparar.");

        // (5) Suficiência POR INDICADOR. Uma parcialidade global causada por capacidade que este indicador não
        //     consome NÃO invalida a evidência; a falta da capacidade que ele consome invalida.
        var missing = KnightIndicatorEvidence.MissingFor(indicatorId, evidence.Capabilities);
        if (missing.Count > 0)
        {
            var nomes = string.Join(", ", missing.Select(m => $"{m.Capability} ({m.Outcome})"));
            return Insufficient(
                $"A avaliação apresentada não coletou o que este achado exige: {nomes}. Ausência de dado não " +
                "comprova melhora.");
        }

        if (!KnightIndicatorEvidence.IsConclusiveVerdict(evidence.Status))
            return Insufficient(
                $"O achado ficou como '{evidence.Status}' na avaliação apresentada. Um indicador sem veredito " +
                "reduz a cobertura e não comprova correção.");

        var before = origin.AffectedCount;
        var after = evidence.AffectedCount;

        // (6) Conjuntos: só quando OS DOIS lados preservaram a lista INTEIRA é possível falar de objetos que
        //     saíram. Fora disso, o produto fala de quantidade — e diz que fala de quantidade.
        var setsUsable = origin.DetailComplete && evidence.DetailComplete;
        int? leftTheSet = null;
        if (setsUsable)
        {
            var remaining = new HashSet<string>(evidence.AffectedExternalIds, StringComparer.OrdinalIgnoreCase);
            leftTheSet = origin.AffectedExternalIds.Count(id => !remaining.Contains(id));
        }

        // (7) Exposição encerrada: o veredito deixou de sinalizar o achado.
        if (evidence.Status == KnightIndicatorStatus.Passed)
        {
            var extra = setsUsable
                ? $" Os {leftTheSet} objeto(s) da lista de origem não aparecem mais no conjunto preservado da nova coleta."
                : " A conclusão se apoia no veredito da regra; o detalhe dos objetos não estava preservado nos dois lados.";
            return new KnightValidationVerdict(
                ActionPlanValidationOutcome.ExposureCleared,
                $"A nova avaliação, {Describe(evidence)}, não sinaliza mais este achado (antes: {before} " +
                $"objeto(s) afetado(s)).{extra}",
                before, after, leftTheSet, setsUsable);
        }

        // (8) Redução observada — nunca "resolvido". O achado continua exposto.
        if (after < before)
        {
            var extra = setsUsable
                ? $" {leftTheSet} objeto(s) da lista de origem não constam mais do conjunto preservado; os demais permanecem."
                : " Sem as duas listas completas, é possível afirmar a variação da QUANTIDADE, não quais objetos foram corrigidos.";
            return new KnightValidationVerdict(
                ActionPlanValidationOutcome.ReductionObserved,
                $"A quantidade afetada caiu de {before} para {after} na avaliação de {Describe(evidence)}, mas o " +
                $"achado continua exposto.{extra}",
                before, after, leftTheSet, setsUsable);
        }

        var direcao = after > before ? "subiu" : "permaneceu";
        return new KnightValidationVerdict(
            ActionPlanValidationOutcome.NoChangeObserved,
            $"A quantidade afetada {direcao} em {after} na avaliação de {Describe(evidence)} (antes: {before}). " +
            "A nova coleta não mostra melhora neste achado.",
            before, after, leftTheSet, setsUsable);
    }

    private static KnightValidationVerdict Insufficient(string rationale) =>
        new(ActionPlanValidationOutcome.EvidenceInsufficient, rationale, null, null, null, ComparedBySets: false);

    private static string Describe(KnightRunEvidence e) =>
        e.CollectedAt.ToUniversalTime().ToString("dd/MM/yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
}
