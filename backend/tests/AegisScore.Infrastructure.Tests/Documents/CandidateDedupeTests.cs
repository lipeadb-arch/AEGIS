using System.Linq;
using AegisScore.Api.Workers;
using AegisScore.Application.Abstractions;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Documents;

/// <summary>
/// Deduplicação dos candidatos da triagem (DocumentAnalysisWorker.DedupeCandidates) — a regressão do Render:
/// PR.AA-05 era julgado 2×, queimando cota. Prova: um código gera um único julgamento, maior confiança, vazio
/// descartado, ordem determinística e teto de chamadas respeitado.
/// </summary>
public sealed class CandidateDedupeTests
{
    [Fact]
    public void Dedupe_DoisPrAa05_ProduzUmUnicoJulgamento_ComMaiorConfianca()
    {
        var claims = new[]
        {
            new DocumentClaim("PR.AA-05", "primeiro", 0.4),
            new DocumentClaim(" pr.aa-05 ", "mesmo código, caixa/espaços diferentes", 0.9),
            new DocumentClaim("GV.PO-01", "outro", 0.5),
            new DocumentClaim("", "vazio descartado", 0.7),
        };

        var result = DocumentAnalysisWorker.DedupeCandidates(claims, maxControlCalls: 8);

        result.Count(c => c.SubcategoryCode == "PR.AA-05").Should().Be(1, "dois PR.AA-05 viram um único julgamento");
        result.Single(c => c.SubcategoryCode == "PR.AA-05").Confidence.Should().Be(0.9, "mantém a MAIOR confiança");
        result.Should().Contain(c => c.SubcategoryCode == "GV.PO-01");
        result.Should().NotContain(c => c.SubcategoryCode == "", "código vazio é descartado");
        result.First().SubcategoryCode.Should().Be("PR.AA-05", "ordem determinística: maior confiança primeiro");
    }

    [Fact]
    public void Dedupe_RespeitaOTetoDeChamadas()
    {
        var claims = new[]
        {
            new DocumentClaim("PR.AA-05", "a", 0.9),
            new DocumentClaim("GV.PO-01", "b", 0.8),
            new DocumentClaim("DE.CM-01", "c", 0.7),
        };

        DocumentAnalysisWorker.DedupeCandidates(claims, maxControlCalls: 2).Should().HaveCount(2);
    }
}
