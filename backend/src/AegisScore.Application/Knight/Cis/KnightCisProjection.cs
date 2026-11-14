using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Cis;

// ============================================================================
//  [AEGIS-KNIGHT-CIS-01] Visão CIS Benchmarks: resultado POR RECOMENDAÇÃO a partir da evidência
// ============================================================================
// Projeção DETERMINÍSTICA e PURA (sem EF, rede ou relógio): a tela lê a projeção ao vivo da composição do escopo, e a
// publicação congela a MESMA projeção na fotografia, de onde o HTML a lê. Cada recomendação (benchmark × edição × seção) é
// contada UMA vez; as variantes do catálogo (tipos de recurso, portas, papéis) são partes dela, nunca recomendações a mais.
//   • Conforme — o vínculo e a evidência comprovam todo o critério aplicável (num critério composto, todas as partes);
//   • Não conforme — a evidência demonstra a violação (uma regra parcial só quando a parte observada, sozinha, a prova);
//   • Não avaliado — coleta ausente ou incompleta, erro, acesso insuficiente, mapeamento não confirmado ou evidência que não
//     permite concluir; o motivo específico vai junto;
//   • Não aplicável — a evidência demonstra que o critério não se aplica ao ambiente;
//   • Fora do escopo — a fonte foi retirada deliberadamente do escopo salvo: aparece, mas fica fora dos totais.
// A verificação manual entra com a origem dita (autoria, evidência e validade); mitigação não é conformidade.

// ---- Entradas -----------------------------------------------------------------------------------------------

/// <summary>Um link oficial (Microsoft Learn) registrado no catálogo para uma regra.</summary>
public sealed record KnightCisLink(string Title, string Url);

/// <summary>Resultado de UMA regra do KNIGHT numa execução da composição, com os textos do catálogo.</summary>
public sealed record KnightCisRuleInput(
    string IndicatorId, string Title, KnightIndicatorStatus Status, string Evidence, string? NotEvaluatedReason,
    int AffectedCount, string? AffectedComposition, int EvidenceCount, string? DetailLimitation,
    string? Recommendation, string? ExpectedConfiguration, string? Criterion, string? Rationale, string? Impact,
    IReadOnlyList<KnightCisLink> Documentation);

/// <summary>Uma fonte candidata da composição, com a execução usada (quando incluída) e as regras dela.</summary>
public sealed record KnightCisSourceInput(
    KnightSourceType Source, string Label, string AvailabilityState, bool Included, Guid? RunId, string? CatalogVersion,
    DateTimeOffset? CapturedAt, string? SourceState, IReadOnlyList<KnightCisRuleInput> Rules);

/// <summary>Totais do KNIGHT na mesma composição, para a reconciliação (nunca recalculados aqui).</summary>
public sealed record KnightCisKnightTotals(int Controls, int Applicable, int Evaluated, int Passed, int Failed, int Mitigated, int NotEvaluated, int Errors, int NotApplicable);

public sealed record KnightCisInput(
    string OriginLabel, DateTimeOffset? DataRecency, IReadOnlyList<KnightCisSourceInput> Sources,
    IReadOnlyList<KnightManualResultView> Manual, KnightCisKnightTotals Knight);

// ---- Saída --------------------------------------------------------------------------------------------------

public sealed record KnightCisCounts(int Compliant, int NonCompliant, int NotEvaluated, int NotApplicable, int OutOfScope)
{
    /// <summary>Recomendações que entram nos totais (tudo menos "Fora do escopo").</summary>
    public int Considered => Compliant + NonCompliant + NotEvaluated + NotApplicable;
    /// <summary>Recomendações com veredito (conforme ou não conforme).</summary>
    public int Evaluated => Compliant + NonCompliant;
    public int Total => Considered + OutOfScope;
}

public sealed record KnightCisMapping(
    string CatalogVersion, string ReferenceRepository, string ReferenceCommit, string TraceRepository, string TraceCommit,
    IReadOnlyList<string> ReliableCatalogVersions, string Note, IReadOnlyList<string>? OfficialSources = null, int? CriteriaConfirmed = null,
    string? CriterionNote = null);

public sealed record KnightCisOriginSource(
    string Source, string Label, string Availability, string AvailabilityLabel, bool Included, Guid? RunId, string? CatalogVersion,
    DateTimeOffset? CapturedAt, string? SourceState, string? SourceStateLabel, bool MappingReliable, string? Note);

public sealed record KnightCisOrigin(string Label, DateTimeOffset? DataRecency, IReadOnlyList<KnightCisOriginSource> Sources);

public sealed record KnightCisProfileOption(string Key, string Label, int Recommendations, KnightCisCounts Counts);

public sealed record KnightCisBenchmarkSummary(
    string Key, string Framework, string Version, string Label, string Url, string Ruleset, int Recommendations, int CatalogEntries,
    KnightCisCounts Counts, KnightCisCounts ManualCounts, int Mitigated, IReadOnlyList<KnightCisProfileOption> Profiles,
    int ProfileNotRecorded, int ProfileDivergent, KnightCisCounts? KnightCounts = null, int? CriterionConfirmed = null);

/// <summary>[v2] Resultado TÉCNICO do KNIGHT contra a recomendação (regras, resultado, evidência) — não é resultado CIS.</summary>
public sealed record KnightCisReading(string Status, string StatusLabel, string Origin, string OriginLabel, bool Mitigated, string? Reason);

public static class KnightCisCriterionState
{
    public const string Confirmed = "Confirmed";
    public const string NotConfirmed = "NotConfirmed";

    public static string Label(string state) => state == Confirmed ? "Critério da edição confirmado em fonte oficial" : KnightCisCriteria.NotConfirmedReason;
}

/// <summary>
/// [v2] Situação do critério da edição: a edição e a seção (do rastreio), a procedência do rastreio e, quando houver, a fonte
/// oficial conferida, por quem, quando e para quais versões do catálogo e regras.
/// </summary>
public sealed record KnightCisCriterion(
    string State, string StateLabel, string Edition, string Section, string Trace, string? OfficialSource, string? Location,
    string? ConsultedOn, string? ReviewedBy, IReadOnlyList<string> ReviewedCatalogVersions, IReadOnlyList<string> ReviewedRules, string? Note);

/// <summary>Uma regra citada por uma variante, com o que o desfecho dela provou contra o critério oficial.</summary>
public sealed record KnightCisRuleRef(string IndicatorId, string Match, string MatchLabel, string? Note, string Proof, string ProofLabel);

public sealed record KnightCisVariant(
    string Key, string? Variant, string Title, string Status, string StatusLabel, string? Reason, string Disposition,
    string DispositionLabel, string? DispositionNote, string? TraceRule, IReadOnlyList<KnightCisRuleRef> Rules);

public sealed record KnightCisManual(
    string Result, string ResultLabel, string Justification, string ResponsibleName, string? EvidenceReference,
    string? EvidenceDocumentTitle, string? EvidenceDocumentSha256, string? ValidUntil, bool Expired, string RecordedByName,
    DateTimeOffset RecordedAt, bool Complement);

