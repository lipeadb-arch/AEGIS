using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Posture.Export;

// ============================================================================
//  [AEGIS-KNIGHT-MULTICLOUD-01] Modelo ÚNICO do relatório KNIGHT
// ============================================================================
// HTML e CSV derivam DESTE modelo, e o modelo deriva EXCLUSIVAMENTE da fotografia imutável. É o que garante
// que os dois arquivos contem a mesma coisa: controles, ocorrências (objeto × controle) e objetos únicos são
// calculados uma vez, aqui. Nada consulta o estado operacional de hoje — nem planos, nem coletas, nem o catálogo
// para substituir o que foi congelado (o catálogo corrente só entra como rótulo de campos que a v1 não tinha,
// e isso é dito no próprio relatório).

public sealed record ReportHeader(
    Guid SnapshotId, Guid? RunId, string? ClientName, string SourceLabel, string SourceType, string Provider,
    bool IsDemo, DateTimeOffset CapturedAt, DateTimeOffset? DataRecency, string SchemaVersion, string CatalogVersion,
    string FormulaVersion, string? ProfileCatalogVersion, string ContentHash, bool IntegrityVerified);

public sealed record ReportCount(string Key, string Label, int Count);

public sealed record ReportKpis(
    double? Score, double Coverage, double? ApprovalPercent, int TotalControls, int Evaluated, int Passed, int Failed,
    int Mitigated, int NotEvaluated, int Errors, int NotApplicable, int Findings,
    IReadOnlyList<ReportCount> FindingsBySeverity, int Occurrences, int? UniqueAffected, bool UniqueAffectedIsFloor,
    string? UniqueAffectedComposition = null);

public sealed record ReportDistributionRow(string Key, string Label, int Passed, int Failed, int Mitigated, int NotEvaluated, int Errors, int NotApplicable)
{
    public int Total => Passed + Failed + Mitigated + NotEvaluated + Errors + NotApplicable;
}

public sealed record ReportReference(string Framework, string? Version, string Code, string? Url, bool IsFramework);

public sealed record ReportObject(
    string Relation, string RelationLabel, string Kind, string KindLabel, string ExternalId, string? DisplayName,
    string? UserPrincipalName, IReadOnlyList<string> Roles, string? Detail, string? ObservedConfiguration);

public sealed record ReportAction(
    string Title, string Status, string? DueDate, bool WasOverdue, string NextStep, string? Responsible,
    string? ValidationOutcome, string? ValidatedAt);

public sealed record ReportControl(
    string Id, string Title, string Status, string StatusLabel, string Severity, string SeverityLabel,
    int SeverityRank, string Domain, string DomainLabel, string Service, string Provider, IReadOnlyList<string> Frameworks,
    string? Description, string? Rationale, string? ExpectedConfiguration, string? DoesNotProve, string? Criterion,
    string? Recommendation, string? NotEvaluatedReason, string Evidence, string CollectedAt, int AffectedCount,
    int EvidenceCount, bool? DetailPreserved, bool? DetailComplete, string? DetailLimitation, int Weight,
    double? Factor, double? Achieved, double? Possible, IReadOnlyList<ReportReference> References,
    IReadOnlyList<ReportObject> Objects, IReadOnlyList<ReportAction> Actions, IReadOnlyList<string> RequiredCapabilities,
    string? Impact = null, string? Platform = null, string? AffectedComposition = null, string? ProvenReach = null);

/// <summary>[AEGIS-KNIGHT-COVERAGE-01] Uma linha da cobertura de implementação congelada (total, plataforma ou serviço).</summary>
public sealed record ReportCoverageRow(
    string Key, string Label, int Total, int Implemented, int Partial, int Pending, int ManualOnly, int RequiresAccess,
    int ApiLimitation, double FullPercent, double PartialPercent, double AnyAutomatedPercent);

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Cobertura de IMPLEMENTAÇÃO do catálogo de referência, congelada na fotografia. É uma
/// propriedade do produto: não se soma à cobertura da avaliação nem à aprovação, e o relatório diz isso.
/// </summary>
public sealed record ReportReferenceCoverage(
    string CatalogVersion, string ReferenceCommit, IReadOnlyList<string> Frameworks, ReportCoverageRow Total,
    IReadOnlyList<ReportCoverageRow> ByPlatform, IReadOnlyList<ReportCoverageRow> ByService);

