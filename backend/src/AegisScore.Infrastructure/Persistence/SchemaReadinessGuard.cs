using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Assessment;
using AegisScore.Application.Scoring;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// [AEGIS-AUD-052] Resultado da verificação de prontidão do banco. Carrega TODOS os problemas
/// encontrados, não apenas o primeiro: quem opera o deploy precisa da lista inteira de uma vez, em vez
/// de descobrir uma pendência por reinício.
/// </summary>
public sealed record SchemaReadinessResult(bool IsReady, IReadOnlyList<string> Problems)
{
    public static SchemaReadinessResult Ready() => new(true, Array.Empty<string>());

    public static SchemaReadinessResult NotReady(IReadOnlyList<string> problems) => new(false, problems);

    public string Describe() => string.Join(" | ", Problems);
}

/// <summary>
/// [AEGIS-AUD-052] Verificação SOMENTE LEITURA do estado do banco no arranque da API.
///
/// A API deixou de aplicar migrations e de semear o catálogo: quem faz isso é o
/// <c>AegisScore.DbMigrator</c>, executado como etapa própria de implantação, sob advisory lock. Aqui
/// apenas CONSTATAMOS o resultado — nenhuma consulta abaixo emite DDL, insere, atualiza ou repara nada.
///
/// Falha em TODOS os ambientes, inclusive Development. Um serviço que sobe com catálogo ausente ou
/// duplicado não fica "degradado": ele passa a calcular postura de segurança sobre um denominador
/// errado e a reportar conformidade falsa — que é exatamente o risco que o programa de remediação
/// existe para eliminar. Melhor não subir.
/// </summary>
public static class SchemaReadinessGuard
{
    /// <summary>Nome canônico do catálogo semeado pelo <see cref="FrameworkSeeder"/>.</summary>
    public const string CatalogName = "NIST CSF 2.0";

    /// <summary>Contagens exatas do pacote CSF 2.0 adotado (6 funções / 22 categorias / 106 subcategorias).</summary>
    public const int ExpectedFunctions = 6;
    public const int ExpectedCategories = 22;
    public const int ExpectedSubcategories = 106;
    public const int ExpectedRules = 99;

    /// <summary>
    /// As SETE subcategorias de governança sem regra automatizável — ausência DECLARADA e validada, nunca
    /// esquecimento silencioso. É a autoridade que o guard usa para conferir o par 99 regras + 7 não
    /// automatizadas = 106; o seed cruza esta constante com o conjunto declarado na metodologia e com o
    /// conjunto real de subcategorias sem regra (as três precisam coincidir).
    /// </summary>
    public static readonly IReadOnlyList<string> NonAutomatedSubcategoryCodes = new[]
    {
        "GV.OC-01", "GV.OC-03", "GV.RM-02", "GV.RM-05", "GV.RR-02", "GV.RR-03", "GV.PO-02",
    };

    /// <summary>
    /// Reúne todas as pendências. Exceções de infraestrutura (banco inacessível) NÃO são convertidas em
    /// "não pronto": propagam, porque a causa e a ação do operador são outras — não adianta rodar o
    /// migrator se o banco não responde.
    /// </summary>
    /// <param name="keyRing">
    /// Nulo apenas quando a persistência do key ring está desligada (<c>Ephemeral</c>) — configuração
    /// restrita a testes, em que não existe migration a conferir. Fora disso, o contexto está sempre
    /// registrado e é verificado.
    /// </param>
    public static async Task<SchemaReadinessResult> CheckAsync(
        AegisScoreDbContext db,
        DataProtectionKeyDbContext? keyRing,
        CancellationToken ct = default)
    {
        var migrations = await CheckMigrationsAsync(db, keyRing, ct);

        // Sem schema aplicado, as consultas de catálogo falhariam por tabela inexistente — e o problema
        // real (migration pendente) já está registrado. Parar aqui dá a mensagem certa.
        if (!migrations.IsReady)
            return migrations;

        // Integridade mínima + validação COMPLETA do pacote CSF 2.0 (contagens, pesos, regras, hints,
        // proveniência, hashes). A API apenas CONSTATA — reparar é do DbMigrator.
        var integrity = await CheckCatalogIntegrityAsync(db, ct);
        var package = await CheckActivePackageAsync(db, ct);
        if (integrity.IsReady && package.IsReady)
            return SchemaReadinessResult.Ready();
        return SchemaReadinessResult.NotReady(integrity.Problems.Concat(package.Problems).ToList());
    }

