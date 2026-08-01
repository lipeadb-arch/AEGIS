using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Assessment;
using AegisScore.Application.Queries;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// [AEGIS-AUD-002] Lê a matriz de conformidade do tenant sobre o AegisScoreDbContext, PARTINDO DO CATÁLOGO
/// ATIVO e associando os estados do tenant — de modo a devolver TAMBÉM as subcategorias sem estado como
/// <c>NotEvaluated</c> (distintas de <c>NonCompliant</c>), fora do denominador do score e dentro da lacuna
/// de cobertura. Nenhuma linha artificial com zero é gravada: NotEvaluated existe só no read model.
///
/// Zero Trust / fail-closed: o tenant é resolvido do <see cref="ITenantContext"/> (claim <c>tenant_id</c>);
/// sem tenant, retorna VAZIO — o catálogo é global e não pode vazar sozinho. Os estados são recortados pelo
/// Global Query Filter (não há <c>.Where(TenantId)</c> explícito).
/// </summary>
public sealed class ControlStateDashboardQuery : IControlStateDashboardQuery
{
    private readonly AegisScoreDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ScoringOptions _options;
    private readonly TimeProvider _clock;

    public ControlStateDashboardQuery(
        AegisScoreDbContext db, ITenantContext tenant, IOptions<ScoringOptions> options, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default)
    {
        // Fail-closed: sem tenant ambiente, nada é projetado — o catálogo é global e não pode vazar sozinho.
        if (_tenant.TenantId is null)
            return Array.Empty<TenantControlStateDto>();

        // A projeção PARTE do catálogo ativo (reference data global), ordenado pelo código NIST.
        var catalog = await (from s in _db.Subcategories.AsNoTracking()
                             join c in _db.Categories on s.CategoryId equals c.Id
                             join f in _db.Functions on c.FunctionId equals f.Id
                             join fv in _db.FrameworkVersions on f.FrameworkVersionId equals fv.Id
                             where fv.IsActive
                             orderby s.Code
                             select new CatalogEntry(s.Id, s.Code, s.MaxScorePoints)).ToListAsync(ct);

        // Estados AVALIADOS do tenant (Global Query Filter fail-closed). Enums CRUS: o status decide a
        // severidade-proxy e o motivo antes de achatar o DTO.
        var states = await _db.TenantControlStates
            .AsNoTracking()
            .Select(x => new Row(
                x.SubcategoryId,
                x.Subcategory!.Code,
                x.CurrentScore,
                x.Subcategory!.MaxScorePoints,
                x.Status,
                x.AiEvidence,
                x.LastEvaluatedAt,
                x.LastVerdictSource,
                x.ChecksJson,
                x.IntelligenceJson,
                x.MissingRequirements))
            .ToListAsync(ct);
        var stateBySub = states.ToDictionary(r => r.SubcategoryId);

        // Contexto de FRESCOR (ver EnrichWithStaleness) apenas para os avaliados — NotEvaluated não tem
        // sinal a envelhecer. Duas consultas fixas, fora do laço — nunca N+1.
        var codes = states.Select(r => r.SubcategoryCode).ToList();
        var rules = await _db.AssessmentRules.AsNoTracking()
            .Where(r => codes.Contains(r.SubcategoryCode))
            .ToDictionaryAsync(r => r.SubcategoryCode, r => r.EvidenceRequirements, ct);

        var verifiedCoverage = await _db.SubcategoryCoverages.AsNoTracking()
            .Where(c => c.Status == CoverageStatus.Coberto
                     && (c.EvidenceSource == CoverageEvidenceSource.Document
                      || c.EvidenceSource == CoverageEvidenceSource.Both))
            .Select(c => c.SubcategoryCode)
            .ToListAsync(ct);
        var verified = verifiedCoverage.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var now = _clock.GetUtcNow();
        var result = new List<TenantControlStateDto>(catalog.Count);
        foreach (var entry in catalog)
        {
            result.Add(stateBySub.TryGetValue(entry.Id, out var r)
                ? EnrichWithStaleness(ToDto(r), r, rules, verified, now)   // avaliado
                : NotEvaluated(entry));                                     // sem estado → NotEvaluated
        }
        return result;
    }

    /// <summary>Subcategoria do catálogo SEM estado — NotEvaluated no read model, nunca uma linha zero no banco.</summary>
    private static TenantControlStateDto NotEvaluated(CatalogEntry entry) =>
        new(entry.Id, entry.Code, 0, entry.MaxScorePoints,
            NotEvaluatedStatus, null, null, null, Array.Empty<ComplianceCheck>())
        {
            Severity = nameof(SeverityLevel.Informational),
            Reason = "Sem evidência avaliada para este controle.",
        };

    /// <summary>Status de fronteira (não existe no enum de domínio) para uma subcategoria sem TenantControlState.</summary>
    private const string NotEvaluatedStatus = "NotEvaluated";

