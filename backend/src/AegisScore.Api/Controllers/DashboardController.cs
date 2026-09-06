using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AegisScore.Api.Contracts;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Queries;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// The "modelo forte" view: turns SOC telemetry into business risk exposure — maturity by NIST
/// function, gaps, risk heat-map and the ICR — modeled on the reference dashboards.
/// </summary>
[ApiController]
[Route("api/v1/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly MaturityScoringService _maturity;
    private readonly IcrScoringService _icr;
    private readonly IDashboardOverviewQuery _overview;

    public DashboardController(
        AegisScoreDbContext db,
        ITenantContext tenant,
        MaturityScoringService maturity,
        IcrScoringService icr,
        IDashboardOverviewQuery overview)
    {
        _db = db;
        _tenant = tenant;
        _maturity = maturity;
        _icr = icr;
        _overview = overview;
    }

    /// <summary>
    /// [AEGIS-MVP-PRODUCT-01] Tela inicial (Visão geral) — read model COMPOSTO por DIMENSÃO INDEPENDENTE:
    /// o que já foi observado no ambiente, quanto foi efetivamente avaliado, o que merece atenção agora, a
    /// postura consultiva de identidade e a saúde/recência das fontes. Cada dimensão carrega o PRÓPRIO estado
    /// (sem fonte / nunca coletado / parcial / disponível) e a própria origem.
    ///
    /// Substitui o uso de <see cref="Executive"/> como fonte única da tela inicial: aquele fluxo mede
    /// maturidade CMMI e registro de riscos — dimensões legítimas, porém DISTINTAS da postura por controle.
    /// A ausência de maturidade/ICR NÃO pode esconder ativos, exposições, vulnerabilidades ou identidade já
    /// coletados, e nenhuma dimensão é combinada com outra num score novo.
    ///
    /// Somente leitura: não aciona coleta externa, não aciona IA e não escreve estado. Tenant IMPLÍCITO.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(DashboardOverviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardOverviewDto>> Overview(CancellationToken ct)
        => Ok(await _overview.GetAsync(ct));

    [HttpGet("executive")]
    public async Task<ActionResult<ExecutiveDashboardDto>> Executive(CancellationToken ct)
    {
        // Tenant implícito: o nome do cliente vem da entidade raiz Tenant (não filtrada por design),
        // resolvida pelo ITenantContext ambiente — a mesma fonte que alimenta os Global Query Filters abaixo.
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == _tenant.TenantId, ct);
        var clientName = tenant?.Name ?? "—";

        // ---- Maturity (all evaluations across the tenant's assessments) ----
        var scoreRows = await (from s in _db.Scopes
                               join e in _db.Evaluations on s.Id equals e.AssessmentScopeId
                               join sub in _db.Subcategories on e.SubcategoryId equals sub.Id
                               select new { sub.Code, e.CurrentScore, e.TargetScore })
                              .ToListAsync(ct);

        var rollup = _maturity.Aggregate(scoreRows.Select(x => new SubcategoryScore(x.Code, x.CurrentScore, x.TargetScore)));

        var functions = await _db.Functions.AsNoTracking().OrderBy(f => f.Order)
            .Select(f => new { f.Code, f.Name }).ToListAsync(ct);

        var radar = functions.Select(f =>
        {
            var agg = rollup.Functions.FirstOrDefault(x => x.RefCode == f.Code);
            return new RadarPointDto(f.Code, f.Name, agg?.CurrentScore ?? 0, agg?.TargetScore ?? 0);
        }).ToList();

        var topGaps = rollup.Categories
            .OrderByDescending(c => c.Gap)
            .Take(8)
            .Select(c => new GapPointDto(c.RefCode, c.RefCode, c.CurrentScore, c.TargetScore, c.Gap))
            .ToList();

        // ---- Risk (latest evaluation per risk) ----
        // Carrega a avaliação + o processo do risco para deduplicar exposição por processo.
        var riskEvals = await (from r in _db.Risks
                               join ev in _db.RiskEvaluations on r.Id equals ev.RiskId
                               select new { Ev = ev, r.BusinessProcessId })
                              .AsNoTracking().ToListAsync(ct);

        var latest = riskEvals
            .GroupBy(x => x.Ev.RiskId)
            .Select(g => g.OrderByDescending(x => x.Ev.EvaluatedAt).First())
            .ToList();

        var heatmap = latest
            .GroupBy(x => new { x.Ev.Probability, x.Ev.Impact })
            .Select(g => new HeatCellDto(g.Key.Probability, g.Key.Impact, g.Count()))
            .ToList();

        var byLevel = latest
            .GroupBy(x => x.Ev.RiskLevel)
            .Select(g => new RiskLevelCountDto(g.Key.ToString(), g.Count()))
            .ToList();

        // ---- Exposure cards ----
        var actionPlans = await (from r in _db.Risks
                                 join ap in _db.ActionPlans on r.Id equals ap.RiskId
                                 select ap).AsNoTracking().ToListAsync(ct);

        var overdueCount = actionPlans.Count(ap => ap.IsOverdue);
        // Processos DISTINTOS com ao menos um risco Alto/Crítico (não a contagem de riscos).
        var criticalExposed = latest
            .Where(x => x.Ev.RiskLevel is RiskLevel.Alto or RiskLevel.Critico)
            .Select(x => x.BusinessProcessId)
            .Where(id => id != null)
            .Distinct()
            .Count();

        var exposure = new ExposureCardsDto(
            criticalExposed,
            overdueCount,
            rollup.Overall.CurrentScore,
            rollup.Overall.TargetScore);

        // ---- ICR: média EXCLUSIVA dos ICRs realmente persistidos (nunca fabricado) ----
        // Sem nenhum IcrScore medido para o tenant, o ICR é NULO (não avaliado). O antigo fallback
        // sintetizava um proxy de constantes (TechnicalSeverity=0.5, AssetCriticality=0.5,
        // RecentExploitation=0.3, RegulatoryExposure=0.4…) que, com os pesos default e sem maturidade,
        // caía exatamente em "45 · Moderado" — um número apresentado como postura apurada sem uma única
        // medição por trás. Ausência de medição não é zero nem banda: é ausência de leitura, e o contrato
        // diz isso com null (o cliente e o instante de apuração continuam presentes no cabeçalho).
        var storedScores = await _db.IcrScores.AsNoTracking().Select(s => s.Score).ToListAsync(ct);
        IcrDto? icr = null;
        if (storedScores.Count > 0)
        {
            var icrScore = Math.Round(storedScores.Average(), 1);
            icr = new IcrDto(icrScore, _icr.BandOf(icrScore).ToString());
        }

        return new ExecutiveDashboardDto(clientName, DateTimeOffset.UtcNow, exposure, radar, topGaps, heatmap, byLevel, icr);
    }

    /// <summary>
    /// O PIOR raio de explosão já calculado para o tenant — "se cair o ativo X, quantos outros caem
    /// junto?". Traduz risco técnico em impacto de negócio para a vitrine executiva.
    ///
    /// Endpoint SEPARADO do /executive de propósito (ver <see cref="BlastRadiusSummaryDto"/>): o painel
    /// é secundário e não pode entrar no caminho crítico do First Contentful Paint.
    ///
    /// Barato por construção: o <c>BlastRadiusAssessment</c> já MATERIALIZA as contagens no momento do
    /// traversal (<c>ImpactedAssetCount</c>, <c>ImpactedProcessCount</c>, <c>MaxDepth</c>), então aqui
    /// não há grafo a percorrer — é um ORDER BY + LIMIT 1 com um JOIN para o nome do ativo.
    ///
    /// Escolhe o de MAIOR score, não o mais recente: a diretoria pergunta "qual é o nosso pior cenário?",
    /// não "qual foi o último que rodamos". Tenant implícito (Global Query Filter fail-closed).
    /// </summary>
    [HttpGet("blast-radius-summary")]
    [ProducesResponseType(typeof(BlastRadiusSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<BlastRadiusSummaryDto>> BlastRadiusSummary(CancellationToken ct)
    {
        var worst = await _db.BlastRadiusAssessments.AsNoTracking()
            .OrderByDescending(a => a.BlastRadiusScore)
            .ThenByDescending(a => a.CreatedAt)
            .Select(a => new BlastRadiusSummaryDto(
                a.RootAsset!.Name,
                a.BlastRadiusScore,
                a.RiskLevel.ToString(),
                a.ImpactedAssetCount,
                a.ImpactedProcessCount,
                a.MaxDepth,
                a.CreatedAt))
            .FirstOrDefaultAsync(ct);

        // 204 e não um DTO zerado: "nunca calculamos um raio" é diferente de "o raio é zero", e o
        // frontend precisa dessa distinção para escolher entre estado vazio e número.
        return worst is null ? NoContent() : worst;
    }
}
