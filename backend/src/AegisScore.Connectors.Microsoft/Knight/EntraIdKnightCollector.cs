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
///   • Directory.Read.All  → papéis privilegiados e membros (contas privilegiadas, externos, mailbox);
///   • AuditLog.Read.All   → detalhes de registro de MFA e atividade de sign-in (cobertura de MFA, obsoletas);
///   • User.Read.All       → convidados e sua atividade;
///   • Policy.Read.All     → acesso condicional e security defaults (legada bloqueada, MFA admin, baseline);
///   • Application.Read.All → aplicações (credenciais vencendo, permissões de alto privilégio).
/// </summary>
public sealed class EntraIdKnightCollector : IKnightCollector
{
    private const string Label = "Microsoft Entra ID";

    // AppRoles (permissões de aplicativo) do Microsoft Graph consideradas de ALTO privilégio sobre o diretório.
    // GUIDs públicos e estáveis do service principal do Graph — não são segredo.
    private static readonly HashSet<string> HighPrivilegeGraphAppRoleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "19dbc75e-c2e2-444c-a770-ec69d8559fc7", // Directory.ReadWrite.All
        "9e3f62cf-ca93-4989-b6ce-bf83c28f9fe8", // RoleManagement.ReadWrite.Directory
        "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9", // Application.ReadWrite.All
        "06b708a9-e830-4db3-a914-8e69da51d44f", // AppRoleAssignment.ReadWrite.All
        "62a82d76-70ea-41e2-9197-370581804d09", // Group.ReadWrite.All
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
            new[] { KnightSignalKey.ApplicationCredentialsExpiring, KnightSignalKey.HighPrivilegeApplications },
            () => CollectApplicationsAsync(token, cfg, obs, ct), obs, caps);

        var facts = new KnightFactSet(obs);
        var runState = DeriveState(caps);
        return new KnightCollectionResult(Source, runState, Label, facts, caps, DateTimeOffset.UtcNow, DescribeState(runState));
    }

    // ---- Execução resiliente de UMA capacidade -----------------------------------------------------

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
            var (outcome, reason) = ex.Kind switch
            {
                EntraGraphErrorKind.InsufficientPermission => (KnightCapabilityOutcome.InsufficientPermission, "Permissão insuficiente para esta coleta."),
                EntraGraphErrorKind.Throttled => (KnightCapabilityOutcome.Unavailable, "Throttling/limite de taxa do Microsoft Graph."),
                EntraGraphErrorKind.AuthFailure => (KnightCapabilityOutcome.Unavailable, "Falha de autenticação nesta coleta."),
                _ => (KnightCapabilityOutcome.Unavailable, "Microsoft Graph indisponível para esta coleta."),
            };
            caps.Add(new KnightCapabilityStatus(capability, outcome, reason));
            MarkMissing(obs, keys, reason);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Falha inesperada ao coletar a capacidade {Capability} do Entra.", capability);
            caps.Add(new KnightCapabilityStatus(capability, KnightCapabilityOutcome.Unavailable, "Erro inesperado nesta coleta."));
            MarkMissing(obs, keys, "Erro inesperado nesta coleta.");
        }
    }

    /// <summary>Marca os sinais de uma capacidade que falhou como Missing SÓ se ainda não observados.</summary>
    private static void MarkMissing(List<KnightObservation> obs, KnightSignalKey[] keys, string reason)
    {
        var already = obs.Select(o => o.Key).ToHashSet();
        foreach (var k in keys)
            if (already.Add(k))
                obs.Add(KnightObservation.MissingData(k, reason));
    }

    // ---- Capacidades -------------------------------------------------------------------------------

    private async Task CollectPrivilegedRolesAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator acc, List<KnightObservation> obs, CancellationToken ct)
    {
        var members = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);

        await foreach (var role in _graph.GetPagedAsync(token, cfg, "directoryRoles?$select=id,displayName", ct))
        {
            var roleId = Str(role, "id");
            if (string.IsNullOrEmpty(roleId)) continue;
            var url = $"directoryRoles/{roleId}/members?$select=id,userType,mail,signInActivity";
            await foreach (var m in _graph.GetPagedAsync(token, cfg, url, ct))
            {
                var id = Str(m, "id");
                if (string.IsNullOrEmpty(id)) continue;
                members[id] = new MemberInfo(
                    UserType: Str(m, "userType"),
                    HasMailbox: !string.IsNullOrWhiteSpace(Str(m, "mail")),
                    LastSignIn: LastSignIn(m));
            }
        }

        acc.Collected = true;
        acc.Ids = members.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var total = members.Count;
        var withMailbox = members.Values.Count(v => v.HasMailbox);
        var external = members.Values.Count(v => string.Equals(v.UserType, "Guest", StringComparison.OrdinalIgnoreCase));

        obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsTotal, total));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithMailbox, withMailbox));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ExternalMembersInPrivilegedRoles, external));

        // Obsolescência exige sinal de atividade; se nenhum membro tem signInActivity, não afirmamos (Missing).
        var withActivity = members.Values.Where(v => v.LastSignIn is not null).ToList();
        if (withActivity.Count == 0 && total > 0)
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.StalePrivilegedAccounts,
                "Atividade de sign-in indisponível (requer AuditLog.Read.All e licença compatível)."));
        }
        else
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-KnightCatalog.StalePrivilegedWindowDays);
            var stale = withActivity.Count(v => v.LastSignIn < cutoff);
            obs.Add(KnightObservation.OfCount(KnightSignalKey.StalePrivilegedAccounts, stale));
        }
    }

    private async Task CollectMfaRegistrationAsync(
        string token, KnightEntraIdConfiguration cfg, PrivilegedAccumulator acc, List<KnightObservation> obs, CancellationToken ct)
    {
        var total = 0;
        var mfaCapable = 0;
        var noMfaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var url = "reports/authenticationMethods/userRegistrationDetails?$select=id,isMfaCapable,isMfaRegistered";
        await foreach (var u in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            total++;
            var capable = Bool(u, "isMfaCapable") ?? Bool(u, "isMfaRegistered") ?? false;
            if (capable) mfaCapable++;
            else
            {
                var id = Str(u, "id");
                if (!string.IsNullOrEmpty(id)) noMfaIds.Add(id);
            }
        }

        var pct = total == 0 ? 100.0 : Math.Round(100.0 * mfaCapable / total, 1);
        obs.Add(KnightObservation.OfRatio(KnightSignalKey.MfaRegistrationCoveragePercent, pct));

        // Contas privilegiadas SEM MFA: interseção com o inventário privilegiado. Sem esse inventário não afirmamos.
        if (acc.Collected)
        {
            var privWithout = acc.Ids.Count(id => noMfaIds.Contains(id));
            obs.Add(KnightObservation.OfCount(KnightSignalKey.PrivilegedAccountsWithoutMfa, privWithout));
        }
        else
        {
            obs.Add(KnightObservation.MissingData(KnightSignalKey.PrivilegedAccountsWithoutMfa,
                "Inventário de papéis privilegiados indisponível para o cruzamento com MFA."));
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

            var clientApps = p.TryGetProperty("conditions", out var condA)
                ? ArrayStrings(condA, "clientAppTypes") : Array.Empty<string>();
            var targetsLegacy = clientApps.Any(c =>
                c.Equals("exchangeActiveSync", StringComparison.OrdinalIgnoreCase) || c.Equals("other", StringComparison.OrdinalIgnoreCase));
            if (targetsLegacy && controls.Contains("block")) legacyBlocked = true;

            var includeRoles = p.TryGetProperty("conditions", out var cond) && cond.TryGetProperty("users", out var users)
                ? ArrayStrings(users, "includeRoles") : Array.Empty<string>();
            if (includeRoles.Count > 0 && controls.Contains("mfa")) adminMfa = true;
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

    private async Task CollectApplicationsAsync(
        string token, KnightEntraIdConfiguration cfg, List<KnightObservation> obs, CancellationToken ct)
    {
        var window = DateTimeOffset.UtcNow.AddDays(KnightCatalog.AppCredentialExpiryWindowDays);
        var expiring = 0;
        var highPriv = 0;
        var url = "applications?$select=id,displayName,passwordCredentials,keyCredentials,requiredResourceAccess&$top=999";
        await foreach (var app in _graph.GetPagedAsync(token, cfg, url, ct))
        {
            if (HasExpiringCredential(app, "passwordCredentials", window) || HasExpiringCredential(app, "keyCredentials", window))
                expiring++;
            if (HasHighPrivilegeGraphPermission(app))
                highPriv++;
        }
        obs.Add(KnightObservation.OfCount(KnightSignalKey.ApplicationCredentialsExpiring, expiring));
        obs.Add(KnightObservation.OfCount(KnightSignalKey.HighPrivilegeApplications, highPriv));
    }

    // ---- Estado geral ------------------------------------------------------------------------------

    private static KnightSourceState DeriveState(IReadOnlyList<KnightCapabilityStatus> caps)
    {
        if (caps.Count == 0) return KnightSourceState.Unavailable;
        var collected = caps.Count(c => c.Outcome == KnightCapabilityOutcome.Collected);
        var failed = caps.Count - collected;
        if (failed == 0) return KnightSourceState.Completed;
        if (collected == 0)
        {
            if (caps.All(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission))
                return KnightSourceState.InsufficientPermission;
            return KnightSourceState.Unavailable;
        }
        return KnightSourceState.PartialCollection;
    }

    private static string DescribeState(KnightSourceState state) => state switch
    {
        KnightSourceState.Completed => "Coleta do Microsoft Entra ID concluída.",
        KnightSourceState.PartialCollection => "Coleta parcial do Microsoft Entra ID — parte das capacidades faltou (permissão/indisponibilidade).",
        KnightSourceState.InsufficientPermission => "Permissões insuficientes para a coleta do Microsoft Entra ID.",
        KnightSourceState.Throttled => "Throttling do Microsoft Graph durante a coleta.",
        KnightSourceState.Unavailable => "Microsoft Graph indisponível durante a coleta.",
        _ => "Coleta do Microsoft Entra ID.",
    };

    // ---- Parsing seguro do JSON do Graph -----------------------------------------------------------

    private sealed record MemberInfo(string? UserType, bool HasMailbox, DateTimeOffset? LastSignIn);

    private sealed class PrivilegedAccumulator
    {
        public bool Collected;
        public HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }
            : null;

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
            if (end is not null && end <= window) return true;   // vencida ou vencendo na janela
        }
        return false;
    }

    private static bool HasHighPrivilegeGraphPermission(JsonElement app)
    {
        if (!app.TryGetProperty("requiredResourceAccess", out var rra) || rra.ValueKind != JsonValueKind.Array) return false;
        foreach (var resource in rra.EnumerateArray())
        {
            if (!resource.TryGetProperty("resourceAccess", out var access) || access.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in access.EnumerateArray())
            {
                // "Role" = permissão de APLICATIVO (app-only); ignora "Scope" (delegada).
                var type = Str(a, "type");
                var id = Str(a, "id");
                if (string.Equals(type, "Role", StringComparison.OrdinalIgnoreCase)
                    && id is not null && HighPrivilegeGraphAppRoleIds.Contains(id))
                    return true;
            }
        }
        return false;
    }
}
