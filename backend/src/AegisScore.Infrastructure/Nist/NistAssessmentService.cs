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

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-JOURNEY-01] Jornada persistida do AEGIS NIST sobre o modelo EXISTENTE de assessment
/// (<see cref="Assessment"/> → <see cref="AssessmentScope"/> → <see cref="SubcategoryEvaluation"/> + <see cref="Evidence"/>).
///
/// Garantias:
/// <list type="bullet">
/// <item>tenant implícito: tudo passa pelo filtro global; avaliação, escopo, documento e execução de outro tenant são
/// simplesmente INEXISTENTES (404), e o carimbo fail-closed do DbContext barra qualquer escrita cruzada;</item>
/// <item>o escopo precisa pertencer à avaliação e a subcategoria ao catálogo da avaliação;</item>
/// <item>níveis só de 1 a 5 (metodologia aegis-methodology-v1) ou ausentes — ausência nunca vira zero;</item>
/// <item>concorrência otimista pela versão da avaliação, reforçada pelo token no UPDATE e pelo índice único;</item>
/// <item>evidência técnica do KNIGHT só entra numa subcategoria que o catálogo do KNIGHT mapeia EXPLICITAMENTE para ela,
/// e vincular não muda nenhum nível — aprovar um controle técnico não comprova o resultado organizacional;</item>
/// <item>a sugestão da IA é devolvida, nunca gravada; a jornada inteira funciona com a IA indisponível.</item>
/// </list>
/// Não escreve no score de postura (TenantControlState / aegis-score-v1): apenas o lê como contexto.
/// </summary>
public sealed class NistAssessmentService : INistAssessmentService
{
    internal const int MaxName = 200;
    internal const int MaxDescription = 2000;
    internal const int MaxLongText = 4000;
    internal const int MinJustification = 10;
    private const int MaxTitle = 300;
    private const int MaxNotes = 2000;

    private static readonly string[] FunctionOrder = { "GV", "ID", "PR", "DE", "RS", "RC" };

    /// <summary>Ordem OFICIAL das categorias no CSF 2.0 (o catálogo não guarda ordem; o código sozinho é alfabético).</summary>
    private static readonly string[] CategoryOrder =
    {
        "GV.OC", "GV.RM", "GV.RR", "GV.PO", "GV.OV", "GV.SC",
        "ID.AM", "ID.RA", "ID.IM",
        "PR.AA", "PR.AT", "PR.DS", "PR.PS", "PR.IR",
        "DE.CM", "DE.AE",
        "RS.MA", "RS.AN", "RS.CO", "RS.MI",
        "RC.RP", "RC.CO",
    };

    private static int CategoryRank(string code) => Array.IndexOf(CategoryOrder, code) is var i && i >= 0 ? i : CategoryOrder.Length;

    private static readonly KnightIndicatorStatus[] EvaluatedKnightStatuses =
        { KnightIndicatorStatus.Passed, KnightIndicatorStatus.Exposed, KnightIndicatorStatus.Mitigated };

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IControlLanguageCatalog? _language;
    private readonly IAiAssessmentService? _ai;
    private readonly IAiFreeTierGate? _gate;
    private readonly IAiTenantResolver? _aiTenant;
    private readonly ILogger<NistAssessmentService> _log;
    private readonly MaturityScoringService _maturity = new();

