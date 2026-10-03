using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;
using static AegisScore.Application.Knight.Catalog.KnightRuleKit;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Controles do Entra ID e do Microsoft 365 sobre leituras em versão beta e complementos
// ============================================================================
// • Versão BETA do Microsoft Graph (MFA por usuário, MFA preferencial do sistema, Authenticator em aplicativos
//   complementares, aplicativos e serviços próprios, Forms): a capacidade é própria; recusa, indisponibilidade ou resposta
//   fora do contrato deixa o controle não avaliado. A versão aparece nas informações técnicas do controle.
// • Sessão ociosa do Microsoft 365 (v1.0) e destino das mensagens denunciadas (Exchange Online): completam referências que
//   eram parciais — cada uma junto do controle que avalia a outra parte do mesmo critério.

public static class EntraClosureControls
{
    private const string M365 = "CIS-M365-7.0.0:";
    private const string AZ = "CIS-AZ-6.0.0:";

    /// <summary>Teto do tempo limite de sessão ociosa aceito pela referência.</summary>
    public static readonly TimeSpan MaxIdleTimeout = TimeSpan.FromHours(3);

    private static KnightIndicatorDefinition Entra(KnightService service, string id, string title, KnightIndicatorCategory category,
        SeverityLevel severity, string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate,
        params KnightReferenceLink[] references) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftEntraId, service, id, title, category, severity, recommendation, criterion, evaluate, references);

    /// <summary>Estado explícito de uma configuração beta ("default" = gerenciado pela Microsoft, que pode mudar sem aviso).</summary>
    private static string StateLabel(string? state) => state?.ToLowerInvariant() switch
    {
        null => "não informado pela fonte",
        "enabled" => "habilitado",
        "disabled" => "desabilitado",
        "default" => "gerenciado pela Microsoft (padrão, sem configuração explícita)",
        _ => state,
    };

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        Entra(KnightService.EntraId, "AK-ENTRA-071", "MFA por usuário (legado) habilitada em contas", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Desabilitar a MFA por usuário (legado) de todas as contas e exigir MFA por acesso condicional (ou security defaults).",
            "Estado da MFA por usuário (perUserMfaState) igual a disabled em todas as contas habilitadas.",
            c => Single<EntraPerUserMfaInventory>(c, inv =>
            {
                var affected = inv.NotDisabled.Select(u => KnightObjects.Affected(KnightAffectedObjectKind.User, u.Id, u.DisplayName, u.UserPrincipalName,
                    $"Encontrado: MFA por usuário {u.State}. Esperado: disabled (MFA exigida por acesso condicional).",
                    observed: "perUserMfaState: " + u.State)).ToList();
                var counts = $"{inv.Read} de {inv.EnabledAccounts} conta(s) habilitada(s) lida(s): {inv.Disabled} desabilitada(s), {inv.Enabled} habilitada(s), {inv.Enforced} imposta(s).";
                var setting = KnightObjects.Setting(EntraPerUserMfaInventory.ExternalId, "MFA por usuário (legado)", counts, "todas desabilitadas");
                // Estado diferente de "disabled" (inclusive um valor novo da versão beta) nunca conta como desabilitado.
                var notDisabled = inv.Read - inv.Disabled;
                if (notDisabled > 0)
                    return KnightControlOutcome.Exposed(Trim($"{notDisabled} conta(s) com MFA por usuário (legado) não desabilitada. {counts}"),
                        affected, new[] { setting }, inv.Complete && inv.NotDisabled.Count >= notDisabled,
                        inv.Complete ? (inv.NotDisabled.Count < notDisabled ? $"Lista limitada às primeiras {inv.NotDisabled.Count} contas." : null) : inv.Limitation,
                        notDisabled);
                return inv.Complete
                    ? KnightControlOutcome.Passed($"Nenhuma das {inv.EnabledAccounts} conta(s) habilitada(s) usa a MFA por usuário (legado).", new[] { setting })
                    : KnightControlOutcome.NotEvaluated(Trim(inv.Limitation ?? "nem todas as contas habilitadas foram lidas."), new[] { setting });
            }),
            Ref(M365 + "5.1.2.1"), Ref(AZ + "5.1.3")),

        Entra(KnightService.EntraId, "AK-ENTRA-072", "MFA preferencial do sistema não habilitada para todos os usuários", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Habilitar explicitamente a MFA preferencial do sistema na política de métodos de autenticação, com alvo em todos os usuários.",
            "systemCredentialPreferences.state = enabled, com includeTargets contendo all_users.",
            c => Single<EntraAuthenticationMethodsPreview>(c, p =>
            {
                if (p.SystemPreferred is not { } s) return KnightControlOutcome.NotEvaluated("a fonte não devolveu a configuração da MFA preferencial do sistema.");
                var all = s.IncludeTargets.Contains("all_users", StringComparer.OrdinalIgnoreCase);
                var ok = string.Equals(s.State, "enabled", StringComparison.OrdinalIgnoreCase) && all;
                var found = $"estado {StateLabel(s.State)}; alvo {(all ? "todos os usuários" : List(s.IncludeTargets))}"
                            + (s.ExcludeTargets.Count > 0 ? $"; exclusões: {s.ExcludeTargets.Count} grupo(s)" : "");
                return Setting(ok, EntraAuthenticationMethodsPreview.ExternalId + ":system-preferred", "MFA preferencial do sistema", found,
                    "habilitada para todos os usuários",
                    "A MFA preferencial do sistema não está habilitada explicitamente para todos os usuários: " + found + ".",
                    "A MFA preferencial do sistema está habilitada para todos os usuários" + (s.ExcludeTargets.Count > 0 ? $", com {s.ExcludeTargets.Count} grupo(s) excluído(s) listado(s) como evidência." : "."));
            }),
            Ref(M365 + "5.2.3.6")),

        Entra(KnightService.EntraId, "AK-ENTRA-073", "Microsoft Authenticator liberado em aplicativos complementares", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Desabilitar explicitamente o uso do Microsoft Authenticator em aplicativos complementares (companionAppAllowedState) na política do Authenticator.",
            "companionAppAllowedState.state = disabled (ou o método Microsoft Authenticator desabilitado).",
            c => Single<EntraAuthenticationMethodsPreview>(c, p =>
            {
                if (p.AuthenticatorPresent && string.Equals(p.AuthenticatorState, "disabled", StringComparison.OrdinalIgnoreCase))
                    return KnightControlOutcome.Passed("O método Microsoft Authenticator está desabilitado no locatário: não há uso em aplicativos complementares.");
                if (p.CompanionApp is not { } comp)
                    return KnightControlOutcome.NotEvaluated(p.AuthenticatorPresent
                        ? "a fonte não devolveu companionAppAllowedState na política do Microsoft Authenticator."
                        : "a política de métodos não devolveu a configuração do Microsoft Authenticator.");
                var ok = string.Equals(comp.State, "disabled", StringComparison.OrdinalIgnoreCase);
                return Setting(ok, EntraAuthenticationMethodsPreview.ExternalId + ":companion-app", "Authenticator em aplicativos complementares",
                    StateLabel(comp.State), "desabilitado",
                    $"O Microsoft Authenticator em aplicativos complementares não está desabilitado explicitamente (estado {StateLabel(comp.State)}).",
                    "O Microsoft Authenticator em aplicativos complementares está desabilitado.");
            }),
            Ref(M365 + "5.2.3.10")),

        Entra(KnightService.EntraId, "AK-ENTRA-074", "Usuários podem instalar aplicativos e iniciar avaliações por conta própria", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Desligar, nas configurações da organização do Microsoft 365, o acesso dos usuários à Office Store e o início de avaliações de aplicativos e serviços.",
            "isOfficeStoreEnabled = false e isAppAndServicesTrialEnabled = false.",
            c => Single<M365AppsAndServicesSettings>(c, s =>
            {
                if (s.OfficeStoreEnabled is null || s.AppAndServicesTrialEnabled is null)
                    return KnightControlOutcome.NotEvaluated("a fonte não informou as duas opções de aplicativos e serviços próprios.");
                var found = $"Office Store: {(s.OfficeStoreEnabled.Value ? "liberada" : "bloqueada")}; avaliações: {(s.AppAndServicesTrialEnabled.Value ? "liberadas" : "bloqueadas")}";
                var ok = s.OfficeStoreEnabled == false && s.AppAndServicesTrialEnabled == false;
                return Setting(ok, M365AppsAndServicesSettings.ExternalId, "Aplicativos e serviços próprios dos usuários", found,
                    "Office Store e avaliações bloqueadas",
                    "Os usuários podem obter aplicativos ou iniciar avaliações por conta própria: " + found + ".",
                    "Os usuários não podem obter aplicativos da Office Store nem iniciar avaliações por conta própria.");
            }),
            Ref(M365 + "1.3.4")),

        Entra(KnightService.Forms, "AK-FORMS-001", "Proteção interna contra phishing do Microsoft Forms desligada", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Ligar a verificação interna de phishing do Microsoft Forms nas configurações da organização.",
            "isInOrgFormsPhishingScanEnabled = true.",
            c => Single<M365FormsSettings>(c, s => Setting(s.InOrgFormsPhishingScanEnabled, M365FormsSettings.ExternalId, "Proteção interna contra phishing do Forms",
                s.InOrgFormsPhishingScanEnabled switch { true => "ligada", false => "desligada", _ => "não informada" }, "ligada",
                "A verificação interna de phishing do Microsoft Forms está desligada: formulários que pedem senhas ou dados sensíveis não são detectados automaticamente.",
                "A verificação interna de phishing do Microsoft Forms está ligada.")),
            Ref(M365 + "1.3.5")),

        Entra(KnightService.EntraId, "AK-ENTRA-075", "Tempo limite de sessão ociosa do Microsoft 365 ausente ou acima de 3 horas", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.Medium,
            "Habilitar o tempo limite de sessão ociosa do Microsoft 365 (centro de administração → Configurações da organização → Segurança e privacidade) com 3 horas ou menos.",
            "Política de inatividade padrão da organização com tempo limite da aplicação \"default\" de 3 horas ou menos.",
            c => Many<EntraActivityTimeoutPolicy>(c, policies =>
            {
                var defaults = policies.Where(p => p.IsOrganizationDefault == true).ToList();
                var evidence = policies.Select(p => PolicyObject(p.Id, p.DisplayName,
                    $"Padrão da organização: {YesNo(p.IsOrganizationDefault)}; tempos: {List(p.Applications.Select(a => $"{a.ApplicationId ?? "?"} = {a.WebSessionIdleTimeout ?? "?"}"), 4)}"
                    + (p.Limitation is null ? "" : $" ({p.Limitation})"))).ToList();
                if (defaults.Count == 0)
                    return KnightControlOutcome.Exposed(
                        $"Não há política de tempo limite por inatividade padrão da organização ({policies.Count} política(s) de inatividade no locatário): a sessão ociosa do Microsoft 365 não expira.",
                        new[] { KnightObjects.AffectedSetting("idle-session-timeout", "Tempo limite de sessão ociosa", "não configurado", "3 horas ou menos") }, evidence);
                var timeouts = defaults.SelectMany(p => p.Applications)
                    .Where(a => string.Equals(a.ApplicationId, "default", StringComparison.OrdinalIgnoreCase)).ToList();
                if (timeouts.Count == 0 || timeouts.Any(t => t.Timeout is null))
                    return KnightControlOutcome.NotEvaluated("a política padrão da organização não traz um tempo limite legível para a aplicação \"default\".", evidence);
                var max = timeouts.Max(t => t.Timeout!.Value);
                var found = $"{max:hh\\:mm} (hh:mm)";
                return max <= MaxIdleTimeout
                    ? KnightControlOutcome.Passed($"O tempo limite de sessão ociosa do Microsoft 365 é {found}, dentro de 3 horas.",
                        evidence.Prepend(KnightObjects.Setting("idle-session-timeout", "Tempo limite de sessão ociosa", found, "3 horas ou menos")).ToList())
                    : KnightControlOutcome.Exposed($"O tempo limite de sessão ociosa do Microsoft 365 é {found}, acima de 3 horas.",
                        new[] { KnightObjects.AffectedSetting("idle-session-timeout", "Tempo limite de sessão ociosa", found, "3 horas ou menos") }, evidence);
            }),
            Ref(M365 + "1.3.2", KnightReferenceMatch.Partial,
                "Parte do critério: o tempo limite de sessão ociosa (3 horas ou menos). A outra parte, a política de acesso condicional com "
                + "restrições impostas pelo aplicativo, é avaliada no AK-ENTRA-069; os dois juntos cobrem a referência.")),

        Entra(KnightService.EntraId, "AK-ENTRA-076", "Usuários membros sem capacidade de MFA", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Fazer cada usuário membro registrar um método capaz de MFA (campanha de registro ou política de registro do acesso condicional).",
            "Todo usuário membro (userType = member) com isMfaCapable = true no relatório de registro de métodos.",
            c => Single<EntraMfaCapabilityInventory>(c, inv =>
            {
                var setting = KnightObjects.Setting(EntraMfaCapabilityInventory.ExternalId, "Capacidade de MFA dos membros",
                    $"{inv.MembersCapable} de {inv.Members} membro(s) capazes de MFA", "todos os membros capazes de MFA");
                var without = inv.Members - inv.MembersCapable;
                if (without > 0)
                    return KnightControlOutcome.Exposed($"{without} de {inv.Members} usuário(s) membro(s) sem método capaz de MFA registrado.",
                        inv.MembersWithoutMfa.Select(u => KnightObjects.Affected(KnightAffectedObjectKind.User, u.Id, u.DisplayName, u.UserPrincipalName,
                            "Encontrado: nenhum método capaz de MFA registrado. Esperado: membro capaz de MFA.", observed: "isMfaCapable: false")).ToList(),
                        new[] { setting }, inv.UnknownType == 0 && inv.MembersWithoutMfa.Count >= without,
                        inv.UnknownType > 0 ? $"{inv.UnknownType} registro(s) sem o tipo do usuário." : inv.MembersWithoutMfa.Count < without ? $"Lista limitada aos primeiros {inv.MembersWithoutMfa.Count}." : null,
                        without);
                if (inv.UnknownType > 0)
                    return KnightControlOutcome.NotEvaluated($"{inv.UnknownType} registro(s) do relatório sem o tipo do usuário: não é possível afirmar que todos os membros são capazes de MFA.", new[] { setting });
                return inv.Members == 0
                    ? KnightControlOutcome.NotApplicable("o relatório de registro não trouxe usuários membros.")
                    : KnightControlOutcome.Passed($"Os {inv.Members} usuário(s) membro(s) do relatório de registro são capazes de MFA.", new[] { setting });
            }),
            Ref(M365 + "5.2.3.4")),

        KnightRuleKit.Control(KnightSourceType.MicrosoftDefenderForOffice365, KnightService.DefenderForOffice365, "AK-MDO-019",
            "Mensagens denunciadas no Teams sem caixa de relatórios da organização", KnightIndicatorCategory.ThreatProtection, SeverityLevel.Medium,
            "Configurar as mensagens denunciadas pelos usuários (Outlook e Teams) para irem à caixa de relatórios da organização no portal do Microsoft Defender.",
            "Denúncias do Teams enviadas à caixa de relatórios (ReportChatMessageToCustomizedAddressEnabled com endereço), lixo/não lixo/phishing do Outlook enviados à caixa de relatórios com endereço, e ReportChatMessageEnabled = false.",
            c => Single<DefenderReportSubmissionPolicy>(c, p =>
            {
                var gaps = new List<string>();
                if (p.ReportChatMessageToCustomizedAddressEnabled != true || p.ReportChatMessageAddresses == 0) gaps.Add("denúncias do Teams sem caixa de relatórios");
                if (p.ReportChatMessageEnabled != false) gaps.Add(p.ReportChatMessageEnabled is null ? "envio das denúncias do Teams à Microsoft não informado" : "denúncias do Teams enviadas à Microsoft (ReportChatMessageEnabled)");
                if (p.ReportJunkToCustomizedAddress != true || p.ReportJunkAddresses == 0) gaps.Add("lixo eletrônico sem caixa de relatórios");
                if (p.ReportNotJunkToCustomizedAddress != true || p.ReportNotJunkAddresses == 0) gaps.Add("não lixo sem caixa de relatórios");
                if (p.ReportPhishToCustomizedAddress != true || p.ReportPhishAddresses == 0) gaps.Add("phishing sem caixa de relatórios");
                var unknown = p.ReportChatMessageToCustomizedAddressEnabled is null || p.ReportJunkToCustomizedAddress is null
                              || p.ReportNotJunkToCustomizedAddress is null || p.ReportPhishToCustomizedAddress is null;
                var found = gaps.Count == 0 ? "denúncias do Teams e do Outlook enviadas à caixa de relatórios" : List(gaps, 5);
                var obj = gaps.Count == 0
                    ? KnightObjects.Setting(p.Identity, "Política de envio de mensagens denunciadas", found, "caixa de relatórios da organização")
                    : KnightObjects.AffectedSetting(p.Identity, "Política de envio de mensagens denunciadas", found, "caixa de relatórios da organização");
                if (gaps.Count == 0) return KnightControlOutcome.Passed("As mensagens denunciadas no Teams e no Outlook vão para a caixa de relatórios da organização.", new[] { obj });
                if (unknown && gaps.All(g => g.Contains("não informado", StringComparison.Ordinal)))
                    return KnightControlOutcome.NotEvaluated("a fonte não informou o destino das mensagens denunciadas.", new[] { obj });
                return KnightControlOutcome.Exposed("As denúncias dos usuários não chegam à equipe de segurança pela caixa de relatórios: " + found + ".",
                    new[] { obj }, Array.Empty<KnightIndicatorObject>());
            }),
            Ref(M365 + "8.6.1", KnightReferenceMatch.Partial,
                "Parte do critério que vive no Defender para Office 365: o destino das mensagens denunciadas. A outra parte, a política de "
                + "mensagens do Teams que permite a denúncia, é avaliada no AK-TEAMS-017; os dois juntos cobrem a referência.")),
    };
}
