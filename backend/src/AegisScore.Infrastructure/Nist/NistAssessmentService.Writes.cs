using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Nist;
using AegisScore.Application.Remediation;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

// [AEGIS-NIST-JOURNEY-02] Gravações da subcategoria: avaliação humana, designação de avaliador e revisor, decisão do
// revisor, evidências e a sugestão (nunca gravada) da IA. Todas nomeiam avaliação · rodada · escopo, recusam rodada
// encerrada, conferem a versão e deixam a mudança na trilha com valores anteriores e novos.
public sealed partial class NistAssessmentService
{
    public async Task<NistSubcategoryDetailView> SaveEvaluationAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, SaveNistEvaluationCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);

        // ---- Validação (nada é gravado se algo falhar) ----
        if (command.CurrentLevel is int cur && (cur < AssessmentMethodology.MinLevel || cur > AssessmentMethodology.MaxLevel))
            throw new NistAssessmentValidationException("A situação atual deve ser um nível de 1 a 5 (ou ficar sem nível).");
        if (command.TargetLevel is int tgt && (tgt < AssessmentMethodology.MinLevel || tgt > AssessmentMethodology.MaxLevel))
            throw new NistAssessmentValidationException("O alvo deve ser um nível de 1 a 5 (ou ficar sem nível).");

        var rationale = Optional(command.Rationale, MaxLongText, "A justificativa");
        if (command.NotApplicable)
        {
            if (command.CurrentLevel is not null || command.TargetLevel is not null)
                throw new NistAssessmentValidationException("Um resultado que não se aplica não recebe situação atual nem alvo.");
            if ((rationale ?? "").Length < MinJustification)
                throw new NistAssessmentValidationException("Explique por que o resultado não se aplica ao escopo (justificativa de pelo menos 10 caracteres).");
        }

        var currentComments = Optional(command.CurrentComments, MaxLongText, "A observação da situação atual");
        var targetComments = Optional(command.TargetComments, MaxLongText, "A observação do alvo");
        var gaps = Optional(command.Gaps, MaxLongText, "As lacunas");
        var risk = Optional(command.RiskImpact, MaxDescription, "O risco/impacto");
        var guidance = Optional(command.ImprovementGuidance, MaxLongText, "A orientação de melhoria");
        var owner = await ResolvePersonAsync(_db, command.OwnerUserId, command.OwnerName, command.OwnerIsExternal, command.OwnerContact, ct);

        var hasContent = command.NotApplicable || command.CurrentLevel is not null || command.TargetLevel is not null
            || new[] { rationale, currentComments, targetComments, gaps, risk, guidance, owner.Name }.Any(t => t is not null);
        if (!hasContent)
            throw new NistAssessmentValidationException("Nada a registrar: informe um nível, uma justificativa ou uma anotação.");

        // [AEGIS-NIST-AI-ASSIST-01] Conteúdo aplicado de uma sugestão: a geração precisa ser deste contexto e o contexto em que
        // foi gerada, o atual (senão 409 — a pessoa revisa ou gera outra). A versão-base do rascunho segue conferida abaixo.
        IReadOnlyList<string> assistedFields = Array.Empty<string>();
        (NistAiAssistance Generation, bool Stale)? assisted = null;
        if (command.Assistance is { } reference)
        {
            assistedFields = NistAssistProvenance.CheckFields(reference, NistAssistProvenance.EvaluationFields.Keys);
            assisted = await NistAssistProvenance.ResolveAsync(_db, reference, NistAssistKind.Subcategory, assessmentId, cycleId, scopeId, sub.Code, null,
                async _ => (await new NistAssistContextBuilder(_db, _language).SubcategoryAsync(ctx, sub, ct)).Fingerprint, ct);
            NistAssistProvenance.RequireApplicable(assisted.Value.Generation, assistedFields);
        }

        // ---- Concorrência: a versão lida precisa ser a vigente ----
        var eval = await _db.Evaluations.FirstOrDefaultAsync(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryId == sub.Id, ct);
        var creating = eval is null;
        if (eval is null)
        {
            if (command.ExpectedVersion != 0)
                throw new NistAssessmentConflictException("Esta avaliação não existe mais na versão informada. Recarregue antes de gravar.");
            eval = new SubcategoryEvaluation { AssessmentScopeId = scopeId, CycleId = cycleId, SubcategoryId = sub.Id, CreatedAt = _clock.GetUtcNow() };
            _db.Evaluations.Add(eval);
        }
        else if (eval.Version != command.ExpectedVersion)
        {
            throw new NistAssessmentConflictException(
                $"Esta subcategoria foi alterada por outra pessoa (versão {eval.Version}, você enviou {command.ExpectedVersion}). " +
                "Recarregue para ver a versão atual antes de gravar.");
        }

        var changes = new List<NistFieldChange>();
        Diff(changes, "notApplicable", "não se aplica", creating ? null : eval.NotApplicable ? "sim" : "não", command.NotApplicable ? "sim" : "não");
        Diff(changes, "currentLevel", "situação atual", Level(eval.CurrentLevel), Level(command.CurrentLevel));
        Diff(changes, "targetLevel", "alvo", Level(eval.TargetLevel), Level(command.TargetLevel));
        Diff(changes, "rationale", "justificativa", eval.Rationale, rationale);
        Diff(changes, "currentComments", "observações da situação atual", eval.CurrentComments, currentComments);
        Diff(changes, "targetComments", "observações do alvo", eval.TargetComments, targetComments);
        Diff(changes, "gaps", "lacunas", eval.Gaps, gaps);
        Diff(changes, "riskImpact", "risco/impacto", eval.RiskImpact, risk);
        Diff(changes, "improvementGuidance", "orientação de melhoria", eval.ImprovementGuidance, guidance);
        Diff(changes, "owner", "responsável pela prática",
            creating ? null : DescribePerson(eval.OwnerUserId, eval.OwnerName, eval.OwnerIsExternal, eval.OwnerContact),
            DescribePerson(owner.UserId, owner.Name, owner.IsExternal, owner.Contact));
        if (eval.ContentOrigin != NistContentOrigin.Analyst)
            Diff(changes, "contentOrigin", "origem do conteúdo", NistLabels.ContentOrigin(eval.ContentOrigin.ToString()), NistLabels.ContentOrigin("Analyst"));
        var wasConfirmed = eval.HumanConfirmed;

        eval.NotApplicable = command.NotApplicable;
        eval.CurrentLevel = command.CurrentLevel;
        eval.CurrentScore = command.CurrentLevel;
        eval.TargetLevel = command.TargetLevel;
        eval.TargetScore = command.TargetLevel;
        eval.CurrentComments = currentComments;
        eval.TargetComments = targetComments;
        eval.Rationale = rationale;
        eval.Gaps = gaps;
        eval.RiskImpact = risk;
        eval.ImprovementGuidance = guidance;
        eval.OwnerName = owner.Name;
        eval.OwnerUserId = owner.UserId;
        eval.OwnerIsExternal = owner.IsExternal;
        eval.OwnerContact = owner.Contact;
        // Revisão HUMANA: autor vem do token, nunca do corpo. A confiança é da IA e não se aplica aqui. Gravar na tela
        // CONFIRMA o conteúdo (inclusive um rascunho herdado ou importado) — a origem anterior fica na nota e na trilha.
        eval.EvaluatedBy = EvaluatedBy.Analyst;
        eval.ContentOrigin = NistContentOrigin.Analyst;
        eval.Confidence = null;
        eval.ReviewedById = actor.AccountId?.ToString();
        eval.ReviewedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        eval.ReviewedAt = _clock.GetUtcNow();
        eval.Version += 1;

        // Andamento do escopo e da avaliação: a primeira revisão tira ambos do estado inicial.
        var scope = await _db.Scopes.FirstAsync(s => s.Id == scopeId, ct);
        if (scope.Status is ScopeStatus.NotStarted or ScopeStatus.Questionnaire or ScopeStatus.Validation)
            scope.Status = ScopeStatus.Evaluation;
        var assessment = await _db.Assessments.FirstAsync(a => a.Id == assessmentId, ct);
        if (assessment.Status == AssessmentStatus.Draft)
            assessment.Status = AssessmentStatus.InProgress;

        if (assisted is { } a)
        {
            var texts = NistAssistProvenance.EvaluationTexts(eval);
            NistAssistProvenance.Record(_db, a.Generation, a.Stale, NistAssistTarget.Evaluation, eval.Id,
                assistedFields.Select(f => new NistAssistProvenance.Incorporated(f, NistAssistProvenance.EvaluationFields[f], texts[f])).ToList(),
                actor, eval.ReviewedAt.Value);
        }

        Audit(_db, actor, eval.ReviewedAt.Value, assessmentId, cycleId, scopeId, sub.Code, "Evaluation", eval.Id,
            creating ? "Created" : wasConfirmed ? "Updated" : "Confirmed",
            creating ? $"Avaliação de {sub.Code} registrada (versão {eval.Version})."
            : wasConfirmed ? $"Avaliação de {sub.Code} alterada (versão {eval.Version})."
            : $"Avaliação de {sub.Code} confirmada por revisão humana (versão {eval.Version}).",
            changes);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException(
                "Esta subcategoria foi alterada por outra pessoa enquanto a sua gravação estava em curso. Recarregue antes de gravar.");
        }
        catch (DbUpdateException) when (creating)
        {
            // Índice único (escopo, rodada, subcategoria): outra pessoa criou a avaliação no mesmo instante.
            throw new NistAssessmentConflictException(
                "Outra pessoa acabou de registrar esta subcategoria. Recarregue para ver a avaliação existente.");
        }

        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, cycleId, scopeId, sub.Code, ct);
    }

    public async Task<NistSubcategoryDetailView> AssignAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, AssignNistRolesCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);

        // Designar NÃO concede privilégio: a pessoa precisa ser um usuário ativo do tenant, e continua com o papel que tem.
        var assessorName = command.AssessorUserId is { } a ? await ActiveUserNameAsync(_db, a, ct) : null;
        var reviewerName = command.ReviewerUserId is { } r ? await ActiveUserNameAsync(_db, r, ct) : null;

        var eval = await _db.Evaluations.FirstOrDefaultAsync(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryId == sub.Id, ct);
        var creating = eval is null;
        if (eval is null)
        {
            if (command.ExpectedVersion != 0)
                throw new NistAssessmentConflictException("Esta avaliação não existe mais na versão informada. Recarregue antes de gravar.");
            // Registro só de designação: sem conteúdo, sem revisão humana — o estado continua "não avaliada".
            eval = new SubcategoryEvaluation { AssessmentScopeId = scopeId, CycleId = cycleId, SubcategoryId = sub.Id, CreatedAt = _clock.GetUtcNow() };
            _db.Evaluations.Add(eval);
        }
        else if (eval.Version != command.ExpectedVersion)
        {
            throw new NistAssessmentConflictException(
                $"Esta subcategoria foi alterada por outra pessoa (versão {eval.Version}, você enviou {command.ExpectedVersion}). Recarregue.");
        }

        var changes = new List<NistFieldChange>();
        Diff(changes, "assessor", "avaliador", eval.AssessorName, assessorName);
        Diff(changes, "reviewer", "revisor", eval.ReviewerName, reviewerName);
        if (changes.Count == 0 && !creating)
            return await BuildDetailAsync(ctx, sub, ct);

        eval.AssessorUserId = command.AssessorUserId;
        eval.AssessorName = assessorName;
        eval.ReviewerUserId = command.ReviewerUserId;
        eval.ReviewerName = reviewerName;
        eval.Version += 1;
        Audit(_db, actor, _clock.GetUtcNow(), assessmentId, cycleId, scopeId, sub.Code, "Assignment", eval.Id, "Updated",
            $"Designação de {sub.Code} alterada.", changes);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("Esta subcategoria foi alterada por outra pessoa enquanto a sua gravação estava em curso.");
        }
        catch (DbUpdateException) when (creating)
        {
            throw new NistAssessmentConflictException("Outra pessoa acabou de registrar esta subcategoria. Recarregue.");
        }
        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, cycleId, scopeId, sub.Code, ct);
    }

    public async Task<NistSubcategoryDetailView> ReviewAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, ReviewNistEvaluationCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var decision = ParseEnum<NistReviewDecision>(command.Decision, "Decisão inválida (Approved ou ChangesRequested).");
        if (decision == NistReviewDecision.None)
            throw new NistAssessmentValidationException("Decisão inválida (Approved ou ChangesRequested).");
        var note = Optional(command.Note, MaxDescription, "A nota da revisão");
        if (decision == NistReviewDecision.ChangesRequested && (note ?? "").Length < MinJustification)
            throw new NistAssessmentValidationException("Diga o que precisa ser ajustado (pelo menos 10 caracteres).");

        var eval = await _db.Evaluations.FirstOrDefaultAsync(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryId == sub.Id, ct)
            ?? throw new NistAssessmentValidationException("Não há avaliação desta subcategoria para revisar.");
        if (eval.Version != command.ExpectedVersion)
            throw new NistAssessmentConflictException(
                $"Esta subcategoria foi alterada (versão {eval.Version}, você revisou a {command.ExpectedVersion}). Recarregue e revise a versão vigente.");
        if (!eval.HumanConfirmed)
            throw new NistAssessmentValidationException("Só se revisa uma avaliação confirmada na tela (rascunho herdado ou importado ainda não é avaliação).");
        if (actor.AccountId is not { } reviewer)
            throw new NistAssessmentValidationException("Autor da revisão não identificado na sessão.");
        if (string.Equals(eval.ReviewedById, reviewer.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new NistAssessmentValidationException("A revisão precisa ser feita por outra pessoa: você registrou a versão vigente desta avaliação.");

        var now = _clock.GetUtcNow();
        var changes = new List<NistFieldChange>();
        Diff(changes, "reviewDecision", "decisão do revisor", NistLabels.Review(ReviewStateOf(eval)), NistLabels.Review(decision.ToString()));
        Diff(changes, "reviewNote", "nota da revisão", eval.ReviewDecisionNote, note);
        eval.ReviewDecision = decision;
        eval.ReviewDecisionFingerprint = ContentFingerprint(eval);
        eval.ReviewDecisionByAccountId = reviewer;
        eval.ReviewDecisionByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        eval.ReviewDecisionAt = now;
        eval.ReviewDecisionNote = note;
        eval.Version += 1;
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Review", eval.Id,
            decision == NistReviewDecision.Approved ? "Approved" : "ChangesRequested",
            decision == NistReviewDecision.Approved
                ? $"Revisão de {sub.Code} aprovada (conteúdo da versão {eval.Version - 1})."
                : $"Revisão de {sub.Code} devolvida para ajuste.",
            changes);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("Esta subcategoria foi alterada por outra pessoa enquanto a revisão era gravada.");
        }
        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, cycleId, scopeId, sub.Code, ct);
    }

    // =============================================================================================
    //  Evidências
    // =============================================================================================

    public async Task<NistSubcategoryDetailView> LinkEvidenceAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, LinkNistEvidenceCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var now = _clock.GetUtcNow();

        if (!Enum.TryParse<EvidenceOriginKind>((command.Kind ?? "").Trim(), ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
            throw new NistAssessmentValidationException("Tipo de evidência inválido.");

        var notes = Optional(command.Notes, MaxNotes, "A observação da evidência");
        var evidence = new Evidence
        {
            AssessmentScopeId = scopeId,
            CycleId = cycleId,
            SubcategoryCode = sub.Code,
            OriginKind = kind,
            RecordedByAccountId = actor.AccountId,
            RecordedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName),
            CreatedAt = now,
        };

        switch (kind)
        {
            case EvidenceOriginKind.Manual:
            {
                evidence.Title = Required(command.Title, "Informe um título para a evidência.", MaxTitle, "O título");
                evidence.Uri = NormalizeUri(command.Uri);
                evidence.Notes = notes;
                evidence.Type = Enum.TryParse<EvidenceType>((command.ManualType ?? "").Trim(), true, out var t)
                        && Enum.IsDefined(t) && t != EvidenceType.ApiSignal
                    ? t
                    : evidence.Uri is null ? EvidenceType.Interview : EvidenceType.Link;
                evidence.Source = EvidenceSource.Analyst;
                evidence.OriginLabel = "Registro do analista";
                evidence.CollectedAt = command.CollectedOn is { } d
                    ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    : now;
                if (evidence.CollectedAt > now.AddDays(1))
                    throw new NistAssessmentValidationException("A data da evidência não pode estar no futuro.");
                break;
            }
            case EvidenceOriginKind.GovernanceDocument:
            {
                var docId = command.DocumentId ?? throw new NistAssessmentValidationException("Escolha o documento.");
                var doc = await _db.GovernanceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == docId, ct)
                    ?? throw new NistAssessmentNotFoundException("Documento não encontrado neste ambiente.");
                evidence.Title = Truncate(doc.Title, MaxTitle);
                evidence.Type = EvidenceType.Document;
                evidence.Source = EvidenceSource.Analyst;
                evidence.OriginRef = doc.Id.ToString();
                evidence.OriginLabel = $"Biblioteca de documentos · {DocumentTypeLabel(doc.Type)}";
                evidence.OriginScope = Truncate($"Situação do documento: {DocumentStatusLabel(doc.Status)}"
                    + (doc.FileName is null ? "" : $"; arquivo {doc.FileName}"), 500);
                evidence.Hash = doc.Sha256;
                evidence.CollectedAt = doc.DocumentDate is { } dd
                    ? new DateTimeOffset(dd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    : doc.CreatedAt;
                evidence.Notes = notes;
                break;
            }
            case EvidenceOriginKind.KnightIndicator:
            {
                var runId = command.KnightRunId ?? throw new NistAssessmentValidationException("Informe a avaliação do KNIGHT.");
                var indicatorId = (command.KnightIndicatorId ?? "").Trim();
                if (indicatorId.Length == 0) throw new NistAssessmentValidationException("Informe o controle do KNIGHT.");

                var run = await _db.KnightAssessmentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct)
                    ?? throw new NistAssessmentNotFoundException("Avaliação do KNIGHT não encontrada neste ambiente.");
                if (run.Status != KnightRunStatus.Completed)
                    throw new NistAssessmentValidationException("Só uma avaliação do KNIGHT concluída sustenta evidência.");
                if (run.SourceType == KnightSourceType.Consolidated)
                    throw new NistAssessmentValidationException("Vincule o resultado da avaliação da própria fonte, não do consolidado.");

                var indicator = await _db.KnightIndicatorResults.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.RunId == runId && i.IndicatorId == indicatorId, ct)
                    ?? throw new NistAssessmentNotFoundException("Controle não encontrado nesta avaliação do KNIGHT.");
                // Critério sustentado: mapeamento EXPLÍCITO do catálogo do KNIGHT para esta subcategoria.
                if (!indicator.NistCodes.Any(c => string.Equals(c, sub.Code, StringComparison.OrdinalIgnoreCase)))
                    throw new NistAssessmentValidationException(
                        $"O controle {indicator.IndicatorId} do KNIGHT não está mapeado para {sub.Code}: só entram evidências técnicas com mapeamento explícito.");
                if (!EvaluatedKnightStatuses.Contains(indicator.Status))
                    throw new NistAssessmentValidationException(
                        "Este controle não tem resultado avaliado nesta execução (não avaliado, erro ou não aplicável) e não sustenta evidência.");

                evidence.Title = Truncate($"{indicator.IndicatorId} — {indicator.Title}", MaxTitle);
                evidence.Type = EvidenceType.ApiSignal;
                evidence.Source = run.Mode == KnightAssessmentMode.Live ? EvidenceSource.ApiValidated : EvidenceSource.SelfDeclared;
                evidence.OriginRef = $"{run.Id}/{indicator.IndicatorId}";
                evidence.OriginLabel = Truncate($"AEGIS KNIGHT · {run.Source} · {indicator.IndicatorId}", 300);
                evidence.OriginScope = Truncate(KnightScopeOf(run), 500);
                evidence.CollectedAt = run.CompletedAt ?? indicator.CollectedAt;
                evidence.Notes = Truncate(
                    $"Resultado técnico: {KnightStatusLabel(indicator.Status)}. Evidência técnica de apoio — não comprova sozinha o resultado organizacional."
                    + (notes is null ? "" : $" {notes}"), MaxNotes);
                break;
            }
            case EvidenceOriginKind.AssetInventory:
            {
                if (!sub.Code.StartsWith("ID.AM", StringComparison.Ordinal))
                    throw new NistAssessmentValidationException("O inventário de ativos só sustenta resultados de gestão de ativos (ID.AM).");
                var assets = await _db.Assets.AsNoTracking().Where(a => a.IsActive)
                    .Select(a => new { a.Category, a.DiscoverySource }).ToListAsync(ct);
                evidence.Title = $"Inventário de ativos — retrato de {now.UtcDateTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
                evidence.Type = EvidenceType.ApiSignal;
                evidence.Source = EvidenceSource.ApiValidated;
                evidence.OriginRef = $"inventory@{now.UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z";
                evidence.OriginLabel = "Inventário de ativos do AEGIS";
                evidence.OriginScope = Truncate(InventoryScopeOf(assets.Select(a => (a.Category, a.DiscoverySource)).ToList()), 500);
                evidence.CollectedAt = now;
                evidence.Notes = Truncate("Retrato do inventário no instante do vínculo. Mostra o que está inventariado, não que a gestão de ativos esteja completa."
                    + (notes is null ? "" : $" {notes}"), MaxNotes);
                break;
            }
        }

        // Um mesmo documento ou controle técnico não é vinculado duas vezes à mesma subcategoria do escopo NA RODADA.
        if (evidence.OriginRef is not null && kind != EvidenceOriginKind.AssetInventory)
        {
            var duplicate = await _db.Evidence.AsNoTracking().AnyAsync(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId
                && e.SubcategoryCode == sub.Code && e.RemovedAt == null && e.OriginKind == kind && e.OriginRef == evidence.OriginRef, ct);
            if (duplicate)
                throw new NistAssessmentConflictException("Esta evidência já está vinculada a esta subcategoria nesta rodada.");
        }

        _db.Evidence.Add(evidence);
        var changes = new List<NistFieldChange>();
        Diff(changes, "evidence", "evidência", null, $"{NistLabels.EvidenceOrigin(kind.ToString())}: {evidence.Title}");
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Evidence", evidence.Id, "Linked",
            $"Evidência vinculada a {sub.Code}: {evidence.Title} ({evidence.OriginLabel}, data na origem {Date(evidence.CollectedAt)}).", changes);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, cycleId, scopeId, sub.Code, ct);
    }

    public async Task<NistSubcategoryDetailView> RemoveEvidenceAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, Guid evidenceId, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        EnsureOpen(ctx.Cycle);
        var sub = ctx.FindSubcategory(code);
        var evidence = await _db.Evidence.FirstOrDefaultAsync(e => e.Id == evidenceId && e.AssessmentScopeId == scopeId && e.CycleId == cycleId
            && e.SubcategoryCode == sub.Code && e.RemovedAt == null, ct)
            ?? throw new NistAssessmentNotFoundException("Evidência não encontrada nesta subcategoria e rodada.");

        // Uma evidência que sustenta um procedimento realizado ou um achado não sai em silêncio: a citação vem antes.
        var citedBy = ctx.Procedures.Where(p => p.SubcategoryCode == sub.Code && p.EvidenceIds.Contains(evidenceId))
            .Select(p => $"procedimento ({NistLabels.Method(p.Method.ToString()).ToLowerInvariant()})")
            .Concat(ctx.Findings.Where(f => f.SubcategoryCode == sub.Code && f.EvidenceIds.Contains(evidenceId)).Select(f => $"achado \"{f.Title}\""))
            .ToList();
        if (citedBy.Count > 0)
            throw new NistAssessmentConflictException(
                $"Esta evidência sustenta {string.Join(", ", citedBy)}. Retire a citação lá antes de retirar o vínculo.");

        var now = _clock.GetUtcNow();
        evidence.RemovedAt = now;
        evidence.RemovedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        var changes = new List<NistFieldChange>();
        Diff(changes, "evidence", "evidência", $"{NistLabels.EvidenceOrigin(evidence.OriginKind.ToString())}: {evidence.Title}", null);
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, sub.Code, "Evidence", evidence.Id, "Removed",
            $"Vínculo de evidência retirado de {sub.Code}: {evidence.Title} (o registro permanece na trilha).", changes);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, cycleId, scopeId, sub.Code, ct);
    }
}
