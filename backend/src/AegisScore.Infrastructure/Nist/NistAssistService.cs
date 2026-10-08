using System;
using System.Collections.Concurrent;
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
using AegisScore.Infrastructure.Ai;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Uma geração por item por vez (na réplica): dois cliques ou duas abas pedindo a mesma assistência
/// não disparam duas chamadas ao provedor.
/// </summary>
public sealed class NistAssistInFlight
{
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);

    public bool TryEnter(string key) => _keys.TryAdd(key, 0);

    public void Exit(string key) => _keys.TryRemove(key, out _);
}

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Assistência contextual de IA da jornada NIST, sobre a infraestrutura de IA EXISTENTE: o mesmo
/// <see cref="IAiAssessmentService"/> roteado pelo gate tenant-scoped (real só com provedor configurado E tenant autorizado;
/// senão o motor simulado, dito como demonstração), os mesmos limites, cancelamento e indisponibilidade.
///
/// Gera SUGESTÕES identificadas — amarradas à impressão digital do contexto — e as guarda como gerações (sem prompt, sem
/// resposta bruta, sem segredo). Nada aqui confirma avaliação, realiza procedimento, aprova revisão, conclui plano, altera
/// severidade ou recalcula médias. O único conteúdo gravado por este serviço é o RESUMO EXECUTIVO que a pessoa aceita.
/// </summary>
public sealed class NistAssistService : INistAssistService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly IControlLanguageCatalog? _language;
    private readonly IAiAssessmentService _ai;
    private readonly IAiFreeTierGate _gate;
    private readonly IAiTenantResolver _resolver;
    private readonly INistPublicationService _publication;
    private readonly NistAssistInFlight _inFlight;
    private readonly ILogger<NistAssistService> _log;

    public NistAssistService(
        AegisScoreDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        IAiAssessmentService ai,
        IAiFreeTierGate gate,
        IAiTenantResolver resolver,
        INistPublicationService publication,
        NistAssistInFlight inFlight,
        IControlLanguageCatalog? language = null,
        ILogger<NistAssistService>? log = null)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
        _ai = ai;
        _gate = gate;
        _resolver = resolver;
        _publication = publication;
        _inFlight = inFlight;
        _language = language;
        _log = log ?? NullLogger<NistAssistService>.Instance;
    }

    private NistAssistContextBuilder Builder => new(_db, _language);

    // =============================================================================================
    //  Disponibilidade (o gate de sempre, com a diferença dita)
    // =============================================================================================

    public async Task<NistAssistAvailabilityView> AvailabilityAsync(CancellationToken ct = default)
    {
        if (_gate.Mode == AiMode.Disabled)
            return new("Disabled", "IA desativada", "A assistência de IA está desativada neste ambiente. A jornada segue manual.", false);
        var external = _gate.Mode is AiMode.ExternalDemo or AiMode.ExternalEnterprise;
        if (!external)
            return new("Simulated", "Assistência simulada (demonstração)",
                "Motor simulado por regras fixas: sem provedor, sem análise real de documentos. Use para conhecer a jornada.", true);
        if (!_gate.ProviderConfigured)
            return new("ProviderNotConfigured", "Provedor de IA não configurado",
                "O modo externo está selecionado, mas sem chave: a assistência responde em demonstração (motor simulado), sem análise real.", true);
        if (!_gate.IsExternalAllowedForSlug(await _resolver.GetCurrentSlugAsync(ct)))
            return new("ExternalBlockedForTenant", "Provedor não autorizado para este tenant",
                "Este tenant não está autorizado a enviar dados ao provedor externo: a assistência responde em demonstração (motor simulado).", true);
        return _gate.Mode == AiMode.ExternalDemo
            ? new("Real", "IA real autorizada (modo demonstrativo)", "Somente dados sintéticos ou demonstrativos. O contexto enviado é minimizado e não leva nomes de pessoas.", true)
            : new("Real", "IA real autorizada", "Uso corporativo autorizado para este tenant. O contexto enviado é minimizado e não leva nomes de pessoas.", true);
    }

    // =============================================================================================
    //  Subcategoria
    // =============================================================================================

    public async Task<NistAssistContextView> SubcategoryContextAsync(Guid assessmentId, Guid cycleId, Guid scopeId, string code, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var sub = ctx.FindSubcategory(code);
        var built = await Builder.SubcategoryAsync(ctx, sub, ct);
        return await ContextViewAsync(built, NistAssistKind.Subcategory, cycleId, scopeId, sub.Code, null, null, ct);
    }

    public async Task<NistAssistView> AssistSubcategoryAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, string code, NistAssistRequest request, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(request);
        var availability = await RequireAvailableAsync(ct);
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var sub = ctx.FindSubcategory(code);
        var built = await Builder.SubcategoryAsync(ctx, sub, ct);
        return await GenerateAsync(NistAssistKind.Subcategory, null, assessmentId, cycleId, scopeId, sub.Code, null, built, availability,
            request.Reuse, actor, async () =>
            {
                var again = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
                return (await Builder.SubcategoryAsync(again, again.FindSubcategory(sub.Code), ct)).Fingerprint;
            }, ct);
    }

    // =============================================================================================
    //  Achado
    // =============================================================================================

    public async Task<NistAssistContextView> FindingContextAsync(Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, string? focus, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var f = NistAssistSections.NormalizeFocus(focus);
        var (ctx, finding, sub) = await LoadFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
        var built = await Builder.FindingAsync(ctx, sub, finding, f, ct);
        return await ContextViewAsync(built, NistAssistKind.Finding, cycleId, scopeId, sub.Code, findingId, f, ct);
    }

    public async Task<NistAssistView> AssistFindingAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, NistAssistRequest request, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(request);
        var focus = NistAssistSections.NormalizeFocus(request.Focus);
        var availability = await RequireAvailableAsync(ct);
        var (ctx, finding, sub) = await LoadFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
        var built = await Builder.FindingAsync(ctx, sub, finding, focus, ct);
        return await GenerateAsync(NistAssistKind.Finding, focus, assessmentId, cycleId, scopeId, sub.Code, findingId, built, availability,
            request.Reuse, actor, async () =>
            {
                var (c2, f2, s2) = await LoadFindingAsync(assessmentId, cycleId, scopeId, findingId, ct);
                return (await Builder.FindingAsync(c2, s2, f2, focus, ct)).Fingerprint;
            }, ct);
    }

    internal async Task<(NistScopeContext Ctx, NistFinding Finding, NistSubcategory Sub)> LoadFindingAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, Guid findingId, CancellationToken ct)
    {
        var ctx = await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var finding = ctx.Findings.FirstOrDefault(f => f.Id == findingId)
                      ?? throw new NistAssessmentNotFoundException("Achado não encontrado nesta avaliação, rodada e escopo.");
        return (ctx, finding, ctx.FindSubcategory(finding.SubcategoryCode));
    }

    // =============================================================================================
    //  Resumo executivo
    // =============================================================================================

    public async Task<NistAssistContextView> ExecutiveContextAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        var built = await ExecutiveBuiltAsync(assessmentId, cycleId, scopeId, ct);
        return await ContextViewAsync(built, NistAssistKind.ExecutiveSummary, cycleId, scopeId, null, null, null, ct);
    }

    public async Task<NistAssistView> AssistExecutiveAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, NistAssistRequest request, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(request);
        var availability = await RequireAvailableAsync(ct);
        var built = await ExecutiveBuiltAsync(assessmentId, cycleId, scopeId, ct);
        return await GenerateAsync(NistAssistKind.ExecutiveSummary, null, assessmentId, cycleId, scopeId, null, null, built, availability,
            request.Reuse, actor, async () => await BasisAsync(assessmentId, cycleId, scopeId, ct), ct);
    }

    private async Task<NistAssistBuilt> ExecutiveBuiltAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct)
    {
        var core = await _publication.BuildBasisReportAsync(assessmentId, cycleId, scopeId, ct);
        return NistAssistContextBuilder.Executive(core, NistReportCanonical.Fingerprint(core));
    }

    /// <summary>Impressão digital da BASE do resumo: o relatório determinístico sem interpretação e sem publicação.</summary>
    private async Task<string> BasisAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct) =>
        NistReportCanonical.Fingerprint(await _publication.BuildBasisReportAsync(assessmentId, cycleId, scopeId, ct));

    public async Task<NistExecutiveSummaryView?> GetExecutiveSummaryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var summary = await _db.NistExecutiveSummaries.AsNoTracking().FirstOrDefaultAsync(x => x.CycleId == cycleId && x.AssessmentScopeId == scopeId, ct);
        if (summary is null) return null;
        return await SummaryViewAsync(summary, await BasisAsync(assessmentId, cycleId, scopeId, ct), ct);
    }

    public async Task<NistExecutiveSummaryView> SaveExecutiveSummaryAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, SaveNistExecutiveSummaryCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);

        var titles = NistAssistSections.ExecutiveKeys.ToDictionary(k => k.Key, k => k.Title, StringComparer.Ordinal);
        var sections = new List<NistExecutiveSectionView>();
        foreach (var s in command.Sections ?? Array.Empty<NistExecutiveSectionInput>())
        {
            var key = (s.Key ?? "").Trim();
            if (!titles.TryGetValue(key, out var title))
                throw new NistAssessmentValidationException($"Seção desconhecida no resumo executivo: \"{key}\".");
            if (sections.Any(x => x.Key == key))
                throw new NistAssessmentValidationException($"A seção \"{title}\" aparece mais de uma vez.");
            var text = Optional(s.Text, 2000, $"A seção \"{title}\"");
            if (text is not null) sections.Add(new NistExecutiveSectionView(key, title, text));
        }
        if (sections.Count == 0) throw new NistAssessmentValidationException("O resumo executivo precisa de pelo menos uma seção com texto.");
        sections = titles.Keys.Select(k => sections.FirstOrDefault(x => x.Key == k)).Where(x => x is not null).Select(x => x!).ToList();

        var basis = await BasisAsync(assessmentId, cycleId, scopeId, ct);
        NistAiAssistance? generation = null;
        var stale = false;
        if (command.AssistanceId is { } assistanceId)
            (generation, stale) = await NistAssistProvenance.ResolveAsync(_db, new NistAssistanceReference(assistanceId, null, command.AcknowledgeStale),
                NistAssistKind.ExecutiveSummary, assessmentId, cycleId, scopeId, null, null, _ => Task.FromResult(basis), ct);

        var existing = await _db.NistExecutiveSummaries.FirstOrDefaultAsync(x => x.CycleId == cycleId && x.AssessmentScopeId == scopeId, ct);
        if (existing is null && command.ExpectedVersion != 0)
            throw new NistAssessmentConflictException("O resumo executivo desta rodada foi retirado por outra pessoa. Recarregue antes de gravar.");
        if (existing is not null && existing.Version != command.ExpectedVersion)
            throw new NistAssessmentConflictException(
                $"O resumo executivo foi alterado por outra pessoa (versão {existing.Version}, você enviou {command.ExpectedVersion}). Recarregue: o seu texto continua na tela.");

        var now = _clock.GetUtcNow();
        var applicable = generation is null ? new Dictionary<string, string>() : NistAssistProvenance.ApplicableOf(generation);
        var edited = generation is not null && (sections.Count != applicable.Count
                                                || sections.Any(s => !applicable.TryGetValue(s.Key, out var g) || NistAssistProvenance.Hash(g) != NistAssistProvenance.Hash(s.Text)));
        var json = JsonSerializer.Serialize(sections, Json);
        var hash = NistAssistProvenance.Hash(json);
        var creating = existing is null;
        var entity = existing ?? new NistExecutiveSummary { AssessmentId = assessmentId, CycleId = cycleId, AssessmentScopeId = scopeId, CreatedAt = now };
        var previousHash = entity.ContentHash;
        entity.SectionsJson = json;
        entity.AssistanceId = generation?.Id;
        entity.BasisFingerprint = basis;
        entity.Edited = edited;
        entity.StaleAcknowledged = stale;
        entity.AcceptedByAccountId = actor.AccountId;
        entity.AcceptedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        entity.AcceptedAt = now;
        entity.ContentHash = hash;
        // Uma revisão pertence à ACEITAÇÃO que examinou (texto, base, origem e autor): aceitar de novo — mesmo com o mesmo texto,
        // sobre outra base ou por quem tinha revisado — exige nova revisão de outra pessoa. A trilha guarda a revisão anterior.
        var hadReview = entity.ReviewedAt is not null;
        entity.ReviewedByAccountId = null;
        entity.ReviewedByName = null;
        entity.ReviewedAt = null;
        entity.ReviewedContentHash = null;
        entity.ReviewNote = null;
        entity.Version += 1;
        if (creating) _db.NistExecutiveSummaries.Add(entity);

        if (generation is not null)
            NistAssistProvenance.Record(_db, generation, stale, NistAssistTarget.ExecutiveSummary, entity.Id,
                sections.Select(s => new NistAssistProvenance.Incorporated(s.Key, s.Title.ToLowerInvariant(), s.Text)).ToList(), actor, now);
        var changes = new List<NistFieldChange>();
        Diff(changes, "executiveSummary", "resumo executivo", creating ? null : previousHash, hash);
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, null, "ExecutiveSummary", entity.Id, creating ? "Accepted" : "Updated",
            (generation is null ? "Resumo executivo redigido pela pessoa" : $"Resumo executivo aceito a partir de sugestão {(generation.Mode == NistAssistEngineMode.Real ? "da IA" : "SIMULADA")}" + (edited ? ", com edição" : ", sem edição")) +
            $" (versão {entity.Version}). Não altera notas, contagens nem classificações; entra na próxima publicação enquanto a base for a mesma." +
            (hadReview ? " A revisão anterior deixou de valer: esta aceitação precisa de nova revisão de outra pessoa." : ""), changes);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("O resumo executivo foi alterado por outra pessoa enquanto a sua gravação estava em curso. Recarregue.");
        }
        catch (DbUpdateException) when (creating)
        {
            throw new NistAssessmentConflictException("Outra pessoa acabou de gravar o resumo executivo desta rodada. Recarregue: o seu texto continua na tela.");
        }
        _db.ChangeTracker.Clear();
        return await SummaryViewAsync(entity, basis, ct);
    }

    public async Task<NistExecutiveSummaryView> ReviewExecutiveSummaryAsync(
        Guid assessmentId, Guid cycleId, Guid scopeId, ReviewNistExecutiveSummaryCommand command, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        ArgumentNullException.ThrowIfNull(command);
        await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var entity = await _db.NistExecutiveSummaries.FirstOrDefaultAsync(x => x.CycleId == cycleId && x.AssessmentScopeId == scopeId, ct)
                     ?? throw new NistAssessmentNotFoundException("Não há resumo executivo aceito nesta rodada e escopo.");
        if (entity.Version != command.ExpectedVersion)
            throw new NistAssessmentConflictException("O resumo executivo mudou depois que você o leu. Recarregue e revise a versão vigente.");
        if (actor.AccountId is not { } reviewer)
            throw new NistAssessmentValidationException("Autor da revisão não identificado na sessão.");
        if (entity.AcceptedByAccountId == reviewer)
            throw new NistAssessmentValidationException("A revisão precisa ser feita por outra pessoa: você aceitou a versão vigente do resumo.");
        var basis = await BasisAsync(assessmentId, cycleId, scopeId, ct);
        if (!string.Equals(basis, entity.BasisFingerprint, StringComparison.Ordinal))
            throw new NistAssessmentConflictException("A rodada mudou depois que o resumo foi aceito: ele precisa ser revisto e aceito de novo antes da revisão.");

        var now = _clock.GetUtcNow();
        entity.ReviewedByAccountId = reviewer;
        entity.ReviewedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName);
        entity.ReviewedAt = now;
        entity.ReviewedContentHash = entity.ContentHash;
        entity.ReviewNote = Optional(command.Note, 2000, "A nota da revisão");
        entity.Version += 1;
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, null, "ExecutiveSummary", entity.Id, "Reviewed",
            $"Resumo executivo revisado por outra pessoa (conteúdo da versão {entity.Version - 1}).");
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("O resumo executivo foi alterado por outra pessoa enquanto a revisão era gravada.");
        }
        _db.ChangeTracker.Clear();
        return await SummaryViewAsync(entity, basis, ct);
    }

    public async Task WithdrawExecutiveSummaryAsync(Guid assessmentId, Guid cycleId, Guid scopeId, int expectedVersion, RemediationActor actor, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        await LoadScopeContextAsync(_db, assessmentId, cycleId, scopeId, ct);
        var entity = await _db.NistExecutiveSummaries.FirstOrDefaultAsync(x => x.CycleId == cycleId && x.AssessmentScopeId == scopeId, ct)
                     ?? throw new NistAssessmentNotFoundException("Não há resumo executivo aceito nesta rodada e escopo.");
        if (entity.Version != expectedVersion)
            throw new NistAssessmentConflictException("O resumo executivo mudou depois que você o leu. Recarregue antes de retirar.");
        var now = _clock.GetUtcNow();
        _db.NistExecutiveSummaries.Remove(entity);
        var changes = new List<NistFieldChange>();
        Diff(changes, "executiveSummary", "resumo executivo", entity.ContentHash, null);
        Audit(_db, actor, now, assessmentId, cycleId, scopeId, null, "ExecutiveSummary", entity.Id, "Withdrawn",
            "Resumo executivo retirado: as próximas publicações saem sem interpretação. Fotografias já publicadas não mudam.", changes);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new NistAssessmentConflictException("O resumo executivo foi alterado por outra pessoa enquanto a retirada era gravada.");
        }
        _db.ChangeTracker.Clear();
    }

    private async Task<NistExecutiveSummaryView> SummaryViewAsync(NistExecutiveSummary s, string currentBasis, CancellationToken ct)
    {
        var generation = s.AssistanceId is { } id ? await _db.NistAiAssistances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) : null;
        var sections = JsonSerializer.Deserialize<List<NistExecutiveSectionView>>(s.SectionsJson, Json) ?? new List<NistExecutiveSectionView>();
        return new NistExecutiveSummaryView(s.Id, sections, generation is null ? "Manual" : "Assisted", generation?.Mode.ToString(), s.AssistanceId,
            generation?.GeneratedAt, generation?.RequestedByName, s.AcceptedByName, s.AcceptedAt, s.Edited, s.StaleAcknowledged,
            string.Equals(s.BasisFingerprint, currentBasis, StringComparison.Ordinal),
            s.ReviewedByName, s.ReviewedAt,
            ReviewIsCurrent(s) && string.Equals(s.BasisFingerprint, currentBasis, StringComparison.Ordinal),
            s.ReviewNote, s.Version);
    }

    /// <summary>
    /// A revisão vale para a aceitação VIGENTE: examinou este mesmo conteúdo e foi feita por outra pessoa que não quem aceitou. Registros
    /// gravados antes desta regra (revisão mantida numa nova aceitação, ou revisor que depois aceitou) deixam de contar como revisados.
    /// </summary>
    internal static bool ReviewIsCurrent(NistExecutiveSummary s) =>
        s.ReviewedAt is not null
        && s.ReviewedByAccountId is { } reviewer
        && reviewer != s.AcceptedByAccountId
        && string.Equals(s.ReviewedContentHash, s.ContentHash, StringComparison.Ordinal);

    // =============================================================================================
    //  Auditor Virtual: o mesmo contexto, sem gerar sugestão
    // =============================================================================================

    public async Task<NistAuditorContext?> AuditorContextAsync(NistAuditorSelection selection, CancellationToken ct = default)
    {
        RequireTenant(_tenant);
        NistScopeContext ctx;
        try
        {
            ctx = await LoadScopeContextAsync(_db, selection.AssessmentId, selection.CycleId, selection.ScopeId, ct);
        }
        catch (NistAssessmentNotFoundException)
        {
            return null;   // seleção de outro tenant, removida ou inventada: o chamador a recusa (nunca a troca por outra)
        }

        var notes = new List<string>
        {
            "Maturidade NIST (1–5, autoral do AEGIS) ≠ postura do ambiente (AEGIS Score, 0–100) ≠ score do AEGIS KNIGHT.",
            "O Auditor sugere; só a gravação humana na tela confirma uma avaliação. Lacuna atual × alvo não é, por si, achado crítico.",
        };
        var code = (selection.SubcategoryCode ?? "").Trim().ToUpperInvariant();
        var sub = code.Length == 0 ? null : ctx.AllSubcategories.FirstOrDefault(s => s.Code == code);
        if (code.Length > 0 && sub is null) return null;   // código fora do catálogo da avaliação: seleção inválida, nunca ignorada
        if (sub is not null)
        {
            var built = await Builder.SubcategoryAsync(ctx, sub, ct);
            var facts = built.Registry.Where(r => r.Kind != "Catalog").Take(14)
                .Select(r => $"[{r.Key}] {KindLabel(r.Kind)} — {r.Title} ({NistAssistBasis.Label(r.Basis)}{(r.Status is null ? "" : $"; {r.Status}")})" +
                             (r.IsDemo ? " — demonstração" : "") + (r.ContentExamined ? "" : " — conteúdo não examinado"))
                .ToList();
            var sources = built.Registry.Take(MaxAuditorSources).Select(AuditorSourceOf).ToList();
            if (built.Registry.Count > MaxAuditorSources)
                notes.Add($"Fontes da subcategoria resumidas: {MaxAuditorSources} de {built.Registry.Count} (abra a subcategoria para ver todas).");
            return new NistAuditorContext(ctx.Assessment.Name, ctx.Cycle.Name, ScopeName(ctx.Scope), $"{sub.Code} — {_language?.Get(sub.Code)?.Title ?? sub.Code}",
                built.Summary, facts, notes, sources);
        }

        var core = await _publication.BuildBasisReportAsync(selection.AssessmentId, selection.CycleId, selection.ScopeId, ct);
        var executive = NistAssistContextBuilder.Executive(core, "");
        var lines = executive.Registry.Where(r => r.Kind is "Metric" or "Finding").Take(20)
            .Select(r => r.Kind == "Metric" ? $"{r.Title}: {r.Detail}" : $"Achado: {r.Title} ({r.Status})").ToList();
        var registry = executive.Registry.Where(r => r.Kind is not "Priority").ToList();
        var findings = registry.Where(r => r.Kind == "Finding").ToList();
        var gaps = registry.Where(r => r.Kind == "Gap").ToList();
        var chosen = registry.Where(r => r.Kind is "Metric" or "Limitation")
            .Concat(gaps.Take(MaxAuditorGaps)).Concat(findings.Take(MaxAuditorFindings)).ToList();
        if (findings.Count > MaxAuditorFindings) notes.Add($"Achados resumidos: {MaxAuditorFindings} de {findings.Count} registrados nesta rodada e escopo.");
        if (gaps.Count > MaxAuditorGaps) notes.Add($"Lacunas resumidas: {MaxAuditorGaps} de {gaps.Count} lacunas confirmadas (as de maior distância até o alvo).");
        return new NistAuditorContext(ctx.Assessment.Name, ctx.Cycle.Name, ScopeName(ctx.Scope), null, executive.Summary, lines, notes,
            chosen.Select(AuditorSourceOf).ToList());
    }

    private const int MaxAuditorSources = 25;
    private const int MaxAuditorFindings = 12;
    private const int MaxAuditorGaps = 10;

    private static NistAuditorSource AuditorSourceOf(NistAssistSourceRecord r) =>
        new(r.Key, r.Kind, r.Basis, r.Title, r.Detail, r.Date, r.Status, r.IsDemo, r.ContentExamined, r.Limitation, r.LinkTarget, r.LinkId, r.LinkCode);

    // =============================================================================================
    //  Geração
    // =============================================================================================

    private async Task<NistAssistAvailabilityView> RequireAvailableAsync(CancellationToken ct)
    {
        var availability = await AvailabilityAsync(ct);
        if (!availability.CanGenerate)
            throw new NistAiUnavailableException(availability.Detail, "Disabled");
        return availability;
    }

    private async Task<NistAssistView> GenerateAsync(
        NistAssistKind kind, string? focus, Guid assessmentId, Guid cycleId, Guid scopeId, string? code, Guid? findingId,
        NistAssistBuilt built, NistAssistAvailabilityView availability, bool reuse, RemediationActor actor,
        Func<Task<string>> rebuildFingerprint, CancellationToken ct)
    {
        var expected = availability.State == "Real" ? NistAssistEngineMode.Real : NistAssistEngineMode.Simulated;
        if (reuse && await LatestAsync(kind, cycleId, scopeId, code, findingId, focus, ct) is { } latest
            && latest.ContextFingerprint == built.Fingerprint && !latest.StaleOnArrival && latest.Mode == expected)
            return View(latest, current: true, reused: true);

        var key = $"{_tenant.TenantId}|{kind}|{cycleId}|{scopeId}|{code}|{findingId}|{focus}";
        if (!_inFlight.TryEnter(key))
            throw new NistAssessmentConflictException("Já há uma geração em curso para este item. Aguarde o resultado antes de pedir outra.");
        try
        {
            NistAssistDraft draft;
            try
            {
                draft = await _ai.AssistNistAsync(built.Prompt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // a pessoa saiu ou cancelou: nada é gravado
            }
            catch (AiInvalidResponseException ex)
            {
                _log.LogWarning(ex, "Assistência NIST: resposta fora do contrato ({Kind}).", kind);
                throw new NistAiUnavailableException("A IA respondeu fora do formato esperado. A resposta foi descartada e nada foi gravado; a jornada segue manual.", "InvalidResponse");
            }
            catch (AiQuotaExhaustedException ex)
            {
                _log.LogWarning(ex, "Assistência NIST: cota do provedor esgotada.");
                throw new NistAiUnavailableException("A cota do provedor de IA está esgotada agora. A jornada segue manual.", "Unavailable");
            }
            catch (AiUnavailableException ex) when (ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(ex, "Assistência NIST: tempo esgotado.");
                throw new NistAiUnavailableException("A IA não respondeu no tempo limite. Nada foi gravado; tente de novo ou siga manualmente.", "Timeout");
            }
            catch (OperationCanceledException ex)
            {
                _log.LogWarning(ex, "Assistência NIST: tempo esgotado.");
                throw new NistAiUnavailableException("A IA não respondeu no tempo limite. Nada foi gravado; tente de novo ou siga manualmente.", "Timeout");
            }
            catch (Exception ex) when (ex is not NistAiUnavailableException)
            {
                _log.LogWarning(ex, "Assistência NIST indisponível ({Kind}).", kind);
                throw new NistAiUnavailableException("A IA não está disponível agora. Nada foi gravado; a jornada segue manual.", "Unavailable");
            }

            var validation = NistAssistValidator.Validate(draft, built, kind.ToString(), focus);
            // Alteração concorrente durante a geração: o contexto de agora não é o enviado → a sugestão já nasce desatualizada.
            var staleOnArrival = !string.Equals(await rebuildFingerprint(), built.Fingerprint, StringComparison.Ordinal);
            var now = _clock.GetUtcNow();
            var entity = new NistAiAssistance
            {
                AssessmentId = assessmentId,
                CycleId = cycleId,
                AssessmentScopeId = scopeId,
                SubcategoryCode = code,
                FindingId = findingId,
                Kind = kind,
                Focus = focus,
                ContextFingerprint = built.Fingerprint,
                ContextSummary = Truncate(built.Summary, 500),
                SourcesJson = JsonSerializer.Serialize(built.Registry, Json),
                OutputJson = JsonSerializer.Serialize(validation.Output, Json),
                ApplicableJson = JsonSerializer.Serialize(validation.Applicable, Json),
                ValidationNotesJson = validation.Notes.Count == 0 ? null : JsonSerializer.Serialize(validation.Notes, Json),
                Mode = draft.Simulated ? NistAssistEngineMode.Simulated : NistAssistEngineMode.Real,
                Availability = availability.State,
                MethodologyVersion = Truncate(built.MethodologyVersion, 50),
                StaleOnArrival = staleOnArrival,
                RequestedByAccountId = actor.AccountId,
                RequestedByName = string.IsNullOrWhiteSpace(actor.DisplayName) ? null : Truncate(actor.DisplayName.Trim(), MaxName),
                GeneratedAt = now,
                CreatedAt = now,
            };
            _db.NistAiAssistances.Add(entity);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            return View(entity, current: !staleOnArrival, reused: false);
        }
        finally
        {
            _inFlight.Exit(key);
        }
    }

    private async Task<NistAiAssistance?> LatestAsync(NistAssistKind kind, Guid cycleId, Guid scopeId, string? code, Guid? findingId, string? focus, CancellationToken ct)
    {
        var rows = await _db.NistAiAssistances.AsNoTracking()
            .Where(x => x.CycleId == cycleId && x.AssessmentScopeId == scopeId && x.Kind == kind && x.SubcategoryCode == code
                        && x.FindingId == findingId && x.Focus == focus)
            .ToListAsync(ct);
        // Ordenado em memória: o SQLite não ordena DateTimeOffset.
        return rows.OrderByDescending(x => x.GeneratedAt.UtcTicks).ThenByDescending(x => x.Id).FirstOrDefault();
    }

    private async Task<NistAssistContextView> ContextViewAsync(
        NistAssistBuilt built, NistAssistKind kind, Guid cycleId, Guid scopeId, string? code, Guid? findingId, string? focus, CancellationToken ct)
    {
        var latest = await LatestAsync(kind, cycleId, scopeId, code, findingId, focus, ct);
        return new NistAssistContextView(built.Fingerprint, built.Summary, Sources(built.Registry), await AvailabilityAsync(ct),
            latest is null ? null : View(latest, current: latest.ContextFingerprint == built.Fingerprint && !latest.StaleOnArrival, reused: false));
    }

    // =============================================================================================
    //  Leitura
    // =============================================================================================

    internal static NistAssistView View(NistAiAssistance g, bool current, bool reused)
    {
        var registry = JsonSerializer.Deserialize<List<NistAssistSourceRecord>>(g.SourcesJson, Json) ?? new List<NistAssistSourceRecord>();
        var output = JsonSerializer.Deserialize<NistAssistOutput>(g.OutputJson, Json)
                     ?? new NistAssistOutput(Array.Empty<NistAssistValidatedSection>(), null, null, Array.Empty<NistAssistValidatedProcedure>());
        var applicable = NistAssistProvenance.ApplicableOf(g);
        var notes = string.IsNullOrWhiteSpace(g.ValidationNotesJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(g.ValidationNotesJson, Json) ?? new List<string>();
        if (g.StaleOnArrival) notes.Insert(0, "O contexto mudou enquanto a sugestão era gerada: ela já nasceu desatualizada. Gere outra antes de aproveitar.");

        var specs = NistAssistSections.For(g.Kind.ToString(), g.Focus);
        var sections = specs.Select(spec =>
        {
            var items = output.Sections.FirstOrDefault(s => s.Key == spec.Key)?.Items ?? Array.Empty<NistAssistValidatedItem>();
            return new NistAssistSectionView(spec.Key, spec.Title, spec.Hint,
                items.Select(i => new NistAssistItemView(i.Text, i.Sources, i.Basis, NistAssistBasis.Label(i.Basis))).ToList());
        }).ToList();

        var simulated = g.Mode == NistAssistEngineMode.Simulated;
        var modeLabel = simulated
            ? g.Availability switch
            {
                "ProviderNotConfigured" => "Demonstração — provedor de IA não configurado (motor simulado, sem análise real)",
                "ExternalBlockedForTenant" => "Demonstração — tenant não autorizado ao provedor (motor simulado, sem análise real)",
                _ => "Demonstração — motor simulado, sem análise real",
            }
            : "Gerada por IA (provedor autorizado para este tenant)";
        var disclaimer = g.Kind switch
        {
            NistAssistKind.ExecutiveSummary =>
                "Interpretação sugerida dos indicadores determinísticos do AEGIS. A IA não calcula nem altera notas, contagens, cobertura ou classificações; " +
                "só o texto aceito por pessoa entra numa publicação futura.",
            NistAssistKind.Finding =>
                "Sugestão para entender o achado e propor tratamento. Não executa, não aprova, não mitiga nem conclui; não cria nem altera plano sem a sua ação.",
            _ => "Sugestão para interpretar o resultado esperado e as evidências. Aplicá-la ao rascunho não confirma a avaliação, não realiza procedimento " +
                 "e não aprova revisão — só a sua gravação registra.",
        } + (simulated ? " Esta é uma DEMONSTRAÇÃO: nenhum documento ou dado foi analisado por IA real." : "");

        return new NistAssistView(
            g.Id, g.Kind.ToString(), g.Focus, g.AssessmentId, g.CycleId, g.AssessmentScopeId, g.SubcategoryCode, g.FindingId,
            g.Mode.ToString(), g.Availability, modeLabel, g.GeneratedAt, g.RequestedByName, g.ContextFingerprint, g.ContextSummary,
            current, g.StaleOnArrival, reused, Sources(registry), sections,
            output.Level is { } lv ? new NistAssistLevelView(lv.Level, lv.LevelName, lv.Rationale, lv.Sources, g.MethodologyVersion) : null,
            output.LevelNote,
            output.Procedures.Select(p => new NistAssistProcedureView(p.Method, NistLabels.Method(p.Method), p.Text, p.Sources)).ToList(),
            applicable, notes, disclaimer);
    }

    private static IReadOnlyList<NistAssistSourceView> Sources(IReadOnlyList<NistAssistSourceRecord> registry) => registry
        .Select(r => new NistAssistSourceView(r.Key, r.Kind, KindLabel(r.Kind), r.Basis, NistAssistBasis.Label(r.Basis), r.Title, r.Detail, r.Date, r.Status,
            r.IsDemo, r.ContentExamined, r.Limitation, r.LinkTarget is null ? null : new NistAssistSourceLink(r.LinkTarget, r.LinkId, r.LinkCode)))
        .ToList();

    internal static string KindLabel(string kind) => kind switch
    {
        "Catalog" => "Catálogo NIST e explicação do AEGIS",
        "Evaluation" => "Avaliação registrada",
        "Manual" => "Registro do analista",
        "Document" => "Documento",
        "DocumentNote" => "Nota do analista sobre documento",
        "KnightIndicator" => "AEGIS KNIGHT",
        "AssetInventory" => "Inventário de ativos",
        "Procedure" => "Procedimento de avaliação",
        "Finding" => "Achado",
        "Plan" => "Plano de tratamento",
        "Reference" => "Rodada anterior (referência)",
        "Gap" => "Lacuna confirmada",
        "Limitation" => "Limitação declarada",
        "Metric" => "Indicador do AEGIS",
        "Priority" => "Prioridade (critério do AEGIS)",
        _ => kind,
    };
}
