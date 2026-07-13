using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// PONTE ID.RA → Aegis Score. Reage à geração de um <see cref="BlastRadiusAssessment"/> e, quando o raio é
/// ALTO/CRÍTICO e AMPLO, projeta uma penalização no ledger de conformidade (ID.RA-01/ID.RA-05) via
/// <see cref="IControlStateWriter"/> — o raio de explosão passa a "doer" na nota NIST do tenant.
/// </summary>
public interface IBlastRadiusScoreProjector
{
    /// <summary>
    /// Avalia o raio e, se cruzar os limiares de severidade × alcance, rebaixa ID.RA-01/ID.RA-05 no
    /// <c>TenantControlState</c>. No-op quando o raio está abaixo do limiar: ausência de um raio grande não
    /// prova a gestão de risco conforme, então o caminho negativo não credita nada.
    /// </summary>
    Task ProjectAsync(BlastRadiusAssessment assessment, CancellationToken ct = default);
}
