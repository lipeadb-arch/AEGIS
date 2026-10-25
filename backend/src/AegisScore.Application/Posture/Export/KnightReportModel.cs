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
    string FormulaVersion, string? ProfileCatalogVersion, string ContentHash, bool IntegrityVerified,
    string? CollectionState = null, string? CollectionStateLabel = null, string? CollectionSummary = null);

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
    string? Impact = null, string? Platform = null, string? AffectedComposition = null, string? ProvenReach = null,
    // [AEGIS-KNIGHT-COVERAGE-04] As mesmas capacidades, em português, para quem lê. Os códigos acima continuam sendo a
    // chave que liga uma limitação aos controles que ela prejudica.
    IReadOnlyList<string>? RequiredCapabilityLabels = null,
    // [AEGIS-KNIGHT-PRESENTATION-01] Siglas do glossário que aparecem nos textos congelados deste controle.
    IReadOnlyList<string>? GlossaryTerms = null,
    // [AEGIS-KNIGHT-CLOSURE-01] Versões preview (beta) das APIs que o controle consome, pelas capacidades congeladas.
    IReadOnlyList<string>? PreviewApis = null);

/// <summary>[AEGIS-KNIGHT-COVERAGE-01] Uma linha da cobertura de implementação congelada (total, plataforma ou serviço).</summary>
public sealed record ReportCoverageRow(
    string Key, string Label, int Total, int Implemented, int Partial, int Pending, int ManualOnly, int RequiresAccess,
    int ApiLimitation, double FullPercent, double PartialPercent, double AnyAutomatedPercent, int PreviewOnly = 0, int PreviewBacked = 0);

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Cobertura de IMPLEMENTAÇÃO do catálogo de referência, congelada na fotografia. É uma
/// propriedade do produto: não se soma à cobertura da avaliação nem à aprovação, e o relatório diz isso.
/// </summary>
public sealed record ReportReferenceCoverage(
    string CatalogVersion, string ReferenceCommit, IReadOnlyList<string> Frameworks, ReportCoverageRow Total,
    IReadOnlyList<ReportCoverageRow> ByPlatform, IReadOnlyList<ReportCoverageRow> ByService,
    // [AEGIS-KNIGHT-CLOSURE-01] Resultados manuais vigentes na publicação — à parte da cobertura automatizada.
    IReadOnlyList<KnightManualResultEntry>? ManualResults = null);

/// <param name="Risk">[AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Risco, impacto, serviço, afetados e orientação CONGELADOS do controle —
/// os mesmos do detalhe; nulos quando a fotografia não os tem (nada é inventado para preencher).</param>
public sealed record ReportPriority(string ControlId, string Title, string Severity, string SeverityLabel, int AffectedCount, string Criterion,
    string? Service = null, string? Risk = null, string? Impact = null, string? Guidance = null, string? AffectedComposition = null);

/// <summary>
/// [AEGIS-KNIGHT-PRESENTATION-01] Um controle citado fora do próprio detalhe: o código e o título CONGELADO na fotografia.
/// <see cref="Title"/> nulo = a fotografia não tem esse controle; o código fica, e o relatório diz que o título não está disponível.
/// </summary>
public sealed record ReportControlRef(string Id, string? Title);

public sealed record ReportTopObject(string ExternalId, string Kind, string KindLabel, string Label, int ControlCount, IReadOnlyList<string> ControlIds,
    IReadOnlyList<ReportControlRef>? Controls = null);

/// <param name="FailureKind">[AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Tipo sanitizado da falha (diagnóstico congelado) e o detalhe técnico seguro
/// em linhas; nulos/vazios em coletas anteriores ao diagnóstico.</param>
public sealed record ReportLimitation(
    string Capability, string CapabilityLabel, string Outcome, string CauseLabel, string? Detail,
    string? RequiredPermission, IReadOnlyList<string> AffectedControls, string Guidance, string? AttemptedAt,
    string? FailureKind = null, IReadOnlyList<string>? Technical = null);

/// <summary>
/// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Limitações EQUIVALENTES agrupadas: mesmo desfecho, mesmo tipo de falha, mesma orientação e
/// mesmos serviços afetados. Falhas diferentes com textos parecidos ficam separadas; cada capacidade continua listada.
/// </summary>
public sealed record ReportLimitationGroup(
    string CauseLabel, string? KindLabel, string Guidance, IReadOnlyList<string> Services, IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> AffectedControls);

/// <summary>
/// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Fontes INCLUÍDAS × fontes com controles EFETIVAMENTE avaliados, pelas contagens congeladas
/// (composição, no consolidado; os indicadores, na fonte única). Incluir uma fonte não é avaliá-la.
/// </summary>
public sealed record ReportSourceInclusion(int Included, int WithEvaluated, IReadOnlyList<string> WithoutEvaluated, string Text);

public sealed record ReportAdvisory(bool FromAi, string ExecutiveSummary, IReadOnlyList<string> PriorityRisks,
    IReadOnlyList<string> RecommendedActions, IReadOnlyList<string> CollectionGaps);

