using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;
using static AegisScore.Application.Knight.KnightObjects;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-01] Controles de CONFIGURAÇÃO do Microsoft Entra ID
// ============================================================================
// Controles ORIGINAIS do AEGIS (textos e regras próprios) que cobrem os controles de referência de Entra ID dos
// benchmarks Microsoft 365 e Azure. Cada um lê a configuração RELIDA da aquisição do ADM e devolve o veredito e os
// objetos que o sustentam. Três regras valem para todos:
//   • dado ausente (permissão, licença, falha, coleta anterior) → NÃO AVALIADO com o motivo — nunca aprovado;
//   • configuração do locatário errada → a própria configuração é a evidência ("onde foi encontrado"); a contagem
//     de afetados só lista objetos concretos (aplicações, domínios, grupos, contas, papéis);
//   • critério que depende de contexto do cliente (ex.: contas de emergência) é dito no texto do achado.

public static class EntraConfigurationControls
{
    private static readonly IReadOnlySet<KnightSourceType> EntraOnly = new HashSet<KnightSourceType> { KnightSourceType.MicrosoftEntraId };

    private const string M365 = "CIS-M365-7.0.0:";
    private const string AZ = "CIS-AZ-6.0.0:";

    private static KnightReferenceLink Ref(string key, KnightReferenceMatch match = KnightReferenceMatch.Exact, string? note = null) =>
        new(key, match, note);

    /// <summary>Regra de fatos para os controles de configuração: sem o contexto da coleta, nada pode ser afirmado.</summary>
    private static KnightIndicatorOutcome FactsOnly(KnightFactSet _) =>
        new(KnightIndicatorStatus.NotEvaluated,
            "Não avaliado: este controle lê a configuração coletada do locatário, indisponível nesta avaliação.", 0,
            "Este controle lê a configuração coletada do locatário, indisponível nesta avaliação.");

    private static KnightIndicatorDefinition Control(
        string id, string title, KnightIndicatorCategory category, SeverityLevel severity,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate,
        params KnightReferenceLink[] references) =>
        new(id, "1", title, category, severity, EntraOnly, Array.Empty<string>(), Array.Empty<string>(),
            recommendation, criterion, FactsOnly)
        {
            Service = KnightService.EntraId,
            References = references,
            Evaluate = evaluate,
        };

    // ---- Leitores ------------------------------------------------------------------------------------

    private static KnightControlOutcome Single<T>(KnightEvaluationContext c, Func<T, KnightControlOutcome> rule) where T : class
    {
        var read = c.Configuration.Read<T>();
        if (!read.Collected) return KnightControlOutcome.NotEvaluated(read.MissingReason ?? "configuração não coletada.");
        return read.Single is { } doc
            ? rule(doc)
            : KnightControlOutcome.NotEvaluated("a fonte não devolveu esta configuração nesta coleta.");
    }

    private static KnightControlOutcome Many<T>(KnightEvaluationContext c, Func<IReadOnlyList<T>, KnightControlOutcome> rule) where T : class
    {
        var read = c.Configuration.Read<T>();
        return read.Collected
            ? rule(read.Items)
            : KnightControlOutcome.NotEvaluated(read.MissingReason ?? "configuração não coletada.");
    }

    /// <summary>Configuração booleana do locatário: valor esperado → aprovado; oposto → exposto; ausente → não avaliado.</summary>
    private static KnightControlOutcome Bool(
        bool? value, bool expected, string settingId, string settingLabel,
        string whenTrue, string whenFalse, string exposedEvidence, string passedEvidence)
    {
        if (value is null)
            return KnightControlOutcome.NotEvaluated($"a fonte não informou “{settingLabel}” nesta coleta.");
        var found = value.Value ? whenTrue : whenFalse;
        var expectedText = expected ? whenTrue : whenFalse;
        var obj = Setting(settingId, settingLabel, found, expectedText);
        return value.Value == expected
            ? KnightControlOutcome.Passed(passedEvidence, new[] { obj })
            : KnightControlOutcome.Exposed(exposedEvidence, Array.Empty<KnightIndicatorObject>(), new[] { obj });
    }

    private static string Membership(string? type) => type switch
    {
        "all" => "todos os usuários",
        "none" => "ninguém",
        "selected" => "usuários ou grupos selecionados",
        null => "não informado pela fonte",
        _ => type,
    };

    // ---- Autorização do diretório ---------------------------------------------------------------------

    private const string GuestSameAsMember = "a0b1b346-4d3e-4e8b-98f8-753987be4970";
    private const string GuestLimited = "10dae51f-b6af-4016-8d66-8c2a99b929b3";
    private const string GuestRestricted = "2af84b1e-32c8-42b7-82bc-daa82404023b";

    private static string GuestRole(string? id) => id?.ToLowerInvariant() switch
    {
        GuestSameAsMember => "mesmo acesso dos membros",
        GuestLimited => "acesso limitado a propriedades e associações de objetos do diretório",
        GuestRestricted => "acesso restrito às propriedades e associações dos próprios objetos",
        null => "não informado pela fonte",
        _ => $"valor não reconhecido ({id})",
    };

    private static string Invites(string? v) => v switch
    {
        "none" => "ninguém pode convidar",
        "adminsAndGuestInviters" => "somente administradores e o papel Emissor de Convites",
        "adminsGuestInvitersAndAllMembers" => "administradores, Emissor de Convites e todos os membros",
        "everyone" => "qualquer pessoa, inclusive convidados",
        null => "não informado pela fonte",
        _ => v,
    };

    // ---- Métodos de autenticação ----------------------------------------------------------------------

    private static string FeatureState(EntraAuthenticatorFeature? f) => f?.State switch
    {
        "enabled" => f.IncludeTargetId is "all_users" ? "habilitado para todos os usuários" : "habilitado para um grupo",
        "disabled" => "desabilitado",
        "default" => "gerenciado pela Microsoft (não fixado)",
        null => "não informado pela fonte",
        _ => f.State!,
    };

    private static bool FeatureEnforced(EntraAuthenticatorFeature? f) =>
        string.Equals(f?.State, "enabled", StringComparison.OrdinalIgnoreCase)
        && string.Equals(f?.IncludeTargetId, "all_users", StringComparison.OrdinalIgnoreCase);

    private static bool MethodEnabled(EntraAuthenticationMethodState? m) =>
        string.Equals(m?.State, "enabled", StringComparison.OrdinalIgnoreCase);

    // ---- Configurações de diretório ---------------------------------------------------------------------

    private static EntraDirectorySettingConfiguration? Template(IReadOnlyList<EntraDirectorySettingConfiguration> all, string templateId) =>
        all.FirstOrDefault(s => string.Equals(s.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));

    private static bool? ParseBool(string? v) =>
        bool.TryParse(v, out var b) ? b : null;

