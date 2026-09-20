using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-COVERAGE-02] Configuração do Microsoft Teams observada numa coleta
// ============================================================================
// Mesmo desenho do bloco do Entra ID: cada contrato é a NORMALIZAÇÃO tipada do que a fonte devolveu, SEM
// interpretação. Os nomes seguem os da fonte (as propriedades documentadas dos comandos oficiais de leitura do
// módulo Teams PowerShell), valores ausentes permanecem nulos — nulo NUNCA vira "desabilitado" nem "conforme" —
// e cada documento carrega o nome e a versão do contrato. A leitura só interpreta as versões que conhece.
//
// A diferença estrutural em relação ao Entra ID está nas POLÍTICAS. No Teams, quase todo critério vive numa
// política que existe em várias instâncias: a PADRÃO DA ORGANIZAÇÃO (identidade "Global") e as PERSONALIZADAS
// (identidade "Tag:<nome>"). Ler só a global responderia a pergunta errada — por isso a coleta preserva TODAS as
// instâncias e, à parte, as ATRIBUIÇÕES A GRUPOS, que são o que permite dizer o alcance de uma personalizada.
// O que a coleta NÃO faz é enumerar usuário a usuário: a atribuição DIRETA por usuário fica declarada como
// limitação (ver TeamsPolicyReach) — jamais preenchida por suposição.

/// <summary>
/// Configuração do cliente do Teams (instância única do locatário, identidade "Global"). Cobre o armazenamento
/// de terceiros oferecido no cliente e o endereço de e-mail dos canais.
/// </summary>
public sealed record TeamsClientConfiguration(
    bool? AllowEmailIntoChannel,
    bool? AllowDropBox,
    bool? AllowBox,
    bool? AllowGoogleDrive,
    bool? AllowShareFile,
    bool? AllowEgnyte,
    bool? AllowGuestUser)
{
    public const string SchemaVersion = "aegis-config-teams-client-v1";
    public const string ExternalId = "teamsClientConfiguration";

    /// <summary>Provedores de armazenamento de TERCEIROS oferecidos no cliente, com o estado observado.</summary>
    public IReadOnlyList<(string Label, bool? Allowed)> ThirdPartyStorage => new[]
    {
        ("Dropbox", AllowDropBox),
        ("Box", AllowBox),
        ("Google Drive", AllowGoogleDrive),
        ("Citrix ShareFile", AllowShareFile),
        ("Egnyte", AllowEgnyte),
    };
}

/// <summary>
/// Configuração de federação do locatário (instância única, identidade "Global"). É a configuração que governa o
/// acesso externo: quais organizações, se contas não gerenciadas do Teams podem conversar e em que sentido.
/// </summary>
/// <param name="AllowedDomainsKind">
/// Como a lista de domínios permitidos foi expressa pela fonte: <see cref="AllowAllKnownDomains"/> (qualquer
/// organização que não esteja bloqueada) ou <see cref="AllowList"/> (somente os domínios listados). Valor não
/// reconhecido é preservado como veio — nunca traduzido para um dos dois.
/// </param>
public sealed record TeamsFederationConfiguration(
    bool? AllowFederatedUsers,
    string? AllowedDomainsKind,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> BlockedDomains,
    bool? BlockAllSubdomains,
    bool? AllowTeamsConsumer,
    bool? AllowTeamsConsumerInbound,
    string? ExternalAccessWithTrialTenants,
    IReadOnlyList<string> AllowedTrialTenantDomains,
    bool? RestrictTeamsConsumerToExternalUserProfiles)
{
    public const string SchemaVersion = "aegis-config-teams-federation-v1";
    public const string ExternalId = "teamsFederationConfiguration";

    /// <summary>Qualquer organização não bloqueada é permitida (lista aberta).</summary>
    public const string AllowAllKnownDomains = "AllowAllKnownDomains";

    /// <summary>Somente os domínios expressamente listados são permitidos (lista fechada).</summary>
    public const string AllowList = "AllowList";

    /// <summary>Comunicação com locatários de avaliação permitida (o valor documentado é "Allowed"/"Blocked").</summary>
    public const string TrialTenantsAllowed = "Allowed";
    public const string TrialTenantsBlocked = "Blocked";
}

/// <summary>
/// Uma política de REUNIÃO do Teams, com as propriedades que os controles leem. A identidade é a da fonte:
/// "Global" para a padrão da organização, "Tag:&lt;nome&gt;" para as personalizadas.
/// </summary>
public sealed record TeamsMeetingPolicyConfiguration(
    string Identity,
    bool? AllowAnonymousUsersToJoinMeeting,
    bool? AllowAnonymousUsersToStartMeeting,
    string? AutoAdmittedUsers,
    bool? AllowPSTNUsersToBypassLobby,
    string? MeetingChatEnabledType,
    string? DesignatedPresenterRoleMode,
    bool? AllowExternalParticipantGiveRequestControl,
    bool? AllowExternalNonTrustedMeetingChat,
    bool? AllowCloudRecording) : ITeamsPolicyDocument
{
    public const string SchemaVersion = "aegis-config-teams-meeting-policy-v1";

    /// <summary>Tipo de política como <c>Get-CsGroupPolicyAssignment</c> o nomeia (para casar as atribuições).</summary>
    public const string PolicyType = "TeamsMeetingPolicy";

    string ITeamsPolicyDocument.PolicyType => PolicyType;
}

