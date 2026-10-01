using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-04] Configuração dos demais serviços do Microsoft 365
// ============================================================================
// Mesmo princípio dos contratos do Entra ID, do Teams e do Exchange: cada contrato é a NORMALIZAÇÃO tipada do que a
// fonte devolveu, com os nomes da fonte, valores ausentes nulos (nulo nunca vira "desabilitado" nem "conforme") e a
// versão do contrato no documento. Enumerações devolvidas como número pela fonte (API de administração do SharePoint)
// são traduzidas para o nome documentado; um número desconhecido é preservado como texto, nunca adivinhado.

// ---- Microsoft Intune ------------------------------------------------------------------------------------

/// <summary>Configurações do serviço de conformidade (<c>deviceManagement.settings</c>).</summary>
/// <param name="SecureByDefault">Verdadeiro = dispositivo SEM política de conformidade atribuída é marcado como não conforme.</param>
public sealed record IntuneComplianceSettings(bool? SecureByDefault, int? DeviceComplianceCheckinThresholdDays)
{
    public const string SchemaVersion = "aegis-config-intune-compliance-settings-v1";
    public const string ExternalId = "deviceManagementSettings";
}

/// <summary>Restrição de UMA plataforma numa configuração de restrição de registro.</summary>
public sealed record IntunePlatformRestriction(string Platform, bool? PlatformBlocked, bool? PersonalDeviceEnrollmentBlocked);

/// <summary>Configuração de restrição de registro de dispositivos (plataformas e dispositivos pessoais).</summary>
public sealed record IntuneEnrollmentRestriction(
    string Id,
    string? DisplayName,
    int? Priority,
    bool IsDefault,
    IReadOnlyList<IntunePlatformRestriction> Platforms,
    int AssignmentCount)
{
    public const string SchemaVersion = "aegis-config-intune-enrollment-restriction-v1";
}

// ---- SharePoint e OneDrive ------------------------------------------------------------------------------

/// <summary>Configurações do locatário lidas pelo Microsoft Graph (<c>GET /admin/sharepoint/settings</c>).</summary>
public sealed record SharePointTenantSettings(
    string? SharingCapability,
    string? SharingDomainRestrictionMode,
    IReadOnlyList<string> SharingAllowedDomainList,
    IReadOnlyList<string> SharingBlockedDomainList,
    bool? IsResharingByExternalUsersEnabled,
    bool? IsLegacyAuthProtocolsEnabled,
    bool? IsUnmanagedSyncAppForTenantRestricted,
    int AllowedDomainGuidsForSyncAppCount,
    bool? IsRequireAcceptingUserToMatchInvitedUserEnabled)
{
    public const string SchemaVersion = "aegis-config-spo-tenant-settings-v1";
    public const string ExternalId = "sharepointSettings";

    /// <summary>Valor da fonte que libera links sem autenticação ("Qualquer pessoa").</summary>
    public const string AnyoneCapability = "externalUserAndGuestSharing";
    public const string DisabledCapability = "disabled";
}

