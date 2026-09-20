using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-MULTICLOUD-01] Leitura DETERMINÍSTICA das políticas de acesso condicional
// ============================================================================
// Antes, cada política era reduzida a duas flags, e a MFA administrativa só era reconhecida numa política que
// mirasse TODOS os usuários, TODAS as aplicações e não tivesse NENHUMA exclusão. Isso reprovava o desenho que
// a própria Microsoft recomenda (política dirigida aos papéis administrativos, com as contas de emergência
// excluídas) e, no sentido oposto, ignorava que uma política "somente relatório" não impõe nada.
//
// A leitura agora distingue, política a política:
//   • ESTADO — habilitada, desabilitada, somente relatório (só a primeira impõe);
//   • ALVOS — todos os usuários, papéis (por id de modelo), grupos, usuários nominais, e as exclusões de cada;
//   • APLICAÇÕES — todas, todas com exceções, ou um subconjunto;
//   • CONDIÇÕES que estreitam a exigência — localização, plataforma, risco, filtro de dispositivo, clientes;
//   • CONCESSÃO — MFA ou força de autenticação EXIGIDA, ou oferecida como ALTERNATIVA num "OU".
//
// Três coisas são mantidas SEPARADAS: a política EXISTIR, o ALVO que ela declara e a COBERTURA que os dados
// comprovam. A cobertura é lida MEMBRO A MEMBRO — é assim que o acesso condicional se aplica: uma política que
// inclui um papel vale para quem DETÉM o papel, e uma exclusão (por usuário, por papel, por grupo, de
// convidados) prevalece sobre qualquer inclusão. Para cada membro privilegiado conhecido:
//   • COBERTO — alguma política que impõe o alcança sem exclusão que o deixe de fora;
//   • NÃO RESOLVIDO — o alcance depende de GRUPO ou da condição de convidado, que esta entrega não coleta;
//   • EXCEÇÃO — as políticas que o alcançariam o excluem EXPLICITAMENTE, e nenhuma outra o cobre;
//   • NÃO ALCANÇADO — nenhuma política que impõe o tem como alvo.
//
// Uma exclusão explícita NÃO é irregular por si (é o padrão para contas de emergência), mas também NÃO comprova
// proteção nem controle compensatório — e a designação de contas de emergência não é inferível por API. Por
// isso ela nunca vira cobertura: um papel com exceções não é aprovado; fica sem conclusão, com as contas
// nomeadas. Já um membro NÃO ALCANÇADO, ou um papel em que NENHUM membro é coberto (todos excluídos, ou o
// próprio papel excluído), é lacuna comprovada. Outra política que comprovadamente cubra a exceção preserva a
// aprovação.
//
// A exigência por POLÍTICA não comprova que cada autenticação aplicou o segundo fator — isso seria
// autenticação observada, que esta entrega não coleta.

/// <summary>Leitura de UMA política: o que ela exige, de quem, onde e se de fato impõe.</summary>
public sealed record ConditionalAccessPolicyReading(
    ConditionalAccessPolicyConfiguration Policy,
    bool IsEnforced,
    bool RequiresMfa,
    bool MfaIsAlternative,
    bool Blocks,
    bool TargetsAllUsers,
    bool CoversAllApplications,
    IReadOnlyList<string> Narrowing,
    string Summary)
{
    /// <summary>A política declara alguma exclusão de usuários, grupos, papéis ou convidados.</summary>
    public bool HasExclusions =>
        Policy.ExcludeUsers.Count > 0 || Policy.ExcludeGroups.Count > 0 || Policy.ExcludeRoles.Count > 0
        || Policy.ExcludesGuestsOrExternalUsers;
}

/// <summary>Situação da exigência de MFA para UM papel privilegiado.</summary>
public enum RoleMfaCoverageState
{
    /// <summary>Todos os membros conhecidos são alcançados por política habilitada que exige MFA, sem exclusão.</summary>
    Covered = 0,

    /// <summary>
    /// Lacuna COMPROVADA: há membro que nenhuma política que impõe alcança, ou nenhum membro é coberto (todos
    /// excluídos, ou o próprio papel excluído).
    /// </summary>
    NotCovered = 1,

    /// <summary>A cobertura de algum membro depende de grupo/condição de convidado — pertencimento não coletado.</summary>
    Unresolved = 2,

    /// <summary>
    /// Há membros cobertos, e os demais estão EXCLUÍDOS explicitamente sem outra política que os cubra. Não é
    /// cobertura: a exceção não é irregular por si, mas também não comprova proteção.
    /// </summary>
    CoveredWithExceptions = 3,
}

/// <summary>Situação da exigência de MFA para UM membro privilegiado (usuário) de um papel.</summary>
public enum MemberMfaCoverageState
{
    Covered = 0,
    Excepted = 1,
    Unresolved = 2,
    NotTargeted = 3,
}

/// <summary>
/// Um membro e as políticas que decidem sua situação: as que o COBREM (<see cref="MemberMfaCoverageState.Covered"/>),
/// as que o EXCLUEM (<see cref="MemberMfaCoverageState.Excepted"/>) ou as que dependem de pertencimento não
/// coletado (<see cref="MemberMfaCoverageState.Unresolved"/>).
/// </summary>
public sealed record MemberMfaCoverage(string MemberId, MemberMfaCoverageState State, IReadOnlyList<string> PolicyIds);

