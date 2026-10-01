using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Domain;
using static AegisScore.Application.Knight.Catalog.KnightRuleKit;

namespace AegisScore.Application.Knight.Catalog;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Controles de configuração dos demais serviços do Microsoft 365
// ============================================================================
// Controles ORIGINAIS do AEGIS (textos e regras próprios) que cobrem as referências de Intune, SharePoint/OneDrive,
// Fabric, Defender para Office 365 e Purview do catálogo de referência. Cada um lê a configuração RELIDA da aquisição
// do ADM. Duas regras deste bloco, além das já firmadas no kit:
//   • POLÍTICA SEM ALCANCE — uma política de proteção só age pela regra habilitada que a aplica (ou por ser a padrão).
//     Uma política que não alcança ninguém não aprova um critério, e uma política mal configurada que alcança alguém
//     reprova, mesmo que outra esteja correta: é o que o destinatário alcançado por ela recebe;
//   • DNS NÃO RESOLVIDO ≠ SEM REGISTRO — um resolvedor que não respondeu não reprova um domínio.

internal static class M365Refs
{
    public const string M365 = "CIS-M365-7.0.0:";
}

// ---- Microsoft Intune ------------------------------------------------------------------------------------

public static class IntuneControls
{
    private static KnightIndicatorDefinition Control(string id, string title, KnightIndicatorCategory cat, SeverityLevel sev,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate, params KnightReferenceLink[] refs) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftIntune, KnightService.Intune, id, title, cat, sev, recommendation, criterion, evaluate, refs);

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        Control("AK-INT-001", "Dispositivos sem política de conformidade tratados como conformes",
            KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Marcar como não conforme todo dispositivo sem política de conformidade atribuída.",
            "Configuração de conformidade do serviço: dispositivo sem política atribuída = não conforme.",
            c => Single<IntuneComplianceSettings>(c, s => Setting(s.SecureByDefault, IntuneComplianceSettings.ExternalId,
                "Dispositivos sem política de conformidade atribuída",
                s.SecureByDefault switch { true => "marcados como não conformes", false => "marcados como conformes", _ => "não informado" },
                "marcados como não conformes",
                "Dispositivos sem política de conformidade atribuída são considerados CONFORMES — o acesso condicional que exige dispositivo conforme os aceita.",
                "Dispositivos sem política de conformidade atribuída são marcados como não conformes.")),
            Ref(M365Refs.M365 + "4.1")),

        Control("AK-INT-002", "Registro de dispositivos pessoais permitido na restrição padrão",
            KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Bloquear, na restrição de registro padrão, o registro de dispositivos pessoais em todas as plataformas (ou a plataforma inteira quando não usada).",
            "Restrição de registro PADRÃO: cada plataforma bloqueada ou com registro de dispositivo pessoal bloqueado.",
            c => Many<IntuneEnrollmentRestriction>(c, all =>
            {
                var def = all.FirstOrDefault(r => r.IsDefault);
                if (def is null)
                    return KnightControlOutcome.NotEvaluated("a coleta não trouxe a restrição de registro padrão do locatário.");
                var others = all.Count(r => !r.IsDefault);
                return Population(def.Platforms,
                    p => KnightItemCheck.Of(
                        p.PlatformBlocked == true || p.PersonalDeviceEnrollmentBlocked == true ? true
                            : p.PlatformBlocked is null && p.PersonalDeviceEnrollmentBlocked is null ? null : false,
                        p.PlatformBlocked == true ? "plataforma bloqueada"
                            : p.PersonalDeviceEnrollmentBlocked == true ? "dispositivo pessoal bloqueado"
                            : p.PersonalDeviceEnrollmentBlocked is null ? "não informado" : "dispositivo pessoal permitido",
                        "plataforma ou dispositivo pessoal bloqueado"),
                    p => SettingObject($"{def.Id}:{p.Platform}", $"{p.Platform} — restrição padrão"),
                    (n, t) => $"{n} de {t} plataforma(s) aceitam o registro de dispositivos pessoais na restrição padrão."
                        + (others > 0 ? $" Há {others} restrição(ões) com prioridade maior, por grupo, avaliadas só como evidência." : ""),
                    t => $"As {t} plataforma(s) da restrição padrão bloqueiam o registro de dispositivos pessoais.",
                    () => KnightControlOutcome.NotEvaluated("a restrição padrão não informou nenhuma plataforma."));
            }),
            Ref(M365Refs.M365 + "4.2")),
    };
}

// ---- SharePoint e OneDrive -------------------------------------------------------------------------------

