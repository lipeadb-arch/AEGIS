using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] ABRANGÊNCIA de uma revisão de acesso
// ============================================================================
// Encontrar uma revisão de acesso não comprova o critério. Três perguntas diferentes precisam de resposta, e a
// terceira é a que costuma ser pulada:
//   1. a revisão EXISTE para aquela população?
//   2. ela atende ao CRITÉRIO (recorrência, vigência, revisores, remoção do acesso ao aplicar)?
//   3. o escopo dela ALCANÇA toda a população exigida?
// O escopo é declarado em consultas (`scope`, `principalScopes`, `resourceScopes`) e, para convidados em todos os
// grupos do Microsoft 365, na enumeração de instâncias (`instanceEnumerationScope`) — que a documentação declara
// necessária justamente para dizer QUAIS grupos entram na revisão. Uma revisão de um grupo e uma revisão de todos
// os grupos são indistinguíveis se só se procurar "userType eq 'Guest'" nas consultas.
//
// Regras de leitura, iguais às do resto do KNIGHT:
//   • o que a fonte não informou NÃO é completado por suposição (intervalo ausente não vira 1);
//   • o que não pode ser interpretado com segurança vira LIMITAÇÃO declarada — nunca aprovação;
//   • uma reprovação demonstrável (escopo comprovadamente menor, série encerrada, sem revisores) é preservada.
// Fontes: documentação da versão estável (v1.0) do Microsoft Graph — accessReviewScheduleDefinition,
// accessReviewStageSettings, recurrencePattern, recurrenceRange e "Configure the scope of your access review
// definition using the Microsoft Graph API".

/// <summary>Quanto da população exigida a revisão comprovadamente alcança.</summary>
public enum KnightAccessReviewReach
{
    /// <summary>A revisão não é dessa população.</summary>
    NotTargeted = 0,

    /// <summary>O escopo alcança toda a população exigida.</summary>
    Full = 1,

    /// <summary>O escopo alcança comprovadamente só parte da população.</summary>
    Limited = 2,

    /// <summary>O escopo não pôde ser interpretado com segurança.</summary>
    Undetermined = 3,
}

/// <summary>
/// O alcance do escopo, a frase curta que o explica na lista e — só para a expansão e as exportações — as
/// consultas que sustentam a leitura.
/// </summary>
public sealed record KnightAccessReviewScope(KnightAccessReviewReach Reach, string Description, string? Queries = null);

/// <summary>Se a revisão atende ao critério, falha de forma demonstrável ou não pôde ser interpretada.</summary>
public enum KnightAccessReviewCriteria
{
    Meets = 0,
    Fails = 1,
    Undetermined = 2,
}

/// <summary>O desfecho do critério e o motivo específico.</summary>
public sealed record KnightAccessReviewVerdict(KnightAccessReviewCriteria State, string Reason);

public static class EntraAccessReviewCoverage
{
    /// <summary>Consulta documentada que enumera todos os grupos do Microsoft 365 do locatário.</summary>
    private const string AllUnifiedGroupsFilter = "(grouptypes/any(c:c eq 'unified'))";

    // ---- Escopo: convidados ---------------------------------------------------------------------------

