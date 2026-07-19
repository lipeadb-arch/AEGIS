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
}
