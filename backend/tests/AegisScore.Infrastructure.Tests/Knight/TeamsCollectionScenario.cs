using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Connectors.Microsoft.Knight.Teams;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] Ambiente SINTÉTICO do Microsoft Teams: monta exatamente o documento JSON que o
/// adaptador de coleta escreve na saída padrão e o faz passar pelo MESMO leitor do produto
/// (<c>PowerShellTeamsAdminReader.Parse</c>). Assim o teste exercita o contrato de verdade — nomes de campo,
/// formato dos itens, categorias de falha — em vez de um objeto montado à mão que nunca existiu.
///
/// Dados 100% sintéticos (example.com). Nenhuma credencial, nenhum locatário real, nenhum processo externo.
/// </summary>
internal sealed class TeamsCollectionScenario : ITeamsAdminReader
{
    internal enum Variant
    {
        /// <summary>Tudo como o critério espera — a política padrão da organização e a personalizada.</summary>
        Compliant,

        /// <summary>Cada critério violado de forma comprovável.</summary>
        NonCompliant,

        /// <summary>A política padrão atende; UMA personalizada, atribuída a um grupo, não.</summary>
        CustomPolicyFails,

        /// <summary>A fonte não informou valores que o critério exige — não aprova nem reprova.</summary>
        Incomplete,

        /// <summary>A leitura das políticas de reunião foi recusada por permissão; as outras concluíram.</summary>
        MeetingPoliciesDenied,

        /// <summary>A conexão de aplicativo foi recusada — nenhuma leitura chegou a ser tentada.</summary>
        NotConnected,
    }

    private readonly Variant _variant;

    public TeamsCollectionScenario(Variant variant) => _variant = variant;

    public int Reads { get; private set; }