public static class SharePointControls
{
    private static KnightIndicatorDefinition Control(string id, string title, KnightIndicatorCategory cat, SeverityLevel sev,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate, params KnightReferenceLink[] refs) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftSharePoint, KnightService.SharePointOnline, id, title, cat, sev, recommendation, criterion, evaluate, refs);

    private static string Capability(string? v) => v switch
    {
        "disabled" => "somente pessoas da organização",
        "existingExternalUserSharingOnly" => "convidados já existentes",
        "externalUserSharingOnly" => "convidados novos e existentes (com entrada)",
        "externalUserAndGuestSharing" => "qualquer pessoa, inclusive por link sem entrada",
        null => "não informado",
        _ => v,
    };

    /// <summary>Compartilhamento externo desligado torna os critérios que o restringem "não aplicáveis".</summary>
    private static KnightControlOutcome? ExternalSharingOff(KnightEvaluationContext c)
    {
        var graph = c.Configuration.Read<SharePointTenantSettings>();
        return graph.Collected && graph.Single?.SharingCapability == SharePointTenantSettings.DisabledCapability
            ? KnightControlOutcome.NotApplicable("o compartilhamento externo está desligado no locatário (somente pessoas da organização).",
                new[] { KnightObjects.Setting("sharingCapability", "Compartilhamento externo do SharePoint", Capability("disabled"), "restrito") })
            : null;
    }

    private static KnightControlOutcome Admin(KnightEvaluationContext c, Func<SharePointAdminTenant, KnightControlOutcome> rule) =>
        Single(c, rule);

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        Control("AK-SPO-001", "Autenticação legada aceita pelo SharePoint e OneDrive", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Desligar os protocolos de autenticação legada no SharePoint para exigir autenticação moderna.",
            "Configuração do locatário: protocolos de autenticação legada desligados.",
            c =>
            {
                var graph = c.Configuration.Read<SharePointTenantSettings>();
                if (graph.Collected && graph.Single is { IsLegacyAuthProtocolsEnabled: { } v })
                    return Setting(!v, "isLegacyAuthProtocolsEnabled", "Protocolos de autenticação legada", v ? "ligados" : "desligados", "desligados",
                        "O SharePoint e o OneDrive aceitam protocolos de autenticação legada, que não carregam segundo fator.",
                        "Os protocolos de autenticação legada estão desligados: o acesso exige autenticação moderna.");
                return Admin(c, a => Setting(a.LegacyAuthProtocolsEnabled is { } l ? !l : null, "legacyAuthProtocolsEnabled",
                    "Protocolos de autenticação legada", a.LegacyAuthProtocolsEnabled switch { true => "ligados", false => "desligados", _ => "não informado" },
                    "desligados",
                    "O SharePoint e o OneDrive aceitam protocolos de autenticação legada, que não carregam segundo fator.",
                    "Os protocolos de autenticação legada estão desligados: o acesso exige autenticação moderna."));
            },
            Ref(M365Refs.M365 + "7.2.1")),

        Control("AK-SPO-002", "Integração do SharePoint e OneDrive com o B2B do Entra desligada", KnightIndicatorCategory.GuestAccess, SeverityLevel.Low,
            "Ligar a integração com o Microsoft Entra B2B para que convidados do SharePoint e OneDrive sejam contas de convidado governadas pelo diretório.",
            "Configuração do locatário: integração com o Microsoft Entra B2B ligada.",
            c => Admin(c, a => Setting(a.EnableAzureADB2BIntegration, "enableAzureADB2BIntegration", "Integração com o Microsoft Entra B2B",
                a.EnableAzureADB2BIntegration switch { true => "ligada", false => "desligada", _ => "não informado" }, "ligada",
                "Convidados do SharePoint e OneDrive entram fora do diretório: não passam pelas políticas de acesso condicional nem pelas revisões de convidados.",
                "A integração com o B2B está ligada: convidados do SharePoint e OneDrive são contas de convidado do diretório.")),
            Ref(M365Refs.M365 + "7.2.2")),

        Control("AK-SPO-003", "Compartilhamento externo do SharePoint aceita links sem entrada", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Restringir o compartilhamento externo do SharePoint a convidados novos e existentes (com entrada) ou menos.",
            "Nível de compartilhamento externo do locatário: sem links “qualquer pessoa”.",
            c => Single<SharePointTenantSettings>(c, s => Setting(
                s.SharingCapability is null ? null : s.SharingCapability != SharePointTenantSettings.AnyoneCapability,
                "sharingCapability", "Compartilhamento externo do SharePoint", Capability(s.SharingCapability),
                "convidados novos e existentes, ou mais restrito",
                "O SharePoint permite links “qualquer pessoa”: quem recebe o link abre o conteúdo sem entrar.",
                $"O compartilhamento externo do SharePoint está em “{Capability(s.SharingCapability)}”, sem links anônimos.")),
            Ref(M365Refs.M365 + "7.2.3")),

        Control("AK-SPO-004", "Compartilhamento externo do OneDrive não restrito à organização", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Restringir o compartilhamento do OneDrive a pessoas da organização; compartilhar com fora por sites do SharePoint governados.",
            "Nível de compartilhamento do OneDrive: somente pessoas da organização.",
            c => Admin(c, a => Setting(a.OneDriveSharingCapability is null ? null : a.OneDriveSharingCapability == SharePointTenantSettings.DisabledCapability,
                "oneDriveSharingCapability", "Compartilhamento do OneDrive", Capability(a.OneDriveSharingCapability), "somente pessoas da organização",
                $"O OneDrive permite compartilhar com pessoas de fora (“{Capability(a.OneDriveSharingCapability)}”).",
                "O OneDrive está restrito a pessoas da organização.")),
            Ref(M365Refs.M365 + "7.2.4")),

        Control("AK-SPO-005", "Convidados podem recompartilhar itens que não são deles", KnightIndicatorCategory.GuestAccess, SeverityLevel.High,
            "Impedir que convidados compartilhem novamente arquivos, pastas e sites que não possuem.",
            "Configuração do locatário: recompartilhamento por convidados desligado.",
            c => ExternalSharingOff(c) ?? Single<SharePointTenantSettings>(c, s => Setting(
                s.IsResharingByExternalUsersEnabled is { } v ? !v : null, "isResharingByExternalUsersEnabled", "Recompartilhamento por convidados",
                s.IsResharingByExternalUsersEnabled switch { true => "permitido", false => "bloqueado", _ => "não informado" }, "bloqueado",
                "Convidados podem compartilhar novamente o que receberam, levando o conteúdo a pessoas que ninguém da organização convidou.",
                "Convidados não podem recompartilhar o que não possuem.")),
            Ref(M365Refs.M365 + "7.2.5")),

        Control("AK-SPO-006", "Compartilhamento externo sem lista de domínios permitidos", KnightIndicatorCategory.DataProtection, SeverityLevel.Low,
            "Restringir o compartilhamento externo a uma lista de domínios permitidos (organizações com as quais há relação).",
            "Modo de restrição por domínio: lista de permitidos, com ao menos um domínio.",
            c => ExternalSharingOff(c) ?? Single<SharePointTenantSettings>(c, s =>
            {
                var allow = string.Equals(s.SharingDomainRestrictionMode, "allowList", StringComparison.OrdinalIgnoreCase);
                bool? ok = s.SharingDomainRestrictionMode is null ? null : allow && s.SharingAllowedDomainList.Count > 0;
                var found = s.SharingDomainRestrictionMode switch
                {
                    "allowList" => $"lista de permitidos ({s.SharingAllowedDomainList.Count} domínio(s): {List(s.SharingAllowedDomainList)})",
                    "blockList" => $"lista de bloqueados ({s.SharingBlockedDomainList.Count} domínio(s))",
                    "none" => "sem restrição por domínio",
                    null => "não informado",
                    var other => other,
                };
                return Setting(ok, "sharingDomainRestrictionMode", "Restrição de compartilhamento por domínio", found, "lista de domínios permitidos",
                    "O compartilhamento externo não está limitado a domínios conhecidos: qualquer domínio pode receber convites.",
                    "O compartilhamento externo está limitado a uma lista de domínios permitidos.");
            }),
            Ref(M365Refs.M365 + "7.2.6")),

        Control("AK-SPO-007", "Link padrão de compartilhamento não é para pessoas específicas", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Definir o tipo de link padrão como “pessoas específicas”.",
            "Tipo de link padrão do SharePoint e OneDrive: pessoas específicas.",
            c => Admin(c, a => Setting(a.DefaultSharingLinkType is null ? null : a.DefaultSharingLinkType == "direct",
                "defaultSharingLinkType", "Tipo de link padrão",
                a.DefaultSharingLinkType switch { "direct" => "pessoas específicas", "internal" => "pessoas da organização", "anonymousAccess" => "qualquer pessoa", "none" => "definido pela política de cada site", null => "não informado", var o => o },
                "pessoas específicas",
                "O link sugerido ao compartilhar alcança mais gente do que a pessoa escolhida: quem o repassa leva o acesso junto.",
                "O link padrão é para pessoas específicas.")),
            Ref(M365Refs.M365 + "7.2.7")),

        Control("AK-SPO-008", "Compartilhamento externo não restrito a grupos de segurança", KnightIndicatorCategory.DataProtection, SeverityLevel.Medium,
            "Permitir o compartilhamento externo apenas a membros de grupos de segurança designados.",
            "Configuração do locatário: lista de grupos de segurança autorizados a compartilhar externamente, não vazia.",
            c => ExternalSharingOff(c) ?? Admin(c, a => Setting(a.GuestSharingGroupAllowList.Count > 0,
                "guestSharingGroupAllowList", "Grupos autorizados a compartilhar externamente",
                a.GuestSharingGroupAllowList.Count > 0 ? $"{a.GuestSharingGroupAllowList.Count} grupo(s)" : "nenhum (todos os usuários podem)",
                "ao menos um grupo designado",
                "Qualquer usuário pode compartilhar com pessoas de fora; não há grupo responsável por essa decisão.",
                $"Só membros de {a.GuestSharingGroupAllowList.Count} grupo(s) designado(s) compartilham externamente.")),
            Ref(M365Refs.M365 + "7.2.8")),

        Control("AK-SPO-009", "Acesso de convidados sem expiração automática", KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium,
            "Exigir expiração do acesso de convidados a sites e OneDrive em até 30 dias.",
            "Expiração de convidados exigida, em até 30 dias.",
            c => Admin(c, a =>
            {
                bool? ok = a.ExternalUserExpirationRequired is null ? null
                    : a.ExternalUserExpirationRequired == true && a.ExternalUserExpireInDays is > 0 and <= 30;
                var found = a.ExternalUserExpirationRequired switch
                {
                    true => $"exigida em {a.ExternalUserExpireInDays?.ToString() ?? "?"} dia(s)",
                    false => "não exigida",
                    _ => "não informado",
                };
                return Setting(ok, "externalUserExpiration", "Expiração do acesso de convidados", found, "exigida em até 30 dias",
                    "O acesso de convidados a sites e OneDrive não expira dentro do prazo esperado: permanece enquanto ninguém o retirar.",
                    $"O acesso de convidados expira em {a.ExternalUserExpireInDays} dia(s).");
            }),
            Ref(M365Refs.M365 + "7.2.9")),

        Control("AK-SPO-010", "Reautenticação por código de verificação sem prazo curto", KnightIndicatorCategory.GuestAccess, SeverityLevel.Medium,
            "Exigir que quem usa código de verificação se reautentique em até 15 dias.",
            "Reautenticação por código de verificação exigida, em até 15 dias.",
            c => Admin(c, a =>
            {
                bool? ok = a.EmailAttestationRequired is null ? null
                    : a.EmailAttestationRequired == true && a.EmailAttestationReAuthDays is > 0 and <= 15;
                var found = a.EmailAttestationRequired switch
                {
                    true => $"exigida a cada {a.EmailAttestationReAuthDays?.ToString() ?? "?"} dia(s)",
                    false => "não exigida",
                    _ => "não informado",
                };
                return Setting(ok, "emailAttestation", "Reautenticação por código de verificação", found, "exigida em até 15 dias",
                    "Quem entrou com código de verificação continua com acesso sem provar de novo que ainda controla aquele e-mail.",
                    $"Quem usa código de verificação se reautentica a cada {a.EmailAttestationReAuthDays} dia(s).");
            }),
            Ref(M365Refs.M365 + "7.2.10")),

        Control("AK-SPO-011", "Permissão padrão dos links de compartilhamento não é somente leitura", KnightIndicatorCategory.DataProtection, SeverityLevel.Medium,
            "Definir a permissão padrão dos links como “exibir”.",
            "Permissão padrão dos links: exibir.",
            c => Admin(c, a => Setting(a.DefaultLinkPermission is null ? null : a.DefaultLinkPermission == "view",
                "defaultLinkPermission", "Permissão padrão dos links",
                a.DefaultLinkPermission switch { "view" => "exibir", "edit" => "editar", "none" => "definida pela política de cada site", null => "não informado", var o => o },
                "exibir",
                "O link sugerido concede edição: quem o recebe altera ou apaga o conteúdo sem que isso tenha sido escolhido.",
                "O link padrão concede apenas exibição.")),
            Ref(M365Refs.M365 + "7.2.11")),

        Control("AK-SPO-012", "Download de arquivos infectados permitido", KnightIndicatorCategory.ThreatProtection, SeverityLevel.Medium,
            "Bloquear o download de arquivos que o antivírus do SharePoint identificou como infectados.",
            "Configuração do locatário: download de arquivo infectado bloqueado.",
            c => Admin(c, a => Setting(a.DisallowInfectedFileDownload, "disallowInfectedFileDownload", "Download de arquivos infectados",
                a.DisallowInfectedFileDownload switch { true => "bloqueado", false => "permitido", _ => "não informado" }, "bloqueado",
                "Um arquivo já identificado como infectado ainda pode ser baixado e aberto num dispositivo.",
                "Arquivos identificados como infectados não podem ser baixados.")),
            Ref(M365Refs.M365 + "7.3.1")),

        Control("AK-SPO-013", "Sincronização do OneDrive permitida em dispositivos não gerenciados", KnightIndicatorCategory.DeviceGovernance, SeverityLevel.Medium,
            "Restringir o aplicativo de sincronização do OneDrive a computadores ingressados nos domínios autorizados.",
            "Configuração do locatário: sincronização restrita a domínios autorizados, com ao menos um domínio.",
            c => Single<SharePointTenantSettings>(c, s => Setting(
                s.IsUnmanagedSyncAppForTenantRestricted is null ? null : s.IsUnmanagedSyncAppForTenantRestricted == true && s.AllowedDomainGuidsForSyncAppCount > 0,
                "isUnmanagedSyncAppForTenantRestricted", "Sincronização do OneDrive em dispositivos não gerenciados",
                s.IsUnmanagedSyncAppForTenantRestricted switch
                {
                    true => $"restrita a {s.AllowedDomainGuidsForSyncAppCount} domínio(s) autorizado(s)",
                    false => "permitida em qualquer computador",
                    _ => "não informado",
                },
                "restrita a domínios autorizados",
                "O aplicativo de sincronização copia bibliotecas para qualquer computador, inclusive pessoais, fora da gestão da organização.",
                "A sincronização está restrita a computadores dos domínios autorizados.")),
            Ref(M365Refs.M365 + "7.3.2")),
    };
}

