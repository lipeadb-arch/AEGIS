using AegisScore.Application.RiskAssessment;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.RiskAssessment;

/// <summary>
/// Blindagem do <see cref="BlastRadiusCalculator"/> — o motor matemático do raio de explosão (ID.RA).
/// Foco: propagação reversa correta, decaimento multiplicativo por <see cref="DependencyStrength"/>,
/// terminação segura sob dependências CIRCULARES e escolha do melhor caminho quando há vários. Grafos
/// sintéticos, sem I/O. Convenção de aresta: <c>Dep(source, target)</c> = "source DEPENDE DE target".
/// </summary>
public sealed class BlastRadiusCalculatorTests
{
    private static readonly BlastRadiusCalculator Sut = new();   // stateless → seguro compartilhar

    // ---- 1. Propagação linear -----------------------------------------------------

    [Fact]
    public void Compute_CadeiaLinearHard_PropagaIntegralComDistanciaCrescente()
    {
        var r = Node(2); var a = Node(4); var b = Node(4);
        // b DEPENDE DE a DEPENDE DE r (tudo Hard) → fator 1.0 em toda a cadeia
        var result = Sut.Compute(Graph(r, new[] { r, a, b }, Hard(a, r), Hard(b, a)));

        result.Nodes.Should().HaveCount(2);
        result.Nodes.Single(n => n.AssetId == a.Id).Distance.Should().Be(1);
        result.Nodes.Single(n => n.AssetId == a.Id).PropagatedImpact.Should().BeApproximately(100, 0.01);
        result.Nodes.Single(n => n.AssetId == b.Id).Distance.Should().Be(2);
        result.Nodes.Single(n => n.AssetId == b.Id).PropagatedImpact.Should().BeApproximately(100, 0.01);
        result.MaxDepth.Should().Be(2);
    }

    // ---- 2. Decaimento por Soft/Redundant -----------------------------------------

    [Fact]
    public void Compute_ElosSoftERedundant_AplicamOFatorDeDecaimentoCorreto()
    {
        var r = Node(2); var soft = Node(4); var redundant = Node(4);
        var result = Sut.Compute(Graph(r, new[] { r, soft, redundant }, Soft(soft, r), Redundant(redundant, r)));

        result.Nodes.Single(n => n.AssetId == soft.Id).PropagatedImpact.Should().BeApproximately(50, 0.01);       // 100 × 0.50
        result.Nodes.Single(n => n.AssetId == redundant.Id).PropagatedImpact.Should().BeApproximately(25, 0.01);  // 100 × 0.25
    }

    [Fact]
    public void Compute_CaminhoComVariosElos_MultiplicaOsFatores()
    {
        var r = Node(2); var a = Node(4); var b = Node(4);
        // b DEPENDE DE a (Soft) DEPENDE DE r (Soft) → 0.5 × 0.5 = 0.25
        var result = Sut.Compute(Graph(r, new[] { r, a, b }, Soft(a, r), Soft(b, a)));

        result.Nodes.Single(n => n.AssetId == a.Id).PropagatedImpact.Should().BeApproximately(50, 0.01);  // 100 × 0.5
        result.Nodes.Single(n => n.AssetId == b.Id).PropagatedImpact.Should().BeApproximately(25, 0.01);  // 100 × 0.25
    }

    // ---- 3. Dependências circulares (quebra segura) -------------------------------