public sealed record KnightCisRecommendation(
    string Id, string Benchmark, string Section, string Title, string Service, string ServiceLabel, string Platform,
    string Severity, string SeverityLabel, string ProfileState, string ProfileStateLabel, IReadOnlyList<string> Profiles,
    IReadOnlyList<string> ProfileLabels, string Status, string StatusLabel, string Origin, string OriginLabel, bool Mitigated,
    string? Reason, string WhatChecked, string Found, string? WhyItMatters, string? HowToFix, IReadOnlyList<KnightCisLink> Links,
    IReadOnlyList<string> Limitations, IReadOnlyList<KnightCisVariant> Variants, IReadOnlyList<string> RuleIds, KnightCisManual? Manual,
    KnightCisReading? Knight = null, KnightCisCriterion? Criterion = null, string? MappingOrigin = null);

public sealed record KnightCisRule(
    string IndicatorId, string Title, string Source, string SourceLabel, Guid? RunId, string? CatalogVersion, string Status,
    string StatusLabel, string Evidence, string? NotEvaluatedReason, int AffectedCount, string? AffectedComposition, int EvidenceCount,
    string? DetailLimitation, string? Expected, string? Criterion, string? Recommendation, IReadOnlyList<KnightCisLink> Links,
    IReadOnlyList<string> Recommendations);

public sealed record KnightCisOwnCheck(string IndicatorId, string Title, string Source, string SourceLabel, string Status, string StatusLabel, string Note);

public sealed record KnightCisReconciliation(
    int KnightControls, int KnightApplicable, int KnightEvaluated, int KnightNotApplicable, int RulesInComposition, int RulesLinked,
    int OwnChecks, int Recommendations, int Considered, int Evaluated, int OutOfScope, int WithoutAutomatedRule, IReadOnlyList<string> Explanation);

public sealed record KnightCisView(
    string SchemaVersion, KnightCisMapping Mapping, KnightCisOrigin Origin, IReadOnlyList<KnightCisBenchmarkSummary> Benchmarks,
    IReadOnlyList<KnightCisRecommendation> Recommendations, IReadOnlyList<KnightCisRule> Rules, IReadOnlyList<KnightCisOwnCheck> OwnChecks,
    KnightCisReconciliation Reconciliation, IReadOnlyList<string> Limitations, string Disclaimer);

// ---- Vocabulário ----------------------------------------------------------------------------------------------

public static class KnightCisStatus
{
    public const string Compliant = "Compliant";
    public const string NonCompliant = "NonCompliant";
    public const string NotEvaluated = "NotEvaluated";
    public const string NotApplicable = "NotApplicable";
    public const string OutOfScope = "OutOfScope";

    public static readonly IReadOnlyList<string> All = new[] { NonCompliant, NotEvaluated, Compliant, NotApplicable, OutOfScope };

    public static string Label(string status) => status switch
    {
        Compliant => "Conforme",
        NonCompliant => "Não conforme",
        NotEvaluated => "Não avaliado",
        NotApplicable => "Não aplicável",
        OutOfScope => "Fora do escopo",
        _ => status,
    };

    public static string OriginLabel(string origin) => origin switch
    {
        "Automated" => "Avaliação automatizada",
        "Manual" => "Verificação manual",
        "AutomatedAndManual" => "Automatizada com complemento manual",
        _ => "Sem avaliação",
    };
}

public static class KnightCisProjection
{
    public const string SchemaVersion = "knight-cis-v2";

    public const string Disclaimer =
        "Recorte técnico do AEGIS KNIGHT sobre as recomendações dos CIS Benchmarks nas edições indicadas. O resultado do KNIGHT sai das "
        + "regras e da coleta desta avaliação; ele só vira resultado CIS quando o critério da edição foi conferido numa fonte oficial do CIS. "
        + "Não é certificação, auditoria nem atestado de conformidade emitido pelo CIS. Títulos e textos são autorais do AEGIS, em português; "
        + "o texto oficial das recomendações não é reproduzido.";

    // Estados internos de uma regra contra UMA referência.
    private enum Proof { Compliant, Violation, NotApplicable, Inconclusive, NoResult, OutOfScope, Unmapped }

    private static string ProofKey(Proof p) => p switch
    {
        Proof.Compliant => "ProvesCompliance",
        Proof.Violation => "ProvesViolation",
        Proof.NotApplicable => "ProvesNotApplicable",
        Proof.Inconclusive => "Inconclusive",
        Proof.NoResult => "NoResult",
        Proof.OutOfScope => "OutOfScope",
        _ => "MappingNotConfirmed",
    };

    private static string ProofLabel(Proof p) => p switch
    {
        Proof.Compliant => "Comprova o critério",
        Proof.Violation => "Comprova a violação",
        Proof.NotApplicable => "Comprova que não se aplica",
        Proof.Inconclusive => "Não permite concluir",
        Proof.NoResult => "Sem resultado desta regra",
        Proof.OutOfScope => "Fonte fora do escopo",
        _ => "Mapeamento não confirmado",
    };

    private sealed record RuleEval(string IndicatorId, KnightReferenceMatch Match, string? Note, Proof Proof, bool RawPassed,
        bool Mitigated, string? Reason, KnightCisRuleInput? Rule, KnightCisSourceInput? Source);

    private sealed record EntryEval(KnightReferenceStatus Status, string Result, string? Reason, string Origin, bool Mitigated,
        IReadOnlyList<RuleEval> Rules, KnightCisManual? Manual, string? Limitation);

    public static KnightCisView Build(KnightCisInput input) =>
        Build(input, KnightReferenceCatalog.Coverage(), KnightCatalog.Indicators, KnightCisCriteria.Registered);

    public static KnightCisView Build(KnightCisInput input, KnightReferenceCoverage coverage, IReadOnlyList<KnightIndicatorDefinition> catalog,
        IReadOnlyList<KnightCisCriterionConfirmation>? confirmations = null)
    {
        var confirmed = KnightCisCriteria.Index(confirmations ?? KnightCisCriteria.Registered);
        var candidates = KnightConsolidatedCandidates.Sources;
        var sources = input.Sources.ToDictionary(s => s.Source);
        var defs = catalog.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var manualByKey = input.Manual
            .Where(m => m.Result != KnightManualResult.Withdrawn)
            .GroupBy(m => m.ReferenceKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.RecordedAt).First(), StringComparer.Ordinal);

        // Regra → fonte e resultado (as regras são exclusivas de cada fonte; as compartilhadas só têm o Entra ID entre as candidatas).
        var ruleIndex = new Dictionary<string, (KnightCisSourceInput Source, KnightCisRuleInput Rule)>(StringComparer.Ordinal);
        foreach (var s in input.Sources.Where(s => s.Included))
            foreach (var r in s.Rules)
                ruleIndex.TryAdd(r.IndicatorId, (s, r));

        KnightSourceType? SourceOfIndicator(string id) =>
            defs.TryGetValue(id, out var d) ? candidates.Cast<KnightSourceType?>().FirstOrDefault(c => d.Sources.Contains(c!.Value)) : null;

        // Vínculos DECLARADOS por chave (inclusive regras ativas sem leitura somente leitura), com o grau de equivalência.
        var declared = new Dictionary<string, List<(string Id, KnightReferenceLink Link)>>(StringComparer.Ordinal);
        foreach (var d in catalog)
            foreach (var l in d.References)
            {
                if (!declared.TryGetValue(l.Key, out var list)) declared[l.Key] = list = new();
                list.Add((d.Id, l));
            }

        bool IsOut(KnightSourceType? s) => s is { } v && sources.TryGetValue(v, out var e)
            && string.Equals(e.AvailabilityState, KnightConsolidatedAvailability.OutOfScope, StringComparison.Ordinal);