// ---- Microsoft Fabric ------------------------------------------------------------------------------------

public static class FabricControls
{
    private static KnightIndicatorDefinition Control(string id, string title, KnightIndicatorCategory cat, SeverityLevel sev,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate, params KnightReferenceLink[] refs) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftFabric, KnightService.Fabric, id, title, cat, sev, recommendation, criterion, evaluate, refs);

    private enum Expect { DisabledOrGroups, Disabled, Enabled }

    private static string State(FabricTenantSetting s) =>
        s.Enabled switch
        {
            false => "desligada",
            true when s.EnabledSecurityGroups.Count > 0 => $"ligada só para {s.EnabledSecurityGroups.Count} grupo(s): {List(s.EnabledSecurityGroups, 3)}",
            true => "ligada para a organização inteira",
            _ => "não informado",
        };

    private static KnightControlOutcome Tenant(KnightEvaluationContext c, string settingName, string label, Expect expect,
        string exposed, string passed) =>
        Many<FabricTenantSetting>(c, all =>
        {
            var s = all.FirstOrDefault(x => string.Equals(x.SettingName, settingName, StringComparison.OrdinalIgnoreCase));
            if (s is null)
                return KnightControlOutcome.NotEvaluated($"a lista de configurações do Fabric não trouxe “{settingName}”.");
            bool? ok = s.Enabled is null ? null : expect switch
            {
                Expect.Disabled => s.Enabled == false,
                Expect.Enabled => s.Enabled == true,
                _ => s.Enabled == false || s.RestrictedToGroups,
            };
            var expected = expect switch
            {
                Expect.Disabled => "desligada",
                Expect.Enabled => "ligada",
                _ => "desligada ou só para grupos designados",
            };
            return Setting(ok, settingName, s.Title ?? label, State(s), expected, exposed, passed);
        });

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        Control("AK-FAB-001", "Convidados acessam o Fabric sem restrição", KnightIndicatorCategory.GuestAccess, SeverityLevel.High,
            "Desligar o acesso de convidados ao Fabric, ou restringi-lo a grupos designados.",
            "Configuração AllowGuestUserToAccessSharedContent desligada ou restrita a grupos.",
            c => Tenant(c, "AllowGuestUserToAccessSharedContent", "Convidados podem acessar o Microsoft Fabric", Expect.DisabledOrGroups,
                "Contas de convidado podem acessar o Fabric em toda a organização.", "O acesso de convidados ao Fabric está desligado ou restrito a grupos."),
            Ref(M365Refs.M365 + "9.1.1")),
        Control("AK-FAB-002", "Convite de usuários externos pelo Fabric sem restrição", KnightIndicatorCategory.GuestAccess, SeverityLevel.High,
            "Desligar, ou restringir a grupos, o convite de usuários externos por compartilhamento de itens do Fabric.",
            "Configuração ExternalSharingV2 desligada ou restrita a grupos.",
            c => Tenant(c, "ExternalSharingV2", "Usuários podem convidar convidados pelo compartilhamento de itens", Expect.DisabledOrGroups,
                "Qualquer usuário pode convidar pessoas de fora ao compartilhar itens do Fabric.", "O convite de externos pelo Fabric está desligado ou restrito a grupos."),
            Ref(M365Refs.M365 + "9.1.2")),
        Control("AK-FAB-003", "Convidados navegam pelo conteúdo do Fabric", KnightIndicatorCategory.GuestAccess, SeverityLevel.High,
            "Desligar a navegação de convidados pelo conteúdo do Fabric, ou restringi-la a grupos.",
            "Configuração ElevatedGuestsTenant desligada ou restrita a grupos.",
            c => Tenant(c, "ElevatedGuestsTenant", "Convidados podem navegar e acessar conteúdo do Fabric", Expect.DisabledOrGroups,
                "Convidados podem navegar pelo conteúdo do Fabric, além do que foi compartilhado com eles.", "A navegação de convidados pelo conteúdo está desligada ou restrita a grupos."),
            Ref(M365Refs.M365 + "9.1.3")),
        Control("AK-FAB-004", "Publicação na web aberta a toda a organização", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Desligar a publicação na web, ou restringi-la a grupos designados.",
            "Configuração PublishToWeb desligada ou restrita a grupos.",
            c => Tenant(c, "PublishToWeb", "Publicar na Web", Expect.DisabledOrGroups,
                "Qualquer usuário pode publicar relatórios na internet, sem autenticação para quem abre.", "A publicação na web está desligada ou restrita a grupos."),
            Ref(M365Refs.M365 + "9.1.4")),
        Control("AK-FAB-005", "Visuais R e Python habilitados no Fabric", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Low,
            "Desligar a interação e o compartilhamento de visuais R e Python.",
            "Configuração RScriptVisual desligada.",
            c => Tenant(c, "RScriptVisual", "Interagir com visuais R e Python e compartilhá-los", Expect.Disabled,
                "Relatórios podem executar scripts R e Python embutidos em visuais.", "Os visuais R e Python estão desligados."),
            Ref(M365Refs.M365 + "9.1.5")),
        Control("AK-FAB-006", "Rótulos de confidencialidade não aplicáveis no Fabric", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Ligar a aplicação de rótulos de confidencialidade ao conteúdo do Fabric.",
            "Configuração EimInformationProtectionEdit ligada.",
            c => Tenant(c, "EimInformationProtectionEdit", "Permitir que usuários apliquem rótulos de confidencialidade", Expect.Enabled,
                "Conteúdo do Fabric não pode receber rótulo de confidencialidade: a classificação da organização não o acompanha.", "Usuários podem aplicar rótulos de confidencialidade no Fabric."),
            Ref(M365Refs.M365 + "9.1.6")),
        Control("AK-FAB-007", "Links compartilháveis para toda a organização sem restrição", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Desligar, ou restringir a grupos, os links que dão acesso a toda a organização.",
            "Configuração ShareLinkToEntireOrg desligada ou restrita a grupos.",
            c => Tenant(c, "ShareLinkToEntireOrg", "Permitir links compartilháveis para toda a organização", Expect.DisabledOrGroups,
                "Qualquer usuário pode gerar links que dão acesso ao item a toda a organização.", "Os links para toda a organização estão desligados ou restritos a grupos."),
            Ref(M365Refs.M365 + "9.1.7")),
        Control("AK-FAB-008", "Compartilhamento externo de dados do Fabric sem restrição", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Desligar o compartilhamento externo de dados, ou restringi-lo a grupos.",
            "Configuração AllowExternalDataSharingSwitch desligada ou restrita a grupos.",
            c => Tenant(c, "AllowExternalDataSharingSwitch", "Compartilhamento externo de dados", Expect.DisabledOrGroups,
                "Qualquer usuário pode compartilhar dados do OneLake com locatários externos.", "O compartilhamento externo de dados está desligado ou restrito a grupos."),
            Ref(M365Refs.M365 + "9.1.8")),
        Control("AK-FAB-009", "Autenticação por ResourceKey não bloqueada no Fabric", KnightIndicatorCategory.AuthenticationPolicy, SeverityLevel.High,
            "Bloquear a autenticação por ResourceKey (chaves de conjuntos de dados de streaming).",
            "Configuração BlockResourceKeyAuthentication ligada.",
            c => Tenant(c, "BlockResourceKeyAuthentication", "Bloquear autenticação por ResourceKey", Expect.Enabled,
                "Conjuntos de dados de streaming aceitam envio de dados autenticado só por uma chave, sem identidade.", "A autenticação por ResourceKey está bloqueada."),
            Ref(M365Refs.M365 + "9.1.9")),
        Control("AK-FAB-010", "Entidades de serviço chamam as APIs do Fabric sem restrição", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Restringir a grupos designados o uso das APIs públicas do Fabric por entidades de serviço.",
            "Configuração ServicePrincipalAccessGlobalAPIs desligada ou restrita a grupos.",
            c => Tenant(c, "ServicePrincipalAccessGlobalAPIs", "Entidades de serviço podem chamar as APIs públicas do Fabric", Expect.DisabledOrGroups,
                "Qualquer entidade de serviço do locatário pode usar as APIs do Fabric.", "O uso das APIs por entidades de serviço está desligado ou restrito a grupos."),
            Ref(M365Refs.M365 + "9.1.10")),
        Control("AK-FAB-011", "Entidades de serviço criam e usam perfis no Fabric", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Desligar, ou restringir a grupos, a criação e o uso de perfis por entidades de serviço.",
            "Configuração AllowServicePrincipalsCreateAndUseProfiles desligada ou restrita a grupos.",
            c => Tenant(c, "AllowServicePrincipalsCreateAndUseProfiles", "Permitir que entidades de serviço criem e usem perfis", Expect.DisabledOrGroups,
                "Entidades de serviço podem criar perfis e operar conteúdo em nome deles.", "A criação de perfis por entidades de serviço está desligada ou restrita a grupos."),
            Ref(M365Refs.M365 + "9.1.11")),
        Control("AK-FAB-012", "Entidades de serviço criam workspaces, conexões e pipelines", KnightIndicatorCategory.ApplicationGovernance, SeverityLevel.High,
            "Restringir a grupos designados a criação de workspaces, conexões e pipelines de implantação por entidades de serviço.",
            "Configuração ServicePrincipalAccessPermissionAPIs desligada ou restrita a grupos.",
            c => Tenant(c, "ServicePrincipalAccessPermissionAPIs", "Entidades de serviço podem criar workspaces, conexões e pipelines", Expect.DisabledOrGroups,
                "Qualquer entidade de serviço pode criar workspaces, conexões e pipelines de implantação.", "Essa criação por entidades de serviço está desligada ou restrita a grupos."),
            Ref(M365Refs.M365 + "9.1.12")),
    };
}