    /// <summary>
    /// O que a revisão alcança da população de CONVIDADOS. A revisão de convidados em todos os grupos do
    /// Microsoft 365 usa consulta relativa ("./members/...") e enumera os grupos em instanceEnumerationScope.
    /// </summary>
    public static KnightAccessReviewScope Guests(EntraAccessReviewDefinition d)
    {
        var scope = Scoping(d).ToList();
        var guests = scope.Where(q => Norm(q.Query).Contains("usertype eq 'guest'")).ToList();
        if (guests.Count == 0) return new(KnightAccessReviewReach.NotTargeted, "a revisão não tem escopo em convidados");
        if (d.RoleDefinitionIds.Count > 0 || scope.Any(q => Norm(q.Query).Contains("/roledefinitions/") || Norm(q.Query).Contains("roledefinitionid eq")))
            return new(KnightAccessReviewReach.NotTargeted, "a revisão é de atribuições de papel do diretório, não do acesso dos convidados");

        var specific = scope.Where(q => SpecificResource(q.Query)).ToList();
        if (specific.Count > 0)
            return new(KnightAccessReviewReach.Limited,
                $"o escopo alcança {Groups(specific.Count)}, não todos os convidados do locatário", Describe(specific));

        if (guests.Any(q => Norm(q.Query).StartsWith("./", StringComparison.Ordinal)))
        {
            var enumeration = d.Queries.Where(q => q.Origin == EntraAccessReviewScopeQuery.OriginInstanceEnumeration).ToList();
            if (enumeration.Count == 0)
                return new(KnightAccessReviewReach.Undetermined,
                    "a revisão usa escopo relativo (\"./members/…\") e a fonte não devolveu instanceEnumerationScope: não há como saber quais grupos entram na revisão",
                    Describe(guests));
            if (enumeration.All(q => AllUnifiedGroups(q.Query)))
                return new(KnightAccessReviewReach.Full, "os convidados de todos os grupos do Microsoft 365 do locatário", Describe(enumeration));
            if (enumeration.All(q => Norm(q.Query).StartsWith("/groups", StringComparison.Ordinal)))
                return new(KnightAccessReviewReach.Limited,
                    "a enumeração de instâncias seleciona um subconjunto dos grupos", Describe(enumeration));
            return new(KnightAccessReviewReach.Undetermined,
                "a enumeração de instâncias não pôde ser interpretada", Describe(enumeration));
        }

        if (guests.Any(q => Norm(q.Query).StartsWith("/users", StringComparison.Ordinal) || Norm(q.Query).StartsWith("/v1.0/users", StringComparison.Ordinal)))
            return new(KnightAccessReviewReach.Full, "todos os convidados do diretório", Describe(guests));

        return new(KnightAccessReviewReach.Undetermined, "o escopo da revisão não pôde ser interpretado", Describe(guests));
    }

    // ---- Escopo: atribuições de um papel --------------------------------------------------------------

    /// <summary>
    /// O que a revisão alcança das atribuições de UM papel do diretório. A consulta documentada que cobre as
    /// atribuições ativas E elegíveis é <c>/roleManagement/directory/roleDefinitions/{id}</c>; as consultas por
    /// <c>roleAssignmentScheduleInstances</c> ou <c>roleEligibilityScheduleInstances</c> filtram um subconjunto,
    /// e um escopo de principais com filtro restringe quem é revisado.
    /// </summary>
    public static KnightAccessReviewScope Role(EntraAccessReviewDefinition d, string roleTemplateId)
    {
        var role = roleTemplateId.ToLowerInvariant();
        var resources = d.Queries
            .Where(q => q.Origin is EntraAccessReviewScopeQuery.OriginScope or EntraAccessReviewScopeQuery.OriginResource)
            .ToList();
        var mentions = resources.Where(q => Norm(q.Query).Contains(role, StringComparison.Ordinal)).ToList();
        if (mentions.Count == 0) return new(KnightAccessReviewReach.NotTargeted, "a revisão não tem escopo neste papel");

        if (!mentions.Any(q => WholeRoleDefinition(q.Query, role)))
            return new(KnightAccessReviewReach.Limited,
                "a consulta alcança apenas parte das atribuições do papel", Describe(mentions));

        var principals = d.Queries.Where(q => q.Origin == EntraAccessReviewScopeQuery.OriginPrincipal).ToList();
        if (principals.Count == 0)
            return new(KnightAccessReviewReach.Full, "todas as atribuições do papel (ativas e elegíveis)", Describe(mentions));
        if (principals.All(q => AllUsers(q.Query)))
            return new(KnightAccessReviewReach.Full, "todas as atribuições de usuário do papel (ativas e elegíveis)", Describe(mentions.Concat(principals)));
        return new(KnightAccessReviewReach.Limited,
            "o escopo de principais restringe quem é revisado", Describe(principals));
    }

    // ---- Critério (recorrência, vigência, revisores, remoção ao aplicar) -------------------------------

