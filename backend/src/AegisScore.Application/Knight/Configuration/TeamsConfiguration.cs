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
/// Resumo do modelo de DISPONIBILIDADE DE APLICATIVOS do locatário, lido por <c>Get-AllM365TeamsApps</c> — o
/// comando oficial do gerenciamento centrado em aplicativos (ACM) / gerenciamento unificado (UAM).
///
/// Por que este documento existe: a documentação oficial do <c>Get-CsTeamsAppPermissionPolicy</c> afirma que ele
/// "só é aplicável a locatários que NÃO foram migrados para ACM ou UAM", e a do próprio ACM afirma que, depois da
/// migração, "não é possível acessar, editar ou usar políticas de permissão". Sem saber qual dos dois modelos
/// governa, aprovar um locatário pela configuração legada é aprovar uma configuração que pode não estar em vigor.
///
/// O que é preservado é AGREGADO de propósito: contagens. Identificador de aplicativo e, principalmente, o
/// identificador do usuário que fez a última alteração (<c>AssignedBy</c>) NÃO atravessam esta fronteira — não são
/// necessários para decidir qual modelo governa, e o ADM não é lugar para dado pessoal que o critério não usa.
/// </summary>
/// <param name="AppsRead">Aplicativos que o comando devolveu no catálogo do locatário.</param>
/// <param name="AppsWithAssignment">
/// Quantos deles carregam uma DEFINIÇÃO DE DISPONIBILIDADE por aplicativo (<c>AvailableTo.AssignmentType</c>) —
/// a forma de governo do ACM/UAM. É este número, e não a existência de políticas legadas, que demonstra o modelo.
/// </param>
public sealed record TeamsAppAvailabilityModel(
    int AppsRead,
    int AppsWithAssignment,
    int AssignedToEveryone,
    int AssignedToUsersAndGroups,
    int AssignedToNoOne)
{
    public const string SchemaVersion = "aegis-config-teams-app-availability-v1";
    public const string ExternalId = "teamsAppAvailabilityModel";
}

/// <summary>Qual modelo governa o acesso a aplicativos no locatário, segundo a coleta.</summary>
public enum TeamsAppGovernanceModel
{
    /// <summary>A coleta não permite dizer qual modelo governa. NUNCA aprova pela configuração legada.</summary>
    Undetermined = 0,

    /// <summary>As POLÍTICAS DE PERMISSÃO legadas governam: o modelo por aplicativo não respondeu por este locatário.</summary>
    LegacyPermissionPolicies = 1,

    /// <summary>O modelo por aplicativo (ACM/UAM) governa: as políticas de permissão legadas não são autoritativas.</summary>
    AppCentricOrUnified = 2,
}

/// <summary>
/// Resolve o modelo de governo de aplicativos a partir do que a coleta trouxe. É deliberadamente conservador:
/// só afirma o modelo legado quando o comando do ACM/UAM FOI executado e não devolveu nenhuma disponibilidade por
/// aplicativo. Leitura ausente, recusada ou vazia = indeterminado.
/// </summary>
public static class TeamsAppGovernance
{
    /// <summary>Requisito que falta para completar a avaliação quando o modelo não pôde ser determinado.</summary>
    public const string Requirement =
        "Para concluir este critério é preciso ler o modelo que governa os aplicativos do locatário: o comando "
        + "oficial Get-AllM365TeamsApps, com o mesmo papel de leitura já exigido pela coleta. Sem essa leitura, a "
        + "configuração legada de permissão de aplicativos é preservada como evidência, mas não sustenta veredito.";

    public static (TeamsAppGovernanceModel Model, string Reason, TeamsAppAvailabilityModel? Observed) Resolve(
        KnightEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var read = context.Configuration.Read<TeamsAppAvailabilityModel>();

        if (!read.Collected)
            return (TeamsAppGovernanceModel.Undetermined,
                read.MissingReason
                ?? "a leitura do modelo de disponibilidade de aplicativos não foi concluída nesta aquisição.",
                null);

        if (read.Items.Count == 0)
            return (TeamsAppGovernanceModel.Undetermined,
                "a leitura do modelo de disponibilidade de aplicativos concluiu sem devolver o catálogo do locatário.",
                null);

        var model = read.Items[0];

        if (model.AppsRead <= 0)
            return (TeamsAppGovernanceModel.Undetermined,
                "o catálogo de aplicativos do locatário voltou vazio: não é possível dizer qual modelo governa o acesso a aplicativos.",
                model);

        if (model.AppsWithAssignment > 0)
            return (TeamsAppGovernanceModel.AppCentricOrUnified,
                $"{model.AppsWithAssignment} de {model.AppsRead} aplicativos do catálogo têm a disponibilidade definida "
                + "por aplicativo (gerenciamento centrado em aplicativos / unificado).",
                model);

        return (TeamsAppGovernanceModel.LegacyPermissionPolicies,
            $"nenhum dos {model.AppsRead} aplicativos do catálogo tem disponibilidade definida por aplicativo: "
            + "o acesso continua governado pelas políticas de permissão.",
            model);
    }
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
