using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Porta de escrita do ledger de conformidade (<c>TenantControlState</c>) do Aegis Score. Recebe um
/// veredito JÁ FORMADO (status + evidência) e faz o upsert idempotente da célula tenant × subcategoria,
/// traduzindo o status em pontos pela regra ÚNICA de scoring.
///
/// Existe para que fontes de evidência distintas — telemetria (<see cref="IAegisAiEvaluatorService"/>)
/// e análise documental (Document Hub / Govern) — compartilhem a mesma persistência e o mesmo scoring
/// SEM se conhecerem. É o seam que evita duas implementações divergentes do numerador do score.
///
/// A implementação vive na Infrastructure (toca o DbContext); a porta, aqui.
/// </summary>
public interface IControlStateWriter
{
    /// <summary>
    /// Upsert idempotente do estado do controle: insere a célula na primeira avaliação, atualiza nas
    /// seguintes. Reexecutar com o mesmo par (tenant, subcategoria) nunca duplica registro.
    ///
    /// A escrita respeita a PRECEDÊNCIA de <paramref name="source"/>:
    /// <list type="bullet">
    /// <item><c>Telemetry</c> — autoritativa: sobrescreve sempre, inclusive rebaixando (se o controle
    /// quebrou, <c>NonCompliant</c> deve prevalecer).</item>
    /// <item><c>Documentary</c> — upgrade condicional: só aplica se PONTUAR MAIS que o estado atual.
    /// Um documento jamais derruba um controle validado por telemetria.</item>
    /// </list>
    /// </summary>
    /// <param name="tenantId">Asserção de defesa em profundidade: precisa casar com o tenant ambiente (fail-closed).</param>
    /// <param name="subcategoryCode">Código NIST CSF 2.0, ex.: "GV.OC-01".</param>
    /// <param name="status">Veredito de conformidade já decidido pela fonte de evidência.</param>
    /// <param name="evidence">Justificativa auditável (origem + racional), gravada em <c>AiEvidence</c>.</param>
    /// <param name="source">Procedência do veredito — define se a escrita é autoritativa ou condicional.</param>
    /// <param name="checks">Checklist técnico que justifica o status (persistido como JSON); nulo/vazio quando o motor não decompõe.</param>
    /// <param name="intelligence">Contexto de inteligência do controle (severidade, rastro cru, plano, confiança,
    /// ameaças, MTTD/MTTR), persistido como JSON ao lado do checklist. Nulo quando o motor não o emite —
    /// a escrita segue válida: o estado do controle nunca depende do enriquecimento.</param>
    /// <param name="missingRequirements">Lacunas de evidência discriminadas por natureza (telemetria ×
    /// documentação) que sustentam a não-conformidade. IGNORADO quando o status é <c>Compliant</c> — a
    /// invariante "controle conforme não tem pendência" é imposta aqui, no escritor único, e não confiada
    /// ao chamador.</param>
    /// <returns>O veredito EFETIVO: o proposto, ou o estado preservado quando o upgrade é recusado.</returns>
    Task<ComplianceVerdict> ApplyVerdictAsync(
        Guid tenantId, string subcategoryCode, ControlStatus status, string evidence,
        VerdictSource source, IReadOnlyList<ComplianceCheck>? checks = null,
        ControlIntelligence? intelligence = null,
        IReadOnlyList<MissingRequirement>? missingRequirements = null, CancellationToken ct = default);

    /// <summary>
    /// RECONCILIA o estado DOCUMENTAL de uma subcategoria a partir da evidência documental vigente — a
    /// operação que exclusão e reanálise usam para RETRAIR ou RECALCULAR sem deixar o ledger órfão. É
    /// distinta de <see cref="ApplyVerdictAsync"/> (que é upgrade-only, para projeção ao vivo): aqui o
    /// chamador já computou qual documento sustenta o controle (ou que NENHUM sustenta), e o escritor
    /// materializa esse fato de forma idempotente.
    ///
    /// Precedência FAIL-CLOSED:
    /// <list type="bullet">
    /// <item>estado vigente de <see cref="VerdictSource.Telemetry"/> → PRESERVADO integralmente (no-op no
    /// ledger): a telemetria é a verdade sobre a implementação e nenhum documento a retrai ou rebaixa;</item>
    /// <item><paramref name="documentary"/> não-nulo → grava/atualiza <c>MitigatedByThirdParty</c> (crédito
    /// parcial de 50%) com a origem documental vigente — refresca mesmo em empate, pois é reconciliação
    /// determinística do documento vencedor, não uma projeção condicional;</item>
    /// <item><paramref name="documentary"/> nulo e estado vigente Documentary → RETRAI (remove a célula),
    /// devolvendo a subcategoria a "não avaliado".</item>
    /// </list>
    /// Código fora do catálogo é no-op silencioso (nada a reconciliar).
    /// </summary>
    Task ReconcileDocumentaryAsync(
        Guid tenantId, string subcategoryCode, DocumentaryEvidence? documentary, CancellationToken ct = default);
}

/// <summary>
/// Evidência documental VIGENTE de um controle, já escolhida pela reconciliação (o documento vencedor):
/// a origem, a confiança e a evidência auditável (trecho literal + racional já compostos). Nulo, na
/// chamada de reconciliação, significa "não há evidência documental elegível" — sinal de retração.
/// </summary>
public sealed record DocumentaryEvidence(Guid OriginDocumentId, double Confidence, string Evidence);