// ---- Microsoft Defender para Office 365 -------------------------------------------------------------------

public static class DefenderForOffice365Controls
{
    private static KnightIndicatorDefinition Control(string id, string title, KnightIndicatorCategory cat, SeverityLevel sev,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate, params KnightReferenceLink[] refs) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftDefenderForOffice365, KnightService.DefenderForOffice365, id, title, cat, sev,
            recommendation, criterion, evaluate, refs);

    /// <summary>Extensões de alto risco que o AEGIS exige no filtro de anexos (lista própria, fundamentada nos tipos executáveis e de macro).</summary>
    public static IReadOnlyList<string> HighRiskExtensions { get; } = new[]
    {
        "ace", "ani", "apk", "app", "appx", "arj", "bat", "cab", "cmd", "com", "deb", "dex", "dll", "docm", "elf", "exe",
        "hta", "img", "iso", "jar", "jnlp", "kext", "lha", "lib", "lnk", "lzh", "macho", "msc", "msi", "msix", "msp", "mst",
        "pif", "ppa", "ppam", "reg", "rev", "scf", "scr", "sct", "sys", "uif", "vb", "vbe", "vbs", "vxd", "wsc", "wsf",
        "wsh", "xll", "xlsm", "xz", "z",
    };

    private static string Reach(DefenderPolicyReach r) =>
        r.IsDefault ? "política padrão (todos os destinatários sem política própria)"
        : !r.HasRule ? "sem regra que a aplique (não alcança ninguém)"
        : !string.Equals(r.RuleState, "Enabled", StringComparison.OrdinalIgnoreCase) ? $"regra {r.RuleState?.ToLowerInvariant() ?? "sem estado"} (não alcança ninguém)"
        : $"regra habilitada para {r.SentTo.Count} pessoa(s), {r.SentToMemberOf.Count} grupo(s) e {r.RecipientDomainIs.Count} domínio(s)";

    private static KnightIndicatorObject PolicyObj(string id, string? name, DefenderPolicyReach? reach) =>
        PolicyObject(id, name, reach is null ? "" : "Alcance: " + Reach(reach) + ".");

    /// <summary>
    /// Critério sobre as políticas EFETIVAS de um tipo: toda política que alcança alguém precisa atender; nenhuma
    /// efetiva → exposto (o critério não vale para ninguém).
    /// </summary>
    private static KnightControlOutcome Effective<T>(KnightEvaluationContext c, Func<T, bool> effective, Func<T, string> id,
        Func<T, string?> name, Func<T, DefenderPolicyReach?> reach, Func<T, KnightItemCheck> check,
        string what, string noneEvidence) where T : class =>
        Many<T>(c, all =>
        {
            var eff = all.Where(effective).ToList();
            if (eff.Count == 0)
                return KnightControlOutcome.Exposed(noneEvidence, Array.Empty<KnightIndicatorObject>(),
                    all.Select(p => PolicyObj(id(p), name(p), reach(p))).Take(50).ToList());
            return Population(eff, check, p => PolicyObj(id(p), name(p), reach(p)),
                (n, t) => $"{n} de {t} política(s) efetiva(s) não atendem: {what}.",
                t => $"As {t} política(s) efetiva(s) atendem: {what}.",
                () => KnightControlOutcome.NotEvaluated("nenhuma política efetiva."));
        });

    private static IEnumerable<string> CustomDomains(KnightEvaluationContext c) =>
        c.Configuration.Read<DefenderAcceptedDomain>().Items.Where(d => !d.IsMicrosoftManaged).Select(d => d.DomainName.ToLowerInvariant()).Distinct();

    /// <summary>
    /// A consulta DNS cobre os domínios aceitos próprios até o teto da coleta. Se há domínio aceito sem registro
    /// consultado, a população não está completa: um domínio reprovado continua reprovado, mas nada é aprovado.
    /// </summary>
    private static (bool Complete, string? Reason) DnsCompleteness(KnightEvaluationContext c, IReadOnlyList<DefenderDnsRecord> records)
    {
        var queried = records.Select(r => r.Domain.ToLowerInvariant()).ToHashSet();
        var missing = CustomDomains(c).Count(d => !queried.Contains(d));
        return missing == 0 ? (true, null)
            : (false, $"{missing} domínio(s) aceito(s) não foram consultados no DNS nesta coleta (teto de consulta por coleta).");
    }

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        Control("AK-MDO-001", "Links Seguros não cobrem e-mail, Teams e aplicativos do Office", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Aplicar Links Seguros a e-mail, Teams e aplicativos do Office, com verificação em tempo real, rastreamento de cliques e sem permitir seguir para o site original.",
            "Toda política de Links Seguros efetiva: e-mail, Teams e Office ligados; varredura em tempo real; cliques rastreados; sem clique direto.",
            c => Effective<DefenderSafeLinksPolicy>(c, p => p.Reach.Effective || p.IsBuiltInProtection, p => p.Identity, p => p.Name,
                p => p.IsBuiltInProtection ? DefenderPolicyReach.Default : p.Reach,
                p =>
                {
                    var values = new[] { p.EnableSafeLinksForEmail, p.EnableSafeLinksForTeams, p.EnableSafeLinksForOffice, p.ScanUrls, p.TrackClicks,
                        p.AllowClickThrough is { } a ? !a : null };
                    bool? ok = values.Any(v => v is null) ? (values.Any(v => v == false) ? false : null) : values.All(v => v == true);
                    return KnightItemCheck.Of(ok,
                        $"e-mail {YesNo(p.EnableSafeLinksForEmail)}, Teams {YesNo(p.EnableSafeLinksForTeams)}, Office {YesNo(p.EnableSafeLinksForOffice)}, "
                        + $"varredura {YesNo(p.ScanUrls)}, rastreamento {YesNo(p.TrackClicks)}, clique direto {YesNo(p.AllowClickThrough)}",
                        "e-mail, Teams e Office ligados; varredura e rastreamento ligados; clique direto desligado");
                },
                "Links Seguros em e-mail, Teams e Office, com varredura, rastreamento e sem clique direto",
                "Nenhuma política de Links Seguros alcança destinatários: links recebidos não são verificados no clique."),
            Ref(M365Refs.M365 + "2.1.1")),

        Control("AK-MDO-002", "Filtro de tipos de anexo desligado na política antimalware", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Ligar o filtro de tipos comuns de anexo em todas as políticas antimalware efetivas.",
            "Toda política antimalware efetiva com o filtro de tipos de arquivo ligado.",
            c => Effective<DefenderMalwarePolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p => KnightItemCheck.Of(p.EnableFileFilter, $"filtro de tipos de arquivo {YesNo(p.EnableFileFilter)}", "filtro ligado"),
                "filtro de tipos comuns de anexo ligado",
                "Nenhuma política antimalware alcança destinatários."),
            Ref(M365Refs.M365 + "2.1.2")),

        Control("AK-MDO-003", "Administradores não notificados quando um usuário interno envia malware", KnightIndicatorCategory.ThreatProtection, SeverityLevel.Medium,
            "Ligar a notificação ao administrador quando um remetente interno enviar malware, com um endereço de destino definido.",
            "Toda política antimalware efetiva notificando um administrador sobre remetentes internos de malware.",
            c => Effective<DefenderMalwarePolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p => KnightItemCheck.Of(p.EnableInternalSenderAdminNotifications is null ? null : p.EnableInternalSenderAdminNotifications == true && p.InternalSenderAdminAddressSet,
                    $"notificação {YesNo(p.EnableInternalSenderAdminNotifications)}, endereço {(p.InternalSenderAdminAddressSet ? "definido" : "não definido")}",
                    "notificação ligada, com endereço definido"),
                "notificação de remetente interno de malware ao administrador",
                "Nenhuma política antimalware alcança destinatários."),
            Ref(M365Refs.M365 + "2.1.3")),

        Control("AK-MDO-004", "Anexos Seguros não bloqueiam anexos maliciosos", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Aplicar Anexos Seguros com a ação de bloqueio a todos os destinatários.",
            "Toda política de Anexos Seguros efetiva ligada, com ação de bloqueio.",
            c => Effective<DefenderSafeAttachmentPolicy>(c, p => p.Reach.Effective || p.IsBuiltInProtection, p => p.Identity, p => p.Name,
                p => p.IsBuiltInProtection ? DefenderPolicyReach.Default : p.Reach,
                p => KnightItemCheck.Of(p.Enable is null || p.Action is null ? null : p.Enable == true && string.Equals(p.Action, "Block", StringComparison.OrdinalIgnoreCase),
                    $"política {YesNo(p.Enable)}, ação {p.Action ?? "não informada"}", "ligada, ação Block"),
                "Anexos Seguros ligados com ação de bloqueio",
                "Nenhuma política de Anexos Seguros alcança destinatários: anexos não são detonados antes da entrega."),
            Ref(M365Refs.M365 + "2.1.4")),

        Control("AK-MDO-005", "Anexos Seguros desligados para SharePoint, OneDrive e Teams", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Ligar Anexos Seguros para SharePoint, OneDrive e Teams e, havendo licença, Documentos Seguros sem permitir abrir arquivo marcado.",
            "Configuração global: Anexos Seguros para SharePoint, OneDrive e Teams ligados; Documentos Seguros (quando informado) ligados e sem abertura de arquivo malicioso.",
            c => Single<DefenderAtpPolicy>(c, a =>
            {
                bool? safeDocsOk = a.EnableSafeDocs is null ? true : a.EnableSafeDocs == true && a.AllowSafeDocsOpen != true;
                bool? ok = a.EnableATPForSPOTeamsODB is null ? null : a.EnableATPForSPOTeamsODB == true && safeDocsOk == true;
                return Setting(ok, DefenderAtpPolicy.ExternalId, "Anexos Seguros para SharePoint, OneDrive e Teams",
                    $"SharePoint/OneDrive/Teams {YesNo(a.EnableATPForSPOTeamsODB)}; Documentos Seguros {YesNo(a.EnableSafeDocs)}; abrir mesmo assim {YesNo(a.AllowSafeDocsOpen)}",
                    "ligados, sem permitir abrir arquivo marcado",
                    "Arquivos maliciosos em bibliotecas e conversas não são bloqueados pela proteção do Defender para Office 365.",
                    "Anexos Seguros protegem SharePoint, OneDrive e Teams.");
            }),
            Ref(M365Refs.M365 + "2.1.5")),

        Control("AK-MDO-006", "Administradores não notificados sobre envio suspeito em massa", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Ligar, nas políticas antispam de saída, a notificação e a cópia oculta a administradores quando um remetente é bloqueado ou envia spam.",
            "Toda política antispam de saída efetiva com notificação e cópia oculta a destinatários definidos.",
            c => Effective<DefenderOutboundSpamPolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p => KnightItemCheck.Of(p.NotifyOutboundSpam is null || p.BccSuspiciousOutboundMail is null ? null
                        : p.NotifyOutboundSpam == true && p.NotifyOutboundSpamRecipientsCount > 0 && p.BccSuspiciousOutboundMail == true && p.BccSuspiciousOutboundAdditionalRecipientsCount > 0,
                    $"notificação {YesNo(p.NotifyOutboundSpam)} ({p.NotifyOutboundSpamRecipientsCount} destinatário(s)); cópia oculta {YesNo(p.BccSuspiciousOutboundMail)} ({p.BccSuspiciousOutboundAdditionalRecipientsCount} destinatário(s))",
                    "notificação e cópia oculta ligadas, com destinatários"),
                "notificação e cópia oculta ao administrador",
                "Nenhuma política antispam de saída alcança remetentes."),
            Ref(M365Refs.M365 + "2.1.6")),

        Control("AK-MDO-007", "Política antiphishing sem as proteções de representação e de inteligência", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Configurar as políticas antiphishing com limiar de phishing elevado, proteção de representação de usuários e domínios, inteligência de caixa de correio e de falsificação, e quarentena como ação.",
            "Toda política antiphishing efetiva ligada: limiar ≥ 2; representação de usuários e dos domínios da organização; inteligência de caixa de correio e de falsificação; ações de representação em quarentena.",
            c => Effective<DefenderAntiPhishPolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p =>
                {
                    static bool Quarantine(string? a) => string.Equals(a, "Quarantine", StringComparison.OrdinalIgnoreCase);
                    var checks = new bool?[]
                    {
                        p.Enabled, p.PhishThresholdLevel is null ? null : p.PhishThresholdLevel >= 2,
                        p.EnableTargetedUserProtection, p.EnableOrganizationDomainsProtection, p.EnableMailboxIntelligence,
                        p.EnableMailboxIntelligenceProtection, p.EnableSpoofIntelligence,
                        p.TargetedUserProtectionAction is null ? null : Quarantine(p.TargetedUserProtectionAction),
                        p.TargetedDomainProtectionAction is null ? null : Quarantine(p.TargetedDomainProtectionAction),
                        p.MailboxIntelligenceProtectionAction is null ? null : Quarantine(p.MailboxIntelligenceProtectionAction),
                    };
                    bool? ok = checks.Any(v => v == false) ? false : checks.Any(v => v is null) ? null : true;
                    return KnightItemCheck.Of(ok,
                        $"ligada {YesNo(p.Enabled)}; limiar {p.PhishThresholdLevel?.ToString() ?? "?"}; usuários {YesNo(p.EnableTargetedUserProtection)} ({p.TargetedUsersToProtectCount}); "
                        + $"domínios da organização {YesNo(p.EnableOrganizationDomainsProtection)}; inteligência {YesNo(p.EnableMailboxIntelligence)}/{YesNo(p.EnableMailboxIntelligenceProtection)}; "
                        + $"falsificação {YesNo(p.EnableSpoofIntelligence)}; ações {p.TargetedUserProtectionAction ?? "?"}/{p.TargetedDomainProtectionAction ?? "?"}/{p.MailboxIntelligenceProtectionAction ?? "?"}",
                        "ligada; limiar ≥ 2; todas as proteções ligadas; ações em quarentena");
                },
                "proteções antiphishing completas",
                "Nenhuma política antiphishing alcança destinatários."),
            Ref(M365Refs.M365 + "2.1.7")),

        Control("AK-MDO-008", "Domínios de e-mail sem registro SPF que autorize o Exchange Online", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Publicar, em cada domínio de e-mail, um único registro SPF que inclua o Exchange Online (include:spf.protection.outlook.com).",
            "Cada domínio aceito próprio (fora de onmicrosoft.com) com exatamente um registro SPF que inclui o Exchange Online.",
            c => Many<DefenderDnsRecord>(c, records => Population(records,
                r => !r.SpfResolved ? KnightItemCheck.Of(null, $"consulta DNS não resolvida ({r.LookupFailure ?? "sem resposta"})", "um registro SPF com o Exchange Online")
                    : r.SpfRecords.Count == 0 ? KnightItemCheck.Of(false, "nenhum registro SPF publicado", "um registro SPF com o Exchange Online")
                    : r.SpfRecords.Count > 1 ? KnightItemCheck.Of(false, $"{r.SpfRecords.Count} registros SPF (inválido: só um é permitido)", "um registro SPF com o Exchange Online")
                    : KnightItemCheck.Of(r.SpfRecords[0].Contains("include:spf.protection.outlook.com", StringComparison.OrdinalIgnoreCase),
                        Trim(r.SpfRecords[0], 300), "um registro SPF com o Exchange Online"),
                r => DomainObject(r.Domain),
                (n, t) => $"{n} de {t} domínio(s) de e-mail sem SPF válido que autorize o Exchange Online.",
                t => $"Os {t} domínio(s) de e-mail publicam SPF que autoriza o Exchange Online.",
                () => KnightControlOutcome.NotApplicable("a organização só usa domínios gerenciados pela Microsoft (onmicrosoft.com)."),
                complete: DnsCompleteness(c, records).Complete, incompleteReason: DnsCompleteness(c, records).Reason)),
            Ref(M365Refs.M365 + "2.1.8")),

        Control("AK-MDO-009", "Domínios de e-mail sem assinatura DKIM", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Ligar a assinatura DKIM em cada domínio de e-mail próprio.",
            "Cada domínio aceito próprio com configuração DKIM ligada.",
            c => Many<DefenderDkimSigning>(c, dkim =>
            {
                var domains = CustomDomains(c).ToList();
                var byDomain = dkim.GroupBy(d => d.Domain.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
                return Population(domains,
                    d => byDomain.TryGetValue(d, out var cfg)
                        ? KnightItemCheck.Of(cfg.Enabled, $"DKIM {YesNo(cfg.Enabled)}" + (cfg.Status is null ? "" : $" (estado: {cfg.Status})"), "DKIM ligado")
                        : KnightItemCheck.Of(false, "sem configuração DKIM", "DKIM ligado"),
                    d => DomainObject(d),
                    (n, t) => $"{n} de {t} domínio(s) de e-mail sem DKIM ligado.",
                    t => $"Os {t} domínio(s) de e-mail assinam com DKIM.",
                    () => KnightControlOutcome.NotApplicable("a organização só usa domínios gerenciados pela Microsoft (onmicrosoft.com)."));
            }),
            Ref(M365Refs.M365 + "2.1.9")),

        Control("AK-MDO-010", "Domínios de e-mail sem política DMARC que rejeite ou coloque em quarentena", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Publicar DMARC em cada domínio de e-mail com política de quarentena ou rejeição aplicada a 100% das mensagens.",
            "Cada domínio aceito próprio com um registro DMARC com p=quarantine ou p=reject e pct ausente ou 100.",
            c => Many<DefenderDnsRecord>(c, records => Population(records,
                r =>
                {
                    if (!r.DmarcResolved) return KnightItemCheck.Of(null, $"consulta DNS não resolvida ({r.LookupFailure ?? "sem resposta"})", "p=quarantine ou p=reject");
                    if (r.DmarcRecords.Count == 0) return KnightItemCheck.Of(false, "nenhum registro DMARC publicado", "p=quarantine ou p=reject");
                    if (r.DmarcRecords.Count > 1) return KnightItemCheck.Of(false, $"{r.DmarcRecords.Count} registros DMARC (inválido)", "p=quarantine ou p=reject");
                    var p = r.DmarcTag("p")?.ToLowerInvariant();
                    var pct = r.DmarcTag("pct");
                    var ok = (p is "quarantine" or "reject") && (pct is null || pct == "100");
                    return KnightItemCheck.Of(ok, $"p={p ?? "?"}" + (pct is null ? "" : $"; pct={pct}"), "p=quarantine ou p=reject, pct 100");
                },
                r => DomainObject(r.Domain),
                (n, t) => $"{n} de {t} domínio(s) de e-mail sem DMARC que rejeite ou coloque em quarentena mensagens falsificadas.",
                t => $"Os {t} domínio(s) de e-mail publicam DMARC com quarentena ou rejeição.",
                () => KnightControlOutcome.NotApplicable("a organização só usa domínios gerenciados pela Microsoft (onmicrosoft.com)."),
                complete: DnsCompleteness(c, records).Complete, incompleteReason: DnsCompleteness(c, records).Reason)),
            Ref(M365Refs.M365 + "2.1.10")),

        Control("AK-MDO-011", "Filtro de anexos não bloqueia tipos de arquivo de alto risco", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Incluir no filtro de anexos das políticas antimalware os tipos executáveis, de script, de macro e de imagem de disco de alto risco.",
            $"Toda política antimalware efetiva com o filtro ligado e contendo as {HighRiskExtensions.Count} extensões de alto risco da lista do AEGIS.",
            c => Effective<DefenderMalwarePolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p =>
                {
                    var have = p.FileTypes.Select(f => f.Trim().TrimStart('.').ToLowerInvariant()).ToHashSet();
                    var missing = HighRiskExtensions.Where(e => !have.Contains(e)).ToList();
                    return KnightItemCheck.Of(p.EnableFileFilter is null ? null : p.EnableFileFilter == true && missing.Count == 0,
                        $"filtro {YesNo(p.EnableFileFilter)}; {p.FileTypes.Count} tipo(s); faltam {missing.Count}: {List(missing, 8)}",
                        "filtro ligado com todos os tipos de alto risco");
                },
                "filtro de anexos com os tipos de alto risco",
                "Nenhuma política antimalware alcança destinatários."),
            Ref(M365Refs.M365 + "2.1.11", KnightReferenceMatch.Partial,
                "O AEGIS exige a própria lista de extensões de alto risco; a lista da referência pode ter tipos a mais.")),

        Control("AK-MDO-012", "Lista de IPs permitidos no filtro de conexão", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Remover os endereços da lista de IPs permitidos do filtro de conexão; tratar exceções por regras específicas.",
            "Filtro de conexão sem IPs permitidos.",
            c => Many<DefenderConnectionFilterPolicy>(c, all => Population(all,
                p => KnightItemCheck.Of(p.IpAllowList.Count == 0, p.IpAllowList.Count == 0 ? "lista vazia" : $"{p.IpAllowList.Count} endereço(s): {List(p.IpAllowList, 4)}", "lista vazia"),
                p => PolicyObject(p.Identity, p.Name),
                (n, t) => "Mensagens vindas de IPs da lista pulam a filtragem de spam.",
                t => "O filtro de conexão não tem IPs permitidos.",
                () => KnightControlOutcome.NotEvaluated("a coleta não trouxe a política de filtro de conexão."))),
            Ref(M365Refs.M365 + "2.1.12")),

        Control("AK-MDO-013", "Lista segura do filtro de conexão ligada", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Desligar a lista segura do filtro de conexão.",
            "Filtro de conexão com a lista segura desligada.",
            c => Many<DefenderConnectionFilterPolicy>(c, all => Population(all,
                p => KnightItemCheck.Of(p.EnableSafeList is { } v ? !v : null, $"lista segura {YesNo(p.EnableSafeList)}", "desligada"),
                p => PolicyObject(p.Identity, p.Name),
                (n, t) => "A lista segura da Microsoft deixa mensagens de remetentes dela pularem a filtragem de spam.",
                t => "A lista segura do filtro de conexão está desligada.",
                () => KnightControlOutcome.NotEvaluated("a coleta não trouxe a política de filtro de conexão."))),
            Ref(M365Refs.M365 + "2.1.13")),

        Control("AK-MDO-014", "Políticas antispam de entrada com domínios permitidos", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Remover os domínios permitidos das políticas antispam de entrada.",
            "Nenhuma política antispam de entrada efetiva com domínios de remetente permitidos.",
            c => Effective<DefenderInboundSpamPolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p => KnightItemCheck.Of(p.AllowedSenderDomains.Count == 0,
                    p.AllowedSenderDomains.Count == 0 ? "nenhum domínio permitido" : $"{p.AllowedSenderDomains.Count} domínio(s) permitido(s): {List(p.AllowedSenderDomains, 4)}",
                    "nenhum domínio permitido"),
                "sem domínios de remetente permitidos",
                "Nenhuma política antispam de entrada alcança destinatários."),
            Ref(M365Refs.M365 + "2.1.14")),

        Control("AK-MDO-015", "Limites de envio das políticas antispam de saída não definidos", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Definir limites explícitos de destinatários por hora (externos e internos) e por dia e bloquear o remetente ao atingi-los.",
            "Toda política antispam de saída efetiva com os três limites definidos (maiores que zero) e ação de bloqueio do usuário.",
            c => Effective<DefenderOutboundSpamPolicy>(c, p => p.Reach.Effective, p => p.Identity, p => p.Name, p => p.Reach,
                p => KnightItemCheck.Of(p.RecipientLimitExternalPerHour is null || p.RecipientLimitInternalPerHour is null || p.RecipientLimitPerDay is null ? null
                        : p.RecipientLimitExternalPerHour > 0 && p.RecipientLimitInternalPerHour > 0 && p.RecipientLimitPerDay > 0
                          && string.Equals(p.ActionWhenThresholdReached, "BlockUser", StringComparison.OrdinalIgnoreCase),
                    $"externos/h {p.RecipientLimitExternalPerHour?.ToString() ?? "?"}; internos/h {p.RecipientLimitInternalPerHour?.ToString() ?? "?"}; "
                    + $"por dia {p.RecipientLimitPerDay?.ToString() ?? "?"} (0 = padrão do serviço); ação {p.ActionWhenThresholdReached ?? "?"}",
                    "três limites definidos e ação BlockUser"),
                "limites de envio definidos com bloqueio",
                "Nenhuma política antispam de saída alcança remetentes."),
            Ref(M365Refs.M365 + "2.1.15")),

        Control("AK-MDO-016", "Proteção de contas prioritárias desligada ou sem contas marcadas", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Low,
            "Ligar a proteção de contas prioritárias e marcar como prioritárias as contas de executivos e de administração.",
            "Proteção de contas prioritárias ligada, com ao menos uma conta marcada.",
            c => Single<DefenderPriorityAccounts>(c, p => Setting(
                p.EnablePriorityAccountProtection is null ? null : p.EnablePriorityAccountProtection == true && p.Accounts.Count > 0,
                DefenderPriorityAccounts.ExternalId, "Proteção de contas prioritárias",
                $"proteção {YesNo(p.EnablePriorityAccountProtection)}; {p.Accounts.Count} conta(s) marcada(s)", "ligada, com contas marcadas",
                "As contas mais visadas não recebem a proteção diferenciada nem aparecem destacadas nos relatórios e alertas.",
                $"A proteção de contas prioritárias está ligada para {p.Accounts.Count} conta(s).")),
            Ref(M365Refs.M365 + "2.4.1")),

        Control("AK-MDO-017", "Contas prioritárias fora da política de segurança predefinida estrita", KnightIndicatorCategory.PrivilegedAccess, SeverityLevel.Low,
            "Incluir as contas prioritárias nas regras da política de segurança predefinida estrita (proteção de e-mail e do Defender para Office 365).",
            "Cada conta prioritária incluída, por pessoa, nas duas regras estritas habilitadas.",
            c => Single<DefenderPriorityAccounts>(c, vip =>
            {
                if (vip.Accounts.Count == 0)
                    return KnightControlOutcome.NotApplicable("nenhuma conta está marcada como prioritária (ver AK-MDO-016).");
                var rules = c.Configuration.Read<DefenderPresetRule>();
                if (!rules.Collected) return KnightControlOutcome.NotEvaluated(Trim(rules.MissingReason ?? "as regras predefinidas não foram coletadas."));
                var strict = rules.Items.Where(r => r.IsStrict && r.Enabled).ToList();
                var eop = strict.Where(r => r.Kind == DefenderPresetRule.KindEop).ToList();
                var atp = strict.Where(r => r.Kind == DefenderPresetRule.KindAtp).ToList();
                var byGroup = strict.Any(r => r.SentToMemberOf.Count > 0);
                static bool Covered(IEnumerable<DefenderPresetRule> rs, DefenderPriorityAccount a) =>
                    rs.Any(r => (a.UserPrincipalName is { } upn && r.SentTo.Any(s => s.Equals(upn, StringComparison.OrdinalIgnoreCase)))
                        || (a.UserPrincipalName is { } u && u.Contains('@') && r.RecipientDomainIs.Any(d => u.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase))));
                return Population(vip.Accounts,
                    a =>
                    {
                        var both = Covered(eop, a) && Covered(atp, a);
                        return KnightItemCheck.Of(both ? true : byGroup ? null : false,
                            both ? "incluída nas duas regras estritas" : byGroup ? "não incluída por pessoa; as regras incluem grupos cuja associação não é lida" : "fora das regras estritas",
                            "incluída nas regras estritas de proteção de e-mail e do Defender");
                    },
                    a => KnightObjects.Evidence(KnightAffectedObjectKind.User, a.ExternalDirectoryObjectId ?? a.UserPrincipalName ?? "conta", a.DisplayName ?? a.UserPrincipalName, "")
                        with { UserPrincipalName = a.UserPrincipalName },
                    (n, t) => $"{n} de {t} conta(s) prioritária(s) fora da política de segurança predefinida estrita.",
                    t => $"As {t} conta(s) prioritária(s) estão na política de segurança predefinida estrita.",
                    () => KnightControlOutcome.NotApplicable("nenhuma conta prioritária."),
                    complete: vip.ListComplete, incompleteReason: "A lista de contas prioritárias atingiu o teto desta coleta.");
            }),
            Ref(M365Refs.M365 + "2.4.2")),

        Control("AK-MDO-018", "Limpeza automática em zero hora (ZAP) desligada para o Teams", KnightIndicatorCategory.ThreatProtection, SeverityLevel.High,
            "Ligar a limpeza automática em zero hora para mensagens do Teams.",
            "Política de proteção do Teams com ZAP ligado.",
            c => Many<DefenderTeamsProtection>(c, all => Population(all,
                p => KnightItemCheck.Of(p.ZapEnabled, $"ZAP {YesNo(p.ZapEnabled)}", "ligado"),
                p => PolicyObject(p.Identity, "Proteção do Teams"),
                (n, t) => "Mensagens do Teams identificadas como maliciosas depois da entrega permanecem visíveis.",
                t => "A limpeza automática em zero hora está ligada para o Teams.",
                () => KnightControlOutcome.NotEvaluated("a coleta não trouxe a política de proteção do Teams (pode exigir licença do Defender para Office 365)."))),
            Ref(M365Refs.M365 + "2.4.4")),
    };
}

