using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Scoring;
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
    /// catálogo por código de subcategoria. INDEPENDENTE do seed do catálogo (guard próprio), para rodar
    /// mesmo quando o framework já existe. Fail-closed na integridade referencial: uma regra cujo código
    /// não casa com nenhuma subcategoria do catálogo aborta o seed ANTES de persistir — nunca entra FK
    /// órfã no banco.
    ///
    /// ⚠️ INCREMENTAL, não "tudo ou nada". O guard anterior era <c>if (AnyAsync) return</c>: bastava UMA
    /// regra no banco para o arquivo inteiro ser ignorado para sempre, de modo que enriquecer o catálogo
    /// exigia TRUNCATE manual da tabela — operação que ninguém faz em produção e que apagaria qualquer
    /// ajuste feito no banco. Agora só as regras AUSENTES são inseridas: adicionar um controle ao JSON
    /// passa a bastar, e as regras já persistidas nunca são sobrescritas (a edição no banco é soberana).
    /// </summary>
    public static async Task SeedAssessmentRulesAsync(AegisScoreDbContext db, string rulesPath, CancellationToken ct = default)
    {
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

        // Regras JÁ persistidas: o que existe no banco não é tocado. O índice único em SubcategoryCode
        // impede duplicata de qualquer forma; a checagem prévia evita o round-trip perdido.
        var existing = (await db.AssessmentRules.Select(r => r.SubcategoryCode).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

            if (existing.Contains(r.SubcategoryId))
                continue;   // já semeada — preserva o que está no banco

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

        if (toAdd.Count == 0)
            return;

        db.AssessmentRules.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// [AEGIS-AUD-043] Semeia os mapeamentos (Capability, SignalKey) → subcategorias NIST no FRAMEWORK ATIVO.
    /// <see cref="SignalMapping"/> é a ÚNICA autoridade determinística de mapeamento; sem estes registros, o
    /// executor de ingestão rejeita (422) todo sinal. INCREMENTAL e idempotente: só insere o que ainda falta
    /// por <c>(FrameworkVersionId, Capability, SignalKey)</c> — a unicidade é invariante de banco. Fail-closed
    /// na integridade referencial: um mapping que cite um código inexistente no catálogo ABORTA o seed.
    /// </summary>
    public static async Task SeedSignalMappingsAsync(AegisScoreDbContext db, CancellationToken ct = default)
    {
        var fv = await db.FrameworkVersions.FirstOrDefaultAsync(f => f.IsActive, ct);
        if (fv is null) return;   // catálogo ainda não semeado nesta base — nada a fazer

        var validCodes = (await (
            from s in db.Subcategories
            join c in db.Categories on s.CategoryId equals c.Id
            join fn in db.Functions on c.FunctionId equals fn.Id
            where fn.FrameworkVersionId == fv.Id
            select s.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var desired = DefaultSignalMappings(fv.Id);

        var unknown = desired
            .SelectMany(m => m.SubcategoryCodes.Select(code => (m.SignalKey, Code: code)))
            .Where(x => !validCodes.Contains(x.Code))
            .ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                "Signal mappings reference unknown NIST codes: " +
                string.Join(", ", unknown.Select(x => $"{x.SignalKey}->{x.Code}")) +
                ". The mappings are out of sync with the NIST catalog.");

        var existingMappings = await db.SignalMappings
            .Where(m => m.FrameworkVersionId == fv.Id)
            .ToListAsync(ct);
        var existingByKey = existingMappings.ToDictionary(m => (m.Capability, m.SignalKey));

        // [AEGIS-AUD-019] Backfill IDEMPOTENTE do ScoringHint nos mappings JÁ existentes — não só nos novos:
        // a regra determinística de scoring v1 é atribuída/atualizada aqui, sem depender de recriar o registro.
        var changed = false;
        foreach (var d in desired)
        {
            if (existingByKey.TryGetValue((d.Capability, d.SignalKey), out var e) && e.ScoringHint != d.ScoringHint)
            {
                e.ScoringHint = d.ScoringHint;
                changed = true;
            }
        }

        var toAdd = desired.Where(m => !existingByKey.ContainsKey((m.Capability, m.SignalKey))).ToList();
        if (toAdd.Count > 0)
            db.SignalMappings.AddRange(toAdd);

        if (changed || toAdd.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mapeamentos de referência (todos com códigos que existem no catálogo NIST CSF 2.0): os cinco sinais do
    /// MicrosoftSecureScoreConnector + um sinal SIEM e um EDR de referência para a ingestão genérica.
    /// </summary>
    private static List<SignalMapping> DefaultSignalMappings(Guid fvId) => new()
    {
        // Secure Score: percentual em que MAIOR é melhor (thresholds explícitos na fórmula v1).
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.overall",  EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.AA-01", "PR.DS-01", "PR.PS-01"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.identity", EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.AA-01", "PR.AA-03", "PR.AA-05"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.data",     EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.DS-01", "PR.DS-02", "PR.DS-10"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.device",   EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.PS-01", "PR.PS-05", "DE.CM-01"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.apps",     EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.PS-06", "DE.CM-09"),
        // SIEM: um alerta correlacionado de alta severidade COMPROVA análise de evento adverso + monitoração.
        Map(fvId, ConnectorCapability.Siem,        "siem.alert.highSeverity", EvidenceSignalEvaluator.EventControlProven, "DE.AE-02", "DE.CM-01"),
        // EDR: uma ameaça bloqueada no endpoint COMPROVA monitoração contínua + mitigação de incidente.
        Map(fvId, ConnectorCapability.Edr,         "edr.threat.blocked", EvidenceSignalEvaluator.EventControlProven, "DE.CM-01", "RS.MI-01"),
    };

    private static SignalMapping Map(
        Guid fvId, ConnectorCapability capability, string signalKey, string scoringHint, params string[] codes) => new()
    {
        FrameworkVersionId = fvId,
        Capability = capability,
        SignalKey = signalKey,
        SubcategoryCodes = codes.ToList(),
        Weight = 1.0,
        ScoringHint = scoringHint,
    };

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
