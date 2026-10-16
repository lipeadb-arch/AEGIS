using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Posture;
using AegisScore.Application.Scoring;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Posture;

namespace AegisScore.Infrastructure.Tests.Legacy;

/// <summary>
/// Gera fotografias HISTÓRICAS <c>aegis-score-v1</c> da abordagem aposentada para os testes de leitura, comparação,
/// integridade e exportação. O código de construção foi transferido de <see cref="PostureSnapshotService"/> sem mudança
/// de semântica (catálogo ATIVO COMPLETO em left join com o ledger, proveniência decisiva, bundle de referência); a
/// publicação reproduz os mesmos passos (cliente congelado, tenant carimbado antes do hash, hash determinístico).
/// A aplicação não gera mais este tipo — em produção ele só existe como histórico.
/// </summary>
internal sealed class LegacyAegisSnapshotFixture
{
    /// <summary>Teto de tamanho do rótulo do documento preservado como referência de evidência (metadado, não conteúdo).</summary>
    private const int MaxDocumentTitleLength = 200;

    private readonly AegisScoreDbContext _db;
    private readonly NistSignalMapper _mapper;

    private LegacyAegisSnapshotFixture(AegisScoreDbContext db)
    {
        _db = db;
        _mapper = new NistSignalMapper(db);
    }

    /// <summary>Publica uma fotografia <c>aegis-score-v1</c> do tenant como a publicação de produção fazia.</summary>
    public static async Task<PostureSnapshotDetailDto> PublishAsync(
        AegisScoreDbContext db, Guid? tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId
            ?? throw new TenantSecurityException("Publicação de fotografia sem tenant resolvido no contexto (fail-closed).");

        var snapshot = await new LegacyAegisSnapshotFixture(db).BuildAegisSnapshotAsync(ct);
        snapshot.ClientName = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenant).Select(t => t.Name).FirstOrDefaultAsync(ct);

        snapshot.TenantId = tenant;
        foreach (var c in snapshot.Controls) c.TenantId = tenant;
        foreach (var i in snapshot.Indicators) i.TenantId = tenant;
        foreach (var a in snapshot.ActionItems) a.TenantId = tenant;
        foreach (var o in snapshot.Objects) o.TenantId = tenant;

        snapshot.ContentHash = PostureSnapshotHasher.Compute(snapshot);

        db.PostureSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        return PostureSnapshotService.ToDetail(snapshot);
    }

    /// <summary>
    /// [AEGIS-MVP-POSTURE-01] Compõe uma identificação AUDITÁVEL e reproduzível do bundle de referência
    /// (catálogo NIST + metodologia AEGIS + regras): um SHA-256 COMPLETO sobre os TRÊS hashes completos da
    /// proveniência vigente e a versão da metodologia — não oito caracteres de cada. Determinística (mesmo
    /// conteúdo → mesma string) e sensível a qualquer componente (qualquer hash muda → bundle diferente), para
    /// o comparador retornar <c>DifferentCatalogVersion</c> em vez de um delta enganoso. Cabe em 200 chars.
    /// </summary>
    private async Task<string> BuildAegisBundleVersionAsync(Guid frameworkVersionId, string frameworkName, CancellationToken ct)
    {
        var prov = await _db.ReferenceDatasetProvenances.AsNoTracking()
            .Where(p => p.FrameworkVersionId == frameworkVersionId && p.IsCurrent)
            .Select(p => new { p.Kind, p.ContentHash, p.MethodologyVersion })
            .ToListAsync(ct);

        string Hash(ReferenceDatasetKind kind) =>
            prov.FirstOrDefault(p => p.Kind == kind)?.ContentHash ?? "unknown";

        var methodologyVersion = prov.FirstOrDefault(p => p.Kind == ReferenceDatasetKind.AegisMethodology)
            ?.MethodologyVersion ?? "meth-desconhecida";

        // Forma canônica sobre os hashes COMPLETOS + versões; o bundle é o SHA-256 disso.
        var canonical = string.Join("|",
            frameworkName, methodologyVersion,
            "cat:" + Hash(ReferenceDatasetKind.NistCatalog),
            "meth:" + Hash(ReferenceDatasetKind.AegisMethodology),
            "rules:" + Hash(ReferenceDatasetKind.AegisAssessmentRules));
        var bundleHash = ReferenceDataFingerprint.Sha256Hex(canonical);

        var bundle = $"{frameworkName}|{methodologyVersion}|bundle:{bundleHash}";
        return bundle.Length <= 200 ? bundle : bundle[..200];
    }

    /// <summary>
    /// Constrói a fotografia AEGIS Score/NIST. Parte do catálogo ATIVO COMPLETO (todas as subcategorias elegíveis)
    /// em LEFT JOIN com o ledger do tenant: cada controle elegível vira uma linha, avaliado ou não. Score/cobertura
    /// vêm da MESMA autoridade do Score Atual (aegis-score-v1). A proveniência é a evidência que fundamenta o veredito.
    /// </summary>
    private async Task<PostureSnapshot> BuildAegisSnapshotAsync(CancellationToken ct)
    {
        var score = await AegisScoreAggregator.AggregateAsync(_db, ct);

        var framework = await _db.FrameworkVersions.AsNoTracking()
            .Where(f => f.IsActive).Select(f => new { f.Id, f.Name }).FirstOrDefaultAsync(ct);
        var frameworkName = framework?.Name ?? "framework-desconhecido";

        // [AEGIS-MVP-POSTURE-01] Identificação REPRODUZÍVEL do bundle (catálogo + metodologia + regras) que
        // produziu a foto — distingue revisões que o nome do framework sozinho não distinguiria. É o que o
        // comparador usa para separar bundles diferentes (não confundir com o CatalogVersion do KNIGHT).
        var bundleVersion = framework is null
            ? frameworkName
            : await BuildAegisBundleVersionAsync(framework.Id, frameworkName, ct);

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
            CatalogVersion = bundleVersion,
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

    /// <summary>Rank de conservadorismo do desempate de telemetria (menor = pior veredito) — igual à projeção.</summary>
    private static int ConservativeRank(ControlStatus status) => status switch
    {
        ControlStatus.NonCompliant => 0,
        ControlStatus.MitigatedByThirdParty => 1,
        ControlStatus.Compliant => 2,
        _ => 3,
    };

    private static string SanitizeTitle(string? title)
    {
        var t = (title ?? "").Trim();
        return t.Length <= MaxDocumentTitleLength ? t : t[..MaxDocumentTitleLength];
    }

    /// <summary>Projeção leve de um sinal (com a capability do seu conector) para reconstruir a proveniência decisiva.</summary>
    private sealed record SignalRef(
        Guid Id, string SignalKey, string? Source, string? ExternalEventId,
        double? NumericValue, int? Severity, string? Unit, DateTimeOffset CollectedAt, ConnectorCapability Capability);
}
