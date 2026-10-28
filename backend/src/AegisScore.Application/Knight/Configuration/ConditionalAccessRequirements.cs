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
//     [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] com o pertencimento lido, o alcance da política dirigida a grupo (e das exclusões por
//     grupo) é conferido conta a conta sobre a enumeração completa das contas habilitadas; leitura incompleta continua não
//     resolvida, e nenhum outro critério da exigência é relaxado;
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

    /// <summary>Só política dirigida a grupo(s) cumpre a exigência e o alcance não pôde ser comprovado (pertencimento não coletado
    /// ou não lido por completo, enumeração das contas ausente ou vazia).</summary>
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
    string? Reason,
    // [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Alcance conta a conta das políticas sem alvo declarado em todos os usuários.
    IReadOnlyDictionary<string, string>? Reach = null);

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
    /// <remarks>
    /// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] Só políticas que impõem a exigência (habilitadas, sem lacuna de aplicação, condição, sessão
    /// ou controle) contam. Com o pertencimento lido: a exclusão por grupo lido por completo vira as contas excluídas, cobertas
    /// ou não por outra dessas políticas; e, sem política com alvo declarado em todos os usuários, as dirigidas a grupo (somadas às
    /// demais) são conferidas conta a conta sobre a enumeração completa das contas habilitadas (<see cref="ConditionalAccessAnalyzer.CoverageByAccount"/>).
    /// Sem o pertencimento coletado (aquisições anteriores), a leitura de antes, com o texto de então.
    /// </remarks>
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
        var scope = new CaScope(directory!);
        var candidates = policies.OrderBy(p => p.Id, StringComparer.Ordinal).Select(Read).Where(requirement.IsCandidate)
            .Select(r => (Reading: r, Gap: r.IsEnforced ? requirement.Gap(r) : StateLabel(r.Policy.State)))
            .ToList();
        var full = candidates.Where(c => c.Gap is null).Select(c => c.Reading).ToList();
        var forAll = full.Where(r => r.TargetsAllUsers).ToList();
        var groupScoped = full.Where(r => !r.TargetsAllUsers && r.Policy.IncludeGroups.Count > 0).ToList();
        var scoped = forAll.Count == 0 && groupScoped.Count > 0 && scope.Membership.Collected
            ? CoverageByAccount(full, scope)
            : null;
        var counted = scoped?.Verdict == ScopedVerdict.Covered ? scoped.CoveringPolicyIds : Array.Empty<string>();

        var satisfying = new List<ConditionalAccessPolicyReading>();
        var notCounting = new List<(ConditionalAccessPolicyReading, string)>();
        foreach (var (r, gap) in candidates)
        {
            if (gap is not null) notCounting.Add((r, gap));
            else if (r.TargetsAllUsers || counted.Contains(r.Policy.Id)) satisfying.Add(r);
            else if (r.Policy.IncludeGroups.Count == 0) notCounting.Add((r, "o alvo não inclui todos os usuários"));
            else if (!scope.Membership.Collected) notCounting.Add((r, "dirigida a grupo(s) cujo pertencimento não é coletado"));
            else notCounting.Add((r, scoped is not null && scoped.Reach.TryGetValue(r.Policy.Id, out var reach)
                ? reach
                : "dirigida a " + scope.Target(r.Policy) + ", sem alvo declarado em todos os usuários"));
        }

        var names = string.Join(", ", groupScoped.Select(r => "“" + Name(r.Policy) + "”"));
        if (forAll.Count > 0)
        {
            if (forAll.Any(r => !r.HasExclusions))
                return new(CaCoverageState.Satisfied, satisfying, notCounting, Array.Empty<string>(), null);

            List<string>? smallest = null;
            foreach (var r in forAll)
            {
                var open = OpenExceptions(r, full, scope, label);
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

        if (groupScoped.Count == 0)
            return new(CaCoverageState.NotSatisfied, satisfying, notCounting, Array.Empty<string>(), null);

        if (scoped is null)
            return new(CaCoverageState.Unresolved, satisfying, notCounting, Array.Empty<string>(),
                $"Só política dirigida a grupo(s) aplica {requirement.Label} ({names}); o pertencimento dos grupos não é coletado "
                + "nesta entrega, então não é possível afirmar que ela alcança todos os usuários.");

        return scoped.Verdict switch
        {
            ScopedVerdict.Covered => new(CaCoverageState.Satisfied, satisfying, notCounting, Array.Empty<string>(), null, scoped.Reach),
            ScopedVerdict.Gap or ScopedVerdict.NoneReached => new(CaCoverageState.NotSatisfied, satisfying, notCounting, Array.Empty<string>(),
                $"Só política dirigida a grupo(s) aplica {requirement.Label} ({names}): {scoped.Detail}.", scoped.Reach),
            ScopedVerdict.Exceptions => new(CaCoverageState.OpenExceptions, satisfying, notCounting,
                new[]
                {
                    Plural(scoped.ExceptedAccounts.Count, "conta habilitada excluída explicitamente", "contas habilitadas excluídas explicitamente")
                    + ": " + string.Join(", ", scoped.ExceptedAccounts.Take(10).Select(label)) + (scoped.ExceptedAccounts.Count > 10 ? "…" : ""),
                },
                $"Só política dirigida a grupo(s) aplica {requirement.Label} ({names}): {scoped.Detail}. Uma exclusão não é irregular "
                + "por si, mas não comprova a proteção dessas contas — o controle não é aprovado nem reprovado.", scoped.Reach),
            _ => new(CaCoverageState.Unresolved, satisfying, notCounting, Array.Empty<string>(),
                $"Só política dirigida a grupo(s) aplica {requirement.Label} ({names}); {scoped.Detail} — não é possível afirmar nem "
                + "negar que ela alcança todos os usuários.", scoped.Reach),
        };
    }

    /// <summary>Lê a exigência para os membros dos papéis privilegiados ativos (membro a membro).</summary>
    /// <remarks>
    /// [AEGIS-KNIGHT-CA-GROUP-SCOPE-01] A cobertura de cada papel é a MESMA da MFA administrativa
    /// (<see cref="ConditionalAccessAnalyzer.RoleCoverage"/>): membros diretos e quem detém o papel via grupo (pelo pertencimento
    /// lido), inclusão e exclusão por grupo resolvidas pelo pertencimento, identidades de aplicação fora da exigência de usuários.
    /// A política dirigida ao papel não comprova alcançar quem o detém por grupo.
    /// </remarks>
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

        var scope = new CaScope(directory);
        var coverages = directory.PrivilegedRoles
            .Where(r => r.MemberCount > 0)
            .OrderBy(r => r.DisplayName ?? r.TemplateId, StringComparer.OrdinalIgnoreCase)
            .Select(role => RoleCoverage(role, full, scope.RolesOf, scope.GroupRolesOf, scope.Membership, Array.Empty<string>()))
            .ToList();

        var uncovered = coverages.Count(c => c.State == RoleMfaCoverageState.NotCovered);
        var unresolved = coverages.Count(c => c.State == RoleMfaCoverageState.Unresolved);
        var excepted = coverages.Count(c => c.State == RoleMfaCoverageState.CoveredWithExceptions);
        if (uncovered > 0) return new(CaCoverageState.NotSatisfied, coverages, full, notCounting, null);
        if (unresolved > 0)
            return new(CaCoverageState.Unresolved, coverages, full, notCounting,
                UnresolvedRolesReason(unresolved, scope.Membership.Collected));
        if (excepted > 0)
            return new(CaCoverageState.OpenExceptions, coverages, full, notCounting,
                $"{Plural(excepted, "papel privilegiado tem", "papéis privilegiados têm")} membro(s) excluído(s) explicitamente das políticas "
                + $"que aplicam {requirement.Label}, sem outra política que os cubra. Uma exclusão não é irregular por si, mas não "
                + "comprova a proteção dessas contas — o controle não é aprovado nem reprovado; as contas aparecem nomeadas na evidência.");
        return new(CaCoverageState.Satisfied, coverages, full, notCounting, null);
    }
}