    /// <summary>Migrations pendentes nos dois contextos. Separado para ser exercitável isoladamente.</summary>
    public static async Task<SchemaReadinessResult> CheckMigrationsAsync(
        AegisScoreDbContext db,
        DataProtectionKeyDbContext? keyRing,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var problems = new List<string>();

        var pendingMain = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pendingMain.Count > 0)
            problems.Add(
                $"AegisScoreDbContext com {pendingMain.Count} migration(s) pendente(s): " +
                $"{string.Join(", ", pendingMain)}");

        if (keyRing is not null)
        {
            var pendingKeyRing = (await keyRing.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pendingKeyRing.Count > 0)
                problems.Add(
                    $"DataProtectionKeyDbContext com {pendingKeyRing.Count} migration(s) pendente(s): " +
                    $"{string.Join(", ", pendingKeyRing)}");
        }

        return problems.Count == 0
            ? SchemaReadinessResult.Ready()
            : SchemaReadinessResult.NotReady(problems);
    }

    /// <summary>
    /// Integridade do conteúdo obrigatório: catálogo único, códigos sem duplicata, regras presentes e
    /// sem órfãs. Somente leitura — nenhuma consulta aqui altera dados.
    /// </summary>
    public static async Task<SchemaReadinessResult> CheckCatalogIntegrityAsync(
        AegisScoreDbContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var problems = new List<string>();

        var catalogs = await db.FrameworkVersions.CountAsync(f => f.Name == CatalogName, ct);
        if (catalogs == 0)
            problems.Add(
                $"Catálogo '{CatalogName}' ausente. Execute o AegisScore.DbMigrator antes de subir a API.");
        else if (catalogs > 1)
            problems.Add(
                $"Catálogo '{CatalogName}' DUPLICADO ({catalogs} versões). O scoring passaria a usar um " +
                "denominador inválido. Exige intervenção manual — a API não repara dados.");

        // Código repetido quebra o mapeamento código→subcategoria de todo o motor de avaliação.
        var duplicateCodes = await db.Subcategories
            .GroupBy(s => s.Code)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);
        if (duplicateCodes.Count > 0)
            problems.Add(
                $"{duplicateCodes.Count} código(s) de subcategoria duplicado(s): " +
                $"{string.Join(", ", duplicateCodes.Take(10))}");

        var rules = await db.AssessmentRules.CountAsync(ct);
        if (rules == 0)
            problems.Add(
                "Nenhuma regra de avaliação semeada. Sem elas o motor reporta conformidade sem base " +
                "técnica. Execute o AegisScore.DbMigrator.");

        // Integridade mínima: regra apontando para subcategoria inexistente produz veredito órfão.
        var orphanRules = await db.AssessmentRules
            .Where(r => !db.Subcategories.Any(s => s.Id == r.SubcategoryId))
            .CountAsync(ct);
        if (orphanRules > 0)
            problems.Add(
                $"{orphanRules} regra(s) de avaliação referenciam subcategorias inexistentes.");

        return problems.Count == 0
            ? SchemaReadinessResult.Ready()
            : SchemaReadinessResult.NotReady(problems);
    }

    /// <summary>
    /// [AEGIS-MVP-POSTURE-01] Validação COMPLETA do pacote CSF 2.0 ATIVO — impede que a aplicação aceite
    /// como pronta uma base inconsistente. Somente leitura. Verifica: EXATAMENTE uma FrameworkVersion ATIVA
    /// (qualquer nome) e que ela é a esperada; 6/22/106; 106 pesos positivos; 99 rubricas; as 7 ausências
    /// declaradas; tipo de evidência válido E COERENTE com os requisitos; hints conhecidos; proveniência
    /// vigente COMPLETA com classificação EXATA (oficial×derivado), versão da metodologia identificada e —
    /// o mais importante — os TRÊS hashes REDERIVADOS do banco batendo com a proveniência (alterar no banco
    /// uma descrição, peso, nível, requisito ou rubrica torna a base não pronta).
    /// </summary>
    public static async Task<SchemaReadinessResult> CheckActivePackageAsync(
        AegisScoreDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var problems = new List<string>();

        // EXATAMENTE uma versão ATIVA, de QUALQUER nome — uma segunda ativa com outro nome também é recusada.
        var active = await db.FrameworkVersions.Where(f => f.IsActive).Take(2).ToListAsync(ct);
        if (active.Count == 0)
            return SchemaReadinessResult.NotReady(new[]
            {
                "Nenhuma FrameworkVersion ATIVA. Execute o AegisScore.DbMigrator.",
            });
        if (active.Count > 1)
            problems.Add("Mais de uma FrameworkVersion ATIVA — exatamente uma é aplicável.");
        var fv = active.FirstOrDefault(f => f.Name == CatalogName);
        if (fv is null)
        {
            problems.Add($"A versão ATIVA não é o catálogo esperado '{CatalogName}'.");
            return SchemaReadinessResult.NotReady(problems);
        }

        // Grafo do catálogo ativo (campos oficiais) para rederivar o hash e contar a topologia.
        var funcEntities = await db.Functions.AsNoTracking()
            .Where(f => f.FrameworkVersionId == fv.Id)
            .Include(f => f.Categories).ThenInclude(c => c.Subcategories)
            .ToListAsync(ct);
        var functionNodes = funcEntities.Select(f => new ReferenceDataFingerprint.FunctionNode(
            f.Code, f.Name, f.Definition,
            f.Categories.Select(c => new ReferenceDataFingerprint.CategoryNode(
                c.Code, c.Name, c.Definition,
                c.Subcategories.Select(s => new ReferenceDataFingerprint.SubcategoryNode(
                    s.Code, s.Description, s.ImplementationExamples,
                    s.InformativeReferences ?? new())).ToList<ReferenceDataFingerprint.SubcategoryNode>()))
                .ToList<ReferenceDataFingerprint.CategoryNode>())).ToList();

        var categoriesCount = funcEntities.Sum(f => f.Categories.Count);
        var subEntities = funcEntities.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories).ToList();

        if (funcEntities.Count != ExpectedFunctions)
            problems.Add($"Funções: {funcEntities.Count} (esperado {ExpectedFunctions}).");
        if (categoriesCount != ExpectedCategories)
            problems.Add($"Categorias: {categoriesCount} (esperado {ExpectedCategories}).");
        if (subEntities.Count != ExpectedSubcategories)
            problems.Add($"Subcategorias: {subEntities.Count} (esperado {ExpectedSubcategories}).");

        var nonPositive = subEntities.Where(s => s.MaxScorePoints <= 0).Select(s => s.Code).ToList();
        if (nonPositive.Count > 0)
            problems.Add($"{nonPositive.Count} subcategoria(s) com peso não positivo: {string.Join(", ", nonPositive.Take(10))}.");

        var rules = await db.AssessmentRules.AsNoTracking()
            .Select(r => new { r.SubcategoryCode, r.EvidenceType, r.EvidenceRequirements, r.CalculationLogic, r.EvaluationMetrics })
            .ToListAsync(ct);
        if (rules.Count != ExpectedRules)
            problems.Add($"Rubricas: {rules.Count} (esperado {ExpectedRules}).");

        var badTypes = rules.Where(r => !Enum.IsDefined(typeof(RuleEvidenceType), r.EvidenceType))
            .Select(r => r.SubcategoryCode).ToList();
        if (badTypes.Count > 0)
            problems.Add($"{badTypes.Count} regra(s) com tipo de evidência inválido: {string.Join(", ", badTypes.Take(10))}.");

        // COERÊNCIA: o tipo persistido tem de bater com o derivado dos requisitos (adulterar um documental
        // para Telemetry, por exemplo, deve reprovar) — a derivação por string é usada aqui só para conferir.
        var incoherent = rules
            .Where(r => RuleEvaluator.DeriveEvidenceType(r.EvidenceRequirements) != r.EvidenceType)
            .Select(r => r.SubcategoryCode).ToList();
        if (incoherent.Count > 0)
            problems.Add($"{incoherent.Count} regra(s) com EvidenceType incoerente com EvidenceRequirements: {string.Join(", ", incoherent.Take(10))}.");

        var ruleCodes = rules.Select(r => r.SubcategoryCode).ToHashSet(StringComparer.Ordinal);
        var withoutRule = subEntities.Select(s => s.Code).Where(c => !ruleCodes.Contains(c)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expectedAbsent = NonAutomatedSubcategoryCodes.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (!withoutRule.SequenceEqual(expectedAbsent))
            problems.Add(
                "Subcategorias SEM regra divergem das 7 não automatizadas declaradas. " +
                $"Sem regra: [{string.Join(", ", withoutRule)}]; declaradas: [{string.Join(", ", expectedAbsent)}].");

        var hints = await db.SignalMappings
            .Where(m => m.FrameworkVersionId == fv.Id).Select(m => m.ScoringHint).ToListAsync(ct);
        var unknownHints = hints.Where(h => !EvidenceSignalEvaluator.IsKnownHint(h)).Distinct().ToList();
        if (unknownHints.Count > 0)
            problems.Add($"Hint(s) de scoring desconhecido(s): {string.Join(", ", unknownHints.Take(10))}.");

        // [AEGIS-MVP-SCORE-GUARD-SIEM-01] Invariante fail-closed: o mapping de scoring PROIBIDO — o alerta de alta
        // severidade do SIEM concedendo conformidade — nunca pode permanecer ATIVO em silêncio. A base não é aprovada
        // enquanto ele existir; o AegisScore.DbMigrator o aposenta antes desta verificação.
        var forbiddenSiemAlert = await db.SignalMappings.AnyAsync(m => m.FrameworkVersionId == fv.Id
            && m.Capability == RetiredSiemAlertMapping.Capability && m.SignalKey == RetiredSiemAlertMapping.SignalKey, ct);
        if (forbiddenSiemAlert)
            problems.Add(
                $"Mapping de scoring PROIBIDO ainda ativo: {RetiredSiemAlertMapping.Capability}/'{RetiredSiemAlertMapping.SignalKey}' " +
                "concederia conformidade por mera presença de alerta. Execute o AegisScore.DbMigrator para aposentá-lo " +
                "(AEGIS-MVP-SCORE-GUARD-SIEM-01).");

        // Proveniência vigente COMPLETA: metadados, classificação EXATA, hash válido, versão de metodologia.
        var provenance = await db.ReferenceDatasetProvenances
            .Where(p => p.FrameworkVersionId == fv.Id && p.IsCurrent).ToListAsync(ct);
        var expectedClass = new Dictionary<ReferenceDatasetKind, DatasetClassification>
        {
            [ReferenceDatasetKind.NistCatalog] = DatasetClassification.Official,
            [ReferenceDatasetKind.AegisMethodology] = DatasetClassification.Derived,
            [ReferenceDatasetKind.AegisAssessmentRules] = DatasetClassification.Derived,
        };
        foreach (var (kind, expected) in expectedClass)
        {
            var row = provenance.FirstOrDefault(p => p.Kind == kind);
            if (row is null)
            {
                problems.Add($"Proveniência vigente ausente para {kind}.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Identifier) || string.IsNullOrWhiteSpace(row.SchemaVersion) ||
                string.IsNullOrWhiteSpace(row.Origin))
                problems.Add($"Proveniência de {kind} com metadados obrigatórios vazios.");
            if (row.Classification != expected)
                problems.Add($"Proveniência de {kind} com classificação {row.Classification} (esperado {expected}).");
            if (!ReferenceDataFingerprint.IsSha256Hex(row.ContentHash))
                problems.Add($"Proveniência de {kind} com hash inválido.");
        }

        var methodologyProv = provenance.FirstOrDefault(p => p.Kind == ReferenceDatasetKind.AegisMethodology);
        if (methodologyProv is not null && string.IsNullOrWhiteSpace(methodologyProv.MethodologyVersion))
            problems.Add("Versão da metodologia AEGIS não identificada na proveniência.");

        // HASHES REDERIVADOS do banco × proveniência vigente — a prova real de integridade do conteúdo.
        var catalogProv = provenance.FirstOrDefault(p => p.Kind == ReferenceDatasetKind.NistCatalog);
        if (catalogProv is not null)
        {
            var rederived = ReferenceDataFingerprint.CatalogHash(functionNodes);
            if (!string.Equals(rederived, catalogProv.ContentHash, StringComparison.Ordinal))
                problems.Add("Hash do catálogo rederivado do banco NÃO bate com a proveniência (conteúdo oficial alterado).");
        }
        if (methodologyProv is not null)
        {
            var maturity = await db.MaturityLevels.AsNoTracking()
                .Where(m => m.FrameworkVersionId == fv.Id)
                .Select(m => new ReferenceDataFingerprint.MaturityNode(m.Level, m.Name, m.Description, m.Score))
                .ToListAsync(ct);
            var weights = subEntities.ToDictionary(s => s.Code, s => s.MaxScorePoints, StringComparer.Ordinal);
            var rederived = ReferenceDataFingerprint.MethodologyHash(
                methodologyProv.MethodologyVersion, maturity, weights, withoutRule);
            if (!string.Equals(rederived, methodologyProv.ContentHash, StringComparison.Ordinal))
                problems.Add("Hash da metodologia rederivado do banco NÃO bate com a proveniência (peso/nível/versão alterado).");
        }
        var rulesProv = provenance.FirstOrDefault(p => p.Kind == ReferenceDatasetKind.AegisAssessmentRules);
        if (rulesProv is not null)
        {
            var ruleNodes = rules.Select(r => new ReferenceDataFingerprint.RuleNode(
                r.SubcategoryCode, r.CalculationLogic, r.EvaluationMetrics, r.EvidenceRequirements)).ToList();
            var rederived = ReferenceDataFingerprint.RulesHash(ruleNodes);
            if (!string.Equals(rederived, rulesProv.ContentHash, StringComparison.Ordinal))
                problems.Add("Hash das regras rederivado do banco NÃO bate com a proveniência (rubrica/requisito alterado).");
        }

        return problems.Count == 0
            ? SchemaReadinessResult.Ready()
            : SchemaReadinessResult.NotReady(problems);
    }

    /// <summary>
    /// Verifica e ABORTA o arranque se o banco não estiver pronto. Mensagem única, com todas as
    /// pendências e a ação operacional correspondente.
    /// </summary>
    public static async Task EnsureReadyAsync(
        AegisScoreDbContext db,
        DataProtectionKeyDbContext? keyRing,
        CancellationToken ct = default)
    {
        var result = await CheckAsync(db, keyRing, ct);
        if (result.IsReady) return;

        throw new SchemaNotReadyException(
            "O banco de dados não está preparado para esta versão da API. " +
            $"Pendências: {result.Describe()}. " +
            "A API não aplica migrations nem semeia dados (AEGIS-AUD-052): execute o " +
            "AegisScore.DbMigrator como etapa de implantação e suba a API em seguida.");
    }
}

/// <summary>
/// [AEGIS-AUD-052] Banco incompatível com a versão da API. Distinta de falha de conexão: aqui o banco
/// respondeu, e o que falta é a etapa de implantação.
/// </summary>
public sealed class SchemaNotReadyException : Exception
{
    public SchemaNotReadyException(string message) : base(message) { }
}
