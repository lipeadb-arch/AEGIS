using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// Seeds the NIST CSF 2.0 catalog (6 functions / 22 categories / 106 subcategories + 5 maturity
/// levels). Idempotent: runs once, skips if present.
/// Source: Catalog loaded via Seed:CatalogPath override (default: AegisScore.Api/Data/nist_csf_2_0_catalog.json)
/// </summary>
public static class FrameworkSeeder
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task SeedAsync(AegisScoreDbContext db, string catalogPath, CancellationToken ct = default)
    {
        // Backfill idempotente: garante peso > 0 em catálogos semeados ANTES de MaxScorePoints existir
        // (o denominador do Aegis Score nunca pode zerar). Roda antes do guard de existência do catálogo.
        await BackfillMissingWeightsAsync(db, ct);
        await SeedCatalogAsync(db, catalogPath, ct);
    }

    /// <summary>Semeia o catálogo NIST CSF 2.0. Idempotente: sai cedo se o framework já existe.</summary>
    public static async Task SeedCatalogAsync(AegisScoreDbContext db, string catalogPath, CancellationToken ct = default)
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
                        InformativeReferences = sub.InformativeReferences ?? new(),
                        // Nunca-zero: usa o peso do catálogo; se ausente ou <= 0, deriva da categoria.
                        MaxScorePoints = sub.MaxScorePoints is > 0 ? sub.MaxScorePoints.Value : DefaultWeight(sub.Code)
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

    /// <summary>
    /// Semeia as regras técnicas de avaliação (<c>aegis_assessment_rules.json</c>) e as vincula ao
    /// catálogo por código de subcategoria. Idempotente e INDEPENDENTE do seed do catálogo (guard próprio),
    /// para rodar mesmo quando o framework já existe. Fail-closed na integridade referencial: uma regra
    /// cujo código não casa com nenhuma subcategoria do catálogo aborta o seed ANTES de persistir — nunca
    /// entra FK órfã no banco.
    /// </summary>
    public static async Task SeedAssessmentRulesAsync(AegisScoreDbContext db, string rulesPath, CancellationToken ct = default)
    {
        if (await db.AssessmentRules.AnyAsync(ct))
            return;

        if (!File.Exists(rulesPath))
            throw new FileNotFoundException($"Assessment rules not found at '{rulesPath}'.", rulesPath);

        var raw = await File.ReadAllTextAsync(rulesPath, ct);
        var rules = JsonSerializer.Deserialize<List<RuleDto>>(raw, Json)
            ?? throw new InvalidOperationException("Could not parse the assessment rules JSON.");

        if (rules.Count == 0)
            throw new InvalidOperationException("Assessment rules JSON parsed to zero rules — check the file/format.");

        // Mapa código→Id das subcategorias (reference data global: cruza tenants, sem query filter).
        var subIdByCode = await db.Subcategories
            .Select(s => new { s.Code, s.Id })
            .ToDictionaryAsync(s => s.Code, s => s.Id, ct);

        var toAdd = new List<AegisAssessmentRule>(rules.Count);
        var unmatched = new List<string>();

        foreach (var r in rules)
        {
            // subcategory_id vazio = binding do JSON falhou (snake_case não mapeado) — falha ruidosa.
            if (string.IsNullOrWhiteSpace(r.SubcategoryId))
                throw new InvalidOperationException(
                    "Assessment rule with empty subcategory_id — JSON binding likely failed (check snake_case).");

            if (!subIdByCode.TryGetValue(r.SubcategoryId, out var subId))
            {
                unmatched.Add(r.SubcategoryId);
                continue;
            }

            toAdd.Add(new AegisAssessmentRule
            {
                SubcategoryId = subId,
                SubcategoryCode = r.SubcategoryId,
                EvaluationMetrics = r.EvaluationMetrics ?? new(),
                CalculationLogic = r.CalculationLogic ?? "",
                EvidenceRequirements = r.EvidenceRequirements ?? new()
            });
        }

        if (unmatched.Count > 0)
            throw new InvalidOperationException(
                $"{unmatched.Count} assessment rule(s) reference unknown subcategory codes: " +
                $"{string.Join(", ", unmatched)}. The rules file is out of sync with the NIST catalog.");

        db.AssessmentRules.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Preenche <c>MaxScorePoints</c> ausente/zero em subcategorias já persistidas (idempotente: não
    /// faz nada quando todas já têm peso). Cobre bases semeadas antes de a coluna existir.
    /// </summary>
    private static async Task BackfillMissingWeightsAsync(AegisScoreDbContext db, CancellationToken ct)
    {
        var stale = await db.Subcategories.Where(s => s.MaxScorePoints <= 0).ToListAsync(ct);
        if (stale.Count == 0) return;

        foreach (var s in stale)
            s.MaxScorePoints = DefaultWeight(s.Code);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fallback de peso (garantidamente &gt; 0) para quando o catálogo não traz <c>maxScorePoints</c>.
    /// Deriva da categoria (prefixo do código, ex.: "PR.AA-01" → "PR.AA") mantendo a mesma gradação
    /// de criticidade do JSON: identidade/dados no topo, governança/comunicação na base.
    /// </summary>
    private static int DefaultWeight(string subcategoryCode)
    {
        var dash = subcategoryCode.IndexOf('-');
        var category = dash > 0 ? subcategoryCode[..dash] : subcategoryCode;
        return category switch
        {
            "PR.AA" or "PR.DS"                                  => 20,  // identidade/acesso e dados (cripto)
            "PR.PS" or "PR.IR" or "DE.CM" or "ID.RA" or "GV.SC" => 15,  // plataforma, resiliência, risco, cadeia
            "GV.OC" or "GV.RM" or "GV.RR" or "GV.PO" or "GV.OV"
                or "RS.CO" or "RC.CO"                           => 5,   // governança, política, comunicação
            _                                                  => 10,  // médio: demais categorias e códigos novos
        };
    }

    // ---- JSON shape below. Source: Catalog loaded via Seed:CatalogPath override (default: AegisScore.Api/Data/nist_csf_2_0_catalog.json) ----
    private record CatalogDto(string Framework, string? Source, List<LevelDto> MaturityScale, List<FunctionDto> Functions);
    private record LevelDto(string Level, string Name, string? Label, string? Description, int Score);
    private record FunctionDto(string Code, string Name, string? Definition, List<CategoryDto> Categories);
    private record CategoryDto(string Code, string Name, string? Definition, List<SubcategoryDto> Subcategories);
    private record SubcategoryDto(string Code, string Description, string? ImplementationExamples, List<string>? InformativeReferences, int? MaxScorePoints);

    // ---- Regras de avaliação. O JSON é snake_case: os [JsonPropertyName] tornam o bind à prova de
    //      convenção (PropertyNameCaseInsensitive não converte underscores). ----
    private record RuleDto(
        [property: JsonPropertyName("subcategory_id")] string SubcategoryId,
        [property: JsonPropertyName("evaluation_metrics")] List<string> EvaluationMetrics,
        [property: JsonPropertyName("calculation_logic")] string CalculationLogic,
        [property: JsonPropertyName("evidence_requirements")] List<string> EvidenceRequirements);
}
