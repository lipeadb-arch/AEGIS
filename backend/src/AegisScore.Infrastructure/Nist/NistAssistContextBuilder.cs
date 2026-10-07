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
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static AegisScore.Infrastructure.Nist.NistJourneySupport;

namespace AegisScore.Infrastructure.Nist;

/// <summary>[AEGIS-NIST-AI-ASSIST-01] Uma fonte do contexto como fica registrada na geração (o que a citação pode apontar).</summary>
internal sealed record NistAssistSourceRecord(
    string Key,
    string Kind,
    string Basis,
    string Title,
    string? Detail,
    string? Date,
    string? Status,
    bool IsDemo,
    bool ContentExamined,
    string? Limitation,
    string? LinkTarget,
    string? LinkId,
    string? LinkCode);

/// <summary>O contexto montado: o que vai ao motor, as fontes citáveis, a impressão digital e a escala da metodologia.</summary>
internal sealed record NistAssistBuilt(
    NistAssistPrompt Prompt,
    IReadOnlyList<NistAssistSourceRecord> Registry,
    string Fingerprint,
    string Summary,
    string MethodologyVersion,
    IReadOnlyDictionary<int, string> LevelNames,
    bool NotApplicable);

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Monta NO SERVIDOR o contexto autorizado e pertinente de uma assistência — subcategoria, achado
/// ou resumo executivo — a partir dos registros do tenant (filtro global fail-closed), sem nome de pessoa, contato, segredo,
/// link ou documento inteiro. Cada fonte recebe uma chave citável e uma classificação:
/// <list type="bullet">
/// <item>fato sustentado: resultado normalizado do KNIGHT (com origem, data, limitação e marca de demonstração), trecho literal
/// já validado da análise documental, retrato do inventário;</item>
/// <item>relato do analista: textos da avaliação confirmada, notas de evidência, observações de procedimentos, achados e planos;</item>
/// <item>não confirmado: conteúdo herdado ou importado aguardando confirmação, rodada anterior, resumo de análise sem trecho literal;</item>
/// <item>documento não examinado: título e cadastro não comprovam conteúdo — dito assim, sem descrever o que não foi lido.</item>
/// </list>
/// A impressão digital cobre o conteúdo E as versões dos registros: mudou avaliação, evidência, procedimento, achado ou plano,
/// a sugestão feita sobre o contexto anterior fica desatualizada.
/// </summary>
internal sealed class NistAssistContextBuilder
{
    internal const string PriorityCriterion =
        "Critério do AEGIS (aegis-nist-priority-v1): achados abertos primeiro, por severidade registrada (crítica → baixa), depois " +
        "prioridade registrada (urgente → baixa) e plano atrasado; em seguida lacunas confirmadas sem achado, pela distância até o alvo. " +
        "A ordem é determinística — a IA não a altera.";

    private const int MaxText = 600;
    private const int MaxExcerpt = 500;

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly AegisScoreDbContext _db;
    private readonly IControlLanguageCatalog? _language;

    public NistAssistContextBuilder(AegisScoreDbContext db, IControlLanguageCatalog? language)
    {
        _db = db;
        _language = language;
    }

    // =============================================================================================
    //  Subcategoria
    // =============================================================================================

    public async Task<NistAssistBuilt> SubcategoryAsync(NistScopeContext ctx, NistSubcategory sub, CancellationToken ct)
    {
        var reg = new Registry();
        var versions = new List<string> { $"sub:{sub.Code}" };
        var eval = ctx.Evaluation(sub.Id);

        AddCatalog(reg, sub);
        AddEvaluation(reg, versions, eval);
        await AddEvidenceAsync(reg, versions, ctx.Scope.Id, ctx.Cycle.Id, sub.Code, null, ct);
        AddProcedures(reg, versions, ctx.Procedures.Where(p => p.SubcategoryCode == sub.Code));
        var findings = ctx.Findings.Where(f => f.SubcategoryCode == sub.Code).OrderBy(f => f.CreatedAt.UtcTicks).ThenBy(f => f.Id).ToList();
        var plans = await PlansByFindingAsync(_db, findings.Select(f => f.Id).ToList(), ct);
        foreach (var f in findings)
            AddFinding(reg, versions, f, CurrentPlan(plans.TryGetValue(f.Id, out var l) ? l : null));
        await AddReferenceAsync(reg, versions, ctx, sub, ct);

        var prompt = new NistAssistPrompt(
            NistAssistKind.Subcategory.ToString(), null, Target(ctx, sub, eval, null),
            reg.Prompt(), Array.Empty<NistAssistPromptMetric>(), Array.Empty<NistAssistPromptPriority>(),
            Methodology(ctx, null), NistAssistSections.For("Subcategory", null).Select(s => s.Key).ToList());
        var procedures = ctx.Procedures.Count(p => p.SubcategoryCode == sub.Code);
        var performed = ctx.Procedures.Count(p => p.SubcategoryCode == sub.Code && p.Status == NistProcedureStatus.Performed);
        var summary = $"{(eval is null ? "sem avaliação registrada" : $"avaliação v{eval.Version}{(eval.HumanConfirmed ? "" : " (a confirmar)")}")} · " +
                      $"{reg.Count("Manual", "Document", "KnightIndicator", "AssetInventory")} evidência(s) · {procedures} procedimento(s), {performed} realizado(s) · " +
                      $"{findings.Count} achado(s)" + (reg.Any("Reference") ? " · rodada anterior como referência" : "");
        return Built(prompt, reg, versions, summary, ctx, eval?.NotApplicable == true && eval.HumanConfirmed);
    }