        RuleEval EvalRule(string key, string id, KnightReferenceLink link, bool partOfComposite)
        {
            var src = SourceOfIndicator(id);
            if (IsOut(src))
                return new RuleEval(id, link.Match, link.Note, Proof.OutOfScope, false, false,
                    $"{KnightSourceCatalog.Label(src!.Value)} está fora do escopo salvo da avaliação.", null, null);
            if (src is null || !sources.TryGetValue(src.Value, out var se) || !se.Included || se.RunId is null)
            {
                var why = src is null ? "A regra não pertence a uma fonte da composição."
                    : sources.TryGetValue(src.Value, out var s2) && string.Equals(s2.AvailabilityState, KnightConsolidatedAvailability.Available, StringComparison.Ordinal)
                        ? $"{KnightSourceCatalog.Label(src.Value)} tem avaliação concluída, mas não foi incluída nesta composição."
                        : $"{KnightSourceCatalog.Label(src.Value)} não tem avaliação concluída nesta composição.";
                return new RuleEval(id, link.Match, link.Note, Proof.NoResult, false, false, why, null, null);
            }
            if (!KnightCisEvidencePolicy.MappingReliableFor(se.CatalogVersion))
                return new RuleEval(id, link.Match, link.Note, Proof.Unmapped, false, false,
                    $"A avaliação de {se.Label} foi executada com o catálogo {se.CatalogVersion ?? "não registrado"}; os vínculos entre regras e "
                    + $"recomendações foram revisados para {string.Join(", ", KnightCisEvidencePolicy.MappingCatalogVersions)}.", null, se);
            if (!ruleIndex.TryGetValue(id, out var hit))
                return new RuleEval(id, link.Match, link.Note, Proof.NoResult, false, false,
                    $"A regra {id} não consta da avaliação de {se.Label}.", null, se);

            var r = hit.Rule;
            var passed = r.Status == KnightIndicatorStatus.Passed;
            var mitigated = r.Status == KnightIndicatorStatus.Mitigated;
            switch (r.Status)
            {
                case KnightIndicatorStatus.NotEvaluated:
                case KnightIndicatorStatus.Error:
                    return new RuleEval(id, link.Match, link.Note, Proof.Inconclusive, false, false,
                        Reason(r), r, se);
            }

            if (link.Match == KnightReferenceMatch.Exact && !partOfComposite)
            {
                var p = r.Status switch
                {
                    KnightIndicatorStatus.Passed => Proof.Compliant,
                    KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated => Proof.Violation,
                    _ => Proof.NotApplicable,
                };
                return new RuleEval(id, link.Match, link.Note, p, passed, mitigated, null, r, se);
            }

            var proof = KnightCisEvidencePolicy.PartialProofOf(key, id);
            var decision = r.Status switch
            {
                KnightIndicatorStatus.Passed => proof?.PassProves == true ? Proof.Compliant : Proof.Inconclusive,
                KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated => proof?.FailProves == true ? Proof.Violation : Proof.Inconclusive,
                _ => proof?.NotApplicableProves == true ? Proof.NotApplicable : Proof.Inconclusive,
            };
            var reason = decision == Proof.Inconclusive
                ? $"{id} ({StatusLabel(r.Status).ToLowerInvariant()}) avalia só parte do critério: " + (proof?.Rationale ?? link.Note ?? "a equivalência é parcial.")
                : null;
            return new RuleEval(id, link.Match, link.Note, decision, passed, mitigated, reason, r, se);
        }

        EntryEval EvalEntry(KnightReferenceStatus st)
        {
            var key = st.Control.Key;
            var serviceSource = SourceOfService(st.Control.Service);
            var manual = manualByKey.TryGetValue(key, out var mv) ? ManualOf(mv, KnightManualResults.IsReadOnlyComplement(st)) : null;

            if (st.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial && st.IndicatorIds.Count > 0)
            {
                var links = declared.TryGetValue(key, out var l) ? l : new();
                var composite = KnightReferenceDispositions.CompositeOf(key) is { } parts && parts.All(p => st.IndicatorIds.Contains(p, StringComparer.Ordinal))
                    ? parts : null;
                var rules = st.IndicatorIds
                    .Select(id => (Id: id, Link: links.FirstOrDefault(x => x.Id == id).Link ?? new KnightReferenceLink(key, KnightReferenceMatch.Partial)))
                    .Select(x => EvalRule(key, x.Id, x.Link, composite is not null))
                    .ToList();

                var (result, reason, mitigatedFlag) = Combine(rules, composite is not null);
                // [AEGIS-KNIGHT-READONLY-01] Complemento manual de leitura parcial: o manual cobre só o que a leitura não alcança.
                if (manual is not null && KnightManualResults.IsReadOnlyComplement(st) && result != KnightCisStatus.OutOfScope)
                {
                    if (result == KnightCisStatus.NonCompliant) return new EntryEval(st, result, reason, "Automated", mitigatedFlag, rules, manual, null);
                    if (manual.Expired)
                        return new EntryEval(st, KnightCisStatus.NotEvaluated, reason + $" O complemento manual venceu em {manual.ValidUntil}.", "Automated", false, rules, manual, null);
                    if (manual.Result == nameof(KnightManualResult.NonCompliant))
                        return new EntryEval(st, KnightCisStatus.NonCompliant, null, "Manual", false, rules, manual, null);
                    if (manual.Result == nameof(KnightManualResult.Compliant) && rules.All(r => r.RawPassed || r.Proof == Proof.NotApplicable) && rules.Any(r => r.RawPassed))
                        return new EntryEval(st, KnightCisStatus.Compliant, null, "AutomatedAndManual", false, rules, manual, null);
                }
                return new EntryEval(st, result, reason, result == KnightCisStatus.NotEvaluated || result == KnightCisStatus.OutOfScope ? "None" : "Automated",
                    mitigatedFlag, rules, KnightManualResults.IsReadOnlyComplement(st) ? manual : null, null);
            }

            var declaredRules = (declared.TryGetValue(key, out var dl) ? dl : new())
                .Select(x => EvalRule(key, x.Id, x.Link, false) with { Proof = Proof.Inconclusive })
                .ToList();

            if (IsOut(serviceSource))
                return new EntryEval(st, KnightCisStatus.OutOfScope, $"{KnightSourceCatalog.Label(serviceSource!.Value)} está fora do escopo salvo da avaliação.",
                    "None", false, declaredRules, manual, null);

            var dispositionText = KnightReferenceCatalog.DispositionLabel(st.Disposition) + (st.Note is null ? "." : ": " + st.Note);
            if (KnightManualResults.Eligible(st.Disposition) && manual is not null)
            {
                if (manual.Expired)
                    return new EntryEval(st, KnightCisStatus.NotEvaluated,
                        $"A verificação manual registrada venceu em {manual.ValidUntil} e precisa ser renovada. {dispositionText}", "None", false, declaredRules, manual, null);
                var r = manual.Result switch
                {
                    nameof(KnightManualResult.Compliant) => KnightCisStatus.Compliant,
                    nameof(KnightManualResult.NonCompliant) => KnightCisStatus.NonCompliant,
                    _ => KnightCisStatus.NotApplicable,
                };
                return new EntryEval(st, r, null, "Manual", false, declaredRules, manual, dispositionText);
            }
            return new EntryEval(st, KnightCisStatus.NotEvaluated, dispositionText, "None", false, declaredRules, manual, null);
        }

        // ---- Recomendações ---------------------------------------------------------------------------------
        var byRecommendation = coverage.Controls
            .Where(c => KnightCisTrace.BenchmarkOf(c.Control.Key) is not null)
            .GroupBy(c => RecommendationId(c.Control.Key), StringComparer.Ordinal)
            .ToList();

