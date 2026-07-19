using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Assessment;
using AegisScore.Application.Queries;
using AegisScore.Application.Telemetry.Models;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Infrastructure.Queries;

/// <summary>
/// Lê a matriz de conformidade do tenant sobre o AegisScoreDbContext.
///
/// Zero Trust: NÃO há <c>.Where(x => x.TenantId == ...)</c>. O Global Query Filter (fail-closed) já
/// restringe TenantControlStates ao tenant ambiente resolvido do JWT — delegar o recorte ao filtro É o
/// próprio isolamento. Um <c>Where</c> explícito seria pior: daria a falsa impressão de que a segurança
/// depende desta linha e não do DbContext, e mascararia um filtro removido por acidente.
///
/// Performance: <c>AsNoTracking</c> (leitura pura, sem change tracker) e projeção direta no banco — o
/// SELECT carrega apenas as 7 colunas do DTO, nunca a entidade inteira nem o grafo da subcategoria.
/// </summary>
public sealed class ControlStateDashboardQuery : IControlStateDashboardQuery
{
    private readonly AegisScoreDbContext _db;
    private readonly ScoringOptions _options;
    private readonly TimeProvider _clock;

    public ControlStateDashboardQuery(
        AegisScoreDbContext db, IOptions<ScoringOptions> options, TimeProvider clock)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default)
    {
        // Projeta as colunas no banco (inclui os blobs crus); a desserialização roda em memória — o EF não
        // traduz JSON→objeto no SQL, e o payload por tenant é pequeno. Os enums vêm CRUS (não .ToString()):
        // o status ainda decide a severidade-proxy, então precisamos dele tipado antes de achatar o DTO.
        var rows = await _db.TenantControlStates
            .AsNoTracking()
            .OrderBy(x => x.Subcategory!.Code)
            .Select(x => new Row(
                x.SubcategoryId,
                x.Subcategory!.Code,
                x.CurrentScore,
                x.Subcategory!.MaxScorePoints,   // denominador do catálogo, via JOIN — jamais desnormalizado
                x.Status,
                x.AiEvidence,
                x.LastEvaluatedAt,
                x.LastVerdictSource,
                x.ChecksJson,
                x.IntelligenceJson,
                x.MissingRequirements))   // jsonb TIPADO — o ValueConverter já entrega a lista pronta
            .ToListAsync(ct);

        // Contexto para a auditoria de FRESCOR (ver EnrichWithStaleness): as exigências de evidência do
        // catálogo (reference data global) e a cobertura documental ACEITA deste tenant. Duas consultas
        // fixas, fora do laço — nunca N+1.
        var codes = rows.Select(r => r.SubcategoryCode).ToList();
        var rules = await _db.AssessmentRules.AsNoTracking()
            .Where(r => codes.Contains(r.SubcategoryCode))
            .ToDictionaryAsync(r => r.SubcategoryCode, r => r.EvidenceRequirements, ct);

        // "Verificado" = processado E ACEITO pelo RAG, não apenas enviado. O DocumentAnalysisWorker só
        // grava Coberto quando a confiança passa do limiar; Parcial significa que o RAG NÃO se convenceu.
        // A fonte precisa incluir Document: cobertura vinda só de Interview é auto-declaração, e tratá-la
        // como prova documental deixaria o auditado atestar a si mesmo.
        var verifiedCoverage = await _db.SubcategoryCoverages.AsNoTracking()
            .Where(c => c.Status == CoverageStatus.Coberto
                     && (c.EvidenceSource == CoverageEvidenceSource.Document
                      || c.EvidenceSource == CoverageEvidenceSource.Both))
            .Select(c => c.SubcategoryCode)
            .ToListAsync(ct);
        var verified = verifiedCoverage.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var now = _clock.GetUtcNow();
        return rows.Select(r => EnrichWithStaleness(ToDto(r), r, rules, verified, now)).ToList();
    }

    /// <summary>
    /// Acrescenta ao DTO as lacunas que só a LEITURA enxerga: o sinal que envelheceu e a cobertura
    /// documental que nunca foi aceita. O motor de ingestão não pode detectá-las — no instante em que ele
    /// roda, o payload que está avaliando é, por definição, fresco.
    ///
    /// ADITIVO por decisão: as lacunas persistidas pelo motor nunca são apagadas — ele viu o payload cru,
    /// esta camada só vê datas. Uma lacuna derivada só entra quando não há já uma da mesma natureza.
    ///
    /// ⚠️ A idade vem de <c>TenantControlState.LastEvaluatedAt</c> com fonte Telemetry, e NÃO de
    /// <c>EvidenceSignal.CollectedAt</c>: a esteira de telemetria (/telemetry/*) não grava EvidenceSignal
    /// — hoje só o MicrosoftSecureScoreConnector o faz. Cronometrar pelo EvidenceSignal marcaria como
    /// obsoleto TODO controle avaliado pela esteira principal, que é a maioria deles.
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

        return additions.Count == 0
            ? dto
            : dto with { MissingRequirements = dto.MissingRequirements.Concat(additions).ToList() };
    }

    /// <summary>
    /// Achata a linha crua no contrato do HUD: enums viram string na fronteira e o blob de inteligência é
    /// espalhado nos campos do DTO. O frontend recebe um objeto plano e não conhece a existência do blob.
    /// </summary>
    private static TenantControlStateDto ToDto(Row r)
    {
        var intel = SafeDeserialize<ControlIntelligence>(r.IntelligenceJson);

        return new TenantControlStateDto(
            r.SubcategoryId, r.SubcategoryCode, r.ScorePoints, r.MaxScorePoints,
            r.Status.ToString(), r.AiEvidence, r.LastEvaluatedAt, r.LastVerdictSource.ToString(),
            SafeDeserialize<IReadOnlyList<ComplianceCheck>>(r.ChecksJson) ?? Array.Empty<ComplianceCheck>())
        {
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

            // Enum → nome na fronteira (ver MissingRequirementDto): o Angular decide o ícone por
            // "Telemetry"/"Documentation", nunca pela posição do enum no domínio.
            MissingRequirements = r.MissingRequirements
                .Select(m => new MissingRequirementDto(
                    m.Type.ToString(), m.SourceIdentifier, m.Description))
                .ToList(),
        };
    }

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

    /// <summary>Projeção intermediária: as colunas cruas do banco, antes da desserialização dos blobs.</summary>
    private sealed record Row(
        Guid SubcategoryId, string SubcategoryCode, int ScorePoints, int MaxScorePoints,
        ControlStatus Status, string? AiEvidence, DateTimeOffset LastEvaluatedAt, VerdictSource LastVerdictSource,
        string? ChecksJson, string? IntelligenceJson, List<MissingRequirement> MissingRequirements);
}
