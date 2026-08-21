using System.Security.Cryptography;
using System.Text;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// [AEGIS-MVP-POSTURE-01] Autoridade PURA e ÚNICA de fingerprint dos conjuntos de dados de referência.
/// O <see cref="FrameworkSeeder"/> a alimenta a partir dos ARTEFATOS (JSON) e o <see cref="SchemaReadinessGuard"/>
/// a alimenta a partir do BANCO — os dois usam EXATAMENTE o mesmo algoritmo, sem duplicá-lo. Assim o guard
/// consegue REDERIVAR os três hashes do estado persistido e compará-los com a proveniência vigente: alterar
/// no banco uma descrição, peso, nível, requisito ou rubrica muda o hash rederivado e reprova a prontidão.
///
/// Forma canônica length-prefixed (o comprimento declarado impede colisão de fronteira). Só entram campos
/// EFETIVAMENTE persistidos: a maturidade NÃO inclui <c>label</c> (não existe coluna para ele — seria um
/// hash impossível de rederivar do banco).
/// </summary>
public static class ReferenceDataFingerprint
{
    public sealed record FunctionNode(string Code, string Name, string Definition, IReadOnlyList<CategoryNode> Categories);
    public sealed record CategoryNode(string Code, string Name, string Definition, IReadOnlyList<SubcategoryNode> Subcategories);
    public sealed record SubcategoryNode(string Code, string Description, string? ImplementationExamples, IReadOnlyList<string> InformativeReferences);
    public sealed record MaturityNode(int Level, string Name, string? Description, int Score);
    public sealed record RuleNode(string SubcategoryCode, string CalculationLogic, IReadOnlyList<string> EvaluationMetrics, IReadOnlyList<string> EvidenceRequirements);

    /// <summary>Hash SHA-256 do conteúdo OFICIAL (independe de ordem de carga e da ordem das referências).</summary>
    public static string CatalogHash(IReadOnlyList<FunctionNode> functions)
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
                    var refs = s.InformativeReferences.OrderBy(x => x, StringComparer.Ordinal).ToList();
                    W(sb, refs.Count.ToString());
                    foreach (var r in refs) W(sb, r);
                }
            }
        }
        return Sha256Hex(sb.ToString());
    }

    /// <summary>
    /// Assinatura ESTRUTURAL incluindo os relacionamentos pai→filho: função, função→categoria e
    /// categoria→subcategoria. Detecta uma categoria movida para outra função ou uma subcategoria movida
    /// para outra categoria mesmo quando NENHUM código muda — o mesmo conjunto de códigos com hierarquia
    /// diferente produz assinatura diferente.
    /// </summary>
    public static string TopologySignature(IReadOnlyList<FunctionNode> functions)
    {
        var entries = new List<string>();
        foreach (var f in functions)
        {
            entries.Add($"F:{f.Code}");
            foreach (var c in f.Categories)
            {
                entries.Add($"C:{f.Code}>{c.Code}");
                foreach (var s in c.Subcategories)
                    entries.Add($"S:{f.Code}>{c.Code}>{s.Code}");
            }
        }
        entries.Sort(StringComparer.Ordinal);
        return string.Join("|", entries);
    }

    /// <summary>Hash da metodologia AUTORAL — só campos persistidos (SEM <c>label</c>).</summary>
    public static string MethodologyHash(
        string? methodologyVersion,
        IReadOnlyList<MaturityNode> maturity,
        IReadOnlyDictionary<string, int> weights,
        IReadOnlyCollection<string> nonAutomatedCodes)
    {
        var sb = new StringBuilder();
        W(sb, methodologyVersion);
        foreach (var lvl in maturity.OrderBy(x => x.Level))
        {
            W(sb, "L"); W(sb, lvl.Level.ToString()); W(sb, lvl.Name); W(sb, lvl.Description); W(sb, lvl.Score.ToString());
        }
        var ordered = weights.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
        W(sb, ordered.Count.ToString());
        foreach (var kv in ordered) { W(sb, kv.Key); W(sb, kv.Value.ToString()); }
        var codes = nonAutomatedCodes.OrderBy(x => x, StringComparer.Ordinal).ToList();
        W(sb, codes.Count.ToString());
        foreach (var code in codes) W(sb, code);
        return Sha256Hex(sb.ToString());
    }

    /// <summary>Hash das rubricas de avaliação.</summary>
    public static string RulesHash(IReadOnlyList<RuleNode> rules)
    {
        var sb = new StringBuilder();
        foreach (var r in rules.OrderBy(x => x.SubcategoryCode, StringComparer.Ordinal))
        {
            W(sb, r.SubcategoryCode); W(sb, r.CalculationLogic);
            W(sb, r.EvaluationMetrics.Count.ToString());
            foreach (var mx in r.EvaluationMetrics) W(sb, mx);
            W(sb, r.EvidenceRequirements.Count.ToString());
            foreach (var e in r.EvidenceRequirements) W(sb, e);
        }
        return Sha256Hex(sb.ToString());
    }

    public static bool IsSha256Hex(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.Length == 64 && hash.All(Uri.IsHexDigit);

    /// <summary>Campo length-prefixed: o comprimento declarado impede qualquer colisão de fronteira.</summary>
    private static void W(StringBuilder sb, string? value)
    {
        if (value is null) { sb.Append("_:\n"); return; }
        sb.Append(value.Length).Append(':').Append(value).Append('\n');
    }

    public static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
