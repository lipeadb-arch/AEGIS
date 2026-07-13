using System.Text.Json;
using AegisScore.Domain;

namespace AegisScore.Application.RiskAssessment;

/// <summary>
/// Fatores de decaimento por <see cref="DependencyStrength"/>: quanto do impacto ATRAVESSA cada aresta no
/// traversal reverso. Hard propaga integral; Soft degrada; Redundant amortece (há redundância a montante).
/// Multiplicativos ao longo do caminho (∈ [0,1]) — por isso o produto nunca CRESCE com a profundidade, o
/// que garante a terminação do best-first (Dijkstra maximizante) mesmo em grafos cíclicos.
/// </summary>
public record DecayProfile(double Hard, double Soft, double Redundant)
{
    public static readonly DecayProfile Default = new(Hard: 1.0, Soft: 0.5, Redundant: 0.25);

    public double For(DependencyStrength strength) => strength switch
    {
        DependencyStrength.Hard => Hard,
        DependencyStrength.Soft => Soft,
        DependencyStrength.Redundant => Redundant,
        _ => Redundant,
    };
}

/// <summary>
/// Entrada IMUTÁVEL do cálculo: o epicentro, o cenário de ameaça (opcional) e o SUBGRAFO já materializado
/// em memória (ativos + arestas + exposições). O motor é puro — quem carrega o subgrafo do banco é a
/// Application/Infra; aqui não há I/O.
/// </summary>
public record BlastRadiusInput(
    Asset Root,
    Threat? Scenario,
    IReadOnlyCollection<Asset> Assets,
    IReadOnlyCollection<AssetDependency> Dependencies,
    IReadOnlyCollection<AssetThreatExposure> Exposures);

/// <summary>Um ativo colateral do raio + como ele foi alcançado (o resultado PURO, sem entidade/tenant).</summary>
public record ImpactedNode(Guid AssetId, int Distance, double PropagatedImpact, DependencyStrength PathStrength);

/// <summary>
/// Resultado puro do motor. A Application o materializa num <see cref="BlastRadiusAssessment"/> +
/// <see cref="BlastRadiusImpactNode"/>[], carimba o tenant e persiste.
/// </summary>
public record BlastRadiusResult(
    double Score,
    RiskLevel Level,
    int MaxDepth,
    string FactorsJson,
    IReadOnlyList<ImpactedNode> Nodes);

/// <summary>
/// Motor do RAIO DE EXPLOSÃO (ID.RA). Dado o subgrafo de topologia, computa quais ativos caem se o root
/// for comprometido e a magnitude agregada do dano. Stateless e puro (sem I/O, sem EF): testável com
/// grafos sintéticos. Espelha o idioma dos <c>*ScoringService</c>.
/// </summary>
public interface IBlastRadiusCalculator
{
    BlastRadiusResult Compute(BlastRadiusInput input);
}

/// <summary>
/// Implementação por BUSCA GULOSA (Dijkstra maximizante) sobre o grafo REVERSO. A aresta é
/// "Source DEPENDE DE Target", então o raio de um ativo comprometido são os <c>Source</c> que o alcançam a
/// montante. Propriedades garantidas:
/// <list type="bullet">
///   <item><b>Terminação sob ciclos:</b> cada ativo é finalizado (<c>settled</c>) no máximo uma vez; arestas
///   que voltam a um nó já finalizado são descartadas na hora — um ciclo A→B→A nunca reenfileira A.</item>
///   <item><b>Múltiplos caminhos:</b> o fator guardado por ativo é o do MELHOR caminho (maior propagação);
///   como estender um caminho só multiplica por ≤ 1, extrair sempre o maior fator primeiro é ótimo.</item>
/// </list>
/// </summary>
public sealed class BlastRadiusCalculator : IBlastRadiusCalculator
{
    /// <summary>Fator de propagação abaixo do qual o caminho é PODADO (irrelevante — evita ruído/explosão).</summary>
    private const double MinPropagationFactor = 0.01;

    /// <summary>Impacto propagado (0–100) abaixo do qual o nó NÃO é materializado (pruning de impacto baixo).</summary>
    private const double MinMaterializedImpact = 1.0;

    private readonly DecayProfile _decay;

    public BlastRadiusCalculator(DecayProfile? decay = null) => _decay = decay ?? DecayProfile.Default;

