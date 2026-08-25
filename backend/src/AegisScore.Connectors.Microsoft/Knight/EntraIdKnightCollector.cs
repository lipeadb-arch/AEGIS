using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AegisScore.Application.Knight;
using AegisScore.Domain;

namespace AegisScore.Connectors.Microsoft.Knight;

/// <summary>
/// Coletor REAL do Microsoft Entra ID (somente leitura) para o AEGIS KNIGHT. Autentica por client credentials
/// (por tenant, segredo recebido DECIFRADO no contexto — nunca persistido/logado aqui), consulta o Microsoft
/// Graph (paginado, com resiliência no HttpClient) e NORMALIZA as respostas em fatos tipados. Cada capacidade
/// tem sua permissão: um 403 vira <see cref="KnightCapabilityOutcome.InsufficientPermission"/> e os sinais
/// daquela capacidade ficam Missing (→ NotEvaluated na avaliação — NUNCA "Conforme"). Falha parcial não
/// invalida o assessment (estado PartialCollection); falha total do Graph NÃO cai para Demo — devolve o estado
/// real (AuthenticationFailure/Throttled/Unavailable). Erros são SANITIZADOS (sem token, segredo ou PII).
///
/// Permissões de aplicativo (Application) SOMENTE LEITURA usadas:
///   • Directory.Read.All   → papéis privilegiados e membros; concessões (appRoleAssignedTo) e consentimentos
///                            delegados (oauth2PermissionGrants);
///   • AuditLog.Read.All    → detalhes de registro de MFA e atividade de sign-in (cobertura de MFA, obsoletas);
///   • User.Read.All        → convidados e sua atividade;
///   • Policy.Read.All      → acesso condicional e security defaults (legada bloqueada, MFA admin, baseline);
///   • Application.Read.All → aplicações (credenciais vencendo) e service principals (permissões concedidas).
///
/// Sobre "concedido × solicitado": AK-ENTRA-010 mede permissões de aplicativo EFETIVAMENTE CONCEDIDAS
/// (appRoleAssignments no service principal do Graph) — não <c>requiredResourceAccess</c>, que é apenas o que a
/// aplicação DECLARA/solicita. AK-ENTRA-013 conta consentimentos DELEGADOS tenant-wide (consentType=AllPrincipals).
/// </summary>
public sealed class EntraIdKnightCollector : IKnightCollector
{
    private const string Label = "Microsoft Entra ID";
    private const string MicrosoftGraphAppId = "00000003-0000-0000-c000-000000000000";

