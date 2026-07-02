using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// Seeds the NIST CSF 2.0 catalog (6 functions / 22 categories / 106 subcategories + 5 maturity
/// levels) from <c>data/nist_csf_2_0_catalog.json</c>. Idempotent: runs once, skips if present.
/// </summary>
public static class FrameworkSeeder
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task SeedAsync(AegisScoreDbContext db, string catalogPath, CancellationToken ct = default)
    {
        if (await db.FrameworkVersions.AnyAsync(f => f.Name == "NIST CSF 2.0", ct))
            return;

        if (!File.Exists(catalogPath))
            throw new FileNotFoundException($"NIST catalog not found at '{catalogPath}'.", catalogPath);

        var raw = await File.ReadAllTextAsync(catalogPath, ct);
        var catalog = JsonSerializer.Deserialize<CatalogDto>(raw, Json)
            ?? throw new InvalidOperationException("Could not parse the NIST catalog JSON.");

        var fv = new FrameworkVersion
        {
            Name = catalog.Framework,
            Source = catalog.Source,
            IsActive = true
        };

        foreach (var lvl in catalog.MaturityScale)
        {
            fv.MaturityLevels.Add(new MaturityLevel
            {
                FrameworkVersionId = fv.Id,
                Level = int.TryParse(lvl.Level, out var n) ? n : lvl.Score,
                Name = lvl.Name,
                Description = lvl.Description ?? "",
                Score = lvl.Score
            });
        }

        var order = 0;
        foreach (var fn in catalog.Functions)
        {
            var func = new NistFunction
            {
                FrameworkVersionId = fv.Id,
                Code = fn.Code,
                Name = fn.Name,
                Definition = fn.Definition ?? "",
                Order = order++
            };

            foreach (var cat in fn.Categories)
            {
                var category = new NistCategory
                {
                    FunctionId = func.Id,
                    Code = cat.Code,
                    Name = cat.Name,
                    Definition = cat.Definition ?? ""
                };

                foreach (var sub in cat.Subcategories)
                {
                    category.Subcategories.Add(new NistSubcategory
                    {
                        CategoryId = category.Id,
                        Code = sub.Code,
                        Description = sub.Description ?? "",
                        ImplementationExamples = sub.ImplementationExamples,
                        InformativeReferences = sub.InformativeReferences ?? new()
                    });
                }

                func.Categories.Add(category);
            }

            fv.Functions.Add(func);
        }

        db.FrameworkVersions.Add(fv);

        if (!await db.IcrWeightProfiles.AnyAsync(ct))
            db.IcrWeightProfiles.Add(new IcrWeightProfile { Name = "default" });

        await db.SaveChangesAsync(ct);
    }

    // ---- JSON shape (matches data/nist_csf_2_0_catalog.json) ----
    private record CatalogDto(string Framework, string? Source, List<LevelDto> MaturityScale, List<FunctionDto> Functions);
    private record LevelDto(string Level, string Name, string? Label, string? Description, int Score);
    private record FunctionDto(string Code, string Name, string? Definition, List<CategoryDto> Categories);
    private record CategoryDto(string Code, string Name, string? Definition, List<SubcategoryDto> Subcategories);
    private record SubcategoryDto(string Code, string Description, string? ImplementationExamples, List<string>? InformativeReferences);
}