/// <summary>Uma política de MENSAGENS do Teams, com as propriedades que os controles leem.</summary>
public sealed record TeamsMessagingPolicyConfiguration(
    string Identity,
    bool? AllowSecurityEndUserReporting) : ITeamsPolicyDocument
{
    public const string SchemaVersion = "aegis-config-teams-messaging-policy-v1";
    public const string PolicyType = "TeamsMessagingPolicy";

    string ITeamsPolicyDocument.PolicyType => PolicyType;
}

/// <summary>
/// Uma política de PERMISSÃO DE APLICATIVOS do Teams. Os três catálogos são independentes: aplicativos da
/// Microsoft (<c>DefaultCatalogApps</c>), de terceiros (<c>GlobalCatalogApps</c>) e personalizados da própria
/// organização (<c>PrivateCatalogApps</c>). O TIPO diz se a lista é de permitidos ou de bloqueados; a contagem é
/// preservada para que o achado possa dizer o tamanho da lista sem listar identificador de aplicativo.
/// </summary>
public sealed record TeamsAppPermissionPolicyConfiguration(
    string Identity,
    string? DefaultCatalogAppsType,
    int DefaultCatalogAppsCount,
    string? GlobalCatalogAppsType,
    int GlobalCatalogAppsCount,
    string? PrivateCatalogAppsType,
    int PrivateCatalogAppsCount) : ITeamsPolicyDocument
{
    public const string SchemaVersion = "aegis-config-teams-app-permission-policy-v1";
    public const string PolicyType = "TeamsAppPermissionPolicy";

    /// <summary>Somente os aplicativos da lista são permitidos (lista fechada).</summary>
    public const string AllowedAppList = "AllowedAppList";

    /// <summary>Todos os aplicativos são permitidos, exceto os da lista (lista aberta).</summary>
    public const string BlockedAppList = "BlockedAppList";

    string ITeamsPolicyDocument.PolicyType => PolicyType;

    public IReadOnlyList<(string Label, string? Type, int Count)> Catalogs => new[]
    {
        ("aplicativos da Microsoft", DefaultCatalogAppsType, DefaultCatalogAppsCount),
        ("aplicativos de terceiros", GlobalCatalogAppsType, GlobalCatalogAppsCount),
        ("aplicativos personalizados da organização", PrivateCatalogAppsType, PrivateCatalogAppsCount),
    };
}

/// <summary>
/// Atribuição de uma política do Teams a um GRUPO. É a evidência de ALCANCE de uma política personalizada que a
/// coleta consegue obter sem enumerar usuários. <paramref name="Rank"/> é a precedência entre atribuições de
/// grupo do mesmo tipo (1 = a que prevalece quando a pessoa está em mais de um grupo).
/// </summary>
public sealed record TeamsPolicyAssignment(
    string PolicyType,
    string? PolicyName,
    string GroupId,
    int? Rank)
{
    public const string SchemaVersion = "aegis-config-teams-policy-assignment-v1";
}

/// <summary>
/// O que todo documento de política do Teams tem em comum: a identidade na fonte e o tipo de política. Permite
/// que a análise de alcance (<see cref="TeamsPolicyReach"/>) seja escrita UMA vez para os três tipos.
/// </summary>
public interface ITeamsPolicyDocument
{
    string Identity { get; }
    string PolicyType { get; }
}

/// <summary>Convenções de identidade de política do Teams — um lugar só, usado pela coleta e pela avaliação.</summary>
public static class TeamsPolicyIdentities
{
    /// <summary>Identidade da política PADRÃO DA ORGANIZAÇÃO (a que vale para quem não tem outra atribuída).</summary>
    public const string Global = "Global";

    /// <summary>Prefixo que a fonte usa nas políticas personalizadas.</summary>
    public const string TagPrefix = "Tag:";

    public static bool IsGlobal(string? identity) =>
        string.IsNullOrWhiteSpace(identity) || identity!.Trim().Equals(Global, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome da política como as ATRIBUIÇÕES a nomeiam: sem o prefixo de marcação.</summary>
    public static string Name(string? identity)
    {
        var id = (identity ?? "").Trim();
        if (id.Length == 0) return Global;
        return id.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) ? id[TagPrefix.Length..] : id;
    }

    /// <summary>Rótulo legível: "padrão da organização" ou o nome da política personalizada, entre aspas.</summary>
    public static string Label(string? identity) =>
        IsGlobal(identity) ? "política padrão da organização" : "política “" + Name(identity) + "”";
}
