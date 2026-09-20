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
    bool SessionAndConditionsCaptured = false)
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
public sealed record DirectoryRoleConfiguration(
    string TemplateId,
    string? DisplayName,
    int MemberCount,
    IReadOnlyList<string> UserMemberIds)
{
    public const string SchemaVersion = "aegis-config-entra-directory-role-v1";
}

/// <summary>
/// A configuração de diretório observada numa coleta. <c>null</c> numa lista significa "não coletado"
/// (a capacidade falhou ou não existe nesta fonte) — distinto de lista VAZIA ("coletado, não há nenhum").
/// </summary>
public sealed record KnightDirectoryConfiguration(
    IReadOnlyList<ConditionalAccessPolicyConfiguration>? ConditionalAccessPolicies,
    IReadOnlyList<DirectoryRoleConfiguration>? PrivilegedRoles)
{
    public static KnightDirectoryConfiguration None { get; } = new(null, null);

    public bool IsEmpty => ConditionalAccessPolicies is null && PrivilegedRoles is null;
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
            list.Add(new IdentityObservedConfiguration(
                ConfigurationObjectKind.DirectoryRole, r.TemplateId.Trim(), r.DisplayName,
                DirectoryRoleConfiguration.SchemaVersion,
                JsonSerializer.Serialize(
                    r with { UserMemberIds = r.UserMemberIds.OrderBy(x => x, StringComparer.Ordinal).ToList() },
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
        IEnumerable<IdentityObservedConfiguration> objects, bool policiesCollected, bool rolesCollected)
    {
        var policies = new List<ConditionalAccessPolicyConfiguration>();
        var roles = new List<DirectoryRoleConfiguration>();

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
                        when o.SchemaVersion == DirectoryRoleConfiguration.SchemaVersion:
                        if (JsonSerializer.Deserialize<DirectoryRoleConfiguration>(o.ConfigurationJson, IdentityEvidenceFactsJson.Options) is { } r)
                            roles.Add(r);
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
            rolesCollected ? roles.OrderBy(r => r.TemplateId, StringComparer.Ordinal).ToList() : null);
    }
}
