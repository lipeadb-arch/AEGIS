using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Infrastructure.Posture;

/// <summary>
/// [AEGIS-AUD-035/036/037] Serviço da fotografia AUDITÁVEL de postura. A publicação é CONTROLADA: o cliente
/// só escolhe o tipo (e, para KNIGHT, a fonte) — o servidor constrói a fotografia EXCLUSIVAMENTE pelas
/// autoridades atuais do domínio (<see cref="AegisScoreAggregator"/> + catálogo ATIVO COMPLETO em left join com
/// o ledger, para AEGIS Score/NIST; o ÚLTIMO <see cref="KnightAssessmentRun"/> para o KNIGHT), calcula o hash
/// determinístico e persiste. NÃO há score combinado entre os instrumentos. A fotografia é APPEND-ONLY (sem
/// update/delete) e o isolamento por tenant vem do Global Query Filter fail-closed + stamping no DbContext.
///
/// O <c>TenantId</c> do agregado é atribuído EXPLICITAMENTE ao tenant ambiente VALIDADO antes de calcular o hash
/// (o stamping do DbContext, que só ocorre no <c>SaveChanges</c>, permanece como defesa fail-closed adicional) —
/// senão o hash cobriria <c>Guid.Empty</c> e não re-derivaria após o round-trip.
/// </summary>
public sealed class PostureSnapshotService : IPostureSnapshotService
{
    /// <summary>Teto de tamanho do rótulo do documento preservado como referência de evidência (metadado, não conteúdo).</summary>
    private const int MaxDocumentTitleLength = 200;

    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly INistSignalMapper _mapper;

    public PostureSnapshotService(AegisScoreDbContext db, ITenantContext tenant, INistSignalMapper mapper)
    {
        _db = db;
        _tenant = tenant;
        _mapper = mapper;
    }

    // ---- Publicação ----------------------------------------------------------------------------------

