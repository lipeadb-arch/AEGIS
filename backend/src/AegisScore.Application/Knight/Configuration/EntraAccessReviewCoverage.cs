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
        var guests = scope.Where(q => MentionsGuests(q.Query)).ToList();
        if (guests.Count == 0) return new(KnightAccessReviewReach.NotTargeted, "a revisão não tem escopo em convidados");
        if (d.RoleDefinitionIds.Count > 0 || scope.Any(q => Norm(q.Query).Contains("/roledefinitions/") || Norm(q.Query).Contains("roledefinitionid eq")))
            return new(KnightAccessReviewReach.NotTargeted, "a revisão é de atribuições de papel do diretório, não do acesso dos convidados");

        // A restrição a usuários INATIVOS não aparece na consulta: está no tipo do escopo e em inactiveDuration.
        if (Inactive(d) is { Count: > 0 } inactive)
            return new(KnightAccessReviewReach.Limited,
                $"o escopo alcança apenas os usuários inativos{Duration(inactive)}, não todos os convidados", Describe(inactive));

        var specific = scope.Where(q => SpecificResource(q.Query)).ToList();
        if (specific.Count > 0)
            return new(KnightAccessReviewReach.Limited,
                $"o escopo alcança {Groups(specific.Count)}, não todos os convidados do locatário", Describe(specific));

        if (guests.Any(q => RelativeGuestMembers(q.Query)))
        {
            var enumeration = d.Queries.Where(q => q.Origin == EntraAccessReviewScopeQuery.OriginInstanceEnumeration).ToList();
            if (enumeration.Count == 0)
                return new(KnightAccessReviewReach.Undetermined,
                    "a revisão usa escopo relativo (\"./members/…\") e a fonte não devolveu instanceEnumerationScope: não há como saber quais grupos entram na revisão",
                    Describe(guests));
            if (enumeration.All(q => AllUnifiedGroups(q.Query)))
                return new(KnightAccessReviewReach.Full, "os convidados de todos os grupos do Microsoft 365 do locatário", Describe(enumeration));
            if (enumeration.Any(q => SpecificResource(q.Query)))
                return new(KnightAccessReviewReach.Limited,
                    "a enumeração de instâncias nomeia grupos específicos", Describe(enumeration));
            if (enumeration.All(q => TeamsOnly(q.Query)))
                return new(KnightAccessReviewReach.Limited,
                    "a enumeração de instâncias alcança apenas os grupos associados a equipes", Describe(enumeration));
            return new(KnightAccessReviewReach.Undetermined,
                "a enumeração de instâncias não corresponde a nenhum formato documentado: não há como afirmar quais grupos entram na revisão",
                Describe(enumeration));
        }

        if (guests.Any(q => DirectoryGuests(q.Query)))
            return new(KnightAccessReviewReach.Full, "todos os convidados do diretório", Describe(guests));

        return new(KnightAccessReviewReach.Undetermined,
            "o escopo da revisão não corresponde a nenhum formato documentado: a população alcançada não pode ser afirmada",
            Describe(guests));
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

        // A mesma restrição por inatividade vale aqui: as consultas não a revelam.
        if (Inactive(d) is { Count: > 0 } inactive)
            return new(KnightAccessReviewReach.Limited,
                $"o escopo alcança apenas os principais inativos{Duration(inactive)}, não todas as atribuições do papel", Describe(inactive));

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
        if (FrequencyDays(r.PatternType!, interval) is not { } frequency)
            return new(KnightAccessReviewCriteria.Undetermined,
                $"padrão de recorrência não interpretado pelo critério: {r.PatternType}");
        if (frequency > 31)
            return new(KnightAccessReviewCriteria.Fails,
                $"a recorrência é {Frequency(r.PatternType!, interval)}, menos frequente que mensal");

        if (Finished(d.Status))
            return new(KnightAccessReviewCriteria.Fails, $"a série está encerrada (estado {d.Status})");

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (Validity(d, r, today) is { State: not KnightAccessReviewCriteria.Meets } validity) return validity;
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

    /// <summary>
    /// VIGÊNCIA da série, pela semântica documentada do <c>recurrenceRange</c>. A faixa só é afirmada encerrada
    /// quando os dados coletados permitem situar a última ocorrência no calendário E conhecer a duração das
    /// revisões; o que falta vira limitação, nunca aprovação por data estimada.
    /// </summary>
    private static KnightAccessReviewVerdict Validity(EntraAccessReviewDefinition d, EntraAccessReviewRecurrence r, DateOnly today)
    {
        const string ok = "vigência demonstrada";
        var duration = DurationInDays(d);
        switch (r.RangeType?.ToLowerInvariant())
        {
            case null when d.Status is null:
                return new(KnightAccessReviewCriteria.Undetermined, "a fonte não informou o estado nem a vigência da série");
            case null or "noend":
                return new(KnightAccessReviewCriteria.Meets, ok);

            case "enddate":
                if (r.EndDate is not { } end)
                    return new(KnightAccessReviewCriteria.Undetermined,
                        "a faixa da série é do tipo endDate e a fonte não informou a data final: a vigência não pode ser afirmada");
                if (end >= today) return new(KnightAccessReviewCriteria.Meets, ok);
                if (duration is not { } days)
                    return new(KnightAccessReviewCriteria.Undetermined,
                        $"a série deixou de gerar ocorrências em {end:dd/MM/yyyy} e a fonte não informou a duração das revisões: não há como demonstrar que a última terminou");

                // LIMITE SUPERIOR: nenhuma ocorrência pode COMEÇAR depois da data final, logo nenhuma pode terminar
                // depois dela mais a duração. Esse limite demonstra o ENCERRAMENTO — nunca a vigência.
                if (end.AddDays(days) < today)
                    return new(KnightAccessReviewCriteria.Fails,
                        $"a série terminou: nenhuma ocorrência podia começar depois de {end:dd/MM/yyyy} e cada revisão dura {days} dia(s)");

                // Daqui em diante o limite superior não decide nada: é preciso situar no calendário a última
                // ocorrência que o padrão permite DENTRO da faixa. A documentação avisa que ela pode não cair na
                // data final.
                var (kind, begun) = LastOccurrenceWithin(r, end);
                if (kind == RangeOccurrence.Unknown)
                    return new(KnightAccessReviewCriteria.Undetermined, MissingForRange(r, end));
                if (kind == RangeOccurrence.None)
                    return new(KnightAccessReviewCriteria.Fails,
                        $"a faixa da série terminou em {end:dd/MM/yyyy} sem permitir nenhuma ocorrência do padrão");

                var closed = begun.AddDays(days);
                if (closed < today)
                    return new(KnightAccessReviewCriteria.Fails,
                        $"a série terminou: a última ocorrência permitida pela faixa começou em {begun:dd/MM/yyyy} e acabou em {closed:dd/MM/yyyy}");

                // A data final impede NOVAS ocorrências; não encerra a instância que ainda corre. Para afirmar que
                // ela corre, o estado informado pela fonte precisa corroborar as datas.
                return Underway(d.Status)
                    ? new(KnightAccessReviewCriteria.Meets,
                        $"vigência demonstrada: a última ocorrência permitida começou em {begun:dd/MM/yyyy} e segue em andamento até {closed:dd/MM/yyyy}")
                    : new(KnightAccessReviewCriteria.Undetermined,
                        $"as datas põem a última ocorrência em andamento até {closed:dd/MM/yyyy}, mas o estado informado ({d.Status ?? "não informado"}) não confirma revisão em andamento: a contradição não é resolvida pelo que foi coletado");

            case "numbered":
                if (r.StartDate is null || r.NumberOfOccurrences is not { } occurrences || occurrences <= 0)
                    return new(KnightAccessReviewCriteria.Undetermined,
                        "a série tem número fixo de ocorrências e a fonte não informou o início ou a quantidade: a vigência não pode ser afirmada");
                if (LastOccurrence(r, occurrences) is not { } last)
                    return new(KnightAccessReviewCriteria.Undetermined, MissingForOccurrences(r));
                if (last >= today) return new(KnightAccessReviewCriteria.Meets, ok);
                if (duration is not { } span)
                    return new(KnightAccessReviewCriteria.Undetermined,
                        $"a última das {N(occurrences)} ocorrências começou em {last:dd/MM/yyyy} e a fonte não informou a duração das revisões: não há como demonstrar que ela terminou");
                return last.AddDays(span) < today
                    ? new(KnightAccessReviewCriteria.Fails,
                        $"a última das {N(occurrences)} ocorrências terminou em {last.AddDays(span):dd/MM/yyyy}")
                    : new(KnightAccessReviewCriteria.Meets, ok);

            default:
                return new(KnightAccessReviewCriteria.Undetermined, $"tipo de faixa da recorrência não interpretado: {r.RangeType}");
        }
    }

    /// <summary>
    /// Início da ÚLTIMA ocorrência, pelo calendário. Só é calculado nos padrões cujas propriedades coletadas
    /// bastam: diário e semanal (que, em revisões de acesso, usam só tipo e intervalo) e mensal absoluto com
    /// <c>dayOfMonth</c> — a documentação avisa que a primeira ocorrência pode ser posterior ao início da faixa.
    /// </summary>
    private static DateOnly? LastOccurrence(EntraAccessReviewRecurrence r, int occurrences) => Occurrence(r, occurrences - 1);

    /// <summary>Data da ocorrência de índice <paramref name="step"/> (0 = a primeira), pelo calendário.</summary>
    private static DateOnly? Occurrence(EntraAccessReviewRecurrence r, int step)
    {
        if (step < 0 || r.StartDate is not { } start || r.Interval is not { } interval || interval <= 0) return null;
        switch (r.PatternType?.ToLowerInvariant())
        {
            case "daily": return start.AddDays(interval * step);
            case "weekly": return start.AddDays(7 * interval * step);
            case "absolutemonthly":
                if (r.DayOfMonth is not { } day || day is < 1 or > 31) return null;
                return FirstMonthly(start, day)?.AddMonths(interval * step);
            default: return null;
        }
    }

    private enum RangeOccurrence { Found, None, Unknown }

    /// <summary>
    /// ÚLTIMA ocorrência que o padrão permite dentro de uma faixa terminada em <paramref name="end"/>. A data final
    /// impede novas ocorrências, mas a última pode ser bem anterior a ela: o cálculo usa o MESMO calendário das
    /// demais contas, e o índice estimado é conferido nas duas direções.
    /// </summary>
    private static (RangeOccurrence Kind, DateOnly Date) LastOccurrenceWithin(EntraAccessReviewRecurrence r, DateOnly end)
    {
        if (Occurrence(r, 0) is not { } first) return (RangeOccurrence.Unknown, default);
        if (first > end) return (RangeOccurrence.None, default);

        var step = Math.Max(0, EstimateSteps(r, first, end));
        while (step > 0 && Occurrence(r, step) > end) step--;
        while (Occurrence(r, step + 1) is { } next && next <= end) step++;
        return Occurrence(r, step) is { } last && last <= end ? (RangeOccurrence.Found, last) : (RangeOccurrence.Unknown, default);
    }

    /// <summary>Índice aproximado da última ocorrência até <paramref name="end"/>, conferido pelo chamador.</summary>
    private static int EstimateSteps(EntraAccessReviewRecurrence r, DateOnly first, DateOnly end)
    {
        var interval = r.Interval!.Value;
        return r.PatternType?.ToLowerInvariant() switch
        {
            "daily" => (end.DayNumber - first.DayNumber) / interval,
            "weekly" => (end.DayNumber - first.DayNumber) / (7 * interval),
            "absolutemonthly" => ((end.Year - first.Year) * 12 + end.Month - first.Month) / interval,
            _ => 0,
        };
    }

    private static string MissingForRange(EntraAccessReviewRecurrence r, DateOnly end) =>
        string.Equals(r.PatternType, "absoluteMonthly", StringComparison.OrdinalIgnoreCase)
            ? $"a faixa da série terminou em {end:dd/MM/yyyy} e a fonte não informou o dia do mês (dayOfMonth) do padrão: a última ocorrência permitida não pode ser situada, e a data final sozinha não demonstra a vigência"
            : $"a faixa da série terminou em {end:dd/MM/yyyy} e o padrão {r.PatternType} não permite situar a última ocorrência com o que a coleta traz";

    /// <summary>
    /// Estados documentados em que uma ocorrência está efetivamente correndo. A documentação lista os estados
    /// TÍPICOS, então o que não estiver nela não corrobora data nenhuma.
    /// </summary>
    private static bool Underway(string? status) =>
        status is not null && (status.Equals("InProgress", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Starting", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Completing", StringComparison.OrdinalIgnoreCase)
            || status.Equals("AutoReviewing", StringComparison.OrdinalIgnoreCase));

    private static string MissingForOccurrences(EntraAccessReviewRecurrence r) =>
        string.Equals(r.PatternType, "absoluteMonthly", StringComparison.OrdinalIgnoreCase)
            ? "a série tem número fixo de ocorrências e a fonte não informou o dia do mês (dayOfMonth) do padrão: a data da última ocorrência não pode ser demonstrada"
            : $"a série tem número fixo de ocorrências e o padrão {r.PatternType} não permite situar a última ocorrência com o que a coleta traz";

    /// <summary>Primeira ocorrência de um padrão mensal: o dia indicado, na faixa a partir do início.</summary>
    private static DateOnly? FirstMonthly(DateOnly start, int day)
    {
        if (DayInMonth(start.Year, start.Month, day) is { } inStartMonth && inStartMonth >= start) return inStartMonth;
        var next = start.AddMonths(1);
        return DayInMonth(next.Year, next.Month, day);
    }

    private static DateOnly? DayInMonth(int year, int month, int day) =>
        day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    /// <summary>
    /// Duração de cada revisão. Quando há etapas, a documentação diz que a soma das durações delas substitui
    /// <c>instanceDurationInDays</c>; etapa sem duração torna a soma desconhecida.
    /// </summary>
    private static int? DurationInDays(EntraAccessReviewDefinition d)
    {
        if (d.Stages.Count > 0)
            return d.Stages.All(s => s.DurationInDays is > 0) ? d.Stages.Sum(s => s.DurationInDays!.Value) : null;
        return d.InstanceDurationInDays is > 0 ? d.InstanceDurationInDays : null;
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

    /// <summary>
    /// Uma consulta separada em CAMINHO, FILTRO e as DEMAIS opções. Não é um interpretador de OData: serve para
    /// reconhecer estritamente os formatos documentados e recusar o resto. <c>$count</c> não restringe a
    /// população e por isso não entra em <see cref="Parsed.Others"/>; qualquer outra opção entra.
    /// </summary>
    private readonly record struct Parsed(string Path, string? Filter, IReadOnlyList<string> Others)
    {
        public bool OnlyFilter => Others.Count == 0;
    }

    private static Parsed Parse(string query)
    {
        var n = Norm(query);
        var mark = n.IndexOf('?', StringComparison.Ordinal);
        var path = (mark < 0 ? n : n[..mark]).TrimEnd('/');
        string? filter = null;
        var others = new List<string>();
        if (mark >= 0)
            foreach (var option in n[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (option.StartsWith("$filter=", StringComparison.Ordinal)) filter = option["$filter=".Length..].Trim();
                else if (!option.StartsWith("$count=", StringComparison.Ordinal)) others.Add(option);
            }
        return new(path, filter, others);
    }

    /// <summary>O filtro documentado que seleciona convidados, e nada além disso.</summary>
    private static bool GuestFilter(string? filter) =>
        filter is not null && Parenthesized(filter) == "(usertype eq 'guest')";

    private static bool MentionsGuests(string query) => Norm(query).Contains("usertype eq 'guest'", StringComparison.Ordinal);

    /// <summary>Exemplo 6: consulta RELATIVA aos membros convidados de cada grupo enumerado.</summary>
    private static bool RelativeGuestMembers(string query)
    {
        var p = Parse(query);
        return p.Path is "./members/microsoft.graph.user" && p.OnlyFilter && GuestFilter(p.Filter);
    }

    /// <summary>Consulta de TODOS os convidados do diretório, sem nenhum outro recorte.</summary>
    private static bool DirectoryGuests(string query)
    {
        var p = Parse(query);
        return p.Path is "/users" or "/v1.0/users" && p.OnlyFilter && GuestFilter(p.Filter);
    }

    /// <summary>Exemplos 5 e 6: enumeração de TODOS os grupos do Microsoft 365, sem outro recorte.</summary>
    private static bool AllUnifiedGroups(string query)
    {
        var p = Parse(query);
        return p.Path is "/groups" or "/v1.0/groups" && p.OnlyFilter
            && p.Filter is not null && Parenthesized(p.Filter) == AllUnifiedGroupsFilter;
    }

    /// <summary>Exemplo 8: enumeração restrita aos grupos associados a equipes (subconjunto documentado).</summary>
    private static bool TeamsOnly(string query)
    {
        var p = Parse(query);
        return p.Path is "/groups" or "/v1.0/groups" && p.OnlyFilter
            && p.Filter is { } f && f.Contains("grouptypes/any(c:c eq 'unified')", StringComparison.Ordinal)
            && f.Contains("resourceprovisioningoptions", StringComparison.Ordinal);
    }

    /// <summary>Exemplo 12.1: o papel inteiro — atribuições ativas e elegíveis, sem filtro nem outra opção.</summary>
    private static bool WholeRoleDefinition(string query, string role)
    {
        var p = Parse(query);
        return p.Filter is null && p.OnlyFilter
            && p.Path.EndsWith($"/rolemanagement/directory/roledefinitions/{role}", StringComparison.Ordinal);
    }

    private static bool AllUsers(string query)
    {
        var p = Parse(query);
        return p.Path is "/users" or "/v1.0/users" && p.Filter is null && p.OnlyFilter;
    }

    /// <summary>Consultas do escopo restritas a usuários INATIVOS (tipo do escopo + inactiveDuration).</summary>
    private static List<EntraAccessReviewScopeQuery> Inactive(EntraAccessReviewDefinition d) =>
        d.Queries.Where(q => q.RestrictedToInactiveUsers).ToList();

    private static string Duration(IEnumerable<EntraAccessReviewScopeQuery> queries) =>
        queries.Select(q => q.InactiveDuration).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } d
            ? $" (inactiveDuration {d})" : "";

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

    /// <summary>
    /// Dias entre ocorrências, só para COMPARAR frequências ("mensal ou mais frequente"): o mês conta como 31 dias,
    /// o limite superior de um mês do calendário. Não serve para calcular datas — a vigência usa o calendário.
    /// </summary>
    private static int? FrequencyDays(string type, int interval) => type.ToLowerInvariant() switch
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
