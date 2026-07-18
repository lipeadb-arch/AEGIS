using System.Text;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Services;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Ai;

/// <summary>
/// Implementação do <see cref="IAssessmentRuleContextBuilder"/>: lê a <c>AegisAssessmentRule</c> (reference
/// data GLOBAL — sem query filter de tenant) pela FK de código e serializa os campos jsonb num bloco de
/// texto rotulado. O formato é pensado para o LLM: rótulos em caixa alta, uma métrica/fonte por linha e a
/// lógica de cálculo íntegra como rubrica de decisão — não JSON cru, que o modelo lê pior que prosa rotulada.
/// </summary>
public sealed class AssessmentRuleContextBuilder : IAssessmentRuleContextBuilder
{
    private readonly AegisScoreDbContext _db;

    public AssessmentRuleContextBuilder(AegisScoreDbContext db) => _db = db;

    public async Task<string?> BuildAsync(string subcategoryCode, CancellationToken ct = default)
    {
        // Reference data global (o motor compartilhado, não dado de tenant): não há isolamento a aplicar
        // e AsNoTracking porque é leitura pura para compor prompt.
        var rule = await _db.AssessmentRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SubcategoryCode == subcategoryCode, ct);

        if (rule is null)
            return null;

        var sb = new StringBuilder();

        sb.AppendLine("EVALUATION METRICS — what to measure technically:");
        foreach (var metric in rule.EvaluationMetrics)
            sb.AppendLine($"  • {metric}");

        sb.AppendLine();
        sb.AppendLine("CALCULATION LOGIC — the scoring rubric that decides the status (follow it strictly):");
        sb.AppendLine($"  {rule.CalculationLogic}");

        sb.AppendLine();
        sb.AppendLine("EXPECTED EVIDENCE SOURCES — where legitimate proof comes from in our stack:");
        foreach (var source in rule.EvidenceRequirements)
            sb.AppendLine($"  • {source}");

        return sb.ToString().TrimEnd();
    }
}
