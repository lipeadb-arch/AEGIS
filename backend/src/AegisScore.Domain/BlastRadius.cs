using System;
using System.Collections.Generic;

namespace AegisScore.Domain;

// ============================================================================
//  ID.RA — Risk Assessment / RAIO DE EXPLOSÃO
//  Topologia de dependências (grafo Asset↔Asset) + ameaças estruturadas +
//  o snapshot explicável do raio. POCOs puros — a config EF fica na infra.
// ============================================================================

/// <summary>
/// Aresta DIRECIONADA do grafo de topologia (ID.RA): o ativo <see cref="SourceAssetId"/> DEPENDE do ativo
/// <see cref="TargetAssetId"/>. A <see cref="Strength"/> define QUANTO a falha do alvo se propaga — é o que
/// transforma o inventário plano (ID.AM) num grafo navegável. O raio de explosão de um ativo comprometido é
/// o traversal REVERSO destas arestas (quem depende dele, transitivamente).
/// </summary>
public class AssetDependency : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Ativo dependente — sofre se o alvo falhar.</summary>
    public Guid SourceAssetId { get; set; }
    public Asset? SourceAsset { get; set; }

    /// <summary>Ativo provedor — a dependência.</summary>
    public Guid TargetAssetId { get; set; }
    public Asset? TargetAsset { get; set; }

    public DependencyType Type { get; set; } = DependencyType.DependsOn;
    public DependencyStrength Strength { get; set; } = DependencyStrength.Hard;

    public AssetDiscoverySource DiscoverySource { get; set; } = AssetDiscoverySource.Manual;
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
}

/// <summary>
/// Ameaça ESTRUTURADA do catálogo (ID.RA-03): CVE, técnica MITRE ATT&amp;CK, item KEV ou ameaça interna.
/// Substitui as strings livres de <see cref="Risk.Threats"/> por dado cruzável. Reference data no idioma do
/// <see cref="IcrWeightProfile"/>: <c>TenantId</c> nulo = catálogo público global; preenchido = do tenant.
/// </summary>
public class Threat : Entity
{
    /// <summary>Nulo = ameaça pública global (CVE/MITRE); preenchido = específica do tenant. (Não é ITenantOwned.)</summary>
    public Guid? TenantId { get; set; }

    public string Code { get; set; } = "";     // "CVE-2024-1234", "T1486"
    public ThreatSource Source { get; set; } = ThreatSource.Internal;
    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Severidade base 0–10 (CVSS-like) — normalizada pelo motor no cálculo do raio.</summary>
    public double BaseSeverity { get; set; }

    /// <summary>Tática/categoria (ex.: "Impact", "Initial Access") para agrupar cenários.</summary>
    public string? Tactic { get; set; }

    /// <summary>
    /// Explorada ativamente in-the-wild (KEV/threat-intel) — eleva a probabilidade. ⚠️ [AEGIS-MVP-VULN-01] NÃO é
    /// derivado de <see cref="PublicExploit"/>/<see cref="ExploitVerified"/> do Defender: disponibilidade de
    /// exploit NÃO prova exploração ativa. Só uma fonte KEV/threat-intel dedicada preenche este campo.
    /// </summary>
    public bool KnownExploited { get; set; }

    // ---- [AEGIS-MVP-VULN-01] Metadados CONSULTÁVEIS de CVE (preenchidos quando Source = Cve, via Defender
    // Vulnerability Management). São FATOS DA FONTE, somente leitura; o AEGIS nunca os inventa. Nulos quando
    // a fonte não os informa (ex.: CVSS ausente NÃO vira 0). exposedMachines/status de exceção são dependentes
    // do tenant e por isso NÃO moram aqui (o vínculo por tenant é a AssetThreatExposure).

    /// <summary>Severidade TEXTUAL informada pela fonte (ex.: "Low", "Medium", "High", "Critical"). Fato da fonte.</summary>
    public string? Severity { get; set; }

    /// <summary>Pontuação CVSS v3 (0–10) informada pela fonte. Nulo quando ausente — nunca inventado.</summary>
    public double? CvssScore { get; set; }

    /// <summary>Vetor CVSS v3 informado pela fonte (ex.: "CVSS:3.1/AV:N/..."). Nulo quando ausente.</summary>
    public string? CvssVector { get; set; }

    /// <summary>Data de publicação do CVE informada pela fonte. Nulo quando ausente.</summary>
    public DateTimeOffset? PublishedOn { get; set; }

    /// <summary>Data da última atualização do CVE informada pela fonte. Nulo quando ausente.</summary>
    public DateTimeOffset? UpdatedOn { get; set; }

    /// <summary>Existe exploit PÚBLICO (fato da fonte). ≠ exploração ativa — NÃO alimenta <see cref="KnownExploited"/>.</summary>
    public bool? PublicExploit { get; set; }

    /// <summary>Exploit VERIFICADO pela fonte (fato da fonte). ≠ exploração ativa — NÃO alimenta <see cref="KnownExploited"/>.</summary>
    public bool? ExploitVerified { get; set; }

