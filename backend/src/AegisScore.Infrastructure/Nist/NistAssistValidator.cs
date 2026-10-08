using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AegisScore.Application.Nist;

namespace AegisScore.Infrastructure.Nist;

internal sealed record NistAssistValidatedItem(string Text, IReadOnlyList<string> Sources, string Basis);

internal sealed record NistAssistValidatedSection(string Key, IReadOnlyList<NistAssistValidatedItem> Items);

internal sealed record NistAssistValidatedLevel(int Level, string LevelName, IReadOnlyList<string> Sources, string Rationale);

internal sealed record NistAssistValidatedProcedure(string Method, string Text, IReadOnlyList<string> Sources);

internal sealed record NistAssistOutput(
    IReadOnlyList<NistAssistValidatedSection> Sections,
    NistAssistValidatedLevel? Level,
    string? LevelNote,
    IReadOnlyList<NistAssistValidatedProcedure> Procedures);

internal sealed record NistAssistValidation(NistAssistOutput Output, IReadOnlyDictionary<string, string> Applicable, IReadOnlyList<string> Notes);

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Validação SEMÂNTICA da resposta, no servidor, contra as fontes do contexto montado — escritor puro
/// (sem banco nem relógio). O que a pessoa recebe como conteúdo vem daqui:
/// <list type="bullet">
/// <item>citação só de chave existente NESTE contexto (inventada, de outro contexto ou de tipo não admitido na seção é descartada);</item>
/// <item>afirmação sobre o ambiente sem fonte válida é descartada — nunca chega como conclusão verificada;</item>
/// <item>documento não examinado não sustenta afirmação; a classificação do item é a da fonte mais fraca citada;</item>
/// <item>nível: inteiro da escala e sustentado por fonte CONFIRMADA; inválido ou sem base é descartado, nunca ajustado aos limites;</item>
/// <item>links que não vieram das fontes são removidos; prioridades seguem a ordem determinística do AEGIS.</item>
/// </list>
/// </summary>
internal static class NistAssistValidator
{
    private const int MaxItems = 8;
    private const int MaxItemText = 1200;
    private static readonly Regex Url = new(@"\b(?:https?|ftp|file)://\S+|\bwww\.[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IntegerLevel = new(@"^\s*""?\s*(\d+)\s*""?\s*$", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"\d+(?:[.,]\d+)?\s?%|\d+[.,]\d+", RegexOptions.Compiled);

    public static NistAssistValidation Validate(NistAssistDraft draft, NistAssistBuilt built, string kind, string? focus)
    {
        var registry = built.Registry.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var specs = NistAssistSections.For(kind, focus);
        var notes = new List<string>();
        int invalidRefs = 0, unsupported = 0, referenceOnly = 0, urls = 0;

        string Clean(string? raw, int max)
        {
            var t = (raw ?? "").Trim();
            var removed = Url.Matches(t).Count;
            if (removed > 0)
            {
                urls += removed;
                t = Url.Replace(t, "[link removido]");
            }
            return t.Length <= max ? t : t[..max].TrimEnd() + "…";
        }

        List<string> ValidKeys(IReadOnlyList<string> raw, NistAssistSectionSpec? spec)
        {
            var keys = raw.Select(k => (k ?? "").Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var valid = keys.Where(k => registry.TryGetValue(k, out var r)
                                        && (spec is null || spec.SourcePrefixes.Contains(k[..1], StringComparer.Ordinal))
                                        && (spec?.SourceKinds is null || spec.SourceKinds.Contains(r.Kind, StringComparer.Ordinal))).ToList();
            invalidRefs += keys.Count - valid.Count;
            return valid;
        }

        var sections = new List<NistAssistValidatedSection>();
        foreach (var spec in specs)
        {
            var items = new List<NistAssistValidatedItem>();
            if (draft.Sections.TryGetValue(spec.Key, out var raw))
                foreach (var item in raw.Take(MaxItems))
                {
                    var text = Clean(item.Text, MaxItemText);
                    if (text.Length == 0) continue;
                    var valid = ValidKeys(item.Sources, spec);
                    if (spec.RequiresSources && !valid.Any(k => registry[k].Basis != NistAssistBasis.NotExamined))
                    {
                        unsupported++;
                        continue;
                    }
                    // A referência ao catálogo explica o requisito; não demonstra nada sobre o ambiente do tenant.
                    if (spec.RequiresEnvironmentEvidence && !valid.Any(k => IsTenantRecord(registry[k])))
                    {
                        referenceOnly++;
                        continue;
                    }
                    items.Add(new NistAssistValidatedItem(text, valid, Derive(valid.Select(k => registry[k]))));
                }

            // Prioridades: uma por chave P, na ordem determinística do AEGIS (a IA não reordena nem cria prioridade).
            if (spec.Key == "priorities")
                items = items
                    .Select(i => (Item: i, P: i.Sources.Where(k => k.StartsWith('P')).Select(k => int.Parse(k[1..], CultureInfo.InvariantCulture)).DefaultIfEmpty(int.MaxValue).Min()))
                    .Where(x => x.P != int.MaxValue)
                    .GroupBy(x => x.P).Select(g => g.First())
                    .OrderBy(x => x.P).Select(x => x.Item).ToList();
            sections.Add(new NistAssistValidatedSection(spec.Key, items));
        }

        // ---- Procedimentos (só subcategoria) ----
        var procedures = new List<NistAssistValidatedProcedure>();
        if (kind == "Subcategory")
            foreach (var p in draft.Procedures.Take(6))
            {
                var method = MethodOf(p.Method);
                var text = Clean(p.Text, 1000);
                if (method is null || text.Length < 10) continue;
                procedures.Add(new NistAssistValidatedProcedure(method, text, ValidKeys(p.Sources, null)));
            }

        // ---- Nível (só subcategoria): válido na escala e sustentado por fonte confirmada; nunca ajustado aos limites ----
        NistAssistValidatedLevel? level = null;
        string? levelNote = null;
        if (kind == "Subcategory")
        {
            if (draft.Level is not { } lv)
                levelNote = "Sem sugestão de nível: a IA não indicou base suficiente nas fontes.";
            else if (built.NotApplicable)
                levelNote = "A subcategoria foi declarada não aplicável ao escopo: sugestão de nível descartada.";
            else if (lv.RawValue is null || IntegerLevel.Match(lv.RawValue) is not { Success: true } m
                     || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                     || !built.LevelNames.ContainsKey(value))
            {
                levelNote = $"A IA devolveu um nível fora da escala {Scale(built)} (\"{Cut(lv.RawValue ?? "vazio", 20)}\"): descartado, sem ajuste.";
                notes.Add(levelNote);
            }
            else
            {
                var valid = ValidKeys(lv.Sources, null);
                var confirmed = valid.Where(k => registry[k].Basis is NistAssistBasis.Fact or NistAssistBasis.AnalystReport).ToList();
                if (confirmed.Count == 0)
                {
                    levelNote = "Sugestão de nível descartada: nenhuma fonte confirmada (fato sustentado ou relato confirmado do analista) a sustenta.";
                    notes.Add(levelNote);
                }
                else
                {
                    level = new NistAssistValidatedLevel(value, built.LevelNames[value], valid, Clean(lv.Rationale, 1500));
                }
            }
        }

        if (invalidRefs > 0) notes.Add($"{invalidRefs} citação(ões) a fontes inexistentes, de outro contexto ou não admitidas na seção foram descartadas.");
        if (unsupported > 0) notes.Add($"{unsupported} afirmação(ões) sobre o ambiente sem fonte válida foram descartadas.");
        if (referenceOnly > 0)
            notes.Add($"{referenceOnly} afirmação(ões) sobre o ambiente apoiadas só no catálogo ou na metodologia foram descartadas: a referência explica o requisito, não comprova o ambiente.");
        if (urls > 0) notes.Add($"{urls} link(s) que não vieram das fontes foram removidos do texto.");

        if (kind == "ExecutiveSummary")
        {
            var allowed = new HashSet<string>(built.Registry.Where(r => r.Kind == "Metric")
                .SelectMany(r => Numbers.Matches(r.Detail ?? "").Select(x => Norm(x.Value))), StringComparer.Ordinal);
            var strange = sections.SelectMany(s => s.Items).SelectMany(i => Numbers.Matches(i.Text).Select(x => x.Value))
                .Where(v => !allowed.Contains(Norm(v))).Distinct().Take(5).ToList();
            if (strange.Count > 0)
                notes.Add($"O texto menciona valor(es) que não constam dos indicadores do AEGIS ({string.Join(", ", strange)}): confira antes de aceitar.");
        }

        if (sections.All(s => s.Items.Count == 0) && procedures.Count == 0 && level is null)
            throw new NistAiUnavailableException(
                "A resposta da IA não trouxe conteúdo aproveitável depois da validação das fontes. Nada foi gravado; a jornada segue manual.",
                "InvalidResponse");

        var output = new NistAssistOutput(sections, level, levelNote, procedures);
        return new NistAssistValidation(output, Applicable(output, built, kind, focus), notes);
    }

    /// <summary>Registro do tenant (fato, relato do analista ou conteúdo ainda não confirmado) — o que pode sustentar afirmação sobre o ambiente.</summary>
    internal static bool IsTenantRecord(NistAssistSourceRecord r) =>
        r.Basis is NistAssistBasis.Fact or NistAssistBasis.AnalystReport or NistAssistBasis.Unconfirmed;

    /// <summary>A classificação do item é a da fonte MAIS FRACA citada sobre o ambiente; só referência → referência.</summary>
    internal static string Derive(IEnumerable<NistAssistSourceRecord> sources)
    {
        var list = sources.ToList();
        if (list.Count == 0) return NistAssistBasis.General;
        var env = list.Where(s => s.Basis is NistAssistBasis.Fact or NistAssistBasis.AnalystReport or NistAssistBasis.Unconfirmed or NistAssistBasis.NotExamined).ToList();
        if (env.Count == 0) return NistAssistBasis.Reference;
        if (env.Any(s => s.Basis == NistAssistBasis.NotExamined)) return NistAssistBasis.NotExamined;
        if (env.Any(s => s.Basis == NistAssistBasis.Unconfirmed)) return NistAssistBasis.Unconfirmed;
        return env.Any(s => s.Basis == NistAssistBasis.AnalystReport) ? NistAssistBasis.AnalystReport : NistAssistBasis.Fact;
    }

    /// <summary>Texto que cada campo receberia ao ser aplicado ao rascunho — a base para saber, na gravação, se a pessoa editou.</summary>
    private static Dictionary<string, string> Applicable(NistAssistOutput o, NistAssistBuilt built, string kind, string? focus)
    {
        var registry = built.Registry.ToDictionary(r => r.Key, StringComparer.Ordinal);
        IReadOnlyList<NistAssistValidatedItem> S(string key) => o.Sections.FirstOrDefault(s => s.Key == key)?.Items ?? Array.Empty<NistAssistValidatedItem>();
        string Lines(IEnumerable<NistAssistValidatedItem> items, bool withSources) => string.Join("\n", items.Select(i =>
            "• " + i.Text + (withSources && i.Sources.Count > 0
                ? " (fontes: " + string.Join("; ", i.Sources.Select(k => registry[k].Title)) + ")"
                : "")));
        string Numbered(IEnumerable<NistAssistValidatedItem> items) => string.Join("\n", items.Select((i, n) => $"{n + 1}. {i.Text}"));
        static string Fit(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string text, int max)
        {
            if (!string.IsNullOrWhiteSpace(text)) map[key] = Fit(text.Trim(), max);
        }

        switch (kind)
        {
            case "Subcategory":
                Put("rationale", Lines(S("justification"), true), 4000);
                Put("gaps", Lines(S("unproven"), true), 4000);
                Put("riskImpact", Lines(S("risks"), true), 2000);
                var guidance = Lines(S("recommendations"), false);
                if (S("criteria").Count > 0) guidance += (guidance.Length > 0 ? "\n" : "") + "Critérios para avaliar a melhoria:\n" + Lines(S("criteria"), false);
                Put("improvementGuidance", guidance, 4000);
                if (o.Level is { } lv) map["currentLevel"] = lv.Level.ToString(CultureInfo.InvariantCulture);
                for (var i = 0; i < o.Procedures.Count; i++) map[$"procedure:{i}"] = o.Procedures[i].Text;
                break;
            case "Finding" when NistAssistSections.NormalizeFocus(focus) == NistAssistSections.FocusTreatment:
                var sb = new StringBuilder(Lines(S("treatment"), false));
                if (S("steps").Count > 0) sb.Append("\nEtapas:\n").Append(Numbered(S("steps")));
                if (S("criteria").Count > 0) sb.Append("\nCritérios e evidências para validar a correção:\n").Append(Lines(S("criteria"), false));
                if (S("confirmations").Count > 0) sb.Append("\nConfirmar tecnicamente antes de executar:\n").Append(Lines(S("confirmations"), false));
                Put("recommendation", sb.ToString(), 4000);
                Put("proposedAction", sb.ToString(), 2000);
                break;
            case "ExecutiveSummary":
                foreach (var s in o.Sections.Where(s => s.Items.Count > 0))
                    Put(s.Key, s.Key == "priorities" ? Numbered(s.Items) : Lines(s.Items, false), 2000);
                break;
        }
        return map;
    }

    internal static string? MethodOf(string? method) => (method ?? "").Trim().ToLowerInvariant() switch
    {
        "examine" or "examinar" => "Examine",
        "interview" or "entrevistar" => "Interview",
        "test" or "testar" => "Test",
        _ => null,
    };

    private static string Scale(NistAssistBuilt built) =>
        built.LevelNames.Count == 0 ? "1–5" : $"{built.LevelNames.Keys.Min()}–{built.LevelNames.Keys.Max()}";

    private static string Norm(string v) => v.Replace(" ", "").Replace(',', '.');

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
