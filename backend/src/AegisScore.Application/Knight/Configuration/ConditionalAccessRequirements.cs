using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;
using static AegisScore.Application.Knight.Configuration.ConditionalAccessAnalyzer;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] Exigências de acesso condicional além da MFA
// ============================================================================
// Os controles de referência pedem outras exigências por acesso condicional: bloquear o fluxo de código de
// dispositivo e a transferência de autenticação, exigir dispositivo gerenciado, reautenticação periódica, reagir
// a risco de usuário e de entrada, frequência de entrada para administradores, força de autenticação resistente a
// phishing. A leitura segue as MESMAS regras do analisador de MFA administrativa:
//   • só política HABILITADA impõe (somente relatório e desabilitada são listadas, nunca contadas);
//   • exigência "para todos os usuários" exige alvo declarado em todos os usuários — política dirigida a grupo
//     depende de pertencimento que esta entrega não coleta e fica não resolvida, nunca aprovada;
//   • exclusão explícita não é irregular por si, mas não comprova cobertura: sem outra política que cubra a
//     exceção, o controle fica não avaliado, com as exceções nomeadas;
//   • exigência para administradores é lida MEMBRO A MEMBRO sobre os papéis privilegiados ativos.

/// <summary>Uma exigência de acesso condicional: quais políticas tratam dela e o que falta a cada uma.</summary>
/// <param name="Label">A exigência em palavras ("bloqueio do fluxo de código de dispositivo").</param>
/// <param name="IsCandidate">A política trata desta exigência (independentemente de estado e alvo).</param>
/// <param name="Gap">O que falta à política para cumprir a exigência (ignorando estado e usuários); <c>null</c> quando cumpre.</param>
/// <param name="NeedsV2">A exigência lê campos que só a coleta v2 registra (sessão, risco, fluxos).</param>
public sealed record CaRequirement(
    string Label,
    Func<ConditionalAccessPolicyReading, bool> IsCandidate,
    Func<ConditionalAccessPolicyReading, string?> Gap,
    bool NeedsV2 = true);

public enum CaCoverageState
{
    /// <summary>Política habilitada cumpre a exigência para todos os usuários (exceções cobertas por outra política).</summary>
    Satisfied = 0,

    /// <summary>Cumpre para todos, mas com exclusões que nenhuma outra política cobre — não afirmado nem negado.</summary>
    OpenExceptions = 1,

    /// <summary>Só política dirigida a grupo(s) cumpre a exigência — pertencimento não coletado.</summary>
    Unresolved = 2,

    /// <summary>Nenhuma política habilitada cumpre a exigência.</summary>
    NotSatisfied = 3,

    /// <summary>As políticas não puderam ser lidas (não coletadas ou coleta sem os campos necessários).</summary>
    NotCollected = 4,
}

/// <summary>Resultado da leitura de UMA exigência para todos os usuários.</summary>
public sealed record CaRequirementEvaluation(
    CaCoverageState State,
    IReadOnlyList<ConditionalAccessPolicyReading> Satisfying,
    IReadOnlyList<(ConditionalAccessPolicyReading Policy, string Why)> NotCounting,
    IReadOnlyList<string> OpenExceptions,
    string? Reason);

/// <summary>Resultado da leitura de UMA exigência para os papéis privilegiados, papel a papel.</summary>
public sealed record CaPrivilegedEvaluation(
    CaCoverageState State,
    IReadOnlyList<RoleMfaCoverage> Roles,
    IReadOnlyList<ConditionalAccessPolicyReading> Satisfying,
    IReadOnlyList<(ConditionalAccessPolicyReading Policy, string Why)> NotCounting,
    string? Reason)
{
    public int UncoveredRoles => Roles.Count(r => r.State == RoleMfaCoverageState.NotCovered);
}

