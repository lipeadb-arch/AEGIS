using System;
using System.IO;
using AegisScore.Infrastructure.Reference;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Reference;

/// <summary>
/// [AEGIS-MVP-GOOGLE-SECOPS-02] Carga do catálogo MITRE ATT&CK v17.1 REAL (o artefato commitado, derivado do STIX
/// oficial): versão fixada, técnicas correntes resolvem, revogadas/deprecadas ficam de fora, subtécnica carrega
/// pai/táticas, e o loader é FAIL-CLOSED (arquivo ausente, JSON inválido, versão errada abortam).
/// </summary>
public sealed class MitreAttackCatalogTests
{
    private static readonly string CatalogPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "mitre_attack_enterprise_v17_1.json");

    private static MitreAttackCatalog Load() =>
        new(CatalogPath, NullLogger<MitreAttackCatalog>.Instance);

    [Fact]
    public void RealArtifact_LoadsAtVersion17_1_WithManyTechniques()
    {
        var cat = Load();
        cat.AttackVersion.Should().Be("17.1");
        cat.DisplayLabel.Should().Contain("v17.1").And.Contain("Google SecOps");
        cat.ActiveTechniqueCount.Should().BeGreaterThan(600, "o Enterprise v17.1 tem centenas de técnicas correntes");
    }

    [Fact]
    public void KnownTechniques_ResolveWithOfficialNamesAndTactics()
    {
        var cat = Load();

        var t1059 = cat.GetTechnique("T1059");
        t1059.Should().NotBeNull();
        t1059!.Name.Should().Be("Command and Scripting Interpreter");
        t1059.IsSubtechnique.Should().BeFalse();
        t1059.TacticIds.Should().Contain("TA0002");

        var sub = cat.GetTechnique("t1059.003");   // caixa normalizada
        sub.Should().NotBeNull();
        sub!.Name.Should().Be("Windows Command Shell");
        sub.IsSubtechnique.Should().BeTrue();
        sub.ParentId.Should().Be("T1059");
    }

    [Fact]
    public void UnknownTechnique_ReturnsNull()
    {
        Load().GetTechnique("T9999").Should().BeNull("um ID inexistente no catálogo é mapeamento inválido");
    }

    [Fact]
    public void RevokedTechnique_IsTreatedAsInvalid()
    {
        // T1002 (Data Compressed) está revogada no v17.1 → não resolve (fora da matriz corrente).
        Load().GetTechnique("T1002").Should().BeNull("técnica revogada não é técnica corrente");
    }

    [Fact]
    public void Tactics_HaveDeterministicPortugueseNames()
    {
        var cat = Load();
        cat.GetTactic("TA0002")!.NamePt.Should().Be("Execução");
        cat.GetTactic("TA0006")!.NamePt.Should().Be("Acesso a Credenciais");
    }

    [Fact]
    public void MissingFile_FailsClosed()
    {
        var act = () => new MitreAttackCatalog(
            Path.Combine(AppContext.BaseDirectory, "Data", "does-not-exist.json"),
            NullLogger<MitreAttackCatalog>.Instance);
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void WrongVersion_FailsClosed()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mitre-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp,
            """{"provenance":{"attackVersion":"16.0"},"tactics":[{"id":"TA0002","name":"Execution"}],"techniques":[{"id":"T1","name":"x"}]}""");
        try
        {
            var act = () => new MitreAttackCatalog(tmp, NullLogger<MitreAttackCatalog>.Instance);
            act.Should().Throw<InvalidOperationException>().WithMessage("*17.1*");
        }
        finally { File.Delete(tmp); }
    }
}
