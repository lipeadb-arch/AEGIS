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
/// papéis sem exigência (afetados de AK-ENTRA-008), políticas relevantes e o estado dos security defaults
/// (evidências). Usa o MESMO analisador que produziu os sinais na coleta — a lista não pode divergir da contagem.
/// </summary>
public static class KnightConfigurationEvidence
{
    public const string LegacyAuthIndicator = "AK-ENTRA-007";
    public const string AdminMfaIndicator = "AK-ENTRA-008";
    public const string BaselineIndicator = "AK-ENTRA-014";

    /// <summary>Indicadores cujo detalhe vem da configuração observada.</summary>
    public static IReadOnlyList<string> Indicators { get; } = new[] { LegacyAuthIndicator, AdminMfaIndicator, BaselineIndicator };

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

        string? limitation = null;
        if (analysis is not null)
        {
            foreach (var r in analysis.Policies)
            {
                var p = r.Policy;
                var name = ConditionalAccessAnalyzer.Name(p);

                if (r.Blocks && TouchesLegacy(p))
                {
                    var full = analysis.LegacyAuth.BlockingPolicyIds.Contains(p.Id);
                    legacy.Add(PolicyObject(r, full
                        ? "Bloqueia a autenticação legada para todos os usuários e aplicações" + Exceptions(p, MemberLabel) + "."
                        : "Não conta como bloqueio completo: " + LegacyGap(r) + "."));
                }

                var targetsAdmins = r.TargetsAllUsers || p.IncludeRoles.Count > 0;
                if ((r.RequiresMfa || r.MfaIsAlternative) && targetsAdmins)
                {
                    var covering = analysis.AdminMfa.Roles.Where(c => c.CoveringPolicyIds.Contains(p.Id)
                        && c.State == RoleMfaCoverageState.Covered).ToList();
                    var detail = covering.Count > 0
                        ? $"Exige MFA em todas as aplicações para {covering.Count} papel(éis) privilegiado(s)" + Exceptions(p, MemberLabel) + "."
                        : r.IsEnforced && r.RequiresMfa && r.CoversAllApplications && r.Narrowing.Count == 0
                            ? "Exige MFA em todas as aplicações" + (r.TargetsAllUsers ? " para todos os usuários" : "") + Exceptions(p, MemberLabel) + "."
                            : "Não conta como exigência para papéis administrativos: " + Gap(r) + ".";
                    admin.Add(PolicyObject(r, detail));
                }

                if (r.IsEnforced && r.RequiresMfa)
                    baseline.Add(PolicyObject(r, "Política habilitada que exige MFA ou força de autenticação."));
            }

            foreach (var c in analysis.AdminMfa.Roles)
            {
                var roleName = c.Role.DisplayName ?? c.Role.TemplateId;
                var members = $"{c.Role.MemberCount} membro(s)";
                switch (c.State)
                {
                    case RoleMfaCoverageState.NotCovered:
                        affectedRoles.Add(new KnightIndicatorObject(
                            KnightObjectRelation.Affected, KnightAffectedObjectKind.DirectoryRole, c.Role.TemplateId,
                            roleName, null, Array.Empty<string>(),
                            "Nenhuma política habilitada exige MFA para este papel em todas as aplicações."
                            + (c.Notes.Count > 0 ? " " + string.Join(" ", c.Notes) : ""),
                            $"Papel ativo · {members}"));
                        break;
                    case RoleMfaCoverageState.Unresolved:
                        admin.Add(new KnightIndicatorObject(
                            KnightObjectRelation.Evidence, KnightAffectedObjectKind.DirectoryRole, c.Role.TemplateId,
                            roleName, null, Array.Empty<string>(),
                            "Cobertura não resolvida. " + string.Join(" ", c.Notes),
                            $"Papel ativo · {members}"));
                        break;
                    default:
                        var exc = c.ExceptionMemberIds.Count > 0
                            ? " Exceções declaradas (excluídas de todas as políticas que cobrem o papel): "
                              + string.Join(", ", c.ExceptionMemberIds.Select(MemberLabel)) + "."
                            : "";
                        admin.Add(new KnightIndicatorObject(
                            KnightObjectRelation.Evidence, KnightAffectedObjectKind.DirectoryRole, c.Role.TemplateId,
                            roleName, null, Array.Empty<string>(),
                            $"Coberto por {c.CoveringPolicyIds.Count} política(s) habilitada(s)." + exc
                            + (c.Notes.Count > 0 ? " " + string.Join(" ", c.Notes) : ""),
                            $"Papel ativo · {members}"));
                        break;
                }
            }

            if (result.DirectoryConfiguration?.PrivilegedRoles is null)
                limitation = "O inventário de papéis privilegiados não foi coletado; a cobertura por papel não pôde ser listada.";
        }
        else
        {
            limitation = "As políticas de acesso condicional não foram coletadas nesta avaliação.";
        }

        map[LegacyAuthIndicator] = new KnightConfigurationObjects(Array.Empty<KnightIndicatorObject>(), legacy, true, limitation);
        map[AdminMfaIndicator] = new KnightConfigurationObjects(affectedRoles, admin, analysis?.AdminMfa.Evaluable ?? false, limitation);
        map[BaselineIndicator] = new KnightConfigurationObjects(Array.Empty<KnightIndicatorObject>(), baseline, true, limitation);
        return map;
    }

    private static KnightIndicatorObject PolicyObject(ConditionalAccessPolicyReading r, string detail) =>
        new(KnightObjectRelation.Evidence, KnightAffectedObjectKind.Policy, r.Policy.Id,
            r.Policy.DisplayName, null, Array.Empty<string>(), detail, r.Summary);

    private static bool TouchesLegacy(ConditionalAccessPolicyConfiguration p) =>
        p.ClientAppTypes.Any(c => c.Equals("exchangeActiveSync", StringComparison.OrdinalIgnoreCase)
            || c.Equals("other", StringComparison.OrdinalIgnoreCase)
            || c.Equals("easSupported", StringComparison.OrdinalIgnoreCase));

    private static string Exceptions(ConditionalAccessPolicyConfiguration p, Func<string, string> label)
    {
        var parts = new List<string>();
        if (p.ExcludeUsers.Count > 0)
            parts.Add($"{p.ExcludeUsers.Count} usuário(s) excluído(s): " + string.Join(", ", p.ExcludeUsers.Take(10).Select(label))
                + (p.ExcludeUsers.Count > 10 ? "…" : ""));
        if (p.ExcludeGroups.Count > 0)
            parts.Add($"{p.ExcludeGroups.Count} grupo(s) excluído(s) — pertencimento não verificado nesta coleta");
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
        var types = r.Policy.ClientAppTypes;
        if (!(types.Contains("exchangeActiveSync", StringComparer.OrdinalIgnoreCase) && types.Contains("other", StringComparer.OrdinalIgnoreCase)))
            reasons.Add("não cobre Exchange ActiveSync e outros clientes ao mesmo tempo");
        if (!r.TargetsAllUsers) reasons.Add("não mira todos os usuários");
        if (r.Policy.ExcludeRoles.Count > 0) reasons.Add("exclui papéis");
        if (!r.CoversAllApplications) reasons.Add("não alcança todas as aplicações");
        var cond = r.Narrowing.Where(n => n != "tipos de cliente restritos").ToList();
        if (cond.Count > 0) reasons.Add("estreitada por " + string.Join(", ", cond));
        return reasons.Count == 0 ? "alcance insuficiente" : string.Join("; ", reasons);
    }
}
