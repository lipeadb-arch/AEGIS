using AegisScore.Application.Services;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
using AegisScore.Infrastructure.RiskAssessment;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.RiskAssessment;

/// <summary>
/// Blindagem do <see cref="BlastRadiusScoreProjector"/> — a ponte ID.RA → ledger. Foco: a REGRA de decisão
/// (severidade × alcance → penalização graduada) e o contrato de escrita (controles, status, fonte, tenant).
/// O <see cref="IControlStateWriter"/> é substituído por um fake que registra as chamadas — sem banco nem
/// catálogo (o catálogo de teste nem semeia ID.RA), isolando a lógica do projector.
/// </summary>
public sealed class BlastRadiusScoreProjectorTests
{
    [Fact]
    public async Task ProjectAsync_RaioCriticoEAmplo_RebaixaIdRa01E05ComoNonCompliant()
    {
        var (sut, ledger) = Build();
        var a = Assessment(RiskLevel.Critico, impactedCount: 8);

        await sut.ProjectAsync(a);

        ledger.Calls.Should().HaveCount(2);
        ledger.Calls.Select(c => c.Code).Should().BeEquivalentTo(new[] { "ID.RA-01", "ID.RA-05" });
        ledger.Calls.Should().OnlyContain(c =>
            c.Status == ControlStatus.NonCompliant &&
            c.Source == VerdictSource.Telemetry &&      // autoritativa — precisa rebaixar
            c.TenantId == a.TenantId);
    }

    [Fact]
    public async Task ProjectAsync_RaioAltoEAmplo_ConcedeCreditoParcial()
    {
        var (sut, ledger) = Build();
        var a = Assessment(RiskLevel.Alto, impactedCount: 6);

        await sut.ProjectAsync(a);

        ledger.Calls.Should().HaveCount(2);
        ledger.Calls.Should().OnlyContain(c => c.Status == ControlStatus.MitigatedByThirdParty);
    }

    [Theory]
    [InlineData(RiskLevel.Critico, 4)]   // severo, mas ESTREITO (< limiar 5)
    [InlineData(RiskLevel.Alto, 4)]
    [InlineData(RiskLevel.Medio, 20)]    // AMPLO, mas não severo
    [InlineData(RiskLevel.Baixo, 50)]
    public async Task ProjectAsync_ForaDosDoisLimiares_NaoTocaOLedger(RiskLevel level, int impactedCount)
    {
        var (sut, ledger) = Build();

        await sut.ProjectAsync(Assessment(level, impactedCount));

        ledger.Calls.Should().BeEmpty("penaliza só quando é severo E amplo; o caminho negativo não credita");
    }

    // ---- helpers ------------------------------------------------------------------

    private static (BlastRadiusScoreProjector Sut, RecordingControlStateWriter Ledger) Build()
    {
        var ledger = new RecordingControlStateWriter();
        var sut = new BlastRadiusScoreProjector(ledger, NullLogger<BlastRadiusScoreProjector>.Instance);
        return (sut, ledger);
    }

    private static BlastRadiusAssessment Assessment(RiskLevel level, int impactedCount) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        RootAssetId = Guid.NewGuid(),
        RiskLevel = level,
        ImpactedAssetCount = impactedCount,
        MaxDepth = 3,
        BlastRadiusScore = 82,
    };

    private sealed record LedgerCall(Guid TenantId, string Code, ControlStatus Status, VerdictSource Source);

    /// <summary>Fake do ledger (test double à mão, padrão do projeto): registra cada veredito aplicado.</summary>
    private sealed class RecordingControlStateWriter : IControlStateWriter
    {
        public List<LedgerCall> Calls { get; } = new();

        public Task<ComplianceVerdict> ApplyVerdictAsync(
            Guid tenantId, string subcategoryCode, ControlStatus status, string evidence,
            VerdictSource source, IReadOnlyList<ComplianceCheck>? checks = null,
            ControlIntelligence? intelligence = null,
            IReadOnlyList<MissingRequirement>? missingRequirements = null, CancellationToken ct = default)
        {
            Calls.Add(new LedgerCall(tenantId, subcategoryCode, status, source));
            return Task.FromResult(new ComplianceVerdict(status, evidence, 0, 15));
        }
    }
}
