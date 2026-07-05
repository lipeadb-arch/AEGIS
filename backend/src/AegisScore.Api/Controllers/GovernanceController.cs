using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// GOVERN (GV) — visão de pilar. Cruza o catálogo GV (referência compartilhada, sem filtro) com o
/// ledger de cobertura híbrida do tenant (documentos + entrevistas). Sem [FromHeader] de tenant.
/// </summary>
[ApiController]
[Route("api/v1/governance")]
public class GovernanceController : ControllerBase
{
    private const string GovernFunctionCode = "GV";

    private readonly AegisScoreDbContext _db;
    public GovernanceController(AegisScoreDbContext db) => _db = db;

    /// <summary>
    /// Mapa de cobertura do pilar GOVERN: cada subcategoria GV com o status derivado do ledger
    /// (Coberto / Parcial / NaoCoberto) e a fonte de evidência (documento, entrevista, ambos).
    /// </summary>
    [HttpGet("coverage")]
    public async Task<ActionResult<GovernCoverageDto>> Coverage(CancellationToken ct)
    {
        var govern = await _db.Categories
            .Include(c => c.Subcategories)
            .Include(c => c.Function)
            .AsNoTracking()
            .Where(c => c.Function!.Code == GovernFunctionCode)
            .OrderBy(c => c.Code)
            .ToListAsync(ct);

        if (govern.Count == 0) return NotFound("Função GOVERN não encontrada. O seed rodou?");

        var ledger = await _db.SubcategoryCoverages.AsNoTracking().ToListAsync(ct);
        var byCode = ledger.ToDictionary(x => x.SubcategoryCode);

        int total = 0, covered = 0, partial = 0;
        var categories = govern.Select(cat =>
        {
            var subs = cat.Subcategories.OrderBy(s => s.Code).Select(s =>
            {
                byCode.TryGetValue(s.Code, out var cov);
                var status = cov?.Status ?? CoverageStatus.NaoCoberto;
                var source = cov?.EvidenceSource ?? CoverageEvidenceSource.None;
                total++;
                if (status == CoverageStatus.Coberto) covered++;
                else if (status == CoverageStatus.Parcial) partial++;
                return new CoverageCellDto(s.Code, s.Description, status.ToString(), source.ToString());
            }).ToList();
            return new GovernCategoryCoverageDto(cat.Code, cat.Name, subs);
        }).ToList();

        var coveredPct = total == 0 ? 0 : Math.Round(100.0 * covered / total, 1);
        var partialPct = total == 0 ? 0 : Math.Round(100.0 * partial / total, 1);
        return new GovernCoverageDto(coveredPct, partialPct, categories);
    }
}
