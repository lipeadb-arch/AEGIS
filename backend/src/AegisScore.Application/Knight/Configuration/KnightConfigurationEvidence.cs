using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Configuration;

/// <summary>
/// [AEGIS-KNIGHT-MULTICLOUD-01] UM objeto preservado para um indicador: afetado (entra na contagem) ou evidência
/// de configuração (sustenta o veredito). Superfície comum às identidades e às configurações.
/// </summary>
public sealed record KnightIndicatorObject(
    KnightObjectRelation Relation,
    KnightAffectedObjectKind Kind,
    string ExternalId,
    string? DisplayName,
    string? UserPrincipalName,
    IReadOnlyList<string> Roles,
    string? Detail,
    string? ObservedConfiguration);

/// <summary>Objetos de configuração de UM indicador, com a completude dos afetados declarada.</summary>
public sealed record KnightConfigurationObjects(
    IReadOnlyList<KnightIndicatorObject> Affected,
    IReadOnlyList<KnightIndicatorObject> Evidence,
    bool AffectedComplete,
    string? Limitation);

/// <summary>
/// Monta, a partir da configuração RELIDA da aquisição, o que sustentou os vereditos de acesso condicional:
/// papéis com lacuna comprovada (afetados de AK-ENTRA-008), papéis cobertos ou com exceções, políticas relevantes
/// e o estado dos security defaults (evidências). Usa o MESMO analisador que produziu os sinais na coleta — a
/// lista não pode divergir da contagem, e o texto de cada objeto não pode contradizer o veredito.
/// </summary>
public static class KnightConfigurationEvidence
{
    public const string LegacyAuthIndicator = "AK-ENTRA-007";
    public const string AdminMfaIndicator = "AK-ENTRA-008";
    public const string BaselineIndicator = "AK-ENTRA-014";

    /// <summary>Indicadores cujo detalhe vem da configuração observada.</summary>
    public static IReadOnlyList<string> Indicators { get; } = new[] { LegacyAuthIndicator, AdminMfaIndicator, BaselineIndicator };

    private const string ExceptionCaveat =
        "Uma exclusão não é irregular por si (é o padrão para contas de emergência), mas não comprova proteção nem controle compensatório.";