    /// <summary>
    /// Acrescenta ao DTO as lacunas que só a LEITURA enxerga: o sinal que envelheceu e a cobertura
    /// documental que nunca foi aceita. O motor de ingestão não pode detectá-las — no instante em que ele
    /// roda, o payload que está avaliando é, por definição, fresco.
    ///
    /// ADITIVO por decisão: as lacunas persistidas pelo motor nunca são apagadas — ele viu o payload cru,
    /// esta camada só vê datas. Uma lacuna derivada só entra quando não há já uma da mesma natureza.
    /// </summary>
    private TenantControlStateDto EnrichWithStaleness(
        TenantControlStateDto dto, Row r,
        IReadOnlyDictionary<string, List<string>> rules, IReadOnlySet<string> verified, DateTimeOffset now)
    {
        if (!rules.TryGetValue(r.SubcategoryCode, out var requirements))
            return dto;   // sem regra no catálogo não há como afirmar a natureza da prova

        var availability = new EvidenceAvailability(
            LastTelemetryAt: r.LastVerdictSource == VerdictSource.Telemetry ? r.LastEvaluatedAt : null,
            HasVerifiedDocumentaryCoverage: verified.Contains(r.SubcategoryCode));

        var derived = RuleEvaluator.Compile(requirements, availability, now, _options.FreshnessWindow);
        if (derived.Count == 0)
            return dto;

        var known = dto.MissingRequirements.Select(m => m.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = derived
            .Where(d => !known.Contains(d.Type.ToString()))
            .Select(d => new MissingRequirementDto(d.Type.ToString(), d.SourceIdentifier, d.Description))
            .ToList();
        if (additions.Count == 0)
            return dto;

        var mergedMissing = dto.MissingRequirements.Concat(additions).ToList();
        return dto with { MissingRequirements = mergedMissing, Reason = ReasonFor(r.Status, mergedMissing) };
    }

    /// <summary>
    /// Achata a linha crua no contrato do HUD: enums viram string na fronteira e o blob de inteligência é
    /// espalhado nos campos do DTO. O frontend recebe um objeto plano e não conhece a existência do blob.
    /// </summary>
    private static TenantControlStateDto ToDto(Row r)
    {
        var intel = SafeDeserialize<ControlIntelligence>(r.IntelligenceJson);

        var missing = r.MissingRequirements
            .Select(m => new MissingRequirementDto(m.Type.ToString(), m.SourceIdentifier, m.Description))
            .ToList();

        return new TenantControlStateDto(
            r.SubcategoryId, r.SubcategoryCode, r.ScorePoints, r.MaxScorePoints,
            r.Status.ToString(), r.AiEvidence, r.LastEvaluatedAt, r.LastVerdictSource.ToString(),
            SafeDeserialize<IReadOnlyList<ComplianceCheck>>(r.ChecksJson) ?? Array.Empty<ComplianceCheck>())
        {
            Reason = ReasonFor(r.Status, missing),

            // A severidade do motor manda; sem ela, o proxy derivado do status (o card nunca fica sem badge).
            Severity = (intel?.Severity ?? SeverityLevels.FromStatus(r.Status)).ToString(),
            TelemetryEvidence = intel?.TelemetryEvidence,
            RemediationPlan = intel?.RemediationPlan,
            AiConfidenceScore = intel?.AiConfidenceScore,
            ThreatLandscape = intel?.ThreatLandscape ?? Array.Empty<string>(),
            MttdMinutes = intel?.MttdMinutes,
            MttrMinutes = intel?.MttrMinutes,

            // ⚠️ Sem produtor: não existe snapshot POR CONTROLE (só o agregado diário do tenant, que
            // alimenta o /trend). Entregar vazio é o honesto — a sparkline se omite; sintetizar a série
            // seria forjar histórico de conformidade. Ver ComplianceHistoryPoint.
            HistoricalCompliance = Array.Empty<ComplianceHistoryPoint>(),

            MissingRequirements = missing,
        };
    }

    /// <summary>[AEGIS-AUD-002] Motivo legível de por que o controle não pontua (ou pontua parcialmente).</summary>
    private static string? ReasonFor(ControlStatus status, IReadOnlyList<MissingRequirementDto> missing) => status switch
    {
        ControlStatus.Compliant => null,   // pontua integralmente — não há motivo de não-pontuação
        ControlStatus.MitigatedByThirdParty =>
            "Risco coberto por controle compensatório/terceiro (crédito parcial de 50%).",
        _ => missing.Count > 0
            ? missing[0].Description                                    // a lacuna concreta (falta log/documento)
            : "Não conformidade comprovada pela evidência avaliada.",  // reprovação de mérito
    };

    /// <summary>
    /// Desserializa um blob persistido; tolera nulo/JSON inválido (devolve null, nunca lança). Um blob
    /// explicável corrompido não pode derrubar o dashboard inteiro — o score é a informação crítica.
    /// </summary>
    private static T? SafeDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Entrada do catálogo ativo (reference data global) para a projeção catalog-first.</summary>
    private sealed record CatalogEntry(Guid Id, string Code, int MaxScorePoints);

    /// <summary>Projeção intermediária: as colunas cruas do banco, antes da desserialização dos blobs.</summary>
    private sealed record Row(
        Guid SubcategoryId, string SubcategoryCode, int ScorePoints, int MaxScorePoints,
        ControlStatus Status, string? AiEvidence, DateTimeOffset LastEvaluatedAt, VerdictSource LastVerdictSource,
        string? ChecksJson, string? IntelligenceJson, List<MissingRequirement> MissingRequirements);
}
