using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Application.Scoring;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Jornada persistida do AEGIS NIST sobre o modelo EXISTENTE de assessment
/// (<see cref="Assessment"/> → <see cref="AssessmentScope"/> → <see cref="SubcategoryEvaluation"/> + <see cref="Evidence"/>).
///
/// [AEGIS-NIST-JOURNEY-02] Cada avaliação tem RODADAS (<see cref="NistAssessmentCycle"/>): leitura e gravação nomeiam
/// avaliação · rodada · escopo, e uma rodada nova nunca sobrescreve a anterior. Conteúdo herdado ou importado aguarda
/// confirmação humana e fica fora das médias. Responsável, avaliador e revisor são usuários ATIVOS do tenant verificados
/// no servidor (ou contato externo / texto legado, sem conversão automática). Toda alteração relevante entra na trilha com
/// autor, instante e valores anteriores/novos.
///
/// Garantias mantidas: tenant implícito e fail-closed; níveis 1–5 ou ausentes (nunca zero); concorrência pela versão;
/// evidência do KNIGHT só por mapeamento explícito e sem mudar nível; sugestão da IA nunca gravada. Não escreve no score de
/// postura (TenantControlState / aegis-score-v1): apenas o lê como contexto.
/// </summary>
public sealed partial class NistAssessmentService : INistAssessmentService
{
    private const int MaxTitle = 300;
    private const int MaxNotes = 2000;

    private static readonly KnightIndicatorStatus[] EvaluatedKnightStatuses =
        { KnightIndicatorStatus.Passed, KnightIndicatorStatus.Exposed, KnightIndicatorStatus.Mitigated };

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IControlLanguageCatalog? _language;
    private readonly ILogger<NistAssessmentService> _log;
    private readonly MaturityScoringService _maturity = new();