    private static readonly HashSet<string> HighPrivilegeGraphAppRoleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "19dbc75e-c2e2-444c-a770-ec69d8559fc7",
        "9e3f62cf-ca93-4989-b6ce-bf83c28f9fe8",
        "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
        "06b708a9-e830-4db3-a914-8e69da51d44f",
        "62a82d76-70ea-41e2-9197-370581804d09",
    };

    private readonly IEntraGraphClient _graph;
    private readonly ILogger<EntraIdKnightCollector>? _log;

    public EntraIdKnightCollector(IEntraGraphClient graph, ILogger<EntraIdKnightCollector>? log = null)
    {
        _graph = graph;
        _log = log;
    }

    public KnightSourceType Source => KnightSourceType.MicrosoftEntraId;

    public async Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default)
    {
        if (context.Configuration is not KnightEntraIdConfiguration cfg)
            return KnightCollectionResult.NotConfigured(Source, Label);

        string token;
        try
        {
            token = await _graph.AcquireTokenAsync(cfg, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var state = ex.Kind switch
            {
                EntraGraphErrorKind.AuthFailure => KnightSourceState.AuthenticationFailure,
                EntraGraphErrorKind.Throttled => KnightSourceState.Throttled,
                _ => KnightSourceState.Unavailable,
            };
            return new KnightCollectionResult(Source, state, Label, KnightFactSet.Empty,
                Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow,
                "Falha ao autenticar a aplicação junto ao Microsoft Graph.");
        }

        var obs = new List<KnightObservation>();
        var caps = new List<KnightCapabilityStatus>();
        var privileged = new PrivilegedAccumulator();

        await RunCapabilityAsync(KnightCapability.PrivilegedRoleInventory,
            new[] { KnightSignalKey.PrivilegedAccountsTotal, KnightSignalKey.PrivilegedAccountsWithMailbox,
                    KnightSignalKey.StalePrivilegedAccounts, KnightSignalKey.ExternalMembersInPrivilegedRoles },
            () => CollectPrivilegedRolesAsync(token, cfg, privileged, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.MfaRegistration,
            new[] { KnightSignalKey.MfaRegistrationCoveragePercent, KnightSignalKey.PrivilegedAccountsWithoutMfa },
            () => CollectMfaRegistrationAsync(token, cfg, privileged, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.GuestAccounts,
            new[] { KnightSignalKey.InactiveGuestAccounts },
            () => CollectGuestsAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ConditionalAccessPolicies,
            new[] { KnightSignalKey.LegacyAuthenticationBlocked, KnightSignalKey.AdminMfaPolicyEnforced },
            () => CollectConditionalAccessAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.SecurityBaseline,
            new[] { KnightSignalKey.SecurityDefaultsEnabled },
            () => CollectSecurityDefaultsAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ApplicationInventory,
            new[] { KnightSignalKey.ApplicationCredentialsExpiring },
            () => CollectApplicationCredentialsAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ApplicationPermissions,
            new[] { KnightSignalKey.HighPrivilegeApplications },
            () => CollectApplicationPermissionsAsync(token, cfg, obs, ct), obs, caps);

        await RunCapabilityAsync(KnightCapability.ApplicationConsents,
            new[] { KnightSignalKey.AdminConsentedApplications },
            () => CollectDelegatedConsentsAsync(token, cfg, obs, ct), obs, caps);

        var facts = new KnightFactSet(obs);
        var runState = DeriveState(caps);
        return new KnightCollectionResult(Source, runState, Label, facts, caps, DateTimeOffset.UtcNow, DescribeState(runState));
    }

    private async Task RunCapabilityAsync(
        KnightCapability capability, KnightSignalKey[] keys, Func<Task> collect,
        List<KnightObservation> obs, List<KnightCapabilityStatus> caps)
    {
        try
        {
            await collect();
            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Collected));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EntraGraphException ex)
        {
            var (outcome, baseReason) = ex.Kind switch
            {
                EntraGraphErrorKind.InsufficientPermission => (KnightCapabilityOutcome.InsufficientPermission, "Permissão insuficiente para esta coleta."),
                EntraGraphErrorKind.Throttled => (KnightCapabilityOutcome.Throttled, "Throttling/limite de taxa do Microsoft Graph."),
                EntraGraphErrorKind.AuthFailure => (KnightCapabilityOutcome.AuthenticationFailure, "Falha de autenticação nesta coleta."),
                _ => (KnightCapabilityOutcome.Unavailable, "Microsoft Graph indisponível para esta coleta."),
            };
            var reason = BuildDiagnosticReason(baseReason, ex);
            _log?.LogWarning(
                "Falha Graph sanitizada em {Capability}: Outcome={Outcome}, HttpStatus={HttpStatus}, GraphCode={GraphCode}, Endpoint={Endpoint}",
                capability, outcome, ex.HttpStatusCode, ex.GraphErrorCode ?? "n/a", ex.EndpointPath ?? "n/a");
            caps.Add(new KnightCapabilityStatus(capability, outcome, reason));
            MarkMissing(obs, keys, reason);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Falha inesperada ao coletar a capacidade {Capability} do Entra.", capability);
            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Error, "Erro inesperado nesta coleta."));
            MarkMissing(obs, keys, "Erro inesperado nesta coleta.");
        }
    }

    private static string BuildDiagnosticReason(string baseReason, EntraGraphException ex)
    {
        var parts = new List<string>();
        if (ex.HttpStatusCode is { } status) parts.Add($"HTTP {status}");
        if (!string.IsNullOrWhiteSpace(ex.GraphErrorCode)) parts.Add($"Graph: {ex.GraphErrorCode}");
        if (!string.IsNullOrWhiteSpace(ex.EndpointPath)) parts.Add($"endpoint: {ex.EndpointPath}");
        return parts.Count == 0 ? baseReason : $"{baseReason} {string.Join(" · ", parts)}";
    }

    private static void MarkMissing(List<KnightObservation> obs, KnightSignalKey[] keys, string reason)
    {
        var already = obs.Select(o => o.Key).ToHashSet();
        foreach (var k in keys)
            if (already.Add(k))
                obs.Add(KnightObservation.MissingData(k, reason));
    }

    private async Task CollectPrivilegedRolesAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator acc, List<KnightObservation> obs, CancellationToken ct)
    {
        var members = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);

        await foreach (var role in _graph.GetPagedAsync(token, cfg, "directoryRoles?$select=id,displayName", ct))
        {
            var roleId = Str(role, "id");
            if (string.IsNullOrEmpty(roleId)) continue;
            var url = $"directoryRoles/{roleId}/members?$select=id,userType,signInActivity";
            await foreach (var m in _graph.GetPagedAsync(token, cfg, url, ct))
            {
                var id = Str(m, "id");
                if (string.IsNullOrEmpty(id)) continue;
                members[id] = new MemberInfo(ClassifyMember(m), Str(m, "userType"), LastSignIn(m));
            }
        }

        acc.Collected = true;
        acc.PrivilegedUsers = members
            .Where(kv => kv.Value.Kind == MemberKind.User)
            .Select(kv => new PrivilegedUser(kv.Key, kv.Value.LastSignIn))
            .ToList();
        acc.AllMembersClassifiable = members.Values.All(v => v.Kind is MemberKind.User or MemberKind.ServicePrincipal);

        var total = members.Count;
        var external = members.Values.Count(v => string.Equals(v.UserType, "Guest", StringComparison.OrdinalIgnoreCase));

        obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, total));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ExternalMembersInPrivilegedRoles, external));
        obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithMailbox,
            "A propriedade de diretório (mail) não comprova mailbox Exchange ativa; não avaliado nesta coleta."));

        if (!acc.AllMembersClassifiable)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.StalePrivilegedAccounts,
                "Há membro de papel privilegiado não identificável como usuário — cobertura de atividade incompleta."));
        }
        else if (acc.PrivilegedUsers.Any(u => u.LastSignIn is null))
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.StalePrivilegedAccounts,
                "Atividade de sign-in ausente para parte das contas privilegiadas — não avaliado (requer AuditLog.Read.All e licença compatível)."));
        }
        else
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-KnightCatalog.StalePrivilegedWindowDays);
            var stale = acc.PrivilegedUsers.Count(u => u.LastSignIn < cutoff);
            obs.Add(KnightObservation.OfCount(KnightSignalKey.StalePrivilegedAccounts, stale));
        }
    }

    private async Task CollectMfaRegistrationAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator acc, List<KnightObservation> obs, CancellationToken ct)
    {
        var total = 0;
        var mfaCapable = 0;
        var malformed = false;
        var noMfaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var url = "reports/authenticationMethods/userRegistrationDetails?$select=id,isMfaCapable,isMfaRegistered";
        await foreach (var u in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            total++;
            var id = Str(u, "id");
            if (!string.IsNullOrEmpty(id)) seenIds.Add(id);
            var capable = Bool(u, "isMfaCapable") ?? Bool(u, "isMfaRegistered");
            if (capable is null) { malformed = true; continue; }
            if (capable.Value) mfaCapable++;
            else if (!string.IsNullOrEmpty(id)) noMfaIds.Add(id);
        }

        if (total == 0)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.MfaRegistrationCoveragePercent,
                "Relatório userRegistrationDetails vazio — cobertura de MFA não pode ser afirmada (denominador zero)."));
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Sem dados de registro de MFA para o cruzamento com contas privilegiadas."));
            return;
        }

        if (malformed)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.MfaRegistrationCoveragePercent,
                "Registro de MFA com dados malformados (isMfaCapable ausente/inválido) — cobertura não avaliada."));
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Registro de MFA malformado impede o cruzamento com contas privilegiadas."));
            return;
        }

        var pct = Math.Round(100.0 * mfaCapable / total, 1);
        obs.Add(KnightObservation.OfRatio(KnightSignalKey.MfaRegistrationCoveragePercent, pct));

        if (!acc.Collected)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Inventário de papéis privilegiados indisponível para o cruzamento com MFA."));
        }
        else if (!acc.AllMembersClassifiable)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Há membro de papel privilegiado não identificável como usuário — cruzamento com MFA incompleto."));
        }
        else if (acc.PrivilegedUsers.Any(u => !seenIds.Contains(u.Id)))
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Há conta(s) privilegiada(s) ausente(s) do relatório de registro de MFA — cobertura incompleta (ausência não implica MFA configurada)."));
        }
        else
        {
            var privWithout = acc.PrivilegedUsers.Count(u => noMfaIds.Contains(u.Id));
            obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, privWithout));
        }
    }

    private async Task CollectGuestsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-KnightCatalog.InactiveGuestWindowDays);
        var inactive = 0;
        var url = "users?$filter=userType eq 'Guest'&$select=id,signInActivity,createdDateTime&$top=999";
        await foreach (var g in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            var last = LastSignIn(g) ?? Date(g, "createdDateTime");
            if (last is null || last < cutoff) inactive++;
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.InactiveGuestAccounts, inactive));
    }

    private async Task CollectConditionalAccessAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var legacyBlocked = false;
        var adminMfa = false;
        await foreach (var p in _graph.GetPagedAsync(token, cfg, "identity/conditionalAccess/policies", ct))
        {
            if (!string.Equals(Str(p, "state"), "enabled", StringComparison.OrdinalIgnoreCase)) continue;

            var controls = BuiltInControls(p);
            var cond = Obj(p, "conditions");
            var users = Obj(cond, "users");
            var apps = Obj(cond, "applications");

            var appliesAllApps = ArrayStrings(apps, "includeApplications")
                .Any(a => a.Equals("All", StringComparison.OrdinalIgnoreCase));
            var appliesAllUsers = ArrayStrings(users, "includeUsers")
                .Any(u => u.Equals("All", StringComparison.OrdinalIgnoreCase));
            var hasExclusions = ArrayStrings(users, "excludeUsers").Count > 0
                || ArrayStrings(users, "excludeGroups").Count > 0
                || ArrayStrings(users, "excludeRoles").Count > 0;
            var targetsLegacy = ArrayStrings(cond, "clientAppTypes").Any(c =>
                c.Equals("exchangeActiveSync", StringComparison.OrdinalIgnoreCase) || c.Equals("other", StringComparison.OrdinalIgnoreCase));

            if (targetsLegacy && controls.Contains("block") && appliesAllApps && appliesAllUsers && !hasExclusions)
                legacyBlocked = true;

            var requiresMfa = controls.Contains("mfa");
            if (requiresMfa && appliesAllUsers && appliesAllApps && !hasExclusions)
                adminMfa = true;
        }
        obs.Add(KnightObservation.OfFlag(KnightSignalKey.LegacyAuthenticationBlocked, legacyBlocked));
        obs.Add(KnightObservation.OfFlag(KnightSignalKey.AdminMfaPolicyEnforced, adminMfa));
    }

    private async Task CollectSecurityDefaultsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var root = await _graph.GetJsonAsync(token, cfg, "policies/identitySecurityDefaultsEnforcementPolicy", ct);
        var enabled = Bool(root, "isEnabled") ?? false;
        obs.Add(KnightObservation.OfFlag(KnightSignalKey.SecurityDefaultsEnabled, enabled));
    }

    private async Task CollectApplicationCredentialsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var window = DateTimeOffset.UtcNow.AddDays(KnightCatalog.AppCredentialExpiryWindowDays);
        var expiring = 0;
        var url = "applications?$select=id,displayName,passwordCredentials,keyCredentials&$top=999";
        await foreach (var app in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            if (HasExpiringCredential(app, "passwordCredentials", window) || HasExpiringCredential(app, "keyCredentials", window))
                expiring++;
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ApplicationCredentialsExpiring, expiring));
    }

    private async Task CollectApplicationPermissionsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var sp = await _graph.GetJsonAsync(token, cfg, $"servicePrincipals(appId='{MicrosoftGraphAppId}')?$select=id", ct);
        var graphSpId = Str(sp, "id");
        if (string.IsNullOrEmpty(graphSpId))
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.HighPrivilegeApplications,
                "Service principal do Microsoft Graph não localizado para ler as concessões de permissão."));
            return;
        }

        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var url = $"servicePrincipals/{graphSpId}/appRoleAssignedTo?$select=principalId,principalType,appRoleId&$top=999";
        await foreach (var a in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            if (!string.Equals(Str(a, "principalType"), "ServicePrincipal", StringComparison.OrdinalIgnoreCase)) continue;
            var appRoleId = Str(a, "appRoleId");
            var principalId = Str(a, "principalId");
            if (appRoleId is not null && principalId is not null && HighPrivilegeGraphAppRoleIds.Contains(appRoleId))
                apps.Add(principalId);
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.HighPrivilegeApplications, apps.Count));
    }

    private async Task CollectDelegatedConsentsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var g in _graph.GetPagedAsync(token, cfg, "oauth2PermissionGrants?$select=clientId,consentType&$top=999", ct))
        {
            if (!string.Equals(Str(g, "consentType"), "AllPrincipals", StringComparison.OrdinalIgnoreCase)) continue;
            var clientId = Str(g, "clientId");
            if (!string.IsNullOrEmpty(clientId)) clients.Add(clientId);
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.AdminConsentedApplications, clients.Count));
    }

    private static KnightSourceState DeriveState(IReadOnlyList<KnightCapabilityStatus> caps)
    {
        if (caps.Count == 0) return KnightSourceState.Unavailable;
        var collected = caps.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        if (collected == caps.Count) return KnightSourceState.Completed;
        if (collected > 0) return KnightSourceState.PartialCollection;

        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission)) return KnightSourceState.InsufficientPermission;
        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.Throttled)) return KnightSourceState.Throttled;
        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure)) return KnightSourceState.AuthenticationFailure;
        if (caps.All(c => c.Outcome == KnightCapabilityOutcome.Error)) return KnightSourceState.Error;
        return KnightSourceState.Unavailable;
    }

    private static string DescribeState(KnightSourceState state) => state switch
    {
        KnightSourceState.Completed => "Coleta do Microsoft Entra ID concluída.",
        KnightSourceState.PartialCollection => "Coleta parcial do Microsoft Entra ID — parte das capacidades faltou (permissão/indisponibilidade).",
        KnightSourceState.InsufficientPermission => "Permissões insuficientes para a coleta do Microsoft Entra ID.",
        KnightSourceState.AuthenticationFailure => "Falha de autenticação junto ao Microsoft Graph durante a coleta.",
        KnightSourceState.Throttled => "Throttling do Microsoft Graph durante a coleta.",
        KnightSourceState.Unavailable => "Microsoft Graph indisponível durante a coleta.",
        KnightSourceState.Error => "Erro inesperado durante a coleta do Microsoft Entra ID.",
        _ => "Coleta do Microsoft Entra ID.",
    };

    private enum MemberKind { User, ServicePrincipal, Other }
    private sealed record MemberInfo(MemberKind Kind, string? UserType, DateTimeOffset? LastSignIn);
    private sealed record PrivilegedUser(string Id, DateTimeOffset? LastSignIn);

    private sealed class PrivilegedAccumulator
    {
        public bool Collected;
        public IReadOnlyList<PrivilegedUser> PrivilegedUsers = Array.Empty<PrivilegedUser>();
        public bool AllMembersClassifiable = true;
    }

    private static MemberKind ClassifyMember(JsonElement m)
    {
        var odata = Str(m, "@odata.type");
        if (odata is not null)
        {
            if (odata.Equals("#microsoft.graph.user", StringComparison.OrdinalIgnoreCase)) return MemberKind.User;
            if (odata.Equals("#microsoft.graph.servicePrincipal", StringComparison.OrdinalIgnoreCase)) return MemberKind.ServicePrincipal;
            return MemberKind.Other;
        }
        return Str(m, "userType") is not null ? MemberKind.User : MemberKind.Other;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }
            : null;

    private static JsonElement Obj(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) ? v : default;

    private static DateTimeOffset? Date(JsonElement e, string prop)
    {
        var s = Str(e, prop);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    private static DateTimeOffset? LastSignIn(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("signInActivity", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        return Date(a, "lastSignInDateTime") ?? Date(a, "lastNonInteractiveSignInDateTime");
    }

    private static IReadOnlyList<string> BuiltInControls(JsonElement policy) =>
        policy.TryGetProperty("grantControls", out var gc) && gc.ValueKind == JsonValueKind.Object
            ? ArrayStrings(gc, "builtInControls").Select(s => s.ToLowerInvariant()).ToList()
            : Array.Empty<string>();

    private static IReadOnlyList<string> ArrayStrings(JsonElement parent, string prop)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var it in arr.EnumerateArray())
            if (it.ValueKind == JsonValueKind.String && it.GetString() is { } s) list.Add(s);
        return list;
    }

    private static bool HasExpiringCredential(JsonElement app, string prop, DateTimeOffset window)
    {
        if (!app.TryGetProperty(prop, out var creds) || creds.ValueKind != JsonValueKind.Array) return false;
        foreach (var c in creds.EnumerateArray())
        {
            var end = Date(c, "endDateTime");
            if (end is not null && end <= window) return true;
        }
        return false;
    }
}