    public BlastRadiusResult Compute(BlastRadiusInput input)
    {
        var assetsById = ToAssetIndex(input.Assets);
        var reverseAdjacency = BuildReverseAdjacency(input.Dependencies);
        var best = TraverseReverse(input.Root.Id, reverseAdjacency);

        var nodes = MaterializeNodes(best, input.Root.Id, assetsById);

        double aggregated = AggregateImpact(input.Root, nodes, assetsById);
        double trigger = TriggerLikelihood(input);
        double score = Math.Round(Math.Clamp(aggregated * trigger, 0, 100), 1);
        int maxDepth = nodes.Count == 0 ? 0 : nodes.Max(n => n.Distance);

        var factorsJson = Explain(input, aggregated, trigger, nodes, maxDepth);
        return new BlastRadiusResult(score, LevelOf(score), maxDepth, factorsJson, nodes);
    }

    // ---- Traversal reverso (o coração) -----------------------------------------

    /// <summary>Melhor caminho (fator + distância + gargalo) até cada ativo alcançável a partir do root.</summary>
    private Dictionary<Guid, PathInfo> TraverseReverse(
        Guid rootId, IReadOnlyDictionary<Guid, List<AssetDependency>> reverseAdjacency)
    {
        var best = new Dictionary<Guid, PathInfo> { [rootId] = new(Factor: 1.0, Distance: 0, Bottleneck: DependencyStrength.Hard) };
        var settled = new HashSet<Guid>();

        // Fila de prioridade MAXIMIZANTE: a prioridade é -fator, então o menor -fator (= maior fator) sai antes.
        var frontier = new PriorityQueue<Guid, double>();
        frontier.Enqueue(rootId, -1.0);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (!settled.Add(current)) continue;         // entrada obsoleta / já finalizado → ignora
            var path = best[current];

            if (!reverseAdjacency.TryGetValue(current, out var incoming)) continue;
            foreach (var edge in incoming)
            {
                var dependent = edge.SourceAssetId;      // quem DEPENDE de current → é atingido se current cair
                if (settled.Contains(dependent)) continue; // quebra ciclos (A→B→A) e reprocessos na hora

                double factor = path.Factor * _decay.For(edge.Strength);
                if (factor < MinPropagationFactor) continue; // poda caminhos irrelevantes

                if (!best.TryGetValue(dependent, out var known) || factor > known.Factor)
                {
                    var bottleneck = current == rootId
                        ? edge.Strength                                          // 1º salto: o próprio elo
                        : (DependencyStrength)Math.Max((int)path.Bottleneck, (int)edge.Strength); // pior (mais fraco) do caminho
                    best[dependent] = new PathInfo(factor, path.Distance + 1, bottleneck);
                    frontier.Enqueue(dependent, -factor);
                }
            }
        }
        return best;
    }

    /// <summary>Índice de adjacência REVERSA: Target → arestas ativas que apontam para ele. Auto-laços ignorados.</summary>
    private static Dictionary<Guid, List<AssetDependency>> BuildReverseAdjacency(IReadOnlyCollection<AssetDependency> deps)
    {
        var adjacency = new Dictionary<Guid, List<AssetDependency>>();
        foreach (var dep in deps)
        {
            if (!dep.IsActive || dep.SourceAssetId == dep.TargetAssetId) continue;
            if (!adjacency.TryGetValue(dep.TargetAssetId, out var list))
                adjacency[dep.TargetAssetId] = list = new List<AssetDependency>();
            list.Add(dep);
        }
        return adjacency;
    }

    // ---- Impacto ---------------------------------------------------------------

    /// <summary>Projeta o mapa de melhores caminhos em nós de impacto, já podando os irrisórios e o próprio root.</summary>
    private static List<ImpactedNode> MaterializeNodes(
        IReadOnlyDictionary<Guid, PathInfo> best, Guid rootId, IReadOnlyDictionary<Guid, Asset> assetsById)
    {
        var nodes = new List<ImpactedNode>();
        foreach (var (assetId, path) in best)
        {
            if (assetId == rootId) continue;                      // o epicentro não é colateral
            if (!assetsById.TryGetValue(assetId, out var asset)) continue; // ativo fora do subgrafo → dado insuficiente
            double propagated = Math.Round(IntrinsicImpact(asset) * path.Factor, 2);
            if (propagated < MinMaterializedImpact) continue;     // pruning de impacto baixo
            nodes.Add(new ImpactedNode(assetId, path.Distance, propagated, path.Bottleneck));
        }
        nodes.Sort((a, b) => b.PropagatedImpact.CompareTo(a.PropagatedImpact));
        return nodes;
    }

    /// <summary>
    /// Agrega o dano do epicentro + colaterais numa magnitude 0–100 via "noisy-OR" (união probabilística):
    /// <c>1 − Π(1 − dᵢ)</c>. Satura em 100, cresce com severidade E alcance, e nunca é dominada por um só nó.
    /// </summary>
    private static double AggregateImpact(Asset root, IReadOnlyList<ImpactedNode> nodes, IReadOnlyDictionary<Guid, Asset> _)
    {
        double survival = 1.0 - IntrinsicImpact(root) / 100.0;    // o epicentro cai com fator 1.0
        foreach (var node in nodes)
            survival *= 1.0 - node.PropagatedImpact / 100.0;
        return (1.0 - survival) * 100.0;
    }

    /// <summary>Impacto intrínseco 0–100 do ativo: high-water mark da matriz CIA/negócio, senão a criticidade escalar.</summary>
    private static double IntrinsicImpact(Asset asset)
    {
        int level = asset.BusinessImpact is { } bi
            ? MaxDimension(bi)
            : asset.Criticality;
        return Math.Clamp(level, 1, 4) / 4.0 * 100.0;             // 1→25, 2→50, 3→75, 4→100
    }

    private static int MaxDimension(BusinessImpactProfile b) => Math.Max(
        Math.Max(Math.Max(b.Confidentiality, b.Integrity), Math.Max(b.Availability, b.Financial)),
        Math.Max(Math.Max(b.Operational, b.Regulatory), b.Reputational));

    /// <summary>
    /// Verossimilhança de o root ser comprometido em primeiro lugar (0–1), das exposições ATIVAS dele
    /// (filtradas pelo cenário, se houver). Sem exposições conhecidas → 1.0: raio hipotético "assuma que caiu".
    /// </summary>
    private static double TriggerLikelihood(BlastRadiusInput input)
    {
        double maxLikelihood = 0;
        bool exploited = false;
        foreach (var e in input.Exposures)
        {
            if (e.AssetId != input.Root.Id || e.Status != ExposureStatus.Active) continue;
            if (input.Scenario is not null && e.ThreatId != input.Scenario.Id) continue;
            maxLikelihood = Math.Max(maxLikelihood, Math.Clamp(e.Likelihood, 1, 4));
            exploited |= e.Threat?.KnownExploited == true;
        }
        if (maxLikelihood == 0) return 1.0;
        double p = maxLikelihood / 4.0;                          // 1→0.25 … 4→1.0
        return exploited ? Math.Min(1.0, p * 1.25) : p;          // KEV/in-the-wild eleva
    }

    private static RiskLevel LevelOf(double score) => score switch  // mesma régua do IcrScoringService (40/60/80)
    {
        < 40 => RiskLevel.Baixo,
        < 60 => RiskLevel.Medio,
        < 80 => RiskLevel.Alto,
        _ => RiskLevel.Critico,
    };

    private static Dictionary<Guid, Asset> ToAssetIndex(IReadOnlyCollection<Asset> assets)
    {
        var index = new Dictionary<Guid, Asset>(assets.Count);
        foreach (var a in assets) index[a.Id] = a;               // último vence (defensivo contra duplicatas)
        return index;
    }

    private static string Explain(
        BlastRadiusInput input, double aggregated, double trigger, IReadOnlyList<ImpactedNode> nodes, int maxDepth) =>
        JsonSerializer.Serialize(new
        {
            rootAssetId = input.Root.Id,
            rootIntrinsicImpact = IntrinsicImpact(input.Root),
            scenarioThreat = input.Scenario?.Code,
            triggerLikelihood = Math.Round(trigger, 3),
            aggregatedImpact = Math.Round(aggregated, 1),
            impactedAssetCount = nodes.Count,
            maxDepth,
        });

    /// <summary>Melhor caminho conhecido até um ativo: fator multiplicativo, distância e elo-gargalo.</summary>
    private readonly record struct PathInfo(double Factor, int Distance, DependencyStrength Bottleneck);
}