    // =============================================================================================
    //  Achado
    // =============================================================================================

    public async Task<NistAssistBuilt> FindingAsync(NistScopeContext ctx, NistSubcategory sub, NistFinding finding, string focus, CancellationToken ct)
    {
        var reg = new Registry();
        var versions = new List<string> { $"sub:{sub.Code}", $"focus:{focus}" };
        var eval = ctx.Evaluation(sub.Id);
        var plans = await PlansByFindingAsync(_db, new[] { finding.Id }, ct);

        AddCatalog(reg, sub);
        AddEvaluation(reg, versions, eval);
        AddFinding(reg, versions, finding, CurrentPlan(plans.TryGetValue(finding.Id, out var l) ? l : null), detailed: true);
        await AddEvidenceAsync(reg, versions, ctx.Scope.Id, ctx.Cycle.Id, sub.Code, finding.EvidenceIds.ToHashSet(), ct);
        AddProcedures(reg, versions, ctx.Procedures.Where(p => p.SubcategoryCode == sub.Code && p.Status == NistProcedureStatus.Performed
                                                               && p.Outcome is NistProcedureOutcome.Unsatisfactory or NistProcedureOutcome.PartiallySatisfactory));

        var prompt = new NistAssistPrompt(
            NistAssistKind.Finding.ToString(), focus, Target(ctx, sub, eval, finding.Title),
            reg.Prompt(), Array.Empty<NistAssistPromptMetric>(), Array.Empty<NistAssistPromptPriority>(),
            Methodology(ctx, null), NistAssistSections.For("Finding", focus).Select(s => s.Key).ToList());
        var plan = reg.Any("Plan") ? "com plano de tratamento" : "sem plano de tratamento";
        var summary = $"achado v{finding.Version} ({NistLabels.FindingStatus(finding.Status.ToString()).ToLowerInvariant()}, {plan}) · " +
                      $"{reg.Count("Manual", "Document", "KnightIndicator", "AssetInventory")} evidência(s) citada(s) · " +
                      $"{(eval is null ? "sem avaliação" : $"avaliação v{eval.Version}")}";
        return Built(prompt, reg, versions, summary, ctx, false);
    }

    // =============================================================================================
    //  Resumo executivo (a partir do relatório determinístico — a IA explica, não calcula)
    // =============================================================================================

