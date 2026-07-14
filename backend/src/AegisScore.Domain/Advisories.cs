using System;

namespace AegisScore.Domain;

// =============================================================================
// PROTECT (PR) — Recomendações de Remediação (Advisories)
// Fase 1 do motor CONSULTIVO: transforma o diagnóstico (TenantControlState) em AÇÃO tática. Não é
// uma ferramenta de execução de TI — é o artefato que o SOC (MSSP) gera para ENTREGAR à TI do cliente:
// o risco documentado + o passo a passo técnico que eleva o Secure Score daquele controle NIST.
// =============================================================================

/// <summary>
/// Uma recomendação de remediação técnica para UMA subcategoria NIST de UM tenant. É a saída
/// consultiva do Aegis Score: o analista do SOC a gera (texto inicialmente produzido pelo motor de IA)
/// e a TI do cliente a executa. Distinta de <see cref="IdentifiedRisk"/> (o achado de uma lacuna) e de
/// <see cref="ActionPlan"/> (o plano formal, ligado ao registro de risco): o advisory é o "como fazer"
/// técnico, mastigado e exportável, endereçado ao código do controle.
/// </summary>
public class RemediationAdvisory : Entity, ITenantOwned
{
    /// <summary>Carimbado no SaveChanges (fail-closed) — nunca confiar em valor vindo do cliente.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Código NIST-alvo ("PR.DS-01"). String, como em <see cref="IdentifiedRisk"/> /
    /// <see cref="SubcategoryCoverage"/> — o advisory endereça o controle pelo código, não por FK ao
    /// catálogo (desacoplado de versão do framework; o texto canned/IA é ancorado no próprio código).
    /// </summary>
    public string SubcategoryCode { get; set; } = "";

    /// <summary>Título curto e acionável da recomendação (ex.: "Impor MFA nas contas privilegiadas").</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Risco Documentado: o texto (gerado pela IA) que explica, em linguagem de negócio/risco, POR QUE
    /// a lacuna importa. É a justificativa que acompanha a recomendação ao cliente.
    /// </summary>
    public string DocumentedRisk { get; set; } = "";

    /// <summary>
    /// Passo a Passo Técnico: o roteiro exportável (gerado pela IA) que a TI do cliente executa para
    /// fechar a lacuna e elevar o score do controle. É o "como fazer" mastigado da recomendação.
    /// </summary>
    public string TechnicalSteps { get; set; } = "";
}
