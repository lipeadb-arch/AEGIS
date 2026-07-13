using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Orquestrador do RAIO DE EXPLOSÃO (ID.RA). Fronteira entre o motor PURO
/// (<c>IBlastRadiusCalculator</c>) e o mundo com I/O: carrega o grafo de topologia do tenant, roda o
/// cálculo e PERSISTE o snapshot (<see cref="BlastRadiusAssessment"/> + <see cref="BlastRadiusImpactNode"/>).
/// O tenant é o ambiente (Global Query Filter + stamping fail-closed) — nunca vem do chamador.
/// </summary>
public interface IBlastRadiusAssessmentService
{
    /// <summary>
    /// Avalia o raio de explosão de <paramref name="rootAssetId"/>, opcionalmente sob um cenário de ameaça
    /// (<paramref name="scenarioThreatId"/>), e persiste o resultado. Lança se o ativo não existir no tenant.
    /// </summary>
    Task<BlastRadiusAssessment> AssessAsync(
        Guid rootAssetId, Guid? scenarioThreatId = null, CancellationToken ct = default);
}
