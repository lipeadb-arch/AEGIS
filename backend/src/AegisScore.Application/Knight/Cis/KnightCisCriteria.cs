using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Cis;

// ============================================================================
//  [AEGIS-KNIGHT-CIS-01] Confirmação do critério da EDIÇÃO CIS — separada do resultado técnico do KNIGHT
// ============================================================================
// O resultado de uma regra do KNIGHT é técnico: diz o que a coleta mostrou contra o critério da PRÓPRIA regra. Ele só vira
// Conforme, Não conforme ou Não aplicável numa recomendação CIS quando o critério daquela edição e seção foi conferido numa
// fonte oficial do CIS e o vínculo da regra foi revisado contra ele. Nada abaixo é inferido:
//   • a classificação "integral" do catálogo de referência, o perfil registrado no Monkey365 e a versão do catálogo da
//     execução NÃO confirmam o critério oficial — são rastreio e mapeamento, ditos como tais;
//   • cada confirmação registra a edição, a seção, a fonte oficial consultada (documento e localização), quem conferiu,
//     quando, as versões do catálogo KNIGHT revisadas e as regras cujo vínculo foi conferido contra o texto da edição.
// Sem confirmação, a recomendação fica "Não avaliado" com o motivo abaixo, e a leitura do KNIGHT (regra, resultado e
// evidência) continua visível ao lado.

/// <summary>Uma conferência do critério oficial de UMA recomendação (edição × seção) numa fonte oficial do CIS.</summary>
public sealed record KnightCisCriterionConfirmation(
    string RecommendationId, string OfficialSource, string Location, string ConsultedOn, string ReviewedBy,
    IReadOnlyList<string> ReviewedCatalogVersions, IReadOnlyList<string> ReviewedRules, string? Note);

public static class KnightCisCriteria
{
    public const string NotConfirmedReason = "Critério da edição CIS ainda não confirmado";

    /// <summary>
    /// Fontes oficiais do CIS efetivamente consultadas para as confirmações abaixo. Vazio: as edições oficiais (PDF do CIS)
    /// são distribuídas mediante cadastro no site do CIS, que não foi feito; nenhum critério foi conferido no texto oficial.
    /// </summary>
    public static IReadOnlyList<string> OfficialSourcesConsulted { get; } = Array.Empty<string>();

    /// <summary>Confirmações registradas. Vazio até alguém conferir o texto oficial de cada edição.</summary>
    public static IReadOnlyList<KnightCisCriterionConfirmation> Registered { get; } = Array.Empty<KnightCisCriterionConfirmation>();

    public static IReadOnlyDictionary<string, KnightCisCriterionConfirmation> Index(IEnumerable<KnightCisCriterionConfirmation> confirmations) =>
        confirmations.GroupBy(c => c.RecommendationId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
}
