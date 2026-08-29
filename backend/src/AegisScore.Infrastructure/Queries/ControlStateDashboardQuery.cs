using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Assessment;
using AegisScore.Application.Queries;
using AegisScore.Application.Services;
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
    private readonly IControlLanguageCatalog _language;

    public ControlStateDashboardQuery(
        AegisScoreDbContext db, ITenantContext tenant, IOptions<ScoringOptions> options, TimeProvider clock,
        IControlLanguageCatalog language)
    {
        _db = db;
        _tenant = tenant;
        _options = options.Value;
        _clock = clock;
        _language = language;
    }

    public async Task<IReadOnlyList<TenantControlStateDto>> GetDashboardAsync(CancellationToken ct = default)
    {
        // Fail-closed: sem tenant ambiente, nada é projetado — o catálogo é global e não pode vazar sozinho.
        if (_tenant.TenantId is null)
            return Array.Empty<TenantControlStateDto>();

        // A projeção PARTE do catálogo ativo (reference data global), ordenado pelo código NIST. A descrição
        // OFICIAL viaja junto — referência técnica secundária, separada da redação autoral em linguagem clara.
        var catalog = await (from s in _db.Subcategories.AsNoTracking()
                             join c in _db.Categories on s.CategoryId equals c.Id
                             join f in _db.Functions on c.FunctionId equals f.Id
                             join fv in _db.FrameworkVersions on f.FrameworkVersionId equals fv.Id
                             where fv.IsActive
                             orderby s.Code
                             select new CatalogEntry(s.Id, s.Code, s.MaxScorePoints, s.Description)).ToListAsync(ct);

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

        // Carrega o tipo PERSISTIDO de evidência junto das exigências — a compilação de lacunas classifica
        // pelo tipo (autoridade única), não por re-inferência da string (AEGIS-MVP-POSTURE-01). O catálogo de
        // regras é GLOBAL e pequeno (99 linhas): carregá-lo INTEIRO numa consulta serve tanto o FRESCOR dos
        // avaliados (EnrichWithStaleness) quanto a CLASSIFICAÇÃO dos NotEvaluated — sem N+1 e sem filtrar por
        // um subconjunto de códigos (que deixaria os NotEvaluated sem a regra para classificar o motivo).
        var ruleRows = await _db.AssessmentRules.AsNoTracking()
            .Select(r => new { r.SubcategoryCode, r.EvidenceRequirements, r.EvidenceType })
            .ToListAsync(ct);
        var rules = ruleRows.ToDictionary(
            r => r.SubcategoryCode, r => (r.EvidenceRequirements, r.EvidenceType), StringComparer.Ordinal);

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
            var dto = stateBySub.TryGetValue(entry.Id, out var r)
                ? EnrichWithStaleness(ToDto(r), r, rules, verified, now)   // avaliado
                : NotEvaluated(entry, rules);                              // sem estado → NotEvaluated
            result.Add(WithLanguage(dto, entry));                          // linguagem clara + descrição oficial
        }
        return result;
    }

    /// <summary>
    /// Subcategoria do catálogo SEM estado — NotEvaluated no read model, nunca uma linha zero no banco. O motivo
    /// é DETERMINÍSTICO (ver <see cref="ClassifyNotEvaluated"/>): derivado do tipo de evidência da regra, ou
    /// <c>Unsupported</c> quando não há regra avaliável — jamais de LLM ou parsing livre. Quando há regra tipada,
    /// as lacunas são MATERIALIZADAS (para "Pontos Cegos"); em <c>Unsupported</c>, ficam vazias (não se finge
    /// que falta telemetria ou documento onde o AEGIS simplesmente não sabe avaliar).
    /// </summary>
    private TenantControlStateDto NotEvaluated(
        CatalogEntry entry,
        IReadOnlyDictionary<string, (List<string> Requirements, RuleEvidenceType EvidenceType)> rules)
    {
        var (kind, reason, missing) = ClassifyNotEvaluated(entry.Code, rules);
        return new(entry.Id, entry.Code, 0, entry.MaxScorePoints,
            NotEvaluatedStatus, null, null, null, Array.Empty<ComplianceCheck>())
        {
            Severity = nameof(SeverityLevel.Informational),
            Reason = reason,
            NotEvaluatedReason = kind.ToString(),
            MissingRequirements = missing,
        };
    }

    /// <summary>
    /// [AEGIS-MVP-LANGUAGE-01] Classifica DETERMINISTICAMENTE por que um controle está sem estado, a partir do
    /// tipo de evidência TIPADO e PERSISTIDO da regra (nunca por texto): Telemetry → TelemetryRequired;
    /// Documentation → DocumentationRequired; Both → BothRequired; sem regra → Unsupported. Para os três
    /// primeiros, materializa UMA lacuna GENÉRICA e provider-neutral (para "Pontos Cegos" mostrar a natureza),
    /// SEM revelar fornecedor, conector, permissão ou aplicabilidade — que um controle NUNCA avaliado não permite
    /// afirmar. Por isso NÃO passa por <see cref="RuleEvaluator.Compile"/>, que escolheria fonte primária e
    /// alternativas do catálogo de regras (revelando nomes de produto); Compile fica só no caminho dos AVALIADOS
    /// (<see cref="EnrichWithStaleness"/>). Unsupported não materializa lacuna alguma.
    /// </summary>
    private static (NotEvaluatedReasonKind Kind, string Reason, IReadOnlyList<MissingRequirementDto> Missing) ClassifyNotEvaluated(
        string code,
        IReadOnlyDictionary<string, (List<string> Requirements, RuleEvidenceType EvidenceType)> rules)
    {
        // Sem regra avaliável → o AEGIS não tem método. SEM lacuna forjada.
        if (!rules.TryGetValue(code, out var rule))
            return (NotEvaluatedReasonKind.Unsupported, UnsupportedMessage, Array.Empty<MissingRequirementDto>());

        return rule.EvidenceType switch
        {
            RuleEvidenceType.Telemetry => (NotEvaluatedReasonKind.TelemetryRequired,
                "Ainda não medido: nenhuma telemetria elegível foi avaliada.",
                One(ComplianceRequirementType.Telemetry, EligibleTelemetrySource,
                    "Nenhuma telemetria elegível foi avaliada para este controle.")),
            RuleEvidenceType.Documentation => (NotEvaluatedReasonKind.DocumentationRequired,
                "Ainda não validado: exige documento ou validação humana.",
                One(ComplianceRequirementType.Documentation, RuleEvaluator.ManualAuditToken,
                    "Este controle exige documento processado ou validação humana.")),
            RuleEvidenceType.Both => (NotEvaluatedReasonKind.BothRequired,
                "Ainda não medido por completo: exige telemetria e validação documental.",
                One(ComplianceRequirementType.Both, TelemetryAndValidationSource,
                    "Este controle exige telemetria elegível e validação documental.")),
            // Tipo fora do enum conhecido (regra adulterada) → não avaliável, sem forjar lacuna.
            _ => (NotEvaluatedReasonKind.Unsupported, UnsupportedMessage, Array.Empty<MissingRequirementDto>()),
        };
    }

    /// <summary>Uma única lacuna genérica e provider-neutral — o caminho NotEvaluated não revela fornecedor.</summary>
    private static IReadOnlyList<MissingRequirementDto> One(ComplianceRequirementType type, string source, string description) =>
        new[] { new MissingRequirementDto(type.ToString(), source, description) };

    /// <summary>
    /// Identificadores de FONTE estáveis (de máquina) para as lacunas genéricas de um controle NUNCA avaliado —
    /// traduzidos por <c>sourceLabelOf()</c> no frontend (o rótulo de apresentação mora lá, não no identificador).
    /// A lacuna documental reusa <see cref="RuleEvaluator.ManualAuditToken"/> (já mapeado para "Validação manual").
    /// </summary>
    private const string EligibleTelemetrySource = "ELIGIBLE_TELEMETRY_SOURCE";
    private const string TelemetryAndValidationSource = "TELEMETRY_AND_VALIDATION";

    private const string UnsupportedMessage =
        "O AEGIS ainda não possui método suficiente para avaliar este controle.";

    /// <summary>
    /// Acopla a camada de LINGUAGEM CLARA (autoral) e a descrição OFICIAL (secundária) ao DTO. FAIL-CLOSED em
    /// runtime: toda subcategoria ATIVA consultada EXIGE redação (<see cref="IControlLanguageCatalog.GetRequired"/>).
    /// Se o catálogo ativo do banco ganhar um código sem entrada, a consulta FALHA de forma explícita e
    /// SANITIZADA (só o código no erro — sem caminho nem conteúdo do arquivo), em vez de devolver campos nulos que
    /// fariam o frontend cair para o código: nenhum controle ativo volta silenciosamente à apresentação
    /// incompleta. A completude do artefato versionado (106 códigos) é garantida por teste; este guard cobre a
    /// divergência catálogo↔redação em runtime.
    /// </summary>
    private TenantControlStateDto WithLanguage(TenantControlStateDto dto, CatalogEntry entry)
    {
        var lang = _language.GetRequired(entry.Code);
        return dto with
        {
            Title = lang.Title,
            Summary = lang.Summary,
            Impact = lang.Impact,
            InitialAction = lang.InitialAction,
            OfficialDescription = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description,
        };
    }

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
        IReadOnlyDictionary<string, (List<string> Requirements, RuleEvidenceType EvidenceType)> rules,
        IReadOnlySet<string> verified, DateTimeOffset now)
    {
        if (!rules.TryGetValue(r.SubcategoryCode, out var rule))
            return dto;   // sem regra no catálogo não há como afirmar a natureza da prova

        var availability = new EvidenceAvailability(
            LastTelemetryAt: r.LastVerdictSource == VerdictSource.Telemetry ? r.LastEvaluatedAt : null,
            HasVerifiedDocumentaryCoverage: verified.Contains(r.SubcategoryCode));

        var derived = RuleEvaluator.Compile(
            rule.EvidenceType, rule.Requirements, availability, now, _options.FreshnessWindow);
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

    /// <summary>Entrada do catálogo ativo (reference data global) para a projeção catalog-first — inclui a
    /// descrição OFICIAL da subcategoria, referência técnica secundária ao lado da redação em linguagem clara.</summary>
    private sealed record CatalogEntry(Guid Id, string Code, int MaxScorePoints, string Description);

    /// <summary>Projeção intermediária: as colunas cruas do banco, antes da desserialização dos blobs.</summary>
    private sealed record Row(
        Guid SubcategoryId, string SubcategoryCode, int ScorePoints, int MaxScorePoints,
        ControlStatus Status, string? AiEvidence, DateTimeOffset LastEvaluatedAt, VerdictSource LastVerdictSource,
        string? ChecksJson, string? IntelligenceJson, List<MissingRequirement> MissingRequirements);
}
