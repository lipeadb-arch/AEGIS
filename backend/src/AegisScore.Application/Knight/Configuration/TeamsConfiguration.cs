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
/// "Global" para a padrão da organização, "Tag:&lt;nome&gt;" para as personalizadas. <c>null</c> quando a fonte NÃO
/// identificou a política — dado insuficiente, jamais convertido em "Global" (ver <see cref="TeamsPolicyIdentities"/>).
/// </summary>
public sealed record TeamsMeetingPolicyConfiguration(
    string? Identity,
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

/// <summary>
/// Uma política de MENSAGENS do Teams. <c>Identity</c> segue a mesma regra de
/// <see cref="TeamsMeetingPolicyConfiguration"/>: <c>null</c> = a fonte não identificou a política.
/// </summary>
public sealed record TeamsMessagingPolicyConfiguration(
    string? Identity,
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
    string? Identity,
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
/// O que falta para avaliar o acesso a aplicativos do Teams — e por que ESTE conector não pode avaliá-lo.
///
/// O comando de leitura das políticas de permissão (<c>Get-CsTeamsAppPermissionPolicy</c>) é documentado como
/// aplicável APENAS a locatários que não foram migrados para o gerenciamento centrado em aplicativos (ACM) ou
/// unificado (UAM); depois da migração, essas políticas não podem mais ser acessadas, editadas nem usadas. Logo,
/// encontrar políticas não prova que elas governem: pode ser resíduo do modelo antigo.
///
/// Dizer qual modelo governa exigiria ler o modelo NOVO — <c>Get-AllM365TeamsApps</c>, <c>Get-M365TeamsApp</c> ou
/// <c>Get-M365UnifiedTenantSettings</c>. A documentação oficial de AUTENTICAÇÃO BASEADA EM APLICATIVO do módulo do
/// Teams lista os três, nominalmente, entre os comandos NÃO SUPORTADOS — e autenticação por aplicativo é a única
/// forma que este conector usa. Isso não é falta de papel de diretório: nenhum papel adicional torna um comando
/// não suportado suportado, e pedir mais acesso ao cliente não resolveria nada.
/// https://learn.microsoft.com/en-us/microsoftteams/teams-powershell-application-authentication
///
/// Consequência assumida: o critério fica NÃO AVALIADO, com a dependência declarada no achado. A configuração
/// legada continua sendo coletada e preservada como evidência — ela existe e foi lida —, mas não sustenta
/// veredito. Aprovar por ela seria aprovar uma configuração que pode não estar em vigor.
/// </summary>
public static class TeamsAppGovernance
{
    /// <summary>Motivo pelo qual o modelo em vigor não pode ser determinado por esta coleta.</summary>
    public const string Reason =
        "a leitura que diria qual modelo governa os aplicativos deste locatário — o gerenciamento centrado em "
        + "aplicativos (ACM/UAM) ou as políticas de permissão legadas — não está disponível para o AEGIS: os "
        + "comandos oficiais que a fariam (Get-AllM365TeamsApps, Get-M365TeamsApp, Get-M365UnifiedTenantSettings) "
        + "são documentados como NÃO SUPORTADOS com autenticação de aplicativo, que é a forma usada por este "
        + "conector. Encontrar políticas de permissão não demonstra que elas governem: podem ser resíduo do "
        + "modelo anterior à migração.";

    /// <summary>O que precisaria existir para completar a avaliação — sem permissão nova e sem endpoint próprio.</summary>
    public const string Requirement =
        "Completar este critério depende de uma leitura do modelo de aplicativos compatível com autenticação de "
        + "aplicativo. Enquanto a Microsoft não oferecer essa leitura, concluir exigiria uma sessão administrativa "
        + "delegada, que é outra forma de acesso ao locatário — decisão do cliente, fora do que esta coleta faz. "
        + "Nenhuma permissão adicional muda esse quadro.";
}

/// <summary>
/// O que todo documento de política do Teams tem em comum: a identidade na fonte e o tipo de política. Permite
/// que a análise de alcance (<see cref="TeamsPolicyReach"/>) seja escrita UMA vez para os três tipos.
/// <see cref="Identity"/> é ANULÁVEL de propósito: identificação ausente é dado insuficiente, não "Global".
/// </summary>
public interface ITeamsPolicyDocument
{
    string? Identity { get; }
    string PolicyType { get; }
}

/// <summary>
/// Convenções de identidade de política do Teams — um lugar só, usado pela coleta e pela avaliação.
///
/// REGRA CENTRAL: identificação AUSENTE (nula, vazia ou só com espaços) é <b>dado insuficiente</b>, e não a
/// política padrão da organização. O caminho anterior tratava nulo como "Global" e, com isso, (a) inventava a
/// instância que sempre se aplica, permitindo APROVAR o ambiente a partir de um registro que a fonte nem
/// identificou, e (b) fundia num só objeto registros distintos que chegaram sem identidade. As duas coisas são
/// afirmações que a coleta não sustenta. Agora: <see cref="IsIdentified"/> separa o caso, <see cref="IsGlobal"/>
/// exige a identidade literal, e a avaliação trata o não identificado como indeterminado, com a limitação dita.
/// </summary>
public static class TeamsPolicyIdentities
{
    /// <summary>Identidade da política PADRÃO DA ORGANIZAÇÃO (a que vale para quem não tem outra atribuída).</summary>
    public const string Global = "Global";

    /// <summary>Prefixo que a fonte usa nas políticas personalizadas.</summary>
    public const string TagPrefix = "Tag:";

    /// <summary>Rótulo de uma instância que a fonte devolveu SEM identificar.</summary>
    public const string UnidentifiedLabel = "política sem identificação na coleta";

    /// <summary>A fonte identificou esta política? Nulo, vazio ou só espaços = não.</summary>
    public static bool IsIdentified(string? identity) => !string.IsNullOrWhiteSpace(identity);

    /// <summary>
    /// É a política PADRÃO DA ORGANIZAÇÃO? Exige a identidade literal: sem identificação a resposta é NÃO —
    /// afirmar o contrário criaria a instância que decide o veredito do ambiente inteiro.
    /// </summary>
    public static bool IsGlobal(string? identity) =>
        IsIdentified(identity) && identity!.Trim().Equals(Global, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Nome da política como as ATRIBUIÇÕES a nomeiam: sem o prefixo de marcação. Sem identificação devolve
    /// <c>null</c> — não há nome a casar com atribuição alguma.
    /// </summary>
    public static string? Name(string? identity)
    {
        if (!IsIdentified(identity)) return null;
        var id = identity!.Trim();
        return id.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) ? id[TagPrefix.Length..] : id;
    }

    /// <summary>Rótulo legível: "padrão da organização", o nome da personalizada entre aspas, ou o não identificado.</summary>
    public static string Label(string? identity) =>
        !IsIdentified(identity) ? UnidentifiedLabel
        : IsGlobal(identity) ? "política padrão da organização"
        : "política “" + Name(identity) + "”";
}
