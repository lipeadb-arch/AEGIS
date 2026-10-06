using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Remediation;
using Microsoft.EntityFrameworkCore;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] O contexto de trabalho de uma leitura ou gravação NIST: avaliação · RODADA · escopo (os três
/// conferidos entre si e com o tenant pelo filtro global), o catálogo da avaliação e o que a rodada registrou no escopo.
/// </summary>
internal sealed class NistScopeContext
{
    public required Assessment Assessment { get; init; }
    public required NistAssessmentCycle Cycle { get; init; }
    public required AssessmentScope Scope { get; init; }
    public required FrameworkVersion Catalog { get; init; }
    public required Dictionary<Guid, SubcategoryEvaluation> Evaluations { get; init; }
    public required Dictionary<string, int> EvidenceCounts { get; init; }
    public required List<NistTestProcedure> Procedures { get; init; }
    public required List<NistFinding> Findings { get; init; }

    public SubcategoryEvaluation? Evaluation(Guid subcategoryId) => Evaluations.TryGetValue(subcategoryId, out var e) ? e : null;
    public int EvidenceCount(string code) => EvidenceCounts.TryGetValue(code, out var n) ? n : 0;

    public IEnumerable<NistSubcategory> AllSubcategories =>
        Catalog.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories);

    public NistSubcategory FindSubcategory(string code)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        return AllSubcategories.FirstOrDefault(s => string.Equals(s.Code, normalized, StringComparison.Ordinal))
               ?? throw new NistAssessmentNotFoundException($"Subcategoria '{code}' não existe no catálogo da avaliação.");
    }
}

internal static class NistJourneySupport
{
    internal const int MaxName = 200;
    internal const int MaxDescription = 2000;
    internal const int MaxLongText = 4000;
    internal const int MinJustification = 10;

    internal static readonly string[] FunctionOrder = { "GV", "ID", "PR", "DE", "RS", "RC" };

    /// <summary>Ordem OFICIAL das categorias no CSF 2.0 (o catálogo não guarda ordem; o código sozinho é alfabético).</summary>
    internal static readonly string[] CategoryOrder =
    {
        "GV.OC", "GV.RM", "GV.RR", "GV.PO", "GV.OV", "GV.SC",
        "ID.AM", "ID.RA", "ID.IM",
        "PR.AA", "PR.AT", "PR.DS", "PR.PS", "PR.IR",
        "DE.CM", "DE.AE",
        "RS.MA", "RS.AN", "RS.CO", "RS.MI",
        "RC.RP", "RC.CO",
    };

    internal static int CategoryRank(string code) => Array.IndexOf(CategoryOrder, code) is var i && i >= 0 ? i : CategoryOrder.Length;

    internal static void RequireTenant(Application.Abstractions.ITenantContext tenant)
    {
        if (tenant.TenantId is not Guid id || id == Guid.Empty)
            throw new NistAssessmentNotFoundException("Tenant não resolvido no contexto.");
    }

    // ---- Carga do contexto ----------------------------------------------------------------------------------------

