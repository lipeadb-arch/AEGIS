using Microsoft.EntityFrameworkCore;

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
        return migrations.IsReady
            ? await CheckCatalogIntegrityAsync(db, ct)
            : migrations;
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
