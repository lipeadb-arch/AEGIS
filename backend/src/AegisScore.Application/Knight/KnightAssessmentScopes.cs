using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-SCOPE-01] Escopo da avaliação KNIGHT por tenant
// ============================================================================
// O seletor "Compor com" da tela passa a ser o escopo SALVO do tenant: a mesma escolha vale ao recarregar, no painel,
// na prévia e na publicação do consolidado. Fora do escopo não é "sem avaliação" nem "disponível, não incluída": é uma
// decisão registrada de que aquela fonte não compõe a avaliação deste tenant — os controles, achados e limitações dela
// saem dos totais, e a composição diz isso com todas as letras.

/// <summary>Escopo salvo: as fontes candidatas FORA da avaliação consolidada, quando e por quem foi salvo.</summary>
public sealed record KnightAssessmentScopeView(
    IReadOnlyList<KnightSourceType> OutOfScope,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByName)
{
    /// <summary>Sem registro salvo: todas as candidatas estão no escopo.</summary>
    public static KnightAssessmentScopeView Default { get; } = new(Array.Empty<KnightSourceType>(), null, null);

    public bool IsOutOfScope(KnightSourceType source) => OutOfScope.Contains(source);

    /// <summary>Fontes candidatas DENTRO do escopo, na ordem de apresentação.</summary>
    public IReadOnlyList<KnightSourceType> InScope =>
        KnightConsolidatedCandidates.Sources.Where(s => !OutOfScope.Contains(s)).ToList();
}

/// <summary>Leitura e gravação do escopo do tenant do contexto (fail-closed: sem tenant, nada é lido nem gravado).</summary>
public interface IKnightAssessmentScopeService
{
    Task<KnightAssessmentScopeView> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Salva o escopo a partir das fontes DENTRO dele (o mesmo vocabulário do parâmetro <c>sources</c> do consolidado). Toda
    /// candidata ausente da lista fica fora do escopo; fontes que não são candidatas são recusadas antes de chegar aqui.
    /// </summary>
    Task<KnightAssessmentScopeView> SaveAsync(IReadOnlyCollection<KnightSourceType> inScope, string? updatedByName, CancellationToken ct = default);
}

/// <summary>Serialização ÚNICA da lista de fontes fora do escopo (coluna de texto) — ordenada e sem repetição.</summary>
public static class KnightAssessmentScopeFormat
{
    public const int MaxLength = 400;

    public static string Serialize(IEnumerable<KnightSourceType> outOfScope) =>
        string.Join(",", Normalize(outOfScope).Select(s => s.ToString()));

    /// <summary>Lê a coluna; um nome que não é mais candidata (catálogo mudou) é ignorado, nunca vira outra fonte.</summary>
    public static IReadOnlyList<KnightSourceType> Parse(string? stored) =>
        Normalize((stored ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Enum.TryParse<KnightSourceType>(x, ignoreCase: false, out var s) ? (KnightSourceType?)s : null)
            .Where(s => s is not null)
            .Select(s => s!.Value));

    /// <summary>Só candidatas, sem repetição, na ordem de apresentação do catálogo.</summary>
    public static IReadOnlyList<KnightSourceType> Normalize(IEnumerable<KnightSourceType> sources)
    {
        var set = new HashSet<KnightSourceType>(sources);
        return KnightConsolidatedCandidates.Sources.Where(set.Contains).ToList();
    }

    /// <summary>Fora do escopo = toda candidata que não está na lista de fontes dentro dele.</summary>
    public static IReadOnlyList<KnightSourceType> OutOfScopeFrom(IReadOnlyCollection<KnightSourceType> inScope) =>
        KnightConsolidatedCandidates.Sources.Where(s => !inScope.Contains(s)).ToList();
}