    internal static async Task<NistAssessmentCycle> LoadCycleAsync(AegisScoreDbContext db, Guid assessmentId, Guid cycleId, CancellationToken ct)
    {
        if (!await db.Assessments.AsNoTracking().AnyAsync(a => a.Id == assessmentId, ct))
            throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        return await db.NistCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId && c.AssessmentId == assessmentId, ct)
               ?? throw new NistAssessmentNotFoundException("Rodada não encontrada nesta avaliação.");
    }

    internal static async Task<NistScopeContext> LoadScopeContextAsync(
        AegisScoreDbContext db, Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        var assessment = await db.Assessments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        var cycle = await db.NistCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId && c.AssessmentId == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Rodada não encontrada nesta avaliação.");
        var scope = await db.Scopes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scopeId && s.AssessmentId == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Escopo não encontrado nesta avaliação.");
        var catalog = await LoadCatalogAsync(db, assessment.FrameworkVersionId, ct);

        var evaluations = await db.Evaluations.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId).ToListAsync(ct);
        var evidence = await db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.RemovedAt == null && e.SubcategoryCode != null)
            .Select(e => e.SubcategoryCode!).ToListAsync(ct);
        var procedures = await db.NistProcedures.AsNoTracking()
            .Where(p => p.AssessmentScopeId == scopeId && p.CycleId == cycleId && p.RemovedAt == null).ToListAsync(ct);
        var findings = await db.NistFindings.AsNoTracking()
            .Where(f => f.AssessmentScopeId == scopeId && f.CycleId == cycleId).ToListAsync(ct);

        return new NistScopeContext
        {
            Assessment = assessment,
            Cycle = cycle,
            Scope = scope,
            Catalog = catalog,
            Evaluations = evaluations.GroupBy(e => e.SubcategoryId).ToDictionary(g => g.Key, g => g.First()),
            EvidenceCounts = evidence.GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Procedures = procedures,
            Findings = findings,
        };
    }

    internal static async Task<FrameworkVersion> LoadCatalogAsync(AegisScoreDbContext db, Guid frameworkVersionId, CancellationToken ct)
    {
        var fv = await db.FrameworkVersions.AsNoTracking()
            .Include(f => f.Functions).ThenInclude(fn => fn.Categories).ThenInclude(c => c.Subcategories)
            .Include(f => f.MaturityLevels)
            .AsSplitQuery()
            .FirstOrDefaultAsync(f => f.Id == frameworkVersionId, ct)
            ?? throw new NistAssessmentNotFoundException("O catálogo desta avaliação não está disponível.");
        fv.Functions = fv.Functions.OrderBy(f => Array.IndexOf(FunctionOrder, f.Code) is var i && i < 0 ? 99 : i).ToList();
        return fv;
    }

    /// <summary>Rodada encerrada é somente leitura: nenhuma gravação passa até ela ser reaberta.</summary>
    internal static void EnsureOpen(NistAssessmentCycle cycle)
    {
        if (cycle.Status != NistCycleStatus.Open)
            throw new NistAssessmentValidationException(
                $"A rodada \"{cycle.Name}\" está encerrada: reabra-a para registrar alterações (a reabertura fica na trilha).");
    }

    // ---- Estados ----------------------------------------------------------------------------------------------------

    internal static bool HasContent(SubcategoryEvaluation e) =>
        e.NotApplicable || e.CurrentLevel is not null || e.TargetLevel is not null
        || new[] { e.CurrentComments, e.TargetComments, e.Rationale, e.Gaps, e.RiskImpact, e.ImprovementGuidance, e.OwnerName }
            .Any(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>
    /// Estado de uma subcategoria na rodada. Só a gravação HUMANA confirma (avaliada / não se aplica); herdado, importado
    /// ou semeado sem revisão fica "aguardando confirmação" e fora das médias. Designar avaliador/revisor não é conteúdo.
    /// </summary>
    internal static string StateOf(SubcategoryEvaluation? e, int evidenceCount)
    {
        if (e is null) return evidenceCount > 0 ? NistSubcategoryStates.InProgress : NistSubcategoryStates.NotEvaluated;
        var content = HasContent(e);
        if (!e.HumanConfirmed)
        {
            if (content && (e.ContentOrigin != NistContentOrigin.Analyst || e.CurrentLevel is not null || e.NotApplicable))
                return NistSubcategoryStates.PendingConfirmation;
            return content || evidenceCount > 0 ? NistSubcategoryStates.InProgress : NistSubcategoryStates.NotEvaluated;
        }
        if (e.NotApplicable) return NistSubcategoryStates.NotApplicable;
        if (e.CurrentLevel is not null) return NistSubcategoryStates.Evaluated;
        return content || evidenceCount > 0 ? NistSubcategoryStates.InProgress : NistSubcategoryStates.NotEvaluated;
    }

    /// <summary>Níveis que ENTRAM nas médias: só os confirmados por revisão humana.</summary>
    internal static Application.Scoring.SubcategoryProfileScore ProfileScoreOf(string code, SubcategoryEvaluation? e) =>
        e is { HumanConfirmed: true }
            ? new(code, e.NotApplicable ? null : e.CurrentLevel, e.NotApplicable ? null : e.TargetLevel, e.NotApplicable)
            : new(code, null, null, false);

    internal static int? ConfirmedGap(SubcategoryEvaluation? e) => e is { HumanConfirmed: true } ? e.Gap : null;

    /// <summary>Impressão digital do CONTEÚDO avaliado (níveis, aplicabilidade e textos) — base da validade da revisão.</summary>
    internal static string ContentFingerprint(SubcategoryEvaluation e)
    {
        var sb = new StringBuilder();
        void S(string? v) => sb.Append(v is null ? "N;" : $"S{v.Length}:{v};");
        sb.Append(e.NotApplicable ? "B1;" : "B0;");
        S(e.CurrentLevel?.ToString(CultureInfo.InvariantCulture));
        S(e.TargetLevel?.ToString(CultureInfo.InvariantCulture));
        S(e.Rationale); S(e.CurrentComments); S(e.TargetComments); S(e.Gaps); S(e.RiskImpact); S(e.ImprovementGuidance);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    internal static string ReviewStateOf(SubcategoryEvaluation e)
    {
        if (e.ReviewDecision == NistReviewDecision.None) return NistReviewStates.None;
        if (!string.Equals(e.ReviewDecisionFingerprint, ContentFingerprint(e), StringComparison.Ordinal)) return NistReviewStates.Outdated;
        return e.ReviewDecision == NistReviewDecision.Approved ? NistReviewStates.Approved : NistReviewStates.ChangesRequested;
    }

    /// <summary>
    /// Há LACUNA DOCUMENTADA que sustente um achado? Diferença positiva entre atual e alvo confirmados, lacuna descrita em
    /// texto ou procedimento realizado com conclusão insatisfatória/parcial. Sem isso, não há achado a registrar.
    /// </summary>
    internal static string? FindingBlockedReason(NistAssessmentCycle cycle, SubcategoryEvaluation? e, IEnumerable<NistTestProcedure> procedures)
    {
        if (cycle.Status != NistCycleStatus.Open) return "A rodada está encerrada.";
        if (e is not { HumanConfirmed: true })
            return "Confirme a avaliação desta subcategoria (gravação humana) antes de registrar um achado.";
        if (e.NotApplicable) return "O resultado foi declarado não aplicável ao escopo.";
        var documented = e.Gap is > 0 || !string.IsNullOrWhiteSpace(e.Gaps)
            || procedures.Any(p => p.Status == NistProcedureStatus.Performed
                                   && p.Outcome is NistProcedureOutcome.Unsatisfactory or NistProcedureOutcome.PartiallySatisfactory);
        return documented ? null
            : "Documente a lacuna antes: diferença entre atual e alvo, texto em \"Lacunas observadas\" ou um procedimento realizado com " +
              "conclusão insatisfatória ou parcial. Uma meta de melhoria não é, por si, um achado.";
    }

    // ---- Responsáveis -----------------------------------------------------------------------------------------------

    internal sealed record ResolvedPerson(Guid? UserId, string? Name, bool IsExternal, string? Contact);

    /// <summary>
    /// Resolve um responsável: usuário ATIVO do tenant (o filtro global garante o tenant), externo (nome obrigatório) ou texto.
    /// Texto legado nunca é convertido em vínculo.
    /// </summary>
    internal static async Task<ResolvedPerson> ResolvePersonAsync(
        AegisScoreDbContext db, Guid? userId, string? name, bool isExternal, string? contact, CancellationToken ct)
    {
        if (userId is { } uid)
            return new ResolvedPerson(uid, await ActiveUserNameAsync(db, uid, ct), false, null);
        var n = Optional(name, MaxName, "O nome do responsável");
        if (isExternal)
        {
            if (n is null) throw new NistAssessmentValidationException("Informe o nome do responsável externo.");
            return new ResolvedPerson(null, n, true, Optional(contact, MaxName, "O contato do responsável externo"));
        }
        return new ResolvedPerson(null, n, false, null);
    }

    internal static async Task<string> ActiveUserNameAsync(AegisScoreDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.IsActive }).FirstOrDefaultAsync(ct);
        if (user is null || !user.IsActive)
            throw new NistAssessmentValidationException("A pessoa designada precisa ser um usuário ativo deste ambiente.");
        return Truncate(user.DisplayName.Trim(), MaxName);
    }

    internal static NistResponsibleView ResponsibleView(Guid? userId, string? name, bool isExternal, string? contact, ISet<Guid> activeUsers) =>
        new(name, userId,
            userId is not null ? NistResponsibleView.KindUser
            : isExternal ? NistResponsibleView.KindExternal
            : string.IsNullOrWhiteSpace(name) ? NistResponsibleView.KindNone : NistResponsibleView.KindText,
            contact,
            userId is { } u && activeUsers.Contains(u));

    internal static string DescribePerson(Guid? userId, string? name, bool isExternal, string? contact) =>
        name is null ? "(sem responsável)"
        : userId is not null ? $"{name} (usuário)"
        : isExternal ? $"{name} (externo{(contact is null ? "" : ", " + contact)})"
        : name;

    internal static async Task<HashSet<Guid>> ActiveUserIdsAsync(AegisScoreDbContext db, CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync(ct)).ToHashSet();

    // ---- Trilha -------------------------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions ChangesJson = new(JsonSerializerDefaults.Web);

    internal static void Diff(List<NistFieldChange> changes, string field, string label, string? from, string? to)
    {
        if (!string.Equals(from, to, StringComparison.Ordinal)) changes.Add(new NistFieldChange(field, label, from, to));
    }

    internal static string? Level(int? v) => v?.ToString(CultureInfo.InvariantCulture);

    internal static void Audit(
        AegisScoreDbContext db, RemediationActor actor, DateTimeOffset at, Guid assessmentId, Guid? cycleId, Guid? scopeId,
        string? code, string subject, Guid? subjectId, string action, string summary, IReadOnlyCollection<NistFieldChange>? changes = null)
    {
        static string? Cut(string? v) => v is null ? null : v.Length <= 500 ? v : v[..500] + "…";
        db.NistAuditEntries.Add(new NistAuditEntry
        {
            AssessmentId = assessmentId,
            CycleId = cycleId,
            AssessmentScopeId = scopeId,
            SubcategoryCode = code,
            Subject = subject,
            SubjectId = subjectId,
            Action = action,
            Summary = Truncate(summary, 1000),
            ChangesJson = changes is null || changes.Count == 0
                ? null
                : JsonSerializer.Serialize(changes.Select(c => new NistFieldChange(c.Field, c.Label, Cut(c.From), Cut(c.To))), ChangesJson),
            At = at,
            ActorAccountId = actor.AccountId,
            ActorName = Truncate(actor.DisplayName ?? "", MaxName),
            CreatedAt = at,
        });
    }

    internal static IReadOnlyList<NistFieldChange> ReadChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<NistFieldChange>();
        try
        {
            return JsonSerializer.Deserialize<List<NistFieldChange>>(json, ChangesJson) ?? new List<NistFieldChange>();
        }
        catch (JsonException)
        {
            return Array.Empty<NistFieldChange>();
        }
    }

    // ---- Planos ------------------------------------------------------------------------------------------------------

    /// <summary>Planos NIST dos achados indicados (todas as gerações), já na visão de leitura comum.</summary>
    internal static async Task<Dictionary<Guid, List<ActionPlanView>>> PlansByFindingAsync(
        AegisScoreDbContext db, IReadOnlyCollection<Guid> findingIds, CancellationToken ct)
    {
        if (findingIds.Count == 0) return new Dictionary<Guid, List<ActionPlanView>>();
        var ids = findingIds.ToList();
        var plans = await db.ActionPlans.AsNoTracking()
            .Include(p => p.Validations).Include(p => p.Events)
            .Where(p => p.OriginKind == ActionPlanOriginKind.NistFinding && p.OriginNistFindingId != null && ids.Contains(p.OriginNistFindingId.Value))
            .AsSplitQuery()
            .ToListAsync(ct);
        return plans.GroupBy(p => p.OriginNistFindingId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CreatedAt.UtcTicks).ThenByDescending(p => p.Id)
                .Select(RemediationService.ToView).ToList());
    }

    /// <summary>O plano que responde pelo achado agora: o ativo, senão o mais recente.</summary>
    internal static ActionPlanView? CurrentPlan(IReadOnlyList<ActionPlanView>? plans) =>
        plans is null || plans.Count == 0 ? null : plans.FirstOrDefault(p => p.IsActive) ?? plans[0];

    internal static string TreatmentLabel(ActionPlanView? plan)
    {
        if (plan is null) return "Sem plano de tratamento";
        var label = RemediationReading.StatusLabel(plan.Status);
        if (plan.IsOverdue) label += " — atrasado";
        if (plan.Status == ActionPlanStatus.Concluido && plan.ApplicableValidation is not null) label += " — validação humana registrada";
        if (plan.WasReopened && plan.Status != ActionPlanStatus.Concluido) label += " — reaberto";
        return label;
    }

    // ---- Textos -------------------------------------------------------------------------------------------------------

    internal static string Required(string? value, string emptyMessage, int max, string field)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new NistAssessmentValidationException(emptyMessage);
        if (v.Length > max) throw new NistAssessmentValidationException($"{field} aceita no máximo {max} caracteres.");
        return v;
    }

    internal static string? Optional(string? value, int max, string field)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length > max) throw new NistAssessmentValidationException($"{field} aceita no máximo {max} caracteres.");
        return v;
    }

    internal static string Justified(string? value, string field)
    {
        var v = Required(value, $"Justifique {field} (pelo menos {MinJustification} caracteres).", MaxDescription, $"A justificativa de {field}");
        if (v.Length < MinJustification)
            throw new NistAssessmentValidationException($"Justifique {field} (pelo menos {MinJustification} caracteres).");
        return v;
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    internal static string ScopeName(AssessmentScope s) => string.IsNullOrWhiteSpace(s.Name) ? "Escopo sem nome" : s.Name;

    internal static string Date(DateTimeOffset v) => v.UtcDateTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    internal static TEnum ParseEnum<TEnum>(string? value, string message) where TEnum : struct, Enum
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || int.TryParse(v, out _) || !Enum.TryParse<TEnum>(v, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new NistAssessmentValidationException(message);
        return parsed;
    }
}
