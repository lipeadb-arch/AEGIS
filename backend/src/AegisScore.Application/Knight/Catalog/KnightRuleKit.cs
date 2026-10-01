using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Kit de regras dos controles de configuração
// ============================================================================
// As regras que os blocos do Entra ID, do Teams e do Exchange firmaram, num lugar só para os catálogos novos:
//   • dado ausente (permissão, licença, falha, coleta anterior) → NÃO AVALIADO com o motivo — nunca aprovado;
//   • configuração da organização entra como EVIDÊNCIA, não como afetado contável;
//   • numa população, uma violação encontrada é violação; a APROVAÇÃO exige a população inteira e conhecida;
//   • valor não informado pela fonte não aprova nem reprova: impede a aprovação e é dito no achado.

/// <summary>Veredito de UM item de uma população: conforme, não conforme ou desconhecido (a fonte não informou).</summary>
public enum KnightItemVerdict
{
    Compliant,
    NonCompliant,
    Unknown,
}

/// <summary>Resultado da checagem de um item, com o texto do que foi encontrado e do que se espera.</summary>
public sealed record KnightItemCheck(KnightItemVerdict Verdict, string Found, string Expected)
{
    public static KnightItemCheck Of(bool? compliant, string found, string expected) =>
        new(compliant switch { true => KnightItemVerdict.Compliant, false => KnightItemVerdict.NonCompliant, _ => KnightItemVerdict.Unknown },
            found, expected);
}

public static class KnightRuleKit
{
    /// <summary>Fábrica de definição de controle de configuração de UMA fonte.</summary>
    public static KnightIndicatorDefinition Control(
        KnightSourceType source, KnightService service,
        string id, string title, KnightIndicatorCategory category, SeverityLevel severity,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate,
        params KnightReferenceLink[] references)
    {
        var label = KnightSourceCatalog.Label(source);
        KnightIndicatorOutcome FactsOnly(KnightFactSet _) =>
            new(KnightIndicatorStatus.NotEvaluated,
                $"Não avaliado: este controle lê a configuração coletada de {label}, indisponível nesta avaliação.", 0,
                $"Este controle lê a configuração coletada de {label}, indisponível nesta avaliação.");
        return new KnightIndicatorDefinition(id, "1", title, category, severity, new HashSet<KnightSourceType> { source },
            Array.Empty<string>(), Array.Empty<string>(), recommendation, criterion, FactsOnly)
        {
            Service = service,
            References = references,
            Evaluate = evaluate,
        };
    }

    public static KnightReferenceLink Ref(string key, KnightReferenceMatch match = KnightReferenceMatch.Exact, string? note = null) =>
        new(key, match, note);

    /// <summary>Configuração de instância ÚNICA: ausente ou não coletada vira motivo, não veredito.</summary>
    public static KnightControlOutcome Single<T>(KnightEvaluationContext c, Func<T, KnightControlOutcome> rule) where T : class
    {
        var read = c.Configuration.Read<T>();
        if (!read.Collected) return KnightControlOutcome.NotEvaluated(Trim(read.MissingReason ?? "configuração não coletada."));
        return read.Single is { } doc
            ? rule(doc)
            : KnightControlOutcome.NotEvaluated("a coleta concluiu sem devolver esta configuração.");
    }

    /// <summary>Lista coletada de um contrato (vazia e coletada é diferente de não coletada).</summary>
    public static KnightControlOutcome Many<T>(KnightEvaluationContext c, Func<IReadOnlyList<T>, KnightControlOutcome> rule) where T : class
    {
        var read = c.Configuration.Read<T>();
        return read.Collected ? rule(read.Items) : KnightControlOutcome.NotEvaluated(Trim(read.MissingReason ?? "configuração não coletada."));
    }

    /// <summary>
    /// Uma configuração da organização comparada ao esperado. A configuração é EVIDÊNCIA — nunca afetado contável.
    /// </summary>
    public static KnightControlOutcome Setting(
        bool? compliant, string settingId, string settingLabel, string found, string expected,
        string exposedEvidence, string passedEvidence)
    {
        var obj = KnightObjects.Setting(settingId, settingLabel, found, expected);
        return compliant switch
        {
            true => KnightControlOutcome.Passed(passedEvidence, new[] { obj }),
            false => KnightControlOutcome.Exposed(exposedEvidence, Array.Empty<KnightIndicatorObject>(), new[] { obj }),
            _ => KnightControlOutcome.NotEvaluated($"a fonte não informou “{settingLabel}” nesta coleta.", new[] { obj }),
        };
    }

