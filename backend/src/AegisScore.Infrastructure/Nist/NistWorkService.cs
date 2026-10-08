using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-02] Procedimentos de avaliação, achados e planos de tratamento da jornada NIST.
///
/// <list type="bullet">
/// <item>procedimento: o PLANEJADO (método + procedimento) e, à parte, o RESULTADO (andamento, data, observação, conclusão
/// e evidências da mesma subcategoria, rodada e escopo); escolher um método não registra resultado algum;</item>
/// <item>achado: só nasce de lacuna DOCUMENTADA, por decisão do analista, com risco, impacto, severidade e prioridade
/// justificados; o contexto de origem é congelado;</item>
/// <item>plano: o MESMO plano de ação do produto (trilha, ciclo, execução, validação humana, reabertura, concorrência), com
/// origem NIST explícita — nenhum identificador do KNIGHT é inventado; concluir o plano não altera maturidade.</item>
/// </list>
/// </summary>
public sealed class NistWorkService : INistWorkService
{
    private static readonly JsonSerializerOptions OriginJson = new(JsonSerializerDefaults.Web);

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IRemediationService _remediation;
    private readonly IControlLanguageCatalog? _language;

    public NistWorkService(
        AegisScoreDbContext db, ITenantContext tenant, TimeProvider clock, IRemediationService remediation, IControlLanguageCatalog? language = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _remediation = remediation;
        _language = language;
    }

    // =============================================================================================
    //  Procedimentos
    // =============================================================================================

    public async Task<NistProcedureView> AddProcedureAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, AddNistProcedureCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var method = ParseEnum<NistTestMethod>(command.Method, "Método inválido (Examine, Interview ou Test).");
        var procedure = Required(command.Procedure, "Descreva o procedimento planejado.", MaxLongText, "O procedimento");
        if (procedure.Length < MinJustification)
            throw new NistAssessmentValidationException("Descreva o procedimento planejado (pelo menos 10 caracteres).");

