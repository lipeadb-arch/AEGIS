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
// E devolve, papel a papel, se há exigência de MFA — COBERTO, NÃO COBERTO ou NÃO RESOLVIDO. "Não resolvido" é
// honesto quando a política mira GRUPOS cujo pertencimento esta entrega não coleta: nesse caso o indicador
// fica inconclusivo, e nunca aprovado ou reprovado por suposição.
//
// EXCLUSÕES de usuários/grupos numa política que cobre o papel são EXCEÇÕES DECLARADAS — o padrão para contas
// de emergência. Elas não reprovam o papel, mas são listadas nominalmente (quando o objeto é um membro
// privilegiado conhecido) para revisão. A exigência por POLÍTICA não comprova que cada autenticação aplicou o
// segundo fator — isso seria autenticação observada, que esta entrega não coleta.

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
    string Summary);

/// <summary>Situação da exigência de MFA para UM papel privilegiado.</summary>
public enum RoleMfaCoverageState
{
    /// <summary>Uma política habilitada exige MFA para o papel em todas as aplicações, sem condição que a estreite.</summary>
    Covered = 0,

    /// <summary>Nenhuma política habilitada exige MFA para o papel com esse alcance.</summary>
    NotCovered = 1,

    /// <summary>Há política que talvez o cubra, mas por GRUPO/usuário nominal — pertencimento não verificável.</summary>
    Unresolved = 2,
}

/// <summary>Como UM papel privilegiado está coberto — com as políticas e as exceções declaradas.</summary>
public sealed record RoleMfaCoverage(
    DirectoryRoleConfiguration Role,
    RoleMfaCoverageState State,
    IReadOnlyList<string> CoveringPolicyIds,
    IReadOnlyList<string> ExceptionMemberIds,
    IReadOnlyList<string> Notes);

/// <summary>Conclusão sobre a exigência de MFA administrativa.</summary>
/// <param name="Evaluable">FALSE quando os dados não permitem concluir (motivo em <paramref name="InconclusiveReason"/>).</param>
/// <param name="UncoveredRoleCount">Papéis privilegiados ativos sem exigência — o número que o indicador conta.</param>
/// <param name="Roles">Cobertura papel a papel (vazio quando o inventário de papéis não foi coletado).</param>
/// <param name="UniversalPolicyIds">Políticas que exigem MFA de TODOS os usuários, sem exceção de papel.</param>
public sealed record AdminMfaConclusion(
    bool Evaluable,
    string? InconclusiveReason,
    int UncoveredRoleCount,
    IReadOnlyList<RoleMfaCoverage> Roles,
    IReadOnlyList<string> UniversalPolicyIds);

/// <summary>Conclusão sobre o bloqueio de autenticação legada.</summary>
public sealed record LegacyAuthConclusion(
    bool Blocked,
    IReadOnlyList<string> BlockingPolicyIds,
    IReadOnlyList<string> PartialPolicyIds);

/// <summary>Resultado completo da leitura das políticas de uma coleta.</summary>
public sealed record ConditionalAccessAnalysis(
    IReadOnlyList<ConditionalAccessPolicyReading> Policies,
    AdminMfaConclusion AdminMfa,
    LegacyAuthConclusion LegacyAuth,
    int EnforcedMfaPolicyCount)
{
    public ConditionalAccessPolicyReading? Find(string policyId) =>
        Policies.FirstOrDefault(p => string.Equals(p.Policy.Id, policyId, StringComparison.Ordinal));
}

