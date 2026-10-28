using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AegisScore.Application.Identity.Adm;
using AegisScore.Domain;

namespace AegisScore.Application.Knight.Configuration;

// ============================================================================
//  [AEGIS-KNIGHT-MULTICLOUD-01] Objetos de CONFIGURAÇÃO observados numa coleta
// ============================================================================
// Até aqui o ADM preservava só identidades (três populações). Uma política de acesso condicional não é uma
// identidade e não pode ser forçada a virar "usuário": ela tem estado, alvos, aplicações, condições e
// controles exigidos — e é exatamente essa combinação que decide se a MFA administrativa é exigida ou não.
//
// Estes contratos são a NORMALIZAÇÃO tipada e versionada dessa configuração. Eles são persistidos como
// documento (o contrato serializado, com o nome e a versão do contrato ao lado), e não como JSON livre: a
// leitura só aceita as versões que conhece e declara o resto como não interpretável.
//
// O tipo de objeto é explícito e extensível: o próximo ciclo acrescenta configurações de Microsoft 365 e
// recursos do Azure como novos tipos, sem tocar os que já existem.

/// <summary>Estado declarado de uma política de acesso condicional.</summary>
public enum ConditionalAccessPolicyState
{
    Enabled = 0,
    Disabled = 1,

    /// <summary>Somente relatório: a política é avaliada e registrada, mas NÃO impõe nada.</summary>
    ReportOnly = 2,

    /// <summary>Valor desconhecido na resposta da fonte — tratado como não impositivo.</summary>
    Unknown = 3,
}

/// <summary>
/// Política de acesso condicional NORMALIZADA — o que a política diz, sem interpretação. Os identificadores de
/// usuários, grupos, papéis e aplicações são os da própria fonte; valores especiais do diretório ("All",
/// "None", "GuestsOrExternalUsers", "MicrosoftAdminPortals") são preservados literalmente.
/// </summary>
public sealed record ConditionalAccessPolicyConfiguration(
    string Id,
    string? DisplayName,
    ConditionalAccessPolicyState State,
    string? RawState,
    IReadOnlyList<string> IncludeUsers,
    IReadOnlyList<string> ExcludeUsers,
    IReadOnlyList<string> IncludeGroups,
    IReadOnlyList<string> ExcludeGroups,
    IReadOnlyList<string> IncludeRoles,
    IReadOnlyList<string> ExcludeRoles,
    bool IncludesGuestsOrExternalUsers,
    bool ExcludesGuestsOrExternalUsers,
    IReadOnlyList<string> IncludeApplications,
    IReadOnlyList<string> ExcludeApplications,
    IReadOnlyList<string> IncludeUserActions,
    IReadOnlyList<string> IncludeAuthenticationContexts,
    IReadOnlyList<string> ClientAppTypes,
    bool HasPlatformCondition,
    bool HasLocationCondition,
    bool HasSignInRiskCondition,
    bool HasUserRiskCondition,
    bool HasDeviceFilter,
    string? GrantOperator,
    IReadOnlyList<string> BuiltInControls,
    string? AuthenticationStrengthId,
    string? AuthenticationStrengthName,
    // ---- [AEGIS-KNIGHT-COVERAGE-01] v2: condições e controles de sessão lidos pelos controles de configuração.
    // Todos com padrão nulo: um documento v1 continua legível, e SessionAndConditionsCaptured=false diz ao
    // controle que a coleta daquela época não registrou estes campos (não avaliado — nunca "ausente").
    IReadOnlyList<string>? UserRiskLevels = null,
    IReadOnlyList<string>? SignInRiskLevels = null,
    string? AuthenticationFlowsTransferMethods = null,
    IReadOnlyList<string>? IncludeLocations = null,
    IReadOnlyList<string>? ExcludeLocations = null,
    bool? SignInFrequencyEnabled = null,
    int? SignInFrequencyValue = null,
    string? SignInFrequencyType = null,
    string? SignInFrequencyInterval = null,
    bool? PersistentBrowserEnabled = null,
    string? PersistentBrowserMode = null,
    bool? ApplicationEnforcedRestrictions = null,
    IReadOnlyList<string>? AuthenticationStrengthCombinations = null,
    bool SessionAndConditionsCaptured = false,
    // [AEGIS-KNIGHT-COVERAGE-04] Proteção de token nas sessões de entrada (sessionControls.secureSignInSession, v1.0).
    // Nulo = coleta anterior a este campo (não avaliado); false = a política não exige.
    bool? SecureSignInSessionEnabled = null)
{
    /// <summary>Nome e versão do contrato de normalização persistido com o objeto.</summary>
    public const string SchemaVersion = "aegis-config-entra-ca-policy-v2";

    /// <summary>Versão anterior (sem condições de risco, fluxos de autenticação e sessão) — ainda legível.</summary>
    public const string SchemaVersionV1 = "aegis-config-entra-ca-policy-v1";
}