// ---- Microsoft Purview -----------------------------------------------------------------------------------

public static class PurviewControls
{
    private static KnightIndicatorDefinition Control(string id, string title, KnightIndicatorCategory cat, SeverityLevel sev,
        string recommendation, string criterion, Func<KnightEvaluationContext, KnightControlOutcome> evaluate, params KnightReferenceLink[] refs) =>
        KnightRuleKit.Control(KnightSourceType.MicrosoftPurview, KnightService.Purview, id, title, cat, sev, recommendation, criterion, evaluate, refs);

    private static KnightControlOutcome AnyPolicy<T>(KnightEvaluationContext c, Func<T, bool> ok, Func<T, string> id, Func<T, string?> name,
        Func<T, string> found, string exposed, Func<int, string> passed) where T : class =>
        Many<T>(c, all =>
        {
            var objects = all.Select(p => PolicyObject(id(p), name(p), $"Encontrado: {found(p)}.")).Take(50).ToList();
            var matching = all.Where(ok).ToList();
            return matching.Count > 0
                ? KnightControlOutcome.Passed(passed(matching.Count), objects)
                : KnightControlOutcome.Exposed(all.Count == 0 ? exposed + " Nenhuma política encontrada." : exposed + $" {all.Count} política(s) encontrada(s), nenhuma atende.",
                    Array.Empty<KnightIndicatorObject>(), objects);
        });

