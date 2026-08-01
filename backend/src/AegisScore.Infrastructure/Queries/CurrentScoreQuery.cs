using AegisScore.Application.Queries;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-AUD-001/002] Consolida o Score Atual do tenant em tempo real sobre o AegisScoreDbContext, pela
/// autoridade de agregação ÚNICA <see cref="AegisScoreAggregator"/> (a MESMA que a foto diária do
/// <c>AegisScoreSnapshotWorker</c>). O numerador e o peso AVALIADO consideram só os estados do framework
/// ATIVO; o peso/contagem ELEGÍVEIS vêm do catálogo ativo — o denominador de COBERTURA, que separa "sem
/// score" de "0%". O Global Query Filter (fail-closed) restringe os estados ao tenant do JWT; o catálogo é
/// reference data global (sem filtro), igual para todos os tenants.
/// </summary>
public sealed class CurrentScoreQuery : ICurrentScoreQuery
{
    private readonly AegisScoreDbContext _db;

    public CurrentScoreQuery(AegisScoreDbContext db) => _db = db;

    public async Task<CurrentScoreDto> GetCurrentAsync(CancellationToken ct = default)
    {
        // Agregação compartilhada com a foto diária: estados restritos ao framework ATIVO (numerador +
        // denominador do score) e universo elegível do catálogo ativo (denominador de cobertura).
        var r = await AegisScoreAggregator.AggregateAsync(_db, ct);
        return new CurrentScoreDto(
            r.AchievedScore, r.EvaluatedMaxScore, r.EvaluatedControls, r.EligibleMaxScore, r.EligibleControls);
    }
}
