#if DEBUG
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;

namespace AegisScore.Api.Controllers;

/// <summary>
/// Development-only utilities. Populates a demo tenant so the executive dashboard can be
/// exercised end to end (maturity, gaps, risk heat-map, ICR) without hand-crafting data.
/// Disabled outside the Development environment.
/// </summary>
[ApiController]
[Route("api/v1/dev")]
public class DevController : ControllerBase
{
    /// <summary>Fixed id so the frontend can hard-code it in environment.ts.</summary>
    public static readonly Guid DemoTenantId = Guid.Parse("aa000000-0000-0000-0000-000000000001");

    private readonly DbContextOptions<AegisScoreDbContext> _dbOptions;
    private readonly RiskScoringService _risk;
    private readonly IWebHostEnvironment _env;

    public DevController(DbContextOptions<AegisScoreDbContext> dbOptions, RiskScoringService risk, IWebHostEnvironment env)
    {
        _dbOptions = dbOptions;
        _risk = risk;
        _env = env;
    }

    /// <summary>
    /// (Re)creates a realistic demo tenant. Idempotent: wipes any prior demo data first.
    /// Runs under a system tenant context (DemoTenantId) so the fail-closed write-stamping
    /// interceptor is satisfied without any request header. Cross-tenant reads/deletes still
    /// use .IgnoreQueryFilters() explicitly where they must span the ambient filter.
    /// </summary>
    [HttpPost("seed-demo")]
    public async Task<IActionResult> SeedDemo(CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        // Dedicated context bound to a system tenant — lives only for this request.
        // Never trusts (or needs) the X-Tenant header; the ambient tenant is DemoTenantId.
        await using var db = new AegisScoreDbContext(_dbOptions, new SystemTenantContext(DemoTenantId));

        var fw = await db.FrameworkVersions.FirstOrDefaultAsync(f => f.IsActive, ct);
        if (fw is null)
            return Problem("Catálogo NIST ainda não semeado. Reinicie a API e tente de novo.");

        var subs = await db.Subcategories.AsNoTracking()
            .Select(s => new { s.Id, s.Code }).ToListAsync(ct);

        await WipeExistingDemoAsync(db, ct);

        // ---- Tenant, business units, processes ----
        var tenant = new Tenant { Id = DemoTenantId, Name = "Grupo Aegis (Demo)", Slug = "demo", Status = TenantStatus.Active };

        var buSec = new BusinessUnit { TenantId = DemoTenantId, Name = "Segurança da Informação", Code = "SEC", ManagerName = "Ana Ribeiro" };
        var buTi = new BusinessUnit { TenantId = DemoTenantId, Name = "Tecnologia", Code = "TI", ManagerName = "Carlos Menezes" };

        var procs = new[]
        {
            new BusinessProcess { TenantId = DemoTenantId, Name = "Gestão de Identidade e Acesso", ProcessCategory = "Operações de Segurança", Classification = ProcessClassification.Restrito, ProcessValue = 4 },
            new BusinessProcess { TenantId = DemoTenantId, Name = "Gestão de Vulnerabilidades", ProcessCategory = "Operações de Segurança", Classification = ProcessClassification.Confidencial, ProcessValue = 3 },
            new BusinessProcess { TenantId = DemoTenantId, Name = "Continuidade de Negócios", ProcessCategory = "Resiliência", Classification = ProcessClassification.Confidencial, ProcessValue = 3 },
            new BusinessProcess { TenantId = DemoTenantId, Name = "Conscientização e Treinamento", ProcessCategory = "Governança", Classification = ProcessClassification.Interno, ProcessValue = 2 },
        };

        // ---- Assessment + scopes ----
        var assessment = new Assessment
        {
            TenantId = DemoTenantId,
            FrameworkVersionId = fw.Id,
            Name = "Diagnóstico NIST CSF 2.0 — 2026",
            Status = AssessmentStatus.InProgress,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
        };

        var primaryScope = new AssessmentScope { TenantId = DemoTenantId, AssessmentId = assessment.Id, BusinessProcessId = procs[0].Id, BusinessUnitId = buSec.Id, Status = ScopeStatus.Evaluation };
        var scope2 = new AssessmentScope { TenantId = DemoTenantId, AssessmentId = assessment.Id, BusinessProcessId = procs[1].Id, BusinessUnitId = buTi.Id, Status = ScopeStatus.Questionnaire };

        // ---- Subcategory evaluations (all subcategories under the primary scope) ----
        // Current maturity varies per function so the radar is not flat; target trends to 4–5.
        var funcBase = new Dictionary<string, int> { ["GV"] = 3, ["ID"] = 3, ["PR"] = 2, ["DE"] = 2, ["RS"] = 2, ["RC"] = 3 };
        var evals = new List<SubcategoryEvaluation>();
        foreach (var s in subs)
        {
            var fn = s.Code.Split('.')[0];
            var baseLevel = funcBase.TryGetValue(fn, out var b) ? b : 2;
            var variation = Math.Abs(s.Code.Sum(c => (int)c)) % 3 - 1; // -1, 0, +1 (deterministic)
            var current = Math.Clamp(baseLevel + variation, 1, 5);
            var target = Math.Clamp(Math.Max(current + 1, 4), current, 5);

            evals.Add(new SubcategoryEvaluation
            {
                AssessmentScopeId = primaryScope.Id,
                SubcategoryId = s.Id,
                CurrentLevel = current,
                CurrentScore = current,
                TargetLevel = target,
                TargetScore = target,
                EvaluatedBy = EvaluatedBy.Analyst,
                Confidence = 0.8,
                Rationale = "Seed de demonstração",
            });
        }

        // ---- Risks, evaluations and action plans ----
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var risks = new List<Risk>();
        var riskEvals = new List<RiskEvaluation>();
        var plans = new List<ActionPlan>();

        void AddRisk(string code, string title, int p, int i, int pv, Guid procId, Guid buId, ActionPlanStatus? planStatus, int? dueDays)
        {
            var r = new Risk
            {
                TenantId = DemoTenantId,
                Code = code,
                Title = title,
                BusinessProcessId = procId,
                BusinessUnitId = buId,
                Classification = ProcessClassification.Confidencial,
            };
            var (score, level) = _risk.Evaluate(p, i, pv);
            riskEvals.Add(new RiskEvaluation
            {
                // TenantId carimbado automaticamente (ambiente = DemoTenantId).
                RiskId = r.Id,
                Phase = RiskPhase.Inherent,
                Probability = p,
                Impact = i,
                ProcessValue = pv,
                RiskScore = score,
                RiskLevel = level,
            });
            if (planStatus is { } ps)
            {
                plans.Add(new ActionPlan
                {
                    // TenantId carimbado automaticamente (ambiente = DemoTenantId).
                    RiskId = r.Id,
                    Treatment = RiskTreatmentType.Mitigar,
                    Description = "Plano de tratamento (demo)",
                    ResponsibleArea = "Segurança da Informação",
                    Status = ps,
                    DueDate = dueDays is { } d ? today.AddDays(d) : null,
                    CompletedAt = ps == ActionPlanStatus.Concluido ? DateTimeOffset.UtcNow : null,
                });
            }
            risks.Add(r);
        }

        AddRisk("SEC0001", "Falta de MFA em contas privilegiadas", 4, 4, 3, procs[0].Id, buSec.Id, ActionPlanStatus.EmAndamento, -20);
        AddRisk("SEC0002", "Backups sem teste de restauração", 3, 4, 3, procs[2].Id, buTi.Id, ActionPlanStatus.EmAndamento, 30);
        AddRisk("SEC0003", "Ausência de inventário de ativos", 3, 3, 2, procs[1].Id, buTi.Id, ActionPlanStatus.Aberto, -5);
        AddRisk("SEC0004", "Logs de segurança não centralizados", 3, 3, 3, procs[1].Id, buSec.Id, ActionPlanStatus.Aberto, 60);
        AddRisk("SEC0005", "Política de senhas fraca", 2, 2, 2, procs[0].Id, buSec.Id, ActionPlanStatus.Concluido, -10);
        AddRisk("SEC0006", "Treinamento de conscientização irregular", 1, 2, 1, procs[3].Id, buSec.Id, null, null);

        db.Tenants.Add(tenant);
        db.BusinessUnits.AddRange(buSec, buTi);
        db.Processes.AddRange(procs);
        db.Assessments.Add(assessment);
        db.Scopes.AddRange(primaryScope, scope2);
        db.Evaluations.AddRange(evals);
        db.Risks.AddRange(risks);
        db.RiskEvaluations.AddRange(riskEvals);
        db.ActionPlans.AddRange(plans);
        await db.SaveChangesAsync(ct);

        var overdue = plans.Count(p => p.Status != ActionPlanStatus.Concluido && p.DueDate is { } d && d < today);

        return Ok(new
        {
            tenantId = DemoTenantId,
            message = "Seed de demonstração criado. Use este tenantId no frontend (environment.ts) — já vem pré-configurado.",
            businessUnits = 2,
            processes = procs.Length,
            subcategoriesEvaluated = evals.Count,
            risks = risks.Count,
            overdueActionPlans = overdue,
        });
    }