/// <summary>Como UM papel privilegiado está coberto — membro a membro, com as políticas e as exceções.</summary>
public sealed record RoleMfaCoverage(
    DirectoryRoleConfiguration Role,
    RoleMfaCoverageState State,
    IReadOnlyList<string> CoveringPolicyIds,
    IReadOnlyList<MemberMfaCoverage> Members,
    IReadOnlyList<string> Notes)
{
    /// <summary>Membros excluídos explicitamente, sem outra política que os cubra.</summary>
    public IReadOnlyList<string> ExceptionMemberIds => IdsIn(MemberMfaCoverageState.Excepted);

    /// <summary>Membros que nenhuma política que impõe tem como alvo.</summary>
    public IReadOnlyList<string> UncoveredMemberIds => IdsIn(MemberMfaCoverageState.NotTargeted);

    /// <summary>Membros cuja cobertura depende de pertencimento não coletado.</summary>
    public IReadOnlyList<string> UnresolvedMemberIds => IdsIn(MemberMfaCoverageState.Unresolved);

    private IReadOnlyList<string> IdsIn(MemberMfaCoverageState state) =>
        Members.Where(m => m.State == state).Select(m => m.MemberId).ToList();
}

/// <summary>Conclusão sobre a exigência de MFA administrativa.</summary>
/// <param name="Evaluable">FALSE quando os dados não permitem afirmar nem negar (motivo em <paramref name="InconclusiveReason"/>).</param>
/// <param name="UncoveredRoleCount">Papéis com lacuna comprovada — o número que o indicador conta.</param>
/// <param name="Roles">Cobertura papel a papel (vazio quando o inventário de papéis não foi coletado).</param>
/// <param name="UniversalPolicyIds">Políticas que exigem MFA de TODOS os usuários, sem nenhuma exclusão.</param>
public sealed record AdminMfaConclusion(
    bool Evaluable,
    string? InconclusiveReason,
    int UncoveredRoleCount,
    IReadOnlyList<RoleMfaCoverage> Roles,
    IReadOnlyList<string> UniversalPolicyIds);

/// <summary>Conclusão sobre o bloqueio de autenticação legada.</summary>
/// <param name="Blocked">Bloqueio COMPROVADO para todos os usuários e aplicações (exceções cobertas por outra política).</param>
/// <param name="BlockingPolicyIds">Políticas que sustentam o bloqueio comprovado.</param>
/// <param name="PartialPolicyIds">Políticas que tocam a autenticação legada mas não contam (estado, alvo, aplicação, condição).</param>
/// <param name="ExceptionPolicyIds">Políticas que bloqueiam para todos os usuários, mas com exclusões que nenhuma outra política cobre.</param>
/// <param name="InconclusiveReason">Preenchido quando só há bloqueio com exceções: não se afirma nem se nega o bloqueio para todos.</param>
public sealed record LegacyAuthConclusion(
    bool Blocked,
    IReadOnlyList<string> BlockingPolicyIds,
    IReadOnlyList<string> PartialPolicyIds,
    IReadOnlyList<string> ExceptionPolicyIds,
    string? InconclusiveReason);

/// <summary>Conclusão sobre a base mínima de exigência de MFA para o ambiente.</summary>
/// <param name="BaselinePolicyIds">Políticas habilitadas que exigem MFA com ALVO DECLARADO em todos os usuários, em todas as aplicações, sem condição que as estreite e sem exclusão de grupo.</param>
/// <param name="UnresolvedPolicyIds">Políticas que seriam base mínima, mas cujo alcance depende de grupo(s) não coletado(s).</param>
/// <param name="RestrictedPolicyIds">Políticas habilitadas que exigem MFA com alcance restrito (usuários, papéis, grupos, aplicações ou condições).</param>
/// <param name="InconclusiveReason">Preenchido quando não há base comprovada e o alcance da candidata depende de grupo.</param>
public sealed record MfaBaselineConclusion(
    IReadOnlyList<string> BaselinePolicyIds,
    IReadOnlyList<string> UnresolvedPolicyIds,
    IReadOnlyList<string> RestrictedPolicyIds,
    string? InconclusiveReason);

/// <summary>Resultado completo da leitura das políticas de uma coleta.</summary>
public sealed record ConditionalAccessAnalysis(
    IReadOnlyList<ConditionalAccessPolicyReading> Policies,
    AdminMfaConclusion AdminMfa,
    LegacyAuthConclusion LegacyAuth,
    MfaBaselineConclusion Baseline)
{
    public ConditionalAccessPolicyReading? Find(string policyId) =>
        Policies.FirstOrDefault(p => string.Equals(p.Policy.Id, policyId, StringComparison.Ordinal));
}

public static class ConditionalAccessAnalyzer
{
    /// <summary>Versão da REGRA de leitura — muda quando a interpretação das políticas muda.</summary>
    public const string RuleVersion = "aegis-ca-analysis-v1";

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private const string GuestsValue = "GuestsOrExternalUsers";
    private const string RestrictedClients = "tipos de cliente restritos";

    /// <summary>
    /// Lê as políticas da coleta. Devolve <c>null</c> quando as políticas NÃO foram coletadas — ausência de
    /// dado nunca vira "nenhuma política".
    /// </summary>
    public static ConditionalAccessAnalysis? Analyze(KnightDirectoryConfiguration? configuration)
    {
        var policies = configuration?.ConditionalAccessPolicies;
        if (policies is null) return null;

        var readings = policies
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(Read)
            .ToList();

        return new ConditionalAccessAnalysis(
            readings,
            ConcludeAdminMfa(readings, configuration!.PrivilegedRoles),
            ConcludeLegacy(readings, configuration.PrivilegedRoles),
            ConcludeBaseline(readings));
    }

