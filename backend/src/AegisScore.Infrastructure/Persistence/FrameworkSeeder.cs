using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Assessment;
using AegisScore.Application.Scoring;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// [AEGIS-MVP-POSTURE-01] Semeia os conjuntos de dados de referência, com SEPARAÇÃO DE RESPONSABILIDADES
/// e PROVENIÊNCIA auditável:
///   • conteúdo OFICIAL do NIST CSF 2.0 (6 funções / 22 categorias / 106 subcategorias) —
///     <c>nist_csf_2_0_catalog.json</c>;
///   • metodologia AUTORAL do AEGIS (escala de maturidade 5 níveis + pesos por subcategoria +
///     subcategorias não automatizadas) — <c>aegis_methodology.json</c>;
///   • regras/rubricas de avaliação (derivadas) — <c>aegis_assessment_rules.json</c>.
/// O catálogo oficial NÃO declara maturidade nem pesos como se fossem NIST; a maturidade e os pesos vêm da
/// metodologia. Cada conjunto ganha uma REVISÃO de <see cref="ReferenceDatasetProvenance"/> com hash do
/// conteúdo — o histórico é preservado (um hash antigo nunca some).
///
/// ATUALIZAÇÃO DETERMINÍSTICA (substitui o antigo "insert once"):
///   • conteúdo idêntico → nenhuma alteração;
///   • metodologia ou regras alteradas → reconciliação determinística in-place + nova revisão de proveniência;
///   • conteúdo oficial alterado SEM mudar a topologia de códigos → atualiza os campos oficiais no lugar e
///     registra nova revisão/hash do catálogo (mesma FrameworkVersion — o índice único em Name é invariante);
///   • mudança ESTRUTURAL (funções/categorias/subcategorias adicionadas ou removidas) → falha CLARA,
///     exigindo transição de versão deliberada (fora deste pacote — preserva estados de tenant e a
///     rastreabilidade dos snapshots).
/// Tudo FAIL-CLOSED e validado ANTES de persistir; resíduos (regra/nível que não pertencem mais ao artefato)
/// são detectados e tratados explicitamente, nunca apagados em silêncio.
/// </summary>
public static class FrameworkSeeder
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Nome canônico do catálogo (fonte única, compartilhada com o guard de prontidão).</summary>
    public const string CatalogName = SchemaReadinessGuard.CatalogName;

    // ==================================================================================================
    //  Catálogo NIST + Metodologia AEGIS + Proveniência (catálogo/metodologia)
    // ==================================================================================================

    public static async Task SeedAsync(
        AegisScoreDbContext db, string catalogPath, string methodologyPath, CancellationToken ct = default)
    {
        var catalog = Load<CatalogDto>(catalogPath, "catálogo NIST");
        var methodology = Load<MethodologyDto>(methodologyPath, "metodologia AEGIS");

        ValidateCatalogAndMethodology(catalog, methodology);

        var fileFunctions = NormalizeFromDto(catalog);
        var fileCatalogHash = ComputeCatalogHash(fileFunctions);
        var fileTopology = TopologySignature(fileFunctions);
        var methodologyHash = ComputeMethodologyHash(methodology);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.FrameworkVersions
            .Include(f => f.Functions).ThenInclude(fn => fn.Categories).ThenInclude(c => c.Subcategories)
            .Include(f => f.MaturityLevels)
            .Include(f => f.Provenance)
            .FirstOrDefaultAsync(f => f.Name == CatalogName, ct);

        if (existing is null)
        {
            await FreshSeedAsync(db, catalog, methodology, fileCatalogHash, methodologyHash, now, ct);
            return;
        }

        var dbFunctions = NormalizeFromEntities(existing);
        var dbHash = ComputeCatalogHash(dbFunctions);
        var changed = false;

        if (!string.Equals(dbHash, fileCatalogHash, StringComparison.Ordinal))
        {
            // Conteúdo oficial DIVERGE. Só é seguro atualizar no lugar se a TOPOLOGIA (conjunto de códigos) for
            // idêntica — caso contrário é mudança estrutural, que exige transição de versão deliberada.
            if (!string.Equals(TopologySignature(dbFunctions), fileTopology, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Mudança ESTRUTURAL do catálogo '{CatalogName}': o conjunto de códigos (funções/" +
                    "categorias/subcategorias) do artefato difere do persistido. Este pacote NÃO reescreve a " +
                    "topologia no lugar (preserva estados de tenant e a rastreabilidade dos snapshots) — uma " +
                    "mudança estrutural exige transição de versão de framework deliberada. Seed abortado.");

            // Mesma topologia, texto oficial diferente → atualiza campos oficiais + nova revisão de catálogo.
            changed |= UpdateOfficialFieldsInPlace(existing, catalog);
            changed |= UpsertProvenanceRevision(db, existing, ReferenceDatasetKind.NistCatalog,
                catalog.Provenance, fileCatalogHash, DatasetClassification.Official, now);
        }
        else
        {
            // Conteúdo oficial idêntico → apenas garante/atualiza a proveniência do catálogo.
            changed |= UpsertProvenanceRevision(db, existing, ReferenceDatasetKind.NistCatalog,
                catalog.Provenance, fileCatalogHash, DatasetClassification.Official, now);
        }

        // Metodologia (maturidade + pesos): reconciliação idempotente + proveniência.
        changed |= ReconcileWeights(existing, methodology);
        changed |= ReconcileMaturity(existing, methodology);
        changed |= UpsertProvenanceRevision(db, existing, ReferenceDatasetKind.AegisMethodology,
            methodology.Provenance, methodologyHash, DatasetClassification.Derived, now);

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private static async Task FreshSeedAsync(
        AegisScoreDbContext db, CatalogDto catalog, MethodologyDto methodology,
        string catalogHash, string methodologyHash, DateTimeOffset now, CancellationToken ct)
    {
        var fv = new FrameworkVersion
        {
            Name = catalog.Framework,
            Source = catalog.Source,
            IsActive = true,
        };

        foreach (var lvl in methodology.MaturityScale)
        {
            fv.MaturityLevels.Add(new MaturityLevel
            {
                FrameworkVersionId = fv.Id,
                Level = ResolveLevel(lvl),
                Name = lvl.Name,
                Description = lvl.Description ?? "",
                Score = lvl.Score,
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
                Order = order++,
            };

            foreach (var cat in fn.Categories)
            {
                var category = new NistCategory
                {
                    FunctionId = func.Id,
                    Code = cat.Code,
                    Name = cat.Name,
                    Definition = cat.Definition ?? "",
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
                        MaxScorePoints = methodology.SubcategoryWeights![sub.Code],   // validado: presente e > 0
                    });
                }

                func.Categories.Add(category);
            }

            fv.Functions.Add(func);
        }

        fv.Provenance.Add(BuildProvenanceRevision(fv.Id, ReferenceDatasetKind.NistCatalog,
            catalog.Provenance, catalogHash, DatasetClassification.Official, revision: 1, now));
        fv.Provenance.Add(BuildProvenanceRevision(fv.Id, ReferenceDatasetKind.AegisMethodology,
            methodology.Provenance, methodologyHash, DatasetClassification.Derived, revision: 1, now));

        db.FrameworkVersions.Add(fv);

        if (!await db.IcrWeightProfiles.AnyAsync(ct))
            db.IcrWeightProfiles.Add(new IcrWeightProfile { Name = "default" });

        await db.SaveChangesAsync(ct);
    }

    // ==================================================================================================
    //  Regras de avaliação (evidência TIPADA + proveniência das regras + resíduos)
    // ==================================================================================================

    /// <summary>
    /// Semeia/reconcilia as regras e as vincula ao catálogo por código. A natureza da evidência é TIPADA de
    /// forma determinística pela ÚNICA autoridade (<see cref="RuleEvaluator.DeriveEvidenceType"/>): só
    /// <c>MANUAL_AUDIT_REQUIRED</c> → Documentation; só ferramentas → Telemetry; ambos → Both. FAIL-CLOSED:
    /// artefato inválido, regra órfã/duplicada ou RESÍDUO (regra no banco que não está mais no artefato)
    /// abortam ANTES de persistir. O conjunto de regras ganha uma revisão de proveniência com hash.
    /// </summary>
    public static async Task SeedAssessmentRulesAsync(
        AegisScoreDbContext db, string rulesPath, string methodologyPath, CancellationToken ct = default)
    {
        var rules = Load<List<RuleDto>>(rulesPath, "regras de avaliação");
        var methodology = Load<MethodologyDto>(methodologyPath, "metodologia AEGIS");
        var now = DateTimeOffset.UtcNow;

        var fv = await db.FrameworkVersions
            .Include(f => f.Provenance)
            .FirstOrDefaultAsync(f => f.Name == CatalogName, ct);
        if (fv is null)
            throw new InvalidOperationException(
                $"Catálogo '{CatalogName}' ausente ao semear regras — o seed do catálogo deve preceder as regras.");

        var subIdByCode = await db.Subcategories
            .Select(s => new { s.Code, s.Id })
            .ToDictionaryAsync(s => s.Code, s => s.Id, ct);

        ValidateRules(rules, subIdByCode.Keys, methodology);

        var existing = await db.AssessmentRules.ToListAsync(ct);
        var existingByCode = existing.ToDictionary(r => r.SubcategoryCode, StringComparer.OrdinalIgnoreCase);
        var artifactCodes = rules.Select(r => r.SubcategoryId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // RESÍDUO: regra persistida que não está mais no artefato — trata explicitamente (não apaga em silêncio).
        var residual = existing.Where(r => !artifactCodes.Contains(r.SubcategoryCode)).Select(r => r.SubcategoryCode).ToList();
        if (residual.Count > 0)
            throw new InvalidOperationException(
                $"{residual.Count} regra(s) no banco não pertencem mais ao artefato de regras: " +
                $"{string.Join(", ", residual)}. Remoção de regra exige transição deliberada — seed abortado " +
                "para não apagar reference data em silêncio.");

        var changed = false;
        foreach (var r in rules)
        {
            var evidence = r.EvidenceRequirements ?? new();
            var evidenceType = RuleEvaluator.DeriveEvidenceType(evidence);

            if (existingByCode.TryGetValue(r.SubcategoryId, out var current))
            {
                changed |= Assign(current.EvaluationMetrics, v => current.EvaluationMetrics = v, r.EvaluationMetrics ?? new());
                changed |= Assign(current.CalculationLogic, v => current.CalculationLogic = v, r.CalculationLogic ?? "");
                changed |= Assign(current.EvidenceRequirements, v => current.EvidenceRequirements = v, evidence);
                if (current.EvidenceType != evidenceType) { current.EvidenceType = evidenceType; changed = true; }
            }
            else
            {
                db.AssessmentRules.Add(new AegisAssessmentRule
                {
                    SubcategoryId = subIdByCode[r.SubcategoryId],
                    SubcategoryCode = r.SubcategoryId,
                    EvaluationMetrics = r.EvaluationMetrics ?? new(),
                    CalculationLogic = r.CalculationLogic ?? "",
                    EvidenceRequirements = evidence,
                    EvidenceType = evidenceType,
                });
                changed = true;
            }
        }

        var rulesHash = ComputeRulesHash(rules);
        changed |= UpsertProvenanceRevision(db, fv, ReferenceDatasetKind.AegisAssessmentRules,
            methodology.RulesProvenance, rulesHash, DatasetClassification.Derived, now);

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    // ==================================================================================================
    //  Signal mappings (inalterado — já idempotente/incremental)
    // ==================================================================================================

    public static async Task SeedSignalMappingsAsync(AegisScoreDbContext db, CancellationToken ct = default)
    {
        var fv = await db.FrameworkVersions.FirstOrDefaultAsync(f => f.IsActive, ct);
        if (fv is null) return;

        var validCodes = (await (
            from s in db.Subcategories
            join c in db.Categories on s.CategoryId equals c.Id
            join fn in db.Functions on c.FunctionId equals fn.Id
            where fn.FrameworkVersionId == fv.Id
            select s.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var desired = DefaultSignalMappings(fv.Id);

        // Fail-closed: nenhum hint desconhecido pode ser persistido (a fórmula v1 não saberia avaliá-lo).
        var unknownHints = desired.Select(m => m.ScoringHint)
            .Where(h => !EvidenceSignalEvaluator.IsKnownHint(h)).Distinct().ToList();
        if (unknownHints.Count > 0)
            throw new InvalidOperationException(
                $"Signal mappings com hint(s) de scoring desconhecido(s): {string.Join(", ", unknownHints)}.");

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

    private static List<SignalMapping> DefaultSignalMappings(Guid fvId) => new()
    {
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.overall",  EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.AA-01", "PR.DS-01", "PR.PS-01"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.identity", EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.AA-01", "PR.AA-03", "PR.AA-05"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.data",     EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.DS-01", "PR.DS-02", "PR.DS-10"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.device",   EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.PS-01", "PR.PS-05", "DE.CM-01"),
        Map(fvId, ConnectorCapability.SecureScore, "secureScore.apps",     EvidenceSignalEvaluator.PercentHigherIsBetter, "PR.PS-06", "DE.CM-09"),
        Map(fvId, ConnectorCapability.Siem,        "siem.alert.highSeverity", EvidenceSignalEvaluator.EventControlProven, "DE.AE-02", "DE.CM-01"),
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

    // ==================================================================================================
    //  Validação FAIL-CLOSED dos artefatos (antes de persistir)
    // ==================================================================================================

    private static void ValidateCatalogAndMethodology(CatalogDto catalog, MethodologyDto methodology)
    {
        if (!string.Equals(catalog.Framework, CatalogName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Catálogo com framework '{catalog.Framework}' — este pacote só aceita '{CatalogName}'.");

        var functions = catalog.Functions ?? new();
        var categories = functions.SelectMany(f => f.Categories ?? new()).ToList();
        var subcategories = categories.SelectMany(c => c.Subcategories ?? new()).ToList();

        if (functions.Count != SchemaReadinessGuard.ExpectedFunctions ||
            categories.Count != SchemaReadinessGuard.ExpectedCategories ||
            subcategories.Count != SchemaReadinessGuard.ExpectedSubcategories)
            throw new InvalidOperationException(
                $"Topologia do catálogo inválida: {functions.Count} funções / {categories.Count} categorias / " +
                $"{subcategories.Count} subcategorias (esperado {SchemaReadinessGuard.ExpectedFunctions}/" +
                $"{SchemaReadinessGuard.ExpectedCategories}/{SchemaReadinessGuard.ExpectedSubcategories}).");

        var allCodes = functions.Select(f => f.Code)
            .Concat(categories.Select(c => c.Code))
            .Concat(subcategories.Select(s => s.Code)).ToList();
        if (allCodes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Catálogo contém código vazio.");

        var subCodes = subcategories.Select(s => s.Code).ToList();
        var dupSub = subCodes.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupSub.Count > 0)
            throw new InvalidOperationException($"Códigos de subcategoria duplicados no catálogo: {string.Join(", ", dupSub)}.");

        // Pesos: EXATAMENTE o conjunto dos 106 códigos, todos positivos. Sem fallback que mascare metodologia
        // incompleta — o artefato versionado tem de ser completo e fail-closed.
        var weights = methodology.SubcategoryWeights ?? new();
        var subCodeSet = subCodes.ToHashSet(StringComparer.Ordinal);
        var missing = subCodes.Where(c => !weights.ContainsKey(c)).ToList();
        var extra = weights.Keys.Where(c => !subCodeSet.Contains(c)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Metodologia sem peso para {missing.Count} subcategoria(s): {string.Join(", ", missing.Take(10))}.");
        if (extra.Count > 0)
            throw new InvalidOperationException($"Metodologia com peso para código(s) fora do catálogo: {string.Join(", ", extra.Take(10))}.");
        var nonPositive = weights.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList();
        if (nonPositive.Count > 0)
            throw new InvalidOperationException($"Metodologia com peso não positivo em: {string.Join(", ", nonPositive.Take(10))}.");

        // Maturidade: níveis resolvíveis, positivos e sem duplicata.
        var levels = (methodology.MaturityScale ?? new()).Select(ResolveLevel).ToList();
        if (levels.Count == 0)
            throw new InvalidOperationException("Metodologia sem escala de maturidade.");
        if (levels.Any(l => l <= 0))
            throw new InvalidOperationException("Metodologia com nível de maturidade inválido (<= 0).");
        if (levels.Distinct().Count() != levels.Count)
            throw new InvalidOperationException("Metodologia com nível de maturidade duplicado.");

        // Não automatizadas: DECLARADAS na metodologia == constante canônica das 7 == subcategorias do catálogo.
        var declared = (methodology.NonAutomatedSubcategories?.Codes ?? new()).ToHashSet(StringComparer.Ordinal);
        var canonical = SchemaReadinessGuard.NonAutomatedSubcategoryCodes.ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(canonical))
            throw new InvalidOperationException(
                "Subcategorias não automatizadas declaradas na metodologia divergem do conjunto canônico " +
                $"({string.Join(", ", SchemaReadinessGuard.NonAutomatedSubcategoryCodes)}).");
        var unknownNonAuto = declared.Where(c => !subCodeSet.Contains(c)).ToList();
        if (unknownNonAuto.Count > 0)
            throw new InvalidOperationException($"Subcategoria não automatizada fora do catálogo: {string.Join(", ", unknownNonAuto)}.");

        ValidateProvenanceMetadata(catalog.Provenance, "catálogo");
        ValidateProvenanceMetadata(methodology.Provenance, "metodologia");
        ValidateProvenanceMetadata(methodology.RulesProvenance, "regras");
    }

    private static void ValidateRules(List<RuleDto> rules, IEnumerable<string> catalogCodes, MethodologyDto methodology)
    {
        if (rules.Count == 0)
            throw new InvalidOperationException("Assessment rules JSON parsed to zero rules — check the file/format.");

        var codeSet = catalogCodes.ToHashSet(StringComparer.Ordinal);

        var empty = rules.Any(r => string.IsNullOrWhiteSpace(r.SubcategoryId));
        if (empty)
            throw new InvalidOperationException(
                "Assessment rule with empty subcategory_id — JSON binding likely failed (check snake_case).");

        var dup = rules.GroupBy(r => r.SubcategoryId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0)
            throw new InvalidOperationException($"Regra(s) duplicada(s) por subcategoria: {string.Join(", ", dup)}.");

        var orphan = rules.Select(r => r.SubcategoryId).Where(c => !codeSet.Contains(c)).ToList();
        if (orphan.Count > 0)
            throw new InvalidOperationException(
                $"{orphan.Count} regra(s) referenciam subcategorias inexistentes: {string.Join(", ", orphan)}. " +
                "As regras estão fora de sincronia com o catálogo NIST.");

        // 99 regras + 7 não automatizadas declaradas = 106, e o par é EXATO (nem sobreposição, nem lacuna extra).
        var ruleCodes = rules.Select(r => r.SubcategoryId).ToHashSet(StringComparer.Ordinal);
        var missingRule = codeSet.Where(c => !ruleCodes.Contains(c)).ToHashSet(StringComparer.Ordinal);
        var declaredNonAuto = SchemaReadinessGuard.NonAutomatedSubcategoryCodes.ToHashSet(StringComparer.Ordinal);
        if (!missingRule.SetEquals(declaredNonAuto))
            throw new InvalidOperationException(
                "As subcategorias sem regra divergem das 7 não automatizadas declaradas. Sem regra: " +
                $"[{string.Join(", ", missingRule.OrderBy(x => x))}]; declaradas: " +
                $"[{string.Join(", ", declaredNonAuto.OrderBy(x => x))}].");
        if (rules.Count != SchemaReadinessGuard.ExpectedRules)
            throw new InvalidOperationException(
                $"Esperado {SchemaReadinessGuard.ExpectedRules} regras, artefato traz {rules.Count}.");
    }

    private static void ValidateProvenanceMetadata(ProvenanceDto? dto, string label)
    {
        if (dto is null)
            throw new InvalidOperationException($"Proveniência ausente para {label}.");
        if (string.IsNullOrWhiteSpace(dto.Identifier))
            throw new InvalidOperationException($"Proveniência de {label} sem identifier.");
        if (string.IsNullOrWhiteSpace(dto.SchemaVersion))
            throw new InvalidOperationException($"Proveniência de {label} sem schemaVersion.");
        if (string.IsNullOrWhiteSpace(dto.Origin))
            throw new InvalidOperationException($"Proveniência de {label} sem origin.");
        if (string.IsNullOrWhiteSpace(dto.Classification))
            throw new InvalidOperationException($"Proveniência de {label} sem classification.");
    }

    // ==================================================================================================
    //  Proveniência — build/upsert com HISTÓRICO (revisões)
    // ==================================================================================================

    private static ReferenceDatasetProvenance BuildProvenanceRevision(
        Guid fvId, ReferenceDatasetKind kind, ProvenanceDto? dto, string contentHash,
        DatasetClassification fallbackClassification, int revision, DateTimeOffset now) => new()
    {
        FrameworkVersionId = fvId,
        Kind = kind,
        Revision = revision,
        IsCurrent = true,
        RecordedAt = now,
        Identifier = dto?.Identifier ?? kind.ToString(),
        Classification = ParseClassification(dto?.Classification, fallbackClassification),
        SchemaVersion = dto?.SchemaVersion ?? "",
        Origin = dto?.Origin ?? "",
        OfficialReference = dto?.OfficialReference,
        Release = dto?.Release,
        OfficialUrl = dto?.OfficialUrl,
        ObtainedOn = dto?.ObtainedOn,
        AppliesToCatalog = dto?.AppliesToCatalog,
        ContentHash = contentHash,
        MethodologyVersion = dto?.MethodologyVersion,
        Notes = dto?.Notes,
    };

    /// <summary>
    /// Upsert com HISTÓRICO: conteúdo idêntico ao vigente → só atualiza metadados descritivos no lugar; hash
    /// diferente → marca a revisão vigente como superada e grava uma NOVA revisão (o hash antigo permanece).
    /// </summary>
    private static bool UpsertProvenanceRevision(
        AegisScoreDbContext db, FrameworkVersion fv, ReferenceDatasetKind kind, ProvenanceDto? dto,
        string contentHash, DatasetClassification fallbackClassification, DateTimeOffset now)
    {
        var forKind = fv.Provenance.Where(p => p.Kind == kind).ToList();
        var current = forKind.FirstOrDefault(p => p.IsCurrent);

        if (current is null)
        {
            var rev = forKind.Count == 0 ? 1 : forKind.Max(p => p.Revision) + 1;
            var fresh = BuildProvenanceRevision(fv.Id, kind, dto, contentHash, fallbackClassification, rev, now);
            db.ReferenceDatasetProvenances.Add(fresh);
            fv.Provenance.Add(fresh);
            return true;
        }

        if (string.Equals(current.ContentHash, contentHash, StringComparison.Ordinal))
            return RefreshMetadata(current, dto, fallbackClassification);   // mesmo conteúdo, metadados no lugar

        // Conteúdo mudou → nova revisão; preserva a anterior (histórico).
        current.IsCurrent = false;
        current.SupersededAt = now;
        var next = forKind.Max(p => p.Revision) + 1;
        var revision = BuildProvenanceRevision(fv.Id, kind, dto, contentHash, fallbackClassification, next, now);
        db.ReferenceDatasetProvenances.Add(revision);
        fv.Provenance.Add(revision);
        return true;
    }

    private static bool RefreshMetadata(ReferenceDatasetProvenance current, ProvenanceDto? dto, DatasetClassification fallback)
    {
        var desired = BuildProvenanceRevision(current.FrameworkVersionId, current.Kind, dto, current.ContentHash,
            fallback, current.Revision, current.RecordedAt);
        var changed = false;
        if (current.Identifier != desired.Identifier) { current.Identifier = desired.Identifier; changed = true; }
        if (current.Classification != desired.Classification) { current.Classification = desired.Classification; changed = true; }
        if (current.SchemaVersion != desired.SchemaVersion) { current.SchemaVersion = desired.SchemaVersion; changed = true; }
        if (current.Origin != desired.Origin) { current.Origin = desired.Origin; changed = true; }
        if (current.OfficialReference != desired.OfficialReference) { current.OfficialReference = desired.OfficialReference; changed = true; }
        if (current.Release != desired.Release) { current.Release = desired.Release; changed = true; }
        if (current.OfficialUrl != desired.OfficialUrl) { current.OfficialUrl = desired.OfficialUrl; changed = true; }
        if (current.ObtainedOn != desired.ObtainedOn) { current.ObtainedOn = desired.ObtainedOn; changed = true; }
        if (current.AppliesToCatalog != desired.AppliesToCatalog) { current.AppliesToCatalog = desired.AppliesToCatalog; changed = true; }
        if (current.MethodologyVersion != desired.MethodologyVersion) { current.MethodologyVersion = desired.MethodologyVersion; changed = true; }
        if (current.Notes != desired.Notes) { current.Notes = desired.Notes; changed = true; }
        return changed;
    }

    private static DatasetClassification ParseClassification(string? value, DatasetClassification fallback) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "official" => DatasetClassification.Official,
            "derived" => DatasetClassification.Derived,
            _ => fallback,
        };

    // ==================================================================================================
    //  Reconcile / atualização in-place (idempotente; resíduos explícitos)
    // ==================================================================================================

    private static bool ReconcileWeights(FrameworkVersion fv, MethodologyDto methodology)
    {
        var changed = false;
        foreach (var sub in fv.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories))
        {
            // Validado: todo código do catálogo tem peso positivo na metodologia.
            var desired = methodology.SubcategoryWeights![sub.Code];
            if (sub.MaxScorePoints != desired) { sub.MaxScorePoints = desired; changed = true; }
        }
        return changed;
    }

    private static bool ReconcileMaturity(FrameworkVersion fv, MethodologyDto methodology)
    {
        var changed = false;
        var desiredByLevel = methodology.MaturityScale.ToDictionary(ResolveLevel);

        // Resíduo: nível persistido que não está mais na metodologia — trata explicitamente (não apaga).
        var residual = fv.MaturityLevels.Where(m => !desiredByLevel.ContainsKey(m.Level)).Select(m => m.Level).ToList();
        if (residual.Count > 0)
            throw new InvalidOperationException(
                $"Nível(is) de maturidade no banco fora do artefato: {string.Join(", ", residual)}. " +
                "Alteração da escala exige transição deliberada — seed abortado.");

        var byLevel = fv.MaturityLevels.ToDictionary(m => m.Level);
        foreach (var (level, lvl) in desiredByLevel)
        {
            if (byLevel.TryGetValue(level, out var e))
            {
                if (e.Name != lvl.Name) { e.Name = lvl.Name; changed = true; }
                if (e.Description != (lvl.Description ?? "")) { e.Description = lvl.Description ?? ""; changed = true; }
                if (e.Score != lvl.Score) { e.Score = lvl.Score; changed = true; }
            }
            else
            {
                fv.MaturityLevels.Add(new MaturityLevel
                {
                    FrameworkVersionId = fv.Id, Level = level, Name = lvl.Name,
                    Description = lvl.Description ?? "", Score = lvl.Score,
                });
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Atualiza os campos OFICIAIS (texto) no lugar, casados por código (topologia idêntica garantida).</summary>
    private static bool UpdateOfficialFieldsInPlace(FrameworkVersion fv, CatalogDto catalog)
    {
        var changed = false;
        var funcs = fv.Functions.ToDictionary(f => f.Code, StringComparer.Ordinal);
        var cats = fv.Functions.SelectMany(f => f.Categories).ToDictionary(c => c.Code, StringComparer.Ordinal);
        var subs = fv.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories)
            .ToDictionary(s => s.Code, StringComparer.Ordinal);

        foreach (var fn in catalog.Functions)
        {
            var e = funcs[fn.Code];
            if (e.Name != fn.Name) { e.Name = fn.Name; changed = true; }
            if (e.Definition != (fn.Definition ?? "")) { e.Definition = fn.Definition ?? ""; changed = true; }
            foreach (var cat in fn.Categories)
            {
                var ce = cats[cat.Code];
                if (ce.Name != cat.Name) { ce.Name = cat.Name; changed = true; }
                if (ce.Definition != (cat.Definition ?? "")) { ce.Definition = cat.Definition ?? ""; changed = true; }
                foreach (var sub in cat.Subcategories)
                {
                    var se = subs[sub.Code];
                    if (se.Description != (sub.Description ?? "")) { se.Description = sub.Description ?? ""; changed = true; }
                    if (se.ImplementationExamples != sub.ImplementationExamples) { se.ImplementationExamples = sub.ImplementationExamples; changed = true; }
                    var refs = sub.InformativeReferences ?? new();
                    if (!se.InformativeReferences.SequenceEqual(refs)) { se.InformativeReferences = refs; changed = true; }
                }
            }
        }
        return changed;
    }

    private static int ResolveLevel(LevelDto lvl) => int.TryParse(lvl.Level, out var n) ? n : lvl.Score;

    // ==================================================================================================
    //  Hashing determinístico (forma canônica length-prefixed — delimitadores nunca colidem)
    // ==================================================================================================

    private sealed record NormFunction(string Code, string Name, string Definition, List<NormCategory> Categories);
    private sealed record NormCategory(string Code, string Name, string Definition, List<NormSubcategory> Subcategories);
    private sealed record NormSubcategory(string Code, string Description, string? ImplementationExamples, List<string> Refs);

    private static List<NormFunction> NormalizeFromDto(CatalogDto catalog) =>
        catalog.Functions.Select(f => new NormFunction(
            f.Code, f.Name, f.Definition ?? "",
            (f.Categories ?? new()).Select(c => new NormCategory(
                c.Code, c.Name, c.Definition ?? "",
                (c.Subcategories ?? new()).Select(s => new NormSubcategory(
                    s.Code, s.Description ?? "", s.ImplementationExamples,
                    s.InformativeReferences ?? new())).ToList())).ToList())).ToList();

    private static List<NormFunction> NormalizeFromEntities(FrameworkVersion fv) =>
        fv.Functions.Select(f => new NormFunction(
            f.Code, f.Name, f.Definition,
            f.Categories.Select(c => new NormCategory(
                c.Code, c.Name, c.Definition,
                c.Subcategories.Select(s => new NormSubcategory(
                    s.Code, s.Description, s.ImplementationExamples,
                    s.InformativeReferences ?? new())).ToList())).ToList())).ToList();

    /// <summary>Assinatura de TOPOLOGIA: só o conjunto ordenado de códigos (funções/categorias/subcategorias).</summary>
    private static string TopologySignature(List<NormFunction> functions)
    {
        var codes = functions.Select(f => "F" + f.Code)
            .Concat(functions.SelectMany(f => f.Categories).Select(c => "C" + c.Code))
            .Concat(functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories).Select(s => "S" + s.Code))
            .OrderBy(x => x, StringComparer.Ordinal);
        return string.Join("|", codes);
    }

    /// <summary>Hash SHA-256 do conteúdo OFICIAL (independe de ordem de carga e de referências).</summary>
    private static string ComputeCatalogHash(List<NormFunction> functions)
    {
        var sb = new StringBuilder();
        foreach (var f in functions.OrderBy(x => x.Code, StringComparer.Ordinal))
        {
            W(sb, "F"); W(sb, f.Code); W(sb, f.Name); W(sb, f.Definition);
            foreach (var c in f.Categories.OrderBy(x => x.Code, StringComparer.Ordinal))
            {
                W(sb, "C"); W(sb, c.Code); W(sb, c.Name); W(sb, c.Definition);
                foreach (var s in c.Subcategories.OrderBy(x => x.Code, StringComparer.Ordinal))
                {
                    W(sb, "S"); W(sb, s.Code); W(sb, s.Description); W(sb, s.ImplementationExamples);
                    var refs = s.Refs.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    W(sb, refs.Count.ToString());
                    foreach (var r in refs) W(sb, r);
                }
            }
        }
        return Sha256Hex(sb.ToString());
    }

    private static string ComputeMethodologyHash(MethodologyDto m)
    {
        var sb = new StringBuilder();
        W(sb, m.MethodologyVersion);
        foreach (var lvl in m.MaturityScale.OrderBy(ResolveLevel))
        {
            W(sb, "L"); W(sb, lvl.Level); W(sb, lvl.Name); W(sb, lvl.Label);
            W(sb, lvl.Description); W(sb, lvl.Score.ToString());
        }
        var weights = (m.SubcategoryWeights ?? new()).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        W(sb, weights.Count.ToString());
        foreach (var kv in weights) { W(sb, kv.Key); W(sb, kv.Value.ToString()); }
        var codes = (m.NonAutomatedSubcategories?.Codes ?? new()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        W(sb, codes.Count.ToString());
        foreach (var code in codes) W(sb, code);
        return Sha256Hex(sb.ToString());
    }

    private static string ComputeRulesHash(List<RuleDto> rules)
    {
        var sb = new StringBuilder();
        foreach (var r in rules.OrderBy(x => x.SubcategoryId, StringComparer.Ordinal))
        {
            W(sb, r.SubcategoryId); W(sb, r.CalculationLogic);
            var metrics = r.EvaluationMetrics ?? new();
            W(sb, metrics.Count.ToString());
            foreach (var mx in metrics) W(sb, mx);
            var evidence = r.EvidenceRequirements ?? new();
            W(sb, evidence.Count.ToString());
            foreach (var e in evidence) W(sb, e);
        }
        return Sha256Hex(sb.ToString());
    }

    /// <summary>Campo length-prefixed: o comprimento declarado impede qualquer colisão de fronteira.</summary>
    private static void W(StringBuilder sb, string? value)
    {
        if (value is null) { sb.Append("_:\n"); return; }
        sb.Append(value.Length).Append(':').Append(value).Append('\n');
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ==================================================================================================
    //  Utilitários
    // ==================================================================================================

    private static T Load<T>(string path, string label)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{label} não encontrado em '{path}'.", path);
        var raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(raw, Json)
            ?? throw new InvalidOperationException($"Não foi possível interpretar o JSON de {label}.");
    }

    private static bool Assign(string current, Action<string> set, string desired)
    {
        if (current == desired) return false;
        set(desired); return true;
    }

    private static bool Assign(List<string> current, Action<List<string>> set, List<string> desired)
    {
        if (current.SequenceEqual(desired)) return false;
        set(desired); return true;
    }

    // ---- Shapes dos JSONs -----------------------------------------------------------------------------
    private sealed record CatalogDto(
        ProvenanceDto? Provenance, string Framework, string? Source, List<FunctionDto> Functions);
    private sealed record FunctionDto(string Code, string Name, string? Definition, List<CategoryDto> Categories);
    private sealed record CategoryDto(string Code, string Name, string? Definition, List<SubcategoryDto> Subcategories);
    private sealed record SubcategoryDto(
        string Code, string Description, string? ImplementationExamples, List<string>? InformativeReferences);

    private sealed record MethodologyDto(
        ProvenanceDto? Provenance,
        string MethodologyVersion,
        List<LevelDto> MaturityScale,
        Dictionary<string, int>? SubcategoryWeights,
        NonAutomatedDto? NonAutomatedSubcategories,
        ProvenanceDto? RulesProvenance);
    private sealed record LevelDto(string Level, string Name, string? Label, string? Description, int Score);
    private sealed record NonAutomatedDto(string? Reason, List<string>? Codes);

    private sealed record ProvenanceDto(
        string? Identifier, string? SchemaVersion, string? Classification, string? Origin,
        string? OfficialReference, string? Release, string? OfficialUrl, string? ObtainedOn,
        string? AppliesToCatalog, string? MethodologyVersion, string? Notes);

    private sealed record RuleDto(
        [property: JsonPropertyName("subcategory_id")] string SubcategoryId,
        [property: JsonPropertyName("evaluation_metrics")] List<string> EvaluationMetrics,
        [property: JsonPropertyName("calculation_logic")] string CalculationLogic,
        [property: JsonPropertyName("evidence_requirements")] List<string> EvidenceRequirements);
}