    public async Task<PostureSnapshotDetailDto> PublishAsync(
        PostureSnapshotType type, KnightSourceType? source, CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId
            ?? throw new TenantSecurityException("Publicação de fotografia sem tenant resolvido no contexto (fail-closed).");

        var snapshot = type switch
        {
            PostureSnapshotType.AegisScoreNist => await BuildAegisSnapshotAsync(ct),
            PostureSnapshotType.Knight => await BuildKnightSnapshotAsync(source, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de fotografia desconhecido."),
        };

        // Tenant ambiente VALIDADO atribuído ao agregado ANTES do hash (o hash cobre o TenantId). O stamping
        // fail-closed do DbContext reconfirma no SaveChanges; jamais se aceita TenantId vindo do cliente.
        snapshot.TenantId = tenantId;
        foreach (var c in snapshot.Controls) c.TenantId = tenantId;
        foreach (var i in snapshot.Indicators) i.TenantId = tenantId;

        // Hash determinístico do conteúdo — re-derivável da linha para detectar adulteração.
        snapshot.ContentHash = PostureSnapshotHasher.Compute(snapshot);

        _db.PostureSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        return ToDetail(snapshot);
    }

    /// <summary>
    /// Constrói a fotografia AEGIS Score/NIST. Parte do catálogo ATIVO COMPLETO (todas as subcategorias elegíveis)
    /// em LEFT JOIN com o ledger do tenant: cada controle elegível vira uma linha, avaliado ou não. Score/cobertura
    /// vêm da MESMA autoridade do Score Atual (aegis-score-v1). A proveniência é a evidência que fundamenta o veredito.
    /// </summary>
    private async Task<PostureSnapshot> BuildAegisSnapshotAsync(CancellationToken ct)
    {
        var score = await AegisScoreAggregator.AggregateAsync(_db, ct);

        var frameworkName = await _db.FrameworkVersions.AsNoTracking()
            .Where(f => f.IsActive).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? "framework-desconhecido";

        // Universo ELEGÍVEL: todas as subcategorias do framework ATIVO (reference data global) — o denominador da
        // cobertura por item. Keyed por Id (único por versão), não por Code (que se repete entre versões).
        var eligible = await (
            from s in _db.Subcategories
            join c in _db.Categories on s.CategoryId equals c.Id
            join fn in _db.Functions on c.FunctionId equals fn.Id
            join fv in _db.FrameworkVersions on fn.FrameworkVersionId equals fv.Id
            where fv.IsActive
            select new { s.Id, s.Code, FunctionCode = fn.Code, s.MaxScorePoints }).ToListAsync(ct);

        // Ledger do tenant (query filter fail-closed), keyed por SubcategoryId — só os controles AVALIADOS.
        var states = (await (
            from t in _db.TenantControlStates
            select new { t.SubcategoryId, t.Status, t.CurrentScore, t.LastVerdictSource, t.LastEvaluatedAt })
            .ToListAsync(ct)).ToDictionary(x => x.SubcategoryId);

        // Proveniência da evidência DECISIVA, por código, só para os controles avaliados (telemetria × documental).
        var telemetryCodes = eligible
            .Where(e => states.TryGetValue(e.Id, out var st) && st.LastVerdictSource == VerdictSource.Telemetry)
            .Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        var documentaryCodes = eligible
            .Where(e => states.TryGetValue(e.Id, out var st) && st.LastVerdictSource == VerdictSource.Documentary)
            .Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        var telemetryRefs = await BuildTelemetryProvenanceAsync(telemetryCodes, ct);
        var documentaryRefs = await BuildDocumentProvenanceAsync(documentaryCodes, ct);

        var snapshot = new PostureSnapshot
        {
            Type = PostureSnapshotType.AegisScoreNist,
            SchemaVersion = PostureSnapshotSchema.Version,
            FormulaVersion = score.FormulaVersion,
            CatalogVersion = frameworkName,
            SemanticFamily = $"aegis-nist:{frameworkName}",
            SourceType = null,
            SourceLabel = null,
            CapturedAt = DateTimeOffset.UtcNow,
            Score = score.Percentage,
            AchievedPoints = score.AchievedScore,
            PossiblePoints = score.EvaluatedMaxScore,
            EligiblePoints = score.EligibleMaxScore,
            Coverage = score.CoveragePercentage,
            EvaluatedItems = score.EvaluatedControls,
            EligibleItems = score.EligibleControls,
        };

        DateTimeOffset? recency = null;
        foreach (var e in eligible)
        {
            var control = new PostureSnapshotControl
            {
                SubcategoryCode = e.Code,
                FunctionCode = e.FunctionCode,
                MaxPoints = e.MaxScorePoints,
            };

            if (states.TryGetValue(e.Id, out var st))
            {
                control.Evaluated = true;
                control.Status = st.Status;
                control.AchievedPoints = st.CurrentScore;
                control.VerdictSource = st.LastVerdictSource;
                control.EvaluatedAt = st.LastEvaluatedAt;
                control.EvidenceRefs = st.LastVerdictSource switch
                {
                    VerdictSource.Telemetry when telemetryRefs.TryGetValue(e.Code, out var tr) => tr,
                    VerdictSource.Documentary when documentaryRefs.TryGetValue(e.Code, out var dr) => dr,
                    _ => new List<PostureEvidenceRef>(),
                };
                recency = recency is null || st.LastEvaluatedAt > recency ? st.LastEvaluatedAt : recency;
            }
            else
            {
                // Elegível porém NÃO avaliado — distinto de NonCompliant: sem status/veredito/instante, zero pontos.
                control.Evaluated = false;
                control.AchievedPoints = 0;
            }

            snapshot.Controls.Add(control);
        }

        snapshot.CompliantCount = snapshot.Controls.Count(c => c.Status == ControlStatus.Compliant);
        snapshot.NonCompliantCount = snapshot.Controls.Count(c => c.Status == ControlStatus.NonCompliant);
        snapshot.MitigatedCount = snapshot.Controls.Count(c => c.Status == ControlStatus.MitigatedByThirdParty);
        snapshot.NotEvaluatedCount = snapshot.Controls.Count(c => !c.Evaluated);
        snapshot.ErrorCount = 0;
        snapshot.NotApplicableCount = 0;
        snapshot.DataRecency = recency;

        return snapshot;
    }

    /// <summary>
    /// Referências à evidência DECISIVA de cada controle de telemetria — a MESMA autoridade da projeção do ledger:
    /// a evidência GLOBALMENTE mais nova (empate exato de instante → pior veredito conservador). Referencia todas as
    /// evidências decisivas do empate, sem incluir sinais "apenas recentes" que não determinaram o veredito.
    /// </summary>
    private async Task<Dictionary<string, List<PostureEvidenceRef>>> BuildTelemetryProvenanceAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        var result = new Dictionary<string, List<PostureEvidenceRef>>(StringComparer.Ordinal);
        if (codes.Count == 0) return result;

        // Todos os sinais do tenant (query filter fail-closed em Signals E Connectors), com a capability do conector.
        var signals = await (
            from s in _db.Signals
            join c in _db.Connectors on s.ConnectorConfigId equals c.Id
            select new SignalRef(
                s.Id, s.SignalKey, s.Source, s.ExternalEventId,
                s.NumericValue, s.Severity, s.Unit, s.CollectedAt, c.Capability)).ToListAsync(ct);
        if (signals.Count == 0) return result;

        var byCapability = new Dictionary<ConnectorCapability, IReadOnlyDictionary<string, SignalMappingResolution>>();
        foreach (var cap in signals.Select(s => s.Capability).Distinct())
        {
            var keys = signals.Where(s => s.Capability == cap)
                .Select(s => (s.SignalKey ?? "").Trim()).Distinct().ToList();
            byCapability[cap] = await _mapper.ResolveAsync(cap, keys, ct);
        }

        foreach (var code in codes)
        {
            var candidates = new List<(SignalRef Signal, EvidenceVerdict Verdict)>();
            foreach (var s in signals)
            {
                var key = (s.SignalKey ?? "").Trim();
                if (!byCapability[s.Capability].TryGetValue(key, out var r) || !r.SubcategoryCodes.Contains(code))
                    continue;
                var verdict = EvidenceSignalEvaluator.Evaluate(r.ScoringHint, s.NumericValue, s.Severity, s.Unit);
                if (verdict is not null)
                    candidates.Add((s, verdict));
            }
            if (candidates.Count == 0) continue;   // proveniência não reconstruível — refs vazias (honesto)

            // Decisiva = a mais nova; no empate EXATO de instante, o PIOR veredito (o que a projeção aplica).
            var maxAt = candidates.Max(c => c.Signal.CollectedAt);
            var atNewest = candidates.Where(c => c.Signal.CollectedAt == maxAt).ToList();
            var worstRank = atNewest.Min(c => ConservativeRank(c.Verdict.Status));
            var deciding = atNewest.Where(c => ConservativeRank(c.Verdict.Status) == worstRank);

            result[code] = deciding
                .Select(c => new PostureEvidenceRef(
                    "telemetry",
                    string.IsNullOrWhiteSpace(c.Signal.Source) ? "(coletor)" : c.Signal.Source!,
                    string.IsNullOrWhiteSpace(c.Signal.ExternalEventId) ? c.Signal.SignalKey : c.Signal.ExternalEventId!,
                    c.Signal.CollectedAt))
                .ToList();
        }

        return result;
    }