    private static string Dlp(PurviewDlpPolicy p) =>
        $"modo {p.Mode ?? "?"}; Teams {(p.CoversAllTeams ? "todos" : p.TeamsLocation.Count == 0 ? "não" : "parcial")}; Copilot {(p.CoversCopilot ? "sim" : "não")}";

    public static IReadOnlyList<KnightIndicatorDefinition> Definitions { get; } = new[]
    {
        Control("AK-PUR-001", "Log de auditoria unificado desligado", KnightIndicatorCategory.DataProtection, SeverityLevel.Medium,
            "Ligar a ingestão do log de auditoria unificado do Microsoft 365.",
            "Configuração de auditoria: ingestão do log unificado ligada.",
            c => Single<PurviewAuditConfig>(c, a => Setting(a.UnifiedAuditLogIngestionEnabled, PurviewAuditConfig.ExternalId, "Log de auditoria unificado",
                a.UnifiedAuditLogIngestionEnabled switch { true => "ligado", false => "desligado", _ => "não informado" }, "ligado",
                "Atividades de usuários e administradores no Microsoft 365 não são registradas para pesquisa.",
                "O log de auditoria unificado está ligado.")),
            Ref(M365Refs.M365 + "3.1.1")),
        Control("AK-PUR-002", "Nenhuma política de DLP em vigor", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Criar e colocar em vigor (fora do modo de teste) políticas de DLP para os tipos de informação sensível da organização.",
            "Ao menos uma política de DLP em modo de aplicação.",
            c => AnyPolicy<PurviewDlpPolicy>(c, p => p.Enforced, p => p.Identity, p => p.Name, Dlp,
                "Nenhuma política de DLP está em modo de aplicação.", n => $"{n} política(s) de DLP em modo de aplicação."),
            Ref(M365Refs.M365 + "3.2.1")),
        Control("AK-PUR-003", "Nenhuma política de DLP em vigor para o Teams", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Aplicar uma política de DLP a todas as conversas e canais do Teams.",
            "Ao menos uma política de DLP em modo de aplicação alcançando todo o Teams.",
            c => AnyPolicy<PurviewDlpPolicy>(c, p => p.Enforced && p.CoversAllTeams, p => p.Identity, p => p.Name, Dlp,
                "Nenhuma política de DLP em vigor alcança todo o Teams.", n => $"{n} política(s) de DLP em vigor alcançam todo o Teams."),
            Ref(M365Refs.M365 + "3.2.2")),
        Control("AK-PUR-004", "Nenhuma política de DLP em vigor para o Microsoft 365 Copilot", KnightIndicatorCategory.DataProtection, SeverityLevel.Medium,
            "Aplicar uma política de DLP ao local Microsoft 365 Copilot e Copilot Chat.",
            "Ao menos uma política de DLP em modo de aplicação no local do Copilot.",
            c => AnyPolicy<PurviewDlpPolicy>(c, p => p.Enforced && p.CoversCopilot, p => p.Identity, p => p.Name, Dlp,
                "Nenhuma política de DLP em vigor alcança o Microsoft 365 Copilot.", n => $"{n} política(s) de DLP em vigor alcançam o Copilot."),
            Ref(M365Refs.M365 + "3.2.3")),
        Control("AK-PUR-005", "Nenhuma política de rótulos de confidencialidade publicada", KnightIndicatorCategory.DataProtection, SeverityLevel.High,
            "Publicar rótulos de confidencialidade para os usuários por uma política habilitada.",
            "Ao menos uma política de rótulos habilitada, com rótulos e locais de publicação.",
            c => AnyPolicy<PurviewLabelPolicy>(c, p => p.Published, p => p.Identity, p => p.Name,
                p => $"habilitada {YesNo(p.Enabled)}; {p.Labels.Count} rótulo(s); {p.LocationCount} local(is)",
                "Nenhuma política de rótulos está publicada.", n => $"{n} política(s) de rótulos publicada(s)."),
            Ref(M365Refs.M365 + "3.3.1")),
    };
}