        var recommendations = new List<KnightCisRecommendation>();
        var ownReferenceEntries = new List<(KnightReferenceStatus Status, EntryEval Eval)>();
        foreach (var g in byRecommendation)
        {
            var entries = g.ToList();
            var evals = entries.Select(EvalEntry).ToList();
            var traces = entries.Select(e => KnightCisTrace.For(e.Control.Key)).ToList();
            // Seção fora do ruleset da edição: não é uma recomendação rastreada — é verificação própria do KNIGHT.
            if (traces.All(t => t is null || t.ProfileState == KnightCisProfileState.NotTraced))
            {
                ownReferenceEntries.AddRange(entries.Zip(evals));
                continue;
            }
            recommendations.Add(BuildRecommendation(g.Key, entries, evals, traces, confirmed));
        }
        recommendations = recommendations
            .OrderBy(r => BenchmarkOrder(r.Benchmark)).ThenBy(r => SectionSortKey(r.Section), StringComparer.Ordinal)
            .ToList();

        // ---- Regras citadas e verificações próprias -----------------------------------------------------------
        var ruleToRecs = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var r in recommendations)
            foreach (var id in r.RuleIds)
            {
                if (!ruleToRecs.TryGetValue(id, out var set)) ruleToRecs[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(r.Id);
            }
        var rules = ruleIndex.Values
            .Where(x => ruleToRecs.ContainsKey(x.Rule.IndicatorId))
            .OrderBy(x => x.Rule.IndicatorId, StringComparer.Ordinal)
            .Select(x => RuleOf(x.Source, x.Rule, ruleToRecs[x.Rule.IndicatorId].ToList()))
            .ToList();
        var ownChecks = ruleIndex.Values
            .Where(x => !ruleToRecs.ContainsKey(x.Rule.IndicatorId))
            .OrderBy(x => x.Rule.IndicatorId, StringComparer.Ordinal)
            .Select(x =>
            {
                var cites = defs.TryGetValue(x.Rule.IndicatorId, out var d) ? d.References.Select(l => l.Key).ToList() : new List<string>();
                var note = cites.Count == 0
                    ? "Verificação própria do KNIGHT: não corresponde a uma recomendação dos CIS Benchmarks registrados."
                    : "Verificação própria do KNIGHT registrada no catálogo de referência sem seção CIS (" + string.Join(", ", cites) + ").";
                return new KnightCisOwnCheck(x.Rule.IndicatorId, x.Rule.Title, x.Source.Source.ToString(), x.Source.Label,
                    x.Rule.Status.ToString(), StatusLabel(x.Rule.Status), note);
            })
            .ToList();

        // ---- Benchmarks -------------------------------------------------------------------------------------
        var benchmarks = KnightCisTrace.Benchmarks.Select(b =>
        {
            var recs = recommendations.Where(r => r.Benchmark == b.Key).ToList();
            var entries = coverage.Controls.Count(c => KnightCisTrace.BenchmarkOf(c.Control.Key)?.Key == b.Key);
            var profileTags = recs.SelectMany(r => r.Profiles).Distinct(StringComparer.Ordinal).OrderBy(ProfileOrder).ToList();
            var options = profileTags.Select(tag =>
            {
                var sel = recs.Where(r => r.Profiles.Contains(tag, StringComparer.Ordinal)).ToList();
                return new KnightCisProfileOption(tag, KnightCisTrace.ProfileLabel(tag), sel.Count, CountsOf(sel));
            }).ToList();
            return new KnightCisBenchmarkSummary(b.Key, b.Framework, b.Version, b.Label, b.Url, b.Ruleset, recs.Count, entries,
                CountsOf(recs), KnightCountsOf(recs.Where(r => r.Knight!.Origin == "Manual")), recs.Count(r => r.Knight!.Mitigated), options,
                recs.Count(r => r.ProfileState == nameof(KnightCisProfileState.NotRecorded)),
                recs.Count(r => r.ProfileState is nameof(KnightCisProfileState.Divergent) or nameof(KnightCisProfileState.Ambiguous)),
                KnightCountsOf(recs), recs.Count(r => r.Criterion!.State == KnightCisCriterionState.Confirmed));
        }).ToList();

        // ---- Origem e reconciliação ------------------------------------------------------------------------
        var origin = new KnightCisOrigin(input.OriginLabel, input.DataRecency, input.Sources
            .Select(s => new KnightCisOriginSource(
                s.Source.ToString(), s.Label, s.AvailabilityState, KnightConsolidatedAvailability.Label(s.AvailabilityState, s.Included),
                s.Included, s.Included ? s.RunId : null, s.Included ? s.CatalogVersion : null, s.Included ? s.CapturedAt : null,
                s.Included ? s.SourceState : null, s.Included && s.SourceState is not null ? KnightConsolidatedCollection.Label(s.SourceState) : null,
                s.Included && KnightCisEvidencePolicy.MappingReliableFor(s.CatalogVersion),
                s.Included && !KnightCisEvidencePolicy.MappingReliableFor(s.CatalogVersion)
                    ? $"Executada com o catálogo {s.CatalogVersion ?? "não registrado"}: as recomendações que dependem desta fonte ficam não avaliadas (vínculos revisados para {string.Join(", ", KnightCisEvidencePolicy.MappingCatalogVersions)})."
                    : null))
            .ToList());

        var all = CountsOf(recommendations);
        var outLabels = input.Sources.Where(s => s.AvailabilityState == KnightConsolidatedAvailability.OutOfScope).Select(s => s.Label).ToList();
        var withoutRule = recommendations.Count(r => r.Variants.All(v => v.Disposition is not (nameof(KnightReferenceDisposition.Implemented) or nameof(KnightReferenceDisposition.Partial))));
        var linked = rules.Count;
        var inComposition = ruleIndex.Count;
        var explanation = new List<string>
        {
            "O KNIGHT conta regras (controles); a visão CIS conta recomendações. Uma regra pode verificar mais de uma recomendação, inclusive "
            + "em benchmarks diferentes, e uma recomendação pode depender de mais de uma regra — por isso os totais não coincidem.",
            $"Nas fontes incluídas, {inComposition} regras foram avaliadas pelo KNIGHT ({input.Knight.Evaluated} de {input.Knight.Applicable} aplicáveis "
            + $"com veredito; {input.Knight.NotApplicable} não aplicáveis). {linked} delas sustentam alguma recomendação CIS; "
            + $"{ownChecks.Count} são verificações próprias do KNIGHT, sem recomendação CIS correspondente, e ficam fora destes totais.",
            $"{withoutRule} recomendações não têm regra automatizada (verificação manual, sem método na API oficial, acesso que o AEGIS não "
            + "usa ou ainda não implementadas): contam como não avaliadas, ou com o resultado manual vigente e a origem dita.",
            "Cada benchmark conta a recomendação uma vez. As variantes do catálogo (tipos de recurso, portas, papéis) são partes da mesma "
            + "recomendação; Microsoft 365 e Azure Foundations podem ter recomendações equivalentes, e cada uma conta no seu benchmark.",
        };
        if (all.OutOfScope > 0)
            explanation.Add($"{all.OutOfScope} recomendações dependem de {(outLabels.Count == 1 ? "fonte fora" : "fontes fora")} do escopo salvo "
                + $"({string.Join(", ", outLabels)}) e aparecem como \"Fora do escopo\", fora dos totais.");
        var knightAll = KnightCountsOf(recommendations);
        var withReading = knightAll.Compliant + knightAll.NonCompliant + knightAll.NotApplicable;
        var sustained = all.Compliant + all.NonCompliant + all.NotApplicable;
        explanation.Add($"O resultado do KNIGHT chega a uma conclusão técnica em {withReading} recomendações ({knightAll.Compliant} aprovadas, "
            + $"{knightAll.NonCompliant} reprovadas, {knightAll.NotApplicable} não aplicáveis). {sustained} têm o critério da edição confirmado "
            + "numa fonte oficial do CIS e a evidência suficiente para um resultado CIS; as demais contam como não avaliadas, com a leitura do KNIGHT ao lado.");
        explanation.Add($"Totais CIS: {all.Considered} recomendações consideradas = {all.Compliant} conformes + {all.NonCompliant} não conformes + "
            + $"{all.NotEvaluated} não avaliadas + {all.NotApplicable} não aplicáveis; {all.OutOfScope} fora do escopo; {recommendations.Count} no total.");

        var reconciliation = new KnightCisReconciliation(input.Knight.Controls, input.Knight.Applicable, input.Knight.Evaluated,
            input.Knight.NotApplicable, inComposition, linked, ownChecks.Count, recommendations.Count, all.Considered, all.Evaluated,
            all.OutOfScope, withoutRule, explanation);

        var limitations = new List<string>
        {
            KnightCisCriteria.OfficialSourcesConsulted.Count == 0
                ? "Nenhuma fonte oficial do CIS foi consultada: as edições oficiais são distribuídas mediante cadastro no site do CIS, que não foi feito. "
                  + "Sem o critério da edição confirmado, nenhuma recomendação recebe Conforme, Não conforme ou Não aplicável "
                  + $"(\"{KnightCisCriteria.NotConfirmedReason}\"); o resultado do KNIGHT aparece ao lado, como leitura técnica."
                : $"Critérios conferidos em: {string.Join("; ", KnightCisCriteria.OfficialSourcesConsulted)}. As demais recomendações ficam não avaliadas "
                  + $"(\"{KnightCisCriteria.NotConfirmedReason}\").",
            $"Identificação da edição e da seção rastreada nos rulesets do Monkey365 (commit {Short(KnightCisTrace.Commit)}). O rastreio, a classificação "
            + "integral ou parcial do catálogo, o perfil e a versão do catálogo da execução não confirmam o critério oficial. Onde a fonte de rastreio "
            + "diverge, a recomendação fica sem veredito.",
            "Perfil (licença E3/E5 e nível) é o registrado pela fonte de rastreio PARA ESTA EDIÇÃO; quando ela não registra, ou registra de "
            + "forma divergente, o perfil fica como limitação e a recomendação aparece só em \"Todos os perfis\".",
            "A orientação de correção é a registrada nas regras do KNIGHT, com os links da Microsoft Learn do catálogo; ela é apresentada "
            + "para revisão humana antes de qualquer mudança.",
        };
        var unmapped = input.Sources.Where(s => s.Included && !KnightCisEvidencePolicy.MappingReliableFor(s.CatalogVersion)).Select(s => s.Label).ToList();
        if (unmapped.Count > 0)
            limitations.Add($"Avaliação histórica sem mapeamento confirmado para a versão executada: {string.Join(", ", unmapped)}. As recomendações "
                + "que dependem dessas fontes ficam não avaliadas nesta visão; os resultados do KNIGHT delas continuam valendo nas abas da fonte.");
        if (ownReferenceEntries.Count > 0)
            limitations.Add($"{ownReferenceEntries.Count} entrada(s) do catálogo de referência sem seção CIS na edição (ex.: {ownReferenceEntries[0].Status.Control.Key}) "
                + "são verificações próprias do KNIGHT e não entram nos totais dos benchmarks.");

        return new KnightCisView(SchemaVersion,
            new KnightCisMapping(coverage.CatalogVersion, "https://github.com/silverhack/monkey365", coverage.ReferenceCommit,
                KnightCisTrace.Repository, KnightCisTrace.Commit, KnightCisEvidencePolicy.MappingCatalogVersions,
                $"Vínculos regra → recomendação do catálogo {coverage.CatalogVersion}, revisados para {string.Join(", ", KnightCisEvidencePolicy.MappingCatalogVersions)}; "
                + $"identificação e perfil rastreados no Monkey365 (commit {Short(KnightCisTrace.Commit)}).",
                KnightCisCriteria.OfficialSourcesConsulted, recommendations.Count(r => r.Criterion!.State == KnightCisCriterionState.Confirmed),
                KnightCisCriteria.OfficialSourcesConsulted.Count == 0 && confirmed.Count == 0
                    ? "Nenhuma fonte oficial do CIS consultada: nenhum critério de edição confirmado."
                    : $"Critério da edição confirmado em {recommendations.Count(r => r.Criterion!.State == KnightCisCriterionState.Confirmed)} recomendação(ões)."),
            origin, benchmarks, recommendations, rules, ownChecks, reconciliation, limitations, Disclaimer);
    }

    // ---- Combinação ---------------------------------------------------------------------------------------------

    private static (string Result, string? Reason, bool Mitigated) Combine(IReadOnlyList<RuleEval> rules, bool composite)
    {
        if (rules.Count == 0) return (KnightCisStatus.NotEvaluated, "Nenhuma regra ativa avalia esta entrada.", false);
        if (rules.All(r => r.Proof == Proof.OutOfScope)) return (KnightCisStatus.OutOfScope, rules[0].Reason, false);
        var violations = rules.Where(r => r.Proof == Proof.Violation).ToList();
        if (violations.Count > 0) return (KnightCisStatus.NonCompliant, null, violations.All(v => v.Mitigated));

        if (composite)
        {
            // Critério composto: todas as partes aprovadas (cada uma é necessária) comprovam o critério.
            if (rules.All(r => r.RawPassed)) return (KnightCisStatus.Compliant, null, false);
            if (rules.All(r => r.Proof == Proof.NotApplicable)) return (KnightCisStatus.NotApplicable, null, false);
            var pending = rules.Where(r => !r.RawPassed).ToList();
            return (KnightCisStatus.NotEvaluated,
                "Critério composto: nem todas as partes estão comprovadas. " + string.Join(" ", pending.Select(r => r.Reason ?? $"{r.IndicatorId}: {ProofLabel(r.Proof).ToLowerInvariant()}.")),
                false);
        }

        var open = rules.Where(r => r.Proof is not (Proof.Compliant or Proof.NotApplicable)).ToList();
        if (open.Count == 0)
            return rules.Any(r => r.Proof == Proof.Compliant)
                ? (KnightCisStatus.Compliant, null, false)
                : (KnightCisStatus.NotApplicable, null, false);
        return (KnightCisStatus.NotEvaluated, string.Join(" ", open.Select(r => r.Reason ?? $"{r.IndicatorId}: {ProofLabel(r.Proof).ToLowerInvariant()}.").Distinct()), false);
    }

    private static KnightCisRecommendation BuildRecommendation(
        string id, IReadOnlyList<KnightReferenceStatus> entries, IReadOnlyList<EntryEval> evals, IReadOnlyList<KnightCisTraceEntry?> traces,
        IReadOnlyDictionary<string, KnightCisCriterionConfirmation> confirmations)
    {
        // Primeiro a LEITURA TÉCNICA do KNIGHT (o que a evidência das regras sustenta contra o critério rastreado); depois, a
        // confirmação do critério da edição decide se ela vira resultado CIS.
        var first = entries[0].Control;
        var bench = KnightCisTrace.BenchmarkOf(first.Key)!;
        var group = entries.Count > 1 ? KnightCisEvidencePolicy.GroupOf(id) : null;
        var limitations = new List<string>();

        string status;
        string? reason = null;
        var considered = evals.Where(e => e.Result != KnightCisStatus.OutOfScope).ToList();
        if (considered.Count == 0)
        {
            status = KnightCisStatus.OutOfScope;
            reason = evals[0].Reason;
        }
        else if (entries.Count > 1 && group is null)
        {
            status = KnightCisStatus.NotEvaluated;
            reason = "A recomendação tem variantes no catálogo sem decisão registrada sobre como se combinam; o AEGIS não conclui.";
        }
        else if (group?.Kind == KnightCisVariantKind.IdentificationConflict)
        {
            status = KnightCisStatus.NotEvaluated;
            reason = group.Rationale;
        }
        else if (considered.Any(e => e.Result == KnightCisStatus.NonCompliant))
            status = KnightCisStatus.NonCompliant;
        else if (considered.Count < evals.Count)
        {
            status = KnightCisStatus.NotEvaluated;
            reason = "Parte da recomendação depende de fonte fora do escopo: " + evals.First(e => e.Result == KnightCisStatus.OutOfScope).Reason;
        }
        else if (considered.All(e => e.Result is KnightCisStatus.Compliant or KnightCisStatus.NotApplicable) && considered.Any(e => e.Result == KnightCisStatus.Compliant))
        {
            if (group?.Kind == KnightCisVariantKind.NotExhaustive)
            {
                status = KnightCisStatus.NotEvaluated;
                reason = group.Rationale;
            }
            else status = KnightCisStatus.Compliant;
        }
        else if (considered.All(e => e.Result == KnightCisStatus.NotApplicable))
            status = KnightCisStatus.NotApplicable;
        else
        {
            status = KnightCisStatus.NotEvaluated;
            reason = string.Join(" ", considered.Where(e => e.Result == KnightCisStatus.NotEvaluated).Select(e => e.Reason).OfType<string>().Distinct());
        }
        if (string.IsNullOrWhiteSpace(reason)) reason = null;

        var deciding = status switch
        {
            KnightCisStatus.NonCompliant => considered.Where(e => e.Result == KnightCisStatus.NonCompliant).ToList(),
            KnightCisStatus.Compliant or KnightCisStatus.NotApplicable => considered,
            _ => new List<EntryEval>(),
        };
        var origins = deciding.Select(e => e.Origin).Where(o => o != "None").Distinct().ToList();
        var origin = origins.Count == 0 ? "None" : origins.Count == 1 ? origins[0] : "AutomatedAndManual";
        var mitigated = status == KnightCisStatus.NonCompliant && deciding.All(e => e.Mitigated) && deciding.Count > 0;
        var knightStatus = status;
        var knightReason = reason;
        var knightOrigin = origin;
        var knightMitigated = mitigated;

        // ---- Critério da edição: só ele transforma a leitura do KNIGHT em resultado CIS --------------------------------
        var trace = TraceText(bench, traces);
        confirmations.TryGetValue(id, out var conf);
        string? gate = null;
        if (knightStatus != KnightCisStatus.OutOfScope)
        {
            if (conf is null)
                gate = $"{KnightCisCriteria.NotConfirmedReason}: a seção {first.Section ?? "s/n"} de {bench.Framework} {bench.Version} não foi conferida "
                    + "numa fonte oficial do CIS. A identificação vem do rastreio e o vínculo, do catálogo KNIGHT; o resultado do KNIGHT fica ao lado "
                    + "e não vale como resultado CIS.";
            else if (knightStatus is KnightCisStatus.Compliant or KnightCisStatus.NonCompliant or KnightCisStatus.NotApplicable
                     && knightOrigin is "Automated" or "AutomatedAndManual")
            {
                var used = deciding.SelectMany(e => e.Rules)
                    .Where(r => r.Rule is not null && (r.Proof is Proof.Compliant or Proof.Violation or Proof.NotApplicable || r.RawPassed))
                    .GroupBy(r => r.IndicatorId, StringComparer.Ordinal).Select(g => g.First()).ToList();
                var notReviewed = used.Where(r => !conf.ReviewedRules.Contains(r.IndicatorId, StringComparer.Ordinal)).Select(r => r.IndicatorId).ToList();
                var otherVersion = used.Select(r => r.Source?.CatalogVersion ?? "não registrado")
                    .Where(v => !conf.ReviewedCatalogVersions.Contains(v, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
                if (notReviewed.Count > 0)
                    gate = $"O critério da edição foi conferido em {conf.OfficialSource}, mas o vínculo de {string.Join(", ", notReviewed)} não foi "
                        + "revisado contra ele; o resultado do KNIGHT fica ao lado e não vale como resultado CIS.";
                else if (otherVersion.Count > 0)
                    gate = $"O vínculo foi revisado contra o critério da edição para o catálogo {string.Join(", ", conf.ReviewedCatalogVersions)}; "
                        + $"a avaliação usou {string.Join(", ", otherVersion)}.";
            }
        }
        if (gate is not null)
        {
            status = KnightCisStatus.NotEvaluated;
            reason = gate;
            origin = "None";
            mitigated = false;
        }
        var criterion = new KnightCisCriterion(
            conf is null ? KnightCisCriterionState.NotConfirmed : KnightCisCriterionState.Confirmed,
            KnightCisCriterionState.Label(conf is null ? KnightCisCriterionState.NotConfirmed : KnightCisCriterionState.Confirmed),
            $"{bench.Framework} {bench.Version}", first.Section ?? "s/n", trace, conf?.OfficialSource, conf?.Location, conf?.ConsultedOn,
            conf?.ReviewedBy, conf?.ReviewedCatalogVersions ?? Array.Empty<string>(), conf?.ReviewedRules ?? Array.Empty<string>(), conf?.Note);

        // Perfil: o mesmo em todas as variantes, ou a limitação dita.
        var states = traces.Select(t => t?.ProfileState ?? KnightCisProfileState.NotRecorded).Distinct().ToList();
        KnightCisProfileState profileState;
        IReadOnlyList<string> profiles = Array.Empty<string>();
        if (states.Any(s => s is KnightCisProfileState.Divergent or KnightCisProfileState.Ambiguous))
            profileState = states.First(s => s is KnightCisProfileState.Divergent or KnightCisProfileState.Ambiguous);
        else if (states.All(s => s == KnightCisProfileState.Recorded))
        {
            var sets = traces.Select(t => string.Join("|", t!.Profiles)).Distinct().ToList();
            if (sets.Count == 1) { profileState = KnightCisProfileState.Recorded; profiles = traces[0]!.Profiles; }
            else profileState = KnightCisProfileState.Divergent;
        }
        else profileState = KnightCisProfileState.NotRecorded;
        if (profileState != KnightCisProfileState.Recorded) limitations.Add(KnightCisTrace.ProfileStateLabel(profileState) + ".");

        var ruleEvals = evals.SelectMany(e => e.Rules).GroupBy(r => r.IndicatorId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var ruleIds = ruleEvals.Select(r => r.IndicatorId).ToList();
        foreach (var n in evals.SelectMany(e => e.Rules).Where(r => r.Match == KnightReferenceMatch.Partial && r.Note is not null).Select(r => $"{r.IndicatorId}: {r.Note}").Distinct())
            limitations.Add("Equivalência parcial — " + n);
        if (group is not null && group.Kind != KnightCisVariantKind.IdentificationConflict) limitations.Add("Variantes — " + group.Rationale);
        foreach (var l in evals.Select(e => e.Limitation).OfType<string>().Distinct()) limitations.Add(l);
        foreach (var r in ruleEvals.Where(r => r.Rule?.DetailLimitation is not null)) limitations.Add($"{r.IndicatorId}: {r.Rule!.DetailLimitation}");

        var rulesWithResult = ruleEvals.Where(r => r.Rule is not null).ToList();
        var manual = evals.Select(e => e.Manual).FirstOrDefault(m => m is not null);

        var what = rulesWithResult.Count > 0 || ruleIds.Count > 0
            ? $"{first.Title}. Verificado pelas regras do KNIGHT {string.Join(", ", ruleIds)}"
              + (evals.Any(e => e.Rules.Any(r => r.Match == KnightReferenceMatch.Partial)) ? ", com equivalência parcial explicada nas limitações." : ".")
            : $"{first.Title}. Sem regra automatizada: {KnightReferenceCatalog.DispositionLabel(entries[0].Disposition).ToLowerInvariant()}.";

        string found = knightStatus switch
        {
            KnightCisStatus.NonCompliant => manual is not null && knightOrigin == "Manual"
                ? $"Verificação manual registrada como não conforme por {manual.ResponsibleName}: {manual.Justification}"
                : string.Join(" ", rulesWithResult.Where(r => r.Proof == Proof.Violation).Select(r => $"{r.IndicatorId} reprovou: {r.Rule!.Evidence}")),
            KnightCisStatus.Compliant => manual is not null && knightOrigin == "Manual"
                ? $"Verificação manual registrada como conforme por {manual.ResponsibleName}: {manual.Justification}"
                : string.Join(" ", rulesWithResult.Where(r => r.RawPassed).Select(r => $"{r.IndicatorId} aprovou: {r.Rule!.Evidence}"))
                  + (knightOrigin == "AutomatedAndManual" && manual is not null ? $" Complemento manual por {manual.ResponsibleName}: {manual.Justification}" : ""),
            KnightCisStatus.NotApplicable => manual is not null && knightOrigin == "Manual"
                ? $"Verificação manual registrada como não aplicável por {manual.ResponsibleName}: {manual.Justification}"
                : string.Join(" ", rulesWithResult.Select(r => $"{r.IndicatorId}: {r.Rule!.Evidence}")),
            // Sem conclusão do KNIGHT: a evidência de cada regra continua visível antes do motivo.
            _ => string.Join(" ", rulesWithResult
                    .Where(r => r.Rule!.Status is KnightIndicatorStatus.Passed or KnightIndicatorStatus.Exposed or KnightIndicatorStatus.Mitigated or KnightIndicatorStatus.NotApplicable)
                    .Select(r => $"{r.IndicatorId} {Verb(r.Rule!.Status)}: {r.Rule!.Evidence.Trim().TrimEnd('.')}.")
                    .Append(knightReason ?? "Sem evidência suficiente para concluir.")),
        };
        if (string.IsNullOrWhiteSpace(found)) found = knightReason ?? "—";

        var why = rulesWithResult.Select(r => r.Rule!).Select(r => Join(r.Rationale, r.Impact)).FirstOrDefault(x => x is not null);
        string? how = knightStatus switch
        {
            KnightCisStatus.NonCompliant => knightOrigin == "Manual"
                ? "Corrija conforme a verificação manual registrada e registre uma nova verificação com a evidência."
                : Join(rulesWithResult.Where(r => r.Proof == Proof.Violation).Select(r => r.Rule!.Recommendation).OfType<string>().Distinct().ToArray()),
            KnightCisStatus.NotEvaluated => HowToEvaluate(evals, entries[0]),
            _ => null,
        };
        var links = rulesWithResult.SelectMany(r => r.Rule!.Documentation)
            .GroupBy(l => l.Url, StringComparer.Ordinal).Select(g => g.First()).Take(6).ToList();

        var variants = entries.Zip(evals).Select(x => new KnightCisVariant(
            x.First.Control.Key, x.First.Control.Variant, x.First.Control.Title, x.Second.Result, KnightCisStatus.Label(x.Second.Result),
            x.Second.Reason, x.First.Disposition.ToString(), KnightReferenceCatalog.DispositionLabel(x.First.Disposition),
            // A nota de uma referência avaliada é a do vínculo, já dita nas limitações; aqui fica só o motivo de quem não tem regra.
            x.First.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial ? null : x.First.Note,
            KnightCisTrace.For(x.First.Control.Key)?.Rule,
            x.Second.Rules.Select(r => new KnightCisRuleRef(r.IndicatorId, r.Match.ToString(),
                r.Match == KnightReferenceMatch.Exact ? "Integral" : "Parcial", r.Note, ProofKey(r.Proof), ProofLabel(r.Proof))).ToList())).ToList();

        var service = KnightServices.Describe(first.Service);
        return new KnightCisRecommendation(
            id, bench.Key, first.Section ?? "s/n", first.Title, first.Service.ToString(), service?.Label ?? first.Service.ToString(),
            service is null ? "" : KnightServices.PlatformLabel(service.Platform),
            first.Severity.ToString(), SeverityLabel(first.Severity),
            profileState.ToString(), KnightCisTrace.ProfileStateLabel(profileState), profiles, profiles.Select(KnightCisTrace.ProfileLabel).ToList(),
            status, KnightCisStatus.Label(status), origin, KnightCisStatus.OriginLabel(origin), mitigated, reason,
            what, found, why, how, links, limitations.Distinct().ToList(), variants, ruleIds, manual,
            new KnightCisReading(knightStatus, KnightReadingLabel(knightStatus), knightOrigin, KnightCisStatus.OriginLabel(knightOrigin), knightMitigated, knightReason),
            criterion, MappingOriginText(entries, evals, trace));
    }

    /// <summary>Rótulo do resultado TÉCNICO: o do KNIGHT, para não ser lido como resultado CIS.</summary>
    public static string KnightReadingLabel(string status) => status switch
    {
        KnightCisStatus.Compliant => "Aprovado pelo KNIGHT",
        KnightCisStatus.NonCompliant => "Reprovado pelo KNIGHT",
        KnightCisStatus.NotApplicable => "Não aplicável pelo KNIGHT",
        KnightCisStatus.OutOfScope => "Fora do escopo",
        _ => "Sem conclusão do KNIGHT",
    };

    private static string Verb(KnightIndicatorStatus s) => s switch
    {
        KnightIndicatorStatus.Passed => "aprovou",
        KnightIndicatorStatus.Exposed => "reprovou",
        KnightIndicatorStatus.Mitigated => "reprovou com mitigação",
        _ => "não se aplica",
    };

    private static string TraceText(KnightCisBenchmarkDescriptor bench, IReadOnlyList<KnightCisTraceEntry?> traces)
    {
        var rules = traces.Select(t => t?.Rule).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return $"Monkey365 · {bench.Ruleset}" + (rules.Count > 0 ? " · " + string.Join(", ", rules) : "") + $" (commit {Short(KnightCisTrace.Commit)}) — rastreio, não texto oficial";
    }

    /// <summary>De onde vem o vínculo regra → recomendação, dito perto do resultado.</summary>
    private static string MappingOriginText(IReadOnlyList<KnightReferenceStatus> entries, IReadOnlyList<EntryEval> evals, string trace)
    {
        var links = evals.SelectMany(e => e.Rules).GroupBy(r => r.IndicatorId, StringComparer.Ordinal).Select(g => g.First())
            .Select(r => $"{r.IndicatorId} ({(r.Match == KnightReferenceMatch.Exact ? "integral" : "parcial")})").ToList();
        var versions = evals.SelectMany(e => e.Rules).Select(r => r.Source?.CatalogVersion).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var reviewed = string.Join(", ", KnightCisEvidencePolicy.MappingCatalogVersions);
        var head = links.Count == 0
            ? $"Sem vínculo de regra no catálogo KNIGHT: {KnightReferenceCatalog.DispositionLabel(entries[0].Disposition).ToLowerInvariant()}."
            : $"Vínculo do catálogo KNIGHT: {string.Join(", ", links)}"
              + (versions.Count > 0 ? $"; avaliação executada com {string.Join(", ", versions)}" : "")
              + $"; vínculos revisados para {reviewed}.";
        return head + $" Identificação: {trace}.";
    }

    private static string? HowToEvaluate(IReadOnlyList<EntryEval> evals, KnightReferenceStatus st)
    {
        if (KnightManualResults.Eligible(st) && st.IndicatorIds.Count == 0)
            return "Faça a verificação no portal indicado e registre o resultado manual (resultado, justificativa, responsável, evidência e validade) "
                + "em Cobertura e verificação manual.";
        if (evals.SelectMany(e => e.Rules).Any(r => r.Proof == Proof.Unmapped))
            return "Sincronize novamente a fonte para obter uma avaliação com o catálogo atual; a visão CIS só projeta vínculos revisados para a versão executada.";
        if (evals.SelectMany(e => e.Rules).Any(r => r.Proof is Proof.NoResult))
            return "Conclua uma avaliação da fonte em Configurações → Integrações e inclua-a na composição.";
        if (evals.SelectMany(e => e.Rules).Any(r => r.Rule?.Status is KnightIndicatorStatus.NotEvaluated or KnightIndicatorStatus.Error))
            return "Resolva a limitação de coleta indicada no motivo (permissão, licença ou leitura) e sincronize novamente a fonte.";
        return null;
    }

    private static KnightCisRule RuleOf(KnightCisSourceInput s, KnightCisRuleInput r, IReadOnlyList<string> recs) => new(
        r.IndicatorId, r.Title, s.Source.ToString(), s.Label, s.RunId, s.CatalogVersion, r.Status.ToString(), StatusLabel(r.Status), r.Evidence,
        r.NotEvaluatedReason, r.AffectedCount, r.AffectedComposition, r.EvidenceCount, r.DetailLimitation, r.ExpectedConfiguration, r.Criterion,
        r.Recommendation, r.Documentation, recs);

    private static KnightCisManual ManualOf(KnightManualResultView v, bool complement) => new(
        v.Result.ToString(), v.ResultLabel, v.Justification, v.ResponsibleName, v.EvidenceReference, v.EvidenceDocumentTitle,
        v.EvidenceDocumentSha256, v.ValidUntil?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), v.Expired,
        v.RecordedByName, v.RecordedAt, complement);

    /// <summary>Contagem pela leitura técnica do KNIGHT (antes da confirmação do critério da edição).</summary>
    public static KnightCisCounts KnightCountsOf(IEnumerable<KnightCisRecommendation> recs)
    {
        var list = recs.ToList();
        int N(string s) => list.Count(r => (r.Knight?.Status ?? r.Status) == s);
        return new KnightCisCounts(N(KnightCisStatus.Compliant), N(KnightCisStatus.NonCompliant), N(KnightCisStatus.NotEvaluated),
            N(KnightCisStatus.NotApplicable), N(KnightCisStatus.OutOfScope));
    }

    public static KnightCisCounts CountsOf(IEnumerable<KnightCisRecommendation> recs)
    {
        var list = recs.ToList();
        int N(string s) => list.Count(r => r.Status == s);
        return new KnightCisCounts(N(KnightCisStatus.Compliant), N(KnightCisStatus.NonCompliant), N(KnightCisStatus.NotEvaluated),
            N(KnightCisStatus.NotApplicable), N(KnightCisStatus.OutOfScope));
    }

    // ---- Auxiliares ---------------------------------------------------------------------------------------------

    /// <summary>"CIS-AZ-6.0.0:7.3#dns" → "CIS-AZ-6.0.0:7.3": a recomendação que a variante verifica.</summary>
    public static string RecommendationId(string referenceKey)
    {
        var hash = referenceKey.IndexOf('#');
        return hash < 0 ? referenceKey : referenceKey[..hash];
    }

    /// <summary>Fonte da composição onde a configuração de um serviço é coletada (para "Fora do escopo" sem regra).</summary>
    public static KnightSourceType? SourceOfService(KnightService service) => service switch
    {
        KnightService.EntraId or KnightService.Forms => KnightSourceType.MicrosoftEntraId,
        KnightService.ExchangeOnline => KnightSourceType.MicrosoftExchangeOnline,
        KnightService.DefenderForOffice365 => KnightSourceType.MicrosoftDefenderForOffice365,
        KnightService.Purview => KnightSourceType.MicrosoftPurview,
        KnightService.Intune => KnightSourceType.MicrosoftIntune,
        KnightService.SharePointOnline => KnightSourceType.MicrosoftSharePoint,
        KnightService.Teams => KnightSourceType.MicrosoftTeams,
        KnightService.Fabric => KnightSourceType.MicrosoftFabric,
        _ when KnightServices.Describe(service)?.Platform == KnightPlatform.Azure => KnightSourceType.MicrosoftAzure,
        _ => null,
    };

    private static string Reason(KnightCisRuleInput r)
    {
        var why = string.IsNullOrWhiteSpace(r.NotEvaluatedReason) ? r.Evidence : r.NotEvaluatedReason!;
        var prefix = r.Status == KnightIndicatorStatus.Error ? "erro na avaliação" : "não avaliada";
        return $"{r.IndicatorId} {prefix}: {why.Trim().TrimEnd('.')}.";
    }

    private static string? Join(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();
        return p.Count == 0 ? null : string.Join(" ", p);
    }

    private static int BenchmarkOrder(string key)
    {
        for (var i = 0; i < KnightCisTrace.Benchmarks.Count; i++)
            if (KnightCisTrace.Benchmarks[i].Key == key) return i;
        return 99;
    }

    private static int ProfileOrder(string tag) => tag switch
    {
        "E3 Level 1" => 0, "E3 Level 2" => 1, "E5 Level 1" => 2, "E5 Level 2" => 3, "Level 1" => 4, "Level 2" => 5, _ => 9,
    };

    /// <summary>Ordem natural de seção ("2.10" depois de "2.9"): cada parte numérica com zeros à esquerda.</summary>
    public static string SectionSortKey(string section) =>
        string.Join(".", section.Split('.').Select(p => int.TryParse(p, out var n) ? n.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) : p));

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

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

    private static string SeverityLabel(SeverityLevel s) => s switch
    {
        SeverityLevel.Critical => "Crítico",
        SeverityLevel.High => "Alto",
        SeverityLevel.Medium => "Médio",
        SeverityLevel.Low => "Baixo",
        SeverityLevel.Informational => "Informativo",
        _ => s.ToString(),
    };
}