    /// <summary>Referências SANITIZADAS ao(s) documento(s)/mapeamento(s) que fundamentaram um veredito documental — só o rótulo/instante, nunca conteúdo bruto.</summary>
    private async Task<Dictionary<string, List<PostureEvidenceRef>>> BuildDocumentProvenanceAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        var result = new Dictionary<string, List<PostureEvidenceRef>>(StringComparer.Ordinal);
        if (codes.Count == 0) return result;

        var codeList = codes.ToList();
        var maps = await (
            from m in _db.DocumentControlMappings
            where codeList.Contains(m.SubcategoryCode)
            join d in _db.GovernanceDocuments on m.GovernanceDocumentId equals d.Id
            select new { m.SubcategoryCode, d.Id, d.Title, d.AnalyzedAt }).ToListAsync(ct);

        foreach (var g in maps.GroupBy(m => m.SubcategoryCode, StringComparer.Ordinal))
        {
            result[g.Key] = g
                .GroupBy(m => m.Id)   // um documento pode mapear o mesmo código mais de uma vez
                .Select(dg => dg.First())
                .Select(m => new PostureEvidenceRef(
                    "document", "Document Hub", SanitizeTitle(m.Title), m.AnalyzedAt))
                .ToList();
        }

        return result;
    }

    /// <summary>Congela o ÚLTIMO assessment KNIGHT do tenant (opcionalmente da fonte indicada) numa fotografia imutável.</summary>
    private async Task<PostureSnapshot> BuildKnightSnapshotAsync(KnightSourceType? source, CancellationToken ct)
    {
        var candidatesQuery = _db.KnightAssessmentRuns.AsNoTracking()
            .Where(r => r.Status == KnightRunStatus.Completed);
        if (source is { } s)
            candidatesQuery = candidatesQuery.Where(r => r.SourceType == s);

        // Ordenação por instante do lado do cliente: o SQLite dos testes não traduz ORDER BY de DateTimeOffset;
        // o conjunto por tenant é pequeno (execuções de assessment), então materializar id+StartedAt e escolher
        // o mais recente em memória é barato e portável. Depois carrega SÓ a execução escolhida com indicadores.
        var candidates = await candidatesQuery.Select(r => new { r.Id, r.StartedAt }).ToListAsync(ct);
        var latestId = candidates
            .OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id)
            .Select(c => (Guid?)c.Id).FirstOrDefault();

        if (latestId is null)
        {
            var scope = source is { } s2 ? $" da fonte {s2}" : "";
            throw new PostureSnapshotNotAvailableException(
                $"Não há assessment KNIGHT concluído{scope} para publicar. Execute um assessment antes de publicar a fotografia.");
        }

        var run = await _db.KnightAssessmentRuns.AsNoTracking()
            .Include(r => r.Indicators)
            .FirstAsync(r => r.Id == latestId.Value, ct);

        // Peso avaliado/elegível pela fórmula do KNIGHT (severidade → peso), coerente com knight-score-v1.
        double achievedWeighted = 0, evaluatedWeight = 0, eligibleWeight = 0;
        foreach (var ind in run.Indicators)
        {
            if (ind.Status == KnightIndicatorStatus.NotApplicable) continue;   // fora do universo aplicável
            var weight = KnightScoreFormula.WeightFor(ind.Severity);
            eligibleWeight += weight;
            var factor = KnightScoreFormula.FactorFor(ind.Status);
            if (factor is null) continue;                                      // NotEvaluated/Error: reduz cobertura
            evaluatedWeight += weight;
            achievedWeighted += weight * factor.Value;
        }

        var evaluatedItems = run.PassedCount + run.ExposedCount + run.MitigatedCount;
        var applicableItems = evaluatedItems + run.NotEvaluatedCount + run.ErrorCount;
        var dataRecency = run.CompletedAt
            ?? (run.Indicators.Count > 0 ? run.Indicators.Max(i => i.CollectedAt) : run.StartedAt);

        var snapshot = new PostureSnapshot
        {
            Type = PostureSnapshotType.Knight,
            SchemaVersion = PostureSnapshotSchema.Version,
            FormulaVersion = run.ScoreFormulaVersion,
            CatalogVersion = run.CatalogVersion,
            SemanticFamily = $"knight:{run.SourceType}",
            SourceType = run.SourceType,
            SourceLabel = run.Source,
            CapturedAt = DateTimeOffset.UtcNow,
            Score = run.Score,                                    // knight-score-v1 é a autoridade (não recalcular)
            AchievedPoints = (int)Math.Round(achievedWeighted, MidpointRounding.AwayFromZero),
            PossiblePoints = (int)Math.Round(evaluatedWeight, MidpointRounding.AwayFromZero),
            EligiblePoints = (int)Math.Round(eligibleWeight, MidpointRounding.AwayFromZero),
            Coverage = run.Coverage,
            EvaluatedItems = evaluatedItems,
            EligibleItems = applicableItems,
            CompliantCount = run.PassedCount,
            NonCompliantCount = run.ExposedCount,
            MitigatedCount = run.MitigatedCount,
            NotEvaluatedCount = run.NotEvaluatedCount,
            ErrorCount = run.ErrorCount,
            NotApplicableCount = run.NotApplicableCount,
            DataRecency = dataRecency,
        };

        foreach (var i in run.Indicators)
        {
            snapshot.Indicators.Add(new PostureSnapshotIndicator
            {
                IndicatorId = i.IndicatorId,
                Title = i.Title,
                Category = i.Category,
                Severity = i.Severity,
                Status = i.Status,
                Evidence = i.Evidence,
                AffectedObjectCount = i.AffectedObjectCount,
                NistCodes = i.NistCodes.ToList(),
                MitreTechniques = i.MitreTechniques.ToList(),
                SourceType = i.SourceType,
                CollectedAt = i.CollectedAt,
            });
        }

        return snapshot;
    }

    // ---- Leitura -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PostureSnapshotSummaryDto>> ListAsync(PostureSnapshotType? type, CancellationToken ct = default)
    {
        var query = _db.PostureSnapshots.AsNoTracking();
        if (type is { } t)
            query = query.Where(s => s.Type == t);

        // Ordena por instante no cliente (o SQLite dos testes não traduz ORDER BY de DateTimeOffset). O histórico
        // por tenant é pequeno (publicações manuais explícitas) — materializar e ordenar em memória é proporcional.
        var rows = await query.ToListAsync(ct);
        return rows
            .OrderByDescending(s => s.CapturedAt).ThenByDescending(s => s.Id)
            .Select(ToSummary).ToList();
    }

    public async Task<PostureSnapshotDetailDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var snapshot = await LoadWithChildrenAsync(id, ct);
        return snapshot is null ? null : ToDetail(snapshot);
    }

    public async Task<PostureComparisonResultDto?> CompareAsync(Guid baseId, Guid targetId, CancellationToken ct = default)
    {
        var a = await LoadWithChildrenAsync(baseId, ct);
        var b = await LoadWithChildrenAsync(targetId, ct);
        if (a is null || b is null)
            return null;   // o controller responde 404 — uma das fotografias não existe (ou é de outro tenant)

        // Ordena por instante: "anterior" é a mais antiga, "atual" a mais recente — o delta segue o tempo, não a
        // ordem em que o cliente passou os ids (evita um comparativo invertido/enganoso).
        var (previous, current) = a.CapturedAt <= b.CapturedAt ? (a, b) : (b, a);

        var prevSide = ToComparisonSide(previous);
        var currSide = ToComparisonSide(current);

        var reasons = PostureSnapshotComparer.CheckCompatibility(prevSide, currSide);
        if (reasons.Count > 0)
            return new PostureComparisonResultDto(false, reasons, ToSummary(previous), ToSummary(current), null);

        var delta = PostureSnapshotComparer.BuildDelta(prevSide, currSide);
        return new PostureComparisonResultDto(true, Array.Empty<string>(), ToSummary(previous), ToSummary(current), delta);
    }

    private async Task<PostureSnapshot?> LoadWithChildrenAsync(Guid id, CancellationToken ct) =>
        await _db.PostureSnapshots.AsNoTracking()
            .Include(s => s.Controls)
            .Include(s => s.Indicators)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    // ---- Mapeamento ----------------------------------------------------------------------------------

    private static PostureComparisonSide ToComparisonSide(PostureSnapshot s)
    {
        var items = s.Type == PostureSnapshotType.AegisScoreNist
            ? s.Controls.Select(c => new PostureComparableItem(
                c.SubcategoryCode, c.SubcategoryCode,
                c.Evaluated ? c.Status!.Value.ToString() : "NotEvaluated",
                IsEvaluated: c.Evaluated,
                QualityRank: c.Evaluated ? AegisRank(c.Status!.Value) : 0)).ToList()
            : s.Indicators
                .Where(i => i.Status != KnightIndicatorStatus.NotApplicable)   // NotApplicable fica fora da comparação
                .Select(i => new PostureComparableItem(
                    i.IndicatorId, i.Title, i.Status.ToString(),
                    IsEvaluated: IsKnightEvaluated(i.Status), QualityRank: KnightRank(i.Status))).ToList();

        return new PostureComparisonSide(
            s.CapturedAt, s.Type.ToString(), s.SemanticFamily, s.FormulaVersion, s.CatalogVersion, s.SchemaVersion,
            s.Score, s.Coverage,
            s.CompliantCount, s.NonCompliantCount, s.MitigatedCount, s.NotEvaluatedCount, s.ErrorCount, s.NotApplicableCount,
            items);
    }

    /// <summary>Rank de qualidade AEGIS (maior = melhor): Compliant &gt; Mitigado &gt; NonCompliant.</summary>
    private static int AegisRank(ControlStatus status) => status switch
    {
        ControlStatus.Compliant => 2,
        ControlStatus.MitigatedByThirdParty => 1,
        _ => 0,   // NonCompliant
    };

    /// <summary>Rank de conservadorismo do desempate de telemetria (menor = pior veredito) — igual à projeção.</summary>
    private static int ConservativeRank(ControlStatus status) => status switch
    {
        ControlStatus.NonCompliant => 0,
        ControlStatus.MitigatedByThirdParty => 1,
        ControlStatus.Compliant => 2,
        _ => 3,
    };

    /// <summary>Rank de qualidade KNIGHT (maior = melhor): Passed &gt; Mitigated &gt; Exposed.</summary>
    private static int KnightRank(KnightIndicatorStatus status) => status switch
    {
        KnightIndicatorStatus.Passed => 2,
        KnightIndicatorStatus.Mitigated => 1,
        _ => 0,   // Exposed (ou não avaliado — o rank é ignorado nesse caso)
    };

    private static bool IsKnightEvaluated(KnightIndicatorStatus status) => status
        is KnightIndicatorStatus.Passed or KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated;

    private static string EvaluationStateOf(double? score) => score is null ? "NotEvaluated" : "Evaluated";

    private static string SanitizeTitle(string? title)
    {
        var t = (title ?? "").Trim();
        return t.Length <= MaxDocumentTitleLength ? t : t[..MaxDocumentTitleLength];
    }

    private static PostureSnapshotSummaryDto ToSummary(PostureSnapshot s) => new(
        s.Id,
        s.Type.ToString(),
        s.SchemaVersion,
        s.FormulaVersion,
        s.CatalogVersion,
        s.SemanticFamily,
        s.SourceType?.ToString(),
        s.SourceLabel,
        s.CapturedAt,
        EvaluationStateOf(s.Score),
        s.Score,
        s.Coverage,
        s.EvaluatedItems,
        s.EligibleItems,
        s.CompliantCount,
        s.NonCompliantCount,
        s.MitigatedCount,
        s.NotEvaluatedCount,
        s.ErrorCount,
        s.NotApplicableCount,
        s.DataRecency,
        s.ContentHash);

    private static PostureSnapshotDetailDto ToDetail(PostureSnapshot s)
    {
        var controls = s.Controls
            .OrderBy(c => c.SubcategoryCode, StringComparer.Ordinal)
            .Select(c => new PostureSnapshotControlDto(
                c.SubcategoryCode, c.FunctionCode, c.Evaluated,
                c.Status?.ToString(), c.AchievedPoints, c.MaxPoints, c.VerdictSource?.ToString(), c.EvaluatedAt,
                c.EvidenceRefs.Select(e => new PostureEvidenceRefDto(e.Kind, e.Source, e.Reference, e.CollectedAt)).ToList()))
            .ToList();

        var indicators = s.Indicators
            .OrderBy(i => i.IndicatorId, StringComparer.Ordinal)
            .Select(i => new PostureSnapshotIndicatorDto(
                i.IndicatorId, i.Title, i.Category.ToString(), i.Severity.ToString(), i.Status.ToString(),
                i.Evidence, i.AffectedObjectCount, i.NistCodes, i.MitreTechniques, i.SourceType.ToString(), i.CollectedAt))
            .ToList();

        return new PostureSnapshotDetailDto(
            ToSummary(s), s.AchievedPoints, s.PossiblePoints, s.EligiblePoints, controls, indicators);
    }

    /// <summary>Projeção leve de um sinal (com a capability do seu conector) para reconstruir a proveniência decisiva.</summary>
    private sealed record SignalRef(
        Guid Id, string SignalKey, string? Source, string? ExternalEventId,
        double? NumericValue, int? Severity, string? Unit, DateTimeOffset CollectedAt, ConnectorCapability Capability);
}