/// <summary>
/// Configurações do locatário lidas pela API de administração do SharePoint (objeto <c>Tenant</c>). É a única leitura
/// oficial destas propriedades; exige a aplicação com <c>Sites.FullControl.All</c> no SharePoint e certificado.
/// </summary>
public sealed record SharePointAdminTenant(
    string? SharingCapability,
    string? OneDriveSharingCapability,
    bool? EnableAzureADB2BIntegration,
    string? DefaultSharingLinkType,
    string? DefaultLinkPermission,
    bool? ExternalUserExpirationRequired,
    int? ExternalUserExpireInDays,
    bool? EmailAttestationRequired,
    int? EmailAttestationReAuthDays,
    bool? DisallowInfectedFileDownload,
    IReadOnlyList<string> GuestSharingGroupAllowList,
    bool? LegacyAuthProtocolsEnabled)
{
    public const string SchemaVersion = "aegis-config-spo-admin-tenant-v1";
    public const string ExternalId = "spoTenant";

    /// <summary>Tradução documentada do enumerado <c>SharingCapabilities</c> (mesmos nomes do Microsoft Graph).</summary>
    public static string? SharingCapabilityName(int? value) => value switch
    {
        null => null,
        0 => "disabled",
        1 => "externalUserSharingOnly",
        2 => "externalUserAndGuestSharing",
        3 => "existingExternalUserSharingOnly",
        _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Tradução documentada do enumerado <c>SharingLinkType</c>.</summary>
    public static string? SharingLinkTypeName(int? value) => value switch
    {
        null => null,
        0 => "none",
        1 => "direct",
        2 => "internal",
        3 => "anonymousAccess",
        _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Tradução documentada do enumerado <c>SharingPermissionType</c>.</summary>
    public static string? SharingPermissionName(int? value) => value switch
    {
        null => null,
        0 => "none",
        1 => "view",
        2 => "edit",
        _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

// ---- Microsoft Fabric ------------------------------------------------------------------------------------

/// <summary>Uma configuração do locatário do Fabric, como a API de administração a devolve.</summary>
public sealed record FabricTenantSetting(
    string SettingName,
    string? Title,
    bool? Enabled,
    bool? CanSpecifySecurityGroups,
    IReadOnlyList<string> EnabledSecurityGroups,
    IReadOnlyList<string> ExcludedSecurityGroups,
    string? TenantSettingGroup)
{
    public const string SchemaVersion = "aegis-config-fabric-tenant-setting-v1";

    /// <summary>Ligada para a organização INTEIRA (ligada e sem grupos que a restrinjam).</summary>
    public bool EnabledForWholeOrganization => Enabled == true && EnabledSecurityGroups.Count == 0;

    /// <summary>Ligada apenas para grupos de segurança nomeados.</summary>
    public bool RestrictedToGroups => Enabled == true && EnabledSecurityGroups.Count > 0;
}

// ---- Microsoft Defender para Office 365 -------------------------------------------------------------------

/// <summary>
/// Alcance de uma política de proteção: a política PADRÃO vale para todos os destinatários sem política própria; as
/// demais valem pelos destinatários da REGRA (pessoas, grupos, domínios), e só enquanto a regra estiver habilitada.
/// </summary>
public sealed record DefenderPolicyReach(
    bool IsDefault,
    bool HasRule,
    string? RuleState,
    int? Priority,
    IReadOnlyList<string> SentTo,
    IReadOnlyList<string> SentToMemberOf,
    IReadOnlyList<string> RecipientDomainIs)
{
    /// <summary>A política age sobre alguém: é a padrão, ou tem regra habilitada.</summary>
    public bool Effective => IsDefault || (HasRule && string.Equals(RuleState, "Enabled", StringComparison.OrdinalIgnoreCase));

    public static DefenderPolicyReach Default { get; } = new(true, false, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>Configuração global: Anexos Seguros para SharePoint/OneDrive/Teams e Documentos Seguros (<c>Get-AtpPolicyForO365</c>).</summary>
public sealed record DefenderAtpPolicy(bool? EnableATPForSPOTeamsODB, bool? EnableSafeDocs, bool? AllowSafeDocsOpen)
{
    public const string SchemaVersion = "aegis-config-mdo-atp-policy-v1";
    public const string ExternalId = "atpPolicyForO365";
}

/// <summary>Política de Links Seguros.</summary>
public sealed record DefenderSafeLinksPolicy(
    string Identity,
    string? Name,
    bool? EnableSafeLinksForEmail,
    bool? EnableSafeLinksForTeams,
    bool? EnableSafeLinksForOffice,
    bool? TrackClicks,
    bool? AllowClickThrough,
    bool? ScanUrls,
    bool? EnableForInternalSenders,
    bool? DeliverMessageAfterScan,
    bool? DisableUrlRewrite,
    bool IsBuiltInProtection,
    DefenderPolicyReach Reach)
{
    public const string SchemaVersion = "aegis-config-mdo-safe-links-v1";
}

/// <summary>Política de Anexos Seguros.</summary>
public sealed record DefenderSafeAttachmentPolicy(
    string Identity,
    string? Name,
    bool? Enable,
    string? Action,
    bool IsBuiltInProtection,
    DefenderPolicyReach Reach)
{
    public const string SchemaVersion = "aegis-config-mdo-safe-attachments-v1";
}

/// <summary>Política antimalware.</summary>
public sealed record DefenderMalwarePolicy(
    string Identity,
    string? Name,
    bool? EnableFileFilter,
    IReadOnlyList<string> FileTypes,
    string? FileTypeAction,
    bool? EnableInternalSenderAdminNotifications,
    bool InternalSenderAdminAddressSet,
    bool? ZapEnabled,
    DefenderPolicyReach Reach)
{
    public const string SchemaVersion = "aegis-config-mdo-malware-v1";
}

/// <summary>Política antispam de entrada.</summary>
public sealed record DefenderInboundSpamPolicy(
    string Identity,
    string? Name,
    IReadOnlyList<string> AllowedSenderDomains,
    int AllowedSendersCount,
    DefenderPolicyReach Reach)
{
    public const string SchemaVersion = "aegis-config-mdo-inbound-spam-v1";
}

/// <summary>Política antispam de saída.</summary>
public sealed record DefenderOutboundSpamPolicy(
    string Identity,
    string? Name,
    int? RecipientLimitExternalPerHour,
    int? RecipientLimitInternalPerHour,
    int? RecipientLimitPerDay,
    string? ActionWhenThresholdReached,
    bool? NotifyOutboundSpam,
    int NotifyOutboundSpamRecipientsCount,
    bool? BccSuspiciousOutboundMail,
    int BccSuspiciousOutboundAdditionalRecipientsCount,
    DefenderPolicyReach Reach)
{
    public const string SchemaVersion = "aegis-config-mdo-outbound-spam-v1";
}

/// <summary>Política de filtro de conexão.</summary>
public sealed record DefenderConnectionFilterPolicy(
    string Identity,
    string? Name,
    IReadOnlyList<string> IpAllowList,
    bool? EnableSafeList)
{
    public const string SchemaVersion = "aegis-config-mdo-connection-filter-v1";
}

/// <summary>Política antiphishing.</summary>
public sealed record DefenderAntiPhishPolicy(
    string Identity,
    string? Name,
    bool? Enabled,
    int? PhishThresholdLevel,
    bool? EnableTargetedUserProtection,
    int TargetedUsersToProtectCount,
    bool? EnableOrganizationDomainsProtection,
    bool? EnableMailboxIntelligence,
    bool? EnableMailboxIntelligenceProtection,
    bool? EnableSpoofIntelligence,
    string? TargetedUserProtectionAction,
    string? TargetedDomainProtectionAction,
    string? MailboxIntelligenceProtectionAction,
    DefenderPolicyReach Reach)
{
    public const string SchemaVersion = "aegis-config-mdo-anti-phish-v1";
}

/// <summary>Assinatura DKIM de um domínio.</summary>
public sealed record DefenderDkimSigning(string Domain, bool? Enabled, string? Status)
{
    public const string SchemaVersion = "aegis-config-mdo-dkim-v1";
}

/// <summary>Domínio aceito da organização.</summary>
public sealed record DefenderAcceptedDomain(string DomainName, string? DomainType, bool? IsDefault)
{
    public const string SchemaVersion = "aegis-config-mdo-accepted-domain-v1";

    /// <summary>Domínio gerenciado pela Microsoft (<c>*.onmicrosoft.com</c>): o cliente não publica o DNS dele.</summary>
    public bool IsMicrosoftManaged =>
        DomainName.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase)
        || DomainName.EndsWith(".mail.onmicrosoft.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Registros DNS públicos de um domínio aceito: os registros TXT de SPF do domínio e o de DMARC em <c>_dmarc</c>. A
/// consulta é feita pelo AEGIS a um resolvedor DNS; "não resolvido" é diferente de "sem registro".
/// </summary>
public sealed record DefenderDnsRecord(
    string Domain,
    bool SpfResolved,
    IReadOnlyList<string> SpfRecords,
    bool DmarcResolved,
    IReadOnlyList<string> DmarcRecords,
    string? LookupFailure)
{
    public const string SchemaVersion = "aegis-config-mdo-dns-v1";

    /// <summary>Valor de uma tag do DMARC (ex.: <c>p</c>, <c>pct</c>) no único registro publicado.</summary>
    public string? DmarcTag(string tag)
    {
        if (DmarcRecords.Count != 1) return null;
        foreach (var part in DmarcRecords[0].Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return null;
    }
}

/// <summary>Política de proteção do Teams (ZAP para mensagens do Teams).</summary>
public sealed record DefenderTeamsProtection(string Identity, bool? ZapEnabled)
{
    public const string SchemaVersion = "aegis-config-mdo-teams-protection-v1";
}

/// <summary>Proteção de contas prioritárias e contas marcadas como prioritárias.</summary>
public sealed record DefenderPriorityAccounts(
    bool? EnablePriorityAccountProtection,
    IReadOnlyList<DefenderPriorityAccount> Accounts,
    bool ListComplete)
{
    public const string SchemaVersion = "aegis-config-mdo-priority-accounts-v1";
    public const string ExternalId = "priorityAccounts";
}

/// <summary>Uma conta marcada como prioritária.</summary>
public sealed record DefenderPriorityAccount(string? ExternalDirectoryObjectId, string? UserPrincipalName, string? DisplayName);

/// <summary>Uma regra das políticas de segurança predefinidas (proteção de e-mail EOP ou do Defender para Office 365).</summary>
public sealed record DefenderPresetRule(
    string Kind,
    string Identity,
    string? Name,
    string? State,
    int? Priority,
    IReadOnlyList<string> SentTo,
    IReadOnlyList<string> SentToMemberOf,
    IReadOnlyList<string> RecipientDomainIs)
{
    public const string SchemaVersion = "aegis-config-mdo-preset-rule-v1";

    public const string KindEop = "EOP";
    public const string KindAtp = "ATP";

    public bool IsStrict => (Identity + " " + Name).Contains("Strict", StringComparison.OrdinalIgnoreCase);

    public bool Enabled => string.Equals(State, "Enabled", StringComparison.OrdinalIgnoreCase);
}

// ---- Microsoft Purview -----------------------------------------------------------------------------------

/// <summary>Configuração do log de auditoria unificado.</summary>
public sealed record PurviewAuditConfig(bool? UnifiedAuditLogIngestionEnabled)
{
    public const string SchemaVersion = "aegis-config-purview-audit-v1";
    public const string ExternalId = "adminAuditLogConfig";
}

/// <summary>Política de DLP e os locais que ela alcança.</summary>
public sealed record PurviewDlpPolicy(
    string Identity,
    string? Name,
    string? Mode,
    bool? Enabled,
    IReadOnlyList<string> Workloads,
    IReadOnlyList<string> ExchangeLocation,
    IReadOnlyList<string> SharePointLocation,
    IReadOnlyList<string> OneDriveLocation,
    IReadOnlyList<string> TeamsLocation,
    IReadOnlyList<string> EnforcementPlanes,
    IReadOnlyList<string> ApplicationLocations)
{
    public const string SchemaVersion = "aegis-config-purview-dlp-v1";

    /// <summary>Identificador documentado do local "Microsoft 365 Copilot e Copilot Chat" nas políticas de DLP.</summary>
    public const string CopilotLocationId = "470f2276-e011-4e9d-a6ec-20768be3a4b0";

    /// <summary>Em vigor: modo de aplicação (não teste) e não desabilitada.</summary>
    public bool Enforced => string.Equals(Mode, "Enable", StringComparison.OrdinalIgnoreCase) && Enabled != false;

    public bool CoversAllTeams => TeamsLocation.Any(l => l.Equals("All", StringComparison.OrdinalIgnoreCase));

    public bool CoversCopilot =>
        EnforcementPlanes.Any(p => p.Equals("CopilotExperiences", StringComparison.OrdinalIgnoreCase))
        || ApplicationLocations.Any(l => l.Contains(CopilotLocationId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Política de publicação de rótulos de confidencialidade.</summary>
public sealed record PurviewLabelPolicy(
    string Identity,
    string? Name,
    bool? Enabled,
    string? Mode,
    IReadOnlyList<string> Labels,
    int LocationCount)
{
    public const string SchemaVersion = "aegis-config-purview-label-policy-v1";

    /// <summary>Publicada: habilitada, com ao menos um rótulo e ao menos um local de publicação.</summary>
    public bool Published => Enabled != false && Labels.Count > 0 && LocationCount > 0
        && !string.Equals(Mode, "PendingDeletion", StringComparison.OrdinalIgnoreCase);
}