public sealed record ReportPriority(string ControlId, string Title, string Severity, string SeverityLabel, int AffectedCount, string Criterion);

public sealed record ReportTopObject(string ExternalId, string Kind, string KindLabel, string Label, int ControlCount, IReadOnlyList<string> ControlIds);

public sealed record ReportLimitation(
    string Capability, string CapabilityLabel, string Outcome, string CauseLabel, string? Detail,
    string? RequiredPermission, IReadOnlyList<string> AffectedControls, string Guidance, string? AttemptedAt);

public sealed record ReportAdvisory(bool FromAi, string ExecutiveSummary, IReadOnlyList<string> PriorityRisks,
    IReadOnlyList<string> RecommendedActions, IReadOnlyList<string> CollectionGaps);

public sealed record KnightReportModel(
    ReportHeader Header, ReportKpis Kpis, IReadOnlyList<ReportControl> Controls,
    IReadOnlyList<ReportDistributionRow> ByDomain, IReadOnlyList<ReportDistributionRow> ByService,
    IReadOnlyList<ReportPriority> Priorities, IReadOnlyList<ReportTopObject> TopObjects,
    IReadOnlyList<ReportLimitation> Limitations, IReadOnlyList<string> LegacyLimitations,
    ReportAdvisory? Advisory, IReadOnlyList<string> Notes, IReadOnlyList<string> FrameworkOptions,
    ReportReferenceCoverage? ReferenceCoverage = null, IReadOnlyList<ReportDistributionRow>? ByPlatform = null);