    public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(PowerShellTeamsAdminReader.Parse(Json(_variant)));
    }

    public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) =>
        Task.FromResult(PowerShellTeamsAdminReader.Parse(Json(Variant.Compliant)));

    // ---- Documento do adaptador ------------------------------------------------------------------------

    internal static string Json(Variant variant)
    {
        if (variant == Variant.NotConnected)
            return Serialize(new
            {
                runtime = Runtime,
                connected = false,
                connectionError = "A aplicação não tem o papel necessário no centro de administração do Teams.",
                connectionErrorCategory = "InsufficientPermission",
                reads = Array.Empty<object>(),
            });

        var compliant = variant is Variant.Compliant or Variant.CustomPolicyFails or Variant.Incomplete or Variant.MeetingPoliciesDenied;

        var reads = new List<object>
        {
            Read("TeamsClientConfiguration", "Get-CsTeamsClientConfiguration", new[]
            {
                new Dictionary<string, object?>
                {
                    ["identity"] = "Global",
                    ["allowEmailIntoChannel"] = variant == Variant.Incomplete ? null : !compliant,
                    ["allowDropBox"] = !compliant,
                    ["allowBox"] = false,
                    ["allowGoogleDrive"] = !compliant,
                    ["allowShareFile"] = false,
                    ["allowEgnyte"] = false,
                    ["allowGuestUser"] = false,
                },
            }),

            Read("TeamsFederationConfiguration", "Get-CsTenantFederationConfiguration", new[]
            {
                new Dictionary<string, object?>
                {
                    ["allowFederatedUsers"] = true,
                    ["allowedDomainsKind"] = compliant ? "AllowList" : "AllowAllKnownDomains",
                    ["allowedDomains"] = compliant ? new[] { "parceiro.example.com" } : Array.Empty<string>(),
                    ["blockedDomains"] = compliant ? Array.Empty<string>() : new[] { "malicioso.example.com" },
                    ["blockAllSubdomains"] = false,
                    ["allowTeamsConsumer"] = !compliant,
                    ["allowTeamsConsumerInbound"] = !compliant,
                    ["externalAccessWithTrialTenants"] = compliant ? "Blocked" : "Allowed",
                    ["allowedTrialTenantDomains"] = Array.Empty<string>(),
                    ["restrictTeamsConsumerToExternalUserProfiles"] = compliant,
                },
            }),

            variant == Variant.MeetingPoliciesDenied
                ? Failed("TeamsMeetingPolicies", "Get-CsTeamsMeetingPolicy", "InsufficientPermission",
                    "O acesso foi negado: a aplicação não tem o papel necessário.")
                : Read("TeamsMeetingPolicies", "Get-CsTeamsMeetingPolicy", MeetingPolicies(variant)),

            Read("TeamsMessagingPolicies", "Get-CsTeamsMessagingPolicy", new[]
            {
                new Dictionary<string, object?>
                {
                    ["identity"] = "Global",
                    ["allowSecurityEndUserReporting"] = compliant,
                },
            }),

            Read("TeamsAppPermissionPolicies", "Get-CsTeamsAppPermissionPolicy", new[]
            {
                new Dictionary<string, object?>
                {
                    ["identity"] = "Global",
                    ["defaultCatalogAppsType"] = "AllowedAppList",
                    ["defaultCatalogAppsCount"] = 12,
                    ["globalCatalogAppsType"] = compliant ? "AllowedAppList" : "BlockedAppList",
                    ["globalCatalogAppsCount"] = compliant ? 4 : 1,
                    ["privateCatalogAppsType"] = "AllowedAppList",
                    ["privateCatalogAppsCount"] = 2,
                },
            }),

            Read("TeamsPolicyAssignments", "Get-CsGroupPolicyAssignment", new[]
            {
                new Dictionary<string, object?>
                {
                    ["policyType"] = "TeamsMeetingPolicy",
                    ["policyName"] = "Convidados",
                    ["groupId"] = "11111111-2222-3333-4444-555555555555",
                    ["rank"] = 1,
                },
            }),
        };

        return Serialize(new
        {
            runtime = Runtime,
            connected = true,
            connectionError = (string?)null,
            connectionErrorCategory = (string?)null,
            reads,
        });
    }

    private static object[] MeetingPolicies(Variant variant)
    {
        var global = Meeting("Global", compliant: variant != Variant.NonCompliant);
        if (variant == Variant.Incomplete) global["autoAdmittedUsers"] = null;

        return variant switch
        {
            // A política padrão atende; a personalizada "Convidados" (atribuída a um grupo) não.
            Variant.CustomPolicyFails => new object[] { global, Meeting("Tag:Convidados", compliant: false) },
            Variant.NonCompliant => new object[] { global, Meeting("Tag:Convidados", compliant: false) },
            _ => new object[] { global, Meeting("Tag:Executivos", compliant: true) },
        };
    }

    private static Dictionary<string, object?> Meeting(string identity, bool compliant) => new()
    {
        ["identity"] = identity,
        ["allowAnonymousUsersToJoinMeeting"] = !compliant,
        ["allowAnonymousUsersToStartMeeting"] = !compliant,
        ["autoAdmittedUsers"] = compliant ? "EveryoneInCompanyExcludingGuests" : "Everyone",
        ["allowPSTNUsersToBypassLobby"] = !compliant,
        ["meetingChatEnabledType"] = compliant ? "EnabledExceptAnonymous" : "Enabled",
        ["designatedPresenterRoleMode"] = compliant ? "OrganizerOnlyUserOverride" : "EveryoneUserOverride",
        ["allowExternalParticipantGiveRequestControl"] = !compliant,
        ["allowExternalNonTrustedMeetingChat"] = !compliant,
        ["allowCloudRecording"] = !compliant,
    };

    private static readonly object Runtime = new { powerShell = "7.4.6", module = "6.9.0", platform = "Unix" };

    private static object Read(string capability, string command, object items) => new
    {
        capability,
        command,
        ok = true,
        items,
        errorCategory = (string?)null,
        error = (string?)null,
    };

    private static object Failed(string capability, string command, string category, string error) => new
    {
        capability,
        command,
        ok = false,
        items = Array.Empty<object>(),
        errorCategory = category,
        error,
    };

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