    // ---- Leitura de uma política ---------------------------------------------------------------------

    public static ConditionalAccessPolicyReading Read(ConditionalAccessPolicyConfiguration p)
    {
        var controls = p.BuiltInControls.Select(c => c.ToLowerInvariant()).ToList();
        var hasMfa = controls.Contains("mfa") || !string.IsNullOrWhiteSpace(p.AuthenticationStrengthId);
        var blocks = controls.Contains("block");

        // Quantas opções de concessão existem: num "OU", MFA é só uma alternativa se houver outras.
        var options = controls.Count(c => c != "block") + (string.IsNullOrWhiteSpace(p.AuthenticationStrengthId) ? 0 : 1);
        var isOr = string.Equals(p.GrantOperator, "OR", StringComparison.OrdinalIgnoreCase);
        var alternative = hasMfa && isOr && options > 1;
        var requiresMfa = hasMfa && !alternative;

        var allUsers = p.IncludeUsers.Contains("All", Ci);
        var allApps = p.IncludeApplications.Contains("All", Ci) && p.ExcludeApplications.Count == 0;

        var narrowing = new List<string>();
        if (p.HasLocationCondition) narrowing.Add("condição de localização");
        if (p.HasPlatformCondition) narrowing.Add("condição de plataforma");
        if (p.HasSignInRiskCondition) narrowing.Add("condição de risco de entrada");
        if (p.HasUserRiskCondition) narrowing.Add("condição de risco do usuário");
        if (p.HasDeviceFilter) narrowing.Add("filtro de dispositivo");
        if (NarrowsModernClients(p.ClientAppTypes)) narrowing.Add(RestrictedClients);

        return new ConditionalAccessPolicyReading(
            p, p.State == ConditionalAccessPolicyState.Enabled, requiresMfa, alternative, blocks,
            allUsers, allApps, narrowing, Describe(p, requiresMfa, alternative, blocks, narrowing));
    }

    /// <summary>
    /// Clientes modernos (navegador + aplicativos) fora do alcance estreitam a exigência. "all" ou lista vazia
    /// = todos os clientes. Clientes legados não fazem MFA de qualquer forma e têm indicador próprio.
    /// </summary>
    private static bool NarrowsModernClients(IReadOnlyList<string> clientAppTypes)
    {
        if (AllClientTypes(clientAppTypes)) return false;
        return !(clientAppTypes.Contains("browser", Ci) && clientAppTypes.Contains("mobileAppsAndDesktopClients", Ci));
    }

    /// <summary>"all" ou lista vazia (o padrão da fonte quando a condição não é configurada) = todos os tipos de cliente.</summary>
    private static bool AllClientTypes(IReadOnlyList<string> clientAppTypes) =>
        clientAppTypes.Count == 0 || clientAppTypes.Contains("all", Ci);

    /// <summary>A política alcança os DOIS canais legados: Exchange ActiveSync e "outros clientes" — ou todos os tipos.</summary>
    public static bool CoversAllLegacyClients(ConditionalAccessPolicyConfiguration p) =>
        AllClientTypes(p.ClientAppTypes)
        || (p.ClientAppTypes.Contains("exchangeActiveSync", Ci) && p.ClientAppTypes.Contains("other", Ci));

    /// <summary>A política alcança ao menos um canal legado (inclusive por abranger todos os tipos de cliente).</summary>
    public static bool TouchesLegacy(ConditionalAccessPolicyConfiguration p) =>
        AllClientTypes(p.ClientAppTypes)
        || p.ClientAppTypes.Contains("exchangeActiveSync", Ci) || p.ClientAppTypes.Contains("other", Ci)
        || p.ClientAppTypes.Contains("easSupported", Ci);

    internal static bool ExcludesGuests(ConditionalAccessPolicyConfiguration p) =>
        p.ExcludesGuestsOrExternalUsers || p.ExcludeUsers.Contains(GuestsValue, Ci);

    internal static bool IncludesGuests(ConditionalAccessPolicyConfiguration p) =>
        p.IncludesGuestsOrExternalUsers || p.IncludeUsers.Contains(GuestsValue, Ci);

    // ---- Alcance de uma política sobre UM usuário conhecido ------------------------------------------

    internal enum Reach { Applies, Excluded, NotTargeted, Unknown }

    /// <summary>
    /// A política vale para o usuário? Exclusões prevalecem sobre inclusões. <paramref name="userRoles"/> são os
    /// modelos dos papéis ATIVOS que o usuário detém (do inventário da mesma coleta). Grupo e condição de
    /// convidado não são resolvidos nesta entrega → <see cref="Reach.Unknown"/>, nunca suposição.
    /// <see cref="Reach.Excluded"/> só é devolvido quando a política o teria como alvo.
    /// </summary>
    internal static Reach ReachOf(ConditionalAccessPolicyReading r, string userId, IReadOnlyCollection<string> userRoles)
    {
        var p = r.Policy;
        var targeted = r.TargetsAllUsers
            || p.IncludeUsers.Contains(userId, Ci)
            || p.IncludeRoles.Any(role => userRoles.Contains(role, Ci));
        var maybeTargeted = !targeted && (p.IncludeGroups.Count > 0 || IncludesGuests(p));

        var excluded = p.ExcludeUsers.Contains(userId, Ci) || p.ExcludeRoles.Any(role => userRoles.Contains(role, Ci));
        if (excluded) return targeted ? Reach.Excluded : Reach.NotTargeted;
        if (!targeted && !maybeTargeted) return Reach.NotTargeted;

        var maybeExcluded = p.ExcludeGroups.Count > 0 || ExcludesGuests(p);
        return targeted && !maybeExcluded ? Reach.Applies : Reach.Unknown;
    }