/// <summary>
/// Papel de diretório privilegiado ATIVO, como observado na coleta. <see cref="TemplateId"/> é o identificador
/// que as políticas de acesso condicional usam para mirar papéis; <see cref="UserMemberIds"/> são os
/// identificadores externos dos membros USUÁRIOS (os mesmos objetos que o ADM já preserva no conjunto de
/// membros privilegiados) — é com eles que uma exclusão nominal numa política é cruzada.
/// </summary>
/// <remarks>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] v2: os membros que NÃO são usuário diretos, por tipo — grupos atribuídos ao papel
/// (cujos usuários detêm o papel via grupo), identidades de aplicação e objetos de tipo não reconhecido. Antes, um grupo
/// atribuído a um papel virava "membro não identificável como usuário" e deixava AK-ENTRA-001/011 sem avaliação. Um documento
/// v1 continua legível: as listas por tipo ficam nulas e <see cref="MemberTypesCaptured"/> = false mantém a leitura de então.
/// </remarks>
public sealed record DirectoryRoleConfiguration(
    string TemplateId,
    string? DisplayName,
    int MemberCount,
    IReadOnlyList<string> UserMemberIds,
    IReadOnlyList<string>? GroupMemberIds = null,
    IReadOnlyList<string>? ServicePrincipalMemberIds = null,
    IReadOnlyList<string>? OtherMemberIds = null)
{
    public const string SchemaVersion = "aegis-config-entra-directory-role-v2";

    /// <summary>Versão anterior (só os membros usuários diretos) — ainda legível.</summary>
    public const string SchemaVersionV1 = "aegis-config-entra-directory-role-v1";

    /// <summary>A coleta registrou os membros por tipo (v2). Falso num documento v1.</summary>
    public bool MemberTypesCaptured => GroupMemberIds is not null && ServicePrincipalMemberIds is not null && OtherMemberIds is not null;
}

/// <summary>[AEGIS-KNIGHT-COLLECTION-GAPS-01] Completude da leitura do pertencimento de UM grupo.</summary>
public enum EntraGroupMembershipState
{
    /// <summary>Todas as páginas lidas: quem não está na lista NÃO é membro.</summary>
    Complete = 0,

    /// <summary>Leitura interrompida (teto de paginação): os listados são membros; os demais, desconhecidos.</summary>
    Incomplete = 1,

    /// <summary>Leitura recusada (ex.: grupo de pertencimento oculto sem Member.Read.Hidden): pertencimento desconhecido.</summary>
    Denied = 2,

    /// <summary>O grupo não foi encontrado (404): pertencimento desconhecido — nunca "vazio".</summary>
    NotFound = 3,

    /// <summary>Outra falha (serviço, tempo, contrato): os listados antes da falha são membros; os demais, desconhecidos.</summary>
    Failed = 4,
}

/// <summary>Tipos de membro devolvidos pelo pertencimento transitivo (pelo <c>@odata.type</c> da resposta).</summary>
public static class EntraMemberKind
{
    public const string User = "User";
    public const string ServicePrincipal = "ServicePrincipal";
    public const string Group = "Group";
    public const string Device = "Device";
    public const string Contact = "Contact";
    public const string Other = "Other";

    public static string FromODataType(string? odataType) => odataType?.ToLowerInvariant() switch
    {
        "#microsoft.graph.user" => User,
        "#microsoft.graph.serviceprincipal" => ServicePrincipal,
        "#microsoft.graph.group" => Group,
        "#microsoft.graph.device" => Device,
        "#microsoft.graph.orgcontact" => Contact,
        _ => Other,
    };
}

/// <summary>
/// Um membro (transitivo) de um grupo, como a leitura o devolveu. Nome e UPN só são guardados para contas PRIVILEGIADAS (as
/// que a evidência precisa nomear e que o ADM já preserva no inventário de papéis) — os demais membros ficam só pelo id.
/// </summary>
public sealed record EntraGroupMember(string Id, string Kind, string? DisplayName = null, string? UserPrincipalName = null);