public static class ConditionalAccessAnalyzer
{
    /// <summary>Versão da REGRA de leitura — muda quando a interpretação das políticas muda.</summary>
    public const string RuleVersion = "aegis-ca-analysis-v1";

    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

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
            ConcludeLegacy(readings),
            readings.Count(r => r.IsEnforced && r.RequiresMfa));
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
        if (NarrowsModernClients(p.ClientAppTypes)) narrowing.Add("tipos de cliente restritos");

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
        if (clientAppTypes.Count == 0 || clientAppTypes.Contains("all", Ci)) return false;
        return !(clientAppTypes.Contains("browser", Ci) && clientAppTypes.Contains("mobileAppsAndDesktopClients", Ci));
    }

    private static bool IsLegacyBlock(ConditionalAccessPolicyConfiguration p) =>
        p.ClientAppTypes.Contains("exchangeActiveSync", Ci) && p.ClientAppTypes.Contains("other", Ci);

    private static bool TouchesLegacy(ConditionalAccessPolicyConfiguration p) =>
        p.ClientAppTypes.Contains("exchangeActiveSync", Ci) || p.ClientAppTypes.Contains("other", Ci)
        || p.ClientAppTypes.Contains("easSupported", Ci);

    // ---- MFA administrativa -------------------------------------------------------------------------

    private static AdminMfaConclusion ConcludeAdminMfa(
        IReadOnlyList<ConditionalAccessPolicyReading> readings, IReadOnlyList<DirectoryRoleConfiguration>? roles)
    {
        // Só uma política HABILITADA, que EXIGE MFA, em TODAS as aplicações e sem condição que a estreite,
        // conta como exigência. Somente relatório, desabilitada, "MFA ou outra coisa", aplicações parciais e
        // condições de localização/risco são registradas como notas — nunca como cobertura.
        var full = readings.Where(r => r.IsEnforced && r.RequiresMfa && r.CoversAllApplications && r.Narrowing.Count == 0).ToList();

        var universal = full
            .Where(r => r.TargetsAllUsers && r.Policy.ExcludeRoles.Count == 0)
            .Select(r => r.Policy.Id).ToList();

        if (roles is null)
        {
            // Sem inventário de papéis não dá para dizer QUAIS papéis estão cobertos. Só uma política universal
            // (todos os usuários, sem exclusão de papel) alcança todos eles por definição.
            return universal.Count > 0
                ? new AdminMfaConclusion(true, null, 0, Array.Empty<RoleMfaCoverage>(), universal)
                : new AdminMfaConclusion(false,
                    "O inventário de papéis privilegiados não foi coletado e nenhuma política exige MFA de todos os "
                    + "usuários — não é possível dizer quais papéis estão cobertos.",
                    0, Array.Empty<RoleMfaCoverage>(), universal);
        }

        var coverages = new List<RoleMfaCoverage>();
        foreach (var role in roles
                     .Where(r => r.MemberCount > 0)
                     .OrderBy(r => r.DisplayName ?? r.TemplateId, StringComparer.OrdinalIgnoreCase))
        {
            var covering = new List<string>();
            var exceptions = new SortedSet<string>(StringComparer.Ordinal);
            var unresolvedVia = new List<string>();
            var notes = new List<string>();

            foreach (var r in full)
            {
                var p = r.Policy;
                if (p.ExcludeRoles.Contains(role.TemplateId, Ci)) { notes.Add($"“{Name(p)}” exclui este papel."); continue; }

                if (r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci))
                {
                    covering.Add(p.Id);
                    foreach (var m in role.UserMemberIds.Where(m => p.ExcludeUsers.Contains(m, Ci))) exceptions.Add(m);
                    continue;
                }

                // Direcionada a grupos ou a usuários nominais: sem o pertencimento dos grupos, não há como saber
                // se os membros do papel estão lá. Usuários nominais cobrem o papel só se incluírem TODOS os membros.
                var nominalAll = role.UserMemberIds.Count > 0
                    && role.UserMemberIds.All(m => p.IncludeUsers.Contains(m, Ci));
                if (nominalAll && p.IncludeGroups.Count == 0) { covering.Add(p.Id); continue; }
                if (p.IncludeGroups.Count > 0 || p.IncludeUsers.Any(u => role.UserMemberIds.Contains(u, Ci)))
                    unresolvedVia.Add(p.Id);
            }

            if (covering.Count > 0)
            {
                // Exceções que TODAS as políticas que cobrem o papel fazem: um membro excluído de uma política mas
                // coberto por outra não é exceção de fato.
                var realExceptions = exceptions
                    .Where(m => covering.All(pid => readings.First(x => x.Policy.Id == pid).Policy.ExcludeUsers.Contains(m, Ci)))
                    .ToList();
                var groupExclusions = covering
                    .Select(pid => readings.First(x => x.Policy.Id == pid).Policy)
                    .Where(p => p.ExcludeGroups.Count > 0)
                    .Select(p => $"“{Name(p)}” exclui {p.ExcludeGroups.Count} grupo(s) — o pertencimento a esses grupos não é verificado nesta coleta.")
                    .ToList();
                notes.AddRange(groupExclusions);
                coverages.Add(new RoleMfaCoverage(role, RoleMfaCoverageState.Covered, covering, realExceptions, notes));
                continue;
            }

            // Não coberto: registra POR QUE as políticas candidatas não contam.
            foreach (var r in readings.Where(r => r.RequiresMfa || r.MfaIsAlternative))
            {
                var p = r.Policy;
                var targets = r.TargetsAllUsers || p.IncludeRoles.Contains(role.TemplateId, Ci);
                if (!targets || p.ExcludeRoles.Contains(role.TemplateId, Ci)) continue;
                if (full.Contains(r)) continue;
                notes.Add($"“{Name(p)}” não conta como exigência: {WhyNotEnforcing(r)}.");
            }

            coverages.Add(unresolvedVia.Count > 0
                ? new RoleMfaCoverage(role, RoleMfaCoverageState.Unresolved, unresolvedVia, Array.Empty<string>(),
                    notes.Append("Política direcionada a grupo(s) ou usuários nominais: o pertencimento não é coletado nesta entrega.").ToList())
                : new RoleMfaCoverage(role, RoleMfaCoverageState.NotCovered, Array.Empty<string>(), Array.Empty<string>(), notes));
        }

        var uncovered = coverages.Count(c => c.State == RoleMfaCoverageState.NotCovered);
        var unresolved = coverages.Count(c => c.State == RoleMfaCoverageState.Unresolved);

        // Um papel sem política é exposição comprovada, mesmo havendo outros não resolvidos. Só quando o que
        // falta é exclusivamente NÃO RESOLVIDO a conclusão fica em aberto.
        if (uncovered == 0 && unresolved > 0)
            return new AdminMfaConclusion(false,
                $"{unresolved} papel(éis) privilegiado(s) dependem de política direcionada a grupos ou usuários "
                + "nominais, cujo pertencimento não é coletado nesta entrega — a cobertura não pode ser afirmada nem negada.",
                0, coverages, universal);

        return new AdminMfaConclusion(true, null, uncovered, coverages, universal);
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

    private static LegacyAuthConclusion ConcludeLegacy(IReadOnlyList<ConditionalAccessPolicyReading> readings)
    {
        var blocking = new List<string>();
        var partial = new List<string>();
        foreach (var r in readings.Where(r => r.Blocks && TouchesLegacy(r.Policy)))
        {
            var full = r.IsEnforced && IsLegacyBlock(r.Policy) && r.TargetsAllUsers && r.CoversAllApplications
                && r.Narrowing.Where(n => n != "tipos de cliente restritos").Count() == 0
                && r.Policy.ExcludeRoles.Count == 0;
            (full ? blocking : partial).Add(r.Policy.Id);
        }
        return new LegacyAuthConclusion(blocking.Count > 0, blocking, partial);
    }

    // ---- Tradução em sinais do KNIGHT -----------------------------------------------------------------

    /// <summary>
    /// Os sinais que as regras do catálogo consomem. Políticas não coletadas → sinais AUSENTES (com o motivo
    /// da capacidade), nunca "nenhuma política". MFA administrativa não resolvida → sinal ausente com o motivo.
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
                KnightObservation.MissingData(KnightSignalKey.EnforcedMfaPolicies, reason),
            };
        }

        return new[]
        {
            KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, analysis.LegacyAuth.Blocked),
            analysis.AdminMfa.Evaluable
                ? KnightObservation.OfCount(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy, analysis.AdminMfa.UncoveredRoleCount)
                : KnightObservation.MissingData(KnightSignalKey.PrivilegedRolesWithoutMfaPolicy,
                    analysis.AdminMfa.InconclusiveReason ?? "Cobertura de MFA administrativa não resolvida."),
            KnightObservation.OfCount(KnightSignalKey.EnforcedMfaPolicies, analysis.EnforcedMfaPolicyCount),
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
        if (p.IncludesGuestsOrExternalUsers || p.IncludeUsers.Contains("GuestsOrExternalUsers", Ci)) users.Add("convidados/externos");
        var excl = new List<string>();
        if (p.ExcludeUsers.Count > 0) excl.Add($"{p.ExcludeUsers.Count} usuário(s)");
        if (p.ExcludeGroups.Count > 0) excl.Add($"{p.ExcludeGroups.Count} grupo(s)");
        if (p.ExcludeRoles.Count > 0) excl.Add($"{p.ExcludeRoles.Count} papel(éis)");
        if (p.ExcludesGuestsOrExternalUsers) excl.Add("convidados/externos");
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

        if (p.ClientAppTypes.Count > 0 && !p.ClientAppTypes.Contains("all", Ci))
            parts.Add("Clientes: " + string.Join(", ", p.ClientAppTypes.Select(ClientLabel)));
        parts.Add("Condições: " + (narrowing.Count(n => n != "tipos de cliente restritos") == 0
            ? "nenhuma que estreite"
            : string.Join(", ", narrowing.Where(n => n != "tipos de cliente restritos"))));

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
        || u.Equals("GuestsOrExternalUsers", StringComparison.OrdinalIgnoreCase);

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
