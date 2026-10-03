using System;
using System.Collections.Generic;
using System.Globalization;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-CLOSURE-01] Contratos das leituras em versão beta do Microsoft Graph e complementos
// ============================================================================
// Mesmo princípio dos demais contratos: o coletor normaliza o que a fonte devolveu, sem interpretar. Um campo que a fonte
// não devolveu fica nulo (desconhecido). Endereços de e-mail e listas de pessoas não são guardados onde a regra só precisa
// saber se existem — o contrato guarda a contagem.

/// <summary>Um usuário cuja MFA por usuário (legado) NÃO está desabilitada.</summary>
public sealed record EntraPerUserMfaUser(string Id, string? UserPrincipalName, string? DisplayName, string State);

/// <summary>
/// Estado da MFA por usuário (legado) das contas HABILITADAS, lido usuário a usuário na versão beta do Microsoft Graph
/// (<c>/users/{id}/authentication/requirements</c>). <see cref="Complete"/> só é verdadeiro quando TODAS as contas
/// habilitadas foram lidas; um usuário com leitura recusada fica em <see cref="Unread"/>.
/// </summary>
public sealed record EntraPerUserMfaInventory(
    int EnabledAccounts,
    int Read,
    int Disabled,
    int Enabled,
    int Enforced,
    int Unread,
    bool Truncated,
    IReadOnlyList<EntraPerUserMfaUser> NotDisabled,
    string? Limitation)
{
    public const string SchemaVersion = "aegis-config-entra-per-user-mfa-v1";
    public const string ExternalId = "per-user-mfa";

    /// <summary>Teto de contas lidas numa coleta (uma chamada por conta).</summary>
    public const int MaxAccounts = 5000;

    /// <summary>Teto de contas listadas como afetadas.</summary>
    public const int MaxListed = 500;

    public bool Complete => !Truncated && Unread == 0 && Read == EnabledAccounts;
}

/// <summary>Um usuário MEMBRO sem capacidade de MFA no relatório de registro.</summary>
public sealed record EntraMemberWithoutMfa(string Id, string? UserPrincipalName, string? DisplayName);

/// <summary>
/// Capacidade de MFA dos usuários MEMBROS (userType = member), lida no MESMO relatório de registro que a coleta de
/// identidade já usa (<c>reports/authenticationMethods/userRegistrationDetails</c>, v1.0, AuditLog.Read.All) — nenhuma
/// leitura nova. <see cref="UnknownType"/> conta registros sem o tipo do usuário: eles impedem dizer "todos os membros".
/// </summary>
public sealed record EntraMfaCapabilityInventory(
    int Total,
    int Members,
    int MembersCapable,
    int UnknownType,
    IReadOnlyList<EntraMemberWithoutMfa> MembersWithoutMfa)
{
    public const string SchemaVersion = "aegis-config-entra-mfa-capability-v1";
    public const string ExternalId = "mfa-capability";

    /// <summary>Teto de membros listados como afetados.</summary>
    public const int MaxListed = 500;
}

/// <summary>Uma configuração do tipo estado + alvo da política de métodos de autenticação (versão beta).</summary>
public sealed record EntraPreviewFeature(string? State, IReadOnlyList<string> IncludeTargets, IReadOnlyList<string> ExcludeTargets);

/// <summary>
/// Campos da política de métodos de autenticação que só a versão beta do Microsoft Graph expõe: a MFA preferencial do
/// sistema (<c>systemCredentialPreferences</c>) e o Authenticator em aplicativos complementares
/// (<c>companionAppAllowedState</c> do método Microsoft Authenticator).
/// </summary>
public sealed record EntraAuthenticationMethodsPreview(
    EntraPreviewFeature? SystemPreferred,
    bool AuthenticatorPresent,
    string? AuthenticatorState,
    EntraPreviewFeature? CompanionApp)
{
    public const string SchemaVersion = "aegis-config-entra-auth-methods-preview-v1";
    public const string ExternalId = "authentication-methods-preview";
}

/// <summary>Aplicativos e serviços próprios dos usuários (<c>/admin/appsAndServices</c>, versão beta).</summary>
public sealed record M365AppsAndServicesSettings(bool? OfficeStoreEnabled, bool? AppAndServicesTrialEnabled)
{
    public const string SchemaVersion = "aegis-config-m365-apps-services-v1";
    public const string ExternalId = "apps-and-services";
}

/// <summary>Configurações do Microsoft Forms (<c>/admin/forms</c>, versão beta) usadas pelas regras e como contexto.</summary>
public sealed record M365FormsSettings(bool? InOrgFormsPhishingScanEnabled, bool? ExternalSendFormEnabled, bool? ExternalShareCollaborationEnabled)
{
    public const string SchemaVersion = "aegis-config-m365-forms-v1";
    public const string ExternalId = "forms";
}

/// <summary>Um tempo limite por aplicação de uma política de inatividade (<c>WebSessionIdleTimeout</c>, "hh:mm:ss").</summary>
public sealed record EntraActivityTimeoutApplication(string? ApplicationId, string? WebSessionIdleTimeout)
{
    /// <summary>O tempo limite como duração; texto ilegível = nulo (desconhecido).</summary>
    public TimeSpan? Timeout =>
        TimeSpan.TryParse(WebSessionIdleTimeout, CultureInfo.InvariantCulture, out var t) ? t : null;
}

/// <summary>
/// Uma política de tempo limite por inatividade do locatário (<c>/policies/activityBasedTimeoutPolicies</c>, versão estável).
/// É onde o centro de administração do Microsoft 365 grava o "tempo limite de sessão ociosa" (aplicação "default").
/// <see cref="Limitation"/> preenchida = a definição não pôde ser interpretada.
/// </summary>
public sealed record EntraActivityTimeoutPolicy(
    string Id,
    string? DisplayName,
    bool? IsOrganizationDefault,
    IReadOnlyList<EntraActivityTimeoutApplication> Applications,
    string? Limitation)
{
    public const string SchemaVersion = "aegis-config-entra-activity-timeout-v1";
}

/// <summary>
/// Política de envio das mensagens denunciadas (<c>Get-ReportSubmissionPolicy</c>): para onde vão as denúncias feitas no
/// Outlook e no Teams. Os endereços não são guardados — só quantos existem.
/// </summary>
public sealed record DefenderReportSubmissionPolicy(
    string Identity,
    bool? ReportJunkToCustomizedAddress,
    bool? ReportNotJunkToCustomizedAddress,
    bool? ReportPhishToCustomizedAddress,
    int ReportJunkAddresses,
    int ReportNotJunkAddresses,
    int ReportPhishAddresses,
    bool? ReportChatMessageEnabled,
    bool? ReportChatMessageToCustomizedAddressEnabled,
    int ReportChatMessageAddresses)
{
    public const string SchemaVersion = "aegis-config-defender-report-submission-v1";
}
