using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

[ApiController]
[Route("api/v1/framework")]
public class FrameworkController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    public FrameworkController(AegisScoreDbContext db) => _db = db;

    /// <summary>The active control framework (functions → categories → subcategories).</summary>
    [HttpGet("active")]
    public async Task<ActionResult<FrameworkDto>> Active(CancellationToken ct)
    {
        var fv = await _db.FrameworkVersions
            .Include(f => f.Functions).ThenInclude(fn => fn.Categories).ThenInclude(c => c.Subcategories)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.IsActive, ct);

        if (fv is null) return NotFound("No active framework. Has the seed run?");

        var dto = new FrameworkDto(fv.Id, fv.Name, fv.Source,
            fv.Functions.OrderBy(f => f.Order).Select(fn => new FunctionDto(
                fn.Code, fn.Name, fn.Definition,
                fn.Categories.OrderBy(c => c.Code).Select(c => new CategoryDto(
                    c.Code, c.Name, c.Definition,
                    c.Subcategories.OrderBy(s => s.Code)
                        .Select(s => new SubcategoryDto(s.Code, s.Description)).ToList()
                )).ToList()
            )).ToList());

        return dto;
    }

    /// <summary>The 1–5 CMMI maturity scale.</summary>
    [HttpGet("maturity-levels")]
    public async Task<ActionResult<IEnumerable<MaturityLevelDto>>> Levels(CancellationToken ct) =>
        await _db.MaturityLevels.AsNoTracking().OrderBy(l => l.Level)
            .Select(l => new MaturityLevelDto(l.Level, l.Name, l.Description, l.Score))
            .ToListAsync(ct);
}