public static class ConditionalAccessRequirements
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>Força de autenticação interna "MFA resistente a phishing".</summary>
    public const string PhishingResistantStrengthId = "00000000-0000-0000-0000-000000000004";

    /// <summary>Aplicação "Microsoft Intune Enrollment".</summary>
    public const string IntuneEnrollmentAppId = "d4ebce55-015a-49b5-a083-c84d1797ae8c";

    /// <summary>Ação de usuário "registrar informações de segurança".</summary>
    public const string RegisterSecurityInfoAction = "urn:user:registersecurityinfo";

    private static readonly HashSet<string> PhishingResistantCombinations = new(Ci)
    {
        "fido2", "windowsHelloForBusiness", "x509CertificateMultiFactor",
    };

    /// <summary>A política exige força de autenticação resistente a phishing (interna ou personalizada só com métodos resistentes).</summary>
    public static bool RequiresPhishingResistant(ConditionalAccessPolicyConfiguration p)
    {
        if (string.Equals(p.AuthenticationStrengthId, PhishingResistantStrengthId, StringComparison.OrdinalIgnoreCase)) return true;
        var combos = p.AuthenticationStrengthCombinations;
        return !string.IsNullOrWhiteSpace(p.AuthenticationStrengthId) && combos is { Count: > 0 }
               && combos.All(c => c.Split(',').All(part => PhishingResistantCombinations.Contains(part.Trim())));
    }

    /// <summary>A política alcança todas as aplicações (ou todas as da Microsoft 365 e portais administrativos quando exigido).</summary>
    public static bool AllApplications(ConditionalAccessPolicyConfiguration p) =>
        p.IncludeApplications.Contains("All", Ci) && p.ExcludeApplications.Count == 0;

    public static bool HasControl(ConditionalAccessPolicyConfiguration p, string control) =>
        p.BuiltInControls.Contains(control, Ci);

    /// <summary>Lê a exigência para TODOS os usuários.</summary>
    public static CaRequirementEvaluation ForAllUsers(
        KnightDirectoryConfiguration? directory, CaRequirement requirement, string? notCollectedReason,
        Func<string, string>? memberLabel = null)
    {
        var policies = directory?.ConditionalAccessPolicies;
        if (policies is null)
            return new(CaCoverageState.NotCollected, Array.Empty<ConditionalAccessPolicyReading>(),
                Array.Empty<(ConditionalAccessPolicyReading, string)>(), Array.Empty<string>(),
                notCollectedReason ?? "As políticas de acesso condicional não foram coletadas nesta avaliação.");
        if (requirement.NeedsV2 && policies.Any(p => !p.SessionAndConditionsCaptured))
            return new(CaCoverageState.NotCollected, Array.Empty<ConditionalAccessPolicyReading>(),
                Array.Empty<(ConditionalAccessPolicyReading, string)>(), Array.Empty<string>(),
                "A coleta desta avaliação não registrou as condições e os controles de sessão das políticas (coleta anterior à ampliação do catálogo).");

        var label = memberLabel ?? (id => id);
        var readings = policies.OrderBy(p => p.Id, StringComparer.Ordinal).Select(Read).ToList();
        var satisfying = new List<ConditionalAccessPolicyReading>();
        var notCounting = new List<(ConditionalAccessPolicyReading, string)>();
        var groupScoped = new List<ConditionalAccessPolicyReading>();

        foreach (var r in readings.Where(requirement.IsCandidate))
        {
            var gap = requirement.Gap(r);
            if (!r.IsEnforced) notCounting.Add((r, StateLabel(r.Policy.State)));
            else if (gap is not null) notCounting.Add((r, gap));
            else if (r.TargetsAllUsers) satisfying.Add(r);
            else if (r.Policy.IncludeGroups.Count > 0) { groupScoped.Add(r); notCounting.Add((r, "dirigida a grupo(s) cujo pertencimento não é coletado")); }
            else notCounting.Add((r, "o alvo não inclui todos os usuários"));
        }

        if (satisfying.Count > 0)
        {
            var clean = satisfying.FirstOrDefault(r => !r.HasExclusions);
            if (clean is not null)
                return new(CaCoverageState.Satisfied, satisfying, notCounting, Array.Empty<string>(), null);

            var rolesOf = RolesOf(directory!.PrivilegedRoles);
            var rolesKnown = directory.PrivilegedRoles is not null;
            List<string>? smallest = null;
            foreach (var r in satisfying)
            {
                var open = OpenExceptions(r, satisfying, rolesOf, rolesKnown, label);
                if (open.Count == 0)
                    return new(CaCoverageState.Satisfied, satisfying, notCounting, Array.Empty<string>(), null);
                if (smallest is null || open.Count < smallest.Count) smallest = open;
            }

            return new(CaCoverageState.OpenExceptions, satisfying, notCounting, smallest!,
                $"Há política habilitada para todos os usuários com {requirement.Label}, mas ela exclui "
                + string.Join("; ", smallest!) + ", e nenhuma outra política habilitada cobre essas exceções. "
                + "Uma exclusão não é irregular por si (é o padrão para contas de emergência), mas não comprova a proteção "
                + "dessas contas — o controle não é aprovado nem reprovado.");
        }

        if (groupScoped.Count > 0)
            return new(CaCoverageState.Unresolved, satisfying, notCounting, Array.Empty<string>(),
                $"Só política dirigida a grupo(s) aplica {requirement.Label} ("
                + string.Join(", ", groupScoped.Select(r => "“" + Name(r.Policy) + "”"))
                + "); o pertencimento dos grupos não é coletado nesta entrega, então não é possível afirmar que ela alcança todos os usuários.");

        return new(CaCoverageState.NotSatisfied, satisfying, notCounting, Array.Empty<string>(), null);
    }

    /// <summary>Lê a exigência para os membros dos papéis privilegiados ativos (membro a membro).</summary>
    public static CaPrivilegedEvaluation ForPrivilegedRoles(
        KnightDirectoryConfiguration? directory, CaRequirement requirement, string? notCollectedReason)
    {
        var policies = directory?.ConditionalAccessPolicies;
        if (policies is null)
            return new(CaCoverageState.NotCollected, Array.Empty<RoleMfaCoverage>(), Array.Empty<ConditionalAccessPolicyReading>(),
                Array.Empty<(ConditionalAccessPolicyReading, string)>(),
                notCollectedReason ?? "As políticas de acesso condicional não foram coletadas nesta avaliação.");
        if (directory!.PrivilegedRoles is null)
            return new(CaCoverageState.NotCollected, Array.Empty<RoleMfaCoverage>(), Array.Empty<ConditionalAccessPolicyReading>(),
                Array.Empty<(ConditionalAccessPolicyReading, string)>(),
                "O inventário de papéis privilegiados não foi coletado; a cobertura por papel não pode ser afirmada nem negada.");
        if (requirement.NeedsV2 && policies.Any(p => !p.SessionAndConditionsCaptured))
            return new(CaCoverageState.NotCollected, Array.Empty<RoleMfaCoverage>(), Array.Empty<ConditionalAccessPolicyReading>(),
                Array.Empty<(ConditionalAccessPolicyReading, string)>(),
                "A coleta desta avaliação não registrou as condições e os controles de sessão das políticas (coleta anterior à ampliação do catálogo).");

        var readings = policies.OrderBy(p => p.Id, StringComparer.Ordinal).Select(Read).ToList();
        var full = new List<ConditionalAccessPolicyReading>();
        var notCounting = new List<(ConditionalAccessPolicyReading, string)>();
        foreach (var r in readings.Where(requirement.IsCandidate))
        {
            var gap = requirement.Gap(r);
            if (!r.IsEnforced) notCounting.Add((r, StateLabel(r.Policy.State)));
            else if (gap is not null) notCounting.Add((r, gap));
            else full.Add(r);
        }

        var rolesOf = RolesOf(directory.PrivilegedRoles);
        var coverages = new List<RoleMfaCoverage>();
        foreach (var role in directory.PrivilegedRoles
                     .Where(r => r.MemberCount > 0)
                     .OrderBy(r => r.DisplayName ?? r.TemplateId, StringComparer.OrdinalIgnoreCase))
        {
            coverages.Add(role.UserMemberIds.Count == 0 ? RoleLevel(role, full) : MemberLevel(role, full, rolesOf));
        }

        var uncovered = coverages.Count(c => c.State == RoleMfaCoverageState.NotCovered);
        var unresolved = coverages.Count(c => c.State == RoleMfaCoverageState.Unresolved);
        var excepted = coverages.Count(c => c.State == RoleMfaCoverageState.CoveredWithExceptions);
        if (uncovered > 0) return new(CaCoverageState.NotSatisfied, coverages, full, notCounting, null);
        if (unresolved > 0)
            return new(CaCoverageState.Unresolved, coverages, full, notCounting,
                $"{Plural(unresolved, "papel privilegiado depende", "papéis privilegiados dependem")} de política dirigida a grupos, "
                + "ou de exclusão de grupos, cujo pertencimento não é coletado — a cobertura não pode ser afirmada nem negada.");
        if (excepted > 0)
            return new(CaCoverageState.OpenExceptions, coverages, full, notCounting,
                $"{Plural(excepted, "papel privilegiado tem", "papéis privilegiados têm")} membro(s) excluído(s) explicitamente das políticas "
                + $"que aplicam {requirement.Label}, sem outra política que os cubra. Uma exclusão não é irregular por si, mas não "
                + "comprova a proteção dessas contas — o controle não é aprovado nem reprovado; as contas aparecem nomeadas na evidência.");
        return new(CaCoverageState.Satisfied, coverages, full, notCounting, null);
    }

    // ---- Auxiliares ----------------------------------------------------------------------------------

    private static Dictionary<string, IReadOnlyCollection<string>> RolesOf(IReadOnlyList<DirectoryRoleConfiguration>? roles) =>
        (roles ?? Array.Empty<DirectoryRoleConfiguration>())
            .SelectMany(role => role.UserMemberIds.Select(m => (m, role.TemplateId)))
            .GroupBy(x => x.m, Ci)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.TemplateId).ToHashSet(Ci), Ci);

    private static List<string> OpenExceptions(
        ConditionalAccessPolicyReading policy, IReadOnlyList<ConditionalAccessPolicyReading> satisfying,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> rolesOf, bool rolesKnown, Func<string, string> label)
    {
        var others = satisfying.Where(o => !ReferenceEquals(o, policy)).ToList();
        var p = policy.Policy;
        bool CoversUser(ConditionalAccessPolicyReading o, string u) =>
            (rolesKnown || o.Policy.ExcludeRoles.Count == 0)
            && ReachOf(o, u, rolesOf.TryGetValue(u, out var held) ? held : Array.Empty<string>()) == Reach.Applies;

        var users = p.ExcludeUsers.Where(u => !u.Equals("GuestsOrExternalUsers", StringComparison.OrdinalIgnoreCase))
            .Where(u => !others.Any(o => CoversUser(o, u))).ToList();
        var groups = p.ExcludeGroups
            .Where(g => !others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || o.Policy.IncludeGroups.Contains(g, Ci)))).ToList();
        var roles = p.ExcludeRoles
            .Where(role => !others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || o.Policy.IncludeRoles.Contains(role, Ci)))).ToList();
        var guests = ExcludesGuests(p) && !others.Any(o => !o.HasExclusions && (o.TargetsAllUsers || IncludesGuests(o.Policy)));

        var open = new List<string>();
        if (users.Count > 0)
            open.Add(Plural(users.Count, "usuário excluído nominalmente", "usuários excluídos nominalmente") + ": "
                + string.Join(", ", users.Take(10).Select(label)) + (users.Count > 10 ? "…" : ""));
        if (groups.Count > 0) open.Add(Plural(groups.Count, "grupo excluído", "grupos excluídos") + " (pertencimento não coletado)");
        if (roles.Count > 0) open.Add(Plural(roles.Count, "papel excluído", "papéis excluídos"));
        if (guests) open.Add("convidados e usuários externos");
        return open;
    }

    private static RoleMfaCoverage MemberLevel(
        DirectoryRoleConfiguration role, IReadOnlyList<ConditionalAccessPolicyReading> full,
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

        int Count(MemberMfaCoverageState s) => members.Count(m => m.State == s);
        var state = Count(MemberMfaCoverageState.NotTargeted) > 0 ? RoleMfaCoverageState.NotCovered
            : Count(MemberMfaCoverageState.Covered) == 0 && Count(MemberMfaCoverageState.Unresolved) == 0 ? RoleMfaCoverageState.NotCovered
            : Count(MemberMfaCoverageState.Unresolved) > 0 ? RoleMfaCoverageState.Unresolved
            : Count(MemberMfaCoverageState.Excepted) > 0 ? RoleMfaCoverageState.CoveredWithExceptions
            : RoleMfaCoverageState.Covered;
        return new RoleMfaCoverage(role, state, covering.ToList(), members, new List<string>());
    }

    private static RoleMfaCoverage RoleLevel(DirectoryRoleConfiguration role, IReadOnlyList<ConditionalAccessPolicyReading> full)
    {
        var covering = new List<string>();
        var unknown = new List<string>();
        foreach (var r in full)
        {
            var p = r.Policy;
            if (p.ExcludeRoles.Contains(role.TemplateId, Ci)) continue;
            var targeted = r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci);
            if (targeted && p.ExcludeGroups.Count == 0 && !ExcludesGuests(p)) covering.Add(p.Id);
            else if (targeted || p.IncludeGroups.Count > 0) unknown.Add(p.Id);
        }
        var state = covering.Count > 0 ? RoleMfaCoverageState.Covered
            : unknown.Count > 0 ? RoleMfaCoverageState.Unresolved
            : RoleMfaCoverageState.NotCovered;
        return new RoleMfaCoverage(role, state, state == RoleMfaCoverageState.Covered ? covering : Array.Empty<string>(),
            Array.Empty<MemberMfaCoverage>(), new List<string>());
    }
}