public static class KnightReportModelBuilder
{
    private static readonly JsonSerializerOptions AdvisoryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static KnightReportModel Build(PostureSnapshot s, bool integrityVerified)
    {
        var isV2 = string.Equals(s.SchemaVersion, PostureSnapshotSchema.KnightReportVersion, StringComparison.Ordinal);
        var sourceType = s.SourceType ?? KnightSourceType.Demo;
        var objectsByIndicator = s.Objects
            .GroupBy(o => o.IndicatorId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var actionsByIndicator = s.ActionItems
            .GroupBy(a => a.IndicatorId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var controls = s.Indicators
            .OrderBy(i => StatusOrder(i.Status))
            .ThenByDescending(i => KnightScoreFormula.WeightFor(i.Severity))
            .ThenByDescending(i => i.AffectedObjectCount)
            .ThenBy(i => i.IndicatorId, StringComparer.Ordinal)
            .Select(i => BuildControl(i, isV2, sourceType,
                objectsByIndicator.TryGetValue(i.IndicatorId, out var objs) ? objs : new List<PostureSnapshotObject>(),
                actionsByIndicator.TryGetValue(i.IndicatorId, out var acts) ? acts : new List<PostureSnapshotActionItem>()))
            .ToList();

        var failedOrAttention = controls.Where(c => c.Status is "Exposed" or "Mitigated").ToList();

        // Ocorrências e objetos únicos: só afetados de controles reprovados/mitigados, na MESMA regra do CSV.
        var occurrences = failedOrAttention.SelectMany(c => c.Objects.Where(o => o.Relation == "Affected").Select(o => (c.Id, o))).ToList();
        var uniqueObjects = occurrences.Select(x => (x.o.Kind, Id: x.o.ExternalId.ToLowerInvariant())).Distinct().ToList();
        var unique = uniqueObjects.Count;
        var floor = !isV2 || failedOrAttention.Any(c => c.AffectedCount > 0 && !(c.DetailPreserved == true && c.DetailComplete == true));

        var severityOrder = new[] { SeverityLevel.Critical, SeverityLevel.High, SeverityLevel.Medium, SeverityLevel.Low, SeverityLevel.Informational };
        var bySeverity = severityOrder
            .Select(sev => new ReportCount(sev.ToString(), SeverityLabel(sev), failedOrAttention.Count(c => c.Severity == sev.ToString())))
            .ToList();

        var passed = s.CompliantCount;
        var failed = s.NonCompliantCount;
        var evaluated = passed + failed + s.MitigatedCount;
        var kpis = new ReportKpis(
            s.Score, s.Coverage,
            evaluated > 0 ? Math.Round(100.0 * passed / evaluated, 1, MidpointRounding.AwayFromZero) : null,
            controls.Count, evaluated, passed, failed, s.MitigatedCount, s.NotEvaluatedCount, s.ErrorCount,
            s.NotApplicableCount, failedOrAttention.Count, bySeverity, occurrences.Count,
            isV2 ? unique : null, floor,
            isV2 && unique > 0
                ? KnightObjectNouns.Composition(uniqueObjects.Select(u => Enum.TryParse<KnightAffectedObjectKind>(u.Kind, out var k) ? k : KnightAffectedObjectKind.Unknown))
                : null);

        var byDomain = Distribution(controls, c => (c.Domain, c.DomainLabel));
        var byService = Distribution(controls, c => (c.Service, c.Service));
        var byPlatform = Distribution(controls, c => (c.Platform ?? c.Provider, c.Platform ?? c.Provider));

        var priorities = controls
            .Where(c => c.Status == "Exposed")
            .OrderByDescending(c => c.Weight).ThenByDescending(c => c.AffectedCount).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(5)
            .Select(c => new ReportPriority(c.Id, c.Title, c.Severity, c.SeverityLabel, c.AffectedCount,
                $"Severidade {c.SeverityLabel.ToLowerInvariant()} (peso {c.Weight})"
                + (c.AffectedCount > 0 ? $" · {c.AffectedComposition}" : "")))
            .ToList();

        var topObjects = occurrences
            .GroupBy(x => (x.o.Kind, Id: x.o.ExternalId.ToLowerInvariant()))
            .Select(g =>
            {
                var o = g.First().o;
                var ids = g.Select(x => x.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                return new ReportTopObject(o.ExternalId, o.Kind, o.KindLabel, o.DisplayName ?? o.UserPrincipalName ?? o.ExternalId, ids.Count, ids);
            })
            .OrderByDescending(t => t.ControlCount).ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10).ToList();

        var limitations = BuildLimitations(s, controls);

        ReportAdvisory? advisory = null;
        if (!string.IsNullOrWhiteSpace(s.AdvisoryJson))
        {
            try
            {
                var a = JsonSerializer.Deserialize<KnightAdvisory>(s.AdvisoryJson!, AdvisoryJson);
                if (a is not null)
                    advisory = new ReportAdvisory(
                        s.AdvisoryFromAi == true, a.ExecutiveSummary,
                        a.PriorityRisks.Select(r => $"{r.Title} — {r.Rationale} [{string.Join(", ", r.IndicatorIds)}]").ToList(),
                        a.RecommendedActions.OrderBy(r => r.Order).Select(r => $"{r.Action} [{string.Join(", ", r.IndicatorIds)}]").ToList(),
                        a.CollectionGaps.ToList());
            }
            catch (JsonException) { advisory = null; }
        }

        var notes = new List<string>();
        if (!isV2)
            notes.Add("Fotografia publicada no formato anterior (v1): recomendações, objetos afetados, evidências de configuração e a narrativa "
                + "consultiva não foram congelados nela e por isso não aparecem aqui. Domínio e serviço foram derivados da categoria e da fonte congeladas.");
        if (sourceType == KnightSourceType.Demo)
            notes.Add("Avaliação de DEMONSTRAÇÃO com dados 100% sintéticos — não representa nenhum ambiente real.");
        if (isV2 && s.ProfileCatalogVersion is { } pv && !string.Equals(pv, s.CatalogVersion, StringComparison.Ordinal))
            notes.Add($"Os textos descritivos (problema, impacto, configuração esperada) são do catálogo {pv}; o veredito foi produzido pelo catálogo {s.CatalogVersion}. O critério da regra só é exibido quando os dois coincidem.");
        if (controls.Any(c => c.Impact is not null))
            notes.Add("Risco e impacto descrevem o que cada condição encontrada permite, no limite do acesso que ela concede. Não indicam que um incidente ocorreu nem que houve acesso indevido.");
        var coverage = BuildCoverage(s.ReferenceCoverageJson);
        if (coverage is not null)
            notes.Add("A cobertura do catálogo de referência mede o que o AEGIS consegue avaliar (propriedade do produto). Ela é diferente da cobertura desta avaliação (o que a coleta conseguiu avaliar neste ambiente) e da aprovação (o que foi avaliado e está conforme).");

        var frameworks = controls.SelectMany(c => c.Frameworks).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var header = new ReportHeader(
            s.Id, s.SourceRunId, s.ClientName, s.SourceLabel ?? KnightControlProfiles.ProviderOf(sourceType), sourceType.ToString(),
            KnightControlProfiles.ProviderOf(sourceType), sourceType == KnightSourceType.Demo, s.CapturedAt, s.DataRecency,
            s.SchemaVersion, s.CatalogVersion, s.FormulaVersion, s.ProfileCatalogVersion, s.ContentHash, integrityVerified);

        return new KnightReportModel(header, kpis, controls, byDomain, byService, priorities, topObjects, limitations,
            isV2 ? Array.Empty<string>() : s.CollectionLimitations.ToList(), advisory, notes, frameworks, coverage, byPlatform);
    }

    private static ReportReferenceCoverage? BuildCoverage(string? json)
    {
        if (KnightReferenceCoverageSnapshot.Deserialize(json) is not { } c) return null;
        static ReportCoverageRow Row(KnightReferenceCoverageGroup g) => new(
            g.Key, g.Label, g.Total, g.Implemented, g.Partial, g.Pending, g.ManualOnly, g.RequiresAccess, g.ApiLimitation,
            g.FullPercent, g.PartialPercent, g.AnyAutomatedPercent);
        return new ReportReferenceCoverage(
            c.CatalogVersion, c.ReferenceCommit,
            c.Frameworks.Select(f => $"{f.Name} {f.Version} ({f.Controls} controles)").ToList(),
            Row(c.Total), c.ByPlatform.Select(Row).ToList(), c.ByService.Select(Row).ToList());
    }

    private static ReportControl BuildControl(
        PostureSnapshotIndicator i, bool isV2, KnightSourceType source,
        IReadOnlyList<PostureSnapshotObject> objects, IReadOnlyList<PostureSnapshotActionItem> actions)
    {
        var domain = isV2 && Enum.TryParse<KnightSecurityDomain>(i.Domain, out var d)
            ? d
            : KnightControlProfiles.DomainOf(i.IndicatorId, i.Category);
        var service = isV2 && !string.IsNullOrWhiteSpace(i.Service) ? i.Service! : KnightControlProfiles.ServiceOf(i.IndicatorId, i.SourceType);
        var provider = isV2 && !string.IsNullOrWhiteSpace(i.Provider) ? i.Provider! : KnightControlProfiles.ProviderOf(i.SourceType);

        var references = isV2
            ? i.References.Select(r => new ReportReference(r.Framework, r.Version, r.Code, SafeUrl(r.Url), IsFramework(r.Framework))).ToList()
            : KnightControlProfiles.ReferencesOf(i.IndicatorId, i.NistCodes, i.MitreTechniques)
                .Where(r => IsFramework(r.Framework))
                .Select(r => new ReportReference(r.Framework, r.Version, r.Code, null, true)).ToList();

        var weight = KnightScoreFormula.WeightFor(i.Severity);
        var factor = KnightScoreFormula.FactorFor(i.Status);
        var affectedObjects = objects.Where(o => o.Relation == KnightObjectRelation.Affected).ToList();
        var composition = i.AffectedObjectCount > 0
            ? KnightObjectNouns.Composition(affectedObjects.Select(o => o.Kind), i.AffectedObjectCount)
            : null;
        string? reach = null;
        if (i.Status is KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated)
        {
            if (composition is not null)
                reach = $"Alcance comprovado nesta coleta: {composition}.";
            else if (objects.Where(o => o.Relation == KnightObjectRelation.Evidence && o.Kind == KnightAffectedObjectKind.TenantSetting)
                         .Select(o => o.DisplayName ?? o.ExternalId).ToList() is { Count: > 0 } settings)
                reach = "Alcance comprovado nesta coleta: configuração do locatário (" + string.Join("; ", settings) + "), que vale para todo o diretório exceto onde houver exceção configurada.";
        }

        return new ReportControl(
            i.IndicatorId, i.Title, i.Status.ToString(), StatusLabel(i.Status), i.Severity.ToString(), SeverityLabel(i.Severity),
            weight, domain.ToString(), KnightControlProfiles.DomainLabel(domain), service, provider,
            references.Where(r => r.IsFramework).Select(r => r.Version is null ? r.Framework : $"{r.Framework} {r.Version}")
                .Distinct(StringComparer.Ordinal).ToList(),
            isV2 ? i.Description : null, isV2 ? i.Rationale : null, isV2 ? i.ExpectedConfiguration : null,
            isV2 ? i.DoesNotProve : null, isV2 ? i.Criterion : null,
            isV2 ? i.Recommendation : null, isV2 ? i.NotEvaluatedReason : null,
            i.Evidence, Iso(i.CollectedAt), i.AffectedObjectCount,
            objects.Count(o => o.Relation == KnightObjectRelation.Evidence),
            isV2 ? i.HasAffectedDetail : null, isV2 ? i.AffectedDetailComplete : null, isV2 ? i.AffectedDetailLimitation : null,
            weight, factor, factor is null ? null : weight * factor.Value, factor is null ? null : weight,
            references,
            objects
                .OrderBy(o => o.Relation).ThenBy(o => o.DisplayName ?? o.UserPrincipalName ?? o.ExternalId, StringComparer.OrdinalIgnoreCase)
                .Select(o => new ReportObject(
                    o.Relation.ToString(),
                    o.Relation == KnightObjectRelation.Affected ? "Afetado" : "Evidência de configuração",
                    o.Kind.ToString(), KindLabel(o.Kind), o.ExternalId, o.DisplayName, o.UserPrincipalName,
                    o.Roles.ToList(), o.Detail, o.ObservedConfiguration))
                .ToList(),
            actions.Select(a => new ReportAction(
                a.Title, a.Status.ToString(), a.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), a.WasOverdue,
                a.NextStep, a.ResponsibleArea ?? a.ResponsiblePerson, a.ApplicableValidationOutcome?.ToString(),
                a.ApplicableValidatedAt is { } v ? Iso(v) : null)).ToList(),
            i.RequiredCapabilities.ToList(),
            isV2 ? i.Impact : null,
            isV2 ? i.Platform : null,
            composition,
            reach);
    }

    private static IReadOnlyList<ReportDistributionRow> Distribution(
        IReadOnlyList<ReportControl> controls, Func<ReportControl, (string Key, string Label)> keyOf) =>
        controls
            .GroupBy(keyOf)
            .Select(g => new ReportDistributionRow(g.Key.Key, g.Key.Label,
                g.Count(c => c.Status == "Passed"), g.Count(c => c.Status == "Exposed"), g.Count(c => c.Status == "Mitigated"),
                g.Count(c => c.Status == "NotEvaluated"), g.Count(c => c.Status == "Error"), g.Count(c => c.Status == "NotApplicable")))
            .OrderByDescending(r => r.Failed).ThenByDescending(r => r.Total).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<ReportLimitation> BuildLimitations(PostureSnapshot s, IReadOnlyList<ReportControl> controls)
    {
        if (string.IsNullOrWhiteSpace(s.CapabilitiesJson)) return Array.Empty<ReportLimitation>();
        var caps = KnightCapabilitiesJson.Deserialize(s.CapabilitiesJson);
        return caps
            .Where(c => c.Outcome != KnightCapabilityOutcome.Collected)
            .OrderBy(c => c.Capability)
            .Select(c =>
            {
                var name = c.Capability.ToString();
                var affected = controls
                    .Where(x => x.RequiredCapabilities.Contains(name, StringComparer.Ordinal)
                        && x.Status is "NotEvaluated" or "Error")
                    .Select(x => x.Id).ToList();
                return new ReportLimitation(
                    name, KnightCapabilityLabels.Label(c.Capability), c.Outcome.ToString(), CauseLabel(c.Outcome), c.Detail,
                    c.Outcome == KnightCapabilityOutcome.InsufficientPermission ? KnightCapabilityLabels.RequiredPermission(c.Capability, s.SourceType) : null,
                    affected, Guidance(c.Outcome), s.DataRecency is { } t ? Iso(t) : null);
            })
            .ToList();
    }

    // ---- Rótulos ------------------------------------------------------------------------------------------

    public static int StatusOrder(KnightIndicatorStatus s) => s switch
    {
        KnightIndicatorStatus.Exposed => 0,
        KnightIndicatorStatus.Mitigated => 1,
        KnightIndicatorStatus.Error => 2,
        KnightIndicatorStatus.NotEvaluated => 3,
        KnightIndicatorStatus.Passed => 4,
        _ => 5,
    };

    public static string StatusLabel(KnightIndicatorStatus s) => s switch
    {
        KnightIndicatorStatus.Passed => "Aprovado",
        KnightIndicatorStatus.Exposed => "Reprovado",
        KnightIndicatorStatus.Mitigated => "Mitigado (atenção)",
        KnightIndicatorStatus.NotEvaluated => "Não avaliado",
        KnightIndicatorStatus.Error => "Erro na avaliação",
        KnightIndicatorStatus.NotApplicable => "Não aplicável",
        _ => s.ToString(),
    };

    public static string SeverityLabel(SeverityLevel s) => s switch
    {
        SeverityLevel.Critical => "Crítico",
        SeverityLevel.High => "Alto",
        SeverityLevel.Medium => "Médio",
        SeverityLevel.Low => "Baixo",
        SeverityLevel.Informational => "Informativo",
        _ => s.ToString(),
    };

    /// <summary>Rótulo do tipo — a MESMA definição usada pela tela (via API), HTML, CSV e PDF.</summary>
    public static string KindLabel(KnightAffectedObjectKind k) => KnightObjectNouns.Label(k);

    private static string CauseLabel(KnightCapabilityOutcome o) => o switch
    {
        KnightCapabilityOutcome.InsufficientPermission => "Permissão ausente",
        KnightCapabilityOutcome.LimitedByLicense => "Licença insuficiente",
        KnightCapabilityOutcome.Throttled => "Limite de taxa do provedor",
        KnightCapabilityOutcome.AuthenticationFailure => "Falha de autenticação",
        KnightCapabilityOutcome.Unavailable => "Serviço indisponível",
        KnightCapabilityOutcome.NotAttempted => "Não executada",
        KnightCapabilityOutcome.Error => "Erro de coleta",
        _ => o.ToString(),
    };

    private static string Guidance(KnightCapabilityOutcome o) => o switch
    {
        KnightCapabilityOutcome.InsufficientPermission =>
            "Conceder a permissão indicada ao aplicativo do conector (consentimento de administrador) e sincronizar novamente em Configurações → Integrações.",
        KnightCapabilityOutcome.LimitedByLicense =>
            "A capacidade depende de licença do provedor. Sem ela, os controles afetados continuam não avaliados — nunca aprovados.",
        KnightCapabilityOutcome.Throttled => "O provedor limitou a taxa de chamadas. Sincronizar novamente mais tarde.",
        KnightCapabilityOutcome.AuthenticationFailure =>
            "Verificar a credencial do conector (segredo expirado ou revogado) e reconectar em Configurações → Integrações.",
        KnightCapabilityOutcome.Unavailable => "O serviço do provedor não respondeu. Sincronizar novamente; persistindo, verificar a conectividade.",
        KnightCapabilityOutcome.NotAttempted => "A capacidade não foi executada nesta coleta.",
        _ => "Erro inesperado nesta capacidade. Sincronizar novamente; persistindo, acionar o suporte com o horário da tentativa.",
    };

    private static bool IsFramework(string framework) =>
        framework == KnightControlProfiles.NistFramework || framework == KnightControlProfiles.MitreFramework
        || KnightControlProfiles.IsBenchmark(framework);

    /// <summary>Só HTTPS absoluto sobrevive — qualquer outra coisa vira texto sem link.</summary>
    public static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;

    private static string Iso(DateTimeOffset v) =>
        v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>[AEGIS-KNIGHT-MULTICLOUD-01] Rótulos e requisitos das capacidades de coleta, pelas chamadas IMPLEMENTADAS.</summary>
public static class KnightCapabilityLabels
{
    public static string Label(KnightCapability c) => c switch
    {
        KnightCapability.PrivilegedRoleInventory => "Inventário de papéis privilegiados",
        KnightCapability.MfaRegistration => "Registro de métodos de MFA",
        KnightCapability.GuestAccounts => "Contas de convidado",
        KnightCapability.ConditionalAccessPolicies => "Políticas de acesso condicional",
        KnightCapability.ApplicationInventory => "Credenciais de aplicações",
        KnightCapability.ApplicationPermissions => "Permissões de aplicativo concedidas",
        KnightCapability.ApplicationConsents => "Consentimentos delegados (todos os usuários)",
        KnightCapability.ServiceAccountExemptions => "Isenções de contas de serviço",
        KnightCapability.SecurityBaseline => "Security defaults",
        KnightCapability.BreakGlassDesignation => "Designação de contas de emergência",
        KnightCapability.DirectoryUsers => "Diretório de usuários",
        KnightCapability.DirectoryGroups => "Grupos e membros externos",
        KnightCapability.DriveSharingAudit => "Auditoria de compartilhamento no Drive",
        KnightCapability.OAuthTokenAudit => "Auditoria de autorizações OAuth",
        KnightCapability.IdentityRiskyUsers => "Usuários sinalizados como de risco",
        KnightCapability.IdentityRiskDetections => "Detecções de risco de identidade",
        KnightCapability.AuthorizationPolicy => "Política de autorização do diretório",
        KnightCapability.AdminConsentPolicy => "Fluxo de consentimento do administrador",
        KnightCapability.AppManagementPolicy => "Política de gerenciamento de aplicações",
        KnightCapability.AuthenticationMethodsPolicy => "Política de métodos de autenticação",
        KnightCapability.DirectorySettings => "Configurações de diretório (senhas e grupos)",
        KnightCapability.Domains => "Domínios",
        KnightCapability.DirectorySynchronization => "Sincronização híbrida",
        KnightCapability.DeviceRegistrationPolicy => "Política de registro de dispositivos",
        KnightCapability.GroupVisibility => "Visibilidade de grupos do Microsoft 365",
        KnightCapability.PrivilegedAccountDetails => "Origem e licenças das contas privilegiadas",
        KnightCapability.PrivilegedIdentityManagement => "Privileged Identity Management (PIM)",
        KnightCapability.AccessReviews => "Revisões de acesso",
        KnightCapability.NamedLocations => "Locais nomeados",
        KnightCapability.ServicePrincipalSettings => "Aplicações de serviço do Microsoft 365",
        _ => c.ToString(),
    };

    /// <summary>
    /// Permissão que a chamada IMPLEMENTADA exige — exibida só quando a coleta falhou por permissão. Não é uma
    /// lista genérica de permissões do provedor: é o requisito do que o AEGIS de fato chama.
    /// </summary>
    public static string? RequiredPermission(KnightCapability c, KnightSourceType? source) => source switch
    {
        KnightSourceType.MicrosoftEntraId => c switch
        {
            KnightCapability.PrivilegedRoleInventory => "Directory.Read.All (aplicativo)",
            KnightCapability.MfaRegistration => "AuditLog.Read.All (aplicativo)",
            KnightCapability.GuestAccounts => "User.Read.All (aplicativo)",
            KnightCapability.ConditionalAccessPolicies => "Policy.Read.All (aplicativo)",
            KnightCapability.SecurityBaseline => "Policy.Read.All (aplicativo)",
            KnightCapability.ApplicationInventory => "Application.Read.All (aplicativo)",
            KnightCapability.ApplicationPermissions => "Application.Read.All (aplicativo)",
            KnightCapability.ApplicationConsents => "Directory.Read.All (aplicativo)",
            KnightCapability.IdentityRiskyUsers => "IdentityRiskyUser.Read.All (aplicativo)",
            KnightCapability.IdentityRiskDetections => "IdentityRiskEvent.Read.All (aplicativo)",
            KnightCapability.AuthorizationPolicy or KnightCapability.AdminConsentPolicy or KnightCapability.AppManagementPolicy
                or KnightCapability.AuthenticationMethodsPolicy or KnightCapability.NamedLocations => "Policy.Read.All (aplicativo)",
            KnightCapability.DirectorySettings or KnightCapability.Domains or KnightCapability.GroupVisibility
                or KnightCapability.PrivilegedAccountDetails => "Directory.Read.All (aplicativo)",
            KnightCapability.DirectorySynchronization => "Directory.Read.All e OnPremDirectorySynchronization.Read.All (aplicativo)",
            KnightCapability.DeviceRegistrationPolicy => "Policy.Read.DeviceConfiguration (aplicativo)",
            KnightCapability.PrivilegedIdentityManagement => "RoleManagement.Read.Directory e RoleManagementPolicy.Read.Directory (aplicativo)",
            KnightCapability.AccessReviews => "AccessReview.Read.All (aplicativo)",
            KnightCapability.ServicePrincipalSettings => "Application.Read.All (aplicativo)",
            _ => null,
        },
        KnightSourceType.GoogleWorkspace => c switch
        {
            KnightCapability.DirectoryUsers => "admin.directory.user.readonly (delegação no domínio)",
            KnightCapability.DirectoryGroups => "admin.directory.group.readonly e admin.directory.group.member.readonly",
            KnightCapability.DriveSharingAudit or KnightCapability.OAuthTokenAudit => "admin.reports.audit.readonly",
            _ => null,
        },
        _ => null,
    };
}
