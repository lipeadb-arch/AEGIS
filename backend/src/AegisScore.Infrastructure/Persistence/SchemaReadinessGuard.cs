using Microsoft.EntityFrameworkCore;
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
    /// como pronta uma base inconsistente. Somente leitura. Verifica: exatamente UMA versão ativa aplicável;
    /// 6/22/106 na versão ativa; 106 pesos positivos; 99 rubricas; as 7 ausências declaradas; tipos de
    /// evidência válidos; hints conhecidos; proveniência vigente COMPLETA (catálogo/metodologia/regras) com
    /// hash SHA-256 válido e versão da metodologia identificada.
    /// </summary>
    public static async Task<SchemaReadinessResult> CheckActivePackageAsync(
        AegisScoreDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var problems = new List<string>();

        var active = await db.FrameworkVersions
            .Where(f => f.Name == CatalogName && f.IsActive).ToListAsync(ct);
        if (active.Count == 0)
            return SchemaReadinessResult.NotReady(new[]
            {
                $"Nenhuma versão ATIVA do catálogo '{CatalogName}'. Execute o AegisScore.DbMigrator.",
            });
        if (active.Count > 1)
            problems.Add($"{active.Count} versões ATIVAS do catálogo '{CatalogName}' — exatamente uma é aplicável.");
        var fv = active[0];

        var functions = await db.Functions.CountAsync(f => f.FrameworkVersionId == fv.Id, ct);
        var categories = await (from c in db.Categories
                                join fn in db.Functions on c.FunctionId equals fn.Id
                                where fn.FrameworkVersionId == fv.Id
                                select c.Id).CountAsync(ct);
        var subs = await (from s in db.Subcategories
                          join c in db.Categories on s.CategoryId equals c.Id
                          join fn in db.Functions on c.FunctionId equals fn.Id
                          where fn.FrameworkVersionId == fv.Id
                          select new { s.Code, s.MaxScorePoints }).ToListAsync(ct);

        if (functions != ExpectedFunctions)
            problems.Add($"Funções: {functions} (esperado {ExpectedFunctions}).");
        if (categories != ExpectedCategories)
            problems.Add($"Categorias: {categories} (esperado {ExpectedCategories}).");
        if (subs.Count != ExpectedSubcategories)
            problems.Add($"Subcategorias: {subs.Count} (esperado {ExpectedSubcategories}).");

        var nonPositive = subs.Where(s => s.MaxScorePoints <= 0).Select(s => s.Code).ToList();
        if (nonPositive.Count > 0)
            problems.Add($"{nonPositive.Count} subcategoria(s) com peso não positivo: {string.Join(", ", nonPositive.Take(10))}.");

        var rules = await db.AssessmentRules
            .Select(r => new { r.SubcategoryCode, r.EvidenceType }).ToListAsync(ct);
        if (rules.Count != ExpectedRules)
            problems.Add($"Rubricas: {rules.Count} (esperado {ExpectedRules}).");

        var badTypes = rules.Where(r => !Enum.IsDefined(typeof(RuleEvidenceType), r.EvidenceType))
            .Select(r => r.SubcategoryCode).ToList();
        if (badTypes.Count > 0)
            problems.Add($"{badTypes.Count} regra(s) com tipo de evidência inválido: {string.Join(", ", badTypes.Take(10))}.");

        var ruleCodes = rules.Select(r => r.SubcategoryCode).ToHashSet(StringComparer.Ordinal);
        var withoutRule = subs.Select(s => s.Code).Where(c => !ruleCodes.Contains(c)).OrderBy(x => x, StringComparer.Ordinal).ToList();
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

        // Proveniência vigente COMPLETA para os três conjuntos, com hash válido e versão de metodologia.
        var provenance = await db.ReferenceDatasetProvenances
            .Where(p => p.FrameworkVersionId == fv.Id && p.IsCurrent).ToListAsync(ct);
        foreach (var kind in new[]
        {
            ReferenceDatasetKind.NistCatalog, ReferenceDatasetKind.AegisMethodology, ReferenceDatasetKind.AegisAssessmentRules,
        })
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
            if (!IsSha256Hex(row.ContentHash))
                problems.Add($"Proveniência de {kind} com hash inválido.");
        }

        var methodology = provenance.FirstOrDefault(p => p.Kind == ReferenceDatasetKind.AegisMethodology);
        if (methodology is not null && string.IsNullOrWhiteSpace(methodology.MethodologyVersion))
            problems.Add("Versão da metodologia AEGIS não identificada na proveniência.");

        return problems.Count == 0
            ? SchemaReadinessResult.Ready()
            : SchemaReadinessResult.NotReady(problems);
    }

    private static bool IsSha256Hex(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.Length == 64 && hash.All(Uri.IsHexDigit);

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
