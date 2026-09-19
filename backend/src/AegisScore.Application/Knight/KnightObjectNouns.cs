using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] NOMES do que foi avaliado — definição única
// ============================================================================
// "75 objeto(s)" não diz o que foi encontrado. Quando o tipo é conhecido, o relatório nomeia: contas de usuário,
// convidados, aplicações, grupos, papéis, políticas, domínios. Quando a população mistura tipos, a composição real é
// mostrada ("75 identidades: 70 contas de usuário, 3 aplicações e 2 grupos") — uma aplicação nunca vira usuário e
// uma contagem de atribuições nunca vira contagem de contas. O modelo interno continua "objeto afetado"; isto é só a
// APRESENTAÇÃO, e a mesma função alimenta a API (tela), o HTML, o CSV e o PDF.

public static class KnightObjectNouns
{
    public static (string Singular, string Plural) Of(KnightAffectedObjectKind kind) => kind switch
    {
        KnightAffectedObjectKind.User => ("conta de usuário", "contas de usuário"),
        KnightAffectedObjectKind.Guest => ("conta de convidado", "contas de convidado"),
        KnightAffectedObjectKind.ServicePrincipal => ("aplicação", "aplicações"),
        KnightAffectedObjectKind.Group => ("grupo", "grupos"),
        KnightAffectedObjectKind.Device => ("dispositivo", "dispositivos"),
        KnightAffectedObjectKind.Policy => ("política", "políticas"),
        KnightAffectedObjectKind.DirectoryRole => ("papel de diretório", "papéis de diretório"),
        KnightAffectedObjectKind.TenantSetting => ("configuração do locatário", "configurações do locatário"),
        KnightAffectedObjectKind.Domain => ("domínio", "domínios"),
        _ => ("item de tipo não identificado", "itens de tipo não identificado"),
    };

    /// <summary>"1 conta de usuário", "3 aplicações".</summary>
    public static string Count(KnightAffectedObjectKind kind, int n)
    {
        var (s, p) = Of(kind);
        return $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? s : p)}";
    }

    /// <summary>Rótulo do tipo com inicial maiúscula (coluna "Tipo" das listas).</summary>
    public static string Label(KnightAffectedObjectKind kind)
    {
        var s = Of(kind).Singular;
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static bool IsIdentity(KnightAffectedObjectKind k) =>
        k is KnightAffectedObjectKind.User or KnightAffectedObjectKind.Guest or KnightAffectedObjectKind.ServicePrincipal
            or KnightAffectedObjectKind.Group or KnightAffectedObjectKind.Unknown;

    /// <summary>
    /// Composição REAL de uma lista: um tipo só → "3 contas de usuário"; vários → "75 identidades: 70 contas de
    /// usuário, 3 aplicações e 2 grupos" (ou "itens" quando a mistura não é só de identidades). Quando a contagem do
    /// veredito é maior que a lista preservada, o total vem da contagem e a composição é dita da lista preservada.
    /// </summary>
    public static string Composition(IEnumerable<KnightAffectedObjectKind> kinds, int? verdictCount = null)
    {
        var groups = kinds.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => (int)g.Key)
            .Select(g => (Kind: g.Key, N: g.Count())).ToList();
        var listed = groups.Sum(g => g.N);
        var total = verdictCount is { } v && v > listed ? v : listed;
        if (groups.Count == 0)
            return total == 0 ? "nenhum item" : $"{total.ToString(CultureInfo.InvariantCulture)} item(ns) sem detalhe preservado";

        var parts = groups.Select(g => Count(g.Kind, g.N)).ToList();
        var joined = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " e " + parts[^1];
        string head;
        if (groups.Count == 1)
            head = total == listed ? joined : $"{total.ToString(CultureInfo.InvariantCulture)} {Of(groups[0].Kind).Plural}";
        else
        {
            var noun = groups.All(g => IsIdentity(g.Kind)) ? "identidades" : "itens";
            head = $"{total.ToString(CultureInfo.InvariantCulture)} {noun}";
        }

        if (groups.Count == 1 && total == listed) return head;
        return total == listed ? $"{head}: {joined}" : $"{head} (lista preservada: {joined})";
    }
}
