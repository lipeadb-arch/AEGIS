#if DEBUG
using Microsoft.AspNetCore.Authorization;
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
[AllowAnonymous]   // utilitários de bootstrap (DEBUG): criam tenant/usuário demo antes de haver credencial
public class DevController : ControllerBase
{
    /// <summary>Fixed id so the frontend can hard-code it in environment.ts.</summary>
    public static readonly Guid DemoTenantId = Guid.Parse("aa000000-0000-0000-0000-000000000001");

    /// <summary>Id FIXO do ativo-raiz do raio de explosão (o AD DC) — o frontend o referencia em environment.ts.</summary>
    public static readonly Guid DemoRootAssetId = Guid.Parse("bb000000-0000-0000-0000-000000000001");

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

        // ---- Govern (GV): Document Hub — documentos lidos pela IA + ledger de cobertura ----
        var polIa = new GovernanceDocument
        {
            TenantId = DemoTenantId,
            Title = "Política de Uso Aceitável de IA",
            Type = GovernanceDocumentType.Politica,
            Source = DocumentSource.UploadManual,
            FileName = "politica-uso-ia.pdf",
            ContentType = "application/pdf",
            Sha256 = "seed-demo-politica-ia",
            Status = GovernanceStatus.Vigente,
            AnalysisStatus = AiAnalysisStatus.Analyzed,
            AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-3),
            AnalysisSummary = "Define uso aceitável de IA generativa e os dados que não podem ser enviados a terceiros.",
            ModelUsed = "claude-opus-4-8",
        };
        polIa.ControlMappings.Add(new DocumentControlMapping { TenantId = DemoTenantId, SubcategoryCode = "GV.PO-01", Confidence = 0.92, Evidence = "Estabelece e revisa anualmente a política de uso de IA." });
        polIa.ControlMappings.Add(new DocumentControlMapping { TenantId = DemoTenantId, SubcategoryCode = "GV.OC-01", Confidence = 0.78, Evidence = "Contextualiza missão e restrições de tratamento de dados." });

        var diretriz = new GovernanceDocument
        {
            TenantId = DemoTenantId,
            Title = "Diretriz de Classificação de Dados",
            Type = GovernanceDocumentType.Diretriz,
            Source = DocumentSource.Integracao,
            SourceReference = "https://contoso.sharepoint.com/sites/grc/Classificacao",
            FileName = "classificacao-dados.md",
            ContentType = "text/markdown",
            Sha256 = "seed-demo-classificacao",
            Status = GovernanceStatus.Vigente,
            AnalysisStatus = AiAnalysisStatus.Pending,   // ainda na fila de leitura da IA
        };

        // Ledger híbrido: cobertos por documento + gaps que o Auditor Virtual vai investigar.
        var coverage = new[]
        {
            new SubcategoryCoverage { TenantId = DemoTenantId, SubcategoryCode = "GV.PO-01", Status = CoverageStatus.Coberto, EvidenceSource = CoverageEvidenceSource.Document, OriginDocumentId = polIa.Id, Confidence = 0.92, LastEvaluatedAt = DateTimeOffset.UtcNow.AddDays(-3) },
            new SubcategoryCoverage { TenantId = DemoTenantId, SubcategoryCode = "GV.OC-01", Status = CoverageStatus.Parcial, EvidenceSource = CoverageEvidenceSource.Document, OriginDocumentId = polIa.Id, Confidence = 0.78, LastEvaluatedAt = DateTimeOffset.UtcNow.AddDays(-3) },
            new SubcategoryCoverage { TenantId = DemoTenantId, SubcategoryCode = "GV.RM-01", Status = CoverageStatus.NaoCoberto, EvidenceSource = CoverageEvidenceSource.None },
            new SubcategoryCoverage { TenantId = DemoTenantId, SubcategoryCode = "GV.SC-04", Status = CoverageStatus.NaoCoberto, EvidenceSource = CoverageEvidenceSource.None },
        };

        // ---- Identify (ID.AM): inventário contínuo de ativos ----
        // Score/nível de risco simulam o motor de IA já tendo avaliado parte do inventário;
        // dois ativos ficam sem score para exercitar o estado "não avaliado" na grid.
        var assets = new[]
        {
            new Asset { Id = DemoRootAssetId, TenantId = DemoTenantId, Name = "AD Domain Controller 01", Category = AssetCategory.Hardware, SubType = "server", Criticality = 4, OwnerName = "Carlos Menezes", ExternalRef = "CMDB-1001", BusinessProcessId = procs[0].Id, DiscoverySource = AssetDiscoverySource.Connector, LastSeenAt = DateTimeOffset.UtcNow.AddHours(-2), RiskScore = 82, RiskLevel = RiskLevel.Critico, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new Asset { TenantId = DemoTenantId, Name = "Microsoft 365 (Identidade)", Category = AssetCategory.Software, SubType = "saas", Criticality = 4, OwnerName = "Ana Ribeiro", ExternalRef = "SAAS-M365", BusinessProcessId = procs[0].Id, DiscoverySource = AssetDiscoverySource.Connector, LastSeenAt = DateTimeOffset.UtcNow.AddHours(-1), RiskScore = 67, RiskLevel = RiskLevel.Alto, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new Asset { TenantId = DemoTenantId, Name = "Base de Clientes (PII)", Category = AssetCategory.Data, SubType = "database", Criticality = 4, OwnerName = "Ana Ribeiro", DiscoverySource = AssetDiscoverySource.Manual, RiskScore = 74, RiskLevel = RiskLevel.Alto, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-2) },
            new Asset { TenantId = DemoTenantId, Name = "Equipe de SOC", Category = AssetCategory.People, SubType = "team", Criticality = 3, OwnerName = "Ana Ribeiro", DiscoverySource = AssetDiscoverySource.Manual, RiskScore = 40, RiskLevel = RiskLevel.Medio, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-4) },
            new Asset { TenantId = DemoTenantId, Name = "Datacenter São Paulo", Category = AssetCategory.Facilities, SubType = "datacenter", Criticality = 3, OwnerName = "Carlos Menezes", ExternalRef = "FAC-SP01", DiscoverySource = AssetDiscoverySource.Manual, RiskScore = 28, RiskLevel = RiskLevel.Baixo, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-6) },
            new Asset { TenantId = DemoTenantId, Name = "Provedor de LLM (Anthropic)", Category = AssetCategory.SupplyChain, SubType = "api", Criticality = 3, OwnerName = "Carlos Menezes", ExternalRef = "SC-LLM01", DiscoverySource = AssetDiscoverySource.Import, RiskScore = 55, RiskLevel = RiskLevel.Medio, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new Asset { TenantId = DemoTenantId, Name = "Gateway VPN", Category = AssetCategory.Hardware, SubType = "appliance", Criticality = 3, OwnerName = "Carlos Menezes", ExternalRef = "CMDB-1042", BusinessProcessId = procs[1].Id, DiscoverySource = AssetDiscoverySource.Connector, LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-30), RiskScore = 61, RiskLevel = RiskLevel.Alto, RiskScoredAt = DateTimeOffset.UtcNow.AddDays(-1) },
            new Asset { TenantId = DemoTenantId, Name = "Notebook Diretoria", Category = AssetCategory.Hardware, SubType = "endpoint", Criticality = 2, OwnerName = "Diretoria", DiscoverySource = AssetDiscoverySource.Connector, LastSeenAt = DateTimeOffset.UtcNow.AddDays(-3) },
            new Asset { TenantId = DemoTenantId, Name = "Portal do Cliente", Category = AssetCategory.Software, SubType = "webapp", Criticality = 3, OwnerName = "Carlos Menezes", ExternalRef = "APP-PORTAL", BusinessProcessId = procs[1].Id, DiscoverySource = AssetDiscoverySource.Manual },
        };

        // ---- Identify (ID.RA): topologia de dependências + ameaça (Raio de Explosão) ----
        // Estrela em torno do AD DC (assets[0]): a identidade/autenticação de meio ambiente depende dele.
        // Raio AMPLO (6 colaterais, alguns a 2 saltos) e CRÍTICO — dispara o hook que penaliza ID.RA-01/05.
        var ransomware = new Threat
        {
            TenantId = DemoTenantId,   // Threat NÃO é ITenantOwned — TenantId setado à mão (limpo pelo wipe do tenant)
            Code = "T1486",
            Source = ThreatSource.MitreAttck,
            Title = "Data Encrypted for Impact (Ransomware)",
            Description = "Adversário cifra dados em escala para interromper a operação e extorquir resgate.",
            BaseSeverity = 9.0,
            Tactic = "Impact",
            KnownExploited = true,
            IsActive = true,
        };

        AssetDependency Depends(Asset source, Asset target, DependencyType type, DependencyStrength strength) => new()
        {
            TenantId = DemoTenantId, SourceAssetId = source.Id, TargetAssetId = target.Id,
            Type = type, Strength = strength, DiscoverySource = AssetDiscoverySource.Connector, IsActive = true,
        };

        // "Source DEPENDE DE Target": o raio do AD é quem depende dele, direta ou transitivamente.
        var dependencies = new[]
        {
            Depends(assets[1], assets[0], DependencyType.AuthenticatesVia, DependencyStrength.Hard),  // M365 → AD
            Depends(assets[8], assets[0], DependencyType.AuthenticatesVia, DependencyStrength.Hard),  // Portal do Cliente → AD
            Depends(assets[2], assets[0], DependencyType.StoresDataIn,     DependencyStrength.Hard),  // Base de Clientes (PII) → AD
            Depends(assets[6], assets[0], DependencyType.AuthenticatesVia, DependencyStrength.Hard),  // Gateway VPN → AD
            Depends(assets[7], assets[6], DependencyType.ConnectsTo,       DependencyStrength.Soft),  // Notebook Diretoria → VPN (2 saltos)
            Depends(assets[3], assets[1], DependencyType.ConsumesService,  DependencyStrength.Soft),  // Equipe de SOC → M365 (2 saltos)
        };

        var ransomwareExposure = new AssetThreatExposure
        {
            TenantId = DemoTenantId,
            AssetId = DemoRootAssetId,          // o ransomware mira o AD DC (epicentro)
            ThreatId = ransomware.Id,
            Likelihood = 4,                     // crítico
            Status = ExposureStatus.Active,
            MitigatingSubcategoryCode = "PR.PS-01",
            DiscoverySource = AssetDiscoverySource.Connector,
            DetectedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        db.Tenants.Add(tenant);
        db.BusinessUnits.AddRange(buSec, buTi);
        db.Processes.AddRange(procs);
        db.Assets.AddRange(assets);
        db.Assessments.Add(assessment);
        db.Scopes.AddRange(primaryScope, scope2);
        db.Evaluations.AddRange(evals);
        db.Risks.AddRange(risks);
        db.RiskEvaluations.AddRange(riskEvals);
        db.ActionPlans.AddRange(plans);
        db.GovernanceDocuments.AddRange(polIa, diretriz);
        db.SubcategoryCoverages.AddRange(coverage);
        db.Threats.Add(ransomware);
        db.AssetDependencies.AddRange(dependencies);
        db.AssetThreatExposures.Add(ransomwareExposure);
        await db.SaveChangesAsync(ct);

        var overdue = plans.Count(p => p.Status != ActionPlanStatus.Concluido && p.DueDate is { } d && d < today);

        return Ok(new
        {
            tenantId = DemoTenantId,
            message = "Seed de demonstração criado. Use este tenantId no frontend (environment.ts) — já vem pré-configurado.",
            businessUnits = 2,
            processes = procs.Length,
            assets = assets.Length,
            assetDependencies = dependencies.Length,
            threatExposures = 1,
            blastRadiusRootAssetId = DemoRootAssetId,
            subcategoriesEvaluated = evals.Count,
            risks = risks.Count,
            overdueActionPlans = overdue,
            governanceDocuments = 2,
            coverageEntries = coverage.Length,
        });
    }

    /// <summary>
    /// (Re)cria um usuário demo no tenant de demonstração para exercitar o login/refresh da Etapa 2.
    /// Idempotente. Roda sob o SystemTenantContext (DemoTenantId), então o stamping fail-closed é
    /// satisfeito sem depender do header X-Tenant.
    /// </summary>
    [HttpPost("seed-user")]
    public async Task<IActionResult> SeedUser([FromServices] IPasswordHasher hasher, CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        const string email = "analista@demo.aegis";
        const string password = "Aegis@12345";

        await using var db = new AegisScoreDbContext(_dbOptions, new SystemTenantContext(DemoTenantId));

        var existing = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.TenantId == DemoTenantId && u.Email == email, ct);
        if (existing is not null)
            return Ok(new { message = "Usuário demo já existe.", email, tenantId = DemoTenantId });

        db.Users.Add(new User
        {
            TenantId = DemoTenantId,
            Email = email,
            DisplayName = "Analista Demo",
            PasswordHash = hasher.Hash(password),
            Role = UserRole.TenantAdmin,
            IsActive = true,
        });
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            message = "Usuário demo criado. Faça login com o header X-Tenant apontando para este tenantId.",
            email,
            password,
            tenantId = DemoTenantId,
        });
    }

    /// <summary>
    /// Prova de vida da ponte Govern → Aegis Score (Etapa 2). Reenfileira um documento JÁ ingerido para
    /// o <c>DocumentAnalysisWorker</c>, que o reprocessa pelo fluxo REAL de produção — extrai o texto,
    /// mapeia os controles NIST e projeta cada claim no ledger (<c>TenantControlState</c>) através do
    /// <c>IControlStateWriter</c>, com o teto documental de 50% (<c>MitigatedByThirdParty</c>). É o que
    /// tira o HUD do 0.0% sem exigir credencial: roda [AllowAnonymous] em DEBUG.
    ///
    /// O binário precisa já estar no storage (upload prévio). O tenant NÃO importa aqui: localizamos o
    /// documento SEM o query filter e apenas o enfileiramos — o worker o reprocessa sob o
    /// SystemTenantContext do próprio dono, exatamente como no fluxo autenticado de produção.
    /// </summary>
    [HttpPost("reprocess-document")]
    public async Task<IActionResult> ReprocessDocument(
        [FromServices] IDocumentAnalysisQueue queue,
        [FromQuery] string fileName = "Politica_Seguranca_Aegis_Tech.pdf",
        CancellationToken ct = default)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        await using var db = new AegisScoreDbContext(_dbOptions, new SystemTenantContext(DemoTenantId));

        // O documento pode ter sido ingerido sob QUALQUER tenant; localizamos sem o query filter e
        // pegamos o mais recente com esse nome de arquivo.
        var doc = await db.GovernanceDocuments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.FileName == fileName)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.TenantId, d.StorageUri, d.FileName, d.AnalysisStatus })
            .FirstOrDefaultAsync(ct);

        if (doc is null)
        {
            // Auto-descoberta: sem o alvo, devolvemos o que existe para o operador escolher o ?fileName.
            var candidates = await db.GovernanceDocuments.IgnoreQueryFilters().AsNoTracking()
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new { d.Id, d.FileName, d.Title, status = d.AnalysisStatus.ToString(), hasBinary = d.StorageUri != null })
                .Take(20).ToListAsync(ct);

            return NotFound(new
            {
                message = $"Nenhum documento com FileName '{fileName}'. Faça upload via POST /api/v1/governance/documents " +
                          "ou repita com ?fileName=<nome> a partir da lista abaixo.",
                requestedFileName = fileName,
                availableDocuments = candidates,
            });
        }

        if (doc.StorageUri is null)
            return BadRequest(new
            {
                message = "Documento encontrado, mas sem binário no storage (StorageUri nulo) — nada a ler. Reenvie o arquivo por upload.",
                documentId = doc.Id,
                fileName = doc.FileName,
            });

        await queue.EnqueueAsync(doc.Id, ct);

        return Accepted(new
        {
            message = "Documento reenfileirado. O worker vai reprocessá-lo pela ponte IControlStateWriter e popular " +
                      "TenantControlStates com o teto documental (MitigatedByThirdParty = 50%). " +
                      "Aguarde ~2s e consulte GET /api/v1/scoring/dashboard.",
            documentId = doc.Id,
            tenantId = doc.TenantId,
            fileName = doc.FileName,
            previousStatus = doc.AnalysisStatus.ToString(),
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

        db.DocumentControlMappings.RemoveRange(await db.DocumentControlMappings.IgnoreQueryFilters().Where(m => m.TenantId == DemoTenantId).ToListAsync(ct));
        db.GovernanceDocuments.RemoveRange(await db.GovernanceDocuments.IgnoreQueryFilters().Where(d => d.TenantId == DemoTenantId).ToListAsync(ct));
        db.SubcategoryCoverages.RemoveRange(await db.SubcategoryCoverages.IgnoreQueryFilters().Where(c => c.TenantId == DemoTenantId).ToListAsync(ct));
        db.GrcInterviewMessages.RemoveRange(await db.GrcInterviewMessages.IgnoreQueryFilters().Where(m => m.TenantId == DemoTenantId).ToListAsync(ct));
        db.GrcInterviewSessions.RemoveRange(await db.GrcInterviewSessions.IgnoreQueryFilters().Where(s => s.TenantId == DemoTenantId).ToListAsync(ct));
        db.IdentifiedRisks.RemoveRange(await db.IdentifiedRisks.IgnoreQueryFilters().Where(r => r.TenantId == DemoTenantId).ToListAsync(ct));

        db.Risks.RemoveRange(await db.Risks.IgnoreQueryFilters().Where(r => r.TenantId == DemoTenantId).ToListAsync(ct));
        db.Scopes.RemoveRange(await db.Scopes.IgnoreQueryFilters().Where(s => s.TenantId == DemoTenantId).ToListAsync(ct));
        db.Assessments.RemoveRange(await db.Assessments.IgnoreQueryFilters().Where(a => a.TenantId == DemoTenantId).ToListAsync(ct));

        // Identify (ID.RA) — remover ANTES dos Assets (FKs Restrict): nós → snapshots → exposições → arestas → ameaças.
        db.BlastRadiusImpactNodes.RemoveRange(await db.BlastRadiusImpactNodes.IgnoreQueryFilters().Where(n => n.TenantId == DemoTenantId).ToListAsync(ct));
        db.BlastRadiusAssessments.RemoveRange(await db.BlastRadiusAssessments.IgnoreQueryFilters().Where(a => a.TenantId == DemoTenantId).ToListAsync(ct));
        db.AssetThreatExposures.RemoveRange(await db.AssetThreatExposures.IgnoreQueryFilters().Where(e => e.TenantId == DemoTenantId).ToListAsync(ct));
        db.AssetDependencies.RemoveRange(await db.AssetDependencies.IgnoreQueryFilters().Where(d => d.TenantId == DemoTenantId).ToListAsync(ct));
        db.Threats.RemoveRange(await db.Threats.IgnoreQueryFilters().Where(t => t.TenantId == DemoTenantId).ToListAsync(ct));

        db.Assets.RemoveRange(await db.Assets.IgnoreQueryFilters().Where(a => a.TenantId == DemoTenantId).ToListAsync(ct));
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