    public NistAssessmentService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        IControlLanguageCatalog? language = null,
        IAiAssessmentService? ai = null,
        IAiFreeTierGate? gate = null,
        IAiTenantResolver? aiTenant = null,
        ILogger<NistAssessmentService>? log = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _language = language;
        _ai = ai;
        _gate = gate;
        _aiTenant = aiTenant;
        _log = log ?? NullLogger<NistAssessmentService>.Instance;
    }

    // =============================================================================================
    //  Avaliações e escopos
    // =============================================================================================

    public async Task<IReadOnlyList<NistAssessmentView>> ListAsync(CancellationToken ct = default)
    {
        RequireTenant();
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
        RequireTenant();
        var a = await _db.Assessments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        return await BuildAssessmentViewAsync(a, ct);
    }

    public async Task<NistAssessmentView> CreateAsync(CreateNistAssessmentCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant();
        ArgumentNullException.ThrowIfNull(command);

        var name = Required(command.Name, "Informe o nome da avaliação.", MaxName, "O nome da avaliação");
        var description = Optional(command.Description, MaxDescription, "A descrição da avaliação");
        if (command.StartDate is { } s && command.EndDate is { } e && e < s)
            throw new NistAssessmentValidationException("A data de término não pode ser anterior à de início.");

        var fv = await _db.FrameworkVersions.AsNoTracking().FirstOrDefaultAsync(f => f.IsActive, ct)
            ?? throw new NistAssessmentValidationException("Não há catálogo NIST ativo para avaliar.");

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
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Assessments.Add(assessment);

        if (!string.IsNullOrWhiteSpace(command.InitialScopeName))
        {
            _db.Scopes.Add(new AssessmentScope
            {
                AssessmentId = assessment.Id,
                Name = Required(command.InitialScopeName, "Informe o nome do escopo.", MaxName, "O nome do escopo"),
                Description = Optional(command.InitialScopeDescription, MaxDescription, "A descrição do escopo"),
                CreatedAt = _clock.GetUtcNow(),
            });
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Avaliação NIST {AssessmentId} criada por {Actor}.", assessment.Id, actor.DisplayName);
        return await BuildAssessmentViewAsync(assessment, ct);
    }

    public async Task<NistScopeView> AddScopeAsync(Guid assessmentId, CreateNistScopeCommand command, CancellationToken ct = default)
    {
        RequireTenant();
        ArgumentNullException.ThrowIfNull(command);
        if (!await _db.Assessments.AnyAsync(a => a.Id == assessmentId, ct))
            throw new NistAssessmentNotFoundException("Avaliação não encontrada.");

        var name = Required(command.Name, "Informe o nome do escopo.", MaxName, "O nome do escopo");
        var normalized = name.ToUpperInvariant();
        var existing = await _db.Scopes.AsNoTracking().Where(s => s.AssessmentId == assessmentId).Select(s => s.Name).ToListAsync(ct);
        if (existing.Any(n => string.Equals((n ?? "").Trim().ToUpperInvariant(), normalized, StringComparison.Ordinal)))
            throw new NistAssessmentConflictException("Já existe um escopo com este nome nesta avaliação.");

        var scope = new AssessmentScope
        {
            AssessmentId = assessmentId,
            Name = name,
            Description = Optional(command.Description, MaxDescription, "A descrição do escopo"),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Scopes.Add(scope);
        await _db.SaveChangesAsync(ct);

        var total = await CountSubcategoriesAsync(
            (await _db.Assessments.AsNoTracking().FirstAsync(a => a.Id == assessmentId, ct)).FrameworkVersionId, ct);
        return new NistScopeView(scope.Id, scope.Name, scope.Description, total, 0, 0, 0, 0, null);
    }

    // =============================================================================================
    //  Funções, perfil e subcategorias
    // =============================================================================================

    public async Task<NistFunctionView> GetFunctionAsync(Guid assessmentId, Guid scopeId, string functionCode, CancellationToken ct = default)
    {
        RequireTenant();
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var code = (functionCode ?? "").Trim().ToUpperInvariant();
        var fn = ctx.Catalog.Functions.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal))
            ?? throw new NistAssessmentNotFoundException($"Função '{functionCode}' não existe no catálogo da avaliação.");

        var subs = fn.Categories.SelectMany(c => c.Subcategories).ToList();
        var profile = _maturity.AggregateProfile(subs.Select(s => ProfileScoreOf(s, ctx)));

        var categories = fn.Categories.OrderBy(c => CategoryRank(c.Code)).ThenBy(c => c.Code, StringComparer.Ordinal).Select(c =>
        {
            var catProfile = profile.Categories.FirstOrDefault(p => p.RefCode == c.Code)
                ?? new ProfileScore(SnapshotLevel.Category, c.Code, null, null, null, 0, 0, 0, 0, 0);
            var rows = c.Subcategories.OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => RowOf(s, ctx)).ToList();
            return new NistCategoryView(c.Code, c.Name, c.Definition, ProfileView(catProfile, rows.Count(r => r.State == NistSubcategoryStates.Evaluated)), rows);
        }).ToList();

        var evaluated = categories.Sum(c => c.Profile.Evaluated);
        var fnProfile = profile.Functions.FirstOrDefault() ?? profile.Overall;
        return new NistFunctionView(assessmentId, scopeId, fn.Code, fn.Name, fn.Definition, ProfileView(fnProfile, evaluated), categories);
    }

    public async Task<NistProfileView> GetProfileAsync(Guid assessmentId, Guid scopeId, CancellationToken ct = default)
    {
        RequireTenant();
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var subs = ctx.Catalog.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories).ToList();
        var profile = _maturity.AggregateProfile(subs.Select(s => ProfileScoreOf(s, ctx)));

        var states = subs.ToDictionary(s => s.Code, s => StateOf(ctx.Evaluation(s.Id), ctx.EvidenceCount(s.Code)), StringComparer.Ordinal);
        int EvaluatedIn(string prefix) => states.Count(kv => kv.Value == NistSubcategoryStates.Evaluated
            && (prefix == "ALL" || kv.Key.StartsWith(prefix, StringComparison.Ordinal) && (kv.Key.Length == prefix.Length || kv.Key[prefix.Length] is '.' or '-')));

        var gaps = subs
            .Select(s => (Sub: s, Eval: ctx.Evaluation(s.Id)))
            .Where(x => x.Eval is { Gap: not null })
            .Select(x => new NistGapView(x.Sub.Code, TitleOf(x.Sub), x.Eval!.CurrentLevel!.Value, x.Eval.TargetLevel!.Value,
                x.Eval.Gap!.Value, x.Eval.OwnerName, x.Eval.ImprovementGuidance))
            .OrderByDescending(g => g.Gap).ThenBy(g => g.Code, StringComparer.Ordinal)
            .ToList();

        var indeterminate = subs.Count(s => ctx.Evaluation(s.Id) is not { NotApplicable: true } && ctx.Evaluation(s.Id)?.Gap is null);

        return new NistProfileView(
            assessmentId, scopeId, ctx.Assessment.MethodologyVersion,
            ProfileView(profile.Overall, EvaluatedIn("ALL")),
            profile.Functions.OrderBy(f => Array.IndexOf(FunctionOrder, f.RefCode)).Select(f => ProfileView(f, EvaluatedIn(f.RefCode))).ToList(),
            profile.Categories.OrderBy(c => CategoryRank(c.RefCode)).Select(c => ProfileView(c, EvaluatedIn(c.RefCode))).ToList(),
            gaps,
            indeterminate);
    }

    public async Task<NistSubcategoryDetailView> GetSubcategoryAsync(Guid assessmentId, Guid scopeId, string code, CancellationToken ct = default)
    {
        RequireTenant();
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var sub = FindSubcategory(ctx, code);
        return await BuildDetailAsync(ctx, sub, ct);
    }

    // =============================================================================================
    //  Gravação da avaliação humana
    // =============================================================================================

    public async Task<NistSubcategoryDetailView> SaveEvaluationAsync(
        Guid assessmentId, Guid scopeId, string code, SaveNistEvaluationCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant();
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var sub = FindSubcategory(ctx, code);

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
        var owner = Optional(command.OwnerName, MaxName, "O responsável");

        var hasContent = command.NotApplicable || command.CurrentLevel is not null || command.TargetLevel is not null
            || new[] { rationale, currentComments, targetComments, gaps, risk, guidance, owner }.Any(t => t is not null);
        if (!hasContent)
            throw new NistAssessmentValidationException("Nada a registrar: informe um nível, uma justificativa ou uma anotação.");

        // ---- Concorrência: a versão lida precisa ser a vigente ----
        var eval = await _db.Evaluations.FirstOrDefaultAsync(e => e.AssessmentScopeId == scopeId && e.SubcategoryId == sub.Id, ct);
        var creating = eval is null;
        if (eval is null)
        {
            if (command.ExpectedVersion != 0)
                throw new NistAssessmentConflictException("Esta avaliação não existe mais na versão informada. Recarregue antes de gravar.");
            eval = new SubcategoryEvaluation { AssessmentScopeId = scopeId, SubcategoryId = sub.Id, CreatedAt = _clock.GetUtcNow() };
            _db.Evaluations.Add(eval);
        }
        else if (eval.Version != command.ExpectedVersion)
        {
            throw new NistAssessmentConflictException(
                $"Esta subcategoria foi alterada por outra pessoa (versão {eval.Version}, você enviou {command.ExpectedVersion}). " +
                "Recarregue para ver a versão atual antes de gravar.");
        }

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
        eval.OwnerName = owner;
        // Revisão HUMANA: autor vem do token, nunca do corpo. A confiança é da IA e não se aplica aqui.
        eval.EvaluatedBy = EvaluatedBy.Analyst;
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
            // Índice único (escopo, subcategoria): outra pessoa criou a avaliação no mesmo instante.
            throw new NistAssessmentConflictException(
                "Outra pessoa acabou de registrar esta subcategoria. Recarregue para ver a avaliação existente.");
        }

        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, scopeId, sub.Code, ct);
    }

    // =============================================================================================
    //  Evidências
    // =============================================================================================

    public async Task<NistSubcategoryDetailView> LinkEvidenceAsync(
        Guid assessmentId, Guid scopeId, string code, LinkNistEvidenceCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant();
        ArgumentNullException.ThrowIfNull(command);
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var sub = FindSubcategory(ctx, code);
        var now = _clock.GetUtcNow();

        if (!Enum.TryParse<EvidenceOriginKind>((command.Kind ?? "").Trim(), ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
            throw new NistAssessmentValidationException("Tipo de evidência inválido.");

        var notes = Optional(command.Notes, MaxNotes, "A observação da evidência");
        var evidence = new Evidence
        {
            AssessmentScopeId = scopeId,
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

        // Um mesmo documento ou controle técnico não é vinculado duas vezes à mesma subcategoria do escopo.
        if (evidence.OriginRef is not null && kind != EvidenceOriginKind.AssetInventory)
        {
            var duplicate = await _db.Evidence.AsNoTracking().AnyAsync(e => e.AssessmentScopeId == scopeId
                && e.SubcategoryCode == sub.Code && e.RemovedAt == null && e.OriginKind == kind && e.OriginRef == evidence.OriginRef, ct);
            if (duplicate)
                throw new NistAssessmentConflictException("Esta evidência já está vinculada a esta subcategoria.");
        }

        _db.Evidence.Add(evidence);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, scopeId, sub.Code, ct);
    }

    public async Task<NistSubcategoryDetailView> RemoveEvidenceAsync(
        Guid assessmentId, Guid scopeId, string code, Guid evidenceId, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant();
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var sub = FindSubcategory(ctx, code);
        var evidence = await _db.Evidence.FirstOrDefaultAsync(e => e.Id == evidenceId && e.AssessmentScopeId == scopeId
            && e.SubcategoryCode == sub.Code && e.RemovedAt == null, ct)
            ?? throw new NistAssessmentNotFoundException("Evidência não encontrada nesta subcategoria.");
        evidence.RemovedAt = _clock.GetUtcNow();
        evidence.RemovedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetSubcategoryAsync(assessmentId, scopeId, sub.Code, ct);
    }

    // =============================================================================================
    //  Sugestão da IA (nunca gravada)
    // =============================================================================================

    public async Task<NistAiSuggestionView> SuggestAsync(Guid assessmentId, Guid scopeId, string code, CancellationToken ct = default)
    {
        RequireTenant();
        var ctx = await LoadScopeContextAsync(assessmentId, scopeId, ct);
        var sub = FindSubcategory(ctx, code);

        if (_ai is null || _gate is null || _gate.Mode == AiMode.Disabled)
            throw new NistAiUnavailableException("A IA está desativada neste ambiente. A avaliação segue normalmente sem ela.");

        var simulated = true;
        if (_gate.ProviderConfigured && _aiTenant is not null)
            simulated = !_gate.IsExternalAllowedForSlug(await _aiTenant.GetCurrentSlugAsync(ct));

        var eval = ctx.Evaluation(sub.Id);
        var linked = await ActiveEvidenceAsync(scopeId, sub.Code, ct);
        var answers = new List<(string Question, string Answer, string? Comment)>();
        if (eval?.CurrentComments is { } cc) answers.Add(("Observação do analista sobre a situação atual", cc, null));
        if (eval?.Gaps is { } g) answers.Add(("Lacunas observadas", g, null));
        var summaries = linked.Select(e => $"{e.OriginLabel ?? e.OriginKind.ToString()} — {e.Title} ({e.CollectedAt:yyyy-MM-dd}){(e.Notes is null ? "" : $": {e.Notes}")}").ToList();

        MaturitySuggestion suggestion;
        try
        {
            suggestion = await _ai.SuggestMaturityAsync(
                new MaturitySuggestionRequest(sub.Code, sub.Description, answers, summaries, Array.Empty<(string, double?, int?)>()), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sugestão da IA indisponível para {Code}.", sub.Code);
            throw new NistAiUnavailableException("A IA não respondeu agora. A avaliação segue normalmente sem ela.");
        }

        return new NistAiSuggestionView(
            Math.Clamp(suggestion.CurrentLevel, AssessmentMethodology.MinLevel, AssessmentMethodology.MaxLevel),
            Math.Clamp(suggestion.Confidence, 0, 1),
            Truncate(suggestion.Rationale ?? "", MaxLongText),
            simulated,
            _clock.GetUtcNow());
    }

    // =============================================================================================
    //  Histórico (maturidade por avaliação e escopo)
    // =============================================================================================

    public async Task<IReadOnlyList<NistAssessmentHistoryItem>> HistoryAsync(CancellationToken ct = default)
    {
        RequireTenant();
        var assessments = await _db.Assessments.AsNoTracking().ToListAsync(ct);
        if (assessments.Count == 0) return Array.Empty<NistAssessmentHistoryItem>();
        var scopes = await _db.Scopes.AsNoTracking().ToListAsync(ct);
        var evaluations = await _db.Evaluations.AsNoTracking().ToListAsync(ct);

        var items = new List<NistAssessmentHistoryItem>();
        foreach (var a in assessments)
        {
            var catalog = await LoadCatalogAsync(a.FrameworkVersionId, ct);
            var subs = catalog.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories).ToList();
            foreach (var s in scopes.Where(x => x.AssessmentId == a.Id))
            {
                var evals = evaluations.Where(e => e.AssessmentScopeId == s.Id)
                    .GroupBy(e => e.SubcategoryId).ToDictionary(g => g.Key, g => g.First());
                var last = evals.Values.Where(e => e.ReviewedAt.HasValue).Select(e => e.ReviewedAt!.Value).DefaultIfEmpty().Max();
                if (last == default) continue; // sem revisão humana: o escopo não representa mês nenhum
                var profile = _maturity.AggregateProfile(subs.Select(sub => evals.TryGetValue(sub.Id, out var e)
                    ? new SubcategoryProfileScore(sub.Code, e.CurrentLevel, e.TargetLevel, e.NotApplicable)
                    : new SubcategoryProfileScore(sub.Code, null, null, false)));
                var utc = last.UtcDateTime;
                items.Add(new NistAssessmentHistoryItem(
                    a.Id, a.Name, s.Id, ScopeName(s), a.MethodologyVersion,
                    new DateOnly(utc.Year, utc.Month, 1),
                    "Mês da revisão humana mais recente registrada no escopo.",
                    last, subs.Count,
                    evals.Values.Count(e => !e.NotApplicable && e.CurrentLevel.HasValue),
                    evals.Values.Count(e => e.NotApplicable),
                    profile.Overall.Current, profile.Overall.Target));
            }
        }
        return items.OrderByDescending(i => i.ReferenceMonth).ThenByDescending(i => i.LastReviewedAt).ToList();
    }

    // =============================================================================================
    //  Montagem
    // =============================================================================================

    private sealed class ScopeContext
    {
        public required Assessment Assessment { get; init; }
        public required AssessmentScope Scope { get; init; }
        public required FrameworkVersion Catalog { get; init; }
        public required Dictionary<Guid, SubcategoryEvaluation> Evaluations { get; init; }
        public required Dictionary<string, int> EvidenceCounts { get; init; }

        public SubcategoryEvaluation? Evaluation(Guid subcategoryId) => Evaluations.TryGetValue(subcategoryId, out var e) ? e : null;
        public int EvidenceCount(string code) => EvidenceCounts.TryGetValue(code, out var n) ? n : 0;
    }

    private async Task<ScopeContext> LoadScopeContextAsync(Guid assessmentId, Guid scopeId, CancellationToken ct)
    {
        var assessment = await _db.Assessments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Avaliação não encontrada.");
        var scope = await _db.Scopes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scopeId && s.AssessmentId == assessmentId, ct)
            ?? throw new NistAssessmentNotFoundException("Escopo não encontrado nesta avaliação.");
        var catalog = await LoadCatalogAsync(assessment.FrameworkVersionId, ct);

        var evaluations = await _db.Evaluations.AsNoTracking().Where(e => e.AssessmentScopeId == scopeId).ToListAsync(ct);
        var evidence = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.RemovedAt == null && e.SubcategoryCode != null)
            .Select(e => e.SubcategoryCode!).ToListAsync(ct);

        return new ScopeContext
        {
            Assessment = assessment,
            Scope = scope,
            Catalog = catalog,
            Evaluations = evaluations.GroupBy(e => e.SubcategoryId).ToDictionary(g => g.Key, g => g.First()),
            EvidenceCounts = evidence.GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }

    private async Task<FrameworkVersion> LoadCatalogAsync(Guid frameworkVersionId, CancellationToken ct)
    {
        var fv = await _db.FrameworkVersions.AsNoTracking()
            .Include(f => f.Functions).ThenInclude(fn => fn.Categories).ThenInclude(c => c.Subcategories)
            .Include(f => f.MaturityLevels)
            .AsSplitQuery()
            .FirstOrDefaultAsync(f => f.Id == frameworkVersionId, ct)
            ?? throw new NistAssessmentNotFoundException("O catálogo desta avaliação não está disponível.");
        fv.Functions = fv.Functions.OrderBy(f => Array.IndexOf(FunctionOrder, f.Code) is var i && i < 0 ? 99 : i).ToList();
        return fv;
    }

    private Task<int> CountSubcategoriesAsync(Guid frameworkVersionId, CancellationToken ct) =>
        _db.Subcategories.CountAsync(s => s.Category!.Function!.FrameworkVersionId == frameworkVersionId, ct);

    private static NistSubcategory FindSubcategory(ScopeContext ctx, string code)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        return ctx.Catalog.Functions.SelectMany(f => f.Categories).SelectMany(c => c.Subcategories)
                   .FirstOrDefault(s => string.Equals(s.Code, normalized, StringComparison.Ordinal))
               ?? throw new NistAssessmentNotFoundException($"Subcategoria '{code}' não existe no catálogo da avaliação.");
    }

    private async Task<NistAssessmentView> BuildAssessmentViewAsync(Assessment a, CancellationToken ct)
    {
        var fvName = await _db.FrameworkVersions.AsNoTracking().Where(f => f.Id == a.FrameworkVersionId).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? "";
        var total = await CountSubcategoriesAsync(a.FrameworkVersionId, ct);
        var scopes = await _db.Scopes.AsNoTracking().Where(s => s.AssessmentId == a.Id).ToListAsync(ct);
        var scopeIds = scopes.Select(s => s.Id).ToList();
        var evals = await _db.Evaluations.AsNoTracking().Where(e => scopeIds.Contains(e.AssessmentScopeId)).ToListAsync(ct);
        var evidence = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId != null && scopeIds.Contains(e.AssessmentScopeId!.Value) && e.RemovedAt == null && e.SubcategoryCode != null)
            .Select(e => new { ScopeId = e.AssessmentScopeId!.Value, Code = e.SubcategoryCode! }).ToListAsync(ct);

        var scopeViews = scopes
            .OrderBy(s => s.CreatedAt.UtcTicks).ThenBy(s => s.Id)
            .Select(s =>
            {
                var se = evals.Where(e => e.AssessmentScopeId == s.Id).ToList();
                var withEvidence = evidence.Where(x => x.ScopeId == s.Id).Select(x => x.Code).Distinct(StringComparer.Ordinal).Count();
                var last = se.Where(e => e.ReviewedAt.HasValue).Select(e => (DateTimeOffset?)e.ReviewedAt!.Value).Max();
                return new NistScopeView(s.Id, ScopeName(s), s.Description, total,
                    se.Count(e => !e.NotApplicable && e.CurrentLevel.HasValue),
                    se.Count(e => e.NotApplicable),
                    se.Count(e => !e.NotApplicable && !e.CurrentLevel.HasValue),
                    withEvidence, last);
            }).ToList();

        var lastReviewed = scopeViews.Select(s => s.LastReviewedAt).Where(d => d.HasValue).Max();
        return new NistAssessmentView(a.Id, a.Name, a.Description, a.Status.ToString(), a.StartDate, a.EndDate,
            a.MethodologyVersion, fvName, a.CreatedAt, lastReviewed, scopeViews);
    }

    private async Task<NistSubcategoryDetailView> BuildDetailAsync(ScopeContext ctx, NistSubcategory sub, CancellationToken ct)
    {
        var category = ctx.Catalog.Functions.SelectMany(f => f.Categories).First(c => c.Id == sub.CategoryId);
        var fn = ctx.Catalog.Functions.First(f => f.Id == category.FunctionId);
        var language = _language?.Get(sub.Code);
        var eval = ctx.Evaluation(sub.Id);
        var linked = await ActiveEvidenceAsync(ctx.Scope.Id, sub.Code, ct);
        var available = await AvailableEvidenceAsync(sub.Code, linked, ct);

        NistPostureReadingView? posture = null;
        var state = await _db.TenantControlStates.AsNoTracking().FirstOrDefaultAsync(s => s.SubcategoryId == sub.Id, ct);
        if (state is not null)
            posture = new NistPostureReadingView(state.Status.ToString(), state.LastVerdictSource.ToString(),
                state.CurrentScore, sub.MaxScorePoints, state.LastEvaluatedAt);

        var evidenceCount = linked.Count;
        return new NistSubcategoryDetailView(
            ctx.Assessment.Id, ctx.Scope.Id, sub.Code, fn.Code, fn.Name, category.Code, category.Name,
            language?.Title ?? sub.Code,
            language?.Summary ?? "",
            language?.Impact ?? "",
            language?.InitialAction ?? "",
            sub.Description,
            string.IsNullOrWhiteSpace(sub.ImplementationExamples) ? null : sub.ImplementationExamples,
            eval is null ? null : EvaluationView(eval, evidenceCount),
            linked.Select(EvidenceView).ToList(),
            available,
            posture,
            ctx.Catalog.MaturityLevels.OrderBy(l => l.Level).Select(l => new NistMaturityLevelView(l.Level, l.Name, l.Description)).ToList(),
            ctx.Assessment.MethodologyVersion);
    }

    private async Task<List<Evidence>> ActiveEvidenceAsync(Guid scopeId, string code, CancellationToken ct)
    {
        var rows = await _db.Evidence.AsNoTracking()
            .Where(e => e.AssessmentScopeId == scopeId && e.SubcategoryCode == code && e.RemovedAt == null)
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

    private SubcategoryProfileScore ProfileScoreOf(NistSubcategory s, ScopeContext ctx)
    {
        var e = ctx.Evaluation(s.Id);
        return new SubcategoryProfileScore(s.Code, e?.CurrentLevel, e?.TargetLevel, e?.NotApplicable ?? false);
    }

    private NistSubcategoryRowView RowOf(NistSubcategory s, ScopeContext ctx)
    {
        var e = ctx.Evaluation(s.Id);
        var evidence = ctx.EvidenceCount(s.Code);
        return new NistSubcategoryRowView(s.Code, TitleOf(s), StateOf(e, evidence), e?.CurrentLevel, e?.TargetLevel, e?.Gap,
            e?.OwnerName, evidence, e?.ReviewedAt);
    }

    private string TitleOf(NistSubcategory s) => _language?.Get(s.Code)?.Title ?? s.Code;

    internal static string StateOf(SubcategoryEvaluation? e, int evidenceCount)
    {
        if (e is null) return evidenceCount > 0 ? NistSubcategoryStates.InProgress : NistSubcategoryStates.NotEvaluated;
        if (e.NotApplicable) return NistSubcategoryStates.NotApplicable;
        if (e.CurrentLevel is not null) return NistSubcategoryStates.Evaluated;
        var anyContent = e.TargetLevel is not null || evidenceCount > 0
            || new[] { e.CurrentComments, e.TargetComments, e.Rationale, e.Gaps, e.RiskImpact, e.ImprovementGuidance, e.OwnerName }
                .Any(t => !string.IsNullOrWhiteSpace(t));
        return anyContent ? NistSubcategoryStates.InProgress : NistSubcategoryStates.NotEvaluated;
    }

    private static NistProfileScoreView ProfileView(ProfileScore p, int evaluated) =>
        new(p.RefCode, p.Current, p.Target, p.Gap, p.Subcategories, p.WithCurrent, p.WithTarget, p.WithGap, p.NotApplicable, evaluated);

    private static NistEvaluationView EvaluationView(SubcategoryEvaluation e, int evidenceCount) => new(
        e.Id, StateOf(e, evidenceCount), e.CurrentLevel, e.TargetLevel, e.Gap, e.NotApplicable,
        e.CurrentComments, e.TargetComments, e.Rationale, e.Gaps, e.RiskImpact, e.ImprovementGuidance, e.OwnerName,
        e.EvaluatedBy.ToString(), e.ReviewedByName, e.ReviewedAt, e.Version);

    private static NistEvidenceView EvidenceView(Evidence e) => new(
        e.Id, e.OriginKind.ToString(), e.Type.ToString(), e.Title ?? "(sem título)", e.Notes, e.Uri, e.OriginRef, e.OriginLabel,
        e.OriginScope, e.CollectedAt, e.CreatedAt, e.RecordedByName);

    private static string ScopeName(AssessmentScope s) => string.IsNullOrWhiteSpace(s.Name) ? "Escopo sem nome" : s.Name;

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

    private static string KnightStatusLabel(KnightIndicatorStatus s) => s switch
    {
        KnightIndicatorStatus.Passed => "Aprovado",
        KnightIndicatorStatus.Exposed => "Reprovado",
        KnightIndicatorStatus.Mitigated => "Mitigado por controle compensatório",
        KnightIndicatorStatus.NotEvaluated => "Não avaliado",
        KnightIndicatorStatus.Error => "Erro de leitura",
        _ => "Não se aplica",
    };

    private static string DocumentTypeLabel(GovernanceDocumentType t) => t switch
    {
        GovernanceDocumentType.Politica => "Política",
        GovernanceDocumentType.Norma => "Norma",
        GovernanceDocumentType.Diretriz => "Diretriz",
        GovernanceDocumentType.Procedimento => "Procedimento",
        GovernanceDocumentType.Contrato => "Contrato",
        _ => "Outro",
    };

    private static string DocumentStatusLabel(GovernanceStatus s) => s switch
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

    private static string Required(string? value, string emptyMessage, int max, string field)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new NistAssessmentValidationException(emptyMessage);
        if (v.Length > max) throw new NistAssessmentValidationException($"{field} aceita no máximo {max} caracteres.");
        return v;
    }

    private static string? Optional(string? value, int max, string field)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return null;
        if (v.Length > max) throw new NistAssessmentValidationException($"{field} aceita no máximo {max} caracteres.");
        return v;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private void RequireTenant()
    {
        if (_tenant.TenantId is not Guid id || id == Guid.Empty)
            throw new NistAssessmentNotFoundException("Tenant não resolvido no contexto.");
    }
}