/// <summary>
/// [AEGIS-KNIGHT-COLLECTION-GAPS-01] Pertencimento TRANSITIVO de UM grupo referenciado por política de acesso condicional
/// (inclusão ou exclusão) ou atribuído a papel privilegiado, como observado numa coleta
/// (<c>GET /groups/{id}/transitiveMembers</c>, Directory.Read.All). Guarda a procedência (quem referencia o grupo e o
/// endpoint), o instante, a completude e a falha — a regra nunca aprova por lista parcial nem presume associação.
/// </summary>
/// <param name="ReferencedBy">"policy:{id}:include", "policy:{id}:exclude", "role:{templateId}".</param>
public sealed record EntraGroupMembership(
    string GroupId,
    string? DisplayName,
    EntraGroupMembershipState State,
    IReadOnlyList<EntraGroupMember> Members,
    IReadOnlyList<string> ReferencedBy,
    DateTimeOffset ReadAt,
    string Endpoint,
    string? Detail = null,
    int? HttpStatus = null,
    string? Code = null)
{
    public const string SchemaVersion = "aegis-config-entra-group-membership-v1";

    public bool IsComplete => State == EntraGroupMembershipState.Complete;

    public IEnumerable<string> UserIds => Members.Where(m => m.Kind == EntraMemberKind.User).Select(m => m.Id);

    /// <summary>true = membro lido; false = leitura completa e ele não está; null = desconhecido.</summary>
    public bool? Holds(string userId) =>
        Members.Any(m => m.Kind == EntraMemberKind.User && string.Equals(m.Id, userId, StringComparison.OrdinalIgnoreCase))
            ? true
            : IsComplete ? false : null;

    public static string Label(EntraGroupMembershipState state) => state switch
    {
        EntraGroupMembershipState.Complete => "lido por completo",
        EntraGroupMembershipState.Incomplete => "lido em parte (teto de paginação)",
        EntraGroupMembershipState.Denied => "leitura recusada",
        EntraGroupMembershipState.NotFound => "grupo não encontrado",
        _ => "leitura não concluída",
    };
}

/// <summary>
/// A configuração de diretório observada numa coleta. <c>null</c> numa lista significa "não coletado"
/// (a capacidade falhou ou não existe nesta fonte) — distinto de lista VAZIA ("coletado, não há nenhum").
/// </summary>
/// <param name="GroupMemberships">[AEGIS-KNIGHT-COLLECTION-GAPS-01] Pertencimento dos grupos referenciados; nulo = não coletado
/// (aquisições anteriores): a análise mantém o grupo como desconhecido, exatamente como antes.</param>
/// <param name="EnabledAccountIds">[AEGIS-KNIGHT-COLLECTION-GAPS-01] Ids das contas habilitadas do diretório, de uma enumeração
/// COMPLETA da mesma coleta (nulo = não disponível ou incompleta). É a população da base mínima conferida conta a conta.</param>
public sealed record KnightDirectoryConfiguration(
    IReadOnlyList<ConditionalAccessPolicyConfiguration>? ConditionalAccessPolicies,
    IReadOnlyList<DirectoryRoleConfiguration>? PrivilegedRoles,
    IReadOnlyList<EntraGroupMembership>? GroupMemberships = null,
    IReadOnlyList<string>? EnabledAccountIds = null)
{
    public static KnightDirectoryConfiguration None { get; } = new(null, null);

    public bool IsEmpty => ConditionalAccessPolicies is null && PrivilegedRoles is null && GroupMemberships is null;
}

/// <summary>
/// Tradução ENTRE a configuração tipada e os objetos de configuração do ADM (documento versionado). Leitura
/// fail-closed: um contrato desconhecido ou ilegível não vira configuração inventada — é omitido e declarado.
/// </summary>
public static class DirectoryConfigurationDocuments
{
    public static IReadOnlyList<IdentityObservedConfiguration> ToObserved(KnightDirectoryConfiguration? configuration)
    {
        var list = new List<IdentityObservedConfiguration>();
        if (configuration is null) return list;

        foreach (var p in configuration.ConditionalAccessPolicies ?? Array.Empty<ConditionalAccessPolicyConfiguration>())
        {
            if (string.IsNullOrWhiteSpace(p.Id)) continue;
            list.Add(new IdentityObservedConfiguration(
                ConfigurationObjectKind.ConditionalAccessPolicy, p.Id.Trim(), p.DisplayName,
                ConditionalAccessPolicyConfiguration.SchemaVersion,
                JsonSerializer.Serialize(p, IdentityEvidenceFactsJson.Options)));
        }

        foreach (var r in configuration.PrivilegedRoles ?? Array.Empty<DirectoryRoleConfiguration>())
        {
            if (string.IsNullOrWhiteSpace(r.TemplateId)) continue;
            static IReadOnlyList<string>? Sorted(IReadOnlyList<string>? ids) => ids?.OrderBy(x => x, StringComparer.Ordinal).ToList();
            list.Add(new IdentityObservedConfiguration(
                ConfigurationObjectKind.DirectoryRole, r.TemplateId.Trim(), r.DisplayName,
                DirectoryRoleConfiguration.SchemaVersion,
                JsonSerializer.Serialize(
                    r with
                    {
                        UserMemberIds = Sorted(r.UserMemberIds)!,
                        GroupMemberIds = Sorted(r.GroupMemberIds) ?? Array.Empty<string>(),
                        ServicePrincipalMemberIds = Sorted(r.ServicePrincipalMemberIds) ?? Array.Empty<string>(),
                        OtherMemberIds = Sorted(r.OtherMemberIds) ?? Array.Empty<string>(),
                    },
                    IdentityEvidenceFactsJson.Options)));
        }

        // [AEGIS-KNIGHT-COLLECTION-GAPS-01] Pertencimento dos grupos referenciados (um documento por grupo, membros ordenados).
        foreach (var g in configuration.GroupMemberships ?? Array.Empty<EntraGroupMembership>())
        {
            if (string.IsNullOrWhiteSpace(g.GroupId)) continue;
            list.Add(new IdentityObservedConfiguration(
                ConfigurationObjectKind.DirectoryGroupMembership, g.GroupId.Trim(),
                g.DisplayName is { Length: > 300 } n ? n[..299] + "…" : g.DisplayName,
                EntraGroupMembership.SchemaVersion,
                JsonSerializer.Serialize(
                    g with
                    {
                        Members = g.Members.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(),
                        ReferencedBy = g.ReferencedBy.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    },
                    IdentityEvidenceFactsJson.Options)));
        }

        return list;
    }