    /// <summary>
    /// Veredito sobre uma POPULAÇÃO (políticas, domínios, recursos). Cada item é checado; os não conformes são os
    /// AFETADOS. Aprovar exige população completa, não vazia e sem item desconhecido; reprovar só exige um item não
    /// conforme demonstrado. População vazia devolve <paramref name="whenEmpty"/>.
    /// </summary>
    public static KnightControlOutcome Population<T>(
        IReadOnlyList<T> items,
        Func<T, KnightItemCheck> check,
        Func<T, KnightIndicatorObject> describe,
        Func<int, int, string> exposedEvidence,
        Func<int, string> passedEvidence,
        Func<KnightControlOutcome> whenEmpty,
        bool complete = true,
        string? incompleteReason = null,
        int maxEvidence = 50)
    {
        if (items.Count == 0) return whenEmpty();

        var offenders = new List<KnightIndicatorObject>();
        var unknown = new List<KnightIndicatorObject>();
        var compliant = new List<KnightIndicatorObject>();
        foreach (var item in items)
        {
            var result = check(item);
            var obj = describe(item);
            var detail = $"Encontrado: {result.Found}. Esperado: {result.Expected}.";
            obj = obj with { Detail = Trim(detail, 1000), ObservedConfiguration = Trim(result.Found, 2000) };
            switch (result.Verdict)
            {
                case KnightItemVerdict.NonCompliant: offenders.Add(obj with { Relation = KnightObjectRelation.Affected }); break;
                case KnightItemVerdict.Unknown: unknown.Add(obj with { Relation = KnightObjectRelation.Evidence }); break;
                default: compliant.Add(obj with { Relation = KnightObjectRelation.Evidence }); break;
            }
        }

        var limitation = string.Join(" ", new[]
        {
            complete ? null : incompleteReason ?? "A enumeração não cobre a população inteira.",
            unknown.Count == 0 ? null : $"{unknown.Count} item(ns) sem o valor informado pela fonte — não aprovam nem reprovam.",
        }.Where(s => s is not null));

        if (offenders.Count > 0)
            return KnightControlOutcome.Exposed(exposedEvidence(offenders.Count, items.Count), offenders,
                unknown.Concat(compliant).Take(maxEvidence).ToList(), complete && unknown.Count == 0,
                limitation.Length == 0 ? null : Trim(limitation, 900));

        if (!complete || unknown.Count > 0)
            return KnightControlOutcome.NotEvaluated(Trim(limitation.Length == 0 ? "população incompleta." : limitation),
                unknown.Concat(compliant).Take(maxEvidence).ToList());

        return KnightControlOutcome.Passed(passedEvidence(items.Count), compliant.Take(maxEvidence).ToList());
    }

    /// <summary>Objeto de política (evidência por padrão; a população o promove a afetado quando não conforme).</summary>
    public static KnightIndicatorObject PolicyObject(string id, string? name, string detail = "") =>
        KnightObjects.Evidence(KnightAffectedObjectKind.Policy, Trim(id, 200), Trim(name ?? id, 300), detail);

    /// <summary>Objeto de configuração do locatário.</summary>
    public static KnightIndicatorObject SettingObject(string id, string name, string detail = "") =>
        KnightObjects.Evidence(KnightAffectedObjectKind.TenantSetting, Trim(id, 200), Trim(name, 300), detail);

    /// <summary>Objeto de domínio.</summary>
    public static KnightIndicatorObject DomainObject(string domain, string detail = "") =>
        KnightObjects.Evidence(KnightAffectedObjectKind.Domain, Trim(domain, 200), Trim(domain, 300), detail);

    public static string YesNo(bool? v) => KnightObjects.YesNo(v);

    public static string Trim(string text, int max = 480) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    public static string List(IEnumerable<string> values, int max = 5)
    {
        var all = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (all.Count == 0) return "nenhum";
        var shown = string.Join(", ", all.Take(max));
        return all.Count > max ? $"{shown} e mais {all.Count - max}" : shown;
    }
}