    /// <summary>EPSS (0–1) informado pela fonte — probabilidade de exploração. Nulo quando ausente.</summary>
    public double? Epss { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Exposição de UM ativo a UMA ameaça (ID.RA-01/04) — a aresta ativo↔ameaça com a probabilidade local e a
/// trilha de mitigação. Insumo que, cruzado com criticidade e topologia, dispara o raio de explosão.
/// </summary>
public class AssetThreatExposure : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }

    public Guid ThreatId { get; set; }
    public Threat? Threat { get; set; }

    /// <summary>Probabilidade de materialização NESTE ativo, 1–4 (mesma régua de <see cref="RiskEvaluation"/>).</summary>
    public int Likelihood { get; set; } = 1;

    /// <summary>
    /// DISPOSIÇÃO da exposição (decisão humana): Active/Mitigated/Accepted/FalsePositive. É ORTOGONAL ao ciclo de
    /// vida das observações de fonte (<see cref="AssetThreatObservation"/>, Open/Resolved): a reconciliação NUNCA
    /// escreve aqui — uma exposição marcada Accepted/FalsePositive preserva essa disposição.
    /// </summary>
    public ExposureStatus Status { get; set; } = ExposureStatus.Active;

    /// <summary>Subcategoria NIST que mitiga esta exposição (ex.: "PR.PS-01" patch) — elo com o ledger de score.</summary>
    public string? MitigatingSubcategoryCode { get; set; }

    public AssetDiscoverySource DiscoverySource { get; set; } = AssetDiscoverySource.Connector;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Evidência LEGADA da exposição (ex.: cenário do raio de explosão). ⚠️ [AEGIS-MVP-VULN-01] O detalhe POR FONTE
    /// (produtos/versões de um scanner) vive na <see cref="AssetThreatObservation"/>, não aqui — a exposição é
    /// consolidada e provider-neutral. Nunca a resposta bruta da fonte, IP ou PII.
    /// </summary>
    public string? EvidenceJson { get; set; }
}

/// <summary>
/// Avaliação de RAIO DE EXPLOSÃO (ID.RA-05): snapshot explicável do impacto de comprometer o
/// <see cref="RootAssetId"/>, opcionalmente sob um cenário de ameaça. Cruza topologia
/// (<see cref="AssetDependency"/>) × exposição (<see cref="AssetThreatExposure"/>) × impacto
/// (<see cref="BusinessImpactProfile"/>). Ponto-no-tempo e explicável (padrão <c>FactorsJson</c> do
/// <see cref="IcrScore"/>). As métricas e os nós são MATERIALIZADOS para leitura O(1) no HUD.
/// </summary>
public class BlastRadiusAssessment : Entity, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Epicentro: o ativo hipoteticamente comprometido.</summary>
    public Guid RootAssetId { get; set; }
    public Asset? RootAsset { get; set; }

    /// <summary>Cenário de ameaça (opcional): "e se T1486 atingir o root?". Nulo = raio topológico puro.</summary>
    public Guid? ScenarioThreatId { get; set; }
    public Threat? ScenarioThreat { get; set; }

    public BlastRadiusTrigger Trigger { get; set; } = BlastRadiusTrigger.Manual;

    /// <summary>Magnitude 0–100 (régua de <see cref="IcrScore"/> / <see cref="Asset.RiskScore"/>).</summary>
    public double BlastRadiusScore { get; set; }
    public RiskLevel RiskLevel { get; set; }

    // Métricas do raio (derivadas do traversal, materializadas para leitura barata).
    public int ImpactedAssetCount { get; set; }
    public int ImpactedProcessCount { get; set; }
    public int MaxDepth { get; set; }

    /// <summary>Breakdown explicável: fatores ponderados, caminho crítico, impacto CIA agregado.</summary>
    public string FactorsJson { get; set; } = "";

    public EvaluatedBy ComputedBy { get; set; } = EvaluatedBy.Ai;
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Os ativos atingidos (materializados para a consulta "o que cai se o root cair").</summary>
    public ICollection<BlastRadiusImpactNode> ImpactedNodes { get; set; } = new List<BlastRadiusImpactNode>();
}

/// <summary>
/// Um ativo dentro do raio de explosão de uma <see cref="BlastRadiusAssessment"/> — o alcance MATERIALIZADO
/// (em vez de recomputar o grafo a cada leitura). Guarda a distância ao epicentro e o impacto propagado já
/// decaído pela força das arestas do melhor caminho. Nós de impacto irrisório são PODADOS (não persistidos).
/// </summary>
public class BlastRadiusImpactNode : Entity, ITenantOwned
{
    /// <summary>Denormalizado (defesa em profundidade + stamping automático) — mesmo padrão de RiskEvaluation.</summary>
    public Guid TenantId { get; set; }

    public Guid AssessmentId { get; set; }
    public BlastRadiusAssessment? Assessment { get; set; }

    public Guid ImpactedAssetId { get; set; }
    public Asset? ImpactedAsset { get; set; }

    /// <summary>Saltos desde o root no grafo (1 = dependente direto).</summary>
    public int Distance { get; set; }

    /// <summary>Impacto herdado (0–100) após o decaimento pela força das dependências do melhor caminho.</summary>
    public double PropagatedImpact { get; set; }

    /// <summary>Elo mais forte no caminho até este nó (o "gargalo" que definiu o quanto propagou).</summary>
    public DependencyStrength PathStrength { get; set; }
}