    public NistAssessmentService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        IControlLanguageCatalog? language = null,
        ILogger<NistAssessmentService>? log = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _language = language;
        _log = log ?? NullLogger<NistAssessmentService>.Instance;
    }

    // =============================================================================================
    //  Avaliações, escopos e rodadas
    // =============================================================================================

    public async Task<IReadOnlyList<NistAssessmentView>> ListAsync(CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var assessments = await _db.Assessments.AsNoTracking().ToListAsync(ct);
        if (assessments.Count == 0) return Array.Empty<NistAssessmentView>();

        var views = new List<NistAssessmentView>();
        foreach (var a in assessments)
            views.Add(await BuildAssessmentViewAsync(a, ct));
        // Mais recente primeiro (ordenado em memória: o SQLite não ordena DateTimeOffset).
        return views.OrderByDescending(v => v.LastReviewedAt ?? v.CreatedAt).ThenByDescending(v => v.CreatedAt).ToList();
    }

    public async Task<NistAssessmentView> GetAsync(Guid assessmentId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var a = await _db.Assessments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        return await BuildAssessmentViewAsync(a, ct);
    }

    public async Task<NistAssessmentView> CreateAsync(CreateNistAssessmentCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);

        var name = Required(command.Name, "Informe o nome da avaliação.", MaxName, "O nome da avaliação");
        var description = Optional(command.Description, MaxDescription, "A descrição da avaliação");
        if (command.StartDate is { } s && command.EndDate is { } e && e < s)
            throw new NistAssessmentValidationException("A data de término não pode ser anterior à de início.");

        var fv = await _db.FrameworkVersions.AsNoTracking().FirstOrDefaultAsync(f => f.IsActive, ct)
            ?? throw new NistAssessmentValidationException("Não há catálogo NIST ativo para avaliar.");

        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var cycleStart = command.InitialCyclePeriodStart ?? command.StartDate ?? today;
        var cycleEnd = command.InitialCyclePeriodEnd ?? command.EndDate ?? cycleStart;
        var cycleKind = string.IsNullOrWhiteSpace(command.InitialCyclePeriodKind)
            ? NistCyclePeriodKind.Other
            : ParseEnum<NistCyclePeriodKind>(command.InitialCyclePeriodKind, "Período da rodada inválido (Monthly, Quarterly ou Other).");
        ValidatePeriod(cycleKind, cycleStart, cycleEnd);
        var cycleName = Optional(command.InitialCycleName, 120, "O nome da rodada") ?? "Rodada 1";

        // Sem TenantId: carimbado no SaveChanges (fail-closed).
        var assessment = new Assessment
        {
            FrameworkVersionId = fv.Id,
            Name = name,
            Description = description,
            StartDate = command.StartDate,
            EndDate = command.EndDate,
            Status = AssessmentStatus.Draft,
            MethodologyVersion = AssessmentMethodology.Version,
            CreatedAt = now,
        };
        _db.Assessments.Add(assessment);

        var cycle = new NistAssessmentCycle
        {
            AssessmentId = assessment.Id,
            Name = cycleName,
            PeriodKind = cycleKind,
            PeriodStart = cycleStart,
            PeriodEnd = cycleEnd,
            Status = NistCycleStatus.Open,
            SeedMode = NistCycleSeedMode.None,
            CreatedByAccountId = actor.AccountId,
            CreatedByName = PersonName(actor),
            Version = 1,
            CreatedAt = now,
        };
        _db.NistCycles.Add(cycle);
        Audit(_db, actor, now, assessment.Id, null, null, null, "Assessment", assessment.Id, "Created",
            $"Avaliação \"{name}\" criada (metodologia {AssessmentMethodology.Version}).");
        Audit(_db, actor, now, assessment.Id, cycle.Id, null, null, "Cycle", cycle.Id, "Created",
            $"Rodada \"{cycleName}\" criada ({NistLabels.PeriodKind(cycleKind.ToString())}, {cycleStart:dd/MM/yyyy} a {cycleEnd:dd/MM/yyyy}).");

        if (!string.IsNullOrWhiteSpace(command.InitialScopeName))
        {
            var scope = new AssessmentScope
            {
                AssessmentId = assessment.Id,
                Name = Required(command.InitialScopeName, "Informe o nome do escopo.", MaxName, "O nome do escopo"),
                Description = Optional(command.InitialScopeDescription, MaxDescription, "A descrição do escopo"),
                CreatedAt = now,
            };
            _db.Scopes.Add(scope);
            Audit(_db, actor, now, assessment.Id, null, scope.Id, null, "Scope", scope.Id, "Created", $"Escopo \"{scope.Name}\" criado.");
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Avaliação NIST {AssessmentId} criada por {Actor}.", assessment.Id, actor.DisplayName);
        _db.ChangeTracker.Clear();
        return await GetAsync(assessment.Id, ct);
    }

    public async Task<NistScopeView> AddScopeAsync(Guid assessmentId, CreateNistScopeCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var assessment = await _db.Assessments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Avaliação não encontrada.");

        var name = Required(command.Name, "Informe o nome do escopo.", MaxName, "O nome do escopo");
        var normalized = name.ToUpperInvariant();
        var existing = await _db.Scopes.AsNoTracking().Where(s => s.AssessmentId == assessmentId).Select(s => s.Name).ToListAsync(ct);
        if (existing.Any(n => string.Equals((n ?? "").Trim().ToUpperInvariant(), normalized, StringComparison.Ordinal)))
            throw new NistAssessmentConflictException("Já existe um escopo com este nome nesta avaliação.");

        var now = _clock.GetUtcNow();
        var scope = new AssessmentScope
        {
            AssessmentId = assessmentId,
            Name = name,
            Description = Optional(command.Description, MaxDescription, "A descrição do escopo"),
            CreatedAt = now,
        };
        _db.Scopes.Add(scope);
        Audit(_db, actor, now, assessmentId, null, scope.Id, null, "Scope", scope.Id, "Created", $"Escopo \"{name}\" criado.");
        await _db.SaveChangesAsync(ct);

        var total = await CountSubcategoriesAsync(assessment.FrameworkVersionId, ct);
        return new NistScopeView(scope.Id, scope.Name, scope.Description, total, 0, 0, 0, 0, null);
    }

    public async Task<NistCycleView> CreateCycleAsync(Guid assessmentId, CreateNistCycleCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        if (!await _db.Assessments.AsNoTracking().AnyAsync(a => a.Id == assessmentId, ct))
            throw new NistAssessmentNotFoundException("Avaliação não encontrada.");

        var name = Required(command.Name, "Informe o nome da rodada.", 120, "O nome da rodada");
        var kind = ParseEnum<NistCyclePeriodKind>(command.PeriodKind, "Período da rodada inválido (Monthly, Quarterly ou Other).");
        ValidatePeriod(kind, command.PeriodStart, command.PeriodEnd);
        var seedMode = ParseEnum<NistCycleSeedMode>(command.SeedMode, "Aproveitamento inválido (None, Reference ou Draft).");

        var cycles = await _db.NistCycles.AsNoTracking().Where(c => c.AssessmentId == assessmentId).ToListAsync(ct);
        if (cycles.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new NistAssessmentConflictException("Já existe uma rodada com este nome nesta avaliação.");

        NistAssessmentCycle? source = null;
        if (seedMode == NistCycleSeedMode.None)
        {
            if (command.SeedFromCycleId is not null)
                throw new NistAssessmentValidationException("Para aproveitar outra rodada, escolha \"referência\" ou \"rascunho\".");
        }
        else
        {
            var sourceId = command.SeedFromCycleId
                ?? throw new NistAssessmentValidationException("Escolha a rodada a aproveitar.");
            source = cycles.FirstOrDefault(c => c.Id == sourceId)
                ?? throw new NistAssessmentNotFoundException("A rodada a aproveitar não pertence a esta avaliação.");
        }

        var now = _clock.GetUtcNow();
        var cycle = new NistAssessmentCycle
        {
            AssessmentId = assessmentId,
            Name = name,
            PeriodKind = kind,
            PeriodStart = command.PeriodStart,
            PeriodEnd = command.PeriodEnd,
            Status = NistCycleStatus.Open,
            SeedFromCycleId = source?.Id,
            SeedMode = seedMode,
            CreatedByAccountId = actor.AccountId,
            CreatedByName = PersonName(actor),
            Version = 1,
            CreatedAt = now,
        };
        _db.NistCycles.Add(cycle);

        int copiedEvaluations = 0, copiedProcedures = 0;
        if (source is not null && seedMode == NistCycleSeedMode.Draft)
        {
            // RASCUNHO identificado: níveis, textos e o responsável pela prática; NADA de revisão humana, evidência,
            // resultado de teste, achado ou decisão do revisor é herdado.
            var sourceEvals = await _db.Evaluations.AsNoTracking().Where(e => e.CycleId == source.Id).ToListAsync(ct);
            foreach (var src in sourceEvals.Where(HasContent))
            {
                var note = $"Rascunho a partir da rodada \"{source.Name}\" (versão {src.Version}"
                    + (src.HumanConfirmed && src.ReviewedAt is { } at
                        ? $", revisão humana de {src.ReviewedByName ?? "autor não identificado"} em {Date(at)})"
                        : ", sem revisão humana)")
                    + ". Confirme na tela para que entre nas médias.";
                _db.Evaluations.Add(new SubcategoryEvaluation
                {
                    AssessmentScopeId = src.AssessmentScopeId,
                    CycleId = cycle.Id,
                    SubcategoryId = src.SubcategoryId,
                    NotApplicable = src.NotApplicable,
                    CurrentLevel = src.CurrentLevel,
                    CurrentScore = src.CurrentLevel,
                    TargetLevel = src.TargetLevel,
                    TargetScore = src.TargetLevel,
                    CurrentComments = src.CurrentComments,
                    TargetComments = src.TargetComments,
                    Rationale = src.Rationale,
                    Gaps = src.Gaps,
                    RiskImpact = src.RiskImpact,
                    ImprovementGuidance = src.ImprovementGuidance,
                    OwnerName = src.OwnerName,
                    OwnerUserId = src.OwnerUserId,
                    OwnerIsExternal = src.OwnerIsExternal,
                    OwnerContact = src.OwnerContact,
                    EvaluatedBy = EvaluatedBy.Analyst,
                    ContentOrigin = NistContentOrigin.CarriedForward,
                    SourceEvaluationId = src.Id,
                    OriginNote = Truncate(note, 500),
                    Version = 1,
                    CreatedAt = now,
                });
                copiedEvaluations++;
            }

            var sourceProcedures = await _db.NistProcedures.AsNoTracking()
                .Where(p => p.CycleId == source.Id && p.RemovedAt == null).ToListAsync(ct);
            foreach (var p in sourceProcedures)
            {
                _db.NistProcedures.Add(new NistTestProcedure
                {
                    AssessmentId = assessmentId,
                    CycleId = cycle.Id,
                    AssessmentScopeId = p.AssessmentScopeId,
                    SubcategoryCode = p.SubcategoryCode,
                    Method = p.Method,
                    Procedure = p.Procedure,
                    Status = NistProcedureStatus.Planned,
                    ContentOrigin = NistContentOrigin.CarriedForward,
                    SourceProcedureId = p.Id,
                    OriginNote = Truncate($"Planejado na rodada \"{source.Name}\"; o resultado não é herdado.", 500),
                    CreatedByAccountId = actor.AccountId,
                    CreatedByName = PersonName(actor),
                    Version = 1,
                    CreatedAt = now,
                });
                copiedProcedures++;
            }
        }

        var changes = new List<NistFieldChange>();
        Diff(changes, "name", "nome", null, name);
        Diff(changes, "period", "período", null, $"{NistLabels.PeriodKind(kind.ToString())}: {command.PeriodStart:dd/MM/yyyy} a {command.PeriodEnd:dd/MM/yyyy}");
        Diff(changes, "seed", "aproveitamento", null, source is null ? NistLabels.SeedMode("None") : $"{NistLabels.SeedMode(seedMode.ToString())} — \"{source.Name}\"");
        Audit(_db, actor, now, assessmentId, cycle.Id, null, null, "Cycle", cycle.Id, "Created",
            $"Rodada \"{name}\" criada." + (seedMode == NistCycleSeedMode.Draft
                ? $" Rascunho herdado: {copiedEvaluations} avaliação(ões) e {copiedProcedures} procedimento(s) planejado(s), aguardando confirmação humana."
                : ""),
            changes);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new NistAssessmentConflictException("Outra pessoa acabou de criar uma rodada com este nome. Recarregue a avaliação.");
        }
        _db.ChangeTracker.Clear();
        return (await CycleViewsAsync(assessmentId, ct)).First(c => c.Id == cycle.Id);
    }

    public async Task<NistCycleView> SetCycleStatusAsync(
        Guid assessmentId, Guid cycleId, SetNistCycleStatusCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var target = ParseEnum<NistCycleStatus>(command.Status, "Situação da rodada inválida (Open ou Closed).");
        var cycle = await _db.NistCycles.FirstOrDefaultAsync(c => c.Id == cycleId && c.AssessmentId == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Rodada não encontrada nesta avaliação.");
        if (cycle.Version != command.ExpectedVersion)
            throw new NistAssessmentConflictException("A rodada foi alterada por outra pessoa. Recarregue antes de continuar.");
        if (cycle.Status == target)
            return (await CycleViewsAsync(assessmentId, ct)).First(c => c.Id == cycleId);

        var now = _clock.GetUtcNow();
        var from = cycle.Status;
        cycle.Status = target;
        cycle.ClosedAt = target == NistCycleStatus.Closed ? now : null;
        cycle.ClosedByName = target == NistCycleStatus.Closed ? PersonName(actor) : null;
        cycle.Version++;
        var changes = new List<NistFieldChange>();
        Diff(changes, "status", "situação", NistLabels.CycleStatus(from.ToString()), NistLabels.CycleStatus(target.ToString()));
        Audit(_db, actor, now, assessmentId, cycleId, null, null, "Cycle", cycleId, target == NistCycleStatus.Closed ? "Closed" : "Reopened",
            target == NistCycleStatus.Closed ? $"Rodada \"{cycle.Name}\" encerrada (somente leitura)." : $"Rodada \"{cycle.Name}\" reaberta.", changes);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("A rodada foi alterada por outra pessoa enquanto a sua gravação estava em curso.");
        }
        _db.ChangeTracker.Clear();
        return (await CycleViewsAsync(assessmentId, ct)).First(c => c.Id == cycleId);
    }

    /// <summary>Mensal e trimestral seguem o calendário; "outro período" aceita qualquer intervalo de até 3 anos.</summary>
    internal static void ValidatePeriod(NistCyclePeriodKind kind, DateOnly start, DateOnly end)
    {
        if (end < start) throw new NistAssessmentValidationException("O fim do período não pode ser anterior ao início.");
        switch (kind)
        {
            case NistCyclePeriodKind.Monthly when start.Day != 1 || end != start.AddMonths(1).AddDays(-1):
                throw new NistAssessmentValidationException("Uma rodada mensal cobre um mês do calendário (do dia 1 ao último dia).");
            case NistCyclePeriodKind.Quarterly when start.Day != 1 || (start.Month - 1) % 3 != 0 || end != start.AddMonths(3).AddDays(-1):
                throw new NistAssessmentValidationException("Uma rodada trimestral cobre um trimestre do calendário (jan–mar, abr–jun, jul–set ou out–dez).");
            case NistCyclePeriodKind.Other when end > start.AddYears(3):
                throw new NistAssessmentValidationException("O período informado pode ter no máximo 3 anos.");
        }
    }

    // =============================================================================================
    //  Funções, perfil e subcategorias
    // =============================================================================================

    public async Task<NistFunctionView> GetFunctionAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string functionCode, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var code = (functionCode ?? "").Trim().ToUpperInvariant();
        var fn = ctx.Catalog.Functions.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal))
            ?? throw new NistAssessmentNotFoundException($"Função '{functionCode}' não existe no catálogo da avaliação.");

        var subs = fn.Categories.SelectMany(c => c.Subcategories).ToList();
        var profile = _maturity.AggregateProfile(subs.Select(s => ProfileScoreOf(s.Code, ctx.Evaluation(s.Id))));

        var categories = fn.Categories.OrderBy(c => CategoryRank(c.Code)).ThenBy(c => c.Code, StringComparer.Ordinal).Select(c =>
        {
            var catProfile = profile.Categories.FirstOrDefault(p => p.RefCode == c.Code)
                ?? new ProfileScore(SnapshotLevel.Category, c.Code, null, null, null, 0, 0, 0, 0, 0);
            var rows = c.Subcategories.OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => RowOf(s, ctx)).ToList();
            return new NistCategoryView(c.Code, c.Name, c.Definition, ProfileView(catProfile, rows.Count(r => r.State == NistSubcategoryStates.Evaluated)), rows);
        }).ToList();

        var evaluated = categories.Sum(c => c.Profile.Evaluated);
        var fnProfile = profile.Functions.FirstOrDefault() ?? profile.Overall;
        return new NistFunctionView(assessmentId, scopeId, fn.Code, fn.Name, fn.Definition, ProfileView(fnProfile, evaluated), categories, cycleId);
    }

    public async Task<NistProfileView> GetProfileAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var subs = ctx.AllSubcategories.ToList();
        var profile = _maturity.AggregateProfile(subs.Select(s => ProfileScoreOf(s.Code, ctx.Evaluation(s.Id))));

        var states = subs.ToDictionary(s => s.Code, s => StateOf(ctx.Evaluation(s.Id), ctx.EvidenceCount(s.Code)), StringComparer.Ordinal);
        int EvaluatedIn(string prefix) => states.Count(kv => kv.Value == NistSubcategoryStates.Evaluated && InPrefix(kv.Key, prefix));

        var openFindings = ctx.Findings.Where(f => f.Status == NistFindingStatus.Open)
            .GroupBy(f => f.SubcategoryCode, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var gaps = subs
            .Select(s => (Sub: s, Eval: ctx.Evaluation(s.Id)))
            .Where(x => ConfirmedGap(x.Eval) is not null)
            .Select(x => new NistGapView(x.Sub.Code, TitleOf(x.Sub), x.Eval!.CurrentLevel!.Value, x.Eval.TargetLevel!.Value,
                x.Eval.Gap!.Value, x.Eval.OwnerName, x.Eval.ImprovementGuidance,
                openFindings.TryGetValue(x.Sub.Code, out var n) ? n : 0, x.Eval.RiskImpact))
            .OrderByDescending(g => g.Gap).ThenBy(g => g.Code, StringComparer.Ordinal)
            .ToList();

        var indeterminate = subs.Count(s => ctx.Evaluation(s.Id) is not { HumanConfirmed: true, NotApplicable: true } && ConfirmedGap(ctx.Evaluation(s.Id)) is null);

        NistStateCountsView Counts(string code, IReadOnlyCollection<NistSubcategory> set) => new(
            code,
            set.Count,
            set.Count(s => states[s.Code] == NistSubcategoryStates.NotEvaluated),
            set.Count(s => states[s.Code] == NistSubcategoryStates.InProgress),
            set.Count(s => states[s.Code] == NistSubcategoryStates.PendingConfirmation),
            set.Count(s => states[s.Code] == NistSubcategoryStates.Evaluated),
            set.Count(s => states[s.Code] == NistSubcategoryStates.NotApplicable),
            set.Count(s => ctx.Evaluation(s.Id) is { } e && ReviewStateOf(e) == NistReviewStates.Approved),
            set.Count(s => ctx.Evaluation(s.Id) is { } e && ReviewStateOf(e) == NistReviewStates.Outdated));

        var functionStates = ctx.Catalog.Functions
            .Select(f => Counts(f.Code, f.Categories.SelectMany(c => c.Subcategories).ToList())).ToList();

        var procedures = Enum.GetValues<NistTestMethod>().Select(m =>
        {
            var set = ctx.Procedures.Where(p => p.Method == m).ToList();
            return new NistProcedureProgressView(m.ToString(),
                set.Count(p => p.Status == NistProcedureStatus.Planned),
                set.Count(p => p.Status == NistProcedureStatus.InProgress),
                set.Count(p => p.Status == NistProcedureStatus.Performed),
                set.Count(p => p.Status == NistProcedureStatus.NotPerformed),
                set.Count(p => p.Outcome == NistProcedureOutcome.Satisfactory),
                set.Count(p => p.Outcome == NistProcedureOutcome.PartiallySatisfactory),
                set.Count(p => p.Outcome == NistProcedureOutcome.Unsatisfactory),
                set.Count(p => p.Outcome == NistProcedureOutcome.Inconclusive));
        }).ToList();

        var plans = await PlansByFindingAsync(_db, ctx.Findings.Select(f => f.Id).ToList(), ct);
        var current = ctx.Findings.Select(f => CurrentPlan(plans.TryGetValue(f.Id, out var l) ? l : null)).Where(p => p is not null).Select(p => p!).ToList();
        var treatment = new NistTreatmentView(
            ctx.Findings.Count(f => f.Status == NistFindingStatus.Open),
            ctx.Findings.Count(f => f.Status == NistFindingStatus.RiskAccepted),
            ctx.Findings.Count(f => f.Status == NistFindingStatus.Closed),
            ctx.Findings.Where(f => f.Status == NistFindingStatus.Open).GroupBy(f => f.Severity.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            ctx.Findings.Count(f => f.Status == NistFindingStatus.Open && !plans.ContainsKey(f.Id)),
            current.Count(p => p.Status == ActionPlanStatus.Aberto),
            current.Count(p => p.Status == ActionPlanStatus.EmAndamento),
            current.Count(p => p.Status == ActionPlanStatus.AguardandoValidacao),
            current.Count(p => p.Status == ActionPlanStatus.Concluido),
            current.Count(p => p.IsOverdue));

        return new NistProfileView(
            assessmentId, scopeId, ctx.Assessment.MethodologyVersion,
            ProfileView(profile.Overall, EvaluatedIn("ALL")),
            profile.Functions.OrderBy(f => Array.IndexOf(FunctionOrder, f.RefCode)).Select(f => ProfileView(f, EvaluatedIn(f.RefCode))).ToList(),
            profile.Categories.OrderBy(c => CategoryRank(c.RefCode)).Select(c => ProfileView(c, EvaluatedIn(c.RefCode))).ToList(),
            gaps,
            indeterminate,
            cycleId,
            Counts("ALL", subs),
            functionStates,
            procedures,
            treatment);
    }

    private static bool InPrefix(string code, string prefix) =>
        prefix == "ALL" || code.StartsWith(prefix, StringComparison.Ordinal) && (code.Length == prefix.Length || code[prefix.Length] is '.' or '-');

    public async Task<NistSubcategoryDetailView> GetSubcategoryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var sub = ctx.FindSubcategory(code);
        return await BuildDetailAsync(ctx, sub, ct);
    }

    // =============================================================================================
    //  Histórico, pessoas e trilha
    // =============================================================================================

    public async Task<IReadOnlyList<NistAssessmentHistoryItem>> HistoryAsync(CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var assessments = await _db.Assessments.AsNoTracking().ToListAsync(ct);
        if (assessments.Count == 0) return Array.Empty<NistAssessmentHistoryItem>();
        var scopes = await _db.Scopes.AsNoTracking().ToListAsync(ct);
        var cycles = await _db.NistCycles.AsNoTracking().ToListAsync(ct);
        var evaluations = await _db.Evaluations.AsNoTracking().ToListAsync(ct);

        var items = new List<NistAssessmentHistoryItem>();
        foreach (var a in assessments)
        {
            var catalog = await LoadCatalogAsync(_db, a.FrameworkVersionId, ct);
            var subs = catalog.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories).ToList();
            foreach (var c in cycles.Where(x => x.AssessmentId == a.Id))
            foreach (var s in scopes.Where(x => x.AssessmentId == a.Id))
            {
                var evals = evaluations.Where(e => e.AssessmentScopeId == s.Id && e.CycleId == c.Id && e.HumanConfirmed)
                    .GroupBy(e => e.SubcategoryId).ToDictionary(g => g.Key, g => g.First());
                if (evals.Count == 0) continue; // sem revisão humana: a rodada não representa nada neste escopo
                var last = evals.Values.Select(e => e.ReviewedAt!.Value).Max();
                var profile = _maturity.AggregateProfile(subs.Select(sub => ProfileScoreOf(sub.Code, evals.TryGetValue(sub.Id, out var e) ? e : null)));
                items.Add(new NistAssessmentHistoryItem(
                    a.Id, a.Name, s.Id, ScopeName(s), a.MethodologyVersion,
                    new DateOnly(c.PeriodEnd.Year, c.PeriodEnd.Month, 1),
                    "Mês de término do período da rodada; só avaliações confirmadas por revisão humana.",
                    last, subs.Count,
                    evals.Values.Count(e => !e.NotApplicable && e.CurrentLevel.HasValue),
                    evals.Values.Count(e => e.NotApplicable),
                    profile.Overall.Current, profile.Overall.Target,
                    c.Id, c.Name, c.PeriodStart, c.PeriodEnd));
            }
        }
        return items.OrderByDescending(i => i.ReferenceMonth).ThenByDescending(i => i.LastReviewedAt).ToList();
    }

    public async Task<IReadOnlyList<NistAssigneeView>> AssigneesAsync(CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var users = await _db.Users.AsNoTracking().Where(u => u.IsActive)
            .Select(u => new { u.Id, u.DisplayName, u.Role }).ToListAsync(ct);
        return users.OrderBy(u => u.DisplayName, StringComparer.CurrentCulture)
            .Select(u => new NistAssigneeView(u.Id, u.DisplayName, u.Role.ToString())).ToList();
    }

    public async Task<IReadOnlyList<NistAuditEntryView>> AuditAsync(Guid assessmentId, Guid? cycleId, Guid? scopeId, string? code, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        if (!await _db.Assessments.AsNoTracking().AnyAsync(a => a.Id == assessmentId, ct))
            throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        var normalized = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

        var q = _db.NistAuditEntries.AsNoTracking().Where(e => e.AssessmentId == assessmentId);
        if (cycleId is { } c) q = q.Where(e => e.CycleId == c);
        if (scopeId is { } s) q = q.Where(e => e.AssessmentScopeId == s);
        if (normalized is not null) q = q.Where(e => e.SubcategoryCode == normalized);
        var entries = (await q.ToListAsync(ct)).Select(e => new NistAuditEntryView(
            e.Id, e.At, e.ActorName, e.Subject, e.SubjectId, e.Action, e.Summary, e.CycleId, e.AssessmentScopeId, e.SubcategoryCode,
            ReadChanges(e.ChangesJson))).ToList();

        // A trilha dos PLANOS vive no próprio plano (ActionPlanEvent, agora com valores anteriores/novos).
        var plans = _db.ActionPlans.AsNoTracking().Where(p => p.OriginKind == ActionPlanOriginKind.NistFinding && p.OriginNistAssessmentId == assessmentId);
        if (cycleId is { } pc) plans = plans.Where(p => p.OriginNistCycleId == pc);
        if (scopeId is { } ps) plans = plans.Where(p => p.OriginNistScopeId == ps);
        if (normalized is not null) plans = plans.Where(p => p.OriginSubcategoryCode == normalized);
        var planInfo = await plans.Select(p => new { p.Id, p.Title, p.OriginNistCycleId, p.OriginNistScopeId, p.OriginSubcategoryCode }).ToListAsync(ct);
        if (planInfo.Count > 0)
        {
            var ids = planInfo.Select(p => p.Id).ToList();
            var events = await _db.ActionPlanEvents.AsNoTracking().Where(e => ids.Contains(e.ActionPlanId)).ToListAsync(ct);
            foreach (var e in events)
            {
                var p = planInfo.First(x => x.Id == e.ActionPlanId);
                var changes = ReadChanges(e.ChangesJson).ToList();
                if (e.FromStatus is { } from && e.ToStatus is { } to)
                    changes.Insert(0, new NistFieldChange("status", "etapa", RemediationReading.StatusLabel(from), RemediationReading.StatusLabel(to)));
                entries.Add(new NistAuditEntryView(e.Id, e.At, e.ActorName, "Plan", e.ActionPlanId, e.Kind.ToString(),
                    $"Plano \"{p.Title}\": " + (e.Note ?? PlanEventLabel(e.Kind)), p.OriginNistCycleId, p.OriginNistScopeId, p.OriginSubcategoryCode, changes));
            }
        }

        return entries.OrderByDescending(e => e.At.UtcTicks).ThenByDescending(e => e.Id).Take(1000).ToList();
    }

    private static string PlanEventLabel(ActionPlanEventKind kind) => kind switch
    {
        ActionPlanEventKind.Created => "criado.",
        ActionPlanEventKind.StatusChanged => "etapa alterada.",
        ActionPlanEventKind.ExecutionRecorded => "execução relatada.",
        ActionPlanEventKind.ValidationRecorded => "validação registrada.",
        _ => "campos alterados.",
    };

    // =============================================================================================
    //  Montagem
    // =============================================================================================

    private async Task<IReadOnlyList<NistCycleView>> CycleViewsAsync(Guid assessmentId, CancellationToken ct)
    {
        var cycles = await _db.NistCycles.AsNoTracking().Where(c => c.AssessmentId == assessmentId).ToListAsync(ct);
        var publications = await _db.PostureSnapshots.AsNoTracking()
            .Where(s => s.Type == PostureSnapshotType.NistMaturity && s.NistAssessmentId == assessmentId)
            .Select(s => s.NistCycleId).ToListAsync(ct);
        return cycles
            .OrderByDescending(c => c.PeriodStart).ThenByDescending(c => c.CreatedAt.UtcTicks).ThenBy(c => c.Id)
            .Select(c => new NistCycleView(c.Id, c.Name, c.PeriodKind.ToString(), c.PeriodStart, c.PeriodEnd, c.Status.ToString(),
                c.SeedFromCycleId, cycles.FirstOrDefault(x => x.Id == c.SeedFromCycleId)?.Name, c.SeedMode.ToString(),
                c.CreatedAt, c.CreatedByName, c.ClosedAt, c.ClosedByName, c.Version, publications.Count(p => p == c.Id)))
            .ToList();
    }

    private Task<int> CountSubcategoriesAsync(Guid frameworkVersionId, CancellationToken ct) =>
        _db.Subcategories.CountAsync(s => s.Category!.Function!.FrameworkVersionId == frameworkVersionId, ct);

    private async Task<NistAssessmentView> BuildAssessmentViewAsync(Assessment a, CancellationToken ct)
    {
        var fvName = await _db.FrameworkVersions.AsNoTracking().Where(f => f.Id == a.FrameworkVersionId).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? "";
        var total = await CountSubcategoriesAsync(a.FrameworkVersionId, ct);
        var cycles = await CycleViewsAsync(a.Id, ct);
        var progressCycle = cycles.FirstOrDefault();
        var scopes = await _db.Scopes.AsNoTracking().Where(s => s.AssessmentId == a.Id).ToListAsync(ct);
        var scopeIds = scopes.Select(s => s.Id).ToList();
        var cycleId = progressCycle?.Id ?? Guid.Empty;
        var evals = await _db.Evaluations.AsNoTracking().Where(e => scopeIds.Contains(e.AssessmentScopeId) && e.CycleId == cycleId).ToListAsync(ct);
        var evidence = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId != null && scopeIds.Contains(e.AssessmentScopeId!.Value) && e.CycleId == cycleId
                        && e.RemovedAt == null && e.SubcategoryCode != null)
            .Select(e => new { ScopeId = e.AssessmentScopeId!.Value, Code = e.SubcategoryCode! }).ToListAsync(ct);
        var lastAny = await _db.Evaluations.AsNoTracking().Where(e => scopeIds.Contains(e.AssessmentScopeId) && e.ReviewedAt != null)
            .Select(e => e.ReviewedAt).ToListAsync(ct);

        var scopeViews = scopes
            .OrderBy(s => s.CreatedAt.UtcTicks).ThenBy(s => s.Id)
            .Select(s =>
            {
                var se = evals.Where(e => e.AssessmentScopeId == s.Id).ToList();
                var withEvidence = evidence.Where(x => x.ScopeId == s.Id).Select(x => x.Code).Distinct(StringComparer.Ordinal).Count();
                var states = se.Select(e => StateOf(e, 0)).ToList();
                var last = se.Where(e => e.HumanConfirmed).Select(e => (DateTimeOffset?)e.ReviewedAt!.Value).Max();
                return new NistScopeView(s.Id, ScopeName(s), s.Description, total,
                    states.Count(x => x == NistSubcategoryStates.Evaluated),
                    states.Count(x => x == NistSubcategoryStates.NotApplicable),
                    states.Count(x => x == NistSubcategoryStates.InProgress),
                    withEvidence, last, progressCycle?.Id,
                    states.Count(x => x == NistSubcategoryStates.PendingConfirmation));
            }).ToList();

        var lastReviewed = lastAny.Where(d => d.HasValue).Max();
        return new NistAssessmentView(a.Id, a.Name, a.Description, a.Status.ToString(), a.StartDate, a.EndDate,
            a.MethodologyVersion, fvName, a.CreatedAt, lastReviewed, scopeViews, cycles, progressCycle?.Id);
    }

    private async Task<NistSubcategoryDetailView> BuildDetailAsync(NistScopeContext ctx, NistSubcategory sub, CancellationToken ct)
    {
        var category = ctx.Catalog.Functions.SelectMany(f => f.Categories).First(c => c.Id == sub.CategoryId);
        var fn = ctx.Catalog.Functions.First(f => f.Id == category.FunctionId);
        var language = _language?.Get(sub.Code);
        var eval = ctx.Evaluation(sub.Id);
        var linked = await ActiveEvidenceAsync(ctx.Scope.Id, ctx.Cycle.Id, sub.Code, ct);
        var available = await AvailableEvidenceAsync(sub.Code, linked, ct);

        NistPostureReadingView? posture = null;
        var state = await _db.TenantControlStates.AsNoTracking().FirstOrDefaultAsync(s => s.SubcategoryId == sub.Id, ct);
        if (state is not null)
            posture = new NistPostureReadingView(state.Status.ToString(), state.LastVerdictSource.ToString(),
                state.CurrentScore, sub.MaxScorePoints, state.LastEvaluatedAt);

        var activeUsers = await ActiveUserIdsAsync(_db, ct);
        var assisted = await NistAssistProvenance.LoadAsync(_db, ctx.Cycle.Id, ctx.Scope.Id, ct);
        var procedures = ctx.Procedures.Where(p => p.SubcategoryCode == sub.Code)
            .OrderBy(p => p.Method).ThenBy(p => p.CreatedAt.UtcTicks).ThenBy(p => p.Id)
            .Select(p => NistWorkService.ProcedureView(p) with { AssistedFrom = NistAssistProvenance.ProcedureField(assisted, p) })
            .ToList();
        var findings = ctx.Findings.Where(f => f.SubcategoryCode == sub.Code).ToList();
        var plans = await PlansByFindingAsync(_db, findings.Select(f => f.Id).ToList(), ct);
        var findingViews = findings
            .OrderByDescending(f => f.CreatedAt.UtcTicks).ThenBy(f => f.Id)
            .Select(f =>
            {
                var plan = CurrentPlan(plans.TryGetValue(f.Id, out var l) ? l : null);
                return NistWorkService.FindingView(f, ctx.Cycle.Name, ScopeName(ctx.Scope), TitleOf(sub), plan)
                    with { AssistedFields = NistAssistProvenance.FindingFields(assisted, f, plan) };
            })
            .ToList();

        return new NistSubcategoryDetailView(
            ctx.Assessment.Id, ctx.Scope.Id, sub.Code, fn.Code, fn.Name, category.Code, category.Name,
            language?.Title ?? sub.Code,
            language?.Summary ?? "",
            language?.Impact ?? "",
            language?.InitialAction ?? "",
            sub.Description,
            string.IsNullOrWhiteSpace(sub.ImplementationExamples) ? null : sub.ImplementationExamples,
            eval is null ? null : EvaluationView(eval, linked.Count, activeUsers)
                with { AssistedFields = assisted.For(NistAssistTarget.Evaluation, eval.Id, NistAssistProvenance.EvaluationTexts(eval)) },
            linked.Select(EvidenceView).ToList(),
            available,
            posture,
            ctx.Catalog.MaturityLevels.OrderBy(l => l.Level).Select(l => new NistMaturityLevelView(l.Level, l.Name, l.Description)).ToList(),
            ctx.Assessment.MethodologyVersion,
            ctx.Cycle.Id,
            ctx.Cycle.Name,
            ctx.Cycle.Status.ToString(),
            procedures,
            findingViews,
            await ReferenceAsync(ctx, sub, ct),
            FindingBlockedReason(ctx.Cycle, eval, ctx.Procedures.Where(p => p.SubcategoryCode == sub.Code)));
    }

    /// <summary>A mesma subcategoria na rodada de origem (referência ou rascunho) — leitura, nunca a avaliação desta rodada.</summary>
    private async Task<NistCycleReferenceView?> ReferenceAsync(NistScopeContext ctx, NistSubcategory sub, CancellationToken ct)
    {
        if (ctx.Cycle.SeedFromCycleId is not { } sourceId || ctx.Cycle.SeedMode == NistCycleSeedMode.None) return null;
        var source = await _db.NistCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceId, ct);
        if (source is null) return null;
        var e = await _db.Evaluations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CycleId == sourceId && x.AssessmentScopeId == ctx.Scope.Id && x.SubcategoryId == sub.Id, ct);
        var evidence = await _db.Evidence.AsNoTracking().CountAsync(x => x.CycleId == sourceId && x.AssessmentScopeId == ctx.Scope.Id
            && x.SubcategoryCode == sub.Code && x.RemovedAt == null, ct);
        var performed = await _db.NistProcedures.AsNoTracking().CountAsync(p => p.CycleId == sourceId && p.AssessmentScopeId == ctx.Scope.Id
            && p.SubcategoryCode == sub.Code && p.RemovedAt == null && p.Status == NistProcedureStatus.Performed, ct);
        var findings = await _db.NistFindings.AsNoTracking().CountAsync(f => f.CycleId == sourceId && f.AssessmentScopeId == ctx.Scope.Id
            && f.SubcategoryCode == sub.Code, ct);
        return new NistCycleReferenceView(source.Id, source.Name, StateOf(e, evidence), e?.CurrentLevel, e?.TargetLevel,
            e?.NotApplicable ?? false, e?.Rationale, e?.Gaps, e?.HumanConfirmed == true ? e.ReviewedByName : null,
            e?.HumanConfirmed == true ? e.ReviewedAt : null, evidence, performed, findings);
    }

    private async Task<List<Evidence>> ActiveEvidenceAsync(Guid scopeId, Guid cycleId, string code, CancellationToken ct)
    {
        var rows = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryCode == code && e.RemovedAt == null)
            .ToListAsync(ct);
        return rows.OrderByDescending(e => e.CreatedAt.UtcTicks).ThenBy(e => e.Id).ToList();
    }

    /// <summary>
    /// Evidência que a plataforma JÁ tem e que é pertinente à subcategoria — oferecida, nunca vinculada sozinha. Cada
    /// origem declara o critério: mapeamento explícito do catálogo KNIGHT, trecho literal validado (ou confirmação do
    /// analista) no documento, inventário para ID.AM.
    /// </summary>
    private async Task<List<NistAvailableEvidenceView>> AvailableEvidenceAsync(string code, List<Evidence> linked, CancellationToken ct)
    {
        var result = new List<NistAvailableEvidenceView>();
        bool IsLinked(EvidenceOriginKind kind, string? reference) =>
            linked.Any(e => e.OriginKind == kind && (kind == EvidenceOriginKind.AssetInventory || e.OriginRef == reference));

        // ---- KNIGHT: execução concluída MAIS RECENTE de cada fonte; só controles com resultado avaliado ----
        var runs = await _db.KnightAssessmentRuns.AsNoTracking()
            .Where(r => r.Status == KnightRunStatus.Completed && r.SourceType != KnightSourceType.Consolidated)
            .Select(r => new { r.Id, r.SourceType, r.Mode, r.Source, r.SourceState, r.CatalogVersion, r.CompletedAt, r.StartedAt, r.IdentityAcquisitionId })
            .ToListAsync(ct);
        var latestRuns = runs
            .GroupBy(r => (r.SourceType, r.Mode))
            .Select(g => g.OrderByDescending(r => (r.CompletedAt ?? r.StartedAt).UtcTicks).ThenBy(r => r.Id).First())
            .ToList();
        if (latestRuns.Count > 0)
        {
            var runIds = latestRuns.Select(r => r.Id).ToList();
            var indicators = await _db.KnightIndicatorResults.AsNoTracking()
                .Where(i => runIds.Contains(i.RunId))
                .Select(i => new { i.RunId, i.IndicatorId, i.Title, i.Status, i.NistCodes, i.CollectedAt })
                .ToListAsync(ct);
            foreach (var i in indicators
                         .Where(i => i.NistCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
                         .OrderBy(i => i.IndicatorId, StringComparer.Ordinal))
            {
                var run = latestRuns.First(r => r.Id == i.RunId);
                var evaluated = EvaluatedKnightStatuses.Contains(i.Status);
                var reference = $"{run.Id}/{i.IndicatorId}";
                result.Add(new NistAvailableEvidenceView(
                    nameof(EvidenceOriginKind.KnightIndicator),
                    $"{i.IndicatorId} — {i.Title}",
                    $"AEGIS KNIGHT · {run.Source}",
                    KnightScopeOf(run.SourceType, run.Mode, run.SourceState, run.CatalogVersion, run.IdentityAcquisitionId),
                    run.CompletedAt ?? i.CollectedAt,
                    KnightStatusLabel(i.Status),
                    $"Mapeamento explícito do catálogo do KNIGHT ({run.CatalogVersion}) para {code}.",
                    evaluated
                        ? "Evidência técnica de apoio: cobre um aspecto técnico e não comprova sozinha o resultado organizacional."
                        : "Sem resultado avaliado nesta execução — não pode ser vinculado.",
                    null, run.Id, i.IndicatorId,
                    run.Mode == KnightAssessmentMode.Demo,
                    IsLinked(EvidenceOriginKind.KnightIndicator, reference)));
            }
        }

        // ---- Documentos: mapeamento com trecho literal validado OU confirmado pelo analista ----
        var mappings = await _db.DocumentControlMappings.AsNoTracking()
            .Where(m => m.SubcategoryCode == code && (m.EvidenceQuote != null || m.AnalystConfirmed))
            .Select(m => new { m.GovernanceDocumentId, m.AnalystConfirmed, Literal = m.EvidenceQuote != null })
            .ToListAsync(ct);
        if (mappings.Count > 0)
        {
            var docIds = mappings.Select(m => m.GovernanceDocumentId).Distinct().ToList();
            var docs = await _db.GovernanceDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Title, d.Type, d.Status, d.DocumentDate, d.CreatedAt })
                .ToListAsync(ct);
            foreach (var d in docs.OrderBy(d => d.Title, StringComparer.CurrentCulture))
            {
                var m = mappings.Where(x => x.GovernanceDocumentId == d.Id).ToList();
                var criterion = m.Any(x => x.AnalystConfirmed)
                    ? "Mapeamento do documento para esta subcategoria confirmado pelo analista."
                    : "Trecho literal do documento validado para esta subcategoria (análise documental).";
                result.Add(new NistAvailableEvidenceView(
                    nameof(EvidenceOriginKind.GovernanceDocument),
                    d.Title,
                    $"Biblioteca de documentos · {DocumentTypeLabel(d.Type)}",
                    $"Situação do documento: {DocumentStatusLabel(d.Status)}",
                    d.DocumentDate is { } dd ? new DateTimeOffset(dd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : d.CreatedAt,
                    DocumentStatusLabel(d.Status),
                    criterion,
                    d.Status == GovernanceStatus.Vigente ? null : "O documento não está vigente.",
                    d.Id, null, null, false,
                    IsLinked(EvidenceOriginKind.GovernanceDocument, d.Id.ToString())));
            }
        }

        // ---- Inventário: só para gestão de ativos (ID.AM) ----
        if (code.StartsWith("ID.AM", StringComparison.Ordinal))
        {
            var assets = await _db.Assets.AsNoTracking().Where(a => a.IsActive)
                .Select(a => new { a.Category, a.DiscoverySource }).ToListAsync(ct);
            result.Add(new NistAvailableEvidenceView(
                nameof(EvidenceOriginKind.AssetInventory),
                "Inventário de ativos do AEGIS",
                "Inventário de ativos do AEGIS",
                InventoryScopeOf(assets.Select(a => (a.Category, a.DiscoverySource)).ToList()),
                null,
                assets.Count == 0 ? "Sem ativos inventariados" : $"{assets.Count} ativo(s) ativo(s)",
                "Gestão de ativos (ID.AM): o inventário mostra o que está registrado.",
                "Não comprova sozinho que o inventário está completo nem que é mantido.",
                null, null, null, false,
                IsLinked(EvidenceOriginKind.AssetInventory, null)));
        }

        return result;
    }

    private NistSubcategoryRowView RowOf(NistSubcategory s, NistScopeContext ctx)
    {
        var e = ctx.Evaluation(s.Id);
        var evidence = ctx.EvidenceCount(s.Code);
        var procedures = ctx.Procedures.Where(p => p.SubcategoryCode == s.Code).ToList();
        return new NistSubcategoryRowView(s.Code, TitleOf(s), StateOf(e, evidence),
            e?.CurrentLevel, e?.TargetLevel, ConfirmedGap(e) ?? (e is { HumanConfirmed: false } ? e.Gap : null),
            e?.OwnerName, evidence, e is { HumanConfirmed: true } ? e.ReviewedAt : null,
            e is null ? NistReviewStates.None : ReviewStateOf(e),
            procedures.Count(p => p.Status is NistProcedureStatus.Planned or NistProcedureStatus.InProgress),
            procedures.Count(p => p.Status == NistProcedureStatus.Performed),
            ctx.Findings.Count(f => f.SubcategoryCode == s.Code && f.Status == NistFindingStatus.Open),
            e?.AssessorName, e?.ReviewerName);
    }

    internal string TitleOf(NistSubcategory s) => _language?.Get(s.Code)?.Title ?? s.Code;

    private static NistProfileScoreView ProfileView(ProfileScore p, int evaluated) =>
        new(p.RefCode, p.Current, p.Target, p.Gap, p.Subcategories, p.WithCurrent, p.WithTarget, p.WithGap, p.NotApplicable, evaluated);

    private static NistEvaluationView EvaluationView(SubcategoryEvaluation e, int evidenceCount, ISet<Guid> activeUsers) => new(
        e.Id, StateOf(e, evidenceCount), e.CurrentLevel, e.TargetLevel, e.Gap, e.NotApplicable,
        e.CurrentComments, e.TargetComments, e.Rationale, e.Gaps, e.RiskImpact, e.ImprovementGuidance, e.OwnerName,
        e.EvaluatedBy.ToString(), e.HumanConfirmed ? e.ReviewedByName : null, e.HumanConfirmed ? e.ReviewedAt : null, e.Version,
        e.ContentOrigin.ToString(), e.OriginNote, e.HumanConfirmed,
        ResponsibleView(e.OwnerUserId, e.OwnerName, e.OwnerIsExternal, e.OwnerContact, activeUsers),
        e.AssessorUserId, e.AssessorName, e.ReviewerUserId, e.ReviewerName,
        ReviewStateOf(e), e.ReviewDecisionByName, e.ReviewDecisionAt, e.ReviewDecisionNote);

    internal static NistEvidenceView EvidenceView(Evidence e) => new(
        e.Id, e.OriginKind.ToString(), e.Type.ToString(), e.Title ?? "(sem título)", e.Notes, e.Uri, e.OriginRef, e.OriginLabel,
        e.OriginScope, e.CollectedAt, e.CreatedAt, e.RecordedByName);

    private static string? PersonName(RemediationActor actor) =>
        string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);

    private static string KnightScopeOf(KnightAssessmentRun run) =>
        KnightScopeOf(run.SourceType, run.Mode, run.SourceState, run.CatalogVersion, run.IdentityAcquisitionId);

    private static string KnightScopeOf(KnightSourceType source, KnightAssessmentMode mode, KnightSourceState state, string catalog, Guid? acquisition) =>
        $"Fonte {source}; {(mode == KnightAssessmentMode.Demo ? "demonstração (dados sintéticos)" : "coleta real")}; " +
        $"estado da coleta {state}; catálogo {catalog}" + (acquisition is { } a ? $"; aquisição ADM {a}" : "");

    private static string InventoryScopeOf(IReadOnlyList<(AssetCategory Category, AssetDiscoverySource Source)> assets)
    {
        if (assets.Count == 0) return "Nenhum ativo ativo no inventário.";
        var bySource = assets.GroupBy(a => a.Source).OrderBy(g => g.Key)
            .Select(g => $"{(g.Key switch { AssetDiscoverySource.Connector => "conector", AssetDiscoverySource.Import => "importação", _ => "manual" })} {g.Count()}");
        var byCategory = assets.GroupBy(a => a.Category).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
        return $"{assets.Count} ativo(s) ativo(s); origem: {string.Join(", ", bySource)}; categorias: {string.Join(", ", byCategory)}.";
    }

    internal static string KnightStatusLabel(KnightIndicatorStatus s) => s switch
    {
        KnightIndicatorStatus.Passed => "Aprovado",
        KnightIndicatorStatus.Exposed => "Reprovado",
        KnightIndicatorStatus.Mitigated => "Mitigado por controle compensatório",
        KnightIndicatorStatus.NotEvaluated => "Não avaliado",
        KnightIndicatorStatus.Error => "Erro de leitura",
        _ => "Não se aplica",
    };

    internal static string DocumentTypeLabel(GovernanceDocumentType t) => t switch
    {
        GovernanceDocumentType.Politica => "Política",
        GovernanceDocumentType.Norma => "Norma",
        GovernanceDocumentType.Diretriz => "Diretriz",
        GovernanceDocumentType.Procedimento => "Procedimento",
        GovernanceDocumentType.Contrato => "Contrato",
        _ => "Outro",
    };

    internal static string DocumentStatusLabel(GovernanceStatus s) => s switch
    {
        GovernanceStatus.Rascunho => "Rascunho",
        GovernanceStatus.Vigente => "Vigente",
        GovernanceStatus.EmRevisao => "Em revisão",
        GovernanceStatus.Expirado => "Expirado",
        _ => "Descontinuado",
    };

    private static string? NormalizeUri(string? uri)
    {
        var v = (uri ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length > 2000 || !Uri.TryCreate(v, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw new NistAssessmentValidationException("O link da evidência precisa ser um endereço http(s) completo.");
        return u.ToString();
    }
}