    [Fact] // termina por construção (settled set); se regredir para loop, este teste PENDURA em vez de passar
    public void Compute_CicloEntreDependentes_TerminaEVisitaCadaAtivoUmaVez()
    {
        var r = Node(2); var a = Node(4); var b = Node(4);
        // a↔b formam um ciclo; ambos alcançáveis a partir de r. O motor não pode entrar em loop.
        var result = Sut.Compute(Graph(r, new[] { r, a, b }, Hard(a, r), Hard(b, a), Hard(a, b)));

        result.Nodes.Should().HaveCount(2);
        result.Nodes.Select(n => n.AssetId).Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    [Fact] // termina por construção (settled set); se regredir para loop, este teste PENDURA em vez de passar
    public void Compute_CicloQueRetornaAoRoot_NaoReprocessaOEpicentro()
    {
        var r = Node(2); var a = Node(4);
        // a depende de r E r depende de a — o ciclo inclui o próprio root.
        var result = Sut.Compute(Graph(r, new[] { r, a }, Hard(a, r), Hard(r, a)));

        result.Nodes.Should().ContainSingle().Which.AssetId.Should().Be(a.Id);  // o root nunca vira colateral de si
    }

    // ---- 4. Múltiplos caminhos até o mesmo ativo ----------------------------------

    [Fact]
    public void Compute_MultiplosCaminhos_EscolheODeMaiorPropagacao()
    {
        var r = Node(2); var a = Node(4); var b = Node(4);
        // a é alcançável por R→a direto (Soft = 0.5) OU por R→b (Hard) → a (Hard) = 1.0. O melhor (1.0) vence.
        var result = Sut.Compute(Graph(r, new[] { r, a, b }, Soft(a, r), Hard(b, r), Hard(a, b)));

        var nodeA = result.Nodes.Single(n => n.AssetId == a.Id);
        nodeA.PropagatedImpact.Should().BeApproximately(100, 0.01);  // caminho Hard, não o Soft direto (que daria 50)
        nodeA.Distance.Should().Be(2);                                // via b, não o salto direto
    }

    // ---- 5. Verossimilhança do gatilho (exposições do root) -----------------------

    [Fact]
    public void Compute_ExposicaoAtivaNoRoot_ModulaOScorePelaVerossimilhanca()
    {
        var r = Node(4);   // sozinho: impacto agregado 100
        var threat = MakeThreat();
        var exposure = new AssetThreatExposure
        {
            AssetId = r.Id, ThreatId = threat.Id, Threat = threat,
            Likelihood = 2, Status = ExposureStatus.Active,
        };
        var input = new BlastRadiusInput(r, null, new[] { r }, Array.Empty<AssetDependency>(), new[] { exposure });

        var result = Sut.Compute(input);

        result.Score.Should().BeApproximately(50, 0.01);   // 100 × (2/4)
        result.Level.Should().Be(RiskLevel.Medio);
    }

    [Fact]
    public void Compute_SemExposicoes_AssumeComprometimentoCerto()
    {
        var r = Node(4);
        var result = Sut.Compute(Graph(r, new[] { r }));   // sem grafo, sem exposições

        result.Score.Should().BeApproximately(100, 0.01);  // trigger 1.0 (hipotético) × agregado 100
        result.Level.Should().Be(RiskLevel.Critico);
    }

    // ---- 6. Pruning de impacto baixo ---------------------------------------------

    [Fact]
    public void Compute_CaminhoAbaixoDoCorte_EhPodado()
    {
        var r = Node(2); var a = Node(4); var b = Node(4); var c = Node(4); var d = Node(4);
        // cadeia Redundant: fatores 0.25, 0.0625, 0.0156, 0.0039 — o 4º cruza o corte de propagação (0.01)
        var result = Sut.Compute(Graph(r, new[] { r, a, b, c, d },
            Redundant(a, r), Redundant(b, a), Redundant(c, b), Redundant(d, c)));

        result.Nodes.Select(n => n.AssetId).Should().BeEquivalentTo(new[] { a.Id, b.Id, c.Id });
        result.Nodes.Should().NotContain(n => n.AssetId == d.Id);  // podado
    }

    // ---- helpers ------------------------------------------------------------------

    private static Asset Node(int criticality) => new() { Id = Guid.NewGuid(), Criticality = criticality };
    private static Threat MakeThreat() => new() { Id = Guid.NewGuid(), Code = "CVE-TEST", KnownExploited = false };

    private static AssetDependency Dep(Asset source, Asset target, DependencyStrength strength) =>
        new() { SourceAssetId = source.Id, TargetAssetId = target.Id, Strength = strength, IsActive = true };

    private static AssetDependency Hard(Asset s, Asset t) => Dep(s, t, DependencyStrength.Hard);
    private static AssetDependency Soft(Asset s, Asset t) => Dep(s, t, DependencyStrength.Soft);
    private static AssetDependency Redundant(Asset s, Asset t) => Dep(s, t, DependencyStrength.Redundant);

    private static BlastRadiusInput Graph(Asset root, Asset[] assets, params AssetDependency[] deps) =>
        new(root, null, assets, deps, Array.Empty<AssetThreatExposure>());
}