    /// <summary>
    /// Se a revisão atende ao critério "ativa, recorrência mensal ou mais frequente, revisores definidos e
    /// remoção do acesso negado ao aplicar". <paramref name="now"/> é o instante da COLETA.
    /// </summary>
    public static KnightAccessReviewVerdict Criteria(EntraAccessReviewDefinition d, DateTimeOffset now)
    {
        if (d.Recurrence is not { } r)
            return new(KnightAccessReviewCriteria.Fails, "a revisão não é recorrente");
        if (string.IsNullOrWhiteSpace(r.PatternType))
            return new(KnightAccessReviewCriteria.Undetermined, "a fonte não informou o padrão de recorrência da série");
        if (r.Interval is not { } interval || interval <= 0)
            return new(KnightAccessReviewCriteria.Undetermined,
                "a fonte não informou o intervalo da recorrência (obrigatório no padrão documentado): a frequência não pode ser afirmada");
        if (PeriodDays(r.PatternType!, interval) is not { } period)
            return new(KnightAccessReviewCriteria.Undetermined,
                $"padrão de recorrência não interpretado pelo critério: {r.PatternType}");
        if (period > 31)
            return new(KnightAccessReviewCriteria.Fails,
                $"a recorrência é {Frequency(r.PatternType!, interval)}, menos frequente que mensal");

        if (Finished(d.Status))
            return new(KnightAccessReviewCriteria.Fails, $"a série está encerrada (estado {d.Status})");

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        switch (r.RangeType?.ToLowerInvariant())
        {
            case "enddate" when r.EndDate is { } end && end < today:
                return new(KnightAccessReviewCriteria.Fails, $"a série terminou em {end:dd/MM/yyyy}");
            case "numbered" when r.StartDate is { } start && r.NumberOfOccurrences is { } occurrences && occurrences > 0:
                var last = start.AddDays(period * occurrences);
                if (last < today) return new(KnightAccessReviewCriteria.Fails, $"as {occurrences} ocorrências da série terminaram em {last:dd/MM/yyyy}");
                break;
            case "numbered":
                return new(KnightAccessReviewCriteria.Undetermined,
                    "a série tem número fixo de ocorrências e a fonte não informou o início ou a quantidade: a vigência não pode ser afirmada");
            case null when d.Status is null:
                return new(KnightAccessReviewCriteria.Undetermined, "a fonte não informou o estado nem a vigência da série");
        }
        if (r.StartDate is { } begin && begin > today)
            return new(KnightAccessReviewCriteria.Fails, $"a série só começa em {begin:dd/MM/yyyy}");

        if (d.Stages.Count > 0)
        {
            var empty = d.Stages.Where(s => s.ReviewerCount == 0 && s.FallbackReviewerCount == 0).ToList();
            if (empty.Count > 0)
                return new(KnightAccessReviewCriteria.Fails,
                    $"{empty.Count} de {d.Stages.Count} etapa(s) não define(m) revisores, e a etapa substitui os revisores do nível superior: nela a revisão é feita por quem está sendo revisado");
        }
        else if (d.ReviewerCount == 0)
        {
            return new(KnightAccessReviewCriteria.Fails, "a revisão não tem revisores definidos");
        }

        if (!d.RemovesAccessWhenApplied)
            return new(KnightAccessReviewCriteria.Fails, "a revisão não remove o acesso negado ao aplicar as decisões");

        return new(KnightAccessReviewCriteria.Meets,
            $"recorrência {Frequency(d.Recurrence!.PatternType!, d.Recurrence.Interval!.Value)}, {Reviewers(d)} e remoção do acesso negado ao aplicar");
    }

    /// <summary>Resumo determinístico da revisão para a evidência.</summary>
    public static string Summary(EntraAccessReviewDefinition d)
    {
        var recurrence = d.Recurrence is { PatternType: not null, Interval: not null } r
            ? Frequency(r.PatternType!, r.Interval!.Value)
            : d.Recurrence?.PatternType is { } type ? $"{type} (intervalo não informado)" : "não recorrente";
        return $"Recorrência: {recurrence} · {Reviewers(d)} · remove acesso ao aplicar: {(d.RemovesAccessWhenApplied ? "sim" : "não")}"
            + $" · aplicação automática: {(d.AutoApplyDecisionsEnabled switch { true => "sim", false => "não", _ => "não informado pela fonte" })}"
            + $" · estado: {d.Status ?? "não informado"}";
    }

    /// <summary>Classificação grosseira do escopo, preservada no documento para leitura humana.</summary>
    public static string ClassifyKind(IReadOnlyList<EntraAccessReviewScopeQuery> queries, IReadOnlyList<string> roleDefinitionIds)
    {
        if (roleDefinitionIds.Count > 0) return EntraAccessReviewDefinition.ScopeDirectoryRole;
        return queries.Any(q => Norm(q.Query).Contains("usertype eq 'guest'"))
            ? EntraAccessReviewDefinition.ScopeGuests
            : EntraAccessReviewDefinition.ScopeOther;
    }

    // ---- Leitura das consultas ------------------------------------------------------------------------

