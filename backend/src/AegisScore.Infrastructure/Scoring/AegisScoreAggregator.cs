using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Scoring;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Infrastructure.Scoring;

/// <summary>
/// [AEGIS-AUD-001/002] Autoridade de AGREGAÇÃO compartilhada do Aegis Score sobre o AegisScoreDbContext — a
/// MESMA semântica para o Score Atual (<c>CurrentScoreQuery</c>) e a foto diária (<c>AegisScoreSnapshotWorker</c>),
/// de modo que numerador, denominador e restrição de universo NUNCA tenham duas implementações capazes de divergir.
///
/// Restringe os estados AVALIADOS ao catálogo do framework ATIVO (join TenantControlState → Subcategory →
/// Category → Function → FrameworkVersion.IsActive): estados de uma versão ANTIGA do framework não entram no
/// numerador nem no denominador do score (o que geraria cobertura &gt; 100% ou score incompatível). Calcula o
/// universo ELEGÍVEL (peso/contagem de TODAS as subcategorias do catálogo ativo) — o denominador de COBERTURA.
/// A fórmula/arredondamento/estado NotEvaluated ficam na autoridade única <see cref="AegisScoreFormulaV1"/>.
///
/// Opera sob o tenant AMBIENTE do contexto recebido (Global Query Filter fail-closed nos <c>TenantControlState</c>);
/// o catálogo é reference data global (sem filtro de tenant). NÃO usa IgnoreQueryFilters.
/// </summary>
public static class AegisScoreAggregator
{
    /// <summary>
    /// Consolida os agregados do tenant ambiente de <paramref name="db"/> no <see cref="AegisScoreResult"/>
    /// oficial. O <c>cast p/ int?</c> cobre o SUM de zero linhas (NULL no SQL) sem estourar em conjunto vazio.
    /// </summary>
    public static async Task<AegisScoreResult> AggregateAsync(AegisScoreDbContext db, CancellationToken ct = default)
    {
        // Numerador + denominador AVALIADO: SÓ estados cuja subcategoria pertence ao framework ATIVO.
        var evaluated = from t in db.TenantControlStates
                        join s in db.Subcategories on t.SubcategoryId equals s.Id
                        join c in db.Categories on s.CategoryId equals c.Id
                        join f in db.Functions on c.FunctionId equals f.Id
                        join fv in db.FrameworkVersions on f.FrameworkVersionId equals fv.Id
                        where fv.IsActive
                        select new { t.CurrentScore, s.MaxScorePoints };

        var achieved = await evaluated.SumAsync(x => (int?)x.CurrentScore, ct) ?? 0;
        var evaluatedMax = await evaluated.SumAsync(x => (int?)x.MaxScorePoints, ct) ?? 0;
        var evaluatedControls = await evaluated.CountAsync(ct);

        // Denominador de COBERTURA: peso e contagem de TODAS as subcategorias do framework ATIVO (o "universo
        // semântico" da fórmula). Controles não avaliados entram AQUI (cobertura), nunca no denominador do score.
        var eligible = from s in db.Subcategories
                       join c in db.Categories on s.CategoryId equals c.Id
                       join f in db.Functions on c.FunctionId equals f.Id
                       join fv in db.FrameworkVersions on f.FrameworkVersionId equals fv.Id
                       where fv.IsActive
                       select s.MaxScorePoints;
        var eligibleMax = await eligible.SumAsync(x => (int?)x, ct) ?? 0;
        var eligibleControls = await eligible.CountAsync(ct);

        return AegisScoreFormulaV1.Compute(achieved, evaluatedMax, evaluatedControls, eligibleMax, eligibleControls);
    }
}