    private static int? ParseInt(string? v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Origin(EntraDirectorySettingConfiguration s, string name) =>
        s.IsDefault(name) ? " (padrão do modelo — não personalizado)" : "";

    // ---- Política de aplicações --------------------------------------------------------------------------

    private static EntraAppCredentialRestriction? Restriction(EntraAppManagementPolicyConfiguration p, string type) =>
        p.PasswordCredentials.FirstOrDefault(r => string.Equals(r.RestrictionType, type, StringComparison.OrdinalIgnoreCase));

    private static bool RestrictionOn(EntraAppCredentialRestriction? r) =>
        r is not null && !string.Equals(r.State, "disabled", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? Duration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        try { return XmlConvert.ToTimeSpan(iso); } catch (FormatException) { return null; }
    }

    private static KnightControlOutcome AppPolicy(
        KnightEvaluationContext c, string restrictionType, string label, Func<EntraAppCredentialRestriction, string?> gap,
        string exposed, string passed) =>
        Single<EntraAppManagementPolicyConfiguration>(c, p =>
        {
            var r = Restriction(p, restrictionType);
            var found = p.IsEnabled != true
                ? "política padrão desabilitada"
                : r is null ? "restrição não configurada"
                : !RestrictionOn(r) ? "restrição desabilitada"
                : gap(r) ?? "restrição habilitada";
            var ok = p.IsEnabled == true && RestrictionOn(r) && gap(r!) is null;
            var obj = Setting("defaultAppManagementPolicy/" + restrictionType, label, found, "restrição habilitada na política padrão");
            return ok
                ? KnightControlOutcome.Passed(passed, new[] { obj })
                : KnightControlOutcome.Exposed(exposed + " Encontrado: " + found + ".", Array.Empty<KnightIndicatorObject>(), new[] { obj });
        });

    // ---- Acesso condicional ------------------------------------------------------------------------------

    private static string? CaMissing(KnightEvaluationContext c)
    {
        var cap = c.Capability(KnightCapability.ConditionalAccessPolicies);
        return cap is null ? null : cap.Outcome == KnightCapabilityOutcome.Collected ? null : cap.Detail;
    }

    private static KnightIndicatorObject PolicyEvidence(ConditionalAccessPolicyReading r, string detail) =>
        new(KnightObjectRelation.Evidence, KnightAffectedObjectKind.Policy, r.Policy.Id, r.Policy.DisplayName, null,
            Array.Empty<string>(), detail, r.Summary);

    private static KnightControlOutcome AllUsersRequirement(
        KnightEvaluationContext c, CaRequirement req, string passed, string exposed)
    {
        var e = ConditionalAccessRequirements.ForAllUsers(c.Directory, req, CaMissing(c), c.MemberLabel);
        var evidence = e.Satisfying.Select(r => PolicyEvidence(r, $"Aplica {req.Label} para todos os usuários"
                + (r.HasExclusions ? ", com exceções listadas na configuração." : ", sem exceções.")))
            .Concat(e.NotCounting.Select(x => PolicyEvidence(x.Policy, $"Não conta para {req.Label}: {x.Why}.")))
            .ToList();
        return e.State switch
        {
            CaCoverageState.Satisfied => KnightControlOutcome.Passed(passed, evidence),
            CaCoverageState.NotSatisfied => KnightControlOutcome.Exposed(
                exposed + (e.NotCounting.Count > 0
                    ? $" {ConditionalAccessAnalyzer.Plural(e.NotCounting.Count, "política trata do tema, mas não conta", "políticas tratam do tema, mas não contam")} (motivos na evidência)."
                    : " Nenhuma política trata do tema."),
                Array.Empty<KnightIndicatorObject>(), evidence),
            _ => KnightControlOutcome.NotEvaluated(e.Reason ?? "cobertura não resolvida.", evidence),
        };
    }

    private static KnightControlOutcome PrivilegedRequirement(
        KnightEvaluationContext c, CaRequirement req, string passed, string exposed)
    {
        var e = ConditionalAccessRequirements.ForPrivilegedRoles(c.Directory, req, CaMissing(c));
        var evidence = e.Satisfying.Select(r => PolicyEvidence(r, $"Aplica {req.Label}."))
            .Concat(e.NotCounting.Select(x => PolicyEvidence(x.Policy, $"Não conta para {req.Label}: {x.Why}.")))
            .ToList();
        var affected = new List<KnightIndicatorObject>();
        foreach (var role in e.Roles)
        {
            var name = role.Role.DisplayName ?? role.Role.TemplateId;
            var notTargeted = role.Members.Where(m => m.State == MemberMfaCoverageState.NotTargeted).Select(m => c.MemberLabel(m.MemberId)).ToList();
            var excepted = role.Members.Where(m => m.State == MemberMfaCoverageState.Excepted).Select(m => c.MemberLabel(m.MemberId)).ToList();
            var detail = role.State switch
            {
                RoleMfaCoverageState.Covered => "Todos os membros conhecidos são alcançados por política habilitada que aplica " + req.Label + ".",
                RoleMfaCoverageState.NotCovered when notTargeted.Count > 0 =>
                    "Lacuna comprovada: nenhuma política habilitada que aplica " + req.Label + " alcança " + string.Join(", ", notTargeted) + ".",
                RoleMfaCoverageState.NotCovered => "Lacuna comprovada: nenhum membro é alcançado por política habilitada que aplica " + req.Label + ".",
                RoleMfaCoverageState.CoveredWithExceptions => "Exceções sem outra cobertura: " + string.Join(", ", excepted) + ".",
                _ => "Cobertura não resolvida — depende de grupo(s) cujo pertencimento não é coletado.",
            };
            var obj = new KnightIndicatorObject(
                role.State == RoleMfaCoverageState.NotCovered ? KnightObjectRelation.Affected : KnightObjectRelation.Evidence,
                KnightAffectedObjectKind.DirectoryRole, role.Role.TemplateId, name, null, Array.Empty<string>(), detail,
                $"Papel ativo · {role.Role.MemberCount} membro(s)");
            if (role.State == RoleMfaCoverageState.NotCovered) affected.Add(obj); else evidence.Add(obj);
        }

        return e.State switch
        {
            CaCoverageState.Satisfied => KnightControlOutcome.Passed(passed, evidence),
            CaCoverageState.NotSatisfied => KnightControlOutcome.Exposed(
                $"{exposed} {ConditionalAccessAnalyzer.Plural(e.UncoveredRoles, "papel privilegiado ativo tem", "papéis privilegiados ativos têm")} membro(s) sem a exigência (nomeados na evidência).",
                affected, evidence),
            _ => KnightControlOutcome.NotEvaluated(e.Reason ?? "cobertura não resolvida.", evidence.Concat(affected.Select(a => a with { Relation = KnightObjectRelation.Evidence })).ToList()),
        };
    }

    private static bool AdminAppsCovered(ConditionalAccessPolicyConfiguration p) =>
        p.IncludeApplications.Contains("All", StringComparer.OrdinalIgnoreCase)
        && !p.ExcludeApplications.Contains("MicrosoftAdminPortals", StringComparer.OrdinalIgnoreCase)
        && !p.ExcludeApplications.Contains("Office365", StringComparer.OrdinalIgnoreCase);

    private static string? StrongGrant(ConditionalAccessPolicyReading r) =>
        r.RequiresMfa ? null : r.MfaIsAlternative ? "MFA é apenas uma alternativa (operador OU)" : "não exige MFA nem força de autenticação";

    private static string? EveryTime(ConditionalAccessPolicyConfiguration p) =>
        p.SignInFrequencyEnabled == true && string.Equals(p.SignInFrequencyInterval, "everyTime", StringComparison.OrdinalIgnoreCase)
            ? null : "sem frequência de entrada “a cada vez”";

    private static bool Contains(IReadOnlyList<string>? list, string value) =>
        list is not null && list.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool TransfersBlocked(ConditionalAccessPolicyConfiguration p, string method) =>
        (p.AuthenticationFlowsTransferMethods ?? "").Split(',').Any(m => string.Equals(m.Trim(), method, StringComparison.OrdinalIgnoreCase));

    private static readonly CaRequirement SignInFrequencyAdmins = new(
        "frequência de entrada de até 4 horas com sessão de navegador não persistente",
        r => r.Policy.SignInFrequencyEnabled == true || r.Policy.PersistentBrowserEnabled == true,
        r =>
        {
            var p = r.Policy;
            var gaps = new List<string>();
            if (!AdminAppsCovered(p)) gaps.Add("não alcança todas as aplicações (ou exclui portais administrativos/Office 365)");
            if (r.Narrowing.Count > 0) gaps.Add("estreitada por " + string.Join(", ", r.Narrowing));
            var hours = string.Equals(p.SignInFrequencyType, "days", StringComparison.OrdinalIgnoreCase) ? p.SignInFrequencyValue * 24 : p.SignInFrequencyValue;
            if (p.SignInFrequencyEnabled != true || hours is null || hours > 4) gaps.Add("frequência de entrada ausente ou acima de 4 horas");
            if (p.PersistentBrowserEnabled != true || !string.Equals(p.PersistentBrowserMode, "never", StringComparison.OrdinalIgnoreCase))
                gaps.Add("sessão de navegador não configurada como nunca persistente");
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement PhishingResistantAdmins = new(
        "MFA resistente a phishing em todas as aplicações",
        r => !string.IsNullOrWhiteSpace(r.Policy.AuthenticationStrengthId) || r.RequiresMfa || r.MfaIsAlternative,
        r =>
        {
            var gaps = new List<string>();
            if (!AdminAppsCovered(r.Policy)) gaps.Add("não alcança todas as aplicações");
            if (r.Narrowing.Count > 0) gaps.Add("estreitada por " + string.Join(", ", r.Narrowing));
            if (!ConditionalAccessRequirements.RequiresPhishingResistant(r.Policy)) gaps.Add("não exige força de autenticação resistente a phishing");
            else if (r.MfaIsAlternative) gaps.Add("a força de autenticação é só uma alternativa (operador OU)");
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement UserRiskPolicy = new(
        "resposta a risco alto de usuário (troca de senha com MFA e reautenticação)",
        r => r.Policy.UserRiskLevels is { Count: > 0 },
        r =>
        {
            var p = r.Policy;
            var gaps = new List<string>();
            if (!AdminAppsCovered(p)) gaps.Add("não alcança todas as aplicações");
            if (!Contains(p.UserRiskLevels, "high")) gaps.Add("não inclui o risco alto de usuário");
            if (!ConditionalAccessRequirements.HasControl(p, "passwordChange")) gaps.Add("não exige troca de senha");
            if (StrongGrant(r) is { } g) gaps.Add(g);
            if (EveryTime(p) is { } t) gaps.Add(t);
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement SignInRiskPolicy = new(
        "exigência de MFA para entradas de risco médio e alto",
        r => r.Policy.SignInRiskLevels is { Count: > 0 } && !r.Blocks,
        r =>
        {
            var p = r.Policy;
            var gaps = new List<string>();
            if (!AdminAppsCovered(p)) gaps.Add("não alcança todas as aplicações");
            if (!Contains(p.SignInRiskLevels, "high") || !Contains(p.SignInRiskLevels, "medium")) gaps.Add("não inclui os riscos médio e alto de entrada");
            if (StrongGrant(r) is { } g) gaps.Add(g);
            if (EveryTime(p) is { } t) gaps.Add(t);
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement BlockSignInRisk = new(
        "bloqueio de entradas de risco médio e alto",
        r => r.Policy.SignInRiskLevels is { Count: > 0 } && r.Blocks,
        r =>
        {
            var p = r.Policy;
            var gaps = new List<string>();
            if (!AdminAppsCovered(p)) gaps.Add("não alcança todas as aplicações");
            if (!Contains(p.SignInRiskLevels, "high") || !Contains(p.SignInRiskLevels, "medium")) gaps.Add("não inclui os riscos médio e alto de entrada");
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static bool ManagedDeviceGrant(ConditionalAccessPolicyConfiguration p) =>
        ConditionalAccessRequirements.HasControl(p, "compliantDevice") || ConditionalAccessRequirements.HasControl(p, "domainJoinedDevice");

    private static string? DeviceOnlyGrant(ConditionalAccessPolicyReading r)
    {
        var p = r.Policy;
        if (!ManagedDeviceGrant(p)) return "não exige dispositivo em conformidade ou ingressado no domínio";
        var isOr = string.Equals(p.GrantOperator, "OR", StringComparison.OrdinalIgnoreCase);
        var others = p.BuiltInControls.Where(x => !x.Equals("compliantDevice", StringComparison.OrdinalIgnoreCase)
                                                  && !x.Equals("domainJoinedDevice", StringComparison.OrdinalIgnoreCase)).ToList();
        if (isOr && (others.Count > 0 || !string.IsNullOrWhiteSpace(p.AuthenticationStrengthId)))
            return "o dispositivo gerenciado é só uma alternativa (operador OU com outro controle)";
        return null;
    }

    private static readonly CaRequirement ManagedDevice = new(
        "exigência de dispositivo gerenciado em todas as aplicações",
        r => ManagedDeviceGrant(r.Policy) && r.Policy.IncludeUserActions.Count == 0,
        r =>
        {
            var gaps = new List<string>();
            if (!AdminAppsCovered(r.Policy)) gaps.Add("não alcança todas as aplicações");
            if (r.Narrowing.Count > 0) gaps.Add("estreitada por " + string.Join(", ", r.Narrowing));
            if (DeviceOnlyGrant(r) is { } g) gaps.Add(g);
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement ManagedDeviceForSecurityInfo = new(
        "exigência de dispositivo gerenciado para registrar informações de segurança",
        r => r.Policy.IncludeUserActions.Contains(ConditionalAccessRequirements.RegisterSecurityInfoAction, StringComparer.OrdinalIgnoreCase),
        r => DeviceOnlyGrant(r) is { } g ? g : r.Narrowing.Count > 0 ? "estreitada por " + string.Join(", ", r.Narrowing) : null,
        NeedsV2: false);

    private static readonly CaRequirement IntuneEnrollmentEveryTime = new(
        "reautenticação a cada vez no registro do Intune",
        r => r.Policy.IncludeApplications.Contains(ConditionalAccessRequirements.IntuneEnrollmentAppId, StringComparer.OrdinalIgnoreCase)
             || (r.Policy.IncludeApplications.Contains("All", StringComparer.OrdinalIgnoreCase) && r.Policy.SignInFrequencyEnabled == true),
        r =>
        {
            var gaps = new List<string>();
            if (r.Policy.ExcludeApplications.Contains(ConditionalAccessRequirements.IntuneEnrollmentAppId, StringComparer.OrdinalIgnoreCase))
                gaps.Add("exclui o registro do Intune");
            if (StrongGrant(r) is { } g) gaps.Add(g);
            if (EveryTime(r.Policy) is { } t) gaps.Add(t);
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static CaRequirement BlockTransfer(string method, string label) => new(
        label,
        r => TransfersBlocked(r.Policy, method),
        r =>
        {
            var gaps = new List<string>();
            if (!r.Blocks) gaps.Add("não bloqueia o acesso");
            if (!AdminAppsCovered(r.Policy)) gaps.Add("não alcança todas as aplicações");
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement PeriodicReauthentication = new(
        "reautenticação periódica de até 7 dias",
        r => r.Policy.SignInFrequencyEnabled == true,
        r =>
        {
            var p = r.Policy;
            var gaps = new List<string>();
            if (!AdminAppsCovered(p)) gaps.Add("não alcança todas as aplicações");
            if (r.Narrowing.Count > 0) gaps.Add("estreitada por " + string.Join(", ", r.Narrowing));
            var everyTime = string.Equals(p.SignInFrequencyInterval, "everyTime", StringComparison.OrdinalIgnoreCase);
            var days = string.Equals(p.SignInFrequencyType, "hours", StringComparison.OrdinalIgnoreCase) ? p.SignInFrequencyValue / 24.0 : p.SignInFrequencyValue;
            if (!everyTime && (days is null || days > 7)) gaps.Add("intervalo acima de 7 dias");
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    private static readonly CaRequirement IdleSessionRestriction = new(
        "restrições impostas pelo aplicativo (sessão ociosa) no navegador",
        r => r.Policy.ApplicationEnforcedRestrictions == true,
        r =>
        {
            var p = r.Policy;
            var gaps = new List<string>();
            var apps = p.IncludeApplications.Contains("All", StringComparer.OrdinalIgnoreCase)
                       || p.IncludeApplications.Contains("Office365", StringComparer.OrdinalIgnoreCase);
            if (!apps || p.ExcludeApplications.Contains("Office365", StringComparer.OrdinalIgnoreCase)) gaps.Add("não alcança o Office 365");
            var browser = p.ClientAppTypes.Count == 0 || Contains(p.ClientAppTypes, "all") || Contains(p.ClientAppTypes, "browser");
            if (!browser) gaps.Add("não alcança o navegador");
            return gaps.Count == 0 ? null : string.Join("; ", gaps);
        });

    // ---- Papéis --------------------------------------------------------------------------------------

    public const string GlobalAdministratorTemplateId = "62e90394-69f5-4237-9190-012177145e10";
    public const string PrivilegedRoleAdministratorTemplateId = "e8611ab8-c189-46e8-94e1-60213ab1f814";
    public const string TenantCreatorTemplateId = "112ca1a2-15ad-4102-995e-45b0bc479a6a";

    /// <summary>
    /// Papéis administrativos marcados como PRIVILEGIADOS na referência oficial de papéis do Microsoft Entra, com
    /// permissão de alteração (leitores ficam de fora). É o conjunto que o controle de PIM examina: a classificação
    /// <c>isPrivileged</c> dos papéis só existe na versão beta do Microsoft Graph, cujo uso em produção a Microsoft
    /// não suporta — por isso a lista é fixada aqui, com os identificadores de modelo conferidos na documentação.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PrivilegedRoleTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [GlobalAdministratorTemplateId] = "Administrador Global",
        [PrivilegedRoleAdministratorTemplateId] = "Administrador de Função Privilegiada",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Administrador de Autenticação Privilegiada",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Administrador de Segurança",
        ["29232cdf-9323-42fd-ade2-1d097af3e4de"] = "Administrador do Exchange",
        ["f28a1f50-f6e7-4571-818b-6a12f2af6b6c"] = "Administrador do SharePoint",
        ["69091246-20e8-4a56-aa4d-066075b2a7a8"] = "Administrador do Teams",
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "Administrador de Usuários",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Administrador de Aplicativos",
        ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Administrador de Aplicativos de Nuvem",
        ["b1be1c3e-b65d-4f19-8427-f6fa0d97feb9"] = "Administrador de Acesso Condicional",
        ["c4e39bd9-1100-46d3-8c65-fb160da0071f"] = "Administrador de Autenticação",
        ["729827e3-9c14-49f7-bb1b-9608f156bbb8"] = "Administrador de Assistência Técnica",
        ["3a2c62db-5318-420d-8d74-23affee5d9d5"] = "Administrador do Intune",
        ["8ac3fc64-6eca-42ea-9e69-59f4c7b60eb2"] = "Administrador de Identidade Híbrida",
    };

    /// <summary>Papéis altamente privilegiados cujas atribuições a referência pede em revisão de acesso.</summary>
    public static readonly IReadOnlyList<(string TemplateId, string Name, string Variant)> ReviewedRoles = new[]
    {
        (GlobalAdministratorTemplateId, "Administrador Global", "global-administrator"),
        ("69091246-20e8-4a56-aa4d-066075b2a7a8", "Administrador do Teams", "teams-administrator"),
        ("f28a1f50-f6e7-4571-818b-6a12f2af6b6c", "Administrador do SharePoint", "sharepoint-administrator"),
        ("29232cdf-9323-42fd-ade2-1d097af3e4de", "Administrador do Exchange", "exchange-administrator"),
        ("194ae4cb-b126-40b2-bd5b-6091b380977d", "Administrador de Segurança", "security-administrator"),
    };

    // ---- Revisões de acesso ---------------------------------------------------------------------------
    // Localizar uma revisão não comprova o critério: o ESCOPO dela precisa alcançar a população exigida e a série
    // precisa estar vigente, ser bastante frequente, ter revisores e remover o acesso negado ao aplicar. A leitura
    // do escopo e da recorrência vive em EntraAccessReviewCoverage, sobre o que a coleta preservou.

    /// <summary>O que UMA revisão sustenta: comprova o critério, reprova de forma demonstrável, ou não permite dizer.</summary>
    private enum ReviewState { Proven, Failed, Undetermined }

    private sealed record AssessedReview(EntraAccessReviewDefinition Def, KnightAccessReviewScope Scope, ReviewState State, string Reason);

    private static AssessedReview Assess(EntraAccessReviewDefinition d, KnightAccessReviewScope scope, DateTimeOffset at)
    {
        if (scope.Reach == KnightAccessReviewReach.Limited) return new(d, scope, ReviewState.Failed, scope.Description);
        if (scope.Reach == KnightAccessReviewReach.Undetermined) return new(d, scope, ReviewState.Undetermined, scope.Description);
        var criteria = EntraAccessReviewCoverage.Criteria(d, at);
        return criteria.State switch
        {
            KnightAccessReviewCriteria.Meets => new(d, scope, ReviewState.Proven, $"{scope.Description}, com {criteria.Reason}"),
            KnightAccessReviewCriteria.Fails => new(d, scope, ReviewState.Failed, criteria.Reason),
            _ => new(d, scope, ReviewState.Undetermined, criteria.Reason),
        };
    }

    private static string ReviewName(EntraAccessReviewDefinition d) => $"“{d.DisplayName ?? d.Id}” ({d.Id})";

    /// <summary>Motivos curtos, para o resumo da lista.</summary>
    private static string Reasons(IEnumerable<AssessedReview> reviews) =>
        string.Join("; ", reviews.Select(r => $"{ReviewName(r.Def)} — {r.Reason}"));

    /// <summary>Os mesmos motivos com as consultas de escopo lidas, para a expansão e as exportações.</summary>
    private static string ReasonsWithScope(IEnumerable<AssessedReview> reviews) =>
        string.Join("; ", reviews.Select(r => $"{ReviewName(r.Def)} — {r.Reason}"
            + (r.Scope.Queries is { Length: > 0 } q ? $" (escopo lido: {q})" : "")));

    /// <summary>O detalhe da expansão: o veredito, o motivo, o resumo da série e as consultas de escopo lidas.</summary>
    private static KnightIndicatorObject ReviewEvidence(AssessedReview r) =>
        Evidence(KnightAffectedObjectKind.Policy, r.Def.Id, r.Def.DisplayName,
            (r.State switch
            {
                ReviewState.Proven => "Comprova o critério: ",
                ReviewState.Failed => "Não atende ao critério: ",
                _ => "Não foi possível comprovar: ",
            }) + r.Reason + ". " + EntraAccessReviewCoverage.Summary(r.Def)
            + (r.Scope.Queries is { Length: > 0 } q ? " · escopo lido — " + q : ""),
            "Revisão de acesso");

    // ---- Catálogo ------------------------------------------------------------------------------------

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        // ==== Política de autorização ====
        Control("AK-ENTRA-016", "Usuários podem registrar aplicações", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Desabilitar o registro de aplicações por usuários comuns e delegar a criação ao papel Desenvolvedor de Aplicativos ou a um processo aprovado.",
            "Política de autorização com “usuários podem registrar aplicações” = Não.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p => Bool(p.AllowedToCreateApps, false,
                "authorizationPolicy/allowedToCreateApps", "Usuários podem registrar aplicações", "Sim", "Não",
                "Qualquer usuário pode registrar aplicações no diretório.", "Somente papéis autorizados registram aplicações.")),
            Ref(M365 + "5.1.2.2")),

        Control("AK-ENTRA-017", "Usuários sem papel administrativo podem criar locatários", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.High,
            "Restringir a criação de locatários a administradores (política de autorização: criação de locatários por usuários = Não).",
            "Política de autorização com “usuários podem criar locatários” = Não.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p => Bool(p.AllowedToCreateTenants, false,
                "authorizationPolicy/allowedToCreateTenants", "Usuários podem criar locatários", "Sim", "Não",
                "Usuários comuns podem criar locatários do Microsoft Entra fora da governança da organização.",
                "A criação de locatários está restrita a administradores.")),
            Ref(M365 + "5.1.2.3")),

        Control("AK-ENTRA-018", "Usuários podem criar grupos de segurança", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Restringir a criação de grupos de segurança a administradores e processos aprovados.",
            "Política de autorização com “usuários podem criar grupos de segurança” = Não.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p => Bool(p.AllowedToCreateSecurityGroups, false,
                "authorizationPolicy/allowedToCreateSecurityGroups", "Usuários podem criar grupos de segurança", "Sim", "Não",
                "Usuários comuns podem criar grupos de segurança.", "A criação de grupos de segurança está restrita.")),
            Ref(M365 + "5.1.3.1")),

        Control("AK-ENTRA-019", "Usuários podem recuperar chaves do BitLocker dos próprios dispositivos", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Impedir que usuários leiam as chaves de recuperação do BitLocker dos próprios dispositivos; manter a recuperação com o suporte.",
            "Política de autorização com “usuários podem ler chaves do BitLocker dos próprios dispositivos” = Não.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p => Bool(p.AllowedToReadBitlockerKeysForOwnedDevice, false,
                "authorizationPolicy/allowedToReadBitlockerKeysForOwnedDevice", "Usuários leem chaves do BitLocker dos próprios dispositivos", "Sim", "Não",
                "Usuários podem obter por conta própria a chave de recuperação do BitLocker dos seus dispositivos.",
                "A recuperação de chaves do BitLocker não está disponível ao próprio usuário.")),
            Ref(M365 + "5.1.4.6")),

        Control("AK-ENTRA-020", "Consentimento de usuários a aplicações permitido", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.Medium,
            "Configurar “não permitir o consentimento do usuário” e tratar pedidos pelo fluxo de consentimento do administrador.",
            "Nenhuma política de concessão “ManagePermissionGrantsForSelf” atribuída aos usuários.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p =>
            {
                var self = p.PermissionGrantPoliciesAssigned
                    .Where(x => x.StartsWith("ManagePermissionGrantsForSelf.", StringComparison.OrdinalIgnoreCase)).ToList();
                var found = self.Count == 0 ? "não permitido" : "permitido (" + string.Join(", ", self) + ")";
                var obj = Setting("authorizationPolicy/permissionGrantPoliciesAssigned", "Consentimento do usuário a aplicações", found, "não permitido");
                return self.Count == 0
                    ? KnightControlOutcome.Passed("Usuários não podem consentir o acesso de aplicações a dados da organização.", new[] { obj })
                    : KnightControlOutcome.Exposed("Usuários podem consentir, em nome próprio, o acesso de aplicações a dados da organização.",
                        Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.1.5.1")),

        Control("AK-ENTRA-021", "Acesso de convidados ao diretório não restrito", KnightIndicatorCategory.GuestAccess, SeverityLevel.High,
            "Configurar o acesso de convidados como limitado ou restrito às propriedades dos próprios objetos.",
            "Política de autorização com papel de convidado “acesso limitado” ou “acesso restrito”.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p =>
            {
                var id = p.GuestUserRoleId?.ToLowerInvariant();
                var obj = Setting("authorizationPolicy/guestUserRoleId", "Permissões de convidados", GuestRole(id), "acesso limitado ou restrito");
                return id switch
                {
                    GuestLimited or GuestRestricted => KnightControlOutcome.Passed("Convidados têm acesso limitado ao diretório.", new[] { obj }),
                    GuestSameAsMember => KnightControlOutcome.Exposed("Convidados têm o mesmo acesso ao diretório que os membros.",
                        Array.Empty<KnightIndicatorObject>(), new[] { obj }),
                    _ => KnightControlOutcome.NotEvaluated("o papel de convidado devolvido pela fonte não é reconhecido.", new[] { obj }),
                };
            }),
            Ref(M365 + "5.1.6.2")),

        Control("AK-ENTRA-022", "Convites de convidados não restritos a papéis administrativos", KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium,
            "Permitir convites somente a administradores e ao papel Emissor de Convites (ou a ninguém).",
            "Política de autorização com convites permitidos a “administradores e Emissor de Convites” ou “ninguém”.",
            c => Single<EntraAuthorizationPolicyConfiguration>(c, p =>
            {
                var obj = Setting("authorizationPolicy/allowInvitesFrom", "Quem pode convidar convidados", Invites(p.AllowInvitesFrom),
                    "somente administradores e Emissor de Convites");
                return p.AllowInvitesFrom switch
                {
                    "adminsAndGuestInviters" or "none" => KnightControlOutcome.Passed("Convites de convidados restritos a papéis autorizados.", new[] { obj }),
                    "adminsGuestInvitersAndAllMembers" or "everyone" => KnightControlOutcome.Exposed(
                        "Convites de convidados não estão restritos a papéis administrativos.", Array.Empty<KnightIndicatorObject>(), new[] { obj }),
                    _ => KnightControlOutcome.NotEvaluated("a fonte não informou quem pode convidar convidados.", new[] { obj }),
                };
            }),
            Ref(M365 + "5.1.6.3")),

        // ==== Consentimento do administrador ====
        Control("AK-ENTRA-023", "Fluxo de consentimento do administrador desabilitado", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Habilitar o fluxo de consentimento do administrador com revisores definidos, para que pedidos de acesso sejam avaliados em vez de negados silenciosamente ou concedidos sem análise.",
            "Fluxo de consentimento do administrador habilitado com ao menos um revisor.",
            c => Single<EntraAdminConsentPolicyConfiguration>(c, p =>
            {
                var found = p.IsEnabled switch
                {
                    true => p.ReviewerCount > 0 ? $"habilitado com {p.ReviewerCount} revisor(es)" : "habilitado sem revisores",
                    false => "desabilitado",
                    null => "não informado pela fonte",
                };
                var obj = Setting("adminConsentRequestPolicy", "Fluxo de consentimento do administrador", found, "habilitado com revisores");
                if (p.IsEnabled is null) return KnightControlOutcome.NotEvaluated("a fonte não informou o estado do fluxo de consentimento.", new[] { obj });
                return p.IsEnabled == true && p.ReviewerCount > 0
                    ? KnightControlOutcome.Passed($"Fluxo de consentimento do administrador habilitado com {p.ReviewerCount} revisor(es).", new[] { obj })
                    : KnightControlOutcome.Exposed(p.IsEnabled == true
                        ? "O fluxo de consentimento do administrador está habilitado, mas não há revisores definidos."
                        : "O fluxo de consentimento do administrador está desabilitado.", Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.1.5.2")),

        // ==== Política de gerenciamento de aplicações ====
        Control("AK-ENTRA-024", "Adição de senhas a aplicações não bloqueada", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.Medium,
            "Habilitar na política padrão de gerenciamento de aplicações a restrição que bloqueia a adição de segredos (senhas) e preferir certificados ou identidades gerenciadas.",
            "Política padrão de gerenciamento de aplicações habilitada com a restrição “adição de senha” ativa.",
            c => AppPolicy(c, "passwordAddition", "Bloqueio de adição de senhas a aplicações", _ => null,
                "Aplicações podem receber novos segredos (senhas).", "A política padrão bloqueia a adição de senhas a aplicações."),
            Ref(M365 + "5.1.5.3")),

        Control("AK-ENTRA-025", "Vida útil de senhas de aplicações acima de 180 dias", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Definir na política padrão de gerenciamento de aplicações a vida útil máxima de segredos em 180 dias ou menos.",
            "Política padrão de gerenciamento de aplicações habilitada com vida útil máxima de senha de até 180 dias.",
            c => AppPolicy(c, "passwordLifetime", "Vida útil máxima de senhas de aplicações",
                r => Duration(r.MaxLifetime) is { } d
                    ? d.TotalDays <= 180 ? null : $"vida útil máxima de {d.TotalDays.ToString("0", CultureInfo.InvariantCulture)} dias"
                    : "vida útil máxima não informada",
                "Segredos de aplicações podem ter vida útil acima de 180 dias.", "A vida útil máxima de segredos de aplicações é de até 180 dias."),
            Ref(M365 + "5.1.5.4")),

        Control("AK-ENTRA-026", "Senhas de aplicações podem ser definidas manualmente", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.Medium,
            "Habilitar a restrição de senhas personalizadas na política padrão, para que segredos de aplicações sejam sempre gerados pelo sistema.",
            "Política padrão de gerenciamento de aplicações habilitada com a restrição “senha personalizada” ativa.",
            c => AppPolicy(c, "customPasswordAddition", "Bloqueio de senhas personalizadas de aplicações", _ => null,
                "Segredos de aplicações podem ser definidos manualmente, sem geração pelo sistema.",
                "Segredos de aplicações só podem ser gerados pelo sistema."),
            Ref(M365 + "5.1.5.5")),

        // ==== Credenciais de aplicações ====
        Control("AK-ENTRA-027", "Certificados de aplicações com vigência acima de 180 dias", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Emitir certificados de aplicações com vigência de até 180 dias e rotacioná-los antes do vencimento.",
            "Nenhuma aplicação registrada com certificado cuja vigência (início ao fim) exceda 180 dias.",
            c => Single<EntraApplicationCredentialInventory>(c, inv =>
            {
                var flagged = inv.Applications.Where(a => a.LongLivedCertificateCount > 0).ToList();
                var affected = flagged.Select(a => Affected(KnightAffectedObjectKind.ServicePrincipal, a.ApplicationId, a.DisplayName, null,
                    $"{a.LongLivedCertificateCount} de {a.CertificateCount} certificado(s) com vigência acima de {inv.LongLivedThresholdDays} dias"
                    + (a.LongestCertificateValidityDays is { } d ? $" (maior: {N(d)} dias)." : "."),
                    observed: $"Certificados: {a.CertificateCount} · segredos: {a.PasswordCount}")).ToList();
                var count = inv.ApplicationsWithLongLivedCertificates;
                var limitation = inv.ListComplete ? null
                    : $"A lista preservada tem até {EntraApplicationCredentialInventory.MaxListed} aplicações; a contagem considera todas as {count}.";
                return count == 0
                    ? KnightControlOutcome.Passed($"Nenhuma das {inv.ApplicationsTotal} aplicação(ões) registrada(s) tem certificado com vigência acima de {inv.LongLivedThresholdDays} dias.")
                    : KnightControlOutcome.Exposed($"{count} aplicação(ões) com certificado de vigência acima de {inv.LongLivedThresholdDays} dias.",
                        affected, null, inv.ListComplete, limitation, count);
            }),
            Ref(M365 + "5.1.5.6")),

        // ==== Métodos de autenticação ====
        Control("AK-ENTRA-028", "Authenticator sem contexto de aplicativo e localização nas aprovações", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Fixar no Microsoft Authenticator, para todos os usuários, a exibição do nome do aplicativo e da localização geográfica nas notificações de MFA.",
            "Microsoft Authenticator com exibição do nome do aplicativo e da localização geográfica habilitadas para todos os usuários. A correspondência de números é sempre aplicada pela plataforma às notificações push.",
            c => Single<EntraAuthenticationMethodsConfiguration>(c, p =>
            {
                var objs = new[]
                {
                    Setting("authenticator/numberMatching", "Correspondência de números", "sempre aplicada pela plataforma às notificações push", "habilitada",
                        "A documentação oficial informa que a correspondência de números vale para todas as notificações push do Authenticator e não pode ser desligada."),
                    Setting("authenticator/displayAppInformation", "Exibir nome do aplicativo", FeatureState(p.DisplayAppInformation), "habilitado para todos os usuários"),
                    Setting("authenticator/displayLocationInformation", "Exibir localização geográfica", FeatureState(p.DisplayLocationInformation), "habilitado para todos os usuários"),
                };
                var auth = p.Method("MicrosoftAuthenticator");
                if (auth is null)
                    return KnightControlOutcome.NotEvaluated("a política não devolveu a configuração do Microsoft Authenticator.", objs);
                if (!MethodEnabled(auth))
                    return KnightControlOutcome.NotApplicable("o Microsoft Authenticator não está habilitado como método de autenticação.", objs);
                if (p.DisplayAppInformation?.State is null || p.DisplayLocationInformation?.State is null)
                    return KnightControlOutcome.NotEvaluated("a fonte não informou o estado das opções de contexto do Authenticator.", objs);
                var missing = new List<string>();
                if (!FeatureEnforced(p.DisplayAppInformation)) missing.Add("nome do aplicativo");
                if (!FeatureEnforced(p.DisplayLocationInformation)) missing.Add("localização geográfica");
                return missing.Count == 0
                    ? KnightControlOutcome.Passed("Nome do aplicativo e localização geográfica estão fixados para todos os usuários; a correspondência de números é aplicada pela plataforma.", objs)
                    : KnightControlOutcome.Exposed("Contexto não fixado para todos os usuários: " + string.Join(" e ", missing)
                        + ". Um estado “gerenciado pela Microsoft” pode mudar sem ação do locatário e não é o valor fixado pedido pela referência.",
                        Array.Empty<KnightIndicatorObject>(), objs);
            }),
            Ref(M365 + "5.2.3.1", note: "A correspondência de números, parte do mesmo critério, é sempre aplicada pela plataforma (documentação oficial do Microsoft Entra) e não é uma opção do locatário.")),

        Control("AK-ENTRA-029", "SMS ou chamada de voz habilitados como método de autenticação", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Desabilitar SMS e chamada de voz na política de métodos de autenticação e migrar os usuários para métodos mais fortes.",
            "SMS e chamada de voz desabilitados na política de métodos de autenticação.",
            c => Single<EntraAuthenticationMethodsConfiguration>(c, p =>
            {
                var sms = p.Method("Sms");
                var voice = p.Method("Voice");
                var objs = new[]
                {
                    Setting("authenticationMethods/Sms", "SMS", sms?.State ?? "não informado pela fonte", "disabled"),
                    Setting("authenticationMethods/Voice", "Chamada de voz", voice?.State ?? "não informado pela fonte", "disabled"),
                };
                var enabled = new[] { (sms, "SMS"), (voice, "chamada de voz") }.Where(x => MethodEnabled(x.Item1)).Select(x => x.Item2).ToList();
                if (sms is null && voice is null)
                    return KnightControlOutcome.NotEvaluated("a política não devolveu a configuração de SMS nem de chamada de voz.", objs);
                return enabled.Count == 0
                    ? KnightControlOutcome.Passed("SMS e chamada de voz estão desabilitados.", objs)
                    : KnightControlOutcome.Exposed("Métodos fracos habilitados: " + string.Join(" e ", enabled) + ".", Array.Empty<KnightIndicatorObject>(), objs);
            }),
            Ref(M365 + "5.2.3.5", note: "O e-mail (senha única), parte do mesmo critério, é avaliado no AK-ENTRA-031.")),

        Control("AK-ENTRA-030", "Revisão de acesso do papel Criador de Locatário não configurada", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium,
            "Criar revisão de acesso recorrente das atribuições do papel Criador de Locatário, com revisores definidos e remoção do acesso negado ao aplicar.",
            "Revisão de acesso ativa com escopo no papel Criador de Locatário, recorrência mensal ou mais frequente, revisores definidos e remoção do acesso ao aplicar.",
            c => RoleReview(c, TenantCreatorTemplateId, "Criador de Locatário"),
            Ref(AZ + "5.3.6")),

        Control("AK-ENTRA-031", "Senha única por e-mail habilitada como método de autenticação", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Desabilitar o método de senha única por e-mail na política de métodos de autenticação.",
            "Método “senha única por e-mail” desabilitado.",
            c => Single<EntraAuthenticationMethodsConfiguration>(c, p =>
            {
                var email = p.Method("Email");
                var obj = Setting("authenticationMethods/Email", "Senha única por e-mail", email?.State ?? "não informado pela fonte", "disabled");
                if (email is null) return KnightControlOutcome.NotEvaluated("a política não devolveu a configuração do método de e-mail.", new[] { obj });
                return MethodEnabled(email)
                    ? KnightControlOutcome.Exposed("O método de senha única por e-mail está habilitado.", Array.Empty<KnightIndicatorObject>(), new[] { obj })
                    : KnightControlOutcome.Passed("O método de senha única por e-mail está desabilitado.", new[] { obj });
            }),
            Ref(M365 + "5.2.3.7"),
            Ref(M365 + "5.2.3.5", KnightReferenceMatch.Partial, "SMS e voz, parte do mesmo critério, são avaliados no AK-ENTRA-029.")),

        // ==== Regras de senha e grupos (configurações de diretório) ====
        Control("AK-ENTRA-033", "Lista personalizada de senhas proibidas não aplicada", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Habilitar a lista personalizada de senhas proibidas com termos da organização (nomes, produtos, localidades).",
            "Configuração “Password Rule Settings” com a verificação de senhas proibidas ativa e lista personalizada não vazia.",
            c => Many<EntraDirectorySettingConfiguration>(c, all =>
            {
                var s = Template(all, EntraDirectorySettingConfiguration.PasswordRuleTemplateId);
                if (s is null) return KnightControlOutcome.NotEvaluated("o modelo de regras de senha não foi encontrado na coleta.");
                var enforce = ParseBool(s.Value("EnableBannedPasswordCheck"));
                var list = (s.Value("BannedPasswordList") ?? "").Split('\t', ',', '\n').Count(x => !string.IsNullOrWhiteSpace(x));
                var found = $"verificação {(enforce == true ? "ativa" : "inativa")}{Origin(s, "EnableBannedPasswordCheck")}, {list} termo(s) na lista{Origin(s, "BannedPasswordList")}";
                var obj = Setting("passwordRuleSettings/bannedPasswords", "Senhas proibidas personalizadas", found, "verificação ativa e lista com termos");
                return enforce == true && list > 0
                    ? KnightControlOutcome.Passed($"A lista personalizada de senhas proibidas está ativa com {list} termo(s).", new[] { obj })
                    : KnightControlOutcome.Exposed("A lista personalizada de senhas proibidas não está em uso.", Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.2.3.2")),

        Control("AK-ENTRA-034", "Proteção de senha não imposta no Active Directory local", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Habilitar a proteção de senha do Microsoft Entra para o Active Directory local no modo Impor.",
            "Em ambiente híbrido: proteção de senha para o AD local habilitada no modo “Enforce”.",
            c =>
            {
                var sync = c.Configuration.Read<EntraDirectorySynchronizationConfiguration>();
                if (!sync.Collected) return KnightControlOutcome.NotEvaluated(sync.MissingReason ?? "sincronização híbrida não coletada.");
                if (sync.Single?.OnPremisesSyncEnabled == false)
                    return KnightControlOutcome.NotApplicable("o diretório não sincroniza com um Active Directory local.");
                if (sync.Single?.OnPremisesSyncEnabled is null)
                    return KnightControlOutcome.NotEvaluated("a fonte não informou se o diretório é híbrido.");
                return Many<EntraDirectorySettingConfiguration>(c, all =>
                {
                    var s = Template(all, EntraDirectorySettingConfiguration.PasswordRuleTemplateId);
                    if (s is null) return KnightControlOutcome.NotEvaluated("o modelo de regras de senha não foi encontrado na coleta.");
                    var enabled = ParseBool(s.Value("EnableBannedPasswordCheckOnPremises"));
                    var mode = s.Value("BannedPasswordCheckOnPremisesMode");
                    var obj = Setting("passwordRuleSettings/onPremises", "Proteção de senha no AD local",
                        $"{(enabled == true ? "habilitada" : "desabilitada")}{Origin(s, "EnableBannedPasswordCheckOnPremises")}, modo {mode ?? "não informado"}{Origin(s, "BannedPasswordCheckOnPremisesMode")}",
                        "habilitada no modo Enforce");
                    return enabled == true && string.Equals(mode, "Enforce", StringComparison.OrdinalIgnoreCase)
                        ? KnightControlOutcome.Passed("A proteção de senha está imposta no Active Directory local.", new[] { obj })
                        : KnightControlOutcome.Exposed("A proteção de senha não está imposta no Active Directory local (desabilitada ou em auditoria).",
                            Array.Empty<KnightIndicatorObject>(), new[] { obj });
                });
            },
            Ref(M365 + "5.2.3.3")),

        Control("AK-ENTRA-035", "Limite de bloqueio de conta acima de 10 tentativas", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Low,
            "Definir o limite de bloqueio inteligente em 10 tentativas ou menos.",
            "Limite de bloqueio (LockoutThreshold) de até 10 tentativas.",
            c => Many<EntraDirectorySettingConfiguration>(c, all =>
            {
                var s = Template(all, EntraDirectorySettingConfiguration.PasswordRuleTemplateId);
                var v = ParseInt(s?.Value("LockoutThreshold"));
                if (s is null || v is null) return KnightControlOutcome.NotEvaluated("o limite de bloqueio não foi informado na coleta.");
                var obj = Setting("passwordRuleSettings/lockoutThreshold", "Limite de bloqueio", $"{v} tentativa(s){Origin(s, "LockoutThreshold")}", "até 10 tentativas");
                return v <= 10
                    ? KnightControlOutcome.Passed($"O bloqueio ocorre após {v} tentativa(s).", new[] { obj })
                    : KnightControlOutcome.Exposed($"O bloqueio só ocorre após {v} tentativas.", Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.2.3.8")),

        Control("AK-ENTRA-036", "Duração do bloqueio de conta abaixo de 60 segundos", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Low,
            "Definir a duração do bloqueio inteligente em 60 segundos ou mais.",
            "Duração do bloqueio (LockoutDurationInSeconds) de ao menos 60 segundos.",
            c => Many<EntraDirectorySettingConfiguration>(c, all =>
            {
                var s = Template(all, EntraDirectorySettingConfiguration.PasswordRuleTemplateId);
                var v = ParseInt(s?.Value("LockoutDurationInSeconds"));
                if (s is null || v is null) return KnightControlOutcome.NotEvaluated("a duração do bloqueio não foi informada na coleta.");
                var obj = Setting("passwordRuleSettings/lockoutDuration", "Duração do bloqueio", $"{v} segundo(s){Origin(s, "LockoutDurationInSeconds")}", "ao menos 60 segundos");
                return v >= 60
                    ? KnightControlOutcome.Passed($"O bloqueio dura {v} segundo(s).", new[] { obj })
                    : KnightControlOutcome.Exposed($"O bloqueio dura só {v} segundo(s).", Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.2.3.9")),

        Control("AK-ENTRA-037", "Usuários podem criar grupos do Microsoft 365", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Low,
            "Desabilitar a criação de grupos do Microsoft 365 por usuários e, se necessário, liberar só para um grupo autorizado.",
            "Configuração “Group.Unified” com EnableGroupCreation = false.",
            c => Many<EntraDirectorySettingConfiguration>(c, all =>
            {
                var s = Template(all, EntraDirectorySettingConfiguration.GroupUnifiedTemplateId);
                var v = ParseBool(s?.Value("EnableGroupCreation"));
                if (s is null || v is null) return KnightControlOutcome.NotEvaluated("a configuração de criação de grupos não foi informada na coleta.");
                var allowed = s.Value("GroupCreationAllowedGroupId");
                var found = v == true ? "permitida a todos" : string.IsNullOrWhiteSpace(allowed) ? "restrita" : "restrita a um grupo autorizado";
                var obj = Setting("groupUnified/enableGroupCreation", "Criação de grupos do Microsoft 365 por usuários", found + Origin(s, "EnableGroupCreation"), "restrita");
                return v == false
                    ? KnightControlOutcome.Passed("A criação de grupos do Microsoft 365 por usuários está restrita.", new[] { obj })
                    : KnightControlOutcome.Exposed("Qualquer usuário pode criar grupos do Microsoft 365.", Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.1.3.4")),

        // ==== Domínios e sincronização ====
        Control("AK-ENTRA-038", "Senhas com expiração periódica", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Configurar as senhas para não expirarem periodicamente nos domínios gerenciados, mantendo MFA e proteção contra senhas vazadas.",
            "Domínios verificados e gerenciados com validade de senha “nunca expira”.",
            c => Many<EntraDomainConfiguration>(c, domains =>
            {
                var managed = domains.Where(d => d.IsVerified == true && string.Equals(d.AuthenticationType, "Managed", StringComparison.OrdinalIgnoreCase)).ToList();
                if (managed.Count == 0) return KnightControlOutcome.NotApplicable("não há domínio verificado com autenticação gerenciada na nuvem.");
                var unknown = managed.Where(d => d.PasswordValidityPeriodInDays is null).ToList();
                var expiring = managed.Where(d => d.PasswordValidityPeriodInDays is { } v && v != EntraDomainConfiguration.NeverExpires).ToList();
                var affected = expiring.Select(d => Affected(KnightAffectedObjectKind.Domain, d.Id, d.Id, null,
                    $"Senhas expiram a cada {N(d.PasswordValidityPeriodInDays!.Value)} dia(s).", observed: "Domínio gerenciado · validade de senha periódica")).ToList();
                if (expiring.Count > 0)
                    return KnightControlOutcome.Exposed($"{expiring.Count} domínio(s) gerenciado(s) com expiração periódica de senha.", affected);
                if (unknown.Count > 0)
                    return KnightControlOutcome.NotEvaluated($"{unknown.Count} domínio(s) sem a validade de senha informada pela fonte.");
                return KnightControlOutcome.Passed($"Os {managed.Count} domínio(s) gerenciado(s) não expiram senhas periodicamente.");
            }),
            Ref(M365 + "1.3.1")),

        Control("AK-ENTRA-039", "Sincronização de hash de senha desabilitada no ambiente híbrido", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Habilitar a sincronização de hash de senha, que também permite detectar credenciais vazadas e manter a autenticação se o ambiente local falhar.",
            "Em ambiente híbrido: sincronização de hash de senha habilitada.",
            c => Single<EntraDirectorySynchronizationConfiguration>(c, s =>
            {
                if (s.OnPremisesSyncEnabled == false) return KnightControlOutcome.NotApplicable("o diretório não sincroniza com um Active Directory local.");
                if (s.OnPremisesSyncEnabled is null) return KnightControlOutcome.NotEvaluated("a fonte não informou se o diretório é híbrido.");
                var obj = Setting("directorySynchronization/passwordHashSync", "Sincronização de hash de senha",
                    s.PasswordHashSyncEnabled switch { true => "habilitada", false => "desabilitada", _ => "não informada" }, "habilitada");
                return s.PasswordHashSyncEnabled switch
                {
                    true => KnightControlOutcome.Passed("A sincronização de hash de senha está habilitada.", new[] { obj }),
                    false => KnightControlOutcome.Exposed("A sincronização de hash de senha está desabilitada no ambiente híbrido.", Array.Empty<KnightIndicatorObject>(), new[] { obj }),
                    _ => KnightControlOutcome.NotEvaluated(s.PasswordHashSyncLimitation ?? "a fonte não informou o estado da sincronização de hash de senha.", new[] { obj }),
                };
            }),
            Ref(M365 + "5.1.8.1")),

        // ==== Dispositivos ====
        Control("AK-ENTRA-040", "Registro ou ingresso de dispositivos sem exigência de MFA", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Exigir MFA para registrar ou ingressar dispositivos — pela política de dispositivos ou por acesso condicional na ação “registrar ou ingressar dispositivos”.",
            "Política de dispositivos com MFA exigida, ou política de acesso condicional habilitada exigindo MFA na ação de registro de dispositivos para todos os usuários.",
            c => Single<EntraDeviceRegistrationConfiguration>(c, p =>
            {
                var obj = Setting("deviceRegistrationPolicy/multiFactorAuthConfiguration", "MFA para registrar ou ingressar dispositivos",
                    p.MultiFactorAuthConfiguration switch { "required" => "exigida", "notRequired" => "não exigida", null => "não informada", var v => v }, "exigida");
                if (string.Equals(p.MultiFactorAuthConfiguration, "required", StringComparison.OrdinalIgnoreCase))
                    return KnightControlOutcome.Passed("A política de dispositivos exige MFA para registrar ou ingressar dispositivos.", new[] { obj });
                var req = new CaRequirement("MFA na ação de registrar ou ingressar dispositivos",
                    r => r.Policy.IncludeUserActions.Contains("urn:user:registerdevice", StringComparer.OrdinalIgnoreCase),
                    r => StrongGrant(r), NeedsV2: false);
                var ca = ConditionalAccessRequirements.ForAllUsers(c.Directory, req, CaMissing(c), c.MemberLabel);
                var ev = new List<KnightIndicatorObject> { obj };
                ev.AddRange(ca.Satisfying.Select(r => PolicyEvidence(r, "Exige MFA para registrar ou ingressar dispositivos.")));
                return ca.State switch
                {
                    CaCoverageState.Satisfied => KnightControlOutcome.Passed("O acesso condicional exige MFA para registrar ou ingressar dispositivos.", ev),
                    CaCoverageState.NotSatisfied => KnightControlOutcome.Exposed("Nem a política de dispositivos nem o acesso condicional exigem MFA para registrar ou ingressar dispositivos.",
                        Array.Empty<KnightIndicatorObject>(), ev),
                    _ => KnightControlOutcome.NotEvaluated(ca.Reason ?? "exigência por acesso condicional não resolvida.", ev),
                };
            }),
            Ref(AZ + "5.1.2")),

        Control("AK-ENTRA-041", "Ingresso de dispositivos no Entra permitido a todos os usuários", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Restringir o ingresso de dispositivos no Microsoft Entra a usuários ou grupos selecionados.",
            "Política de dispositivos com ingresso permitido a usuários/grupos selecionados ou a ninguém.",
            c => Single<EntraDeviceRegistrationConfiguration>(c, p =>
            {
                var obj = Setting("deviceRegistrationPolicy/azureADJoin", "Quem pode ingressar dispositivos", Membership(p.AllowedToJoin), "usuários ou grupos selecionados");
                return p.AllowedToJoin switch
                {
                    "all" => KnightControlOutcome.Exposed("Qualquer usuário pode ingressar dispositivos no Microsoft Entra.", Array.Empty<KnightIndicatorObject>(), new[] { obj }),
                    "selected" or "none" => KnightControlOutcome.Passed("O ingresso de dispositivos está restrito.", new[] { obj }),
                    _ => KnightControlOutcome.NotEvaluated("a fonte não informou quem pode ingressar dispositivos.", new[] { obj }),
                };
            }),
            Ref(M365 + "5.1.4.1")),

        Control("AK-ENTRA-042", "Limite de dispositivos por usuário acima de 10", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Definir o número máximo de dispositivos por usuário em 10 ou menos.",
            "Política de dispositivos com cota por usuário de até 10.",
            c => Single<EntraDeviceRegistrationConfiguration>(c, p =>
            {
                if (p.UserDeviceQuota is null) return KnightControlOutcome.NotEvaluated("a fonte não informou a cota de dispositivos por usuário.");
                var obj = Setting("deviceRegistrationPolicy/userDeviceQuota", "Dispositivos por usuário", N(p.UserDeviceQuota.Value), "até 10");
                return p.UserDeviceQuota <= 10
                    ? KnightControlOutcome.Passed($"Cada usuário pode ter até {p.UserDeviceQuota} dispositivo(s).", new[] { obj })
                    : KnightControlOutcome.Exposed($"Cada usuário pode ter até {p.UserDeviceQuota} dispositivos.", Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "5.1.4.2")),

        Control("AK-ENTRA-043", "Administradores globais viram administradores locais no ingresso de dispositivos", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Desmarcar a inclusão do papel Administrador Global como administrador local dos dispositivos ingressados.",
            "Política de dispositivos com “administradores globais como administradores locais” = Não.",
            c => Single<EntraDeviceRegistrationConfiguration>(c, p => Bool(p.GlobalAdminsAreLocalAdmins, false,
                "deviceRegistrationPolicy/enableGlobalAdmins", "Administradores globais como administradores locais", "Sim", "Não",
                "Administradores globais recebem direitos de administrador local em todos os dispositivos ingressados.",
                "Administradores globais não são adicionados como administradores locais.")),
            Ref(M365 + "5.1.4.3")),

        Control("AK-ENTRA-044", "Usuário que ingressa o dispositivo vira administrador local", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Limitar a atribuição de administrador local no ingresso a usuários selecionados ou a ninguém.",
            "Política de dispositivos com administradores locais adicionais restritos a selecionados ou a ninguém.",
            c => Single<EntraDeviceRegistrationConfiguration>(c, p =>
            {
                var obj = Setting("deviceRegistrationPolicy/registeringUsers", "Administradores locais adicionais no ingresso",
                    Membership(p.RegisteringUsersAreLocalAdmins), "usuários selecionados ou ninguém");
                return p.RegisteringUsersAreLocalAdmins switch
                {
                    "all" => KnightControlOutcome.Exposed("Todo usuário que ingressa um dispositivo se torna administrador local dele.", Array.Empty<KnightIndicatorObject>(), new[] { obj }),
                    "selected" or "none" => KnightControlOutcome.Passed("A atribuição de administrador local no ingresso está limitada.", new[] { obj }),
                    _ => KnightControlOutcome.NotEvaluated("a fonte não informou a atribuição de administrador local.", new[] { obj }),
                };
            }),
            Ref(M365 + "5.1.4.4")),

        Control("AK-ENTRA-045", "Solução de senha de administrador local (LAPS) desabilitada", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Habilitar a LAPS do Microsoft Entra na política de dispositivos e aplicar a configuração nos dispositivos.",
            "Política de dispositivos com LAPS habilitada.",
            c => Single<EntraDeviceRegistrationConfiguration>(c, p => Bool(p.LocalAdminPasswordEnabled, true,
                "deviceRegistrationPolicy/localAdminPassword", "LAPS do Microsoft Entra", "habilitada", "desabilitada",
                "A LAPS está desabilitada: senhas de administrador local não são rotacionadas nem guardadas no diretório.",
                "A LAPS está habilitada no locatário (a aplicação em cada dispositivo depende da política de gerenciamento).")),
            Ref(M365 + "5.1.4.5")),

        // ==== Grupos ====
        Control("AK-ENTRA-046", "Grupos do Microsoft 365 públicos", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Low,
            "Revisar os grupos públicos: torná-los privados ou registrar a aprovação dos que precisam continuar públicos.",
            "Nenhum grupo do Microsoft 365 público sem aprovação registrada (a aprovação não é verificável por API: todo grupo público é listado para revisão).",
            c => Single<EntraGroupVisibilityInventory>(c, inv =>
            {
                if (inv.PublicGroupsTotal == 0)
                    return KnightControlOutcome.Passed($"Nenhum dos {inv.UnifiedGroupsTotal} grupo(s) do Microsoft 365 é público.");
                var affected = inv.PublicGroups.Select(g => Affected(KnightAffectedObjectKind.Group, g.Id, g.DisplayName, null,
                    "Grupo público: qualquer pessoa da organização pode entrar e ler o conteúdo.", observed: "Visibilidade: pública")).ToList();
                return KnightControlOutcome.Exposed(
                    $"{inv.PublicGroupsTotal} de {inv.UnifiedGroupsTotal} grupo(s) do Microsoft 365 são públicos. A aprovação de cada um não é verificável por API — confirme se é intencional.",
                    affected, null, inv.ListComplete,
                    inv.ListComplete ? null : $"A lista preserva até {EntraGroupVisibilityInventory.MaxListed} grupos; a contagem considera todos.",
                    inv.PublicGroupsTotal);
            }),
            Ref(M365 + "1.2.1")),

        // ==== Contas privilegiadas ====
        Control("AK-ENTRA-047", "Contas administrativas sincronizadas do diretório local", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Low,
            "Usar contas administrativas somente em nuvem, separadas das contas sincronizadas do Active Directory local.",
            "Nenhum usuário com papel privilegiado sincronizado do diretório local.",
            c => Many<EntraPrivilegedAccountProfile>(c, profiles =>
            {
                if (profiles.Count == 0) return KnightControlOutcome.Passed("Nenhuma conta de usuário com papel privilegiado na coleta.");
                var unknown = profiles.Count(p => p.OnPremisesSyncEnabled is null && p.AccountEnabled is null);
                var synced = profiles.Where(p => p.OnPremisesSyncEnabled == true).ToList();
                var affected = synced.Select(p => Affected(KnightAffectedObjectKind.User, p.UserId, p.DisplayName, p.UserPrincipalName,
                    "Conta sincronizada do Active Directory local: um comprometimento local alcança o administrador de nuvem.", p.RoleNames)).ToList();
                if (synced.Count > 0)
                    return KnightControlOutcome.Exposed($"{synced.Count} conta(s) privilegiada(s) sincronizada(s) do diretório local.", affected);
                return unknown > 0
                    ? KnightControlOutcome.NotEvaluated($"{unknown} conta(s) privilegiada(s) sem a origem informada pela fonte.")
                    : KnightControlOutcome.Passed($"As {profiles.Count} conta(s) privilegiada(s) são somente em nuvem.");
            }),
            Ref(M365 + "1.1.1")),

        Control("AK-ENTRA-048", "Contas administrativas com licenças de produtividade", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium,
            "Licenciar contas administrativas só com o necessário para administrar (por exemplo, Microsoft Entra ID P1/P2), sem e-mail, Teams ou aplicativos do Office.",
            "Nenhum usuário com papel privilegiado com plano de serviço de produtividade efetivo (e-mail, SharePoint/OneDrive, Teams, aplicativos do Office).",
            c => Many<EntraPrivilegedAccountProfile>(c, profiles =>
            {
                if (profiles.Count == 0) return KnightControlOutcome.Passed("Nenhuma conta de usuário com papel privilegiado na coleta.");
                var unresolved = profiles.Count(p => !p.LicensesResolved);
                var flagged = profiles.Where(p => p.LicensesResolved && p.ProductivityServicePlans.Count > 0).ToList();
                var affected = flagged.Select(p => Affected(KnightAffectedObjectKind.User, p.UserId, p.DisplayName, p.UserPrincipalName,
                    "Planos de produtividade efetivos: " + string.Join(", ", p.ProductivityServicePlans.Take(8)) + (p.ProductivityServicePlans.Count > 8 ? "…" : "") + ".",
                    p.RoleNames, "Licenças: " + (p.SkuPartNumbers.Count == 0 ? "nenhuma" : string.Join(", ", p.SkuPartNumbers)))).ToList();
                if (flagged.Count > 0)
                    return KnightControlOutcome.Exposed($"{flagged.Count} conta(s) privilegiada(s) com licença de produtividade (e-mail, colaboração ou Office).", affected,
                        null, unresolved == 0, unresolved == 0 ? null : $"{unresolved} conta(s) sem licenças resolvidas na coleta.");
                return unresolved > 0
                    ? KnightControlOutcome.NotEvaluated($"{unresolved} conta(s) privilegiada(s) sem licenças resolvidas na coleta.")
                    : KnightControlOutcome.Passed($"Nenhuma das {profiles.Count} conta(s) privilegiada(s) tem plano de produtividade efetivo.");
            }),
            Ref(M365 + "1.1.4")),

        Control("AK-ENTRA-049", "Número de administradores globais fora do intervalo de 2 a 4", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Manter entre dois e quatro administradores globais: ao menos dois para continuidade e no máximo quatro para limitar a superfície de ataque.",
            "Entre 2 e 4 membros ativos no papel Administrador Global.",
            c =>
            {
                var roles = c.Directory?.PrivilegedRoles;
                if (roles is null)
                    return KnightControlOutcome.NotEvaluated(c.Capability(KnightCapability.PrivilegedRoleInventory)?.Detail ?? "o inventário de papéis privilegiados não foi coletado.");
                var ga = roles.FirstOrDefault(r => string.Equals(r.TemplateId, GlobalAdministratorTemplateId, StringComparison.OrdinalIgnoreCase));
                var count = ga?.MemberCount ?? 0;
                var obj = Evidence(KnightAffectedObjectKind.DirectoryRole, GlobalAdministratorTemplateId, ga?.DisplayName ?? "Global Administrator",
                    $"{count} membro(s) ativo(s).", $"Papel ativo · {count} membro(s)");
                var members = (ga?.UserMemberIds ?? Array.Empty<string>())
                    .Select(id => Affected(KnightAffectedObjectKind.User, id, c.MemberLabel(id), null, "Membro ativo do papel Administrador Global.")).ToList();
                if (count > 4)
                    return KnightControlOutcome.Exposed($"{count} membros no papel Administrador Global — acima do máximo de 4.", members, new[] { obj },
                        members.Count == count, members.Count == count ? null : "Membros que não são usuários (grupos ou aplicações) entram na contagem, mas não na lista.", count);
                if (count < 2)
                    return KnightControlOutcome.Exposed($"{count} membro(s) no papel Administrador Global — abaixo do mínimo de 2 para continuidade.",
                        Array.Empty<KnightIndicatorObject>(), new[] { obj });
                return KnightControlOutcome.Passed($"{count} membros no papel Administrador Global, dentro do intervalo de 2 a 4.", new[] { obj });
            },
            Ref(M365 + "1.1.3")),

        // ==== PIM ====
        Control("AK-ENTRA-050", "Atribuições permanentes em papéis privilegiados", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Converter atribuições ativas permanentes em atribuições elegíveis no Privileged Identity Management, mantendo permanentes só as contas de emergência.",
            "Nenhuma atribuição ativa permanente (sem data de término) em papel classificado como privilegiado, exceto contas de emergência — que não são identificáveis por API.",
            c => Many<EntraPrivilegedRoleGovernance>(c, roles =>
            {
                var privileged = roles.Where(r => r.IsPrivileged).ToList();
                var permanent = privileged
                    .SelectMany(r => r.ActiveAssignments.Where(a => a.Permanent).Select(a => (Role: r, Assignment: a)))
                    .GroupBy(x => x.Assignment.PrincipalId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var affected = permanent.Select(g =>
                {
                    var first = g.First().Assignment;
                    var kind = first.PrincipalType switch
                    {
                        "Group" => KnightAffectedObjectKind.Group,
                        "ServicePrincipal" => KnightAffectedObjectKind.ServicePrincipal,
                        "User" => KnightAffectedObjectKind.User,
                        _ => KnightAffectedObjectKind.Unknown,
                    };
                    var roleNames = g.Select(x => x.Role.DisplayName ?? x.Role.RoleDefinitionId).Distinct().ToList();
                    return Affected(kind, g.Key, c.MemberLabel(g.Key), null,
                        "Atribuição ativa permanente em: " + string.Join(", ", roleNames) + ". Contas de emergência podem justificar a exceção.", roleNames);
                }).ToList();
                if (privileged.Count == 0)
                    return KnightControlOutcome.NotEvaluated("a coleta não leu as atribuições dos papéis administrativos privilegiados.");
                return affected.Count == 0
                    ? KnightControlOutcome.Passed($"Nenhuma atribuição ativa permanente nos {privileged.Count} papéis administrativos privilegiados examinados.")
                    : KnightControlOutcome.Exposed($"{affected.Count} identidade(s) com atribuição ativa permanente em papel administrativo privilegiado.", affected);
            }),
            Ref(M365 + "5.3.1", KnightReferenceMatch.Partial,
                "O conjunto examinado são os papéis administrativos marcados como privilegiados na documentação oficial; a classificação automática de papéis privilegiados do Microsoft Graph só existe na versão beta. Contas de emergência não são identificáveis por API e aparecem na lista para decisão humana.")),

        Control("AK-ENTRA-051", "Ativação do papel Administrador Global sem aprovação", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium,
            "Exigir aprovação, por ao menos dois aprovadores, para ativar o papel Administrador Global no PIM.",
            "Regra de ativação do papel Administrador Global com aprovação exigida e ao menos dois aprovadores.",
            c => RoleApproval(c, GlobalAdministratorTemplateId, "Administrador Global"),
            Ref(M365 + "5.3.4")),

        Control("AK-ENTRA-052", "Ativação do papel Administrador de Função Privilegiada sem aprovação", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Exigir aprovação, por ao menos dois aprovadores, para ativar o papel Administrador de Função Privilegiada no PIM.",
            "Regra de ativação do papel Administrador de Função Privilegiada com aprovação exigida e ao menos dois aprovadores.",
            c => RoleApproval(c, PrivilegedRoleAdministratorTemplateId, "Administrador de Função Privilegiada"),
            Ref(M365 + "5.3.5")),

        // ==== Revisões de acesso ====
        Control("AK-ENTRA-053", "Revisões de acesso de convidados não configuradas", KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium,
            "Criar revisão de acesso recorrente (mensal ou mais frequente) dos convidados de todos os grupos do Microsoft 365, com revisores definidos e remoção do acesso negado ao aplicar.",
            "Revisão de acesso vigente cujo escopo alcança os convidados de todos os grupos do Microsoft 365 (ou todos os convidados do diretório), com recorrência mensal ou mais frequente, revisores definidos e remoção do acesso ao aplicar.",
            c => Many<EntraAccessReviewDefinition>(c, defs =>
            {
                var candidates = defs
                    .Select(d => (Def: d, Scope: EntraAccessReviewCoverage.Guests(d)))
                    .Where(x => x.Scope.Reach != KnightAccessReviewReach.NotTargeted)
                    .Select(x => Assess(x.Def, x.Scope, c.CollectedAt))
                    .ToList();
                if (candidates.Count == 0)
                    return KnightControlOutcome.Exposed("Não há revisão de acesso com escopo em convidados configurada no locatário.",
                        Array.Empty<KnightIndicatorObject>());

                var ev = candidates.Select(ReviewEvidence).ToList();
                if (candidates.FirstOrDefault(x => x.State == ReviewState.Proven) is { } proven)
                    return KnightControlOutcome.Passed(
                        $"A revisão de acesso {ReviewName(proven.Def)} alcança {proven.Scope.Description} e atende ao critério.", ev);

                var undetermined = candidates.Where(x => x.State == ReviewState.Undetermined).ToList();
                if (undetermined.Count > 0)
                    return KnightControlOutcome.NotEvaluated(
                        "não foi possível comprovar a abrangência da revisão de convidados: " + Reasons(undetermined), ev);

                return KnightControlOutcome.Exposed(
                    "Há revisão de acesso de convidados, mas nenhuma comprova o critério: " + Reasons(candidates),
                    Array.Empty<KnightIndicatorObject>(), ev);
            }),
            Ref(M365 + "5.3.2", KnightReferenceMatch.Partial,
                "A avaliação confirma a revisão dos convidados de todos os grupos do Microsoft 365, que é o alcance da própria configuração: a enumeração documentada não inclui grupos de associação dinâmica nem grupos atribuíveis a papéis, e convidados sem grupo não entram na revisão."),
            Ref(AZ + "5.3.2", KnightReferenceMatch.Partial, "A referência do Azure pede revisão ao menos quinzenal; o critério do AEGIS aceita recorrência mensal, como a referência do Microsoft 365.")),

        Control("AK-ENTRA-054", "Revisões de acesso de papéis altamente privilegiados não configuradas", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Criar revisão de acesso recorrente para os papéis Administrador Global, do Exchange, do SharePoint, do Teams e de Segurança.",
            "Para cada um dos cinco papéis: revisão de acesso vigente que alcance as atribuições do papel, com recorrência mensal ou mais frequente, revisores definidos e remoção do acesso ao aplicar.",
            c => Many<EntraAccessReviewDefinition>(c, defs =>
            {
                var roles = ReviewedRoles.Select(r => (r.Name, Review: RoleReviewObject(defs, r.TemplateId, r.Name, c.CollectedAt))).ToList();
                var failed = roles.Where(x => x.Review.State == ReviewState.Failed).ToList();
                var undetermined = roles.Where(x => x.Review.State == ReviewState.Undetermined).ToList();
                var affected = roles.Select(x => x.Review.Object).Where(o => o.Relation == KnightObjectRelation.Affected).ToList();
                var ev = roles.Select(x => x.Review.Object).Where(o => o.Relation == KnightObjectRelation.Evidence).ToList();

                if (failed.Count > 0)
                    return KnightControlOutcome.Exposed(
                        $"{failed.Count} de {ReviewedRoles.Count} papéis altamente privilegiados sem revisão de acesso que comprove o critério.",
                        affected, ev, complete: undetermined.Count == 0,
                        limitation: undetermined.Count == 0 ? null
                            : $"A revisão de {string.Join(", ", undetermined.Select(x => x.Name))} não pôde ser interpretada e não entra na contagem.");
                if (undetermined.Count > 0)
                    return KnightControlOutcome.NotEvaluated(
                        "não foi possível comprovar a abrangência da revisão de " + string.Join(", ", undetermined.Select(x => x.Name)), ev);
                return KnightControlOutcome.Passed(
                    $"Os {ReviewedRoles.Count} papéis altamente privilegiados têm revisão de acesso que comprova o critério.", ev);
            }),
            ReviewedRoles.Select(r => Ref(M365 + "5.3.3#" + r.Variant, KnightReferenceMatch.Exact,
                "A avaliação confirma que o escopo da revisão alcança as atribuições do papel (ativas e elegíveis); revisões limitadas a um subconjunto de principais ou só às atribuições ativas não são aceitas.")).ToArray()),

        // ==== Locais e aplicações de serviço ====
        Control("AK-ENTRA-055", "Locais confiáveis não definidos", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Definir os locais nomeados confiáveis da organização para uso nas políticas de acesso condicional.",
            "Ao menos um local nomeado marcado como confiável.",
            c => Many<EntraNamedLocationConfiguration>(c, locations =>
            {
                var trusted = locations.Where(l => l.IsTrusted == true).ToList();
                var ev = locations.Select(l => Evidence(KnightAffectedObjectKind.Policy, l.Id, l.DisplayName,
                    (l.Kind == "country" ? $"Local por país ({l.CountryCount} país(es))" : $"Local por IP ({l.IpRangeCount} faixa(s))")
                    + (l.IsTrusted == true ? " · confiável." : "."), "Local nomeado")).ToList();
                return trusted.Count > 0
                    ? KnightControlOutcome.Passed($"{trusted.Count} local(is) nomeado(s) confiável(is) definido(s).", ev)
                    : KnightControlOutcome.Exposed("Não há local nomeado marcado como confiável.", Array.Empty<KnightIndicatorObject>(), ev);
            }),
            Ref(M365 + "5.2.2.14")),

        Control("AK-ENTRA-056", "Nenhuma política de restrição de acesso por geografia", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Avaliar e, quando aplicável, criar política de acesso condicional que bloqueie países onde a organização não opera.",
            "Política de acesso condicional habilitada que bloqueia o acesso a partir de locais por país (inclusão) ou permite só a partir deles (exclusão).",
            c => Many<EntraNamedLocationConfiguration>(c, locations =>
            {
                var countries = locations.Where(l => l.Kind == "country").Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var policies = c.Directory?.ConditionalAccessPolicies;
                if (policies is null) return KnightControlOutcome.NotEvaluated(CaMissing(c) ?? "as políticas de acesso condicional não foram coletadas.");
                if (policies.Any(p => !p.SessionAndConditionsCaptured))
                    return KnightControlOutcome.NotEvaluated("a coleta desta avaliação não registrou os locais das políticas (coleta anterior à ampliação do catálogo).");
                var geo = policies.Where(p => p.State == ConditionalAccessPolicyState.Enabled && p.BuiltInControls.Contains("block", StringComparer.OrdinalIgnoreCase)
                    && ((p.IncludeLocations ?? Array.Empty<string>()).Any(countries.Contains)
                        || ((p.IncludeLocations ?? Array.Empty<string>()).Contains("All", StringComparer.OrdinalIgnoreCase)
                            && (p.ExcludeLocations ?? Array.Empty<string>()).Any(countries.Contains)))).ToList();
                var ev = geo.Select(p => PolicyEvidence(ConditionalAccessAnalyzer.Read(p), "Bloqueia o acesso com base em locais por país.")).ToList();
                return geo.Count > 0
                    ? KnightControlOutcome.Passed($"{geo.Count} política(s) habilitada(s) restringem o acesso por geografia.", ev)
                    : KnightControlOutcome.Exposed(countries.Count == 0
                        ? "Não há local nomeado por país nem política que restrinja o acesso por geografia."
                        : "Há locais por país, mas nenhuma política habilitada bloqueia o acesso com base neles.", Array.Empty<KnightIndicatorObject>(), ev);
            }),
            Ref(M365 + "5.2.2.15")),

        Control("AK-ENTRA-057", "Armazenamento de terceiros permitido no Microsoft 365 na web", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Medium,
            "Desabilitar o uso de serviços de armazenamento de terceiros no Microsoft 365 na web (centro de administração: Configurações da organização).",
            "Aplicação de serviço “armazenamento de terceiros” do Microsoft 365 presente e desabilitada no locatário.",
            c => Many<EntraServicePrincipalState>(c, states =>
            {
                var s = states.FirstOrDefault(x => string.Equals(x.AppId, EntraServicePrincipalState.ThirdPartyStorageAppId, StringComparison.OrdinalIgnoreCase));
                if (s is null) return KnightControlOutcome.NotEvaluated("o estado da aplicação de armazenamento de terceiros não foi lido nesta coleta.");
                var found = !s.Present ? "não configurado (padrão: permitido)" : s.AccountEnabled == false ? "desabilitado" : "habilitado";
                var obj = Setting("servicePrincipal/thirdPartyStorage", "Armazenamento de terceiros no Microsoft 365 na web", found, "desabilitado");
                return s.Present && s.AccountEnabled == false
                    ? KnightControlOutcome.Passed("O uso de armazenamento de terceiros no Microsoft 365 na web está desabilitado.", new[] { obj })
                    : KnightControlOutcome.Exposed("Usuários podem abrir e salvar arquivos em serviços de armazenamento de terceiros no Microsoft 365 na web.",
                        Array.Empty<KnightIndicatorObject>(), new[] { obj });
            }),
            Ref(M365 + "1.3.7")),

        // ==== Acesso condicional (além de MFA e autenticação legada) ====
        Control("AK-ENTRA-058", "Frequência de entrada e sessão não persistente não exigidas para administradores", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Medium,
            "Criar política de acesso condicional para os papéis administrativos com frequência de entrada de até 4 horas e sessão de navegador nunca persistente.",
            "Cada membro dos papéis privilegiados ativos alcançado por política habilitada, em todas as aplicações, com frequência de entrada de até 4 horas e navegador nunca persistente.",
            c => PrivilegedRequirement(c, SignInFrequencyAdmins,
                "Administradores têm frequência de entrada de até 4 horas e sessão não persistente em todas as aplicações.",
                "Frequência de entrada e sessão não persistente não são exigidas de todos os administradores."),
            Ref(M365 + "5.2.2.4")),

        Control("AK-ENTRA-059", "MFA resistente a phishing não exigida para administradores", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.High,
            "Exigir a força de autenticação “MFA resistente a phishing” para os papéis administrativos em todas as aplicações.",
            "Cada membro dos papéis privilegiados ativos alcançado por política habilitada, em todas as aplicações, que exige força de autenticação resistente a phishing.",
            c => PrivilegedRequirement(c, PhishingResistantAdmins,
                "Administradores precisam de MFA resistente a phishing em todas as aplicações.",
                "MFA resistente a phishing não é exigida de todos os administradores."),
            Ref(M365 + "5.2.2.5")),

        Control("AK-ENTRA-060", "Política de risco de usuário ausente", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Criar política de acesso condicional para risco alto de usuário exigindo troca de senha com MFA e reautenticação a cada vez.",
            "Política habilitada para todos os usuários e aplicações, risco de usuário alto, troca de senha e MFA exigidas (operador E) e frequência de entrada “a cada vez”.",
            c => AllUsersRequirement(c, UserRiskPolicy,
                "Usuários de risco alto precisam trocar a senha com MFA.", "Não há política habilitada que responda ao risco alto de usuário."),
            Ref(M365 + "5.2.2.6")),

        Control("AK-ENTRA-061", "Política de risco de entrada ausente", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Criar política de acesso condicional para entradas de risco médio e alto exigindo MFA e reautenticação a cada vez.",
            "Política habilitada para todos os usuários e aplicações, riscos de entrada médio e alto, MFA exigida e frequência de entrada “a cada vez”.",
            c => AllUsersRequirement(c, SignInRiskPolicy,
                "Entradas de risco médio e alto exigem MFA.", "Não há política habilitada que exija MFA em entradas de risco médio e alto."),
            Ref(M365 + "5.2.2.7")),

        Control("AK-ENTRA-062", "Entradas de risco médio e alto não bloqueadas", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Criar política de acesso condicional que bloqueie entradas de risco médio e alto.",
            "Política habilitada para todos os usuários e aplicações que bloqueia riscos de entrada médio e alto.",
            c => AllUsersRequirement(c, BlockSignInRisk,
                "Entradas de risco médio e alto são bloqueadas.", "Não há política habilitada que bloqueie entradas de risco médio e alto."),
            Ref(M365 + "5.2.2.8")),

        Control("AK-ENTRA-063", "Dispositivo gerenciado não exigido para autenticação", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Exigir dispositivo em conformidade ou ingressado no domínio híbrido para acessar as aplicações da organização.",
            "Política habilitada para todos os usuários e aplicações exigindo dispositivo em conformidade ou ingressado (sem alternativa de outro controle).",
            c => AllUsersRequirement(c, ManagedDevice,
                "O acesso exige dispositivo gerenciado.", "Não há política habilitada que exija dispositivo gerenciado para todos."),
            Ref(M365 + "5.2.2.9")),

        Control("AK-ENTRA-064", "Registro de informações de segurança sem dispositivo gerenciado", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Exigir dispositivo em conformidade ou ingressado para a ação “registrar informações de segurança”.",
            "Política habilitada para todos os usuários na ação de registro de informações de segurança exigindo dispositivo gerenciado.",
            c => AllUsersRequirement(c, ManagedDeviceForSecurityInfo,
                "O registro de informações de segurança exige dispositivo gerenciado.", "Não há política habilitada que exija dispositivo gerenciado para registrar informações de segurança."),
            Ref(M365 + "5.2.2.10")),

        Control("AK-ENTRA-065", "Registro no Intune sem reautenticação a cada vez", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Criar política para o aplicativo de registro do Intune exigindo MFA e frequência de entrada “a cada vez”.",
            "Política habilitada para todos os usuários, alcançando o registro do Intune, com MFA e frequência de entrada “a cada vez”.",
            c => AllUsersRequirement(c, IntuneEnrollmentEveryTime,
                "O registro de dispositivos no Intune exige reautenticação com MFA a cada vez.", "Não há política habilitada que exija reautenticação a cada vez no registro do Intune."),
            Ref(M365 + "5.2.2.11")),

        Control("AK-ENTRA-066", "Fluxo de código de dispositivo não bloqueado", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Bloquear por acesso condicional o fluxo de código de dispositivo para todos os usuários, com exceções só para cenários aprovados.",
            "Política habilitada para todos os usuários e aplicações que bloqueia a transferência “fluxo de código de dispositivo”.",
            c => AllUsersRequirement(c, BlockTransfer("deviceCodeFlow", "bloqueio do fluxo de código de dispositivo"),
                "O fluxo de código de dispositivo está bloqueado.", "O fluxo de código de dispositivo não está bloqueado por política habilitada."),
            Ref(M365 + "5.2.2.12")),

        Control("AK-ENTRA-067", "Reautenticação periódica não exigida", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Exigir reautenticação periódica (frequência de entrada de até 7 dias) para todos os usuários em todas as aplicações.",
            "Política habilitada para todos os usuários e aplicações com frequência de entrada de até 7 dias.",
            c => AllUsersRequirement(c, PeriodicReauthentication,
                "Todos os usuários precisam se autenticar novamente em até 7 dias.", "Não há política habilitada que exija reautenticação periódica de todos."),
            Ref(M365 + "5.2.2.13")),

        Control("AK-ENTRA-068", "Transferência de autenticação não bloqueada", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Bloquear por acesso condicional a transferência de autenticação entre dispositivos.",
            "Política habilitada para todos os usuários e aplicações que bloqueia a “transferência de autenticação”.",
            c => AllUsersRequirement(c, BlockTransfer("authenticationTransfer", "bloqueio da transferência de autenticação"),
                "A transferência de autenticação está bloqueada.", "A transferência de autenticação não está bloqueada por política habilitada."),
            Ref(M365 + "5.2.2.17")),

        Control("AK-ENTRA-069", "Tempo limite de sessão ociosa não imposto por política", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Habilitar o tempo limite de sessão ociosa do Microsoft 365 e a política de acesso condicional com restrições impostas pelo aplicativo para navegadores.",
            "Política habilitada para todos os usuários, alcançando o Office 365 no navegador, com “restrições impostas pelo aplicativo”.",
            c => AllUsersRequirement(c, IdleSessionRestriction,
                "O acesso pelo navegador aplica as restrições de sessão impostas pelo aplicativo.", "Não há política habilitada que aplique restrições de sessão impostas pelo aplicativo no navegador."),
            Ref(M365 + "1.3.2", KnightReferenceMatch.Partial,
                "A referência também exige a configuração de tempo limite de sessão ociosa do Microsoft 365 (3 horas ou menos), lida no serviço SharePoint e OneDrive, ainda não implementada.")),
    };

    /// <summary>
    /// O que as revisões coletadas comprovam sobre UM papel. Só entra como AFETADO o papel cuja falta de revisão
    /// (ou cujo escopo menor) é demonstrável; o que não pôde ser interpretado vira evidência com a limitação.
    /// </summary>
    private static (ReviewState State, KnightIndicatorObject Object) RoleReviewObject(
        IReadOnlyList<EntraAccessReviewDefinition> defs, string templateId, string name, DateTimeOffset at)
    {
        var assessed = defs
            .Select(d => (Def: d, Scope: EntraAccessReviewCoverage.Role(d, templateId)))
            .Where(x => x.Scope.Reach != KnightAccessReviewReach.NotTargeted)
            .Select(x => Assess(x.Def, x.Scope, at))
            .ToList();
        var proven = assessed.FirstOrDefault(x => x.State == ReviewState.Proven);
        var state = proven is not null ? ReviewState.Proven
            : assessed.Any(x => x.State == ReviewState.Undetermined) ? ReviewState.Undetermined
            : ReviewState.Failed;
        var detail = proven is not null
            ? $"Revisão {ReviewName(proven.Def)}: alcança {proven.Scope.Description}. {EntraAccessReviewCoverage.Summary(proven.Def)}"
                + (proven.Scope.Queries is { Length: > 0 } q ? " · escopo lido — " + q : "")
            : assessed.Count == 0
                ? "Nenhuma revisão de acesso configurada para o papel."
                : ReasonsWithScope(assessed);
        return (state, new KnightIndicatorObject(
            state == ReviewState.Failed ? KnightObjectRelation.Affected : KnightObjectRelation.Evidence,
            KnightAffectedObjectKind.DirectoryRole, templateId, name, null, Array.Empty<string>(), detail, "Revisão de acesso do papel"));
    }

    private static KnightControlOutcome RoleReview(KnightEvaluationContext c, string templateId, string name) =>
        Many<EntraAccessReviewDefinition>(c, defs =>
        {
            var (state, obj) = RoleReviewObject(defs, templateId, name, c.CollectedAt);
            return state switch
            {
                ReviewState.Proven => KnightControlOutcome.Passed($"O papel {name} tem revisão de acesso que comprova o critério.", new[] { obj }),
                ReviewState.Undetermined => KnightControlOutcome.NotEvaluated(
                    $"não foi possível comprovar a abrangência da revisão do papel {name}: {obj.Detail}", new[] { obj }),
                _ => KnightControlOutcome.Exposed($"O papel {name} não tem revisão de acesso que comprove o critério.", new[] { obj }),
            };
        });

    private static KnightControlOutcome RoleApproval(KnightEvaluationContext c, string templateId, string roleName) =>
        Many<EntraPrivilegedRoleGovernance>(c, roles =>
        {
            var role = roles.FirstOrDefault(r => string.Equals(r.RoleDefinitionId, templateId, StringComparison.OrdinalIgnoreCase));
            if (role is null) return KnightControlOutcome.NotEvaluated($"a regra de ativação do papel {roleName} não foi lida nesta coleta.");
            if (role.ActivationRequiresApproval is null)
                return KnightControlOutcome.NotEvaluated(role.PolicyLimitation ?? $"a regra de ativação do papel {roleName} não foi informada pela fonte.");
            var found = role.ActivationRequiresApproval == true ? $"aprovação exigida · {role.ActivationApproverCount} aprovador(es)" : "aprovação não exigida";
            var obj = Setting("pim/" + templateId + "/approval", $"Aprovação para ativar {roleName}", found, "aprovação exigida com ao menos 2 aprovadores");
            return role.ActivationRequiresApproval == true && role.ActivationApproverCount >= 2
                ? KnightControlOutcome.Passed($"Ativar o papel {roleName} exige aprovação de {role.ActivationApproverCount} aprovador(es).", new[] { obj })
                : KnightControlOutcome.Exposed(role.ActivationRequiresApproval == true
                    ? $"A ativação do papel {roleName} exige aprovação, mas com menos de dois aprovadores."
                    : $"A ativação do papel {roleName} não exige aprovação.", Array.Empty<KnightIndicatorObject>(), new[] { obj });
        });
}