    public static IReadOnlyDictionary<string, KnightConfigurationObjects> Build(KnightCollectionResult result)
    {
        var map = new Dictionary<string, KnightConfigurationObjects>(StringComparer.Ordinal);
        var analysis = ConditionalAccessAnalyzer.Analyze(result.DirectoryConfiguration);

        var defaults = result.Facts.Get(KnightSignalKey.SecurityDefaultsEnabled);
        var defaultsObject = defaults.IsCollected
            ? new KnightIndicatorObject(
                KnightObjectRelation.Evidence, KnightAffectedObjectKind.TenantSetting, "securityDefaults",
                "Security defaults", null, Array.Empty<string>(),
                defaults.Flag == true
                    ? "Habilitados: impõem MFA a administradores e bloqueiam a autenticação legada."
                    : "Desabilitados: a proteção depende das políticas de acesso condicional.",
                "Security defaults: " + (defaults.Flag == true ? "habilitados" : "desabilitados"))
            : null;

        // Nomes dos membros privilegiados, da MESMA coleta — para listar exceções por nome, nunca por suposição.
        var names = result.AffectedObjectSets
            .Where(s => s.Signal == KnightSignalKey.PrivilegedAccountsTotal)
            .SelectMany(s => s.Objects)
            .GroupBy(o => o.ExternalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string MemberLabel(string id) =>
            names.TryGetValue(id, out var o) ? (o.DisplayName ?? o.UserPrincipalName ?? id) : id;

        var legacy = new List<KnightIndicatorObject>();
        var admin = new List<KnightIndicatorObject>();
        var affectedRoles = new List<KnightIndicatorObject>();
        var baseline = new List<KnightIndicatorObject>();
        if (defaultsObject is not null) { legacy.Add(defaultsObject); admin.Add(defaultsObject); baseline.Add(defaultsObject); }

        string? legacyLimitation = null, adminLimitation = null, baselineLimitation = null;
        if (analysis is not null)
        {
            string PolicyName(string id) =>
                analysis.Find(id) is { } r ? "“" + ConditionalAccessAnalyzer.Name(r.Policy) + "”" : id;

            foreach (var r in analysis.Policies)
            {
                var p = r.Policy;

                if (r.Blocks && ConditionalAccessAnalyzer.TouchesLegacy(p))
                    legacy.Add(PolicyObject(r, LegacyDetail(r, analysis.LegacyAuth, MemberLabel)));

                var targetsAdmins = r.TargetsAllUsers || p.IncludeRoles.Count > 0
                    || p.IncludeUsers.Any(u => names.ContainsKey(u));
                if ((r.RequiresMfa || r.MfaIsAlternative) && targetsAdmins)
                {
                    var reached = analysis.AdminMfa.Roles.Count(c => c.CoveringPolicyIds.Contains(p.Id));
                    var fullMfa = r.IsEnforced && r.RequiresMfa && r.CoversAllApplications && r.Narrowing.Count == 0;
                    var detail = !fullMfa
                        ? "Não conta como exigência para papéis administrativos: " + Gap(r) + "."
                        : reached > 0
                            ? $"Exige MFA em todas as aplicações e alcança membros de {reached} papel(éis) privilegiado(s)" + Exceptions(p, MemberLabel) + "."
                            : "Exige MFA em todas as aplicações" + (r.TargetsAllUsers ? " com alvo declarado em todos os usuários" : "")
                              + Exceptions(p, MemberLabel) + ", mas não alcança comprovadamente nenhum membro privilegiado.";
                    admin.Add(PolicyObject(r, detail));
                }

                if (r.IsEnforced && r.RequiresMfa)
                    baseline.Add(PolicyObject(r, BaselineDetail(r, analysis.Baseline, MemberLabel)));
            }

            foreach (var c in analysis.AdminMfa.Roles)
            {
                var roleName = c.Role.DisplayName ?? c.Role.TemplateId;
                var members = $"{c.Role.MemberCount} membro(s)";
                var detail = RoleDetail(c, MemberLabel, PolicyName);
                var obj = new KnightIndicatorObject(
                    c.State == RoleMfaCoverageState.NotCovered ? KnightObjectRelation.Affected : KnightObjectRelation.Evidence,
                    KnightAffectedObjectKind.DirectoryRole, c.Role.TemplateId, roleName, null, Array.Empty<string>(),
                    detail, $"Papel ativo · {members}");
                (c.State == RoleMfaCoverageState.NotCovered ? affectedRoles : admin).Add(obj);
            }

            if (result.DirectoryConfiguration?.PrivilegedRoles is null)
                adminLimitation = "O inventário de papéis privilegiados não foi coletado; a cobertura por papel não pôde ser listada.";
            else if (!analysis.AdminMfa.Evaluable)
                adminLimitation = analysis.AdminMfa.InconclusiveReason;
            legacyLimitation = analysis.LegacyAuth.InconclusiveReason;
            baselineLimitation = analysis.Baseline.InconclusiveReason;
        }
        else
        {
            legacyLimitation = adminLimitation = baselineLimitation =
                "As políticas de acesso condicional não foram coletadas nesta avaliação.";
        }

        map[LegacyAuthIndicator] = new KnightConfigurationObjects(Array.Empty<KnightIndicatorObject>(), legacy, true, legacyLimitation);
        map[AdminMfaIndicator] = new KnightConfigurationObjects(affectedRoles, admin, analysis?.AdminMfa.Evaluable ?? false, adminLimitation);
        map[BaselineIndicator] = new KnightConfigurationObjects(Array.Empty<KnightIndicatorObject>(), baseline, true, baselineLimitation);
        return map;
    }

    /// <summary>
    /// O papel em palavras, membro a membro: quem é coberto e por qual política, quem ficou de fora e por quê. O
    /// texto segue o estado do papel — um papel com exceções nunca é descrito como coberto.
    /// </summary>
    private static string RoleDetail(RoleMfaCoverage c, Func<string, string> member, Func<string, string> policy)
    {
        string Who(IEnumerable<MemberMfaCoverage> ms, bool withPolicies) => string.Join(", ", ms.Select(m =>
            member(m.MemberId) + (withPolicies && m.PolicyIds.Count > 0
                ? " (" + string.Join(", ", m.PolicyIds.Select(policy)) + ")"
                : "")));

        var covered = c.Members.Where(m => m.State == MemberMfaCoverageState.Covered).ToList();
        var excepted = c.Members.Where(m => m.State == MemberMfaCoverageState.Excepted).ToList();
        var unresolved = c.Members.Where(m => m.State == MemberMfaCoverageState.Unresolved).ToList();
        var notTargeted = c.Members.Where(m => m.State == MemberMfaCoverageState.NotTargeted).ToList();
        var parts = new List<string>();

        switch (c.State)
        {
            case RoleMfaCoverageState.NotCovered when c.Members.Count == 0:
                parts.Add("Nenhuma política habilitada exige MFA para este papel em todas as aplicações.");
                break;
            case RoleMfaCoverageState.NotCovered:
                parts.Add("Lacuna comprovada de exigência de MFA por política.");
                if (notTargeted.Count > 0)
                    parts.Add("Nenhuma política habilitada que exija MFA em todas as aplicações alcança: " + Who(notTargeted, false) + ".");
                if (excepted.Count > 0)
                    parts.Add((covered.Count == 0 && unresolved.Count == 0 ? "Nenhum membro é coberto — todos estão" : "Estão")
                        + " excluídos explicitamente das políticas que os alcançariam: " + Who(excepted, true) + ". " + ExceptionCaveat);
                if (covered.Count > 0)
                    parts.Add("Cobertos: " + Who(covered, false) + ".");
                break;
            case RoleMfaCoverageState.CoveredWithExceptions:
                parts.Add($"Coberto para {covered.Count} de {c.Members.Count} membro(s) por política habilitada que exige MFA em todas as aplicações.");
                parts.Add("Exceção(ões) sem outra política que exija MFA: " + Who(excepted, true) + ". " + ExceptionCaveat
                    + " Por isso o papel não é contado como coberto.");
                break;
            case RoleMfaCoverageState.Unresolved:
                parts.Add(unresolved.Count > 0
                    ? "Cobertura não resolvida para: " + Who(unresolved, true) + " — depende de grupo ou da condição de convidado, cujo pertencimento não é coletado."
                    : "Cobertura não resolvida — depende de grupo, cujo pertencimento não é coletado.");
                if (excepted.Count > 0)
                    parts.Add("Excluídos explicitamente, sem outra política: " + Who(excepted, true) + ". " + ExceptionCaveat);
                if (covered.Count > 0)
                    parts.Add("Cobertos: " + Who(covered, false) + ".");
                break;
            default:
                parts.Add(c.Members.Count > 0
                    ? $"Todos os {c.Members.Count} membro(s) são alcançados por política habilitada que exige MFA em todas as aplicações: "
                      + string.Join(", ", c.CoveringPolicyIds.Select(policy)) + "."
                    : $"Coberto por {c.CoveringPolicyIds.Count} política(s) habilitada(s) que miram o papel.");
                break;
        }

        parts.AddRange(c.Notes);
        return string.Join(" ", parts);
    }

    private static string LegacyDetail(
        ConditionalAccessPolicyReading r, LegacyAuthConclusion legacy, Func<string, string> member)
    {
        var p = r.Policy;
        var clients = ConditionalAccessAnalyzer.CoversAllLegacyClients(p) && !p.ClientAppTypes.Contains("exchangeActiveSync", StringComparer.OrdinalIgnoreCase)
            ? " (alcança todos os tipos de cliente, inclusive os legados)"
            : "";

        if (legacy.BlockingPolicyIds.Contains(p.Id))
        {
            if (!r.TargetsAllUsers)
                return "Bloqueia a autenticação legada para contas excluídas de outra política de bloqueio — cobre essas exceções" + clients + ".";
            return r.HasExclusions
                ? "Bloqueia a autenticação legada para todos os usuários e aplicações" + clients + Exceptions(p, member)
                  + "; cada exceção é coberta por outra política de bloqueio habilitada."
                : "Bloqueia a autenticação legada para todos os usuários e aplicações, sem exclusões" + clients + ".";
        }

        if (legacy.ExceptionPolicyIds.Contains(p.Id))
            return "Bloqueia a autenticação legada em todas as aplicações para todos os usuários" + clients + Exceptions(p, member)
                + " — nas exceções, o bloqueio não se aplica. " + ExceptionCaveat
                + (legacy.Blocked
                    ? " O bloqueio para todos é comprovado por outra política."
                    : " Sem outra política que cubra as exceções, o bloqueio para todos os usuários não é afirmado.");

        return "Não conta como bloqueio completo: " + LegacyGap(r) + ".";
    }

    private static string BaselineDetail(ConditionalAccessPolicyReading r, MfaBaselineConclusion baseline, Func<string, string> member)
    {
        var p = r.Policy;
        if (baseline.BaselinePolicyIds.Contains(p.Id))
            return "Sustenta a base mínima: exige MFA com alvo declarado em todos os usuários, em todas as aplicações e sem condição que a estreite"
                + Exceptions(p, member) + ".";
        if (baseline.UnresolvedPolicyIds.Contains(p.Id))
            return "Exige MFA em todas as aplicações, mas o alcance depende de grupo(s) cujo pertencimento não é coletado"
                + Exceptions(p, member) + " — não sustenta, sozinha, uma conclusão sobre o ambiente.";
        return "Não sustenta a base mínima: " + RestrictedScope(r) + ".";
    }

    private static string RestrictedScope(ConditionalAccessPolicyReading r)
    {
        var p = r.Policy;
        var reasons = new List<string>();
        if (!r.TargetsAllUsers)
        {
            var targets = new List<string>();
            var nominal = p.IncludeUsers.Count(u => !u.Equals("None", StringComparison.OrdinalIgnoreCase)
                && !u.Equals("GuestsOrExternalUsers", StringComparison.OrdinalIgnoreCase));
            if (nominal > 0) targets.Add($"{nominal} usuário(s) nominal(is)");
            if (p.IncludeRoles.Count > 0) targets.Add($"{p.IncludeRoles.Count} papel(éis)");
            if (p.IncludeGroups.Count > 0) targets.Add($"{p.IncludeGroups.Count} grupo(s)");
            reasons.Add("alvo restrito a " + (targets.Count == 0 ? "um subconjunto de usuários" : string.Join(", ", targets)));
        }
        if (!r.CoversAllApplications) reasons.Add("não alcança todas as aplicações");
        if (r.Narrowing.Count > 0) reasons.Add("estreitada por " + string.Join(", ", r.Narrowing));
        return reasons.Count == 0 ? "alcance restrito" : string.Join("; ", reasons);
    }

    private static KnightIndicatorObject PolicyObject(ConditionalAccessPolicyReading r, string detail) =>
        new(KnightObjectRelation.Evidence, KnightAffectedObjectKind.Policy, r.Policy.Id,
            r.Policy.DisplayName, null, Array.Empty<string>(), detail, r.Summary);

    private static string Exceptions(ConditionalAccessPolicyConfiguration p, Func<string, string> label)
    {
        var parts = new List<string>();
        var users = p.ExcludeUsers.Where(u => !u.Equals("GuestsOrExternalUsers", StringComparison.OrdinalIgnoreCase)).ToList();
        if (users.Count > 0)
            parts.Add($"{users.Count} usuário(s) excluído(s): " + string.Join(", ", users.Take(10).Select(label))
                + (users.Count > 10 ? "…" : ""));
        if (p.ExcludeGroups.Count > 0)
            parts.Add($"{p.ExcludeGroups.Count} grupo(s) excluído(s) — pertencimento não verificado nesta coleta");
        if (p.ExcludeRoles.Count > 0)
            parts.Add($"{p.ExcludeRoles.Count} papel(éis) excluído(s)");
        if (p.ExcludesGuestsOrExternalUsers || p.ExcludeUsers.Contains("GuestsOrExternalUsers", StringComparer.OrdinalIgnoreCase))
            parts.Add("convidados e usuários externos excluídos");
        return parts.Count == 0 ? "" : " (exceções: " + string.Join("; ", parts) + ")";
    }

    private static string Gap(ConditionalAccessPolicyReading r)
    {
        var reasons = new List<string>();
        if (r.Policy.State == ConditionalAccessPolicyState.ReportOnly) reasons.Add("somente relatório — não impõe");
        else if (r.Policy.State == ConditionalAccessPolicyState.Disabled) reasons.Add("desabilitada");
        else if (r.Policy.State == ConditionalAccessPolicyState.Unknown) reasons.Add("estado não reconhecido");
        if (r.MfaIsAlternative) reasons.Add("MFA é só uma alternativa (operador OU)");
        if (!r.CoversAllApplications) reasons.Add("não alcança todas as aplicações");
        if (r.Narrowing.Count > 0) reasons.Add("estreitada por " + string.Join(", ", r.Narrowing));
        return reasons.Count == 0 ? "alvo não inclui papéis administrativos" : string.Join("; ", reasons);
    }

    private static string LegacyGap(ConditionalAccessPolicyReading r)
    {
        var reasons = new List<string>();
        if (r.Policy.State == ConditionalAccessPolicyState.ReportOnly) reasons.Add("somente relatório — não bloqueia");
        else if (r.Policy.State == ConditionalAccessPolicyState.Disabled) reasons.Add("desabilitada");
        else if (r.Policy.State == ConditionalAccessPolicyState.Unknown) reasons.Add("estado não reconhecido");
        if (!ConditionalAccessAnalyzer.CoversAllLegacyClients(r.Policy))
            reasons.Add("não cobre Exchange ActiveSync e outros clientes ao mesmo tempo");
        if (!r.TargetsAllUsers) reasons.Add("não mira todos os usuários");
        if (!r.CoversAllApplications) reasons.Add("não alcança todas as aplicações");
        var cond = r.Narrowing.Where(n => n != "tipos de cliente restritos").ToList();
        if (cond.Count > 0) reasons.Add("estreitada por " + string.Join(", ", cond));
        return reasons.Count == 0 ? "alcance insuficiente" : string.Join("; ", reasons);
    }
}