    /// <summary>
    /// Reconstrói a configuração tipada a partir dos objetos persistidos. <paramref name="policiesCollected"/> e
    /// <paramref name="rolesCollected"/> vêm do desfecho das CAPACIDADES da mesma aquisição: sem elas, uma lista
    /// vazia seria ambígua entre "não há políticas" e "não conseguimos ler as políticas".
    /// </summary>
    public static KnightDirectoryConfiguration FromObserved(
        IEnumerable<IdentityObservedConfiguration> objects, bool policiesCollected, bool rolesCollected,
        bool groupsCollected = false, IReadOnlyList<string>? enabledAccountIds = null)
    {
        var policies = new List<ConditionalAccessPolicyConfiguration>();
        var roles = new List<DirectoryRoleConfiguration>();
        var groups = new List<EntraGroupMembership>();

        foreach (var o in objects ?? Array.Empty<IdentityObservedConfiguration>())
        {
            try
            {
                switch (o.Kind)
                {
                    case ConfigurationObjectKind.ConditionalAccessPolicy
                        when o.SchemaVersion == ConditionalAccessPolicyConfiguration.SchemaVersion
                             || o.SchemaVersion == ConditionalAccessPolicyConfiguration.SchemaVersionV1:
                        if (JsonSerializer.Deserialize<ConditionalAccessPolicyConfiguration>(o.ConfigurationJson, IdentityEvidenceFactsJson.Options) is { } p)
                            policies.Add(p);
                        break;
                    case ConfigurationObjectKind.DirectoryRole
                        when o.SchemaVersion == DirectoryRoleConfiguration.SchemaVersion
                             || o.SchemaVersion == DirectoryRoleConfiguration.SchemaVersionV1:
                        if (JsonSerializer.Deserialize<DirectoryRoleConfiguration>(o.ConfigurationJson, IdentityEvidenceFactsJson.Options) is { } r)
                            // v1 não tinha os tipos: as listas ficam nulas mesmo que o JSON as traga vazias por acaso.
                            roles.Add(o.SchemaVersion == DirectoryRoleConfiguration.SchemaVersionV1
                                ? r with { GroupMemberIds = null, ServicePrincipalMemberIds = null, OtherMemberIds = null }
                                : r);
                        break;
                    case ConfigurationObjectKind.DirectoryGroupMembership
                        when o.SchemaVersion == EntraGroupMembership.SchemaVersion:
                        if (JsonSerializer.Deserialize<EntraGroupMembership>(o.ConfigurationJson, IdentityEvidenceFactsJson.Options) is { } g)
                            groups.Add(g);
                        break;
                }
            }
            catch (JsonException)
            {
                // Documento ilegível: omitido. A análise seguinte vê menos objetos e declara a limitação —
                // nunca completa o que faltou com suposição.
            }
        }

        return new KnightDirectoryConfiguration(
            policiesCollected ? policies.OrderBy(p => p.Id, StringComparer.Ordinal).ToList() : null,
            rolesCollected ? roles.OrderBy(r => r.TemplateId, StringComparer.Ordinal).ToList() : null,
            // Um documento de grupo ilegível some da lista — e o grupo fica DESCONHECIDO na análise (nunca vazio).
            groupsCollected ? groups.OrderBy(g => g.GroupId, StringComparer.Ordinal).ToList() : null,
            enabledAccountIds);
    }
}
