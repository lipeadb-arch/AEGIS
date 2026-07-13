using Microsoft.EntityFrameworkCore;
using AegisScore.Application.RiskAssessment;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.RiskAssessment;

/// <summary>
/// Implementação do orquestrador do Raio de Explosão (ID.RA). Costura o motor PURO
/// (<see cref="IBlastRadiusCalculator"/>) ao mundo com I/O: carrega o grafo do tenant pelo
/// <see cref="AegisScoreDbContext"/> (Global Query Filter fail-closed), computa e materializa o snapshot.
///
/// Secure-by-design: opera SEMPRE no tenant ambiente. O grafo lido é só o do tenant (query filter); o
/// <see cref="BlastRadiusAssessment"/> e seus nós são <see cref="ITenantOwned"/> e recebem o carimbo de
/// tenant no <c>SaveChanges</c> (fail-closed), nunca de valor vindo do chamador.
/// </summary>
public sealed class BlastRadiusAssessmentService : IBlastRadiusAssessmentService
{
    private readonly AegisScoreDbContext _db;
    private readonly IBlastRadiusCalculator _calculator;
    private readonly IBlastRadiusScoreProjector _scoreProjector;

    public BlastRadiusAssessmentService(
        AegisScoreDbContext db, IBlastRadiusCalculator calculator, IBlastRadiusScoreProjector scoreProjector)
    {
        _db = db;
        _calculator = calculator;
        _scoreProjector = scoreProjector;
    }

    public async Task<BlastRadiusAssessment> AssessAsync(
        Guid rootAssetId, Guid? scenarioThreatId = null, CancellationToken ct = default)
    {
        // 1) Epicentro. O query filter garante que só enxergamos ativos DESTE tenant (fail-closed).
        var root = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == rootAssetId, ct)
            ?? throw new InvalidOperationException(
                $"Ativo '{rootAssetId}' não encontrado no tenant — impossível calcular o raio de explosão.");

        // 2) Cenário de ameaça opcional. Threat é reference data (sem query filter): global ou do tenant.
        var scenario = scenarioThreatId is { } threatId
            ? await _db.Threats.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threatId, ct)
            : null;

        // 3) Subgrafo do tenant. Carregamos o grafo inteiro do tenant e traversamos EM MEMÓRIA (o motor poda
        //    e é O(E log V)); uma CTE recursiva no banco fica para quando o volume exigir. AsNoTracking: são
        //    só entrada de cálculo, nunca modificadas. As exposições restringem-se ao root (insumo do gatilho).
        var assets = await _db.Assets.AsNoTracking().Where(a => a.IsActive).ToListAsync(ct);
        var dependencies = await _db.AssetDependencies.AsNoTracking().Where(d => d.IsActive).ToListAsync(ct);
        var exposures = await _db.AssetThreatExposures.AsNoTracking()
            .Include(e => e.Threat)          // o motor lê e.Threat.KnownExploited na verossimilhança do gatilho
            .Where(e => e.AssetId == rootAssetId && e.Status == ExposureStatus.Active)
            .ToListAsync(ct);

        // 4) Motor PURO — nenhuma regra de raio de explosão vive nesta camada.
        var result = _calculator.Compute(new BlastRadiusInput(root, scenario, assets, dependencies, exposures));

        // 5) Materializa o snapshot + nós. O TenantId é carimbado no SaveChanges (ITenantOwned, fail-closed).
        var assessment = new BlastRadiusAssessment
        {
            RootAssetId = rootAssetId,
            ScenarioThreatId = scenario?.Id,
            Trigger = scenario is null ? BlastRadiusTrigger.Manual : BlastRadiusTrigger.ThreatDriven,
            BlastRadiusScore = result.Score,
            RiskLevel = result.Level,
            ImpactedAssetCount = result.Nodes.Count,
            ImpactedProcessCount = CountImpactedProcesses(rootAssetId, result.Nodes, assets),
            MaxDepth = result.MaxDepth,
            FactorsJson = result.FactorsJson,
            ComputedBy = EvaluatedBy.Ai,
            ComputedAt = DateTimeOffset.UtcNow,
            ImpactedNodes = result.Nodes
                .Select(n => new BlastRadiusImpactNode
                {
                    ImpactedAssetId = n.AssetId,
                    Distance = n.Distance,
                    PropagatedImpact = n.PropagatedImpact,
                    PathStrength = n.PathStrength,
                })
                .ToList(),
        };

        _db.BlastRadiusAssessments.Add(assessment);
        await _db.SaveChangesAsync(ct);   // stamping fail-closed carimba o assessment E os nós

        // Hook ID.RA → ledger: um raio alto/amplo penaliza ID.RA-01/05 no TenantControlState (o raio "dói"
        // na nota NIST). O assessment já persistido e carimbado é a fonte de verdade do que projetar.
        await _scoreProjector.ProjectAsync(assessment, ct);
        return assessment;
    }

    /// <summary>Nº de processos de negócio distintos tocados pelo raio (epicentro + colaterais).</summary>
    private static int CountImpactedProcesses(Guid rootAssetId, IReadOnlyList<ImpactedNode> nodes, List<Asset> assets)
    {
        var processByAsset = assets
            .Where(a => a.BusinessProcessId is not null)
            .ToDictionary(a => a.Id, a => a.BusinessProcessId!.Value);

        var processes = new HashSet<Guid>();
        foreach (var assetId in nodes.Select(n => n.AssetId).Append(rootAssetId))
            if (processByAsset.TryGetValue(assetId, out var processId))
                processes.Add(processId);
        return processes.Count;
    }
}