public sealed record KnightReportModel(
    ReportHeader Header, ReportKpis Kpis, IReadOnlyList<ReportControl> Controls,
    IReadOnlyList<ReportDistributionRow> ByDomain, IReadOnlyList<ReportDistributionRow> ByService,
    IReadOnlyList<ReportPriority> Priorities, IReadOnlyList<ReportTopObject> TopObjects,
    IReadOnlyList<ReportLimitation> Limitations, IReadOnlyList<string> LegacyLimitations,
    ReportAdvisory? Advisory, IReadOnlyList<string> Notes, IReadOnlyList<string> FrameworkOptions,
    ReportReferenceCoverage? ReferenceCoverage = null, IReadOnlyList<ReportDistributionRow>? ByPlatform = null,
    /// <summary>
    /// [AEGIS-KNIGHT-CONSOLIDATED-01] Composição das fontes candidatas (as elegíveis do catálogo) — nula
    /// fora de fotografias consolidadas. Cada fonte traz a NOTA E COBERTURA PRÓPRIAS, nunca somadas às demais.
    /// </summary>
    IReadOnlyList<KnightConsolidatedSourceEntry>? Composition = null,
    /// <summary>[AEGIS-KNIGHT-PRESENTATION-01] Termos do glossário único que aparecem nos textos desta fotografia.</summary>
    IReadOnlyList<KnightGlossaryTerm>? Glossary = null,
    string? IdentifierExplanation = null,
    ReportSourceInclusion? Sources = null,
    IReadOnlyList<ReportLimitationGroup>? LimitationGroups = null,
    /// <summary>[AEGIS-KNIGHT-RESULTS-PRESENTATION-01] O que dizer quando não há limitação a listar — fiel ao registro da fotografia.</summary>
    string? NoLimitationText = null);

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
        // [AEGIS-KNIGHT-PRESENTATION-01] As siglas saem dos textos CONGELADOS do controle — nunca do catálogo de hoje.
        controls = controls.Select(c => c with
        {
            GlossaryTerms = KnightGlossary.UsedIn(GlossaryTexts(c)).Select(t => t.Term).ToList(),
        }).ToList();
        var titles = controls.ToDictionary(c => c.Id, c => c.Title, StringComparer.Ordinal);

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
                + (c.AffectedCount > 0 ? $" · {c.AffectedComposition}" : ""),
                c.Service, Blank(c.Rationale), Blank(c.Impact), Blank(c.Recommendation), c.AffectedCount > 0 ? c.AffectedComposition : null))
            .ToList();

        var topObjects = occurrences
            .GroupBy(x => (x.o.Kind, Id: x.o.ExternalId.ToLowerInvariant()))
            .Select(g =>
            {
                var o = g.First().o;
                var ids = g.Select(x => x.Id).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                return new ReportTopObject(o.ExternalId, o.Kind, o.KindLabel, o.DisplayName ?? o.UserPrincipalName ?? o.ExternalId, ids.Count, ids,
                    ids.Select(id => RefOf(titles, id)).ToList());
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
        var composition = ReadableComposition(KnightConsolidatedCompositionJson.Deserialize(s.CompositionJson));
        if (sourceType == KnightSourceType.Consolidated)
            notes.Add("Este relatório COMPÕE avaliações concluídas de mais de uma fonte do mesmo tenant, cada uma "
                + "com a própria versão de catálogo e data de coleta (ver \"Composição das fontes\"). A nota KNIGHT "
                + "acima aplica a mesma fórmula sobre a união dos controles das fontes INCLUÍDAS — não é a média das "
                + "notas de cada fonte. Uma fonte disponível e não incluída, ou sem avaliação concluída, aparece na "
                + "composição sem virar aprovação.");
        if (isV2 && s.ProfileCatalogVersion is { } pv && !string.Equals(pv, s.CatalogVersion, StringComparison.Ordinal))
            notes.Add($"Os textos descritivos (problema, impacto, configuração esperada) são do catálogo {pv}; o veredito foi produzido pelo catálogo {s.CatalogVersion}. O critério da regra só é exibido quando os dois coincidem.");
        if (controls.Any(c => c.Impact is not null))
            notes.Add("Risco e impacto descrevem o que cada condição encontrada permite, no limite do acesso que ela concede. Não indicam que um incidente ocorreu nem que houve acesso indevido.");
        var coverage = BuildCoverage(s.ReferenceCoverageJson);
        if (coverage is not null)
            notes.Add("A cobertura do catálogo de referência mede o que o AEGIS consegue avaliar (propriedade do produto). Ela é diferente da cobertura desta avaliação (o que a coleta conseguiu avaliar neste ambiente) e da aprovação (o que foi avaliado e está conforme).");
        if (coverage?.ManualResults is { Count: > 0 } manual)
            notes.Add($"Há {manual.Count} resultado(s) de verificação MANUAL registrados pela organização para controles de referência sem avaliação automatizada"
                + (manual.Any(m => m.Complement)
                    ? $" ou, como complemento ({manual.Count(m => m.Complement)}), para referências com leitura automatizada parcial: nelas o indicador automatizado continua com o próprio resultado, e o manual cobre só o que a leitura somente leitura não alcança"
                    : "")
                + ". Eles são atestações (resultado, justificativa, responsável e evidência informados por pessoas), aparecem à parte e não entram na nota, na cobertura desta avaliação nem na aprovação.");

        var frameworks = controls.SelectMany(c => c.Frameworks).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var header = new ReportHeader(
            s.Id, s.SourceRunId, s.ClientName, s.SourceLabel ?? KnightControlProfiles.ProviderOf(sourceType), sourceType.ToString(),
            KnightControlProfiles.ProviderOf(sourceType), sourceType == KnightSourceType.Demo, s.CapturedAt, s.DataRecency,
            s.SchemaVersion, s.CatalogVersion, s.FormulaVersion, s.ProfileCatalogVersion, s.ContentHash, integrityVerified,
            // [AEGIS-KNIGHT-COVERAGE-04] Consolidado: completude da coleta vem da composição congelada (por fonte).
            composition is { Count: > 0 } ? KnightConsolidatedCollection.StateOf(composition).ToString() : null,
            composition is { Count: > 0 } ? KnightConsolidatedCollection.Label(KnightConsolidatedCollection.StateOf(composition).ToString()) : null,
            composition is { Count: > 0 } ? KnightConsolidatedCollection.Describe(composition) : null);

        return new KnightReportModel(header, kpis, controls, byDomain, byService, priorities, topObjects, limitations,
            isV2 ? Array.Empty<string>() : s.CollectionLimitations.ToList(), advisory, notes, frameworks, coverage, byPlatform,
            composition,
            KnightGlossary.UsedIn(controls.SelectMany(GlossaryTexts)),
            KnightGlossary.IdentifierExplanation,
            Inclusion(composition, header.SourceLabel, evaluated),
            GroupLimitations(limitations, controls),
            NoLimitationText(KnightCapabilitiesJson.Deserialize(s.CapabilitiesJson), kpis));
    }

    /// <summary>
    /// [AEGIS-KNIGHT-RESULTS-PRESENTATION-01] Ausência de registro não é coleta comprovada: só se afirma que todas as capacidades foram
    /// lidas quando a fotografia as registra e TODAS saíram coletadas. Sem esse registro (fotografia antiga, consolidada ou sem
    /// capacidades), diz-se apenas que nada foi registrado — e, havendo controle sem avaliação, que isso não comprova coleta completa.
    /// </summary>
    public static string NoLimitationText(IReadOnlyList<KnightCapabilityStatus> capabilities, ReportKpis k)
    {
        if (capabilities.Count > 0 && capabilities.All(c => c.Outcome == KnightCapabilityOutcome.Collected))
            return capabilities.Count == 1
                ? "A única capacidade de coleta registrada nesta fotografia foi lida."
                : $"Todas as {capabilities.Count} capacidades de coleta registradas nesta fotografia foram lidas.";
        var unevaluated = k.NotEvaluated + k.Errors;
        return "Não há limitações de coleta registradas nesta fotografia." + (unevaluated == 0 ? ""
            : unevaluated == 1 ? " Isso não comprova coleta completa: 1 controle aplicável ficou sem avaliação."
            : $" Isso não comprova coleta completa: {unevaluated} controles aplicáveis ficaram sem avaliação.");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Consolidado: as fontes incluídas na composição congelada e as que tiveram algum veredito; fonte única: ela mesma.</summary>
    public static ReportSourceInclusion Inclusion(IReadOnlyList<KnightConsolidatedSourceEntry>? composition, string sourceLabel, int evaluated)
    {
        var included = composition is { Count: > 0 }
            ? composition.Where(e => e.Included)
                .Select(e => (e.Label, Evaluated: (e.PassedCount ?? 0) + (e.ExposedCount ?? 0) + (e.MitigatedCount ?? 0))).ToList()
            : new List<(string Label, int Evaluated)> { (sourceLabel, evaluated) };
        var with = included.Count(x => x.Evaluated > 0);
        var head = included.Count == 1 ? "1 fonte incluída" : $"{included.Count} fontes incluídas";
        var tail = with == 0 ? "nenhuma com controles avaliados" : $"{with} com controles avaliados";
        return new ReportSourceInclusion(included.Count, with, included.Where(x => x.Evaluated == 0).Select(x => x.Label).ToList(), $"{head} · {tail}");
    }

    /// <summary>Agrupa por desfecho, tipo de falha, orientação e serviços dos controles prejudicados; mais controles afetados primeiro.</summary>
    public static IReadOnlyList<ReportLimitationGroup> GroupLimitations(IReadOnlyList<ReportLimitation> limitations, IReadOnlyList<ReportControl> controls)
    {
        var serviceOf = controls.ToDictionary(c => c.Id, c => c.Service, StringComparer.Ordinal);
        return limitations
            .Select(l => (l, Services: l.AffectedControls.Select(id => serviceOf.TryGetValue(id, out var sv) ? sv : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()))
            .GroupBy(x => string.Join("|", x.l.Outcome, x.l.FailureKind ?? "", x.l.Guidance, string.Join("+", x.Services)), StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                return new ReportLimitationGroup(
                    first.l.CauseLabel, FailureKindLabel(first.l.FailureKind), first.l.Guidance, first.Services,
                    g.Select(x => x.l.CapabilityLabel).ToList(),
                    g.SelectMany(x => x.l.AffectedControls).Distinct(StringComparer.Ordinal).ToList());
            })
            .OrderByDescending(g => g.AffectedControls.Count).ThenByDescending(g => g.Capabilities.Count)
            .ThenBy(g => g.CauseLabel, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Tipo sanitizado da falha em palavras — o mesmo vocabulário da tela (nunca causa atribuída sem prova).</summary>
    public static string? FailureKindLabel(string? kind) => kind switch
    {
        null or "" => null,
        nameof(KnightFailureKind.HttpStatus) => "resposta HTTP de erro do serviço",
        nameof(KnightFailureKind.Timeout) => "tempo esgotado",
        nameof(KnightFailureKind.CircuitOpen) => "leitura não enviada — circuito do cliente HTTP aberto por falhas recentes",
        nameof(KnightFailureKind.Transport) => "falha de rede ou de transporte antes da resposta",
        nameof(KnightFailureKind.Contract) => "resposta fora do contrato documentado",
        nameof(KnightFailureKind.Internal) => "falha interna do AEGIS (não atribuída ao provedor)",
        nameof(KnightFailureKind.ProcessExit) => "o processo do adaptador terminou com código de erro",
        nameof(KnightFailureKind.ProcessNoResult) => "o processo do adaptador terminou sem resultado",
        nameof(KnightFailureKind.OutputLimit) => "saída do adaptador acima do limite",
        nameof(KnightFailureKind.RuntimeUnavailable) => "runtime ou módulo do adaptador indisponível",
        nameof(KnightFailureKind.PlatformUnsupported) => "leitura indisponível nesta plataforma",
        nameof(KnightFailureKind.Connection) => "sessão do serviço não estabelecida",
        nameof(KnightFailureKind.Command) => "comando de leitura falhou na sessão",
        nameof(KnightFailureKind.Incomplete) => "leitura incompleta",
        nameof(KnightFailureKind.NoScope) => "nada a consultar no escopo",
        _ => kind,
    };

    /// <summary>Detalhe técnico SEGURO em linhas (vocabulário fechado e números), como na tela.</summary>
    public static IReadOnlyList<string> TechnicalLines(KnightCapabilityDiagnostic? d)
    {
        if (d is null) return Array.Empty<string>();
        var lines = new List<string> { "Tipo: " + FailureKindLabel(d.Kind.ToString()), "Estágio: " + d.Stage };
        if (d.DurationMs is { } ms) lines.Add("Duração: " + (ms / 1000.0).ToString("0.0", ReportChart.Pt) + " s");
        if (d.HttpStatus is not null || !string.IsNullOrEmpty(d.Code))
            lines.Add(string.Join(" · ", new[] { d.HttpStatus is { } h ? $"HTTP {h}" : null, string.IsNullOrEmpty(d.Code) ? null : "código " + d.Code }.OfType<string>()));
        if (!string.IsNullOrEmpty(d.Endpoint)) lines.Add("Endpoint: " + d.Endpoint);
        if (!string.IsNullOrEmpty(d.CorrelationId)) lines.Add("Correlação da execução: " + d.CorrelationId);
        if (!string.IsNullOrEmpty(d.ServiceRequestId)) lines.Add("Requisição no serviço: " + d.ServiceRequestId);
        return lines;
    }

    /// <summary>Código + título congelado; título nulo quando a fotografia não tem o controle (nunca o catálogo atual).</summary>
    public static ReportControlRef RefOf(IReadOnlyDictionary<string, string> titles, string id) =>
        new(id, titles.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t) ? t : null);

    private static IEnumerable<string?> GlossaryTexts(ReportControl c) =>
        new[] { c.Title, c.Description, c.Rationale, c.Impact, c.ExpectedConfiguration, c.DoesNotProve, c.Criterion, c.Recommendation,
                c.NotEvaluatedReason, c.Evidence, c.Service, c.DomainLabel }
            .Concat(c.Frameworks);

    private static ReportReferenceCoverage? BuildCoverage(string? json)
    {
        if (KnightReferenceCoverageSnapshot.Deserialize(json) is not { } c) return null;
        static ReportCoverageRow Row(KnightReferenceCoverageGroup g) => new(
            g.Key, g.Label, g.Total, g.Implemented, g.Partial, g.Pending, g.ManualOnly, g.RequiresAccess, g.ApiLimitation,
            g.FullPercent, g.PartialPercent, g.AnyAutomatedPercent, g.PreviewOnly, g.PreviewBacked);
        return new ReportReferenceCoverage(
            c.CatalogVersion, c.ReferenceCommit,
            c.Frameworks.Select(f => $"{f.Name} {f.Version} ({f.Controls} controles)").ToList(),
            Row(c.Total), c.ByPlatform.Select(Row).ToList(), c.ByService.Select(Row).ToList(),
            c.ManualResults ?? Array.Empty<KnightManualResultEntry>());
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
                    o.Kind.ToString(), KnightObjectNouns.Label(o.Kind, o.ExternalId), o.ExternalId, o.DisplayName, o.UserPrincipalName,
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
            reach,
            i.RequiredCapabilities
                .Select(c => Enum.TryParse<KnightCapability>(c, out var cap) ? KnightCapabilityLabels.Label(cap) : c)
                .ToList(),
            null,
            KnightCapabilityLabels.PreviewApis(i.RequiredCapabilities));
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
                    affected, Guidance(c.Capability, c.Outcome), s.DataRecency is { } t ? Iso(t) : null,
                    c.Diagnostic?.Kind.ToString(), TechnicalLines(c.Diagnostic));
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

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] A composição congela cada limitação como "Capacidade: Desfecho — detalhe" (identificadores).
    /// Para leitura, os identificadores viram os MESMOS rótulos da tabela de limitações; o detalhe fica como foi registrado.
    /// </summary>
    public static string ReadableLimitation(string raw)
    {
        var sep = raw.IndexOf(": ", StringComparison.Ordinal);
        if (sep <= 0) return raw;
        var rest = raw[(sep + 2)..];
        var dash = rest.IndexOf(" — ", StringComparison.Ordinal);
        var outcomeText = dash < 0 ? rest : rest[..dash];
        if (!Enum.TryParse<KnightCapability>(raw[..sep], out var cap) || !Enum.TryParse<KnightCapabilityOutcome>(outcomeText, out var outcome))
            return raw;
        return $"{KnightCapabilityLabels.Label(cap)}: {CauseLabel(outcome)}" + (dash < 0 ? "" : rest[dash..]);
    }

    public static IReadOnlyList<KnightConsolidatedSourceEntry>? ReadableComposition(IReadOnlyList<KnightConsolidatedSourceEntry>? composition) =>
        composition?.Select(e => e with { CollectionLimitations = e.CollectionLimitations.Select(ReadableLimitation).ToList() }).ToList();

    private static string CauseLabel(KnightCapabilityOutcome o) => o switch
    {
        // [AEGIS-KNIGHT-COVERAGE-03] O rótulo é o que foi OBSERVADO. A recusa não distingue entre consentimento
        // ausente, papel de diretório sem alcance, domínio errado e método de autenticação não aceito — e no
        // Exchange Online a permissão de API sozinha não autoriza comando nenhum. O requisito concreto sai no
        // campo ao lado (RequiredPermission), que é requisito, e não diagnóstico.
        KnightCapabilityOutcome.InsufficientPermission => "Autorização recusada",
        KnightCapabilityOutcome.LimitedByLicense => "Licença insuficiente",
        KnightCapabilityOutcome.Throttled => "Limite de taxa do provedor",
        KnightCapabilityOutcome.AuthenticationFailure => "Falha de autenticação",
        KnightCapabilityOutcome.Unavailable => "Serviço indisponível",
        KnightCapabilityOutcome.NotAttempted => "Não executada",
        KnightCapabilityOutcome.Error => "Erro de coleta",
        // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Leitura oficial indisponível no ambiente de execução — nem falha nem permissão.
        KnightCapabilityOutcome.PlatformUnsupported => "Limitação de plataforma",
        _ => o.ToString(),
    };

    private static string Guidance(KnightCapability c, KnightCapabilityOutcome o) => o switch
    {
        // [AEGIS-KNIGHT-READONLY-01] Leitura não solicitada por política: não há permissão a conceder.
        KnightCapabilityOutcome.NotAttempted when KnightCollectorCapabilities.NotRequested.Contains(c) =>
            "Nada a conceder: o AEGIS não faz esta leitura porque a única forma documentada exige permissão que também concede escrita "
            + "ou administração. Os controles afetados ficam não avaliados; a verificação feita no portal do fornecedor pode ser registrada "
            + "como resultado manual da referência.",
        // [AEGIS-KNIGHT-COVERAGE-04] No Azure a autorização é uma ATRIBUIÇÃO DE PAPEL no escopo recusado (Azure RBAC ou o
        // plano de dados do cofre), não um consentimento de administrador.
        KnightCapabilityOutcome.InsufficientPermission when c.ToString().StartsWith("Azure", StringComparison.Ordinal) =>
            "Atribuir à aplicação o papel indicado no escopo recusado (Azure RBAC na assinatura, ou no cofre) e sincronizar novamente em Configurações → Integrações.",
        KnightCapabilityOutcome.InsufficientPermission =>
            "Conceder a permissão indicada ao aplicativo do conector (consentimento de administrador) e sincronizar novamente em Configurações → Integrações.",
        KnightCapabilityOutcome.LimitedByLicense =>
            "A capacidade depende de licença do provedor. Sem ela, os controles afetados continuam não avaliados — nunca aprovados.",
        KnightCapabilityOutcome.Throttled => "O provedor limitou a taxa de chamadas. Sincronizar novamente mais tarde.",
        KnightCapabilityOutcome.AuthenticationFailure =>
            "Verificar a credencial do conector (segredo expirado ou revogado) e reconectar em Configurações → Integrações.",
        KnightCapabilityOutcome.Unavailable => "O serviço do provedor não respondeu. Sincronizar novamente; persistindo, verificar a conectividade.",
        KnightCapabilityOutcome.NotAttempted => "A capacidade não foi executada nesta coleta.",
        KnightCapabilityOutcome.PlatformUnsupported =>
            "Nada a conceder: a leitura oficial não está disponível na plataforma em que o AEGIS executa. Os controles afetados ficam não "
            + "avaliados; a verificação feita no portal do fornecedor pode ser registrada como resultado manual da referência.",
        _ => "Erro de coleta nesta capacidade (o detalhe técnico diz o tipo e o estágio). Sincronizar novamente; persistindo, acionar o "
            + "suporte com o identificador de correlação.",
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

        // [AEGIS-KNIGHT-COVERAGE-02] Microsoft Teams.
        KnightCapability.TeamsClientConfiguration => "Configuração do cliente do Teams",
        KnightCapability.TeamsFederationConfiguration => "Federação e acesso externo do Teams",
        KnightCapability.TeamsMeetingPolicies => "Políticas de reunião do Teams",
        KnightCapability.TeamsMessagingPolicies => "Políticas de mensagens do Teams",
        KnightCapability.TeamsAppPermissionPolicies => "Políticas de permissão de aplicativos do Teams",
        KnightCapability.TeamsPolicyAssignments => "Atribuições de política do Teams a grupos",

        // [AEGIS-KNIGHT-COVERAGE-03] Exchange Online. Sem estes rótulos, a tabela de limitações do relatório
        // mostrava o nome do símbolo do código ("ExchangeCasMailboxes") para quem lê o relatório.
        KnightCapability.ExchangeOrganizationConfig => "Configuração da organização do Exchange",
        KnightCapability.ExchangeTransportConfig => "Configuração de transporte do Exchange",
        KnightCapability.ExchangeSharingPolicies => "Políticas de compartilhamento",
        KnightCapability.ExchangeOwaMailboxPolicies => "Políticas do Outlook na web",
        KnightCapability.ExchangeTransportRules => "Regras de transporte (fluxo de emails)",
        KnightCapability.ExchangeRoleAssignmentPolicies => "Políticas de atribuição de função ao usuário final",
        KnightCapability.ExchangeExternalSenderIdentification => "Identificação de remetentes externos",
        KnightCapability.ExchangeOutboundSpamFilterPolicies => "Políticas de filtro de spam de saída",
        KnightCapability.ExchangeMailboxes => "Caixas de correio",
        KnightCapability.ExchangeMailboxSignIn => "Estado de entrada das contas",
        KnightCapability.ExchangeCasMailboxes => "Acesso de cliente por caixa de correio",
        KnightCapability.ExchangeAuditBypassAssociations => "Desvios de auditoria de caixa de correio",

        // [AEGIS-KNIGHT-COVERAGE-04] Defender para Office 365, Purview, SharePoint/OneDrive, Intune e Fabric.
        KnightCapability.DefenderAtpPolicy => "Anexos Seguros para SharePoint, OneDrive e Teams",
        KnightCapability.DefenderSafeLinks => "Políticas e regras de Links Seguros",
        KnightCapability.DefenderSafeAttachments => "Políticas e regras de Anexos Seguros",
        KnightCapability.DefenderMalwareFilter => "Políticas e regras antimalware",
        KnightCapability.DefenderInboundSpam => "Políticas e regras antispam de entrada",
        KnightCapability.DefenderConnectionFilter => "Filtro de conexão",
        KnightCapability.DefenderOutboundSpam => "Políticas e regras antispam de saída",
        KnightCapability.DefenderAntiPhish => "Políticas e regras antiphishing",
        KnightCapability.DefenderDkim => "Assinatura DKIM por domínio",
        KnightCapability.DefenderAcceptedDomains => "Domínios aceitos",
        KnightCapability.DefenderDnsRecords => "Registros SPF e DMARC (consulta DNS)",
        KnightCapability.DefenderTeamsProtection => "Proteção do Teams (ZAP)",
        KnightCapability.DefenderPriorityAccounts => "Contas prioritárias",
        KnightCapability.DefenderPresetPolicies => "Políticas de segurança predefinidas",
        KnightCapability.PurviewAuditConfig => "Configuração do log de auditoria unificado",
        KnightCapability.PurviewDlpPolicies => "Políticas de prevenção contra perda de dados (DLP)",
        KnightCapability.PurviewLabelPolicies => "Políticas de rótulos de confidencialidade",
        KnightCapability.SharePointTenantSettings => "Configurações do SharePoint (Microsoft Graph)",
        KnightCapability.SharePointAdminTenant => "Configurações administrativas do SharePoint e OneDrive",
        KnightCapability.IntuneServiceSettings => "Configurações de conformidade do Intune",
        KnightCapability.IntuneEnrollmentRestrictions => "Restrições de registro de dispositivos",
        KnightCapability.FabricTenantSettings => "Configurações do locatário do Fabric",

        // [AEGIS-KNIGHT-COVERAGE-04] Azure Resource Manager.
        KnightCapability.AzureSubscriptions => "Assinaturas do Azure no escopo",
        KnightCapability.AzureAuthorization => "Atribuições de papel, papéis personalizados e bloqueios",
        KnightCapability.AzurePolicy => "Atribuições de política do Azure",
        KnightCapability.AzureDefenderForCloud => "Planos e configurações do Defender para Nuvem",
        KnightCapability.AzureMonitor => "Alertas do log de atividades e Application Insights",
        KnightCapability.AzureNetworking => "Rede do Azure (NSGs, redes virtuais, gateways, logs de fluxo)",
        KnightCapability.AzureStorage => "Contas de armazenamento e serviços de blob e arquivos",
        KnightCapability.AzureKeyVault => "Cofres de chaves, chaves e segredos (metadados)",
        KnightCapability.AzureKeyVaultCertificates => "Políticas de certificado dos cofres (plano de dados)",
        KnightCapability.AzureCompute => "Máquinas virtuais, discos, contêineres e Batch",
        KnightCapability.AzureAppService => "App Service, Functions, slots e ambientes",
        KnightCapability.AzureDatabases => "Bancos de dados (SQL, PostgreSQL, MySQL, Cosmos DB, Redis, Data Factory)",
        KnightCapability.AzureDatabricks => "Workspaces do Azure Databricks",
        KnightCapability.AzureTenantDiagnostics => "Configurações de diagnóstico do Microsoft Entra ID",

        // [AEGIS-KNIGHT-CLOSURE-01] Leituras em versão preview, acesso adicional e complementos.
        KnightCapability.AzureResourceDiagnostics => "Configurações de diagnóstico dos recursos do Azure (versão preview)",
        KnightCapability.AzureActivityLogExport => "Exportação do log de atividades das assinaturas (versão preview)",
        KnightCapability.AzureSecurityContacts => "Contatos e notificações do Defender para Nuvem (versão preview)",
        KnightCapability.AzureAppSettingsKeyVaultReferences => "Referências ao Key Vault nas configurações do App Service",
        KnightCapability.AzureDatabricksWorkspaceApi => "API dos workspaces do Azure Databricks",
        KnightCapability.PerUserMfaStates => "MFA por usuário (legado) (versão beta do Microsoft Graph)",
        KnightCapability.AuthenticationMethodsPolicyPreview => "Métodos de autenticação: campos da versão beta do Microsoft Graph",
        KnightCapability.M365AppsAndServicesSettings => "Aplicativos e serviços próprios dos usuários (versão beta do Microsoft Graph)",
        KnightCapability.M365FormsSettings => "Configurações do Microsoft Forms (versão beta do Microsoft Graph)",
        KnightCapability.ActivityBasedTimeoutPolicy => "Tempo limite de sessão ociosa do Microsoft 365",
        KnightCapability.DefenderReportSubmissionPolicy => "Destino das mensagens denunciadas pelos usuários",

        _ => c.ToString(),
    };

    /// <summary>
    /// [AEGIS-KNIGHT-CLOSURE-01] Versão PREVIEW (beta) da API que a capacidade consome, ou nula quando a leitura usa só
    /// versões estáveis. É a informação técnica que o relatório mostra ao lado dos controles que dependem dela: o
    /// fornecedor não suporta essas versões em produção, e uma mudança de contrato vira limitação, nunca aprovação.
    /// </summary>
    public static string? PreviewApi(KnightCapability c) => KnightCollectorCapabilities.PreviewApi(c);

    /// <summary>Versões preview das capacidades exigidas (identificadores congelados), sem repetição.</summary>
    public static IReadOnlyList<string> PreviewApis(IEnumerable<string> requiredCapabilities) =>
        requiredCapabilities
            .Select(c => Enum.TryParse<KnightCapability>(c, out var cap) ? PreviewApi(cap) : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();

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
            // [AEGIS-KNIGHT-CLOSURE-01] As leituras beta do estado da MFA por usuário e da política de métodos usam a mesma
            // permissão das estáveis; as configurações da organização têm permissões próprias, só de leitura.
            KnightCapability.PerUserMfaStates or KnightCapability.AuthenticationMethodsPolicyPreview
                or KnightCapability.ActivityBasedTimeoutPolicy => "Policy.Read.All (aplicativo)",
            KnightCapability.M365AppsAndServicesSettings => "OrgSettings-AppsAndServices.Read.All (aplicativo)",
            KnightCapability.M365FormsSettings => "OrgSettings-Forms.Read.All (aplicativo)",
            _ => null,
        },
        KnightSourceType.GoogleWorkspace => c switch
        {
            KnightCapability.DirectoryUsers => "admin.directory.user.readonly (delegação no domínio)",
            KnightCapability.DirectoryGroups => "admin.directory.group.readonly e admin.directory.group.member.readonly",
            KnightCapability.DriveSharingAudit or KnightCapability.OAuthTokenAudit => "admin.reports.audit.readonly",
            _ => null,
        },

        // [AEGIS-KNIGHT-COVERAGE-03] No Exchange Online a autorização NÃO é uma permissão de API por leitura: a
        // permissão habilita a conexão de aplicativo, e o que autoriza CADA comando é o papel de diretório que
        // viaja no token. Por isso a linha é a mesma para todas as capacidades — e diz as duas coisas, sem
        // sugerir que exista um consentimento específico por comando.
        KnightSourceType.MicrosoftExchangeOnline =>
            "Exchange.ManageAsApp (aplicativo) e um papel de diretório atribuído à aplicação — para leitura, Leitor Global",

        // [AEGIS-KNIGHT-CONSOLIDATED-01] A fotografia consolidada não tem UMA fonte — o requisito é inferido pelo
        // próprio nome da capacidade, que já é namespaced por fonte (Exchange*/Teams*/o restante é do Entra ID).
        // O Teams nunca teve requisito aqui (nenhum caso acima o cobre) — permanece assim.
        // [AEGIS-KNIGHT-COVERAGE-04] O Defender para Office 365 lê pela mesma conexão de aplicativo do Exchange Online.
        // O Purview divide: a auditoria vem dessa conexão; DLP e rótulos vêm da conexão do Security & Compliance, cuja
        // permissão fica na API "Microsoft Exchange Online Protection".
        KnightSourceType.MicrosoftDefenderForOffice365 => c == KnightCapability.DefenderDnsRecords
            ? null
            : RequiredPermission(c, KnightSourceType.MicrosoftExchangeOnline),
        KnightSourceType.MicrosoftPurview => c == KnightCapability.PurviewAuditConfig
            ? RequiredPermission(c, KnightSourceType.MicrosoftExchangeOnline)
            // [AEGIS-KNIGHT-READONLY-01] Papéis de leitura da tabela oficial de autenticação de aplicativo do Security &
            // Compliance (Leitor Global e Leitor de Segurança); "Leitor de Conformidade" não consta nela.
            : "Exchange.ManageAsApp na API Microsoft Exchange Online Protection (aplicativo) e um papel de diretório de leitura — Leitor Global ou Leitor de Segurança",
        KnightSourceType.MicrosoftSharePoint => c switch
        {
            KnightCapability.SharePointTenantSettings => "SharePointTenantSettings.Read.All (aplicativo)",
            // [AEGIS-KNIGHT-READONLY-01] Não solicitada: a permissão que a abre também concede escrita.
            KnightCapability.SharePointAdminTenant =>
                "Não solicitada pelo AEGIS: sem usuário, a API administrativa do SharePoint só aceita Sites.FullControl.All, que também concede escrita",
            _ => null,
        },
        KnightSourceType.MicrosoftIntune => c switch
        {
            KnightCapability.IntuneServiceSettings => "DeviceManagementConfiguration.Read.All (aplicativo)",
            KnightCapability.IntuneEnrollmentRestrictions => "DeviceManagementServiceConfig.Read.All (aplicativo)",
            _ => null,
        },
        // [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Requisitos DOCUMENTADOS da chamada, não diagnóstico: a recusa (ex.: 403
        // InsufficientScopes) não identifica qual deles falta, e o AEGIS não deduz isso dela.
        KnightSourceType.MicrosoftFabric =>
            "pré-requisitos documentados para entidade de serviço — a recusa não indica qual deles falta: a configuração do Fabric que "
            + "permite a entidades de serviço usar as APIs de administração somente leitura, habilitada para um grupo de segurança que "
            + "contenha a aplicação; e o token emitido para o recurso do Fabric com a credencial da própria aplicação",
        // [AEGIS-KNIGHT-COVERAGE-04] Azure: a autorização é do Azure RBAC, não do Microsoft Graph — salvo o estado das contas
        // com papel (Graph) e o plano de dados dos cofres. O diagnóstico do Entra ID é um recurso do locatário.
        KnightSourceType.MicrosoftAzure => c switch
        {
            KnightCapability.AzureKeyVaultCertificates =>
                "Leitura de certificados no plano de dados de cada cofre — papel Leitor do Key Vault (cofres com RBAC) ou permissões get/list de certificados na política de acesso",
            KnightCapability.AzureTenantDiagnostics =>
                "Leitura de microsoft.aadiam/diagnosticSettings no escopo do locatário (requisito documentado; a recusa não indica qual atribuição falta)",
            KnightCapability.AzureAuthorization =>
                "Papel Leitor do Azure RBAC nas assinaturas do escopo; o estado das contas com papel exige User.Read.All (Microsoft Graph, aplicativo)",
            KnightCapability.AzureAppSettingsKeyVaultReferences =>
                "Papel Leitor do Azure RBAC (Microsoft.Web/sites/config/Read); a ação Microsoft.Web/sites/config/list/action, que devolve os valores, não é usada",
            // [AEGIS-KNIGHT-READONLY-01] Não solicitada: sem acesso somente leitura documentado.
            KnightCapability.AzureDatabricksWorkspaceApi =>
                "Não solicitada pelo AEGIS: a API dos workspaces do Databricks não tem acesso somente leitura documentado (permissões de tokens e scripts globais são exclusivos do administrador do workspace; os direitos de um membro comum também permitem escrever objetos)",
            _ => "Papel Leitor do Azure RBAC nas assinaturas do escopo (ou no grupo de gerenciamento que as contém)",
        },

        KnightSourceType.Consolidated => SourceOfCapability(c) is { } owner ? RequiredPermission(c, owner) : null,

        _ => null,
    };

    /// <summary>A fonte cujo coletor produz a capacidade (as capacidades são exclusivas de cada fonte).</summary>
    private static KnightSourceType? SourceOfCapability(KnightCapability c) =>
        KnightSourceCatalog.All.Select(d => d.Source)
            .Where(s => s != KnightSourceType.Demo)
            .Cast<KnightSourceType?>()
            .FirstOrDefault(s => KnightCollectorCapabilities.Produces(s!.Value).Contains(c));
}