        var now = _clock.GetUtcNow();
        var entity = new NistTestProcedure
        {
            AssessmentId = assessmentId,
            CycleId = cycleId,
            AssessmentScopeId = scopeId,
            SubcategoryCode = sub.Code,
            Method = method,
            Procedure = procedure,
            Status = NistProcedureStatus.Planned,
            CreatedByAccountId = actor.AccountId,
            CreatedByName = Name(actor),
            Version = 1,
            CreatedAt = now,
        };
        _db.NistProcedures.Add(entity);
        var changes = new List<NistFieldChange>();
        Diff(changes, "method", "método", null, NistLabels.Method(method.ToString()));
        Diff(changes, "procedure", "procedimento", null, procedure);
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Procedure", entity.Id, "Created",
            $"Procedimento planejado em {sub.Code} ({NistLabels.Method(method.ToString())}).", changes);
        await _db.SaveChangesAsync(ct);
        return ProcedureView(entity);
    }

    /// <summary>
    /// [AEGIS-NIST-AI-ASSIST-01] Planeja, numa única gravação, os procedimentos sugeridos que a PESSOA escolheu (podendo editar o
    /// texto). Entram como PLANEJADOS — planejar não comprova a realização — com a procedência registrada.
    /// </summary>
    public async Task<IReadOnlyList<NistProcedureView>> PlanProceduresFromAssistanceAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, PlanNistProceduresFromAssistanceCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var items = command.Procedures ?? Array.Empty<NistAssistedProcedureInput>();
        if (items.Count == 0) throw new NistAssessmentValidationException("Escolha pelo menos um procedimento sugerido para planejar.");
        if (items.Count > 6) throw new NistAssessmentValidationException("Planeje no máximo 6 procedimentos de uma vez.");
        var parsed = items.Select(i =>
        {
            var method = ParseEnum<NistTestMethod>(i.Method, "Método inválido (Examine, Interview ou Test).");
            var text = Required(i.Procedure, "Descreva o procedimento planejado.", MaxLongText, "O procedimento");
            if (text.Length < MinJustification) throw new NistAssessmentValidationException("Descreva o procedimento planejado (pelo menos 10 caracteres).");
            return (Method: method, Text: text);
        }).ToList();

        var (generation, stale) = await NistAssistProvenance.ResolveAsync(_db,
            new NistAssistanceReference(command.AssistanceId, null, command.AcknowledgeStale), NistAssistKind.Subcategory,
            assessmentId, cycleId, scopeId, sub.Code, null,
            async _ => (await new NistAssistContextBuilder(_db, _language).SubcategoryAsync(ctx, sub, ct)).Fingerprint, ct);
        var suggested = NistAssistProvenance.ApplicableOf(generation).Where(kv => kv.Key.StartsWith("procedure:", StringComparison.Ordinal))
            .Select(kv => kv.Value).ToList();
        if (suggested.Count == 0) throw new NistAssessmentValidationException("A sugestão não traz procedimentos para planejar.");

        var now = _clock.GetUtcNow();
        var created = new List<NistTestProcedure>();
        foreach (var (method, text) in parsed)
        {
            var entity = new NistTestProcedure
            {
                AssessmentId = assessmentId,
                CycleId = cycleId,
                AssessmentScopeId = scopeId,
                SubcategoryCode = sub.Code,
                Method = method,
                Procedure = text,
                Status = NistProcedureStatus.Planned,
                OriginNote = Truncate($"Proposto pela assistência de IA ({(generation.Mode == NistAssistEngineMode.Real ? "provedor autorizado" : "simulada")}) " +
                                      "e planejado pela pessoa. Planejar não comprova a realização.", 500),
                CreatedByAccountId = actor.AccountId,
                CreatedByName = Name(actor),
                Version = 1,
                CreatedAt = now,
            };
            _db.NistProcedures.Add(entity);
            created.Add(entity);
            var changes = new List<NistFieldChange>();
            Diff(changes, "method", "método", null, NistLabels.Method(method.ToString()));
            Diff(changes, "procedure", "procedimento", null, text);
            Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Procedure", entity.Id, "Created",
                $"Procedimento planejado em {sub.Code} ({NistLabels.Method(method.ToString())}) a partir de sugestão da assistência.", changes);
            var same = suggested.FirstOrDefault(s => NistAssistProvenance.Hash(s) == NistAssistProvenance.Hash(text));
            NistAssistProvenance.Record(_db, generation, stale, NistAssistTarget.Procedure, entity.Id,
                new[] { new NistAssistProvenance.Incorporated("procedure", "procedimento planejado", text) }, actor, now, suggestedOverride: same ?? "");
        }
        await SaveAsync(ct);
        return created.Select(ProcedureView).ToList();
    }

    public async Task<NistProcedureView> UpdateProcedureAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid procedureId, UpdateNistProcedureCommand command,
        RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var entity = await _db.NistProcedures.FirstOrDefaultAsync(p => p.Id == procedureId && p.CycleId == cycleId
            && p.AssessmentScopeId == scopeId && p.SubcategoryCode == sub.Code && p.RemovedAt == null, ct)
            ?? throw new NistAssessmentNotFoundException("Procedimento não encontrado nesta subcategoria e rodada.");
        if (entity.Version != command.ExpectedVersion)
            throw new NistAssessmentConflictException(
                $"Este procedimento foi alterado por outra pessoa (versão {entity.Version}, você enviou {command.ExpectedVersion}). Recarregue.");

        var status = ParseEnum<NistProcedureStatus>(command.Status, "Andamento inválido (Planned, InProgress, Performed ou NotPerformed).");
        var procedure = command.Procedure is null
            ? entity.Procedure
            : Required(command.Procedure, "Descreva o procedimento planejado.", MaxLongText, "O procedimento");
        var observation = Optional(command.Observation, MaxLongText, "A observação");
        NistProcedureOutcome? outcome = string.IsNullOrWhiteSpace(command.Outcome)
            ? null
            : ParseEnum<NistProcedureOutcome>(command.Outcome, "Conclusão inválida (Satisfactory, PartiallySatisfactory, Unsatisfactory ou Inconclusive).");
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        // PLANEJADO ≠ RESULTADO: só o realizado tem conclusão; a conclusão exige data e observação.
        switch (status)
        {
            case NistProcedureStatus.Performed:
                if (outcome is null) throw new NistAssessmentValidationException("Um procedimento realizado precisa da conclusão observada.");
                if (command.PerformedOn is not { } on) throw new NistAssessmentValidationException("Informe a data em que o procedimento foi realizado.");
                if (on > today.AddDays(1)) throw new NistAssessmentValidationException("A data de realização não pode estar no futuro.");
                if ((observation ?? "").Length < MinJustification)
                    throw new NistAssessmentValidationException("Registre o que foi observado (pelo menos 10 caracteres).");
                break;
            case NistProcedureStatus.NotPerformed:
                if (outcome is not null) throw new NistAssessmentValidationException("Um procedimento não realizado não tem conclusão.");
                if ((observation ?? "").Length < MinJustification)
                    throw new NistAssessmentValidationException("Explique por que o procedimento não foi realizado (pelo menos 10 caracteres).");
                break;
            default:
                if (outcome is not null)
                    throw new NistAssessmentValidationException("Só um procedimento realizado tem conclusão: escolher o método não comprova o teste.");
                if (command.PerformedOn is not null)
                    throw new NistAssessmentValidationException("A data de realização só existe para um procedimento realizado.");
                break;
        }

        var evidenceIds = command.EvidenceIds is null ? entity.EvidenceIds : await ValidEvidenceAsync(scopeId, cycleId, sub.Code, command.EvidenceIds, ct);

        var changes = new List<NistFieldChange>();
        Diff(changes, "procedure", "procedimento", entity.Procedure, procedure);
        Diff(changes, "status", "andamento", NistLabels.ProcedureStatus(entity.Status.ToString()), NistLabels.ProcedureStatus(status.ToString()));
        Diff(changes, "outcome", "conclusão", entity.Outcome is null ? null : NistLabels.Outcome(entity.Outcome.ToString()),
            outcome is null ? null : NistLabels.Outcome(outcome.ToString()));
        Diff(changes, "observation", "observação", entity.Observation, observation);
        Diff(changes, "performedOn", "data de realização", entity.PerformedOn?.ToString("yyyy-MM-dd"),
            status == NistProcedureStatus.Performed ? command.PerformedOn?.ToString("yyyy-MM-dd") : null);
        Diff(changes, "evidence", "evidências citadas", string.Join(", ", entity.EvidenceIds.Order()), string.Join(", ", evidenceIds.Order()));
        if (changes.Count == 0) return ProcedureView(entity);

        var resultChanged = changes.Any(c => c.Field is "status" or "outcome" or "observation" or "performedOn");
        var now = _clock.GetUtcNow();
        entity.Procedure = procedure;
        entity.Status = status;
        entity.Outcome = outcome;
        entity.Observation = observation;
        entity.PerformedOn = status == NistProcedureStatus.Performed ? command.PerformedOn : null;
        entity.EvidenceIds = evidenceIds.ToList();
        if (resultChanged)
        {
            entity.ResultRecordedByAccountId = actor.AccountId;
            entity.ResultRecordedByName = Name(actor);
            entity.ResultRecordedAt = now;
        }
        // Um procedimento herdado passa a ser trabalho DESTA rodada assim que alguém o altera.
        entity.ContentOrigin = NistContentOrigin.Analyst;
        entity.Version++;
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Procedure", entity.Id,
            resultChanged ? "ResultRecorded" : "Updated",
            resultChanged
                ? $"Resultado de procedimento em {sub.Code}: {NistLabels.ProcedureStatus(status.ToString())}" +
                  (outcome is null ? "." : $" — {NistLabels.Outcome(outcome.ToString())}.")
                : $"Procedimento de {sub.Code} alterado.",
            changes);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("Este procedimento foi alterado por outra pessoa enquanto a sua gravação estava em curso.");
        }
        return ProcedureView(entity);
    }

    public async Task RemoveProcedureAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid procedureId, int expectedVersion, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var entity = await _db.NistProcedures.FirstOrDefaultAsync(p => p.Id == procedureId && p.CycleId == cycleId
            && p.AssessmentScopeId == scopeId && p.SubcategoryCode == sub.Code && p.RemovedAt == null, ct)
            ?? throw new NistAssessmentNotFoundException("Procedimento não encontrado nesta subcategoria e rodada.");
        if (entity.Version != expectedVersion)
            throw new NistAssessmentConflictException("Este procedimento foi alterado por outra pessoa. Recarregue antes de retirar.");
        var now = _clock.GetUtcNow();
        entity.RemovedAt = now;
        entity.RemovedByName = Name(actor);
        entity.Version++;
        var changes = new List<NistFieldChange>();
        Diff(changes, "procedure", "procedimento", $"{NistLabels.Method(entity.Method.ToString())}: {entity.Procedure}", null);
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Procedure", entity.Id, "Removed",
            $"Procedimento retirado de {sub.Code} (o registro permanece na trilha).", changes);
        await _db.SaveChangesAsync(ct);
    }

    // =============================================================================================
    //  Achados
    // =============================================================================================

    public async Task<NistFindingView> CreateFindingAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, CreateNistFindingCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var sub = ctx.FindSubcategory(code);
        var eval = ctx.Evaluation(sub.Id);
        var procedures = ctx.Procedures.Where(p => p.SubcategoryCode == sub.Code).ToList();
        if (FindingBlockedReason(ctx.Cycle, eval, procedures) is { } blocked)
            throw new NistAssessmentValidationException(blocked);

        if (command.Plan?.Assistance is not null)
            throw new NistAssessmentValidationException("A sugestão de tratamento é de um achado já registrado: registre o achado e crie o plano a partir dele.");
        var fields = ValidateFinding(command.Title, command.Condition, command.Risk, command.Impact, command.Severity, command.SeverityRationale,
            command.Priority, command.PriorityRationale, command.Recommendation);
        var evidenceIds = await ValidEvidenceAsync(scopeId, cycleId, sub.Code, command.EvidenceIds ?? Array.Empty<Guid>(), ct);

        var now = _clock.GetUtcNow();
        var origin = new NistFindingOriginContext(
            NistFindingOriginContext.SchemaV1, assessmentId, cycleId, ctx.Cycle.Name, scopeId, ScopeName(ctx.Scope), sub.Code, TitleOf(sub),
            eval!.Version, eval.CurrentLevel, eval.TargetLevel, eval.Gap, eval.Gaps,
            procedures.Where(p => p.Status == NistProcedureStatus.Performed
                                  && p.Outcome is NistProcedureOutcome.Unsatisfactory or NistProcedureOutcome.PartiallySatisfactory)
                .Select(p => $"{NistLabels.Method(p.Method.ToString())} — {NistLabels.Outcome(p.Outcome?.ToString())}: {Truncate(p.Observation ?? "", 300)}")
                .ToList(),
            now);

        var finding = new NistFinding
        {
            AssessmentId = assessmentId,
            CycleId = cycleId,
            AssessmentScopeId = scopeId,
            SubcategoryCode = sub.Code,
            Title = fields.Title,
            Condition = fields.Condition,
            Risk = fields.Risk,
            Impact = fields.Impact,
            Severity = fields.Severity,
            SeverityRationale = fields.SeverityRationale,
            Priority = fields.Priority,
            PriorityRationale = fields.PriorityRationale,
            Recommendation = fields.Recommendation,
            EvidenceIds = evidenceIds.ToList(),
            Status = NistFindingStatus.Open,
            OriginContextJson = JsonSerializer.Serialize(origin, OriginJson),
            CreatedByAccountId = actor.AccountId,
            CreatedByName = Name(actor),
            Version = 1,
            CreatedAt = now,
        };
        _db.NistFindings.Add(finding);
        var changes = new List<NistFieldChange>();
        Diff(changes, "title", "problema", null, finding.Title);
        Diff(changes, "severity", "severidade", null, NistLabels.Severity(finding.Severity.ToString()));
        Diff(changes, "priority", "prioridade", null, NistLabels.Priority(finding.Priority.ToString()));
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Finding", finding.Id, "Created",
            $"Achado registrado em {sub.Code}: {finding.Title}.", changes);

        if (command.Plan is { } plan)
        {
            // O achado (ainda não gravado) e o plano entram na MESMA gravação: ou os dois existem, ou nenhum.
            try
            {
                await CreatePlanForAsync(finding, plan, actor, ct);
            }
            catch
            {
                _db.ChangeTracker.Clear();   // nada do achado sobra pendurado no contexto
                throw;
            }
        }
        else
        {
            await SaveAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, finding.Id, ct);
    }

    public async Task<NistFindingView> UpdateFindingAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, UpdateNistFindingCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var cycle = await LoadCycleAsync(_db, assessmentId, cycleId, ct);
        EnsureOpen(cycle);
        (NistAiAssistance Generation, bool Stale)? assisted = command.Assistance is { } reference
            ? await ResolveFindingAssistanceAsync(assessmentId, cycleId, scopeId, findingId, reference, new[] { "recommendation" }, ct)
            : null;
        var finding = await LoadFindingForWriteAsync(assessmentId, cycleId, scopeId, findingId, command.ExpectedVersion, ct);

        var fields = ValidateFinding(
            command.Title ?? finding.Title, command.Condition ?? finding.Condition, command.Risk ?? finding.Risk, command.Impact ?? finding.Impact,
            command.Severity ?? finding.Severity.ToString(), command.SeverityRationale ?? finding.SeverityRationale,
            command.Priority ?? finding.Priority.ToString(), command.PriorityRationale ?? finding.PriorityRationale,
            command.Recommendation ?? finding.Recommendation);
        var evidenceIds = command.EvidenceIds is null
            ? finding.EvidenceIds
            : await ValidEvidenceAsync(scopeId, cycleId, finding.SubcategoryCode, command.EvidenceIds, ct);

        var changes = new List<NistFieldChange>();
        Diff(changes, "title", "problema", finding.Title, fields.Title);
        Diff(changes, "condition", "condição observada", finding.Condition, fields.Condition);
        Diff(changes, "risk", "risco", finding.Risk, fields.Risk);
        Diff(changes, "impact", "impacto", finding.Impact, fields.Impact);
        Diff(changes, "severity", "severidade", NistLabels.Severity(finding.Severity.ToString()), NistLabels.Severity(fields.Severity.ToString()));
        Diff(changes, "severityRationale", "justificativa da severidade", finding.SeverityRationale, fields.SeverityRationale);
        Diff(changes, "priority", "prioridade", NistLabels.Priority(finding.Priority.ToString()), NistLabels.Priority(fields.Priority.ToString()));
        Diff(changes, "priorityRationale", "justificativa da prioridade", finding.PriorityRationale, fields.PriorityRationale);
        Diff(changes, "recommendation", "recomendação", finding.Recommendation, fields.Recommendation);
        Diff(changes, "evidence", "evidências citadas", string.Join(", ", finding.EvidenceIds.Order()), string.Join(", ", evidenceIds.Order()));
        if (changes.Count == 0) return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);

        finding.Title = fields.Title;
        finding.Condition = fields.Condition;
        finding.Risk = fields.Risk;
        finding.Impact = fields.Impact;
        finding.Severity = fields.Severity;
        finding.SeverityRationale = fields.SeverityRationale;
        finding.Priority = fields.Priority;
        finding.PriorityRationale = fields.PriorityRationale;
        finding.Recommendation = fields.Recommendation;
        finding.EvidenceIds = evidenceIds.ToList();
        finding.Version++;
        Audit(_db, actor, _clock.GetUtcNow(), assessmentId, cycleId, scopeId, finding.SubcategoryCode, "Finding", finding.Id, "Updated",
            $"Achado alterado: {finding.Title}.", changes);
        if (assisted is { } a)
            NistAssistProvenance.Record(_db, a.Generation, a.Stale, NistAssistTarget.Finding, finding.Id,
                new[] { new NistAssistProvenance.Incorporated("recommendation", "recomendação", finding.Recommendation) }, actor, _clock.GetUtcNow());
        await SaveAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
    }

    public async Task<NistFindingView> SetFindingStatusAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, SetNistFindingStatusCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var finding = await LoadFindingForWriteAsync(assessmentId, cycleId, scopeId, findingId, command.ExpectedVersion, ct);
        var target = ParseEnum<NistFindingStatus>(command.Status, "Situação inválida (Open, RiskAccepted ou Closed).");
        var note = Optional(command.Note, MaxDescription, "A justificativa");
        if (target != NistFindingStatus.Open && (note ?? "").Length < MinJustification)
            throw new NistAssessmentValidationException(target == NistFindingStatus.RiskAccepted
                ? "Justifique a aceitação do risco (pelo menos 10 caracteres): quem aceitou, por quê e até quando."
                : "Justifique o encerramento (pelo menos 10 caracteres) — por exemplo, a reavaliação que o sustenta.");
        if (finding.Status == target) return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);

        var now = _clock.GetUtcNow();
        var changes = new List<NistFieldChange>();
        Diff(changes, "status", "situação do achado", NistLabels.FindingStatus(finding.Status.ToString()), NistLabels.FindingStatus(target.ToString()));
        Diff(changes, "statusNote", "justificativa", finding.StatusNote, note);
        finding.Status = target;
        finding.StatusNote = note;
        finding.StatusChangedAt = now;
        finding.StatusChangedByName = Name(actor);
        finding.Version++;
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, finding.SubcategoryCode, "Finding", finding.Id, "StatusChanged",
            $"Achado \"{finding.Title}\": {NistLabels.FindingStatus(target.ToString()).ToLowerInvariant()}. A maturidade não muda por isso.", changes);
        await SaveAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
    }

    public async Task<NistFindingView> GetFindingAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var finding = await _db.NistFindings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == findingId && f.AssessmentId == assessmentId
            && f.CycleId == cycleId && f.AssessmentScopeId == scopeId, ct)
            ?? throw new NistAssessmentNotFoundException("Achado não encontrado nesta avaliação, rodada e escopo.");
        return (await ViewsAsync(new[] { finding }, ct)).Single();
    }

    public async Task<IReadOnlyList<NistFindingView>> ListFindingsAsync(Guid assessmentId, NistFindingFilter filter, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        if (!await _db.Assessments.AsNoTracking().AnyAsync(a => a.Id == assessmentId, ct))
            throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        var q = _db.NistFindings.AsNoTracking().Where(f => f.AssessmentId == assessmentId);
        if (filter.CycleId is { } c) q = q.Where(f => f.CycleId == c);
        if (filter.ScopeId is { } s) q = q.Where(f => f.AssessmentScopeId == s);
        if (!string.IsNullOrWhiteSpace(filter.SubcategoryCode))
        {
            var code = filter.SubcategoryCode.Trim().ToUpperInvariant();
            q = q.Where(f => f.SubcategoryCode == code);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = ParseEnum<NistFindingStatus>(filter.Status, "Situação inválida (Open, RiskAccepted ou Closed).");
            q = q.Where(f => f.Status == status);
        }
        var rows = await q.ToListAsync(ct);
        var views = await ViewsAsync(rows, ct);
        return views
            .OrderBy(v => v.Status == "Open" ? 0 : 1)
            .ThenBy(v => NistLabels.SeverityRank(v.Severity))
            .ThenByDescending(v => v.CreatedAt.UtcTicks)
            .ToList();
    }

    // =============================================================================================
    //  Planos (o mecanismo único de planos, com origem NIST)
    // =============================================================================================

    public async Task<NistFindingView> CreatePlanAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, NistPlanInput input, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(input);
        var finding = await _db.NistFindings.FirstOrDefaultAsync(f => f.Id == findingId && f.AssessmentId == assessmentId
            && f.CycleId == cycleId && f.AssessmentScopeId == scopeId, ct)
            ?? throw new NistAssessmentNotFoundException("Achado não encontrado nesta avaliação, rodada e escopo.");
        if (finding.Status != NistFindingStatus.Open)
            throw new NistAssessmentValidationException("Só um achado aberto recebe plano de tratamento novo. Reabra o achado antes.");
        if (input.Assistance is not { } reference)
        {
            await CreatePlanForAsync(finding, input, actor, ct);
        }
        else
        {
            var assisted = await ResolveFindingAssistanceAsync(assessmentId, cycleId, scopeId, findingId, reference, new[] { "proposedAction" }, ct);
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var planId = await CreatePlanForAsync(finding, input, actor, ct);
            var proposed = await _db.ActionPlans.AsNoTracking().Where(p => p.Id == planId).Select(p => p.Description).FirstAsync(ct);
            NistAssistProvenance.Record(_db, assisted.Generation, assisted.Stale, NistAssistTarget.Plan, planId,
                new[] { new NistAssistProvenance.Incorporated("proposedAction", "ação proposta do plano", proposed) }, actor, _clock.GetUtcNow());
            await SaveAsync(ct);
            await tx.CommitAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
    }

    public async Task<NistFindingView> UpdatePlanAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, UpdateNistPlanCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        await EnsurePlanOfFindingAsync(assessmentId, cycleId, scopeId, findingId, planId, ct);
        ActionPlanStatus? status = string.IsNullOrWhiteSpace(command.Status)
            ? null
            : Enum.TryParse<ActionPlanStatus>(command.Status.Trim(), true, out var st) && Enum.IsDefined(st) && !int.TryParse(command.Status, out _)
                ? st
                : throw new NistAssessmentValidationException("Etapa inválida (Aberto, EmAndamento, AguardandoValidacao ou Concluido).");
        await Remediate(() => _remediation.UpdateAsync(planId, new UpdateActionPlanCommand(
            command.ExpectedVersion, command.Title, command.ProposedAction, null, command.ResponsibleArea, command.DueDate, status,
            command.Responsible is { } r ? new ActionPlanResponsible(r.UserId, r.Name, r.IsExternal, r.Contact) : null), actor, ct));
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
    }

    public async Task<NistFindingView> RecordPlanExecutionAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, RecordExecutionCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        await EnsurePlanOfFindingAsync(assessmentId, cycleId, scopeId, findingId, planId, ct);
        await Remediate(() => _remediation.RecordExecutionAsync(planId, command, actor, ct));
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
    }

    public async Task<NistFindingView> ValidatePlanAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, ValidateActionPlanCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        await EnsurePlanOfFindingAsync(assessmentId, cycleId, scopeId, findingId, planId, ct);
        if (command.ValidationRunId is not null)
            throw new NistAssessmentValidationException("Um plano de achado NIST é validado por pessoa, com evidência referenciada — não por coleta do KNIGHT.");
        await Remediate(() => _remediation.ValidateAsync(planId, command, actor, ct));
        _db.ChangeTracker.Clear();
        return await GetFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
    }

    // =============================================================================================
    //  Apoio
    // =============================================================================================

    private sealed record FindingFields(string Title, string Condition, string Risk, string Impact, SeverityLevel Severity,
        string SeverityRationale, NistFindingPriority Priority, string PriorityRationale, string Recommendation);

    private static FindingFields ValidateFinding(string? title, string? condition, string? risk, string? impact, string? severity,
        string? severityRationale, string? priority, string? priorityRationale, string? recommendation)
    {
        var sev = ParseEnum<SeverityLevel>(severity, "Severidade inválida (Critical, High, Medium ou Low).");
        if (sev == SeverityLevel.Informational)
            throw new NistAssessmentValidationException("Um achado tem severidade Baixa, Média, Alta ou Crítica — informativo não é achado.");
        return new FindingFields(
            Required(title, "Descreva o problema (título do achado).", 200, "O título do achado"),
            Required(condition, "Descreva a condição observada.", MaxLongText, "A condição observada"),
            Required(risk, "Fundamente o risco: o que pode acontecer por causa da condição.", MaxDescription, "O risco"),
            Required(impact, "Descreva o impacto, no limite do que a condição permite afirmar.", MaxDescription, "O impacto"),
            sev,
            Justified(severityRationale, "a severidade"),
            ParseEnum<NistFindingPriority>(priority, "Prioridade inválida (Low, Medium, High ou Urgent)."),
            Justified(priorityRationale, "a prioridade"),
            Required(recommendation, "Registre a recomendação.", MaxLongText, "A recomendação"));
    }

    /// <summary>As evidências citadas precisam estar VIGENTES na mesma subcategoria, rodada e escopo.</summary>
    private async Task<IReadOnlyList<Guid>> ValidEvidenceAsync(Guid scopeId, Guid cycleId, string code, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) return distinct;
        var found = await _db.Evidence.AsNoTracking()
            .Where(e => distinct.Contains(e.Id) && e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryCode == code && e.RemovedAt == null)
            .Select(e => e.Id).ToListAsync(ct);
        if (found.Count != distinct.Count)
            throw new NistAssessmentValidationException("Cite só evidências vigentes desta subcategoria, nesta rodada e escopo.");
        return distinct;
    }

    /// <summary>
    /// O tratamento segue além da rodada: um achado de uma rodada encerrada continua recebendo plano, execução e validação —
    /// encerrar a rodada congela a AVALIAÇÃO, não o trabalho de correção.
    /// </summary>
    private async Task<Guid> CreatePlanForAsync(NistFinding finding, NistPlanInput plan, RemediationActor actor, CancellationToken ct)
    {
        var created = await Remediate(() => _remediation.CreateForNistFindingAsync(new CreateNistFindingActionPlanCommand(
            finding.Id, finding.AssessmentId, finding.CycleId, finding.AssessmentScopeId, finding.SubcategoryCode, finding.Title,
            plan.Title, plan.ProposedAction ?? finding.Recommendation,
            plan.Responsible is { } r ? new ActionPlanResponsible(r.UserId, r.Name, r.IsExternal, r.Contact) : null,
            plan.ResponsibleArea, plan.DueDate), actor, ct));
        return created.Id;
    }

    /// <summary>
    /// [AEGIS-NIST-AI-ASSIST-01] A sugestão de TRATAMENTO deste achado, ainda válida para o contexto atual (senão 409, a menos que a
    /// pessoa declare a revisão). Não cria, não altera e não conclui plano: só confere o que a pessoa vai gravar.
    /// </summary>
    private async Task<(NistAiAssistance Generation, bool Stale)> ResolveFindingAssistanceAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, NistAssistanceReference reference, string[] allowed, CancellationToken ct)
    {
        var fields = NistAssistProvenance.CheckFields(reference, allowed);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var row = ctx.Findings.FirstOrDefault(f => f.Id == findingId)
                  ?? throw new NistAssessmentNotFoundException("Achado não encontrado nesta avaliação, rodada e escopo.");
        var sub = ctx.FindSubcategory(row.SubcategoryCode);
        var resolved = await NistAssistProvenance.ResolveAsync(_db, reference, NistAssistKind.Finding, assessmentId, cycleId, scopeId, row.SubcategoryCode, findingId,
            async g => (await new NistAssistContextBuilder(_db, _language).FindingAsync(ctx, sub, row, NistAssistSections.NormalizeFocus(g.Focus), ct)).Fingerprint, ct);
        NistAssistProvenance.RequireApplicable(resolved.Generation, fields);
        return resolved;
    }

    /// <summary>O plano precisa ser DESTE achado, desta avaliação, rodada e escopo — o link nomeia os quatro.</summary>
    private async Task EnsurePlanOfFindingAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, Guid planId, CancellationToken ct)
    {
        if (!await _db.NistFindings.AsNoTracking().AnyAsync(f => f.Id == findingId && f.AssessmentId == assessmentId
                && f.CycleId == cycleId && f.AssessmentScopeId == scopeId, ct))
            throw new NistAssessmentNotFoundException("Achado não encontrado nesta avaliação, rodada e escopo.");
        if (!await _db.ActionPlans.AsNoTracking().AnyAsync(p => p.Id == planId && p.OriginKind == ActionPlanOriginKind.NistFinding
                && p.OriginNistFindingId == findingId, ct))
            throw new NistAssessmentNotFoundException("Plano não encontrado para este achado.");
    }

    private async Task<NistFinding> LoadFindingForWriteAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, int expectedVersion, CancellationToken ct)
    {
        var finding = await _db.NistFindings.FirstOrDefaultAsync(f => f.Id == findingId && f.AssessmentId == assessmentId
            && f.CycleId == cycleId && f.AssessmentScopeId == scopeId, ct)
            ?? throw new NistAssessmentNotFoundException("Achado não encontrado nesta avaliação, rodada e escopo.");
        if (finding.Version != expectedVersion)
            throw new NistAssessmentConflictException(
                $"Este achado foi alterado por outra pessoa (versão {finding.Version}, você enviou {expectedVersion}). Recarregue.");
        return finding;
    }

    /// <summary>Traduz os erros do serviço de planos para a jornada NIST (mesmos status HTTP).</summary>
    private static async Task<T> Remediate<T>(Func<Task<T>> call)
    {
        try
        {
            var result = await call();
            return result ?? throw new NistAssessmentNotFoundException("Plano não encontrado.");
        }
        catch (ActionPlanConflictException ex)
        {
            throw new NistAssessmentConflictException(ex.Message);
        }
        catch (ActionPlanValidationException ex)
        {
            throw new NistAssessmentValidationException(ex.Message);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("O registro foi alterado por outra pessoa enquanto a sua gravação estava em curso. Recarregue.");
        }
    }

    private async Task<List<NistFindingView>> ViewsAsync(IReadOnlyCollection<NistFinding> findings, CancellationToken ct)
    {
        if (findings.Count == 0) return new List<NistFindingView>();
        var cycleIds = findings.Select(f => f.CycleId).Distinct().ToList();
        var scopeIds = findings.Select(f => f.AssessmentScopeId).Distinct().ToList();
        var cycles = await _db.NistCycles.AsNoTracking().Where(c => cycleIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var scopes = await _db.Scopes.AsNoTracking().Where(s => scopeIds.Contains(s.Id)).ToListAsync(ct);
        var plans = await PlansByFindingAsync(_db, findings.Select(f => f.Id).ToList(), ct);
        var current = findings.ToDictionary(f => f.Id, f => CurrentPlan(plans.TryGetValue(f.Id, out var l) ? l : null));
        var assisted = await NistAssistProvenance.LoadForTargetsAsync(_db,
            findings.Select(f => f.Id).Concat(current.Values.Where(p => p is not null).Select(p => p!.Id)).ToList(), ct);
        return findings.Select(f => FindingView(f,
            cycles.TryGetValue(f.CycleId, out var cn) ? cn : "",
            scopes.FirstOrDefault(s => s.Id == f.AssessmentScopeId) is { } sc ? ScopeName(sc) : "",
            _language?.Get(f.SubcategoryCode)?.Title ?? f.SubcategoryCode,
            current[f.Id]) with { AssistedFields = NistAssistProvenance.FindingFields(assisted, f, current[f.Id]) }).ToList();
    }

    private string TitleOf(NistSubcategory s) => _language?.Get(s.Code)?.Title ?? s.Code;

    private static string? Name(RemediationActor actor) =>
        string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);

    internal static NistProcedureView ProcedureView(NistTestProcedure p) => new(
        p.Id, p.Method.ToString(), p.Procedure, p.Status.ToString(), p.Outcome?.ToString(), p.Observation, p.PerformedOn,
        p.ResultRecordedByName, p.ResultRecordedAt, p.EvidenceIds.ToList(), p.ContentOrigin.ToString(), p.OriginNote,
        p.CreatedByName, p.CreatedAt, p.Version);

    internal static NistFindingView FindingView(NistFinding f, string cycleName, string scopeName, string subcategoryTitle, ActionPlanView? plan)
    {
        NistFindingOriginContext? origin = null;
        try
        {
            origin = JsonSerializer.Deserialize<NistFindingOriginContext>(f.OriginContextJson, OriginJson);
        }
        catch (JsonException)
        {
            // Registro de origem ilegível: dito como ausente, nunca substituído pela leitura atual.
        }
        return new NistFindingView(
            f.Id, f.AssessmentId, f.CycleId, cycleName, f.AssessmentScopeId, scopeName, f.SubcategoryCode, subcategoryTitle,
            f.Title, f.Condition, f.Risk, f.Impact, f.Severity.ToString(), f.SeverityRationale, f.Priority.ToString(), f.PriorityRationale,
            f.Recommendation, f.EvidenceIds.ToList(), f.Status.ToString(), f.StatusNote, f.StatusChangedAt, f.StatusChangedByName,
            origin, f.CreatedByName, f.CreatedAt, f.Version, plan is null ? null : NistPlanView.From(plan), TreatmentLabel(plan));
    }
}