    // ---- MFA administrativa -------------------------------------------------------------------------

    /// <summary>
    /// Só uma política HABILITADA, que EXIGE MFA, em TODAS as aplicações e sem condição que a estreite, conta
    /// como exigência. Somente relatório, desabilitada, "MFA ou outra coisa", aplicações parciais e condições de
    /// localização/risco são registradas como notas — nunca como cobertura.
    /// </summary>
    private static bool IsFullMfa(ConditionalAccessPolicyReading r) =>
        r.IsEnforced && r.RequiresMfa && r.CoversAllApplications && r.Narrowing.Count == 0;

    private static AdminMfaConclusion ConcludeAdminMfa(
        IReadOnlyList<ConditionalAccessPolicyReading> readings, IReadOnlyList<DirectoryRoleConfiguration>? roles)
    {
        var full = readings.Where(IsFullMfa).ToList();

        var universal = full.Where(r => r.TargetsAllUsers && !r.HasExclusions).Select(r => r.Policy.Id).ToList();

        if (roles is null)
        {
            // Sem inventário de papéis não dá para dizer QUAIS papéis estão cobertos. Só uma política universal
            // SEM NENHUMA exclusão alcança todos eles por definição — com exclusão, o excluído pode ser um deles.
            if (universal.Count > 0)
                return new AdminMfaConclusion(true, null, 0, Array.Empty<RoleMfaCoverage>(), universal);
            var withExclusions = full.Any(r => r.TargetsAllUsers);
            return new AdminMfaConclusion(false,
                withExclusions
                    ? "O inventário de papéis privilegiados não foi coletado e a política que exige MFA de todos os "
                      + "usuários declara exclusões — sem o inventário não é possível saber se algum administrador está entre os excluídos."
                    : "O inventário de papéis privilegiados não foi coletado e nenhuma política exige MFA de todos os "
                      + "usuários — não é possível dizer quais papéis estão cobertos.",
                0, Array.Empty<RoleMfaCoverage>(), universal);
        }

        // Papéis que cada usuário DETÉM — a inclusão/exclusão por papel vale para quem tem o papel.
        var rolesOf = roles
            .SelectMany(role => role.UserMemberIds.Select(m => (m, role.TemplateId)))
            .GroupBy(x => x.m, Ci)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.TemplateId).ToHashSet(Ci), Ci);

        var coverages = new List<RoleMfaCoverage>();
        foreach (var role in roles
                     .Where(r => r.MemberCount > 0)
                     .OrderBy(r => r.DisplayName ?? r.TemplateId, StringComparer.OrdinalIgnoreCase))
        {
            coverages.Add(role.UserMemberIds.Count == 0
                ? RoleLevelCoverage(role, readings, full)
                : MemberLevelCoverage(role, readings, full, rolesOf));
        }

        var uncovered = coverages.Count(c => c.State == RoleMfaCoverageState.NotCovered);
        var unresolved = coverages.Where(c => c.State == RoleMfaCoverageState.Unresolved).ToList();
        var excepted = coverages
            .Where(c => c.State == RoleMfaCoverageState.CoveredWithExceptions
                        || (c.State == RoleMfaCoverageState.Unresolved && c.ExceptionMemberIds.Count > 0))
            .ToList();

        // Um papel com lacuna comprovada é exposição, mesmo havendo outros não resolvidos ou com exceções.
        if (uncovered > 0)
            return new AdminMfaConclusion(true, null, uncovered, coverages, universal);

        if (unresolved.Count > 0 || excepted.Count > 0)
        {
            var parts = new List<string>();
            if (unresolved.Count > 0)
                parts.Add($"{Plural(unresolved.Count, "papel privilegiado depende", "papéis privilegiados dependem")} de "
                    + "política direcionada a grupos ou a convidados, ou de exclusão de grupos, cujo pertencimento não é "
                    + "coletado nesta entrega — a cobertura não pode ser afirmada nem negada.");
            if (excepted.Count > 0)
                parts.Add(ExceptionsReason(excepted));
            return new AdminMfaConclusion(false, string.Join(" ", parts), 0, coverages, universal);
        }

        return new AdminMfaConclusion(true, null, 0, coverages, universal);
    }

    private static string ExceptionsReason(IReadOnlyList<RoleMfaCoverage> excepted)
    {
        var members = excepted.SelectMany(c => c.ExceptionMemberIds).Distinct(Ci).Count();
        var roleNames = string.Join(", ", excepted.Select(c => c.Role.DisplayName ?? c.Role.TemplateId));
        return $"{Plural(members, "conta privilegiada está excluída", "contas privilegiadas estão excluídas")} "
            + $"explicitamente das políticas que exigem MFA do papel, sem outra política que a(s) cubra ({roleNames}). "
            + "Uma exclusão não é irregular por si — é o padrão para contas de emergência —, mas não comprova proteção "
            + "nem controle compensatório, e a designação dessas contas não é verificável nesta coleta. O controle não é "
            + "aprovado nem reprovado; as contas aparecem nomeadas na evidência.";
    }

    private static RoleMfaCoverage MemberLevelCoverage(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> readings,
        IReadOnlyList<ConditionalAccessPolicyReading> full,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf)
    {
        var members = new List<MemberMfaCoverage>();
        var covering = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var memberId in role.UserMemberIds.Distinct(Ci).OrderBy(m => m, StringComparer.Ordinal))
        {
            var userRoles = rolesOf.TryGetValue(memberId, out var held) ? held : new[] { role.TemplateId };
            var applies = new List<string>();
            var excludes = new List<string>();
            var unknown = new List<string>();
            foreach (var r in full)
            {
                switch (ReachOf(r, memberId, userRoles))
                {
                    case Reach.Applies: applies.Add(r.Policy.Id); break;
                    case Reach.Excluded: excludes.Add(r.Policy.Id); break;
                    case Reach.Unknown: unknown.Add(r.Policy.Id); break;
                }
            }

            foreach (var id in applies) covering.Add(id);
            members.Add(applies.Count > 0 ? new MemberMfaCoverage(memberId, MemberMfaCoverageState.Covered, applies)
                : unknown.Count > 0 ? new MemberMfaCoverage(memberId, MemberMfaCoverageState.Unresolved, unknown)
                : excludes.Count > 0 ? new MemberMfaCoverage(memberId, MemberMfaCoverageState.Excepted, excludes)
                : new MemberMfaCoverage(memberId, MemberMfaCoverageState.NotTargeted, Array.Empty<string>()));
        }

        var notes = CandidateNotes(role, readings, full);
        foreach (var r in full.Where(r => r.Policy.ExcludeRoles.Contains(role.TemplateId, Ci)))
            notes.Insert(0, $"“{Name(r.Policy)}” exclui este papel.");

        int Count(MemberMfaCoverageState s) => members.Count(m => m.State == s);
        RoleMfaCoverageState state;
        if (Count(MemberMfaCoverageState.NotTargeted) > 0) state = RoleMfaCoverageState.NotCovered;
        else if (Count(MemberMfaCoverageState.Covered) == 0 && Count(MemberMfaCoverageState.Unresolved) == 0)
            state = RoleMfaCoverageState.NotCovered; // todos excluídos: a exigência não alcança o papel
        else if (Count(MemberMfaCoverageState.Unresolved) > 0) state = RoleMfaCoverageState.Unresolved;
        else if (Count(MemberMfaCoverageState.Excepted) > 0) state = RoleMfaCoverageState.CoveredWithExceptions;
        else state = RoleMfaCoverageState.Covered;

        if (state == RoleMfaCoverageState.Unresolved)
            notes.Add("Parte da cobertura depende de grupo(s) ou da condição de convidado: o pertencimento não é coletado nesta entrega.");

        return new RoleMfaCoverage(role, state, covering.ToList(), members, notes);
    }

    /// <summary>
    /// Papel ativo sem membro USUÁRIO conhecido (só identidades de aplicação ou grupos atribuíveis): lido pelo
    /// alvo declarado ao papel, como antes — exclusão de grupo ou de convidados deixa a cobertura não resolvida.
    /// </summary>
    private static RoleMfaCoverage RoleLevelCoverage(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> readings,
        IReadOnlyList<ConditionalAccessPolicyReading> full)
    {
        var covering = new List<string>();
        var unknown = new List<string>();
        var notes = CandidateNotes(role, readings, full);
        foreach (var r in full)
        {
            var p = r.Policy;
            if (p.ExcludeRoles.Contains(role.TemplateId, Ci)) { notes.Insert(0, $"“{Name(p)}” exclui este papel."); continue; }
            var targeted = r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci);
            if (targeted && p.ExcludeGroups.Count == 0 && !ExcludesGuests(p)) covering.Add(p.Id);
            else if (targeted || p.IncludeGroups.Count > 0) unknown.Add(p.Id);
        }

        var state = covering.Count > 0 ? RoleMfaCoverageState.Covered
            : unknown.Count > 0 ? RoleMfaCoverageState.Unresolved
            : RoleMfaCoverageState.NotCovered;
        if (state == RoleMfaCoverageState.Unresolved)
            notes.Add("Política direcionada a grupo(s), ou com exclusão de grupo(s): o pertencimento não é coletado nesta entrega.");
        return new RoleMfaCoverage(role, state, state == RoleMfaCoverageState.Covered ? covering : Array.Empty<string>(),
            Array.Empty<MemberMfaCoverage>(), notes);
    }

    /// <summary>Por que as políticas que miram o papel e mencionam MFA NÃO contam como exigência.</summary>
    private static List<string> CandidateNotes(
        DirectoryRoleConfiguration role,
        IReadOnlyList<ConditionalAccessPolicyReading> readings,
        IReadOnlyList<ConditionalAccessPolicyReading> full)
    {
        var notes = new List<string>();
        foreach (var r in readings.Where(r => r.RequiresMfa || r.MfaIsAlternative))
        {
            var p = r.Policy;
            var targets = r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci)
                || p.IncludeUsers.Any(u => role.UserMemberIds.Contains(u, Ci));
            if (!targets || p.ExcludeRoles.Contains(role.TemplateId, Ci) || full.Contains(r)) continue;
            notes.Add($"“{Name(p)}” não conta como exigência: {WhyNotEnforcing(r)}.");
        }
        return notes;
    }

    private static string WhyNotEnforcing(ConditionalAccessPolicyReading r)
    {
        var reasons = new List<string>();
        if (r.Policy.State == ConditionalAccessPolicyState.ReportOnly) reasons.Add("está em somente relatório (não impõe)");
        else if (r.Policy.State == ConditionalAccessPolicyState.Disabled) reasons.Add("está desabilitada");
        else if (r.Policy.State == ConditionalAccessPolicyState.Unknown) reasons.Add("estado não reconhecido");
        if (r.MfaIsAlternative) reasons.Add("MFA é apenas uma alternativa (operador OU)");
        if (!r.CoversAllApplications) reasons.Add("não alcança todas as aplicações");
        if (r.Narrowing.Count > 0) reasons.Add("estreitada por " + string.Join(", ", r.Narrowing));
        return reasons.Count == 0 ? "alcance insuficiente" : string.Join("; ", reasons);
    }

    // ---- Autenticação legada -------------------------------------------------------------------------

    /// <summary>Bloqueio que alcança os dois canais legados, habilitado, em todas as aplicações e sem condição que o estreite.</summary>
    private static bool IsFullLegacyBlock(ConditionalAccessPolicyReading r) =>
        r.IsEnforced && r.Blocks && CoversAllLegacyClients(r.Policy) && r.CoversAllApplications
        && !r.Narrowing.Any(n => n != RestrictedClients);

    private static LegacyAuthConclusion ConcludeLegacy(
        IReadOnlyList<ConditionalAccessPolicyReading> readings, IReadOnlyList<DirectoryRoleConfiguration>? roles)
    {
        var candidates = readings.Where(r => r.Blocks && TouchesLegacy(r.Policy)).ToList();
        var fullBlocks = candidates.Where(IsFullLegacyBlock).ToList();
        var forAll = fullBlocks.Where(r => r.TargetsAllUsers).ToList();

        var rolesOf = (roles ?? Array.Empty<DirectoryRoleConfiguration>())
            .SelectMany(role => role.UserMemberIds.Select(m => (m, role.TemplateId)))
            .GroupBy(x => x.m, Ci)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.TemplateId).ToHashSet(Ci), Ci);

        var blocking = new List<string>();
        var withExceptions = new List<(ConditionalAccessPolicyReading Policy, IReadOnlyList<string> Open)>();
        foreach (var r in forAll)
        {
            var open = OpenLegacyExceptions(r, fullBlocks, rolesOf, roles is not null);
            if (open.Count == 0)
            {
                blocking.Add(r.Policy.Id);
                // Quem cobre as exceções também sustenta o bloqueio.
                foreach (var id in CoveringLegacyPolicies(r, fullBlocks, rolesOf, roles is not null))
                    if (!blocking.Contains(id)) blocking.Add(id);
            }
            else withExceptions.Add((r, open));
        }

        var partial = candidates
            .Where(r => !blocking.Contains(r.Policy.Id) && withExceptions.All(w => !ReferenceEquals(w.Policy, r)))
            .Select(r => r.Policy.Id).ToList();

        if (blocking.Count > 0)
            return new LegacyAuthConclusion(true, blocking, partial, withExceptions.Select(w => w.Policy.Policy.Id).ToList(), null);

        if (withExceptions.Count > 0)
        {
            var reason = string.Join(" ", withExceptions.Select(w =>
                $"“{Name(w.Policy.Policy)}” bloqueia a autenticação legada em todas as aplicações para todos os usuários, "
                + "exceto " + string.Join(", ", w.Open) + ", e nenhuma outra política habilitada comprova o bloqueio para essas exceções."))
                + " Uma exclusão não é irregular por si, mas nela a autenticação legada continua possível; sem verificar as "
                + "exceções, o bloqueio para todos os usuários não pode ser afirmado nem negado.";
            return new LegacyAuthConclusion(false, blocking, partial, withExceptions.Select(w => w.Policy.Policy.Id).ToList(), reason);
        }

        return new LegacyAuthConclusion(false, blocking, partial, Array.Empty<string>(), null);
    }

    /// <summary>
    /// Exclusões de um bloqueio "para todos" que NENHUMA outra política de bloqueio completo comprovadamente cobre,
    /// descritas em palavras. Usuário: coberto se outra política o alcança sem exclusão que o deixe de fora.
    /// Grupo, papel e convidados: cobertos só por outra política que os inclua (ou inclua todos) SEM exclusões.
    /// Sem o inventário de papéis, uma exclusão por papel na outra política torna a cobertura do usuário incerta.
    /// </summary>
    private static List<string> OpenLegacyExceptions(
        ConditionalAccessPolicyReading policy,
        IReadOnlyList<ConditionalAccessPolicyReading> fullBlocks,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf,
        bool rolesKnown)
    {
        var others = fullBlocks.Where(o => !ReferenceEquals(o, policy)).ToList();
        var p = policy.Policy;

        var users = p.ExcludeUsers.Where(u => !u.Equals(GuestsValue, StringComparison.OrdinalIgnoreCase))
            .Where(u => !others.Any(o => CoversUser(o, u, rolesOf, rolesKnown))).ToList();
        var groups = p.ExcludeGroups
            .Where(g => !others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || o.Policy.IncludeGroups.Contains(g, Ci)))).ToList();
        var excludedRoles = p.ExcludeRoles
            .Where(role => !others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || o.Policy.IncludeRoles.Contains(role, Ci)))).ToList();
        var guests = ExcludesGuests(p)
            && !others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || IncludesGuests(o.Policy)));

        var open = new List<string>();
        if (users.Count > 0) open.Add(Plural(users.Count, "usuário excluído nominalmente", "usuários excluídos nominalmente"));
        if (groups.Count > 0) open.Add(Plural(groups.Count, "grupo excluído", "grupos excluídos") + " (pertencimento não coletado)");
        if (excludedRoles.Count > 0) open.Add(Plural(excludedRoles.Count, "papel excluído", "papéis excluídos"));
        if (guests) open.Add("convidados e usuários externos");
        return open;
    }

    private static IEnumerable<string> CoveringLegacyPolicies(
        ConditionalAccessPolicyReading policy,
        IReadOnlyList<ConditionalAccessPolicyReading> fullBlocks,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf,
        bool rolesKnown)
    {
        var p = policy.Policy;
        foreach (var o in fullBlocks.Where(o => !ReferenceEquals(o, policy)))
        {
            var coversUser = p.ExcludeUsers.Any(u => CoversUser(o, u, rolesOf, rolesKnown));
            var coversRest = !o.HasExclusions && (
                p.ExcludeGroups.Any(g => o.TargetsAllUsers || o.Policy.IncludeGroups.Contains(g, Ci))
                || p.ExcludeRoles.Any(role => o.TargetsAllUsers || o.Policy.IncludeRoles.Contains(role, Ci))
                || (ExcludesGuests(p) && (o.TargetsAllUsers || IncludesGuests(o.Policy))));
            if (coversUser || coversRest) yield return o.Policy.Id;
        }
    }

    private static bool CoversUser(
        ConditionalAccessPolicyReading other, string userId,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf, bool rolesKnown) =>
        (rolesKnown || other.Policy.ExcludeRoles.Count == 0)
        && ReachOf(other, userId, rolesOf.TryGetValue(userId, out var held) ? held : Array.Empty<string>()) == Reach.Applies;

    // ---- Base mínima -------------------------------------------------------------------------------

    /// <summary>
    /// A base mínima é uma exigência AMPLA: habilitada, exigindo MFA, com alvo DECLARADO em todos os usuários, em
    /// todas as aplicações e sem condição que a estreite. Exclusões nominais, de papel e de convidados são
    /// delimitadas e ficam listadas na evidência; exclusão de GRUPO não — o grupo pode conter quase todos, e seu
    /// pertencimento não é coletado. Política para um usuário, um papel ou uma aplicação não sustenta uma
    /// conclusão sobre o ambiente.
    /// </summary>
    private static MfaBaselineConclusion ConcludeBaseline(IReadOnlyList<ConditionalAccessPolicyReading> readings)
    {
        var baseline = new List<string>();
        var unresolved = new List<string>();
        var restricted = new List<string>();
        foreach (var r in readings.Where(r => r.IsEnforced && r.RequiresMfa))
        {
            var broadScope = r.CoversAllApplications && r.Narrowing.Count == 0;
            if (broadScope && r.TargetsAllUsers && r.Policy.ExcludeGroups.Count == 0) baseline.Add(r.Policy.Id);
            else if (broadScope && (r.TargetsAllUsers || r.Policy.IncludeGroups.Count > 0)) unresolved.Add(r.Policy.Id);
            else restricted.Add(r.Policy.Id);
        }

        string? reason = null;
        if (baseline.Count == 0 && unresolved.Count > 0)
            reason = "Há política habilitada que exige MFA em todas as aplicações, mas o alcance dela depende de grupo(s) "
                + "cujo pertencimento não é coletado nesta entrega ("
                + string.Join(", ", unresolved.Select(id => "“" + Name(readings.First(x => x.Policy.Id == id).Policy) + "”"))
                + ") — não é possível afirmar nem negar uma base mínima para o ambiente.";
        return new MfaBaselineConclusion(baseline, unresolved, restricted, reason);
    }

    // ---- Tradução em sinais do KNIGHT -----------------------------------------------------------------

    /// <summary>
    /// Os sinais que as regras do catálogo consomem. Políticas não coletadas → sinais AUSENTES (com o motivo
    /// da capacidade), nunca "nenhuma política". Conclusão que não pode ser afirmada nem negada (pertencimento
    /// não coletado, exceções explícitas sem outra cobertura) → sinal ausente com o motivo — nunca zero.
    /// </summary>
    public static IReadOnlyList<KnightObservation> ToObservations(ConditionalAccessAnalysis? analysis, string? missingReason)
    {
        if (analysis is null)
        {
            var reason = missingReason ?? "Políticas de acesso condicional não coletadas.";
            return new[]
            {
                KnightObservation.MissingData(KnightSignalKey.LegacyAuthenticationBlocked, reason),
                KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, reason),
                KnightObservation.MissingData(KnightSignalKey.BaselineMfaPolicies, reason),
            };
        }

        var legacy = analysis.LegacyAuth;
        var baseline = analysis.Baseline;
        return new[]
        {
            legacy.Blocked || legacy.InconclusiveReason is null
                ? KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, legacy.Blocked)
                : KnightObservation.MissingData(KnightSignalKey.LegacyAuthenticationBlocked, legacy.InconclusiveReason),
            analysis.AdminMfa.Evaluable
                ? KnightObservation.OfCount(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, analysis.AdminMfa.UncoveredRoleCount)
                : KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy,
                    analysis.AdminMfa.InconclusiveReason ?? "Cobertura de MFA administrativa não resolvida."),
            baseline.BaselinePolicyIds.Count > 0 || baseline.InconclusiveReason is null
                ? KnightObservation.OfCount(KnightSignalKey.BaselineMfaPolicies, baseline.BaselinePolicyIds.Count)
                : KnightObservation.MissingData(KnightSignalKey.BaselineMfaPolicies, baseline.InconclusiveReason),
        };
    }

    // ---- Descrição legível -----------------------------------------------------------------------------

    public static string StateLabel(ConditionalAccessPolicyState state) => state switch
    {
        ConditionalAccessPolicyState.Enabled => "habilitada",
        ConditionalAccessPolicyState.Disabled => "desabilitada",
        ConditionalAccessPolicyState.ReportOnly => "somente relatório (não impõe)",
        _ => "estado não reconhecido",
    };

    public static string Name(ConditionalAccessPolicyConfiguration p) =>
        string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName!;

    private static string Describe(
        ConditionalAccessPolicyConfiguration p, bool requiresMfa, bool alternative, bool blocks, IReadOnlyList<string> narrowing)
    {
        var parts = new List<string> { "Estado: " + StateLabel(p.State) };

        var users = new List<string>();
        if (p.IncludeUsers.Contains("All", Ci)) users.Add("todos os usuários");
        else if (p.IncludeUsers.Contains("None", Ci) && p.IncludeRoles.Count == 0 && p.IncludeGroups.Count == 0) users.Add("nenhum usuário");
        var nominal = p.IncludeUsers.Count(u => !IsSpecialUser(u));
        if (nominal > 0) users.Add($"{nominal} usuário(s) nominal(is)");
        if (p.IncludeRoles.Count > 0) users.Add($"{p.IncludeRoles.Count} papel(éis)");
        if (p.IncludeGroups.Count > 0) users.Add($"{p.IncludeGroups.Count} grupo(s)");
        if (IncludesGuests(p)) users.Add("convidados/externos");
        var excl = new List<string>();
        var nominalExcluded = p.ExcludeUsers.Count(u => !IsSpecialUser(u));
        if (nominalExcluded > 0) excl.Add($"{nominalExcluded} usuário(s)");
        if (p.ExcludeGroups.Count > 0) excl.Add($"{p.ExcludeGroups.Count} grupo(s)");
        if (p.ExcludeRoles.Count > 0) excl.Add($"{p.ExcludeRoles.Count} papel(éis)");
        if (ExcludesGuests(p)) excl.Add("convidados/externos");
        parts.Add("Alvos: " + (users.Count == 0 ? "não identificados" : string.Join(", ", users))
            + (excl.Count > 0 ? " (exceto " + string.Join(", ", excl) + ")" : ""));

        string apps;
        if (p.IncludeApplications.Contains("All", Ci))
            apps = p.ExcludeApplications.Count == 0 ? "todas" : $"todas, exceto {p.ExcludeApplications.Count}";
        else if (p.IncludeApplications.Contains("MicrosoftAdminPortals", Ci))
            apps = "portais administrativos da Microsoft";
        else if (p.IncludeApplications.Count > 0)
            apps = $"{p.IncludeApplications.Count} aplicação(ões) específica(s)";
        else if (p.IncludeUserActions.Count > 0)
            apps = "ações do usuário (" + string.Join(", ", p.IncludeUserActions) + ")";
        else if (p.IncludeAuthenticationContexts.Count > 0)
            apps = "contextos de autenticação";
        else
            apps = "não identificadas";
        parts.Add("Aplicações: " + apps);

        if (!AllClientTypes(p.ClientAppTypes))
            parts.Add("Clientes: " + string.Join(", ", p.ClientAppTypes.Select(ClientLabel)));
        parts.Add("Condições: " + (narrowing.Count(n => n != RestrictedClients) == 0
            ? "nenhuma que estreite"
            : string.Join(", ", narrowing.Where(n => n != RestrictedClients))));

        string grant;
        if (blocks) grant = "bloquear acesso";
        else if (requiresMfa)
            grant = string.IsNullOrWhiteSpace(p.AuthenticationStrengthId)
                ? "exigir MFA"
                : $"exigir força de autenticação “{p.AuthenticationStrengthName ?? p.AuthenticationStrengthId}”";
        else if (alternative) grant = "MFA como alternativa (operador OU)";
        else if (p.BuiltInControls.Count > 0) grant = string.Join(" / ", p.BuiltInControls);
        else grant = "sem controle de concessão";
        parts.Add("Concessão: " + grant);

        return string.Join(" · ", parts);
    }

    private static bool IsSpecialUser(string u) =>
        u.Equals("All", StringComparison.OrdinalIgnoreCase) || u.Equals("None", StringComparison.OrdinalIgnoreCase)
        || u.Equals(GuestsValue, StringComparison.OrdinalIgnoreCase);

    private static string ClientLabel(string c) => c.ToLowerInvariant() switch
    {
        "browser" => "navegador",
        "mobileappsanddesktopclients" => "aplicativos móveis e de desktop",
        "exchangeactivesync" => "Exchange ActiveSync",
        "other" => "outros clientes (legados)",
        "eassupported" => "Exchange ActiveSync (suportado)",
        _ => c,
    };

    /// <summary>Contagem legível ("1 papel", "3 papéis") — só para textos de evidência.</summary>
    public static string Plural(int n, string singular, string plural) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural);
}