    public static NistAssistBuilt Executive(NistMaturityReport core, string basisFingerprint)
    {
        var reg = new Registry();
        var s = core.Summary;
        var metrics = new List<NistAssistPromptMetric>();
        void M(string label, string value) => metrics.Add(reg.Metric(label, value));
        static string L(double? v) => NistReportCanonical.Level(v);

        M("Atual (média)", s.WithCurrent == 0 ? "sem avaliação confirmada" : $"{L(s.Current)} (sobre {s.WithCurrent} subcategoria(s))");
        M("Alvo (média)", s.WithTarget == 0 ? "sem alvo confirmado" : $"{L(s.Target)} (sobre {s.WithTarget} subcategoria(s))");
        M("Lacuna média", s.WithGap == 0 ? "indeterminada" : $"{L(s.Gap)} (sobre {s.WithGap} com atual e alvo)");
        M("Cobertura", $"{s.Coverage.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"))}% ({s.Evaluated} avaliadas + {s.NotApplicable} não aplicáveis de {s.Subcategories})");
        M("Avaliadas", s.Evaluated.ToString(CultureInfo.InvariantCulture));
        M("Aguardando confirmação", s.PendingConfirmation.ToString(CultureInfo.InvariantCulture));
        M("Sem avaliação", (s.NotEvaluated + s.InProgress).ToString(CultureInfo.InvariantCulture));
        M("Lacunas indeterminadas", s.IndeterminateGaps.ToString(CultureInfo.InvariantCulture));
        M("Revisões aprovadas", s.ReviewApproved.ToString(CultureInfo.InvariantCulture));
        M("Revisões desatualizadas", s.ReviewOutdated.ToString(CultureInfo.InvariantCulture));
        M("Procedimentos realizados", s.ProceduresPerformed.ToString(CultureInfo.InvariantCulture));
        M("Procedimentos sem resultado", s.ProceduresPlanned.ToString(CultureInfo.InvariantCulture));
        M("Achados abertos", s.FindingsOpen.ToString(CultureInfo.InvariantCulture));
        M("Achados com risco aceito", s.FindingsRiskAccepted.ToString(CultureInfo.InvariantCulture));
        M("Planos ativos", s.PlansActive.ToString(CultureInfo.InvariantCulture));
        M("Planos concluídos", s.PlansCompleted.ToString(CultureInfo.InvariantCulture));
        M("Planos atrasados", s.PlansOverdue.ToString(CultureInfo.InvariantCulture));
        foreach (var f in core.Functions)
            M($"Função {f.Code}", f.Current is null && f.Target is null
                ? $"sem avaliação confirmada ({f.Evaluated}/{f.Subcategories} avaliadas)"
                : $"atual {L(f.Current)} · alvo {L(f.Target)} · {f.Evaluated}/{f.Subcategories} avaliadas");

        var gapKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in core.PriorityGaps)
            gapKeys[g.Code] = reg.Add("Gap", NistAssistBasis.AnalystReport, $"{g.Code} — {g.Title}",
                $"Atual {g.CurrentLevel} → alvo {g.TargetLevel} (lacuna +{g.Gap}, confirmada por revisão humana). Risco registrado: {Cut(g.RiskImpact ?? "não registrado", 300)}. " +
                $"Achados abertos: {g.OpenFindings}. Tratamento: {g.TreatmentLabel}.",
                null, null, false, true, "Lacuna é distância até a meta escolhida; sem achado, é meta de melhoria.", "Subcategory", null, g.Code);
        var findingKeys = new Dictionary<Guid, string>();
        foreach (var f in core.Findings)
            findingKeys[f.Id] = reg.Add("Finding", NistAssistBasis.AnalystReport, $"{f.Title} ({f.SubcategoryCode})",
                $"Severidade {f.SeverityLabel}; prioridade {f.PriorityLabel}; situação {f.StatusLabel}; tratamento: {f.TreatmentLabel}" +
                (f.Plan?.DueDate is { } due ? $"; prazo {due:dd/MM/yyyy}" + (f.Plan!.IsOverdue ? " (atrasado)" : "") : "") +
                $". Risco registrado: {Cut(f.Risk, 300)}",
                null, f.StatusLabel, false, true, null, "Finding", f.Id.ToString("D"), f.SubcategoryCode);
        foreach (var lim in core.Limitations)
            reg.Add("Limitation", NistAssistBasis.Reference, "Limitação declarada pelo AEGIS", lim, null, null, false, true, null, null, null, null);

        // Prioridades: ordem DETERMINÍSTICA do AEGIS (não da IA).
        var priorities = new List<NistAssistPromptPriority>();
        foreach (var f in core.Findings.Where(f => f.Status == "Open")
                     .OrderBy(f => NistLabels.SeverityRank(f.Severity))
                     .ThenBy(f => PriorityRank(f.Priority))
                     .ThenBy(f => f.Plan?.IsOverdue == true ? 0 : 1)
                     .ThenBy(f => f.SubcategoryCode, StringComparer.Ordinal).ThenBy(f => f.Id))
            priorities.Add(reg.Priority($"Tratar o achado aberto \"{f.Title}\" ({f.SubcategoryCode}) — severidade {f.SeverityLabel.ToLowerInvariant()}, " +
                                        $"prioridade {f.PriorityLabel.ToLowerInvariant()}; tratamento: {f.TreatmentLabel}.", findingKeys[f.Id]));
        foreach (var g in core.PriorityGaps.Where(g => g.OpenFindings == 0))
            priorities.Add(reg.Priority($"Planejar a melhoria de {g.Code} — {g.Title} (atual {g.CurrentLevel} → alvo {g.TargetLevel}); meta de melhoria, sem achado registrado.",
                gapKeys[g.Code]));
        var top = priorities.Take(8).ToList();
        foreach (var dropped in priorities.Skip(8)) reg.Remove(dropped.Key);

        var prompt = new NistAssistPrompt(
            NistAssistKind.ExecutiveSummary.ToString(), null,
            new NistAssistPromptTarget(core.Assessment.Name, core.Cycle.Name,
                $"{core.Cycle.PeriodKindLabel}, {core.Cycle.PeriodStart:dd/MM/yyyy} a {core.Cycle.PeriodEnd:dd/MM/yyyy}",
                core.Scope.Name, core.Scope.Description, null, null, null, null, null, false, false, null),
            reg.Prompt(), metrics, top,
            new NistAssistPromptMethodology(core.Methodology.Version, core.Methodology.Scale.Select(x => $"{x.Level} — {x.Name}").ToList(),
                core.Methodology.Statement, core.Methodology.InstrumentsNote, PriorityCriterion),
            NistAssistSections.For("ExecutiveSummary", null).Select(x => x.Key).ToList());
        var summary = $"relatório da rodada {core.Cycle.Name} · {s.Evaluated} avaliada(s) de {s.Subcategories} · {core.Findings.Count} achado(s) · " +
                      $"{s.PlansActive} plano(s) ativo(s)";
        return new NistAssistBuilt(prompt, reg.Records, basisFingerprint, summary, core.Methodology.Version,
            core.Methodology.Scale.ToDictionary(x => x.Level, x => x.Name), false);
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "Urgent" => 0,
        "High" => 1,
        "Medium" => 2,
        _ => 3,
    };

    // =============================================================================================
    //  Fontes
    // =============================================================================================

    private void AddCatalog(Registry reg, NistSubcategory sub)
    {
        var language = _language?.Get(sub.Code);
        var content = new StringBuilder();
        content.Append("NIST CSF 2.0 (texto oficial): ").Append(Cut(sub.Description, 400));
        if (language is not null)
        {
            if (!string.IsNullOrWhiteSpace(language.Summary)) content.Append(" | Explicação do AEGIS: ").Append(Cut(language.Summary, 300));
            if (!string.IsNullOrWhiteSpace(language.Impact)) content.Append(" | Por que importa: ").Append(Cut(language.Impact, 300));
            if (!string.IsNullOrWhiteSpace(language.InitialAction)) content.Append(" | Primeiro passo: ").Append(Cut(language.InitialAction, 300));
        }
        if (!string.IsNullOrWhiteSpace(sub.ImplementationExamples)) content.Append(" | Exemplos (NIST): ").Append(Cut(sub.ImplementationExamples, 400));
        reg.Add("Catalog", NistAssistBasis.Reference, $"{sub.Code} — resultado esperado e explicação", content.ToString(),
            null, null, false, true, "Orientação geral: descreve o resultado, não o ambiente.", "Outcome", null, sub.Code);
    }

    private static void AddEvaluation(Registry reg, List<string> versions, SubcategoryEvaluation? e)
    {
        if (e is null) return;
        versions.Add($"eval:{e.Id}:{e.Version}");
        if (!HasContent(e)) return;
        var parts = new List<string>();
        if (e.NotApplicable) parts.Add("Declarada não aplicável ao escopo");
        if (e.CurrentLevel is { } c) parts.Add($"situação atual {c}");
        if (e.TargetLevel is { } t) parts.Add($"alvo {t}");
        void P(string label, string? v) { if (!string.IsNullOrWhiteSpace(v)) parts.Add($"{label}: {Cut(v, MaxText)}"); }
        P("Justificativa", e.Rationale);
        P("Observações (atual)", e.CurrentComments);
        P("Observações (alvo)", e.TargetComments);
        P("Lacunas observadas", e.Gaps);
        P("Risco ou impacto", e.RiskImpact);
        P("Orientação de melhoria", e.ImprovementGuidance);
        var confirmed = e.HumanConfirmed;
        reg.Add("Evaluation", confirmed ? NistAssistBasis.AnalystReport : NistAssistBasis.Unconfirmed,
            confirmed ? $"Avaliação registrada nesta rodada (versão {e.Version})" : $"Rascunho {NistLabels.ContentOrigin(e.ContentOrigin.ToString()).ToLowerInvariant()} (versão {e.Version})",
            string.Join("; ", parts), e.ReviewedAt is { } at ? Date(at) : null, confirmed ? "Confirmada por revisão humana" : "Aguardando confirmação humana",
            false, true, confirmed ? "Relato do analista — não é verificação independente." : "Conteúdo herdado ou importado: ainda não confirmado.",
            "Evaluation", e.Id.ToString("D"), null);
    }

    /// <summary>Evidências VIGENTES da subcategoria na rodada (ou só as citadas por um achado).</summary>
    private async Task AddEvidenceAsync(Registry reg, List<string> versions, Guid scopeId, Guid cycleId, string code, ISet<Guid>? only, CancellationToken ct)
    {
        var rows = (await _db.Evidence.AsNoTracking()
                .Where(e => e.AssessmentScopeId == scopeId && e.CycleId == cycleId && e.SubcategoryCode == code && e.RemovedAt == null)
                .ToListAsync(ct))
            .Where(e => only is null || only.Contains(e.Id))
            .OrderBy(e => e.CreatedAt.UtcTicks).ThenBy(e => e.Id).ToList();

        var docIds = rows.Where(e => e.OriginKind == EvidenceOriginKind.GovernanceDocument && Guid.TryParse(e.OriginRef, out _))
            .Select(e => Guid.Parse(e.OriginRef!)).Distinct().ToList();
        var docs = docIds.Count == 0 ? new Dictionary<Guid, GovernanceDocument>()
            : await _db.GovernanceDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var mappings = docIds.Count == 0 ? new List<DocumentControlMapping>()
            : await _db.DocumentControlMappings.AsNoTracking().Where(m => docIds.Contains(m.GovernanceDocumentId) && m.SubcategoryCode == code).ToListAsync(ct);

        foreach (var e in rows)
        {
            versions.Add($"ev:{e.Id}:{e.CreatedAt.UtcTicks}");
            var date = Date(e.CollectedAt);
            switch (e.OriginKind)
            {
                case EvidenceOriginKind.GovernanceDocument:
                {
                    var docId = Guid.TryParse(e.OriginRef, out var g) ? g : Guid.Empty;
                    docs.TryGetValue(docId, out var doc);
                    var literal = mappings.Where(m => m.GovernanceDocumentId == docId && !string.IsNullOrWhiteSpace(m.EvidenceQuote))
                        .OrderByDescending(m => m.AnalystConfirmed).ThenByDescending(m => m.Confidence).FirstOrDefault();
                    var status = doc is null ? "documento não encontrado" : NistAssessmentService.DocumentStatusLabel(doc.Status);
                    versions.Add($"doc:{docId}:{doc?.Status}:{doc?.AnalyzedAt?.UtcTicks}:{Hash(literal?.EvidenceQuote)}");
                    if (literal is not null)
                    {
                        reg.Add("Document", NistAssistBasis.Fact, e.Title ?? doc?.Title ?? "Documento",
                            $"Trecho literal validado na análise documental: «{Cut(literal.EvidenceQuote!, MaxExcerpt)}»" +
                            (string.IsNullOrWhiteSpace(literal.Evidence) ? "" : $" | Racional da análise: {Cut(literal.Evidence, 300)}") +
                            $" | Situação do documento: {status}.",
                            date, status, false, true,
                            "Trecho literal do documento: comprova o que o texto estabelece, não a execução da prática." +
                            (doc?.Status == GovernanceStatus.Vigente ? "" : " O documento não está vigente."),
                            "Evidence", e.Id.ToString("D"), null);
                    }
                    else if (doc is { AnalysisStatus: AiAnalysisStatus.Analyzed } && !string.IsNullOrWhiteSpace(doc.AnalysisSummary))
                    {
                        reg.Add("Document", NistAssistBasis.Unconfirmed, e.Title ?? doc.Title,
                            $"Resumo de análise documental anterior, sem trecho literal validado para {code}: {Cut(doc.AnalysisSummary, 400)} | Situação: {status}.",
                            date, status, false, true,
                            "Resumo gerado em análise anterior, sem trecho literal para esta subcategoria: não sustenta fato.",
                            "Evidence", e.Id.ToString("D"), null);
                    }
                    else
                    {
                        reg.Add("Document", NistAssistBasis.NotExamined, e.Title ?? doc?.Title ?? "Documento",
                            $"Documento cadastrado (título e situação: {status}). O conteúdo NÃO foi examinado pela assistência: não há trecho nem análise utilizável.",
                            date, status, false, false,
                            "Título, cadastro ou existência do arquivo não comprovam o conteúdo.",
                            "Evidence", e.Id.ToString("D"), null);
                    }
                    if (!string.IsNullOrWhiteSpace(e.Notes))
                        reg.Add("DocumentNote", NistAssistBasis.AnalystReport, $"Nota do analista sobre \"{e.Title}\"", Cut(e.Notes, MaxText),
                            date, null, false, true, "Relato do analista — não é o conteúdo do documento.", "Evidence", e.Id.ToString("D"), null);
                    break;
                }
                case EvidenceOriginKind.KnightIndicator:
                {
                    var parts = (e.OriginRef ?? "").Split('/', 2);
                    KnightIndicatorResult? indicator = null;
                    KnightAssessmentRun? run = null;
                    if (parts.Length == 2 && Guid.TryParse(parts[0], out var runId))
                    {
                        run = await _db.KnightAssessmentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
                        indicator = await _db.KnightIndicatorResults.AsNoTracking().FirstOrDefaultAsync(i => i.RunId == runId && i.IndicatorId == parts[1], ct);
                    }
                    var demo = run?.Mode == KnightAssessmentMode.Demo || (e.OriginScope ?? "").Contains("demonstração", StringComparison.OrdinalIgnoreCase);
                    var partial = run is not null && run.SourceState != KnightSourceState.Completed;
                    var status = indicator is null ? "resultado não localizado" : NistAssessmentService.KnightStatusLabel(indicator.Status);
                    versions.Add($"knight:{e.OriginRef}:{indicator?.Status}");
                    var limitation = "Controle técnico isolado: apoio mapeado explicitamente, não comprova sozinho a prática organizacional." +
                                     (demo ? " Cenário de DEMONSTRAÇÃO (dados sintéticos)." : "") +
                                     (partial ? $" Coleta da fonte em estado {run!.SourceState}: leitura parcial." : "");
                    reg.Add("KnightIndicator", indicator is null ? NistAssistBasis.Unconfirmed : NistAssistBasis.Fact, e.Title ?? e.OriginRef ?? "Controle do KNIGHT",
                        indicator is null
                            ? "A execução do KNIGHT referenciada não está mais disponível para leitura."
                            : $"Resultado {status}; severidade {NistLabels.Severity(indicator.Severity.ToString())}; objetos afetados: {indicator.AffectedObjectCount}; " +
                              $"evidência normalizada: {Cut(indicator.Evidence, 300)}; origem: {e.OriginLabel}; {e.OriginScope}.",
                        date, status, demo, indicator is not null, limitation, "Evidence", e.Id.ToString("D"), null);
                    break;
                }
                case EvidenceOriginKind.AssetInventory:
                    reg.Add("AssetInventory", NistAssistBasis.Fact, e.Title ?? "Inventário de ativos",
                        $"Retrato do inventário no vínculo: {e.OriginScope}", date, null, false, true,
                        "Mostra o que está inventariado, não que a gestão de ativos esteja completa.", "Evidence", e.Id.ToString("D"), null);
                    break;
                default:
                    reg.Add("Manual", NistAssistBasis.AnalystReport, e.Title ?? "Registro do analista",
                        $"{ManualType(e.Type)} registrada pelo analista" + (string.IsNullOrWhiteSpace(e.Notes) ? "." : $": {Cut(e.Notes, MaxText)}") +
                        (e.Uri is null ? "" : " (há link externo, não acessado pela assistência)"),
                        date, null, false, true, "Relato do analista — não é verificação independente.", "Evidence", e.Id.ToString("D"), null);
                    break;
            }
        }
    }

    private static void AddProcedures(Registry reg, List<string> versions, IEnumerable<NistTestProcedure> procedures)
    {
        foreach (var p in procedures.OrderBy(p => p.Method).ThenBy(p => p.CreatedAt.UtcTicks).ThenBy(p => p.Id))
        {
            versions.Add($"proc:{p.Id}:{p.Version}");
            var method = NistLabels.Method(p.Method.ToString());
            var status = p.Status == NistProcedureStatus.Performed ? NistLabels.Outcome(p.Outcome?.ToString()) : NistLabels.ProcedureStatus(p.Status.ToString());
            var content = p.Status switch
            {
                NistProcedureStatus.Performed => $"Realizado em {p.PerformedOn:dd/MM/yyyy}; conclusão {status}; observação: {Cut(p.Observation ?? "", MaxText)}",
                NistProcedureStatus.NotPerformed => $"Não realizado; motivo: {Cut(p.Observation ?? "", MaxText)}",
                _ => "Planejado, ainda sem resultado (planejar não comprova a realização).",
            };
            reg.Add("Procedure", p.ContentOrigin == NistContentOrigin.Analyst ? NistAssistBasis.AnalystReport : NistAssistBasis.Unconfirmed,
                $"{method}: {Cut(p.Procedure, 200)}", content, p.PerformedOn?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), status, false, true,
                p.ContentOrigin == NistContentOrigin.Analyst ? "Relato do avaliador." : "Herdado de outra rodada: sem resultado nesta rodada.",
                "Procedure", p.Id.ToString("D"), null);
        }
    }

    private static void AddFinding(Registry reg, List<string> versions, NistFinding f, ActionPlanView? plan, bool detailed = false)
    {
        versions.Add($"finding:{f.Id}:{f.Version}");
        var content = $"Condição: {Cut(f.Condition, detailed ? MaxText : 300)}; risco: {Cut(f.Risk, detailed ? 400 : 200)}; impacto: {Cut(f.Impact, detailed ? 400 : 200)}; " +
                      $"severidade {NistLabels.Severity(f.Severity.ToString())} ({Cut(f.SeverityRationale, 200)}); prioridade {NistLabels.Priority(f.Priority.ToString())}; " +
                      $"situação {NistLabels.FindingStatus(f.Status.ToString())}" +
                      (detailed ? $"; recomendação registrada: {Cut(f.Recommendation, 400)}" : "");
        reg.Add("Finding", NistAssistBasis.AnalystReport, f.Title, content, Date(f.CreatedAt), NistLabels.FindingStatus(f.Status.ToString()),
            false, true, "Achado registrado pelo analista.", "Finding", f.Id.ToString("D"), f.SubcategoryCode);
        if (plan is null) return;
        versions.Add($"plan:{plan.Id}:{plan.Version}:{plan.Status}:{plan.IsOverdue}");
        reg.Add("Plan", NistAssistBasis.AnalystReport, $"Plano: {Cut(plan.Title, 200)}",
            $"Etapa {RemediationReading.StatusLabel(plan.Status)}{(plan.IsOverdue ? " (prazo vencido)" : "")}; prazo {plan.DueDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "não definido"}; " +
            $"próxima providência: {plan.NextStep}" +
            (detailed && plan.ProposedAction is { } pa ? $"; ação proposta: {Cut(pa, 400)}" : "") +
            (detailed && plan.ExecutionNotes is { } ex ? $"; execução relatada: {Cut(ex, 300)}" : "") +
            (plan.ApplicableValidation is { } v ? $"; validação humana: {RemediationReading.OutcomeLabel(v.Outcome)}" : ""),
            null, RemediationReading.StatusLabel(plan.Status), false, true,
            "Concluir o plano não altera a maturidade: a reavaliação é um ato separado.", "Finding", f.Id.ToString("D"), f.SubcategoryCode);
    }

    private async Task AddReferenceAsync(Registry reg, List<string> versions, NistScopeContext ctx, NistSubcategory sub, CancellationToken ct)
    {
        if (ctx.Cycle.SeedFromCycleId is not { } sourceId || ctx.Cycle.SeedMode == NistCycleSeedMode.None) return;
        var source = await _db.NistCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceId, ct);
        var e = await _db.Evaluations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CycleId == sourceId && x.AssessmentScopeId == ctx.Scope.Id && x.SubcategoryId == sub.Id, ct);
        if (source is null || e is null || !HasContent(e)) return;
        versions.Add($"ref:{e.Id}:{e.Version}");
        reg.Add("Reference", NistAssistBasis.Unconfirmed, $"Rodada anterior \"{source.Name}\" (referência)",
            $"Situação {(e.NotApplicable ? "não aplicável" : e.CurrentLevel?.ToString(CultureInfo.InvariantCulture) ?? "sem nível")}, alvo {e.TargetLevel?.ToString(CultureInfo.InvariantCulture) ?? "sem alvo"}" +
            (string.IsNullOrWhiteSpace(e.Rationale) ? "" : $"; justificativa: {Cut(e.Rationale, 300)}") +
            (string.IsNullOrWhiteSpace(e.Gaps) ? "" : $"; lacunas: {Cut(e.Gaps, 300)}"),
            e.ReviewedAt is { } at ? Date(at) : null, e.HumanConfirmed ? "confirmada naquela rodada" : "não confirmada", false, true,
            "Referência de OUTRA rodada: não é a avaliação desta rodada.", "Reference", null, sub.Code);
    }

    // =============================================================================================
    //  Apoio
    // =============================================================================================

    private NistAssistPromptTarget Target(NistScopeContext ctx, NistSubcategory sub, SubcategoryEvaluation? e, string? findingTitle) => new(
        ctx.Assessment.Name, ctx.Cycle.Name,
        $"{NistLabels.PeriodKind(ctx.Cycle.PeriodKind.ToString())}, {ctx.Cycle.PeriodStart:dd/MM/yyyy} a {ctx.Cycle.PeriodEnd:dd/MM/yyyy}",
        ScopeName(ctx.Scope), ctx.Scope.Description is null ? null : Cut(ctx.Scope.Description, 300),
        sub.Code, _language?.Get(sub.Code)?.Title ?? sub.Code,
        NistLabels.State(StateOf(e, ctx.EvidenceCount(sub.Code))),
        e?.HumanConfirmed == true ? e.CurrentLevel : null, e?.HumanConfirmed == true ? e.TargetLevel : null,
        e is { HumanConfirmed: true, NotApplicable: true }, e?.HumanConfirmed == true, findingTitle);

    private static NistAssistPromptMethodology Methodology(NistScopeContext ctx, string? priority) => new(
        ctx.Assessment.MethodologyVersion,
        ctx.Catalog.MaturityLevels.OrderBy(l => l.Level).Select(l => $"{l.Level} — {l.Name}: {Cut(l.Description, 200)}").ToList(),
        NistMethodologyText.Statement, NistMethodologyText.InstrumentsNote, priority);

    private NistAssistBuilt Built(NistAssistPrompt prompt, Registry reg, List<string> versions, string summary, NistScopeContext ctx, bool notApplicable)
    {
        var payload = JsonSerializer.Serialize(prompt, Canonical) + "\n" + string.Join("\n", versions);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new NistAssistBuilt(prompt, reg.Records, fingerprint, summary, ctx.Assessment.MethodologyVersion,
            ctx.Catalog.MaturityLevels.ToDictionary(l => l.Level, l => l.Name), notApplicable);
    }

    private static string ManualType(EvidenceType t) => t switch
    {
        EvidenceType.Interview => "Entrevista",
        EvidenceType.Link => "Link",
        EvidenceType.Screenshot => "Captura",
        EvidenceType.Document => "Documento externo",
        _ => "Registro",
    };

    internal static string Cut(string? s, int max)
    {
        var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    private static string? Hash(string? s) => s is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>Fontes numeradas na ordem de entrada (S1, S2…), indicadores (M…) e prioridades (P…).</summary>
    private sealed class Registry
    {
        private readonly List<NistAssistSourceRecord> _records = new();
        private int _s, _m, _p;

        public IReadOnlyList<NistAssistSourceRecord> Records => _records;

        public string Add(string kind, string basis, string title, string? detail, string? date, string? status, bool isDemo, bool examined,
            string? limitation, string? linkTarget, string? linkId, string? linkCode)
        {
            var key = $"S{++_s}";
            _records.Add(new NistAssistSourceRecord(key, kind, basis, Cut(title, 300), detail, date, status, isDemo, examined, limitation, linkTarget, linkId, linkCode));
            return key;
        }

        public NistAssistPromptMetric Metric(string label, string value)
        {
            var key = $"M{++_m}";
            _records.Add(new NistAssistSourceRecord(key, "Metric", NistAssistBasis.Reference, label, value, null, null, false, true,
                "Indicador determinístico do AEGIS.", null, null, null));
            return new NistAssistPromptMetric(key, label, value);
        }

        public NistAssistPromptPriority Priority(string text, string source)
        {
            var key = $"P{++_p}";
            var target = _records.First(r => r.Key == source);
            _records.Add(new NistAssistSourceRecord(key, "Priority", NistAssistBasis.Reference, $"Prioridade {_p} (critério do AEGIS)", text, null, null,
                false, true, "Ordem determinística do AEGIS.", target.LinkTarget, target.LinkId, target.LinkCode));
            return new NistAssistPromptPriority(key, text, new[] { source });
        }

        public void Remove(string key) => _records.RemoveAll(r => r.Key == key);

        public int Count(params string[] kinds) => _records.Count(r => kinds.Contains(r.Kind));

        public bool Any(string kind) => _records.Any(r => r.Kind == kind);

        public IReadOnlyList<NistAssistPromptSource> Prompt() => _records
            .Where(r => r.Kind is not "Metric" and not "Priority")
            .Select(r => new NistAssistPromptSource(r.Key, r.Kind, r.Basis, r.Title, r.Detail, r.Date, r.Status, r.IsDemo, r.ContentExamined, r.Limitation))
            .ToList();
    }
}