    private static IEnumerable<EntraAccessReviewScopeQuery> Scoping(EntraAccessReviewDefinition d) =>
        d.Queries.Where(q => q.Origin is EntraAccessReviewScopeQuery.OriginScope
            or EntraAccessReviewScopeQuery.OriginPrincipal or EntraAccessReviewScopeQuery.OriginResource);

    /// <summary>Consulta normalizada: minúsculas, "+" e "%20" como espaço, espaços colapsados.</summary>
    private static string Norm(string? query)
    {
        var s = (query ?? "").Replace("%20", " ", StringComparison.Ordinal).Replace('+', ' ').ToLowerInvariant().Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s;
    }

    /// <summary>Um recurso NOMEADO na consulta (um grupo, uma equipe, uma aplicação) — o oposto de "todos".</summary>
    private static bool SpecificResource(string query)
    {
        var n = Norm(query);
        foreach (var prefix in new[] { "/groups/", "/teams/", "/v1.0/groups/", "/v1.0/teams/" })
        {
            var i = n.IndexOf(prefix, StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = n[(i + prefix.Length)..];
            var id = rest.Split('/', '?')[0];
            if (Guid.TryParse(id, out _)) return true;
        }
        return false;
    }

    /// <summary>A consulta documentada de enumeração de TODOS os grupos do Microsoft 365, sem outro filtro.</summary>
    private static bool AllUnifiedGroups(string query)
    {
        var n = Norm(query);
        if (!n.StartsWith("/groups?", StringComparison.Ordinal) && !n.StartsWith("/v1.0/groups?", StringComparison.Ordinal)) return false;
        var filter = FilterOf(n);
        return filter is not null && Parenthesized(filter) == AllUnifiedGroupsFilter;
    }

    private static bool WholeRoleDefinition(string query, string role)
    {
        var n = Norm(query).TrimEnd('/');
        if (FilterOf(n) is not null) return false;
        return n.EndsWith($"/rolemanagement/directory/roledefinitions/{role}", StringComparison.Ordinal);
    }

    private static bool AllUsers(string query)
    {
        var n = Norm(query).TrimEnd('/');
        return n is "/users" or "/v1.0/users";
    }

    private static string? FilterOf(string normalized)
    {
        var i = normalized.IndexOf("$filter=", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = normalized[(i + "$filter=".Length)..];
        var end = rest.IndexOf('&', StringComparison.Ordinal);
        return (end < 0 ? rest : rest[..end]).Trim();
    }

    private static string Parenthesized(string filter) =>
        filter.StartsWith('(') && filter.EndsWith(')') ? filter : "(" + filter + ")";

    private static string Groups(int n) => n == 1 ? "1 grupo ou equipe específico" : $"{N(n)} grupos ou equipes específicos";

    private static string Describe(IEnumerable<EntraAccessReviewScopeQuery> queries) =>
        string.Join("; ", queries.Select(q => $"{q.Origin}: {Shorten(q.Query)}"));

    private static string Shorten(string query) =>
        query.Length <= 120 ? query : query[..117] + "…";

    private static bool Finished(string? status) =>
        status is not null && (status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("AutoReviewed", StringComparison.OrdinalIgnoreCase));

    /// <summary>Dias entre ocorrências, pela semântica documentada do padrão (o mês conta como 31 dias).</summary>
    private static int? PeriodDays(string type, int interval) => type.ToLowerInvariant() switch
    {
        "daily" => interval,
        "weekly" => interval * 7,
        "absolutemonthly" or "relativemonthly" => interval * 31,
        "absoluteyearly" or "relativeyearly" => interval * 366,
        _ => null,
    };

    private static string Frequency(string type, int interval) => type.ToLowerInvariant() switch
    {
        "daily" => interval == 1 ? "diária" : $"a cada {N(interval)} dias",
        "weekly" => interval == 1 ? "semanal" : $"a cada {N(interval)} semanas",
        "absolutemonthly" or "relativemonthly" => interval == 1 ? "mensal" : $"a cada {N(interval)} meses",
        "absoluteyearly" or "relativeyearly" => interval == 1 ? "anual" : $"a cada {N(interval)} anos",
        _ => $"{type} a cada {N(interval)}",
    };

    private static string Reviewers(EntraAccessReviewDefinition d) =>
        d.Stages.Count > 0
            ? $"revisores definidos em {d.Stages.Count} etapa(s)"
            : $"{N(d.ReviewerCount)} revisor(es)";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