    /// <summary>Removes any prior demo-tenant data so the seed can be re-run safely.</summary>
    private async Task WipeExistingDemoAsync(AegisScoreDbContext db, CancellationToken ct)
    {
        var existing = await db.Tenants.FirstOrDefaultAsync(t => t.Id == DemoTenantId, ct);
        if (existing is null)
            return;

        var scopeIds = await db.Scopes.IgnoreQueryFilters()
            .Where(s => s.TenantId == DemoTenantId).Select(s => s.Id).ToListAsync(ct);
        db.Evaluations.RemoveRange(await db.Evaluations.Where(e => scopeIds.Contains(e.AssessmentScopeId)).ToListAsync(ct));

        var riskIds = await db.Risks.IgnoreQueryFilters()
            .Where(r => r.TenantId == DemoTenantId).Select(r => r.Id).ToListAsync(ct);
        db.ActionPlans.RemoveRange(await db.ActionPlans.IgnoreQueryFilters().Where(a => riskIds.Contains(a.RiskId)).ToListAsync(ct));
        db.RiskEvaluations.RemoveRange(await db.RiskEvaluations.IgnoreQueryFilters().Where(e => riskIds.Contains(e.RiskId)).ToListAsync(ct));

        // EvidenceSignal / Evidence têm o mesmo DemoTenantId fixo mas nenhum FK/cascade — se não
        // forem removidos aqui, sinais de uma sessão anterior "reaparecem" no tenant re-semeado.
        db.Signals.RemoveRange(await db.Signals.IgnoreQueryFilters().Where(s => s.TenantId == DemoTenantId).ToListAsync(ct));
        db.Evidence.RemoveRange(await db.Evidence.IgnoreQueryFilters().Where(ev => ev.TenantId == DemoTenantId).ToListAsync(ct));

        db.Risks.RemoveRange(await db.Risks.IgnoreQueryFilters().Where(r => r.TenantId == DemoTenantId).ToListAsync(ct));
        db.Scopes.RemoveRange(await db.Scopes.IgnoreQueryFilters().Where(s => s.TenantId == DemoTenantId).ToListAsync(ct));
        db.Assessments.RemoveRange(await db.Assessments.IgnoreQueryFilters().Where(a => a.TenantId == DemoTenantId).ToListAsync(ct));
        db.Processes.RemoveRange(await db.Processes.IgnoreQueryFilters().Where(p => p.TenantId == DemoTenantId).ToListAsync(ct));
        db.BusinessUnits.RemoveRange(await db.BusinessUnits.IgnoreQueryFilters().Where(bu => bu.TenantId == DemoTenantId).ToListAsync(ct));
        db.Tenants.Remove(existing);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Privileged, non-HTTP tenant context used to run the demo seeder under an explicit tenant.
    /// The write-stamping interceptor is fail-closed, so seed writes need a resolved tenant that
    /// is NOT derived from a request header.
    /// </summary>
    private sealed class SystemTenantContext : ITenantContext
    {
        public SystemTenantContext(Guid tenantId) => TenantId = tenantId;
        public Guid? TenantId { get; }
    }
}
#endif
