using System;

namespace AegisScore.Domain;

// ============================================================================
//  [AEGIS-KNIGHT-SCOPE-01] Escopo da avaliação KNIGHT por tenant
// ============================================================================
// Qual fonte elegível compõe a avaliação consolidada DESTE tenant. O registro guarda as fontes FORA do escopo — e não
// as de dentro — para que uma fonte que o tenant ainda não conhecia (ou que o catálogo acrescente depois) entre no
// escopo por padrão, em vez de ficar de fora sem que ninguém a tenha retirado. Uma fonte fora do escopo continua
// existindo: o conector, as avaliações dela, a cobertura do catálogo de referência e as fotografias já publicadas não
// mudam. Ela só deixa de contar nos totais da avaliação consolidada (tela, painel, prévia e publicação).

/// <summary>Escopo salvo da avaliação KNIGHT de UM tenant — no máximo um registro por tenant.</summary>
public class KnightAssessmentScope : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Fontes FORA do escopo, pelo nome do enum (<see cref="KnightSourceType"/>), separadas por vírgula e ordenadas.</summary>
    public string ExcludedSources { get; set; } = "";

    /// <summary>Nome de exibição de quem salvou por último, como o token o apresentou (vazio quando ausente).</summary>
    public string? UpdatedByName { get; set; }
}
